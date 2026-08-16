using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;
using static TarkovHelper.Tests.SettingsServiceTestSupport;

namespace TarkovHelper.Tests;

/// <summary>
/// The settings half of the transition guard (fix-profile-settings-race.spec.md).
/// <see cref="SettingsService"/> was the last <c>ActiveProfileChanged</c> subscriber without one:
/// it refilled eight nullable fields through eight separate ambient-selection reads, so a switch
/// landing mid-reload tore the cache across two profiles, and two switches in flight could leave
/// the older one's values published last. The eight values are now one immutable
/// <see cref="ProfileSettingsSnapshot"/> carrying its profile and its transition revision.
/// <para>
/// Most cases here are built on an uninitialized service (see
/// <see cref="SettingsServiceTestSupport.NewService"/>) so no singleton constructor runs and no
/// user_data.db is touched. Where no store is seeded, the field
/// is null and the bulk read throws, which the service's own catch turns into "publish this
/// profile's defaults" - the exact outcome a stale load must NOT be allowed to produce. That makes
/// the race assertions sharp: seeded values left standing can only mean the revision guard
/// discarded the load.
/// </para>
/// <para>
/// Scope note, matching <see cref="ProfileReloadRaceTests"/>: the guard is asserted at its seam
/// (an injectable revision, and a snapshot whose profile the writes follow) rather than by racing
/// two real reloads, which would mean flipping the process-wide <see cref="ProfileService"/>
/// singleton mid-load. The suite deliberately never does that.
/// </para>
/// </summary>
[Collection(SchedulingSensitiveCollection.Name)]
public sealed class SettingsReloadRaceTests : IDisposable
{
    private static string IdOf(AppProfile profile) => ProfileService.GetProfileId(profile);

    /// <summary>Temp home for the real-SQLite stores the load and write cases need.</summary>
    private readonly TempStoreRoot _stores = new("settings-race");

    public void Dispose() => _stores.Dispose();

    private UserDataDbService NewStore() => _stores.NewStore();

    /// <summary>
    /// Delivers an <c>ActiveProfileChanged</c> the way ProfileService does. Reached by reflection
    /// because the handler is private and raising the real event would mean moving the
    /// process-wide singleton (the same trade ProfileResetHooksTests makes for the debounce
    /// flush and the log-line parser).
    /// </summary>
    private static void RaiseProfileChanged(
        SettingsService service, AppProfile profile, long revision, bool profileChanged)
    {
        var handler = typeof(SettingsService).GetMethod(
            "OnActiveProfileChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(handler != null, "SettingsService has no OnActiveProfileChanged");
        handler!.Invoke(service, new object?[]
        {
            null,
            new ProfileChangedEventArgs(profile, isAuto: true, profileChanged, revision),
        });
    }

    #region The revision guard

    [Fact]
    public void A_settings_reload_that_lost_the_race_does_not_publish_over_the_newer_one()
    {
        // Revision 2 has already been requested: this load serves the transition before it.
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 2);
        var events = RecordEventNames(service);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        Assert.Equal(IdOf(AppProfile.PvpSeason), service.ProfileSettings.ProfileId);
        Assert.Equal(42, service.PlayerLevel);
        Assert.Empty(events);
    }

    // The contrast that keeps the test above honest: the same call for the CURRENT transition
    // does publish, so "the level survived" cannot be explained by the reload doing nothing.
    [Fact]
    public void A_settings_reload_for_the_current_transition_publishes_and_raises_every_event()
    {
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 2);
        var events = RecordEventNames(service);

        service.ReloadForProfile(AppProfile.PveZone, revision: 2);

