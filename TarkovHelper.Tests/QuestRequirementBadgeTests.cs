using TarkovHelper.Models;
using TarkovHelper.Pages;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;
using static TarkovHelper.Tests.LoyaltyFixtures;

namespace TarkovHelper.Tests;

/// <summary>
/// The string a level-locked quest's badge shows, which is the only thing telling the player
/// which of the three gates behind that one status is actually holding the quest.
/// <para>
/// Driven through the status engine rather than against a hand-written order: which gate is
/// holding a quest is <see cref="QuestProgressService.GetStatus(TarkovTask, ProgressSnapshot, ProfileSettingsSnapshot, out QuestGate)"/>'s
/// answer, and the badge is a formatter over it. A precedence asserted here without running the
/// walk would be a second copy of the rule, which is exactly the drift these cases exist to stop.
/// </para>
/// </summary>
public sealed class QuestRequirementBadgeTests
{
    /// <summary>The English nickname, which is what the display resolver answers in EN.</summary>
    private static string EnglishName(QuestTraderRequirement requirement) => requirement.TraderName;

    /// <summary>
    /// The status and the gate the engine answers for one quest, exactly as a render pass gets
    /// them: one call, so the badge below cannot be formatting a gate from a different walk.
    /// </summary>
    private static (QuestStatus Status, QuestGate Gate) Walk(
        TarkovTask task, ProfileSettingsSnapshot settings,
        ProgressSnapshot? progress = null, params TarkovTask[] alsoLoaded)
    {
        var loaded = alsoLoaded.Length == 0 ? new[] { task } : alsoLoaded;
        var service = progress == null
            ? ProgressServiceHarness.Create(new ProgressStoreFake(), AppProfile.PvpSeason, loaded)
            : ProgressServiceHarness.Create(new ProgressStoreFake(), progress, loaded);
        var status = service.GetStatus(task, service.Snapshot, settings, out var gate);
        return (status, gate);
    }

    /// <summary>
    /// The value the gate the engine stopped at names, or null when it names none. Null is what
    /// the four single-cause statuses and an ungated quest answer.
    /// </summary>
    private static string? BadgeFor(TarkovTask task, ProfileSettingsSnapshot settings)
    {
        var (_, gate) = Walk(task, settings);
        return QuestRequirementBadge.For(gate, task, settings, EnglishName);
    }

    /// <summary>
    /// The whole badge, which is what every pane actually shows: the row, the detail pane, the
    /// prerequisite rows and the alternative-quest rows all reach the formatter through this one
    /// function, so a string asserted here is the string all four of them show.
    /// </summary>
    private static string StatusTextFor(TarkovTask task, ProfileSettingsSnapshot settings)
    {
        var (status, gate) = Walk(task, settings);
        return QuestRequirementBadge.StatusText(status, gate, task, settings, EnglishName);
    }

    /// <summary>
    /// The formatter alone, for the cases whose subject is a status/gate pair the walk does not
    /// produce - the defensive tails a caller could still hand it.
    /// </summary>
    private static string Format(
        QuestStatus status, QuestGate gate, TarkovTask task, ProfileSettingsSnapshot settings)
        => QuestRequirementBadge.StatusText(status, gate, task, settings, EnglishName);

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
        var task = Quest("Skier", (Jaeger, "Jaeger", 2));
        var (_, gate) = Walk(task, Settings());

        var badge = QuestRequirementBadge.For(gate, task, Settings(), _ => "예거");

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
    public void The_badge_reads_the_loader_sorted_order_not_the_order_the_rows_arrived_in()
    {
        // The rows as a DB query can hand them back, giver LAST. The loader sorts them into
        // badge order at attach (QuestDbService.SortIntoBadgeOrder), so the badge names the
        // giver in the narrow form. Built by hand in arrival order it named Prapor instead.
        var task = Quest("Jaeger", (Prapor, "Prapor", 3), (Jaeger, "Jaeger", 2));

        Assert.Equal("LL2", BadgeFor(task, Settings()));
    }

    [Fact]
    public void A_status_that_stands_for_one_thing_reads_as_that_word()
    {
        // Four of the six statuses have a single cause, so there is no gate to name: the walk
        // reports None for a quest it let through and for a recorded Done or Failed row, and a
        // gate whose cause is a whole quest (a prerequisite) has no value to print either.
        var task = Quest("Prapor", (Prapor, "Prapor", 2));

        Assert.Equal("Locked", Format(QuestStatus.Locked, QuestGate.Prerequisite, task, Settings()));
        Assert.Equal("Locked", Format(QuestStatus.Locked, QuestGate.DecodeCount, task, Settings()));
        Assert.Equal("Active", Format(QuestStatus.Active, QuestGate.None, task, Settings()));
        Assert.Equal("Done", Format(QuestStatus.Done, QuestGate.None, task, Settings()));
        Assert.Equal("Failed", Format(QuestStatus.Failed, QuestGate.None, task, Settings()));
    }

