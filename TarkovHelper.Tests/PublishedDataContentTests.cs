using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using TarkovDBEditor.Services;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// What the published database has to say, checked against the file on the endpoint rather than
/// against a fixture.
/// <para>
/// The pipeline's own guards run at regeneration time and are gone by the time anything ships.
/// These run in CI on every build, against the file every install downloads, so a publish that
/// quietly lost a fact stays broken visibly instead of silently. The seven months of NULL
/// external IDs are what that costs when nothing watches the shipped file: log sync matched no
/// quest and hideout requirements resolved nothing, and every guard in the editor was green.
/// </para>
/// <para>
/// The named rows are the ones patch 1.1 made fragile: quests it renamed, titles it handed to a
/// different quest, and the chain it renumbered. Each is pinned by its external game ID rather
/// than by its title, because the title is exactly what moved.
/// </para>
/// </summary>
public sealed class PublishedDataContentTests
{
    #region Shape

    [Fact]
    public void The_quest_set_is_whole()
    {
        Assert.InRange(Count("SELECT COUNT(*) FROM Quests"), 450, 600);
    }

    [Fact]
    public void Exactly_thirteen_quests_are_flagged_for_Kappa()
    {
        // The container needs all thirteen; a fourteenth or a twelfth is a parse that drifted,
        // and the gauge in the app is computed straight off this flag.
        Assert.Equal(13, Count("SELECT COUNT(*) FROM Quests WHERE KappaRequired = 1"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM Quests WHERE KappaRequired = 1 AND Name = 'Collector'"));
    }

    [Fact]
    public void Collector_requires_twelve_quests_and_all_of_them_are_Kappa_quests()
    {
        var rows = Query(
            @"SELECT r.RequiredQuestId, r.GroupId, q.KappaRequired
              FROM QuestRequirements r
              JOIN Quests c ON c.Id = r.QuestId
              LEFT JOIN Quests q ON q.Id = r.RequiredQuestId
              WHERE c.Name = 'Collector'");

        Assert.Equal(12, rows.Count);
        // One AND set: the app reads a single group as "all of these", and a stray group number
        // would turn the set into alternatives.
        Assert.All(rows, r => Assert.Equal("0", r[1]));
        // Every required quest exists and carries the flag, so the chain cannot point at a row
        // the publish dropped.
        Assert.All(rows, r => Assert.Equal("1", r[2]));
    }

    #endregion

    #region The rows patch 1.1 made fragile

    [Theory]
    // A Shooter Born in Heaven -> Shooter Born in Heaven, keeping the key its progress is under.
    [InlineData("5c0bde0986f77479cf22c2f8", "Shooter Born in Heaven", "a-shooter-born-in-heaven")]
    // No Questions Asked -> Special Order. The one rename no snapshot could supply an ID for,
    // carried by BsgIdBackfillService.HandBridgedQuestIds.
    [InlineData("68ee1c18b4e5bc9a68018cd7", "Special Order", "no-questions-asked")]
    public void A_renamed_quest_keeps_the_normalized_name_its_progress_is_filed_under(
        string bsgId, string name, string normalizedName)
    {
        var row = Assert.Single(Query(
            $"SELECT Name, NormalizedName FROM Quests WHERE BsgId = '{bsgId}'"));

        Assert.Equal(name, row[0]);
        Assert.Equal(normalizedName, row[1]);
    }

    [Fact]
    public void The_Sew_it_Good_titles_stay_with_the_quests_that_earned_them()
    {
        // 1.1 rotated this chain's titles, so a page-keyed refresh would have moved one quest's
        // recorded completion onto another. Pinned by ID in both directions.
        Assert.Equal("Sew it Good - Part 2", Scalar("SELECT Name FROM Quests WHERE BsgId = '5ae4497b86f7744cf402ed00'"));
        Assert.Equal("5ae4496986f774459e77beb6", Scalar("SELECT BsgId FROM Quests WHERE Name = 'Sew it Good - Part 4'"));
    }

    [Fact]
    public void The_renumbered_Tarkov_Shooter_chain_points_at_the_right_records()
    {
        // 1.1 dropped an entry and renumbered the rest: the task published as Part 6 is now
        // Part 5, and a player who finished Part 6 keeps that progress under the new title.
        Assert.Equal("5bc4836986f7740c0152911c", Scalar("SELECT BsgId FROM Quests WHERE Name = 'The Tarkov Shooter - Part 5'"));

        var part6Requirements = Query(
            @"SELECT r.RequirementType, p.Name
              FROM QuestRequirements r
              JOIN Quests q ON q.Id = r.QuestId
              JOIN Quests p ON p.Id = r.RequiredQuestId
              WHERE q.Name = 'The Tarkov Shooter - Part 6'");

        // The chain still runs through the renumbered row, which is the point. The status is
        // Accept rather than Complete: the spec predicted Complete, and the game's own record
        // says this chain opens the next entry when the previous one is taken, not finished.
        var only = Assert.Single(part6Requirements);
        Assert.Equal("The Tarkov Shooter - Part 5", only[1]);
        Assert.Equal("Accept", only[0]);
    }

    [Fact]
    public void The_prestige_row_the_game_still_records_is_published()
    {
        Assert.Equal("6761ff17cdc36bd66102e9d0", Scalar("SELECT BsgId FROM Quests WHERE Name = 'New Beginning (Prestige 2)'"));
    }

    [Fact]
    public void A_seasonal_quest_the_API_does_not_carry_is_imported_on_the_wikis_word()
    {
        // The seasonal exception firing. If the wiki reworded its marker, this row leaves the
        // app silently along with the rest of the season's quests.
        Assert.Equal(1, Count("SELECT COUNT(*) FROM Quests WHERE Name = 'Uninvited Guests - Part 1'"));
    }

    [Fact]
    public void Stirrup_still_reads_the_way_the_wiki_writes_it()
    {
        // The objective parser's canary: one objective, a map, a count, and no prerequisite.
        var row = Assert.Single(Query(
            @"SELECT q.MinLevel, o.MapName, o.TargetCount, o.Description
              FROM Quests q JOIN QuestObjectives o ON o.QuestId = q.Id
              WHERE q.Name = 'Stirrup'"));

        Assert.Equal("NULL", row[0]);
        Assert.Equal("Factory", row[1]);
        Assert.Equal("10", row[2]);
        Assert.Contains("pistols", row[3], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Count("SELECT COUNT(*) FROM QuestRequirements WHERE QuestId = (SELECT Id FROM Quests WHERE Name = 'Stirrup')"));
    }

    #endregion

    #region Trader gates

    [Fact]
    public void Trader_loyalty_gates_are_published_for_the_quests_that_have_them()
    {
        Assert.True(Count("SELECT COUNT(*) FROM QuestTraderRequirements") >= 100);
    }

    [Fact]
    public void Collector_gates_on_seven_traders_at_level_four()
    {
        var rows = Query(
            @"SELECT TraderName, RequiredLevel FROM QuestTraderRequirements
              WHERE QuestId = (SELECT Id FROM Quests WHERE Name = 'Collector')");

        Assert.Equal(7, rows.Count);
        Assert.All(rows, r => Assert.Equal("4", r[1]));
    }

    [Fact]
    public void A_gate_can_name_a_trader_other_than_the_one_giving_the_quest()
    {
        // The shape the old schema could not express at all: Skier gives this quest and Jaeger
        // gates it, so reading the gate off the giver would be wrong.
        var row = Assert.Single(Query(
            @"SELECT q.Trader, r.TraderName, r.RequiredLevel
              FROM QuestTraderRequirements r JOIN Quests q ON q.Id = r.QuestId
              WHERE q.Name = 'Chemical - Part 3'"));

        Assert.Equal("Skier", row[0]);
        Assert.Equal("Jaeger", row[1]);
        Assert.Equal("2", row[2]);
    }

    [Fact]
    public void Every_trader_a_gate_names_is_a_trader_the_database_has()
    {
        Assert.Equal(0, Count(
            @"SELECT COUNT(*) FROM QuestTraderRequirements r
              WHERE NOT EXISTS (SELECT 1 FROM Traders t WHERE t.Id = r.TraderId)"));
    }

    /// <summary>
    /// The drawer builds one input group per trader a gate names, and its automation ids and its
    /// display order both come from that trader's NormalizedName. A gate naming a trader whose
    /// row has none would produce a group the e2e cannot address and a rank of "last" for a
    /// trader the game lists first.
    /// </summary>
    [Fact]
    public void Every_trader_a_gate_names_has_a_normalized_name_to_be_shown_under()
    {
        Assert.Equal(0, Count(
            @"SELECT COUNT(*) FROM QuestTraderRequirements r
              JOIN Traders t ON t.Id = r.TraderId
              WHERE t.NormalizedName IS NULL OR t.NormalizedName = ''"));
    }

    /// <summary>
    /// One gate per (quest, trader). Nothing in the schema enforces it: QuestTraderRequirements
    /// is keyed on its own Id alone, so a parser that read a trader's line twice would publish
    /// two rows for one trader on one quest. The app reads them as written, one detail line per
    /// row, so the player would be shown two contradicting levels for the same trader and the
    /// quest's badge would name whichever of them the tiebreak happened to land on.
    /// <para>
    /// Vacuous only if the table were empty, which
    /// <see cref="Trader_loyalty_gates_are_published_for_the_quests_that_have_them"/> rules out.
    /// </para>
    /// </summary>
    [Fact]
    public void No_quest_states_two_loyalty_levels_for_the_same_trader()
    {
        var duplicates = Query(
            @"SELECT q.Name, r.TraderName, COUNT(*)
              FROM QuestTraderRequirements r JOIN Quests q ON q.Id = r.QuestId
              GROUP BY r.QuestId, r.TraderId
              HAVING COUNT(*) > 1");

        Assert.True(duplicates.Count == 0,
            "a quest states a loyalty level more than once for the same trader: " +
            string.Join(", ", duplicates.Select(d => $"{d[0]} / {d[1]} x{d[2]}")));
    }

    /// <summary>
    /// The one thing the app hard-codes about loyalty is that the levels run to four
    /// (<see cref="SettingsService.MaxTraderLoyaltyLevel"/>), because every trader with loyalty
    /// levels has had exactly four for as long as loyalty has existed. This is what keeps that
    /// constant honest: a publish carrying a level 5 requirement fails here, on the publish PR,
    /// rather than quietly locking a quest behind a level the drawer cannot enter.
    /// <para>
    /// The floor is 2 rather than 1 for a different reason: every trader starts at 1, so a
    /// published requirement of 1 gates nothing and is a parse that lost its level.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_published_loyalty_level_is_one_the_drawer_can_be_set_to()
    {
        var levels = Query("SELECT DISTINCT RequiredLevel FROM QuestTraderRequirements")
            .Select(r => int.Parse(r[0], CultureInfo.InvariantCulture))
            .ToList();

        Assert.NotEmpty(levels);
        Assert.All(levels, level =>
            Assert.InRange(level, 2, SettingsService.MaxTraderLoyaltyLevel));
    }

    #endregion

    #region External IDs, and what joins through them

    [Fact]
    public void Almost_every_quest_carries_its_external_game_ID()
    {
        var total = Count("SELECT COUNT(*) FROM Quests");
        var missing = Count("SELECT COUNT(*) FROM Quests WHERE BsgId IS NULL OR BsgId = ''");

        // Log sync matches raid events by this ID. It was NULL on all 488 rows for seven months.
        Assert.True(missing <= total * 0.05, $"{missing} of {total} quests have no external ID");
    }

    [Fact]
    public void Every_quest_without_an_external_ID_is_one_the_game_has_no_record_of()
    {
        // The only rows allowed to have none are the seasonal pages imported on the wiki's word,
        // and the game gives those no Kappa flag, no faction and no loyalty gate, because it has
        // no record of them at all. A row missing an ID that carries any of those is a match
        // that failed rather than a quest the API has not published, and its progress will not
        // sync no matter what the player does.
        var withoutId = Query(
            @"SELECT Name, KappaRequired, Faction,
                     (SELECT COUNT(*) FROM QuestTraderRequirements t WHERE t.QuestId = q.Id)
              FROM Quests q WHERE BsgId IS NULL OR BsgId = ''");

        Assert.NotEmpty(withoutId);
        Assert.All(withoutId, row =>
        {
            Assert.Equal("0", row[1]);
            Assert.Equal("NULL", row[2]);
            Assert.Equal("0", row[3]);
        });
    }

    [Fact]
    public void Almost_every_item_carries_its_external_game_ID()
    {
        var total = Count("SELECT COUNT(*) FROM Items");
        var missing = Count("SELECT COUNT(*) FROM Items WHERE BsgId IS NULL OR BsgId = ''");

        Assert.True(missing <= total * 0.10, $"{missing} of {total} items have no external ID");
    }

    [Fact]
    public void Hideout_requirements_reach_the_items_they_name()
    {
        // These name their item by the game's ID and join through Items.BsgId. Every one of them
        // resolved to nothing for seven months, showing a raw identifier and no icon.
        var total = Count("SELECT COUNT(*) FROM HideoutItemRequirements");
        var joined = Count(
            @"SELECT COUNT(*) FROM HideoutItemRequirements h
              WHERE EXISTS (SELECT 1 FROM Items i WHERE i.BsgId = h.ItemId)");

        Assert.True(joined >= total * 0.90, $"only {joined} of {total} hideout requirements join to an item");
    }

    [Fact]
    public void A_dogtag_requirement_has_an_item_behind_it()
    {
        // These carry a faction and no ItemId until the synthesis links them. The first 1.1
        // publish shipped six of each with nothing behind them: a name, no icon, no inventory.
        Assert.Equal(0, Count(
            "SELECT COUNT(*) FROM QuestRequiredItems WHERE DogtagFaction IS NOT NULL AND (ItemId IS NULL OR ItemId = '')"));
        Assert.Equal(0, Count(
            "SELECT COUNT(*) FROM QuestObjectives WHERE DogtagFaction IS NOT NULL AND (ItemId IS NULL OR ItemId = '')"));
        Assert.Equal(2, Count("SELECT COUNT(*) FROM Items WHERE IsDogtagItem = 1"));
    }

    #endregion

    #region Values the fielded build has to be able to read

    [Fact]
    public void Every_quest_names_its_trader()
    {
        Assert.Equal(0, Count("SELECT COUNT(*) FROM Quests WHERE Trader IS NULL OR Trader = ''"));
    }

    [Fact]
    public void No_quest_claims_a_minimum_level_of_zero()
    {
        // The API says 0 for "no requirement"; stored as-is it reads as a real gate of zero.
        Assert.Equal(0, Count("SELECT COUNT(*) FROM Quests WHERE MinLevel = 0"));
    }

    [Fact]
    public void Faction_only_ever_says_Bear_Usec_or_nothing()
    {
        Assert.Equal(0, Count("SELECT COUNT(*) FROM Quests WHERE Faction IS NOT NULL AND Faction NOT IN ('Bear', 'Usec')"));
    }

    [Fact]
    public void A_quest_behind_one_page_for_both_factions_names_neither()
    {
        // The BEAR/USEC pairs share a page, so a faction on the row would hide the quest from
        // half the players.
        Assert.Equal(4, Count(
            @"SELECT COUNT(*) FROM Quests WHERE Faction IS NULL AND Name IN
              ('Textile - Part 1', 'Textile - Part 2', 'Drip-Out - Part 1', 'Drip-Out - Part 2')"));
    }

    [Fact]
    public void A_prerequisite_only_ever_asks_for_a_status_the_build_understands()
    {
        Assert.Equal(0, Count(
            "SELECT COUNT(*) FROM QuestRequirements WHERE RequirementType NOT IN ('Complete', 'Accept', 'Fail')"));
        Assert.Equal(0, Count(
            "SELECT COUNT(*) FROM QuestRequirements WHERE AltRequirementType IS NOT NULL AND AltRequirementType NOT IN ('Complete', 'Accept', 'Fail')"));
    }

    [Fact]
    public void No_prerequisite_points_at_a_quest_the_publish_does_not_have()
    {
        Assert.Equal(0, Count(
            @"SELECT COUNT(*) FROM QuestRequirements r
              WHERE NOT EXISTS (SELECT 1 FROM Quests q WHERE q.Id = r.RequiredQuestId)"));
    }

    [Fact]
    public void No_quest_is_its_own_prerequisite()
    {
        Assert.Equal(0, Count("SELECT COUNT(*) FROM QuestRequirements WHERE QuestId = RequiredQuestId"));
    }

    #endregion

    #region Row identity

    [Fact]
    public void Every_row_carries_a_normalized_name_and_no_two_share_one()
    {
        Assert.Equal(0, Count("SELECT COUNT(*) FROM Quests WHERE NormalizedName IS NULL OR NormalizedName = ''"));
        Assert.Equal(0, Count(
            "SELECT COUNT(*) FROM (SELECT NormalizedName FROM Quests GROUP BY NormalizedName HAVING COUNT(*) > 1)"));
    }

    [Fact]
    public void Every_normalized_name_is_the_one_its_row_key_spells()
    {
        // The app computes this value from the row key when the column is absent, so the two
        // have to agree or a build finds recorded progress through one path and not the other.
        var mismatches = Query("SELECT Id, Name, NormalizedName FROM Quests")
            .Where(r => QuestNormalizedName.SqlForm(WikiQuestIdentity.TitleOf(r[0]) ?? "") != r[2])
            .Select(r => $"{r[1]}: stored '{r[2]}'")
            .ToList();

        Assert.True(mismatches.Count == 0, string.Join("\n  ", mismatches.Take(10)));
    }

    #endregion

    #region Localization

    [Fact]
    public void Korean_names_cover_at_least_half_the_quests()
    {
        // Replaces the coverage test that ran against NameKO alone: the 1.0.10 data stored the
        // English title in that column when no translation existed, so counting non-NULL values
        // there reported 100% while half of them were English.
        var total = Count("SELECT COUNT(*) FROM Quests");
        var korean = Query("SELECT Name, NameKO FROM Quests")
            .Count(r => r[1] != "NULL" && r[1] != r[0] && r[1].Any(IsHangul));

        Assert.True(korean >= total * 0.50, $"only {korean} of {total} quests carry a Korean name");
    }

    [Fact]
    public void A_localized_name_is_never_just_the_English_one_repeated()
    {
        // NULL means "fall back to English" and the app does that at read time. Storing the
        // English string instead makes an untranslated quest indistinguishable from a
        // translated one, which is how the old coverage number read as 100%.
        Assert.Equal(0, Count("SELECT COUNT(*) FROM Quests WHERE NameKO IS NOT NULL AND NameKO = NameEN"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM Quests WHERE NameJA IS NOT NULL AND NameJA = NameEN"));
    }

    #endregion

    #region Icons

    [Fact]
    public void Every_item_has_an_icon_in_the_repository()
    {
        var missing = ItemIds().Where(id => !File.Exists(IconPath(id))).ToList();

        Assert.True(missing.Count == 0,
            $"{missing.Count} items have no icon under Assets/icons:\n  {string.Join("\n  ", missing.Take(10))}");
    }

    [Fact]
    public void No_icon_is_kept_for_an_item_that_is_gone()
    {
        // Dead weight in every release zip, and the sign of a rename whose old file was left
        // behind.
        var items = new HashSet<string>(ItemIds(), StringComparer.Ordinal);
        var orphans = Directory.GetFiles(IconDirectory(), "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name != null && !items.Contains(name))
            .ToList();

        Assert.True(orphans.Count == 0,
            $"{orphans.Count} icons have no item:\n  {string.Join("\n  ", orphans.Take(10))}");
    }

    #endregion

    #region Reading the published file

    private static string PublishedDatabase()
    {
        var path = Path.Combine(TestRepo.Root(), "data", "v1", "tarkov_data.db");
        Assert.True(File.Exists(path), $"{path} is missing, so there is nothing published to check");
        return path;
    }

    private static string IconDirectory() => Path.Combine(TestRepo.Root(), "TarkovHelper", "Assets", "icons");

    private static string IconPath(string itemId) => Path.Combine(IconDirectory(), $"{itemId}.png");

    private static IEnumerable<string> ItemIds() => Query("SELECT Id FROM Items").Select(r => r[0]);

    private static bool IsHangul(char c) => c >= '가' && c <= '힣';

    private static int Count(string sql) =>
        int.Parse(Scalar(sql), CultureInfo.InvariantCulture);

    private static string Scalar(string sql)
    {
        var row = Assert.Single(Query(sql));
        return row[0];
    }

    /// <summary>Every row the query returns, each column rendered as text, NULL as "NULL".</summary>
    private static List<string[]> Query(string sql)
    {
        var rows = new List<string[]>();

        using (var connection = new SqliteConnection($"Data Source={PublishedDatabase()};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = new SqliteCommand(sql, connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var values = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    values[i] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString() ?? "";
                rows.Add(values);
            }
        }

        SqliteConnection.ClearAllPools();
        return rows;
    }

    #endregion
}
