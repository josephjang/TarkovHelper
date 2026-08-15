using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards for where legacy <c>app_settings.json</c> values land. The file predates profiles, so
/// every value in it belongs to PvP, exactly like the quest, hideout and inventory migrations in
/// the same flow (which all pass <c>ProfileService.PvpProfileId</c> explicitly) and like
/// <c>SettingsService.MigrateFromJsonIfNeeded</c>.
/// <para>
/// The trap these pin: the three profile-scoped values used to be imported through the
/// <c>SettingsService</c> property setters, and those setters now persist under the profile the
/// live settings snapshot names (docs/decisions/fix-profile-settings-race.spec.md). Pressing
/// "Data Migration" with PvE or PvP Season loaded therefore filed the imported level, Fence karma
/// and DSP count under that profile while the rest of the same import went to PvP.
/// </para>
/// <para>
/// Real SQLite through the internal path-taking constructor, one temp file per test, the way
/// <c>ProfileResetStoreTests</c> does it: the assertion is about which partition a row is in, and
/// only the store can answer that.
/// </para>
/// </summary>
public sealed class ConfigMigrationProfileAttributionTests : IDisposable
{
    private readonly TempStoreRoot _stores = new("migration");

    public void Dispose() => _stores.Dispose();

    private UserDataDbService NewStore() => _stores.NewStore();

    /// <summary>
    /// A store every write throws through: the database path is an existing DIRECTORY, which
    /// SQLite cannot open as a file. Stands in for the failure this path really meets, a
    /// user_data.db held by a log sync or a profile reset.
    /// </summary>
    private UserDataDbService NewUnwritableStore()
        => new(_stores.NewFolder("unwritable"));

    /// <summary>Writes an app_settings.json into a fresh Config folder and returns its path.</summary>
    private string ConfigFolderWith(string json)
    {
        var folder = _stores.NewFolder("config");
        File.WriteAllText(Path.Combine(folder, "app_settings.json"), json);
        return folder;
    }

    /// <summary>The imported PvP rows read back the way SettingsService reads them.</summary>
    private static ProfileSettingsSnapshot PvpSnapshot(UserDataDbService store)
        => ProfileSettingsSnapshot.From(
            ProfileService.PvpProfileId, 1, store.LoadProfileSettings(ProfileService.PvpProfileId));

    [Fact]
    public async Task Profile_scoped_settings_land_in_the_pvp_partition()
    {
        var store = NewStore();
        var folder = ConfigFolderWith(
            """{"playerLevel": 42, "scavRep": 2.5, "dspDecodeCount": 2}""");

        var (count, profileWrittenTo, error) =
            await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Null(error);
        Assert.Equal(3, count);
        Assert.Equal(ProfileService.PvpProfileId, profileWrittenTo);

        Assert.Equal("42", await store.GetProfileSettingAsync(ProfileService.PvpProfileId, SettingsService.KeyPlayerLevel));
        Assert.Equal("2.5", await store.GetProfileSettingAsync(ProfileService.PvpProfileId, SettingsService.KeyScavRep));
        Assert.Equal("2", await store.GetProfileSettingAsync(ProfileService.PvpProfileId, SettingsService.KeyDspDecodeCount));
    }

    [Fact]
    public async Task No_other_profile_receives_a_row()
    {
        var store = NewStore();
        var folder = ConfigFolderWith(
            """{"playerLevel": 42, "scavRep": 2.5, "dspDecodeCount": 2}""");

        await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        // The whole point of the fix: the destination is the legacy data's own profile, not
        // whichever profile happened to be loaded when the button was pressed.
        foreach (var other in new[] { ProfileService.PveProfileId, ProfileService.SeasonProfileId })
        {
            Assert.Empty(store.LoadProfileSettings(other));
        }
    }

    [Fact]
    public async Task Out_of_range_values_are_clamped_the_way_the_property_setters_clamped_them()
    {
        var store = NewStore();
        var folder = ConfigFolderWith(
            """{"playerLevel": 999, "scavRep": 12.75, "dspDecodeCount": -3}""");

        await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Equal(
            SettingsService.MaxPlayerLevel.ToString(CultureInfo.InvariantCulture),
            await store.GetProfileSettingAsync(ProfileService.PvpProfileId, SettingsService.KeyPlayerLevel));
        Assert.Equal("6", await store.GetProfileSettingAsync(ProfileService.PvpProfileId, SettingsService.KeyScavRep));
        Assert.Equal(
            SettingsService.MinDspDecodeCount.ToString(CultureInfo.InvariantCulture),
            await store.GetProfileSettingAsync(ProfileService.PvpProfileId, SettingsService.KeyDspDecodeCount));
    }

