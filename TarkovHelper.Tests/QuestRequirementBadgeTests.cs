using TarkovHelper.Models;
using TarkovHelper.Pages;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// The string a level-locked quest's badge shows, which is the only thing telling the player
/// which of the three gates behind that one status is actually holding the quest.
/// </summary>
public sealed class QuestRequirementBadgeTests
{
    private const string Prapor = "54cb50c76803fa8b248b4571";
    private const string Jaeger = "5c0647fdd443bc2504c2d371";

    private static TarkovTask Quest(
        string giver, params (string TraderId, string TraderName, int Level)[] loyalty)
    {
        var task = new TarkovTask
        {
            Ids = new List<string> { "q" },
            Name = "q",
            NormalizedName = "q",
            Trader = giver,
        };

        if (loyalty.Length > 0)
        {
            task.TraderLoyaltyRequirements = loyalty
                .Select(l => new QuestTraderRequirement
                {
                    TraderId = l.TraderId, TraderName = l.TraderName, Level = l.Level,
                })
                .ToList();
        }

        return task;
    }

    private static ProfileSettingsSnapshot Settings(
        int playerLevel = 15, double scavRep = 1.0, params (string TraderId, int Level)[] loyalty)
    {
        var levels = TraderLoyaltyLevels.Empty;
        foreach (var (traderId, level) in loyalty) levels = levels.With(traderId, level);

        return new ProfileSettingsSnapshot(
            "profile", 0, playerLevel, scavRep, null, null, null, null, null, null, levels);
    }

    /// <summary>The English nickname, which is what the display resolver answers in EN.</summary>
    private static string EnglishName(QuestTraderRequirement requirement) => requirement.TraderName;

    private static string? BadgeFor(TarkovTask task, ProfileSettingsSnapshot settings)
        => QuestRequirementBadge.For(task, settings, EnglishName);

    [Fact]
    public void A_met_quest_has_no_badge_of_this_kind()
    {
        Assert.Null(BadgeFor(Quest("Prapor"), Settings()));
        Assert.Null(BadgeFor(
            Quest("Prapor", (Prapor, "Prapor", 2)), Settings(loyalty: (Prapor, 2))));
    }

    [Fact]
    public void A_loyalty_gate_on_the_quests_own_trader_reads_as_the_level_alone()
    {
        // The row already shows Prapor's initial, so naming the trader again would widen
        // eighty-nine badges for nothing.
        Assert.Equal("LL2", BadgeFor(Quest("Prapor", (Prapor, "Prapor", 2)), Settings()));
        Assert.Equal("LL4", BadgeFor(Quest("Prapor", (Prapor, "Prapor", 4)), Settings()));
    }

    [Fact]
    public void A_loyalty_gate_on_another_trader_names_that_trader()
    {
        // Chemical - Part 3: on a Skier row, "LL2" alone would send the player to raise Skier.
        Assert.Equal("Jaeger LL2", BadgeFor(Quest("Skier", (Jaeger, "Jaeger", 2)), Settings()));
    }

    [Fact]
    public void The_trader_name_comes_from_the_resolver_it_is_given()
    {
        // The page passes the localization service's resolver, so a Korean UI shows the Korean
        // name here. The badge itself never reads a name off the row.
        var badge = QuestRequirementBadge.For(
            Quest("Skier", (Jaeger, "Jaeger", 2)), Settings(), _ => "예거");

        Assert.Equal("예거 LL2", badge);
    }

    [Fact]
    public void Level_beats_karma_beats_loyalty()
    {
        var task = Quest("Prapor", (Prapor, "Prapor", 3));
        task.RequiredLevel = 40;
        task.RequiredScavKarma = 4.0;

        // All three unmet: the badge names the gate the status walk stops at first.
        Assert.Equal("Lv.40", BadgeFor(task, Settings(playerLevel: 15, scavRep: 1.0)));
        // Level cleared, karma still short.
        Assert.Equal("Rep 4", BadgeFor(task, Settings(playerLevel: 40, scavRep: 1.0)));
        // Level and karma cleared: loyalty is what is left.
        Assert.Equal("LL3", BadgeFor(task, Settings(playerLevel: 40, scavRep: 4.0)));
        // ...and with the level entered there is nothing to say.
        Assert.Null(BadgeFor(
            task, Settings(playerLevel: 40, scavRep: 4.0, loyalty: (Prapor, 3))));
    }

    [Fact]
    public void A_quest_naming_several_traders_names_the_giver_first_and_then_display_order()
    {
        var task = Quest(
            "Jaeger",
            (Jaeger, "Jaeger", 2), (Prapor, "Prapor", 3));

        // Giver short: its own row wins, so the badge stays the narrow form.
        Assert.Equal("LL2", BadgeFor(task, Settings()));
        // Giver satisfied: the other trader is named, because now it is someone else's level the
        // player has to go and raise.
        Assert.Equal("Prapor LL3", BadgeFor(task, Settings(loyalty: (Jaeger, 2))));
    }

    [Fact]
    public void A_negative_karma_requirement_still_prints_its_sign()
    {
        // Pre-existing behaviour of the karma branch, carried over from the page unchanged: a
        // "bad karma" quest wants a rep at MOST the value, and the badge shows that value.
        var task = Quest("Prapor");
        task.RequiredScavKarma = -3.0;

        Assert.Equal("Rep -3", BadgeFor(task, Settings(scavRep: 1.0)));
        Assert.Null(BadgeFor(task, Settings(scavRep: -4.0)));
    }
}
