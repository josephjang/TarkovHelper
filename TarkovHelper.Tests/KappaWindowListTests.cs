using System.Text.RegularExpressions;
using TarkovHelper.Models;
using TarkovHelper.Pages.Components;
using TarkovHelper.Services;
using Window = System.Windows.Window;
using WindowStartupLocation = System.Windows.WindowStartupLocation;

namespace TarkovHelper.Tests;

/// <summary>
/// The Kappa quest list popup (<see cref="KappaQuestListWindow"/>): where it opens, what its
/// header says, and what it calls each quest.
/// <para>
/// The window itself cannot be constructed here - its constructor resolves App.xaml's theme
/// brushes through FindResource, and WPF allows one Application per AppDomain (see
/// <c>ProfileDrawerFitTests</c>). So the rules it is built from are asserted directly, and the
/// two things only the constructor and its callers can get wrong - using those rules, and not
/// computing the count a second time on the way in - are pinned at the source, as
/// <see cref="SourceGuards"/> describes.
/// </para>
/// </summary>
// StaThread.Run joins a thread to construct the owner window WPF requires an STA thread for.
[Collection(SchedulingSensitiveCollection.Name)]
public sealed class KappaWindowListTests
{
    private static string WindowSource() =>
        SourceGuards.Read("TarkovHelper", "Pages", "Components", "KappaQuestListWindow.cs");

    private static TarkovTask Flagged(string name, string trader = "Prapor", string? nameKo = null)
        => new()
        {
            Ids = new List<string> { "id-" + name },
            Name = name,
            NameKo = nameKo,
            NormalizedName = name,
            Trader = trader,
            ReqKappa = true,
        };

    private static QuestGraphService Graph(params TarkovTask[] tasks)
    {
        var graph = new QuestGraphService();
        graph.Initialize(tasks.ToList());
        return graph;
    }

    #region Where it opens (SCAN-8)

    [Fact]
    public void With_no_owner_it_centres_on_the_screen()
    {
        // The parameter is nullable and the docs promise a screen-centred window, but WPF's
        // CenterOwner has no such fallback: with a null Owner it leaves the placement to the OS
        // cascade, which walks each new window down and right of the last one. Measured on this
        // machine, two openings of the same 500x600 window landed at (783.5, 151.5) and
        // (930.5, 298.5); CenterScreen put both at (1030, 216).
        Assert.Equal(
            WindowStartupLocation.CenterScreen,
            KappaQuestListWindow.StartupLocationFor(null));
    }

    [Fact]
    public void With_an_owner_it_centres_on_the_owner()
    {
        var location = StaThread.Run(() => KappaQuestListWindow.StartupLocationFor(new Window()));

        Assert.Equal(WindowStartupLocation.CenterOwner, location);
    }

