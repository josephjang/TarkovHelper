using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

public sealed class ProfileService
{
    private static readonly ILogger _log = Log.For<ProfileService>();
    private static readonly Lazy<ProfileService> _instance = new(() => new ProfileService());
    public static ProfileService Instance => _instance.Value;

    public const string PvpProfileId = "pvp";
    public const string PveProfileId = "pve";
    public const string SeasonProfileId = "season";
    private const string SettingKey = "app.activeGameMode";

    /// <summary>
    /// Guards the selection and its revision as one unit. They are two halves of a single
    /// fact ("transition N put us on profile P"), and they are written from at least two
    /// threads: the dispatcher when the user clicks, and the EFT log watcher's thread pool
    /// when fresh evidence arrives. Assigning the fields outside a lock let two transitions
    /// assign in one order and take revisions in the other, leaving the service reporting one
    /// profile while the winning revision belonged to another.
    /// <para>
    /// Static, not per instance, so the gate exists even on an instance built by
    /// <c>RuntimeHelpers.GetUninitializedObject</c>, which skips field initializers and would
    /// leave an instance field null with every lock on it throwing (ProfileSwitchingTests builds
    /// one that way). This class is a singleton, so a per-type gate is a per-instance gate in
    /// production, and two test instances sharing one only serialize with each other.
    /// </para>
    /// </summary>
    private static readonly object _gate = new();

    private AppProfile _activeProfile = AppProfile.PvpZone;
    private bool _isAutoDetected;
    private long _transitionRevision;

    public AppProfile ActiveProfile
    {
        get { lock (_gate) return _activeProfile; }
    }

    public string ActiveProfileId => GetProfileId(ActiveProfile);

    public bool IsAutoDetected
    {
        get { lock (_gate) return _isAutoDetected; }
    }

    /// <summary>
    /// Monotonic counter of announced transitions, incremented once per raised
    /// <see cref="ActiveProfileChanged"/> and carried on the event args.
    /// <para>
    /// Subscribers reload asynchronously, so two transitions in quick succession start two
    /// loads that can finish in either order. The revision is what lets a subscriber discard a
    /// load that lost the race instead of publishing the older profile's data over the newer
    /// one's. It counts every raise, including a provenance-only re-confirmation, so it never
    /// repeats a value across two different loads.
    /// </para>
    /// </summary>
    public long TransitionRevision
    {
        get { lock (_gate) return _transitionRevision; }
    }

    /// <summary>
    /// The current selection and the revision it was announced under, read together under
    /// <see cref="_gate"/>.
    /// <para>
    /// A caller that reads <see cref="ActiveProfile"/> and <see cref="TransitionRevision"/> as
    /// two separate properties can have a transition land between them and end up pairing the
    /// OLD profile with the NEW revision: its load then reads one profile's rows and publishes
    /// them as if they served the other profile's transition, and the revision guard sees
    /// nothing wrong. Anything that carries the pair into an async load must ask for it here.
    /// </para>
    /// </summary>
    public (AppProfile Profile, long Revision) CurrentTransition
    {
        get { lock (_gate) return (_activeProfile, _transitionRevision); }
    }

    public event EventHandler<ProfileChangedEventArgs>? ActiveProfileChanged;

    private ProfileService()
    {
        EftRaidEventService.Instance.RaidEvent += OnRaidEvent;
    }

    public async Task InitializeAsync()
    {
        var saved = await UserDataDbService.Instance.GetSettingAsync(SettingKey);
        var profile = ParseStoredProfile(saved);
        _log.Info($"Initialized: {profile}");

        // SettingsService and other singletons may already be constructed (default PvP Zone)
        // before InitializeAsync runs. If the saved profile differs, fire the event so they
        // reload their profile-scoped state.
        //
        // Only a real profile difference is interesting here. An earlier version also fired
        // when _isAutoDetected was set, which resolved a stored-vs-detected conflict toward
        // the STORED token and relabelled the detection as manual -- the opposite of this
        // feature's log-wins design -- while firing a same-profile reload for no benefit.
        ProfileChangedEventArgs args;
        lock (_gate)
        {
            if (profile == _activeProfile) return;

            _activeProfile = profile;
            _isAutoDetected = false;
            args = new ProfileChangedEventArgs(
                profile, false, profileChanged: true, revision: ++_transitionRevision);
        }

        // Raised outside the lock. Handlers run synchronously on this thread and take locks of
        // their own (the progress caches do), so holding the gate across the raise is a
        // lock-ordering hazard, and it would serialize every subscriber's work behind a gate
        // whose only job is to keep two fields consistent. The args were built under the lock,
        // so each subscriber still sees a profile and revision that belong together.
        ActiveProfileChanged?.Invoke(this, args);
    }

    public void SetActiveProfile(AppProfile profile, bool isAuto = false)
    {
        if (!Enum.IsDefined(profile)) return;

        // The comparison, the assignment and the revision are one atomic step: two threads
        // transitioning at once (a click on the dispatcher, log evidence on the watcher's
        // thread) must not be able to assign in one order and number themselves in the other.
        bool profileChanged;
        ProfileChangedEventArgs args;
        lock (_gate)
        {
            if (_activeProfile == profile && _isAutoDetected == isAuto) return;

            // Repeated identical log evidence flips only the provenance flag. Subscribers that
            // announce a transition must be able to tell that apart from a real destination
            // change, or they claim a switch that never happened.
            profileChanged = _activeProfile != profile;

            _activeProfile = profile;
            _isAutoDetected = isAuto;
            args = new ProfileChangedEventArgs(
                profile, isAuto, profileChanged, revision: ++_transitionRevision);
        }

        if (profileChanged)
        {
            _ = UserDataDbService.Instance.SetSettingAsync(SettingKey, SerializeProfile(profile));
        }
        _log.Info($"Switched to {profile} (auto={isAuto}, changed={profileChanged})");

        // Raised outside the lock, for the reasons given in InitializeAsync.
        ActiveProfileChanged?.Invoke(this, args);
    }

