using static TarkovHelper.Tests.QuestTabDriver;

namespace TarkovHelper.Tests;

/// <summary>
/// End-to-end coverage for preserve-quest-filters-on-navigation (see
/// feature-preserve-quest-filters-on-navigation.spec.md): navigating to a quest,
/// via a prerequisite link, a recommendation, or a quest link on the Items/Collector
/// tabs, must never change the quest-list filters. When the target is hidden by the
/// current filters, only the detail panel switches, the list selection clears, and
/// a notice offers the explicit "show in list" reset.
///
/// The notice's Border (PnlFilteredOutNotice) has no UI Automation peer (WPF panels
/// and borders are invisible to UIA), so its Button, BtnShowInList, is the probe for
/// "notice visible" throughout.
///
/// Quest/prerequisite pairs come from E2EQuestData (derived from the bundled
/// tarkov_data.db, so the tests survive database updates). All tests run against a
/// fresh profile: no progress, default player level, so a quest with prerequisites
/// is Locked and a prerequisite-free quest is Active. The shared query also
/// guarantees the prerequisite completes without the quest-complete confirmation
/// dialog (empty cascade), which Detail_buttons_act_on_the_shown_quest_while_it_is_hidden_by_filters
/// relies on.
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class QuestNavigationE2ETests : E2ETestBase
{
    // ---------- shared choreography ----------

    /// <summary>
    /// On the Quests tab: filter to Locked + the quest's name, open the quest
    /// (shared QuestTabDriver.ShowQuestDetail choreography), and click its prerequisite
    /// link, landing in the "shown quest hidden by filters" state (the prerequisite
    /// is Active, so the Locked filter excludes it).
    /// </summary>
    private static void NavigateToHiddenPrereq(AppDriver app, string questName, string prereqName)
    {
        ShowQuestDetail(app, questName, "Locked");

        app.ClickTextElementWithScroll(prereqName, "PrerequisitesList", "DetailScrollViewer");
        WaitUntil(() => app.GetElementText("TxtDetailName") == prereqName,
            $"detail panel to show prerequisite '{prereqName}'");
    }

    // ---------- tests ----------

    [E2EFact]
    public void Prerequisite_link_preserves_filters_and_show_in_list_is_the_explicit_reset()
    {
        var (questName, prereqName) = E2EQuestData.FindLockedQuestWithActivePrereq();
        using var app = LaunchMaximized();

        NavigateToHiddenPrereq(app, questName, prereqName);

        // Filters and search survive the navigation; the hidden target shows a notice
        // and no list row claims to be it.
        WaitForSelectedStatusChip(app, "Locked");
        Assert.Equal(questName, app.GetTextBoxValue("TxtSearch"));
        app.WaitForElementVisibility("BtnShowInList", visible: true);
        Assert.False(app.ListHasSelection("LstQuests"),
            "a list row stayed selected while the panel shows a filtered-out quest");

        // The notice's button is the explicit escape hatch: it performs the old
        // reset-and-highlight behavior.
        app.InvokeElement("BtnShowInList");
        WaitForSelectedStatusChip(app, "All");
        WaitUntil(() => app.GetTextBoxValue("TxtSearch") == "", "search box to be cleared");
        app.WaitForElementVisibility("BtnShowInList", visible: false);
        app.WaitForListSelection("LstQuests", hasSelection: true);
        Assert.Equal(prereqName, app.GetElementText("TxtDetailName"));
    }

    [E2EFact]
    public void Detail_buttons_act_on_the_shown_quest_while_it_is_hidden_by_filters()
    {
        var (questName, prereqName) = E2EQuestData.FindLockedQuestWithActivePrereq();
        using var app = LaunchMaximized();

        NavigateToHiddenPrereq(app, questName, prereqName);
        app.WaitForElementVisibility("BtnShowInList", visible: true);

        // The list selection is intentionally null in this state, but the detail
        // panel's action buttons must still act on the shown quest: Mark Complete
        // completes the prerequisite, which flips its button row (Complete hides,
        // Reset appears) via the progress-change refresh.
        app.InvokeElement("BtnComplete");
        app.WaitForElementVisibility("BtnComplete", visible: false);
        app.WaitForElementVisibility("BtnReset", visible: true);

        // Cross-check through the list: the prerequisite is now Done, so the Done
        // filter reveals it and reconciliation re-selects it.
        app.SetTextBoxValue("TxtSearch", "");
        SelectStatusChip(app, "Done");
        app.WaitForElementVisibility("BtnShowInList", visible: false);
        app.WaitForListSelection("LstQuests", hasSelection: true);
        Assert.Equal(prereqName, app.GetElementText("TxtDetailName"));
    }

    [E2EFact]
    public void Ctrl_click_deselect_collapses_the_detail_panel()
    {
        using var app = LaunchMaximized();

        app.SelectTab("TabQuests", "LstQuests");
        app.SelectListItemAt("LstQuests", 0);
        WaitUntil(() => app.IsElementVisible("TxtDetailName"), "detail panel to render");

        // Ctrl+Click on the selected row toggles the selection off; the panel must
        // return to the empty placeholder instead of resurrecting the quest.
        app.CtrlClickElement(app.GetListItemAt("LstQuests", 0));
        app.WaitForListSelection("LstQuests", hasSelection: false);
        WaitUntil(() => !app.IsElementVisible("TxtDetailName"), "detail panel to collapse");
        WaitUntil(() => app.IsElementVisible("TxtSelectQuest"), "select-a-quest placeholder to appear");
    }

    [E2EFact]
    public void Filter_change_that_reveals_the_shown_quest_selects_it_and_hides_the_notice()
    {
        var (questName, prereqName) = E2EQuestData.FindLockedQuestWithActivePrereq();
        using var app = LaunchMaximized();

        NavigateToHiddenPrereq(app, questName, prereqName);
        app.WaitForElementVisibility("BtnShowInList", visible: true);

        // Clearing the search alone does not reveal the prerequisite (it is Active,
        // the filter still says Locked), so the notice must stay truthful. Wait for the
        // debounced clear to actually apply first: the list was narrowed to one row by
        // the search, so it widening past one row is the signal. Without the wait both
        // assertions below hold before the clear lands and could never fail.
        app.SetTextBoxValue("TxtSearch", "");
        WaitUntil(() => app.GetListItemCount("LstQuests") > 1,
            "the cleared search to widen the quest list");
        WaitForSelectedStatusChip(app, "Locked");
        app.WaitForElementVisibility("BtnShowInList", visible: true);

        // Switching the status filter to Active reveals it: reconciliation selects it
        // in the list and the notice disappears.
        SelectStatusChip(app, "Active");
        app.WaitForElementVisibility("BtnShowInList", visible: false);
        app.WaitForListSelection("LstQuests", hasSelection: true);
        Assert.Equal(prereqName, app.GetElementText("TxtDetailName"));
    }

    [E2EFact]
    public void Items_page_quest_link_preserves_quest_tab_filters()
    {
        var questNames = E2EQuestData.AllQuestNames();
        using var app = LaunchMaximized();

        // Park the quest tab on a non-default filter, then leave the tab.
        app.SelectTab("TabQuests", "LstQuests");
        SelectStatusChip(app, "Locked");
        app.SelectTab("TabItems", "LstItems", bounceTabAutomationId: "TabQuests");

        var clicked = ClickFirstQuestLinkInItemList(app, questNames);

        // The click lands back on the Quests tab; the filter must still say Locked
        // (the old behavior reset it to All), and the panel/list must agree.
        app.SelectTab("TabQuests", "LstQuests");
        WaitForSelectedStatusChip(app, "Locked");
        WaitUntil(() => app.GetElementText("TxtDetailName") == clicked,
            $"quest detail to show '{clicked}'");
        WaitUntil(
            () => app.IsElementVisible("BtnShowInList") != app.ListHasSelection("LstQuests"),
            "exactly one of: filtered-out notice, or a selected list row");
    }

    [E2EFact]
    public void Collector_page_quest_link_preserves_quest_tab_filters()
    {
        var questNames = E2EQuestData.AllQuestNames();
        using var app = LaunchMaximized();

        app.SelectTab("TabQuests", "LstQuests");
        SelectStatusChip(app, "Done");
        app.SelectTab("TabCollector", "LstItems", bounceTabAutomationId: "TabQuests");

        var clicked = ClickFirstQuestLinkInItemList(app, questNames);

        // Fresh profile: nothing is Done, so the target is necessarily hidden, so the
        // filter must survive as Done with the notice up and no selection.
        app.SelectTab("TabQuests", "LstQuests");
        WaitForSelectedStatusChip(app, "Done");
        WaitUntil(() => app.GetElementText("TxtDetailName") == clicked,
            $"quest detail to show '{clicked}'");
        app.WaitForElementVisibility("BtnShowInList", visible: true);
        Assert.False(app.ListHasSelection("LstQuests"));
    }

    [E2EFact]
    public void Recommendation_click_preserves_filters_when_target_is_filtered_out()
    {
        var questNames = E2EQuestData.AllQuestNames();
        using var app = LaunchMaximized();

        app.SelectTab("TabQuests", "LstQuests");
        app.WaitForElementVisibility("RecommendationsExpander", visible: true, timeoutSeconds: 60);
        app.ExpandElement("RecommendationsExpander");

        // Pick a recommendation row's quest-name text that is actually on screen:
        // an expanded panel can have more rows than fit the window, and off-screen
        // rows expose no clickable point.
        System.Windows.Automation.AutomationElement? target = null;
        string? recommended = null;
        WaitUntil(() =>
        {
            foreach (var element in app.TryGetTextElements("RecommendationsList"))
            {
                var name = element.Current.Name;
                if (!questNames.Contains(name)) continue;
                try
                {
                    element.GetClickablePoint();
                }
                catch (System.Windows.Automation.NoClickablePointException)
                {
                    continue;
                }
                target = element;
                recommended = name;
                return true;
            }
            return false;
        }, "a clickable recommendation row naming a known quest");

        // Make the list show nothing, so the recommended quest is filtered out. The
        // search is debounced, so wait for the zero-result state (its Reset button is
        // the probe) before clicking: a click that beats the debounce would take
        // SelectQuestInternal's *visible* branch against the still-unfiltered list, and
        // the assertions below would still pass once the tick landed, silently
        // exercising the opposite path from the one this test names.
        const string noMatchSearch = "e2e-no-such-quest";
        app.SetTextBoxValue("TxtSearch", noMatchSearch);
        app.WaitForElementVisibility("BtnResetFilters", visible: true);

        app.ClickElement(target!);

        WaitUntil(() => app.GetElementText("TxtDetailName") == recommended,
            $"quest detail to show '{recommended}'");
        Assert.Equal(noMatchSearch, app.GetTextBoxValue("TxtSearch"));
        WaitForSelectedStatusChip(app, "Active");
        app.WaitForElementVisibility("BtnShowInList", visible: true);
    }

    /// <summary>
    /// Walks the Items/Collector item list top-down until an item's detail exposes a
    /// "Required for Quests" link, clicks that link, and returns the quest name. Both
    /// pages share the LstItems / QuestRequirementsList / DetailScrollViewer ids.
    /// QuestRequirementsList drops out of the UIA tree while its section is collapsed,
    /// so the probe uses the non-waiting TryGetTextElements.
    /// </summary>
    private static string ClickFirstQuestLinkInItemList(AppDriver app, HashSet<string> questNames)
    {
        for (var index = 0; index < 10; index++)
        {
            app.SelectListItemAt("LstItems", index);

            // Give the detail panel a moment to render, then look for a quest link.
            string? questLink = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (questLink == null && DateTime.UtcNow < deadline)
            {
                questLink = app.TryGetTextElements("QuestRequirementsList")
                    .Select(e => e.Current.Name)
                    .FirstOrDefault(questNames.Contains);
                if (questLink == null) Thread.Sleep(250);
            }
            if (questLink == null) continue; // item has no quest requirement, so try the next one

            app.ClickTextElementWithScroll(questLink, "QuestRequirementsList", "DetailScrollViewer");
            return questLink;
        }

        Assert.Fail("none of the first 10 items exposed a quest requirement link");
        return null!; // unreachable
    }
}
