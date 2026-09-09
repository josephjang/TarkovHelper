using TarkovHelper.Pages;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// End-to-end coverage for the top-bar redesign: the redesigned title bar shows the
/// version chip / sync-status chip / profile chip / settings gear, the demoted
/// controls (Reset, Support, language) live inside the Settings overlay instead of
/// the bar, and the level stepper moved into the profile drawer. Uses the shared
/// <see cref="AppDriver"/> harness (x:Name surfaces as the UIA AutomationId).
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class HeaderE2ETests : E2ETestBase
{
    [E2EFact]
    public void Active_profile_selector_uses_fixed_width_menu_below_full_width()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "False");
        using var app = AppDriver.Launch(configDir);

        app.WaitForElementVisibility("BtnPvpZone", visible: true);
        Assert.False(app.IsElementVisible("BtnActiveProfileMenu"));

        app.ResizeWindow(900, 700);
        app.WaitForElementVisibility("BtnActiveProfileMenu", visible: true);
        app.WaitForElementVisibility("BtnPvpZone", visible: false);
        Assert.Equal("Active profile: PvP Zone", app.GetElementText("BtnActiveProfileMenu"));

        // PRD R6: the selector does not persist Manual/Auto/Pinned source state. Nothing was
        // auto-detected here (log monitoring is off), and even if it had been, the source cue is
        // transient -- so there must be no lasting source label to read.
        Assert.Equal(string.Empty, app.GetItemStatus("BtnActiveProfileMenu"));

        app.ResizeWindow(700, 700);
        app.WaitForElementVisibility("BtnActiveProfileMenu", visible: true);
        app.WaitForElementVisibility("BtnPvpZone", visible: false);

        app.ResizeWindow(1100, 700);
        app.WaitForElementVisibility("BtnPvpZone", visible: true);
        app.WaitForElementVisibility("BtnActiveProfileMenu", visible: false);
    }

    [E2EFact]
    public void Title_bar_shows_chips_and_demoted_controls_live_in_settings()
    {
        using var app = AppDriver.Launch(NewConfigDir());

        // Redesigned bar: version chip, sync status, profile chip, settings gear, tabs
        app.WaitForElement("TxtVersionChip");
        app.WaitForElement("TxtSyncStatus");
        app.WaitForElement("BtnProfile");
        app.WaitForElement("BtnSettings");
        app.WaitForElement("TabQuests");

        // The green install pill only exists while an update is available
        Assert.False(app.IsElementVisible("BtnVersionChip"), "update pill visible without an update");

        // Demoted controls must not be visible while the Settings overlay is closed
        Assert.False(app.IsElementVisible("BtnResetProgress"), "Reset button leaked into the bar");
        Assert.False(app.IsElementVisible("BtnCoffee"), "Support button leaked into the bar");
        Assert.False(app.IsElementVisible("CmbLanguage"), "Language combo leaked into the bar");

        // ...and appear once the Settings overlay opens
        app.InvokeElement("BtnSettings");
        app.WaitForElementVisibility("BtnResetProgress", visible: true);
        app.WaitForElementVisibility("BtnCoffee", visible: true);
        app.WaitForElementVisibility("CmbLanguage", visible: true);
        app.WaitForElementVisibility("BtnCheckUpdateSettings", visible: true);
    }

    [E2EFact]
    public void Profile_drawer_holds_the_level_stepper_and_the_loyalty_inputs()
    {
        using var app = AppDriver.Launch(NewConfigDir());

        app.WaitForElement("BtnProfile");
        Assert.False(app.IsElementVisible("TxtPlayerLevel"), "level stepper visible before opening the drawer");

        app.InvokeElement("BtnProfile");
        app.WaitForElementVisibility("TxtPlayerLevel", visible: true);

        // The loyalty groups are built from the loaded quest data rather than declared in XAML,
        // so unlike the stepper they only exist once the roster is known. Prapor is in it for
        // every published database this build reads (fourteen quests gate on Prapor's loyalty),
        // and the group toggles with the drawer like everything else in it.
        app.WaitForElementVisibility("Loyalty_prapor_1", visible: true);
        app.WaitForElementVisibility("Loyalty_prapor_4", visible: true);
        // Nothing entered yet, so level 1 is the selection every trader reads at.
        Assert.Equal(QuestStatusTags.ChipSelected, app.GetItemStatus("Loyalty_prapor_1"));
        Assert.Equal(QuestStatusTags.ChipUnselected, app.GetItemStatus("Loyalty_prapor_4"));

        // The row carries a heading, like the five labelled groups above it. Without it the
        // player reads seven bordered "Prapor 1 2 3 4" pills with nothing saying what the
        // numbers are.
        app.WaitForElementVisibility("TxtLoyaltyLabel", visible: true);
        Assert.Equal("Loyalty", app.GetElementText("TxtLoyaltyLabel"));

        app.InvokeElement("BtnProfile");
        app.WaitForElementVisibility("TxtPlayerLevel", visible: false);
        app.WaitForElementVisibility("Loyalty_prapor_1", visible: false);
        // The heading hides with the groups it names, never on its own.
        app.WaitForElementVisibility("TxtLoyaltyLabel", visible: false);

        // Opening Settings force-closes an open drawer (it would otherwise keep
        // floating beneath the overlay scrim with a stale up-chevron). The sync
        // status chip is a real button whose invoke opens Settings.
        app.InvokeElement("BtnProfile");
        app.WaitForElementVisibility("TxtPlayerLevel", visible: true);
        app.InvokeElement("ChipSyncStatus");
        app.WaitForElementVisibility("BtnResetProgress", visible: true);
        app.WaitForElementVisibility("TxtPlayerLevel", visible: false);
    }

    /// <summary>
    /// The loyalty heading is a localized string, not the English literal the XAML carries as a
    /// design-time placeholder: a KO launch must read it from ProfileLoyaltyLabel like the five
    /// group labels beside it.
    /// </summary>
    [E2EFact]
    public void Profile_drawer_loyalty_heading_reads_the_apps_language()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, "app.language", nameof(AppLanguage.KO));
        using var app = AppDriver.Launch(configDir);

        app.WaitForElement("BtnProfile");
        app.InvokeElement("BtnProfile");
        app.WaitForElementVisibility("TxtLoyaltyLabel", visible: true);

        var heading = app.GetElementText("TxtLoyaltyLabel");
        Assert.Equal(TestLocalization.WithLanguage(AppLanguage.KO).ProfileLoyaltyLabel, heading);
        Assert.NotEqual("Loyalty", heading);
    }

    [E2EFact]
    public void Switching_tabs_dismisses_the_profile_drawer()
    {
        using var app = AppDriver.Launch(NewConfigDir());

        app.WaitForElement("BtnProfile");
        app.InvokeElement("BtnProfile");
        app.WaitForElementVisibility("TxtPlayerLevel", visible: true);

        // Navigating to another tab must close the drawer, otherwise the centered
        // popover keeps floating over the newly selected tab's content.
        app.SelectTab("TabMap", "CmbMapSelect");
        app.WaitForElementVisibility("TxtPlayerLevel", visible: false);
    }
}
