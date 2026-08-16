using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// tarkov_data.db 업데이트를 관리하는 서비스.
/// GitHub에서 버전을 확인하고 새 버전이 있으면 자동으로 다운로드.
/// 5분마다 백그라운드에서 업데이트 체크.
///
/// The endpoint it polls is this build's data-format channel (data/v&lt;N&gt;/), where N
/// comes from the TarkovDataFormat assembly metadata the csproj stamps in. That same
/// property selects the seed database bundled into Assets\, so a build can only ever
/// poll the channel its own bundled data belongs to. A publish that cannot stay
/// additive creates the next format's directory and freezes this one, which the
/// endpoint announces with a "frozen" directive in db_version.txt.
/// Design: feature-versioned-data-channel.spec.md.
/// </summary>
public sealed class DatabaseUpdateService : IDisposable
{
    private static readonly ILogger _log = Log.For<DatabaseUpdateService>();
    private static DatabaseUpdateService? _instance;
    public static DatabaseUpdateService Instance => _instance ??= new DatabaseUpdateService();

    private const string DATA_FORMAT_METADATA_KEY = "TarkovDataFormat";
    private const string CHANNEL_BASE_URL_FORMAT =
        "https://raw.githubusercontent.com/josephjang/TarkovHelper/refs/heads/main/data/v{0}";
    private const string LOCAL_VERSION_FILE = "db_version.txt";
    private const string DATABASE_FILE = "tarkov_data.db";
    private const string FROZEN_DIRECTIVE = "frozen";
    private const int UPDATE_INTERVAL_MS = 5 * 60 * 1000; // 5분

    /// <summary>
    /// The tarkov_data.db contract this build reads, from assembly metadata. Declared
    /// before the URLs below because they derive from it (static fields initialize in
    /// declaration order).
    /// </summary>
    internal static readonly int DataFormatVersion = ReadDataFormatVersion();

    /// <summary>
    /// This build's endpoint directory. The remote file names deliberately equal the
    /// local ones: the endpoint is a mirror of the Assets layout, so one pair of
    /// constants names both sides.
    /// </summary>
    internal static readonly string CHANNEL_BASE_URL =
        string.Format(CultureInfo.InvariantCulture, CHANNEL_BASE_URL_FORMAT, DataFormatVersion);
    internal static readonly string VERSION_URL = $"{CHANNEL_BASE_URL}/{LOCAL_VERSION_FILE}";
    internal static readonly string DATABASE_URL = $"{CHANNEL_BASE_URL}/{DATABASE_FILE}";

