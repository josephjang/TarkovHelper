using Brush = System.Windows.Media.Brush;
using Colors = System.Windows.Media.Colors;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using TarkovHelper.Models;
using TarkovHelper.Pages;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;
using static TarkovHelper.Tests.LoyaltyFixtures;

namespace TarkovHelper.Tests;

/// <summary>
/// The Collector page's unlock panel (feature-kappa-collector-1-1.md, R1 and R2): the badge the
/// quest list shows for Collector, one line per unlock condition coloured met or unmet, and the
/// Kappa count, composed by <see cref="CollectorUnlockViewModel.BuildFor"/> from one pass.
/// <para>
/// Every case drives the status engine for real and hands the panel the status and gate it
/// answered, the way the page does. The claim under test is COMPOSITION: that the panel shows
/// the formatter's own badge, the line builder's own lines and the shared count format, and
/// that the badge and the first unmet line name the same condition because both come from the
/// gate. The badge rule itself is <c>QuestRequirementBadgeTests</c>; the lines are
/// <c>QuestRequirementLinesTests</c>.
/// </para>
/// </summary>
public sealed class CollectorUnlockViewModelTests
{
    // Compared by reference only, so a case says "met" or "unmet" without the theme's colours.
    // The page's own palette is RequirementLineBrushes.FromResources; this stands in for it.
    private static readonly Brush Met = new SolidColorBrush(Colors.White);
    private static readonly Brush Unmet = new SolidColorBrush(Colors.Red);
    private static readonly RequirementLineBrushes Palette = new(Met, Unmet);

    private static readonly LocalizationService English =
        TestLocalization.WithLanguage(AppLanguage.EN);

    private const int KappaTotal = 13;

    /// <summary>The English nickname, which is what the display resolver answers in EN.</summary>
    private static string EnglishName(QuestTraderRequirement requirement) => requirement.TraderName;

    /// <summary>
    /// The seven traders Collector's published rows name, in the game's display order, which is
    /// the order the loader leaves the rows in and the panel lists them in.
    /// </summary>
    private static readonly (string Id, string Name)[] SevenTraders =
    {
        (Prapor, "Prapor"), (Therapist, "Therapist"), (Skier, "Skier"),
        (Peacekeeper, "Peacekeeper"), (Mechanic, "Mechanic"), (Ragman, "Ragman"),
        (Jaeger, "Jaeger"),
    };

    private sealed record Shape(TarkovTask Collector, TarkovTask[] Prerequisites);

    /// <summary>
    /// Collector shaped like the published row: given by Fence, player level 42, Scav karma 3,
    /// seven non-giver loyalty rows at 4, and twelve Complete-type prerequisites that are
    /// themselves flagged. The rows are handed to the fixture in REVERSE display order, so the
    /// order the panel lists them in is the loader's doing and not the test's.
    /// </summary>
    private static Shape PublishedShape()
    {
        var prerequisites = Enumerable.Range(1, 12)
            .Select(i =>
            {
                var quest = TestTasks.Quest($"kappa-{i}", $"kappa-{i}");
                quest.ReqKappa = true;
                return quest;
            })
            .ToArray();

        var collector = Quest("collector", "Fence",
            SevenTraders.Reverse().Select(t => (t.Id, t.Name, 4)).ToArray());
        collector.Name = "Collector";
        collector.ReqKappa = true;
        collector.RequiredLevel = 42;
        collector.RequiredScavKarma = 3.0;
        collector.TaskRequirements = prerequisites
            .Select(p => new TaskRequirement
            {
                TaskId = p.Ids![0],
                TaskNormalizedName = p.NormalizedName!,
                Status = new List<string> { "complete" },
                GroupId = 0,
            })
            .ToList();

        return new Shape(collector, prerequisites);
    }

    /// <summary>
    /// The status and gate the engine answers for Collector, exactly as a render pass gets them:
    /// one call against one progress snapshot and one settings snapshot.
    /// </summary>
    private static (QuestStatus Status, QuestGate Gate) Walk(
        Shape shape, ProfileSettingsSnapshot settings, bool prerequisitesDone, bool collectorDone)
    {
        var rows = new Dictionary<string, QuestStatus>();
        if (prerequisitesDone)
        {
            foreach (var prerequisite in shape.Prerequisites) rows[prerequisite.Ids![0]] = QuestStatus.Done;
        }
        if (collectorDone) rows[shape.Collector.Ids![0]] = QuestStatus.Done;

        var progress = ProgressSnapshot.From("profile", 0, rows, new Dictionary<string, bool>());
        var service = ProgressServiceHarness.Create(
            new ProgressStoreFake(), progress, shape.Prerequisites.Append(shape.Collector).ToArray());

        var status = service.GetStatus(shape.Collector, service.Snapshot, settings, out var gate);
        return (status, gate);
    }

