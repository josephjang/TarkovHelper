using System.IO;
using System.Text.Json;
using TarkovHelper.Models;
using TarkovHelper.Services.Eft;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services
{
    /// <summary>
    /// Service for synchronizing quest progress from Tarkov game logs
    /// </summary>
    public class LogSyncService : IDisposable
    {
        private static readonly ILogger _log = Log.For<LogSyncService>();
        private static LogSyncService? _instance;
        public static LogSyncService Instance => _instance ??= new LogSyncService();

        private static readonly string DebugLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TarkovHelper", "logsync_debug.log");

        private static void DebugLog(string message)
        {
            try
            {
                var dir = Path.GetDirectoryName(DebugLogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(DebugLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
            }
            catch { }
        }

        private FileSystemWatcher? _logWatcher;
        private FileSystemWatcher? _applicationLogWatcher;
        private readonly object _watcherLock = new();
        private DateTime _lastEventTime = DateTime.MinValue;
        private DateTime _lastMapEventTime = DateTime.MinValue;
        private string? _lastModifiedFile;
        private string? _lastMapModifiedFile;
        private bool _isWatching;
        private long _lastApplicationLogPosition;
        private string? _currentMapKey;

        // Session-mode timelines for the live path, one per session folder, kept across events
        // so a growing application log is re-read only from where the last read stopped. The
        // sync path builds its own throwaway timelines instead: it runs once over a fixed set
        // of folders and must not inherit offsets from whatever the watcher has already seen.
        private readonly Dictionary<string, SessionModeTimeline> _liveTimelines =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _timelineLock = new();

        /// <summary>
        /// Store used to read each owning profile's saved rows during a sync. Internal and
        /// settable so tests can substitute a fake: a sync spans several profiles and at most
        /// one of them is loaded in memory, so "already recorded?" has to be answered from
        /// storage, per profile.
        /// </summary>
        internal IQuestProgressStore Store { get; set; } = UserDataDbService.Instance;

        /// <summary>
        /// Event fired when a quest event is detected from logs
        /// </summary>
        public event EventHandler<QuestLogEvent>? QuestEventDetected;

        /// <summary>
        /// Event fired when a map change is detected from logs
        /// </summary>
        public event EventHandler<MapDetectedEventArgs>? MapDetected;

        /// <summary>
        /// Event fired when log monitoring status changes
        /// </summary>
        public event EventHandler<bool>? MonitoringStatusChanged;

        /// <summary>
        /// Whether log monitoring is currently active
        /// </summary>
        public bool IsMonitoring => _isWatching;

        /// <summary>
        /// Currently detected map key
        /// </summary>
        public string? CurrentMapKey => _currentMapKey;

        // Message type codes from logs
        private const int MSG_TYPE_STARTED = 10;
        private const int MSG_TYPE_FAILED = 11;
        private const int MSG_TYPE_COMPLETED = 12;

        // Map name to key mapping (EFT log name -> map_configs.json key)
        // All keys are stored in lowercase for case-insensitive matching
        // Use TryGetMapKey() method for lookups instead of direct dictionary access
        //
        // EFT uses two patterns in logs:
        // 1. scene preset path:maps/<name>.bundle (e.g., "maps/shoreline_preset.bundle")
        // 2. [Transit] Locations:<name> (e.g., "Locations:Shoreline")
        private static readonly Dictionary<string, string> MapNameToKey = new(StringComparer.OrdinalIgnoreCase)
        {
            // Woods
            // Transit: "Woods", Preset: "woods_preset"
            { "woods", "Woods" },
            { "woods_preset", "Woods" },

            // Customs
            // Transit: "bigmap", Preset: "customs_preset"
            { "customs", "Customs" },
            { "customs_preset", "Customs" },
            { "bigmap", "Customs" },
            { "bigmap_preset", "Customs" },

            // Shoreline
            // Transit: "Shoreline", Preset: "shoreline_preset"
            { "shoreline", "Shoreline" },
            { "shoreline_preset", "Shoreline" },

            // Interchange
            // Transit: "Interchange", Preset: "shopping_mall"
            { "interchange", "Interchange" },
            { "interchange_preset", "Interchange" },
            { "shopping_mall", "Interchange" },
            { "shopping_mall_preset", "Interchange" },

            // Reserve
            // Transit: "RezervBase", Preset: "rezerv_base_preset"
            { "reserve", "Reserve" },
            { "rezervbase", "Reserve" },
            { "rezerv_base", "Reserve" },
            { "rezerv_base_preset", "Reserve" },
            { "rezervbase_preset", "Reserve" },

            // Lighthouse
            // Transit: "Lighthouse", Preset: "lighthouse_preset"
            { "lighthouse", "Lighthouse" },
            { "lighthouse_preset", "Lighthouse" },

            // Streets of Tarkov
            // Transit: "TarkovStreets", Preset: "city_preset"
            { "streetsoftarkov", "StreetsOfTarkov" },
            { "streets", "StreetsOfTarkov" },
            { "tarkovstreets", "StreetsOfTarkov" },
            { "tarkovstreets_preset", "StreetsOfTarkov" },
            { "city", "StreetsOfTarkov" },
            { "city_preset", "StreetsOfTarkov" },

            // Factory (Day/Night variants)
            // Transit: "factory4_day", "factory4_night", Preset: "factory_day_preset", "factory_night_preset"
            { "factory", "Factory" },
            { "factory4", "Factory" },
            { "factory4_day", "Factory" },
            { "factory4_night", "Factory" },
            { "factory_day", "Factory" },
            { "factory_night", "Factory" },
            { "factory_day_preset", "Factory" },
            { "factory_night_preset", "Factory" },
            { "factory4_day_preset", "Factory" },
            { "factory4_night_preset", "Factory" },

            // Ground Zero (Sandbox_start for level 1-20, Sandbox_high for level 21+)
            // Transit: "Sandbox_high", "Sandbox_start", Preset: "sandbox_high_preset", "sandbox_start_preset"
            { "groundzero", "GroundZero" },
            { "sandbox", "GroundZero" },
            { "sandbox_high", "GroundZero" },
            { "sandbox_start", "GroundZero" },
            { "sandbox_preset", "GroundZero" },
            { "sandbox_high_preset", "GroundZero" },
            { "sandbox_start_preset", "GroundZero" },

            // Labs
            // Transit: "laboratory", Preset: "laboratory_preset"
            { "labs", "Labs" },
            { "lab", "Labs" },
            { "laboratory", "Labs" },
            { "thelab", "Labs" },
            { "laboratory_preset", "Labs" },

            // Labyrinth (if available)
            { "labyrinth", "Labyrinth" },
            { "thelabyrinth", "Labyrinth" },
            { "labyrinth_preset", "Labyrinth" },
        };

        /// <summary>
        /// Try to get the map key from a map name (case-insensitive)
        /// </summary>
        private static bool TryGetMapKey(string mapName, out string? mapKey)
        {
            mapKey = null;
            if (string.IsNullOrEmpty(mapName))
                return false;

            // Direct lookup (case-insensitive due to StringComparer.OrdinalIgnoreCase)
            if (MapNameToKey.TryGetValue(mapName, out mapKey))
                return true;

            // Try removing common suffixes and prefixes
            var cleanedName = mapName
                .Replace("_preset", "")
                .Replace("preset_", "")
                .Replace("_high", "")
                .Replace("_low", "")
                .Replace("_day", "")
                .Replace("_night", "")
                .Trim();

            if (!string.IsNullOrEmpty(cleanedName) && MapNameToKey.TryGetValue(cleanedName, out mapKey))
                return true;

            return false;
        }

        // Internal rather than private so tests can build a real instance with its field
        // initializers intact. Constructing one bypasses the singleton, which is the point:
        // these tests assert which storage partition a sync wrote to, and a shared instance
        // carrying another test's cached timelines would make that answer depend on test order.
        internal LogSyncService() { }

        #region Log File Monitoring

        /// <summary>
        /// Start monitoring log folder for quest events and map detection
        /// </summary>
        public void StartMonitoring(string logFolderPath)
        {
            lock (_watcherLock)
            {
                StopMonitoring();

                DebugLog($"StartMonitoring called with path: {logFolderPath}");

                if (string.IsNullOrEmpty(logFolderPath) || !Directory.Exists(logFolderPath))
                {
                    DebugLog($"Invalid path or directory does not exist");
                    return;
                }

                try
                {
                    // Quest event watcher (push-notifications logs)
                    _logWatcher = new FileSystemWatcher(logFolderPath)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        Filter = "*push-notifications*.log",
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true
                    };

                    _logWatcher.Changed += OnLogFileChanged;
                    _logWatcher.Created += OnLogFileChanged;

                    // Map detection watcher (application logs).
                    //
                    // NOTE: this filter, and the two Directory.GetFiles calls that share it,
                    // match nothing against real EFT logs: they are named
                    // "<date>_<time>_<version> application.log", so the pattern needs a leading
                    // wildcard (see EftLogPatterns / SessionModeTimeline, which use
                    // "*application*.log"). This whole map path is dead as a result: MapDetected
                    // and FindLastMapFromLogs have no subscribers anywhere, and live map
                    // detection is LogMapWatcherService's job. Left as-is deliberately rather
                    // than "fixed" into doing unused work; quest attribution reads the session
                    // mode through SessionModeTimeline, not through here.
                    _applicationLogWatcher = new FileSystemWatcher(logFolderPath)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                        Filter = "application*.log",
                        IncludeSubdirectories = true,
                        EnableRaisingEvents = true
                    };

                    _applicationLogWatcher.Changed += OnApplicationLogChanged;
                    _applicationLogWatcher.Created += OnApplicationLogChanged;

                    // Initialize position for latest application log
                    InitializeLatestApplicationLogPosition(logFolderPath);

                    _isWatching = true;
                    MonitoringStatusChanged?.Invoke(this, true);
                }
                catch
                {
                    _isWatching = false;
                    MonitoringStatusChanged?.Invoke(this, false);
                }
            }
        }

        /// <summary>
        /// Initialize position to end of latest application log file
        /// </summary>
        private void InitializeLatestApplicationLogPosition(string logFolderPath)
        {
            try
            {
                var latestLog = Directory.GetFiles(logFolderPath, "application*.log", SearchOption.AllDirectories)
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .FirstOrDefault();

                if (latestLog != null && File.Exists(latestLog))
                {
                    var fileInfo = new FileInfo(latestLog);
                    _lastApplicationLogPosition = fileInfo.Length;
                    _lastMapModifiedFile = latestLog;
                }
            }
            catch
            {
                _lastApplicationLogPosition = 0;
            }
        }

        /// <summary>
        /// Stop monitoring log folder
        /// </summary>
        public void StopMonitoring()
        {
            lock (_watcherLock)
            {
                if (_logWatcher != null)
                {
                    _logWatcher.EnableRaisingEvents = false;
                    _logWatcher.Changed -= OnLogFileChanged;
                    _logWatcher.Created -= OnLogFileChanged;
                    _logWatcher.Dispose();
                    _logWatcher = null;
                }

                if (_applicationLogWatcher != null)
                {
                    _applicationLogWatcher.EnableRaisingEvents = false;
                    _applicationLogWatcher.Changed -= OnApplicationLogChanged;
                    _applicationLogWatcher.Created -= OnApplicationLogChanged;
                    _applicationLogWatcher.Dispose();
                    _applicationLogWatcher = null;
                }

                _lastApplicationLogPosition = 0;
                _currentMapKey = null;

                // Drop the cached timelines with their resume offsets: monitoring may restart
                // against a different log folder, and a stale offset there would skip the
                // transitions that attribute the next session's events.
                lock (_timelineLock)
                {
                    _liveTimelines.Clear();
                }

                _isWatching = false;
                MonitoringStatusChanged?.Invoke(this, false);
            }
        }

        private void OnLogFileChanged(object sender, FileSystemEventArgs e)
        {
            // Debounce events (file system can fire multiple events)
            var now = DateTime.Now;
            if ((now - _lastEventTime).TotalMilliseconds < 500 && e.FullPath == _lastModifiedFile)
                return;

            _lastEventTime = now;
            _lastModifiedFile = e.FullPath;

            // Process new events from the modified file
            Task.Run(() => ProcessLatestLogEvents(e.FullPath));
        }

        private void OnApplicationLogChanged(object sender, FileSystemEventArgs e)
        {
            DebugLog($"OnApplicationLogChanged: {e.FullPath}");

            // Debounce events
            var now = DateTime.Now;
            if ((now - _lastMapEventTime).TotalMilliseconds < 300 && e.FullPath == _lastMapModifiedFile)
            {
                DebugLog($"Debounced - skipping");
                return;
            }

            _lastMapEventTime = now;
            _lastMapModifiedFile = e.FullPath;

            // Process new lines for map detection
            Task.Run(() => ProcessApplicationLogForMap(e.FullPath));
        }

        private async Task ProcessApplicationLogForMap(string filePath)
        {
            try
            {
                await Task.Delay(100); // Small delay for file write completion

                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var fileLength = stream.Length;

                DebugLog($"ProcessApplicationLogForMap: fileLength={fileLength}, lastPos={_lastApplicationLogPosition}");

                // Only read new content
                if (fileLength <= _lastApplicationLogPosition)
                {
                    DebugLog($"No new content to read");
                    return;
                }

                stream.Seek(_lastApplicationLogPosition, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                var newContent = await reader.ReadToEndAsync();
                _lastApplicationLogPosition = fileLength;

                DebugLog($"Read {newContent.Length} chars of new content");

                // Parse for map loading events
                var detectedMap = ParseMapFromLogContent(newContent);
                DebugLog($"ParseMapFromLogContent result: {detectedMap ?? "null"}");

                if (!string.IsNullOrEmpty(detectedMap) && detectedMap != _currentMapKey)
                {
                    _currentMapKey = detectedMap;
                    DebugLog($"Map changed! Firing MapDetected event: {detectedMap}");
                    MapDetected?.Invoke(this, new MapDetectedEventArgs(detectedMap, DateTime.Now));
                }
            }
            catch (Exception ex)
            {
                DebugLog($"Error reading application log: {ex.Message}");
            }
        }

        /// <summary>
        /// Parse map name from log content using multiple detection patterns
        /// </summary>
        private string? ParseMapFromLogContent(string content)
        {
            // Pattern 1 (most reliable): [Transit] Locations:MapName ->
            // Example: "[Transit] Flag:None, RaidId:..., Locations:Shoreline ->"
            // This appears after map is fully loaded and raid starts
            var transitMatch = System.Text.RegularExpressions.Regex.Match(
                content,
                @"\[Transit\].*Locations:([a-zA-Z0-9_]+)\s*->",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (transitMatch.Success)
            {
                var mapName = transitMatch.Groups[1].Value;
                _log.Debug($"Transit pattern matched: {mapName}");
                if (TryGetMapKey(mapName, out var mapKey))
                {
                    return mapKey;
                }
            }

            // Pattern 2: scene preset path:maps/<mapname>.bundle
            // Examples:
            //   "scene preset path:maps/shoreline_preset.bundle"
            //   "scene preset path:maps/shopping_mall.bundle"
            //   "scene preset path:maps/city_preset.bundle"
            // This appears when map loading starts
            var scenePresetMatch = System.Text.RegularExpressions.Regex.Match(
                content,
                @"scene preset path:maps/([a-zA-Z0-9_]+)\.bundle",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (scenePresetMatch.Success)
            {
                var mapName = scenePresetMatch.Groups[1].Value;
                _log.Debug($"Scene preset pattern matched: {mapName}");
                if (TryGetMapKey(mapName, out var mapKey))
                {
                    return mapKey;
                }
            }

            // Pattern 3: LocationLoaded (backup pattern, less specific about which map)
            // This just confirms a location was loaded but Transit pattern is preferred

            return null;
        }

        /// <summary>
        /// Find the last map from application logs (for initial map selection)
        /// </summary>
        /// <param name="logFolderPath">Log folder path</param>
        /// <returns>Last detected map key, or null if not found</returns>
        public string? FindLastMapFromLogs(string? logFolderPath = null)
        {
            var path = logFolderPath ?? SettingsService.Instance.LogFolderPath;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return null;

            try
            {
                // Find the most recent application log file
                var logFiles = Directory.GetFiles(path, "application*.log", SearchOption.AllDirectories)
                    .OrderByDescending(f => File.GetLastWriteTime(f))
                    .Take(3)  // Check last 3 log files
                    .ToList();

                if (logFiles.Count == 0)
                    return null;

                foreach (var logFile in logFiles)
                {
                    var mapKey = FindLastMapInFile(logFile);
                    if (!string.IsNullOrEmpty(mapKey))
                    {
                        _log.Info($"Found last map from logs: {mapKey}");
                        return mapKey;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Error finding last map: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Find the last map mentioned in a single log file (reads from end)
        /// </summary>
        private string? FindLastMapInFile(string filePath)
        {
            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // Read last 200KB of file (should be enough to find recent map)
                var readSize = Math.Min(stream.Length, 200 * 1024);
                if (readSize <= 0) return null;

                stream.Seek(-readSize, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                var content = reader.ReadToEnd();

                // Split into lines and search from end
                var lines = content.Split('\n');
                string? lastFoundMap = null;

                // Search through all lines to find the LAST map reference
                foreach (var line in lines)
                {
                    var mapKey = ParseMapFromLogContent(line);
                    if (!string.IsNullOrEmpty(mapKey))
                    {
                        lastFoundMap = mapKey;
                    }
                }

                return lastFoundMap;
            }
            catch (Exception ex)
            {
                _log.Error($"Error reading log file {filePath}: {ex.Message}");
                return null;
            }
        }

        private async Task ProcessLatestLogEvents(string filePath)
        {
            try
            {
                // Read the last portion of the file to get recent events
                var events = await ParseLogFileAsync(filePath, tailOnly: true);
                if (events.Count == 0) return;

                // Attribute against the session folder this notification log lives in, refreshed
                // first so a mode line written moments ago is already in the timeline.
                var timeline = LiveTimelineFor(filePath);
                var dropped = 0;

                foreach (var evt in events)
                {
                    evt.OwnerProfile = ResolveOwner(timeline, evt);

                    // Only fire for recent events (within last minute)
                    if ((DateTime.Now - evt.Timestamp).TotalMinutes >= 1) continue;

                    // PRD R3: an event with no session mode evidence has no destination, so it is
                    // never raised. Dropping it here rather than at each subscriber is what makes
                    // the rule enforceable: a consumer that forgets the null check cannot record
                    // it under whatever profile happens to be selected.
                    if (evt.OwnerProfile == null)
                    {
                        dropped++;
                        continue;
                    }

                    QuestEventDetected?.Invoke(this, evt);
                }

                if (dropped > 0)
                {
                    _log.Warning(
                        $"Dropped {dropped} live quest events with no session mode evidence in " +
                        $"{Path.GetDirectoryName(filePath)}");
                }
            }
            catch (Exception ex)
            {
                // A swallowed failure here is invisible: the whole batch of live quest events is
                // lost and the player only finds out when a finished raid never registers.
                _log.Warning($"Failed to process live quest events from {filePath}: {ex}");
            }
        }

        /// <summary>
        /// The profile a parsed event belongs to, or null when nothing says. An event whose log
        /// block carried no <c>dt</c> has no real time to look up, so it is left unattributed
        /// instead of being resolved against a substituted "now", which would return whatever mode
        /// the folder ended in.
        /// </summary>
        private static AppProfile? ResolveOwner(SessionModeTimeline? timeline, QuestLogEvent evt)
            => evt.HasTimestamp ? timeline?.Resolve(evt.Timestamp) : null;

        /// <summary>
        /// The live timeline for the session folder holding <paramref name="notificationLogPath"/>,
        /// created on first use and refreshed on every call. Null when the path has no parent
        /// directory, which leaves its events unattributed rather than guessed at.
        /// </summary>
        private SessionModeTimeline? LiveTimelineFor(string notificationLogPath)
        {
            var folder = Path.GetDirectoryName(notificationLogPath);
            if (string.IsNullOrEmpty(folder)) return null;

            lock (_timelineLock)
            {
                if (!_liveTimelines.TryGetValue(folder, out var timeline))
                {
                    timeline = SessionModeTimeline.Build(folder);
                    _liveTimelines[folder] = timeline;
                }
                else
                {
                    timeline.Refresh();
                }

                return timeline;
            }
        }

        #endregion

        #region Log Parsing

        /// <summary>
        /// Parse all log files in a directory for quest events, stamping each with the profile
        /// the session that produced it was running.
        /// <para>
        /// One run covers every session folder the game still retains, across all game modes, so
        /// attribution has to be per event. It happens here, at parse time, rather than at apply
        /// time: here the folder each event came from is still known, so one timeline is built
        /// per folder instead of per event, and every consumer of a
        /// <see cref="QuestLogEvent"/> (present or future) receives it already attributed
        /// rather than having to remember to resolve it.
        /// </para>
        /// </summary>
        /// <param name="logFolderPath">Root folder holding EFT's session folders.</param>
        /// <param name="progress">Progress reporter.</param>
        /// <param name="daysRange">
        /// Number of days to look back, 0 for all. Applied to the SESSION FOLDERS, before any of
        /// them is read: building a timeline means reading a session's whole application log, so
        /// filtering only the parsed events afterwards made "last 7 days" cost the same as "all"
        /// while the player waits behind a modal overlay.
        /// </param>
        public async Task<List<QuestLogEvent>> ParseLogDirectoryAsync(
            string logFolderPath, IProgress<string>? progress = null, int daysRange = 0)
        {
            var allEvents = new List<QuestLogEvent>();

            if (!Directory.Exists(logFolderPath))
                return allEvents;

            // Find all push-notifications log files
            var logFiles = Directory.GetFiles(logFolderPath, "*push-notifications*.log", SearchOption.AllDirectories)
                .OrderBy(f => File.GetLastWriteTime(f))
                .ToList();

            if (daysRange > 0)
            {
                var cutoff = DateTime.Now.AddDays(-daysRange);
                var discovered = logFiles.Count;
                logFiles = logFiles.Where(f => IsWithinCutoff(f, cutoff)).ToList();
                if (logFiles.Count < discovered)
                {
                    _log.Debug(
                        $"Skipping {discovered - logFiles.Count} of {discovered} log files older than {cutoff:yyyy-MM-dd}");
                }
            }

            progress?.Report($"Found {logFiles.Count} log files");

            // One timeline per session folder, not per file and not per event: several
            // notification logs can share a folder, and building it per event would re-read the
            // application log thousands of times.
            var timelines = new Dictionary<string, SessionModeTimeline>(StringComparer.OrdinalIgnoreCase);

            int processed = 0;
            foreach (var file in logFiles)
            {
                try
                {
                    var events = await ParseLogFileAsync(file);

                    var folder = Path.GetDirectoryName(file);
                    SessionModeTimeline? timeline = null;
                    if (!string.IsNullOrEmpty(folder))
                    {
                        if (!timelines.TryGetValue(folder, out timeline))
                        {
                            timeline = SessionModeTimeline.Build(folder);
                            timelines[folder] = timeline;
                        }
                    }

                    foreach (var evt in events)
                    {
                        evt.OwnerProfile = ResolveOwner(timeline, evt);
                    }

                    allEvents.AddRange(events);

                    processed++;
                    progress?.Report($"Parsed {processed}/{logFiles.Count} files ({allEvents.Count} events)");
                }
                catch
                {
                    // Skip files that can't be read
                }
            }

            // Sort by timestamp
            allEvents = allEvents.OrderBy(e => e.Timestamp).ToList();

            return allEvents;
        }

        /// <summary>
        /// EFT names a session folder <c>log_&lt;yyyy.MM.dd&gt;_&lt;HH-mm-ss&gt;_&lt;version&gt;</c>,
        /// e.g. <c>log_2026.08.12_21-03-11_1.1.0.46657</c>. Only the leading date and time are
        /// matched, because the version tail varies and is not needed.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex SessionFolderStampRegex = new(
            @"^log_(\d{4}\.\d{2}\.\d{2}_\d{2}-\d{2}-\d{2})",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        private const string SessionFolderStampFormat = "yyyy.MM.dd_HH-mm-ss";

        /// <summary>
        /// Whether a notification log may still hold events at or after <paramref name="cutoff"/>.
        /// <para>
        /// Deliberately generous: it prunes work, so an unfamiliar folder name, an unreadable
        /// timestamp, or a session that started before the cutoff and ran past it must all keep
        /// the file. A session's own end is its log's last write, so that is the primary test;
        /// the folder name's start stamp is the fallback for when the file time is unavailable.
        /// Both are parsed with the invariant culture: a Buddhist or Persian ambient calendar
        /// would otherwise read the folder stamp as a different century and discard everything.
        /// </para>
        /// </summary>
        private static bool IsWithinCutoff(string logFilePath, DateTime cutoff)
        {
            try
            {
                if (File.GetLastWriteTime(logFilePath) >= cutoff) return true;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }

            var folderName = Path.GetFileName(Path.GetDirectoryName(logFilePath));
            if (string.IsNullOrEmpty(folderName)) return true;

            var match = SessionFolderStampRegex.Match(folderName);
            if (!match.Success) return true;

            return !DateTime.TryParseExact(
                       match.Groups[1].Value, SessionFolderStampFormat,
                       System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.None, out var startedAt)
                   || startedAt >= cutoff;
        }

        /// <summary>
        /// Parse a single log file for quest events
        /// </summary>
        public async Task<List<QuestLogEvent>> ParseLogFileAsync(string filePath, bool tailOnly = false)
        {
            var events = new List<QuestLogEvent>();

            if (!File.Exists(filePath))
                return events;

            try
            {
                // Read file with shared access (game might be writing)
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                var fileName = Path.GetFileName(filePath);

                // If tailOnly, skip to last 50KB
                if (tailOnly && stream.Length > 50000)
                {
                    stream.Seek(-50000, SeekOrigin.End);
                    reader.ReadLine(); // Skip partial line
                }

                // Read entire content for multiline JSON parsing
                var content = await reader.ReadToEndAsync();
                var parsedEvents = ParseLogContent(content, fileName);
                events.AddRange(parsedEvents);
            }
            catch
            {
                // File access error, return what we have
            }

            return events;
        }

        /// <summary>
        /// Parse log content with multiline JSON support
        /// </summary>
        private List<QuestLogEvent> ParseLogContent(string content, string? sourceFile)
        {
            var events = new List<QuestLogEvent>();

            // Split into lines
            var lines = content.Split('\n');
            var jsonBuilder = new System.Text.StringBuilder();
            bool inJson = false;
            int braceCount = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');

                // Check if this line starts a JSON block (line starting with '{')
                if (!inJson && line.TrimStart().StartsWith("{"))
                {
                    inJson = true;
                    jsonBuilder.Clear();
                    braceCount = 0;
                }

                if (inJson)
                {
                    jsonBuilder.AppendLine(line);

                    // Count braces
                    foreach (char c in line)
                    {
                        if (c == '{') braceCount++;
                        else if (c == '}') braceCount--;
                    }

                    // JSON block complete
                    if (braceCount == 0)
                    {
                        inJson = false;
                        var jsonString = jsonBuilder.ToString();

                        var evt = ParseJsonBlock(jsonString, sourceFile);
                        if (evt != null)
                        {
                            events.Add(evt);
                        }
                    }
                }
            }

            return events;
        }

        /// <summary>
        /// Parse a JSON block for quest event
        /// </summary>
        private QuestLogEvent? ParseJsonBlock(string jsonString, string? sourceFile)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonString);
                var root = doc.RootElement;

                // Check if this is a new_message notification
                if (!root.TryGetProperty("type", out var typeElement) ||
                    typeElement.GetString() != "new_message")
                    return null;

                // Get message element
                if (!root.TryGetProperty("message", out var messageElement))
                    return null;

                // Get message type
                if (!messageElement.TryGetProperty("type", out var msgTypeElement))
                    return null;

                var msgType = msgTypeElement.GetInt32();

                // Check if this is a quest-related message
                if (msgType != MSG_TYPE_STARTED && msgType != MSG_TYPE_COMPLETED && msgType != MSG_TYPE_FAILED)
                    return null;

                // Get templateId (contains quest ID)
                if (!messageElement.TryGetProperty("templateId", out var templateIdElement))
                    return null;

                var templateId = templateIdElement.GetString();
                if (string.IsNullOrEmpty(templateId))
                    return null;

                // Extract quest ID (first token in templateId)
                var questId = templateId.Split(' ')[0];

                // Get dialogId (trader ID)
                var traderId = "";
                if (root.TryGetProperty("dialogId", out var dialogIdElement))
                {
                    traderId = dialogIdElement.GetString() ?? "";
                }

                // Get timestamp. A block with no readable "dt" keeps DateTime.Now as an ordering
                // placeholder but is marked as having no timestamp: "now" resolves against the
                // session timeline as the LAST transition in the folder, so attributing an undated
                // event with it would file an old PvE event under whatever mode the session ended
                // in. HasTimestamp is what keeps that guess from being made.
                var timestamp = DateTime.Now;
                var hasTimestamp = false;
                if (messageElement.TryGetProperty("dt", out var dtElement) &&
                    dtElement.TryGetInt64(out var unixTime))
                {
                    timestamp = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime;
                    hasTimestamp = true;
                }

                // Determine event type
                var eventType = msgType switch
                {
                    MSG_TYPE_STARTED => QuestEventType.Started,
                    MSG_TYPE_COMPLETED => QuestEventType.Completed,
                    MSG_TYPE_FAILED => QuestEventType.Failed,
                    _ => QuestEventType.Started
                };

                return new QuestLogEvent
                {
                    QuestId = questId,
                    EventType = eventType,
                    TraderId = traderId,
                    Timestamp = timestamp,
                    HasTimestamp = hasTimestamp,
                    OriginalLine = jsonString.Substring(0, Math.Min(200, jsonString.Length)),
                    SourceFile = sourceFile
                };
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Quest Synchronization

        /// <summary>
        /// Synchronize quest progress from log files
        /// </summary>
        /// <param name="logFolderPath">Path to log folder</param>
        /// <param name="progress">Progress reporter, null for none</param>
        /// <param name="daysRange">Number of days to look back (0 = all logs)</param>
        /// <remarks>
        /// Neither parameter is optional, on purpose. The defect PRD R8 records is a caller that
        /// simply left the range off: it took the default of 0, the configured
        /// <c>SettingsService.SyncDaysRange</c> reached nothing, and every sync silently covered
        /// every retained log. A required parameter turns the next such omission into a compile
        /// error instead of a setting that quietly stops working.
        /// </remarks>
        public Task<SyncResult> SyncFromLogsAsync(
            string logFolderPath, IProgress<string>? progress, int daysRange)
            => SyncFromLogsAsync(
                logFolderPath, QuestProgressService.Instance, QuestGraphService.Instance, progress, daysRange);

        /// <summary>
        /// Same sync with its two read-only collaborators supplied explicitly. Neither is written
        /// to: the graph answers prerequisite questions, and the progress service answers the
        /// profile-independent task lookups plus, for the one profile that is loaded, the rows in
        /// its current snapshot. Naming them makes the whole run drivable from a test without
        /// seeding two singletons that other tests in the same assembly share.
        /// </summary>
        internal async Task<SyncResult> SyncFromLogsAsync(
            string logFolderPath,
            QuestProgressService progressService,
            QuestGraphService graphService,
            IProgress<string>? progress,
            int daysRange)
        {
            var result = new SyncResult();

            _log.Info($"Starting sync from: {logFolderPath}");
            progress?.Report("Scanning log files...");

            // Parse the log files within range. The range is handed down so whole session folders
            // outside it are skipped before their application logs are read; what comes back is
            // then filtered again per event, because a session that spans the cutoff is retained
            // whole and only some of its events belong.
            var events = await ParseLogDirectoryAsync(logFolderPath, progress, daysRange);
            _log.Info($"Found {events.Count} quest events in logs");

            if (daysRange > 0)
            {
                var cutoffDate = DateTime.Now.AddDays(-daysRange);
                var originalCount = events.Count;
                events = events.Where(e => e.Timestamp >= cutoffDate).ToList();
                progress?.Report($"Filtered to {events.Count}/{originalCount} events from last {daysRange} days");
            }

            result.TotalEventsFound = events.Count;

            if (events.Count == 0)
            {
                result.Errors.Add("No quest events found in logs");
                return result;
            }

            // PRD R3: an event from before the first mode marker in its session folder (or one
            // whose log block carried no timestamp at all) has no evidence for where it belongs.
            // It is dropped and counted, never assigned to a default. The app has already had one
            // defect of exactly that shape, where a value that could not distinguish permanent PvP
            // from seasonal play was used to pick storage and merged the two.
            result.UnattributedEventCount = events.Count(e => e.OwnerProfile == null);
            if (result.UnattributedEventCount > 0)
            {
                _log.Warning(
                    $"{result.UnattributedEventCount} quest events carry no session mode evidence and were dropped");
            }

            var attributed = events.Where(e => e.OwnerProfile.HasValue).ToList();

            progress?.Report($"Processing {attributed.Count} quest events...");

            var run = new SyncRun
            {
                Progress = progressService,
                Graph = graphService,
                Result = result,
            };

            // One pass per profile the logs cover. The passes are independent: each compares its
            // events against ITS OWN rows, never against another profile's, because at most one
            // profile is loaded and a sync routinely spans several (PRD R1). Each returns only its
            // own changes, so no pass can read another's.
            var changes = new List<QuestChangeInfo>();
            foreach (var group in attributed.GroupBy(e => e.OwnerProfile!.Value))
            {
                var owner = group.Key;
                var ownerId = ProfileService.GetProfileId(owner);
                var ownerEvents = group.ToList();

                // The reset fence (PRD R6 of feature-complete-profile-reset.md): events not
                // after the owner's reset watermark describe progress the player deliberately
                // removed, and the game retains their session logs for days. The boundary rule
                // itself lives in ResetFence.IsFencedOut, so the count below and the events that
                // survive it stay exact complements instead of two hand-written comparisons that
                // can drift. This fence sits at scan time because its count is what the player is
                // shown in the sync summary, before confirming anything is applied.
                var resetAt = await Store.GetProgressResetAtAsync(ownerId);
                if (resetAt.HasValue)
                {
                    var preReset = ownerEvents.Count(e => ResetFence.IsFencedOut(e.Timestamp, resetAt));
                    if (preReset > 0)
                    {
                        result.PreResetEventCount += preReset;
                        ownerEvents = ownerEvents
                            .Where(e => !ResetFence.IsFencedOut(e.Timestamp, resetAt))
                            .ToList();
                        _log.Info(
                            $"Dropped {preReset} quest events for {ownerId} that are not after " +
                            $"its reset at {resetAt.Value:o}");
                    }
                    if (ownerEvents.Count == 0) continue;
                }

                // The loaded profile's rows are the snapshot, not the store: hand edits publish to
                // the snapshot synchronously and persist fire-and-forget, so a fresh store read can
                // be behind what the user is looking at. It would also disagree with the apply step,
                // which plans the loaded profile from the snapshot and only an off-screen profile
                // from the store (QuestProgressService.ApplyForOwnerAsync). Every other profile MUST
                // come from the store: at most one is loaded.
                var snapshot = progressService.Snapshot;
                IReadOnlyDictionary<string, QuestStatus> storedProgress =
                    string.Equals(snapshot.ProfileId, ownerId, StringComparison.Ordinal)
                        ? snapshot.Quests
                        : await Store.LoadQuestProgressAsync(ownerId);

                changes.AddRange(RunProfilePass(owner, ownerEvents, storedProgress, run));
            }

            // Sort by timestamp (oldest first) for chronological display
            result.QuestsToComplete = changes.OrderBy(q => q.Timestamp).ToList();

            progress?.Report($"Found {result.QuestsToComplete.Count} quests to update");

            _log.Info(
                $"Sync complete: {result.TotalEventsFound} events, {result.QuestsToComplete.Count} to apply, " +
                $"{result.AlreadyCurrentCount} already current, {result.UnattributedEventCount} unattributed, " +
                $"{result.PreResetEventCount} pre-reset, " +
                $"{result.InProgressQuests.Count} in progress, {result.UnmatchedQuestIds.Count} unmatched");

            // 매칭되지 않은 ID 샘플 출력
            if (result.UnmatchedQuestIds.Count > 0)
            {
                var sampleUnmatched = result.UnmatchedQuestIds.Take(10).ToList();
                _log.Debug($"Sample unmatched IDs: {string.Join(", ", sampleUnmatched)}");

                // DB의 샘플 ID도 출력
                var sampleDbIds = progressService.AllTasks
                    .SelectMany(t => t.Ids ?? Enumerable.Empty<string>())
                    .Where(id => !string.IsNullOrEmpty(id))
                    .Take(10)
                    .ToList();
                _log.Debug($"Sample DB IDs: {string.Join(", ", sampleDbIds)}");
            }

            return result;
        }

        /// <summary>
        /// What every profile pass in one sync run shares: the profile-independent quest data,
        /// and the result the passes accumulate their statistics into. Grouped rather than passed
        /// positionally because the pass itself only varies by three things (the owner, its
        /// events, and its stored rows) and those are what a reader needs to see at the call site.
        /// </summary>
        private sealed class SyncRun
        {
            public required QuestProgressService Progress { get; init; }
            public required QuestGraphService Graph { get; init; }
            public required SyncResult Result { get; init; }

            /// <summary>
            /// Progress keys already counted as in progress, across every pass.
            /// <see cref="SyncResult.InProgressQuests"/> is ordered output, not a set: without
            /// this a quest started in two profiles would be counted twice and "still in progress:
            /// N" would exceed the number of distinct quests.
            /// </summary>
            public HashSet<string> InProgressKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The key a quest is deduplicated by across profile passes: its normalized name when it
        /// has one, its first id otherwise, so two tasks are never merged by an empty key.
        /// </summary>
        private static string DedupKeyOf(TarkovTask task)
            => !string.IsNullOrEmpty(task.NormalizedName)
                ? task.NormalizedName!
                : task.Ids?.FirstOrDefault(id => !string.IsNullOrEmpty(id)) ?? task.Name;

        /// <summary>
        /// Derives one profile's changes from the events attributed to it, compared against
        /// <paramref name="storedProgress"/> (that profile's own rows), and returns them.
        /// <para>
        /// This is the pre-attribution sync body, with every "is this already recorded?" question
        /// answered from the OWNING profile's rows instead of from whatever the loaded cache holds.
        /// Reading the cache unconditionally was correct only while a run could touch a single
        /// profile; it named the wrong profile for every group but at most one. The caller supplies
        /// the loaded profile's rows from the snapshot and every other profile's from the store.
        /// </para>
        /// <para>
        /// The changes are returned rather than appended to a run-wide list: a pass that shared
        /// the list had to filter the whole accumulated set back down to its own owner to see what
        /// it had just produced, which is one read away from acting on another pass's rows.
        /// </para>
        /// </summary>
        private List<QuestChangeInfo> RunProfilePass(
            AppProfile owner,
            List<QuestLogEvent> events,
            IReadOnlyDictionary<string, QuestStatus> storedProgress,
            SyncRun run)
        {
            var progressService = run.Progress;
            var graphService = run.Graph;
            var result = run.Result;
            var questsToComplete = new List<QuestChangeInfo>();

            // STEP 1: Determine final state for each quest (last event wins)
            // Key: normalizedName, Value: (finalEventType, timestamp, task)
            var questFinalStates = new Dictionary<string, (QuestEventType EventType, DateTime Timestamp, TarkovTask Task)>(StringComparer.OrdinalIgnoreCase);
            var startedQuests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var evt in events)
            {
                var task = progressService.GetTaskById(evt.QuestId);
                if (task == null)
                {
                    if (!result.UnmatchedQuestIds.Contains(evt.QuestId))
                        result.UnmatchedQuestIds.Add(evt.QuestId);
                    continue;
                }

                var normalizedName = task.NormalizedName ?? "";
                if (string.IsNullOrEmpty(normalizedName)) continue;

                // Track started quests (for in-progress detection)
                if (evt.EventType == QuestEventType.Started)
                {
                    startedQuests.Add(normalizedName);
                }

                // Last event for each quest determines final state
                questFinalStates[normalizedName] = (evt.EventType, evt.Timestamp, task);

                // Count events
                switch (evt.EventType)
                {
                    case QuestEventType.Started: result.QuestsStarted++; break;
                    case QuestEventType.Completed: result.QuestsCompleted++; break;
                    case QuestEventType.Failed: result.QuestsFailed++; break;
                }
            }

            // STEP 2: Build questsToComplete based on final states
            var processedPrereqs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // First, collect all quests that will be in a terminal state (Completed or Failed)
            var terminalStateQuests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in questFinalStates)
            {
                if (kvp.Value.EventType == QuestEventType.Completed || kvp.Value.EventType == QuestEventType.Failed)
                {
                    terminalStateQuests.Add(kvp.Key);
                }
            }

            foreach (var kvp in questFinalStates)
            {
                var normalizedName = kvp.Key;
                var (eventType, timestamp, task) = kvp.Value;
                var currentStatus = QuestProgressService.StoredStatusOf(storedProgress, task);

                switch (eventType)
                {
                    case QuestEventType.Started:
                        // Started quests: only complete prerequisites, not the quest itself
                        // Quest stays Active
                        //
                        // One exception for the statistics: a quest whose last log event is
                        // Started but which is already recorded Done for this profile is not "in
                        // progress" (the loop below excludes it) and produces no change, so
                        // without this it would fall into no bucket at all and the summary's
                        // numbers would not add up. Stored state already matches the log, which is
                        // exactly what "already current" counts.
                        if (currentStatus == QuestStatus.Done)
                        {
                            result.AlreadyCurrentCount++;
                        }
                        break;

                    case QuestEventType.Completed:
                        // Only add if status will actually change
                        if (currentStatus != QuestStatus.Done)
                        {
                            questsToComplete.Add(new QuestChangeInfo
                            {
                                QuestName = task.Name,
                                NormalizedName = normalizedName,
                                Trader = task.Trader,
                                IsPrerequisite = false,
                                ChangeType = QuestEventType.Completed,
                                OwnerProfile = owner,
                                Timestamp = timestamp
                            });
                        }
                        else
                        {
                            result.AlreadyCurrentCount++;
                        }
                        break;

                    case QuestEventType.Failed:
                        // Only add if status will actually change
                        if (currentStatus != QuestStatus.Failed)
                        {
                            questsToComplete.Add(new QuestChangeInfo
                            {
                                QuestName = task.Name,
                                NormalizedName = normalizedName,
                                Trader = task.Trader,
                                IsPrerequisite = false,
                                ChangeType = QuestEventType.Failed,
                                OwnerProfile = owner,
                                Timestamp = timestamp
                            });
                        }
                        else
                        {
                            result.AlreadyCurrentCount++;
                        }
                        break;
                }

                // STEP 3: Complete prerequisites for quests that were COMPLETED or FAILED only
                // For Started quests, we cannot reliably determine prerequisite completion
                // because a quest can be started even if prerequisites are still in progress in some cases
                if (eventType == QuestEventType.Started)
                    continue;

                var prereqs = graphService.GetAllPrerequisites(normalizedName);
                foreach (var prereq in prereqs)
                {
                    if (prereq.NormalizedName == null) continue;
                    if (processedPrereqs.Contains(prereq.NormalizedName)) continue;

                    // Skip if this prereq will have a terminal state from logs
                    if (terminalStateQuests.Contains(prereq.NormalizedName)) continue;

                    // Skip if prereq has no event in logs (we cannot determine its state)
                    // This prevents auto-completing quests that have no log evidence
                    if (!questFinalStates.ContainsKey(prereq.NormalizedName))
                        continue;

                    // Skip if prereq is started but not in terminal state (still in progress)
                    if (startedQuests.Contains(prereq.NormalizedName) && !terminalStateQuests.Contains(prereq.NormalizedName))
                        continue;

                    var prereqStatus = QuestProgressService.StoredStatusOf(storedProgress, prereq);
                    if (prereqStatus != QuestStatus.Done)
                    {
                        // Skip alternative quests - will be collected separately
                        if (progressService.HasAlternativeQuests(prereq))
                        {
                            _log.Debug($"Skipping alternative quest prereq: {prereq.Name}");
                            continue;
                        }

                        questsToComplete.Add(new QuestChangeInfo
                        {
                            QuestName = prereq.Name,
                            NormalizedName = prereq.NormalizedName,
                            Trader = prereq.Trader,
                            IsPrerequisite = true,
                            ChangeType = QuestEventType.Completed,
                            OwnerProfile = owner,
                            Timestamp = timestamp
                        });
                        processedPrereqs.Add(prereq.NormalizedName);
                        result.PrerequisitesAutoCompleted++;
                    }
                }
            }

            // STEP 4: Collect alternative quest groups that need user selection
            // These are mutually exclusive quests where user must choose which one they completed
            result.AlternativeQuestGroups.AddRange(
                CollectAlternativeQuestGroups(
                    owner, progressService, graphService, storedProgress, questFinalStates));

            // Build InProgressQuests list: quests whose final state is Started (not Completed/Failed).
            // Deduplicated across passes: the same quest can be in progress in two profiles at
            // once, and the summary reports a count of distinct quests.
            foreach (var kvp in questFinalStates)
            {
                var (eventType, _, task) = kvp.Value;

                // Only include quests whose FINAL state is Started
                if (eventType == QuestEventType.Started)
                {
                    // Check if already done in saved progress
                    var currentStatus = QuestProgressService.StoredStatusOf(storedProgress, task);
                    if (currentStatus != QuestStatus.Done && run.InProgressKeys.Add(DedupKeyOf(task)))
                    {
                        result.InProgressQuests.Add(task);
                    }
                }
            }

            return questsToComplete;
        }

        /// <summary>
        /// What one apply run wrote: how many rows landed in each profile, and which profiles threw.
        /// A failed partition is not the same as an untouched one, and a bare count dictionary cannot
        /// tell them apart: the failed one is simply missing from it.
        /// </summary>
        public sealed record QuestApplyOutcome(
            Dictionary<AppProfile, int> AppliedByProfile, List<AppProfile> FailedProfiles);

        /// <summary>
        /// Applies derived quest changes, each to the profile it was attributed to, and reports
        /// how many rows landed in each and which profiles failed.
        /// <para>
        /// One sync run distributes across every profile the retained logs cover, so the changes
        /// are grouped by owner and the batch save runs once per group with that group's profile
        /// id. Nothing here consults the selected profile: that is the misattribution this whole
        /// change removes (PRD R1).
        /// </para>
        /// </summary>
        public Task<QuestApplyOutcome> ApplyQuestChangesAsync(List<QuestChangeInfo> changes)
            => ApplyQuestChangesAsync(changes, QuestProgressService.Instance);

        /// <summary>Same apply with the progress service supplied explicitly, for tests.</summary>
        internal async Task<QuestApplyOutcome> ApplyQuestChangesAsync(
            List<QuestChangeInfo> changes, QuestProgressService progressService)
        {
            var selectedChanges = changes.Where(c => c.IsSelected).ToList();
            var appliedByProfile = new Dictionary<AppProfile, int>();
            var failedProfiles = new List<AppProfile>();

            _log.Info($"ApplyQuestChangesAsync: {changes.Count} total changes, {selectedChanges.Count} selected");

            foreach (var group in selectedChanges.GroupBy(c => c.OwnerProfile))
            {
                // One failing profile must not cost the profiles that already succeeded their
                // report: the counts below are the only signal the player gets about where a sync
                // landed, and losing all of them because one partition threw hides the successes
                // as well as the failure. The failure is not swallowed either, only isolated: the
                // catch names the profile in FailedProfiles so the summary can report it.
                try
                {
                    // Build batch of changes for this profile
                    var batchChanges = new List<(TarkovTask Task, QuestStatus Status)>();

                    foreach (var change in group)
                    {
                        var task = progressService.GetTask(change.NormalizedName);
                        if (task == null)
                        {
                            _log.Warning($"Task not found for NormalizedName: {change.NormalizedName}");
                            continue;
                        }

                        var status = change.ChangeType switch
                        {
                            QuestEventType.Completed => QuestStatus.Done,
                            QuestEventType.Failed => QuestStatus.Failed,
                            _ => QuestStatus.Active
                        };

                        if (status != QuestStatus.Active)
                        {
                            batchChanges.Add((task, status));
                            _log.Debug($"Queued change for {group.Key}: {change.NormalizedName} -> {change.ChangeType}");
                        }
                    }

                    // Apply this profile's changes in one batch (single DB transaction, single UI update)
                    if (batchChanges.Count == 0) continue;

                    // Report what was WRITTEN, not what was queued. The two differ routinely: the
                    // batch skips rows already recorded and adds Failed rows for the mutually
                    // exclusive alternatives a completion rules out, and a profile switch between
                    // the read and the write can leave it writing nothing at all. A profile
                    // nothing landed in is left out of the map entirely, so the summary cannot
                    // name a partition it did not touch.
                    var applied = await progressService.ApplyQuestChangesBatchAsync(batchChanges, group.Key);
                    if (applied > 0)
                    {
                        appliedByProfile[group.Key] = applied;
                    }

                    _log.Info($"Batch applied {applied} quest records to {group.Key} from {batchChanges.Count} queued changes");
                }
                catch (Exception ex)
                {
                    _log.Error($"Failed to apply quest changes to {group.Key}", ex);
                    failedProfiles.Add(group.Key);
                }
            }

            _log.Info("ApplyQuestChangesAsync completed");
            return new QuestApplyOutcome(appliedByProfile, failedProfiles);
        }

        /// <summary>
        /// Collect alternative quest groups that need user selection
        /// </summary>
        private List<AlternativeQuestGroup> CollectAlternativeQuestGroups(
            AppProfile owner,
            QuestProgressService progressService,
            QuestGraphService graphService,
            IReadOnlyDictionary<string, QuestStatus> storedProgress,
            Dictionary<string, (QuestEventType EventType, DateTime Timestamp, TarkovTask Task)> questFinalStates)
        {
            var groups = new List<AlternativeQuestGroup>();
            var processedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Find all alternative quest groups that are prerequisites for started/completed quests
            foreach (var kvp in questFinalStates)
            {
                var normalizedName = kvp.Key;
                var task = kvp.Value.Task;

                // Get all prerequisites
                var prereqs = graphService.GetAllPrerequisites(normalizedName);

                foreach (var prereq in prereqs)
                {
                    if (prereq.NormalizedName == null) continue;
                    if (!progressService.HasAlternativeQuests(prereq)) continue;

                    // Skip if already processed this group
                    var groupKey = GetAlternativeGroupKey(prereq);
                    if (processedGroups.Contains(groupKey)) continue;
                    processedGroups.Add(groupKey);

                    // Build the group
                    var group = new AlternativeQuestGroup { IsRequired = true, OwnerProfile = owner };

                    // Add the main quest
                    var mainStatus = QuestProgressService.StoredStatusOf(storedProgress, prereq);
                    group.Choices.Add(new AlternativeQuestChoice
                    {
                        Task = prereq,
                        IsCompleted = mainStatus == QuestStatus.Done,
                        IsFailed = mainStatus == QuestStatus.Failed,
                        IsSelected = mainStatus == QuestStatus.Done
                    });

                    // Add alternative quests
                    if (prereq.AlternativeQuests != null)
                    {
                        foreach (var altName in prereq.AlternativeQuests)
                        {
                            var altTask = progressService.GetTask(altName) ?? progressService.GetTaskById(altName);
                            if (altTask != null)
                            {
                                var altStatus = QuestProgressService.StoredStatusOf(storedProgress, altTask);
                                group.Choices.Add(new AlternativeQuestChoice
                                {
                                    Task = altTask,
                                    IsCompleted = altStatus == QuestStatus.Done,
                                    IsFailed = altStatus == QuestStatus.Failed,
                                    IsSelected = altStatus == QuestStatus.Done
                                });
                            }
                        }
                    }

                    // Only add if there are multiple choices and none are completed yet
                    if (group.Choices.Count > 1 && !group.Choices.Any(c => c.IsCompleted))
                    {
                        groups.Add(group);
                    }
                }
            }

            return groups;
        }

        /// <summary>
        /// Get a unique key for an alternative quest group
        /// </summary>
        private static string GetAlternativeGroupKey(TarkovTask task)
        {
            var names = new List<string> { task.NormalizedName ?? "" };

            if (task.AlternativeQuests != null)
            {
                names.AddRange(task.AlternativeQuests);
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return string.Join("|", names);
        }

        #endregion

        public void Dispose()
        {
            StopMonitoring();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// Event arguments for map detection
    /// </summary>
    public class MapDetectedEventArgs : EventArgs
    {
        /// <summary>
        /// Detected map key (matches map_configs.json key)
        /// </summary>
        public string MapKey { get; }

        /// <summary>
        /// Time when map was detected
        /// </summary>
        public DateTime DetectedAt { get; }

        public MapDetectedEventArgs(string mapKey, DateTime detectedAt)
        {
            MapKey = mapKey;
            DetectedAt = detectedAt;
        }
    }
}
