using System.Windows.Automation;
using TarkovHelper.Services;
using static TarkovHelper.Tests.QuestTabDriver;

namespace TarkovHelper.Tests;

/// <summary>
/// End-to-end coverage for the quest complete-cascade confirmation dialog (see
/// feature-quest-complete-cascade-confirm.md): completing a quest whose cascade is
/// non-empty must show QuestCompleteConfirmDialog first. Dismissing it (Cancel or
/// the X) changes nothing, Confirm applies the quest plus its cascade verbatim,
/// while a cascade-free completion stays one-click with no dialog. Both halves of
/// the preview are covered: auto-completed prerequisites and the red auto-failed
/// alternatives section, including that Confirm persists the rows to user_data.db
/// (the batch save is fire-and-forget, so UI state alone proves nothing).
///
/// The dialog is an owned top-level window, not a reliable UIA descendant of the
/// main window, so it is located by its window title (AppDriver.WaitForAppWindow)
/// and probed with scope-rooted searches. It is opened via InvokePattern
/// (InvokeElement): WPF's ButtonAutomationPeer raises the click asynchronously
/// (Dispatcher.BeginInvoke), so the invoke returns before the handler enters the
/// modal ShowDialog pump, and unlike a real mouse click, it cannot miss on a layout
/// shift or a denied SetForegroundWindow.
///
/// Test data comes from E2EQuestData on a fresh profile: the locked-quest query
/// guarantees exactly one Active prerequisite and no OptionalQuests involvement
/// (one completion, zero failures); the single-alternative query guarantees
/// exactly one auto-failed alternative.
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class QuestCascadeConfirmE2ETests : E2ETestBase
{
    /// <summary>
    /// Dialog strings derived from the same LocalizationService the dialog reads (EN,
    /// because e2e profiles default to EN and AppDriver.RemoveLegacyLanguageOverride keeps that
    /// true), so editing user-facing copy cannot break these tests. The COUNTS stay
    /// hard-coded on the test side, so a cascade previewing 0 or 2 still fails.
    /// </summary>
    private static readonly LocalizationService Loc = TestLocalization.WithLanguage(AppLanguage.EN);
    private static readonly string DialogTitle = Loc.CascadeConfirmTitle;

    /// <summary>Invokes Mark Complete and waits for the cascade dialog window.</summary>
    private static AutomationElement OpenCascadeDialog(AppDriver app)
    {
        app.InvokeElement("BtnComplete");
        return app.WaitForAppWindow(DialogTitle);
    }

    /// <summary>
    /// Asserts the dialog previews exactly the one guaranteed prerequisite: the
    /// completed-section header counts 1, the prerequisite is listed, and the failed
    /// section (no alternatives by construction) is absent.
    /// </summary>
    private static void AssertDialogPreviewsPrereq(AutomationElement dialog, string prereqName)
    {
        WaitUntil(() => AppDriver.HasTextElementUnder(dialog, prereqName),
            $"cascade dialog to list prerequisite '{prereqName}'");
        Assert.Equal(string.Format(Loc.CascadeCompletedHeaderFormat, 1),
            AppDriver.WaitForElementUnder(dialog, "TxtCascadeCompletedHeader").Current.Name);

        // A Collapsed WPF element exposes no automation peer at all, so the failed
        // section being collapsed means FindFirst returns null; non-null would mean
        // the section is actually rendered.
        var failedHeader = dialog.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "TxtCascadeFailedHeader"));
        Assert.True(failedHeader == null,
            "failed section is visible for a quest without alternatives");
    }

    [E2ETheory]
    [InlineData("BtnCascadeCancel")]
    [InlineData("BtnCascadeClose")]
    public void Locked_quest_completion_shows_dialog_and_dismissing_changes_nothing(string dismissButtonId)
    {
        var (questName, prereqName) = E2EQuestData.FindLockedQuestWithActivePrereq();
        using var app = LaunchMaximized();

        ShowQuestDetail(app, questName, "All");
        var dialog = OpenCascadeDialog(app);
        AssertDialogPreviewsPrereq(dialog, prereqName);

        // Cancel and the X are distinct buttons wired to the same dismiss path; the
        // spec promises every close path leaves the completion unapplied, so each
        // gets exercised.
        AppDriver.Invoke(AppDriver.WaitForElementUnder(dialog, dismissButtonId));
        app.WaitForAppWindowClosed(DialogTitle);

        // Nothing changed on the quest: still completable, no Reset button.
        app.WaitForElementVisibility("BtnComplete", visible: true);
        Assert.False(app.IsElementVisible("BtnReset"), "quest gained a Reset button after dismissing");

        // The prerequisite is untouched too: its detail still offers Mark Complete.
        app.ClickTextElementWithScroll(prereqName, "PrerequisitesList", "DetailScrollViewer");
        WaitUntil(() => app.GetElementText("TxtDetailName") == prereqName,
            $"detail panel to show prerequisite '{prereqName}'");
        app.WaitForElementVisibility("BtnComplete", visible: true);
        Assert.False(app.IsElementVisible("BtnReset"), "prerequisite gained a Reset button after dismissing");
    }

    [E2EFact]
    public void Confirm_completes_the_quest_and_its_prerequisite()
    {
        var (questName, prereqName) = E2EQuestData.FindLockedQuestWithActivePrereq();
        var configDir = NewConfigDir();
        using var app = LaunchMaximized(configDir);

        ShowQuestDetail(app, questName, "All");
        var dialog = OpenCascadeDialog(app);
        AssertDialogPreviewsPrereq(dialog, prereqName);

        AppDriver.Invoke(AppDriver.WaitForElementUnder(dialog, "BtnCascadeConfirm"));
        app.WaitForAppWindowClosed(DialogTitle);

        // The quest completed: the progress refresh flips its button row.
        app.WaitForElementVisibility("BtnComplete", visible: false);
        app.WaitForElementVisibility("BtnReset", visible: true);

        // The prerequisite was auto-completed with it (its detail offers Reset only).
        app.ClickTextElementWithScroll(prereqName, "PrerequisitesList", "DetailScrollViewer");
        WaitUntil(() => app.GetElementText("TxtDetailName") == prereqName,
            $"detail panel to show prerequisite '{prereqName}'");
        app.WaitForElementVisibility("BtnComplete", visible: false);
        app.WaitForElementVisibility("BtnReset", visible: true);

        // Both completions actually reached user_data.db. The batch save is
        // fire-and-forget with a swallowing catch, so poll the rows themselves.
        var questId = E2EQuestData.QuestIdByName(questName);
        var prereqId = E2EQuestData.QuestIdByName(prereqName);
        WaitUntil(() => E2EDb.ReadQuestProgress(configDir, questId) == "Done",
            $"'{questName}' Done row to persist");
        WaitUntil(() => E2EDb.ReadQuestProgress(configDir, prereqId) == "Done",
            $"'{prereqName}' Done row to persist");
    }

    [E2EFact]
    public void Completion_with_alternative_previews_and_persists_the_failure()
    {
        var (questName, altName, questId, altId) = E2EQuestData.FindQuestWithSingleAlternative();
        var configDir = NewConfigDir();
        using var app = LaunchMaximized(configDir);

        ShowQuestDetail(app, questName, "All");
        var dialog = OpenCascadeDialog(app);

        // The red failed section previews exactly the one guaranteed alternative.
        WaitUntil(() => AppDriver.HasTextElementUnder(dialog, altName),
            $"cascade dialog to list alternative '{altName}'");
        Assert.Equal(string.Format(Loc.CascadeFailedHeaderFormat, 1),
            AppDriver.WaitForElementUnder(dialog, "TxtCascadeFailedHeader").Current.Name);

        AppDriver.Invoke(AppDriver.WaitForElementUnder(dialog, "BtnCascadeConfirm"));
        app.WaitForAppWindowClosed(DialogTitle);

        // The quest completed, and the previewed failure was applied and persisted.
        app.WaitForElementVisibility("BtnComplete", visible: false);
        app.WaitForElementVisibility("BtnReset", visible: true);
        WaitUntil(() => E2EDb.ReadQuestProgress(configDir, questId) == "Done",
            $"'{questName}' Done row to persist");
        WaitUntil(() => E2EDb.ReadQuestProgress(configDir, altId) == "Failed",
            $"'{altName}' Failed row to persist");
    }

    [E2EFact]
    public void Cascade_free_completion_shows_no_dialog_and_completes_immediately()
    {
        var questName = E2EQuestData.FindStandaloneActiveQuest();
        using var app = LaunchMaximized();

        ShowQuestDetail(app, questName, "All");
        app.InvokeElement("BtnComplete");

        // Race the two possible outcomes: either the dialog opened (a regression:
        // the completion then never applies and the buttons never flip) or the
        // one-click completion flipped the button row. Checking the dialog FIRST
        // keeps the assertion live; waiting for the flip alone would time out on an
        // unrelated line instead of naming the regression.
        WaitUntil(() => app.HasAppWindow(DialogTitle) || !app.IsElementVisible("BtnComplete"),
            "the completion to either open the cascade dialog or apply");
        Assert.False(app.HasAppWindow(DialogTitle),
            "cascade dialog appeared for a cascade-free completion");

        app.WaitForElementVisibility("BtnComplete", visible: false);
        app.WaitForElementVisibility("BtnReset", visible: true);
    }
}
