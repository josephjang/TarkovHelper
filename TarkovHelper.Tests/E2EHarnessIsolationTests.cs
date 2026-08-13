using System.IO;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Unit guards for the e2e harness's own isolation steps, run as ordinary tests: they need no
/// app launch and no interactive desktop, so they stay outside the E2E category and catch
/// harness rot on every quick run. Two steps are covered.
///
/// AppDriver.RemoveLegacyLanguageOverride deletes a leftover legacy Data\settings.json next to
/// the app under test so a stale language override cannot flip e2e text assertions to KO/JA
/// (see the helper's own doc comment for why TARKOVHELPER_CONFIG_PATH cannot isolate it).
///
/// E2EDb.CreateUserDataDb builds the pre-launch user_data.db. It used to hand-copy production
/// CREATE TABLE statements, and the app adopts a pre-created table as-is (its own DDL is
/// CREATE TABLE IF NOT EXISTS). A hand-written copy that fell behind production would therefore
/// be adopted silently and the app under test would fail on the missing column instead of the
/// e2e test failing on the real behavior. Building the file through the app's own store removes
/// that class of drift, and the assertions below pin it: RaidHistory.AppProfileId is the canary,
/// since the harness never wrote RaidHistory by hand at all.
/// </summary>
public sealed class E2EHarnessIsolationTests : IDisposable
{
    /// <summary>The production tables UserDataDbService creates, in sqlite_master name order.</summary>
    private static readonly string[] ProductionTables =
    {
        "HideoutProgress", "ItemInventory", "ObjectiveProgress", "ProfileSettings",
        "QuestProgress", "RaidHistory", "UserSettings",
    };

