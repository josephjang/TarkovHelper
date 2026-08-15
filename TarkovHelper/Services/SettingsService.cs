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

    // Lazy rather than "??= new", the way ProfileService builds its own singleton: the
    // constructor loads every setting off SQLite and subscribes to ActiveProfileChanged, so a
    // lost check-then-act race would leave a second, permanently subscribed instance behind:
    // every later profile switch reloading twice, and any handler wired to the loser
    // (App.xaml.cs subscribes to BaseFontSizeChanged) never hearing from the instance the rest
    // of the app uses. Nothing reached from the constructor reads Instance back, so
    // ExecutionAndPublication cannot deadlock or throw on re-entry here.
    private static readonly Lazy<SettingsService> _instance =
        new(() => new SettingsService(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static SettingsService Instance => _instance.Value;

    private readonly UserDataDbService _userDataDb = UserDataDbService.Instance;

    // Setting keys
    private const string KeyLogFolderPath = "app.logFolderPath";
    private const string KeyLogMonitoringEnabled = "app.logMonitoringEnabled";

    private const string KeyHideWipeWarning = "app.hideWipeWarning";
    private const string KeySyncDaysRange = "app.syncDaysRange";
    private const string KeyBaseFontSize = "app.baseFontSize";

    // The eight profile-scoped keys, kept here rather than on ProfileSettingsSnapshot because
    // this service owns the storage contract they name. Internal, not private, so the three
    // readers outside this class name the constants instead of copying their text:
    // ProfileSettingsSnapshot.From parses a row set by the same names the writers use (the
    // direction it already reads in for the Default* constants), ConfigMigrationService's legacy
    // import writes five of them straight to the store, and the tests that pin the key/field
    // pairing enumerate ProfileSpecificKeys below.
    //
    // ProfileSpecificKeys is NOT the reset's list - the reset takes its sibling
    // ProfileKeysSurvivingReset and deletes everything else - it is the list the one-time
    // UserSettings-to-ProfileSettings migration walks.
    internal const string KeyPlayerLevel = "app.playerLevel";
    internal const string KeyScavRep = "app.scavRep";
    internal const string KeyShowLevelLockedQuests = "app.showLevelLockedQuests";
    internal const string KeyDspDecodeCount = "app.dspDecodeCount";
    internal const string KeyPlayerFaction = "app.playerFaction";
    internal const string KeyHasEodEdition = "app.hasEodEdition";
    internal const string KeyHasUnheardEdition = "app.hasUnheardEdition";
    internal const string KeyPrestigeLevel = "app.prestigeLevel";

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
    /// The one gate <see cref="_profileSettings"/> is written under. It is held across "is this
    /// publish still wanted, and if so swap the reference" and across nothing else: never over a
    /// store read, never while a changed event is raised. That is what makes every guard below
    /// act on the state it just checked, instead of on state a competing publisher may have
    /// moved in between. Readers stay lock-free; see <see cref="ProfileSettings"/>.
    /// <para>
    /// Static, not per instance, for the reason <c>ProfileService._gate</c> and
    /// <c>HideoutProgressService._stateGate</c> are: the gate then exists even on an instance
    /// built by <c>RuntimeHelpers.GetUninitializedObject</c>, which skips field initializers and
    /// would leave an instance field null with every lock on it throwing (the race tests build
    /// one that way). This class is a singleton, so a per-type gate is a per-instance gate in
    /// production, and two test instances sharing one only serialize with each other.
    /// </para>
    /// </summary>
    private static readonly object _publishGate = new();

    /// <summary>
    /// Counts the profile-scoped EDITS that have begun, which is how a reload tells that the
    /// rows it read off to the side have been overtaken by a value the player just typed.
    /// Bumped BEFORE the edit's store write and its publish, never after, so an edit can never
    /// be invisible to a load that publishes after it: the load captures this counter before its
    /// read and reads again when it has moved (<see cref="LoadAndPublish"/>).
    /// </summary>
    private long _editGeneration;

    /// <summary>
    /// How many profile-scoped edits have begun but not yet finished writing their row and
    /// publishing it. The counter above cannot answer that on its own, and the gap it leaves is
    /// real: an edit bumps <see cref="_editGeneration"/> BEFORE its store write, so a load can
    /// capture the already-bumped counter, read rows the write has not committed yet, find the
    /// counter unmoved at publish time and republish the value the player just replaced. The
    /// snapshot then holds pre-edit rows under the right profile id with no failure recorded,
    /// which nothing else in this service repairs until the player switches profile by hand.
    /// <para>
    /// Bumping <see cref="_editGeneration"/> after the durable write instead would only move the
    /// hole: a load could then read before the write and check the counter before the bump. Two
    /// counters close both directions, provided each is touched in the opposite order on the two
    /// sides. An edit raises THIS one first and lowers it last, so it is non-zero for the whole
    /// span in which its row may be unreadable; a load captures <see cref="_editGeneration"/>
    /// first and this one second, so an edit that began after the capture necessarily bumps the
    /// generation after the capture too. Either signal makes the load read again.
    /// </para>
    /// </summary>
    private long _editsInFlight;

    /// <summary>How many times a load re-reads the store before giving the edits the last word.</summary>
    private const int MaxLoadAttempts = 3;

    /// <summary>
    /// True when the load that produced the current snapshot could not read the store and
    /// published that profile's defaults in their place. It is what makes the defaults publish
    /// recoverable: see <see cref="OnActiveProfileChanged"/>.
    /// </summary>
    private volatile bool _lastLoadFailed;

    /// <summary>
    /// The live profile-scoped snapshot. Reading it is what makes the lazy load unskippable: a
    /// caller can only observe a snapshot that exists, so no consumer has to repeat the
    /// "settings loaded yet?" question. Read-only on purpose: the field has exactly one writer,
    /// <see cref="Publish"/>, and every path to it holds <see cref="_publishGate"/> and has
    /// decided under that gate that its publish is still wanted. Tests seed the field directly
    /// because <c>GetUninitializedObject</c> skips the constructor.
    /// </summary>
    internal ProfileSettingsSnapshot ProfileSettings
    {
        get
        {
            if (!_settingsLoaded) LoadSettings();
            return Volatile.Read(ref _profileSettings);
        }
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

        // A provenance-only re-confirmation (the same profile, now backed by log evidence
        // instead of a click, or the other way round) normally names the profile the snapshot
        // already holds. Reloading it would re-read identical rows and re-raise seven events for
        // nothing, so the usual answer is "do nothing".
        //
        // Two states make it worth reloading anyway, and they are why this is not a plain
        // "if (!e.ProfileChanged) return":
        //  - the last load failed, so the catch in LoadAndPublish published defaults and the
        //    player is looking at level 15 and no editions;
        //  - the snapshot names a different profile than this event does, which a reload that
        //    lost its race can leave behind.
        // Both are otherwise curable only by switching profile by hand, and repairing them here
        // costs nothing on the common path. It is NOT a general self-heal: ProfileService drops
        // identical (profile, provenance) evidence without raising anything, so an event with
        // ProfileChanged == false arrives once per provenance flip (manual to auto-detected or
        // back), not once per profile-screen visit. A load that fails during an automatic switch
        // therefore has no further flip coming and stays on defaults until the player picks a
        // profile by hand. The same shape guards QuestProgressService.OnActiveProfileChanged.
        if (!e.ProfileChanged && !_lastLoadFailed &&
            string.Equals(ProfileSettings.ProfileId, profileId, StringComparison.Ordinal))
            return;

        // Runs to completion on whatever thread raised the event - the dispatcher for a manual
        // switch, the log watcher's thread for an automatic one - and deliberately so. The three
        // sibling services write "_ = ReloadForProfileAsync(...)" here, which is less of a
        // difference than it reads as: Microsoft.Data.Sqlite has no true async I/O (its OpenAsync
        // and ExecuteReaderAsync run synchronously, see TrackedUserDataWrites), so their reload
        // also finishes on the raising thread and only an explicit Task.Run would move any of
        // this off it. Finishing here is what lets every profile-scoped getter answer the NEW
        // profile the moment the event returns, which the same method has to do anyway for the
        // startup load (it runs inside a property read) and for HandleProfileReset (a plain
        // Action whose caller contracts that the cache is current when it returns). The cost is
        // one connection open and one indexed query per switch.
        ReloadForTransition(profileId, e.Revision, notify: true);
    }

    /// <summary>
    /// The in-memory consequence of someone else writing <paramref name="profileId"/>'s rows: when
    /// that profile is the one whose values are cached, the cached level, scav rep, faction,
    /// prestige, DSP count and editions no longer describe the store, so reload them and re-raise
    /// the changed events exactly as a profile switch would. A write to any other profile touches
    /// no cached value here, so nothing needs reloading.
    /// <para>
    /// Two writers ask this question, and both get the same answer. <see cref="ProfileResetService"/>
    /// asks through <see cref="HandleProfileReset"/>, strictly AFTER the reset transaction commits,
    /// which is why the reload below reads post-reset rows (every profile row deleted except the
    /// editions, which survive by design). <see cref="ConfigMigrationService"/> asks once after a
    /// legacy <c>app_settings.json</c> import has written PvP's rows straight to the store.
    /// </para>
    /// <para>
    /// The comparison is against the SNAPSHOT's profile id, like the three sibling reset hooks
    /// compare against their captured loaded-profile identity. It used to compare against the
    /// ambient selection, because back then every profile-scoped read resolved the selection at
    /// call time and the selection was therefore the only identity the cache had. That premise is
    /// what docs/decisions/fix-profile-settings-race.spec.md removed.
    /// </para>
    /// <para>
    /// Synchronous, and it must stay so: the reset reaches it as a plain <c>Action</c> from
    /// <c>ProfileResetService.RunRefreshHooks</c>, whose contract is that the cache is current
    /// when the hook returns.
    /// </para>
    /// </summary>
    public void ReloadAfterExternalWrite(string profileId)
    {
        var current = ProfileSettings;
        if (!string.Equals(profileId, current.ProfileId, StringComparison.Ordinal))
            return;

        // An out-of-band write announces no transition, so this reload neither claims a revision
        // nor is gated on one: it carries the snapshot's own revision forward, and its publish is
        // allowed while the live snapshot still names the profile that was written. Gating it on
        // _latestRevision instead would silently publish NOTHING whenever a transition had claimed
        // a newer revision without publishing yet, leaving the player with a committed reset and a
        // settings panel still showing the wiped level, karma and faction.
        //
        // The identity gate is the honest one anyway: what makes these rows worth republishing
        // is that they are the written profile's, and a transition that moves the cache to another
        // profile makes them nobody's business. Such a transition publishes its own rows, which
        // reflect the external write too, because this runs after that write is durable.
        LoadAndPublish(
            profileId, current.Revision, notify: true,
            live => string.Equals(live.ProfileId, profileId, StringComparison.Ordinal));
    }

    /// <summary>
    /// The profile-reset contract's name for <see cref="ReloadAfterExternalWrite"/>: a committed
    /// reset is one of its two out-of-band writers. Kept as an adapter rather than as the name of
    /// the behaviour itself, because the reset orchestrator calls all four caches through this one
    /// signature (<c>ProfileResetOrchestrationTests</c> pins the four), while this service - alone
    /// among them - RELOADS rather than clears and is asked the same question by an import that is
    /// not a reset at all.
    /// </summary>
    public void HandleProfileReset(string profileId) => ReloadAfterExternalWrite(profileId);

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
        => ReloadForTransition(ProfileService.GetProfileId(profile), revision, notify: true);

    /// <summary>
    /// Reloads for an announced profile transition: claims <paramref name="revision"/>, then
    /// publishes only while that revision is still the newest one announced. A load that finishes
    /// after a newer transition was announced belongs to a profile the user has already left, and
    /// that newer transition publishes its own rows.
    /// </summary>
    private void ReloadForTransition(string profileId, long revision, bool notify)
    {
        RevisionGate.Claim(ref _latestRevision, revision);
        LoadAndPublish(
            profileId, revision, notify, _ => Interlocked.Read(ref _latestRevision) == revision);
    }

    /// <summary>
    /// The reload proper: read one profile's rows off to the side, then publish them as a single
    /// snapshot, and only while that publish is still wanted.
    /// <para>
    /// An unreadable store publishes <paramref name="profileId"/>'s DEFAULTS and remembers the
    /// failure, exactly as the three sibling services publish empty rows on failure: keeping the
    /// previous values instead would show one profile's level and editions under another
    /// profile's name, which is the very defect this guard exists to remove. A later
    /// re-confirmation heals it through <see cref="_lastLoadFailed"/>, when one arrives.
    /// </para>
    /// <para>
    /// Rows read off to the side can also be overtaken by an edit the player makes while the read
    /// is in flight. Publishing them would revert, on screen, a number they just typed and that
    /// is already durable in the store, with nothing to reconcile it afterwards. So the load
    /// reads again instead (the edit writes its row before it publishes, so a re-read sees it),
    /// and if edits keep landing it gives them the last word rather than reverting one. "Overtaken"
    /// covers both an edit that began after this read and one that was still mid-write during it;
    /// see <see cref="_editsInFlight"/>.
    /// </para>
    /// </summary>
    /// <param name="notify">
    /// False for the startup and lazy-load path only. That path runs inside a property read and,
    /// on startup, with the dispatcher blocked; raising the seven events from there would reenter
    /// pages that are still being built. Its callers redraw once they finish anyway, which is why
    /// the pre-snapshot initial load never raised them either.
    /// </param>
    /// <param name="isStillWanted">
    /// Asked about the LIVE snapshot under <see cref="_publishGate"/>, immediately before the
    /// swap, so its answer cannot go stale between the question and the publish. A transition
    /// asks about its revision, a reset asks whether the cache still holds the profile it reset.
    /// </param>
    private void LoadAndPublish(
        string profileId, long revision, bool notify,
        Func<ProfileSettingsSnapshot, bool> isStillWanted)
    {
        for (var attempt = 1; ; attempt++)
        {
            // Both captured BEFORE the read, and in this order: see _editsInFlight for why the
            // pair is what makes "the rows below are not older than any edit" checkable at all,
            // and why the generation has to be read first.
            var generation = Volatile.Read(ref _editGeneration);
            var editsInFlight = Volatile.Read(ref _editsInFlight);

            // Null means the read failed, which is also what _lastLoadFailed records: one query
            // per reload, so there is no half-read state to represent.
            Dictionary<string, string>? values = null;
            try
            {
                values = _userDataDb.LoadProfileSettings(profileId);
            }
            catch (Exception ex)
            {
                _log.Error($"Failed to load profile settings for {profileId}: {ex.Message}");
            }

            var snapshot = values == null
                ? ProfileSettingsSnapshot.Defaults(profileId, revision)
                : ProfileSettingsSnapshot.From(profileId, revision, values);

            switch (TryPublishLoad(snapshot, values == null, generation, editsInFlight, isStillWanted))
            {
                case LoadPublishOutcome.Published:
                    if (notify) RaiseProfileSettingsChanged(snapshot);
                    return;

                case LoadPublishOutcome.Superseded:
                    _log.Debug($"Discarding stale settings load for {profileId} (revision {revision})");
                    return;

                case LoadPublishOutcome.OvertakenByEdit when attempt < MaxLoadAttempts:
                    continue;

                default:
                    // Only reachable if an edit to this profile was landing or still in flight on
                    // every attempt, which also means a snapshot is live (nothing overtakes the
                    // very first publish). That snapshot holds those edits, all of them durable,
                    // and each edit was derived from the one before it: keeping it is strictly
                    // better than replacing it with rows that predate the last one.
                    _log.Warning(
                        $"Settings load for {profileId} kept being overtaken by edits after " +
                        $"{attempt} attempts; keeping the edited snapshot");

                    // The cache is current, but the caller was promised a fan-out and one half of
                    // that promise is unconditional: HandleProfileReset runs as a plain Action
                    // whose contract is that the cache is current AND announced when it returns,
                    // and returning quietly here would leave the settings panel showing values
                    // the reset wiped. So the LIVE snapshot is announced instead of the rows just
                    // read, which is the honest one: it is the one every getter answers from.
                    if (notify) RaiseProfileSettingsChanged(Volatile.Read(ref _profileSettings));
                    return;
            }
        }
    }

    /// <summary>What a load's publish attempt did, and why.</summary>
    private enum LoadPublishOutcome
    {
        /// <summary>The snapshot is live; its changed events are the caller's to raise.</summary>
        Published,

        /// <summary>Something newer owns the cache now, so this load has nothing to say.</summary>
        Superseded,

        /// <summary>An edit landed while the rows were being read; read them again.</summary>
        OvertakenByEdit,
    }

    /// <summary>
    /// Decides under <see cref="_publishGate"/> whether a load's snapshot may replace the live
    /// one, and swaps it in the same breath if so. Both questions ("is this still wanted" and
    /// "was it overtaken by an edit") are asked about the state that is live at that moment, and
    /// nothing can move that state until this returns.
    /// </summary>
    private LoadPublishOutcome TryPublishLoad(
        ProfileSettingsSnapshot snapshot, bool loadFailed, long generation, long editsInFlight,
        Func<ProfileSettingsSnapshot, bool> isStillWanted)
    {
        lock (_publishGate)
        {
            var live = _profileSettings;

            // Null only until the very first load publishes. Nothing is live to supersede or to
            // overtake, and every getter would answer from a null snapshot, so this one publishes
            // unconditionally rather than risking a cache that never gets filled at all.
            if (live != null)
            {
                if (!isStillWanted(live)) return LoadPublishOutcome.Superseded;

                // An edit began after the rows were read, or one was still mid-write while they
                // were being read: either way the rows may predate a value the player typed.
                // Only an edit to the profile being published can be lost by this swap: a load
                // that moves the cache to a different profile is meant to replace what is there,
                // and the edit stays in the profile it was written to.
                if ((Volatile.Read(ref _editGeneration) != generation || editsInFlight != 0) &&
                    string.Equals(live.ProfileId, snapshot.ProfileId, StringComparison.Ordinal))
                    return LoadPublishOutcome.OvertakenByEdit;
            }

            // Under the gate and before the swap, so it always describes the snapshot that is
            // live, and a throwing subscriber afterwards cannot leave it stale.
            _lastLoadFailed = loadFailed;
            Publish(snapshot);
            return LoadPublishOutcome.Published;
        }
    }

    /// <summary>
    /// The single writer of <see cref="_profileSettings"/>. Callers MUST hold
    /// <see cref="_publishGate"/> and must already have decided, under it, that the publish is
    /// wanted; the write itself is a plain reference swap, which is what makes every reader see
    /// values that belong together with the profile id beside them.
    /// </summary>
    private void Publish(ProfileSettingsSnapshot snapshot)
        => Volatile.Write(ref _profileSettings, snapshot);

    /// <summary>
    /// Re-raises every profile-scoped changed event from one captured snapshot, in the order the
    /// reset contract pins (<c>ProfileResetHooksTests</c>), for as long as that snapshot is the
    /// one the cache holds.
    /// <para>
    /// All seven fire on every published reload, whether or not the value differs. Raising only
    /// actual changes would make pages refresh less often, which is a UI-timing change and not
    /// this one's business. The snapshot is a parameter rather than re-read per event so a
    /// publish landing mid-fan-out cannot make one event carry a value from another profile.
    /// </para>
    /// <para>
    /// A fan-out that has been superseded stops instead of finishing. It cannot be raised under
    /// <see cref="_publishGate"/> to avoid the question: the subscribers call straight back into
    /// this service and marshal onto the dispatcher, so holding the gate across them would invite
    /// both reentrancy and a deadlock against a dispatcher-thread publish. What it can do is ask,
    /// as late as possible, whether it is still announcing the profile the cache holds. Without
    /// that, a log-thread reload could publish profile A, get its fan-out queued behind a manual
    /// switch the dispatcher is running inline, and then drain after that switch published B:
    /// QuestListPage takes its faction radio (and, on the next filter change, the value it
    /// PERSISTS) from these events rather than from the snapshot, so B's screen would end up
    /// showing and saving A's faction.
    /// </para>
    /// <para>
    /// Re-asked before EVERY event, not once up front, because each handler can block this thread
    /// for as long as the dispatcher takes to run it: a switch that lands while event k is being
    /// delivered still stops events k+1 onwards. A partial fan-out is safe by construction, since
    /// whatever superseded this snapshot publishes and announces all seven of its own values. The
    /// residual window (a publish between the last check and the handler actually running) is not
    /// closable from this side. Closing it for good means the pages reading the snapshot rather
    /// than trusting whichever event reached them last.
    /// </para>
    /// </summary>
    private void RaiseProfileSettingsChanged(ProfileSettingsSnapshot snapshot)
    {
        Announce(() => PlayerLevelChanged?.Invoke(this, snapshot.PlayerLevelOrDefault));
        Announce(() => ScavRepChanged?.Invoke(this, snapshot.ScavRepOrDefault));
        Announce(() => DspDecodeCountChanged?.Invoke(this, snapshot.DspDecodeCountOrDefault));
        Announce(() => PlayerFactionChanged?.Invoke(this, snapshot.PlayerFaction));
        Announce(() => HasEodEditionChanged?.Invoke(this, snapshot.HasEodEditionOrDefault));
        Announce(() => HasUnheardEditionChanged?.Invoke(this, snapshot.HasUnheardEditionOrDefault));
        Announce(() => PrestigeLevelChanged?.Invoke(this, snapshot.PrestigeLevelOrDefault));

        void Announce(Action raise)
        {
            if (!ReferenceEquals(Volatile.Read(ref _profileSettings), snapshot)) return;
            raise();
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
    /// Log sync look-back constants: 0 means "all logs", and 30 days is the longest window the
    /// settings panel offers. Named rather than written into the setter's Math.Clamp, so the
    /// legacy import can clamp to the same bounds instead of to a second pair of literals
    /// (<see cref="LegacyAppSettingsValues.SyncDaysRange"/>).
    /// </summary>
    public const int MinSyncDaysRange = 0;
    public const int MaxSyncDaysRange = 30;
    public const int DefaultSyncDaysRange = 0;

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
            ApplyProfileEdit(
                s => s.PlayerLevel == clampedValue ? null : s with { PlayerLevel = clampedValue },
                KeyPlayerLevel, clampedValue.ToString(),
                () => PlayerLevelChanged?.Invoke(this, clampedValue));
        }
    }

    /// <summary>
    /// Whether to show level-locked quests in the quest list.
    /// <para>
    /// Read by nothing outside this service and the legacy JSON migration: the toggle this was
    /// prepared for was never wired to UI. Kept because removing a stored user setting is its own
    /// product decision. Tracked in https://github.com/josephjang/TarkovHelper/issues/45
    /// </para>
    /// </summary>
    public bool ShowLevelLockedQuests
    {
        get => ProfileSettings.ShowLevelLockedQuestsOrDefault;
        // No "value differs" guard, unlike the seven properties around it: this one has never
        // had one, and it raises no changed event, so an unconditional write is the whole of
        // its observable behaviour. The outcome is discarded for the same reason: with no event
        // to raise, Applied and Superseded ask nothing of this setter (UpdateProfileSetting
        // logs the latter).
        set => _ = UpdateProfileSetting(
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
            ApplyProfileEdit(
                // Compared against the EFFECTIVE value, so setting an unstored profile's scav rep
                // to the default writes nothing, as it always has.
                s => Math.Abs(s.ScavRepOrDefault - clampedValue) > 0.01
                    ? s with { ScavRep = clampedValue }
                    : null,
                // Invariant, like every other persisted double: "5.5" written under en-US used to
                // read back as 55.0 under a comma-decimal locale and reach Fence karma filtering
                // far past MaxScavRep, and "-2,5" written under de-DE used to read back as
                // nothing at all under en-US.
                KeyScavRep, SettingsValue.FormatDouble(clampedValue),
                () => ScavRepChanged?.Invoke(this, clampedValue));
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
            return _syncDaysRange ?? DefaultSyncDaysRange;
        }
        set
        {
            var clampedValue = Math.Clamp(value, MinSyncDaysRange, MaxSyncDaysRange);
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
                SaveSetting(KeyBaseFontSize, SettingsValue.FormatDouble(clampedValue));
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
            ApplyProfileEdit(
                s => s.DspDecodeCount == clampedValue ? null : s with { DspDecodeCount = clampedValue },
                KeyDspDecodeCount, clampedValue.ToString(),
                () => DspDecodeCountChanged?.Invoke(this, clampedValue));
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
            ApplyProfileEdit(
                s => s.PlayerFaction == normalizedValue ? null : s with { PlayerFaction = normalizedValue },
                KeyPlayerFaction, normalizedValue ?? "",
                () => PlayerFactionChanged?.Invoke(this, normalizedValue));
        }
    }

    /// <summary>
    /// Check if a task should be included based on player's selected faction
    /// </summary>
    public bool ShouldIncludeTask(string? taskFaction)
        => ShouldIncludeTask(taskFaction, ProfileSettings.PlayerFaction);

    /// <summary>
    /// The faction rule against an explicitly captured faction, so a caller that decides one
    /// quest status from several profile-scoped values answers them all from ONE snapshot
    /// instead of re-reading the live one per question.
    /// </summary>
    internal static bool ShouldIncludeTask(string? taskFaction, string? playerFaction)
    {
        if (string.IsNullOrEmpty(taskFaction))
            return true;

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
            ApplyProfileEdit(
                // Compared against the NULLABLE value, as before: an unstored edition is not the
                // same as a stored false, so the first "no, I don't own it" is still written and
                // announced rather than mistaken for a no-op.
                s => s.HasEodEdition == value ? null : s with { HasEodEdition = value },
                KeyHasEodEdition, value.ToString(),
                () => HasEodEditionChanged?.Invoke(this, value));
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
            // Compared against the NULLABLE value, for the reason spelled out on HasEodEdition.
            ApplyProfileEdit(
                s => s.HasUnheardEdition == value ? null : s with { HasUnheardEdition = value },
                KeyHasUnheardEdition, value.ToString(),
                () => HasUnheardEditionChanged?.Invoke(this, value));
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
            ApplyProfileEdit(
                s => s.PrestigeLevel == clampedValue ? null : s with { PrestigeLevel = clampedValue },
                KeyPrestigeLevel, clampedValue.ToString(),
                () => PrestigeLevelChanged?.Invoke(this, clampedValue));
        }
    }

    #region Map Settings (Facade - delegates to MapSettings)

    // Map settings are now managed by MapSettings service.
    // These properties delegate to MapSettings.Instance for backward compatibility.
    //
    // A pure pass-through that adds no behaviour and can drift from what it forwards to. Removing
    // it means repointing 42 reads across three UI files to MapSettings.Instance, changing both
    // receiver and property name at each site, so it is not a mechanical rename and wants its own
    // change. Tracked in https://github.com/josephjang/TarkovHelper/issues/44

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
    /// <para>
    /// This and the ~210 lines of registry/filesystem probing below it belong in their own
    /// locator type rather than a settings service, which would also give them the tests they
    /// currently lack. Two known smells live in here: <c>_detectionMethod</c> is a non-volatile
    /// field mutated as a side effect of the <see cref="LogFolderPath"/> getter, and
    /// <see cref="GetLogsPathFromGameFolder"/> is declared nullable but cannot return null, so
    /// its null-guards are dead and a nonexistent folder still reports "BSG Launcher".
    /// Tracked in https://github.com/josephjang/TarkovHelper/issues/44
    /// </para>
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
    /// One profile-scoped property setter, minus the part that differs. Seven of the eight setters
    /// were the same five lines around a different key, a different field and a different event:
    /// apply the edit, and announce it only when it reached the live snapshot. The bounds stay at
    /// each property, where the constants they clamp against are declared, and each derivation
    /// stays a lambda beside its own comment.
    /// </summary>
    /// <param name="update">The property's derivation; see <see cref="UpdateProfileSetting"/>.</param>
    /// <param name="key">ProfileSettings key to persist under.</param>
    /// <param name="value">Serialized value to persist.</param>
    /// <param name="raise">
    /// Raises the property's changed event. Run only for <see cref="EditPublishOutcome.Applied"/>:
    /// the other two outcomes would push a value at pages showing something else.
    /// </param>
    private void ApplyProfileEdit(
        Func<ProfileSettingsSnapshot, ProfileSettingsSnapshot?> update, string key, string value,
        Action raise)
    {
        if (UpdateProfileSetting(update, key, value) == EditPublishOutcome.Applied) raise();
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
    /// Which of the three things happened. Only <see cref="EditPublishOutcome.Applied"/> means the
    /// edit reached the live snapshot, which is when the caller raises its changed event: the
    /// other two would push a value at pages that are showing something else.
    /// </returns>
    private EditPublishOutcome UpdateProfileSetting(
        Func<ProfileSettingsSnapshot, ProfileSettingsSnapshot?> update, string key, string value)
    {
        // Captured before the derivation, and the only profile named below: the value the player
        // just corrected was read off THIS snapshot.
        var origin = ProfileSettings;

        var next = update(origin);
        if (next == null) return EditPublishOutcome.Unchanged;

        EditPublishOutcome outcome;
        string? supersedingProfileId;

        // Raised first and lowered last, so this edit counts as in flight for the whole span in
        // which its row may be unreadable to a load: from before the generation bump until after
        // the publish. A load that read rows during that span re-reads instead of publishing them
        // over the edit. See _editsInFlight for why the generation alone cannot say this.
        Interlocked.Increment(ref _editsInFlight);
        try
        {
            // Announced before either half of the edit lands, so a load whose store read is
            // already in flight cannot publish rows that predate it: that load captured this
            // counter before its read and reads again when it has moved. Bumped even when the
            // publish below is abandoned, which costs such a load one extra read and nothing else.
            Interlocked.Increment(ref _editGeneration);

            // Written BEFORE the publish, and whether or not the graft below lands: the store is
            // the copy an overtaken load reads again, so the row has to be there by the time it
            // looks. Dropping the write when the graft fails would also lose a correction the
            // player made deliberately, and the row it lands in is still the row they were editing.
            SaveProfileSetting(origin.ProfileId, key, value);

            outcome = TryPublish(origin, next, update, out supersedingProfileId);
        }
        finally
        {
            Interlocked.Decrement(ref _editsInFlight);
        }

        // Logged out here rather than inside TryPublish, which must not hold _publishGate across
        // anything that can block. Warning, not debug: the player typed a value, it is durable
        // under the profile they typed it against, and the screen they are now looking at will
        // never show it - the one outcome of the three that a support log should be able to
        // explain after the fact.
        if (outcome == EditPublishOutcome.Superseded)
        {
            _log.Warning(
                $"Discarded the in-memory half of an edit to {key} made against profile " +
                $"{origin.ProfileId}: the cache now holds {supersedingProfileId}. The value is " +
                $"stored under {origin.ProfileId} and reappears when that profile is loaded.");
        }

        return outcome;
    }

    /// <summary>
    /// What one profile-scoped edit did, which the eight property setters need distinguished
    /// because only one of the three is worth announcing, and one of the other two is worth
    /// logging. Internal so the tests can name the outcome they assert rather than the bool it
    /// used to be flattened into.
    /// </summary>
    internal enum EditPublishOutcome
    {
        /// <summary>The new value is live; the setter raises its changed event.</summary>
        Applied,

        /// <summary>The snapshot already held this value, so nothing was written or published.</summary>
        Unchanged,

        /// <summary>
        /// The cache moved to another profile before the edit could be grafted onto it. The row
        /// is written under the profile the player edited; only the in-memory half was dropped.
        /// </summary>
        Superseded,
    }

    /// <summary>
    /// Publishes <paramref name="next"/> over the live snapshot under <see cref="_publishGate"/>,
    /// re-deriving through <paramref name="update"/> when another publisher moved the snapshot
    /// between the derivation and here. One re-derivation is enough, and not one attempt in a
    /// loop, because nothing can move the snapshot while this holds the gate.
    /// <para>
    /// Re-application stops as soon as the live snapshot names a profile other than
    /// <paramref name="origin"/>'s: grafting one profile's edited value onto another profile's
    /// values is the exact shape of the defect this change removes. The edit's row is written
    /// either way, so nothing the player typed is lost; only the graft is.
    /// </para>
    /// </summary>
    /// <param name="supersedingProfileId">
    /// The profile the cache holds instead, set only for
    /// <see cref="EditPublishOutcome.Superseded"/> so the caller can name both profiles in its
    /// log without reading the snapshot again (by then it may have moved a third time).
    /// </param>
    private EditPublishOutcome TryPublish(
        ProfileSettingsSnapshot origin, ProfileSettingsSnapshot next,
        Func<ProfileSettingsSnapshot, ProfileSettingsSnapshot?> update,
        out string? supersedingProfileId)
    {
        supersedingProfileId = null;

        lock (_publishGate)
        {
            var live = _profileSettings;
            if (!ReferenceEquals(live, origin))
            {
                if (!string.Equals(live.ProfileId, origin.ProfileId, StringComparison.Ordinal))
                {
                    supersedingProfileId = live.ProfileId;
                    return EditPublishOutcome.Superseded;
                }

                // Re-derived from the winner, never republished as derived from origin: the
                // winner may carry another edit or freshly loaded rows, and this edit is a
                // change to ONE value, not a reason to undo the rest.
                var retried = update(live);

                // Same profile, and it already holds the new value: another publisher landed the
                // same edit first, so there is nothing left to apply and nothing to announce.
                if (retried == null) return EditPublishOutcome.Unchanged;
                next = retried;
            }

            Publish(next);
            return EditPublishOutcome.Applied;
        }
    }

    /// <summary>
    /// Save a profile-specific setting into <paramref name="profileId"/>'s partition, which is
    /// always the ProfileId of the snapshot the edit was derived from and never the selection at
    /// the moment this runs.
    /// <para>
    /// Deliberately NOT routed through <see cref="TrackedUserDataWrites"/>, unlike every other
    /// profile-owned write: this one must be durable before <see cref="TryPublish"/> runs, so it
    /// would have to block the dispatcher on the reset barrier. See the second paragraph of
    /// <see cref="TrackedUserDataWrites"/> for what that would cost and what would make it safe.
    /// </para>
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
        // would pair the OLD profile with the NEW revision, and the guard in LoadAndPublish
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

            // Clamped on the way in, like the font size below: a row out of range (a hand edit, or
            // a legacy import from a build that clamped less than today's) otherwise reaches
            // LogSyncService as the window it scans and the settings panel as a value its own
            // control cannot represent.
            if (int.TryParse(_userDataDb.GetSetting(KeySyncDaysRange), out var syncDays))
                _syncDaysRange = Math.Clamp(syncDays, MinSyncDaysRange, MaxSyncDaysRange);

            // Invariant-first, like every other persisted double in the app (SettingsValue), and
            // clamped on the way in. A row written by an older build under a comma-decimal
            // locale reads as "18,5" and used to parse as 185 under en-US, which App.xaml.cs
            // assigns straight into Resources["BaseFontSize"] and renders every control at
            // 185px. The clamp makes an out-of-range row unable to do that whatever wrote it.
            if (SettingsValue.TryParseDouble(_userDataDb.GetSetting(KeyBaseFontSize), out var fontSize))
                _baseFontSize = Math.Clamp(fontSize, MinFontSize, MaxFontSize);

            // Map settings are now loaded by MapSettings service
        }
        catch (Exception ex)
        {
            _log.Error($"Load failed: {ex.Message}");
        }

        // Outside the try above, and never skipped: every profile-scoped getter reads the
        // snapshot this publishes, so a global read or a migration that threw must not be able
        // to leave it absent. The reload has its own catch, which publishes this profile's
        // defaults if the store cannot be read at all, and its publish is unconditional while
        // nothing is live, so not even a transition claimed mid-startup can leave it null.
        ReloadForTransition(ProfileService.GetProfileId(profile), revision, notify: false);
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
    /// Migrate from legacy app_settings.json if it exists, from this install's own Config folder.
    /// </summary>
    private void MigrateFromJsonIfNeeded()
        => MigrateFromJson(Path.Combine(AppEnv.ConfigPath, "app_settings.json"));

    /// <summary>
    /// Imports the legacy <c>app_settings.json</c> at <paramref name="jsonPath"/> and deletes it.
    /// <para>
    /// This is the STARTUP reader of that file. The other one is
    /// <c>ConfigMigrationService.MigrateAppSettingsAsync</c>, which reads a Config folder the
    /// player points at by hand; the two differ in where their values go (this one writes the
    /// store directly and deletes the file unconditionally) but must not differ in what a given
    /// JSON value becomes as a row, which is why both take every transform from
    /// <see cref="LegacyAppSettingsValues"/> rather than clamping and formatting locally.
    /// </para>
    /// <para>
    /// The path is a parameter rather than read from <see cref="AppEnv"/> inside so the two
    /// readers can be driven over one file and compared; <see cref="MigrateFromJsonIfNeeded"/> is
    /// the production entry point and names the only path production uses.
    /// </para>
    /// <para>
    /// Every bounded number below is CLAMPED and every double is written in the invariant format.
    /// This file is hand-editable and was written by builds that clamped less than today's, so it
    /// is the one importer that can introduce a row no setter could have produced, and a
    /// current-culture <c>ToString()</c> here is what would store the "18,5" that later reads back
    /// as 185. The reads clamp too (<see cref="LoadSettings"/> for the font size and the sync
    /// range, <c>ProfileSettingsSnapshot.From</c> for the profile values), so this is belt and
    /// braces: it keeps the ROW itself sane, which is what the config migration between installs
    /// then copies.
    /// </para>
    /// </summary>
    internal void MigrateFromJson(string jsonPath)
    {
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
                    _userDataDb.SetSetting(
                        KeySyncDaysRange, LegacyAppSettingsValues.SyncDaysRange(settings.SyncDaysRange.Value));

                if (settings.BaseFontSize.HasValue)
                    _userDataDb.SetSetting(
                        KeyBaseFontSize, LegacyAppSettingsValues.BaseFontSize(settings.BaseFontSize.Value));

                // Profile-specific settings → ProfileSettings (legacy data belongs to PvP)
                if (settings.PlayerLevel.HasValue)
                    _userDataDb.SetProfileSetting(
                        ProfileService.PvpProfileId, KeyPlayerLevel,
                        LegacyAppSettingsValues.PlayerLevel(settings.PlayerLevel.Value));

                if (settings.ScavRep.HasValue)
                    _userDataDb.SetProfileSetting(
                        ProfileService.PvpProfileId, KeyScavRep,
                        LegacyAppSettingsValues.ScavRep(settings.ScavRep.Value));

                if (settings.ShowLevelLockedQuests.HasValue)
                    _userDataDb.SetProfileSetting(
                        ProfileService.PvpProfileId, KeyShowLevelLockedQuests,
                        LegacyAppSettingsValues.ShowLevelLockedQuests(settings.ShowLevelLockedQuests.Value));

                if (settings.DspDecodeCount.HasValue)
                    _userDataDb.SetProfileSetting(
                        ProfileService.PvpProfileId, KeyDspDecodeCount,
                        LegacyAppSettingsValues.DspDecodeCount(settings.DspDecodeCount.Value));

                var faction = LegacyAppSettingsValues.PlayerFaction(settings.PlayerFaction);
                if (faction != null)
                    _userDataDb.SetProfileSetting(ProfileService.PvpProfileId, KeyPlayerFaction, faction);
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
