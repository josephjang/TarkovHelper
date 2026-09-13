using System.Text.RegularExpressions;
using TarkovHelper.Pages;

namespace TarkovHelper.Tests;

/// <summary>
/// What the Collector page has to keep true across a reload of its item list: the user's
/// selection survives the rows being replaced, and everything the page says about that list is
/// read from the scope the list was built under.
/// <para>
/// The defect these pin: <c>ApplyFilters</c> assigned <c>LstItems.ItemsSource</c> directly, and
/// WPF answers a swap for new instances by clearing the selection and raising SelectionChanged
/// with <c>SelectedItem == null</c>. The handler read that as a deselection and wiped the
/// remembered NAME as well as the instance, so the restore in <c>UpdateDetailPanel</c> had
/// nothing to restore from: the detail pane collapsed to "Select an item" on every progress,
/// language and database refresh. And the list's scope was half-remembered - the pass was
/// stored, the "include prerequisites" option was re-read from the live checkbox - so the quest
/// sources and the stats line could describe a list the option had moved past.
/// </para>
/// <para>
/// The selection's identity is behavioural here, over <see cref="CollectorPage.FindSelectedRow"/>
/// (the one resolver both writers of the pane use). The rest is structural, because the page
/// cannot be constructed in this suite: its markup resolves App.xaml's brushes and its
/// constructor reaches the singletons that open the databases (see <see cref="SourceGuards"/>).
/// </para>
/// </summary>
public sealed class CollectorPageFixSelectionTests
{
    private static readonly string Source =
        SourceGuards.Read("TarkovHelper", "Pages", "CollectorPage.xaml.cs");

    private static string Body(string signature) => SourceGuards.MemberBody(Source, signature);

    /// <summary>One item row, as a load builds it: a fresh instance keyed by normalized name.</summary>
    private static CollectorItemViewModel Row(string normalizedName) =>
        new()
        {
            ItemId = normalizedName,
            ItemNormalizedName = normalizedName,
            DisplayName = normalizedName
        };

    [Fact]
    public void A_reload_finds_the_selected_item_again_as_the_instance_it_just_built()
    {
        var beforeReload = Row("gas-analyzer");
        var afterReload = Row("gas-analyzer");

        var found = CollectorPage.FindSelectedRow(
            beforeReload.ItemNormalizedName, new[] { Row("bronze-lion"), afterReload });

        // The name is the identity that survives a reload; the instance is not.
        Assert.Same(afterReload, found);
        Assert.NotSame(beforeReload, found);
    }

    [Fact]
    public void The_selected_row_is_matched_the_way_every_other_item_lookup_matches()
    {
        var row = Row("gas-analyzer");

        Assert.Same(row, CollectorPage.FindSelectedRow("Gas-Analyzer", new[] { row }));
    }

    [Fact]
    public void Nothing_is_selected_when_no_name_is_remembered()
    {
        var rows = new[] { Row("gas-analyzer") };

        Assert.Null(CollectorPage.FindSelectedRow(null, rows));
        Assert.Null(CollectorPage.FindSelectedRow(string.Empty, rows));
    }

    [Fact]
    public void An_item_the_rows_do_not_hold_resolves_to_no_row_rather_than_throwing()
    {
        // Two ways to have no row: the filters exclude the selected item, or there are no rows at
        // all (before the first load, or a search that matches nothing). Neither is an error, and
        // neither may lose the name - the detail pane still describes the selection.
        Assert.Null(CollectorPage.FindSelectedRow("gas-analyzer", new[] { Row("bronze-lion") }));
        Assert.Null(CollectorPage.FindSelectedRow("gas-analyzer", Array.Empty<CollectorItemViewModel>()));
    }

