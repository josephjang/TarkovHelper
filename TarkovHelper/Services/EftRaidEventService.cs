using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// EFT 게임 로그를 모니터링하여 레이드 이벤트를 발생시키는 서비스.
/// Profile ID 감지, PMC/SCAV 구분, 레이드 시작/종료 등의 이벤트를 제공합니다.
/// </summary>
public sealed class EftRaidEventService : IDisposable
{
    private static readonly ILogger _log = Log.For<EftRaidEventService>();
    private static readonly Lazy<EftRaidEventService> _instance = new(() => new EftRaidEventService());
    public static EftRaidEventService Instance => _instance.Value;

    #region Regex Patterns

    // Legacy SelectProfile and EFT 1.1's completed two-phase selection. Prepare-only
    // lines are deliberately excluded because an interrupted transition may never commit.
    private static readonly Regex CompletedProfileSelectionRegex = new(
        @"\b(?:SelectProfile|CompleteSelectedProfile)\s+ProfileId:([a-f0-9]+)\s+AccountId:(\d+)(?:\s|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Match the complete token so PvpSeason cannot fall through to the Pvp prefix.
    private static readonly Regex SessionModeRegex = new(
        @"Session mode:\s*(Pve|PvpSeason|Pvp|Regular)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matching with group id: 솔로/파티 구분
    private static readonly Regex MatchingGroupRegex = new(
        @"Matching with group id: (\d*)",
        RegexOptions.Compiled);

    // Local game matching cancelled: 매칭 취소
    private static readonly Regex MatchingCancelledRegex = new(
        @"Local game matching cancelled|Interrupted\. Interrupted by user",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // TRACE-NetworkGameCreate: 레이드 정보
    private static readonly Regex TraceNetworkCreateRegex = new(
        @"TRACE-NetworkGameCreate profileStatus: 'Profileid: ([^,]+), Status: ([^,]+), RaidMode: ([^,]+), Ip: ([^,]+), Port: ([^,]+), Location: ([^,]+), Sid: ([^,]+), GameMode: ([^,]+), shortId: ([^']+)'",
        RegexOptions.Compiled);

    // scene preset path: 맵 로딩
    private static readonly Regex ScenePresetRegex = new(
        @"scene preset path:maps/([^.]+)\.bundle",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // [Transit] 맵 전환
    private static readonly Regex TransitRegex = new(
        @"\[Transit\] Flag:([^,]+), RaidId:([^,]+), Count:(\d+), Locations:([^ ]+)",
        RegexOptions.Compiled);

    // [Transit] 레이드 종료 상세
    private static readonly Regex TransitEndRegex = new(
        @"\[Transit\] `([a-f0-9]+)` Count:(\d+), EventPlayer:(True|False)",
        RegexOptions.Compiled);

    // Connect (network-connection)
    private static readonly Regex ConnectRegex = new(
        @"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}).*Connect \(address: ([\d.]+):(\d+)\)",
        RegexOptions.Compiled);

    // Disconnect (network-connection)
    private static readonly Regex DisconnectRegex = new(
        @"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}).*Disconnect \(address: ([\d.]+):(\d+)\)",
        RegexOptions.Compiled);

    // Enter to the 'Connected' state
    private static readonly Regex ConnectedStateRegex = new(
        @"Enter to the 'Connected' state \(address: ([\d.]+):(\d+)",
        RegexOptions.Compiled);

    // Statistics (network-connection)
    private static readonly Regex StatisticsRegex = new(
        @"Statistics \(address: ([\d.]+):(\d+), rtt: ([\d.]+), lose: ([^,]+), sent: (\d+), received: (\d+)\)",
        RegexOptions.Compiled);

    // Timeout (network-connection)
    private static readonly Regex TimeoutRegex = new(
        @"Timeout: (Messages|Connection) timed out after not receiving any message for (\d+)ms \(address: ([\d.]+):(\d+)\)",
        RegexOptions.Compiled);

    // Timestamp 추출 (로그 라인 시작)
    private static readonly Regex TimestampRegex = new(
        @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})",
        RegexOptions.Compiled);

    // Init: 새 게임 세션 시작 (이전 레이드 종료 감지용 fallback)
    private static readonly Regex InitGameRegex = new(
        @"Init: pstrGameVersion:",
        RegexOptions.Compiled);

    #endregion

    #region Map Name Mapping

    // Map name to key mapping (EFT log values -> map_configs.json key)
    // Keys must match map_configs.json "key" field exactly
    private static readonly Dictionary<string, string> MapNameMapping = new(StringComparer.OrdinalIgnoreCase)
    {
        // Woods (key: Woods)
        { "woods", "Woods" },
        { "woods_preset", "Woods" },

        // Customs (key: Customs)
        { "customs", "Customs" },
        { "customs_preset", "Customs" },
        { "bigmap", "Customs" },  // Transit log uses "bigmap"

        // Shoreline (key: Shoreline)
        { "shoreline", "Shoreline" },
        { "shoreline_preset", "Shoreline" },

        // Interchange (key: Interchange)
        { "interchange", "Interchange" },
        { "shopping_mall", "Interchange" },  // scene preset uses shopping_mall

        // Reserve (key: Reserve)
        { "reserve", "Reserve" },
        { "rezervbase", "Reserve" },  // Transit log uses "RezervBase"
        { "rezerv_base_preset", "Reserve" },

        // Lighthouse (key: Lighthouse)
        { "lighthouse", "Lighthouse" },
        { "lighthouse_preset", "Lighthouse" },

        // Streets of Tarkov (key: StreetsOfTarkov)
        { "tarkovstreets", "StreetsOfTarkov" },  // Transit log uses "TarkovStreets"
        { "streets", "StreetsOfTarkov" },
        { "city_preset", "StreetsOfTarkov" },  // scene preset uses city_preset

        // Factory (key: Factory) - Day/Night are same map
        { "factory", "Factory" },
        { "factory4_day", "Factory" },
        { "factory4_night", "Factory" },
        { "factory_day_preset", "Factory" },
        { "factory_night_preset", "Factory" },

        // Ground Zero (key: GroundZero) - All levels are same map
        { "groundzero", "GroundZero" },
        { "sandbox", "GroundZero" },
        { "sandbox_high", "GroundZero" },  // Level 21+
        { "sandbox_start", "GroundZero" },  // Tutorial/starting
        { "sandbox_preset", "GroundZero" },
        { "sandbox_high_preset", "GroundZero" },
        { "sandbox_start_preset", "GroundZero" },

        // Labs (key: Labs)
        { "laboratory", "Labs" },
        { "laboratory_preset", "Labs" },
        { "labs", "Labs" },
        { "lab", "Labs" },

        // Labyrinth (key: Labyrinth)
        { "labyrinth", "Labyrinth" },
        { "labyrinth_preset", "Labyrinth" },
    };

    #endregion

    #region Fields

    private FileSystemWatcher? _applicationLogWatcher;
    private FileSystemWatcher? _networkLogWatcher;
    private readonly object _watcherLock = new();
    // volatile: 쓰기는 _watcherLock 아래에서 일어나지만 IsMonitoring은 UI 스레드가
    // 락 없이 읽는다 — 최신 값이 보이도록 보장한다
    private volatile bool _isWatching;
    private bool _isDisposed;

    private readonly ConcurrentDictionary<string, long> _filePositions = new();
    private DateTime _lastApplicationEventTime = DateTime.MinValue;
    private DateTime _lastNetworkEventTime = DateTime.MinValue;

    // Polling fallback: FileSystemWatcher misses writes when EFT keeps the log open
    // and the on-disk size/timestamp metadata isn't refreshed until a flush.
    private readonly object _readLock = new();
    private System.Timers.Timer? _pollTimer;
    private string? _monitoredFolderPath;

    // 폴더 캐시: 베이스 Logs 폴더의 mtime(하위 폴더 생성/삭제 시 갱신)을 키로,
    // 새 세션 폴더가 생길 때만 재탐색하고 그 외에는 캐시를 재사용한다.
    private volatile string? _cachedLogFolder;
    private DateTime _baseFolderMtimeAtResolve = DateTime.MinValue;

    // 현재 상태
    private EftProfileInfo? _currentProfile;
    private EftRaidInfo? _currentRaid;
    private GameMode _currentGameMode = GameMode.Unknown;
    private SessionProfileHint _currentSessionProfileHint = SessionProfileHint.Unknown;
    private string? _pendingGroupId;

    #endregion

    #region Events

    /// <summary>
    /// 프로파일 선택/변경 이벤트
    /// </summary>
    public event EventHandler<EftProfileEventArgs>? ProfileChanged;

    /// <summary>
    /// 레이드 이벤트 (시작, 종료, 상태 변경 등)
    /// </summary>
    public event EventHandler<EftRaidEventArgs>? RaidEvent;

    /// <summary>
    /// 모니터링 상태 변경
    /// </summary>
    public event EventHandler<bool>? MonitoringStateChanged;

    /// <summary>
    /// 오류 발생
    /// </summary>
    public event EventHandler<string>? ErrorOccurred;

    #endregion

    #region Properties

    /// <summary>
    /// 현재 프로파일 정보
    /// </summary>
    public EftProfileInfo? CurrentProfile => _currentProfile;

    /// <summary>
    /// 현재 레이드 정보
    /// </summary>
    public EftRaidInfo? CurrentRaid => _currentRaid;

    /// <summary>
    /// 현재 게임 모드 (PVE/PVP)
    /// </summary>
    public GameMode CurrentGameMode => _currentGameMode;

    /// <summary>
    /// Most recent app-profile hint parsed from a session-mode line.
    /// </summary>
    public SessionProfileHint CurrentSessionProfileHint => _currentSessionProfileHint;

    /// <summary>
    /// 모니터링 중인지 여부
    /// </summary>
    public bool IsMonitoring => _isWatching;

    #endregion

    private EftRaidEventService() { }

    #region Public Methods

    /// <summary>
    /// 로그 모니터링 시작
    /// </summary>
    /// <param name="logFolderPath">EFT 로그 폴더 경로 (null이면 기본 경로 사용)</param>
    public bool StartMonitoring(string? logFolderPath = null)
    {
        lock (_watcherLock)
        {
            _log.Debug($"StartMonitoring called with path: {logFolderPath ?? "(null, will use default)"}");
            StopMonitoring();

            var folderPath = logFolderPath ?? GetDefaultLogFolderPath();
            _log.Debug($"Resolved folder path: {folderPath}");

            if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            {
                _log.Warning($"Log folder not found or invalid: {folderPath}");
                ErrorOccurred?.Invoke(this, "EFT 로그 폴더를 찾을 수 없습니다.");
                return false;
            }

            try
            {
                _log.Debug("Creating FileSystemWatchers...");
                // Application log watcher
                _applicationLogWatcher = new FileSystemWatcher(folderPath)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    Filter = "*application*.log",
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
                _applicationLogWatcher.Changed += OnApplicationLogChanged;
                _applicationLogWatcher.Created += OnLogFileCreated;

                // Network connection log watcher
                _networkLogWatcher = new FileSystemWatcher(folderPath)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    Filter = "*network-connection*.log",
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };
                _networkLogWatcher.Changed += OnNetworkLogChanged;
                _networkLogWatcher.Created += OnLogFileCreated;

                _isWatching = true;
                _filePositions.Clear();
                _cachedLogFolder = null;
                _baseFolderMtimeAtResolve = DateTime.MinValue;
                _monitoredFolderPath = folderPath;

                // 기존 프로파일 정보 로드
                Task.Run(LoadProfileFromDbAsync);

                // 초기 스캔 - 최신 로그에서 프로파일과 세션 정보 찾기
                InitialScan(folderPath);

                // 폴링 폴백 시작 (FileSystemWatcher가 놓치는 쓰기를 보완)
                StartPollTimer();

                MonitoringStateChanged?.Invoke(this, true);
                _log.Info($"EftRaidEventService started monitoring: {folderPath}");
                return true;
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to start monitoring: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"모니터링 시작 실패: {ex.Message}");
                _isWatching = false;
                MonitoringStateChanged?.Invoke(this, false);
                return false;
            }
        }
    }

