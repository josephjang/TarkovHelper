using System.IO;
using System.Reflection;
using System.Text.Json;
using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;
using static TarkovHelper.Tests.SettingsServiceTestSupport;

namespace TarkovHelper.Tests;

/// <summary>
/// The contract each of the nine profile-scoped settings keeps with the store: the setter writes
/// its OWN key, changes its OWN snapshot field and announces its OWN event, and the read path
/// refuses a row no setter could have produced.
/// <para>
/// The setters are near-identical five-line bodies differing only in a key, a field and an
/// event, which is the classic surface for a copy-paste swap that no test notices: before this
/// file, only four of the eight then present were ever driven to a store write anywhere in the
/// suite. The table below drives every one of them and asserts all three pairings at once, so
/// swapping any one of them fails here rather than in a player's user_data.db.
/// </para>
/// <para>
/// Built on uninitialized services (see <see cref="TestReflection"/>) with a real SQLite store, the
/// way <c>SettingsReloadRaceTests</c> and <c>SettingsPublishOutcomeTests</c> are: the real singleton
/// constructor loads every setting and subscribes the process to <c>ProfileService</c>.
/// </para>
/// </summary>
public sealed class SettingsSetterContractTests : IDisposable
{
    /// <summary>Temp home for the real-SQLite stores every case here writes through.</summary>
    private readonly TempStoreRoot _stores = new("settings-contract");

    public void Dispose() => _stores.Dispose();

    private UserDataDbService NewStore() => _stores.NewStore();

    #region The setters

    /// <summary>
    /// One setter's whole contract. The keys are spelled as literals rather than through
    /// <c>SettingsService.Key*</c> on purpose: the constants are what the setter under test uses,
    /// so quoting them here would let a setter and its assertion move together.
    /// </summary>
    /// <param name="Apply">Drives the setter to a value the seed does not already hold.</param>
    /// <param name="Key">The ProfileSettings key the row must land under, and no other.</param>
    /// <param name="Stored">The serialized value that row must carry.</param>
    /// <param name="Expected">
    /// The snapshot the seed must become. Records compare by value, so this pins the changed
    /// field AND that the others were left alone.
    /// </param>
    /// <param name="Event">
    /// The changed event the setter announces, or null for the one setting that has none.
    /// </param>
    /// <param name="RewritesUnchangedValue">
    /// True for the one setting with no "value differs" guard, which therefore rewrites its row
    /// even when the snapshot already holds the value.
    /// </param>
    private sealed record SetterCase(
        string Name,
        Action<SettingsService> Apply,
        string Key,
        string Stored,
        Func<ProfileSettingsSnapshot, ProfileSettingsSnapshot> Expected,
        string? Event,
        bool RewritesUnchangedValue = false);

    private static readonly SetterCase[] AllSetters =
    {
        new("PlayerLevel", s => s.PlayerLevel = 51, "app.playerLevel", "51",
            seed => seed with { PlayerLevel = 51 }, "PlayerLevel"),
        new("ScavRep", s => s.ScavRep = -2.5, "app.scavRep", "-2.5",
            seed => seed with { ScavRep = -2.5 }, "ScavRep"),
        new("ShowLevelLockedQuests", s => s.ShowLevelLockedQuests = true,
            "app.showLevelLockedQuests", "True",
            seed => seed with { ShowLevelLockedQuests = true }, null,
            RewritesUnchangedValue: true),
        new("DspDecodeCount", s => s.DspDecodeCount = 1, "app.dspDecodeCount", "1",
            seed => seed with { DspDecodeCount = 1 }, "DspDecodeCount"),
        // Upper-cased on the way in, lower-cased on the way out: the setter normalizes.
        new("PlayerFaction", s => s.PlayerFaction = "USEC", "app.playerFaction", "usec",
            seed => seed with { PlayerFaction = "usec" }, "PlayerFaction"),
        new("HasEodEdition", s => s.HasEodEdition = false, "app.hasEodEdition", "False",
            seed => seed with { HasEodEdition = false }, "HasEodEdition"),
        new("HasUnheardEdition", s => s.HasUnheardEdition = false, "app.hasUnheardEdition", "False",
            seed => seed with { HasUnheardEdition = false }, "HasUnheardEdition"),
        new("PrestigeLevel", s => s.PrestigeLevel = 2, "app.prestigeLevel", "2",
            seed => seed with { PrestigeLevel = 2 }, "PrestigeLevel"),
        // The ninth value, and the only one addressed by trader rather than by a bare key: its
        // row lands under the prefix plus the trader id, and its snapshot field is the map with
        // that one entry replaced. Driven to a level the seed does not hold, so the reference
        // guard in the setter has to actually decide something.
        new("TraderLoyalty", s => s.SetTraderLoyalty(SeededTraderId, 2),
            "app.traderLoyalty." + SeededTraderId, "2",
            seed => seed with { TraderLoyalty = seed.TraderLoyalty.With(SeededTraderId, 2) },
            "TraderLoyalty"),
    };

