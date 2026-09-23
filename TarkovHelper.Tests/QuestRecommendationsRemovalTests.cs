using System.IO;
using System.Reflection;
using System.Xml.Linq;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// Pins the removal of the Quests tab's recommendations panel
/// (2026-09-22-remove-quest-recommendations.md): the code that made it is gone, the layout row
/// it sat in is gone with it, the refresh paths that fed it still do everything else, and
/// nothing in the solution still talks about it.
/// <para>
/// Source and reflection guards rather than behavioural ones, for the reason
/// <see cref="SourceGuards"/> gives: the page cannot be constructed in this suite. The runtime
/// half (the tab loads without the expander, and a user database that still holds the old
/// expander row behaves as one without it) is
/// <c>QuestOverviewFiltersE2ETests.A_stored_recommendations_expander_row_is_inert</c>.
/// </para>
/// </summary>
public sealed class QuestRecommendationsRemovalTests
{
    /// <summary>R2: the types behind the panel, by name, since they cannot be named in code.</summary>
    [Theory]
    [InlineData("QuestRecommendationService")]
    [InlineData("QuestRecommendation")]
    [InlineData("RecommendationType")]
    [InlineData("QuestRecommendationsPanel")]
    [InlineData("RecommendationViewModel")]
    public void The_recommendation_types_are_gone(string typeName)
    {
        var matches = typeof(LocalizationService).Assembly.GetTypes()
            .Where(type => type.Name == typeName)
            .Select(type => type.FullName);

        Assert.Empty(matches);
    }

    /// <summary>
    /// R2: the seven strings the proposal names, plus the three the same 2025-12 commit added
    /// for the panel and nothing else ever read.
    /// </summary>
    [Theory]
    [InlineData("RecommendedQuests")]
    [InlineData("ReadyToComplete")]
    [InlineData("ItemHandInOnly")]
    [InlineData("KappaPriority")]
    [InlineData("UnlocksMany")]
    [InlineData("EasyQuest")]
    [InlineData("NoRecommendations")]
    [InlineData("ItemsOwned")]
    [InlineData("ItemsNeeded")]
    [InlineData("UnlocksQuests")]
    public void The_recommendation_strings_are_gone(string key)
    {
        var property = typeof(LocalizationService).GetProperty(key, BindingFlags.Public | BindingFlags.Instance);

        Assert.Null(property);
    }

    /// <summary>
    /// R2: the setting's property is gone. Its key string is left to
    /// <see cref="No_source_file_still_refers_to_the_panel"/>, which scans QuestListSettings.cs
    /// with the rest of the source.
    /// </summary>
    [Fact]
    public void The_expander_setting_is_gone()
        => Assert.Null(typeof(QuestListSettings).GetProperty("RecommendationsExpanded"));

    /// <summary>
    /// R1: the page's outer grid has one row per child and no more, so removing the panel's
    /// element without its row definition (an empty Auto row, which renders as nothing today
    /// but is a slot the next edit would fill by accident) or leaving a child pointing past
    /// the last row fails here. The list and detail pane stay in the one star row.
    /// </summary>
    [Fact]
    public void The_quest_page_grid_has_no_row_left_for_the_panel()
    {
        var page = XDocument.Parse(SourceGuards.Read("TarkovHelper", "Pages", "QuestListPage.xaml"));
        var outer = page.Root!.Elements().Single(element => element.Name.LocalName == "Grid");

        var rowDefinitions = outer.Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .ToList();
        var children = outer.Elements()
            .Where(element => !element.Name.LocalName.StartsWith("Grid.", StringComparison.Ordinal))
            .ToList();
        var rows = children
            .Select(child => int.Parse((string?)child.Attribute("Grid.Row") ?? "0"))
            .OrderBy(row => row)
            .ToList();

        Assert.Equal(Enumerable.Range(0, rowDefinitions.Count), rows);
        Assert.Equal("*", (string?)rowDefinitions[^1].Attribute("Height"));
        Assert.DoesNotContain(children.Select(child => child.Name.LocalName),
            name => name.Contains("Recommendation", StringComparison.Ordinal));
    }

