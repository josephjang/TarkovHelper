using System.Text.RegularExpressions;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Which quest IS Collector (<see cref="QuestGraphService.IsCollectorQuest"/>): the rule the
/// Collector page's whole item list and unlock panel hang on, and the one the quest tab's detail
/// pane shows its Kappa section for.
/// <para>
/// It used to be spelled three times - once in each page and once in a method of the graph
/// service that nothing called - so a data publish that renamed the row would have had to be
/// chased through three files, and a Collector page showing nothing beside a quest tab still
/// claiming to show Collector's Kappa count was one edit away. These cases assert the rule and
/// that the literal it is built on now appears once.
/// </para>
/// </summary>
public sealed class CollectorIdentityTests
{
    private static TarkovTask Quest(string? normalizedName)
        => new()
        {
            Ids = new List<string> { "id-" + (normalizedName ?? "none") },
            Name = normalizedName ?? "unnamed",
            NormalizedName = normalizedName,
            Trader = "Fence",
        };

    private static QuestGraphService Graph(params TarkovTask[] tasks)
    {
        var graph = new QuestGraphService();
        graph.Initialize(tasks.ToList());
        return graph;
    }

    [Fact]
    public void The_rule_picks_Collector_out_of_the_loaded_quests()
    {
        // The lookup both pages do: the loaded rows, filtered by the one rule.
        var collector = Quest("collector");
        var tasks = new[] { Quest("debut"), collector, Quest("shooter-born-in-heaven") };
        var graph = Graph(tasks);

        Assert.Same(collector, tasks.FirstOrDefault(QuestGraphService.IsCollectorQuest));
        Assert.Same(collector, graph.GetTask("collector"));
    }

    [Fact]
    public void It_answers_nothing_when_the_data_carries_no_Collector_quest()
    {
        // The page collapses its unlock panel and lists no items on this answer, so it has to be
        // an absence and not an exception or a wrong row.
        var tasks = new[] { Quest("debut"), Quest("the-punisher-part-1") };

        Assert.Null(tasks.FirstOrDefault(QuestGraphService.IsCollectorQuest));
        Assert.Null(Array.Empty<TarkovTask>().FirstOrDefault(QuestGraphService.IsCollectorQuest));
    }

    [Theory]
    [InlineData("collector", true)]
    [InlineData("Collector", true)]
    [InlineData("COLLECTOR", true)]
    [InlineData("collectors", false)]
    [InlineData("the-collector", false)]
    [InlineData("debut", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_rule_is_the_published_normalized_name_case_insensitively(
        string? normalizedName, bool isCollector)
    {
        // Case-insensitive because the data's casing is not a contract, and false for a row with
        // no normalized name at all: that row cannot be identified, and treating it as Collector
        // would hand the page a quest with no progress key.
        Assert.Equal(isCollector, QuestGraphService.IsCollectorQuest(Quest(normalizedName)));
    }

    [Fact]
    public void The_quest_name_is_spelled_in_exactly_one_place()
    {
        // The guard behind the other cases: a fourth spelling would pass every one of them and
        // still be a second rule to keep in step.
        var literal = new Regex("\"collector\"");

        var sources = new[]
        {
            ("QuestGraphService.cs", SourceGuards.Read("TarkovHelper", "Services", "QuestGraphService.cs")),
            ("CollectorPage.xaml.cs", SourceGuards.Read("TarkovHelper", "Pages", "CollectorPage.xaml.cs")),
            ("QuestListPage.xaml.cs", SourceGuards.Read("TarkovHelper", "Pages", "QuestListPage.xaml.cs")),
        };

        foreach (var (name, source) in sources)
        {
            var expected = name == "QuestGraphService.cs" ? 1 : 0;
            Assert.Equal(expected, literal.Matches(source).Count);
        }

        // And it is the constant the rule reads, not an unrelated string that happens to match.
        Assert.Contains(
            "private const string CollectorNormalizedName = \"collector\";",
            sources[0].Item2,
            StringComparison.Ordinal);

        // Both former sites now ask the rule.
        Assert.Contains(
            "FirstOrDefault(QuestGraphService.IsCollectorQuest)",
            sources[1].Item2,
            StringComparison.Ordinal);
        Assert.Contains(
            "QuestGraphService.IsCollectorQuest(task)",
            sources[2].Item2,
            StringComparison.Ordinal);
    }
}