    /// <summary>Temp home for every directory these tests create; deleted as one tree.</summary>
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "TarkovHelperHarnessTests", Guid.NewGuid().ToString("N"));

    /// <summary>Stand-in for the app directory the legacy-override tests operate on.</summary>
    private readonly string _appDir;

    public E2EHarnessIsolationTests()
    {
        _appDir = Path.Combine(_root, "app");
        Directory.CreateDirectory(_appDir);
    }

    private string NewConfigDir()
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Releases the pooled connections holding user_data.db open so the temp tree can be
    /// deleted, matching E2ETestBase.Dispose and ProfileResetHooksTests.Dispose.
    /// </summary>
    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;

        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    #region Legacy language override

    [Fact]
    public void Deletes_a_leftover_legacy_settings_file()
    {
        var dataDir = Path.Combine(_appDir, "Data");
        Directory.CreateDirectory(dataDir);
        var legacy = Path.Combine(dataDir, "settings.json");
        File.WriteAllText(legacy, """{"language":"KO"}""");

        AppDriver.RemoveLegacyLanguageOverride(_appDir);

        Assert.False(File.Exists(legacy), "the legacy language override was not deleted");
    }

    [Fact]
    public void Is_a_no_op_when_the_file_is_already_gone()
    {
        // Data\ exists but holds no settings.json (the state right after a first
        // app launch migrated and deleted it): a second call must not throw.
        Directory.CreateDirectory(Path.Combine(_appDir, "Data"));

        AppDriver.RemoveLegacyLanguageOverride(_appDir);
        AppDriver.RemoveLegacyLanguageOverride(_appDir);
    }

    [Fact]
    public void Tolerates_a_nonexistent_app_directory()
    {
        AppDriver.RemoveLegacyLanguageOverride(Path.Combine(_appDir, "does-not-exist"));
    }

    #endregion

    #region Pre-launch user_data.db

    [Fact]
    public void CreateUserDataDb_builds_the_full_production_table_set()
    {
        var configDir = NewConfigDir();

        E2EDb.CreateUserDataDb(configDir);

        Assert.Equal(ProductionTables, TableNames(configDir));
    }

    /// <summary>
    /// The drift canary: the harness never hand-wrote RaidHistory, so this column can only
    /// come from the app's own DDL. It fails the moment CreateUserDataDb goes back to
    /// hand-copied CREATE TABLE statements.
    /// </summary>
    [Fact]
    public void CreateUserDataDb_gives_RaidHistory_the_AppProfileId_column()
    {
        var configDir = NewConfigDir();

        E2EDb.CreateUserDataDb(configDir);

        Assert.Contains("AppProfileId", ColumnNames(configDir, "RaidHistory"));
    }

    /// <summary>
    /// The seeders no longer create their own tables, so this pins their precondition: after
    /// CreateUserDataDb they insert into the production tables and read back.
    /// </summary>
    [Fact]
    public void The_seeders_write_rows_the_readers_find()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);

        E2EDb.SeedSetting(configDir, "app.language", "en");
        E2EDb.SeedQuestProgress(
            configDir, ProfileService.SeasonProfileId, "q-1", "a-quest", "Completed");
        E2EDb.SeedProfileSetting(configDir, ProfileService.SeasonProfileId, "app.playerLevel", "42");

        Assert.Equal("en", E2EDb.ReadSetting(configDir, "app.language"));
        Assert.Equal("Completed",
            E2EDb.ReadQuestProgress(configDir, ProfileService.SeasonProfileId, "a-quest"));
        Assert.Equal("42",
            E2EDb.ReadProfileSetting(configDir, ProfileService.SeasonProfileId, "app.playerLevel"));
    }

    /// <summary>
    /// A row seeded into one profile must not leak into another: the seeded partition is the
    /// precondition every reset e2e test asserts against ("cleared here, untouched there").
    /// </summary>
    [Fact]
    public void SeedQuestProgress_writes_only_the_profile_it_was_given()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);

        E2EDb.SeedQuestProgress(
            configDir, ProfileService.SeasonProfileId, "q-1", "a-quest", "Completed");

        Assert.Null(E2EDb.ReadQuestProgress(configDir, ProfileService.PveProfileId, "a-quest"));
    }

    /// <summary>
    /// Edge case the old hand-written version could not survive: opening a SqliteConnection
    /// against a path in a missing directory throws, while the store creates the directory
    /// first. Callers pass a config dir the app has never launched against.
    /// </summary>
    [Fact]
    public void CreateUserDataDb_creates_a_config_directory_that_does_not_exist_yet()
    {
        var configDir = Path.Combine(_root, Guid.NewGuid().ToString("N"), "Config");

        E2EDb.CreateUserDataDb(configDir);

        Assert.True(File.Exists(Path.Combine(configDir, "user_data.db")));
        Assert.Equal(ProductionTables, TableNames(configDir));
    }

    /// <summary>
    /// Calling it twice must be a no-op, since several e2e tests create the db and then relaunch
    /// the app against the same dir (which runs the same schema creation again).
    /// </summary>
    [Fact]
    public void CreateUserDataDb_is_idempotent_and_keeps_seeded_rows()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, "app.language", "en");

        E2EDb.CreateUserDataDb(configDir);

        Assert.Equal(ProductionTables, TableNames(configDir));
        Assert.Equal("en", E2EDb.ReadSetting(configDir, "app.language"));
    }

    #endregion

    /// <summary>
    /// The table names in the config dir's user_data.db. The NOT LIKE 'sqlite_%' filter drops
    /// sqlite_sequence, which RaidHistory's AUTOINCREMENT primary key creates.
    /// </summary>
    private static List<string> TableNames(string configDir)
        => Query(configDir,
            "SELECT name FROM sqlite_master WHERE type = 'table' " +
            "AND name NOT LIKE 'sqlite_%' ORDER BY name");

    private static List<string> ColumnNames(string configDir, string table)
        => Query(configDir, $"SELECT name FROM pragma_table_info('{table}')");

    /// <summary>
    /// Runs a single-column query. The SQL is always a literal from this file (the only
    /// interpolated value is a table name this file also owns), so nothing test-supplied
    /// reaches the statement.
    /// </summary>
    private static List<string> Query(string configDir, string sql)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(configDir, "user_data.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }
}
