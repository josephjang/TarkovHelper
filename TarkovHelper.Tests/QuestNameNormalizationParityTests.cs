using System.IO;
using Microsoft.Data.Sqlite;
using TarkovDBEditor.Services;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// There is one normalized-name rule, and these tests are what keeps it that way.
/// <para>
/// Three places spell it: the SQL expression in <c>QuestDbService.LoadBaseQuestsAsync</c> that a
/// build applies when the column is absent, <c>QuestDbService.GenerateNormalizedName</c> that the
/// same build applies when the column exists but a row's value is NULL, and
/// <c>QuestNormalizedName.SqlForm</c> that TarkovDBEditor writes the stored column from. They
/// cannot share code (TarkovHelper depends on nothing in the editor, and the SQL runs inside
/// SQLite), so nothing but a test can hold them together.
/// </para>
/// <para>
/// The stake: recorded progress is keyed by whatever the rule produces. A spelling only one of
/// the three computes files a quest's progress under a name the others never look it up by, and
/// nothing throws, logs an error, or shows red. The completion simply stops appearing.
/// </para>
/// </summary>
public sealed class QuestNameNormalizationParityTests
{
    [Theory]
    [InlineData("Stirrup", "stirrup")]
    [InlineData("Sew it Good - Part 4", "sew-it-good---part-4")]
    [InlineData("Sew it Good - Part 2.5", "sew-it-good---part-25")]
    // The ASCII apostrophe is dropped, and nothing else is: the SQL REPLACE chain looks for
    // exactly ' ', U+0027 and '.'. A comma, question mark, colon, exclamation mark or quote
    // survives, and so does the typographic apostrophe U+2019.
    [InlineData("What’s on the Flash Drive?", "what’s-on-the-flash-drive?")]
    [InlineData("What's on it", "whats-on-it")]
    [InlineData("Chemical - Part 1: The Delivery", "chemical---part-1:-the-delivery")]
    [InlineData("Hot Delivery, Now!", "hot-delivery,-now!")]
    [InlineData("The \"Face\"", "the-\"face\"")]
    // LOWER is ASCII-only here: the bundled e_sqlite3 is built without ICU, so it leaves every
    // non-ASCII letter alone. ToLowerInvariant, which this method used to call, does not.
    [InlineData("ABC ÄÖ", "abc-ÄÖ")]
    [InlineData("Ölhandel", "Ölhandel")]
    public void The_apps_fallback_spells_a_name_the_one_agreed_way(string name, string expected)
    {
        Assert.Equal(expected, QuestDbService.GenerateNormalizedName(name));
        // ... and the editor writes the stored column the same way.
        Assert.Equal(expected, QuestNormalizedName.SqlForm(name));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void The_apps_fallback_yields_an_empty_name_for_an_empty_input(string? name, string expected)
    {
        Assert.Equal(expected, QuestDbService.GenerateNormalizedName(name));
    }

    /// <summary>
    /// Whitespace is not a special case: it normalizes to dashes, exactly as the SQL does.
    /// Returning "" here instead (what an IsNullOrWhiteSpace guard would do) would be a fourth
    /// spelling of the rule.
    /// </summary>
    [Fact]
    public void A_name_that_is_only_whitespace_normalizes_to_dashes()
    {
        Assert.Equal("---", QuestDbService.GenerateNormalizedName("   "));
        Assert.Equal("---", QuestNormalizedName.SqlForm("   "));
    }

    /// <summary>
    /// The test that cannot be fooled by a shared misunderstanding of what SQLite does: it has
    /// SQLite evaluate the app's own expression over every published quest name and compares the
    /// result with what the app computes in C# for a row whose stored value is NULL.
    /// </summary>
    [Fact]
    public void The_apps_fallback_agrees_with_SQLite_over_every_published_name()
    {
        var databasePath = TestSeed.DatabasePath;
        Assert.True(File.Exists(databasePath), $"{databasePath} is missing, so there are no published names to check");

        var mismatches = new List<string>();
        var checkedRows = 0;

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var cmd = new SqliteCommand(
                "SELECT Name, LOWER(REPLACE(REPLACE(REPLACE(Name, ' ', '-'), '''', ''), '.', '')) FROM Quests",
                connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                var fromSql = reader.GetString(1);
                checkedRows++;

                var fromFallback = QuestDbService.GenerateNormalizedName(name);
                if (fromSql != fromFallback)
                    mismatches.Add($"{name}: SQL '{fromSql}' vs fallback '{fromFallback}'");
            }
        }

        SqliteConnection.ClearAllPools();

        Assert.True(checkedRows > 0, "the published Quests table is empty");
        Assert.True(mismatches.Count == 0,
            "QuestDbService.GenerateNormalizedName has drifted from the SQL expression in the same file, so a "
            + "row with no stored NormalizedName would be keyed under a name nothing else computes:\n  "
            + string.Join("\n  ", mismatches.Take(20)));
    }
}