        Assert.Equal(IdOf(AppProfile.PveZone), service.ProfileSettings.ProfileId);
        Assert.Equal(SettingsService.DefaultPlayerLevel, service.PlayerLevel);
        Assert.Equal(AllChangedEvents, events);
    }

    // Two transitions announced in quick succession: whichever load finishes last, the values
    // left loaded belong to the LATER one. Run in the order that used to lose, the older
    // transition finishing after the newer.
    [Fact]
    public async Task The_later_of_two_settings_transitions_wins_whatever_order_they_finish_in()
    {
        var store = NewStore();
        await store.SetProfileSettingAsync(IdOf(AppProfile.PvpSeason), "app.playerLevel", "9");
        await store.SetProfileSettingAsync(IdOf(AppProfile.PveZone), "app.playerLevel", "3");
        var service = NewService(Seeded(IdOf(AppProfile.PvpZone)), latestRevision: 0, store);

        // The newer transition completes first and publishes; the older one then finishes and
        // must not restore anything.
        service.ReloadForProfile(AppProfile.PvpSeason, revision: 2);
        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        Assert.Equal(IdOf(AppProfile.PvpSeason), service.ProfileSettings.ProfileId);
        Assert.Equal(9, service.PlayerLevel);
    }

    // The two tests above pin the guard when it is read before the publish on one thread. These
    // two pin it where it used to be unguarded: the window between "this load is still wanted"
    // and the swap itself. Both suspend the load at the publish gate and land the competing
    // publish inside that window, which is exactly what a check made outside the gate cannot see.
    [Fact]
    public async Task A_load_superseded_while_it_waited_to_publish_publishes_nothing()
    {
        // No store, so the read throws and the service's own catch takes the load straight to
        // the publish gate, where this test is holding it.
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 1);
        var events = RecordEventNames(service);
        var gate = PublishGate();

        Task load;
        Monitor.Enter(gate);
        try
        {
            // Off the test thread, because the gate is reentrant and would not stop a load
            // running on this one.
            load = Task.Run(() => service.ReloadForProfile(AppProfile.PveZone, revision: 1));
            Thread.Sleep(200);

            Assert.False(load.IsCompleted, "the settings load published outside the publish gate");

            // The newer transition is announced AND completes entirely while the older load is
            // stopped one statement short of publishing.
            service.ReloadForProfile(AppProfile.PvpZone, revision: 2);
            Assert.Equal(IdOf(AppProfile.PvpZone), service.ProfileSettings.ProfileId);
        }
        finally
        {
            Monitor.Exit(gate);
        }

        await load;

        // The older load asked again under the gate and dropped its rows. The seven events are
        // the newer transition's single fan-out: the older one raised none.
        Assert.Equal(IdOf(AppProfile.PvpZone), service.ProfileSettings.ProfileId);
        Assert.Equal(AllChangedEvents, events);
    }

    [Fact]
    public async Task A_load_overtaken_by_an_edit_reads_again_instead_of_reverting_it()
    {
        var store = NewStore();
        var target = IdOf(AppProfile.PveZone);
        await store.SetProfileSettingAsync(target, "app.playerLevel", "7");
        var service = NewService(Seeded(target), latestRevision: 1, store);
        var gate = PublishGate();

        Task load;
        Monitor.Enter(gate);
        try
        {
            load = Task.Run(() => service.ReloadForProfile(AppProfile.PveZone, revision: 1));
            Thread.Sleep(200);

            Assert.False(load.IsCompleted, "the settings load published outside the publish gate");

            // The player types a level while the load is holding rows that predate it. Same
            // profile, so the load is about to publish over the very value they just typed.
            service.PlayerLevel = 51;
            Assert.Equal(51, service.PlayerLevel);
        }
        finally
        {
            Monitor.Exit(gate);
        }

        await load;

        // The edit stands, on screen and in the store, and the load did not simply give up: it
        // read again and published rows that include the edit. The seed's scav rep is gone,
        // which only a second read can explain (the store has no row for it).
        Assert.Equal(51, service.PlayerLevel);
        Assert.Equal("51", await store.GetProfileSettingAsync(target, "app.playerLevel"));
        Assert.Null(service.ProfileSettings.ScavRep);
    }

    #endregion

    #region Atomicity

    // The tear the eight per-key reads allowed: a switch between read k and read k+1 left keys
    // 1..k holding one profile's values and the rest holding another's, with nothing able to
    // detect it. One published reload now replaces all eight values AND the profile id as a
    // single reference, so no mixture is observable.
    [Fact]
    public async Task A_published_reload_replaces_every_value_and_the_profile_id_together()
    {
        var store = NewStore();
        var target = IdOf(AppProfile.PveZone);
        await store.SetProfileSettingAsync(target, "app.playerLevel", "7");
        // Fractional, and written in the invariant format the service stores: the parse is
        // culture-proof now (see the storage-format region), so this reads back as -2.5 whatever
        // decimal separator the runner uses.
        await store.SetProfileSettingAsync(target, "app.scavRep", "-2.5");
        await store.SetProfileSettingAsync(target, "app.showLevelLockedQuests", "True");
        await store.SetProfileSettingAsync(target, "app.dspDecodeCount", "1");
        await store.SetProfileSettingAsync(target, "app.playerFaction", "usec");
        // app.hasEodEdition, app.hasUnheardEdition and app.prestigeLevel deliberately have no
        // row: an absent key must fall back to its default, never to the seed's value.

        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 0, store);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        var snapshot = service.ProfileSettings;
        Assert.Equal(target, snapshot.ProfileId);
        Assert.Equal(1L, snapshot.Revision);
        Assert.Equal(7, snapshot.PlayerLevel);
        Assert.Equal(-2.5, snapshot.ScavRep);
        Assert.True(snapshot.ShowLevelLockedQuests);
        Assert.Equal(1, snapshot.DspDecodeCount);
        Assert.Equal("usec", snapshot.PlayerFaction);

        // Nothing of the season profile survived the swap: these three were true, true and 4.
        Assert.Null(snapshot.HasEodEdition);
        Assert.Null(snapshot.HasUnheardEdition);
        Assert.Null(snapshot.PrestigeLevel);
        Assert.False(service.HasEodEdition);
        Assert.False(service.HasUnheardEdition);
        Assert.Equal(SettingsService.DefaultPrestigeLevel, service.PrestigeLevel);
    }

    // A row the store cannot parse is not a reason to fall back to another profile's value: it
    // leaves its own field null, exactly as the per-key TryParse fallbacks did.
    [Fact]
    public async Task An_unparsable_row_falls_back_to_its_default_and_leaves_its_neighbours_alone()
    {
        var store = NewStore();
        var target = IdOf(AppProfile.PveZone);
        await store.SetProfileSettingAsync(target, "app.playerLevel", "not-a-number");
        await store.SetProfileSettingAsync(target, "app.prestigeLevel", "2");

        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 0, store);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        Assert.Equal(SettingsService.DefaultPlayerLevel, service.PlayerLevel);
        Assert.Equal(2, service.PrestigeLevel);
    }

    #endregion

    #region Write attribution

    // The reproducing case for the write half of the defect. The snapshot holds one profile's
    // values while the ambient selection names another - the collision window, where the switch
    // has already moved the selection and the reload has not caught up. A correction made there
    // is a correction of the number that was ON SCREEN, so it belongs to that profile; the old
    // code resolved ProfileService.Instance.ActiveProfileId at write time and silently
    // overwrote the selected profile's value instead (PRD R2).
    [Fact]
    public async Task An_edit_is_stored_in_the_profile_whose_value_was_on_screen()
    {
        var store = NewStore();
        // Synthetic, so it cannot coincide with the ambient selection whatever that happens to
        // be, which is what makes the "nothing else was written" assertions below exhaustive.
        var onScreen = NewProfileId("onscreen");
        var service = NewService(Seeded(onScreen), latestRevision: 0, store);
        var events = RecordEventNames(service);

        service.PlayerLevel = 51;
        service.PlayerFaction = "USEC";
        service.HasUnheardEdition = false;

        Assert.Equal("51", await store.GetProfileSettingAsync(onScreen, "app.playerLevel"));
        Assert.Equal("usec", await store.GetProfileSettingAsync(onScreen, "app.playerFaction"));
        Assert.Equal("False", await store.GetProfileSettingAsync(onScreen, "app.hasUnheardEdition"));

        // Whichever profile is selected, it is one of these three, and the pre-snapshot code
        // would have written all three edits into it. None of them received anything.
        foreach (var profileId in new[]
                 {
                     ProfileService.PvpProfileId, ProfileService.PveProfileId,
                     ProfileService.SeasonProfileId,
                 })
        {
            Assert.Null(await store.GetProfileSettingAsync(profileId, "app.playerLevel"));
            Assert.Null(await store.GetProfileSettingAsync(profileId, "app.playerFaction"));
            Assert.Null(await store.GetProfileSettingAsync(profileId, "app.hasUnheardEdition"));
        }

        // The edits are on screen too, under the same profile, and announced once each.
        Assert.Equal(onScreen, service.ProfileSettings.ProfileId);
        Assert.Equal(51, service.PlayerLevel);
        Assert.Equal(new[] { "PlayerLevel", "PlayerFaction", "HasUnheardEdition" }, events);
    }

    // Re-setting a value that is already current changes nothing and announces nothing, which is
    // what the per-property "value differs" guards decided before the snapshot existed.
    [Fact]
    public async Task Re_setting_the_current_value_publishes_nothing_and_raises_nothing()
    {
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        var service = NewService(Seeded(onScreen), latestRevision: 0, store);
        var before = service.ProfileSettings;
        var events = RecordEventNames(service);

        service.PlayerLevel = 42;
        service.ScavRep = 5.5;
        service.PlayerFaction = "bear";
        service.HasEodEdition = true;

        Assert.Same(before, service.ProfileSettings);
        Assert.Empty(events);

        // The skip covers the WRITE as well as the publish, which is the half nothing used to
        // check: the store starts empty, so a row appearing here would mean every settings-panel
        // redraw that re-assigns the current values rewrites four rows for nothing.
        Assert.Null(await store.GetProfileSettingAsync(onScreen, "app.playerLevel"));
        Assert.Null(await store.GetProfileSettingAsync(onScreen, "app.scavRep"));
        Assert.Null(await store.GetProfileSettingAsync(onScreen, "app.playerFaction"));
        Assert.Null(await store.GetProfileSettingAsync(onScreen, "app.hasEodEdition"));
    }

    // The publish is decided under the gate, so a competing publisher can only preempt an edit
    // BEFORE the gate is taken: between reading the snapshot the value was corrected on and
    // arriving at the swap. These two tests drive that window directly, from inside the
    // derivation itself, because it is the one place a test can be while it happens.
    //
    // Preempted by ANOTHER profile: the edit is abandoned rather than re-derived. Re-deriving
    // here would graft the value the player typed for one profile onto a different profile's
    // snapshot, which is the exact defect PRD R2 removes.
    [Fact]
    public async Task An_edit_preempted_by_another_profile_is_not_grafted_onto_it()
    {
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        var intruderId = NewProfileId("intruder");
        var intruder = Seeded(intruderId, revision: 1);
        var service = NewService(Seeded(onScreen), latestRevision: 0, store);

        var derivations = 0;
        var outcome = UpdateProfileSetting(
            service,
            s =>
            {
                // A switch publishes another profile's values while this edit is in flight.
                if (++derivations == 1)
                    TestReflection.SetPrivateField(service, "_profileSettings", intruder);
                return s with { PlayerLevel = 51 };
            },
            "app.playerLevel", "51");

        // Superseded is what stops the property setter announcing the new level at pages that
        // are showing the other profile: this path raises no events itself, so the outcome IS
        // the "nothing was announced" assertion. Not Unchanged, which would mean the edit was a
        // no-op rather than one deliberately dropped.
        Assert.Equal(SettingsService.EditPublishOutcome.Superseded, outcome);
        // Derived once, never re-derived against the intruder.
        Assert.Equal(1, derivations);
        Assert.Same(intruder, service.ProfileSettings);

        // ...and the correction is still durable, under the profile whose value was on screen
        // and under no other.
        Assert.Equal("51", await store.GetProfileSettingAsync(onScreen, "app.playerLevel"));
        Assert.Null(await store.GetProfileSettingAsync(intruderId, "app.playerLevel"));
    }

    // Preempted on its OWN profile: not a reason to drop the edit, a reason to re-apply it to
    // the winner. Publishing the snapshot derived from the original would undo whatever the
    // winner carried (here a prestige level, in production a freshly loaded row or another edit).
    [Fact]
    public void An_edit_preempted_on_its_own_profile_is_re_derived_from_the_winner()
    {
        var store = NewStore();
        var onScreen = NewProfileId("onscreen");
        var winner = Seeded(onScreen) with { PrestigeLevel = 5 };
        var service = NewService(Seeded(onScreen), latestRevision: 0, store);

        var derivations = 0;
        var outcome = UpdateProfileSetting(
            service,
            s =>
            {
                if (++derivations == 1)
                    TestReflection.SetPrivateField(service, "_profileSettings", winner);
                return s with { PlayerLevel = 51 };
            },
            "app.playerLevel", "51");

        Assert.Equal(SettingsService.EditPublishOutcome.Applied, outcome);
        Assert.Equal(2, derivations);

        var live = service.ProfileSettings;
        Assert.Equal(51, live.PlayerLevel);
        // Both values at once, which only a re-derivation against the winner can produce: the
        // snapshot derived from the original still carries the seed's prestige 4.
        Assert.Equal(5, live.PrestigeLevel);
    }

    // An edit made against one profile does not follow the snapshot when it moves: the switch
    // republishes the new profile's own stored values, and the earlier edit stays where it was
    // written (PRD R1).
    [Fact]
    public async Task An_edit_does_not_follow_the_snapshot_to_the_next_profile()
    {
        var store = NewStore();
        await store.SetProfileSettingAsync(IdOf(AppProfile.PveZone), "app.playerLevel", "3");
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 0, store);

        service.PlayerLevel = 51;
        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        Assert.Equal(3, service.PlayerLevel);
        Assert.Equal("51", await store.GetProfileSettingAsync(IdOf(AppProfile.PvpSeason), "app.playerLevel"));
    }

    #endregion

    #region The singleton

    // Structural, and deliberately so: the defect is a lost first-access race, and the only
    // behavioural test for it would have to build the REAL singleton, which loads the build
    // output's user_data.db and subscribes the process to ProfileService for every later test.
    // What the race costs is worth pinning anyway - the loser of "??= new" stays subscribed to
    // ActiveProfileChanged forever, so every profile switch reloads twice, and because "??= new"
    // hands each racing caller the instance IT built, App.xaml.cs's BaseFontSizeChanged handler
    // can end up wired to one nothing else writes to.
    // Reading the field does not construct the service: Lazy is what makes that true.
    [Fact]
    public void The_settings_singleton_is_built_exactly_once()
    {
        var field = typeof(SettingsService).GetField(
            "_instance", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(field != null, "SettingsService has no private static field '_instance'");
        Assert.Equal(typeof(Lazy<SettingsService>), field!.FieldType);

        // The field TYPE is only half the guarantee: Lazy<T> serializes the factory in one mode
        // only. Under PublicationOnly it runs the factory on every racing thread and keeps the
        // first result, and under None a concurrent first access is undefined outright - both
        // leave the losers' constructors having run LoadSettings and subscribed to
        // ActiveProfileChanged, so every later switch pays a redundant SQLite load per zombie.
        // Neither mode changes the field type, so the mode has to be asserted separately.
        Assert.Contains(
            "LazyThreadSafetyMode.ExecutionAndPublication", SettingsFieldInitializer("_instance"));
    }

    /// <summary>
    /// The initializer expression of a field declared in SettingsService.cs: everything between
    /// its "=" and the ";" that ends the declaration. Read from the source tree, the way
    /// <c>ProfileAttributionSourceTests</c> reads it, because the mode is only observable at
    /// runtime while the value is still uncreated - which nothing can promise about a
    /// process-wide singleton once any test has touched it.
    /// </summary>
    private static string SettingsFieldInitializer(string fieldName)
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepo.Root(), "TarkovHelper", "Services", "SettingsService.cs"));

        // First match, so the declaration wins over any later assignment of the same field.
        var match = Regex.Match(
            source,
            $@"\b{Regex.Escape(fieldName)}\s*=\s*(?<initializer>[^;]*);",
            RegexOptions.Singleline);
        Assert.True(match.Success, $"SettingsService.cs declares no initialized field '{fieldName}'");
        return match.Groups["initializer"].Value;
    }

    #endregion

    #region Storage format

    /// <summary>
    /// Runs <paramref name="body"/> under <paramref name="cultureName"/> and restores the
    /// thread's culture afterwards, so a stored decimal separator cannot leak into another test.
    /// Nothing is awaited inside, deliberately: the culture is what is being pinned.
    /// </summary>
    private static T UnderCulture<T>(string cultureName, Func<T> body)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // Scav rep is the one double among the eight profile-scoped values, and it used to be
    // written and read in whatever culture the machine ran in. A machine whose decimal
    // separator is a comma reads "5.5" back as 55.0 with the default NumberStyles, which is
    // nine times MaxScavRep and reaches Fence karma quest filtering unchallenged.
    [Fact]
    public async Task A_stored_fractional_scav_rep_keeps_its_value_under_a_comma_decimal_culture()
    {
        var store = NewStore();
        await store.SetProfileSettingAsync(IdOf(AppProfile.PveZone), "app.scavRep", "5.5");
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 1, store);

        var reloaded = UnderCulture("de-DE", () =>
        {
            service.ReloadForProfile(AppProfile.PveZone, revision: 1);
            return service.ScavRep;
        });

        Assert.Equal(5.5, reloaded);
    }

    [Fact]
    public async Task A_fractional_scav_rep_round_trips_through_a_comma_decimal_culture()
    {
        var store = NewStore();
        var target = IdOf(AppProfile.PveZone);
        var service = NewService(Seeded(target), latestRevision: 1, store);

        var reloaded = UnderCulture("de-DE", () =>
        {
            service.ScavRep = -2.5;
            service.ReloadForProfile(AppProfile.PveZone, revision: 1);
            return service.ScavRep;
        });

        Assert.Equal(-2.5, reloaded);
        // Written in the invariant format whatever the machine's separator is, so the row is
        // portable: config migration carries these rows between installs.
        Assert.Equal("-2.5", await store.GetProfileSettingAsync(target, "app.scavRep"));
    }

    // The other direction, which is why the read is tolerant rather than invariant-only: a row
    // an older build wrote under this same comma-decimal locale still loads.
    [Fact]
    public async Task A_legacy_comma_decimal_scav_rep_row_still_loads()
    {
        var store = NewStore();
        await store.SetProfileSettingAsync(IdOf(AppProfile.PveZone), "app.scavRep", "-2,5");
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 1, store);

        var reloaded = UnderCulture("de-DE", () =>
        {
            service.ReloadForProfile(AppProfile.PveZone, revision: 1);
            return service.ScavRep;
        });

        Assert.Equal(-2.5, reloaded);
    }

    // A row the app cannot vouch for (a hand edit, or one of the mis-parsed values an older
    // build could store) is clamped on the way in rather than handed to quest filtering as it
    // stands. The setter has always clamped; the load had not.
    [Fact]
    public async Task An_out_of_range_stored_scav_rep_is_clamped_on_the_way_in()
    {
        var store = NewStore();
        await store.SetProfileSettingAsync(IdOf(AppProfile.PveZone), "app.scavRep", "55");
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 1, store);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        Assert.Equal(SettingsService.MaxScavRep, service.ScavRep);
    }

    // The base font size has the same shape on a global key, and a mis-read one is worse: it
    // goes straight into Resources["BaseFontSize"], so 185 renders every control at 185 px.
    [Fact]
    public void A_stored_fractional_base_font_size_keeps_its_value_under_a_comma_decimal_culture()
    {
        var store = NewStore();
        store.SetSetting("app.baseFontSize", "18.5");
        var service = NewService(Seeded(IdOf(AppProfile.PvpZone)), latestRevision: 0, store);
        // Force the real load, which is the only reader of this key.
        TestReflection.SetPrivateField(service, "_settingsLoaded", false);

        var loaded = UnderCulture("de-DE", () => WithTempConfigPath(() => service.BaseFontSize));

        Assert.Equal(18.5, loaded);
    }

    [Fact]
    public void A_base_font_size_is_written_in_the_invariant_format()
    {
        var store = NewStore();
        var service = NewService(Seeded(IdOf(AppProfile.PvpZone)), latestRevision: 0, store);

        UnderCulture("de-DE", () => service.BaseFontSize = 18.5);

        Assert.Equal("18.5", store.GetSetting("app.baseFontSize"));
    }

    // A stored size no setter could have produced cannot make the window unusable either.
    [Fact]
    public void An_out_of_range_stored_base_font_size_is_clamped_on_the_way_in()
    {
        var store = NewStore();
        store.SetSetting("app.baseFontSize", "185");
        var service = NewService(Seeded(IdOf(AppProfile.PvpZone)), latestRevision: 0, store);
        TestReflection.SetPrivateField(service, "_settingsLoaded", false);

        var loaded = WithTempConfigPath(() => service.BaseFontSize);

        Assert.Equal(SettingsService.MaxFontSize, loaded);
    }

    /// <summary>
    /// Runs <paramref name="body"/> with <see cref="AppEnv.ConfigPath"/> pointed at an empty
    /// temp folder. <c>LoadSettings</c> imports and then DELETES a legacy app_settings.json
    /// under that path, which must never be the build output's own Config folder.
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

    // The ProfileSettings table has no COLLATE NOCASE, so (ProfileId, Key) admits both spellings
    // as separate rows and the app's own key is the only one that counts. A case-insensitive
    // read would let a hand-edited row take over, with row order deciding which one wins.
    [Fact]
    public async Task A_row_whose_key_differs_only_in_case_is_not_the_setting()
    {
        var store = NewStore();
        var target = IdOf(AppProfile.PveZone);
        await store.SetProfileSettingAsync(target, "app.PlayerLevel", "99");
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 1, store);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        Assert.Null(service.ProfileSettings.PlayerLevel);
        Assert.Equal(SettingsService.DefaultPlayerLevel, service.PlayerLevel);
    }

    #endregion

    #region The failure publish and its self-heal

    [Fact]
    public async Task A_failed_load_publishes_the_new_profiles_defaults_and_heals_on_a_re_confirmation()
    {
        // No store, so the bulk read throws. The catch must publish the NEW profile's defaults:
        // leaving the season profile's level 42 and its editions standing under the PvE name is
        // the defect with better manners (PRD R5).
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 0);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        Assert.Equal(IdOf(AppProfile.PveZone), service.ProfileSettings.ProfileId);
        Assert.Equal(SettingsService.DefaultPlayerLevel, service.PlayerLevel);
        Assert.Equal(SettingsService.DefaultScavRep, service.ScavRep);
        Assert.Null(service.PlayerFaction);
        Assert.False(service.HasEodEdition);

        // The store comes back. A provenance-only re-confirmation (the same profile, now backed
        // by log evidence instead of a click) reloads instead of skipping, because the last load
        // failed. It repairs the failure it can reach: no such event follows a failure during an
        // automatic switch, which stays on defaults until the player picks a profile by hand.
        var store = NewStore();
        await store.SetProfileSettingAsync(IdOf(AppProfile.PveZone), "app.playerLevel", "7");
        TestReflection.SetPrivateField(service, "_userDataDb", store);

        RaiseProfileChanged(service, AppProfile.PveZone, revision: 2, profileChanged: false);

        Assert.Equal(7, service.PlayerLevel);
    }

    // "The store answered and this profile owns no rows" and "the store could not be read" both
    // publish an all-null snapshot, so the only thing telling them apart is the failure flag -
    // and it decides whether a later re-confirmation reloads. A profile the player has never
    // configured is the common case of the first, and mistaking it for a failure would make
    // every provenance flip re-read it and re-raise seven events for rows that are not there.
    [Fact]
    public async Task An_empty_but_successful_load_is_not_a_failed_load()
    {
        // A real store with a row in it, so "no values" below can only mean the target profile
        // owns none - not that the store was unreadable, which is the state under test.
        var store = NewStore();
        await store.SetProfileSettingAsync(IdOf(AppProfile.PvpSeason), "app.playerLevel", "9");
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 0, store);

        service.ReloadForProfile(AppProfile.PveZone, revision: 1);

        // Nothing of the season profile survived: the empty result replaced the seed whole.
        var snapshot = service.ProfileSettings;
        Assert.Equal(IdOf(AppProfile.PveZone), snapshot.ProfileId);
        Assert.Null(snapshot.PlayerLevel);
        Assert.Null(snapshot.ScavRep);
        Assert.Null(snapshot.PrestigeLevel);

        // Recorded after the load, so these count the re-confirmation's events and not its own.
        var events = RecordEventNames(service);
        RaiseProfileChanged(service, AppProfile.PveZone, revision: 2, profileChanged: false);

        Assert.Empty(events);
    }

    // A snapshot naming a different profile than the event does is the other state a lost race
    // can leave behind, and the other reason a re-confirmation reloads anyway.
    [Fact]
    public async Task A_re_confirmation_heals_a_snapshot_left_naming_another_profile()
    {
        var store = NewStore();
        await store.SetProfileSettingAsync(IdOf(AppProfile.PveZone), "app.playerLevel", "7");
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 1, store);

        RaiseProfileChanged(service, AppProfile.PveZone, revision: 2, profileChanged: false);

        Assert.Equal(IdOf(AppProfile.PveZone), service.ProfileSettings.ProfileId);
        Assert.Equal(7, service.PlayerLevel);
    }

    // ...and the case that makes the two above a guard rather than an unconditional reload: a
    // re-confirmation of the profile already loaded, from a load that did not fail, is not worth
    // re-reading identical rows and refreshing three pages for. It arrives once per provenance
    // flip (ProfileService drops identical evidence), so the saving is small, but so is the
    // guard.
    [Fact]
    public async Task A_re_confirmation_that_needs_no_healing_does_not_reload()
    {
        var store = NewStore();
        // A row that a reload WOULD surface, so a skipped reload is the only explanation for
        // the seeded value still standing below.
        await store.SetProfileSettingAsync(IdOf(AppProfile.PveZone), "app.playerLevel", "7");
        var service = NewService(Seeded(IdOf(AppProfile.PveZone)), latestRevision: 1, store);
        var events = RecordEventNames(service);

        RaiseProfileChanged(service, AppProfile.PveZone, revision: 2, profileChanged: false);

        Assert.Equal(42, service.PlayerLevel);
        Assert.Empty(events);
    }

    // A real destination change always reloads, re-confirmation logic notwithstanding.
    [Fact]
    public async Task A_profile_change_always_reloads()
    {
        var store = NewStore();
        await store.SetProfileSettingAsync(IdOf(AppProfile.PveZone), "app.playerLevel", "7");
        var service = NewService(Seeded(IdOf(AppProfile.PvpSeason)), latestRevision: 1, store);
        var events = RecordEventNames(service);

        RaiseProfileChanged(service, AppProfile.PveZone, revision: 2, profileChanged: true);

        Assert.Equal(IdOf(AppProfile.PveZone), service.ProfileSettings.ProfileId);
        Assert.Equal(7, service.PlayerLevel);
        Assert.Equal(AllChangedEvents, events);
    }

    #endregion
}
