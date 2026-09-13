using TarkovHelper.Models;
using TarkovHelper.Pages;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// The readings a <see cref="RenderPass"/> answers: a quest's status and the gate the walk
/// stopped at, and the Kappa count over those statuses, all taken from the pass's own snapshots
/// and its own quest graph rather than from whatever the singletons carry at the moment of the
/// call.
/// <para>
/// Both pages used to spell each of those reads themselves - a private <c>StatusIn(pass, task)</c>
/// over their own progress-service field, and a private <c>KappaProgressIn</c>/<c>KappaQuestsIn</c>
/// pair over the graph singleton - the same lines twice over, with nothing stopping a third copy
/// from passing the LIVE snapshot instead, or a fourth from dropping the "is the graph built"
/// guard and painting "0/0" over a graph that carries nothing yet. Moving them onto the pass is
/// what makes the wrong reading unspellable: the only snapshots in scope are the pass's own, and
/// the graph is not a member a caller can reach.
/// </para>
/// </summary>
public sealed class RenderPassStatusTests
{
    private static TarkovTask Collector() => new()
    {
        Ids = new List<string> { "id-collector" },
        Name = "collector",
        NormalizedName = "collector",
        Trader = "Fence",
        ReqKappa = true,
    };

    private static ProgressSnapshot Snapshot(params (string Id, QuestStatus Status)[] rows)
        => ProgressSnapshot.From(
            "profile", 0,
            rows.ToDictionary(row => row.Id, row => row.Status),
            new Dictionary<string, bool>());

    [Fact]
    public void A_status_read_through_the_pass_comes_from_the_pass_and_not_from_the_live_service()
    {
        // The service holds a Done row; the pass was captured before it landed. The point of a
        // pass is that the surface rendering from it keeps saying what it started saying, so the
        // two answers must differ here, and the pass's must be the older one.
        var quest = Collector();
        var settings = ProfileSettingsSnapshot.Defaults("profile", 0);
        var live = Snapshot(("id-collector", QuestStatus.Done));
        var progress = ProgressServiceHarness.Create(new ProgressStoreFake(), live, quest);

        var pass = new RenderPass(progress, Snapshot(), settings, new QuestGraphService());

        Assert.Equal(QuestStatus.Done, progress.GetStatus(quest, progress.Snapshot, settings));
        Assert.Equal(QuestStatus.Active, pass.StatusOf(quest).Status);
        Assert.False(pass.IsDone(quest));
    }

    [Fact]
    public void The_gate_comes_from_the_same_call_as_the_status()
    {
        // The badge names a requirement, and it has to be the one that produced the status beside
        // it. StatusOf answers both from the one walk, so a caller cannot pair a status from one
        // reading with a gate from another.
        var quest = Collector();
        quest.RequiredLevel = 42;
        var progress = ProgressServiceHarness.Create(new ProgressStoreFake(), Snapshot(), quest);

        var (status, gate) = new RenderPass(
            progress, progress.Snapshot, ProfileSettingsSnapshot.Defaults("profile", 0),
            new QuestGraphService()).StatusOf(quest);

        Assert.Equal(QuestStatus.LevelLocked, status);
        Assert.Equal(QuestGate.PlayerLevel, gate);
    }

    [Fact]
    public void A_done_quest_in_the_pass_reads_done_through_the_pass()
    {
        // The happy frame of IsDone, which is what the Kappa count is walked with: a row the
        // pass's own snapshot carries as Done.
        var quest = Collector();
        var done = Snapshot(("id-collector", QuestStatus.Done));
        var progress = ProgressServiceHarness.Create(new ProgressStoreFake(), done, quest);

        var pass = new RenderPass(
            progress, progress.Snapshot, ProfileSettingsSnapshot.Defaults("profile", 0),
            new QuestGraphService());

        Assert.True(pass.IsDone(quest));
    }

    [Fact]
    public void A_graph_that_is_not_built_yet_is_no_Kappa_reading_rather_than_a_zero_one()
    {
        // The pages show a Kappa number from a pass, and every one of them takes null as "say
        // nothing": a "0/0" would read as "nothing is flagged", which is the case
        // QuestGraphService.IsInitialized exists to keep apart from "no data yet". The guard used
        // to be spelled twice per page, so a third surface could reach the graph without it -
        // and the graph does not answer quietly, it throws (KappaProgressTests).
        var quest = Collector();
        var progress = ProgressServiceHarness.Create(
            new ProgressStoreFake(), Snapshot(("id-collector", QuestStatus.Done)), quest);
        var graph = new QuestGraphService();

        var pass = new RenderPass(
            progress, progress.Snapshot, ProfileSettingsSnapshot.Defaults("profile", 0), graph);

        Assert.False(graph.IsInitialized);
        Assert.Null(pass.KappaProgress());
        Assert.Null(pass.KappaQuests());

        // The same pass, once the graph it was captured with has been built: the count is the
        // pass's own done-ness, not a live walk, and the flagged quest it carries is listed.
        graph.Initialize(new List<TarkovTask> { quest });

        Assert.Equal((1, 1, 100), pass.KappaProgress());
        Assert.Equal(new[] { (quest, true) }, pass.KappaQuests());
    }

    [Fact]
    public void The_Kappa_count_answers_within_the_pass_and_not_from_the_live_service()
    {
        // The reason the count moved onto the pass at all: a profile publish landing between the
        // capture and the count must not make the number disagree with the chips beside it.
        var quest = Collector();
        var live = Snapshot(("id-collector", QuestStatus.Done));
        var progress = ProgressServiceHarness.Create(new ProgressStoreFake(), live, quest);
        var graph = new QuestGraphService();
        graph.Initialize(new List<TarkovTask> { quest });

        // Captured over the EMPTY snapshot, while the service already holds the Done row.
        var pass = new RenderPass(
            progress, Snapshot(), ProfileSettingsSnapshot.Defaults("profile", 0), graph);

        Assert.Equal((0, 1, 0), pass.KappaProgress());
        Assert.Equal(new[] { (quest, false) }, pass.KappaQuests());
    }
}