    [Fact]
    public void The_window_places_itself_by_that_rule_rather_than_naming_a_location()
    {
        // The defect was a constructor that assigned CenterOwner unconditionally. A guard,
        // because the constructor cannot be reached from here.
        var source = WindowSource();

        Assert.Contains(
            "WindowStartupLocation = StartupLocationFor(owner);", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WindowStartupLocation = WindowStartupLocation.CenterOwner;", source, StringComparison.Ordinal);
    }

    #endregion

    #region What the header says (DESIGN-2)

    [Fact]
    public void The_header_counts_the_rows_it_sits_over()
    {
        var done = Flagged("collector", "Fence");
        var alsoDone = Flagged("shooter-born-in-heaven");
        var toDo = Flagged("chemical-part-1");
        var alsoToDo = Flagged("sew-it-good-part-1");
        var graph = Graph(done, alsoDone, toDo, alsoToDo);

        bool IsDone(TarkovTask task) => task == done || task == alsoDone;

        var rows = graph.GetKappaQuestsWithStatus(IsDone);

        Assert.Equal(
            "Kappa quests (2/4)",
            KappaQuestListWindow.HeaderText(
                Rows(rows), TestLocalization.WithLanguage(AppLanguage.EN)));
    }

    [Fact]
    public void The_header_says_what_the_gauge_says_for_the_same_pass()
    {
        // The pair used to be passed in from GetKappaProgress beside the rows, so agreement was
        // the caller's to maintain; derived from the rows, the two cannot drift. Asserted
        // against the gauge's own source over the same predicate, not against a hand-written
        // string, so this fails if either side changes its idea of the count.
        var collector = Flagged("collector", "Fence");
        var flagged = Flagged("the-punisher-part-1");
        var unflagged = new TarkovTask
        {
            Ids = new List<string> { "id-debut" }, Name = "debut", NormalizedName = "debut",
        };
        var graph = Graph(collector, flagged, unflagged);

        foreach (var language in new[] { AppLanguage.EN, AppLanguage.KO, AppLanguage.JA })
        {
            var loc = TestLocalization.WithLanguage(language);
            var (completed, total, _) = graph.GetKappaProgress(task => task == collector);
            var rows = Rows(graph.GetKappaQuestsWithStatus(task => task == collector));

            Assert.Equal(
                string.Format(loc.KappaQuestListTitle, completed, total),
                KappaQuestListWindow.HeaderText(rows, loc));
        }
    }

    [Fact]
    public void An_empty_list_reads_zero_of_zero_rather_than_dividing_by_anything()
    {
        // A publish that flagged nothing: the window still opens, over no rows.
        Assert.Equal(
            "Kappa quests (0/0)",
            KappaQuestListWindow.HeaderText(
                Array.Empty<(TarkovTask, bool)>(), TestLocalization.WithLanguage(AppLanguage.EN)));
    }

    [Fact]
    public void Neither_page_asks_for_the_count_a_second_time_to_open_the_list()
    {
        // Both handlers used to walk every flagged quest twice: once for the rows and once for
        // the header's pair. Guards, because neither page can be constructed here.
        var collectorHandler = SourceGuards.MemberBody(
            SourceGuards.Read("TarkovHelper", "Pages", "CollectorPage.xaml.cs"),
            "private void BtnCollectorKappaQuests_Click(");
        var questHandler = SourceGuards.MemberBody(
            SourceGuards.Read("TarkovHelper", "Pages", "QuestListPage.xaml.cs"),
            "private void BtnShowKappaQuests_Click(");

        foreach (var handler in new[] { collectorHandler, questHandler })
        {
            // The rows come from pass.KappaQuests(), the one guarded reading of the flagged set;
            // the handler asks for them and passes them on with no number of its own.
            Assert.Contains("pass.KappaQuests()", handler, StringComparison.Ordinal);
            Assert.DoesNotContain("KappaProgress", handler, StringComparison.Ordinal);
            Assert.DoesNotContain("GetKappaProgress", handler, StringComparison.Ordinal);
        }

        // Neither page queries the flagged set itself any more: the row query lives on the render
        // pass, once, behind the built-yet guard (RenderPassStatusTests runs it). Asserted over
        // the whole file because the reading is expression-bodied and has no braces to match; a
        // second query would have to be spelled somewhere this name is not.
        foreach (var page in new[] { "CollectorPage.xaml.cs", "QuestListPage.xaml.cs" })
        {
            Assert.DoesNotContain(
                "GetKappaQuestsWithStatus(",
                SourceGuards.Read("TarkovHelper", "Pages", page), StringComparison.Ordinal);
        }

        var pass = SourceGuards.Read("TarkovHelper", "Pages", "RenderPass.cs");

        Assert.Contains(
            "_graph.IsInitialized ? _graph.GetKappaQuestsWithStatus(IsDone) : null",
            pass, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(pass, @"GetKappaQuestsWithStatus\("));

        // And the window takes no numbers to be given: the owner, the rows, the strings source.
        var window = WindowSource();
        Assert.DoesNotContain("int completed", window, StringComparison.Ordinal);
        Assert.DoesNotContain("int total", window, StringComparison.Ordinal);
    }

    #endregion

    #region What it calls each quest (DESIGN-3)

    [Theory]
    [InlineData(AppLanguage.EN, "Debut")]
    [InlineData(AppLanguage.KO, "데뷔")]
    public void A_row_is_named_the_way_the_quest_list_names_it(AppLanguage language, string expected)
    {
        // The quest page used to hand the window its own resolver, GetLocalizedNames(...)
        // .DisplayName, which is character for character GetQuestName's rule: the two differ
        // only in the subtitle the window never renders. So the window resolving names itself
        // cannot change a single row's text - pinned here over both rules.
        var quest = new TarkovTask { Name = "Debut", NameKo = "데뷔" };
        var loc = TestLocalization.WithLanguage(language);

        Assert.Equal(expected, loc.GetQuestName(quest));
        Assert.Equal(
            LocalizationService.GetQuestDisplayName(language, quest).DisplayName,
            loc.GetQuestName(quest));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_untranslated_row_falls_back_to_English_under_both_rules(string? nameKo)
    {
        // The boundary the two rules could have disagreed on: a missing translation.
        var quest = new TarkovTask { Name = "Debut", NameKo = nameKo };
        var loc = TestLocalization.WithLanguage(AppLanguage.KO);

        Assert.Equal("Debut", loc.GetQuestName(quest));
        Assert.Equal(
            LocalizationService.GetQuestDisplayName(AppLanguage.KO, quest).DisplayName,
            loc.GetQuestName(quest));
    }

    [Fact]
    public void The_window_resolves_the_names_itself_and_no_caller_passes_a_resolver()
    {
        var window = WindowSource();
        Assert.Contains("loc.GetQuestName(quest)", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Func<TarkovTask, string>", window, StringComparison.Ordinal);

        foreach (var page in new[] { "CollectorPage.xaml.cs", "QuestListPage.xaml.cs" })
        {
            var source = SourceGuards.Read("TarkovHelper", "Pages", page);
            var call = source.IndexOf("KappaQuestListWindow.Show(", StringComparison.Ordinal);
            Assert.True(call >= 0, $"{page} no longer opens the Kappa quest list.");

            var arguments = source[call..source.IndexOf(';', call)];
            Assert.DoesNotContain("GetQuestName", arguments, StringComparison.Ordinal);
            Assert.DoesNotContain("GetLocalizedNames", arguments, StringComparison.Ordinal);
        }
    }

    #endregion

    /// <summary>The rows as the window takes them: the service's tuple, renamed.</summary>
    private static IReadOnlyList<(TarkovTask Quest, bool IsDone)> Rows(
        List<(TarkovTask Quest, bool IsCompleted)> rows)
        => rows.Select(row => (row.Quest, row.IsCompleted)).ToList();
}
