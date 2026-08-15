using TarkovHelper.Services;
using TarkovHelper.Services.Settings;
using static TarkovHelper.Tests.SettingsServiceTestSupport;

namespace TarkovHelper.Tests;

/// <summary>
/// What one profile-scoped settings edit reports back, and what the property setters do with it.
/// <para>
/// The three outcomes used to be one bool, so "the snapshot already held this value" and "a
/// profile switch landed between the derivation and the publish, and the value the player typed
/// will never appear on the screen they are looking at" were the same answer. They are told apart
/// here through <see cref="SettingsService.EditPublishOutcome"/>, which is also what lets the
/// second case be logged rather than silently swallowed at all eight setters.
/// </para>
/// <para>
/// The outcome cases drive the private <c>UpdateProfileSetting</c> by reflection, because that is
/// where the answer is produced and the eight setters only consume it. They move the live snapshot
/// from INSIDE the derivation delegate, which is the window the outcome is about and needs no
/// second thread. The one case that asserts on a changed EVENT goes through the real property
/// setter instead - asserting "no event" on a path that cannot raise one would prove nothing.
/// </para>
/// </summary>
public sealed class SettingsPublishOutcomeTests : IDisposable
{
    /// <summary>Temp home for the real-SQLite stores the write assertions need.</summary>
    private readonly TempStoreRoot _stores = new("settings-outcome");

    public void Dispose() => _stores.Dispose();

    private UserDataDbService NewStore() => _stores.NewStore();

    /// <summary>The setter's own "level changed to 51" derivation, so the retry below behaves like the real one.</summary>
    private static ProfileSettingsSnapshot? ToLevel51(ProfileSettingsSnapshot s)
        => s.PlayerLevel == 51 ? null : s with { PlayerLevel = 51 };

    [Fact]
    public async Task An_edit_that_reaches_the_snapshot_reports_Applied()
    {
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        var service = NewService(Seeded(onScreen), store: store);

        var outcome = UpdateProfileSetting(service, ToLevel51, "app.playerLevel", "51");

        Assert.Equal(SettingsService.EditPublishOutcome.Applied, outcome);
        Assert.Equal(51, service.PlayerLevel);
        Assert.Equal("51", await store.GetProfileSettingAsync(onScreen, "app.playerLevel"));
    }

    // The outcome the eight setters branch on, from the other side: Applied is the only one that
    // announces, and it announces exactly once.
    [Fact]
    public void An_applied_edit_announces_itself_once()
    {
        var store = NewStore();
        var service = NewService(Seeded(NewProfileId("onscreen")), store: store);
        var events = RecordEventNames(service);

        service.PlayerLevel = 51;

        Assert.Equal(new[] { "PlayerLevel" }, events);
    }

    [Fact]
    public async Task Re_setting_the_value_the_snapshot_already_holds_reports_Unchanged()
    {
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        var service = NewService(Seeded(onScreen), store: store);
        var before = service.ProfileSettings;

        // 42 is the seeded level, so the setter's derivation returns null and neither half of the
        // edit runs.
        var outcome = UpdateProfileSetting(
            service, s => s.PlayerLevel == 42 ? null : s with { PlayerLevel = 42 },
            "app.playerLevel", "42");

        Assert.Equal(SettingsService.EditPublishOutcome.Unchanged, outcome);
        Assert.Same(before, service.ProfileSettings);
        // Nothing was written either: an unchanged set is not a reason to touch the store.
        Assert.Null(await store.GetProfileSettingAsync(onScreen, "app.playerLevel"));
    }

    // The other Unchanged site, and the one a bool could not tell from the first: the edit DID
    // change the snapshot it was derived from, but by the time it reached the gate another
    // publisher had already landed the same value for the same profile.
    [Fact]
    public void An_edit_another_publisher_already_landed_reports_Unchanged()
    {
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        var service = NewService(Seeded(onScreen), store: store);
        var winner = Seeded(onScreen) with { PlayerLevel = 51 };

        var moved = false;
        var outcome = UpdateProfileSetting(service, s =>
        {
            // Once, on the first derivation: the competing publish lands between the derivation
            // and the gate. The retry inside the gate sees the winner and has nothing to add.
            if (!moved)
            {
                moved = true;
                TestReflection.SetPrivateField(service, "_profileSettings", winner);
            }
            return ToLevel51(s);
        }, "app.playerLevel", "51");

        Assert.Equal(SettingsService.EditPublishOutcome.Unchanged, outcome);
        Assert.Same(winner, service.ProfileSettings);
    }

    [Fact]
    public async Task An_edit_overtaken_by_a_profile_switch_reports_Superseded()
    {
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        var next = NewProfileId("next");
        var service = NewService(Seeded(onScreen), store: store);
        var arrived = Seeded(next);

        var moved = false;
        var outcome = UpdateProfileSetting(service, s =>
        {
            // The profile switch lands between the derivation and the publish. This is the
            // window the outcome exists for: the value is durable under the profile the player
            // edited, and grafting it onto the profile now on screen would be the defect.
            if (!moved)
            {
                moved = true;
                TestReflection.SetPrivateField(service, "_profileSettings", arrived);
            }
            return ToLevel51(s);
        }, "app.playerLevel", "51");

        Assert.Equal(SettingsService.EditPublishOutcome.Superseded, outcome);

        // Nothing was grafted, and nothing was lost: the row is under the edited profile.
        Assert.Same(arrived, service.ProfileSettings);
        Assert.Equal("51", await store.GetProfileSettingAsync(onScreen, "app.playerLevel"));
        Assert.Null(await store.GetProfileSettingAsync(next, "app.playerLevel"));
    }

    // The consequence at the property setters, through the real setter and the real gate: a
    // superseded edit announces nothing, so no page is told to show a value belonging to a
    // profile it is not displaying.
    [Fact]
    public async Task A_superseded_edit_announces_nothing()
    {
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        var next = NewProfileId("next");
        // Warm the store, so the 500 ms below measures the gate and not schema creation.
        await store.SetProfileSettingAsync(onScreen, "app.scavRep", "1.0");

        var service = NewService(Seeded(onScreen), store: store);
        var events = RecordEventNames(service);
        var gate = PublishGate();

        Task edit;
        Monitor.Enter(gate);
        try
        {
            // Off the test thread, because the gate is reentrant and would not stop an edit
            // running on this one.
            edit = Task.Run(() => service.PlayerLevel = 51);
            Thread.Sleep(500);

            Assert.False(edit.IsCompleted, "the edit published outside the publish gate");

            // The switch completes entirely while the edit is stopped one statement short of
            // publishing.
            TestReflection.SetPrivateField(service, "_profileSettings", Seeded(next));
        }
        finally
        {
            Monitor.Exit(gate);
        }

        await edit;

        Assert.Empty(events);
        Assert.Equal(next, service.ProfileSettings.ProfileId);
        // The arrived profile's own level, not the edited one grafted on top of it.
        Assert.Equal(42, service.PlayerLevel);
        Assert.Equal("51", await store.GetProfileSettingAsync(onScreen, "app.playerLevel"));
    }
}
