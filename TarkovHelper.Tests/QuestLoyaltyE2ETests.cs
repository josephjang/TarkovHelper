using System.Windows.Automation;
using Microsoft.Data.Sqlite;
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
    /// The badge the list row shows and the badge the detail pane shows are ONE string for one
    /// quest, in whatever language the app is set to.
    /// <para>
    /// Both panes ask <c>QuestRequirementBadge</c>, but they ask it at different moments: the row
    /// caches its answer on the view model, the detail pane rebuilds its own on every pass. So
    /// they can only be proved to agree from outside, and the two ways they drift are exactly
    /// what this drives - a pane that does not pass the task falls back to the bare status word,
    /// and a refresh that does not recompute the cached string leaves the row in the language the
    /// player just left.
    /// </para>
    /// </summary>
    [E2EFact]
    public void The_row_badge_and_the_detail_badge_are_the_same_string_in_either_language()
    {
        var quest = FindCrossTraderLoyaltyQuest();
        var configDir = NewQuietConfigDir();
        E2EDb.SeedSetting(configDir, "app.language", nameof(AppLanguage.EN));
        using var app = LaunchMaximized(configDir);

        // The giver's row is first in badge order, so while it is short both panes read the
        // narrow form: no trader name, nothing language-dependent about it yet.
        QuestTabDriver.ShowQuestDetail(app, quest.QuestName, QuestStatusTags.All);
        WaitUntil(() => RowBadge(app) == $"LL{quest.GiverLevel}",
            $"'{quest.QuestName}' to show the LL{quest.GiverLevel} badge in the list row");
        Assert.Equal(app.GetElementText("TxtDetailStatus"), RowBadge(app));

        // Clearing the giver moves the badge onto the other trader, which is where it starts
        // carrying a NAME. Before the detail pane was handed the task it read the bare "Level"
        // here while the row beside it named the trader.
        OpenDrawer(app, quest.GiverNormalizedName, quest.GiverLevel);
        app.InvokeElement(LoyaltyButtonId(quest.GiverNormalizedName, quest.GiverLevel));
        var englishBadge = $"{quest.OtherTraderName} LL{quest.OtherLevel}";
        WaitUntil(() => RowBadge(app) == englishBadge,
            $"the row badge to name the trader still holding the quest ('{englishBadge}')");
        Assert.Equal(englishBadge, app.GetElementText("TxtDetailStatus"));

        // The Requirements lines do NOT reshuffle as levels are entered: they stay in badge
        // order, the giver first. So what the badge names here is the first line that is not met,
        // which is the later of the two, and the met/unmet colouring is what tells them apart.
        // Located by trader name rather than by index: the section is one list covering every
        // requirement kind, so the quest may legitimately carry a level line too - the query
        // above only asks that its MinLevel be reachable, not that it have none.
        var lines = app.TryGetTextElements("RequirementsList")
            .Select(line => line.Current.Name)
            .ToList();
        var giverLine = lines.FindIndex(
            line => line.Contains(quest.GiverTraderName, StringComparison.Ordinal));
        var otherLine = lines.FindIndex(
            line => line.Contains(quest.OtherTraderName, StringComparison.Ordinal));
        Assert.True(giverLine >= 0,
            $"no Requirements line names the giver '{quest.GiverTraderName}': {string.Join(" | ", lines)}");
        Assert.True(otherLine >= 0,
            $"no Requirements line names '{quest.OtherTraderName}': {string.Join(" | ", lines)}");
        Assert.True(giverLine < otherLine,
            $"the giver's line must come first, but the lines read: {string.Join(" | ", lines)}");

        // ...and the pair still agrees after a language switch. The row's copy is cached, so this
        // is the half that goes stale when the language handler refreshes only the quest names.
        SelectLanguage(app, "한국어");
        var koreanBadge = $"{quest.OtherTraderNameKo} LL{quest.OtherLevel}";
        WaitUntil(() => app.GetElementText("TxtDetailStatus") == koreanBadge,
            $"the detail badge to read '{koreanBadge}' after the switch to Korean");
        WaitUntil(() => RowBadge(app) == koreanBadge,
            $"the row badge to follow the language too rather than staying at '{englishBadge}'");
    }

    /// <summary>
    /// The status badge of the quest list's first row (the single row the search leaves), read
    /// through the per-row AutomationId QuestListPage.xaml stamps on it. Null while the badge is
    /// not in the UIA tree, so callers can poll on it.
    /// </summary>
    private static string? RowBadge(AppDriver app)
        => app.GetListItemAt("LstQuests", 0)
              .FindFirst(TreeScope.Descendants,
                  new PropertyCondition(AutomationElement.AutomationIdProperty, "TxtRowStatus"))
              ?.Current.Name;

    /// <summary>
    /// Switches the app's language through the Settings overlay's combo, by the item's rendered
    /// name (the three items are declared in MainWindow.xaml and carry no AutomationId). Selected
    /// through SelectionItemPattern rather than clicked: the drop-down is a popup window of its
    /// own, which a click would have to chase outside this window's UIA tree.
    /// </summary>
    private static void SelectLanguage(AppDriver app, string itemName)
    {
        app.InvokeElement("BtnSettings");
        app.WaitForElementVisibility("CmbLanguage", visible: true);
        var combo = app.WaitForElement("CmbLanguage");

        // Expanded first because WPF realizes a ComboBox's items only once its popup opens: until
        // then there is no item element to select.
        app.ExpandElement("CmbLanguage");
        AutomationElement? item = null;
        AppDriver.PollUntil(
            () => (item = combo.FindFirst(TreeScope.Descendants, new AndCondition(
                      new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                      new PropertyCondition(AutomationElement.NameProperty, itemName)))) != null,
            $"the language combo to offer '{itemName}'");

        ((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
    }

    /// <summary>
    /// A quest gated on its own trader AND on exactly one other, and on nothing else a fresh
    /// profile fails: entering the giver's level moves the badge onto the other trader, which is
    /// the only badge form carrying a trader name and so the only one that changes with the app's
    /// language.
    /// <para>
    /// Queried here rather than from <see cref="E2EQuestData"/> because this shape is this
    /// suite's alone; the constraints are that class's own (no prerequisite, no alternative, no
    /// categorical gate, reachable at the default level, a unique search substring). The other
    /// trader must have a Korean name that differs from its English one, or the language half of
    /// the assertion would hold whether the page refreshed the row or not.
    /// </para>
    /// </summary>
    private static CrossTraderQuest FindCrossTraderLoyaltyQuest()
    {
        var sql = $@"
            SELECT q.Name, giver.TraderName, gt.NormalizedName, giver.RequiredLevel,
                   other.TraderName, ot.NameKO, other.RequiredLevel
            FROM Quests q
            JOIN QuestTraderRequirements giver
              ON giver.QuestId = q.Id AND lower(giver.TraderName) = lower(q.Trader)
            JOIN QuestTraderRequirements other
              ON other.QuestId = q.Id AND lower(other.TraderName) <> lower(q.Trader)
            JOIN Traders gt ON gt.Id = giver.TraderId
            JOIN Traders ot ON ot.Id = other.TraderId
            WHERE (SELECT COUNT(*) FROM QuestTraderRequirements r2 WHERE r2.QuestId = q.Id) = 2
              AND giver.RequiredLevel >= 2 AND other.RequiredLevel >= 2
              AND gt.NormalizedName IS NOT NULL AND gt.NormalizedName <> ''
              AND ot.NameKO IS NOT NULL AND ot.NameKO <> '' AND ot.NameKO <> ot.Name
              AND NOT EXISTS (SELECT 1 FROM QuestRequirements qr WHERE qr.QuestId = q.Id)
              AND q.Faction IS NULL AND q.RequiredEdition IS NULL
              AND (q.RequiredPrestigeLevel IS NULL OR q.RequiredPrestigeLevel = 0)
              AND (q.RequiredDecodeCount IS NULL OR q.RequiredDecodeCount = 0)
              AND (q.MinLevel IS NULL OR q.MinLevel <= {SettingsService.DefaultPlayerLevel})
              AND q.MinScavKarma IS NULL
              AND NOT EXISTS (SELECT 1 FROM OptionalQuests o
                              WHERE o.QuestId = q.Id OR o.AlternativeQuestId = q.Id)
              AND (SELECT COUNT(*) FROM Quests q2
                       WHERE instr(lower(q2.Name), lower(q.Name)) > 0
                          OR instr(lower(ifnull(q2.NameKO, '')), lower(q.Name)) > 0
                          OR instr(lower(ifnull(q2.NameJA, '')), lower(q.Name)) > 0) = 1
            ORDER BY q.Name
            LIMIT 1";

        using var connection = new SqliteConnection(
            $"Data Source={TestSeed.DatabasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(),
            "tarkov_data.db has no quest gated on its own trader plus exactly one other "
            + "matching the test constraints");
        return new CrossTraderQuest(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt32(6));
    }

    /// <summary>The quest <see cref="FindCrossTraderLoyaltyQuest"/> answers with.</summary>
    private sealed record CrossTraderQuest(
        string QuestName,
        string GiverTraderName,
        string GiverNormalizedName,
        int GiverLevel,
        string OtherTraderName,
        string OtherTraderNameKo,
        int OtherLevel);

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