    public static IEnumerable<object[]> SetterNames()
        => AllSetters.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(SetterNames))]
    public async Task Each_setter_writes_its_own_key_field_and_event(string name)
    {
        var setter = AllSetters.Single(c => c.Name == name);
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        var seed = Seeded(onScreen);
        var service = NewService(seed, store: store);
        var events = RecordEvents(service);

        setter.Apply(service);

        // The row landed under this setting's key, carrying this setting's value.
        Assert.Equal(setter.Stored, await store.GetProfileSettingAsync(onScreen, setter.Key));

        // ...and under no other key. The store starts empty and the profile id is unique to this
        // case, so any other row can only have come from the setter just driven.
        foreach (var otherKey in AllSetters.Select(c => c.Key).Where(k => k != setter.Key))
        {
            Assert.Null(await store.GetProfileSettingAsync(onScreen, otherKey));
        }

        // The snapshot changed in exactly one field. Value equality on the record makes this the
        // assertion that a "s with { OtherField = value }" typo cannot survive.
        Assert.Equal(setter.Expected(seed), service.ProfileSettings);

        // Announced once, on its own event, and on no other.
        Assert.Equal(
            setter.Event == null ? Array.Empty<string>() : new[] { setter.Event },
            events.Select(e => e.Name));
    }

    // The negative half of the table above: re-setting the value the snapshot already holds is a
    // no-op, for the settings that guard on "value differs" (all but one). Without this, a
    // setter whose guard read the wrong field would still pass the case above.
    [Theory]
    [MemberData(nameof(SetterNames))]
    public async Task Each_setter_re_set_to_the_value_it_already_holds_writes_nothing(string name)
    {
        var setter = AllSetters.Single(c => c.Name == name);
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        // Seeded with the value the setter is about to write, so every setter's guard should fire.
        var seed = setter.Expected(Seeded(onScreen));
        var service = NewService(seed, store: store);
        var events = RecordEvents(service);

        setter.Apply(service);

        // Nothing is announced either way: the guarded settings skip the edit outright, and the
        // one without a guard has no changed event to raise.
        Assert.Empty(events);

        foreach (var key in AllSetters.Select(c => c.Key))
        {
            var stored = await store.GetProfileSettingAsync(onScreen, key);

            // ShowLevelLockedQuests is the exception the service documents: it has never had a
            // "value differs" guard and raises no event, so an unconditional write is the whole
            // of its observable behaviour. It still writes its OWN key and only that one.
            if (setter.RewritesUnchangedValue && key == setter.Key)
                Assert.Equal(setter.Stored, stored);
            else
                Assert.Null(stored);
        }

        // The ones that skip leave the very snapshot they were handed in place, so no page is
        // asked to redraw for a value that did not move. The unguarded one republishes an equal one.
        if (setter.RewritesUnchangedValue)
            Assert.Equal(seed, service.ProfileSettings);
        else
            Assert.Same(seed, service.ProfileSettings);
    }

    #endregion

    #region Clamping on the read path

    /// <summary>
    /// A stored row and the value the snapshot must answer once it has been read back. Every one
    /// of these is a row no setter could have written: the setters clamp. The store is a plain
    /// SQLite file a player can edit, the legacy JSON import wrote its numbers through unchecked
    /// for years, and an older build could mis-read its own comma-decimal scav rep into one.
    /// </summary>
    public static IEnumerable<object[]> OutOfRangeRows() => new[]
    {
        // A level past the cap answers every quest's level requirement, so the whole list reads
        // as unlocked.
        new object[] { "app.playerLevel", "9999", nameof(ProfileSettingsSnapshot.PlayerLevel), SettingsService.MaxPlayerLevel },
        new object[] { "app.playerLevel", "0", nameof(ProfileSettingsSnapshot.PlayerLevel), SettingsService.MinPlayerLevel },
        new object[] { "app.playerLevel", "-5", nameof(ProfileSettingsSnapshot.PlayerLevel), SettingsService.MinPlayerLevel },
        // Make Amends selects its branch by an EXACT match on this count, so a count outside the
        // range matches no branch and locks all three.
        new object[] { "app.dspDecodeCount", "7", nameof(ProfileSettingsSnapshot.DspDecodeCount), SettingsService.MaxDspDecodeCount },
        new object[] { "app.dspDecodeCount", "-1", nameof(ProfileSettingsSnapshot.DspDecodeCount), SettingsService.MinDspDecodeCount },
        new object[] { "app.prestigeLevel", "99", nameof(ProfileSettingsSnapshot.PrestigeLevel), SettingsService.MaxPrestigeLevel },
        new object[] { "app.prestigeLevel", "-3", nameof(ProfileSettingsSnapshot.PrestigeLevel), SettingsService.MinPrestigeLevel },
    };

    [Theory]
    [MemberData(nameof(OutOfRangeRows))]
    public async Task An_out_of_range_stored_row_is_clamped_on_the_way_in(
        string key, string stored, string field, int expected)
    {
        var store = NewStore();
        var target = ProfileService.GetProfileId(AppProfile.PveZone);
        await store.SetProfileSettingAsync(target, key, stored);
        var service = NewService(Seeded(NewProfileId("other")), store: store);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        var property = typeof(ProfileSettingsSnapshot).GetProperty(field);
        Assert.True(property != null, $"ProfileSettingsSnapshot has no property '{field}'");
        Assert.Equal(expected, property!.GetValue(service.ProfileSettings));
    }

    // The contrast that keeps the clamp honest: an in-range row is read back untouched, so
    // "the value equals the bound" above cannot be explained by the parse collapsing everything.
    [Fact]
    public async Task An_in_range_stored_row_is_read_back_exactly()
    {
        var store = NewStore();
        var target = ProfileService.GetProfileId(AppProfile.PveZone);
        await store.SetProfileSettingAsync(target, "app.playerLevel", "37");
        await store.SetProfileSettingAsync(target, "app.dspDecodeCount", "2");
        await store.SetProfileSettingAsync(target, "app.prestigeLevel", "3");
        var service = NewService(Seeded(NewProfileId("other")), store: store);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        var snapshot = service.ProfileSettings;
        Assert.Equal(37, snapshot.PlayerLevel);
        Assert.Equal(2, snapshot.DspDecodeCount);
        Assert.Equal(3, snapshot.PrestigeLevel);
    }

    /// <summary>
    /// The prefixed loyalty rows go through the same clamp as the keyed values, one row at a
    /// time. Kept out of <see cref="OutOfRangeRows"/> because the field is a map rather than an
    /// int, and worth its own case for what an unclamped row would do: a hand-written 9 would
    /// answer every published requirement and silently unlock all 94 gated quests, and a 0 would
    /// lock a requirement of 1 that the game has already met.
    /// </summary>
    [Theory]
    [InlineData("9", SettingsService.MaxTraderLoyaltyLevel)]
    [InlineData("0", SettingsService.MinTraderLoyaltyLevel)]
    [InlineData("-4", SettingsService.MinTraderLoyaltyLevel)]
    [InlineData("3", 3)]
    public async Task An_out_of_range_trader_loyalty_row_is_clamped_on_the_way_in(
        string stored, int expected)
    {
        var store = NewStore();
        var target = ProfileService.GetProfileId(AppProfile.PveZone);
        await store.SetProfileSettingAsync(
            target, SettingsService.TraderLoyaltyKey(SeededTraderId), stored);
        var service = NewService(Seeded(NewProfileId("other")), store: store);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        Assert.Equal(expected, service.ProfileSettings.TraderLoyalty.LevelOf(SeededTraderId));
    }

    /// <summary>
    /// A row the read cannot make sense of leaves no entry at all, so the trader answers the
    /// default rather than a level invented from a broken value. The unparsable case is the one
    /// that matters: an entry of 0 from a failed parse would lock every level 1 requirement.
    /// </summary>
    [Theory]
    [InlineData("not-a-level")]
    [InlineData("")]
    [InlineData("2.5")]
    public async Task An_unparsable_trader_loyalty_row_leaves_no_entry(string stored)
    {
        var store = NewStore();
        var target = ProfileService.GetProfileId(AppProfile.PveZone);
        await store.SetProfileSettingAsync(
            target, SettingsService.TraderLoyaltyKey(SeededTraderId), stored);
        var service = NewService(Seeded(NewProfileId("other")), store: store);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        Assert.Equal(0, service.ProfileSettings.TraderLoyalty.Count);
        Assert.Equal(
            SettingsService.DefaultTraderLoyaltyLevel,
            service.ProfileSettings.TraderLoyalty.LevelOf(SeededTraderId));
    }

    /// <summary>
    /// A row keyed on the bare prefix names no trader. It is dropped rather than kept under an
    /// empty id, where it could never be matched to a requirement and would sit in the map
    /// forever answering nothing.
    /// </summary>
    [Fact]
    public async Task A_loyalty_row_with_no_trader_id_is_dropped()
    {
        var store = NewStore();
        var target = ProfileService.GetProfileId(AppProfile.PveZone);
        await store.SetProfileSettingAsync(target, SettingsService.TraderLoyaltyKeyPrefix, "3");
        var service = NewService(Seeded(NewProfileId("other")), store: store);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        Assert.Equal(0, service.ProfileSettings.TraderLoyalty.Count);
    }

    /// <summary>
    /// A row for a trader the loaded data gates nothing on is KEPT. It costs nothing (no gate
    /// reads it) and it is the mechanism the drawer's data-driven roster depends on: the publish
    /// that starts gating on that trader finds the player's entry already there.
    /// </summary>
    [Fact]
    public async Task A_loyalty_row_for_an_unknown_trader_is_kept()
    {
        var store = NewStore();
        var target = ProfileService.GetProfileId(AppProfile.PveZone);
        await store.SetProfileSettingAsync(
            target, SettingsService.TraderLoyaltyKey("not-a-trader-the-data-knows"), "4");
        var service = NewService(Seeded(NewProfileId("other")), store: store);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        Assert.Equal(4, service.ProfileSettings.TraderLoyalty.LevelOf("not-a-trader-the-data-knows"));
    }

    // The legacy importer is the one writer that can introduce an out-of-range row, so it clamps
    // too: the row it leaves behind is what config migration then copies between installs.
    [Fact]
    public async Task The_legacy_json_import_clamps_every_bounded_value_it_writes()
    {
        var store = NewStore();
        var service = NewService(Seeded(NewProfileId("other")), store: store);

        WithTempConfigPath(() =>
        {
            File.WriteAllText(
                Path.Combine(AppEnv.ConfigPath, "app_settings.json"),
                JsonSerializer.Serialize(new
                {
                    playerLevel = 9999,
                    scavRep = 55.0,
                    dspDecodeCount = 7,
                    baseFontSize = 185.0,
                }));

            var migrate = typeof(SettingsService).GetMethod(
                "MigrateFromJsonIfNeeded", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(migrate != null, "SettingsService has no MigrateFromJsonIfNeeded");
            migrate!.Invoke(service, Array.Empty<object?>());
            return 0;
        });

        var pvp = ProfileService.PvpProfileId;
        Assert.Equal(
            SettingsService.MaxPlayerLevel.ToString(),
            await store.GetProfileSettingAsync(pvp, "app.playerLevel"));
        Assert.Equal(
            SettingsValue.FormatDouble(SettingsService.MaxScavRep),
            await store.GetProfileSettingAsync(pvp, "app.scavRep"));
        Assert.Equal(
            SettingsService.MaxDspDecodeCount.ToString(),
            await store.GetProfileSettingAsync(pvp, "app.dspDecodeCount"));
        Assert.Equal(
            SettingsValue.FormatDouble(SettingsService.MaxFontSize),
            store.GetSetting("app.baseFontSize"));
    }

    /// <summary>
    /// Runs <paramref name="body"/> with <see cref="AppEnv.ConfigPath"/> pointed at an empty temp
    /// folder. The legacy import DELETES the app_settings.json it read, which must never be the
    /// build output's own Config folder.
    /// </summary>
    private T WithTempConfigPath<T>(Func<T> body)
    {
        var original = AppEnv.ConfigPath;
        try
        {
            AppEnv.ConfigPath = _stores.NewFolder("config");
            return body();
        }
        finally
        {
            AppEnv.ConfigPath = original;
        }
    }

    #endregion

    #region The fan-out and the snapshot it announces

    /// <summary>
    /// Raises the seven changed events for one snapshot through the service's own private
    /// fan-out. Reached by reflection because the two states worth pinning (announcing the live
    /// snapshot, and announcing one that has been superseded) are decided inside it.
    /// </summary>
    private static void RaiseProfileSettingsChanged(
        SettingsService service, ProfileSettingsSnapshot snapshot)
    {
        var method = typeof(SettingsService).GetMethod(
            "RaiseProfileSettingsChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(method != null, "SettingsService has no RaiseProfileSettingsChanged");
        method!.Invoke(service, new object?[] { snapshot });
    }

    // A fan-out announces the snapshot it was handed only while that snapshot is still the live
    // one. This is the pair: the live snapshot announces all seven...
    [Fact]
    public void A_fan_out_for_the_live_snapshot_announces_every_event()
    {
        var service = NewService(Seeded(NewProfileId("live")));
        var events = RecordEvents(service);

        RaiseProfileSettingsChanged(service, service.ProfileSettings);

        Assert.Equal(EventsFor(service.ProfileSettings), events.Select(e => e.Name));
    }

    // ...and one the cache has moved past announces nothing. Left unguarded, a reload on the log
    // watcher's thread could publish profile A, have its fan-out queued behind a manual switch the
    // dispatcher is running inline, and drain after that switch published B: QuestListPage sets
    // its faction radio from these events and PERSISTS the radio on the next filter change, so
    // B's screen would show and then save A's faction.
    [Fact]
    public void A_fan_out_the_cache_has_moved_past_announces_nothing()
    {
        var service = NewService(Seeded(NewProfileId("live")));
        var events = RecordEvents(service);

        RaiseProfileSettingsChanged(service, Seeded(NewProfileId("superseded")));

        Assert.Empty(events);
    }

    /// <summary>
    /// The reload signal exists for readers that cannot repaint from a value event, because the
    /// value they show may have no event on a given publish: trader loyalty announces once per
    /// STORED entry, so a profile that has entered none announces nothing at all. Without this,
    /// the drawer would keep the previous profile's loyalty highlights under the new profile's
    /// name after a switch, and its old ones after a reset.
    /// </summary>
    [Fact]
    public void A_fan_out_raises_the_reload_signal_once_even_when_the_snapshot_holds_no_loyalty()
    {
        var service = NewService(
            Seeded(NewProfileId("live")) with { TraderLoyalty = TraderLoyaltyLevels.Empty });
        var reloads = 0;
        var loyaltyEvents = 0;
        service.ProfileSettingsReloaded += (_, _) => reloads++;
        service.TraderLoyaltyChanged += (_, _) => loyaltyEvents++;

        RaiseProfileSettingsChanged(service, service.ProfileSettings);

        Assert.Equal(0, loyaltyEvents);
        Assert.Equal(1, reloads);
    }

    // ...and it is a fan-out signal, not an edit signal: a single setter announces its own value
    // and nothing else, so a reader that rebuilds everything on a reload is not made to do it on
    // every click in the drawer.
    [Fact]
    public async Task A_single_edit_does_not_raise_the_reload_signal()
    {
        var store = NewStore();
        var service = NewService(Seeded(NewProfileId("onscreen")), store: store);
        var reloads = 0;
        service.ProfileSettingsReloaded += (_, _) => reloads++;

        service.PlayerLevel = 51;
        service.SetTraderLoyalty(SeededTraderId, 2);

        Assert.Equal(0, reloads);
        // The edits really happened, so the zero above is not an edit path that did nothing.
        Assert.Equal(51, service.PlayerLevel);
        Assert.Equal(2, service.GetTraderLoyalty(SeededTraderId));
        Assert.Equal("2", await store.GetProfileSettingAsync(
            service.ProfileSettings.ProfileId, SettingsService.TraderLoyaltyKey(SeededTraderId)));
    }

    // A fan-out the cache has moved past announces nothing, the reload signal included: it is
    // raised under the same guard as the value events, and a reader that rebuilt on it would
    // otherwise repaint from a snapshot that is no longer live.
    [Fact]
    public void A_superseded_fan_out_does_not_raise_the_reload_signal()
    {
        var service = NewService(Seeded(NewProfileId("live")));
        var reloads = 0;
        service.ProfileSettingsReloaded += (_, _) => reloads++;

        RaiseProfileSettingsChanged(service, Seeded(NewProfileId("superseded")));

        Assert.Equal(0, reloads);
    }

    // The same question is re-asked before EVERY event, not once up front, because each handler
    // can block the raising thread for as long as the dispatcher takes to run it. Here the switch
    // lands from inside the first handler, which is the one place a single-threaded test can be
    // while the fan-out is in progress.
    [Fact]
    public async Task A_switch_landing_mid_fan_out_stops_the_rest_of_it()
    {
        var store = NewStore();
        var target = ProfileService.GetProfileId(AppProfile.PveZone);
        await store.SetProfileSettingAsync(target, "app.playerLevel", "7");
        var service = NewService(Seeded(NewProfileId("before")), store: store);
        var events = RecordEvents(service);

        var arrived = Seeded(NewProfileId("arrived"));
        service.PlayerLevelChanged += (_, _) =>
            TestReflection.SetPrivateField(service, "_profileSettings", arrived);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        // The first event was already out when the cache moved; the other six were not raised,
        // so no page is told to show a value belonging to a profile it is not displaying.
        Assert.Equal(new[] { "PlayerLevel" }, events.Select(e => e.Name));
        Assert.Same(arrived, service.ProfileSettings);
    }

    #endregion

    #region Edits in flight

    // The window the edit counter alone could not see: an edit bumps the generation BEFORE it
    // writes its row, so a load can capture the already-bumped generation, read rows the write has
    // not committed yet, find the generation unmoved at publish time and republish the value the
    // player just replaced - under the right profile id, with no failure recorded, and with
    // nothing in the service to repair it afterwards. An edit is therefore counted as in flight
    // across its whole write-and-publish span, and a load that read during one re-reads.
    //
    // Seeding the counter is what an edit does between its first and last statement; the store
    // holding a value the live snapshot does not is what "the row was not durable yet" looks like
    // from the load's side.
    [Fact]
    public async Task A_load_that_read_while_an_edit_was_in_flight_does_not_publish_its_rows()
    {
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        await store.SetProfileSettingAsync(onScreen, "app.playerLevel", "7");
        var service = NewService(Seeded(onScreen), store: store);
        TestReflection.SetPrivateField(service, "_editsInFlight", 1L);
        var live = service.ProfileSettings;
        var events = RecordEvents(service);

        service.HandleProfileReset(onScreen);

        // The rows read alongside the edit were dropped, every attempt: the snapshot the edits
        // built is still the live one, down to its identity.
        Assert.Same(live, service.ProfileSettings);
        Assert.Equal(42, service.PlayerLevel);

        // ...and the hook still announced. Its caller (ProfileResetService.RunRefreshHooks) runs
        // it as a plain Action whose contract is that the cache is current and announced when it
        // returns, so giving up quietly would leave the settings panel showing wiped values.
        Assert.Equal(EventsFor(service.ProfileSettings), events.Select(e => e.Name));
        // Announced from the LIVE snapshot, which is the one every getter answers from.
        Assert.Equal(42, events.Single(e => e.Name == "PlayerLevel").Value);
    }

    // The contrast: with no edit in flight the same hook publishes the rows it read, so the case
    // above cannot be explained by the reset hook never publishing anything.
    [Fact]
    public async Task The_same_load_with_no_edit_in_flight_publishes_the_rows_it_read()
    {
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        await store.SetProfileSettingAsync(onScreen, "app.playerLevel", "7");
        var service = NewService(Seeded(onScreen), store: store);
        var events = RecordEvents(service);

        service.HandleProfileReset(onScreen);

        Assert.Equal(7, service.PlayerLevel);
        Assert.Equal(EventsFor(service.ProfileSettings), events.Select(e => e.Name));
        Assert.Equal(7, events.Single(e => e.Name == "PlayerLevel").Value);
    }

    // Where the in-flight bracket starts and ends, observed from inside an edit. The derivation
    // runs twice here: once before the bracket opens, and once inside it (a competing publish on
    // the same profile forces the re-derivation, which happens under the publish gate). The second
    // reading also proves the store write is inside the bracket, so the counter is up before the
    // row is written - which is the whole point of raising it first.
    [Fact]
    public void An_edit_counts_as_in_flight_across_its_write_and_its_publish()
    {
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        var service = NewService(Seeded(onScreen), store: store);
        var winner = Seeded(onScreen) with { PrestigeLevel = 5 };

        var inFlight = new List<long>();
        var rowsSeen = new List<string?>();
        var outcome = UpdateProfileSetting(
            service,
            s =>
            {
                inFlight.Add(EditsInFlight(service));
                rowsSeen.Add(store.LoadProfileSettings(onScreen).GetValueOrDefault("app.playerLevel"));
                if (inFlight.Count == 1)
                    TestReflection.SetPrivateField(service, "_profileSettings", winner);
                return s.PlayerLevel == 51 ? null : s with { PlayerLevel = 51 };
            },
            "app.playerLevel", "51");

        Assert.Equal(SettingsService.EditPublishOutcome.Applied, outcome);
        Assert.Equal(2, inFlight.Count);
        // Outside the bracket before the edit begins, and inside it at publish time.
        Assert.Equal(0, inFlight[0]);
        Assert.True(inFlight[1] >= 1, $"the edit was not counted in flight while publishing (saw {inFlight[1]})");
        // The row is not there before the bracket opens and is there inside it: the write is
        // bracketed, so no load can read around it without seeing the counter raised.
        Assert.Null(rowsSeen[0]);
        Assert.Equal("51", rowsSeen[1]);
        // Lowered again on the way out, so one edit cannot make every later load re-read.
        Assert.Equal(0, EditsInFlight(service));
    }

    private static long EditsInFlight(SettingsService service)
    {
        var field = typeof(SettingsService).GetField(
            "_editsInFlight", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(field != null, "SettingsService has no private field '_editsInFlight'");
        return (long)field!.GetValue(service)!;
    }

    #endregion
}
