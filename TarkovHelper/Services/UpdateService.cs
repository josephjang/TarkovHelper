using System.Net.Http;
using System.Reflection;
using System.Timers;
using System.Xml.Linq;
using TarkovHelper.Debug;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services
{
    /// <summary>
    /// Service for checking and managing application updates
    /// </summary>
    public class UpdateService
    {
        private static readonly ILogger _log = Log.For<UpdateService>();
        private static readonly Lazy<UpdateService> _instance = new(() => new UpdateService());
        public static UpdateService Instance => _instance.Value;

        internal const string UpdateXmlUrl = "https://raw.githubusercontent.com/josephjang/TarkovHelper/main/update.xml";
        /// <summary>
        /// One hour, matching the data channel's interval. Releases happen a few times a
        /// year, so a three-minute timer was 480 checks a day against a feed that raw
        /// GitHub caches for minutes at a time anyway; the check that finds anything is
        /// almost always the one at startup, which still runs immediately. Settings also
        /// keeps a manual check button for anyone who wants an answer sooner.
        /// </summary>
        private const int CheckIntervalMinutes = 60;

        private readonly HttpClient _httpClient;
        private readonly System.Timers.Timer _checkTimer;
        private readonly Version _currentVersion;

        private bool _isChecking;
        private UpdateInfo? _availableUpdate;
        private DateTime? _lastCheckTime;
        private Exception? _lastCheckError;

        /// <summary>
        /// Fired when update check is completed
        /// </summary>
        public event EventHandler<UpdateCheckEventArgs>? UpdateCheckCompleted;

        /// <summary>
        /// Fired when update check starts
        /// </summary>
        public event EventHandler? UpdateCheckStarted;

        /// <summary>
        /// Currently available update (null if no update available)
        /// </summary>
        public UpdateInfo? AvailableUpdate => _availableUpdate;

        /// <summary>
        /// Whether an update check is in progress
        /// </summary>
        public bool IsChecking => _isChecking;

        /// <summary>
        /// Current application version
        /// </summary>
        public Version CurrentVersion => _currentVersion;

        /// <summary>
        /// Last time update was checked
        /// </summary>
        public DateTime? LastCheckTime => _lastCheckTime;

        /// <summary>
        /// Error from the most recent completed check; null when it succeeded or no
        /// check has run yet. Lives here (not on UI subscribers) so every consumer
        /// sees the same success/fail/never-checked state.
        /// </summary>
        public Exception? LastCheckError => _lastCheckError;

        /// <summary>
        /// Whether the most recent completed update check failed.
        /// </summary>
        public bool LastCheckFailed => _lastCheckError != null;

        private UpdateService()
        {
            _httpClient = new HttpClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(30);

            _currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

            _checkTimer = new System.Timers.Timer(TimeSpan.FromMinutes(CheckIntervalMinutes).TotalMilliseconds);
            _checkTimer.Elapsed += OnTimerElapsed;
            _checkTimer.AutoReset = true;
        }

        /// <summary>
        /// Start automatic update checking
        /// </summary>
        public void StartAutoCheck() => StartAutoCheck(AppEnv.DisableUpdateCheck);

        /// <summary>
        /// Testable core of <see cref="StartAutoCheck()"/>: when disabled (the e2e
        /// harness sets TARKOVHELPER_DISABLE_UPDATE_CHECK — see
        /// <see cref="AppEnv.DisableUpdateCheck"/>), neither the timer nor the initial
        /// network check is started.
        /// </summary>
        internal void StartAutoCheck(bool disabled)
        {
            if (disabled)
            {
                _log.Info("Automatic update check disabled (TARKOVHELPER_DISABLE_UPDATE_CHECK)");
                return;
            }

            _log.Info($"Starting automatic update check (interval: {CheckIntervalMinutes} minutes)");
            _checkTimer.Start();

            // Do initial check immediately
            _ = CheckForUpdateAsync();
        }

        /// <summary>
        /// Stop automatic update checking
        /// </summary>
        public void StopAutoCheck()
        {
            _log.Info("Stopping automatic update check");
            _checkTimer.Stop();
        }

        /// <summary>
        /// Manually check for updates
        /// </summary>
        public async Task<UpdateInfo?> CheckForUpdateAsync()
        {
            if (_isChecking)
            {
                _log.Debug("Update check already in progress, skipping");
                return _availableUpdate;
            }

            _isChecking = true;
            UpdateCheckStarted?.Invoke(this, EventArgs.Empty);
            _log.Debug("Checking for updates...");

            try
            {
                var response = await _httpClient.GetStringAsync(UpdateXmlUrl);
                var updateInfo = ParseUpdateXml(response);

                if (updateInfo != null && updateInfo.Version > _currentVersion)
                {
                    _availableUpdate = updateInfo;
                    _log.Info($"Update available: {updateInfo.Version} (current: {_currentVersion})");
                }
                else
                {
                    _availableUpdate = null;
                    _log.Debug($"No update available (current: {_currentVersion}, latest: {updateInfo?.Version})");
                }

                _lastCheckTime = DateTime.Now;
                _lastCheckError = null;
                UpdateCheckCompleted?.Invoke(this, new UpdateCheckEventArgs(_availableUpdate, null));
                return _availableUpdate;
            }
            catch (Exception ex)
            {
                _log.Error("Failed to check for updates", ex);
                _lastCheckTime = DateTime.Now;
                _lastCheckError = ex;
                // Note: _availableUpdate is intentionally left as-is — an update found by an
                // earlier successful check remains installable while re-checks are failing.
                UpdateCheckCompleted?.Invoke(this, new UpdateCheckEventArgs(null, ex));
                return null;
            }
            finally
            {
                _isChecking = false;
            }
        }

        /// <summary>
        /// Start the update download and installation process
        /// </summary>
        public void StartUpdate()
        {
            if (_availableUpdate == null)
            {
                _log.Warning("No update available to install");
                return;
            }

            _log.Info($"Starting update to version {_availableUpdate.Version}");

            // Use AutoUpdater.NET to handle the actual update
            AutoUpdaterDotNET.AutoUpdater.InstalledVersion = _currentVersion;
            AutoUpdaterDotNET.AutoUpdater.ShowSkipButton = false;
            AutoUpdaterDotNET.AutoUpdater.ShowRemindLaterButton = false;
            AutoUpdaterDotNET.AutoUpdater.Start(UpdateXmlUrl);
        }

        private void OnTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            _ = CheckForUpdateAsync();
        }

        /// <summary>
        /// Formats a version for display as "vX.Y.Z" (falling back to "vX.Y" for a
        /// two-part version, where ToString(3) would throw). Single source for every
        /// version string the UI shows, so the chip and the Settings section can't
        /// format the same version two different ways.
        /// </summary>
        public static string FormatVersion(Version version)
            => $"v{(version.Build >= 0 ? version.ToString(3) : version.ToString(2))}";

        /// <summary>
        /// Pure mapping from update-service state to the status the UI should report.
        /// Order matters: an in-progress check wins, then a failure — a failed re-check
        /// must stay visible even while an update found earlier remains installable.
        /// </summary>
        public static UpdateStatusKind GetStatusKind(
            bool isChecking, bool lastCheckFailed, bool updateAvailable, bool hasCompletedCheck)
        {
            if (isChecking) return UpdateStatusKind.Checking;
            if (lastCheckFailed) return UpdateStatusKind.Failed;
            if (updateAvailable) return UpdateStatusKind.UpdateAvailable;
            if (hasCompletedCheck) return UpdateStatusKind.UpToDate;
            return UpdateStatusKind.None;
        }

        internal static UpdateInfo? ParseUpdateXml(string xmlContent)
        {
            try
            {
                var doc = XDocument.Parse(xmlContent);
                var item = doc.Root;

                // Root element is <item> directly
                if (item == null || item.Name.LocalName != "item")
                {
                    _log.Warning("Update XML does not have item as root element");
                    return null;
                }

                var versionStr = item.Element("version")?.Value;
                var url = item.Element("url")?.Value;
                var changelog = item.Element("changelog")?.Value;

                if (string.IsNullOrEmpty(versionStr) || string.IsNullOrEmpty(url))
                {
                    _log.Warning("Update XML missing required elements (version or url)");
                    return null;
                }

                if (!Version.TryParse(versionStr, out var version))
                {
                    _log.Warning($"Failed to parse version string: {versionStr}");
                    return null;
                }

                return new UpdateInfo
                {
                    Version = version,
                    DownloadUrl = url,
                    ChangelogUrl = changelog
                };
            }
            catch (Exception ex)
            {
                _log.Error("Failed to parse update XML", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// Update status to display, derived by <see cref="UpdateService.GetStatusKind"/>.
    /// </summary>
    public enum UpdateStatusKind
    {
        /// <summary>No check has completed yet.</summary>
        None,

        /// <summary>A check is currently running.</summary>
        Checking,

        /// <summary>The most recent check failed (an earlier-found update may still exist).</summary>
        Failed,

        /// <summary>The most recent check succeeded and an update is available.</summary>
        UpdateAvailable,

        /// <summary>The most recent check succeeded and the app is current.</summary>
        UpToDate
    }

    /// <summary>
    /// Information about an available update
    /// </summary>
    public class UpdateInfo
    {
        public required Version Version { get; init; }
        public required string DownloadUrl { get; init; }
        public string? ChangelogUrl { get; init; }
    }

    /// <summary>
    /// Event args for update check completion
    /// </summary>
    public class UpdateCheckEventArgs : EventArgs
    {
        public UpdateInfo? UpdateInfo { get; }
        public Exception? Error { get; }
        public bool IsUpdateAvailable => UpdateInfo != null;

        public UpdateCheckEventArgs(UpdateInfo? updateInfo, Exception? error)
        {
            UpdateInfo = updateInfo;
            Error = error;
        }
    }
}