    [Fact]
    public void A_level_locked_quest_names_its_gate_and_falls_back_to_the_bare_word()
    {
        var task = Quest("Skier", (Jaeger, "Jaeger", 2));

        Assert.Equal("Jaeger LL2", StatusTextFor(task, Settings()));

        // No gate to name under a status that stands for three. The status still has to render as
        // something, and the word it stands for is what is left: the badge never goes blank.
        Assert.Equal("Level", Format(QuestStatus.LevelLocked, QuestGate.None, task, Settings()));
    }

    /// <summary>
    /// Unavailable hides three causes the same way LevelLocked hides three, so the badge names
    /// which one. The list row has always shown this; the detail pane read the bare "N/A" until
    /// it was handed the task, so the two panes disagreed about the same quest.
    /// </summary>
    [Fact]
    public void An_unavailable_quest_names_the_edition_prestige_or_faction_barring_it()
    {
        var eod = Quest("Prapor");
        eod.RequiredEdition = "edge_of_darkness";
        Assert.Equal("EOD", StatusTextFor(eod, Settings()));

        var unheard = Quest("Prapor");
        unheard.RequiredEdition = "the_unheard";
        Assert.Equal("Unheard", StatusTextFor(unheard, Settings()));

        // Excluded rather than required: the player owns an edition the quest bars, so neither
        // edition name would be right and the badge says which KIND of gate it is.
        var excluded = Quest("Prapor");
        excluded.ExcludedEdition = "eod";
        Assert.Equal("Edition", StatusTextFor(excluded, Settings(hasEod: true)));

        var prestige = Quest("Prapor");
        prestige.RequiredPrestigeLevel = 3;
        Assert.Equal("P.3", StatusTextFor(prestige, Settings()));

        var bear = Quest("Prapor");
        bear.Faction = "bear";
        Assert.Equal("BEAR", StatusTextFor(bear, Settings(faction: "usec")));

        var usec = Quest("Prapor");
        usec.Faction = "usec";
        Assert.Equal("USEC", StatusTextFor(usec, Settings(faction: "bear")));
    }

    /// <summary>
    /// The two edition rules are independent, so a quest can pass the edition it REQUIRES and
    /// still be barred by the edition it EXCLUDES. Behind one bool the badge answered "EOD" here
    /// - telling a player who already owns EOD to go and buy it - because the only thing it could
    /// see was that the edition check had failed.
    /// </summary>
    [Fact]
    public void An_owned_required_edition_does_not_mask_the_excluded_one()
    {
        var task = Quest("Prapor");
        task.RequiredEdition = "eod";
        task.ExcludedEdition = "unheard";

        var settings = Settings(hasEod: true, hasUnheard: true);
        var (status, gate) = Walk(task, settings);

        Assert.Equal(QuestStatus.Unavailable, status);
        Assert.Equal(QuestGate.ExcludedEdition, gate);
        Assert.Equal("Edition", StatusTextFor(task, settings));

        // ...and the quest is available to an account that owns only the edition it asks for.
        Assert.Equal("Active", StatusTextFor(task, Settings(hasEod: true)));
    }

    [Fact]
    public void Edition_beats_prestige_beats_faction_within_unavailable()
    {
        // The status walk's own order, so the badge names the gate the engine stopped at rather
        // than one further down that the player would clear for nothing.
        var task = Quest("Prapor");
        task.RequiredEdition = "eod";
        task.RequiredPrestigeLevel = 3;
        task.Faction = "bear";

        Assert.Equal("EOD", StatusTextFor(task, Settings(faction: "usec")));
        Assert.Equal("P.3", StatusTextFor(task, Settings(faction: "usec", hasEod: true)));
        Assert.Equal(
            "BEAR",
            StatusTextFor(task, Settings(faction: "usec", hasEod: true, prestige: 3)));
    }

