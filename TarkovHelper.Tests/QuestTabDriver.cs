using TarkovHelper.Pages;

namespace TarkovHelper.Tests;

/// <summary>
/// Page object for the Quests tab: the status-chip AutomationId convention, the chip
/// row's selection semantics, and the search/select/detail choreography the quest e2e
/// suites share.
///
/// Lives here rather than on <see cref="E2ETestBase"/> so that base class stays what
/// its own doc says it is: page-agnostic scaffolding (throwaway Config dirs, launch,
/// cleanup, the shared poll loop) for the map, header and window-bounds suites that
/// inherit it and have no business seeing QuestListPage's naming conventions.
/// </summary>
internal static class QuestTabDriver
{
    /// <summary>The status chip's AutomationId for a tag (QuestListPage.xaml names them "Chip" + tag).</summary>
    internal static string StatusChipId(string tag) => "Chip" + tag;

    /// <summary>
    /// The status-filter state as the chip row reports it: the tags whose chips
    /// currently publish <see cref="QuestStatusTags.ChipSelected"/>. Normally exactly
    /// one; returning the whole set is what lets callers assert the EXCLUSIVITY the
    /// deleted status ComboBox used to guarantee structurally (a SelectionPattern can
    /// report at most one selected item; per-element ItemStatus strings cannot).
    /// </summary>
    internal static string[] SelectedStatusChips(AppDriver app)
        => QuestStatusTags.ChipTags
            .Where(tag => app.TryGetItemStatus(StatusChipId(tag)) == QuestStatusTags.ChipSelected)
            .ToArray();

    /// <summary>
    /// Polls until exactly the tag's chip reports selected: the assert-only probe for
    /// the quest list's status filter (the chips hold the only status-filter state).
    /// Flows that assert a pre-existing selection (e.g. relaunch restore) must use
    /// THIS, never <see cref="SelectStatusChip"/>, whose click path could mutate the
    /// very state under test.
    ///
    /// The timeout message names whatever IS selected instead: the combo assertions
    /// this replaced were `Assert.Equal`s that printed expected-vs-actual immediately,
    /// and a bare "did not become selected" after 30s would lose that diagnostic.
    /// </summary>
    internal static void WaitForSelectedStatusChip(AppDriver app, string tag)
        => AppDriver.PollUntil(
            () => SelectedStatusChips(app) is [var only] && only == tag,
            DateTime.UtcNow + TimeSpan.FromSeconds(30),
            () => $"status chip '{tag}' to become the only selected chip; "
                  + $"selected now: [{string.Join(", ", SelectedStatusChips(app))}]");

    /// <summary>
    /// Selects a status chip idempotently: waits for the chips to initialize (a chip
    /// publishes a non-empty ItemStatus only once the page has loaded its data and is
    /// acting on clicks; see QuestListPage.UpdateStatusChips), invokes the chip only
    /// when it is not already selected (a blind click on the selected chip would
    /// TOGGLE the filter back to "All"), and then waits for it to report Selected.
    /// </summary>
    internal static void SelectStatusChip(AppDriver app, string tag)
    {
        var chipId = StatusChipId(tag);
        // GetItemStatus (not TryGetItemStatus) so a missing chip fails fast with the
        // AutomationId in the message rather than idling out this readiness wait.
        AppDriver.PollUntil(() => !string.IsNullOrEmpty(app.GetItemStatus(chipId)),
            $"status chips to initialize (chip '{tag}')");
        if (app.GetItemStatus(chipId) != QuestStatusTags.ChipSelected)
            app.InvokeElement(chipId);
        WaitForSelectedStatusChip(app, tag);
    }

    /// <summary>
    /// Shared quest-tab choreography: applies the status filter, searches the quest
    /// (a unique substring per the E2EQuestData queries), selects the single
    /// surviving row, and waits for its detail panel.
    /// </summary>
    internal static void ShowQuestDetail(AppDriver app, string questName, string statusFilter)
    {
        app.SelectTab("TabQuests", "LstQuests");
        SelectStatusChip(app, statusFilter);
        app.SetTextBoxValue("TxtSearch", questName);
        // The search filter is debounced (QuestListPage.TxtSearch_TextChanged), so wait
        // for it to apply before touching row 0, or this could grab the first
        // row of the still-unfiltered list. The E2EQuestData queries guarantee the
        // name is a unique search substring, so exactly one row survives.
        AppDriver.PollUntil(() => app.GetListItemCount("LstQuests") == 1,
            $"quest list to filter down to '{questName}'");
        app.SelectListItemAt("LstQuests", 0);
        AppDriver.PollUntil(() => app.GetElementText("TxtDetailName") == questName,
            $"detail panel to show '{questName}'");
    }
}
