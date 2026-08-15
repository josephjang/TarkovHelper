using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Services;

/// <summary>
/// Application settings service for managing user preferences
/// Settings are stored in user_data.db (UserSettings table)
/// </summary>
public class SettingsService
{
    private static readonly ILogger _log = Log.For<SettingsService>();
    private static SettingsService? _instance;
    public static SettingsService Instance => _instance ??= new SettingsService();

    private readonly UserDataDbService _userDataDb = UserDataDbService.Instance;

    // Setting keys
    private const string KeyLogFolderPath = "app.logFolderPath";
    private const string KeyLogMonitoringEnabled = "app.logMonitoringEnabled";

    // Logging settings keys
    private const string KeyLoggingLevel = "logging.level";
    private const string KeyLoggingMaxDays = "logging.maxDays";
    private const string KeyLoggingMaxSizeMB = "logging.maxSizeMB";
    private const string KeyPlayerLevel = "app.playerLevel";
    private const string KeyScavRep = "app.scavRep";
    private const string KeyShowLevelLockedQuests = "app.showLevelLockedQuests";
    private const string KeyHideWipeWarning = "app.hideWipeWarning";
    private const string KeySyncDaysRange = "app.syncDaysRange";
    private const string KeyBaseFontSize = "app.baseFontSize";
    private const string KeyDspDecodeCount = "app.dspDecodeCount";
    private const string KeyPlayerFaction = "app.playerFaction";
    private const string KeyHasEodEdition = "app.hasEodEdition";
    private const string KeyHasUnheardEdition = "app.hasUnheardEdition";
    private const string KeyPrestigeLevel = "app.prestigeLevel";

    // One-time flag: legacy profile-specific settings copied from UserSettings to ProfileSettings('pvp')
    private const string KeyProfileSettingsMigrated = "app.profileSettingsMigrated";

    // Profile-specific keys: stored per game mode in the ProfileSettings table.
    // All other keys remain global in the UserSettings table.
    // Internal so the survivor-classification test can pin the subset relation below.
    internal static readonly string[] ProfileSpecificKeys =
    {
        KeyPlayerLevel, KeyScavRep, KeyShowLevelLockedQuests, KeyDspDecodeCount,
        KeyPlayerFaction, KeyHasEodEdition, KeyHasUnheardEdition, KeyPrestigeLevel
    };

    // Profile keys a complete profile reset PRESERVES (feature-complete-profile-reset.md):
    // the editions describe what the account owns, not what a character progressed, and
    // wiping them would corrupt quest filtering until the player noticed. Declared next to
    // ProfileSpecificKeys so a future profile key is added in sight of the question "does
    // this survive a reset?". Deletion is the default: a key not listed here is wiped,
    // which is the safe direction for progress-shaped data.
    internal static readonly string[] ProfileKeysSurvivingReset =
    {
        KeyHasEodEdition, KeyHasUnheardEdition
    };

    // Map settings keys moved to MapSettings service

    private bool _settingsLoaded;
    private string? _detectionMethod;

    // Cached global values (UserSettings table). Each of these is per install, so none of them
    // needs a partition, a revision or a publish order; the profile-scoped values below do.
    private string? _logFolderPath;
    private bool? _logMonitoringEnabled;
    private bool? _hideWipeWarning;
    private int? _syncDaysRange;
    private double? _baseFontSize;

    /// <summary>
    /// The profile-scoped values (ProfileSettings table) and the profile they belong to, as one
    /// immutable value that is only ever replaced whole. Never null once
    /// <see cref="LoadSettings"/> has run, which every path into it guarantees; see
    /// <see cref="ProfileSettings"/>.
    /// </summary>
    private ProfileSettingsSnapshot _profileSettings = null!;

    /// <summary>
    /// The highest transition revision a reload has been started for. A reload that finishes
    /// after a newer one was requested discards its result instead of publishing the profile the
    /// user has already switched away from.
    /// </summary>
    private long _latestRevision;

    /// <summary>
    /// True when the load that produced the current snapshot could not read the store and
    /// published that profile's defaults in their place. It is what makes the defaults publish
    /// recoverable: see <see cref="OnActiveProfileChanged"/>.
    /// </summary>
    private volatile bool _lastLoadFailed;

    /// <summary>
    /// The live profile-scoped snapshot. Reading it is what makes the lazy load unskippable: a
    /// caller can only observe a snapshot that exists, so no consumer has to repeat the
    /// "settings loaded yet?" question. Production publishes only through
    /// <see cref="ReloadForProfile(string, long, bool)"/> and <see cref="UpdateProfileSetting"/>;
    /// tests seed the field directly because <c>GetUninitializedObject</c> skips the constructor.
    /// </summary>
    internal ProfileSettingsSnapshot ProfileSettings
    {
        get
        {
            if (!_settingsLoaded) LoadSettings();
            return Volatile.Read(ref _profileSettings);
        }
        set => Volatile.Write(ref _profileSettings, value);
    }

    // Map cached values moved to MapSettings service

    public event EventHandler<string?>? LogFolderChanged;
    public event EventHandler<int>? PlayerLevelChanged;
    public event EventHandler<double>? ScavRepChanged;
    public event EventHandler<double>? BaseFontSizeChanged;
    public event EventHandler<int>? DspDecodeCountChanged;
    public event EventHandler<string?>? PlayerFactionChanged;
    public event EventHandler<bool>? HasEodEditionChanged;
    public event EventHandler<bool>? HasUnheardEditionChanged;
    public event EventHandler<int>? PrestigeLevelChanged;

    private SettingsService()
    {
        LoadSettings();
        ProfileService.Instance.ActiveProfileChanged += OnActiveProfileChanged;
    }

