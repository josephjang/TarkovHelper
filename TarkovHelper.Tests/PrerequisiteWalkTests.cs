using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The two prerequisite walks and the one difference between them:
/// <see cref="QuestGraphService.GetPrerequisiteClosure"/> answers the target quest as its last
/// entry, <see cref="QuestGraphService.GetAllPrerequisites"/> answers what its name promises and
/// nothing else.
/// <para>
/// Written because the inclusive walk used to be the one called GetAllPrerequisites: every
/// caller that wanted the prerequisites had to remember to drop the target, and the quest-started
/// path (see <c>QuestStartedEventTests</c>) is where forgetting it recorded a quest the player
/// had only just started as finished.
/// </para>
/// </summary>
public sealed class PrerequisiteWalkTests
{
    /// <summary>
    /// A private graph, never <see cref="QuestGraphService.Instance"/>: a graph installed in the
    /// process-global singleton would outlive this class and be there for every later test.
    /// </summary>
    private static QuestGraphService Graph(params TarkovTask[] tasks)
    {
        var graph = new QuestGraphService();
        graph.Initialize(tasks.ToList());
        return graph;
    }

    private static TarkovTask Quest(string id, string name, params string[] previous)
    {
        var task = TestTasks.Quest(id, name);
        if (previous.Length > 0) task.Previous = previous.ToList();
        return task;
    }

    private static string[] NamesOf(IEnumerable<TarkovTask> tasks)
        => tasks.Select(t => t.NormalizedName!).ToArray();

    [Fact]
    public void The_closure_includes_the_target_and_the_prerequisite_walk_does_not()
    {
        var first = Quest("a-1", "first");
        var second = Quest("b-1", "second", "first");
        var graph = Graph(first, second);

        Assert.Equal(new[] { "first" }, NamesOf(graph.GetAllPrerequisites("second")));
        Assert.Equal(new[] { "first", "second" }, NamesOf(graph.GetPrerequisiteClosure("second")));
    }

    [Fact]
    public void A_root_quest_has_no_prerequisites_but_is_its_own_closure()
    {
        var only = Quest("a-1", "first");
        var graph = Graph(only);

        Assert.Empty(graph.GetAllPrerequisites("first"));
        Assert.Equal(new[] { "first" }, NamesOf(graph.GetPrerequisiteClosure("first")));
    }

    [Fact]
    public void The_target_is_excluded_case_insensitively()
    {
        // The lookup the walk reads is OrdinalIgnoreCase, so it can answer a task whose stored
        // name differs in case from the name asked for. The exclusion has to match that.
        var first = Quest("a-1", "first");
        var second = Quest("b-1", "second", "first");
        var graph = Graph(first, second);

        Assert.Equal(new[] { "first" }, NamesOf(graph.GetAllPrerequisites("SECOND")));
        Assert.Empty(graph.GetAllPrerequisites("FIRST"));
    }

    [Fact]
    public void An_unknown_quest_walks_to_nothing()
    {
        var graph = Graph(Quest("a-1", "first"));

        Assert.Empty(graph.GetAllPrerequisites("no-such-quest"));
        Assert.Empty(graph.GetPrerequisiteClosure("no-such-quest"));
    }

    [Fact]
    public void A_cycle_terminates_and_still_excludes_the_target()
    {
        // Bad data, not a real quest tree: the visiting set is what stops the recursion, and the
        // exclusion must not depend on the target being the LAST entry, which a cycle breaks.
        var a = Quest("a-1", "a", "b");
        var b = Quest("b-1", "b", "a");
        var graph = Graph(a, b);

        Assert.Equal(new[] { "b" }, NamesOf(graph.GetAllPrerequisites("a")));
        Assert.Equal(new[] { "b", "a" }, NamesOf(graph.GetPrerequisiteClosure("a")));
        Assert.Equal(new[] { "a" }, NamesOf(graph.GetAllPrerequisites("b")));
    }

    [Fact]
    public void A_diamond_lists_the_shared_ancestor_once_and_the_target_never()
    {
        var root = Quest("r-1", "root");
        var left = Quest("l-1", "left", "root");
        var right = Quest("g-1", "right", "root");
        var target = Quest("t-1", "target", "left", "right");
        var graph = Graph(root, left, right, target);

        var prerequisites = NamesOf(graph.GetAllPrerequisites("target"));

        Assert.Equal(new[] { "root", "left", "right" }, prerequisites);
        Assert.Equal(new[] { "root", "left", "right", "target" },
            NamesOf(graph.GetPrerequisiteClosure("target")));
    }

    [Fact]
    public void Both_walks_throw_before_the_graph_is_initialized()
    {
        var graph = new QuestGraphService();

        Assert.Throws<InvalidOperationException>(() => graph.GetAllPrerequisites("first"));
        Assert.Throws<InvalidOperationException>(() => graph.GetPrerequisiteClosure("first"));
    }
}
