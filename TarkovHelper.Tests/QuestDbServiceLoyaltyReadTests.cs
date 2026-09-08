using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// What the quest load does with the QuestTraderRequirements rows, driven against an in-memory
/// database rather than the published seed.
/// <para>
/// The published database only ever holds rows the pipeline's own guards accepted, so the
/// branches that matter most here - a row for a quest that is not loaded, a row naming no trader,
/// a database with no such table at all - cannot be reached from it. Each of those, taken
/// wrongly, is a quest permanently locked behind something the player cannot enter, so they are
/// covered where they can be produced.
/// </para>
/// </summary>
public sealed class QuestDbServiceLoyaltyReadTests
{
    private const string Prapor = "54cb50c76803fa8b248b4571";
    private const string Jaeger = "5c0647fdd443bc2504c2d371";
    private const string Therapist = "54cb57776803fa99248b456e";

    /// <summary>
    /// An open in-memory database carrying the requirement table and <paramref name="rows"/>.
    /// Kept open by the caller: an in-memory SQLite database exists only while a connection to it
    /// does.
    /// </summary>
    private static SqliteConnection WithRows(params (string QuestId, string TraderId, string TraderName, int Level)[] rows)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            // The published shape, minus the editor bookkeeping columns the app never reads.
            create.CommandText = @"
                CREATE TABLE QuestTraderRequirements (
                    Id TEXT PRIMARY KEY,
                    QuestId TEXT NOT NULL,
                    TraderId TEXT NOT NULL,
                    TraderName TEXT NOT NULL,
                    RequiredLevel INTEGER NOT NULL)";
            create.ExecuteNonQuery();
        }

        var index = 0;
        foreach (var (questId, traderId, traderName, level) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                @"INSERT INTO QuestTraderRequirements (Id, QuestId, TraderId, TraderName, RequiredLevel)
                  VALUES ($id, $quest, $trader, $name, $level)";
            insert.Parameters.AddWithValue("$id", "row-" + index++);
            insert.Parameters.AddWithValue("$quest", questId);
            insert.Parameters.AddWithValue("$trader", traderId);
            insert.Parameters.AddWithValue("$name", traderName);
            insert.Parameters.AddWithValue("$level", level);
            insert.ExecuteNonQuery();
        }

        return connection;
    }

    private static TarkovTask Quest(string id) => new()
    {
        Ids = new List<string> { id },
        Name = id,
        NormalizedName = id,
        Trader = "Prapor",
    };

    private static Dictionary<string, TarkovTask> LookupOf(params TarkovTask[] quests)
        => quests.ToDictionary(q => q.Ids![0], q => q, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public async Task Rows_attach_to_their_quest_with_the_id_name_and_level()
    {
        var quest = Quest("q1");
        await using var connection = WithRows(("q1", Jaeger, "Jaeger", 2));

        await QuestDbService.AttachQuestTraderRequirementsAsync(connection, LookupOf(quest));

        var requirement = Assert.Single(quest.TraderLoyaltyRequirements!);
        Assert.Equal(Jaeger, requirement.TraderId);
        Assert.Equal("Jaeger", requirement.TraderName);
        Assert.Equal(2, requirement.Level);
        Assert.True(quest.HasTraderLoyaltyRequirements);
    }

    [Fact]
    public async Task A_quest_with_several_rows_keeps_all_of_them()
    {
        // Thirsty - Hounds' shape: three traders on one quest, all of which have to be met.
        var quest = Quest("q1");
        await using var connection = WithRows(
            ("q1", Jaeger, "Jaeger", 2),
            ("q1", Therapist, "Therapist", 2),
            ("q1", Prapor, "Prapor", 3));

        await QuestDbService.AttachQuestTraderRequirementsAsync(connection, LookupOf(quest));

        Assert.Equal(3, quest.TraderLoyaltyRequirements!.Count);
        Assert.Equal(
            new[] { Jaeger, Prapor, Therapist }.OrderBy(id => id).ToArray(),
            quest.TraderLoyaltyRequirements.Select(r => r.TraderId).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task A_quest_with_no_rows_is_left_with_no_requirement_list_at_all()
    {
        // Null rather than an empty list: HasTraderLoyaltyRequirements answers false either way,
        // but every ungated quest allocating a list is a cost paid on 400 quests for nothing.
        var gated = Quest("q1");
        var ungated = Quest("q2");
        await using var connection = WithRows(("q1", Jaeger, "Jaeger", 2));

        await QuestDbService.AttachQuestTraderRequirementsAsync(connection, LookupOf(gated, ungated));

        Assert.Null(ungated.TraderLoyaltyRequirements);
        Assert.False(ungated.HasTraderLoyaltyRequirements);
    }

    [Fact]
    public async Task A_row_for_a_quest_this_load_does_not_have_is_skipped()
    {
        var quest = Quest("q1");
        await using var connection = WithRows(
            ("q1", Jaeger, "Jaeger", 2),
            ("q-not-loaded", Prapor, "Prapor", 4));

        await QuestDbService.AttachQuestTraderRequirementsAsync(connection, LookupOf(quest));

        Assert.Single(quest.TraderLoyaltyRequirements!);
    }

    /// <summary>
    /// A row the gate could never satisfy is dropped rather than attached. Attaching it would
    /// lock the quest for good: there is no trader to enter a level for, and no level below 1
    /// to fall short of.
    /// </summary>
    [Theory]
    [InlineData("", "Jaeger", 2)]
    [InlineData("  ", "", 2)]
    [InlineData("5c0647fdd443bc2504c2d371", "Jaeger", 0)]
    [InlineData("5c0647fdd443bc2504c2d371", "Jaeger", -1)]
    public async Task An_unusable_row_is_skipped(string traderId, string traderName, int level)
    {
        var quest = Quest("q1");
        await using var connection = WithRows(("q1", traderId, traderName, level));

        await QuestDbService.AttachQuestTraderRequirementsAsync(connection, LookupOf(quest));

        Assert.Null(quest.TraderLoyaltyRequirements);
    }

    /// <summary>
    /// A database published before the 1.1 refresh has no such table. That is a legal input, not
    /// a failure: those builds gated nothing on loyalty and this one must not either.
    /// </summary>
    [Fact]
    public async Task A_database_without_the_table_loads_with_no_requirements_and_no_failure()
    {
        var quest = Quest("q1");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var loaded = await QuestDbService.LoadQuestTraderRequirementsAsync(connection, LookupOf(quest));

        Assert.False(loaded);
        Assert.Null(quest.TraderLoyaltyRequirements);
        Assert.Empty(QuestDbService.BuildLoyaltyTraders(new List<TarkovTask> { quest }, _ => null));
    }

    #region The drawer's roster

    [Fact]
    public void The_roster_is_distinct_by_trader_and_in_the_games_display_order()
    {
        var first = Quest("q1");
        first.TraderLoyaltyRequirements = new List<QuestTraderRequirement>
        {
            new() { TraderId = Jaeger, TraderName = "Jaeger", Level = 2 },
            new() { TraderId = Therapist, TraderName = "Therapist", Level = 2 },
        };
        var second = Quest("q2");
        second.TraderLoyaltyRequirements = new List<QuestTraderRequirement>
        {
            // Jaeger again, at another level: the roster is about traders, not requirements.
            new() { TraderId = Jaeger, TraderName = "Jaeger", Level = 4 },
            new() { TraderId = Prapor, TraderName = "Prapor", Level = 3 },
        };

        var roster = QuestDbService.BuildLoyaltyTraders(
            new List<TarkovTask> { first, second }, NormalizedNames);

        // Prapor, Therapist, Jaeger: the order the game lists traders in, not alphabetical
        // (which would put Jaeger first) and not the order the rows arrived in.
        Assert.Equal(new[] { Prapor, Therapist, Jaeger }, roster.Select(t => t.TraderId).ToArray());
    }

    [Fact]
    public void A_trader_the_display_order_does_not_name_sorts_last()
    {
        // The case a data publish creates the day a new trader starts gating a quest: the drawer
        // has to offer it, and it goes after the traders whose place is known.
        var quest = Quest("q1");
        quest.TraderLoyaltyRequirements = new List<QuestTraderRequirement>
        {
            new() { TraderId = "newcomer-b", TraderName = "Voevoda", Level = 2 },
            new() { TraderId = "newcomer-a", TraderName = "Taran", Level = 2 },
            new() { TraderId = Jaeger, TraderName = "Jaeger", Level = 2 },
        };

        var roster = QuestDbService.BuildLoyaltyTraders(
            new List<TarkovTask> { quest }, NormalizedNames);

        // Jaeger by rank, then the two unranked ones alphabetically among themselves.
        Assert.Equal(
            new[] { "Jaeger", "Taran", "Voevoda" },
            roster.Select(t => t.TraderName).ToArray());
    }

    [Fact]
    public void A_trader_missing_from_the_traders_table_falls_back_to_its_lower_cased_nickname()
    {
        var quest = Quest("q1");
        quest.TraderLoyaltyRequirements = new List<QuestTraderRequirement>
        {
            new() { TraderId = Prapor, TraderName = "Prapor", Level = 2 },
        };

        // No Traders row at all: the normalized name still has to come out usable, because it is
        // what the drawer builds its automation ids from and what the display order reads.
        var roster = QuestDbService.BuildLoyaltyTraders(new List<TarkovTask> { quest }, _ => null);

        var trader = Assert.Single(roster);
        Assert.Equal("prapor", trader.NormalizedName);
        Assert.Equal(0, TraderDbService.DisplayRank(trader.NormalizedName));
    }

    /// <summary>The Traders table's normalized names, for the three traders these cases use.</summary>
    private static string? NormalizedNames(string traderId) => traderId switch
    {
        Prapor => "prapor",
        Jaeger => "jaeger",
        Therapist => "therapist",
        _ => null,
    };

    #endregion

    #region Display rank

    [Fact]
    public void The_display_order_puts_the_traders_where_the_game_does()
    {
        Assert.True(TraderDbService.DisplayRank("prapor") < TraderDbService.DisplayRank("therapist"));
        Assert.True(TraderDbService.DisplayRank("therapist") < TraderDbService.DisplayRank("skier"));
        Assert.True(TraderDbService.DisplayRank("skier") < TraderDbService.DisplayRank("jaeger"));
        // Case-insensitive, since the fallback path lower-cases a nickname and the table does not.
        Assert.Equal(TraderDbService.DisplayRank("prapor"), TraderDbService.DisplayRank("Prapor"));
    }

    [Fact]
    public void An_unknown_or_missing_normalized_name_ranks_last()
    {
        Assert.Equal(int.MaxValue, TraderDbService.DisplayRank("no-such-trader"));
        Assert.Equal(int.MaxValue, TraderDbService.DisplayRank(""));
        Assert.Equal(int.MaxValue, TraderDbService.DisplayRank(null));
    }

    #endregion
}