    /// <summary>
    /// The panel for one state, with the composition claim asserted on the way out: the badge is
    /// the formatter's own answer for the same status, gate, task and settings, and the status
    /// is carried unchanged for the badge's fill.
    /// </summary>
    private static CollectorUnlockViewModel PanelFor(
        Shape shape, ProfileSettingsSnapshot settings,
        bool prerequisitesDone, bool collectorDone = false, int kappaDone = 0)
    {
        var (status, gate) = Walk(shape, settings, prerequisitesDone, collectorDone);

        var panel = CollectorUnlockViewModel.BuildFor(
            shape.Collector, status, gate, settings, English, EnglishName,
            kappa: (kappaDone, KappaTotal), Palette);

        Assert.Equal(
            QuestRequirementBadge.StatusText(status, gate, shape.Collector, settings, EnglishName),
            panel.StatusText);
        Assert.Equal(status, panel.Status);
        return panel;
    }

    private static ProfileSettingsSnapshot AllSevenAtFour(int playerLevel = 42, double scavRep = 3.0)
        => Settings(playerLevel: playerLevel, scavRep: scavRep,
            loyalty: SevenTraders.Select(t => (t.Id, 4)).ToArray());

    private static string[] Texts(CollectorUnlockViewModel panel)
        => panel.Lines.Select(line => line.DisplayText).ToArray();

    private static RequirementLineViewModel FirstUnmet(CollectorUnlockViewModel panel)
        => panel.Lines.First(line => line.Foreground == Unmet);

    [Fact]
    public void Before_the_twelve_are_done_Collector_reads_Locked_and_every_condition_is_unmet()
    {
        // A fresh profile: the prerequisite gate comes before the value gates, so the badge says
        // Locked, and the nine lines are all present and all unmet at the defaults (level 15,
        // Scav Rep 1, every trader at 1). Nothing is hidden behind the badge.
        var panel = PanelFor(PublishedShape(), Settings(), prerequisitesDone: false);

        Assert.Equal("Locked", panel.StatusText);
        Assert.Equal(9, panel.Lines.Count);
        Assert.All(panel.Lines, line => Assert.Same(Unmet, line.Foreground));
        Assert.Equal("0/13 Kappa quests completed", panel.CountText);
    }

    [Fact]
    public void With_the_twelve_done_the_level_holds_it_and_its_line_is_the_first_unmet()
    {
        var panel = PanelFor(PublishedShape(), Settings(), prerequisitesDone: true);

        Assert.Equal("Lv.42", panel.StatusText);
        Assert.Equal("Level 42 (Current: 15)", FirstUnmet(panel).DisplayText);
    }

    [Fact]
    public void At_level_42_the_karma_holds_it()
    {
        var panel = PanelFor(PublishedShape(), Settings(playerLevel: 42), prerequisitesDone: true);

        Assert.Equal("Rep 3", panel.StatusText);
        Assert.Same(Met, panel.Lines[0].Foreground);
        Assert.Equal("Scav Karma ≥ 3 (Current: 1)", FirstUnmet(panel).DisplayText);
    }

    [Fact]
    public void At_karma_3_the_first_trader_holds_it_and_the_loyalty_lines_keep_the_loaded_order()
    {
        var panel = PanelFor(
            PublishedShape(), Settings(playerLevel: 42, scavRep: 3.0), prerequisitesDone: true);

        // The first trader in the game's order is the one the badge names...
        Assert.Equal("Prapor LL4", panel.StatusText);
        Assert.Equal("Prapor LL4 (Current: 1)", FirstUnmet(panel).DisplayText);

        // ...and the seven lines are in that order, although the fixture received them
        // reversed: the order is the loader's (QuestDbService.SortIntoBadgeOrder), not the row
        // order and not the entered levels.
        Assert.Equal(
            SevenTraders.Select(t => $"{t.Name} LL4 (Current: 1)").ToArray(),
            Texts(panel).Skip(2).ToArray());
    }

    [Fact]
    public void With_all_seven_at_4_it_reads_Active_with_every_line_met()
    {
        var panel = PanelFor(PublishedShape(), AllSevenAtFour(), prerequisitesDone: true);

        Assert.Equal("Active", panel.StatusText);
        Assert.Equal(9, panel.Lines.Count);
        Assert.All(panel.Lines, line => Assert.Same(Met, line.Foreground));
    }

