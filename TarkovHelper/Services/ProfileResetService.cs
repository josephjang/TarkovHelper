using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// How one complete profile reset ended (PRD R5 of feature-complete-profile-reset.md).
/// </summary>
public enum ProfileResetStatus
{
    /// <summary>The transaction committed: everything the profile owned is gone.</summary>
    Succeeded,

    /// <summary>
    /// The transaction rolled back, or never started at all. Nothing was removed and the app still
    /// shows the data it showed before, which is the guarantee PRD R5 makes: the removal is one
    /// SQLite transaction, so no failure can leave a half-reset profile behind.
    /// </summary>
    Failed,

    /// <summary>
    /// The store wait ran out of budget (<see cref="ProfileResetService.StoreTimeout"/>).
    /// Abandoning a wait does not cancel the work behind it, so this is the one outcome that
    /// cannot promise "nothing was removed": the transaction may still commit or roll back on its
    /// own. The dialog says so rather than repeating PRD R5's guarantee.
    /// </summary>
    Abandoned,
}

/// <summary>
/// What one complete profile reset did. Built only through the factories below, so the
/// combinations that would render nonsense (a success carrying a failure message, a failure
/// carrying none) cannot be constructed at all and no consumer has to defend against them.
/// </summary>
public sealed record ProfileResetOutcome
{
    private ProfileResetOutcome(ProfileResetStatus status, string? error)
    {
        Status = status;
        Error = error;
    }

    /// <summary>How the reset ended.</summary>
    public ProfileResetStatus Status { get; }

    /// <summary>
    /// The library-level detail behind a <see cref="ProfileResetStatus.Failed"/> outcome ("database
    /// is locked"), rendered under the dialog's own localized headline and never empty. Null for
    /// every other status: neither success nor an abandoned wait has a detail to add.
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// True only when the transaction committed. The in-memory refresh that follows is best effort:
    /// a throwing change-event subscriber leaves one page stale without making the reset a failure.
    /// </summary>
    public bool Success => Status == ProfileResetStatus.Succeeded;

    /// <summary>The reset committed.</summary>
    public static ProfileResetOutcome Succeeded() => new(ProfileResetStatus.Succeeded, null);

    /// <summary>
    /// A failure that removed nothing, detailed by <paramref name="message"/>. The message is
    /// required because the dialog's failure state exists to explain WHY, and a blank detail line
    /// under the headline explains nothing.
    /// </summary>
    public static ProfileResetOutcome Failed(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException(
                "A failed reset must carry a detail message for the result dialog.", nameof(message));
        }

        return new ProfileResetOutcome(ProfileResetStatus.Failed, message);
    }

    /// <summary>
    /// A failure detailed by the exception's own message, which is what this app surfaces for
    /// library errors. An exception whose message is blank falls back to its type name, so the
    /// detail line stays non-empty without the caller having to check.
    /// </summary>
    public static ProfileResetOutcome Failed(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Failed(string.IsNullOrWhiteSpace(error.Message) ? error.GetType().Name : error.Message);
    }

    /// <summary>
    /// The store wait was abandoned, so what the transaction did is unknown. Carries no detail:
    /// there is no exception behind it, and the dialog's own text is the whole story.
    /// </summary>
    public static ProfileResetOutcome Abandoned() => new(ProfileResetStatus.Abandoned, null);
}

/// <summary>
/// The four things one reset drives, named so the sequence that drives them can be run against
/// recorders instead of five singletons. The order they run in is the load-bearing guarantee of
/// the reset (PRD R5): the barrier goes up, staged writes for the target are discarded, the store
/// transaction commits, and only then does memory follow.
/// <para>
/// A record of delegates rather than an injected interface: the services behind these are the
/// repo-wide <c>Instance</c> singletons and stay that way. <see cref="ProfileResetService"/> wires
/// the real ones once in <see cref="ProfileResetService.ProductionCollaborators"/>, which is the
/// only production value of this type.
/// </para>
/// </summary>
/// <param name="BeginReset">
/// Raises the per-profile write barrier and drains what is in flight, returning the handle that
/// lowers it. Throws <see cref="TimeoutException"/> when the drain runs out of budget.
/// </param>
/// <param name="DiscardStagedWrites">
/// Drops the target's pending debounced writes, which describe rows the transaction is about to
/// delete.
/// </param>
/// <param name="Store">The one transaction, taking the profile and the reset watermark.</param>
/// <param name="RefreshHooks">
/// The in-memory consequences to run after the commit, named so a failure names its hook.
/// </param>
internal sealed record ResetCollaborators(
    Func<string, Task<IAsyncDisposable>> BeginReset,
    Action<string> DiscardStagedWrites,
    Func<string, DateTime, Task> Store,
    Func<string, IReadOnlyList<(string Name, Action Run)>> RefreshHooks);

