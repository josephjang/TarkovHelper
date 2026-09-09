using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;
using static TarkovHelper.Tests.LoyaltyFixtures;

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
    /// <summary>
    /// Local, unlike the four ids these cases share with the gate's own suites
    /// (<see cref="LoyaltyFixtures"/>): BTR Driver gates no quest today and is here for its
    /// hyphenated published name alone.
    /// </summary>
    private const string BtrDriver = "656f0f98d80a697f855d34b1";

    /// <summary>
    /// The Traders table's normalized names, for the traders these cases use. BTR Driver is the
    /// one that matters most: its published name is hyphenated, so it is where a reader deriving
    /// the name from the nickname instead answers something the display order does not contain.
    /// </summary>
    private static string? NormalizedNames(string traderId) => traderId switch
    {
        Prapor => "prapor",
        Jaeger => "jaeger",
        Therapist => "therapist",
        Skier => "skier",
        BtrDriver => "btr-driver",
        _ => null,
    };

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

    private static Dictionary<string, TarkovTask> LookupOf(params TarkovTask[] quests)
        => quests.ToDictionary(q => q.Ids![0], q => q, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attaches with the Traders table stubbed by <see cref="NormalizedNames"/>, which is how
    /// every case here but the fallback ones want it.
    /// </summary>
    private static Task Attach(SqliteConnection connection, params TarkovTask[] quests)
        => QuestDbService.AttachQuestTraderRequirementsAsync(
            connection, LookupOf(quests), NormalizedNames);

    [Fact]
    public async Task Rows_attach_to_their_quest_with_the_id_name_and_level()
    {
        var quest = Quest("q1", "Prapor");
        await using var connection = WithRows(("q1", Jaeger, "Jaeger", 2));

        await Attach(connection, quest);

        var requirement = Assert.Single(quest.TraderLoyaltyRequirements!);
        Assert.Equal(Jaeger, requirement.TraderId);
        Assert.Equal("Jaeger", requirement.TraderName);
        Assert.Equal("jaeger", requirement.NormalizedName);
        Assert.Equal(2, requirement.Level);
        Assert.True(quest.HasTraderLoyaltyRequirements);
    }

    /// <summary>
    /// The normalized name is the Traders table's own, not the nickname lower-cased. Every
    /// multi-word trader is the case that matters: "BTR Driver" lower-cases to "btr driver",
    /// which is not a name the display order contains, so a reader deriving it that way ranks
    /// the trader last and puts it at the bottom of the drawer and the badge's tie-break.
    /// </summary>
    [Fact]
    public async Task The_published_normalized_name_is_stamped_on_the_row_not_the_lower_cased_nickname()
    {
        var quest = Quest("q1", "Prapor");
        await using var connection = WithRows(("q1", BtrDriver, "BTR Driver", 2));

        await Attach(connection, quest);

        var requirement = Assert.Single(quest.TraderLoyaltyRequirements!);
        Assert.Equal("btr-driver", requirement.NormalizedName);
        Assert.NotEqual("btr driver", requirement.NormalizedName);
        // The point of the stamped name: the display order recognises it.
        Assert.NotEqual(int.MaxValue, TraderDbService.DisplayRank(requirement.NormalizedName));
        Assert.Equal(
            int.MaxValue, TraderDbService.DisplayRank(requirement.TraderName.ToLowerInvariant()));
    }

    /// <summary>
    /// A trader the Traders table has no row for still gets a usable normalized name, since that
    /// is what the drawer builds its automation ids from and what the display order reads.
    /// </summary>
    [Theory]
    [InlineData("Voevoda", "voevoda")]
    [InlineData("", "newcomer")]
    public async Task A_trader_the_traders_table_does_not_carry_falls_back_for_its_normalized_name(
        string traderName, string expected)
    {
        var quest = Quest("q1", "Prapor");
        await using var connection = WithRows(("q1", "newcomer", traderName, 2));

        // No Traders row at all: the nickname lower-cased, and the id when there is no nickname
        // either, because a blank normalized name would collide with every other blank one.
        await QuestDbService.AttachQuestTraderRequirementsAsync(
            connection, LookupOf(quest), _ => null);

        Assert.Equal(expected, Assert.Single(quest.TraderLoyaltyRequirements!).NormalizedName);
    }

    [Fact]
    public async Task A_quest_with_several_rows_keeps_all_of_them()
    {
        // Thirsty - Hounds' shape: three traders on one quest, all of which have to be met.
        var quest = Quest("q1", "Prapor");
        await using var connection = WithRows(
            ("q1", Jaeger, "Jaeger", 2),
            ("q1", Therapist, "Therapist", 2),
            ("q1", Prapor, "Prapor", 3));

        await Attach(connection, quest);

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
        var gated = Quest("q1", "Prapor");
        var ungated = Quest("q2", "Prapor");
        await using var connection = WithRows(("q1", Jaeger, "Jaeger", 2));

        await Attach(connection, gated, ungated);

        Assert.Null(ungated.TraderLoyaltyRequirements);
        Assert.False(ungated.HasTraderLoyaltyRequirements);
    }

    [Fact]
    public async Task A_row_for_a_quest_this_load_does_not_have_is_skipped()
    {
        var quest = Quest("q1", "Prapor");
        await using var connection = WithRows(
            ("q1", Jaeger, "Jaeger", 2),
            ("q-not-loaded", Prapor, "Prapor", 4));

        await Attach(connection, quest);

        Assert.Single(quest.TraderLoyaltyRequirements!);
    }

    /// <summary>
    /// A row the gate could never satisfy is dropped rather than attached. Attaching it would
    /// lock the quest for good: there is no trader to enter a level for, and no level below 1
    /// to fall short of.
    /// </summary>
    [Theory]
    [InlineData("", "Jaeger", 2)]
    [InlineData("  ", "Jaeger", 2)]
    [InlineData("5c0647fdd443bc2504c2d371", "Jaeger", 0)]
    [InlineData("5c0647fdd443bc2504c2d371", "Jaeger", -1)]
    public async Task An_unusable_row_is_skipped(string traderId, string traderName, int level)
    {
        var quest = Quest("q1", "Prapor");
        await using var connection = WithRows(("q1", traderId, traderName, level));

        await Attach(connection, quest);

        Assert.Null(quest.TraderLoyaltyRequirements);
    }

    /// <summary>
    /// A blank nickname is NOT an unusable row. The published column is NOT NULL but permits '',
    /// the gate compares the trader id alone, and dropping the row would fail OPEN: the quest
    /// would read as available although the game still gates it, and the trader would vanish from
    /// the drawer, so nothing the player could enter would ever lock it again.
    /// </summary>
    [Fact]
    public async Task A_row_with_a_blank_nickname_still_gates_the_quest()
    {
        var quest = Quest("q1", "Prapor");
        await using var connection = WithRows(("q1", Jaeger, "", 2));

        await Attach(connection, quest);

        var requirement = Assert.Single(quest.TraderLoyaltyRequirements!);
        Assert.Equal(Jaeger, requirement.TraderId);
        Assert.Equal(2, requirement.Level);
        // The display values still resolve: the normalized name off the Traders table, and the
        // roster's label off the id rather than the blank the row carried.
        Assert.Equal("jaeger", requirement.NormalizedName);
        Assert.Equal(
            Jaeger,
            Assert.Single(QuestDbService.BuildLoyaltyTraders(new List<TarkovTask> { quest }))
                .TraderName);
    }

    /// <summary>
    /// A level above the highest a profile can hold is NOT an unusable row either, for the same
    /// reason: dropping it would fail OPEN and un-gate a quest the game still gates. It is kept
    /// and left to gate, and the loader warns about it, because it is the one kept row no entry
    /// the player can make will ever clear - every entered level is clamped to
    /// <see cref="SettingsService.MaxTraderLoyaltyLevel"/> on the way in. The warning itself is
    /// not asserted here: the logger is a file-backed singleton with no capture seam.
    /// </summary>
    [Fact]
    public async Task A_level_above_the_highest_a_profile_can_hold_is_kept_and_still_gates()
    {
        var beyondTheCeiling = SettingsService.MaxTraderLoyaltyLevel + 1;
        var quest = Quest("q1", "Prapor");
        await using var connection = WithRows(("q1", Jaeger, "Jaeger", beyondTheCeiling));

        await Attach(connection, quest);

        var requirement = Assert.Single(quest.TraderLoyaltyRequirements!);
        Assert.Equal(beyondTheCeiling, requirement.Level);
        // Still unmet with the highest level a profile can hold entered, which is exactly the
        // state the warning exists to make visible in the log.
        Assert.NotNull(QuestProgressService.FirstUnmetTraderLoyalty(
            quest, Settings(loyalty: (Jaeger, SettingsService.MaxTraderLoyaltyLevel))));
    }

    #region The order a quest carries its requirements in

    /// <summary>
    /// The badge order is established once, at load: the quest's own trader first, then the
    /// game's trader order. Every reader then takes the list as it stands - the badge is "the
    /// first unmet entry" and the detail pane a plain projection - rather than re-sorting, which
    /// is three places for one rule to drift.
    /// </summary>
    [Fact]
    public async Task A_quests_requirements_are_stored_with_the_giver_first_then_display_order()
    {
        var quest = Quest("q1", "Jaeger");
        // Deliberately arriving in neither order: Jaeger gives the quest but sorts eighth, and
        // Skier is neither the giver nor the earliest.
        await using var connection = WithRows(
            ("q1", Skier, "Skier", 2),
            ("q1", Therapist, "Therapist", 2),
            ("q1", Jaeger, "Jaeger", 2));

        await Attach(connection, quest);

        Assert.Equal(
            new[] { Jaeger, Therapist, Skier },
            quest.TraderLoyaltyRequirements!.Select(r => r.TraderId).ToArray());
    }

    /// <summary>
    /// The tie-break, for the case a publish creates the day a trader the display order does not
    /// name starts gating a quest: alphabetical by nickname, never the order the rows arrived in.
    /// </summary>
    [Fact]
    public async Task Two_traders_the_display_order_does_not_name_are_stored_alphabetically()
    {
        var quest = Quest("q1", "Prapor");
        await using var connection = WithRows(
            ("q1", "newcomer-v", "Voevoda", 2),
            ("q1", "newcomer-t", "Taran", 2));

        await QuestDbService.AttachQuestTraderRequirementsAsync(
            connection, LookupOf(quest), _ => null);

        Assert.Equal(
            new[] { "Taran", "Voevoda" },
            quest.TraderLoyaltyRequirements!.Select(r => r.TraderName).ToArray());
    }

    /// <summary>
    /// The giver comes first even when it sorts last in the game's order, which is the whole
    /// point of the first clause: the row already shows that trader's initial.
    /// </summary>
    [Fact]
    public void The_giver_sorts_ahead_of_a_trader_the_game_lists_earlier()
    {
        var quest = Quest("q1", "Jaeger");
        quest.TraderLoyaltyRequirements = new List<QuestTraderRequirement>
        {
            new() { TraderId = Prapor, TraderName = "Prapor", NormalizedName = "prapor", Level = 3 },
            new() { TraderId = Jaeger, TraderName = "Jaeger", NormalizedName = "jaeger", Level = 2 },
        };

        QuestDbService.SortIntoBadgeOrder(quest);

        Assert.Equal(
            new[] { Jaeger, Prapor },
            quest.TraderLoyaltyRequirements.Select(r => r.TraderId).ToArray());
    }

    #endregion

    /// <summary>
    /// A database published before the 1.1 refresh has no such table. That is a legal input, not
    /// a failure: those builds gated nothing on loyalty and this one must not either.
    /// </summary>
    [Fact]
    public async Task A_database_without_the_table_loads_with_no_requirements_and_no_failure()
    {
        var quest = Quest("q1", "Prapor");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var loaded = await QuestDbService.LoadQuestTraderRequirementsAsync(
            connection, LookupOf(quest), NormalizedNames);

        Assert.False(loaded);
        Assert.Null(quest.TraderLoyaltyRequirements);
        Assert.Empty(QuestDbService.BuildLoyaltyTraders(new List<TarkovTask> { quest }));
    }

    #region The drawer's roster

    /// <summary>One requirement row as the loader would have stamped it.</summary>
    private static QuestTraderRequirement Row(string traderId, string traderName, int level)
        => new()
        {
            TraderId = traderId,
            TraderName = traderName,
            NormalizedName = NormalizedNames(traderId) ?? traderName.ToLowerInvariant(),
            Level = level,
        };

    [Fact]
    public void The_roster_is_distinct_by_trader_and_in_the_games_display_order()
    {
        var first = Quest("q1", "Prapor");
        first.TraderLoyaltyRequirements = new List<QuestTraderRequirement>
        {
            Row(Jaeger, "Jaeger", 2),
            Row(Therapist, "Therapist", 2),
        };
        var second = Quest("q2", "Prapor");
        second.TraderLoyaltyRequirements = new List<QuestTraderRequirement>
        {
            // Jaeger again, at another level: the roster is about traders, not requirements.
            Row(Jaeger, "Jaeger", 4),
            Row(Prapor, "Prapor", 3),
        };

        var roster = QuestDbService.BuildLoyaltyTraders(new List<TarkovTask> { first, second });

        // Prapor, Therapist, Jaeger: the order the game lists traders in, not alphabetical
        // (which would put Jaeger first) and not the order the rows arrived in.
        Assert.Equal(new[] { Prapor, Therapist, Jaeger }, roster.Select(t => t.TraderId).ToArray());
    }

    [Fact]
    public void A_trader_the_display_order_does_not_name_sorts_last()
    {
        // The case a data publish creates the day a new trader starts gating a quest: the drawer
        // has to offer it, and it goes after the traders whose place is known.
        var quest = Quest("q1", "Prapor");
        quest.TraderLoyaltyRequirements = new List<QuestTraderRequirement>
        {
            Row("newcomer-b", "Voevoda", 2),
            Row("newcomer-a", "Taran", 2),
            Row(Jaeger, "Jaeger", 2),
        };

        var roster = QuestDbService.BuildLoyaltyTraders(new List<TarkovTask> { quest });

        // Jaeger by rank, then the two unranked ones alphabetically among themselves.
        Assert.Equal(
            new[] { "Jaeger", "Taran", "Voevoda" },
            roster.Select(t => t.TraderName).ToArray());
    }

    /// <summary>
    /// A multi-word trader takes its place in the drawer from the name the row was stamped with,
    /// which is the whole reason the loader stamps it: "btr driver" is in no display order.
    /// </summary>
    [Fact]
    public void The_roster_carries_the_normalized_name_the_row_was_stamped_with()
    {
        var quest = Quest("q1", "Prapor");
        quest.TraderLoyaltyRequirements = new List<QuestTraderRequirement>
        {
            Row(BtrDriver, "BTR Driver", 2),
        };

        var trader = Assert.Single(QuestDbService.BuildLoyaltyTraders(new List<TarkovTask> { quest }));

        Assert.Equal("btr-driver", trader.NormalizedName);
        Assert.NotEqual(int.MaxValue, TraderDbService.DisplayRank(trader.NormalizedName));
    }

    /// <summary>
    /// Two ids differing only in case are two traders here, because they are two entries in the
    /// ProfileSettings table the entered levels are read back from (no COLLATE NOCASE, and
    /// TraderLoyaltyLevels compares Ordinal for exactly that reason). Folding them together would
    /// build one drawer button whose level the gate could not read back for the other id.
    /// </summary>
    [Fact]
    public void Two_trader_ids_differing_only_in_case_are_two_entries()
    {
        var quest = Quest("q1", "Prapor");
        quest.TraderLoyaltyRequirements = new List<QuestTraderRequirement>
        {
            Row("54CB50C76803FA8B248B4571", "Prapor", 2),
            Row(Prapor, "Prapor", 3),
        };

        var roster = QuestDbService.BuildLoyaltyTraders(new List<TarkovTask> { quest });

        Assert.Equal(2, roster.Count);

        // Why it has to be two: entering one id's level leaves the other still at the default,
        // so a roster that folded them would offer a button the gate could not read back.
        var entered = TraderLoyaltyLevels.Empty.With(Prapor, 3);
        Assert.Equal(3, entered.LevelOf(Prapor));
        Assert.Equal(
            SettingsService.DefaultTraderLoyaltyLevel,
            entered.LevelOf("54CB50C76803FA8B248B4571"));
    }

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