    [Fact]
    public void An_unavailable_quest_with_no_gate_to_name_still_reads_N_A()
    {
        // The engine answers Unavailable for those four gates alone, so this is the defensive
        // tail: a status handed in without one renders as the status word, never as an empty
        // badge.
        Assert.Equal(
            "N/A", Format(QuestStatus.Unavailable, QuestGate.None, Quest("Prapor"), Settings()));
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

    /// <summary>
    /// The engine and the badge, driven together down the whole walk: one quest failing every
    /// gate there is, with the gates cleared one at a time, asserting each time that the badge
    /// names THE GATE THE ENGINE ACTUALLY STOPPED AT.
    /// <para>
    /// This is the case the split into two files needed and did not have. The badge used to
    /// re-walk the same predicates in an order hand-copied from the engine, with nothing tying
    /// the two together: reorder the walk and every other case here still passed while the badge
    /// pointed the player at a requirement that was not the next one to clear. Nothing below
    /// writes that order down - the expectation is keyed BY GATE, and the order the states visit
    /// them in is read back from the engine.
    /// </para>
    /// </summary>
    [Fact]
    public void The_badge_names_the_gate_the_engine_stopped_at_at_every_step_of_the_walk()
    {
        // What each gate has to name, per gate. Two of them stand for a whole quest rather than
        // a value, so their badge is the status word.
        var expected = new Dictionary<QuestGate, string>
        {
            [QuestGate.RequiredEdition] = "EOD",
            [QuestGate.ExcludedEdition] = "Edition",
            [QuestGate.PrestigeLevel] = "P.2",
            [QuestGate.Faction] = "BEAR",
            [QuestGate.DecodeCount] = "Locked",
            [QuestGate.Prerequisite] = "Locked",
            [QuestGate.PlayerLevel] = "Lv.40",
            [QuestGate.ScavKarma] = "Rep 4",
            [QuestGate.TraderLoyalty] = "LL3",
            [QuestGate.None] = "Active",
        };

        var prerequisite = Quest("prereq", "Prapor");
        var task = Quest("q", "Prapor", (Prapor, "Prapor", 3));
        task.RequiredEdition = "eod";
        task.ExcludedEdition = "unheard";
        task.RequiredPrestigeLevel = 2;
        task.Faction = "bear";
        task.RequiredDecodeCount = 2;
        task.RequiredLevel = 40;
        task.RequiredScavKarma = 4.0;
        task.Previous = new List<string> { "prereq" };

        var prereqDone = ProgressSnapshot.From(
            "profile", 0,
            new Dictionary<string, QuestStatus> { ["prereq"] = QuestStatus.Done },
            new Dictionary<string, bool>());

        // Each state clears one more requirement than the one above it, in no order this test
        // asserts: what it asserts is that whatever gate the engine names, the badge names it too.
        var states = new List<(ProfileSettingsSnapshot Settings, ProgressSnapshot? Progress)>
        {
            (Barred(), null),
            (Barred(hasEod: true), null),
            (Barred(hasEod: true, hasUnheard: false), null),
            (Barred(hasEod: true, hasUnheard: false, prestige: 2), null),
            (Barred(hasEod: true, hasUnheard: false, prestige: 2, faction: "bear"), null),
            (Barred(hasEod: true, hasUnheard: false, prestige: 2, faction: "bear", dsp: 2), null),
            (Barred(hasEod: true, hasUnheard: false, prestige: 2, faction: "bear", dsp: 2),
                prereqDone),
            (Barred(hasEod: true, hasUnheard: false, prestige: 2, faction: "bear", dsp: 2,
                playerLevel: 40), prereqDone),
            (Barred(hasEod: true, hasUnheard: false, prestige: 2, faction: "bear", dsp: 2,
                playerLevel: 40, scavRep: 4.0), prereqDone),
            (Barred(hasEod: true, hasUnheard: false, prestige: 2, faction: "bear", dsp: 2,
                playerLevel: 40, scavRep: 4.0, praporLoyalty: 3), prereqDone),
        };

        var visited = new List<QuestGate>();
        foreach (var (settings, progress) in states)
        {
            var (status, gate) = Walk(task, settings, progress, prerequisite, task);
            var badge = QuestRequirementBadge.StatusText(
                status, gate, task, settings, EnglishName);

            Assert.True(
                expected.ContainsKey(gate),
                $"the walk reported the gate {gate}, which nothing here says how to render");
            Assert.Equal(expected[gate], badge);
            visited.Add(gate);
        }

        // Every gate was actually exercised, and each exactly once: without this the chain could
        // skip one and still be green. Compared as a set, because the order is the engine's.
        Assert.Equal(
            expected.Keys.OrderBy(g => g).ToList(),
            visited.OrderBy(g => g).ToList());
    }

    /// <summary>
    /// A profile barred by every gate the quest in the coupling case carries, with the named
    /// values cleared. Every default here fails its gate.
    /// </summary>
    private static ProfileSettingsSnapshot Barred(
        bool hasEod = false,
        bool hasUnheard = true,
        int prestige = 0,
        string faction = "usec",
        int? dsp = null,
        int playerLevel = 15,
        double scavRep = 1.0,
        int? praporLoyalty = null)
    {
        var settings = Settings(
            playerLevel: playerLevel,
            scavRep: scavRep,
            faction: faction,
            hasEod: hasEod,
            hasUnheard: hasUnheard,
            prestige: prestige,
            loyalty: praporLoyalty == null
                ? Array.Empty<(string, int)>()
                : new[] { (Prapor, praporLoyalty.Value) });

        // The one value LoyaltyFixtures.Settings does not name, because no other suite gates on
        // it: the DSP decode count, which is met only on an exact match.
        return settings with { DspDecodeCount = dsp };
    }
}