/// <summary>
/// Orchestrates the complete per-profile reset (feature-complete-profile-reset.spec.md):
/// barrier up and in-flight writes drained, pending debounced saves for the target discarded,
/// one store transaction across everything the profile owns, then each service's in-memory
/// consequence, then barrier down. In-memory state changes only after durable success,
/// reversing the old memory-first reset order.
/// <para>
/// The target profile arrives as a parameter, captured by the caller when the confirmation
/// opened; nothing in this flow reads the ambient selection (an automatic profile switch while
/// the dialog is open must not move the reset, PRD R1). The whole flow is async end to end: a
/// blocking wait on the dispatcher would deadlock against tracked writes whose continuations
/// return to it.
/// </para>
/// </summary>
public sealed class ProfileResetService
{
    private static readonly ILogger _log = Log.For<ProfileResetService>();
    private static ProfileResetService? _instance;
    public static ProfileResetService Instance => _instance ??= new ProfileResetService();

    private ProfileResetService()
    {
    }

    /// <summary>
    /// How long the store transaction gets before the reset gives up on it. The transaction is a
    /// handful of DELETEs and one INSERT against a local SQLite file, so a wait near this bound
    /// means a wedged connection rather than a slow one. Bounded for the same reason the drain is
    /// (<see cref="TrackedUserDataWrites.DefaultDrainTimeout"/>): the caller is a modal dialog
    /// that refuses to close while the reset runs, and an unbounded wait there leaves the player
    /// with a window nothing can dismiss.
    /// </summary>
    public static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The upper bound on one <see cref="ResetAsync(AppProfile)"/> call: the drain wait plus the
    /// store wait, the only two steps that can block. Past this the call has reported an outcome,
    /// so the UI can size its own "give the window back" backstop against it.
    /// </summary>
    public static TimeSpan MaxDuration => TrackedUserDataWrites.DefaultDrainTimeout + StoreTimeout;

    /// <summary>
    /// The real collaborators, wired once. The only production value of
    /// <see cref="ResetCollaborators"/>; every other one belongs to a test that drives the
    /// sequence without standing up five singletons.
    /// </summary>
    internal static readonly ResetCollaborators ProductionCollaborators = new(
        BeginReset: profileId => TrackedUserDataWrites.BeginResetAsync(profileId),
        DiscardStagedWrites: profileId => ItemInventoryService.Instance.DiscardPendingSaves(profileId),
        Store: (profileId, resetAt) => UserDataDbService.Instance.ResetProfileAsync(
            profileId, resetAt, SettingsService.ProfileKeysSurvivingReset),
        RefreshHooks: RefreshHooksFor);

    /// <summary>
    /// Resets <paramref name="target"/> completely, through the real services. Returns rather than
    /// throws: the caller is a dialog that must render "the reset failed and nothing was removed"
    /// as an outcome, not crash on it. Every exit is one of
    /// <see cref="ProfileResetOutcome"/>'s factories.
    /// </summary>
    public Task<ProfileResetOutcome> ResetAsync(AppProfile target)
        => ResetAsync(
            ProfileService.GetProfileId(target),
            // Local time, matching the log-timestamp convention the fence compares against
            // (fix-profile-data-attribution.spec.md records why log timestamps are local).
            DateTime.Now,
            ProductionCollaborators);

