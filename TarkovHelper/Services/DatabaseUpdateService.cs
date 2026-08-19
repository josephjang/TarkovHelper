using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// tarkov_data.db 업데이트를 관리하는 서비스.
/// GitHub에서 매니페스트를 확인하고 새 버전이 있으면 자동으로 다운로드.
/// 한 시간마다 백그라운드에서 업데이트 체크 (시작 시 1회 즉시).
///
/// The endpoint it polls is this build's data-format channel (data/v&lt;N&gt;/), where N
/// comes from the TarkovDataFormatVersion assembly metadata the csproj stamps in. That same
/// property selects the seed database bundled into Assets\, so a build can only ever
/// poll the channel its own bundled data belongs to.
///
/// Two documents are read. data/v&lt;N&gt;/manifest.json describes the payload this build
/// should have; data/index.json names the data format version the project currently publishes,
/// which is how a build learns it has been left behind by a newer format. Endpoint
/// directories are never rewritten once superseded, so that pointer is the only mutable
/// part of the channel. Design: feature-versioned-data-channel.spec.md.
/// </summary>
public sealed class DatabaseUpdateService : IDisposable
{
    private static readonly ILogger _log = Log.For<DatabaseUpdateService>();
    private static DatabaseUpdateService? _instance;
    public static DatabaseUpdateService Instance => _instance ??= new DatabaseUpdateService();

    private const string DATA_FORMAT_METADATA_KEY = "TarkovDataFormatVersion";
    private const string LOCAL_VERSION_FILE = "db_version.txt";
    private const string DATABASE_FILE = "tarkov_data.db";

    /// <summary>
    /// One hour. The payload changes a handful of times per game patch, and raw
    /// GitHub caches each file for minutes at a time, so polling faster re-reads the
    /// same cached bytes without ever learning anything new. The check that actually
    /// matters is the one at startup.
    /// </summary>
    private const int UPDATE_INTERVAL_MS = 60 * 60 * 1000;

    /// <summary>
    /// Which data format this build reads, from assembly metadata.
    /// <para>
    /// The data format is the contract a build must satisfy to read tarkov_data.db
    /// correctly, covering the SQLite schema, the meaning of each field, and the range
    /// of values a field may take. This number identifies which one, and increments only
    /// when forward compatibility breaks, meaning a build already in the field would read
    /// the new data with its existing code and show the user something wrong. Additions
    /// an older build simply ignores do not increment it.
    /// </para>
    /// <para>
    /// Named "format" rather than "schema" on purpose: schema versioning in common use
    /// (Avro, JSON Schema, Confluent) compares structure only, and would not catch a
    /// field whose meaning or permitted values changed. Apache Iceberg's
    /// <c>format-version</c> is the same idea under the same name.
    /// </para>
    /// It identifies this build rather than the wire protocol, which is why it lives
    /// here and not on <see cref="DataChannel"/>: most of its readers ask "which format
    /// does this build read" (which seed the repo ships, which pin the csproj carries,
    /// which baseline the drift test compares). <see cref="DataChannel"/> reads it in one
    /// place, to name the endpoint such a build polls.
    /// </summary>
    internal static readonly int DataFormatVersion = ReadDataFormatVersion();

    private readonly string _assetsPath;
    private readonly string _databasePath;
    private readonly string _versionFilePath;
    private readonly string _indexUrl;
    private readonly string _channelBaseUrl;
    private readonly HttpClient _httpClient;
    private readonly System.Threading.Timer _updateTimer;

    /// <summary>
    /// How the <see cref="DatabaseUpdated"/> raise reaches the thread its subscribers
    /// expect. Production hands it to the application dispatcher, because every
    /// subscriber reloads data a WPF page is bound to; the test seam runs it inline.
    /// <para>
    /// A seam rather than reading <see cref="System.Windows.Application.Current"/> at the
    /// raise: the test process owns an Application (one test constructs an
    /// <c>App</c> to exercise the font swap) whose dispatcher no message loop ever pumps,
    /// so a posted raise would silently never run and whether a subscriber was called at
    /// all would depend on which test ran first.
    /// </para>
    /// </summary>
    private readonly Action<Action> _raiseOnUiThread;

