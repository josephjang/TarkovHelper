using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TarkovHelper.Models;
using TarkovHelper.Pages.Components;
using TarkovHelper.Services;
using TarkovHelper.Services.Logging;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Pages
{
    public partial class CollectorPage : UserControl
    {
        // An instance field, not a static one: a static initializer would drag the logging
        // singleton (and the settings it reads) in behind any static member of this page, which
        // the unit suite touches without ever constructing the page.
        private readonly ILogger _log = Log.For<CollectorPage>();
        private readonly LocalizationService _loc = LocalizationService.Instance;
        private readonly QuestProgressService _questProgressService = QuestProgressService.Instance;
        private readonly QuestGraphService _questGraphService = QuestGraphService.Instance;
        private readonly ItemInventoryService _inventoryService = ItemInventoryService.Instance;
        private readonly ImageCacheService _imageCache = ImageCacheService.Instance;
        private List<CollectorItemViewModel> _allItemViewModels = new();
        private Dictionary<string, TarkovItem>? _itemLookup;

        /// <summary>
        /// Suppresses the control handlers while the PAGE is the one changing a control, so a
        /// programmatic change cannot be read as the user filtering or picking a row. True until
        /// the first load has finished, and again around <see cref="SelectItemInternal"/> and the
        /// rows swap in <see cref="SetItemsSourcePreservingSelection"/>.
        /// </summary>
        private bool _isInitializing = true;
        private bool _isDataLoaded = false;
        private bool _isUnloaded = false;
        private bool _needsRefreshOnLoad = false; // Flag to indicate data refresh needed after unload
        private string? _pendingItemSelection = null;

        /// <summary>
        /// What the rendered item list was built from, in one value, so the detail panel's quest
        /// sources name exactly the quests whose items the list shows. Null until the first load.
        /// <para>
        /// The whole input, not half of it: it used to hold the pass alone, and the other half of
        /// the same decision (the "include prerequisites" option) was re-read from the live
        /// checkbox wherever it was needed again, which let the panel and the stats line describe
        /// an option the list had not been rebuilt under. Keeping the quest set too means the
        /// scope walk runs once per load instead of once per selection.
        /// </para>
        /// </summary>
        private ListScope? _listScope;

        /// <summary>
        /// The full input of one rendered item list: the pass its quest statuses were read from,
        /// the "include prerequisites" option it was built under, and the quests whose items it
        /// therefore holds. Captured by <see cref="CaptureListScope"/>, once per load.
        /// </summary>
        private sealed record ListScope(RenderPass Pass, bool IncludePrerequisites, HashSet<string> Quests);

        /// <summary>
        /// Collapses the profile-scoped settings burst into one rebuild of the unlock panel. A
        /// published reload (a profile switch, a reset, a self-heal) announces the player level,
        /// the Scav Rep, one <see cref="SettingsService.TraderLoyaltyChanged"/> per STORED loyalty
        /// entry and then <see cref="SettingsService.ProfileSettingsReloaded"/>; for a seven-trader
        /// profile that is 1 + 1 + 7 + 1 = ten events this page listens to, so the panel would
        /// otherwise be repainted ten times over one snapshot. Built by
        /// <see cref="RefreshCoalescer.OnDispatcher"/> in the constructor BODY, not here:
        /// <see cref="System.Windows.Threading.DispatcherObject.Dispatcher"/> is only set once the
        /// base constructor has run, which is after field initializers.
        /// </summary>
        private readonly RefreshCoalescer _unlockRefresh;

        public CollectorPage()
        {
            _unlockRefresh = RefreshCoalescer.OnDispatcher(this, RefreshUnlockPanelForSettingsChange);

            InitializeComponent();
            ApplyLocalizedTexts();
            SubscribeServiceEvents();

            Loaded += CollectorPage_Loaded;
            Unloaded += CollectorPage_Unloaded;
        }

        /// <summary>
        /// The page's own localized labels: the option's text and tooltip. Written once at
        /// construction and again on a language switch. The stats line is composed in
        /// <see cref="ApplyFilters"/> and the unlock panel in <see cref="RebuildUnlockPanel"/>,
        /// both of which the language switch re-runs; the page's other literals (search
        /// placeholder, combo items, column headers, item detail) stay English by the recorded
        /// Non-Goal.
        /// </summary>
        private void ApplyLocalizedTexts()
        {
            ChkIncludePreQuest.Content = _loc.CollectorIncludePrerequisites;
            ChkIncludePreQuest.ToolTip = _loc.CollectorIncludePrerequisitesTip;
        }

        /// <summary>
        /// The service events this page consumes. The constructor, Unloaded and the Loaded
        /// re-subscribe all go through this pair, so an event added to one list cannot be
        /// forgotten in another (the three lists used to be kept by hand).
        /// <para>
        /// Four settings events, not the quest page's eight (feature-kappa-collector-1-1.spec.md,
        /// TD4). The panel reads only the level, the Scav karma and the trader loyalty: those are
        /// the gates Collector itself carries, and the Kappa count moves on recorded progress
        /// alone. The item list is the part that needs stating carefully, because it DOES read
        /// the edition, prestige and faction values, transitively: its scope drops a quest whose
        /// status is Unavailable, and that is exactly what
        /// <see cref="QuestProgressService.GetStatus(TarkovTask)"/> returns for an unmet edition,
        /// prestige or faction gate - for Collector AND for every prerequisite in the scope. What
        /// keeps the list insensitive is the DATA, not this code: no quest in Collector's
        /// prerequisite closure carries one of those gates, which
        /// <c>PublishedDataContentTests.The_quests_Collector_depends_on_carry_no_edition_prestige_or_faction_gate</c>
        /// pins against the published file. The DSP count cannot matter either way, since its
        /// gate reads Locked and Locked stays in scope. When that guard fails, this page needs
        /// the edition, prestige and faction events too, and the settings refresh has to reload
        /// the ITEMS and not just the panel. A publish that changed the data arrives through
        /// DataRefreshed, which rebuilds everything. Progress and language changes reload the
        /// whole page already, and the panel is part of that reload.
        /// </para>
        /// </summary>
        private void SubscribeServiceEvents()
        {
            _loc.LanguageChanged += OnLanguageChanged;
            _questProgressService.ProgressChanged += OnProgressChanged;
            _inventoryService.InventoryChanged += OnInventoryChanged;
            QuestDbService.Instance.DataRefreshed += OnDatabaseRefreshed;
            ItemDbService.Instance.DataRefreshed += OnDatabaseRefreshed;
            SettingsService.Instance.PlayerLevelChanged += OnPlayerLevelChanged;
            SettingsService.Instance.ScavRepChanged += OnScavRepChanged;
            SettingsService.Instance.TraderLoyaltyChanged += OnTraderLoyaltyChanged;
            SettingsService.Instance.ProfileSettingsReloaded += OnProfileSettingsReloaded;
        }

        /// <summary>Mirror of <see cref="SubscribeServiceEvents"/>; keep the lists in sync.</summary>
        private void UnsubscribeServiceEvents()
        {
            _loc.LanguageChanged -= OnLanguageChanged;
            _questProgressService.ProgressChanged -= OnProgressChanged;
            _inventoryService.InventoryChanged -= OnInventoryChanged;
            QuestDbService.Instance.DataRefreshed -= OnDatabaseRefreshed;
            ItemDbService.Instance.DataRefreshed -= OnDatabaseRefreshed;
            SettingsService.Instance.PlayerLevelChanged -= OnPlayerLevelChanged;
            SettingsService.Instance.ScavRepChanged -= OnScavRepChanged;
            SettingsService.Instance.TraderLoyaltyChanged -= OnTraderLoyaltyChanged;
            SettingsService.Instance.ProfileSettingsReloaded -= OnProfileSettingsReloaded;
        }

        private void CollectorPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            _needsRefreshOnLoad = true; // Mark for refresh on next load to catch changes
            UnsubscribeServiceEvents();
        }

        // The four settings events each book the same coalesced rebuild: a value typed into the
        // drawer, a profile switch and a reset all reach the panel through one refresh per burst.

        private void OnPlayerLevelChanged(object? sender, int e) => _unlockRefresh.Request();

        private void OnScavRepChanged(object? sender, double e) => _unlockRefresh.Request();

        private void OnTraderLoyaltyChanged(object? sender, TraderLoyaltyChange e) => _unlockRefresh.Request();

        // Not redundant with the three above: a profile that has entered no levels raises no
        // loyalty event at all, and this closing signal joins the burst it closes rather than
        // booking a rebuild of its own.
        private void OnProfileSettingsReloaded(object? sender, EventArgs e) => _unlockRefresh.Request();

        /// <summary>
        /// The rebuild a profile-scoped settings change needs: the unlock panel only, against a
        /// fresh pass. The item list is not reloaded, because none of the four events can move a
        /// quest into or out of Done, Failed or Unavailable, which is all the list's scope reads.
        /// Runs on the dispatcher, once per burst, scheduled by <see cref="_unlockRefresh"/>.
        /// </summary>
        private void RefreshUnlockPanelForSettingsChange()
        {
            // Scheduled rather than inline, so it can land after Unloaded dropped the
            // subscriptions or before the first load built anything; both are skipped, and the
            // next Loaded or load rebuilds the panel anyway.
            if (_isUnloaded || !_isDataLoaded) return;

            RebuildUnlockPanel(RenderPass.Capture(_questProgressService, _questGraphService));
        }

        private void OnInventoryChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var vm in _allItemViewModels)
                {
                    var inventory = _inventoryService.GetInventory(vm.ItemNormalizedName);
                    vm.OwnedFirQuantity = inventory.FirQuantity;
                    vm.OwnedNonFirQuantity = inventory.NonFirQuantity;
                }
                UpdateDetailPanel();
            });
        }

        /// <summary>
        /// A published database swap: re-read the item lookup, then reload everything from it.
        /// <para>
        /// Posted rather than invoked, because the raiser may be the background thread that just
        /// finished the swap and blocking it on a full UI reload is how this path would deadlock.
        /// BeginInvoke rather than InvokeAsync: a discarded <c>DispatcherOperation&lt;T&gt;.Task</c>
        /// swallows the reload's exception, while BeginInvoke's operation raises
        /// <see cref="System.Windows.Threading.Dispatcher.UnhandledException"/>, the error path
        /// App.xaml.cs logs through (the rule <see cref="RefreshCoalescer.OnDispatcher"/> is built
        /// on). The body is synchronous, so nothing of it can run outside the posted callback.
        /// </para>
        /// </summary>
        private void OnDatabaseRefreshed(object? sender, EventArgs e)
        {
            // DB 업데이트 후 데이터 다시 로드
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Item lookup 새로고침
                _itemLookup = ItemDbService.Instance.GetItemLookup();

                // Collector items 데이터 다시 로드
                ReloadItems();
            }));
        }

        private async void CollectorPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_isUnloaded)
            {
                _isUnloaded = false;
                SubscribeServiceEvents();
            }

            // Check if data needs refresh (changes might have occurred while unloaded)
            if (_isDataLoaded && _needsRefreshOnLoad)
            {
                _needsRefreshOnLoad = false;
                ReloadItems();
                return;
            }

            if (_isDataLoaded)
            {
                return;
            }

            LoadingOverlay.Visibility = Visibility.Visible;
            MainContent.Visibility = Visibility.Collapsed;

            try
            {
                // Load items lookup from DB
                var itemDbService = ItemDbService.Instance;
                if (!itemDbService.IsLoaded)
                {
                    await itemDbService.LoadItemsAsync();
                }
                if (_isUnloaded) return;

                _itemLookup = itemDbService.GetItemLookup();

                LoadItems();
                if (_isUnloaded) return;

                _isInitializing = false;
                _isDataLoaded = true;
                ApplyFilters();

                if (!string.IsNullOrEmpty(_pendingItemSelection))
                {
                    var pendingName = _pendingItemSelection;
                    _pendingItemSelection = null;
                    SelectItemInternal(pendingName);
                }
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                MainContent.Visibility = Visibility.Visible;
            }

            StartBackgroundImageLoad();
        }

        /// <summary>
        /// Starts the background icon pass without waiting for it: the list is usable before the
        /// icons arrive. Not simply discarded, though - a dropped Task swallows its exception, so
        /// a fault is logged here, which is what the first load alone used to do.
        /// </summary>
        private void StartBackgroundImageLoad()
        {
            _ = LoadImagesInBackgroundAsync().ContinueWith(
                t => _log.Error("Background image loading failed", t.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private void OnLanguageChanged(object? sender, AppLanguage e)
        {
            Dispatcher.Invoke(() =>
            {
                ApplyLocalizedTexts();
                ReloadItems();
            });
        }

        private void OnProgressChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(ReloadItems);
        }

        /// <summary>
        /// The page's one reload: rebuild the view models from a fresh pass, re-render the list,
        /// repaint the detail pane and then fill the icons in the background. Every path that has
        /// to re-read the data (a progress change, a language switch, a database swap, the scope
        /// option, coming back to the tab) goes through here, so none of them can forget a step -
        /// two of them used to leave the detail pane describing the previous load.
        /// <para>
        /// Synchronous, and deliberately so: a reload posted as an <c>async</c> lambda to
        /// <c>Dispatcher.Invoke</c> binds to the <c>Func&lt;Task&gt;</c> overload, which returns at
        /// the body's first await and drops the task carrying the rest of the work and any
        /// exception in it. Keeping the whole sequence synchronous means there is no await to
        /// return at; work that must be awaited belongs in a caller that can await it.
        /// </para>
        /// </summary>
        private void ReloadItems()
        {
            LoadItems();
            ApplyFilters();
            UpdateDetailPanel();
            StartBackgroundImageLoad();
        }

        /// <summary>
        /// Rebuilds the item view models, the scope they were aggregated under and the unlock
        /// panel above them, from one pass. Callers that also have to re-render the list and the
        /// detail pane use <see cref="ReloadItems"/>; the first load calls this directly, because
        /// it renders after flipping the initialization flags.
        /// </summary>
        private void LoadItems()
        {
            // One pass for the item aggregation, the detail panel's quest sources and the unlock
            // panel, so the list and the panel above it describe one profile (see RenderPass).
            var pass = RenderPass.Capture(_questProgressService, _questGraphService);
            var scope = CaptureListScope(pass);
            _listScope = scope;

            var collectorItems = GetCollectorItemRequirements(scope);

            _allItemViewModels = collectorItems.Values.Select(item =>
            {
                var (displayName, subtitle, showSubtitle) = GetLocalizedNames(
                    item.ItemName, item.ItemNameKo, item.ItemNameJa);

                return new CollectorItemViewModel
                {
                    ItemId = item.ItemId,
                    ItemNormalizedName = item.ItemNormalizedName,
                    DisplayName = displayName,
                    SubtitleName = subtitle,
                    SubtitleVisibility = showSubtitle ? Visibility.Visible : Visibility.Collapsed,
                    QuestCount = item.QuestCount,
                    QuestFIRCount = item.QuestFIRCount,
                    TotalCount = item.QuestCount,
                    TotalFIRCount = item.QuestFIRCount,
                    FoundInRaid = item.FoundInRaid,
                    IconLink = item.IconLink,
                    WikiLink = item.WikiLink
                };
            }).ToList();

            // Load inventory data
            foreach (var vm in _allItemViewModels)
            {
                var inventory = _inventoryService.GetInventory(vm.ItemNormalizedName);
                vm.OwnedFirQuantity = inventory.FirQuantity;
                vm.OwnedNonFirQuantity = inventory.NonFirQuantity;
            }

            // Every view model above is a NEW instance, so a remembered one belongs to the
            // previous load and would paint the detail pane from counts this load has replaced.
            // The name is what survives; the render resolves the instance from it again.
            _selectedItem = null;

            RebuildUnlockPanel(pass);
        }

        /// <summary>
        /// The items the Collector quest and (with the option on) its prerequisites want, summed
        /// per item within the scope captured for this load. The rule is
        /// <see cref="CollectorScope.Aggregate"/>; what this adds is the page's two inputs, the
        /// scope's pairs and the loaded Items table.
        /// </summary>
        private Dictionary<string, CollectorQuestItemAggregate> GetCollectorItemRequirements(ListScope scope)
        {
            return CollectorScope.Aggregate(ItemsInScope(scope), _itemLookup);
        }

        private async Task LoadImagesInBackgroundAsync()
        {
            if (_allItemViewModels == null || _allItemViewModels.Count == 0)
                return;

            await LoadVisibleItemImagesAsync();
            await LoadRemainingItemImagesAsync();
        }

        private Task LoadVisibleItemImagesAsync()
        {
            var visibleItems = GetVisibleItems();
            if (visibleItems.Count == 0)
                return Task.CompletedTask;

            var itemsNeedingIcons = visibleItems
                .Where(vm => !string.IsNullOrEmpty(vm.ItemId) && vm.IconSource == null)
                .ToList();

            if (itemsNeedingIcons.Count == 0)
                return Task.CompletedTask;

            LoadItemImages(itemsNeedingIcons);
            return Task.CompletedTask;
        }

        private Task LoadRemainingItemImagesAsync()
        {
            if (_allItemViewModels == null)
                return Task.CompletedTask;

            var itemsNeedingIcons = _allItemViewModels
                .Where(vm => !string.IsNullOrEmpty(vm.ItemId) && vm.IconSource == null)
                .ToList();

            if (itemsNeedingIcons.Count == 0)
                return Task.CompletedTask;

            LoadItemImages(itemsNeedingIcons);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Load images for a specific list of items from local files.
        /// </summary>
        private void LoadItemImages(List<CollectorItemViewModel> items)
        {
            if (items.Count == 0)
                return;

            foreach (var vm in items)
            {
                if (_isUnloaded) return;
                if (vm.IconSource != null) continue;

                var icon = _imageCache.GetLocalItemIcon(vm.ItemId);
                if (icon != null)
                {
                    vm.IconSource = icon;
                }
            }
        }

        private List<CollectorItemViewModel> GetVisibleItems()
        {
            var visibleItems = new List<CollectorItemViewModel>();

            if (LstItems.ItemsSource == null)
                return visibleItems;

            var scrollViewer = GetScrollViewer(LstItems);
            if (scrollViewer == null)
                return visibleItems;

            var itemsSource = LstItems.ItemsSource as IList<CollectorItemViewModel>;
            if (itemsSource == null || itemsSource.Count == 0)
                return visibleItems;

            const double estimatedItemHeight = 50;
            var viewportHeight = scrollViewer.ViewportHeight;
            var verticalOffset = scrollViewer.VerticalOffset;

            var startIndex = Math.Max(0, (int)(verticalOffset / estimatedItemHeight) - 2);
            var visibleCount = (int)(viewportHeight / estimatedItemHeight) + 5;
            var endIndex = Math.Min(itemsSource.Count - 1, startIndex + visibleCount);

            for (int i = startIndex; i <= endIndex; i++)
            {
                visibleItems.Add(itemsSource[i]);
            }

            return visibleItems;
        }

        private static ScrollViewer? GetScrollViewer(DependencyObject element)
        {
            if (element is ScrollViewer sv)
                return sv;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            {
                var child = VisualTreeHelper.GetChild(element, i);
                var result = GetScrollViewer(child);
                if (result != null)
                    return result;
            }
            return null;
        }

        private System.Windows.Threading.DispatcherTimer? _scrollDebounceTimer;

        private void LstItems_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange == 0 && e.ViewportHeightChange == 0)
                return;

            _scrollDebounceTimer?.Stop();
            _scrollDebounceTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _scrollDebounceTimer.Tick += (s, args) =>
            {
                _scrollDebounceTimer?.Stop();
                LoadVisibleItemImagesAsync();
            };
            _scrollDebounceTimer.Start();
        }

        private (string DisplayName, string Subtitle, bool ShowSubtitle) GetLocalizedNames(
            string name, string? nameKo, string? nameJa)
        {
            var lang = _loc.CurrentLanguage;

            if (lang == AppLanguage.EN)
            {
                return (name, string.Empty, false);
            }

            var localizedName = lang switch
            {
                AppLanguage.KO => nameKo,
                AppLanguage.JA => nameJa,
                _ => null
            };

            if (!string.IsNullOrEmpty(localizedName))
            {
                return (localizedName, name, true);
            }

            return (name, string.Empty, false);
        }

        private void ApplyFilters()
        {
            var searchText = TxtSearch.Text?.Trim().ToLowerInvariant() ?? string.Empty;
            var fulfillmentFilter = (CmbFulfillment.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
            var firOnly = ChkFirOnly.IsChecked == true;
            var hideFulfilled = ChkHideFulfilled.IsChecked == true;
            var sortBy = (CmbSort.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Name";

            var filtered = _allItemViewModels.Where(vm =>
            {
                if (!string.IsNullOrEmpty(searchText))
                {
                    if (!vm.DisplayName.ToLowerInvariant().Contains(searchText) &&
                        !vm.SubtitleName.ToLowerInvariant().Contains(searchText))
                        return false;
                }

                if (firOnly && !vm.FoundInRaid)
                    return false;

                if (fulfillmentFilter != "All")
                {
                    var status = vm.FulfillmentStatus;
                    if (fulfillmentFilter == "NotStarted" && status != ItemFulfillmentStatus.NotStarted)
                        return false;
                    if (fulfillmentFilter == "InProgress" && status != ItemFulfillmentStatus.PartiallyFulfilled)
                        return false;
                    if (fulfillmentFilter == "Fulfilled" && status != ItemFulfillmentStatus.Fulfilled)
                        return false;
                }

                if (hideFulfilled && vm.IsFulfilled)
                    return false;

                return true;
            });

            filtered = sortBy switch
            {
                "Total" => filtered.OrderByDescending(vm => vm.TotalCount).ThenBy(vm => vm.DisplayName),
                "Quest" => filtered.OrderByDescending(vm => vm.QuestCount).ThenBy(vm => vm.DisplayName),
                "Progress" => filtered.OrderByDescending(vm => vm.ProgressPercent).ThenBy(vm => vm.DisplayName),
                _ => filtered.OrderBy(vm => vm.DisplayName)
            };

            var filteredList = filtered.ToList();
            SetItemsSourcePreservingSelection(filteredList);

            var totalItems = filteredList.Count;
            var totalCount = filteredList.Sum(i => i.TotalCount);
            var fulfilledCount = filteredList.Count(i => i.IsFulfilled);
            var inProgressCount = filteredList.Count(i => i.FulfillmentStatus == ItemFulfillmentStatus.PartiallyFulfilled);

            // The scope names what the list holds: Collector's items, or those plus its
            // prerequisite quests' items. It used to read "Kappa Quests Only" with the option
            // off, which under 1.1 names the thirteen flagged quests, a set this page never
            // lists (feature-kappa-collector-1-1.md, R6). Read from the scope the items were
            // aggregated under, never from the live checkbox, so the line cannot name an option
            // the list has not been rebuilt under. There is always a scope here in practice, since
            // every path into this method runs after a load (the filter handlers stay suppressed
            // until the first one finishes); with none there are no items to describe either, and
            // the option's own default is off.
            var includePrerequisites = _listScope?.IncludePrerequisites ?? false;
            var scopeText = includePrerequisites
                ? _loc.CollectorScopeWithPrerequisites
                : _loc.CollectorScopeCollectorOnly;
            TxtStats.Text = string.Format(
                _loc.CollectorStatsFormat, totalItems, totalCount, fulfilledCount, inProgressCount, scopeText);
        }

        /// <summary>
        /// Renders <paramref name="rows"/> and puts the selection back on the row for the selected
        /// item, which is more than an assignment for two reasons.
        /// <para>
        /// Replacing a ListBox's ItemsSource with DIFFERENT instances makes WPF clear the
        /// selection and raise SelectionChanged with <c>SelectedItem == null</c>, which is not the
        /// user deselecting anything: unsuppressed, <see cref="LstItems_SelectionChanged"/> read
        /// that as a deselection and wiped both the selected instance AND the remembered name, so
        /// the restore in <see cref="UpdateDetailPanel"/> had nothing left to restore from and the
        /// detail pane collapsed to "Select an item" on every progress, language and database
        /// refresh. And a reload builds brand new view models, so the remembered instance belongs
        /// to the previous list; the normalized name is the identity that survives a reload, and
        /// the row is re-found by it. An item the filters currently exclude has no row to select
        /// but is still the selection: the name is kept, and the detail pane resolves it against
        /// the full list.
        /// </para>
        /// </summary>
        private void SetItemsSourcePreservingSelection(List<CollectorItemViewModel> rows)
        {
            var wasInitializing = _isInitializing;
            _isInitializing = true;

            try
            {
                LstItems.ItemsSource = rows;

                _selectedItem = FindSelectedRow(_selectedItemNormalizedName, rows);
                LstItems.SelectedItem = _selectedItem;
            }
            finally
            {
                _isInitializing = wasInitializing;
            }
        }

        /// <summary>
        /// The row carrying the current selection among <paramref name="rows"/>, or null when
        /// nothing is selected or none of them is it. The one place the selection's identity is
        /// decided: the normalized name, case-insensitively, never the view model instance, which
        /// a reload replaces.
        /// </summary>
        internal static CollectorItemViewModel? FindSelectedRow(
            string? selectedItemNormalizedName, IEnumerable<CollectorItemViewModel> rows)
        {
            if (string.IsNullOrEmpty(selectedItemNormalizedName)) return null;

            return rows.FirstOrDefault(vm => string.Equals(
                vm.ItemNormalizedName, selectedItemNormalizedName, StringComparison.OrdinalIgnoreCase));
        }

        private void ChkIncludePreQuest_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            // The option is half of the list's scope (see ListScope), so a change to it has to
            // re-aggregate the items, not just re-filter them.
            ReloadItems();
        }

        private void CmbFulfillment_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitializing) ApplyFilters();
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isInitializing) ApplyFilters();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitializing) ApplyFilters();
        }

        private void CmbSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitializing) ApplyFilters();
        }

        public void SelectItem(string itemNormalizedName)
        {
            if (!_isDataLoaded)
            {
                _pendingItemSelection = itemNormalizedName;
                return;
            }

            SelectItemInternal(itemNormalizedName);
        }

        private void SelectItemInternal(string itemNormalizedName)
        {
            _isInitializing = true;

            try
            {
                ResetFiltersForNavigationInternal();
                ApplyFilters();

                var filteredItems = LstItems.ItemsSource as IEnumerable<CollectorItemViewModel>
                    ?? Enumerable.Empty<CollectorItemViewModel>();
                var itemVm = FindSelectedRow(itemNormalizedName, filteredItems);

                if (itemVm == null) return;

                LstItems.ScrollIntoView(itemVm);
                LstItems.UpdateLayout();
                LstItems.SelectedItem = itemVm;
                LstItems.UpdateLayout();
                LstItems.ScrollIntoView(itemVm);

                _selectedItem = itemVm;
                _selectedItemNormalizedName = itemVm.ItemNormalizedName;
                UpdateDetailPanel();

                LstItems.Focus();
            }
            finally
            {
                _isInitializing = false;
            }
        }

        private void ResetFiltersForNavigationInternal()
        {
            TxtSearch.Text = "";
            CmbFulfillment.SelectedIndex = 0;
            ChkFirOnly.IsChecked = false;
            ChkHideFulfilled.IsChecked = false;
            CmbSort.SelectedIndex = 0;
        }

        private CollectorItemViewModel? _selectedItem;
        private string? _selectedItemNormalizedName;

        private void LstItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;

            _selectedItem = LstItems.SelectedItem as CollectorItemViewModel;
            _selectedItemNormalizedName = _selectedItem?.ItemNormalizedName;
            UpdateDetailPanel();
        }

        /// <summary>
        /// Paints the detail pane from the current selection, or shows the empty state when there
        /// is none. The one writer of the pane: a reload, an inventory edit and a navigation all
        /// come through here, so the pane cannot be written two different ways.
        /// <para>
        /// The selection is re-resolved by name against the full item list when the remembered
        /// instance is gone, which covers the item the filters currently exclude (no row to be
        /// selected, still the selection).
        /// </para>
        /// </summary>
        private void UpdateDetailPanel()
        {
            _selectedItem ??= FindSelectedRow(_selectedItemNormalizedName, _allItemViewModels);

            if (_selectedItem == null)
            {
                TxtSelectItem.Visibility = Visibility.Visible;
                DetailPanel.Visibility = Visibility.Collapsed;
                return;
            }

            TxtSelectItem.Visibility = Visibility.Collapsed;
            DetailPanel.Visibility = Visibility.Visible;

            TxtDetailName.Text = _selectedItem.DisplayName;
            TxtDetailSubtitle.Text = _selectedItem.SubtitleName;
            TxtDetailSubtitle.Visibility = _selectedItem.SubtitleVisibility;
            ImgDetailIcon.Source = _selectedItem.IconSource;

            TxtDetailQuestCount.Text = _selectedItem.QuestDisplay;
            TxtDetailTotalCount.Text = _selectedItem.TotalDisplay;

            BtnWiki.IsEnabled = !string.IsNullOrEmpty(_selectedItem.WikiLink);

            UpdateDetailInventoryDisplay();

            var questSources = GetQuestSources(_selectedItem.ItemNormalizedName);
            QuestRequirementsList.ItemsSource = questSources;
            QuestSection.Visibility = questSources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// The quests in scope that ask for <paramref name="itemNormalizedName"/>, within the
        /// scope the listed items were aggregated under - the same pass, the same option, the same
        /// quest set - so the detail panel names exactly the quests whose items the list shows.
        /// Empty before the first load. Nothing here is read live: a fresh pass, or the checkbox
        /// as it stands now, could name quests the list was not built from.
        /// </summary>
        private List<CollectorQuestItemSourceViewModel> GetQuestSources(string itemNormalizedName)
        {
            var sources = new List<CollectorQuestItemSourceViewModel>();
            if (_listScope is not { } scope) return sources;

            foreach (var (task, questItem) in ItemsInScope(scope))
            {
                if (!string.Equals(questItem.ItemNormalizedName, itemNormalizedName, StringComparison.OrdinalIgnoreCase))
                    continue;

                sources.Add(new CollectorQuestItemSourceViewModel
                {
                    QuestName = _loc.GetQuestName(task),
                    TraderName = task.Trader,
                    Amount = questItem.Amount,
                    FoundInRaid = questItem.FoundInRaid,
                    IsKappaRequired = task.ReqKappa,
                    Task = task,
                    QuestNormalizedName = task.NormalizedName ?? string.Empty
                });
            }

            return sources;
        }

        #region Quest scope and the unlock panel

        /// <summary>
        /// The loaded Collector quest, or null when the data has none. Which row that is comes
        /// from <see cref="QuestGraphService.IsCollectorQuest"/>, the one place the quest's
        /// identity is spelled, so this page and the quest tab cannot come to differ on it.
        /// </summary>
        private TarkovTask? FindCollector()
            => _questProgressService.AllTasks.FirstOrDefault(QuestGraphService.IsCollectorQuest);

        /// <summary>
        /// The whole input of one render of the item list, captured together: the pass, the scope
        /// option as the checkbox stands at this load, and the quest set those two produce. The
        /// ONE place the checkbox is read, so no later consumer can answer the same question from
        /// a control the list has moved on from. The walk itself is
        /// <see cref="CollectorScope.QuestsInScope"/>, asked within this pass: the statuses it
        /// judges the quests by are the pass's, never a live reading.
        /// </summary>
        private ListScope CaptureListScope(RenderPass pass)
        {
            var includePrerequisites = ChkIncludePreQuest.IsChecked == true;
            var quests = CollectorScope.QuestsInScope(
                FindCollector(),
                task => pass.StatusOf(task).Status,
                _questGraphService.GetAllPrerequisites,
                includePrerequisites);
            return new ListScope(pass, includePrerequisites, quests);
        }

        /// <summary>
        /// Every (quest, required item) pair the given scope covers, out of the loaded quests: the
        /// one decision site for whose items count, shared by the item aggregation and the detail
        /// panel's quest sources, which used to carry a copy of its exclusions each. The rule is
        /// <see cref="CollectorScope.ItemsInScope"/>; what this adds is the page's quest list.
        /// </summary>
        private IEnumerable<(TarkovTask Task, QuestItem Item)> ItemsInScope(ListScope scope)
        {
            return CollectorScope.ItemsInScope(_questProgressService.AllTasks, scope.Quests);
        }

        /// <summary>
        /// Writes the unlock panel from one pass: Collector's badge, its condition lines and the
        /// Kappa count, all composed by <see cref="CollectorUnlockViewModel.BuildFor"/> from the
        /// engine's answer for that pass. Collapsed when the loaded data has no Collector quest.
        /// Runs from every load of the page (a progress change, a language switch, a data
        /// refresh, coming back to the tab) and from the coalesced settings refresh.
        /// </summary>
        private void RebuildUnlockPanel(RenderPass pass)
        {
            // The one place "no Collector quest in the data" is handled: the panel has nothing to
            // say and is collapsed. Everything below, the view model included, is written for a
            // quest that exists.
            var collector = FindCollector();
            if (collector == null)
            {
                UnlockPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var (status, gate) = pass.StatusOf(collector);
            var kappa = pass.KappaProgress();

            var panel = CollectorUnlockViewModel.BuildFor(
                collector, status, gate, pass.Settings, _loc, _loc.GetTraderDisplayName,
                kappa is { } k ? (k.Completed, k.Total) : null,
                RequirementLineBrushes.FromResources(this));

            TxtUnlockHeading.Text = _loc.CollectorUnlockHeading;
            TxtCollectorStatus.Text = panel.StatusText;
            CollectorStatusBadge.Background = QuestStatusBrushes.For(panel.Status);
            CollectorRequirementsList.ItemsSource = panel.Lines;
            TxtKappaCount.Text = panel.CountText;
            BtnCollectorKappaQuests.Content = _loc.ShowKappaQuests;

            // No reading, nothing offered: the count is blank and the button that would open an
            // empty list is not shown (the same "no number until there is one" the gauge paints).
            var kappaVisibility = kappa == null ? Visibility.Collapsed : Visibility.Visible;
            TxtKappaCount.Visibility = kappaVisibility;
            BtnCollectorKappaQuests.Visibility = kappaVisibility;

            UnlockPanel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Opens the same Kappa quest list the detail pane on the quest tab opens, against a pass
        /// captured at the click, and the window counts its header from those same rows.
        /// </summary>
        private void BtnCollectorKappaQuests_Click(object sender, RoutedEventArgs e)
        {
            var pass = RenderPass.Capture(_questProgressService, _questGraphService);
            if (pass.KappaQuests() is not { } quests) return;

            KappaQuestListWindow.Show(Window.GetWindow(this), quests, _loc);
        }

        #endregion

        private void QuestName_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is CollectorQuestItemSourceViewModel vm)
            {
                if (string.IsNullOrEmpty(vm.QuestNormalizedName)) return;

                var mainWindow = Window.GetWindow(this) as MainWindow;
                mainWindow?.NavigateToQuest(vm.QuestNormalizedName);
            }
        }

        private void BtnWiki_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedItem == null || string.IsNullOrEmpty(_selectedItem.WikiLink))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = _selectedItem.WikiLink,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private void BtnQuestWiki_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is CollectorQuestItemSourceViewModel vm && vm.Task != null)
            {
                var wikiPageName = NormalizedNameGenerator.GetWikiPageName(vm.Task.Name);
                var wikiUrl = $"https://escapefromtarkov.fandom.com/wiki/{Uri.EscapeDataString(wikiPageName.Replace(" ", "_"))}";

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = wikiUrl,
                        UseShellExecute = true
                    });
                }
                catch
                {
                }
            }
        }

        #region Inventory Quantity Controls

        // One control per kind per delta, each an expression-bodied one-liner naming the half it
        // edits, so the kind is data at the boundary (see FirKind) and never a flag a caller can
        // get wrong. Everything below them is written once instead of once per kind.
        private void BtnFirMinus1_Click(object sender, RoutedEventArgs e) =>
            AdjustRowQuantity(sender, FirKind.Fir, -1);

        private void BtnFirPlus1_Click(object sender, RoutedEventArgs e) =>
            AdjustRowQuantity(sender, FirKind.Fir, 1);

        private void BtnNonFirMinus1_Click(object sender, RoutedEventArgs e) =>
            AdjustRowQuantity(sender, FirKind.NonFir, -1);

        private void BtnNonFirPlus1_Click(object sender, RoutedEventArgs e) =>
            AdjustRowQuantity(sender, FirKind.NonFir, 1);

        /// <summary>
        /// Nudges one half of the quantity on the row whose spinner was clicked, then reads the
        /// service back instead of computing the new number here: the service clamps at zero, so
        /// it is the only one that knows what was actually stored.
        /// </summary>
        private void AdjustRowQuantity(object sender, FirKind kind, int delta)
        {
            if (sender is Button btn && btn.DataContext is CollectorItemViewModel vm)
            {
                _inventoryService.AdjustQuantity(vm.ItemNormalizedName, kind, delta);
                vm.SetOwned(kind, _inventoryService.GetQuantity(vm.ItemNormalizedName, kind));
            }
        }

        private void BtnDetailFirMinus5_Click(object sender, RoutedEventArgs e) =>
            AdjustDetailQuantity(FirKind.Fir, -5);

        private void BtnDetailFirMinus1_Click(object sender, RoutedEventArgs e) =>
            AdjustDetailQuantity(FirKind.Fir, -1);

        private void BtnDetailFirPlus1_Click(object sender, RoutedEventArgs e) =>
            AdjustDetailQuantity(FirKind.Fir, 1);

        private void BtnDetailFirPlus5_Click(object sender, RoutedEventArgs e) =>
            AdjustDetailQuantity(FirKind.Fir, 5);

        private void BtnDetailNonFirMinus5_Click(object sender, RoutedEventArgs e) =>
            AdjustDetailQuantity(FirKind.NonFir, -5);

        private void BtnDetailNonFirMinus1_Click(object sender, RoutedEventArgs e) =>
            AdjustDetailQuantity(FirKind.NonFir, -1);

        private void BtnDetailNonFirPlus1_Click(object sender, RoutedEventArgs e) =>
            AdjustDetailQuantity(FirKind.NonFir, 1);

        private void BtnDetailNonFirPlus5_Click(object sender, RoutedEventArgs e) =>
            AdjustDetailQuantity(FirKind.NonFir, 5);

        /// <summary>The same nudge from the detail pane, which edits the selected row.</summary>
        private void AdjustDetailQuantity(FirKind kind, int delta)
        {
            if (_selectedItem == null) return;
            _inventoryService.AdjustQuantity(_selectedItem.ItemNormalizedName, kind, delta);
            _selectedItem.SetOwned(kind, _inventoryService.GetQuantity(_selectedItem.ItemNormalizedName, kind));
            UpdateDetailInventoryDisplay();
        }

        private void UpdateDetailInventoryDisplay()
        {
            if (_selectedItem == null) return;

            TxtDetailOwnedFir.Text = _selectedItem.OwnedFirQuantity.ToString();
            TxtDetailOwnedNonFir.Text = _selectedItem.OwnedNonFirQuantity.ToString();

            var (statusText, statusBrush) = FulfillmentDisplay(_selectedItem.FulfillmentStatus);
            TxtDetailFulfillmentStatus.Text = statusText;
            TxtDetailFulfillmentStatus.Foreground = statusBrush;

            DetailProgressBar.Value = _selectedItem.ProgressPercent;
        }

        /// <summary>
        /// The detail pane's fulfillment label and the colour it is written in, from ONE switch
        /// over the status. The label and the colour used to be two switches over the same value,
        /// in two copies each (the pane had a second writer), which is how a fourth status would
        /// have been added to three of the four places. The three brush keys are App.xaml-level.
        /// </summary>
        private (string Text, Brush Brush) FulfillmentDisplay(ItemFulfillmentStatus status) => status switch
        {
            ItemFulfillmentStatus.Fulfilled => ("Fulfilled", (Brush)FindResource("SuccessBrush")),
            ItemFulfillmentStatus.PartiallyFulfilled => ("In Progress", (Brush)FindResource("WarningBrush")),
            _ => ("Not Started", (Brush)FindResource("TextSecondaryBrush"))
        };

        private void TxtDetailOwned_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !int.TryParse(e.Text, out _);
        }

        // Both quantity boxes raise these two, as they already shared
        // TxtDetailOwned_PreviewTextInput: which half is being edited follows from which box
        // raised the event, so there is one pair of handlers rather than one pair per kind.
        private void TxtDetailOwned_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyQuantityFromSender(sender);
        }

        private void TxtDetailOwned_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyQuantityFromSender(sender);
                Keyboard.ClearFocus();
            }
        }

        /// <summary>
        /// Applies the edit from whichever of the two quantity boxes raised the event. A sender
        /// that is neither is not one of ours and is left alone.
        /// </summary>
        private void ApplyQuantityFromSender(object sender)
        {
            if (ReferenceEquals(sender, TxtDetailOwnedFir))
            {
                ApplyQuantityFromTextBox(TxtDetailOwnedFir, FirKind.Fir);
            }
            else if (ReferenceEquals(sender, TxtDetailOwnedNonFir))
            {
                ApplyQuantityFromTextBox(TxtDetailOwnedNonFir, FirKind.NonFir);
            }
        }

        /// <summary>
        /// Reads one half of the quantity out of its box and stores it, clamped at zero. Text
        /// that is not a number is not an edit: the box is put back to the quantity the row
        /// actually holds rather than the store being written with a guess.
        /// </summary>
        private void ApplyQuantityFromTextBox(TextBox box, FirKind kind)
        {
            if (_selectedItem == null) return;

            if (int.TryParse(box.Text, out var quantity))
            {
                quantity = Math.Max(0, quantity);
                _inventoryService.SetQuantity(_selectedItem.ItemNormalizedName, kind, quantity);
                _selectedItem.SetOwned(kind, quantity);
                UpdateDetailInventoryDisplay();
            }
            else
            {
                box.Text = _selectedItem.Owned(kind).ToString();
            }
        }

        #endregion
    }
}
