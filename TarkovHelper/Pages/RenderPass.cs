using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Pages;

/// <summary>
/// The two snapshots one render pass reads: the recorded progress and the profile-scoped
/// player settings. Captured ONCE at the top of a pass and threaded through it, so a profile
/// publish landing mid-pass cannot render a quest's status from one profile and its badge, or
/// the Requirements line under it, from another - the tearing
/// <see cref="ProfileSettingsSnapshot"/> exists to make unobservable.
/// <see cref="QuestProgressService.GetStatus(TarkovTask, ProgressSnapshot, ProfileSettingsSnapshot)"/>
/// takes the same pair for the same reason.
/// <para>
/// A file of its own rather than a nested type on the quest page, because two pages now capture
/// one: the quest page for its rows, chips and detail pane, and the Collector page for its
/// unlock panel and the item list under it. A future page that reads a status should capture
/// one of these too rather than call <c>GetStatus(task)</c> live per quest. Nothing enforces
/// that by structure, which is why the reason is written here
/// (feature-kappa-collector-1-1.spec.md, Risks).
/// </para>
/// </summary>
internal readonly record struct RenderPass(ProgressSnapshot Progress, ProfileSettingsSnapshot Settings)
{
    /// <summary>
    /// The snapshots for one pass, read from the progress service handed in and from the
    /// settings singleton. Call once per pass, never per quest.
    /// </summary>
    internal static RenderPass Capture(QuestProgressService progress)
        => new(progress.Snapshot, SettingsService.Instance.ProfileSettings);
}
