using System.IO;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services;
using static TarkovHelper.Tests.QuestTabDriver;

namespace TarkovHelper.Tests;

/// <summary>
/// R4 on the build under test: progress recorded before a data refresh still shows against the
/// same quest afterwards, including a quest the refresh renamed.
/// <para>
/// The mechanism is the one thing the whole 1.1 identity design rests on, and it is invisible
/// from the outside: recorded progress is keyed by the quest's normalized name, the published
/// database now carries that value in a column, and a renamed quest keeps the value its
/// original title produced rather than the one its new title would. If the column ever drifted
/// to a different spelling, nothing would break, throw, or show red - the completions would
/// simply stop appearing, which is exactly the failure this test exists to make loud.
/// </para>
/// <para>
/// The quest is chosen from the shipped database rather than named, so the test survives every
/// data update. It prefers a quest whose stored normalized name no longer matches its title,
/// which is precisely a carried-over rename; that only exists once the 1.1 data ships, so
/// before then it falls back to an ordinary quest and still exercises the same read path.
/// </para>
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class ProgressCarryOverE2ETests : E2ETestBase
{
    [E2EFact]
    public void A_completion_keyed_by_the_stored_normalized_name_shows_against_its_quest()
    {
        var quest = PickQuest();
        var configDir = NewConfigDir();

        SeedFixedProfile(configDir);
        E2EDb.SeedQuestProgress(configDir, ProfileService.PvpProfileId, quest.Id, quest.NormalizedName, "Done");

        using var app = LaunchMaximized(configDir);

        ShowQuestDetail(app, quest.Name, "Done");

        Assert.Equal(quest.Name, app.GetElementText("TxtDetailName"));
    }

    [E2EFact]
    public void A_failure_keyed_the_same_way_shows_as_failed()
    {
        var quest = PickQuest();
        var configDir = NewConfigDir();

        SeedFixedProfile(configDir);
        E2EDb.SeedQuestProgress(configDir, ProfileService.PvpProfileId, quest.Id, quest.NormalizedName, "Failed");

        using var app = LaunchMaximized(configDir);

        ShowQuestDetail(app, quest.Name, "Failed");

        Assert.Equal(quest.Name, app.GetElementText("TxtDetailName"));
    }

    [E2EFact]
    public void Progress_recorded_under_a_row_key_alone_still_finds_its_quest()
    {
        // The fielded build writes the row key and the normalized name together, but a row
        // whose stored name is null (an older write path) has to keep resolving through the id.
        var quest = PickQuest();
        var configDir = NewConfigDir();

        SeedFixedProfile(configDir);
        E2EDb.SeedQuestProgress(configDir, ProfileService.PvpProfileId, quest.Id, normalizedName: null, "Done");

        using var app = LaunchMaximized(configDir);

        ShowQuestDetail(app, quest.Name, "Done");

        Assert.Equal(quest.Name, app.GetElementText("TxtDetailName"));
    }

    /// <summary>
    /// Creates the config dir's user_data.db and pins the profile the seeded progress belongs to.
    /// <para>
    /// Log monitoring is off because the app's profile auto-detection wins over the stored
    /// selection by design: on a machine with real EFT logs it reads them at startup and
    /// switches to whichever profile the last session used, leaving these tests asserting
    /// against an empty profile. That is correct app behaviour and a wrong test environment.
    /// </para>
    /// </summary>
    private static void SeedFixedProfile(string configDir)
    {
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "False");
    }

    private sealed record SeedQuest(string Id, string Name, string NormalizedName, bool IsCarriedRename);

    /// <summary>
    /// Picks a quest from the shipped database whose name is a unique search substring, so the
    /// quest list filters down to exactly one row. Prefers a quest whose stored normalized name
    /// differs from what its current title would produce, because that is a rename whose
    /// progress was carried across; falls back to any quest when the shipped data has none yet.
    /// </summary>
    private static SeedQuest PickQuest()
    {
        var databasePath = TestSeed.DatabasePath;
        Assert.True(File.Exists(databasePath), $"seed database not found at {databasePath}");

        var candidates = new List<SeedQuest>();
        var names = new List<string>();

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();

            var hasNormalizedName = ColumnExists(connection, "Quests", "NormalizedName");
            var expression = hasNormalizedName
                ? "NormalizedName"
                : "LOWER(REPLACE(REPLACE(REPLACE(Name, ' ', '-'), '''', ''), '.', ''))";

            using var cmd = new SqliteCommand(
                $"SELECT Id, Name, {expression} FROM Quests WHERE Name IS NOT NULL AND Name <> '' ORDER BY Name",
                connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var normalizedName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                if (normalizedName.Length == 0)
                    continue;

                names.Add(name);
                candidates.Add(new SeedQuest(
                    id,
                    name,
                    normalizedName,
                    // A quest whose stored key no longer matches its title: a carried rename.
                    IsCarriedRename: normalizedName != SqlForm(name)));
            }
        }

        SqliteConnection.ClearAllPools();
        Assert.NotEmpty(candidates);

        // The quest tab's search is a substring match, so a name that is a prefix of another
        // would leave two rows and the shared choreography would time out.
        bool IsUnique(SeedQuest quest) =>
            names.Count(n => n.Contains(quest.Name, StringComparison.OrdinalIgnoreCase)) == 1;

        var unique = candidates.Where(IsUnique).ToList();
        Assert.True(unique.Count > 0, "no quest in the seed database has a name that is a unique search substring");

        return unique.FirstOrDefault(q => q.IsCarriedRename) ?? unique[0];
    }

    /// <summary>
    /// What the current title would normalize to, spelled out here rather than borrowed from
    /// the pipeline: this is only used to notice that a stored key no longer matches its title,
    /// and calling the pipeline's own function would hide a drift between the two.
    /// </summary>
    private static string SqlForm(string name) =>
        name.Replace(" ", "-").Replace("'", "").Replace(".", "").ToLowerInvariant();

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var cmd = new SqliteCommand($"PRAGMA table_info({table})", connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
