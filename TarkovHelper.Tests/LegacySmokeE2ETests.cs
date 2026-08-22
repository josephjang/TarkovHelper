using System.IO;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// R9 and R4 on the build already installed in the field: the release users are running today
/// must keep working against the refreshed data, and progress it recorded before the refresh
/// must still show afterwards.
/// <para>
/// This is the only check that covers those users. Every install in the field runs v2026.7.0,
/// which predates the data channel: it polls for a database every five minutes, installs
/// whatever it downloads with no verification, and cannot be fixed after the fact. So the
/// candidate database has to be proven readable by that build before it is published, not
/// after.
/// </para>
/// <para>
/// What that build honours, so this test makes no false assumption: it reads
/// TARKOVHELPER_CONFIG_PATH and no other harness variable, its data check is neutralised by
/// leaving Assets/db_version.txt at the token currently live (so it reports up to date instead
/// of overwriting the candidate), and its app-update check is neutralised by running this
/// before update.xml is repointed. Its quest tab has no status chips, so the smoke waits on the
/// list and reads the detail status text rather than driving the chip row.
/// </para>
/// <para>
/// Requires an extracted previous-release folder and a candidate database, both named by
/// environment variables; the suite skips when either is absent, because neither exists on a
/// machine that has not cut a release.
/// </para>
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class LegacySmokeE2ETests : E2ETestBase
{
    /// <summary>Folder holding an extracted previous-release zip (the one with TarkovHelper.dll in it).</summary>
    public const string LegacyAppDirVariable = "TARKOVHELPER_LEGACY_APP_DIR";

    /// <summary>The regenerated database the publish is about to ship.</summary>
    public const string CandidateDbVariable = "TARKOVHELPER_CANDIDATE_DB";

    [LegacySmokeFact]
    public void The_fielded_build_loads_every_page_against_the_candidate_data()
    {
        var scratch = PrepareLegacyBuild(out var configDir);
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "False");

        using var app = LaunchLegacy(scratch, configDir);

        // Each tab's list control is the page's own "I loaded" signal; a page that threw would
        // never publish one.
        app.SelectTab("TabQuests", "LstQuests");
        AppDriver.PollUntil(() => app.GetListItemCount("LstQuests") > 0, "the quest list to fill");

        app.SelectTab("TabHideout", "LstHideout");
        app.SelectTab("TabItems", "LstItems");
        app.SelectTab("TabCollector", "LstCollectorQuests");
    }

    [LegacySmokeFact]
    public void A_completion_recorded_before_the_refresh_shows_against_the_renamed_quest()
    {
        var scratch = PrepareLegacyBuild(out var configDir);
        var renamed = FindCarriedRename(CandidateDatabase());

        Assert.NotNull(renamed);

        // Seeded the way v2026.7.0 writes progress: the row key it knew, and the normalized name
        // it computed from the title it knew.
        //
        // The profile column is filled with "pvp" even though that build predates profiles
        // entirely: its QuestProgress reads carry no profile filter, and the column the current
        // schema creates defaults to exactly this value, so a row written here is the row that
        // build reads. Log monitoring is off so a real EFT log on the machine cannot sync a
        // quest event over the seeded state mid-test.
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "False");
        E2EDb.SeedQuestProgress(
            configDir, ProfileService.PvpProfileId, renamed!.Id, renamed.NormalizedName, "Done");

        using var app = LaunchLegacy(scratch, configDir);

        app.SelectTab("TabQuests", "LstQuests");
        app.SetTextBoxValue("TxtSearch", renamed.Name);
        AppDriver.PollUntil(() => app.GetListItemCount("LstQuests") == 1,
            $"quest list to filter down to '{renamed.Name}'");
        app.SelectListItemAt("LstQuests", 0);

        AppDriver.PollUntil(() => app.GetElementText("TxtDetailName") == renamed.Name,
            $"detail panel to show '{renamed.Name}'");
        AppDriver.PollUntil(() => app.GetElementText("TxtDetailStatus").Contains("Done", StringComparison.OrdinalIgnoreCase)
                || app.GetElementText("TxtDetailStatus").Contains("완료", StringComparison.Ordinal),
            $"'{renamed.Name}' to show as done in the fielded build");
    }

    #region Scratch build

    /// <summary>
    /// Copies the release into a scratch folder, puts the candidate database where the app
    /// reads it, and leaves the version token alone so the build's undisableable five-minute
    /// data check reports up to date rather than downloading over the candidate.
    /// </summary>
    private string PrepareLegacyBuild(out string configDir)
    {
        var source = Environment.GetEnvironmentVariable(LegacyAppDirVariable)!;
        var scratch = Path.Combine(NewConfigDir(), "legacy-app");
        CopyDirectory(source, scratch);

        var assets = Path.Combine(scratch, "Assets");
        Directory.CreateDirectory(assets);
        File.Copy(CandidateDatabase(), Path.Combine(assets, "tarkov_data.db"), overwrite: true);

        configDir = Path.Combine(scratch, "SmokeConfig");
        Directory.CreateDirectory(configDir);
        return scratch;
    }

    private static AppDriver LaunchLegacy(string scratch, string configDir)
    {
        var dll = Path.Combine(scratch, "TarkovHelper.dll");
        Assert.True(File.Exists(dll), $"{dll} is missing; point {LegacyAppDirVariable} at an extracted release");

        var app = AppDriver.Launch(dll, configDir);
        try
        {
            app.ShowWindow(Win32.SW_MAXIMIZE);
            return app;
        }
        catch
        {
            app.Dispose();
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);

        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static string CandidateDatabase() => Environment.GetEnvironmentVariable(CandidateDbVariable)!;

    #endregion

    #region Reading the candidate

    private sealed record CarriedRename(string Id, string Name, string NormalizedName);

    /// <summary>
    /// Finds a quest in the candidate database whose stored normalized name no longer matches
    /// its title: that is a rename whose row key and progress key were carried across. The
    /// name must be a unique search substring so the quest list filters to one row.
    /// </summary>
    private static CarriedRename? FindCarriedRename(string databasePath)
    {
        var names = new List<string>();
        var carried = new List<CarriedRename>();

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var cmd = new SqliteCommand("SELECT Id, Name, NormalizedName FROM Quests", connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(1);
                names.Add(name);

                var normalizedName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                if (normalizedName.Length > 0 && normalizedName != SqlForm(name))
                    carried.Add(new CarriedRename(reader.GetString(0), name, normalizedName));
            }
        }

        SqliteConnection.ClearAllPools();

        return carried.FirstOrDefault(q =>
            names.Count(n => n.Contains(q.Name, StringComparison.OrdinalIgnoreCase)) == 1);
    }

    private static string SqlForm(string name) =>
        name.Replace(" ", "-").Replace("'", "").Replace(".", "").ToLowerInvariant();

    #endregion
}

/// <summary>
/// Skips the legacy smoke unless both an extracted previous release and a candidate database
/// are named. Neither exists on a machine that has not cut a release, and the whole point of
/// the suite is to run against real artefacts rather than a fixture.
/// </summary>
public sealed class LegacySmokeFactAttribute : FactAttribute
{
    public LegacySmokeFactAttribute()
    {
        var appDir = Environment.GetEnvironmentVariable(LegacySmokeE2ETests.LegacyAppDirVariable);
        var candidate = Environment.GetEnvironmentVariable(LegacySmokeE2ETests.CandidateDbVariable);

        if (string.IsNullOrEmpty(appDir) || !Directory.Exists(appDir))
        {
            Skip = $"Set {LegacySmokeE2ETests.LegacyAppDirVariable} to an extracted previous-release folder.";
        }
        else if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
        {
            Skip = $"Set {LegacySmokeE2ETests.CandidateDbVariable} to the candidate tarkov_data.db.";
        }
    }
}