    [Fact]
    public void The_rows_are_swapped_only_through_the_helper_that_keeps_the_selection()
    {
        // A bare assignment costs the user their selection, so there is exactly one, inside the
        // helper: suppressed for the swap (the handler may still null the name for a REAL
        // deselection, which is the user's), restored in a finally, and re-selecting the row by
        // name afterwards.
        Assert.Single(Regex.Matches(Source, @"LstItems\.ItemsSource = "));
        Assert.DoesNotContain(
            "LstItems.ItemsSource = ", Body("private void ApplyFilters()"), StringComparison.Ordinal);

        var swap = Body("private void SetItemsSourcePreservingSelection(List<CollectorItemViewModel> rows)");

        Assert.Contains("_isInitializing = true;", swap, StringComparison.Ordinal);
        Assert.Contains("finally", swap, StringComparison.Ordinal);
        Assert.Contains("_isInitializing = wasInitializing;", swap, StringComparison.Ordinal);
        Assert.Contains("FindSelectedRow(_selectedItemNormalizedName, rows)", swap, StringComparison.Ordinal);
        Assert.Contains(
            "if (_isInitializing) return;",
            Body("private void LstItems_SelectionChanged(object sender, SelectionChangedEventArgs e)"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_reload_drops_the_previous_loads_instance_and_resolves_the_name_again()
    {
        // The rebuilt view models are new instances, so keeping the old one would paint the pane
        // from counts the load has just replaced.
        Assert.Contains("_selectedItem = null;", Body("private void LoadItems()"), StringComparison.Ordinal);
        Assert.Contains(
            "_selectedItem ??= FindSelectedRow(_selectedItemNormalizedName, _allItemViewModels);",
            Body("private void UpdateDetailPanel()"), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_reload_path_re_renders_the_list_and_repaints_the_pane()
    {
        // Four paths spelled the same three or four statements by hand, and two of them left out
        // the repaint - the pane went on describing the previous load until something else
        // touched it.
        var reload = Body("private void ReloadItems()");

        Assert.Contains("LoadItems();", reload, StringComparison.Ordinal);
        Assert.Contains("ApplyFilters();", reload, StringComparison.Ordinal);
        Assert.Contains("UpdateDetailPanel();", reload, StringComparison.Ordinal);
        Assert.Contains("StartBackgroundImageLoad();", reload, StringComparison.Ordinal);

        Assert.Contains(
            "Dispatcher.Invoke(ReloadItems);",
            Body("private void OnProgressChanged(object? sender, EventArgs e)"), StringComparison.Ordinal);
        Assert.Contains(
            "ReloadItems();",
            Body("private void OnLanguageChanged(object? sender, AppLanguage e)"), StringComparison.Ordinal);
        Assert.Contains(
            "ReloadItems();",
            Body("private void OnDatabaseRefreshed(object? sender, EventArgs e)"), StringComparison.Ordinal);
        Assert.Contains(
            "ReloadItems();",
            Body("private void ChkIncludePreQuest_Changed(object sender, RoutedEventArgs e)"),
            StringComparison.Ordinal);

        // The icon pass is deliberately not awaited, but a dropped Task swallows its exception,
        // so it is started in one place that reports a fault.
        Assert.DoesNotContain("_ = LoadImagesInBackgroundAsync();", Source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(Source, @"LoadImagesInBackgroundAsync\(\)\.ContinueWith"));
    }

    [Fact]
    public void No_refresh_is_handed_to_the_dispatcher_as_an_async_lambda()
    {
        // Dispatcher.Invoke(async () => ...) binds to the Func<Task> overload: it returns at the
        // body's first await and drops the Task carrying the rest of the refresh, so the work
        // finishes unobserved and an exception in it is swallowed. The page's refreshes are
        // synchronous instead, which leaves no await to return at.
        Assert.DoesNotContain("Dispatcher.Invoke(async", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("InvokeAsync(async", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginInvoke(async", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_scope_option_is_read_once_per_load_and_never_again()
    {
        // Half of the list's input used to be re-derived from a live control after the fact.
        Assert.Single(Regex.Matches(Source, @"ChkIncludePreQuest\.IsChecked"));
        Assert.Contains(
            "ChkIncludePreQuest.IsChecked == true;",
            Body("private ListScope CaptureListScope(RenderPass pass)"), StringComparison.Ordinal);

        var sources = Body(
            "private List<CollectorQuestItemSourceViewModel> GetQuestSources(string itemNormalizedName)");

        Assert.Contains("_listScope is not { } scope", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("ChkIncludePreQuest", sources, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderPass.Capture", sources, StringComparison.Ordinal);
        Assert.Contains(
            "_listScope?.IncludePrerequisites", Body("private void ApplyFilters()"), StringComparison.Ordinal);
    }

    [Fact]
    public void One_decision_site_says_whose_items_count()
    {
        // The aggregation and the quest sources each carried the same three exclusions over
        // AllTasks, and each re-ran the scope walk: the aggregation once per load, the sources
        // once per selection and once per inventory change.
        var consumers = new[]
        {
            Body("private Dictionary<string, CollectorQuestItemAggregate> GetCollectorItemRequirements(ListScope scope)"),
            Body("private List<CollectorQuestItemSourceViewModel> GetQuestSources(string itemNormalizedName)"),
        };

        foreach (var consumer in consumers)
        {
            Assert.Contains("ItemsInScope(scope)", consumer, StringComparison.Ordinal);
            Assert.DoesNotContain("AllTasks", consumer, StringComparison.Ordinal);
            Assert.DoesNotContain("QuestsInScope(", consumer, StringComparison.Ordinal);
            Assert.DoesNotContain("task.RequiredItems", consumer, StringComparison.Ordinal);
        }

        // And the walk has one call site, inside the capture. The rules it runs are no longer
        // spelled on the page at all: they are CollectorScope's, where CollectorScopeTests runs
        // them instead of reading them.
        Assert.Single(Regex.Matches(Source, @"CollectorScope\.QuestsInScope\("));
        Assert.Contains(
            "CollectorScope.QuestsInScope(", Body("private ListScope CaptureListScope(RenderPass pass)"),
            StringComparison.Ordinal);
        Assert.Contains(
            "CollectorScope.ItemsInScope(_questProgressService.AllTasks, scope.Quests)",
            Body("private IEnumerable<(TarkovTask Task, QuestItem Item)> ItemsInScope(ListScope scope)"),
            StringComparison.Ordinal);
        Assert.Contains(
            "CollectorScope.Aggregate(ItemsInScope(scope), _itemLookup)",
            Body("private Dictionary<string, CollectorQuestItemAggregate> GetCollectorItemRequirements(ListScope scope)"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("IsCurrency", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("QuestStatus.Unavailable", Source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_detail_pane_has_one_writer_and_one_fulfillment_switch()
    {
        // ShowItemDetail was UpdateDetailPanel, plus an inlined copy of the inventory display,
        // plus a null branch its only caller had already excluded. The status label and its
        // colour were two switches over the same value, in two copies each.
        Assert.DoesNotContain("ShowItemDetail", Source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(Source, @"ItemFulfillmentStatus\.Fulfilled =>"));
        Assert.Single(Regex.Matches(Source, @"FindResource\(""SuccessBrush""\)"));
        Assert.Contains(
            "FulfillmentDisplay(_selectedItem.FulfillmentStatus)",
            Body("private void UpdateDetailInventoryDisplay()"), StringComparison.Ordinal);
        Assert.Contains(
            "UpdateDetailPanel();",
            Body("private void SelectItemInternal(string itemNormalizedName)"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_kappa_count_is_taken_once_through_the_pass_the_way_the_quest_page_takes_it()
    {
        // Two call sites spelled the predicate by hand, then each page owned a private copy of
        // the one-liner; the reading is the render pass's now, so both pages ask the same
        // question of the same object and neither spells the query at all.
        Assert.DoesNotContain("GetKappaProgress(", Source, StringComparison.Ordinal);
        Assert.Contains(
            "_graph.IsInitialized ? _graph.GetKappaProgress(IsDone) : null",
            SourceGuards.Read("TarkovHelper", "Pages", "RenderPass.cs"), StringComparison.Ordinal);

        // And the page takes the count ONCE, for the unlock panel's line. Opening the quest list
        // used to take it a second time, for the window's header, over a second walk of the same
        // flagged set; the window counts that header from the rows it is handed
        // (KappaQuestListWindow.HeaderText), so the handler walks them once and passes them
        // through with no number of its own. The single-match assertion is what keeps a second
        // walk out of the page, wherever it would be reintroduced.
        Assert.Contains(
            "pass.KappaProgress()", Body("private void RebuildUnlockPanel(RenderPass pass)"),
            StringComparison.Ordinal);
        Assert.Single(Regex.Matches(Source, @"pass\.KappaProgress\(\)"));

        var open = Body("private void BtnCollectorKappaQuests_Click(object sender, RoutedEventArgs e)");

        Assert.Contains("pass.KappaQuests() is not { } quests", open, StringComparison.Ordinal);
        Assert.Contains(
            "KappaQuestListWindow.Show(Window.GetWindow(this), quests, _loc);",
            open, StringComparison.Ordinal);
    }

    [Fact]
    public void The_graph_precondition_is_asked_inside_the_pass_readings_and_nowhere_else()
    {
        // One spelling of "the graph is not built yet", inside each reading, answering null. The
        // panel used to substitute (0, 0, 0) at the call site instead, which paints "0/0 Kappa
        // quests completed" - the exact reading QuestGraphService.IsInitialized exists to keep
        // apart from "nothing is flagged", and the one the quest tab's gauge is forbidden to
        // paint (KappaGaugeSourceTests). The handler asked separately, so one precondition had
        // three spellings on one page; then the page owned a guarded pair the quest page owned a
        // copy of. The readings are the pass's now, so this page states the precondition nowhere
        // and cannot reach the graph to skip it: the pass does not expose one.
        var rebuild = Body("private void RebuildUnlockPanel(RenderPass pass)");

        Assert.DoesNotContain("(0, 0, 0)", Source, StringComparison.Ordinal);
        Assert.DoesNotContain("IsInitialized", Source, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(
                SourceGuards.Read("TarkovHelper", "Pages", "RenderPass.cs"),
                @"_graph\.IsInitialized \? _graph\.GetKappa").Count);

        // The graph the pass is captured with is the page's own field, so the graph its item
        // scope is walked with and the graph its Kappa count comes from are one object. All three
        // captures thread it: the item load, the coalesced settings refresh, and the click that
        // opens the quest list.
        Assert.Equal(3, Regex.Matches(Source, @"RenderPass\.Capture\(").Count);
        Assert.Equal(
            3, Regex.Matches(Source, @"RenderPass\.Capture\(_questProgressService, _questGraphService\)").Count);

        // And a missing count hides the count and the button that would open an empty list,
        // rather than showing a zero one.
        Assert.Contains("TxtKappaCount.Visibility = kappaVisibility;", rebuild, StringComparison.Ordinal);
        Assert.Contains(
            "BtnCollectorKappaQuests.Visibility = kappaVisibility;", rebuild, StringComparison.Ordinal);
    }
}
