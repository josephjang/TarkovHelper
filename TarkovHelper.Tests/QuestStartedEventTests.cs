using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// A "quest started" log event records what the start proves, the prerequisites, and leaves the
/// started quest Active: that is what <c>QuestProgressService.PlanLogEvent</c>'s own comment
/// promises, and what the main window relies on when it routes every live log event through
/// <see cref="QuestProgressService.ApplyLogEventAsync"/>.
/// <para>
/// It did not hold. The walk the plan ran over answered the target quest as its last entry, and
/// the batch planner marks every entry Done, so every quest the live sync saw a player START was
/// recorded as finished. The contract now lives in the two walks' names:
/// <see cref="QuestGraphService.GetAllPrerequisites"/> excludes the target,
/// <see cref="QuestGraphService.GetPrerequisiteClosure"/> includes it. Found while correcting the
/// walk's comment during feature-kappa-collector-1-1; nothing covered the Started path before
/// this.
/// </para>
/// </summary>
public sealed class QuestStartedEventTests
{
    private static string IdOf(AppProfile profile) => ProfileService.GetProfileId(profile);

    /// <summary>
    /// The graph the plan walks: one plain prerequisite of the started quest, plus any
    /// <paramref name="extraPrerequisites"/> the caller wants the walk to answer alongside it.
    /// It is handed to the service through its <c>Graph</c> seam rather than installed into
    /// <see cref="QuestGraphService.Instance"/>, because a graph left in the process-global
    /// singleton would still be there for every later test in the assembly.
    /// </summary>
    private static (QuestGraphService Graph, TarkovTask Prerequisite, TarkovTask Started) Graph(
        params TarkovTask[] extraPrerequisites)
    {
        var prerequisite = TestTasks.Quest("p-1", "prerequisite");
        var started = TestTasks.Quest("s-1", "started");
        started.Previous = new List<string> { prerequisite.NormalizedName! };
        foreach (var extra in extraPrerequisites)
        {
            started.Previous.Add(extra.NormalizedName!);
        }

        var tasks = new List<TarkovTask> { prerequisite, started };
        tasks.AddRange(extraPrerequisites);
        var graph = new QuestGraphService();
        graph.Initialize(tasks);
        return (graph, prerequisite, started);
    }

    [Fact]
    public void No_test_in_this_suite_installs_a_process_wide_graph()
    {
        // The plan walks the graph the service was handed, so nothing here needs the singleton.
        // Stated rather than implied: this is the guarantee the Graph seam exists to give, and
        // the assertion fails the moment any test in the assembly installs a graph globally,
        // which is the state this suite used to leave behind for every test that ran after it.
        Graph(TestTasks.Quest("x-1", "extra"));

        Assert.False(QuestGraphService.Instance.IsInitialized);
    }

    [Fact]
    public async Task A_started_quest_stays_active_and_only_its_prerequisites_are_recorded()
    {
        var (graph, prerequisite, started) = Graph();
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(
            store, AppProfile.PveZone, graph, prerequisite, started);

        await service.ApplyLogEventAsync(started, QuestEventType.Started, AppProfile.PveZone, DateTime.Now);

        // The prerequisite is what the start proves; it is recorded under the key the write
        // named (its Id), as every loaded-profile write is.
        var rows = ProgressServiceHarness.LoadedQuestsOf(service);
        Assert.Equal(QuestStatus.Done, rows["p-1"]);
        Assert.False(rows.ContainsKey("s-1"), "the started quest was recorded instead of left Active");
        Assert.Equal(
            QuestStatus.Active,
            service.GetStatus(started, service.Snapshot, ProfileSettingsSnapshot.Defaults("pve", 0)));
        // The stored row is keyed by NormalizedName, the shape a reload reads back.
        Assert.Equal(new[] { "prerequisite" }, store.QuestsOf(IdOf(AppProfile.PveZone)).Keys);
    }

    [Fact]
    public async Task A_started_quest_with_no_prerequisites_writes_nothing()
    {
        var (graph, _, started) = Graph();
        started.Previous = null;
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, graph, started);

        await service.ApplyLogEventAsync(started, QuestEventType.Started, AppProfile.PveZone, DateTime.Now);

        // Not a vacuous assertion: the closure walk still answers the started quest itself for a
        // quest with no Previous, so a plan built on it would hold one Done row here.
        Assert.Empty(ProgressServiceHarness.LoadedQuestsOf(service));
        Assert.Empty(store.QuestsOf(IdOf(AppProfile.PveZone)));
        Assert.Equal(
            QuestStatus.Active,
            service.GetStatus(started, service.Snapshot, ProfileSettingsSnapshot.Defaults("pve", 0)));
    }

    [Fact]
    public async Task A_mutually_exclusive_prerequisite_of_a_started_quest_is_not_recorded()
    {
        // The other half of the Started case's contract: the start proves the plain prerequisite
        // but says nothing about which of two mutually exclusive predecessors was taken, so the
        // planner's "alternative quests skipped (the user must choose which one they completed)"
        // rule has to reach this call site. It does only while the Started case leaves
        // PlanBatchCompletion's skipAlternativeQuests at its default.
        var alternative = TestTasks.Quest("a-1", "alternative");
        alternative.AlternativeQuests = new List<string> { "the-other-branch" };
        var (graph, prerequisite, started) = Graph(alternative);
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(
            store, AppProfile.PveZone, graph, prerequisite, alternative, started);

        await service.ApplyLogEventAsync(started, QuestEventType.Started, AppProfile.PveZone, DateTime.Now);

        // The walk answered both prerequisites; exactly one of them is the player's to claim.
        var rows = ProgressServiceHarness.LoadedQuestsOf(service);
        Assert.Equal(QuestStatus.Done, rows["p-1"]);
        Assert.False(rows.ContainsKey("a-1"), "a mutually exclusive prerequisite was recorded Done");
        Assert.False(rows.ContainsKey("s-1"), "the started quest was recorded instead of left Active");
        Assert.Equal(
            new[] { "prerequisite" },
            store.QuestsOf(IdOf(AppProfile.PveZone)).Keys.OrderBy(key => key, StringComparer.Ordinal));
    }
}