    /// <summary>
    /// <see cref="Idle"/> or <see cref="Running"/>, claimed with <see cref="Interlocked"/>
    /// rather than a bool: two callers can reach a check at once (the hourly timer and a
    /// manual one), and a read-then-write would let both through.
    /// </summary>
    private int _isUpdating;

    private const int Idle = 0;
    private const int Running = 1;

    private bool _disposed;

    /// <summary>
    /// 데이터베이스 파일 경로
    /// </summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// 현재 로컬 버전
    /// </summary>
    public string? LocalVersion { get; private set; }

    /// <summary>
    /// 최신 원격 버전
    /// </summary>
    public string? RemoteVersion { get; private set; }

    /// <summary>
    /// Whether the project has moved on to a data format version this build cannot read, which
    /// means this build's endpoint will receive nothing further. Re-derived from every
    /// check that reaches index.json, and deliberately left alone when that fetch
    /// fails: a network blip must not clear a real notice.
    /// <para>
    /// True implies a newer app build exists, because a new data format version can only ship
    /// with the build that pins it. The UI therefore escalates the existing app-update
    /// affordance instead of raising a second one.
    /// </para>
    /// </summary>
    public bool IsSuperseded { get; private set; }

    /// <summary>
    /// 업데이트 진행 중 여부
    /// </summary>
    public bool IsUpdating => Volatile.Read(ref _isUpdating) != Idle;

    /// <summary>
    /// 데이터베이스가 업데이트되었을 때 발생하는 이벤트.
    /// 모든 DB 서비스는 이 이벤트를 구독하여 데이터를 리로드해야 함.
    /// </summary>
    public event EventHandler? DatabaseUpdated;

    /// <summary>
    /// 업데이트 체크 시작 시 발생
    /// </summary>
    public event EventHandler? UpdateCheckStarted;

    /// <summary>
    /// 업데이트 체크 완료 시 발생 (업데이트 여부와 관계없이)
    /// </summary>
    public event EventHandler<UpdateCheckResult>? UpdateCheckCompleted;

    private DatabaseUpdateService()
        : this(
            DataChannel.DATA_ROOT_URL,
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets"),
            PostToApplicationDispatcher)
    {
    }

    /// <summary>
    /// Test seam: points an instance at a local channel root and asset directory so the
    /// protocol can be exercised without touching the network or the build output.
    /// Production goes through <see cref="Instance"/>, which pins both to this build's
    /// data format.
    /// <para>
    /// It also raises <see cref="DatabaseUpdated"/> inline instead of posting it, for the
    /// reason <see cref="_raiseOnUiThread"/> gives. Wired here rather than left to each
    /// test to opt into, so a test that subscribes cannot silently observe nothing.
    /// </para>
    /// </summary>
    internal DatabaseUpdateService(string dataRootUrl, string assetsPath)
        : this(dataRootUrl, assetsPath, raise => raise())
    {
    }

    private DatabaseUpdateService(string dataRootUrl, string assetsPath, Action<Action> raiseOnUiThread)
    {
        _raiseOnUiThread = raiseOnUiThread;
        _assetsPath = assetsPath;
        _databasePath = Path.Combine(_assetsPath, DATABASE_FILE);
        _versionFilePath = Path.Combine(_assetsPath, LOCAL_VERSION_FILE);
        _indexUrl = DataChannel.BuildIndexUrl(dataRootUrl);
        _channelBaseUrl = DataChannel.BuildChannelBaseUrl(dataRootUrl, DataFormatVersion);

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // 로컬 버전 로드
        LoadLocalVersion();

        // 주기적 업데이트 체크 타이머 설정
        _updateTimer = new System.Threading.Timer(
            OnUpdateTimerElapsed,
            null,
            Timeout.Infinite,
            Timeout.Infinite);
    }

