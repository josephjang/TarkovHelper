using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The hideout and inventory halves of the transition guard. Two profile switches in quick
/// succession start two reloads, and they can finish in either order; without a revision the
/// slower one publishes last and leaves the EARLIER profile's data on screen under the later
/// profile's name. QuestProgressService's version of this is covered by ProgressSnapshotTests;
/// these two services grew the same guard and need the same proof.
/// <para>
/// Both services are built uninitialized (see <see cref="TestReflection"/>) so no singleton
/// constructor runs and no user_data.db is touched. Their store field is therefore null and the
/// read inside the reload throws, which the services' own catch turns into "publish empty" - the
/// exact outcome a stale load must NOT be allowed to produce. That makes the assertion sharp:
/// data left standing can only mean the revision guard discarded the load.
/// </para>
/// </summary>
public sealed class ProfileReloadRaceTests
{
    private static HideoutProgressService NewHideoutService(long latestRevision, string module, int level)
    {
        var service = TestReflection.Uninitialized<HideoutProgressService>();
        TestReflection.SetPrivateField(service, "_progress", new HideoutProgress
        {
            Modules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [module] = level },
        });
        TestReflection.SetPrivateField(service, "_latestRevision", latestRevision);
        return service;
    }

    private static ItemInventoryService NewInventoryService(long latestRevision, string item, int quantity)
    {
        var service = TestReflection.Uninitialized<ItemInventoryService>();
        TestReflection.SetPrivateField(service, "_lock", new object());
        TestReflection.SetPrivateField(
            service, "_pendingSaves", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var data = new ItemInventoryData();
        data.Items[item] = new ItemInventory { ItemNormalizedName = item, NonFirQuantity = quantity };
        TestReflection.SetPrivateField(service, "_inventoryData", data);
        TestReflection.SetPrivateField(service, "_latestRevision", latestRevision);
        return service;
    }

    [Fact]
    public async Task A_hideout_reload_that_lost_the_race_does_not_publish_over_the_newer_one()
    {
        // Revision 2 has already been requested: this load serves the transition before it.
        var service = NewHideoutService(latestRevision: 2, "stash", level: 3);
        var notified = 0;
        service.ProgressChanged += (_, _) => notified++;

        await service.ReloadForProfileAsync(AppProfile.PveZone, revision: 1);

        Assert.Equal(3, service.GetCurrentLevel("stash"));
        Assert.Equal(0, notified);
    }

    // The contrast that keeps the test above honest: the same call for the CURRENT transition
    // does publish, so "the level survived" cannot be explained by the reload doing nothing.
    [Fact]
    public async Task A_hideout_reload_for_the_current_transition_publishes()
    {
        var service = NewHideoutService(latestRevision: 2, "stash", level: 3);
        var notified = 0;
        service.ProgressChanged += (_, _) => notified++;

        await service.ReloadForProfileAsync(AppProfile.PveZone, revision: 2);

        Assert.Equal(0, service.GetCurrentLevel("stash"));
        Assert.Equal(1, notified);
    }

    [Fact]
    public async Task An_inventory_reload_that_lost_the_race_does_not_publish_over_the_newer_one()
    {
        var service = NewInventoryService(latestRevision: 2, "bolts", quantity: 7);
        var notified = 0;
        service.InventoryChanged += (_, _) => notified++;

        await service.ReloadForProfileAsync(AppProfile.PveZone, revision: 1);

        Assert.Equal(7, service.GetTotalQuantity("bolts"));
        Assert.Equal(0, notified);
    }

    [Fact]
    public async Task An_inventory_reload_for_the_current_transition_publishes()
    {
        var service = NewInventoryService(latestRevision: 2, "bolts", quantity: 7);
        var notified = 0;
        service.InventoryChanged += (_, _) => notified++;

        await service.ReloadForProfileAsync(AppProfile.PveZone, revision: 2);

        Assert.Equal(0, service.GetTotalQuantity("bolts"));
        Assert.Equal(1, notified);
    }

    // Two transitions announced in quick succession: whichever load finishes last, the data left
    // loaded belongs to the LATER one. Run through the public entry point in the order that used
    // to lose - the older transition finishing after the newer.
    [Fact]
    public async Task The_later_of_two_hideout_transitions_wins_whatever_order_they_finish_in()
    {
        var service = NewHideoutService(latestRevision: 0, "stash", level: 3);

        // The newer transition completes first and publishes (empty, since the store is
        // unreachable here); the older one then finishes and must not restore anything.
        await service.ReloadForProfileAsync(AppProfile.PvpSeason, revision: 2);
        TestReflection.SetPrivateField(service, "_progress", new HideoutProgress
        {
            Modules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["stash"] = 9 },
        });

        await service.ReloadForProfileAsync(AppProfile.PveZone, revision: 1);

        // The season's published state (level 9 standing in for "what the newer load produced")
        // is intact: the older transition's load discarded itself.
        Assert.Equal(9, service.GetCurrentLevel("stash"));
    }
}