    /// <summary>
    /// When the active game mode changes, reload profile-specific settings and
    /// notify subscribers so the UI reflects the new profile's values.
    /// </summary>
    private void OnActiveProfileChanged(object? sender, ProfileChangedEventArgs e)
    {
        var profileId = ProfileService.GetProfileId(e.Profile);

        // A provenance-only re-confirmation (EFT re-logs the session mode every time the player
        // opens the profile screen) normally names the profile the snapshot already holds.
        // Reloading it would re-read identical rows and re-raise seven events for nothing, so
        // the usual answer is "do nothing".
        //
        // Two states make it worth reloading anyway, and they are why this is not a plain
        // "if (!e.ProfileChanged) return":
        //  - the last load failed, so the catch in ReloadForProfile published defaults and the
        //    player is looking at level 15 and no editions;
        //  - the snapshot names a different profile than this event does, which a reload that
        //    lost its race can leave behind.
        // Both used to be curable only by switching profile by hand. A re-confirmation is the
        // one event that keeps arriving on its own, so it is where self-healing belongs. The
        // same shape guards QuestProgressService.OnActiveProfileChanged.
        if (!e.ProfileChanged && !_lastLoadFailed &&
            string.Equals(ProfileSettings.ProfileId, profileId, StringComparison.Ordinal))
            return;

        ReloadForProfile(profileId, e.Revision, notify: true);
    }

    /// <summary>
    /// The in-memory consequence of a committed profile reset: when the reset target is the
    /// profile whose values are cached, the cached level, scav rep, faction, prestige, DSP count
    /// and editions are stale (their rows were just deleted, editions excepted), so reload them
    /// and re-raise the changed events exactly as a profile switch would. A reset of any other
    /// profile touches no cached value here, so nothing needs reloading. Called by
    /// <see cref="ProfileResetService"/> strictly AFTER the store transaction commits, which is
    /// why the reload below reads post-reset rows.
    /// <para>
    /// The comparison is against the SNAPSHOT's profile id, like the three sibling hooks compare
    /// against their captured loaded-profile identity. It used to compare against the ambient
    /// selection, because back then every profile-scoped read resolved the selection at call time
    /// and the selection was therefore the only identity the cache had. That premise is what
    /// docs/decisions/fix-profile-settings-race.spec.md removed.
    /// </para>
    /// <para>
    /// Synchronous, and it must stay so: the hook runs as a plain <c>Action</c> from
    /// <c>ProfileResetService.RunRefreshHooks</c>, whose contract is that the cache is current
    /// when the hook returns.
    /// </para>
    /// </summary>
    public void HandleProfileReset(string profileId)
    {
        var current = ProfileSettings;
        if (!string.Equals(profileId, current.ProfileId, StringComparison.Ordinal))
            return;

        // The snapshot's own revision, not a fresh one: a reset announces no transition. If a
        // transition landed while this hook was reading, the revision guard inside discards this
        // publish and lets that transition win, which is correct because its own reload also
        // reads post-reset rows.
        ReloadForProfile(profileId, current.Revision, notify: true);
    }

    /// <summary>
    /// Reloads <paramref name="profile"/>'s settings for the transition numbered
    /// <paramref name="revision"/> and re-raises every profile-scoped changed event.
    /// <para>
    /// Internal rather than public because the only production caller is
    /// <see cref="OnActiveProfileChanged"/>; the race tests drive it directly, the way
    /// <c>ProfileReloadRaceTests</c> drives the sibling services' reloads.
    /// </para>
    /// </summary>
    internal void ReloadForProfile(AppProfile profile, long revision)
        => ReloadForProfile(ProfileService.GetProfileId(profile), revision, notify: true);

    /// <summary>
    /// The reload proper: read one profile's rows off to the side, then publish them as a single
    /// snapshot only if no newer transition has been announced meanwhile.
    /// <para>
    /// An unreadable store publishes <paramref name="profileId"/>'s DEFAULTS and remembers the
    /// failure, exactly as the three sibling services publish empty rows on failure: keeping the
    /// previous values instead would show one profile's level and editions under another
    /// profile's name, which is the very defect this guard exists to remove. The next
    /// re-confirmation heals it through <see cref="_lastLoadFailed"/>.
    /// </para>
    /// </summary>
    /// <param name="notify">
    /// False for the startup and lazy-load path only. That path runs inside a property read and,
    /// on startup, with the dispatcher blocked; raising the seven events from there would reenter
    /// pages that are still being built. Its callers redraw once they finish anyway, which is why
    /// the pre-snapshot initial load never raised them either.
    /// </param>
    private void ReloadForProfile(string profileId, long revision, bool notify)
    {
        ClaimRevision(revision);

        // Null means the read failed, which is also what _lastLoadFailed records: one query per
        // reload, so there is no half-read state to represent.
        Dictionary<string, string>? values = null;
        try
        {
            values = _userDataDb.LoadProfileSettings(profileId);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load profile settings for {profileId}: {ex.Message}");
        }

        if (Interlocked.Read(ref _latestRevision) != revision)
        {
            _log.Debug($"Discarding stale settings load for {profileId} (revision {revision})");
            return;
        }

        // Set before the publish, so a throwing subscriber below cannot leave it stale.
        _lastLoadFailed = values == null;

        var snapshot = values == null
            ? ProfileSettingsSnapshot.Defaults(profileId, revision)
            : SnapshotOf(profileId, revision, values);
        ProfileSettings = snapshot;

        if (notify) RaiseProfileSettingsChanged(snapshot);
    }

    /// <summary>
    /// Re-raises every profile-scoped changed event from one captured snapshot, in the order the
    /// reset contract pins (<c>ProfileResetHooksTests</c>).
    /// <para>
    /// All seven fire on every published reload, whether or not the value differs. Raising only
    /// actual changes would make pages refresh less often, which is a UI-timing change and not
    /// this one's business. The snapshot is a parameter rather than re-read per event so a
    /// publish landing mid-fan-out cannot make one event carry a value from another profile.
    /// </para>
    /// </summary>
    private void RaiseProfileSettingsChanged(ProfileSettingsSnapshot snapshot)
    {
        PlayerLevelChanged?.Invoke(this, snapshot.PlayerLevelOrDefault);
        ScavRepChanged?.Invoke(this, snapshot.ScavRepOrDefault);
        DspDecodeCountChanged?.Invoke(this, snapshot.DspDecodeCountOrDefault);
        PlayerFactionChanged?.Invoke(this, snapshot.PlayerFaction);
        HasEodEditionChanged?.Invoke(this, snapshot.HasEodEditionOrDefault);
        HasUnheardEditionChanged?.Invoke(this, snapshot.HasUnheardEditionOrDefault);
        PrestigeLevelChanged?.Invoke(this, snapshot.PrestigeLevelOrDefault);
    }

