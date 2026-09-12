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
/// It did not hold. The prerequisite walk the plan runs over
/// (<see cref="QuestGraphService.GetAllPrerequisites"/>) answers the target quest as its last
/// entry, and the batch planner marks every entry Done, so every quest the live sync saw a
/// player START was recorded as finished. Found while correcting the walk's comment during
/// feature-kappa-collector-1-1; nothing covered the Started path before this.
/// </para>
/// </summary>
[Collection(SchedulingSensitiveCollection.Name)]
public sealed class QuestStartedEventTests
{
    private static string IdOf(AppProfile profile) => ProfileService.GetProfileId(profile);

    /// <summary>
    /// The plan walks the graph through the singleton, so the graph is the test's two quests.
    /// </summary>
    private static (TarkovTask Prerequisite, TarkovTask Started) Graph()
    {
        var prerequisite = TestTasks.Quest("p-1", "prerequisite");
        var started = TestTasks.Quest("s-1", "started");
        started.Previous = new List<string> { prerequisite.NormalizedName! };
        QuestGraphService.Instance.Initialize(new List<TarkovTask> { prerequisite, started });
        return (prerequisite, started);
    }

    [Fact]
    public async Task A_started_quest_stays_active_and_only_its_prerequisites_are_recorded()
    {
        var (prerequisite, started) = Graph();
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, prerequisite, started);

        await service.ApplyLogEventAsync(started, QuestEventType.Started, AppProfile.PveZone, DateTime.Now);

        // The prerequisite is what the start proves; it is recorded under the key the write
        // named (its Id), as every loaded-profile write is.
        var rows = ProgressServiceHarness.LoadedQuestsOf(service);
        Assert.Equal(QuestStatus.Done, rows["p-1"]);
        Assert.False(rows.ContainsKey("s-1"), "the started quest was recorded instead of left Active");
        Assert.Equal(
            QuestStatus.Active,
            service.GetStatus(started, service.Snapshot, ProfileSettingsSnapshot.Defaults("pve", 0)));
        Assert.Single(store.QuestsOf(IdOf(AppProfile.PveZone)));
    }

    [Fact]
    public async Task A_started_quest_with_no_prerequisites_writes_nothing()
    {
        var (_, started) = Graph();
        started.Previous = null;
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, started);

        await service.ApplyLogEventAsync(started, QuestEventType.Started, AppProfile.PveZone, DateTime.Now);

        Assert.Empty(ProgressServiceHarness.LoadedQuestsOf(service));
        Assert.Empty(store.QuestsOf(IdOf(AppProfile.PveZone)));
    }
}
