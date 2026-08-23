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
    /// The invariant recorded progress actually rests on, checked against real data: for every
    /// published row, <c>NormalizedName</c> is the normalized form of the title decoded out of
    /// the row key.
    /// <para>
    /// This used to assert that the key decoded to the row's own <em>name</em>, which held only
    /// because every key had been minted from the title its row still carried. Patch 1.1 renamed
    /// 100 published quests, and each keeps the key it was first published under, so the key now
    /// decodes to the old title on all of them: "Shooter Born in Heaven" answers to a key reading
    /// "A Shooter Born in Heaven". That is the point of the identity change, not a defect.
    /// </para>
    /// <para>
    /// What must not drift is this: the app computes the normalized name from the key when the
    /// column is absent, so a row whose column disagrees with its own key is a row whose recorded
    /// progress one of the two lookups will not find.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_published_normalized_name_matches_the_title_in_its_row_key()
    {
        var databasePath = PublishedDatabasePath();
        Assert.True(File.Exists(databasePath), $"{databasePath} is missing");

        var mismatches = new List<string>();
        var checkedRows = 0;

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var cmd = new SqliteCommand("SELECT Id, Name, NormalizedName FROM Quests", connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var stored = reader.IsDBNull(2) ? null : reader.GetString(2);
                checkedRows++;

                var decoded = WikiQuestIdentity.TitleOf(id);
                if (decoded == null)
                {
                    mismatches.Add($"{name}: row key is not base64 of a wiki page URL");
                    continue;
                }

                if (stored == null)
                {
                    mismatches.Add($"{name}: NormalizedName is NULL, so the column and the key cannot agree");
                    continue;
                }

                var fromTheKey = QuestNormalizedName.SqlForm(decoded);
                if (stored != fromTheKey)
                    mismatches.Add($"{name}: NormalizedName is '{stored}' but its key reads '{fromTheKey}'");
            }
        }

        SqliteConnection.ClearAllPools();

        Assert.True(checkedRows > 0, "the published Quests table is empty");
        Assert.True(mismatches.Count == 0,
            "Published rows whose NormalizedName disagrees with the title in their own row key. The app "
            + "computes that value from the key when the column is absent, so these rows are found by one "
            + $"lookup and not the other:\n  {string.Join("\n  ", mismatches.Take(20))}");
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