    /// <summary>
    /// Raises <see cref="_latestRevision"/> to <paramref name="revision"/> if it is newer.
    /// <para>
    /// The fourth copy of this loop in the solution (QuestProgressService, HideoutProgressService
    /// and ItemInventoryService carry the others), and deliberately so: extracting the shared gate
    /// would edit three already-guarded services for no behaviour change. The cache unification
    /// that would absorb it is THR-1 in docs/assessments/2026-08-code-health.md, and
    /// docs/decisions/fix-profile-settings-race.spec.md records the direction under which this
    /// copy becomes a pure move: keep the immutable snapshot as the shared state model and
    /// extract only this gate, rather than building a reload framework over per-service flows
    /// whose differences are real.
    /// </para>
    /// </summary>
    private void ClaimRevision(long revision)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _latestRevision);
            if (revision <= current) return;
            if (Interlocked.CompareExchange(ref _latestRevision, revision, current) == current) return;
        }
    }

    /// <summary>
    /// Player level constants
    /// </summary>
    public const int MinPlayerLevel = 1;
    public const int MaxPlayerLevel = 79;
    public const int DefaultPlayerLevel = 15;

    /// <summary>
    /// Scav Rep constants
    /// </summary>
    public const double MinScavRep = -6.0;
    public const double MaxScavRep = 6.0;
    public const double DefaultScavRep = 1.0;
    public const double ScavRepStep = 0.1;

    /// <summary>
    /// Font size constants
    /// </summary>
    public const double MinFontSize = 10;
    public const double MaxFontSize = 28;
    public const double DefaultBaseFontSize = 18;

    /// <summary>
    /// DSP Decode count constants (for Make Amends quest branches)
    /// </summary>
    public const int MinDspDecodeCount = 0;
    public const int MaxDspDecodeCount = 3;
    public const int DefaultDspDecodeCount = 0;

    /// <summary>
    /// Prestige level constants
    /// </summary>
    public const int MinPrestigeLevel = 0;
    public const int MaxPrestigeLevel = 5;
    public const int DefaultPrestigeLevel = 0;

    /// <summary>
    /// Player level for quest filtering
    /// </summary>
    public int PlayerLevel
    {
        get => ProfileSettings.PlayerLevelOrDefault;
        set
        {
            var clampedValue = Math.Clamp(value, MinPlayerLevel, MaxPlayerLevel);
            if (UpdateProfileSetting(
                    s => s.PlayerLevel == clampedValue ? null : s with { PlayerLevel = clampedValue },
                    KeyPlayerLevel, clampedValue.ToString()))
            {
                PlayerLevelChanged?.Invoke(this, clampedValue);
            }
        }
    }

    /// <summary>
    /// Whether to show level-locked quests in the quest list
    /// </summary>
    public bool ShowLevelLockedQuests
    {
        get => ProfileSettings.ShowLevelLockedQuestsOrDefault;
        // No "value differs" guard, unlike the seven properties around it: this one has never
        // had one, and it raises no changed event, so an unconditional write is the whole of
        // its observable behaviour.
        set => UpdateProfileSetting(
            s => s with { ShowLevelLockedQuests = value }, KeyShowLevelLockedQuests, value.ToString());
    }

    /// <summary>
    /// Scav reputation for quest filtering (Fence karma)
    /// </summary>
    public double ScavRep
    {
        get => ProfileSettings.ScavRepOrDefault;
        set
        {
            var clampedValue = Math.Round(Math.Clamp(value, MinScavRep, MaxScavRep), 1);
            // Compared against the EFFECTIVE value, so setting an unstored profile's scav rep to
            // the default writes nothing, as it always has.
            if (UpdateProfileSetting(
                    s => Math.Abs(s.ScavRepOrDefault - clampedValue) > 0.01
                        ? s with { ScavRep = clampedValue }
                        : null,
                    KeyScavRep, clampedValue.ToString()))
            {
                ScavRepChanged?.Invoke(this, clampedValue);
            }
        }
    }

    /// <summary>
    /// Log folder path (user-set or auto-detected)
    /// </summary>
    public string? LogFolderPath
    {
        get
        {
            if (!_settingsLoaded) LoadSettings();

            // If user has set a path, use it
            if (!string.IsNullOrEmpty(_logFolderPath))
            {
                return _logFolderPath;
            }

            // Otherwise try auto-detection
            return AutoDetectLogFolder();
        }
        set
        {
            _logFolderPath = value;
            SaveSetting(KeyLogFolderPath, value ?? "");
            LogFolderChanged?.Invoke(this, value);
        }
    }

    /// <summary>
    /// How the log folder was detected
    /// </summary>
    public string? DetectionMethod => _detectionMethod;

    /// <summary>
    /// Whether log monitoring is enabled (auto-start on app launch)
    /// </summary>
    public bool LogMonitoringEnabled
    {
        get
        {
            if (!_settingsLoaded) LoadSettings();
            return _logMonitoringEnabled ?? true;  // Default: enabled
        }
        set
        {
            if (_logMonitoringEnabled != value)
            {
                _logMonitoringEnabled = value;
                SaveSetting(KeyLogMonitoringEnabled, value.ToString());
            }
        }
    }

    /// <summary>
    /// Check if log folder is valid
    /// </summary>
    public bool IsLogFolderValid
    {
        get
        {
            var folder = LogFolderPath;
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder);
        }
    }

    /// <summary>
    /// Whether to hide the wipe warning dialog before quest sync
    /// </summary>
    public bool HideWipeWarning
    {
        get
        {
            if (!_settingsLoaded) LoadSettings();
            return _hideWipeWarning ?? false;
        }
        set
        {
            _hideWipeWarning = value;
            SaveSetting(KeyHideWipeWarning, value.ToString());
        }
    }

    /// <summary>
    /// Number of days to look back when syncing quest progress from logs
    /// 0 = All logs, 1-30 = specific range
    /// </summary>
    public int SyncDaysRange
    {
        get
        {
            if (!_settingsLoaded) LoadSettings();
            return _syncDaysRange ?? 0;
        }
        set
        {
            var clampedValue = Math.Clamp(value, 0, 30);
            if (_syncDaysRange != clampedValue)
            {
                _syncDaysRange = clampedValue;
                SaveSetting(KeySyncDaysRange, clampedValue.ToString());
            }
        }
    }

    /// <summary>
    /// Base font size for the application
    /// </summary>
    public double BaseFontSize
    {
        get
        {
            if (!_settingsLoaded) LoadSettings();
            return _baseFontSize ?? DefaultBaseFontSize;
        }
        set
        {
            var clampedValue = Math.Clamp(value, MinFontSize, MaxFontSize);
            if (Math.Abs((_baseFontSize ?? DefaultBaseFontSize) - clampedValue) > 0.01)
            {
                _baseFontSize = clampedValue;
                SaveSetting(KeyBaseFontSize, clampedValue.ToString());
                BaseFontSizeChanged?.Invoke(this, clampedValue);
            }
        }
    }

    /// <summary>
    /// DSP Radio Transmitter decode count for Make Amends quest branches
    /// 0 = Buyout, 1 = Security, 2 or 3 = Software
    /// </summary>
    public int DspDecodeCount
    {
        get => ProfileSettings.DspDecodeCountOrDefault;
        set
        {
            var clampedValue = Math.Clamp(value, MinDspDecodeCount, MaxDspDecodeCount);
            if (UpdateProfileSetting(
                    s => s.DspDecodeCount == clampedValue ? null : s with { DspDecodeCount = clampedValue },
                    KeyDspDecodeCount, clampedValue.ToString()))
            {
                DspDecodeCountChanged?.Invoke(this, clampedValue);
            }
        }
    }

    /// <summary>
    /// Player faction (bear, usec, or null for any/both)
    /// </summary>
    public string? PlayerFaction
    {
        get => ProfileSettings.PlayerFaction;
        set
        {
            var normalizedValue = string.IsNullOrEmpty(value) ? null : value.ToLowerInvariant();
            if (UpdateProfileSetting(
                    s => s.PlayerFaction == normalizedValue ? null : s with { PlayerFaction = normalizedValue },
                    KeyPlayerFaction, normalizedValue ?? ""))
            {
                PlayerFactionChanged?.Invoke(this, normalizedValue);
            }
        }
    }

    /// <summary>
    /// Check if a task should be included based on player's selected faction
    /// </summary>
    public bool ShouldIncludeTask(string? taskFaction)
    {
        if (string.IsNullOrEmpty(taskFaction))
            return true;

        var playerFaction = PlayerFaction;
        if (string.IsNullOrEmpty(playerFaction))
            return true;

        return string.Equals(taskFaction, playerFaction, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether player has Edge of Darkness edition
    /// </summary>
    public bool HasEodEdition
    {
        get => ProfileSettings.HasEodEditionOrDefault;
        set
        {
            // Compared against the NULLABLE value, as before: an unstored edition is not the
            // same as a stored false, so the first "no, I don't own it" is still written and
            // announced rather than mistaken for a no-op.
            if (UpdateProfileSetting(
                    s => s.HasEodEdition == value ? null : s with { HasEodEdition = value },
                    KeyHasEodEdition, value.ToString()))
            {
                HasEodEditionChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>
    /// Whether player has The Unheard edition
    /// </summary>
    public bool HasUnheardEdition
    {
        get => ProfileSettings.HasUnheardEditionOrDefault;
        set
        {
            if (UpdateProfileSetting(
                    s => s.HasUnheardEdition == value ? null : s with { HasUnheardEdition = value },
                    KeyHasUnheardEdition, value.ToString()))
            {
                HasUnheardEditionChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>
    /// Player's prestige level (0-5)
    /// </summary>
    public int PrestigeLevel
    {
        get => ProfileSettings.PrestigeLevelOrDefault;
        set
        {
            var clampedValue = Math.Clamp(value, MinPrestigeLevel, MaxPrestigeLevel);
            if (UpdateProfileSetting(
                    s => s.PrestigeLevel == clampedValue ? null : s with { PrestigeLevel = clampedValue },
                    KeyPrestigeLevel, clampedValue.ToString()))
            {
                PrestigeLevelChanged?.Invoke(this, clampedValue);
            }
        }
    }

    #region Map Settings (Facade - delegates to MapSettings)

    // Map settings are now managed by MapSettings service.
    // These properties delegate to MapSettings.Instance for backward compatibility.

    private MapSettings Map => MapSettings.Instance;

    public const double MinMarkerScale = MapSettings.MinMarkerScale;
    public const double MaxMarkerScale = MapSettings.MaxMarkerScale;
    public const double DefaultMarkerScale = MapSettings.DefaultMarkerScale;
    public const double DefaultDrawerWidth = MapSettings.DefaultDrawerWidth;

    public bool MapDrawerOpen { get => Map.DrawerOpen; set => Map.DrawerOpen = value; }
    public double MapDrawerWidth { get => Map.DrawerWidth; set => Map.DrawerWidth = value; }
    public bool MapShowExtracts { get => Map.ShowExtracts; set => Map.ShowExtracts = value; }
    public bool MapShowPmcExtracts { get => Map.ShowPmcExtracts; set => Map.ShowPmcExtracts = value; }
    public bool MapShowScavExtracts { get => Map.ShowScavExtracts; set => Map.ShowScavExtracts = value; }
    public bool MapShowTransits { get => Map.ShowTransits; set => Map.ShowTransits = value; }
    public bool MapShowQuests { get => Map.ShowQuests; set => Map.ShowQuests = value; }
    public bool MapIncompleteOnly { get => Map.IncompleteOnly; set => Map.IncompleteOnly = value; }
    public bool MapCurrentMapOnly { get => Map.CurrentMapOnly; set => Map.CurrentMapOnly = value; }
    public string MapSortOption { get => Map.SortOption; set => Map.SortOption = value; }
    public HashSet<string> MapHiddenQuests { get => Map.HiddenQuests; set => Map.HiddenQuests = value; }
    public HashSet<string> MapCollapsedQuests { get => Map.CollapsedQuests; set => Map.CollapsedQuests = value; }
    public string? MapLastSelectedMap { get => Map.LastSelectedMap; set => Map.LastSelectedMap = value; }
    public double MapMarkerScale { get => Map.MarkerScale; set => Map.MarkerScale = value; }
    public bool MapShowTrail { get => Map.ShowTrail; set => Map.ShowTrail = value; }
    public bool MapShowMinimap { get => Map.ShowMinimap; set => Map.ShowMinimap = value; }
    public string MapMinimapSize { get => Map.MinimapSize; set => Map.MinimapSize = value; }
    public double MapMarkerOpacity { get => Map.MarkerOpacity; set => Map.MarkerOpacity = value; }
    public bool MapAutoHideCompleted { get => Map.AutoHideCompleted; set => Map.AutoHideCompleted = value; }
    public bool MapFadeCompleted { get => Map.FadeCompleted; set => Map.FadeCompleted = value; }
    public bool MapShowLabels { get => Map.ShowLabels; set => Map.ShowLabels = value; }
    public double MapLabelScale { get => Map.LabelScale; set => Map.LabelScale = value; }
    public bool MapQuestStatusColors { get => Map.QuestStatusColors; set => Map.QuestStatusColors = value; }
    public bool MapHideCompletedQuests { get => Map.HideCompletedQuests; set => Map.HideCompletedQuests = value; }
    public bool MapShowActiveOnly { get => Map.ShowActiveOnly; set => Map.ShowActiveOnly = value; }
    public bool MapHideCompletedObjectives { get => Map.HideCompletedObjectives; set => Map.HideCompletedObjectives = value; }
    public int MapQuestMarkerStyle { get => Map.QuestMarkerStyle; set => Map.QuestMarkerStyle = value; }
    public bool MapShowKappaHighlight { get => Map.ShowKappaHighlight; set => Map.ShowKappaHighlight = value; }
    public string MapTraderFilter { get => Map.TraderFilter; set => Map.TraderFilter = value; }
    public string MapTrailColor { get => Map.TrailColor; set => Map.TrailColor = value; }
    public double MapTrailThickness { get => Map.TrailThickness; set => Map.TrailThickness = value; }
    public bool MapAutoStartTracking { get => Map.AutoStartTracking; set => Map.AutoStartTracking = value; }
    public bool MapClusteringEnabled { get => Map.ClusteringEnabled; set => Map.ClusteringEnabled = value; }
    public double MapClusterZoomThreshold { get => Map.ClusterZoomThreshold; set => Map.ClusterZoomThreshold = value; }
    public bool MapAutoFloorEnabled { get => Map.AutoFloorEnabled; set => Map.AutoFloorEnabled = value; }
    public bool MapShowBosses { get => Map.ShowBosses; set => Map.ShowBosses = value; }
    public bool MapShowSpawns { get => Map.ShowSpawns; set => Map.ShowSpawns = value; }
    public bool MapShowLevers { get => Map.ShowLevers; set => Map.ShowLevers = value; }
    public bool MapShowKeys { get => Map.ShowKeys; set => Map.ShowKeys = value; }
    public bool LeftPanelExpanded { get => Map.LeftPanelExpanded; set => Map.LeftPanelExpanded = value; }
    public bool ExpanderLayersExpanded { get => Map.ExpanderLayersExpanded; set => Map.ExpanderLayersExpanded = value; }
    public bool ExpanderFloorExpanded { get => Map.ExpanderFloorExpanded; set => Map.ExpanderFloorExpanded = value; }
    public bool ExpanderMapInfoExpanded { get => Map.ExpanderMapInfoExpanded; set => Map.ExpanderMapInfoExpanded = value; }
    public bool QuestPanelVisible { get => Map.QuestPanelVisible; set => Map.QuestPanelVisible = value; }
    public string? MapScreenshotPath { get => Map.ScreenshotPath; set => Map.ScreenshotPath = value; }
    public int MapQuestMarkerSize { get => Map.QuestMarkerSize; set => Map.QuestMarkerSize = value; }
    public int MapPlayerMarkerSize { get => Map.PlayerMarkerSize; set => Map.PlayerMarkerSize = value; }
    public double MapExtractNameSize { get => Map.ExtractNameSize; set => Map.ExtractNameSize = value; }
    public double MapQuestNameSize { get => Map.QuestNameSize; set => Map.QuestNameSize = value; }
    public double MapLastZoomLevel { get => Map.LastZoomLevel; set => Map.LastZoomLevel = value; }
    public double MapLastTranslateX { get => Map.LastTranslateX; set => Map.LastTranslateX = value; }
    public double MapLastTranslateY { get => Map.LastTranslateY; set => Map.LastTranslateY = value; }

    /// <summary>Saves the whole map view state (map/zoom/pan) in one DB round-trip; see MapSettings.SaveLastView.</summary>
    public void SaveMapLastView(string? mapKey, double zoomLevel, double translateX, double translateY)
        => Map.SaveLastView(mapKey, zoomLevel, translateX, translateY);

    public void AddHiddenQuest(string questId) => Map.AddHiddenQuest(questId);
    public void RemoveHiddenQuest(string questId) => Map.RemoveHiddenQuest(questId);
    public void ClearHiddenQuests() => Map.ClearHiddenQuests();
    public void ToggleQuestCollapsed(string questId) => Map.ToggleQuestCollapsed(questId);

    #endregion

    /// <summary>
    /// Auto-detect Tarkov log folder from game installation
    /// </summary>
    public string? AutoDetectLogFolder()
    {
        string? gameFolder;

        // 1. Try BSG Launcher registry
        gameFolder = TryDetectFromBsgLauncher();
        if (gameFolder != null)
        {
            var logsPath = GetLogsPathFromGameFolder(gameFolder);
            if (logsPath != null)
            {
                _detectionMethod = "BSG Launcher";
                return logsPath;
            }
        }

        // 2. Try Steam installation
        gameFolder = TryDetectFromSteam();
        if (gameFolder != null)
        {
            var logsPath = GetLogsPathFromGameFolder(gameFolder);
            if (logsPath != null)
            {
                _detectionMethod = "Steam";
                return logsPath;
            }
        }

        // 3. Try default installation paths
        gameFolder = TryDetectFromDefaultPaths();
        if (gameFolder != null)
        {
            var logsPath = GetLogsPathFromGameFolder(gameFolder);
            if (logsPath != null)
            {
                _detectionMethod = "Default Path";
                return logsPath;
            }
        }

        _detectionMethod = null;
        return null;
    }

    private string? GetLogsPathFromGameFolder(string gameFolder)
    {
        var steamLogsPath = Path.Combine(gameFolder, "build", "Logs");
        if (Directory.Exists(steamLogsPath))
            return steamLogsPath;

        var bsgLogsPath = Path.Combine(gameFolder, "Logs");
        if (Directory.Exists(bsgLogsPath))
            return bsgLogsPath;

        var buildFolder = Path.Combine(gameFolder, "build");
        if (Directory.Exists(buildFolder))
            return steamLogsPath;

        if (gameFolder.Contains("steamapps", StringComparison.OrdinalIgnoreCase) ||
            gameFolder.Contains("Steam", StringComparison.OrdinalIgnoreCase))
            return steamLogsPath;

        return bsgLogsPath;
    }

    private string? TryDetectFromBsgLauncher()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov");
            var installPath = key?.GetValue("InstallLocation")?.ToString();
            if (!string.IsNullOrEmpty(installPath) && IsValidTarkovFolder(installPath))
                return installPath;

            using var userKey = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Battlestate Games\EscapeFromTarkov");
            var userPath = userKey?.GetValue("InstallLocation")?.ToString();
            if (!string.IsNullOrEmpty(userPath) && IsValidTarkovFolder(userPath))
                return userPath;
        }
        catch
        {
            // Registry access failed
        }

        return null;
    }

    private string? TryDetectFromSteam()
    {
        try
        {
            string? steamPath = null;

            using (var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam"))
            {
                steamPath = key?.GetValue("SteamPath")?.ToString();
            }

            if (string.IsNullOrEmpty(steamPath))
            {
                var defaultSteamPath = @"C:\Program Files (x86)\Steam";
                if (Directory.Exists(defaultSteamPath))
                    steamPath = defaultSteamPath;
            }

            if (string.IsNullOrEmpty(steamPath))
                return null;

            steamPath = steamPath.Replace("/", "\\");

            var libraryFolders = GetSteamLibraryFolders(steamPath);
            string[] possibleFolderNames = ["Escape from Tarkov", "EscapeFromTarkov"];

            foreach (var libraryFolder in libraryFolders)
            {
                foreach (var folderName in possibleFolderNames)
                {
                    var tarkovPath = Path.Combine(libraryFolder, "steamapps", "common", folderName);
                    if (IsValidTarkovFolder(tarkovPath))
                        return tarkovPath;
                }
            }
        }
        catch
        {
            // Steam detection failed
        }

        return null;
    }

    private List<string> GetSteamLibraryFolders(string steamPath)
    {
        var folders = new List<string> { steamPath };

        try
        {
            var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath))
                return folders;

            var content = File.ReadAllText(vdfPath);
            var pathRegex = new Regex(@"""path""\s+""([^""]+)""", RegexOptions.IgnoreCase);
            var matches = pathRegex.Matches(content);

            foreach (Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    var path = match.Groups[1].Value.Replace("\\\\", "\\");
                    if (Directory.Exists(path) && !folders.Contains(path, StringComparer.OrdinalIgnoreCase))
                        folders.Add(path);
                }
            }
        }
        catch
        {
            // VDF parsing failed
        }

        return folders;
    }

    private string? TryDetectFromDefaultPaths()
    {
        string[] defaultPaths =
        [
            @"C:\Battlestate Games\EFT",
            @"C:\Battlestate Games\Escape from Tarkov",
            @"D:\Battlestate Games\EFT",
            @"D:\Battlestate Games\Escape from Tarkov",
            @"E:\Battlestate Games\EFT",
            @"E:\Battlestate Games\Escape from Tarkov",
            @"C:\Games\EFT",
            @"D:\Games\EFT",
            @"C:\Program Files\Battlestate Games\EFT",
            @"C:\Program Files (x86)\Battlestate Games\EFT"
        ];

        foreach (var path in defaultPaths)
        {
            if (IsValidTarkovFolder(path))
                return path;
        }

        return null;
    }

    public bool IsValidTarkovFolder(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
            return false;

        var exePath = Path.Combine(folderPath, "EscapeFromTarkov.exe");
        var bsgLogsPath = Path.Combine(folderPath, "Logs");
        var steamBuildPath = Path.Combine(folderPath, "build");
        var steamLogsPath = Path.Combine(folderPath, "build", "Logs");
        var steamExePath = Path.Combine(folderPath, "build", "EscapeFromTarkov.exe");

        return File.Exists(exePath) ||
               File.Exists(steamExePath) ||
               Directory.Exists(bsgLogsPath) ||
               Directory.Exists(steamLogsPath) ||
               Directory.Exists(steamBuildPath);
    }

    private void SaveSetting(string key, string value)
    {
        try
        {
            _log.Debug($"SaveSetting called: key={key}, value={value}");
            _userDataDb.SetSetting(key, value);
            _log.Debug($"SaveSetting success: key={key}");
        }
        catch (Exception ex)
        {
            _log.Error($"SaveSetting failed: key={key}, error={ex.Message}");
        }
    }

    /// <summary>
    /// Applies one profile-scoped edit: derives the next snapshot from the live one, publishes it
    /// by compare-and-swap, and persists the new value under the ProfileId of the snapshot the
    /// edit was derived from. That profile is the one whose value was on screen when the player
    /// changed it, which is the only profile the correction can honestly be attributed to; the
    /// ambient selection is never consulted, so an edit made in the moment around an automatic
    /// switch can no longer overwrite a value the player never saw
    /// (docs/decisions/fix-profile-settings-race.md, R2).
    /// </summary>
    /// <param name="update">
    /// Pure: re-run on every compare-and-swap retry. Returns null when the snapshot already holds
    /// the value, which skips both the publish and the write, exactly as the per-property
    /// "value differs" guards did before the snapshot existed.
    /// </param>
    /// <param name="key">ProfileSettings key to persist under.</param>
    /// <param name="value">Serialized value to persist.</param>
    /// <returns>
    /// True when the edit reached the live snapshot, which is when the caller raises its changed
    /// event. False means either "nothing changed" or "a reload for another profile overtook this
    /// edit"; in both cases announcing the new value would push it at pages that are showing a
    /// different profile.
    /// </returns>
    private bool UpdateProfileSetting(
        Func<ProfileSettingsSnapshot, ProfileSettingsSnapshot?> update, string key, string value)
    {
        // Captured before the derivation, and the only profile named below: the value the player
        // just corrected was read off THIS snapshot.
        var origin = ProfileSettings;

        var next = update(origin);
        if (next == null) return false;

        var published = TryPublish(origin, next, update);

        // Persisted whether or not the graft landed. A reload that overtook this edit read the
        // store before the write, so dropping the write too would lose a correction the player
        // made deliberately, and the row it lands in is still the row they were editing.
        //
        // The publish runs first, so a reload for the SAME profile finishing between the two
        // steps leaves the snapshot a moment behind the store. The value is durable either way
        // and the next reload reconciles; closing the gap would mean holding a lock across the
        // store read and the publish, which is the discipline this shape exists to avoid.
        SaveProfileSetting(origin.ProfileId, key, value);
        return published;
    }

    /// <summary>
    /// Publishes <paramref name="next"/> over the live snapshot, re-deriving through
    /// <paramref name="update"/> whenever another publisher wins the swap.
    /// <para>
    /// Re-application stops as soon as the live snapshot names a profile other than
    /// <paramref name="origin"/>'s: grafting one profile's edited value onto another profile's
    /// values is the exact shape of the defect this change removes. A plain assignment would be
    /// enough today, because edits arrive from the dispatcher, but nothing enforces that and a
    /// lost update would be silent; the loop will almost never spin.
    /// </para>
    /// </summary>
    private bool TryPublish(
        ProfileSettingsSnapshot origin, ProfileSettingsSnapshot next,
        Func<ProfileSettingsSnapshot, ProfileSettingsSnapshot?> update)
    {
        var current = origin;
        while (true)
        {
            var observed = Interlocked.CompareExchange(ref _profileSettings, next, current);
            if (ReferenceEquals(observed, current)) return true;

            current = observed;
            if (!string.Equals(current.ProfileId, origin.ProfileId, StringComparison.Ordinal))
                return false;

            var retried = update(current);
            if (retried == null) return false;
            next = retried;
        }
    }

    /// <summary>
    /// Save a profile-specific setting into <paramref name="profileId"/>'s partition, which is
    /// always the ProfileId of the snapshot the edit was derived from and never the selection at
    /// the moment this runs.
    /// </summary>
    private void SaveProfileSetting(string profileId, string key, string value)
    {
        try
        {
            _userDataDb.SetProfileSetting(profileId, key, value);
        }
        catch (Exception ex)
        {
            _log.Error($"SaveProfileSetting failed: profile={profileId}, key={key}, error={ex.Message}");
        }
    }

    /// <summary>
    /// Generic getter for any setting key
    /// </summary>
    public string GetValue(string key, string defaultValue = "")
    {
        try
        {
            var value = _userDataDb.GetSetting(key);
            return string.IsNullOrEmpty(value) ? defaultValue : value;
        }
        catch
        {
            return defaultValue;
        }
    }

    /// <summary>
    /// Generic setter for any setting key
    /// </summary>
    public void SetValue(string key, string value)
    {
        SaveSetting(key, value);
    }

    private void LoadSettings()
    {
        _settingsLoaded = true;

        // The one allowlisted selection read left in this service (ProfileAttributionSourceTests):
        // this is the only load with no ActiveProfileChanged to learn its profile from. The pair
        // is read atomically because taken as two properties, a transition landing between them
        // would pair the OLD profile with the NEW revision, and the guard in ReloadForProfile
        // would see nothing wrong with publishing one profile's rows for the other's transition.
        var (profile, revision) = ProfileService.Instance.CurrentTransition;

        try
        {
            // First check if JSON migration is needed
            MigrateFromJsonIfNeeded();

            // One-time: move legacy profile-specific values from UserSettings to ProfileSettings('pvp')
            MigrateGlobalSettingsToProfileIfNeeded();

            // Load global settings from UserSettings
            _logFolderPath = _userDataDb.GetSetting(KeyLogFolderPath);
            if (string.IsNullOrEmpty(_logFolderPath)) _logFolderPath = null;

            if (bool.TryParse(_userDataDb.GetSetting(KeyLogMonitoringEnabled), out var logMonitoring))
                _logMonitoringEnabled = logMonitoring;

            if (bool.TryParse(_userDataDb.GetSetting(KeyHideWipeWarning), out var hideWarning))
                _hideWipeWarning = hideWarning;

            if (int.TryParse(_userDataDb.GetSetting(KeySyncDaysRange), out var syncDays))
                _syncDaysRange = syncDays;

            if (double.TryParse(_userDataDb.GetSetting(KeyBaseFontSize), out var fontSize))
                _baseFontSize = fontSize;

            // Map settings are now loaded by MapSettings service
        }
        catch (Exception ex)
        {
            _log.Error($"Load failed: {ex.Message}");
        }

        // Outside the try above, and never skipped: every profile-scoped getter reads the
        // snapshot this publishes, so a global read or a migration that threw must not be able
        // to leave it absent. The reload has its own catch, which publishes this profile's
        // defaults if the store cannot be read at all.
        ReloadForProfile(ProfileService.GetProfileId(profile), revision, notify: false);
    }

    /// <summary>
    /// One profile's stored rows parsed into a snapshot, per key, with exactly the fallbacks the
    /// eight separate reads used: an absent row and an unparsable one both leave the field null,
    /// which is what makes the property answer its default.
    /// </summary>
    private static ProfileSettingsSnapshot SnapshotOf(
        string profileId, long revision, IReadOnlyDictionary<string, string> values)
    {
        string? Value(string key) => values.TryGetValue(key, out var stored) ? stored : null;

        var faction = Value(KeyPlayerFaction);

        return new ProfileSettingsSnapshot(
            profileId,
            revision,
            PlayerLevel: int.TryParse(Value(KeyPlayerLevel), out var level) ? level : null,
            ScavRep: double.TryParse(Value(KeyScavRep), out var scavRep) ? scavRep : null,
            ShowLevelLockedQuests: bool.TryParse(Value(KeyShowLevelLockedQuests), out var showLocked) ? showLocked : null,
            DspDecodeCount: int.TryParse(Value(KeyDspDecodeCount), out var dspCount) ? dspCount : null,
            PlayerFaction: string.IsNullOrEmpty(faction) ? null : faction,
            HasEodEdition: bool.TryParse(Value(KeyHasEodEdition), out var hasEod) ? hasEod : null,
            HasUnheardEdition: bool.TryParse(Value(KeyHasUnheardEdition), out var hasUnheard) ? hasUnheard : null,
            PrestigeLevel: int.TryParse(Value(KeyPrestigeLevel), out var prestige) ? prestige : null);
    }

    /// <summary>
    /// One-time migration: copy legacy profile-specific values stored globally in UserSettings
    /// into ProfileSettings under the PvP profile (existing data belongs to PvP).
    /// </summary>
    private void MigrateGlobalSettingsToProfileIfNeeded()
    {
        if (_userDataDb.GetSetting(KeyProfileSettingsMigrated) == "true")
            return;

        try
        {
            foreach (var key in ProfileSpecificKeys)
            {
                var globalValue = _userDataDb.GetSetting(key);
                if (!string.IsNullOrEmpty(globalValue))
                {
                    _userDataDb.SetProfileSetting(ProfileService.PvpProfileId, key, globalValue);
                }
            }

            _userDataDb.SetSetting(KeyProfileSettingsMigrated, "true");
            _log.Info("Migrated profile-specific settings to PvP profile");
        }
        catch (Exception ex)
        {
            _log.Error($"Profile settings migration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Migrate from legacy app_settings.json if it exists
    /// </summary>
    private void MigrateFromJsonIfNeeded()
    {
        var jsonPath = Path.Combine(AppEnv.ConfigPath, "app_settings.json");
        if (!File.Exists(jsonPath)) return;

        try
        {
            var json = File.ReadAllText(jsonPath);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var settings = JsonSerializer.Deserialize<LegacyAppSettings>(json, options);

            if (settings != null)
            {
                // Global settings → UserSettings
                if (!string.IsNullOrEmpty(settings.LogFolderPath))
                    _userDataDb.SetSetting(KeyLogFolderPath, settings.LogFolderPath);

                if (settings.HideWipeWarning.HasValue)
                    _userDataDb.SetSetting(KeyHideWipeWarning, settings.HideWipeWarning.Value.ToString());

                if (settings.SyncDaysRange.HasValue)
                    _userDataDb.SetSetting(KeySyncDaysRange, settings.SyncDaysRange.Value.ToString());

                if (settings.BaseFontSize.HasValue)
                    _userDataDb.SetSetting(KeyBaseFontSize, settings.BaseFontSize.Value.ToString());

                // Profile-specific settings → ProfileSettings (legacy data belongs to PvP)
                if (settings.PlayerLevel.HasValue)
                    _userDataDb.SetProfileSetting(ProfileService.PvpProfileId, KeyPlayerLevel, settings.PlayerLevel.Value.ToString());

                if (settings.ScavRep.HasValue)
                    _userDataDb.SetProfileSetting(ProfileService.PvpProfileId, KeyScavRep, settings.ScavRep.Value.ToString());

                if (settings.ShowLevelLockedQuests.HasValue)
                    _userDataDb.SetProfileSetting(ProfileService.PvpProfileId, KeyShowLevelLockedQuests, settings.ShowLevelLockedQuests.Value.ToString());

                if (settings.DspDecodeCount.HasValue)
                    _userDataDb.SetProfileSetting(ProfileService.PvpProfileId, KeyDspDecodeCount, settings.DspDecodeCount.Value.ToString());

                if (!string.IsNullOrEmpty(settings.PlayerFaction))
                    _userDataDb.SetProfileSetting(ProfileService.PvpProfileId, KeyPlayerFaction, settings.PlayerFaction);
            }

            // Delete the JSON file after migration
            File.Delete(jsonPath);
            _log.Info($"Migrated and deleted: {jsonPath}");
        }
        catch (Exception ex)
        {
            _log.Error($"Migration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reset log folder setting (use auto-detection)
    /// </summary>
    public void ResetLogFolderPath()
    {
        _logFolderPath = null;
        SaveSetting(KeyLogFolderPath, "");
        LogFolderChanged?.Invoke(this, AutoDetectLogFolder());
    }

    private class LegacyAppSettings
    {
        public string? LogFolderPath { get; set; }
        public int? PlayerLevel { get; set; }
        public double? ScavRep { get; set; }
        public bool? ShowLevelLockedQuests { get; set; }
        public bool? HideWipeWarning { get; set; }
        public int? SyncDaysRange { get; set; }
        public double? BaseFontSize { get; set; }
        public int? DspDecodeCount { get; set; }
        public string? PlayerFaction { get; set; }
    }
}