    /// <summary>
    /// The reset sequence itself, over the collaborators it drives. Everything the flow needs
    /// arrives here as an argument, so the order the steps run in - the guarantee four services'
    /// doc comments lean on - is assertable without a database, a barrier or a UI thread.
    /// </summary>
    /// <param name="profileId">
    /// The storage partition to reset, captured by the caller when the confirmation opened.
    /// Nothing below reads the ambient selection (PRD R1).
    /// </param>
    /// <param name="resetAt">The watermark the store writes in the same commit.</param>
    /// <param name="deps">Where the four steps go; <see cref="ProductionCollaborators"/> in the app.</param>
    internal static async Task<ProfileResetOutcome> ResetAsync(
        string profileId, DateTime resetAt, ResetCollaborators deps)
    {
        // Barrier up: every persistence write already in flight for this profile completes
        // before the deletes run, and every new one waits until the reset releases. Writes for
        // other profiles are unaffected.
        IAsyncDisposable resetGuard;
        try
        {
            resetGuard = await deps.BeginReset(profileId);
        }
        catch (TimeoutException ex)
        {
            // A wedged write never drained. The barrier lowered itself on the way out, so the
            // profile keeps working; the caller renders "nothing was removed" and its dialog
            // becomes closable again, which an unbounded wait in there could not.
            _log.Error($"Profile reset for {profileId} could not start; nothing was removed", ex);
            return ProfileResetOutcome.Failed(ex);
        }

        try
        {
            // Pending debounced quantities for the target describe rows the transaction is
            // about to delete; flushing them first would write rows only to remove them. A flush
            // already under way cannot put a discarded entry back: SavePendingItemsAsync claims
            // each entry from INSIDE that entry's own tracked write, after the write has passed
            // the barrier, so an entry removed here is simply gone when the flush reaches it.
            // Other profiles' entries stay.
            deps.DiscardStagedWrites(profileId);

            var stored = await RunStoreWithinBudget(
                profileId, () => deps.Store(profileId, resetAt), StoreTimeout);
            if (!stored.Success)
            {
                // No cache was touched, so the app still shows what it showed before; the barrier
                // is lowered by the finally and the caller renders the failure (or, for an
                // abandoned wait, says the outcome is unknown).
                return stored;
            }

            // Only after the commit does memory follow. Each hook no-ops when its loaded state
            // belongs to a different profile, and raises its usual change event so pages refresh
            // through the existing subscriptions.
            RunRefreshHooks(profileId, deps.RefreshHooks(profileId));

            _log.Info($"Profile {profileId} completely reset (watermark {resetAt:o})");
            return ProfileResetOutcome.Succeeded();
        }
        finally
        {
            await resetGuard.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs the store transaction under an overall time bound and turns every failure into an
    /// outcome the dialog can render. A successful outcome means the transaction committed, which
    /// is the only state in which the caller may touch memory.
    /// <para>
    /// The bound exists because the caller cannot wait forever: a wedged transaction would
    /// otherwise hang a modal that refuses to close, with the main window disabled behind it.
    /// Abandoning the wait is not cancelling the work, so the timeout is reported as
    /// <see cref="ProfileResetStatus.Abandoned"/> rather than a failure: PRD R5's "nothing was
    /// removed" comes from the transaction's rollback, which an abandoned call has not necessarily
    /// reached yet, and the dialog has its own sentence for that.
    /// </para>
    /// </summary>
    internal static async Task<ProfileResetOutcome> RunStoreWithinBudget(
        string profileId, Func<Task> storeReset, TimeSpan budget)
    {
        try
        {
            await storeReset().WaitAsync(budget);
            return ProfileResetOutcome.Succeeded();
        }
        catch (TimeoutException)
        {
            _log.Error(
                $"Profile reset for {profileId} did not finish within {budget}; the transaction was " +
                "abandoned and may still commit or roll back on its own");
            return ProfileResetOutcome.Abandoned();
        }
        catch (Exception ex)
        {
            // The transaction rolled back, so nothing was removed and the app still shows the
            // data it showed before.
            _log.Error($"Profile reset for {profileId} failed; nothing was removed", ex);
            return ProfileResetOutcome.Failed(ex);
        }
    }

    /// <summary>
    /// The in-memory consequence of a committed reset, one named hook per service, in the order
    /// they run. Named because <see cref="RunRefreshHooks"/> reports a failure by hook.
    /// </summary>
    private static IReadOnlyList<(string Name, Action Run)> RefreshHooksFor(string profileId)
        => new (string, Action)[]
        {
            ("quest progress", () => QuestProgressService.Instance.HandleProfileReset(profileId)),
            ("hideout progress", () => HideoutProgressService.Instance.HandleProfileReset(profileId)),
            ("item inventory", () => ItemInventoryService.Instance.HandleProfileReset(profileId)),
            ("settings", () => SettingsService.Instance.HandleProfileReset(profileId)),
        };

    /// <summary>
    /// Runs every hook, each in its OWN catch. A hook raises its change event synchronously into
    /// UI handlers this service does not own, so a single throwing subscriber must cost only its
    /// own hook: sharing one catch would leave the database wiped while the pages of the three
    /// hooks that never ran still showed the old levels, quantities and player level.
    /// <para>
    /// Nothing is reported upwards. The transaction has already committed, so a failed refresh is
    /// a stale page (repaired by the next profile switch or restart), not a failed reset, and
    /// reporting "nothing was removed" for one would be false.
    /// </para>
    /// </summary>
    internal static void RunRefreshHooks(
        string profileId, IReadOnlyList<(string Name, Action Run)> hooks)
    {
        foreach (var (name, run) in hooks)
        {
            try
            {
                run();
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"The {name} refresh hook failed for {profileId}; the reset itself committed " +
                    "and the remaining hooks still ran", ex);
            }
        }
    }
}
