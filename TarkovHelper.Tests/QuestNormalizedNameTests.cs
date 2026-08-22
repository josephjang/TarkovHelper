using System.IO;
using Microsoft.Data.Sqlite;
using TarkovDBEditor.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Pins the normalized quest name to the expression the app computes for itself.
/// <para>
/// Recorded progress is filed under that value, and both TarkovHelper builds switch to reading
/// the <c>Quests.NormalizedName</c> column the moment one exists. So a column written in any
/// other spelling would silently un-key the progress of every quest whose two spellings differ
/// (228 of the 488 published rows), while looking to the schema drift guard like an ordinary
/// additive column. These tests are the thing that would catch that.
/// </para>
/// </summary>
public sealed class QuestNormalizedNameTests
{
    [Theory]
    [InlineData("Stirrup", "stirrup")]
    [InlineData("Sew it Good - Part 4", "sew-it-good---part-4")]
    [InlineData("Shooter Born in Heaven", "shooter-born-in-heaven")]
    [InlineData("The Punisher - Part 1", "the-punisher---part-1")]
    // The ASCII apostrophe and the period are removed, not replaced.
    [InlineData("Sew it Good - Part 2.5", "sew-it-good---part-25")]
    [InlineData("Gunsmith - Part 7", "gunsmith---part-7")]
    public void SqlForm_matches_the_expected_spelling(string name, string expected)
    {
        Assert.Equal(expected, QuestNormalizedName.SqlForm(name));
    }

    [Fact]
    public void SqlForm_removes_the_ascii_apostrophe_and_keeps_the_typographic_one()
    {
        // SQLite's REPLACE only ever looks for U+0027, so U+2019 survives. "What's on the Flash
        // Drive?" is the published quest this rule exists for.
        Assert.Equal("whats-on-it", QuestNormalizedName.SqlForm("What's on it"));
        Assert.Equal("what’s-on-it", QuestNormalizedName.SqlForm("What’s on it"));
    }

    [Fact]
    public void SqlForm_lowercases_ascii_only()
    {
        // The bundled e_sqlite3 is built without ICU, so LOWER leaves every non-ASCII letter
        // alone. Lowering more than SQLite does would drift from the stored progress key.
        Assert.Equal("abc-ÄÖ", QuestNormalizedName.SqlForm("ABC ÄÖ"));
    }

    [Fact]
    public void SqlForm_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => QuestNormalizedName.SqlForm(null!));
    }

    /// <summary>
    /// The one test that cannot be fooled by a shared misunderstanding: it evaluates the app's
    /// actual SQL expression over every name in the published database and compares it with the
    /// C# function the pipeline writes the column from.
    /// </summary>
    [Fact]
    public void SqlForm_agrees_with_the_apps_SQL_expression_over_the_published_names()
    {
        var databasePath = PublishedDatabasePath();
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
                var fromCSharp = QuestNormalizedName.SqlForm(name);
                checkedRows++;

                if (fromSql != fromCSharp)
                    mismatches.Add($"{name}: SQL '{fromSql}' vs C# '{fromCSharp}'");
            }
        }

        SqliteConnection.ClearAllPools();

        Assert.True(checkedRows > 0, "the published Quests table is empty");
        Assert.True(mismatches.Count == 0,
            "QuestNormalizedName.SqlForm has drifted from the expression the app computes when the column is "
            + $"absent, so progress recorded in the field would not be found:\n  {string.Join("\n  ", mismatches.Take(20))}");
    }

    /// <summary>
    /// The publish guard's invariant, checked against real data: for every published row, the
    /// title decoded out of the row key normalizes to the same value the row's name does. It
    /// holds because every published key was minted from that row's own title, and it is what
    /// lets a renamed quest keep a normalized name that no longer matches its new title.
    /// </summary>
    [Fact]
    public void Every_published_row_key_decodes_to_its_own_name()
    {
        var databasePath = PublishedDatabasePath();
        Assert.True(File.Exists(databasePath), $"{databasePath} is missing");

        var mismatches = new List<string>();
        var checkedRows = 0;

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var cmd = new SqliteCommand("SELECT Id, Name FROM Quests", connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                checkedRows++;

                var decoded = WikiQuestIdentity.TitleOf(id);
                if (decoded == null)
                {
                    mismatches.Add($"{name}: row key is not base64 of a wiki page URL");
                    continue;
                }

                if (QuestNormalizedName.SqlForm(decoded) != QuestNormalizedName.SqlForm(name))
                    mismatches.Add($"{name}: row key decodes to '{decoded}'");
            }
        }

        SqliteConnection.ClearAllPools();

        Assert.True(checkedRows > 0, "the published Quests table is empty");
        Assert.True(mismatches.Count == 0,
            "Published rows whose key does not decode to their own name would fail the refresh's publish "
            + $"constraint:\n  {string.Join("\n  ", mismatches.Take(20))}");
    }

    [Theory]
    [InlineData("Stirrup")]
    [InlineData("Sew it Good - Part 4")]
    [InlineData("New Beginning (Prestige 2)")]
    [InlineData("What's on the Flash Drive?")]
    [InlineData("Gunsmith - M4A1")]
    public void Row_keys_round_trip_through_the_page_URL(string title)
    {
        Assert.Equal(title, WikiQuestIdentity.TitleOf(WikiQuestIdentity.IdFor(title)));
    }

    [Fact]
    public void Page_links_keep_parentheses_bare_the_way_tarkov_dev_writes_them()
    {
        Assert.Equal(
            "https://escapefromtarkov.fandom.com/wiki/New_Beginning_(Prestige_2)",
            WikiQuestIdentity.PageLinkFor("New Beginning (Prestige 2)"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    // Valid base64, but not a wiki page URL: a hand-edited or foreign key.
    [InlineData("aGVsbG8=")]
    public void TitleOf_returns_null_for_a_key_it_cannot_decode(string questId)
    {
        Assert.Null(WikiQuestIdentity.TitleOf(questId));
    }

    private static string PublishedDatabasePath() =>
        Path.Combine(TestRepo.Root(), "data", "v1", "tarkov_data.db");
}
