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
    private static readonly TarkovTask Quest = TestTasks.Quest("q-1", "a-quest");

    /// <summary>
    /// The key <see cref="Quest"/>'s stored row comes back under. Writes name the row by Id
    /// ("q-1"), reads key it by <c>NormalizedName ?? Id</c>, so everything read out of the store
    /// - and every snapshot a reload publishes - is name-keyed. The asymmetry is the store's, not
    /// the test's: see ProgressStoreFakeTests.
    /// </summary>
    private static readonly string StoredKey = Quest.NormalizedName!;

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
        store.Seed(IdOf(AppProfile.PveZone), Quest, QuestStatus.Done);
        var service = ProgressServiceHarness.Create(
            store,
            ProgressSnapshot.From(
                IdOf(AppProfile.PveZone), 0,
                new Dictionary<string, QuestStatus> { [StoredKey] = QuestStatus.Done },
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
                () => store.QuestsOf(IdOf(AppProfile.PveZone)).TryGetValue(StoredKey, out var s)
                      && s == QuestStatus.Failed,
                "the deferred failure to reach the PvE partition");
        }
        else
        {
            await WaitUntil(() => !store.QuestsOf(IdOf(AppProfile.PveZone)).ContainsKey(StoredKey),
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
        await WaitUntil(() => store.QuestsOf(IdOf(AppProfile.PveZone)).ContainsKey(StoredKey),
            "the edit to reach the PvE partition");

        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PveZone))[StoredKey]);
        Assert.False(store.QuestsOf(IdOf(AppProfile.PvpSeason)).ContainsKey(StoredKey),
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
        var seasonQuest = TestTasks.Quest("q-2", "another-quest");
        store.Seed(IdOf(AppProfile.PveZone), Quest, QuestStatus.Done);
        store.Seed(IdOf(AppProfile.PvpSeason), seasonQuest, QuestStatus.Failed);

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
        Assert.True(ProgressServiceHarness.LoadedQuestsOf(service).ContainsKey(seasonQuest.NormalizedName!));
        Assert.False(ProgressServiceHarness.LoadedQuestsOf(service).ContainsKey(StoredKey),
            "a stale reload published the previous profile's rows over a newer one");
    }

    // Quest and objective rows are two halves of one snapshot: a reader must never see one
    // profile's quests beside another's objectives.
    [Fact]
    public async Task A_reload_publishes_quest_and_objective_rows_together()
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PvpSeason), Quest, QuestStatus.Done);
        store.SeedObjective(IdOf(AppProfile.PvpSeason), "a-quest:0", true);

        var service = ProgressServiceHarness.Create(store, AppProfile.PvpZone, Quest);

        var observedBetweenLoads = new List<(string ProfileId, bool HasQuest, bool HasObjective)>();
        store.LoadGate = _ =>
        {
            var snapshot = service.Snapshot;
            observedBetweenLoads.Add((snapshot.ProfileId,
                snapshot.Quests.ContainsKey(StoredKey), snapshot.Objectives.ContainsKey("a-quest:0")));
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

        Assert.True(service.Snapshot.Quests.ContainsKey(StoredKey));
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
        // middle of the batch: the window that used to split one objective across two
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

        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PvpSeason))[StoredKey]);
        Assert.Empty(store.QuestsOf(IdOf(AppProfile.PveZone)));
        // Empty rather than "no q-1": a leak into the snapshot would arrive under the write key
        // ("q-1") while a leaked reload would arrive under the stored key, and neither belongs.
        Assert.Empty(ProgressServiceHarness.LoadedQuestsOf(service));
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

        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PveZone))[StoredKey]);
        // In memory the row keeps the key the WRITE named, until the next reload re-reads it
        // under its name. The write also drops the name spelling if one was already loaded, so
        // the two keys never both hold a status for the same quest.
        Assert.Equal(QuestStatus.Done, ProgressServiceHarness.LoadedQuestsOf(service)["q-1"]);
        Assert.Single(ProgressServiceHarness.LoadedQuestsOf(service));
        Assert.Equal(QuestStatus.Done, service.GetStatus(Quest));
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

        Assert.Equal(QuestStatus.Failed, store.QuestsOf(IdOf(AppProfile.PveZone))[StoredKey]);
        // Once, not twice: the second pass re-reads the stored row under its NAME, so an
        // Id-only "is it already Failed" check would plan the write all over again.
        Assert.Single(store.QuestWrites);
    }

    // A batch aimed at an unloaded profile compares against THAT profile's stored rows, not the
    // loaded cache: judging "already Done" from the wrong profile would skip a real change.
    [Fact]
    public async Task A_batch_for_an_unloaded_profile_is_judged_against_that_profiles_rows()
    {
        var store = new ProgressStoreFake();
        // Loaded profile already has it Done; the target profile does not.
        store.Seed(IdOf(AppProfile.PvpZone), Quest, QuestStatus.Done);
        var service = ProgressServiceHarness.Create(
            store,
            ProgressSnapshot.From(
                IdOf(AppProfile.PvpZone), 0,
                new Dictionary<string, QuestStatus> { [StoredKey] = QuestStatus.Done },
                new Dictionary<string, bool>()),
            Quest);

        await service.ApplyQuestChangesBatchAsync(new[] { (Quest, QuestStatus.Done) }, AppProfile.PveZone);

        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PveZone))[StoredKey]);
    }

    [Fact]
    public async Task Resetting_a_quest_deletes_it_from_the_loaded_profile_only()
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PveZone), Quest, QuestStatus.Done);
        store.Seed(IdOf(AppProfile.PvpSeason), Quest, QuestStatus.Done);
        var service = ProgressServiceHarness.Create(
            store,
            ProgressSnapshot.From(
                IdOf(AppProfile.PveZone), 0,
                new Dictionary<string, QuestStatus> { [StoredKey] = QuestStatus.Done },
                new Dictionary<string, bool>()),
            Quest);

        service.ResetQuest(Quest);

        await WaitUntil(() => !store.QuestsOf(IdOf(AppProfile.PveZone)).ContainsKey(StoredKey),
            "the reset to reach the PvE partition");
        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PvpSeason))[StoredKey]);
        Assert.Empty(ProgressServiceHarness.LoadedQuestsOf(service));

        // Every delete the reset issued named the profile the row was on screen under.
        Assert.NotEmpty(store.QuestDeletes);
        Assert.All(store.QuestDeletes, delete => Assert.Equal(IdOf(AppProfile.PveZone), delete.ProfileId));
    }

    // The applied count is what the sync summary reports to the user, so it has to be the rows
    // that actually reached the store. Reporting the queued count told a player their season had
    // been updated when nothing was written.
    [Fact]
    public async Task A_batch_whose_changes_are_all_already_recorded_reports_nothing_applied()
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PveZone), Quest, QuestStatus.Done);
        var service = ProgressServiceHarness.Create(store, AppProfile.PvpZone, Quest);

        var applied = await service.ApplyQuestChangesBatchAsync(
            new[] { (Quest, QuestStatus.Done) }, AppProfile.PveZone);

        Assert.Equal(0, applied);
        Assert.Empty(store.QuestWrites);
    }

    // ...and a completion that rules out its alternative writes TWO rows, so the count cannot be
    // "one per change requested" either.
    [Fact]
    public async Task A_completion_that_fails_an_alternative_reports_both_rows()
    {
        var chosen = TestTasks.Quest("q-1", "chosen-quest");
        var alternative = TestTasks.Quest("q-2", "ruled-out-quest");
        chosen.AlternativeQuests = new List<string> { alternative.NormalizedName! };
        alternative.AlternativeQuests = new List<string> { chosen.NormalizedName! };

        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PvpZone, chosen, alternative);

        var applied = await service.ApplyQuestChangesBatchAsync(
            new[] { (chosen, QuestStatus.Done) }, AppProfile.PveZone);

        Assert.Equal(2, applied);
        var rows = store.QuestsOf(IdOf(AppProfile.PveZone));
        Assert.Equal(QuestStatus.Done, rows["chosen-quest"]);
        Assert.Equal(QuestStatus.Failed, rows["ruled-out-quest"]);
    }

    // Second sync, same evidence: the stored row comes back keyed by NormalizedName while the
    // plan keys by Id, so an Id-only "already Failed?" check re-wrote the row on every run and
    // counted it as applied every time.
    [Fact]
    public async Task An_already_failed_quest_in_an_off_screen_profile_plans_nothing_on_a_second_sync()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PvpZone, Quest);

        var first = await service.ApplyQuestChangesBatchAsync(
            new[] { (Quest, QuestStatus.Failed) }, AppProfile.PveZone);
        var second = await service.ApplyQuestChangesBatchAsync(
            new[] { (Quest, QuestStatus.Failed) }, AppProfile.PveZone);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(store.QuestWrites);
    }

    // A write for a profile that is not on screen goes only to the database - but the user can
    // switch TO that profile while it is in flight. That reload read the store before these rows
    // landed, so without a re-read the quests look un-completed until something else refreshes.
    [Fact]
    public async Task A_profile_that_becomes_loaded_mid_write_ends_up_showing_the_rows()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, Quest);

        // Hold the off-screen write open across the switch to its profile.
        var releaseWrite = new TaskCompletionSource();
        store.SaveGate = async _ => await releaseWrite.Task;

        var apply = service.ApplyQuestChangesBatchAsync(
            new[] { (Quest, QuestStatus.Done) }, AppProfile.PvpSeason);

        // The user switches to the season; its rows are read while the write is still blocked,
        // so the snapshot that lands is empty.
        await service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 1);
        Assert.Empty(ProgressServiceHarness.LoadedQuestsOf(service));

        releaseWrite.SetResult();
        Assert.Equal(1, await apply);

        Assert.Equal(IdOf(AppProfile.PvpSeason), ProgressServiceHarness.LoadedProfileOf(service));
        Assert.Equal(QuestStatus.Done, ProgressServiceHarness.LoadedQuestsOf(service)[StoredKey]);
    }

    // A profile switch can land between the plan and the swap that publishes it. The rows still
    // belong to a partition whose name is known, so they are re-planned against that profile's
    // stored rows and written there. Dropping them is not recoverable: a later sync only re-reads
    // sessions inside the configured day range.
    //
    // The interleaving cannot be forced from outside (the window is between a volatile read and
    // a compare-exchange), so this runs the write against a profile that is being switched away
    // from, repeatedly. It can only FAIL when a row is genuinely lost, never when the race simply
    // did not occur.
    [Fact]
    public async Task A_write_that_loses_the_swap_is_re_planned_off_screen_rather_than_dropped()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, Quest);
        var pve = IdOf(AppProfile.PveZone);
        var season = IdOf(AppProfile.PvpSeason);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var switcher = Task.Run(() =>
        {
            var revision = 1L;
            while (!stop.IsCancellationRequested)
            {
                service.Snapshot = ProgressSnapshot.Empty(season, revision++);
                service.Snapshot = ProgressSnapshot.Empty(pve, revision++);
            }
        });

        try
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                var quest = TestTasks.Quest($"q-{attempt}", $"quest-{attempt}");
                var applied = await service.ApplyQuestChangesBatchAsync(
                    new[] { (quest, QuestStatus.Done) }, AppProfile.PveZone);

                Assert.Equal(1, applied);
                Assert.True(store.QuestsOf(pve).ContainsKey(quest.NormalizedName!),
                    $"attempt {attempt}: the row never reached the PvE partition");
                Assert.False(store.QuestsOf(season).ContainsKey(quest.NormalizedName!),
                    $"attempt {attempt}: the row was filed under the profile that was switching in");
            }
        }
        finally
        {
            stop.Cancel();
            await switcher;
        }
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
                new Dictionary<string, QuestStatus> { [StoredKey] = QuestStatus.Done },
                new Dictionary<string, bool>()),
            Quest);

        store.LoadGate = _ => throw new InvalidOperationException("database is locked");

        await service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 1);

        Assert.Equal(IdOf(AppProfile.PvpSeason), ProgressServiceHarness.LoadedProfileOf(service));
        Assert.Empty(ProgressServiceHarness.LoadedQuestsOf(service));
    }

    // The two halves are read separately, so one unreadable table must not blank the other. A
    // single try around both threw the quest rows away because the objective read failed, and
    // the user saw every quest un-completed for a reason that had nothing to do with quests.
    [Fact]
    public async Task An_objective_load_failure_leaves_the_quest_rows_loaded()
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PvpSeason), Quest, QuestStatus.Done);
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, Quest);

        store.ObjectiveLoadGate = _ => throw new InvalidOperationException("objective table is locked");

        await service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 1);

        Assert.Equal(IdOf(AppProfile.PvpSeason), ProgressServiceHarness.LoadedProfileOf(service));
        Assert.Equal(QuestStatus.Done, ProgressServiceHarness.LoadedQuestsOf(service)[StoredKey]);
        Assert.Empty(service.Snapshot.Objectives);
    }

    // EFT re-logs the session mode on every profile-screen visit, so a re-confirmation of the
    // profile already loaded keeps arriving on its own. That makes it the one place self-healing
    // can live: after a failed load left the user looking at every quest un-completed, the next
    // re-confirmation must re-read rather than return early because "nothing changed".
    [Fact]
    public async Task A_re_confirmation_after_a_failed_load_reloads_instead_of_returning_early()
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PvpSeason), Quest, QuestStatus.Done);
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, Quest);

        store.LoadGate = _ => throw new InvalidOperationException("database is locked");
        await service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 1);
        Assert.Empty(ProgressServiceHarness.LoadedQuestsOf(service));

        // The store recovers, and the same profile is re-confirmed: same destination, no change.
        store.LoadGate = null;
        RaiseActiveProfileChanged(service, AppProfile.PvpSeason, profileChanged: false, revision: 2);

        await WaitUntil(
            () => ProgressServiceHarness.LoadedQuestsOf(service).ContainsKey(StoredKey),
            "the re-confirmation to re-read the rows the failed load could not");
    }

    // A re-confirmation with nothing wrong must NOT reload: it would re-read identical rows and
    // could republish a view taken before an edit made while the read was in flight.
    [Fact]
    public async Task A_re_confirmation_after_a_healthy_load_does_not_reload()
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PvpSeason), Quest, QuestStatus.Done);
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, Quest);

        await service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 1);
        var loads = 0;
        store.LoadGate = _ =>
        {
            Interlocked.Increment(ref loads);
            return System.Threading.Tasks.Task.CompletedTask;
        };

        RaiseActiveProfileChanged(service, AppProfile.PvpSeason, profileChanged: false, revision: 2);

        // Nothing to wait for, so give a reload that should not happen a chance to happen.
        await System.Threading.Tasks.Task.Delay(100);
        Assert.Equal(0, loads);
    }

    // A quest loaded under its NormalizedName and then written under its Id used to occupy TWO
    // entries at once, and the dual-key recorded-status read then answered from the STALE one:
    // the cascade's "already Done?" gate saw the loaded row and refused to re-complete a quest the
    // user had just failed by hand. The click did nothing, with no dialog and no error.
    [Fact]
    public void Re_completing_a_quest_failed_by_hand_is_not_blocked_by_its_loaded_row()
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PveZone), Quest, QuestStatus.Done);
        var service = ProgressServiceHarness.Create(
            store,
            ProgressSnapshot.From(
                IdOf(AppProfile.PveZone), 0,
                new Dictionary<string, QuestStatus> { [StoredKey] = QuestStatus.Done },
                new Dictionary<string, bool>()),
            Quest);

        service.FailQuest(Quest);
        Assert.Equal(QuestStatus.Failed, service.GetStatus(Quest));

        service.CompleteQuest(Quest, completePrerequisites: false);

        Assert.Equal(QuestStatus.Done, service.GetStatus(Quest));
        // One quest, one entry: the loaded spelling is gone, not merely shadowed.
        Assert.Single(ProgressServiceHarness.LoadedQuestsOf(service));
    }

    // The mirror: a hand completion over a loaded Failed row left "a-quest = Failed" behind, so a
    // later log-detected failure for the same quest planned nothing and was silently lost.
    [Fact]
    public async Task A_log_failure_after_a_hand_completion_is_still_recorded()
    {
        var store = new ProgressStoreFake();
        store.Seed(IdOf(AppProfile.PveZone), Quest, QuestStatus.Failed);
        var service = ProgressServiceHarness.Create(
            store,
            ProgressSnapshot.From(
                IdOf(AppProfile.PveZone), 0,
                new Dictionary<string, QuestStatus> { [StoredKey] = QuestStatus.Failed },
                new Dictionary<string, bool>()),
            Quest);

        service.CompleteQuest(Quest, completePrerequisites: false);
        Assert.Equal(QuestStatus.Done, service.GetStatus(Quest));

        var applied = await service.ApplyQuestChangesBatchAsync(
            new[] { (Quest, QuestStatus.Failed) }, AppProfile.PveZone);

        Assert.Equal(1, applied);
        Assert.Equal(QuestStatus.Failed, service.GetStatus(Quest));
    }

    /// <summary>
    /// Delivers a ProfileService transition to the service's own handler. The harness service is
    /// built without running the constructor, so it is not subscribed to the real singleton, and
    /// a test that went through ProfileService.Instance would share state with every other test.
    /// </summary>
    private static void RaiseActiveProfileChanged(
        QuestProgressService service, AppProfile profile, bool profileChanged, long revision)
    {
        var handler = typeof(QuestProgressService).GetMethod(
            "OnActiveProfileChanged",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.True(handler != null, "QuestProgressService has no OnActiveProfileChanged handler");

        handler!.Invoke(service, new object?[]
        {
            null,
            new ProfileChangedEventArgs(profile, isAuto: true, profileChanged, revision),
        });
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
