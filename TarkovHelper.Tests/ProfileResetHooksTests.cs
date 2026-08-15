using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards for the per-service reset hooks and the log-event fence
/// (feature-complete-profile-reset.spec.md): each cache clears only when it holds the reset
/// profile's data, the settings cache reloads only when the reset target is the profile its
/// snapshot holds, the fence drops log events that are not after the watermark, hand entry is
/// never fenced, pending debounced saves are discarded per profile, and the survivor list stays
/// a subset of the profile-scoped keys.
/// </summary>
public sealed class ProfileResetHooksTests : IDisposable
{
    private static readonly TarkovTask Quest = TestTasks.Quest("q-1", "a-quest");

    private static string IdOf(AppProfile profile) => ProfileService.GetProfileId(profile);

    /// <summary>
    /// Temp home for the real-SQLite stores the settings hook needs (it reloads through the
    /// store, so a fake would prove nothing about the reload). The store creates the directory
    /// on its first write, so a test that never builds one leaves nothing behind.
    /// </summary>
    private readonly string _storeRoot = Path.Combine(
        Path.GetTempPath(), "tarkovhelper-hooks-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_storeRoot)) return;

        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_storeRoot, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private UserDataDbService NewStore()
        => new(Path.Combine(_storeRoot, Guid.NewGuid().ToString("N") + ".db"));

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(20);
        }
    }

    #region Quest snapshot hook

    [Fact]
    public void The_quest_hook_clears_the_snapshot_only_when_it_holds_the_reset_profile()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(
            store,
            ProgressSnapshot.From(
                IdOf(AppProfile.PvpSeason), 0,
                new Dictionary<string, QuestStatus> { ["a-quest"] = QuestStatus.Done },
                new Dictionary<string, bool> { ["a-quest:0"] = true }),
            Quest);
        var progressChanged = 0;
        service.ProgressChanged += (_, _) => progressChanged++;

        // Another profile's reset leaves the loaded snapshot alone and raises nothing.
        service.HandleProfileReset(IdOf(AppProfile.PveZone));
        Assert.Equal(QuestStatus.Done, ProgressServiceHarness.LoadedQuestsOf(service)["a-quest"]);
        Assert.Equal(0, progressChanged);

        // The loaded profile's reset publishes empty rows and notifies once.
        service.HandleProfileReset(IdOf(AppProfile.PvpSeason));
        Assert.Empty(ProgressServiceHarness.LoadedQuestsOf(service));
        Assert.Empty(service.Snapshot.Objectives);
        Assert.Equal(1, progressChanged);

        // The hook is the in-memory half only: deleting rows is the reset transaction's job,
        // so no store write may originate here.
        Assert.Empty(store.QuestWrites);
        Assert.Empty(store.QuestDeletes);
    }

    #endregion

    #region The log-event fence

    [Fact]
    public async Task A_log_event_not_after_the_watermark_writes_nothing()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PvpSeason, Quest);
        var resetAt = new DateTime(2026, 8, 13, 12, 0, 0);
        store.ResetWatermarks[IdOf(AppProfile.PvpSeason)] = resetAt;

        // Strictly before the reset: the resurrection this fence exists to stop.
        await service.ApplyLogEventAsync(
            Quest, QuestEventType.Completed, AppProfile.PvpSeason, resetAt.AddMinutes(-5));
        // The boundary is "not after": an event stamped exactly at the reset moment drops too.
        await service.ApplyLogEventAsync(
            Quest, QuestEventType.Completed, AppProfile.PvpSeason, resetAt);

        Assert.Empty(store.QuestWrites);
        Assert.Empty(ProgressServiceHarness.LoadedQuestsOf(service));
    }

    [Fact]
    public async Task A_log_event_after_the_watermark_applies_normally()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PvpSeason, Quest);
        var resetAt = new DateTime(2026, 8, 13, 12, 0, 0);
        store.ResetWatermarks[IdOf(AppProfile.PvpSeason)] = resetAt;

        // Progress created after the reset behaves normally (PRD R7).
        await service.ApplyLogEventAsync(
            Quest, QuestEventType.Completed, AppProfile.PvpSeason, resetAt.AddSeconds(1));

        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PvpSeason))["a-quest"]);
        Assert.Equal(QuestStatus.Done, ProgressServiceHarness.LoadedQuestsOf(service)["q-1"]);
    }

    [Fact]
    public async Task The_fence_is_per_profile_not_global()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PveZone, Quest);
        store.ResetWatermarks[IdOf(AppProfile.PvpSeason)] = new DateTime(2026, 8, 13, 12, 0, 0);

        // Another profile's reset must not fence this profile's events.
        await service.ApplyLogEventAsync(
            Quest, QuestEventType.Completed, AppProfile.PveZone, new DateTime(2026, 8, 13, 11, 0, 0));

        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PveZone))["a-quest"]);
    }

    [Fact]
    public async Task Hand_entry_is_never_fenced()
    {
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(store, AppProfile.PvpSeason, Quest);
        // A watermark newer than "now": every log event would be fenced, hand entry never is.
        store.ResetWatermarks[IdOf(AppProfile.PvpSeason)] = DateTime.Now.AddDays(1);

        service.CompleteQuest(Quest, completePrerequisites: false);

        await WaitUntil(() => store.QuestWrites.Count == 1, "the hand completion to persist");
        Assert.Equal(QuestStatus.Done, store.QuestsOf(IdOf(AppProfile.PvpSeason))["a-quest"]);
    }

    #endregion

    #region The barrier through the real persistence path

    // The resurrection SPA-3 describes: a batch save scheduled before a reset landing after its
    // deletes. The tracked write registers before it blocks in the store, so the reset's drain
    // must wait it out; when the drain returns, the write has landed and the deletes that
    // follow it sweep the rows for good.
    [Fact]
    public async Task A_pending_batch_save_is_drained_before_a_reset_proceeds()
    {
        var profileId = "drain-" + Guid.NewGuid().ToString("N");
        var store = new ProgressStoreFake();
        var service = ProgressServiceHarness.Create(
            store, ProgressSnapshot.Empty(profileId, 0), Quest);

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        store.SaveGate = _ =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        service.CompleteQuest(Quest, completePrerequisites: false);
        await entered.Task;

        var drain = TrackedUserDataWrites.BeginResetAsync(profileId);
        await Task.Delay(100);
        Assert.False(drain.IsCompleted, "the reset proceeded while a batch save was in flight");
        Assert.Empty(store.QuestWrites);

        release.SetResult();
        var handle = await drain;
        // Landed before the reset's deletes would run: nothing can arrive after them.
        Assert.Single(store.QuestWrites);
        await handle.DisposeAsync();
    }

    #endregion

    #region Inventory: pending saves and the loaded-profile guard

    private static ItemInventoryService NewInventoryService(
        string loadedProfileId,
        Dictionary<string, string> pendingSaves,
        ItemInventoryData inventory,
        UserDataDbService? store = null)
    {
        var service = TestReflection.Uninitialized<ItemInventoryService>();
        TestReflection.SetPrivateField(service, "_lock", new object());
        TestReflection.SetPrivateField(service, "_pendingSaves", pendingSaves);
        TestReflection.SetPrivateField(service, "_inventoryData", inventory);
        TestReflection.SetPrivateField(service, "_loadedProfileId", loadedProfileId);
        if (store != null) TestReflection.SetPrivateField(service, "_userDataDb", store);
        return service;
    }

    /// <summary>
    /// Runs the debounce flush the way the timer does. Reached by reflection because driving it
    /// through the real 500ms timer would make this a timing exercise, and the flush's ordering
    /// against a reset is exactly what is under test.
    /// </summary>
    private static Task FlushPendingSaves(ItemInventoryService service)
    {
        var method = typeof(ItemInventoryService).GetMethod(
            "SavePendingItemsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.True(method != null, "ItemInventoryService has no SavePendingItemsAsync");
        return (Task)method!.Invoke(service, Array.Empty<object>())!;
    }

    // A debounced save staged before a reset must not survive it. The flush used to empty
    // _pendingSaves up front and only then walk the entries one round-trip at a time, so an
    // entry could sit in a local list - invisible to the barrier's drain AND to
    // DiscardPendingSaves - until after the reset committed, and then write its row back.
    // Claiming each entry from inside its own tracked write closes that gap.
    [Fact]
    public async Task A_pending_save_staged_before_a_reset_cannot_land_after_it()
    {
        var store = NewStore();
        var target = "reset-" + Guid.NewGuid().ToString("N");
        var bystander = "keep-" + Guid.NewGuid().ToString("N");

        // Three entries, so the target's second one is the entry the old flush stranded behind
        // the first one's round-trip.
        var inventory = new ItemInventoryData();
        inventory.Items["salewa"] = new ItemInventory { ItemNormalizedName = "salewa", FirQuantity = 3 };
        inventory.Items["bandage"] = new ItemInventory { ItemNormalizedName = "bandage", NonFirQuantity = 5 };
        inventory.Items["car-battery"] = new ItemInventory { ItemNormalizedName = "car-battery", NonFirQuantity = 1 };
        var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["salewa"] = target,
            ["bandage"] = target,
            ["car-battery"] = bystander,
        };

        var service = NewInventoryService(target, pending, inventory, store);

        // The reset raises its barrier first, exactly as ProfileResetService does.
        var guard = await TrackedUserDataWrites.BeginResetAsync(target);

        // The debounce timer fires now, mid-reset.
        var flush = FlushPendingSaves(service);
        // ...and the reset drops the target's pending entries, still under the barrier.
        service.DiscardPendingSaves(target);

        await guard.DisposeAsync();
        await flush;

        // Nothing the reset was about to delete was written back.
        Assert.Empty(await store.LoadItemInventoryAsync(target));
        // The control that keeps the assertion above honest: the same flush did persist the
        // entry captured for an unrelated profile, so "no rows" cannot mean "the flush did
        // nothing".
        Assert.Equal(1, (await store.LoadItemInventoryAsync(bystander))["car-battery"].NonFirQuantity);
        // Every entry is accounted for: claimed and written, or discarded by the reset.
        Assert.Empty(pending);
    }

    // The other half of the claim rule: with no reset in the way, every pending entry is
    // written under its own captured profile and leaves the pending map clean.
    [Fact]
    public async Task A_flush_persists_every_pending_entry_under_its_captured_profile()
    {
        var store = NewStore();
        var first = "one-" + Guid.NewGuid().ToString("N");
        var second = "two-" + Guid.NewGuid().ToString("N");

        var inventory = new ItemInventoryData();
        inventory.Items["salewa"] = new ItemInventory { ItemNormalizedName = "salewa", FirQuantity = 3 };
        inventory.Items["bandage"] = new ItemInventory { ItemNormalizedName = "bandage", NonFirQuantity = 5 };
        var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["salewa"] = first,
            ["bandage"] = second,
        };

        var service = NewInventoryService(first, pending, inventory, store);

        await FlushPendingSaves(service);

        Assert.Equal(3, (await store.LoadItemInventoryAsync(first))["salewa"].FirQuantity);
        Assert.Equal(5, (await store.LoadItemInventoryAsync(second))["bandage"].NonFirQuantity);
        Assert.Empty(pending);
    }

    // ProfileService raises ActiveProfileChanged synchronously, so whatever this handler does
    // before its first suspension runs on the raising thread and holds up every subscriber
    // behind it. Flushing with a blocking wait parked that thread for the whole of a reset it
    // had a pending entry for, and deadlocked outright when the raising thread was the
    // dispatcher the reset needs to finish on.
    [Fact]
    public async Task A_profile_switch_does_not_block_its_caller_while_a_reset_holds_a_pending_save()
    {
        var store = NewStore();
        var target = "held-" + Guid.NewGuid().ToString("N");

        var inventory = new ItemInventoryData();
        inventory.Items["salewa"] = new ItemInventory { ItemNormalizedName = "salewa", FirQuantity = 3 };
        var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["salewa"] = target,
        };
        var service = NewInventoryService(target, pending, inventory, store);

        var guard = await TrackedUserDataWrites.BeginResetAsync(target);
        var release = Task.Run(async () =>
        {
            await Task.Delay(500);
            await guard.DisposeAsync();
        });

        var elapsed = Stopwatch.StartNew();
        var reload = service.ReloadForProfileAsync(AppProfile.PveZone, revision: 1);
        elapsed.Stop();

        // The handler handed control back at its first suspension, long before the reset
        // released; a blocking flush would only have returned after the release.
        Assert.True(
            elapsed.ElapsedMilliseconds < 250,
            $"the profile switch blocked its caller for {elapsed.ElapsedMilliseconds}ms");
        Assert.False(reload.IsCompleted, "the flush slipped past a raised reset barrier");

        await release;
        await reload;

        // ...and it still finished the work: flush first, then the new profile's load.
        Assert.Equal(3, (await store.LoadItemInventoryAsync(target))["salewa"].FirQuantity);
        Assert.Empty(pending);
    }

    [Fact]
    public void Discarding_pending_saves_removes_only_the_target_profiles_entries()
    {
        var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["salewa"] = IdOf(AppProfile.PvpSeason),
            ["bandage"] = IdOf(AppProfile.PvpSeason),
            ["car-battery"] = IdOf(AppProfile.PveZone),
        };
        var service = NewInventoryService(
            IdOf(AppProfile.PvpSeason), pending, new ItemInventoryData());

        service.DiscardPendingSaves(IdOf(AppProfile.PvpSeason));

        // The target's dirty quantities describe rows the reset deletes; the other profile's
        // entry survives to flush normally afterwards.
        var survivor = Assert.Single(pending);
        Assert.Equal("car-battery", survivor.Key);
        Assert.Equal(IdOf(AppProfile.PveZone), survivor.Value);
    }

    [Fact]
    public void The_inventory_hook_clears_only_the_loaded_profile()
    {
        var inventory = new ItemInventoryData();
        inventory.Items["salewa"] = new ItemInventory { ItemNormalizedName = "salewa", FirQuantity = 3 };
        var service = NewInventoryService(
            IdOf(AppProfile.PvpSeason), new Dictionary<string, string>(), inventory);
        var changed = 0;
        service.InventoryChanged += (_, _) => changed++;

        service.HandleProfileReset(IdOf(AppProfile.PveZone));
        Assert.Equal(3, service.GetFirQuantity("salewa"));
        Assert.Equal(0, changed);

        service.HandleProfileReset(IdOf(AppProfile.PvpSeason));
        Assert.Equal(0, service.GetFirQuantity("salewa"));
        Assert.Equal(1, changed);
    }

    #endregion

    #region Hideout: the loaded-profile guard

    private static HideoutProgressService NewHideoutService(string loadedProfileId, string module, int level)
    {
        var service = TestReflection.Uninitialized<HideoutProgressService>();
        var progress = new HideoutProgress();
        progress.Modules[module] = level;
        TestReflection.SetPrivateField(service, "_progress", progress);
        TestReflection.SetPrivateField(service, "_loadedProfileId", loadedProfileId);
        return service;
    }

    /// <summary>The profile id the hideout cache currently claims to hold.</summary>
    private static string? LoadedHideoutProfileId(HideoutProgressService service)
    {
        var field = typeof(HideoutProgressService).GetField(
            "_loadedProfileId",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.True(field != null, "HideoutProgressService has no private field '_loadedProfileId'");
        return (string?)field!.GetValue(service);
    }

    /// <summary>
    /// The gate the hideout cache publishes and checks under. Held directly by the two tests
    /// below: it is the only way to suspend a publish or a reset hook exactly where the
    /// unguarded version used to be interruptible.
    /// </summary>
    private static object HideoutStateGate()
    {
        var field = typeof(HideoutProgressService).GetField(
            "_stateGate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.True(field != null, "HideoutProgressService has no private static field '_stateGate'");
        var gate = field!.GetValue(null);
        Assert.NotNull(gate);
        return gate!;
    }

    [Fact]
    public void The_hideout_hook_clears_only_the_loaded_profile()
    {
        var service = NewHideoutService(IdOf(AppProfile.PvpSeason), "workbench", 2);
        var changed = 0;
        service.ProgressChanged += (_, _) => changed++;

        service.HandleProfileReset(IdOf(AppProfile.PveZone));
        Assert.Equal(2, service.GetCurrentLevel("workbench"));
        Assert.Equal(0, changed);

        service.HandleProfileReset(IdOf(AppProfile.PvpSeason));
        Assert.Equal(0, service.GetCurrentLevel("workbench"));
        Assert.Equal(1, changed);
    }

    // ProfileService raises ActiveProfileChanged from a pool thread, so a load can publish while
    // a reset hook is deciding on the dispatcher. The load used to publish its rows and its
    // profile id as two bare assignments: a reset of the OUTGOING profile landing between them
    // passed its guard and emptied the profile that had just finished loading, leaving the
    // hideout page at level 0 for rows that are still in the database. These two tests pin the
    // halves of the fix - the publish and the hook share one critical section, so neither can
    // observe the other mid-step.
    [Fact]
    public async Task A_hideout_load_publishes_its_rows_and_its_profile_id_in_one_gated_step()
    {
        var service = NewHideoutService(IdOf(AppProfile.PvpSeason), "workbench", 2);
        var gate = HideoutStateGate();

        Task reload;
        Monitor.Enter(gate);
        try
        {
            // This instance has no store, so the read throws and the service's own catch takes
            // the load straight to the publish. Off the test thread, because the gate is
            // reentrant and would not stop a load running on this one.
            reload = Task.Run(() => service.ReloadForProfileAsync(AppProfile.PveZone, revision: 1));
            Thread.Sleep(200);

            Assert.False(reload.IsCompleted, "the hideout load published outside the state gate");
            // Read from the gate-holding thread, so this sees the state as an interrupted reset
            // hook would: neither half of the pair has moved.
            Assert.Equal(2, service.GetCurrentLevel("workbench"));
            Assert.Equal(IdOf(AppProfile.PvpSeason), LoadedHideoutProfileId(service));
        }
        finally
        {
            Monitor.Exit(gate);
        }

        await reload;

        // Both halves moved together.
        Assert.Equal(0, service.GetCurrentLevel("workbench"));
        Assert.Equal(IdOf(AppProfile.PveZone), LoadedHideoutProfileId(service));
    }

    [Fact]
    public async Task The_hideout_reset_hook_decides_under_the_state_gate()
    {
        var service = NewHideoutService(IdOf(AppProfile.PvpSeason), "workbench", 2);
        var gate = HideoutStateGate();

        Task reset;
        Monitor.Enter(gate);
        try
        {
            reset = Task.Run(() => service.HandleProfileReset(IdOf(AppProfile.PvpSeason)));
            Thread.Sleep(200);

            // An unguarded hook would have read the profile id and wiped the rows by now, which
            // is precisely what it must not be able to do while a load holds the gate.
            Assert.False(reset.IsCompleted, "the hideout reset hook decided outside the state gate");
            Assert.Equal(2, service.GetCurrentLevel("workbench"));
        }
        finally
        {
            Monitor.Exit(gate);
        }

        await reset;
        Assert.Equal(0, service.GetCurrentLevel("workbench"));
    }

    #endregion

    #region Settings: the loaded-profile hook

    /// <summary>
    /// The profile the seeded snapshot holds. A synthetic id, so it can never coincide with the
    /// ambient selection: the hook compares the reset target against the SNAPSHOT's profile id
    /// now, and an id the selection cannot equal is what stops these tests from passing on the
    /// old selection-based comparison.
    /// </summary>
    private readonly string _loadedProfileId = "loaded-" + Guid.NewGuid().ToString("N");

    /// <summary>Any profile the seeded snapshot does not hold.</summary>
    private readonly string _otherProfileId = "other-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// A SettingsService with no constructor run: the real one loads every setting and
    /// subscribes to ProfileService. Only the store, the "already loaded" flag and the snapshot
    /// are seeded, so the property getters answer from the cache this test controls rather than
    /// re-entering LoadSettings and its migrations.
    /// </summary>
    private SettingsService NewSettingsService(UserDataDbService store)
    {
        var service = TestReflection.Uninitialized<SettingsService>();
        TestReflection.SetPrivateField(service, "_userDataDb", store);
        TestReflection.SetPrivateField(service, "_settingsLoaded", true);
        TestReflection.SetPrivateField(service, "_profileSettings", StaleSnapshot(_loadedProfileId));
        return service;
    }

    /// <summary>
    /// The values a completed reset has just made stale, as one snapshot for
    /// <paramref name="profileId"/>. Revision 0 matches the untouched <c>_latestRevision</c> of
    /// an uninitialized service, which is the state a reset hook publishes against.
    /// </summary>
    private static ProfileSettingsSnapshot StaleSnapshot(string profileId) => new(
        profileId,
        Revision: 0,
        PlayerLevel: 42,
        ScavRep: 5.5,
        ShowLevelLockedQuests: false,
        DspDecodeCount: 3,
        PlayerFaction: "bear",
        HasEodEdition: true,
        HasUnheardEdition: true,
        PrestigeLevel: 4);

    /// <summary>Records every profile-scoped changed event in the order it is raised.</summary>
    private static List<(string Name, object? Value)> RecordSettingEvents(SettingsService service)
    {
        var events = new List<(string Name, object? Value)>();
        service.PlayerLevelChanged += (_, v) => events.Add(("PlayerLevel", v));
        service.ScavRepChanged += (_, v) => events.Add(("ScavRep", v));
        service.DspDecodeCountChanged += (_, v) => events.Add(("DspDecodeCount", v));
        service.PlayerFactionChanged += (_, v) => events.Add(("PlayerFaction", v));
        service.HasEodEditionChanged += (_, v) => events.Add(("HasEodEdition", v));
        service.HasUnheardEditionChanged += (_, v) => events.Add(("HasUnheardEdition", v));
        service.PrestigeLevelChanged += (_, v) => events.Add(("PrestigeLevel", v));
        return events;
    }

    [Fact]
    public async Task The_settings_hook_reloads_the_cache_when_the_reset_target_is_the_loaded_profile()
    {
        var store = NewStore();
        // What the reset transaction leaves behind for the target: every profile row deleted
        // except the editions, which survive by design (ProfileKeysSurvivingReset).
        await store.SetProfileSettingAsync(_loadedProfileId, "app.hasEodEdition", "True");

        var service = NewSettingsService(store);
        var events = RecordSettingEvents(service);

        service.HandleProfileReset(_loadedProfileId);

        // The cache now answers from the post-reset rows: the deleted keys fall back to their
        // defaults, and the surviving edition row is read back as it stands.
        Assert.Equal(SettingsService.DefaultPlayerLevel, service.PlayerLevel);
        Assert.Equal(SettingsService.DefaultScavRep, service.ScavRep);
        Assert.Equal(SettingsService.DefaultDspDecodeCount, service.DspDecodeCount);
        Assert.Null(service.PlayerFaction);
        Assert.Equal(SettingsService.DefaultPrestigeLevel, service.PrestigeLevel);
        Assert.True(service.HasEodEdition);
        Assert.False(service.HasUnheardEdition);
        // The eighth value rides along with the seven that have events (it has none).
        Assert.True(service.ShowLevelLockedQuests);

        // The reload republished under the same profile, so a later reset of it still lands.
        Assert.Equal(_loadedProfileId, service.ProfileSettings.ProfileId);

        // Every profile-scoped changed event is re-raised once, carrying the reloaded value:
        // the UI redraws from these, exactly as it does on a profile switch.
        Assert.Equal(new (string, object?)[]
        {
            ("PlayerLevel", SettingsService.DefaultPlayerLevel),
            ("ScavRep", SettingsService.DefaultScavRep),
            ("DspDecodeCount", SettingsService.DefaultDspDecodeCount),
            ("PlayerFaction", null),
            ("HasEodEdition", true),
            ("HasUnheardEdition", false),
            ("PrestigeLevel", SettingsService.DefaultPrestigeLevel),
        }, events);
    }

    [Fact]
    public async Task The_settings_hook_ignores_a_reset_of_a_profile_the_cache_does_not_hold()
    {
        var store = NewStore();
        // A row for the LOADED profile that differs from the seeded snapshot, so any reload
        // would be visible in the assertions. Another profile's reset must not trigger one:
        // this cache holds the loaded profile's values and none of them went stale.
        await store.SetProfileSettingAsync(_loadedProfileId, "app.playerLevel", "7");

        var service = NewSettingsService(store);
        var events = RecordSettingEvents(service);

        service.HandleProfileReset(_otherProfileId);

        Assert.Equal(42, service.PlayerLevel);
        Assert.Equal("bear", service.PlayerFaction);
        Assert.True(service.HasUnheardEdition);
        Assert.Empty(events);
    }

    #endregion

    #region Survivor classification

    // Deletion is the default: a future profile key must be wiped unless someone deliberately
    // adds it to the survivor list, and the survivor list can only name keys that are actually
    // profile-scoped (a global key on it would be dead weight that reads as protection).
    [Fact]
    public void The_surviving_keys_are_a_strict_subset_of_the_profile_specific_keys()
    {
        Assert.All(SettingsService.ProfileKeysSurvivingReset, key =>
            Assert.Contains(key, SettingsService.ProfileSpecificKeys));
        Assert.True(
            SettingsService.ProfileKeysSurvivingReset.Length < SettingsService.ProfileSpecificKeys.Length,
            "every profile key survives the reset; the wipe would remove nothing");
        Assert.Equal(
            new[] { "app.hasEodEdition", "app.hasUnheardEdition" },
            SettingsService.ProfileKeysSurvivingReset);
    }

    #endregion

    #region Raid ownership capture

    // The raid row's owner comes from the session evidence current at raid creation, mapped
    // through the same pure profile maps everything else uses. Unknown stays null: guessing an
    // owner is what the PRD rejected, and null is the value a reset never deletes.
    [Theory]
    [InlineData(SessionProfileHint.PvpZone, "pvp")]
    [InlineData(SessionProfileHint.PveZone, "pve")]
    [InlineData(SessionProfileHint.PvpSeason, "season")]
    [InlineData(SessionProfileHint.Unknown, null)]
    public void A_session_hint_resolves_to_its_storage_profile_or_null(
        SessionProfileHint hint, string? expected)
    {
        Assert.Equal(expected, EftRaidEventService.AppProfileIdOf(hint));
    }

    private const string RaidCreateLine =
        "2026-08-13 21:05:00.000|1.1.0|Info|application|TRACE-NetworkGameCreate profileStatus: " +
        "'Profileid: 6812e6d33c20fd23a17cd044, Status: Busy, RaidMode: Online, Ip: 192.168.0.1, " +
        "Port: 17000, Location: bigmap, Sid: sid-1, GameMode: deathmatch, shortId: AAAA11'";

    /// <summary>
    /// Drives the service's own line parser, the way the tail reader does. Reached by
    /// reflection because a real FileSystemWatcher would make this a timing exercise (the
    /// same trade LogSyncAttributionTests makes for ProcessLatestLogEvents).
    /// </summary>
    private static void Parse(EftRaidEventService service, string line)
    {
        var method = typeof(EftRaidEventService).GetMethod(
            "ParseApplicationLogLine",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.True(method != null, "EftRaidEventService has no ParseApplicationLogLine");
        method!.Invoke(service, new object[] { line });
    }

    // The owner is decided by the session that produced the raid, at the moment the raid
    // object first exists; the ambient selection is never consulted, so nothing that happens
    // between creation and the deferred save can re-own the row.
    [Fact]
    public void A_raid_created_under_a_season_session_is_owned_by_the_season()
    {
        var service = TestReflection.Uninitialized<EftRaidEventService>();

        Parse(service, "2026-08-13 21:00:00.000 | Session mode: PvpSeason");
        Parse(service, RaidCreateLine);

        Assert.NotNull(service.CurrentRaid);
        Assert.Equal("season", service.CurrentRaid!.AppProfileId);
    }

    // No session evidence, no owner: the row stays null and is preserved by every reset.
    [Fact]
    public void A_raid_with_no_session_evidence_stays_unowned()
    {
        var service = TestReflection.Uninitialized<EftRaidEventService>();

        Parse(service, RaidCreateLine);

        Assert.NotNull(service.CurrentRaid);
        Assert.Null(service.CurrentRaid!.AppProfileId);
    }

    #endregion
}
