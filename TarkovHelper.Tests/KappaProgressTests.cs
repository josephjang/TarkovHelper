using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The one Kappa count every surface shows (the quest tab's gauge, Collector's detail pane, the
/// Kappa quest list and the Collector page), as <see cref="QuestGraphService"/> computes it.
/// <para>
/// Membership is the game's own flag (<see cref="TarkovTask.ReqKappa"/>), Collector included,
/// and "done" is whatever the caller's render pass says: the count takes a predicate over the
/// task rather than over its name, so the page that shows the number decides against the same
/// snapshots it drew the rows beside it from. The old shape took a name and re-ran a live status
/// walk per quest against the singletons, which is the one reading on the quest tab that could
/// disagree with the chips next to it during a profile switch
/// (feature-kappa-collector-1-1.spec.md, TD2 and TD3).
/// </para>
/// </summary>
public sealed class KappaProgressTests
{
    private static TarkovTask Quest(string normalizedName, bool kappa, string trader = "Prapor")
        => new()
        {
            Ids = new List<string> { "id-" + normalizedName },
            Name = normalizedName,
            NormalizedName = normalizedName,
            Trader = trader,
            ReqKappa = kappa,
        };

    private static QuestGraphService Graph(params TarkovTask[] tasks)
    {
        var graph = new QuestGraphService();
        graph.Initialize(tasks.ToList());
        return graph;
    }

    private static Func<TarkovTask, bool> DoneWhen(params TarkovTask[] done)
        => task => done.Contains(task);

    [Fact]
    public void The_count_runs_over_flagged_quests_only_and_Collector_is_one_of_them()
    {
        var collector = Quest("collector", kappa: true, trader: "Fence");
        var flagged = Quest("shooter-born-in-heaven", kappa: true);
        var unflagged = Quest("debut", kappa: false);

        var (completed, total, _) = Graph(collector, flagged, unflagged)
            .GetKappaProgress(DoneWhen(collector));

        // Two flagged, one of them done. The flag-less quest is nowhere in either number.
        Assert.Equal(2, total);
        Assert.Equal(1, completed);
    }

    [Fact]
    public void A_done_quest_without_the_flag_is_not_counted()
    {
        // The count is not "done quests": a player with a hundred completions and none of the
        // thirteen reads 0, or the gauge would move for quests that do not earn the container.
        var flagged = Quest("chemical-part-1", kappa: true);
        var doneButUnflagged = Quest("debut", kappa: false);

        var (completed, total, percentage) = Graph(flagged, doneButUnflagged)
            .GetKappaProgress(DoneWhen(doneButUnflagged));

        Assert.Equal((0, 1, 0), (completed, total, percentage));
    }

    [Fact]
    public void The_percentage_is_an_integer_rounded_down()
    {
        var a = Quest("a", kappa: true);
        var b = Quest("b", kappa: true);
        var c = Quest("c", kappa: true);
        var graph = Graph(a, b, c);

        Assert.Equal(33, graph.GetKappaProgress(DoneWhen(a)).Percentage);
        Assert.Equal(66, graph.GetKappaProgress(DoneWhen(a, b)).Percentage);
        Assert.Equal(100, graph.GetKappaProgress(DoneWhen(a, b, c)).Percentage);
    }

    [Fact]
    public void Zero_flagged_quests_is_zero_of_zero_at_zero_percent()
    {
        // A publish that flagged nothing must not divide by zero into an exception on every
        // ApplyFilters; the gauge reads 0/0.
        var graph = Graph(Quest("debut", kappa: false));

        Assert.Equal((0, 0, 0), graph.GetKappaProgress(_ => true));
    }

    [Fact]
    public void A_flagged_quest_with_no_normalized_name_is_left_out_of_both_numbers()
    {
        // The guard the old shape had because it keyed the predicate by name. Kept: a row with no
        // name has no progress key either, so it can never be done and would only inflate the
        // total.
        var nameless = new TarkovTask { Ids = new List<string> { "x" }, Name = "x", ReqKappa = true };
        var named = Quest("a", kappa: true);

        var (completed, total, _) = Graph(nameless, named).GetKappaProgress(_ => true);

        Assert.Equal((1, 1), (completed, total));
    }

    [Fact]
    public void The_list_puts_incomplete_quests_first_then_orders_by_trader_then_name()
    {
        var zTherapistDone = Quest("z", kappa: true, trader: "Therapist");
        var bPrapor = Quest("b", kappa: true, trader: "Prapor");
        var aPrapor = Quest("a", kappa: true, trader: "Prapor");
        var cJaegerDone = Quest("c", kappa: true, trader: "Jaeger");
        var unflagged = Quest("debut", kappa: false);

        var list = Graph(zTherapistDone, bPrapor, aPrapor, cJaegerDone, unflagged)
            .GetKappaQuestsWithStatus(DoneWhen(zTherapistDone, cJaegerDone));

        Assert.Equal(
            new[] { ("a", false), ("b", false), ("c", true), ("z", true) },
            list.Select(entry => (entry.Quest.NormalizedName!, entry.IsCompleted)).ToArray());
    }

    [Fact]
    public void The_predicate_is_asked_once_per_flagged_quest_and_is_handed_the_task()
    {
        // TD3: the caller decides "done" against its own pass, so it must be given the task the
        // pass can look up, exactly once, and never a name it would have to resolve again.
        var collector = Quest("collector", kappa: true, trader: "Fence");
        var flagged = Quest("sew-it-good-part-1", kappa: true);
        var unflagged = Quest("debut", kappa: false);
        var graph = Graph(collector, flagged, unflagged);

        var askedForCount = new List<TarkovTask>();
        graph.GetKappaProgress(task => { askedForCount.Add(task); return false; });

        var askedForList = new List<TarkovTask>();
        graph.GetKappaQuestsWithStatus(task => { askedForList.Add(task); return false; });

        Assert.Equal(new[] { collector, flagged }, askedForCount);
        Assert.Equal(new[] { collector, flagged }, askedForList);
    }

    [Fact]
    public void An_uninitialized_graph_says_so_rather_than_counting_nothing()
    {
        // The pages ask before counting instead of catching: a page that swallowed the
        // exception into "0/0" could not tell "no data yet" from "nothing is flagged".
        var graph = new QuestGraphService();

        Assert.False(graph.IsInitialized);
        Assert.Throws<InvalidOperationException>(() => graph.GetKappaProgress(_ => true));

        graph.Initialize(new List<TarkovTask> { Quest("a", kappa: true) });

        Assert.True(graph.IsInitialized);
        Assert.Equal((0, 1, 0), graph.GetKappaProgress(_ => false));
    }
}
