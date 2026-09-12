using System.Globalization;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// End-to-end coverage for feature-kappa-collector-1-1.md: the Collector page says whether
/// Collector is available and what still holds it, the drawer edits reach it without leaving
/// the tab, and every Kappa count in the app is one number with one label.
/// <para>
/// Only the running app can say these add up: the gate is in the status engine, the panel is
/// composed on the page from a captured pass, the count is drawn on two tabs and a popup, and
/// the drawer's events reach the page through a coalescer. The unit suites pin each of those;
/// this is the one thing that says a player sees them agree.
/// </para>
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class KappaCollectorE2ETests : E2ETestBase
{
    /// <summary>
    /// A config dir with log monitoring off, so the machine's own EFT logs cannot switch the
    /// profile out from under a test that seeded rows into one profile.
    /// </summary>
    private string NewQuietConfigDir()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "False");
        E2EDb.SeedSetting(configDir, "app.language", nameof(AppLanguage.EN));
        return configDir;
    }

    private static void OpenCollectorPage(AppDriver app)
        => app.SelectTab("TabCollector", "LstItems", bounceTabAutomationId: "TabQuests");

    /// <summary>The unlock panel's condition lines, as rendered, in order.</summary>
    private static List<string> ConditionLines(AppDriver app)
        => app.TryGetTextElements("CollectorRequirementsList")
              .Select(line => line.Current.Name)
              .ToList();

    /// <summary>
    /// R1, R2, R5 and R6: with the twelve done and the level and karma at their thresholds, the
    /// page names the first trader still holding Collector; seven drawer clicks, without leaving
    /// the tab, make it Active; the item list's scope reads "Collector only"; and a switch to
    /// Korean renames the trader on the condition line.
    /// </summary>
    [E2EFact]
    public void The_Collector_page_names_what_holds_Collector()
    {
        var collector = E2EQuestData.Collector();
        var kappaQuests = E2EQuestData.KappaQuests();
        var total = E2EQuestData.KappaFlagCount();
        var first = collector.Traders[0];

        var configDir = NewQuietConfigDir();
        foreach (var quest in kappaQuests)
        {
            E2EDb.SeedQuestProgress(
                configDir, ProfileService.PvpProfileId, quest.Id, quest.NormalizedName, "Done");
        }
        E2EDb.SeedProfileSetting(
            configDir, ProfileService.PvpProfileId, "app.playerLevel",
            collector.MinLevel!.Value.ToString(CultureInfo.InvariantCulture));
        E2EDb.SeedProfileSetting(
            configDir, ProfileService.PvpProfileId, "app.scavRep",
            collector.MinScavKarma!.Value.ToString(CultureInfo.InvariantCulture));
        using var app = LaunchMaximized(configDir);

        // The badge names the first trader, the same badge the quest list shows for Collector...
        OpenCollectorPage(app);
        var badge = $"{first.Name} LL{first.Level}";
        WaitUntil(() => app.GetElementText("TxtCollectorStatus") == badge,
            $"the Collector page to show '{badge}' with the level and karma met");

        // ...the count is the flagged set with Collector still to do, and the scope is honest.
        Assert.StartsWith($"{kappaQuests.Count}/{total}", app.GetElementText("TxtKappaCount"),
            StringComparison.Ordinal);
        Assert.EndsWith("Collector only", app.GetElementText("TxtStats"), StringComparison.Ordinal);

        // Every unlock condition is on the page: the level, the karma, one line per trader.
        Assert.Equal(2 + collector.Traders.Count, ConditionLines(app).Count);

        // Seven clicks in the drawer, and the page follows without being left or reopened: the
        // drawer's events reach it through its own subscriptions.
        foreach (var trader in collector.Traders)
        {
            ProfileDrawerDriver.EnterLoyalty(app, trader.NormalizedName, trader.Level);
        }
        WaitUntil(() => app.GetElementText("TxtCollectorStatus") == "Active",
            "the Collector page to read Active once every trader's level is entered");
        Assert.True(app.IsElementVisible("LstItems"), "the item list should still be on screen");

        // The first loyalty line follows the language, the lines being level, karma, then the
        // traders in badge order.
        app.SelectLanguage("한국어");
        WaitUntil(
            () => ConditionLines(app) is { Count: >= 3 } lines
                  && lines[2].StartsWith($"{first.NameKo} LL{first.Level}", StringComparison.Ordinal),
            $"the first loyalty line to name '{first.NameKo}' after the switch to Korean");
    }

    /// <summary>
    /// R3 and R4: the quest tab's gauge, Collector's detail pane and the Collector page all show
    /// the same number over the same total, and the pane no longer calls the thirteen
    /// "Prerequisites".
    /// </summary>
    [E2EFact]
    public void Every_Kappa_count_is_the_same_number_with_the_same_label()
    {
        var total = E2EQuestData.KappaFlagCount();
        var one = E2EQuestData.KappaQuests()[0];
        var configDir = NewQuietConfigDir();

        using (var app = LaunchMaximized(configDir))
        {
            app.SelectTab("TabQuests", "LstQuests");
            WaitUntil(() => app.GetElementText("TxtKappaGauge") == $"0/{total}",
                "the gauge to read 0 of the seed's flag count on a fresh profile");
            app.CloseAndWaitForExit();
        }

        E2EDb.SeedQuestProgress(configDir, ProfileService.PvpProfileId, one.Id, one.NormalizedName, "Done");

        using (var app = LaunchMaximized(configDir))
        {
            app.SelectTab("TabQuests", "LstQuests");
            WaitUntil(() => app.GetElementText("TxtKappaGauge") == $"1/{total}",
                $"the gauge to read 1/{total} after '{one.Name}' was recorded Done");

            ShowCollectorDetail(app);
            WaitUntil(
                () => app.GetElementText("TxtKappaProgress").StartsWith($"1/{total}", StringComparison.Ordinal),
                $"Collector's detail pane to count 1/{total}");
            Assert.DoesNotContain("Prerequisites", app.GetElementText("TxtKappaProgress"),
                StringComparison.OrdinalIgnoreCase);

            OpenCollectorPage(app);
            WaitUntil(
                () => app.GetElementText("TxtKappaCount").StartsWith($"1/{total}", StringComparison.Ordinal),
                $"the Collector page to count 1/{total}");
        }
    }

    /// <summary>
    /// Shows Collector's detail pane. Reached through the Kappa filter rather than by search
    /// alone: "Collector" is not guaranteed to be a unique search substring across every
    /// quest name, but among the flagged quests it is.
    /// </summary>
    private static void ShowCollectorDetail(AppDriver app)
    {
        app.SelectTab("TabQuests", "LstQuests");
        QuestTabDriver.SelectStatusChip(app, "All");
        if (!app.GetToggleState("ChkKappaOnly")) app.ToggleElement("ChkKappaOnly");
        app.SetTextBoxValue("TxtSearch", "Collector");
        AppDriver.PollUntil(() => app.GetListItemCount("LstQuests") == 1,
            "the quest list to filter down to Collector");
        app.SelectListItemAt("LstQuests", 0);
        AppDriver.PollUntil(() => app.GetElementText("TxtDetailName") == "Collector",
            "the detail panel to show Collector");
    }
}
