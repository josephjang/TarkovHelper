using TarkovHelper.Pages;

namespace TarkovHelper.Tests;

/// <summary>
/// Page object for the profile drawer's trader loyalty inputs: the button naming convention
/// <c>TraderLoyaltyPanel</c> stamps on them and the open/click/wait choreography the quest
/// loyalty and Kappa/Collector e2e suites share. Lives beside <see cref="QuestTabDriver"/> for
/// the reason that one does: <see cref="E2ETestBase"/> stays page-agnostic.
/// </summary>
internal static class ProfileDrawerDriver
{
    /// <summary>The AutomationId of one trader's level button (TraderLoyaltyPanel builds them so).</summary>
    internal static string LoyaltyButtonId(string traderNormalizedName, int level)
        => $"Loyalty_{traderNormalizedName}_{level}";

    /// <summary>Opens the drawer if it is closed, and waits for the named loyalty button to be there.</summary>
    internal static void Open(AppDriver app, string traderNormalizedName, int level)
    {
        app.WaitForElement("BtnProfile");
        if (!app.IsElementVisible("TxtPlayerLevel")) app.InvokeElement("BtnProfile");
        app.WaitForElementVisibility(LoyaltyButtonId(traderNormalizedName, level), visible: true);
    }

    /// <summary>
    /// Enters one trader's level: opens the drawer, clicks the level's button and waits for it to
    /// report selected, which is when the edit has been published.
    /// </summary>
    internal static void EnterLoyalty(AppDriver app, string traderNormalizedName, int level)
    {
        Open(app, traderNormalizedName, level);
        app.InvokeElement(LoyaltyButtonId(traderNormalizedName, level));
        AppDriver.PollUntil(
            () => app.TryGetItemStatus(LoyaltyButtonId(traderNormalizedName, level)) == QuestStatusTags.ChipSelected,
            $"the level {level} button for '{traderNormalizedName}' to report selected");
    }
}
