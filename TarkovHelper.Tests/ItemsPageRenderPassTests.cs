using System.Text.RegularExpressions;

namespace TarkovHelper.Tests;

/// <summary>
/// The Items page reads every quest status from one captured <c>RenderPass</c>, asserted
/// structurally against the source.
/// <para>
/// Structurally, because the page cannot be constructed in this suite: its markup resolves
/// App.xaml's brushes through StaticResource and its constructor reaches the singletons that
/// open the databases (see <see cref="SourceGuards"/>). What is pinned here is the shape the
/// defect had: the list aggregation and the detail pane each walked the quests calling the live
/// per-quest overload, against two snapshots (recorded progress and profile settings) that
/// background work republishes atomically, so a publish landing mid-walk left the rows mixing
/// two profiles and the pane naming quests the rows had not counted.
/// </para>
/// </summary>
public sealed class ItemsPageRenderPassTests
{
    private static readonly string Source =
        SourceGuards.Read("TarkovHelper", "Pages", "ItemsPage.xaml.cs");

    private static string Body(string signature) => SourceGuards.MemberBody(Source, signature);

    [Fact]
    public void The_item_load_captures_one_pass_and_hands_it_to_the_aggregation()
    {
        // One capture per load, remembered for the detail pane and threaded into the walk, so
        // both halves of the page describe one profile.
        var load = Body("private Task LoadItemsAsync()");

        Assert.Single(Regex.Matches(load, @"RenderPass\.Capture\("));
        Assert.Contains("_listPass = pass;", load, StringComparison.Ordinal);
        Assert.Contains("GetQuestItemRequirements(pass)", load, StringComparison.Ordinal);
    }

    [Fact]
    public void The_load_is_the_only_place_the_page_captures_a_pass()
    {
        // A second capture, anywhere, would reintroduce the divergence from the other end: the
        // detail pane would answer from a pass the rows were not built under.
        Assert.Single(Regex.Matches(Source, @"RenderPass\.Capture\("));
    }

    [Fact]
    public void The_aggregation_reads_each_quest_status_from_the_pass_it_was_handed()
    {
        var aggregate = Body(
            "private Dictionary<string, QuestItemAggregate> GetQuestItemRequirements(RenderPass pass)");

        Assert.Contains("pass.StatusOf(task).Status", aggregate, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detail_pane_answers_from_the_remembered_pass_and_is_empty_before_the_first_load()
    {
        // Not a fresh pass and not the live singletons: the pane's quest sources have to name
        // exactly the quests the rendered rows counted. Before the first load there is no such
        // pass, so there are no sources to name.
        var sources = Body("private List<QuestItemSourceViewModel> GetQuestSources(string itemNormalizedName)");

        Assert.Contains("if (_listPass is not { } pass) return sources;", sources, StringComparison.Ordinal);
        Assert.Contains("pass.StatusOf(task).Status", sources, StringComparison.Ordinal);
    }

    [Fact]
    public void No_status_on_the_page_is_read_live_per_quest()
    {
        // The two live reads this guard replaced: the aggregation's and the detail pane's. Both
        // walked every quest against whatever the singletons held at that instant.
        Assert.Empty(Regex.Matches(Source, @"GetStatus\("));
    }
}
