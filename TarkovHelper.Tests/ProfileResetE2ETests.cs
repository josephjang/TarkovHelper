using System.Runtime.InteropServices;
using System.Windows.Automation;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// End-to-end guard for feature-complete-profile-reset.md: the dialog resets the selected
/// profile completely (quests gone, profile values gone, watermark up), spares every other
/// profile and the edition facts, and declining changes nothing. Driven by AutomationId through
/// <see cref="AppDriver"/>; the dialog replaced the old native MessageBox precisely so this test
/// can exist.
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class ProfileResetE2ETests : E2ETestBase
{
    private const string ActiveProfileSetting = "app.activeGameMode";
    private const string DialogTitle = "Reset Profile";

    /// <summary>Seeds two profiles' worth of data and selects the season profile.</summary>
    private string SeedTwoProfiles()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);

        E2EDb.SeedQuestProgress(configDir, ProfileService.SeasonProfileId,
            "e2e-season-quest", "e2e-season-quest", "Done");
        E2EDb.SeedQuestProgress(configDir, ProfileService.PveProfileId,
            "e2e-pve-quest", "e2e-pve-quest", "Done");
        E2EDb.SeedProfileSetting(configDir, ProfileService.SeasonProfileId, "app.playerLevel", "42");
        E2EDb.SeedProfileSetting(configDir, ProfileService.SeasonProfileId, "app.hasEodEdition", "True");
        E2EDb.SeedProfileSetting(configDir, ProfileService.PveProfileId, "app.playerLevel", "17");

        E2EDb.SeedSetting(configDir, ActiveProfileSetting, "SEASON");
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "False");
        return configDir;
    }

    private const byte VkEscape = 0x1B;
    private const uint KeyUp = 0x0002;

    /// <summary>
    /// The harness keeps keybd_event private to its Ctrl-click helper, which holds a modifier
    /// rather than pressing a key; these tests need the key press itself.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

    /// <summary>
    /// Presses Escape at <paramref name="window"/>, the keyboard dismissal path a borderless
    /// modal has no title-bar X for.
    /// </summary>
    private static void PressEscape(AutomationElement window)
    {
        var hwnd = new IntPtr(window.Current.NativeWindowHandle);
        Assert.NotEqual(IntPtr.Zero, hwnd);
        Win32.SetForegroundWindow(hwnd);
        Thread.Sleep(100); // let the focus change land before the key arrives
        keybd_event(VkEscape, 0, 0, UIntPtr.Zero);
        keybd_event(VkEscape, 0, KeyUp, UIntPtr.Zero);
    }

    [E2EFact]
    public void Resetting_the_selected_profile_clears_it_completely_and_spares_the_rest()
    {
        var configDir = SeedTwoProfiles();
        var loc = TestLocalization.WithLanguage(AppLanguage.EN);

        using (var app = LaunchMaximized(configDir))
        {
            WaitUntil(() => app.GetItemStatus("BtnPvpSeason") == "Selected",
                "PvP Season to be the selected profile");

            app.InvokeElement("BtnSettings");
            app.WaitForElementVisibility("BtnResetProgress", visible: true);
            app.InvokeElement("BtnResetProgress");

            var dialog = app.WaitForAppWindow(DialogTitle);

            // The confirmation names the CAPTURED target with its localized label (PRD R1).
            Assert.True(AppDriver.HasTextElementUnder(dialog,
                    string.Format(loc.ProfileResetTargetFormat, loc.HeaderPvpSeason)),
                "the dialog does not name the target profile");

            // Log monitoring is off in this seed, so no raid state is meaningful and the warning
            // must stay collapsed (the stale-raid half of that rule is unit-tested in
            // ProfileResetOrchestrationTests, which this run cannot reach).
            Assert.False(AppDriver.HasTextElementUnder(dialog, loc.ProfileResetRaidWarning),
                "the raid warning appeared with the raid watcher off");

            AppDriver.Invoke(AppDriver.WaitForElementUnder(dialog, "BtnConfirmReset"));

            // The result state appears with the success text (PRD R5's happy half).
            var result = AppDriver.WaitForElementUnder(dialog, "TxtResetResult");
            WaitUntil(
                () => result.Current.Name ==
                      string.Format(loc.ProfileResetSuccessFormat, loc.HeaderPvpSeason),
                "the success result to appear");

            AppDriver.Invoke(AppDriver.WaitForElementUnder(dialog, "BtnCloseReset"));
            app.WaitForAppWindowClosed(DialogTitle);

            app.CloseAndWaitForExit();
        }

        // The target profile owns nothing any more (PRD R3)...
        Assert.Null(E2EDb.ReadQuestProgress(configDir, ProfileService.SeasonProfileId, "e2e-season-quest"));
        Assert.Null(E2EDb.ReadProfileSetting(configDir, ProfileService.SeasonProfileId, "app.playerLevel"));

        // ...the editions and the fence survive in its partition (PRD R4, R6)...
        Assert.Equal("True",
            E2EDb.ReadProfileSetting(configDir, ProfileService.SeasonProfileId, "app.hasEodEdition"));
        Assert.NotNull(
            E2EDb.ReadProfileSetting(configDir, ProfileService.SeasonProfileId, "app.progressResetAt"));

        // ...and the other profile is untouched (PRD R4).
        Assert.Equal("Done",
            E2EDb.ReadQuestProgress(configDir, ProfileService.PveProfileId, "e2e-pve-quest"));
        Assert.Equal("17",
            E2EDb.ReadProfileSetting(configDir, ProfileService.PveProfileId, "app.playerLevel"));
    }

    [E2EFact]
    public void Declining_the_reset_changes_nothing()
    {
        var configDir = SeedTwoProfiles();

        using (var app = LaunchMaximized(configDir))
        {
            WaitUntil(() => app.GetItemStatus("BtnPvpSeason") == "Selected",
                "PvP Season to be the selected profile");

            app.InvokeElement("BtnSettings");
            app.WaitForElementVisibility("BtnResetProgress", visible: true);
            app.InvokeElement("BtnResetProgress");

            var dialog = app.WaitForAppWindow(DialogTitle);
            AppDriver.Invoke(AppDriver.WaitForElementUnder(dialog, "BtnCancelReset"));
            app.WaitForAppWindowClosed(DialogTitle);

            app.CloseAndWaitForExit();
        }

        // Declining changes nothing (PRD R2): rows intact, no fence went up.
        Assert.Equal("Done",
            E2EDb.ReadQuestProgress(configDir, ProfileService.SeasonProfileId, "e2e-season-quest"));
        Assert.Equal("42",
            E2EDb.ReadProfileSetting(configDir, ProfileService.SeasonProfileId, "app.playerLevel"));
        Assert.Null(
            E2EDb.ReadProfileSetting(configDir, ProfileService.SeasonProfileId, "app.progressResetAt"));
    }

    /// <summary>
    /// The borderless confirmation has no title-bar X, so Escape is its only dismissal that is
    /// not a mouse click on Cancel (the house rule the sibling modals state in
    /// QuestCompleteConfirmDialog.xaml). Declining by keyboard must be as inert as declining by
    /// mouse: Escape reaches Cancel, never Confirm.
    /// </summary>
    [E2EFact]
    public void Escape_dismisses_the_confirmation_and_resets_nothing()
    {
        var configDir = SeedTwoProfiles();

        using (var app = LaunchMaximized(configDir))
        {
            WaitUntil(() => app.GetItemStatus("BtnPvpSeason") == "Selected",
                "PvP Season to be the selected profile");

            app.InvokeElement("BtnSettings");
            app.WaitForElementVisibility("BtnResetProgress", visible: true);
            app.InvokeElement("BtnResetProgress");

            var dialog = app.WaitForAppWindow(DialogTitle);
            PressEscape(dialog);
            app.WaitForAppWindowClosed(DialogTitle);

            app.CloseAndWaitForExit();
        }

        // Escape is a decline, not a confirm: every row survives and no watermark was written.
        Assert.Equal("Done",
            E2EDb.ReadQuestProgress(configDir, ProfileService.SeasonProfileId, "e2e-season-quest"));
        Assert.Equal("42",
            E2EDb.ReadProfileSetting(configDir, ProfileService.SeasonProfileId, "app.playerLevel"));
        Assert.Null(
            E2EDb.ReadProfileSetting(configDir, ProfileService.SeasonProfileId, "app.progressResetAt"));
    }
}
