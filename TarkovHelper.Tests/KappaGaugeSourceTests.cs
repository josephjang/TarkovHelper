using System.Text.RegularExpressions;
using TarkovHelper.Models;
using TarkovHelper.Pages;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// Where the quest tab's Kappa gauge gets its "done" from: the pass the row statuses were
/// produced under (<c>_rowPass</c>), never a second done-source of its own.
/// <para>
/// The gauge had one. <c>ApplyFilters</c> captures no pass, so <c>UpdateKappaGauge</c> rebuilt a
/// HashSet of the NormalizedNames of the rows whose cached Status was Done and counted the
/// flagged quests against that. Two defects followed: a flagged quest the graph carries but the
/// row list does not was silently missing from the count, and a refresh that reached
/// <c>ApplyFilters</c> with no rows yet (the constructor subscribes to every service event the
/// page consumes, through <c>SubscribeServiceEvents</c>, while <c>LoadQuests</c> sits behind an
/// await, and the quest graph is built before the page is constructed, so the graph guard does
/// not fire) painted "0/13" over a profile that had done some of the thirteen. Remembering the
/// pass - what <c>CollectorPage</c> already does, inside the <c>ListScope</c> its
/// <c>CaptureListScope</c> stores per load - deletes the second source and both defects with it.
/// </para>
/// <para>
/// Structural, because the page cannot be constructed in this suite: its markup resolves
/// App.xaml's brushes through StaticResource and its constructor reaches the singletons that open
/// the databases (see <see cref="SourceGuards"/>). What the guards cannot show is WHY the source
/// matters, so the two counts are also run head to head over a real
/// <see cref="QuestGraphService"/> and a real <see cref="QuestProgressService"/> below.
/// </para>
/// </summary>
public sealed class KappaGaugeSourceTests
{
    private static readonly string Source =
        SourceGuards.Read("TarkovHelper", "Pages", "QuestListPage.xaml.cs");

    private static string Body(string signature) => SourceGuards.MemberBody(Source, signature);

    private static string Gauge => Body("private void UpdateKappaGauge()");