    /// <summary>
    /// R3: every refresh path that used to end in the panel's update still runs the rest of its
    /// sequence, in order, and RefreshDisplay still delegates to the shared sequence rather
    /// than growing a shorter copy of it (the shortcut feature-quest-chip-only-status-filter
    /// closed).
    /// </summary>
    [Theory]
    [InlineData("private void RefreshAllForStateChange()",
        new[] { "RefreshQuestStatuses();", "ApplyFilters();", "UpdateDetailPanel();" })]
    [InlineData("private async void OnDatabaseRefreshed(object? sender, EventArgs e)",
        new[] { "LoadQuests();", "PopulateTraderFilter();", "PopulateMapFilter();", "ApplyFilters();", "UpdateDetailPanel();" })]
    [InlineData("private async void QuestListPage_Loaded(object sender, RoutedEventArgs e)",
        new[] { "RefreshAllForStateChange();", "LoadQuests();", "RestoreFilterSettings();", "ApplyFilters();", "SelectQuestInternal(pendingName);" })]
    public void Each_refresh_path_keeps_its_remaining_steps_in_order(string signature, string[] steps)
    {
        var body = SourceGuards.MemberBody(QuestListPageSource, signature);

        var previous = -1;
        foreach (var step in steps)
        {
            var at = body.IndexOf(step, previous + 1, StringComparison.Ordinal);
            Assert.True(at > previous, $"'{signature}' no longer runs '{step}' after the step before it.");
            previous = at;
        }
    }

    [Fact]
    public void RefreshDisplay_still_delegates_to_the_shared_sequence()
        => Assert.Contains("public void RefreshDisplay() => RefreshAllForStateChange();",
            QuestListPageSource, StringComparison.Ordinal);

    /// <summary>
    /// R6: no source file names the panel, its service, view model or expander, or describes
    /// them in prose, including the comments that cited the panel as a precedent. Markup is
    /// scanned too, since the panel was declared there. Build output is skipped: a stale
    /// generated file from an earlier build is not source.
    /// <para>
    /// Two test files are exempt because pinning an absence means naming the absent thing:
    /// this one, and the e2e suite whose case checks the expander is not on screen.
    /// </para>
    /// </summary>
    [Fact]
    public void No_source_file_still_refers_to_the_panel()
    {
        string[] identifiers =
        {
            "QuestRecommendation", "RecommendationsPanel", "RecommendationViewModel",
            "RecommendationsExpand", "recommendationsExpanded", "UpdateRecommendations", "RecommendationsList",
        };
        string[] phrases =
        {
            "recommendations panel", "recommendation panel", "recommendation service",
            "recommendations area", "recommendations expander", "recommendation reason",
            "recommendation priority", "recommendation badge", "recommendation row",
        };
        string[] exempt =
        {
            Path.Combine("TarkovHelper.Tests", nameof(QuestRecommendationsRemovalTests) + ".cs"),
            Path.Combine("TarkovHelper.Tests", "QuestOverviewFiltersE2ETests.cs"),
        };

        var root = TestRepo.Root();
        var scanned = 0;
        var offenders = new List<string>();

        foreach (var project in new[] { "TarkovHelper", "TarkovHelper.Tests" })
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(root, project), "*.*", SearchOption.AllDirectories))
            {
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)) continue;

                var relative = Path.GetRelativePath(root, path);
                var segments = relative.Split(Path.DirectorySeparatorChar);
                if (segments.Contains("bin") || segments.Contains("obj") || exempt.Contains(relative)) continue;

                var text = File.ReadAllText(path);
                scanned++;

                offenders.AddRange(identifiers
                    .Where(token => text.Contains(token, StringComparison.Ordinal))
                    .Concat(phrases.Where(token => text.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    .Select(token => $"{relative}: '{token}'"));
            }
        }

        Assert.Empty(offenders);
        // The walk found real source, so a green run is not an empty directory passing.
        Assert.InRange(scanned, 100, int.MaxValue);
    }

    private static string QuestListPageSource =>
        SourceGuards.Read("TarkovHelper", "Pages", "QuestListPage.xaml.cs");
}
