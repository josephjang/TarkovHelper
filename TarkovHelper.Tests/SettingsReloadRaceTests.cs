using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// The settings half of the transition guard (fix-profile-settings-race.spec.md).
/// <see cref="SettingsService"/> was the last <c>ActiveProfileChanged</c> subscriber without one:
/// it refilled eight nullable fields through eight separate ambient-selection reads, so a switch
/// landing mid-reload tore the cache across two profiles, and two switches in flight could leave
/// the older one's values published last. The eight values are now one immutable
/// <see cref="ProfileSettingsSnapshot"/> carrying its profile and its transition revision.
/// <para>
/// Most cases here are built on an uninitialized service (see <see cref="TestReflection"/>) so no
/// singleton constructor runs and no user_data.db is touched. Where no store is seeded, the field
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
public sealed class SettingsReloadRaceTests : IDisposable
{
    private static string IdOf(AppProfile profile) => ProfileService.GetProfileId(profile);

    /// <summary>Temp home for the real-SQLite stores the load and write cases need.</summary>
    private readonly string _storeRoot = Path.Combine(
        Path.GetTempPath(), "tarkovhelper-settings-race-" + Guid.NewGuid().ToString("N"));

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

    /// <summary>
    /// A snapshot whose every value differs from every default, so a value still standing after
    /// a reload can only have come from this seed and not from a coincidence.
    /// </summary>
    private static ProfileSettingsSnapshot Seeded(string profileId, long revision = 0) => new(
        profileId,
        revision,
        PlayerLevel: 42,
        ScavRep: 5.5,
        ShowLevelLockedQuests: false,
        DspDecodeCount: 3,
        PlayerFaction: "bear",
        HasEodEdition: true,
        HasUnheardEdition: true,
        PrestigeLevel: 4);

    private static SettingsService NewService(
        ProfileSettingsSnapshot snapshot, long latestRevision, UserDataDbService? store = null)
    {
        var service = TestReflection.Uninitialized<SettingsService>();
        TestReflection.SetPrivateField(service, "_settingsLoaded", true);
        TestReflection.SetPrivateField(service, "_profileSettings", snapshot);
        TestReflection.SetPrivateField(service, "_latestRevision", latestRevision);
        if (store != null) TestReflection.SetPrivateField(service, "_userDataDb", store);
        return service;
    }

    /// <summary>Records every profile-scoped changed event in the order it is raised.</summary>
    private static List<string> RecordEvents(SettingsService service)
    {
        var events = new List<string>();
        service.PlayerLevelChanged += (_, _) => events.Add("PlayerLevel");
        service.ScavRepChanged += (_, _) => events.Add("ScavRep");
        service.DspDecodeCountChanged += (_, _) => events.Add("DspDecodeCount");
        service.PlayerFactionChanged += (_, _) => events.Add("PlayerFaction");
        service.HasEodEditionChanged += (_, _) => events.Add("HasEodEdition");
        service.HasUnheardEditionChanged += (_, _) => events.Add("HasUnheardEdition");
        service.PrestigeLevelChanged += (_, _) => events.Add("PrestigeLevel");
        return events;
    }

    /// <summary>The seven events a published reload raises, in the order the reset contract pins.</summary>
    private static readonly string[] AllChangedEvents =
    {
        "PlayerLevel", "ScavRep", "DspDecodeCount", "PlayerFaction",
        "HasEodEdition", "HasUnheardEdition", "PrestigeLevel",
    };

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
        var events = RecordEvents(service);

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
        var events = RecordEvents(service);

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
        // A whole number, deliberately: SettingsService still parses this key in the current
        // culture, so a fractional literal here would make the test depend on the runner's
        // decimal separator rather than on the reload.
        await store.SetProfileSettingAsync(target, "app.scavRep", "-2");
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
        Assert.Equal(-2.0, snapshot.ScavRep);
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
        var onScreen = "onscreen-" + Guid.NewGuid().ToString("N");
        var service = NewService(Seeded(onScreen), latestRevision: 0, store);
        var events = RecordEvents(service);

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
    public void Re_setting_the_current_value_publishes_nothing_and_raises_nothing()
    {
        var store = NewStore();
        var onScreen = "onscreen-" + Guid.NewGuid().ToString("N");
        var service = NewService(Seeded(onScreen), latestRevision: 0, store);
        var before = service.ProfileSettings;
        var events = RecordEvents(service);

        service.PlayerLevel = 42;
        service.ScavRep = 5.5;
        service.PlayerFaction = "bear";
        service.HasEodEdition = true;

        Assert.Same(before, service.ProfileSettings);
        Assert.Empty(events);
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

        // The store comes back. A provenance-only re-confirmation is the one event that keeps
        // arriving on its own, so it is where the recovery has to happen: it reloads instead of
        // skipping, because the last load failed.
        var store = NewStore();
        await store.SetProfileSettingAsync(IdOf(AppProfile.PveZone), "app.playerLevel", "7");
        TestReflection.SetPrivateField(service, "_userDataDb", store);

        RaiseProfileChanged(service, AppProfile.PveZone, revision: 2, profileChanged: false);

        Assert.Equal(7, service.PlayerLevel);
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

    // ...and the case that makes the two above a guard rather than an unconditional reload: EFT
    // re-logs the session mode on every profile-screen visit, and a re-confirmation of the
    // profile already loaded, from a load that did not fail, is not worth re-reading identical
    // rows and refreshing three pages for.
    [Fact]
    public async Task A_re_confirmation_that_needs_no_healing_does_not_reload()
    {
        var store = NewStore();
        // A row that a reload WOULD surface, so a skipped reload is the only explanation for
        // the seeded value still standing below.
        await store.SetProfileSettingAsync(IdOf(AppProfile.PveZone), "app.playerLevel", "7");
        var service = NewService(Seeded(IdOf(AppProfile.PveZone)), latestRevision: 1, store);
        var events = RecordEvents(service);

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
        var events = RecordEvents(service);

        RaiseProfileChanged(service, AppProfile.PveZone, revision: 2, profileChanged: true);

        Assert.Equal(IdOf(AppProfile.PveZone), service.ProfileSettings.ProfileId);
        Assert.Equal(7, service.PlayerLevel);
        Assert.Equal(AllChangedEvents, events);
    }

    #endregion
}