    private readonly string _assetsPath;
    private readonly string _databasePath;
    private readonly string _versionFilePath;
    private readonly string _versionUrl;
    private readonly string _databaseUrl;
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
    /// Whether this build's endpoint has announced that it no longer receives data.
    /// Re-derived from every check that reaches the endpoint, and deliberately left
    /// alone when a check fails: a network blip must not clear a real freeze notice.
    /// </summary>
    public bool IsEndpointFrozen { get; private set; }

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
        : this(CHANNEL_BASE_URL, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets"))
    {
    }

    /// <summary>
    /// Test seam: points an instance at a local endpoint and asset directory so the
    /// channel contract can be exercised without touching the network or the build
    /// output. Production goes through <see cref="Instance"/>, which pins both to this
    /// build's data format.
    /// </summary>
    internal DatabaseUpdateService(string channelBaseUrl, string assetsPath)
    {
        _assetsPath = assetsPath;
        _databasePath = Path.Combine(_assetsPath, DATABASE_FILE);
        _versionFilePath = Path.Combine(_assetsPath, LOCAL_VERSION_FILE);
        _versionUrl = $"{channelBaseUrl}/{LOCAL_VERSION_FILE}";
        _databaseUrl = $"{channelBaseUrl}/{DATABASE_FILE}";

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovHelper/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);

        // 로컬 버전 로드
        LoadLocalVersion();

        // 5분마다 업데이트 체크 타이머 설정
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
                $"(got '{value}'). TarkovHelper.csproj must set <TarkovDataFormat> to the "
                + "data format this build reads; it selects both the bundled seed database "
                + "and the update endpoint, so there is no safe default.");
        }

        return format;
    }

    /// <summary>
    /// Parsed db_version.txt: the version token, plus the endpoint directives after it.
    /// </summary>
    internal sealed record DataChannelVersion(string Version, bool IsFrozen);

    /// <summary>
    /// Parses a channel db_version.txt. The first non-blank line is the version token,
    /// compared for exact equality exactly as before; every later non-blank line is a
    /// directive. Unknown directives are ignored on purpose, so an endpoint can say new
    /// things to newer builds without breaking the builds already shipped. Returns null
    /// for content carrying no token at all, which the caller treats as a failed check.
    /// </summary>
    internal static DataChannelVersion? ParseVersionFile(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        string? version = null;
        var frozen = false;

        foreach (var rawLine in content.Split('\n'))
        {
            // Trim also drops the \r of CRLF endings, so both line endings parse alike.
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (version == null)
            {
                version = line;
                continue;
            }

            // Case-insensitive: a freeze marker is sometimes appended by hand, and a
            // capitalized one silently doing nothing would be a bad failure mode.
            if (line.Equals(FROZEN_DIRECTIVE, StringComparison.OrdinalIgnoreCase))
            {
                frozen = true;
            }
        }

        return version == null ? null : new DataChannelVersion(version, frozen);
    }

    /// <summary>
    /// 로컬 버전 파일에서 버전 정보 로드.
    /// Parsed through the same reader as the remote file: an install that ran a
    /// pre-channel build against a frozen endpoint has the directive in its local file
    /// too, and only the token takes part in the comparison.
    /// </summary>
    private void LoadLocalVersion()
    {
        try
        {
            if (File.Exists(_versionFilePath))
            {
                LocalVersion = ParseVersionFile(File.ReadAllText(_versionFilePath))?.Version;
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

        _log.Info($"Starting background update checks (every 5 minutes) against {_versionUrl}");
        _updateTimer.Change(0, UPDATE_INTERVAL_MS); // 즉시 시작 후 5분마다 반복
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
            return new UpdateCheckResult(false, false, "Update already in progress", IsEndpointFrozen);
        }

        _isUpdating = true;
        UpdateCheckStarted?.Invoke(this, EventArgs.Empty);

        try
        {
            // 1. 원격 버전 확인
            _log.Debug("Checking remote version...");
            var remote = await GetRemoteVersionAsync();

            if (remote == null)
            {
                var result = new UpdateCheckResult(false, false, "Failed to get remote version", IsEndpointFrozen);
                UpdateCheckCompleted?.Invoke(this, result);
                return result;
            }

            RemoteVersion = remote.Version;
            // Endpoint state, re-derived per successful check rather than persisted.
            if (remote.IsFrozen != IsEndpointFrozen)
            {
                _log.Info(remote.IsFrozen
                    ? $"Data endpoint {CHANNEL_BASE_URL} is frozen: it no longer receives updates for this build"
                    : $"Data endpoint {CHANNEL_BASE_URL} is no longer marked frozen");
            }
            IsEndpointFrozen = remote.IsFrozen;
            _log.Debug($"Remote version: {remote.Version}, Local version: {LocalVersion}");

            // 2. 버전 비교
            if (LocalVersion == remote.Version)
            {
                _log.Debug("Database is up to date");
                var result = new UpdateCheckResult(true, false, "Database is up to date", IsEndpointFrozen);
                UpdateCheckCompleted?.Invoke(this, result);
                return result;
            }

            // 3. 새 버전 다운로드
            _log.Info($"New version available: {remote.Version}");
            var downloadSuccess = await DownloadDatabaseAsync();

            if (!downloadSuccess)
            {
                var result = new UpdateCheckResult(false, false, "Failed to download database", IsEndpointFrozen);
                UpdateCheckCompleted?.Invoke(this, result);
                return result;
            }

            // 4. 버전 파일 업데이트 (토큰만 기록; 디렉티브는 데이터가 아니라 엔드포인트의 상태)
            await UpdateLocalVersionAsync(remote.Version);

            // 5. 업데이트 완료 이벤트 발생
            _log.Info("Database updated successfully, notifying services...");
            OnDatabaseUpdated();

            var successResult = new UpdateCheckResult(
                true, true, $"Updated to version {remote.Version}", IsEndpointFrozen);
            UpdateCheckCompleted?.Invoke(this, successResult);
            return successResult;
        }
        catch (Exception ex)
        {
            _log.Error($"Error during update check: {ex.Message}");
            var result = new UpdateCheckResult(false, false, ex.Message, IsEndpointFrozen);
            UpdateCheckCompleted?.Invoke(this, result);
            return result;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// 원격 버전 정보 가져오기
    /// </summary>
    private async Task<DataChannelVersion?> GetRemoteVersionAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync(_versionUrl);
            return ParseVersionFile(response);
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to get remote version: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 데이터베이스 파일 다운로드
    /// </summary>
    private async Task<bool> DownloadDatabaseAsync()
    {
        try
        {
            _log.Info("Downloading database...");

            // Assets 폴더가 없으면 생성
            if (!Directory.Exists(_assetsPath))
            {
                Directory.CreateDirectory(_assetsPath);
            }

            // 임시 파일로 다운로드
            var tempPath = _databasePath + ".tmp";

            using (var response = await _httpClient.GetAsync(_databaseUrl, HttpCompletionOption.ResponseHeadersRead))
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

            // 다운로드 실패 시 임시 파일 정리
            var tempPath = _databasePath + ".tmp";
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }

            return false;
        }
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
    /// Whether the endpoint this build polls has stopped receiving data. Carried on
    /// every result, including failures, where it holds the last known state.
    /// </summary>
    public bool IsEndpointFrozen { get; }

    public UpdateCheckResult(bool success, bool wasUpdated, string message, bool isEndpointFrozen = false)
    {
        Success = success;
        WasUpdated = wasUpdated;
        Message = message;
        IsEndpointFrozen = isEndpointFrozen;
    }
}