    [Fact]
    public void The_gauge_builds_no_done_source_of_its_own()
    {
        // The row view models are the chips' input, not the gauge's: keying "done" off them made
        // the gauge answer for the quests that happen to have a row instead of for the flagged
        // set. No row list, no HashSet, no per-name lookup.
        Assert.DoesNotContain("_allQuestViewModels", Gauge, StringComparison.Ordinal);
        Assert.DoesNotContain("ToHashSet", Gauge, StringComparison.Ordinal);
        Assert.DoesNotContain("QuestStatus.Done", Gauge, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gauge_counts_within_the_stored_row_pass_through_the_one_count_helper()
    {
        // pass.KappaProgress() is the count the detail pane and the quest window already use;
        // the gauge calling GetKappaProgress itself is how the second predicate got in.
        Assert.Contains("pass.KappaProgress()", Gauge, StringComparison.Ordinal);
        Assert.DoesNotContain("GetKappaProgress(", Gauge, StringComparison.Ordinal);
    }

    [Fact]
    public void No_pass_and_no_graph_are_one_guard_that_paints_no_number()
    {
        // Both are "no data yet", and neither may state a reading: "0/0" reads as "nothing is
        // flagged" - the case QuestGraphService.IsInitialized exists to keep distinguishable -
        // and a count over no rows read as "none of the thirteen are done".
        Assert.Contains(
            "if (_rowPass is not { } pass || pass.KappaProgress() is not { } kappa)",
            Gauge, StringComparison.Ordinal);
        Assert.Contains("TxtKappaGauge.Text = string.Empty;", Gauge, StringComparison.Ordinal);
        Assert.DoesNotContain("\"0/0\"", Gauge, StringComparison.Ordinal);
    }

    [Fact]
    public void The_graph_precondition_is_asked_inside_the_pass_readings_and_nowhere_else()
    {
        // "The graph is not built yet" has one spelling per reading, inside the reading, and
        // every surface takes it as a null rather than as a number: the gauge above, the detail
        // pane's section, and the window the button opens. The section and the button used not
        // to ask at all, so a graph that was not built threw EnsureInitialized onto the
        // dispatcher from a click; then each page carried its own guarded pair, which is four
        // copies of the same two lines. The readings live on the pass now
        // (RenderPassStatusTests runs them), so this page asks the graph nothing at all.
        Assert.DoesNotContain("QuestGraphService.Instance.IsInitialized", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetKappaProgress(", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetKappaQuestsWithStatus(", Source, StringComparison.Ordinal);

        // One guard per reading, where the reading is.
        var pass = SourceGuards.Read("TarkovHelper", "Pages", "RenderPass.cs");

        Assert.Equal(2, Regex.Matches(pass, @"_graph\.IsInitialized \? _graph\.GetKappa").Count);

        Assert.Contains(
            "if (pass.KappaProgress() is not { } kappa)",
            Body("private void UpdateKappaProgressSection(TarkovTask task, RenderPass pass)"),
            StringComparison.Ordinal);
        Assert.Contains(
            "if (pass.KappaQuests() is not { } kappaQuests) return;",
            Body("private void BtnShowKappaQuests_Click(object sender, RoutedEventArgs e)"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_row_pass_is_stored_only_where_the_row_statuses_are_produced()
    {
        // One assignment, inside the capture seam, and only the two producers of the row
        // statuses capture through it. A pass stored by anything else (the detail pane, the
        // Kappa quest window, each a fresh reading of its own) would let the gauge count within
        // snapshots the rows beside it were never derived from.
        Assert.Single(Regex.Matches(Source, @"_rowPass\s*="));
        Assert.Contains("_rowPass = pass;", Body("private RenderPass CaptureRowPass()"),
            StringComparison.Ordinal);

        Assert.Equal(2, Regex.Matches(Source, @"= CaptureRowPass\(\)").Count);
        Assert.Contains("var pass = CaptureRowPass();", Body("private void LoadQuests()"),
            StringComparison.Ordinal);
        Assert.Contains("var pass = CaptureRowPass();", Body("private void RefreshQuestStatuses()"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_gauge_bar_width_comes_from_the_constant_that_mirrors_the_xaml()
    {
        // The bar is a percentage OF the gauge's width, so the number here and the Grid's Width
        // in QuestListPage.xaml are one value: a resized gauge with a stale literal here either
        // clips the bar or leaves it short of full at 100%.
        Assert.Contains("private const double KappaGaugeWidth = 120;", Source, StringComparison.Ordinal);
        Assert.Contains("* KappaGaugeWidth", Gauge, StringComparison.Ordinal);
        Assert.Contains(
            "<Grid Width=\"120\" Height=\"16\"",
            SourceGuards.Read("TarkovHelper", "Pages", "QuestListPage.xaml"),
            StringComparison.Ordinal);
    }

    // The rest is the behaviour behind the guards: the two done-sources, over real services.

    private static TarkovTask Flagged(string name)
        => new()
        {
            Ids = new List<string> { "id-" + name },
            Name = name,
            NormalizedName = name,
            Trader = "Prapor",
            ReqKappa = true,
        };

    private static QuestGraphService Graph(params TarkovTask[] tasks)
    {
        var graph = new QuestGraphService();
        graph.Initialize(tasks.ToList());
        return graph;
    }

    /// <summary>
    /// A progress service holding <paramref name="tasks"/> with <paramref name="done"/> recorded
    /// Done, plus the pass a page would capture from it.
    /// </summary>
    private static (QuestProgressService Progress, RenderPass Pass) Recorded(
        QuestGraphService graph, TarkovTask[] tasks, params TarkovTask[] done)
    {
        var snapshot = ProgressSnapshot.From(
            "profile", 0,
            done.ToDictionary(t => t.Ids![0], _ => QuestStatus.Done),
            new Dictionary<string, bool>());
        var progress = ProgressServiceHarness.Create(new ProgressStoreFake(), snapshot, tasks);
        return (
            progress,
            new RenderPass(
                progress, progress.Snapshot, ProfileSettingsSnapshot.Defaults("profile", 0), graph));
    }

    /// <summary>What the gauge used to ask: is this flagged quest's name among the Done rows.</summary>
    private static Func<TarkovTask, bool> DoneAmongRows(params TarkovTask[] doneRows)
    {
        var names = doneRows.Select(t => t.NormalizedName!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return task => names.Contains(task.NormalizedName!);
    }

    [Fact]
    public void A_flagged_quest_with_no_row_is_counted_within_the_pass_and_was_missed_by_the_rows()
    {
        // The quest tab's rows come from QuestProgressService.AllTasks and the flagged set from
        // the graph's own task list, so the row list is not guaranteed to carry every flagged
        // quest. Counting from the rows dropped the ones it did not carry, silently.
        var withRow = Flagged("shooter-born-in-heaven");
        var withoutRow = Flagged("collector");
        var graph = Graph(withRow, withoutRow);
        var (_, pass) = Recorded(graph, new[] { withRow }, withRow, withoutRow);

        Assert.Equal((2, 2), Both(pass.KappaProgress()));
        // The old source could only ever see the quest it had a row for.
        Assert.Equal((1, 2), Both(graph.GetKappaProgress(DoneAmongRows(withRow))));
    }

    [Fact]
    public void An_empty_row_list_counted_zero_while_the_pass_counts_the_recorded_progress()
    {
        // The refresh that reaches ApplyFilters before LoadQuests has built a single row: every
        // service event the page consumes is subscribed in the constructor and the graph is
        // already built, so this really happens, and it painted 0 of thirteen over a profile
        // mid-progression.
        var done = Flagged("chemical-part-1");
        var notDone = Flagged("collector");
        var graph = Graph(done, notDone);
        var (_, pass) = Recorded(graph, new[] { done, notDone }, done);

        Assert.Equal((1, 2), Both(pass.KappaProgress()));
        Assert.Equal((0, 2), Both(graph.GetKappaProgress(DoneAmongRows())));
    }

    [Fact]
    public void A_recorded_done_that_is_not_flagged_moves_neither_count()
    {
        // The gauge counts the thirteen, not completions: the pass-based predicate is only ever
        // asked about flagged quests, so a hundred other completions leave it at zero.
        var flagged = Flagged("collector");
        var unflagged = new TarkovTask
        {
            Ids = new List<string> { "id-debut" }, Name = "debut", NormalizedName = "debut",
            Trader = "Prapor", ReqKappa = false,
        };
        var graph = Graph(flagged, unflagged);
        var (_, pass) = Recorded(graph, new[] { flagged, unflagged }, unflagged);

        Assert.Equal((0, 1), Both(pass.KappaProgress()));
    }

    /// <summary>
    /// The two numbers a gauge paints, out of a reading that has to be there: a null would be
    /// "the graph is not built", which none of these cases is about.
    /// </summary>
    private static (int Completed, int Total) Both((int Completed, int Total, int Percentage)? progress)
    {
        Assert.NotNull(progress);
        return (progress.Value.Completed, progress.Value.Total);
    }
}
