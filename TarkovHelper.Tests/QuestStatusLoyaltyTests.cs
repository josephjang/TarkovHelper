using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;
using static TarkovHelper.Tests.LoyaltyFixtures;

namespace TarkovHelper.Tests;

/// <summary>
/// The trader loyalty gate, quest shape by quest shape.
/// <para>
/// Patch 1.1 dissolved most quest chains and put the freed quests behind trader loyalty instead,
/// which makes loyalty the main thing deciding whether a quest can be taken. Ninety-four published
/// quests carry such a requirement and the app treated every one of them as met, so twenty-seven
/// of them read as available on a fresh profile although the game locks each behind loyalty 2.
/// Every unmet case below answered Active before the gate existed; that is what these cases were
/// written to fail against.
/// </para>
/// </summary>
public sealed class QuestStatusLoyaltyTests
{
    private static QuestStatus StatusOf(
        TarkovTask task, ProfileSettingsSnapshot settings, params TarkovTask[] alsoLoaded)
        => WalkOf(task, settings, alsoLoaded).Status;

    /// <summary>
    /// The status AND the gate the walk stopped at. Several gates share one status, so the status
    /// alone cannot say which requirement was decisive - and which one that is is what the row's
    /// badge shows. Asserted here, at the engine, because the engine is where the order lives.
    /// </summary>
    private static (QuestStatus Status, QuestGate Gate) WalkOf(
        TarkovTask task, ProfileSettingsSnapshot settings, params TarkovTask[] alsoLoaded)
    {
        var service = ProgressServiceHarness.Create(
            new ProgressStoreFake(), AppProfile.PvpSeason,
            alsoLoaded.Length == 0 ? new[] { task } : alsoLoaded);
        var status = service.GetStatus(task, service.Snapshot, settings, out var gate);
        return (status, gate);
    }

    [Fact]
    public void A_quest_with_no_loyalty_rows_is_unaffected_by_any_entered_level()
    {
        var task = Quest("q", "Prapor");

        Assert.Equal(QuestStatus.Active, StatusOf(task, Settings()));
        Assert.Equal(QuestStatus.Active, StatusOf(task, Settings(loyalty: (Prapor, 4))));
    }

    [Fact]
    public void A_gate_on_the_quests_own_trader_locks_it_until_that_trader_is_entered()
    {
        // Glory to CPSU's shape, and the shape twenty-seven quests share: nothing else gates it,
        // so a fresh profile used to see it as available on day one.
        var task = Quest("q", "Prapor", (Prapor, "Prapor", 2));

        Assert.Equal(QuestStatus.LevelLocked, StatusOf(task, Settings()));
        Assert.Equal(QuestStatus.LevelLocked, StatusOf(task, Settings(loyalty: (Prapor, 1))));
        Assert.Equal(QuestStatus.Active, StatusOf(task, Settings(loyalty: (Prapor, 2))));
        // Above the requirement is met too: the comparison is at least, not exactly.
        Assert.Equal(QuestStatus.Active, StatusOf(task, Settings(loyalty: (Prapor, 4))));
    }

    [Fact]
    public void A_gate_naming_another_trader_is_decided_by_that_trader_and_not_the_giver()
    {
        // Chemical - Part 3: Skier gives it, Jaeger gates it. Reading the gate off the giver
        // would send the player to raise Skier for nothing.
        var task = Quest("q", "Skier", (Jaeger, "Jaeger", 2));

        Assert.Equal(QuestStatus.LevelLocked, StatusOf(task, Settings(loyalty: (Skier, 4))));
        Assert.Equal(QuestStatus.Active, StatusOf(task, Settings(loyalty: (Jaeger, 2))));
    }

