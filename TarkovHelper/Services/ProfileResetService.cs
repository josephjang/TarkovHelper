using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// What one complete profile reset did: success with everything the profile owned removed, or
/// failure with nothing removed (PRD R5 of feature-complete-profile-reset.md). There is no
/// partial outcome by construction: the removal is one SQLite transaction.
/// </summary>
/// <param name="Success">True when the transaction committed and the caches were refreshed.</param>
/// <param name="Error">The failure's message for the result dialog; null on success.</param>
public sealed record ProfileResetOutcome(bool Success, string? Error);

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
    /// Resets <paramref name="target"/> completely. Returns rather than throws: the caller is
    /// a dialog that must render "the reset failed and nothing was removed" as an outcome, not
    /// crash on it.
    /// </summary>
    public async Task<ProfileResetOutcome> ResetAsync(AppProfile target)
    {
        var profileId = ProfileService.GetProfileId(target);

        // Local time, matching the log-timestamp convention the fence compares against
        // (fix-profile-data-attribution.spec.md records why log timestamps are local).
        var resetAt = DateTime.Now;

        // Barrier up: every persistence write already in flight for this profile completes
        // before the deletes run, and every new one waits until the reset releases. Writes for
        // other profiles are unaffected.
        var resetGuard = await TrackedUserDataWrites.BeginResetAsync(profileId);
        try
        {
            // Pending debounced quantities for the target describe rows the transaction is
            // about to delete; flushing them first would write rows only to remove them. This
            // runs under the barrier, so no flush is mid-flight. Other profiles' entries stay.
            ItemInventoryService.Instance.DiscardPendingSaves(profileId);

            try
            {
                await UserDataDbService.Instance.ResetProfileAsync(
                    profileId, resetAt, SettingsService.ProfileKeysSurvivingReset);
            }
            catch (Exception ex)
            {
                // No cache was touched, so the app still shows the surviving data; the barrier
                // is lowered by the finally and the caller reports that nothing was removed.
                _log.Error($"Profile reset for {profileId} failed; nothing was removed", ex);
                return new ProfileResetOutcome(false, ex.Message);
            }

            // Only after the commit does memory follow. Each hook no-ops when its loaded state
            // belongs to a different profile, and raises its usual change event so pages
            // refresh through the existing subscriptions. The hooks run inside their own catch:
            // the transaction has already committed, so a throwing change-event subscriber must
            // not turn a durable success into a "nothing was removed" report that is now false.
            try
            {
                QuestProgressService.Instance.HandleProfileReset(profileId);
                HideoutProgressService.Instance.HandleProfileReset(profileId);
                ItemInventoryService.Instance.HandleProfileReset(profileId);
                SettingsService.Instance.HandleProfileReset(profileId);
            }
            catch (Exception ex)
            {
                _log.Error($"A post-reset refresh hook failed for {profileId}; the reset itself committed", ex);
            }

            _log.Info($"Profile {profileId} completely reset (watermark {resetAt:o})");
            return new ProfileResetOutcome(true, null);
        }
        finally
        {
            await resetGuard.DisposeAsync();
        }
    }
}
