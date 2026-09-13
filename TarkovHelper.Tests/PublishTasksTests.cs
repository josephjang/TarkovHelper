using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The mid-session republish: when a data publish lands, <c>QuestDbService</c> swaps in new task
/// objects and MainWindow hands them to the two services that are HANDED their tasks, so every
/// quest surface (which all render <c>QuestProgressService.AllTasks</c>) and the Kappa
/// denominator catch up in-session instead of staying on the previous publish until restart.
/// <para>
/// The shape under test is that the republish carries the tasks only: the recorded progress is
/// keyed by Id/NormalizedName, not by task instance, so re-reading it from the store would be
/// both unnecessary and, on the dispatcher, blocking.
/// </para>
/// </summary>
public sealed class PublishTasksTests
{
    private const string Profile = "pve";

    private static TarkovTask Quest(string id, string name, bool kappa = false) => new()
    {
        Ids = new List<string> { id },
        Name = name,
        NormalizedName = name,
        Trader = "Prapor",
        ReqKappa = kappa,
    };

    /// <summary>
    /// Happy path: after a republish the three lookups and the task list answer the NEW publish,
    /// and the snapshot is the very one that was seeded - not a re-read of the store. The second
    /// assertion is the load-bearing one: routing a mid-session republish through
    /// <c>Initialize</c> would replace the snapshot with whatever the store returns, which is
    /// what makes this the difference between the two entry points rather than a restatement of
    /// the first assertion.
    /// </summary>
    [Fact]
    public void A_republish_swaps_the_task_set_and_leaves_the_recorded_progress_untouched()
    {
        var oldQuest = Quest("id-debut", "debut");
        var store = new ProgressStoreFake();
        var seeded = ProgressSnapshot.From(
            Profile, 7,
            new Dictionary<string, QuestStatus> { ["debut"] = QuestStatus.Done },
            new Dictionary<string, bool>());
        var service = ProgressServiceHarness.Create(store, seeded, oldQuest);

        var renamed = Quest("id-debut", "debut-renamed");
        var added = Quest("id-checking", "checking");
        service.PublishTasks(new List<TarkovTask> { renamed, added });

        // The new publish answers every lookup, and the quest it dropped is gone from all of them.
        Assert.Equal(new[] { renamed, added }, service.AllTasks);
        Assert.Same(renamed, service.GetTask("debut-renamed"));
        Assert.Same(added, service.GetTaskById("id-checking"));
        Assert.Null(service.GetTask("debut"));

        // The player's rows are untouched, down to the instance: a publish changes which quests
        // exist, never which rows were recorded.
        Assert.Same(seeded, service.Snapshot);
        Assert.Equal(QuestStatus.Done, service.Snapshot.Quests["debut"]);
        Assert.Equal(7, service.Snapshot.Revision);
        Assert.Empty(store.QuestWrites);
        Assert.Empty(store.QuestDeletes);
    }

    /// <summary>
    /// Edge case: a publish that removes every quest the player had recorded still leaves the
    /// rows alone. The snapshot is keyed by Id/NormalizedName, so a row whose quest is gone
    /// simply matches nothing until a publish brings the quest back - it must not be dropped,
    /// because the next publish would then read as "never completed".
    /// </summary>
    [Fact]
    public void A_republish_with_no_overlap_keeps_the_rows_of_quests_it_removed()
    {
        var store = new ProgressStoreFake();
        var seeded = ProgressSnapshot.From(
            Profile, 3,
            new Dictionary<string, QuestStatus> { ["debut"] = QuestStatus.Done },
            new Dictionary<string, bool> { ["debut:0"] = true });
        var service = ProgressServiceHarness.Create(store, seeded, Quest("id-debut", "debut"));

        service.PublishTasks(new List<TarkovTask> { Quest("id-checking", "checking") });

        Assert.Null(service.GetTask("debut"));
        Assert.Same(seeded, service.Snapshot);
        Assert.Equal(QuestStatus.Done, service.Snapshot.Quests["debut"]);
        Assert.True(service.Snapshot.Objectives["debut:0"]);
    }

    /// <summary>
    /// Edge case: an empty publish is published as empty rather than throwing or half-applying.
    /// MainWindow's handler is what declines to hand an empty reload over (an empty load is a
    /// failed load, not a publish that deleted every quest); the service itself stays total.
    /// </summary>
    [Fact]
    public void An_empty_republish_empties_the_lookups_without_touching_progress()
    {
        var store = new ProgressStoreFake();
        var seeded = ProgressSnapshot.From(
            Profile, 1,
            new Dictionary<string, QuestStatus> { ["debut"] = QuestStatus.Done },
            new Dictionary<string, bool>());
        var service = ProgressServiceHarness.Create(store, seeded, Quest("id-debut", "debut"));

        service.PublishTasks(new List<TarkovTask>());

        Assert.Empty(service.AllTasks);
        Assert.Null(service.GetTask("debut"));
        Assert.Null(service.GetTaskById("id-debut"));
        Assert.Same(seeded, service.Snapshot);
    }

    /// <summary>
    /// The graph half of the same republish: re-initializing with a list whose Kappa flags differ
    /// moves the denominator, which is what makes a publish that adds or removes a Kappa flag
    /// visible in-session. Without the republish this number stays on the previous publish while
    /// the item table beside it is fresh.
    /// </summary>
    [Fact]
    public void Re_initializing_the_graph_moves_the_kappa_denominator_to_the_new_publish()
    {
        var collector = Quest("id-collector", "collector", kappa: true);
        var flagged = Quest("id-shooter", "shooter-born-in-heaven", kappa: true);
        var graph = new QuestGraphService();
        graph.Initialize(new List<TarkovTask> { collector, flagged });

        Assert.Equal(2, graph.GetKappaProgress(_ => false).Total);

        // The new publish flags one more quest and unflags one of the two.
        var unflagged = Quest("id-shooter", "shooter-born-in-heaven", kappa: false);
        var added = Quest("id-chemical", "chemical-part-1", kappa: true);
        var alsoAdded = Quest("id-setup", "setup", kappa: true);
        graph.Initialize(new List<TarkovTask> { collector, unflagged, added, alsoAdded });

        var (completed, total, _) = graph.GetKappaProgress(task => task.NormalizedName == "collector");
        Assert.Equal(3, total);
        Assert.Equal(1, completed);

        // The unflagged quest is gone from the list the popup renders, not merely uncounted.
        var listed = graph.GetKappaQuestsWithStatus(_ => false).Select(entry => entry.Quest.NormalizedName);
        Assert.DoesNotContain("shooter-born-in-heaven", listed);
    }
}
