using Brush = System.Windows.Media.Brush;
using Colors = System.Windows.Media.Colors;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TarkovHelper.Models;
using TarkovHelper.Pages;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;
using static TarkovHelper.Tests.LoyaltyFixtures;

namespace TarkovHelper.Tests;

/// <summary>
/// The detail pane's Requirements section, which is one list over every requirement kind rather
/// than a named TextBlock per kind plus a list for the third.
/// <para>
/// Three mechanisms for one concept is what this replaced: the level and Scav karma lines each
/// had their own TextBlock, their own inline if/else deciding text, colour and visibility, and
/// their own "has this requirement" bool feeding a section-visibility condition that grew a term
/// per kind - and the level line carried a third copy of the "met" rule
/// (<c>playerLevel &gt;= reqLevel</c>) that nothing kept in step with the status engine.
/// </para>
/// </summary>
public sealed class QuestRequirementLinesTests
{
    // Two brushes that are only ever compared by reference, so a case says "met" or "unmet"
    // without depending on the theme's actual colours.
    private static readonly Brush Met = new SolidColorBrush(Colors.White);
    private static readonly Brush Unmet = new SolidColorBrush(Colors.Red);

    private static readonly LocalizationService English =
        TestLocalization.WithLanguage(AppLanguage.EN);

    private static List<RequirementLineViewModel> LinesFor(
        TarkovTask task, ProfileSettingsSnapshot settings)
        => RequirementLineViewModel.BuildFor(
            task, settings, English, requirement => requirement.TraderName, Met, Unmet);

    private static string[] TextsFor(TarkovTask task, ProfileSettingsSnapshot settings)
        => LinesFor(task, settings).Select(line => line.DisplayText).ToArray();

    [Fact]
    public void A_quest_with_no_requirement_of_these_kinds_produces_no_lines()
    {
        // What collapses the section: the count, not a condition with a term per kind.
        Assert.Empty(LinesFor(Quest("Prapor"), Settings()));
    }

    [Fact]
    public void A_level_requirement_of_zero_is_not_a_line()
    {
        // The published data uses 0 for "no level requirement", and the status engine treats it
        // as met. A line saying "Level 0" would be noise in the met colour.
        var task = Quest("Prapor");
        task.RequiredLevel = 0;

        Assert.Empty(LinesFor(task, Settings()));
    }

    [Fact]
    public void Each_kind_gets_a_line_showing_the_requirement_against_the_entered_value()
    {
        var task = Quest("Prapor", (Prapor, "Prapor", 3));
        task.RequiredLevel = 20;
        task.RequiredScavKarma = 2.0;

        Assert.Equal(
            new[]
            {
                "Level 20 (Current: 15)",
                "Scav Karma ≥ 2 (Current: 1)",
                "Prapor LL3 (Current: 1)",
            },
            TextsFor(task, Settings(playerLevel: 15, scavRep: 1.0)));
    }

    [Fact]
    public void The_lines_come_in_the_badges_precedence_order()
    {
        // Level, then karma, then the loyalty rows: the badge names the first unmet gate in that
        // same order, so the first unmet LINE is the one the badge is talking about. Emitted in
        // any other order the pane would open on a requirement the player does not have to clear
        // next.
        var task = Quest("Prapor", (Prapor, "Prapor", 3));
        task.RequiredLevel = 40;
        task.RequiredScavKarma = 4.0;

        var lines = LinesFor(task, Settings(playerLevel: 15, scavRep: 1.0));
        var firstUnmet = lines.First(line => line.Foreground == Unmet);

        Assert.Equal("Level 40 (Current: 15)", firstUnmet.DisplayText);
        Assert.Equal(
            "Lv.40",
            QuestRequirementBadge.For(
                QuestGate.PlayerLevel, task, Settings(playerLevel: 15, scavRep: 1.0),
                requirement => requirement.TraderName));
    }

    [Fact]
    public void A_met_requirement_is_told_from_an_unmet_one_by_its_colour_alone()
    {
        // The lines do NOT reshuffle as values are entered, so the colour is the only thing
        // saying which one is still holding the quest.
        var task = Quest("Prapor", (Prapor, "Prapor", 3));
        task.RequiredLevel = 20;

        var lines = LinesFor(task, Settings(playerLevel: 20));

        Assert.Equal(new[] { Met, Unmet }, lines.Select(line => line.Foreground).ToArray());
    }

    [Fact]
    public void The_level_lines_met_colour_follows_the_status_engine_rather_than_its_own_rule()
    {
        // The boundary the inline copy got right and had no test for: at exactly the required
        // level the quest is available, so the line is met.
        var task = Quest("Prapor");
        task.RequiredLevel = 20;

        Assert.Equal(Unmet, LinesFor(task, Settings(playerLevel: 19)).Single().Foreground);
        Assert.Equal(Met, LinesFor(task, Settings(playerLevel: 20)).Single().Foreground);
        Assert.Equal(Met, LinesFor(task, Settings(playerLevel: 21)).Single().Foreground);
    }

    [Fact]
    public void A_negative_karma_requirement_reads_as_an_upper_bound()
    {
        // A "bad karma" quest wants a rep at MOST the value, which is the one place the
        // comparison symbol is not the default one.
        var task = Quest("Prapor");
        task.RequiredScavKarma = -3.0;

        Assert.Equal(
            "Scav Karma ≤ -3 (Current: 1)", TextsFor(task, Settings(scavRep: 1.0)).Single());
        Assert.Equal(Unmet, LinesFor(task, Settings(scavRep: 1.0)).Single().Foreground);
        Assert.Equal(Met, LinesFor(task, Settings(scavRep: -4.0)).Single().Foreground);
    }

    [Fact]
    public void Every_trader_the_quest_names_gets_its_own_line_in_the_order_the_quest_carries_them()
    {
        // Giver first, then the game's trader order, established once at load by
        // QuestDbService.SortIntoBadgeOrder - here from rows arriving in the opposite order.
        var task = Quest("Jaeger", (Prapor, "Prapor", 3), (Jaeger, "Jaeger", 2));

        Assert.Equal(
            new[] { "Jaeger LL2 (Current: 4)", "Prapor LL3 (Current: 1)" },
            TextsFor(task, Settings(loyalty: (Jaeger, 4))));

        // ...and the order holds although the first line is now the MET one, so the badge names
        // the second: the list must not reorder itself around what the player has entered.
        var lines = LinesFor(task, Settings(loyalty: (Jaeger, 4)));
        Assert.Equal(Met, lines[0].Foreground);
        Assert.Equal(Unmet, lines[1].Foreground);
    }

    [Fact]
    public void The_trader_name_comes_from_the_resolver_it_is_given()
    {
        // The page passes the localization service's resolver, so a Korean UI shows the Korean
        // name on the line as well as on the badge.
        var lines = RequirementLineViewModel.BuildFor(
            Quest("Prapor", (Prapor, "Prapor", 2)), Settings(),
            TestLocalization.WithLanguage(AppLanguage.KO), _ => "프라파", Met, Unmet);

        Assert.Equal("프라파 LL2 (현재: 1)", lines.Single().DisplayText);
    }
}