    /// <summary>
    /// 로그 모니터링 중지
    /// </summary>
    public void StopMonitoring()
    {
        lock (_watcherLock)
        {
            _pollTimer?.Stop();
            _pollTimer?.Dispose();
            _pollTimer = null;
            _monitoredFolderPath = null;

            if (_applicationLogWatcher != null)
            {
                _applicationLogWatcher.EnableRaisingEvents = false;
                _applicationLogWatcher.Changed -= OnApplicationLogChanged;
                _applicationLogWatcher.Created -= OnLogFileCreated;
                _applicationLogWatcher.Dispose();
                _applicationLogWatcher = null;
            }

            if (_networkLogWatcher != null)
            {
                _networkLogWatcher.EnableRaisingEvents = false;
                _networkLogWatcher.Changed -= OnNetworkLogChanged;
                _networkLogWatcher.Created -= OnLogFileCreated;
                _networkLogWatcher.Dispose();
                _networkLogWatcher = null;
            }

            _filePositions.Clear();
            _cachedLogFolder = null;
            _baseFolderMtimeAtResolve = DateTime.MinValue;
            _isWatching = false;
            MonitoringStateChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// 수동으로 프로파일 정보 설정
    /// </summary>
    public async Task SetProfileAsync(string pmcProfileId, string? accountId = null)
    {
        _currentProfile = new EftProfileInfo
        {
            PmcProfileId = pmcProfileId,
            ScavProfileId = CalculateScavProfileId(pmcProfileId),
            AccountId = accountId,
            UpdatedAt = DateTime.Now
        };

        await SaveProfileToDbAsync(_currentProfile);
        ProfileChanged?.Invoke(this, new EftProfileEventArgs { ProfileInfo = _currentProfile });
    }

    #endregion

    #region Private Methods - Log Processing

    private static string? GetDefaultLogFolderPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var eftLogsPath = Path.Combine(localAppData, "Battlestate Games", "EFT", "Logs");
        return Directory.Exists(eftLogsPath) ? eftLogsPath : null;
    }

    private void InitialScan(string folderPath)
    {
        _log.Debug($"InitialScan started for: {folderPath}");
        try
        {
            var latestFolder = FindLatestLogSubfolder(folderPath);
            _log.Debug($"Latest log subfolder: {latestFolder}");
            if (latestFolder == null) return;

            // Application log 스캔
            var appLogs = Directory.GetFiles(latestFolder, "*application*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            _log.Debug($"Found {appLogs.Count} application logs");
            if (appLogs.Count > 0)
            {
                var latestAppLog = appLogs[0];
                _log.Debug($"Latest application log: {latestAppLog}");
                _filePositions[latestAppLog] = new FileInfo(latestAppLog).Length;
                _log.Debug($"Set file position to: {_filePositions[latestAppLog]}");
                ScanLogFileForProfile(latestAppLog);
            }

            // Network log 스캔
            var netLogs = Directory.GetFiles(latestFolder, "*network-connection*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();

            _log.Debug($"Found {netLogs.Count} network logs");
            if (netLogs.Count > 0)
            {
                _filePositions[netLogs[0]] = new FileInfo(netLogs[0]).Length;
                _log.Debug($"Latest network log: {netLogs[0]}, position: {_filePositions[netLogs[0]]}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Initial scan failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 최신 로그 서브폴더를 캐싱해서 반환. 베이스 폴더 mtime이 그대로이고(=새 세션 폴더 없음)
    /// 캐시 폴더가 살아있으면 비싼 디렉터리 스캔을 건너뛴다.
    /// </summary>
    private string? GetLatestLogSubfolderCached(string basePath)
    {
        DateTime baseMtime;
        try { baseMtime = Directory.GetLastWriteTimeUtc(basePath); }
        catch { baseMtime = DateTime.MinValue; }

        var cached = _cachedLogFolder;
        if (cached != null && baseMtime == _baseFolderMtimeAtResolve && Directory.Exists(cached))
            return cached;

        // 재탐색은 캐시가 비었거나, base 폴더 mtime이 바뀌었거나(=새 세션 폴더), 캐시 폴더가
        // 사라졌을 때만 일어난다. 정상상태(같은 세션)에서는 이 로그가 뜨지 않아야 정상.
        var resolved = FindLatestLogSubfolder(basePath);
        _log.Debug($"[FolderCache] Re-resolved log folder: '{resolved}' " +
                   $"(prevCached='{cached ?? "none"}', baseMtime {_baseFolderMtimeAtResolve:HH:mm:ss.fff} -> {baseMtime:HH:mm:ss.fff})");
        _cachedLogFolder = resolved;
        _baseFolderMtimeAtResolve = baseMtime;
        return resolved;
    }

    private string? FindLatestLogSubfolder(string basePath)
    {
        try
        {
            var subdirs = Directory.GetDirectories(basePath)
                .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^log_\d{4}\.\d{2}\.\d{2}"))
                .OrderByDescending(d => Directory.GetLastWriteTime(d))
                .ToList();

            return subdirs.Count > 0 ? subdirs[0] : basePath;
        }
        catch
        {
            return basePath;
        }
    }

    private void ScanLogFileForProfile(string filePath)
    {
        _log.Debug($"ScanLogFileForProfile started: {filePath}");
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            string? lastPmcProfileId = null;
            string? lastAccountId = null;
            SessionProfileHint lastProfileHint = SessionProfileHint.Unknown;
            GameMode lastGameMode = GameMode.Unknown;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                // A profile switch is authoritative only after the completed line.
                if (TryParseCompletedProfileSelection(line, out var profileId, out var accountId))
                {
                    lastPmcProfileId = profileId;
                    lastAccountId = accountId;
                    _log.Debug($"[Scan] Found completed profile selection: PMC={lastPmcProfileId}, Account={lastAccountId}");
                }

                // Session mode 감지
                if (TryParseSessionProfile(line, out var profileHint, out var gameMode))
                {
                    lastProfileHint = profileHint;
                    lastGameMode = gameMode;
                    _log.Debug($"[Scan] Found Session mode: {lastProfileHint} ({lastGameMode})");
                }
            }

            // 프로파일 정보 업데이트
            if (!string.IsNullOrEmpty(lastPmcProfileId))
            {
                var existingProfile = _currentProfile;
                _log.Debug($"[Scan] Updating profile: existing={existingProfile?.PmcProfileId}, new={lastPmcProfileId}");
                if (existingProfile?.PmcProfileId != lastPmcProfileId)
                {
                    var scavId = CalculateScavProfileId(lastPmcProfileId);
                    _currentProfile = new EftProfileInfo
                    {
                        PmcProfileId = lastPmcProfileId,
                        ScavProfileId = scavId,
                        AccountId = lastAccountId,
                        UpdatedAt = DateTime.Now
                    };
                    _log.Debug($"[Scan] Profile set: PMC={lastPmcProfileId}, SCAV={scavId}");

                    Task.Run(() => SaveProfileToDbAsync(_currentProfile));
                    ProfileChanged?.Invoke(this, new EftProfileEventArgs { ProfileInfo = _currentProfile });
                }
            }

            if (lastGameMode != GameMode.Unknown)
            {
                _currentGameMode = lastGameMode;
                _currentSessionProfileHint = lastProfileHint;
                _log.Debug($"[Scan] Session profile set: {_currentSessionProfileHint} ({_currentGameMode})");

                // Propagate the detected mode so the active profile auto-switches at startup,
                // not only on a live session line appended while monitoring.
                RaidEvent?.Invoke(this, new EftRaidEventArgs
                {
                    EventType = EftRaidEventType.SessionModeDetected,
                    SessionProfileHint = _currentSessionProfileHint,
                    Timestamp = DateTime.Now,
                    Message = $"Session profile: {_currentSessionProfileHint}; game mode: {_currentGameMode} (startup scan)"
                });
            }
            _log.Debug("ScanLogFileForProfile completed");
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to scan log file: {ex.Message}");
        }
    }

    private void OnLogFileCreated(object sender, FileSystemEventArgs e)
    {
        _log.Debug($"[FileWatcher] New log file created: {e.FullPath}");
        _filePositions[e.FullPath] = 0;
    }

    private void OnApplicationLogChanged(object sender, FileSystemEventArgs e)
    {
        var now = DateTime.Now;
        if ((now - _lastApplicationEventTime).TotalMilliseconds < 200) return;
        _lastApplicationEventTime = now;

        _log.Debug($"[FileWatcher] Application log changed: {e.FullPath}");
        Task.Run(() => ProcessApplicationLogChanges(e.FullPath));
    }

    private void OnNetworkLogChanged(object sender, FileSystemEventArgs e)
    {
        var now = DateTime.Now;
        if ((now - _lastNetworkEventTime).TotalMilliseconds < 200) return;
        _lastNetworkEventTime = now;

        _log.Debug($"[FileWatcher] Network log changed: {e.FullPath}");
        Task.Run(() => ProcessNetworkLogChanges(e.FullPath));
    }

    private void StartPollTimer()
    {
        _pollTimer = new System.Timers.Timer(1000) { AutoReset = true };
        _pollTimer.Elapsed += (_, _) => PollLatestLogs();
        _pollTimer.Start();
    }

    /// <summary>
    /// Polling fallback. FileSystemWatcher does not fire reliably while EFT keeps the
    /// log file open (the on-disk size/timestamp isn't refreshed until a flush), so we
    /// periodically re-read the latest logs from the last position regardless of metadata.
    /// </summary>
    private void PollLatestLogs()
    {
        var folderPath = _monitoredFolderPath;
        if (string.IsNullOrEmpty(folderPath)) return;

        try
        {
            var latestFolder = GetLatestLogSubfolderCached(folderPath);
            if (latestFolder == null) return;

            var appLog = Directory.GetFiles(latestFolder, "*application*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
            if (appLog != null) ProcessApplicationLogChanges(appLog);

            var netLog = Directory.GetFiles(latestFolder, "*network-connection*.log", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
            if (netLog != null) ProcessNetworkLogChanges(netLog);
        }
        catch (Exception ex)
        {
            _log.Debug($"[Poll] failed: {ex.Message}");
        }
    }

    private void ProcessApplicationLogChanges(string filePath)
    {
        lock (_readLock)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                // Use the open handle's length, not FileInfo, so flushed-but-uncached writes are seen.
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                var lastPosition = _filePositions.GetValueOrDefault(filePath, 0);
                if (stream.Length < lastPosition) lastPosition = 0;
                if (stream.Length <= lastPosition) return;

                stream.Seek(lastPosition, SeekOrigin.Begin);

                using var reader = new StreamReader(stream);
                string? line;
                int lineCount = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineCount++;
                    ParseApplicationLogLine(line);
                }

                _filePositions[filePath] = stream.Position;
                _log.Debug($"[Process] Processed {lineCount} lines from application log");
            }
            catch (Exception ex)
            {
                _log.Warning($"Failed to process application log: {ex.Message}");
            }
        }
    }

    private void ProcessNetworkLogChanges(string filePath)
    {
        lock (_readLock)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                var lastPosition = _filePositions.GetValueOrDefault(filePath, 0);
                if (stream.Length < lastPosition) lastPosition = 0;
                if (stream.Length <= lastPosition) return;

                stream.Seek(lastPosition, SeekOrigin.Begin);

                using var reader = new StreamReader(stream);
                string? line;
                int lineCount = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineCount++;
                    ParseNetworkLogLine(line);
                }

                _filePositions[filePath] = stream.Position;
                _log.Debug($"[Process] Processed {lineCount} lines from network log");
            }
            catch (Exception ex)
            {
                _log.Warning($"Failed to process network log: {ex.Message}");
            }
        }
    }

    private void ParseApplicationLogLine(string line)
    {
        var timestamp = ExtractTimestamp(line);

        // Init: 새 게임 세션 시작 - 이전 레이드가 종료되었음을 의미 (network-connection 로그 없을 때 fallback)
        if (InitGameRegex.IsMatch(line))
        {
            if (_currentRaid != null && _currentRaid.State != RaidState.Ended)
            {
                _log.Debug($"[Parse] Init detected - ending current raid (fallback). Previous state: {_currentRaid.State}");
                _currentRaid.State = RaidState.Ended;
                _currentRaid.EndTime ??= timestamp;

                // 레이드 히스토리에 저장
                Task.Run(() => SaveRaidHistoryAsync(_currentRaid));

                RaidEvent?.Invoke(this, new EftRaidEventArgs
                {
                    EventType = EftRaidEventType.Disconnected,
                    RaidInfo = _currentRaid,
                    Timestamp = timestamp,
                    Message = "Raid ended (detected from Init)"
                });

                _currentRaid = null;
            }
        }

        // Session mode
        if (TryParseSessionProfile(line, out var profileHint, out var gameMode))
        {
            _currentSessionProfileHint = profileHint;
            _currentGameMode = gameMode;
            _log.Debug($"[Parse] Session mode detected: {_currentSessionProfileHint} ({_currentGameMode})");

            RaidEvent?.Invoke(this, new EftRaidEventArgs
            {
                EventType = EftRaidEventType.SessionModeDetected,
                SessionProfileHint = _currentSessionProfileHint,
                Timestamp = timestamp,
                Message = $"Session profile: {_currentSessionProfileHint}; game mode: {_currentGameMode}"
            });
        }

        // Legacy SelectProfile or EFT 1.1 CompleteSelectedProfile. A
        // PrepareSelectedProfileLocally line is intentionally not accepted.
        if (TryParseCompletedProfileSelection(line, out var profileId, out var accountId))
        {
            _log.Debug($"[Parse] Completed profile selection detected: ProfileId={profileId}, AccountId={accountId}");

            if (_currentProfile?.PmcProfileId != profileId)
            {
                var scavId = CalculateScavProfileId(profileId);
                _currentProfile = new EftProfileInfo
                {
                    PmcProfileId = profileId,
                    ScavProfileId = scavId,
                    AccountId = accountId,
                    UpdatedAt = DateTime.Now
                };
                _log.Debug($"[Parse] Profile updated: PMC={profileId}, SCAV={scavId}");

                Task.Run(() => SaveProfileToDbAsync(_currentProfile));
                ProfileChanged?.Invoke(this, new EftProfileEventArgs
                {
                    ProfileInfo = _currentProfile,
                    Timestamp = timestamp
                });

                RaidEvent?.Invoke(this, new EftRaidEventArgs
                {
                    EventType = EftRaidEventType.ProfileSelected,
                    Timestamp = timestamp
                });
            }
        }

        // Matching with group id
        var matchingMatch = MatchingGroupRegex.Match(line);
        if (matchingMatch.Success)
        {
            _pendingGroupId = matchingMatch.Groups[1].Value;
            _log.Debug($"[Parse] Matching started: GroupId={_pendingGroupId ?? "(solo)"}");

            RaidEvent?.Invoke(this, new EftRaidEventArgs
            {
                EventType = EftRaidEventType.MatchingStarted,
                Timestamp = timestamp,
                Message = string.IsNullOrEmpty(_pendingGroupId) ? "Solo" : $"Party (Leader: {_pendingGroupId})"
            });
        }

        // 매칭 취소 처리 - _currentRaid.State를 Ended로 설정하여 다음 매칭에서 새 레이드 생성 가능하게 함
        if (MatchingCancelledRegex.IsMatch(line))
        {
            if (_currentRaid != null && _currentRaid.State == RaidState.Matching)
            {
                _log.Debug($"[Parse] Matching cancelled - resetting raid state from {_currentRaid.State} to Ended");
                _currentRaid.State = RaidState.Ended;
                _currentRaid = null; // 레이드 리셋
            }
            _pendingGroupId = null;
        }

        // TRACE-NetworkGameCreate
        var traceMatch = TraceNetworkCreateRegex.Match(line);
        if (traceMatch.Success)
        {
            var raidProfileId = traceMatch.Groups[1].Value;
            _log.Debug($"[Parse] TRACE-NetworkGameCreate detected: RaidProfileId={raidProfileId}");
            _log.Debug($"[Parse] Current profile: PMC={_currentProfile?.PmcProfileId}, SCAV={_currentProfile?.ScavProfileId}");

            var raidType = _currentProfile?.GetRaidType(raidProfileId) ?? RaidType.Unknown;
            _log.Debug($"[Parse] RaidType determined: {raidType}");

            var mapName = traceMatch.Groups[6].Value;
            var mapKey = MapNameMapping.TryGetValue(mapName, out var key) ? key : mapName;
            _log.Debug($"[Parse] Map: {mapName} -> {mapKey}");

            _currentRaid = new EftRaidInfo
            {
                ProfileId = raidProfileId,
                RaidType = raidType,
                GameMode = _currentGameMode,
                MapName = mapName,
                MapKey = mapKey,
                ServerIp = traceMatch.Groups[4].Value,
                ServerPort = int.TryParse(traceMatch.Groups[5].Value, out var port) ? port : 0,
                SessionId = traceMatch.Groups[7].Value,
                ShortId = traceMatch.Groups[9].Value,
                IsParty = !string.IsNullOrEmpty(_pendingGroupId),
                PartyLeaderAccountId = string.IsNullOrEmpty(_pendingGroupId) ? null : _pendingGroupId,
                State = RaidState.Matching
            };

            _log.Info($"[RaidStarted] Map={mapKey}, RaidType={raidType}, GameMode={_currentGameMode}");
            RaidEvent?.Invoke(this, new EftRaidEventArgs
            {
                EventType = EftRaidEventType.RaidStarted,
                RaidInfo = _currentRaid,
                Timestamp = timestamp
            });
        }

        // scene preset (맵 로딩) - Fallback: TRACE-NetworkGameCreate가 없을 때 레이드 시작 감지
        var sceneMatch = ScenePresetRegex.Match(line);
        if (sceneMatch.Success)
        {
            var bundleName = sceneMatch.Groups[1].Value;
            var mapKey = MapNameMapping.TryGetValue(bundleName, out var key) ? key : bundleName;

            // Fallback: _currentRaid가 없으면 scene preset으로 레이드 생성
            if (_currentRaid == null || _currentRaid.State == RaidState.Ended)
            {
                _log.Debug($"Fallback: Creating raid from scene preset - {bundleName} -> {mapKey}");

                _currentRaid = new EftRaidInfo
                {
                    ProfileId = _currentProfile?.PmcProfileId,
                    RaidType = RaidType.Unknown, // scene preset만으로는 PMC/SCAV 구분 불가
                    GameMode = _currentGameMode,
                    MapName = bundleName,
                    MapKey = mapKey,
                    IsParty = !string.IsNullOrEmpty(_pendingGroupId),
                    PartyLeaderAccountId = string.IsNullOrEmpty(_pendingGroupId) ? null : _pendingGroupId,
                    State = RaidState.Matching
                };

                RaidEvent?.Invoke(this, new EftRaidEventArgs
                {
                    EventType = EftRaidEventType.RaidStarted,
                    RaidInfo = _currentRaid,
                    Timestamp = timestamp,
                    Message = $"Raid started (fallback): {mapKey}"
                });
            }
            else
            {
                _currentRaid.MapKey = mapKey;
            }

            RaidEvent?.Invoke(this, new EftRaidEventArgs
            {
                EventType = EftRaidEventType.MapLoadingStarted,
                RaidInfo = _currentRaid,
                Timestamp = timestamp,
                Message = $"Loading map: {mapKey}"
            });
        }

        // [Transit] 맵 전환 - Fallback: RaidId와 맵 정보 보완
        var transitMatch = TransitRegex.Match(line);
        if (transitMatch.Success)
        {
            var flag = transitMatch.Groups[1].Value;
            var raidId = transitMatch.Groups[2].Value;
            var locations = transitMatch.Groups[4].Value;

            // Locations에서 맵 키 추출 (예: "factory4_day ->")
            var mapFromTransit = locations.Split(new[] { ' ', '-', '>' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var mapKey = mapFromTransit != null && MapNameMapping.TryGetValue(mapFromTransit, out var key)
                ? key
                : mapFromTransit;

            // Fallback: _currentRaid가 없으면 Transit 로그로 레이드 생성
            if (_currentRaid == null || _currentRaid.State == RaidState.Ended)
            {
                if (!string.IsNullOrEmpty(mapKey))
                {
                    _log.Debug($"Fallback: Creating raid from Transit - RaidId:{raidId}, Map:{mapFromTransit} -> {mapKey}");

                    _currentRaid = new EftRaidInfo
                    {
                        RaidId = raidId,
                        ProfileId = _currentProfile?.PmcProfileId,
                        RaidType = RaidType.Unknown,
                        GameMode = _currentGameMode,
                        MapName = mapFromTransit,
                        MapKey = mapKey,
                        IsParty = !string.IsNullOrEmpty(_pendingGroupId),
                        PartyLeaderAccountId = string.IsNullOrEmpty(_pendingGroupId) ? null : _pendingGroupId,
                        State = RaidState.Matching
                    };

                    RaidEvent?.Invoke(this, new EftRaidEventArgs
                    {
                        EventType = EftRaidEventType.RaidStarted,
                        RaidInfo = _currentRaid,
                        Timestamp = timestamp,
                        Message = $"Raid started (transit fallback): {mapKey}"
                    });
                }
            }
            else if (_currentRaid != null)
            {
                // 기존 레이드에 RaidId 보완
                if (string.IsNullOrEmpty(_currentRaid.RaidId))
                    _currentRaid.RaidId = raidId;
                if (!string.IsNullOrEmpty(mapKey) && string.IsNullOrEmpty(_currentRaid.MapKey))
                    _currentRaid.MapKey = mapKey;
            }
        }

        // [Transit] 레이드 종료 상세
        var transitEndMatch = TransitEndRegex.Match(line);
        if (transitEndMatch.Success && _currentRaid != null)
        {
            var endProfileId = transitEndMatch.Groups[1].Value;
            _currentRaid.State = RaidState.Ended;
            _currentRaid.EndTime = timestamp;

            // 레이드 히스토리에 저장
            Task.Run(() => SaveRaidHistoryAsync(_currentRaid));

            RaidEvent?.Invoke(this, new EftRaidEventArgs
            {
                EventType = EftRaidEventType.RaidEnded,
                RaidInfo = _currentRaid,
                Timestamp = timestamp
            });
        }
    }

    internal static bool TryParseSessionProfile(
        string line,
        out SessionProfileHint profileHint,
        out GameMode gameMode)
    {
        var match = SessionModeRegex.Match(line);
        if (!match.Success)
        {
            profileHint = SessionProfileHint.Unknown;
            gameMode = GameMode.Unknown;
            return false;
        }

        profileHint = match.Groups[1].Value.ToLowerInvariant() switch
        {
            "pve" => SessionProfileHint.PveZone,
            "pvpseason" => SessionProfileHint.PvpSeason,
            "pvp" or "regular" => SessionProfileHint.PvpZone,
            _ => SessionProfileHint.Unknown
        };
        gameMode = profileHint switch
        {
            SessionProfileHint.PveZone => GameMode.PVE,
            SessionProfileHint.PvpZone or SessionProfileHint.PvpSeason => GameMode.PVP,
            _ => GameMode.Unknown
        };
        return profileHint != SessionProfileHint.Unknown;
    }

    internal static bool TryParseCompletedProfileSelection(
        string line,
        out string profileId,
        out string accountId)
    {
        var match = CompletedProfileSelectionRegex.Match(line);
        if (!match.Success)
        {
            profileId = string.Empty;
            accountId = string.Empty;
            return false;
        }

        profileId = match.Groups[1].Value;
        accountId = match.Groups[2].Value;
        return true;
    }

    private void ParseNetworkLogLine(string line)
    {
        var timestamp = ExtractTimestamp(line);

        // Connect
        var connectMatch = ConnectRegex.Match(line);
        if (connectMatch.Success)
        {
            var ip = connectMatch.Groups[2].Value;
            var port = int.TryParse(connectMatch.Groups[3].Value, out var p) ? p : 0;

            if (_currentRaid != null)
            {
                _currentRaid.ServerIp = ip;
                _currentRaid.ServerPort = port;
                _currentRaid.State = RaidState.Connecting;
            }

            RaidEvent?.Invoke(this, new EftRaidEventArgs
            {
                EventType = EftRaidEventType.Connecting,
                RaidInfo = _currentRaid,
                Timestamp = timestamp,
                Message = $"Connecting to {ip}:{port}"
            });
        }

        // Connected state
        var connectedMatch = ConnectedStateRegex.Match(line);
        if (connectedMatch.Success && _currentRaid != null)
        {
            _currentRaid.State = RaidState.InRaid;
            _currentRaid.StartTime = timestamp;

            RaidEvent?.Invoke(this, new EftRaidEventArgs
            {
                EventType = EftRaidEventType.Connected,
                RaidInfo = _currentRaid,
                Timestamp = timestamp
            });
        }

        // Disconnect
        var disconnectMatch = DisconnectRegex.Match(line);
        if (disconnectMatch.Success && _currentRaid != null)
        {
            _currentRaid.State = RaidState.Ended;
            _currentRaid.EndTime ??= timestamp;

            RaidEvent?.Invoke(this, new EftRaidEventArgs
            {
                EventType = EftRaidEventType.Disconnected,
                RaidInfo = _currentRaid,
                Timestamp = timestamp
            });
        }

        // Statistics
        var statsMatch = StatisticsRegex.Match(line);
        if (statsMatch.Success && _currentRaid != null)
        {
            _currentRaid.Rtt = double.TryParse(statsMatch.Groups[3].Value, out var rtt) ? rtt : null;
            _currentRaid.PacketLoss = double.TryParse(statsMatch.Groups[4].Value, out var loss) ? loss : null;
            _currentRaid.PacketsSent = long.TryParse(statsMatch.Groups[5].Value, out var sent) ? sent : null;
            _currentRaid.PacketsReceived = long.TryParse(statsMatch.Groups[6].Value, out var recv) ? recv : null;
        }

        // Timeout
        var timeoutMatch = TimeoutRegex.Match(line);
        if (timeoutMatch.Success)
        {
            var timeoutType = timeoutMatch.Groups[1].Value;
            var timeoutMs = timeoutMatch.Groups[2].Value;

            RaidEvent?.Invoke(this, new EftRaidEventArgs
            {
                EventType = EftRaidEventType.NetworkTimeout,
                RaidInfo = _currentRaid,
                Timestamp = timestamp,
                Message = $"{timeoutType} timeout after {timeoutMs}ms"
            });
        }
    }

    private DateTime ExtractTimestamp(string line)
    {
        var match = TimestampRegex.Match(line);
        if (match.Success && DateTime.TryParse(match.Groups[1].Value, out var dt))
        {
            return dt;
        }
        return DateTime.Now;
    }

    private static string CalculateScavProfileId(string pmcProfileId)
    {
        if (string.IsNullOrEmpty(pmcProfileId)) return "";

        var baseId = pmcProfileId[..^1];
        var lastChar = pmcProfileId[^1];

        try
        {
            var hex = Convert.ToInt32(lastChar.ToString(), 16);
            var nextHex = (hex + 1) % 16;
            return baseId + nextHex.ToString("x");
        }
        catch
        {
            return pmcProfileId;
        }
    }

    #endregion

    #region Database Operations

    private async Task LoadProfileFromDbAsync()
    {
        _log.Debug("[DB] LoadProfileFromDbAsync started");
        try
        {
            var dbService = UserDataDbService.Instance;
            var pmcId = await dbService.GetSettingAsync("eft.pmcProfileId");
            var scavId = await dbService.GetSettingAsync("eft.scavProfileId");
            var accountId = await dbService.GetSettingAsync("eft.accountId");

            _log.Debug($"[DB] Loaded from DB: PMC={pmcId ?? "(null)"}, SCAV={scavId ?? "(null)"}, Account={accountId ?? "(null)"}");

            if (!string.IsNullOrEmpty(pmcId))
            {
                var calculatedScav = scavId ?? CalculateScavProfileId(pmcId);
                _currentProfile = new EftProfileInfo
                {
                    PmcProfileId = pmcId,
                    ScavProfileId = calculatedScav,
                    AccountId = accountId,
                    UpdatedAt = DateTime.Now
                };

                _log.Debug($"[DB] Profile set from DB: PMC={pmcId}, SCAV={calculatedScav}");
            }
            else
            {
                _log.Debug("[DB] No profile found in DB");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[DB] Failed to load profile from DB: {ex.Message}");
        }
    }

    private async Task SaveProfileToDbAsync(EftProfileInfo profile)
    {
        _log.Debug($"[DB] SaveProfileToDbAsync: PMC={profile.PmcProfileId}, SCAV={profile.ScavProfileId}");
        try
        {
            var dbService = UserDataDbService.Instance;
            if (!string.IsNullOrEmpty(profile.PmcProfileId))
            {
                await dbService.SetSettingAsync("eft.pmcProfileId", profile.PmcProfileId);
                _log.Debug($"[DB] Saved eft.pmcProfileId={profile.PmcProfileId}");
            }
            if (!string.IsNullOrEmpty(profile.ScavProfileId))
            {
                await dbService.SetSettingAsync("eft.scavProfileId", profile.ScavProfileId);
                _log.Debug($"[DB] Saved eft.scavProfileId={profile.ScavProfileId}");
            }
            if (!string.IsNullOrEmpty(profile.AccountId))
            {
                await dbService.SetSettingAsync("eft.accountId", profile.AccountId);
                _log.Debug($"[DB] Saved eft.accountId={profile.AccountId}");
            }

            _log.Debug("[DB] SaveProfileToDbAsync completed");
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to save profile to DB: {ex.Message}");
        }
    }

    private async Task SaveRaidHistoryAsync(EftRaidInfo raid)
    {
        try
        {
            var dbService = UserDataDbService.Instance;
            await dbService.SaveRaidHistoryAsync(raid);
            _log.Debug($"Saved raid history: {raid.MapKey} ({raid.RaidType})");
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to save raid history: {ex.Message}");
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        StopMonitoring();
    }

    #endregion
}
