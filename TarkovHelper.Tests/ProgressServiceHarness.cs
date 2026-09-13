using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Builds a <see cref="QuestProgressService"/> with its state seeded directly, so the private
/// constructor's <see cref="ProfileService"/> subscription and the user_data.db load are both
/// skipped. Same technique as <c>ProfileSwitchingTests</c> and <c>TestLocalization</c>.
/// <para>
/// Note this deliberately does NOT go through the singleton: these tests assert which storage
/// partition a write named, and a shared singleton carrying another test's snapshot would make
/// that answer depend on test order.
/// </para>
/// </summary>
internal static class ProgressServiceHarness
{
    public static QuestProgressService Create(
        IQuestProgressStore store,
        AppProfile loadedProfile,
        params TarkovTask[] tasks)
        => Create(store, loadedProfile, graph: null, tasks);

    /// <summary>
    /// The same service with the quest dependency graph its Started plan walks handed in, so a
    /// test that needs a graph builds a private <see cref="QuestGraphService"/> instead of
    /// initializing the process-global <see cref="QuestGraphService.Instance"/> that every other
    /// test in the assembly shares.
    /// </summary>
    public static QuestProgressService Create(
        IQuestProgressStore store,
        AppProfile loadedProfile,
        QuestGraphService? graph,
        params TarkovTask[] tasks)
        => Create(store, ProgressSnapshot.Empty(ProfileService.GetProfileId(loadedProfile), 0), graph, tasks);

    public static QuestProgressService Create(
        IQuestProgressStore store,
        ProgressSnapshot snapshot,
        params TarkovTask[] tasks)
        => Create(store, snapshot, graph: null, tasks);

    public static QuestProgressService Create(
        IQuestProgressStore store,
        ProgressSnapshot snapshot,
        QuestGraphService? graph,
        params TarkovTask[] tasks)
    {
        var service = TestReflection.Uninitialized<QuestProgressService>();

        var byId = new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            foreach (var id in task.Ids ?? new List<string>())
            {
                if (!string.IsNullOrEmpty(id)) byId[id] = task;
            }
            if (task.NormalizedName != null) byName[task.NormalizedName] = task;
        }

        TestReflection.SetPrivateField(service, "_tasksById", byId);
        TestReflection.SetPrivateField(service, "_tasksByNormalizedName", byName);
        TestReflection.SetPrivateField(
            service, "_tasksByBsgId", new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase));
        TestReflection.SetPrivateField(service, "_allTasks", tasks.ToList());

        service.Store = store;
        service.Snapshot = snapshot;
        // Left at its lazy default (the singleton) when no graph is asked for, so the many tests
        // that never reach the graph keep working unchanged.
        if (graph != null) service.Graph = graph;
        return service;
    }

    /// <summary>The quest rows the service currently holds in memory, by progress key.</summary>
    public static IReadOnlyDictionary<string, QuestStatus> LoadedQuestsOf(QuestProgressService service)
        => service.Snapshot.Quests;

    /// <summary>The profile whose rows the service currently holds.</summary>
    public static string LoadedProfileOf(QuestProgressService service) => service.Snapshot.ProfileId;
}