    [Fact]
    public void A_quest_naming_several_traders_needs_every_one_of_them()
    {
        // Thirsty - Hounds: given by Jaeger, gated on Jaeger, Therapist and Skier at 2.
        var task = Quest(
            "q", "Jaeger",
            (Jaeger, "Jaeger", 2), (Therapist, "Therapist", 2), (Skier, "Skier", 2));

        Assert.Equal(
            QuestStatus.LevelLocked,
            StatusOf(task, Settings(loyalty: new[] { (Jaeger, 4), (Therapist, 4) })));
        Assert.Equal(
            QuestStatus.Active,
            StatusOf(task, Settings(loyalty: new[] { (Jaeger, 2), (Therapist, 2), (Skier, 2) })));
    }

    [Fact]
    public void A_requirement_of_level_one_is_met_by_a_profile_that_has_entered_nothing()
    {
        // Every trader starts at 1 in the game, so a published requirement of 1 gates nothing.
        // The published data carries none today; the gate must not invent one either.
        var task = Quest("q", "Prapor", (Prapor, "Prapor", 1));

        Assert.Equal(QuestStatus.Active, StatusOf(task, Settings()));
    }

    [Fact]
    public void A_loyalty_locked_prerequisite_leaves_its_dependant_locked_not_level_locked()
    {
        // The dependant is waiting on a QUEST, so it reads Locked whatever the prerequisite's own
        // reason is: a prerequisite counts only once it is recorded Done, and a loyalty-locked one
        // is not. What this pins is that the recursion carries the same settings snapshot rather
        // than re-reading the live one, and that the dependant does not inherit the LevelLocked
        // the prerequisite itself now shows.
        var prerequisite = Quest("prereq", "Prapor", (Prapor, "Prapor", 3));
        var dependant = Quest("dependant", "Prapor");
        dependant.Previous = new List<string> { "prereq" };

        Assert.Equal(
            QuestStatus.LevelLocked,
            StatusOf(prerequisite, Settings(), prerequisite, dependant));
        Assert.Equal(
            QuestStatus.Locked,
            StatusOf(dependant, Settings(), prerequisite, dependant));

        // Entering the level unlocks the prerequisite; the dependant still waits for it to be
        // done, which is the pre-existing rule and not something loyalty changes.
        Assert.Equal(
            QuestStatus.Active,
            StatusOf(prerequisite, Settings(loyalty: (Prapor, 3)), prerequisite, dependant));
        Assert.Equal(
            QuestStatus.Locked,
            StatusOf(dependant, Settings(loyalty: (Prapor, 3)), prerequisite, dependant));
    }

    [Fact]
    public void A_dependant_of_a_completed_prerequisite_is_active_whatever_the_prerequisites_gate_was()
    {
        // The contrast that keeps the case above honest: the Locked there is the prerequisite not
        // being done, and recording it done clears it even though its loyalty row is still unmet.
        var prerequisite = Quest("prereq", "Prapor", (Prapor, "Prapor", 3));
        var dependant = Quest("dependant", "Prapor");
        dependant.Previous = new List<string> { "prereq" };

        var service = ProgressServiceHarness.Create(
            new ProgressStoreFake(),
            ProgressSnapshot.From(
                "profile", 0,
                new Dictionary<string, QuestStatus> { ["prereq"] = QuestStatus.Done },
                new Dictionary<string, bool>()),
            prerequisite, dependant);

        Assert.Equal(QuestStatus.Active, service.GetStatus(dependant, service.Snapshot, Settings()));
    }

