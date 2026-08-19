using System.IO;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Tests;

/// <summary>
/// Data regression guard (PRD Root Cause 1): the shipped tarkov_data.db must carry Korean
/// quest names for a meaningful fraction of quests. This is the test that would have caught
/// a stale tarkov.dev cache producing English-everywhere NameKO.
/// </summary>
public class QuestDataCoverageTests
{
    // tarkov.dev currently translates ~40-45% of tasks into Korean. Require at least 30%
    // to allow for translation gaps while still failing on a stale/English-only DB.
    private const double MinKoreanCoverage = 0.30;

    // Skipped on the code branch: the regenerated DB ships on the `data/korean-quest-names-db`
    // branch, so this code branch carries the upstream baseline DB (no Korean). Enable when
    // validating the DB asset together with the data branch / before release.
    [Fact(Skip = "Requires the regenerated tarkov_data.db (data/korean-quest-names-db branch).")]
    public void Quests_have_sufficient_korean_name_coverage()
    {
        var dbPath = TestSeed.DatabasePath;
        Assert.True(File.Exists(dbPath), $"Asset DB not found at {dbPath}");

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name, NameKO FROM Quests";

        int total = 0, korean = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            total++;
            var name = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var ko = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (!string.IsNullOrEmpty(ko) && ko != name && ContainsHangul(ko))
                korean++;
        }

        Assert.True(total > 0, "Quests table is empty");

        var coverage = (double)korean / total;
        Assert.True(
            coverage >= MinKoreanCoverage,
            $"Korean quest-name coverage {coverage:P1} ({korean}/{total}) is below {MinKoreanCoverage:P0}. " +
            "Regenerate the tarkov.dev cache and rebuild tarkov_data.db (PRD Phase 1.3).");
    }

    private static bool ContainsHangul(string s)
    {
        foreach (var c in s)
            if (c >= 0xAC00 && c <= 0xD7A3)
                return true;
        return false;
    }
}
