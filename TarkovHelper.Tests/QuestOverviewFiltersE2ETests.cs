using TarkovHelper.Pages;
using static TarkovHelper.Tests.QuestTabDriver;

namespace TarkovHelper.Tests;

/// <summary>
/// End-to-end coverage for feature-quest-overview-filters and its
/// feature-quest-chip-only-status-filter successor: the status chips as the sole
/// status filter (with the All chip and toggle-to-All), the zero-results empty state
/// with its reset button, and quest-tab filter persistence across an app relaunch
/// against the same Config dir.
///
/// Status-filter state is read through the chips' UIA ItemStatus
/// (QuestStatusTags.ChipSelected/ChipUnselected, published by UpdateStatusChips) via
/// QuestTabDriver.WaitForSelectedStatusChip / SelectStatusChip. Those helpers assert
/// EXCLUSIVITY (exactly one chip selected) because, unlike the deleted ComboBox's
/// SelectionPattern, per-element ItemStatus strings carry no such guarantee.
///
/// PnlEmptyState is a StackPanel with no UI Automation peer, so its Button
/// (BtnResetFilters) is the probe for "empty state visible", the same pattern
/// QuestNavigationE2ETests uses for PnlFilteredOutNotice/BtnShowInList.
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class QuestOverviewFiltersE2ETests : E2ETestBase
{
    [E2EFact]
    public void Empty_state_appears_at_zero_results_and_reset_restores_the_list()
    {
        using var app = LaunchMaximized();
        app.SelectTab("TabQuests", "LstQuests");

        // No quest name contains this, so the (debounced) search filters to zero rows.
        app.SetTextBoxValue("TxtSearch", "e2e-no-such-quest");
        app.WaitForElementVisibility("BtnResetFilters", visible: true);
        WaitUntil(() => app.GetListItemCount("LstQuests") == 0, "quest list to become empty");

        app.InvokeElement("BtnResetFilters");
        app.WaitForElementVisibility("BtnResetFilters", visible: false);
        WaitUntil(() => app.GetListItemCount("LstQuests") > 0, "quest list to repopulate");
        Assert.Equal("", app.GetTextBoxValue("TxtSearch"));
        // ResetFilters lands on the most-permissive "All", not the "Active" default.
        WaitForSelectedStatusChip(app, "All");
    }

    [E2EFact]
    public void Status_chips_are_the_sole_status_filter_with_All_first_and_toggle_to_All()
    {
        using var app = LaunchMaximized();
        app.SelectTab("TabQuests", "LstQuests");
        WaitForSelectedStatusChip(app, "Active"); // the fresh-profile default, so the flows below are deterministic

        // Every chip is rendered and reachable: the whole tag table, not just the ones
        // this test clicks (a clipped or missing chip is a status the user cannot pick).
        foreach (var tag in QuestStatusTags.ChipTags)
        {
            Assert.True(app.IsElementVisible(StatusChipId(tag)), $"status chip '{tag}' should be on screen");
        }

        // Clicking a status chip applies that status; the previous chip deselects.
        app.InvokeElement(StatusChipId("Done"));
        WaitForSelectedStatusChip(app, "Done");
        Assert.Equal(QuestStatusTags.ChipUnselected, app.GetItemStatus(StatusChipId("Active")));

        // The All chip is the new direct gesture back to the unfiltered list.
        app.InvokeElement(StatusChipId("All"));
        WaitForSelectedStatusChip(app, "All");

        // Clicking All while All is selected is a no-op (PRD R3): the selection stays
        // and the list is untouched. Asserted against the list length so the check
        // cannot pass merely because the chip visuals were left alone.
        var allCount = app.GetListItemCount("LstQuests");
        app.InvokeElement(StatusChipId("All"));
        WaitForSelectedStatusChip(app, "All");
        Assert.Equal(allCount, app.GetListItemCount("LstQuests"));

        // Re-clicking the selected status chip still toggles back to All.
        app.InvokeElement(StatusChipId("Done"));
        WaitForSelectedStatusChip(app, "Done");
        app.InvokeElement(StatusChipId("Done"));
        WaitForSelectedStatusChip(app, "All");

        // Regression pins for the combo/stats removal and the chip relabel: the
        // status ComboBox and the "Lv.X | n/m" stats text are gone, and every chip
        // reads "<tag> <count>", which is how the Unavailable chip now says
        // "Unavailable" rather than "N/A".
        Assert.False(app.IsElementVisible("CmbStatus"),
            "the status ComboBox should be removed: the chips are the only status filter");
        // "TxtStats" is an x:Name four other views also use; this holds because
        // MainWindow shows one page at a time, so only the Quests tab is in the tree.
        Assert.False(app.IsElementVisible("TxtStats"),
            "the stats text should be removed: its counts live on the chips now");
        foreach (var tag in QuestStatusTags.ChipTags)
        {
            Assert.Matches($@"^{tag} \d+$", app.GetElementText(StatusChipId(tag)));
        }
    }

    [E2EFact]
    public void Each_chips_count_is_the_number_of_rows_clicking_it_produces()
    {
        // The chips' whole promise (PRD R2) is "the number IS what clicking shows".
        // CountByStatusTag and ApplyFilters are separate passes over the real quest
        // database, so only an end-to-end check can catch them drifting apart: a
        // criteria snapshot fed to one but not the other, or a chip wired to the wrong
        // tag's count. Unit tests pin the same invariant on 3-8 synthetic quests.
        using var app = LaunchMaximized();
        app.SelectTab("TabQuests", "LstQuests");
        WaitForSelectedStatusChip(app, "Active");

        foreach (var tag in QuestStatusTags.ChipTags)
        {
            SelectStatusChip(app, tag);
            var previewed = ChipCount(app, tag);
            // GetListItemCount only sees realized containers, so it is exact solely for
            // small counts (see its doc), hence the guard rather than a blanket assert.
            if (previewed > 20) continue;
            WaitUntil(() => app.GetListItemCount("LstQuests") == previewed,
                $"the quest list to show the {previewed} row(s) the '{tag}' chip previewed");
        }
    }

    /// <summary>The integer a status chip's "Label N" content ends with.</summary>
    private static int ChipCount(AppDriver app, string tag)
    {
        var text = app.GetElementText(StatusChipId(tag));
        var digits = text[(text.LastIndexOf(' ') + 1)..];
        Assert.True(int.TryParse(digits, out var count), $"chip '{tag}' should end in a count, got '{text}'");
        return count;
    }

    [E2EFact]
    public void A_search_typed_just_before_leaving_the_tab_is_still_applied_on_return()
    {
        using var app = LaunchMaximized();
        app.SelectTab("TabQuests", "LstQuests");

        // Type and leave immediately: the 250ms debounce has not ticked yet. Unloading
        // must FLUSH that pending apply, not cancel it: a cancelled tick would leave
        // the list showing every quest while the search box still reads the query, and
        // Loaded early-returns on the way back so nothing would ever reconcile them.
        app.SetTextBoxValue("TxtSearch", "e2e-no-such-quest");
        app.SelectTab("TabItems", "LstItems", bounceTabAutomationId: "TabQuests");
        app.SelectTab("TabQuests", "LstQuests");

        Assert.Equal("e2e-no-such-quest", app.GetTextBoxValue("TxtSearch"));
        WaitUntil(() => app.GetListItemCount("LstQuests") == 0,
            "the quest list to agree with the search box after the tab round-trip");
        app.WaitForElementVisibility("BtnResetFilters", visible: true);
    }

    [E2EFact]
    public void An_unknown_persisted_status_tag_falls_back_to_All_not_to_Active()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);
        // A tag no build knows: written by a newer version the user rolled back from,
        // or a hand-edited row. The restore-time Coerce validation must widen it to
        // "All", never narrow it to the "Active" fresh-install default.
        E2EDb.SeedSetting(configDir, "questList.statusTag", "NotAStatus");

        using var app = AppDriver.Launch(configDir);
        app.ShowWindow(Win32.SW_MAXIMIZE);
        app.SelectTab("TabQuests", "LstQuests");

        WaitForSelectedStatusChip(app, "All");
        app.CloseAndWaitForExit();
    }

    [E2EFact]
    public void Filter_state_persists_across_an_app_relaunch()
    {
        var configDir = NewConfigDir();

        using (var app = AppDriver.Launch(configDir))
        {
            app.ShowWindow(Win32.SW_MAXIMIZE);
            app.SelectTab("TabQuests", "LstQuests");

            SelectStatusChip(app, "Done");     // status filter -> Done
            app.ToggleElement("ChkKappaOnly"); // Kappa filter -> on
            WaitUntil(() => app.GetToggleState("ChkKappaOnly"), "Kappa checkbox to check");
            app.SetTextBoxValue("TxtSearch", "transient text"); // must NOT survive the relaunch

            app.CloseAndWaitForExit();
        }

        // The snapshot ApplyFilters persisted is readable straight from user_data.db.
        Assert.Equal("Done", E2EDb.ReadSetting(configDir, "questList.statusTag"));
        Assert.Equal("True", E2EDb.ReadSetting(configDir, "questList.kappaOnly"));

        using (var app = AppDriver.Launch(configDir))
        {
            app.ShowWindow(Win32.SW_MAXIMIZE);
            app.SelectTab("TabQuests", "LstQuests");

            // Assert-only: SelectStatusChip's click path could toggle the restored
            // chip (the very state under test), so only wait for it.
            WaitForSelectedStatusChip(app, "Done");
            Assert.True(app.GetToggleState("ChkKappaOnly"),
                "the Kappa checkbox should be restored checked after a relaunch");
            // Search text is deliberately transient: always empty on a fresh launch.
            Assert.Equal("", app.GetTextBoxValue("TxtSearch"));

            app.CloseAndWaitForExit();
        }
    }
}