    [Fact]
    public void A_done_Collector_reads_Done_with_its_lines_met()
    {
        var panel = PanelFor(
            PublishedShape(), AllSevenAtFour(), prerequisitesDone: true, collectorDone: true,
            kappaDone: 13);

        Assert.Equal("Done", panel.StatusText);
        Assert.All(panel.Lines, line => Assert.Same(Met, line.Foreground));
        Assert.Equal("13/13 Kappa quests completed", panel.CountText);
    }

    [Fact]
    public void The_conditions_are_shown_and_never_counted_into_the_Kappa_number()
    {
        // PD1: every condition met and no quest done reads 0 of 13, not 9 of 22. The lines are
        // values the player typed; the count is quests.
        var panel = PanelFor(PublishedShape(), AllSevenAtFour(), prerequisitesDone: true);

        Assert.All(panel.Lines, line => Assert.Same(Met, line.Foreground));
        Assert.Equal("0/13 Kappa quests completed", panel.CountText);
    }

    [Fact]
    public void The_count_uses_the_shared_format_in_the_apps_language()
    {
        // The same KappaCountFormat the quest tab's detail pane shows, so the two pages cannot
        // differ in wording either (R3, R4).
        var shape = PublishedShape();
        var settings = Settings();
        var (status, gate) = Walk(shape, settings, prerequisitesDone: true, collectorDone: false);

        var korean = CollectorUnlockViewModel.BuildFor(
            shape.Collector, status, gate, settings, TestLocalization.WithLanguage(AppLanguage.KO),
            EnglishName, kappa: (12, KappaTotal), Palette);

        Assert.Equal("카파 퀘스트 12/13 완료", korean.CountText);
        Assert.Equal("레벨 42 (현재: 15)", korean.Lines[0].DisplayText);
    }

    [Fact]
    public void With_no_count_to_show_the_panel_says_nothing_rather_than_zero_of_zero()
    {
        // The graph is not built yet, so there is no count. The panel has to say nothing: "0/13"
        // would be a reading, and "0/0 Kappa quests completed" reads as "nothing is flagged" -
        // the case QuestGraphService.IsInitialized exists to keep apart, and the string the quest
        // tab's gauge is forbidden to paint (KappaGaugeSourceTests). Before this, "no count" had
        // no spelling at all: the page substituted (0, 0, 0) and printed it.
        var shape = PublishedShape();
        var settings = Settings();
        var (status, gate) = Walk(shape, settings, prerequisitesDone: false, collectorDone: false);

        var panel = CollectorUnlockViewModel.BuildFor(
            shape.Collector, status, gate, settings, English, EnglishName,
            kappa: null, Palette);

        Assert.Equal(string.Empty, panel.CountText);
        // A missing count hides no condition: the badge and the nine lines are unaffected.
        Assert.Equal("Locked", panel.StatusText);
        Assert.Equal(9, panel.Lines.Count);
    }

    /// <summary>
    /// The panel builder takes a non-null quest, so "the loaded data has no Collector quest" is
    /// the page's case to answer, and the only answer that makes sense is to collapse the panel:
    /// there is no badge to show for a quest that is not there. The builder used to accept null
    /// and answer null, which read as a second guard but was unreachable behind this one, and the
    /// page discarded the nullable result with <c>!</c> anyway.
    /// <para>
    /// Pinned at the source because the page cannot be constructed in this suite (see
    /// <see cref="SourceGuards"/>), and the defect would be the guard quietly going missing,
    /// which nothing else here would notice.
    /// </para>
    /// </summary>
    [Fact]
    public void The_page_collapses_the_panel_when_the_data_has_no_Collector_quest()
    {
        var rebuild = SourceGuards.MemberBody(
            SourceGuards.Read("TarkovHelper", "Pages", "CollectorPage.xaml.cs"),
            "private void RebuildUnlockPanel(RenderPass pass)");

        // The guard, and it comes before the build: the collapse returns.
        var guard = rebuild.IndexOf("if (collector == null)", StringComparison.Ordinal);
        var collapse = rebuild.IndexOf(
            "UnlockPanel.Visibility = Visibility.Collapsed;", StringComparison.Ordinal);
        var build = rebuild.IndexOf("CollectorUnlockViewModel.BuildFor(", StringComparison.Ordinal);

        Assert.True(guard >= 0, "The page no longer guards against a missing Collector quest.");
        Assert.InRange(collapse, guard, build);
        Assert.Contains("return;", rebuild[collapse..build], StringComparison.Ordinal);

        // And it does not paper over a nullable result any more: BuildFor answers a panel.
        Assert.DoesNotContain(
            "RequirementLineBrushes.FromResources(this))!", rebuild, StringComparison.Ordinal);
    }
}