    /// <summary>
    /// Reads the data format off the running assembly. A build whose metadata is
    /// missing or unparseable throws here rather than falling back to a default: the
    /// wrong endpoint is exactly the failure this mechanism exists to prevent, and it
    /// must be loud at startup instead of silent in the field.
    /// </summary>
    private static int ReadDataFormatVersion()
    {
        var value = typeof(DatabaseUpdateService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == DATA_FORMAT_METADATA_KEY)?.Value;

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var format)
            || format < 1)
        {
            throw new InvalidOperationException(
                $"Assembly metadata '{DATA_FORMAT_METADATA_KEY}' is missing or invalid " +
                $"(got '{value}'). TarkovHelper.csproj must set <TarkovDataFormatVersion> to the "
                + "data format this build reads; it selects both the bundled seed database "
                + "and the update endpoint, so there is no safe default.");
        }

        return format;
    }

    /// <summary>
    /// 로컬 버전 파일에서 버전 정보 로드. 로컬 파일은 엔드포인트가 아니라 "지금 가진
    /// 버전"을 적어 두는 북마크이므로 토큰 한 줄만 담는다.
    /// <para>
    /// Only the first non-blank line is the token, which is how the publisher
    /// (TarkovDBEditor's DataPublishService) reads the same file. Reading the whole file
    /// instead would turn any trailing line into part of the version, making the
    /// bookmark compare unequal to every published version and re-download the whole
    /// database on every check.
    /// </para>
    /// </summary>
    private void LoadLocalVersion()
    {
        try
        {
            if (File.Exists(_versionFilePath))
            {
                var token = File.ReadLines(_versionFilePath)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.Length > 0);
                LocalVersion = string.IsNullOrEmpty(token) ? null : token;
                _log.Debug($"Local version: {LocalVersion}");
            }
            else
            {
                LocalVersion = null;
                _log.Debug("No local version file found");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Error loading local version: {ex.Message}");
            LocalVersion = null;
        }
    }

    /// <summary>
    /// 백그라운드 업데이트 체크 시작
    /// </summary>
    public void StartBackgroundUpdates()
    {
        // E2e tests disable the automatic checks: the immediate first check would
        // download a newer DB over the build-output Assets copy the tests derive
        // their expectations from (see AppEnv.DisableDbUpdate). Manual checks via
        // CheckAndUpdateAsync stay available.
        if (TarkovHelper.Debug.AppEnv.DisableDbUpdate)
        {
            _log.Info("Background update checks disabled via TARKOVHELPER_DISABLE_DB_UPDATE");
            return;
        }

        _log.Info($"Starting background update checks (hourly) against {_channelBaseUrl}");
        _updateTimer.Change(0, UPDATE_INTERVAL_MS); // 즉시 시작 후 주기적으로 반복
    }

    /// <summary>
    /// 백그라운드 업데이트 체크 중지
    /// </summary>
    public void StopBackgroundUpdates()
    {
        _log.Info("Stopping background update checks");
        _updateTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// 타이머 콜백
    /// </summary>
    private async void OnUpdateTimerElapsed(object? state)
    {
        await CheckAndUpdateAsync();
    }

    /// <summary>
    /// 업데이트 확인 및 필요시 다운로드.
    /// <para>
    /// One check raises <see cref="UpdateCheckStarted"/> once and
    /// <see cref="UpdateCheckCompleted"/> exactly once, from the single exit below. The
    /// pairing is structural rather than a matter of counting call sites, because this
    /// runs from an <c>async void</c> timer callback where an escaped exception would
    /// terminate the process.
    /// </para>
    /// <para>
    /// Both events are raised while the "a check is running" flag is still set, so a
    /// subscriber that calls back in is answered "already in progress" rather than
    /// starting a second check from inside the first one's own notification.
    /// </para>
    /// </summary>
    public async Task<UpdateCheckResult> CheckAndUpdateAsync()
    {
        // Claimed atomically: a plain read-then-write lets two callers (a manual check
        // beside the hourly timer) both see "idle" and run two checks over one Assets
        // folder, downloading and swapping the same payload twice.
        if (Interlocked.CompareExchange(ref _isUpdating, Running, Idle) != Idle)
        {
            // The only exit that raises nothing: no check began here, so raising a
            // completion would leave a started/completed pairing the subscribed UI uses
            // to drive its progress affordance one event out of balance.
            _log.Debug("Update already in progress, skipping");
            return Result(false, false, "Update already in progress");
        }

        UpdateCheckResult result;

        try
        {
            try
            {
                RaiseStarted();
                result = await RunCheckAsync();
            }
            catch (Exception ex)
            {
                _log.Error($"Error during update check: {ex.Message}");
                result = Result(false, false, ex.Message);
            }

            // Inside the flag, and inside the outer try: a completion subscriber that
            // starts another check is turned away here, and one that throws (which
            // RaiseCompleted absorbs) still cannot leave the flag stuck on, which would
            // make every later check return "already in progress" forever.
            RaiseCompleted(result);
        }
        finally
        {
            Interlocked.Exchange(ref _isUpdating, Idle);
        }

        return result;
    }

    /// <summary>
    /// The check itself. Returns a result for every outcome and raises nothing, so its
    /// caller owns the one completion event.
    /// </summary>
    private async Task<UpdateCheckResult> RunCheckAsync()
    {
        // 1. 채널 인덱스로 이 빌드가 뒤에 남았는지 확인 (실패해도 업데이트는 계속:
        //    동결은 미래 발행을 끝낼 뿐, 아직 못 받은 마지막 데이터를 뺏지 않는다)
        await RefreshSupersededStateAsync();

        // 2. 이 엔드포인트의 매니페스트 확인
        _log.Debug("Checking remote manifest...");
        var manifest = await GetManifestAsync();

        if (manifest == null)
        {
            return Result(false, false, "Failed to get remote manifest");
        }

        if (manifest.SchemaVersion > DataChannel.MAX_SUPPORTED_SCHEMA_VERSION)
        {
            // Newer document shape at our own URL. Not a supersession and not the
            // user's problem: it means a publish put something here this build was
            // never taught to read, so refuse loudly and change nothing.
            _log.Error(
                $"Manifest at {_channelBaseUrl} declares schema version {manifest.SchemaVersion}, "
                + $"above the {DataChannel.MAX_SUPPORTED_SCHEMA_VERSION} this build understands. Ignoring it.");
            return Result(false, false, "Manifest schema version is newer than this build");
        }

        if (manifest.DataFormatVersion != DataFormatVersion)
        {
            // The directory is ours but the payload it describes is not. A
            // mis-published endpoint, fixed by the next publish, so no user notice.
            _log.Error(
                $"Manifest at {_channelBaseUrl} serves data format version {manifest.DataFormatVersion}, "
                + $"but this build reads {DataFormatVersion}. Refusing to install it.");
            return Result(false, false, "Endpoint serves a different data format version");
        }

        RemoteVersion = manifest.Version;
        _log.Debug($"Remote version: {manifest.Version}, Local version: {LocalVersion}");

        // 3. 버전 비교
        // The bookmark records which version was installed, not that the file it names is
        // still on disk: an antivirus quarantine, a half-finished copy or a manual delete
        // leaves the bookmark intact with no database under it. Re-download in that case
        // rather than reporting "up to date" about a file that is not there.
        if (LocalVersion == manifest.Version && File.Exists(_databasePath))
        {
            _log.Debug("Database is up to date");
            return Result(true, false, "Database is up to date");
        }

        // 4. 새 버전 다운로드
        _log.Info($"New version available: {manifest.Version}");
        if (!await DownloadDatabaseAsync(manifest.Database))
        {
            return Result(false, false, "Failed to download database");
        }

        // 5. 버전 파일 업데이트
        var bookmarked = await UpdateLocalVersionAsync(manifest.Version);

        // 6. 업데이트 완료 이벤트 발생
        _log.Info("Database updated successfully, notifying services...");
        OnDatabaseUpdated();

        // The database is installed either way, so this is a success; a bookmark that
        // did not survive only costs one re-download on the next launch.
        return Result(true, true, bookmarked
            ? $"Updated to version {manifest.Version}"
            : $"Updated to version {manifest.Version}, but the version file could not be written");
    }

    /// <summary>
    /// Builds a result, stamping the supersession state as it stands right now. Every
    /// exit carries it, including failures, where it holds the last known state.
    /// </summary>
    private UpdateCheckResult Result(bool success, bool wasUpdated, string message) =>
        new(success, wasUpdated, message, IsSuperseded);

    /// <summary>
    /// Announces a check that is about to run. Contained for the same reason
    /// <see cref="RaiseCompleted"/> is, and symmetrically: a listener exists to be told
    /// what happened, so one that throws must not be able to cancel the work it was
    /// merely being notified of. Uncontained, a single subscriber throwing on every raise
    /// (a UI handler touching a disposed control, say) would end every update this
    /// install ever attempts while the log blamed the check.
    /// </summary>
    private void RaiseStarted()
    {
        try
        {
            UpdateCheckStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Error($"A subscriber to UpdateCheckStarted threw: {ex.Message}");
        }
    }

    /// <summary>
    /// Reports a finished check. A subscriber that throws (a dispatcher shutting down
    /// mid-check is the realistic one) must not turn a completed check into a failure,
    /// and must never reach <see cref="OnUpdateTimerElapsed"/>, whose <c>async void</c>
    /// signature would promote the exception to an unhandled one and end the process.
    /// </summary>
    private void RaiseCompleted(UpdateCheckResult result)
    {
        try
        {
            UpdateCheckCompleted?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            _log.Error($"A subscriber to UpdateCheckCompleted threw: {ex.Message}");
        }
    }

    /// <summary>
    /// 채널 인덱스를 읽어 이 빌드가 뒤에 남았는지 갱신. 읽지 못하면 마지막으로 알던
    /// 상태를 유지한다 (일시적 실패가 알림을 껐다 켰다 하면 안 되므로).
    /// </summary>
    private async Task RefreshSupersededStateAsync()
    {
        DataChannel.Index? index;
        try
        {
            index = DataChannel.ParseIndex(await _httpClient.GetStringAsync(_indexUrl));
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to get channel index: {ex.Message}");
            return;
        }

        if (index == null)
        {
            _log.Warning($"Channel index at {_indexUrl} is unreadable; keeping the last known state");
            return;
        }

        var superseded = index.CurrentDataFormatVersion > DataFormatVersion;
        if (superseded != IsSuperseded)
        {
            _log.Info(superseded
                ? $"This build reads data format version {DataFormatVersion}, but the channel now publishes "
                  + $"{index.CurrentDataFormatVersion}: no further data updates will arrive for this app version"
                : $"This build's data format {DataFormatVersion} is current again");
        }

        IsSuperseded = superseded;
    }

    /// <summary>
    /// 원격 매니페스트 가져오기
    /// </summary>
    private async Task<DataChannel.Manifest?> GetManifestAsync()
    {
        try
        {
            return DataChannel.ParseManifest(
                await _httpClient.GetStringAsync(DataChannel.BuildManifestUrl(_channelBaseUrl)));
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to get remote manifest: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 데이터베이스 파일 다운로드. 매니페스트가 크기와 해시를 실었으면 교체 전에 검증한다.
    /// Three steps, in order: fetch beside the working database, verify what arrived,
    /// then swap it in. Any failure leaves the working database and the local version
    /// bookmark exactly as they were, and takes the temp file with it.
    /// </summary>
    private async Task<bool> DownloadDatabaseAsync(DataChannel.Payload payload)
    {
        var tempPath = _databasePath + ".tmp";

        try
        {
            // Assets 폴더가 없으면 생성
            if (!Directory.Exists(_assetsPath))
            {
                Directory.CreateDirectory(_assetsPath);
            }

            await DownloadToTempAsync($"{_channelBaseUrl}/{payload.File}", tempPath);

            if (!VerifyDownload(tempPath, payload) || !VerifyDataFormatStamp(tempPath))
            {
                TryDelete(tempPath);
                return false;
            }

            await SwapIntoPlaceAsync(tempPath);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to download database: {ex.Message}");
            TryDelete(tempPath);
            return false;
        }
    }

    /// <summary>
    /// Streams the payload to <paramref name="tempPath"/>, overwriting whatever a
    /// previous attempt left there. Throws on any transport failure; the caller treats
    /// that as a failed check.
    /// </summary>
    private async Task DownloadToTempAsync(string databaseUrl, string tempPath)
    {
        _log.Info("Downloading database...");

        using var response = await _httpClient.GetAsync(databaseUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        _log.Debug($"Database size: {totalBytes} bytes");

        // Overwriting is what FileMode.Create means, and Windows will not overwrite a
        // read-only file: a leftover temp carrying that attribute would fail every
        // download from here on rather than just this one.
        ClearReadOnly(tempPath);

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long downloadedBytes = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            downloadedBytes += bytesRead;

            if (totalBytes > 0)
            {
                var progress = (double)downloadedBytes / totalBytes * 100;
                _log.Trace($"Download progress: {progress:F1}%");
            }
        }
    }

    /// <summary>
    /// Installs a verified temp file as the working database.
    /// <para>
    /// <see cref="File.Replace(string, string, string?)"/> rather than a pair of moves:
    /// it is atomic on NTFS and writes the backup itself, so at every instant the
    /// database path holds either the old file or the new one. Two moves have a window
    /// between them where it holds neither, and a failure landing in that window would
    /// leave the install with no database at all and nothing that reads the backup back.
    /// </para>
    /// <para>
    /// Windows lets a reader keep the file open, so the swap is retried a few times
    /// after draining SQLite's connection pool. If every attempt fails the exception
    /// escapes and the caller keeps the current database. <c>internal</c> for that
    /// retry path's sake: it is the one branch a test can drive directly.
    /// </para>
    /// <para>
    /// The read-only attribute is cleared off both the destination and any leftover
    /// backup first, because either one alone makes every future update on that install
    /// fail forever: Windows refuses <see cref="File.Replace(string, string, string?)"/>
    /// onto a read-only destination, and refuses to overwrite or delete a read-only
    /// backup, so the swap would fail identically on every hourly check while the whole
    /// payload was downloaded again each time.
    /// </para>
    /// </summary>
    internal async Task SwapIntoPlaceAsync(string tempPath)
    {
        if (!File.Exists(_databasePath))
        {
            // First install, or a working database that went missing. File.Replace needs
            // an existing destination, and there is nothing here to back up or lose.
            File.Move(tempPath, _databasePath);
            _log.Info("Database installed");
            return;
        }

        // SQLite 연결 풀 클리어 - 파일 핸들 해제를 위해 필수
        _log.Debug("Clearing SQLite connection pools...");
        SqliteConnection.ClearAllPools();

        // 연결 풀 클리어 후 파일 핸들이 해제될 시간 확보
        await Task.Delay(100);

        var backupPath = _databasePath + ".bak";
        TryDelete(backupPath);
        ClearReadOnly(_databasePath);

        // 파일 교체 재시도 로직 (연결 풀 해제 지연 대응)
        const int maxRetries = 3;
        for (int retry = 0; ; retry++)
        {
            try
            {
                File.Replace(tempPath, _databasePath, backupPath, ignoreMetadataErrors: true);
                break;
            }
            // UnauthorizedAccessException as well as IOException: it is not an IOException
            // (its base is SystemException), so a filter naming only IOException lets the
            // denied-access swap escape on the first attempt without ever retrying.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       && retry < maxRetries - 1)
            {
                _log.Warning(
                    $"Database swap failed ({ex.Message}), retrying ({retry + 1}/{maxRetries})...");
                SqliteConnection.ClearAllPools();
                await Task.Delay(500 * (retry + 1));
            }
        }

        _log.Info("Database downloaded successfully");

        // 백업 파일 삭제. The new bytes are already in place, so the update has
        // succeeded; removing the backup is housekeeping and must not be able to report
        // the whole download as failed and send the next check to re-download it.
        TryDelete(backupPath);
    }

    /// <summary>
    /// Checks a freshly downloaded file against the manifest before it replaces the
    /// working database. This is what makes a version stamp and a payload atomic:
    /// raw GitHub caches each file separately, so a check can otherwise pair a fresh
    /// manifest with a stale or truncated database and record the new version against
    /// the wrong bytes. Integrity fields are optional, and their absence downgrades to
    /// the previous behavior rather than blocking the update. A field that is present but
    /// unreadable is the opposite case: the publisher asked for a check this build cannot
    /// perform as written, so the payload is refused rather than installed unchecked.
    /// </summary>
    private bool VerifyDownload(string tempPath, DataChannel.Payload payload)
    {
        if (payload.Size is { } expectedSize)
        {
            var actualSize = new FileInfo(tempPath).Length;
            if (actualSize != expectedSize)
            {
                _log.Error($"Downloaded database is {actualSize} bytes, manifest says {expectedSize}. Discarding it.");
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(payload.Digest))
        {
            _log.Debug("Manifest carries no digest; installing without content verification");
            return true;
        }

        var parsed = DataChannel.ParseDigest(payload.Digest);
        if (parsed == null)
        {
            _log.Error(
                $"Manifest digest '{payload.Digest}' is not in '<algorithm>:<hex>' form, so this "
                + "build cannot tell what it was asked to check. Keeping the current database.");
            return false;
        }

        var (algorithm, expectedHash) = parsed.Value;

        // An algorithm this build does not implement is a publish from the future, not a
        // bad download: warn and install, the same way an absent digest installs. Refusing
        // would turn a hash upgrade into a breaking change for every build already in the
        // field, which is the outcome this channel exists to avoid.
        if (!string.Equals(algorithm, "sha256", StringComparison.OrdinalIgnoreCase))
        {
            _log.Warning(
                $"Manifest digest '{payload.Digest}' names algorithm '{algorithm}', which this "
                + "build cannot check; installing without content verification.");
            return true;
        }

        string actualHash;
        using (var stream = File.OpenRead(tempPath))
        {
            actualHash = Convert.ToHexString(SHA256.HashData(stream));
        }

        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            _log.Error(
                $"Downloaded database digest sha256:{actualHash.ToLowerInvariant()} does not match "
                + $"the manifest's {payload.Digest}. Keeping the current database.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the data format the database stamps into itself (SQLite's user_version, the
    /// 32-bit slot SQLite reserves for the application and never touches) and refuses a
    /// file built for a different one.
    /// <para>
    /// The manifest already claims a data format, but that is the publisher describing
    /// the payload; this is the payload describing itself, so a mis-published endpoint
    /// is caught even when its manifest is internally consistent. An unstamped database
    /// reads 0, which is not "format 0" but "this file makes no claim", and is refused:
    /// every publish stamps the database before hashing it and aborts if it cannot, so a
    /// payload that arrives unstamped did not come from a publish. That is precisely the
    /// case this check exists for, a directory populated by hand or a copy from the wrong
    /// build, and it is also the case whose manifest can carry no digest at all.
    /// </para>
    /// <para>
    /// A file SQLite cannot open at all is refused for a related reason. It is not a
    /// database: a truncated download, an error page served with a 200, a file the
    /// publisher never meant to ship. The manifest's integrity fields are optional and
    /// its digest algorithm may be one this build cannot check, so a payload can reach
    /// here without any content ever having been verified; installing it would replace
    /// the working database with something no reader can open, and the version bookmark
    /// would then record it as current.
    /// </para>
    /// </summary>
    private bool VerifyDataFormatStamp(string tempPath)
    {
        int stamped;
        try
        {
            using var connection = new SqliteConnection(
                $"Data Source={tempPath};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            stamped = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException ex)
        {
            _log.Error(
                $"Downloaded file is not a readable SQLite database: {ex.Message}. "
                + "Keeping the current database.");
            return false;
        }
        catch (Exception ex)
        {
            // Anything else that stops the stamp being read (the file locked away by a
            // scanner, a path the platform refuses) is still an unverified payload, and
            // an unverified payload is not installed. Contained here rather than left to
            // the caller's catch-all so the log names the step that actually failed.
            _log.Error(
                $"Could not read the data format stamp from the downloaded file: {ex.Message}. "
                + "Keeping the current database.");
            return false;
        }

        if (stamped == 0)
        {
            _log.Error(
                "Downloaded database carries no data format stamp. Every publish stamps one "
                + "before hashing, so a file without it did not come from a publish. Keeping "
                + "the current database.");
            return false;
        }

        if (stamped != DataFormatVersion)
        {
            _log.Error(
                $"Downloaded database is stamped data format version {stamped}, but this build reads "
                + $"{DataFormatVersion}. Keeping the current database.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Removes a file whose removal must never fail the operation around it: a leftover
    /// temp download or a superseded backup costs disk space, not correctness. Logged
    /// rather than swallowed, so a folder that keeps filling up is visible.
    /// </summary>
    private static void TryDelete(string path)
    {
        if (!File.Exists(path)) return;

        try
        {
            // Windows refuses to delete a read-only file, and the leftover this removes is
            // most often a backup an earlier build wrote from a read-only database, which
            // inherited the attribute. Left in place it makes File.Replace fail even onto
            // a perfectly normal destination, so this delete has to be able to finish.
            ClearReadOnly(path);
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not delete {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Clears the read-only attribute so a file can be replaced or deleted, reporting
    /// what it did: the attribute was somebody's intent, and the update mechanism
    /// overriding it should be visible in the log rather than silent. Nothing here
    /// throws; the operation that needed the attribute gone reports its own failure.
    /// </summary>
    private static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) == 0) return;

            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            _log.Warning($"Cleared the read-only attribute on {path} so the update could proceed");
        }
        catch (Exception ex)
        {
            _log.Warning($"Could not clear the read-only attribute on {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// 로컬 버전 파일 업데이트. Returns whether the bookmark reached disk.
    /// <para>
    /// The in-memory bookmark advances even when the write fails, because the new
    /// database is already installed by the time this runs. Leaving it behind would make
    /// every later check in this process see a version it can never reconcile and
    /// re-download the whole database, hourly, forever. A failed write costs one
    /// re-download after the next launch instead.
    /// </para>
    /// </summary>
    private async Task<bool> UpdateLocalVersionAsync(string version)
    {
        LocalVersion = version;

        try
        {
            await File.WriteAllTextAsync(_versionFilePath, version);
            _log.Debug($"Local version updated to: {version}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(
                $"Failed to write the local version file {_versionFilePath}: {ex.Message}. "
                + "The database was updated, so the next launch will download it again.");
            return false;
        }
    }

    /// <summary>
    /// 데이터베이스 업데이트 완료 이벤트 발생. 어디서 발생시킬지는
    /// <see cref="_raiseOnUiThread"/>가 결정한다.
    /// </summary>
    private void OnDatabaseUpdated() => _raiseOnUiThread(RaiseDatabaseUpdated);

    /// <summary>
    /// The production hop: queue the raise on the application's dispatcher when there is
    /// one, and run it here when there is not (the app has not started yet, or is shutting
    /// down), so the subscribed services are never left unreloaded because no UI existed.
    /// </summary>
    private static void PostToApplicationDispatcher(Action raise)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null)
        {
            dispatcher.BeginInvoke(raise);
        }
        else
        {
            raise();
        }
    }

    /// <summary>
    /// Tells the subscribed services the database on disk was replaced, one at a time and
    /// absorbing what each one throws. Contained for the same reason
    /// <see cref="RaiseStarted"/> and <see cref="RaiseCompleted"/> are, plus one this raise
    /// alone has: it happens AFTER the swap and the bookmark, so an escaping exception is
    /// caught by <see cref="CheckAndUpdateAsync"/> and reports an install that actually
    /// completed as a failed check.
    /// <para>
    /// Per subscriber rather than one try around the whole raise, because every
    /// *DbService reloads from this one event: a single handler throwing would otherwise
    /// skip every service after it in the invocation list, leaving half the app reading
    /// the old data with nothing to retry it until the next publish.
    /// </para>
    /// </summary>
    private void RaiseDatabaseUpdated()
    {
        var subscribers = DatabaseUpdated;
        if (subscribers == null) return;

        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try
            {
                ((EventHandler)subscriber)(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _log.Error($"A subscriber to DatabaseUpdated threw: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 수동 업데이트 체크 (UI에서 호출용)
    /// </summary>
    public async Task<UpdateCheckResult> ForceUpdateCheckAsync()
    {
        _log.Info("Manual update check requested");
        return await CheckAndUpdateAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _updateTimer.Dispose();
        _httpClient.Dispose();
    }
}

/// <summary>
/// 업데이트 체크 결과
/// </summary>
public class UpdateCheckResult
{
    public bool Success { get; }
    public bool WasUpdated { get; }
    public string Message { get; }

    /// <summary>
    /// Whether the project has moved past the data format this build reads. Carried on
    /// every result, including failures, where it holds the last known state.
    /// </summary>
    public bool IsSuperseded { get; }

    public UpdateCheckResult(bool success, bool wasUpdated, string message, bool isSuperseded = false)
    {
        Success = success;
        WasUpdated = wasUpdated;
        Message = message;
        IsSuperseded = isSuperseded;
    }
}
