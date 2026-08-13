using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards for the per-service reset hooks and the log-event fence
/// (feature-complete-profile-reset.spec.md): each cache clears only when it holds the reset
/// profile's data, the fence drops log events that are not after the watermark, hand entry is
/// never fenced, pending debounced saves are discarded per profile, and the survivor list stays
/// a subset of the profile-scoped keys.
/// </summary>
public sealed class ProfileResetHooksTests
{
    private static readonly TarkovTask Quest = TestTasks.Quest("q-1", "a-quest");

    private static string IdOf(AppProfile profile) => ProfileService.GetProfileId(profile);

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
        ItemInventoryData inventory)
    {
        var service = TestReflection.Uninitialized<ItemInventoryService>();
        TestReflection.SetPrivateField(service, "_lock", new object());
        TestReflection.SetPrivateField(service, "_pendingSaves", pendingSaves);
        TestReflection.SetPrivateField(service, "_inventoryData", inventory);
        TestReflection.SetPrivateField(service, "_loadedProfileId", loadedProfileId);
        return service;
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

    [Fact]
    public void The_hideout_hook_clears_only_the_loaded_profile()
    {
        var service = TestReflection.Uninitialized<HideoutProgressService>();
        var progress = new HideoutProgress();
        progress.Modules["workbench"] = 2;
        TestReflection.SetPrivateField(service, "_progress", progress);
        TestReflection.SetPrivateField(service, "_loadedProfileId", IdOf(AppProfile.PvpSeason));
        var changed = 0;
        service.ProgressChanged += (_, _) => changed++;

        service.HandleProfileReset(IdOf(AppProfile.PveZone));
        Assert.Equal(2, service.GetCurrentLevel("workbench"));
        Assert.Equal(0, changed);

        service.HandleProfileReset(IdOf(AppProfile.PvpSeason));
        Assert.Equal(0, service.GetCurrentLevel("workbench"));
        Assert.Equal(1, changed);
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