    [Fact]
    public async Task Scav_rep_is_rounded_to_one_decimal_like_the_setter()
    {
        var store = NewStore();
        var folder = ConfigFolderWith("""{"scavRep": 2.349}""");

        await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Equal("2.3", await store.GetProfileSettingAsync(ProfileService.PvpProfileId, SettingsService.KeyScavRep));
    }

    [Fact]
    public async Task Negative_scav_rep_survives_the_round_trip()
    {
        var store = NewStore();
        var folder = ConfigFolderWith("""{"scavRep": -5.5}""");

        await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Equal("-5.5", await store.GetProfileSettingAsync(ProfileService.PvpProfileId, SettingsService.KeyScavRep));
    }

    [Fact]
    public async Task Scav_rep_is_written_in_the_invariant_format_under_a_comma_decimal_culture()
    {
        var store = NewStore();
        var folder = ConfigFolderWith("""{"scavRep": 2.5}""");
        var previous = CultureInfo.CurrentCulture;

        try
        {
            // de-DE writes 2.5 as "2,5", which SettingsService.SnapshotOf would then read back
            // as 25 on an en-US machine (or not at all).
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        Assert.Equal("2.5", await store.GetProfileSettingAsync(ProfileService.PvpProfileId, SettingsService.KeyScavRep));
    }

    [Fact]
    public async Task A_config_folder_without_app_settings_writes_nothing()
    {
        var store = NewStore();
        var folder = _stores.NewFolder("config");

        var (count, profileWrittenTo, error) =
            await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Null(error);
        Assert.Equal(0, count);
        Assert.Null(profileWrittenTo);
        Assert.Empty(store.LoadProfileSettings(ProfileService.PvpProfileId));
    }

    [Fact]
    public async Task Null_and_absent_profile_settings_write_nothing()
    {
        var store = NewStore();
        var folder = ConfigFolderWith("""{"playerLevel": null}""");

        var (count, profileWrittenTo, error) =
            await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Null(error);
        Assert.Equal(0, count);
        Assert.Null(profileWrittenTo);
        Assert.Empty(store.LoadProfileSettings(ProfileService.PvpProfileId));
    }

    [Fact]
    public async Task Malformed_json_is_reported_as_a_warning_rather_than_thrown()
    {
        var store = NewStore();
        var folder = ConfigFolderWith("{ not json");

        var (count, profileWrittenTo, error) =
            await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.NotNull(error);
        Assert.Equal(0, count);
        Assert.Null(profileWrittenTo);
    }

    // The faction was simply not read by this importer, while the sibling reader
    // (SettingsService.MigrateFromJsonIfNeeded) did read it. A Config folder brought in through
    // the "Data Migration" button therefore lost it, and ShouldIncludeTask admits every task when
    // the faction is unset, so the quest list showed BEAR and USEC quests at once.
    [Fact]
    public async Task The_player_faction_is_imported_and_normalised_to_lower_case()
    {
        var store = NewStore();
        var folder = ConfigFolderWith("""{"playerFaction": "USEC"}""");

        var (count, profileWrittenTo, error) =
            await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Null(error);
        Assert.Equal(1, count);
        Assert.Equal(ProfileService.PvpProfileId, profileWrittenTo);
        Assert.Equal("usec", await store.GetProfileSettingAsync(
            ProfileService.PvpProfileId, SettingsService.KeyPlayerFaction));
        // Read back the way the settings service reads it, so the stored spelling is pinned
        // against the parser that has to accept it and not just against itself.
        Assert.Equal("usec", PvpSnapshot(store).PlayerFaction);
    }

    [Fact]
    public async Task An_empty_player_faction_writes_no_row()
    {
        var store = NewStore();
        var folder = ConfigFolderWith("""{"playerFaction": ""}""");

        var (count, profileWrittenTo, error) =
            await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Null(error);
        Assert.Equal(0, count);
        Assert.Null(profileWrittenTo);
        Assert.Empty(store.LoadProfileSettings(ProfileService.PvpProfileId));
    }

    [Fact]
    public async Task Level_locked_quest_visibility_is_imported()
    {
        var store = NewStore();
        // false, not true: true is also the default, so importing nothing would look identical.
        var folder = ConfigFolderWith("""{"showLevelLockedQuests": false}""");

        var (count, profileWrittenTo, error) =
            await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Null(error);
        Assert.Equal(1, count);
        Assert.Equal(ProfileService.PvpProfileId, profileWrittenTo);
        Assert.False(PvpSnapshot(store).ShowLevelLockedQuestsOrDefault);
    }

    // Every profile-scoped value in one file, and the rows checked against the list
    // SettingsService publishes: a key this importer misspells lands in a partition nothing ever
    // reads, which no per value assertion above would notice.
    [Fact]
    public async Task Every_imported_profile_row_lands_under_a_key_settings_service_reads()
    {
        var store = NewStore();
        var folder = ConfigFolderWith(
            """
            {
              "playerLevel": 42,
              "scavRep": 2.5,
              "dspDecodeCount": 2,
              "showLevelLockedQuests": false,
              "playerFaction": "bear"
            }
            """);

        var (count, profileWrittenTo, error) =
            await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Null(error);
        Assert.Equal(5, count);
        Assert.Equal(ProfileService.PvpProfileId, profileWrittenTo);

        var rows = store.LoadProfileSettings(ProfileService.PvpProfileId);
        Assert.Equal(5, rows.Count);
        foreach (var key in rows.Keys)
        {
            Assert.Contains(key, SettingsService.ProfileSpecificKeys);
        }

        var snapshot = PvpSnapshot(store);
        Assert.Equal(42, snapshot.PlayerLevelOrDefault);
        Assert.Equal(2.5, snapshot.ScavRepOrDefault, 3);
        Assert.Equal(2, snapshot.DspDecodeCountOrDefault);
        Assert.False(snapshot.ShowLevelLockedQuestsOrDefault);
        Assert.Equal("bear", snapshot.PlayerFaction);
    }

    // A JSON number where a string belongs used to reach JsonElement.GetString(), which throws:
    // the whole import then reported zero even though the rows before it were already committed,
    // and the auto migration path leaves the file in place when the total is zero, so the same
    // file re-triggered the migration overlay on every launch.
    [Fact]
    public async Task A_badly_typed_value_costs_only_itself()
    {
        var store = NewStore();
        var folder = ConfigFolderWith(
            """{"playerLevel": 42, "logFolderPath": 12345, "dspDecodeCount": 2}""");

        var (count, profileWrittenTo, error) =
            await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Null(error);
        Assert.Equal(2, count);
        Assert.Equal(ProfileService.PvpProfileId, profileWrittenTo);
        Assert.Equal("42", await store.GetProfileSettingAsync(
            ProfileService.PvpProfileId, SettingsService.KeyPlayerLevel));
        Assert.Equal("2", await store.GetProfileSettingAsync(
            ProfileService.PvpProfileId, SettingsService.KeyDspDecodeCount));
    }

    // One unwritable value must cost only that value. The property setters this path replaced
    // persisted through SaveProfileSetting, which logs and swallows, so the loop always continued;
    // writing to the store directly without a guard let the first SqliteException abort the rest.
    [Fact]
    public async Task A_store_that_refuses_a_write_still_attempts_every_other_value()
    {
        var store = NewUnwritableStore();
        var folder = ConfigFolderWith(
            """{"playerLevel": 42, "scavRep": 2.5, "dspDecodeCount": 2}""");

        var (count, profileWrittenTo, error) =
            await ConfigMigrationService.Instance.MigrateAppSettingsAsync(folder, store);

        Assert.Equal(0, count);
        Assert.Null(profileWrittenTo);

        // All three named, which is only possible if the loop ran past the first failure.
        Assert.NotNull(error);
        foreach (var name in new[] { "playerLevel", "scavRep", "dspDecodeCount" })
        {
            Assert.Contains(name, error);
        }
    }

    // MigrateFromConfigFolderAsync has no test caller: both call sites are production
    // (ConfigMigrationService.MigrateFromCurrentConfigAsync and MainWindow's "Data Migration"
    // button), and every test here calls the inner MigrateAppSettingsAsync. Without this the
    // refreshes could be deleted with the suite still green, and the settings panel and the items
    // page would keep rendering the pre-import values until the next profile switch - one nudge
    // of a spinner then writes the stale value back over the just-imported row.
    [Fact]
    public void The_import_refreshes_each_cache_whose_rows_it_wrote_behind()
    {
        var body = MemberSource("TarkovHelper/Services/ConfigMigrationService.cs", "MigrateFromConfigFolderAsync");

        // Each refresh names the partition its own write step REPORTED writing, so the two can
        // never disagree. A second literal ProfileService.PvpProfileId here would have to be kept
        // in step with the writes by discipline alone, which is what this shape removes.
        Assert.Matches(
            new Regex(@"SettingsService\.Instance\.ReloadAfterExternalWrite\(\s*settingsMigrationResult\.profileWrittenTo\s*\)"),
            body);
        Assert.Matches(
            new Regex(@"ItemInventoryService\.Instance\.ReloadAfterExternalWriteAsync\(\s*inventoryMigrationResult\.profileWrittenTo\s*\)"),
            body);
        Assert.Contains("settingsMigrationResult.profileWrittenTo != null", body);
        Assert.Contains("inventoryMigrationResult.profileWrittenTo != null", body);

        // Not the reset contract's name: this import is not a reset, and SettingsService now
        // spells the "your rows changed underneath you" behaviour as what it is.
        Assert.DoesNotContain("SettingsService.Instance.HandleProfileReset(", body);
        Assert.DoesNotContain("ReloadAfterExternalWrite(ProfileService.PvpProfileId)", body);
    }

    // The order is the whole fix. ItemInventoryService persists ABSOLUTE cached quantities, so a
    // debounced save staged before the import and flushed after it writes the pre-import number
    // straight back over the imported row.
    [Fact]
    public void The_inventory_import_flushes_pending_saves_before_its_first_write()
    {
        var body = MemberSource("TarkovHelper/Services/ConfigMigrationService.cs", "MigrateItemInventoryAsync");

        var flush = body.IndexOf("FlushPendingSavesAsync()", StringComparison.Ordinal);
        var write = body.IndexOf("SaveItemInventoryAsync(", StringComparison.Ordinal);

        Assert.True(flush >= 0, "the inventory import no longer flushes the debounced saves first");
        Assert.True(write >= 0, "the inventory import no longer writes any row; this scan is stale");
        Assert.True(
            flush < write,
            "the debounced flush must run before the first imported row is written, or it " +
            "overwrites that row with the quantity the import replaced");
    }

    #region The two legacy readers agree

    /// <summary>
    /// Drives <c>SettingsService.MigrateFromJson</c>, the STARTUP reader of app_settings.json,
    /// against <paramref name="store"/>. Built uninitialized (see <see cref="TestReflection"/>)
    /// because the real constructor loads every setting off the app's own user_data.db and
    /// subscribes the process to <c>ProfileService</c>; the reader itself touches nothing but the
    /// store field seeded here.
    /// </summary>
    private static void RunStartupReader(UserDataDbService store, string configFolder)
    {
        var service = TestReflection.Uninitialized<SettingsService>();
        TestReflection.SetPrivateField(service, "_userDataDb", store);
        service.MigrateFromJson(Path.Combine(configFolder, "app_settings.json"));
    }

    // The anti-drift guard the per-key coverage test cannot be: that one proves both readers KNOW
    // every key, and both did know these - they just turned them into different rows. The startup
    // reader stored the faction in the file's own casing (so QuestListPage's ordinal comparison
    // selected neither radio button while quest filtering still hid the other faction's quests)
    // and clamped the scav rep without rounding it, writing a value no setter could produce.
    [Fact]
    public async Task Both_legacy_readers_turn_one_file_into_the_same_profile_rows()
    {
        // Every profile-scoped key, with values chosen so each transform is exercised: an
        // out-of-range level, a scav rep that needs rounding as well as clamping, and a faction
        // in the casing the legacy file actually used.
        const string json =
            """
            {
              "playerLevel": 999,
              "scavRep": 2.37,
              "dspDecodeCount": 2,
              "showLevelLockedQuests": false,
              "playerFaction": "USEC"
            }
            """;

        var fromConfigFolder = NewStore();
        await ConfigMigrationService.Instance.MigrateAppSettingsAsync(ConfigFolderWith(json), fromConfigFolder);

        var fromStartup = NewStore();
        RunStartupReader(fromStartup, ConfigFolderWith(json));

        var configRows = fromConfigFolder.LoadProfileSettings(ProfileService.PvpProfileId);
        var startupRows = fromStartup.LoadProfileSettings(ProfileService.PvpProfileId);

        // Guards the comparison itself: two empty row sets are trivially equal.
        Assert.Equal(5, startupRows.Count);
        Assert.Equal(
            configRows.OrderBy(row => row.Key, StringComparer.Ordinal),
            startupRows.OrderBy(row => row.Key, StringComparer.Ordinal));
    }

    // The startup reader wrote this value through with no bounds at all, and nothing re-clamped
    // it on read either, so a hand-edited file could set the log look-back to any int - a window
    // the settings panel's own control cannot represent and LogSyncService then scans.
    [Fact]
    public void The_startup_reader_clamps_the_sync_day_range()
    {
        var store = NewStore();

        RunStartupReader(store, ConfigFolderWith("""{"syncDaysRange": 900}"""));

        Assert.Equal(
            SettingsService.MaxSyncDaysRange.ToString(CultureInfo.InvariantCulture),
            store.GetSetting("app.syncDaysRange"));
    }

    // Read-path repair for the rows the startup reader already wrote upper-case in released
    // builds: they are in players' databases now, and only the read can fix them.
    [Fact]
    public async Task A_faction_row_stored_in_the_files_own_casing_reads_back_lower_case()
    {
        var store = NewStore();
        await store.SetProfileSettingAsync(
            ProfileService.PvpProfileId, SettingsService.KeyPlayerFaction, "USEC");

        Assert.Equal("usec", PvpSnapshot(store).PlayerFaction);
    }

    #endregion

    // The two legacy readers are the defect's root: SettingsService.MigrateFromJsonIfNeeded reads
    // app_settings.json on startup, this one reads it out of an imported Config folder, and a key
    // known to only one of them is silently dropped on the other's path. LegacyAppSettings is the
    // published shape of the file, so every property on it must have an arm here.
    [Fact]
    public void Every_key_the_startup_reader_knows_has_an_arm_in_this_reader()
    {
        var settingsSource = File.ReadAllLines(Path.Combine(
            TestRepo.Root(), "TarkovHelper", "Services", "SettingsService.cs"));
        var legacyKeys = LegacyAppSettingsKeys(settingsSource);

        // Guards the scan itself: an empty or tiny list would make the loop below vacuous.
        Assert.True(legacyKeys.Count >= 9, $"only found {legacyKeys.Count} legacy keys: {string.Join(", ", legacyKeys)}");

        var importer = MemberSource("TarkovHelper/Services/ConfigMigrationService.cs", "MigrateAppSettingsAsync");
        foreach (var key in legacyKeys)
        {
            Assert.Contains($"case \"{key}\":", importer);
        }
    }

    /// <summary>
    /// The JSON property names <c>SettingsService.LegacyAppSettings</c> declares, camel-cased the
    /// way its <c>JsonNamingPolicy.CamelCase</c> deserializer spells them.
    /// </summary>
    private static List<string> LegacyAppSettingsKeys(string[] lines)
    {
        var property = new Regex(@"^\s*public\s+[\w.?<>]+\s+(\w+)\s*\{\s*get;\s*set;\s*\}");
        var keys = new List<string>();
        var inside = false;

        foreach (var line in lines)
        {
            if (!inside)
            {
                inside = line.Contains("class LegacyAppSettings", StringComparison.Ordinal);
                continue;
            }

            var match = property.Match(line);
            if (match.Success)
            {
                var name = match.Groups[1].Value;
                keys.Add(char.ToLowerInvariant(name[0]) + name[1..]);
                continue;
            }

            // The class's own closing brace, at its indentation: nothing else is declared here.
            if (line.TrimEnd() == "    }") break;
        }

        return keys;
    }

    /// <summary>
    /// The source lines of one member of <paramref name="relativePath"/>, joined. Uses
    /// <see cref="ProfileAttributionSourceTests.EnclosingMember"/>, the scan that file already
    /// pins with its own unit test.
    /// </summary>
    private static string MemberSource(string relativePath, string member)
    {
        var lines = File.ReadAllLines(Path.Combine(
            TestRepo.Root(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var body = lines
            .Select((line, index) => (line, index))
            .Where(entry => ProfileAttributionSourceTests.EnclosingMember(lines, entry.index) == member)
            .Select(entry => entry.line)
            .ToArray();

        Assert.True(body.Length > 0, $"no member named '{member}' was found in {relativePath}");
        return string.Join("\n", body);
    }
}
