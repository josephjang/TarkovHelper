using TarkovHelper.Pages;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// End-to-end coverage for feature-quest-loyalty-gating.md: a quest gated on a trader's loyalty
/// shows as locked with a badge naming the level, one click in the profile drawer clears it, the
/// value survives a restart, and it belongs to the profile it was entered under.
/// <para>
/// Every step of that is only real through the running app: the gate is in the status engine, the
/// input is built at runtime from the database, the badge is a page concern and the row is in
/// user_data.db. The unit suites pin each of those separately; this is the one thing that says
/// they add up to a player being able to unlock a quest by telling the app their loyalty.
/// </para>
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class QuestLoyaltyE2ETests : E2ETestBase
{
    /// <summary>
    /// A config dir with log monitoring off, so the machine's own EFT logs cannot switch the
    /// profile out from under a test that asserts which profile a row landed in.
    /// </summary>
    private string NewQuietConfigDir()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "False");
        return configDir;
    }

    private static string LoyaltyButtonId(string traderNormalizedName, int level)
        => $"Loyalty_{traderNormalizedName}_{level}";

    /// <summary>Opens the drawer if it is closed, and waits for the loyalty inputs to be there.</summary>
    private static void OpenDrawer(AppDriver app, string traderNormalizedName, int level)
    {
        app.WaitForElement("BtnProfile");
        if (!app.IsElementVisible("TxtPlayerLevel")) app.InvokeElement("BtnProfile");
        app.WaitForElementVisibility(LoyaltyButtonId(traderNormalizedName, level), visible: true);
    }

    [E2EFact]
    public void A_loyalty_gated_quest_unlocks_when_its_traders_level_is_entered()
    {
        var (questName, _, trader, level) = E2EQuestData.FindLoyaltyGatedQuest();
        var configDir = NewQuietConfigDir();
        using var app = LaunchMaximized(configDir);

        // Locked, with a badge naming the level rather than the bare word "Level": before this
        // phase the detail badge said "Level" for every level-locked quest, which told the player
        // nothing about which requirement was holding it.
        QuestTabDriver.ShowQuestDetail(app, questName, "All");
        WaitUntil(() => app.GetElementText("TxtDetailStatus") == $"LL{level}",
            $"'{questName}' to show the LL{level} badge before any loyalty is entered");

        // One click in the drawer, on the group the roster built for this trader.
        OpenDrawer(app, trader, level);
        app.InvokeElement(LoyaltyButtonId(trader, level));
        WaitUntil(
            () => app.TryGetItemStatus(LoyaltyButtonId(trader, level)) == QuestStatusTags.ChipSelected,
            $"the level {level} button for '{trader}' to report selected");

        // ...and the quest is available, without reopening or re-searching anything: the drawer
        // edit refreshes the list and the detail pane the way a level edit does.
        WaitUntil(() => app.GetElementText("TxtDetailStatus") == "Active",
            $"'{questName}' to become Active once {trader} level {level} is entered");
    }

    [E2EFact]
    public void An_entered_loyalty_level_survives_a_restart()
    {
        var (questName, _, trader, level) = E2EQuestData.FindLoyaltyGatedQuest();
        var configDir = NewQuietConfigDir();

        using (var app = LaunchMaximized(configDir))
        {
            OpenDrawer(app, trader, level);
            app.InvokeElement(LoyaltyButtonId(trader, level));
            WaitUntil(
                () => app.TryGetItemStatus(LoyaltyButtonId(trader, level)) == QuestStatusTags.ChipSelected,
                "the entered level to be selected before the restart");
            app.CloseAndWaitForExit();
        }

        // The row is durable and under the active profile, not the account-wide settings.
        var traderId = TraderIdOf(trader);
        Assert.Equal(
            level.ToString(),
            E2EDb.ReadProfileSetting(
                configDir, ProfileService.PvpProfileId, SettingsService.TraderLoyaltyKey(traderId)));

        using (var restarted = LaunchMaximized(configDir))
        {
            // Read back through the app: the quest is Active from the first paint, and the
            // button the reload painted reports the stored level.
            QuestTabDriver.ShowQuestDetail(restarted, questName, "All");
            WaitUntil(() => restarted.GetElementText("TxtDetailStatus") == "Active",
                $"'{questName}' to still be Active after a restart");

            OpenDrawer(restarted, trader, level);
            WaitUntil(
                () => restarted.TryGetItemStatus(LoyaltyButtonId(trader, level))
                      == QuestStatusTags.ChipSelected,
                "the stored level to be selected after the restart");
        }
    }

    /// <summary>
    /// Loyalty is per profile (PRD R5). This is also the case that would fail if the drawer
    /// repainted only on the per-trader event: the PvE profile has no loyalty row, so switching
    /// to it announces no loyalty event at all, and a drawer that did not repaint from the
    /// snapshot would keep showing the PvP profile's selection under the PvE profile's name.
    /// </summary>
    [E2EFact]
    public void A_level_entered_in_one_profile_does_not_reach_another()
    {
        var (questName, _, trader, level) = E2EQuestData.FindLoyaltyGatedQuest();
        var configDir = NewQuietConfigDir();
        using var app = LaunchMaximized(configDir);

        OpenDrawer(app, trader, level);
        app.InvokeElement(LoyaltyButtonId(trader, level));
        QuestTabDriver.ShowQuestDetail(app, questName, "All");
        WaitUntil(() => app.GetElementText("TxtDetailStatus") == "Active",
            $"'{questName}' to be Active in the profile the level was entered under");

        // The profile toggles are radio buttons: selected, not invoked.
        app.SelectElement("BtnPveZone");
        WaitUntil(() => app.GetItemStatus("BtnPveZone") == "Selected", "PvE Zone to be selected");

        // The other profile has entered nothing, so the quest is locked again...
        WaitUntil(() => app.GetElementText("TxtDetailStatus") == $"LL{level}",
            $"'{questName}' to read LL{level} again under a profile that entered no loyalty");

        // ...and the drawer says so too, rather than still showing the other profile's level.
        OpenDrawer(app, trader, level);
        WaitUntil(
            () => app.TryGetItemStatus(LoyaltyButtonId(trader, level)) == QuestStatusTags.ChipUnselected,
            "the drawer to stop showing the other profile's entered level");
        WaitUntil(
            () => app.TryGetItemStatus(LoyaltyButtonId(trader, SettingsService.DefaultTraderLoyaltyLevel))
                  == QuestStatusTags.ChipSelected,
            "the drawer to fall back to level 1 for a profile with no entry");
    }

    /// <summary>
    /// The chip counts move with the gate: a quest that leaves Locked has to arrive in Active,
    /// or the row's badge and the row's count would be telling the player different things.
    /// </summary>
    [E2EFact]
    public void Unlocking_a_quest_moves_it_from_the_locked_chip_to_the_active_chip()
    {
        var (_, _, trader, level) = E2EQuestData.FindLoyaltyGatedQuest();
        using var app = LaunchMaximized(NewQuietConfigDir());

        app.SelectTab("TabQuests", "LstQuests");
        QuestTabDriver.SelectStatusChip(app, QuestStatusTags.All);
        var lockedBefore = ChipCount(app, QuestStatusTags.Locked);
        var activeBefore = ChipCount(app, QuestStatusTags.Active);
        Assert.True(lockedBefore > 0, "no quest is locked before any loyalty is entered");

        OpenDrawer(app, trader, level);
        app.InvokeElement(LoyaltyButtonId(trader, level));

        // At least one quest moved, and every quest that left Locked arrived in Active: the
        // entered level cannot make a quest vanish from the list or turn Unavailable.
        WaitUntil(() => ChipCount(app, QuestStatusTags.Locked) < lockedBefore,
            "the Locked chip count to fall once a trader's loyalty is entered");
        var lockedAfter = ChipCount(app, QuestStatusTags.Locked);
        var activeAfter = ChipCount(app, QuestStatusTags.Active);
        Assert.Equal(lockedBefore - lockedAfter, activeAfter - activeBefore);
    }

    /// <summary>
    /// The chip's own label carries its count ("Locked 12"), which is the only place the e2e can
    /// read it from.
    /// </summary>
    private static int ChipCount(AppDriver app, string tag)
    {
        var label = app.GetElementText(QuestTabDriver.StatusChipId(tag));
        var parts = label.Split(' ');
        Assert.True(parts.Length >= 2, $"status chip '{tag}' reads '{label}', which carries no count");
        return int.Parse(parts[^1]);
    }

    /// <summary>The trader id behind a NormalizedName, for the stored-row assertions.</summary>
    private static string TraderIdOf(string normalizedName)
    {
        Assert.True(TraderDbService.Instance.LoadTradersAsync().GetAwaiter().GetResult(),
            "asset db traders did not load");
        var trader = TraderDbService.Instance.AllTraders.Single(
            t => string.Equals(t.NormalizedName, normalizedName, StringComparison.OrdinalIgnoreCase));
        return trader.Id;
    }
}