    public void ApplyDetectedProfile(SessionProfileHint hint)
    {
        if (TryResolveDetectedProfile(hint, out var profile))
            SetActiveProfile(profile, isAuto: true);
    }

    /// <summary>
    /// Maps exact log evidence to its destination profile. Returns false for evidence that
    /// carries no destination, leaving the current selection untouched (PRD R4).
    /// <para>
    /// Deliberately does NOT take the current profile: every known hint resolves to the same
    /// destination from every current profile (PRD R1-R3, R5), so a function that cannot see
    /// the current state cannot accidentally reintroduce the retired seasonal-pin exception.
    /// </para>
    /// </summary>
    public static bool TryResolveDetectedProfile(SessionProfileHint detected, out AppProfile profile)
    {
        switch (detected)
        {
            case SessionProfileHint.PvpZone:
                profile = AppProfile.PvpZone;
                return true;
            case SessionProfileHint.PveZone:
                profile = AppProfile.PveZone;
                return true;
            case SessionProfileHint.PvpSeason:
                profile = AppProfile.PvpSeason;
                return true;
            default:
                // Unknown, and any hint added later without a mapping here. Reporting a
                // destination for unrecognized evidence would silently move the user's
                // storage target (and persist it); preserving the selection is the fail-safe.
                profile = default;
                return false;
        }
    }

    // The only many-to-one map that must keep a catch-all: this reads USER DATA, so a token
    // written by an older build (or corrupted) has to resolve to something rather than throw.
    public static AppProfile ParseStoredProfile(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "PVE" => AppProfile.PveZone,
        "SEASON" => AppProfile.PvpSeason,
        _ => AppProfile.PvpZone
    };

    // The maps below throw instead of aliasing an unmapped profile onto PvP: silently
    // answering "PVP"/"pvp" for a profile added later would merge its progress into the
    // permanent PvP rows, which is unrecoverable. SetActiveProfile's Enum.IsDefined guard
    // means only a new enum member without a case here can reach these.
    public static string SerializeProfile(AppProfile profile) => profile switch
    {
        AppProfile.PvpZone => "PVP",
        AppProfile.PveZone => "PVE",
        AppProfile.PvpSeason => "SEASON",
        _ => throw new ArgumentOutOfRangeException(
            nameof(profile), profile, "No persisted token is defined for this profile.")
    };

    public static string GetProfileId(AppProfile profile) => profile switch
    {
        AppProfile.PvpZone => PvpProfileId,
        AppProfile.PveZone => PveProfileId,
        AppProfile.PvpSeason => SeasonProfileId,
        _ => throw new ArgumentOutOfRangeException(
            nameof(profile), profile, "No storage profile id is defined for this profile.")
    };

    // Deliberately no GetProfileId(GameMode) overload: GameMode has two values but there
    // are three storage profiles, so any GameMode-keyed lookup would have to answer "pvp"
    // for PvP Season and silently merge seasonal progress into the permanent PvP rows.
    // Storage identity is keyed on AppProfile only; GameMode answers game-rules questions.
    public static GameMode GetGameMode(AppProfile profile) => profile switch
    {
        AppProfile.PveZone => GameMode.PVE,
        // PvP Zone and PvP Season deliberately share PvP game rules while keeping separate
        // storage, which is why game mode must never be used as a storage key.
        AppProfile.PvpZone or AppProfile.PvpSeason => GameMode.PVP,
        _ => throw new ArgumentOutOfRangeException(
            nameof(profile), profile, "No game mode is defined for this profile.")
    };

    private void OnRaidEvent(object? sender, EftRaidEventArgs e)
    {
        if (e.EventType != EftRaidEventType.SessionModeDetected) return;
        ApplyDetectedProfile(e.SessionProfileHint);
    }
}

public class ProfileChangedEventArgs : EventArgs
{
    // No GameMode here on purpose: it cannot distinguish PvP Zone from PvP Season, so a
    // subscriber switching on it would see "no change" across that transition and serve
    // the previous profile's data. Subscribers key on Profile (or ProfileId).
    public AppProfile Profile { get; }
    public bool IsAutoDetected { get; }

    /// <summary>
    /// False when only the provenance changed (the same profile was re-confirmed by fresh
    /// log evidence). Subscribers that announce or animate a transition must check this;
    /// subscribers that merely render the current state can ignore it.
    /// </summary>
    public bool ProfileChanged { get; }

    /// <summary>
    /// This transition's position in <see cref="ProfileService.TransitionRevision"/>'s
    /// monotonic sequence. A subscriber that reloads asynchronously must carry this into the
    /// reload and publish only if no later revision has arrived meanwhile; without it, two
    /// transitions in flight can finish out of order and leave the older profile's data loaded
    /// under the newer profile's name.
    /// </summary>
    public long Revision { get; }

    public ProfileChangedEventArgs(AppProfile profile, bool isAuto, bool profileChanged, long revision)
    {
        Profile = profile;
        IsAutoDetected = isAuto;
        ProfileChanged = profileChanged;
        Revision = revision;
    }
}
