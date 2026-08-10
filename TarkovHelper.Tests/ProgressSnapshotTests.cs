using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the timing half of fix-profile-data-attribution.spec.md: the progress cache and the
/// profile it belongs to are one immutable value, so a write always names the profile whose data
/// the writer actually saw, and a reload that lost a race cannot publish over a newer one.
/// </summary>
public sealed class ProgressSnapshotTests
{
    private static TarkovTask Task(string id, string name) => new()
    {
        Ids = new List<string> { id },
        Name = name,
        NormalizedName = name,
        Trader = "Prapor",
    };

    private static readonly TarkovTask Quest = Task("q-1", "a-quest");

    private static string IdOf(AppProfile profile) => ProfileService.GetProfileId(profile);

    // FailQuest and ResetQuest defer their write to a Task.Run body, so it can still be pending
    // when a profile switch completes. The write must land in the partition the edit was made
    // against, whatever the selection has become by the time it runs (PRD R5).
    //
    // Scope note: this catches an implementation that resolves the destination from live state
    // at write time. It cannot catch the ORIGINAL defect shape on its own -- reading
    // ProfileService.ActiveProfileId, which had already moved ahead of the cache before the
    // write was even scheduled. ProfileAttributionSourceTests is the guard for that, by keeping
    // the lookup out of these paths entirely.
    [Theory]
    [InlineData(nameof(QuestProgressService.FailQuest))]
    [InlineData(nameof(QuestProgressService.ResetQuest))]
    public async Task A_deferred_write_names_the_profile_the_edit_was_made_against(string method)
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PveZone), ("q-1", QuestStatus.Done));
        var service = ProgressServiceHarness.Create(
            store,
            ProgressSnapshot.From(
                IdOf(AppProfile.PveZone), 0,
                new Dictionary<string, QuestStatus> { ["q-1"] = QuestStatus.Done },
                new Dictionary<string, bool>()),
            Quest);

        // Every write blocks until the switch has completed in full. Reloads only read, so the
        // gate holds the deferred write open across the whole transition.
        var switched = new TaskCompletionSource();
        store.SaveGate = async _ => await switched.Task;

        if (method == nameof(QuestProgressService.FailQuest)) service.FailQuest(Quest);
        else service.ResetQuest(Quest);

        await service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 1);
        Assert.Equal(IdOf(AppProfile.PvpSeason), ProgressServiceHarness.LoadedProfileOf(service));
        switched.SetResult();

        if (method == nameof(QuestProgressService.FailQuest))
        {
            await WaitUntil(
                () => store.QuestsOf(IdOf(AppProfile.PveZone)).TryGetValue("q-1", out var s) && s == QuestStatus.Failed,
                "the deferred failure to reach the PvE partition");
        }
        else
        {
            await WaitUntil(() => !store.QuestsOf(IdOf(AppProfile.PveZone)).ContainsKey("q-1"),
                "the deferred reset to reach the PvE partition");
        }

        Assert.Empty(store.QuestsOf(IdOf(AppProfile.PvpSeason)));
    }

    // A completion is persisted under the profile whose rows were on screen when it was made,
    // even though the selection has already moved on and the reload has not caught up. This is
    // the window the pre-snapshot code wrote the WRONG profile in: ActiveProfileId already named
    // the new profile while the cache still held the old one's rows (PRD R5).
    [Fact]
    public async Task An_edit_made_while_a_reload_is_in_flight_persists_under_the_profile_on_screen()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, Quest);

        var loadStarted = new TaskCompletionSource();
        var releaseLoad = new TaskCompletionSource();
        store.LoadGate = async _ =>
        {
            loadStarted.TrySetResult();
            await releaseLoad.Task;
        };

        // A transition to PvP Season is announced; its reload blocks inside the store.
        var reload = service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 1);
        await loadStarted.Task;

        // The user checks the quest off while the PvE rows are still what they can see.
        service.CompleteQuest(Quest, completePrerequisites: false);

        releaseLoad.SetResult();
        await reload;

        // Waits out the fire-and-forget save the UI path schedules.
        await WaitUntil(() => store.QuestsOf(IdOf(AppProfile.PveZone)).ContainsKey("q-1"),
            "the edit to reach the PvE partition");

        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PveZone))["q-1"]);
        Assert.False(store.QuestsOf(IdOf(AppProfile.PvpSeason)).ContainsKey("q-1"),
            "an edit made against PvE rows was recorded under PvP Season");

        // The swap still completed: the screen now shows the season.
        Assert.Equal(IdOf(AppProfile.PvpSeason), ProgressServiceHarness.LoadedProfileOf(service));
    }

    // Two transitions in flight can finish in either order. The later one must win regardless,
    // or the snapshot ends up naming the newer profile while holding the older one's rows.
    [Fact]
    public async Task A_reload_that_finishes_late_does_not_replace_a_newer_one()
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PveZone), ("q-1", QuestStatus.Done));
        store.Seed(IdOf(AppProfile.PvpSeason), ("q-2", QuestStatus.Failed));

        var service = ProgressServiceHarness.Create(store, AppProfile.PvpZone, Quest);

        var pveLoadStarted = new TaskCompletionSource();
        var releasePve = new TaskCompletionSource();
        store.LoadGate = async profileId =>
        {
            if (profileId != IdOf(AppProfile.PveZone)) return;
            pveLoadStarted.TrySetResult();
            await releasePve.Task;
        };

        var stale = service.ReloadForProfileAsync(AppProfile.PveZone, revision: 1);
        await pveLoadStarted.Task;

        // A newer transition lands and completes first.
        await service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 2);
        Assert.Equal(IdOf(AppProfile.PvpSeason), ProgressServiceHarness.LoadedProfileOf(service));

        releasePve.SetResult();
        await stale;

        Assert.Equal(IdOf(AppProfile.PvpSeason), ProgressServiceHarness.LoadedProfileOf(service));
        Assert.True(ProgressServiceHarness.LoadedQuestsOf(service).ContainsKey("q-2"));
        Assert.False(ProgressServiceHarness.LoadedQuestsOf(service).ContainsKey("q-1"),
            "a stale reload published the previous profile's rows over a newer one");
    }

    // Quest and objective rows are two halves of one snapshot: a reader must never see one
    // profile's quests beside another's objectives.
    [Fact]
    public async Task A_reload_publishes_quest_and_objective_rows_together()
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PvpSeason), ("q-1", QuestStatus.Done));
        store.SeedObjective(IdOf(AppProfile.PvpSeason), "a-quest:0", true);

        var service = ProgressServiceHarness.Create(store, AppProfile.PvpZone, Quest);

        var observedBetweenLoads = new List<(string ProfileId, bool HasQuest, bool HasObjective)>();
        store.LoadGate = _ =>
        {
            var snapshot = service.Snapshot;
            observedBetweenLoads.Add((snapshot.ProfileId,
                snapshot.Quests.ContainsKey("q-1"), snapshot.Objectives.ContainsKey("a-quest:0")));
            return System.Threading.Tasks.Task.CompletedTask;
        };

        await service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 1);

        // Neither loader saw a half-swapped snapshot: throughout both reads the field still held
        // the previous profile, complete and self-consistent.
        Assert.All(observedBetweenLoads, observed =>
        {
            Assert.Equal(IdOf(AppProfile.PvpZone), observed.ProfileId);
            Assert.False(observed.HasQuest);
            Assert.False(observed.HasObjective);
        });

        Assert.True(service.Snapshot.Quests.ContainsKey("q-1"));
        Assert.True(service.Snapshot.Objectives["a-quest:0"]);
    }

    // One objective edit records two keys (index for the Quests tab, id for the Map tracker).
    // The rows are written one at a time with no transaction, so the profile has to be resolved
    // once up front; resolving it per row let a transition mid-batch split one edit permanently.
    [Fact]
    public async Task Both_keys_of_one_objective_edit_land_in_the_same_partition()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, Quest);

        // Hold the save open after its FIRST row, so the transition below lands squarely in the
        // middle of the batch — the window that used to split one objective across two
        // partitions permanently, since the rows are written one connection at a time.
        var firstRowStarted = new TaskCompletionSource();
        var releaseSecondRow = new TaskCompletionSource();
        var rowsStarted = 0;
        store.SaveGate = async _ =>
        {
            if (Interlocked.Increment(ref rowsStarted) != 2) return;
            firstRowStarted.TrySetResult();
            await releaseSecondRow.Task;
        };

        service.SetObjectiveCompleted("a-quest", 0, completed: true, objectiveId: "obj-1");
        await firstRowStarted.Task;

        await service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 1);
        releaseSecondRow.SetResult();

        await WaitUntil(() => store.ObjectiveWrites.Count >= 2, "both objective rows to be written");

        Assert.All(store.ObjectiveWrites, write =>
            Assert.Equal(IdOf(AppProfile.PveZone), write.ProfileId));
        Assert.True(store.ObjectivesOf(IdOf(AppProfile.PveZone))["a-quest:0"]);
        Assert.True(store.ObjectivesOf(IdOf(AppProfile.PveZone))["id:obj-1"]);
        Assert.Empty(store.ObjectivesOf(IdOf(AppProfile.PvpSeason)));
    }

    // PRD R4 plus the silent-write decision: a raid event for a mode the player is not looking at
    // is recorded where it belongs and changes nothing on screen.
    [Fact]
    public async Task A_log_event_for_an_unloaded_profile_writes_the_database_and_not_the_snapshot()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, Quest);
        var progressChanged = 0;
        service.ProgressChanged += (_, _) => progressChanged++;

        await service.ApplyLogEventAsync(Quest, QuestEventType.Completed, AppProfile.PvpSeason);

        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PvpSeason))["q-1"]);
        Assert.Empty(store.QuestsOf(IdOf(AppProfile.PveZone)));
        Assert.False(ProgressServiceHarness.LoadedQuestsOf(service).ContainsKey("q-1"),
            "another profile's raid progress appeared in the loaded snapshot");
        Assert.Equal(0, progressChanged);
    }

    [Fact]
    public async Task A_log_event_for_the_loaded_profile_updates_the_snapshot_and_notifies()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, Quest);
        var progressChanged = 0;
        service.ProgressChanged += (_, _) => progressChanged++;

        await service.ApplyLogEventAsync(Quest, QuestEventType.Completed, AppProfile.PveZone);

        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PveZone))["q-1"]);
        Assert.Equal(QuestStatus.Done, ProgressServiceHarness.LoadedQuestsOf(service)["q-1"]);
        Assert.Equal(1, progressChanged);
    }

    // A log-detected failure belongs to the raid's mode too, and must not be re-written when the
    // same evidence is replayed (the startup scan replays the last lines).
    [Fact]
    public async Task A_failure_event_is_recorded_once_under_the_raids_profile()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PvpZone, Quest);

        await service.ApplyLogEventAsync(Quest, QuestEventType.Failed, AppProfile.PveZone);
        await service.ApplyLogEventAsync(Quest, QuestEventType.Failed, AppProfile.PveZone);

        Assert.Equal(QuestStatus.Failed, store.QuestsOf(IdOf(AppProfile.PveZone))["q-1"]);
        Assert.Single(store.QuestWrites);
    }

    // A batch aimed at an unloaded profile compares against THAT profile's stored rows, not the
    // loaded cache: judging "already Done" from the wrong profile would skip a real change.
    [Fact]
    public async Task A_batch_for_an_unloaded_profile_is_judged_against_that_profiles_rows()
    {
        var store = new ProgressStoreFake();
        // Loaded profile already has it Done; the target profile does not.
        store.Seed(IdOf(AppProfile.PvpZone), ("q-1", QuestStatus.Done));
        var service = ProgressServiceHarness.Create(
            store,
            ProgressSnapshot.From(
                IdOf(AppProfile.PvpZone), 0,
                new Dictionary<string, QuestStatus> { ["q-1"] = QuestStatus.Done },
                new Dictionary<string, bool>()),
            Quest);

        await service.ApplyQuestChangesBatchAsync(new[] { (Quest, QuestStatus.Done) }, AppProfile.PveZone);

        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PveZone))["q-1"]);
    }

    [Fact]
    public async Task Resetting_a_quest_deletes_it_from_the_loaded_profile_only()
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PveZone), ("q-1", QuestStatus.Done));
        store.Seed(IdOf(AppProfile.PvpSeason), ("q-1", QuestStatus.Done));
        var service = ProgressServiceHarness.Create(
            store,
            ProgressSnapshot.From(
                IdOf(AppProfile.PveZone), 0,
                new Dictionary<string, QuestStatus> { ["q-1"] = QuestStatus.Done },
                new Dictionary<string, bool>()),
            Quest);

        service.ResetQuest(Quest);

        await WaitUntil(() => !store.QuestsOf(IdOf(AppProfile.PveZone)).ContainsKey("q-1"),
            "the reset to reach the PvE partition");
        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PvpSeason))["q-1"]);
        Assert.False(ProgressServiceHarness.LoadedQuestsOf(service).ContainsKey("q-1"));
    }

    // A store that cannot be read must not leave the previous profile's rows on screen labelled
    // with the new profile's name.
    [Fact]
    public async Task A_failed_reload_publishes_an_empty_snapshot_rather_than_the_previous_rows()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(
            store,
            ProgressSnapshot.From(
                IdOf(AppProfile.PveZone), 0,
                new Dictionary<string, QuestStatus> { ["q-1"] = QuestStatus.Done },
                new Dictionary<string, bool>()),
            Quest);

        store.LoadGate = _ => throw new InvalidOperationException("database is locked");

        await service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 1);

        Assert.Equal(IdOf(AppProfile.PvpSeason), ProgressServiceHarness.LoadedProfileOf(service));
        Assert.Empty(ProgressServiceHarness.LoadedQuestsOf(service));
    }

    private static async Task WaitUntil(Func<bool> condition, string description)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await System.Threading.Tasks.Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for {description}");
    }
}
