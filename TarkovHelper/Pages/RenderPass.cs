using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Pages;

/// <summary>
/// What one render pass reads from, and every reading it answers: the recorded progress and the
/// profile-scoped player settings as they stood when the pass was captured, plus the two services
/// that interpret them. Captured ONCE at the top of a pass and threaded through it, so a profile
/// publish landing mid-pass cannot render a quest's status from one profile and its badge, or the
/// Requirements line under it, from another - the tearing
/// <see cref="ProfileSettingsSnapshot"/> exists to make unobservable.
/// <see cref="QuestProgressService.GetStatus(TarkovTask, ProgressSnapshot, ProfileSettingsSnapshot)"/>
/// takes the same pair for the same reason.
/// <para>
/// A file of its own rather than a nested type on the quest page, because three pages now
/// capture one: the quest page for its rows, chips and detail pane, the Collector page for its
/// unlock panel and the item list under it, and the Items page for its quest list and detail
/// pane. The pass answers the status itself (<see cref="StatusOf"/>), so a page holding one has
/// no reason to reach for the live per-quest overload and no way to read one by accident: this
/// is the only file under Pages/ that names <c>GetStatus</c> at all, apart from the map page,
/// and <c>RenderPassStatusGuardTests</c> keeps Pages/ that way rather than leaving it to review.
/// The live overload stays for callers that are not rendering anything and are right to read
/// the current state: the quest recommendation service, the sync comparisons, and the
/// in-progress quest dialog (feature-kappa-collector-1-1.spec.md, Risks).
/// </para>
/// <para>
/// The Kappa readings (<see cref="KappaProgress"/>, <see cref="KappaQuests"/>) are here for the
/// same reason the status is. Both pages that show a Kappa number used to spell them as a private
/// helper pair of their own - the graph query over the pass's own done-ness, behind the
/// <see cref="QuestGraphService.IsInitialized"/> guard - which was four copies of two one-liners
/// with nothing stopping a fifth from forgetting the guard and painting "0/0" over a graph that
/// is not built yet. On the pass they are written once, and the graph they are answered from is
/// the one the pass was captured with: a surface holding a pass cannot reach past it for a live
/// reading, because the graph is not a member of it.
/// </para>
/// <para>
/// A class rather than the record struct it started as: with two services in it this is a read
/// context and not a value, equality over service references would mean nothing, and
/// <c>default(RenderPass)</c> - a pass whose services are null - stops being spellable.
/// </para>
/// </summary>
internal sealed class RenderPass
{
    /// <summary>
    /// The quest graph the pass's Kappa readings are answered from. Private, so the readings below
    /// are the only door to it: a caller that could reach the graph could ask it for a count
    /// without the built-yet guard, which is the defect the guard exists for.
    /// </summary>
    private readonly QuestGraphService _graph;

    internal RenderPass(
        QuestProgressService service,
        ProgressSnapshot progress,
        ProfileSettingsSnapshot settings,
        QuestGraphService graph)
    {
        Service = service;
        Progress = progress;
        Settings = settings;
        _graph = graph;
    }

    /// <summary>The progress service whose rules read this pass's snapshots.</summary>
    internal QuestProgressService Service { get; }

    /// <summary>The recorded progress as it stood at the capture.</summary>
    internal ProgressSnapshot Progress { get; }

    /// <summary>The profile-scoped player settings as they stood at the capture.</summary>
    internal ProfileSettingsSnapshot Settings { get; }

    /// <summary>
    /// The snapshots for one pass, read from the progress service handed in and from the
    /// settings singleton, against the quest graph singleton. Call once per pass, never per quest.
    /// </summary>
    internal static RenderPass Capture(QuestProgressService progress)
        => Capture(progress, QuestGraphService.Instance);

    /// <summary>
    /// The same capture against a given quest graph, for a caller that holds one: the Collector
    /// page threads its own field through, so the graph its item scope is walked with and the
    /// graph its Kappa count is read from are provably the same object.
    /// </summary>
    internal static RenderPass Capture(QuestProgressService progress, QuestGraphService graph)
        => new(progress, progress.Snapshot, SettingsService.Instance.ProfileSettings, graph);

    /// <summary>
    /// The status of one quest within this pass, against this pass's snapshots, together with
    /// the gate the walk stopped at - the requirement the badge names. Both come from the one
    /// call, so no pane can name a gate other than the one that produced the status it shows.
    /// </summary>
    internal (QuestStatus Status, QuestGate Gate) StatusOf(TarkovTask task)
    {
        var status = Service.GetStatus(task, Progress, Settings, out var gate);
        return (status, gate);
    }

    /// <summary>Whether <paramref name="task"/> is Done within this pass.</summary>
    internal bool IsDone(TarkovTask task) => StatusOf(task).Status == QuestStatus.Done;

    /// <summary>
    /// The Kappa count within this pass - the flagged quests, Collector included, against this
    /// pass's snapshots (see <see cref="QuestGraphService.GetKappaProgress"/>) - or null when the
    /// graph is not built yet. Null is "no reading", never a zero: a painted "0/0" reads as
    /// "nothing is flagged", which is the case <see cref="QuestGraphService.IsInitialized"/>
    /// exists to keep distinguishable. The guard lives here so no surface can forget it.
    /// </summary>
    internal (int Completed, int Total, int Percentage)? KappaProgress()
        => _graph.IsInitialized ? _graph.GetKappaProgress(IsDone) : null;

    /// <summary>
    /// The flagged quests with their done state within this pass, or null when the graph is not
    /// built. The same "no reading rather than an empty one" <see cref="KappaProgress"/> answers
    /// with: an empty list would open a window saying nothing is flagged.
    /// </summary>
    internal List<(TarkovTask Quest, bool IsCompleted)>? KappaQuests()
        => _graph.IsInitialized ? _graph.GetKappaQuestsWithStatus(IsDone) : null;
}
