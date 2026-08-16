using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
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
    private const string DATA_ROOT_URL_VALUE =
        "https://raw.githubusercontent.com/josephjang/TarkovHelper/refs/heads/main/data";
    private const string INDEX_FILE = "index.json";
    private const string MANIFEST_FILE = "manifest.json";
    private const string LOCAL_VERSION_FILE = "db_version.txt";
    private const string DATABASE_FILE = "tarkov_data.db";

    /// <summary>
    /// Document shapes this build can read. Only ever compared as an upper bound: a
    /// lower bound is unnecessary because the endpoint URL already selects which
    /// documents this build can meet at all.
    /// <para>
    /// "Schema version" here means the shape of the JSON document itself, the sense
    /// Docker's manifest <c>schemaVersion</c> and TUF's <c>spec_version</c> use. The
    /// contract of the database the document describes is the data format, which is a
    /// different thing and covers more (see <see cref="DataFormatVersion"/>).
    /// </para>
    /// </summary>
    internal const int MAX_SUPPORTED_SCHEMA_VERSION = 1;

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
    /// Declared before the URLs below because they derive from it (static fields
    /// initialize in declaration order).
    /// </summary>
    internal static readonly int DataFormatVersion = ReadDataFormatVersion();

    /// <summary>Channel root, holding index.json beside one directory per data format version.</summary>
    internal static readonly string DATA_ROOT_URL = DATA_ROOT_URL_VALUE;
    internal static readonly string INDEX_URL = $"{DATA_ROOT_URL}/{INDEX_FILE}";
    internal static readonly string CHANNEL_BASE_URL = string.Format(
        CultureInfo.InvariantCulture, "{0}/v{1}", DATA_ROOT_URL, DataFormatVersion);
    internal static readonly string MANIFEST_URL = $"{CHANNEL_BASE_URL}/{MANIFEST_FILE}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _assetsPath;
    private readonly string _databasePath;
    private readonly string _versionFilePath;
    private readonly string _indexUrl;
    private readonly string _channelBaseUrl;
    private readonly HttpClient _httpClient;
    private readonly System.Threading.Timer _updateTimer;
    private bool _isUpdating;
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
    public bool IsUpdating => _isUpdating;

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
        : this(DATA_ROOT_URL, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets"))
    {
    }

    /// <summary>
    /// Test seam: points an instance at a local channel root and asset directory so the
    /// protocol can be exercised without touching the network or the build output.
    /// Production goes through <see cref="Instance"/>, which pins both to this build's
    /// data format.
    /// </summary>
    internal DatabaseUpdateService(string dataRootUrl, string assetsPath)
    {
        _assetsPath = assetsPath;
        _databasePath = Path.Combine(_assetsPath, DATABASE_FILE);
        _versionFilePath = Path.Combine(_assetsPath, LOCAL_VERSION_FILE);
        _indexUrl = $"{dataRootUrl}/{INDEX_FILE}";
        _channelBaseUrl = string.Format(
            CultureInfo.InvariantCulture, "{0}/v{1}", dataRootUrl, DataFormatVersion);

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

    #region Channel documents

    /// <summary>The payload an endpoint serves. Integrity fields are optional by design.</summary>
    internal sealed record DataChannelPayload(string File, string? Sha256, long? Size);

    /// <summary>data/v&lt;N&gt;/manifest.json: what this endpoint currently offers.</summary>
    internal sealed record DataChannelManifest(
        int SchemaVersion, int DataFormatVersion, string Version, DataChannelPayload Database);

    /// <summary>data/index.json: the data format version the project publishes right now.</summary>
    internal sealed record DataChannelIndex(int SchemaVersion, int CurrentDataFormatVersion);

    /// <summary>
    /// Parses a manifest document. Returns null for anything unreadable, which callers
    /// treat as a failed check: no download and no local state change. Unknown fields
    /// are ignored, so an endpoint can carry information newer builds use without
    /// disturbing the ones already shipped.
    /// </summary>
    internal static DataChannelManifest? ParseManifest(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        try
        {
            var manifest = JsonSerializer.Deserialize<DataChannelManifest>(content, JsonOptions);

            // Required fields, checked explicitly: System.Text.Json leaves a missing
            // string null rather than failing, and a null version would compare unequal
            // to every local version and re-download the database forever.
            if (manifest == null
                || manifest.SchemaVersion < 1
                || manifest.DataFormatVersion < 1
                || string.IsNullOrWhiteSpace(manifest.Version)
                || string.IsNullOrWhiteSpace(manifest.Database?.File))
            {
                return null;
            }

            return manifest with { Version = manifest.Version.Trim() };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the channel index. Returns null for anything unreadable; the caller keeps
    /// its previous knowledge rather than assuming the build is current.
    /// </summary>
    internal static DataChannelIndex? ParseIndex(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        try
        {
            var index = JsonSerializer.Deserialize<DataChannelIndex>(content, JsonOptions);
            return index is { SchemaVersion: >= 1, CurrentDataFormatVersion: >= 1 } ? index : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    #endregion

    /// <summary>
    /// 로컬 버전 파일에서 버전 정보 로드. 로컬 파일은 엔드포인트가 아니라 "지금 가진
    /// 버전"을 적어 두는 북마크이므로 토큰 한 줄만 담는다.
    /// </summary>
    private void LoadLocalVersion()
    {
        try
        {
            if (File.Exists(_versionFilePath))
            {
                var token = File.ReadAllText(_versionFilePath).Trim();
                LocalVersion = token.Length == 0 ? null : token;
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
    /// 업데이트 확인 및 필요시 다운로드
    /// </summary>
    public async Task<UpdateCheckResult> CheckAndUpdateAsync()
    {
        if (_isUpdating)
        {
            _log.Debug("Update already in progress, skipping");
            return new UpdateCheckResult(false, false, "Update already in progress", IsSuperseded);
        }

        _isUpdating = true;
        UpdateCheckStarted?.Invoke(this, EventArgs.Empty);

        try
        {
            // 1. 채널 인덱스로 이 빌드가 뒤에 남았는지 확인 (실패해도 업데이트는 계속:
            //    동결은 미래 발행을 끝낼 뿐, 아직 못 받은 마지막 데이터를 뺏지 않는다)
            await RefreshSupersededStateAsync();

            // 2. 이 엔드포인트의 매니페스트 확인
            _log.Debug("Checking remote manifest...");
            var manifest = await GetManifestAsync();

            if (manifest == null)
            {
                var result = new UpdateCheckResult(false, false, "Failed to get remote manifest", IsSuperseded);
                UpdateCheckCompleted?.Invoke(this, result);
                return result;
            }

            if (manifest.SchemaVersion > MAX_SUPPORTED_SCHEMA_VERSION)
            {
                // Newer document shape at our own URL. Not a supersession and not the
                // user's problem: it means a publish put something here this build was
                // never taught to read, so refuse loudly and change nothing.
                _log.Error(
                    $"Manifest at {_channelBaseUrl} declares schema version {manifest.SchemaVersion}, "
                    + $"above the {MAX_SUPPORTED_SCHEMA_VERSION} this build understands. Ignoring it.");
                var result = new UpdateCheckResult(false, false, "Manifest schema version is newer than this build", IsSuperseded);
                UpdateCheckCompleted?.Invoke(this, result);
                return result;
            }

            if (manifest.DataFormatVersion != DataFormatVersion)
            {
                // The directory is ours but the payload it describes is not. A
                // mis-published endpoint, fixed by the next publish, so no user notice.
                _log.Error(
                    $"Manifest at {_channelBaseUrl} serves data format version {manifest.DataFormatVersion}, "
                    + $"but this build reads {DataFormatVersion}. Refusing to install it.");
                var result = new UpdateCheckResult(false, false, "Endpoint serves a different data format version", IsSuperseded);
                UpdateCheckCompleted?.Invoke(this, result);
                return result;
            }

            RemoteVersion = manifest.Version;
            _log.Debug($"Remote version: {manifest.Version}, Local version: {LocalVersion}");

            // 3. 버전 비교
            if (LocalVersion == manifest.Version)
            {
                _log.Debug("Database is up to date");
                var result = new UpdateCheckResult(true, false, "Database is up to date", IsSuperseded);
                UpdateCheckCompleted?.Invoke(this, result);
                return result;
            }

            // 4. 새 버전 다운로드
            _log.Info($"New version available: {manifest.Version}");
            var downloadSuccess = await DownloadDatabaseAsync(manifest.Database);

            if (!downloadSuccess)
            {
                var result = new UpdateCheckResult(false, false, "Failed to download database", IsSuperseded);
                UpdateCheckCompleted?.Invoke(this, result);
                return result;
            }

            // 5. 버전 파일 업데이트
            await UpdateLocalVersionAsync(manifest.Version);

            // 6. 업데이트 완료 이벤트 발생
            _log.Info("Database updated successfully, notifying services...");
            OnDatabaseUpdated();

            var successResult = new UpdateCheckResult(
                true, true, $"Updated to version {manifest.Version}", IsSuperseded);
            UpdateCheckCompleted?.Invoke(this, successResult);
            return successResult;
        }
        catch (Exception ex)
        {
            _log.Error($"Error during update check: {ex.Message}");
            var result = new UpdateCheckResult(false, false, ex.Message, IsSuperseded);
            UpdateCheckCompleted?.Invoke(this, result);
            return result;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// 채널 인덱스를 읽어 이 빌드가 뒤에 남았는지 갱신. 읽지 못하면 마지막으로 알던
    /// 상태를 유지한다 (일시적 실패가 알림을 껐다 켰다 하면 안 되므로).
    /// </summary>
    private async Task RefreshSupersededStateAsync()
    {
        DataChannelIndex? index;
        try
        {
            index = ParseIndex(await _httpClient.GetStringAsync(_indexUrl));
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
    private async Task<DataChannelManifest?> GetManifestAsync()
    {
        try
        {
            return ParseManifest(await _httpClient.GetStringAsync($"{_channelBaseUrl}/{MANIFEST_FILE}"));
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to get remote manifest: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 데이터베이스 파일 다운로드. 매니페스트가 크기와 해시를 실었으면 교체 전에 검증한다.
    /// </summary>
    private async Task<bool> DownloadDatabaseAsync(DataChannelPayload payload)
    {
        var tempPath = _databasePath + ".tmp";

        try
        {
            _log.Info("Downloading database...");

            // Assets 폴더가 없으면 생성
            if (!Directory.Exists(_assetsPath))
            {
                Directory.CreateDirectory(_assetsPath);
            }

            var databaseUrl = $"{_channelBaseUrl}/{payload.File}";
            using (var response = await _httpClient.GetAsync(databaseUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1;
                _log.Debug($"Database size: {totalBytes} bytes");

                await using var contentStream = await response.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

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

            if (!VerifyDownload(tempPath, payload) || !VerifyDataFormatStamp(tempPath))
            {
                TryDeleteTemp(tempPath);
                return false;
            }

            // 기존 파일 백업 및 교체
            var backupPath = _databasePath + ".bak";
            if (File.Exists(_databasePath))
            {
                // SQLite 연결 풀 클리어 - 파일 핸들 해제를 위해 필수
                _log.Debug("Clearing SQLite connection pools...");
                SqliteConnection.ClearAllPools();

                // 연결 풀 클리어 후 파일 핸들이 해제될 시간 확보
                await Task.Delay(100);

                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }

                // 파일 이동 재시도 로직 (연결 풀 해제 지연 대응)
                const int maxRetries = 3;
                for (int retry = 0; retry < maxRetries; retry++)
                {
                    try
                    {
                        File.Move(_databasePath, backupPath);
                        break;
                    }
                    catch (IOException) when (retry < maxRetries - 1)
                    {
                        _log.Warning($"File move failed, retrying ({retry + 1}/{maxRetries})...");
                        SqliteConnection.ClearAllPools();
                        await Task.Delay(500 * (retry + 1));
                    }
                }
            }

            File.Move(tempPath, _databasePath);
            _log.Info("Database downloaded successfully");

            // 백업 파일 삭제
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to download database: {ex.Message}");
            TryDeleteTemp(tempPath);
            return false;
        }
    }

    /// <summary>
    /// Checks a freshly downloaded file against the manifest before it replaces the
    /// working database. This is what makes a version stamp and a payload atomic:
    /// raw GitHub caches each file separately, so a check can otherwise pair a fresh
    /// manifest with a stale or truncated database and record the new version against
    /// the wrong bytes. Integrity fields are optional, and their absence downgrades to
    /// the previous behavior rather than blocking the update.
    /// </summary>
    private bool VerifyDownload(string tempPath, DataChannelPayload payload)
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

        if (string.IsNullOrWhiteSpace(payload.Sha256))
        {
            _log.Debug("Manifest carries no hash; installing without content verification");
            return true;
        }

        string actualHash;
        using (var stream = File.OpenRead(tempPath))
        {
            actualHash = Convert.ToHexString(SHA256.HashData(stream));
        }

        if (!actualHash.Equals(payload.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _log.Error(
                $"Downloaded database hash {actualHash} does not match the manifest's "
                + $"{payload.Sha256}. Keeping the current database.");
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
    /// reads 0, which means "no claim" and is accepted: databases published before
    /// stamping existed must keep working, and capability is judged by what a field
    /// says, not by a version number.
    /// </para>
    /// </summary>
    private bool VerifyDataFormatStamp(string tempPath)
    {
        int stamped;
        try
        {
            using var connection = new SqliteConnection($"Data Source={tempPath};Mode=ReadOnly");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            stamped = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            // The bytes already matched the manifest's hash, so the file is what the
            // publisher meant to serve. Failing to read the stamp is our problem, and
            // rejecting a verified download over it would be the worse outcome.
            _log.Warning($"Could not read the data format stamp: {ex.Message}. Installing anyway.");
            return true;
        }
        finally
        {
            // Microsoft.Data.Sqlite pools per connection string, so the handle outlives
            // the using block and the File.Move below would fail on Windows.
            SqliteConnection.ClearAllPools();
        }

        if (stamped == 0)
        {
            _log.Debug("Downloaded database carries no data format stamp; installing without that check");
            return true;
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

    private static void TryDeleteTemp(string tempPath)
    {
        if (!File.Exists(tempPath)) return;
        try { File.Delete(tempPath); } catch { }
    }

    /// <summary>
    /// 로컬 버전 파일 업데이트
    /// </summary>
    private async Task UpdateLocalVersionAsync(string version)
    {
        try
        {
            await File.WriteAllTextAsync(_versionFilePath, version);
            LocalVersion = version;
            _log.Debug($"Local version updated to: {version}");
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to update local version file: {ex.Message}");
        }
    }

    /// <summary>
    /// 데이터베이스 업데이트 완료 이벤트 발생
    /// </summary>
    private void OnDatabaseUpdated()
    {
        // UI 스레드에서 이벤트 발생
        if (System.Windows.Application.Current?.Dispatcher != null)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                DatabaseUpdated?.Invoke(this, EventArgs.Empty);
            });
        }
        else
        {
            DatabaseUpdated?.Invoke(this, EventArgs.Empty);
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