    [Fact]
    public void Level_is_checked_before_loyalty_and_both_answer_LevelLocked()
    {
        // Loyalty levels themselves need player levels upstream (Prapor 3 needs level 21), so a
        // quest short of both is most often waiting on level first. Either way the status is the
        // same; what the order decides is which badge the row shows.
        var task = Quest("q", "Prapor", (Prapor, "Prapor", 3));
        task.RequiredLevel = 40;

        // Short of both: the status is LevelLocked and the gate reported is the LEVEL, which is
        // the requirement the badge then names. The status alone cannot say that.
        Assert.Equal(
            (QuestStatus.LevelLocked, QuestGate.PlayerLevel),
            WalkOf(task, Settings(playerLevel: 15)));
        Assert.Equal(
            (QuestStatus.LevelLocked, QuestGate.PlayerLevel),
            WalkOf(task, Settings(playerLevel: 15, loyalty: (Prapor, 4))));
        // Level cleared, loyalty short: same status, and now loyalty is the gate.
        Assert.Equal(
            (QuestStatus.LevelLocked, QuestGate.TraderLoyalty),
            WalkOf(task, Settings(playerLevel: 40)));
        Assert.Equal(
            (QuestStatus.Active, QuestGate.None),
            WalkOf(task, Settings(playerLevel: 40, loyalty: (Prapor, 3))));
    }

    [Fact]
    public void An_edition_gate_still_wins_over_loyalty()
    {
        // Unavailable takes precedence over every Locked kind, and loyalty joins the walk after
        // all of them: a quest the account cannot own must not start reading as loyalty-locked.
        var task = Quest("q", "Prapor", (Prapor, "Prapor", 4));
        task.RequiredEdition = "eod";

        Assert.Equal(
            (QuestStatus.Unavailable, QuestGate.RequiredEdition), WalkOf(task, Settings()));
    }

    #region The row the badge names

    [Fact]
    public void The_first_unmet_row_is_the_givers_when_the_giver_is_short()
    {
        // The badge says "raise this trader". When the quest's own trader is one of the unmet
        // ones, that is the trader to name: the row already shows its initial.
        var task = Quest(
            "q", "Jaeger",
            (Therapist, "Therapist", 2), (Jaeger, "Jaeger", 2), (Skier, "Skier", 2));

        var unmet = QuestProgressService.FirstUnmetTraderLoyalty(task, Settings());

        Assert.NotNull(unmet);
        Assert.Equal(Jaeger, unmet!.TraderId);
    }

    [Fact]
    public void The_first_unmet_row_falls_back_to_display_order_when_the_giver_is_met()
    {
        // Giver satisfied, two others short: the earlier trader in the game's own order is named,
        // not whichever row the database happened to return first.
        var task = Quest(
            "q", "Jaeger",
            (Skier, "Skier", 2), (Jaeger, "Jaeger", 2), (Therapist, "Therapist", 2));

        var unmet = QuestProgressService.FirstUnmetTraderLoyalty(
            task, Settings(loyalty: (Jaeger, 4)));

        Assert.NotNull(unmet);
        Assert.Equal(Therapist, unmet!.TraderId);
    }

    [Fact]
    public void Two_unranked_traders_are_broken_by_name_not_by_the_order_the_rows_arrived_in()
    {
        // The case a data publish creates the day a trader the display order does not name starts
        // gating a quest. Both rank last, so the tie-break decides, and it must not be "whichever
        // row the query returned first".
        var task = Quest(
            "q", "Prapor",
            ("newcomer-v", "Voevoda", 2), ("newcomer-t", "Taran", 2));
        var reversed = Quest(
            "q", "Prapor",
            ("newcomer-t", "Taran", 2), ("newcomer-v", "Voevoda", 2));

        Assert.Equal(
            "Taran", QuestProgressService.FirstUnmetTraderLoyalty(task, Settings())!.TraderName);
        Assert.Equal(
            "Taran", QuestProgressService.FirstUnmetTraderLoyalty(reversed, Settings())!.TraderName);
    }

    [Fact]
    public void There_is_no_unmet_row_when_every_requirement_is_met_or_absent()
    {
        Assert.Null(QuestProgressService.FirstUnmetTraderLoyalty(
            Quest("q", "Prapor"), Settings()));
        Assert.Null(QuestProgressService.FirstUnmetTraderLoyalty(
            Quest("q", "Prapor", (Prapor, "Prapor", 2)), Settings(loyalty: (Prapor, 2))));
    }

    #endregion
}
