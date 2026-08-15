using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;
using TarkovHelper.Windows;
using System.Linq;

namespace TarkovHelper.Pages
{
    public partial class QuestListPage : UserControl
    {
        private readonly LocalizationService _loc = LocalizationService.Instance;
        private readonly QuestProgressService _progressService = QuestProgressService.Instance;
        private readonly ImageCacheService _imageCache = ImageCacheService.Instance;
        private readonly ItemInventoryService _inventoryService = ItemInventoryService.Instance;
        private List<QuestViewModel> _allQuestViewModels = new();
        // Forward progression (unlock) rank per quest NormalizedName; computed once per data load.
        // Language- and progress-independent, so it is not recomputed on language change.
        private Dictionary<string, int> _unlockRank = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _traders = new();
        private List<string> _maps = new();
        private Dictionary<string, TarkovItem>? _itemLookup;
        private bool _isInitializing = true;
        private bool _isDataLoaded = false;
        // The status filter's single source of truth — the chips are the only status
        // UI and carry no state of their own (StatusChip_Click sets this, ApplyFilters
        // reads it). "Active" fresh-install default; RestoreFilterSettings overwrites
        // it from the persisted questList.statusTag on Loaded.
        private string _statusTag = QuestListSettings.DefaultStatusTag;
        private bool _isUnloaded = false;
        private string? _pendingQuestSelection = null;
        private List<GuideImage>? _pendingGuideImages = null;
        private bool _guideImagesLoaded = false;
        private TarkovTask? _currentDetailTask = null;
        // Debounces per-keystroke search filtering (see TxtSearch_TextChanged); any
        // explicit ApplyFilters cancels a pending tick.
        private DispatcherTimer? _searchDebounceTimer;
        private static readonly TimeSpan SearchDebounceInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>
        /// Collapses the profile-scoped settings burst into one refresh. SettingsService raises all
        /// seven of its changed events on every published reload (profile switch, profile reset,
        /// self-heal), all seven of which this page consumes, and each one used to run a full
        /// <see cref="RefreshAllForStateChange"/> pass over every task. Built by
        /// <see cref="RefreshCoalescer.OnDispatcher"/> in the constructor BODY, not here:
        /// <see cref="DispatcherObject.Dispatcher"/> is only set once the base constructor has run,
        /// which is after field initializers.
        /// </summary>
        private readonly RefreshCoalescer _settingsRefresh;

        /// <summary>
        /// The quest list column's MinWidth, mirroring QuestListPage.xaml's first
        /// ColumnDefinition — the space the detail panel must leave for the list.
        /// </summary>
        private const double QuestListMinWidth = 300;

        /// <summary>The Tag of CmbTrader's "All Traders" entry (QuestListPage.xaml).</summary>
        private const string AllTradersTag = "";
        /// <summary>The Tag of CmbMap's "All Maps" entry (QuestListPage.xaml).</summary>
        private const string AllMapsTag = "";

        /// <summary>
        /// One status chip as UpdateStatusChips needs it: the Button, the status tag it
        /// filters to (read from the Button's own XAML Tag — see <see cref="ChipEntry"/>),
        /// and the three brushes the paint pass uses, all derived from the chip's XAML
        /// Foreground color and frozen once. Every visual comes from this snapshot so no
        /// single property can be sourced differently from its siblings.
        /// </summary>
        private readonly record struct StatusChipEntry(
            Button Chip,
            string Tag,
            Brush SelectedFill,
            Brush SelectedBorder,
            Brush UnselectedBorder);

        /// <summary>
        /// The status chips on the statistics bar in display order — pinned equal to
        /// QuestStatusTags.ChipTags by <see cref="BuildStatusChips"/>, not merely
        /// "mirroring" it. Built once — the chips are page fields, so the array never
        /// needs rebuilding, and the derived brushes are frozen once.
        /// </summary>
        private StatusChipEntry[]? _statusChips;

        private StatusChipEntry[] StatusChips => _statusChips ??= BuildStatusChips();

        /// <summary>
        /// Builds the chip table and enforces the invariant the rest of the status
        /// filter rests on: the chips ARE QuestStatusTags.ChipTags, same tags in the
        /// same order. A tag in ChipTags with no chip would be accepted from
        /// questList.statusTag by <see cref="QuestStatusTags.Coerce"/> and then filter
        /// the list with no chip rendering selected; a chip whose tag is missing from
        /// ChipTags would work in-session but be widened back to "All" on every
        /// relaunch; a duplicated tag would double-count in
        /// <see cref="QuestListFilter.CountByStatusTag"/>. Throwing here turns all three
        /// into a deterministic first-paint failure any run catches, instead of three
        /// silent misbehaviours a reader has to notice.
        /// </summary>
        private StatusChipEntry[] BuildStatusChips()
        {
            var entries = new[]
            {
                ChipEntry(ChipAll),
                ChipEntry(ChipActive),
                ChipEntry(ChipLocked),
                ChipEntry(ChipDone),
                ChipEntry(ChipFailed),
                ChipEntry(ChipUnavailable),
            };

            if (!entries.Select(e => e.Tag).SequenceEqual(QuestStatusTags.ChipTags, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "Status chip Tags must equal QuestStatusTags.ChipTags exactly (same tags, same order). " +
                    $"Chips: [{string.Join(", ", entries.Select(e => e.Tag))}]; " +
                    $"ChipTags: [{string.Join(", ", QuestStatusTags.ChipTags)}].");
            }

            return entries;
        }

        /// <summary>
        /// Builds one <see cref="StatusChips"/> entry. The status tag is READ FROM the
        /// Button's XAML Tag rather than passed in, so the tag has exactly one
        /// declaration: StatusChip_Click already routes by chip.Tag, and a second C#
        /// copy could disagree with it (a chip that filters to one status while a
        /// different chip paints as selected). It doubles as the chip's rendered label,
        /// so that cannot drift from the tag either. The three brushes are derived from the
        /// chip's Foreground color — full-strength selected border, 0x33-alpha selected
        /// fill, 0x66-alpha unselected border — and frozen for reuse, because this runs
        /// once but UpdateStatusChips repaints on every ApplyFilters.
        ///
        /// Runs lazily at the first UpdateStatusChips (first ApplyFilters), not in the
        /// constructor. A Foreground that is not a SolidColorBrush (a gradient or image
        /// brush — every chip declares a solid color today, one of them via the
        /// TextPrimaryBrush resource) would break the derivation, so it asserts in Debug
        /// and falls back to white: a color no chip uses, unlike a gray fallback that
        /// would silently impersonate the Unavailable chip.
        /// </summary>
        private static StatusChipEntry ChipEntry(Button chip)
        {
            var tag = chip.Tag as string
                ?? throw new InvalidOperationException(
                    $"Status chip '{chip.Name}' must declare a string Tag in QuestListPage.xaml.");

            // Fully qualified: the project has its own TarkovHelper.Debug namespace,
            // which shadows the `Debug` in the file's `using System.Diagnostics;`.
            System.Diagnostics.Debug.Assert(chip.Foreground is SolidColorBrush,
                $"Status chip '{chip.Name}' must declare a SolidColorBrush Foreground; " +
                "the selected fill and both borders are derived from its color.");
            var color = (chip.Foreground as SolidColorBrush)?.Color ?? Colors.White;

            return new StatusChipEntry(chip, tag,
                SelectedFill: FrozenBrush(Color.FromArgb(0x33, color.R, color.G, color.B)),
                SelectedBorder: FrozenBrush(color),
                UnselectedBorder: FrozenBrush(Color.FromArgb(0x66, color.R, color.G, color.B)));
        }

        private static Brush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // Status brushes
        private static readonly Brush LockedBrush = new SolidColorBrush(Color.FromRgb(102, 102, 102));
        private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80));
        private static readonly Brush DoneBrush = new SolidColorBrush(Color.FromRgb(33, 150, 243));
        private static readonly Brush FailedBrush = new SolidColorBrush(Color.FromRgb(244, 67, 54));
        private static readonly Brush LevelLockedBrush = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange for Level Locked
        private static readonly Brush UnavailableBrush = new SolidColorBrush(Color.FromRgb(158, 158, 158)); // Gray for Unavailable

        public QuestListPage()
        {
            _settingsRefresh = RefreshCoalescer.OnDispatcher(this, RefreshForSettingsChange);

            InitializeComponent();
            SubscribeServiceEvents();

            Loaded += QuestListPage_Loaded;
            Unloaded += QuestListPage_Unloaded;
            SizeChanged += QuestListPage_SizeChanged;
        }

        /// <summary>
        /// The service events this page consumes. The constructor, Unloaded, and the
        /// Loaded re-subscribe all go through this pair, so an event added to one list
        /// cannot be forgotten in another (a classic WPF leak / double-subscription).
        /// </summary>
        private void SubscribeServiceEvents()
        {
            _loc.LanguageChanged += OnLanguageChanged;
            _progressService.ProgressChanged += OnProgressChanged;
            SettingsService.Instance.HasEodEditionChanged += OnProfileSettingChanged;
            SettingsService.Instance.HasUnheardEditionChanged += OnProfileSettingChanged;
            SettingsService.Instance.PrestigeLevelChanged += OnPrestigeLevelChanged;
            SettingsService.Instance.DspDecodeCountChanged += OnDspDecodeCountChanged;
            SettingsService.Instance.PlayerLevelChanged += OnPlayerLevelChanged;
            SettingsService.Instance.ScavRepChanged += OnScavRepChanged;
            SettingsService.Instance.PlayerFactionChanged += OnPlayerFactionChanged;
            QuestDbService.Instance.DataRefreshed += OnDatabaseRefreshed;
        }

        /// <summary>Mirror of <see cref="SubscribeServiceEvents"/> — keep the lists in sync.</summary>
        private void UnsubscribeServiceEvents()
        {
            _loc.LanguageChanged -= OnLanguageChanged;
            _progressService.ProgressChanged -= OnProgressChanged;
            SettingsService.Instance.HasEodEditionChanged -= OnProfileSettingChanged;
            SettingsService.Instance.HasUnheardEditionChanged -= OnProfileSettingChanged;
            SettingsService.Instance.PrestigeLevelChanged -= OnPrestigeLevelChanged;
            SettingsService.Instance.DspDecodeCountChanged -= OnDspDecodeCountChanged;
            SettingsService.Instance.PlayerLevelChanged -= OnPlayerLevelChanged;
            SettingsService.Instance.ScavRepChanged -= OnScavRepChanged;
            SettingsService.Instance.PlayerFactionChanged -= OnPlayerFactionChanged;
            QuestDbService.Instance.DataRefreshed -= OnDatabaseRefreshed;
        }

        private void QuestListPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            // FLUSH the pending debounced search rather than cancelling it. The page
            // keeps its control state across a tab switch but Loaded early-returns once
            // _isDataLoaded, so a discarded tick would leave the list filtered by the
            // PREVIOUS search text while TxtSearch shows the new one — permanently, and
            // with SelectQuestInternal's visibility probe reading that stale list.
            FlushPendingSearch();
            // Unsubscribe from events to prevent memory leaks
            UnsubscribeServiceEvents();
        }

        /// <summary>
        /// Applies a debounced search that has not ticked yet, so the list always agrees
        /// with the search box. No-op when nothing is pending.
        /// </summary>
        private void FlushPendingSearch()
        {
            if (_searchDebounceTimer?.IsEnabled == true)
            {
                ApplyFilters(); // stops the timer itself
            }
        }

        private async void QuestListPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Re-subscribe events if page was previously unloaded
            if (_isUnloaded)
            {
                _isUnloaded = false;
                SubscribeServiceEvents();

                // Every service event raised while the page sat unsubscribed was missed
                // — including the log-sync quest completions that fire during a raid, the
                // ones most likely to happen while the user is on another tab. Nothing
                // else re-syncs on the way back (MainWindow only re-assigns Content), so
                // without this the list, the chip counts and the persisted snapshot would
                // all be derived from stale statuses until some later event happened to
                // fire.
                if (_isDataLoaded) RefreshAllForStateChange();
            }

            // Skip if already loaded (prevents re-initialization on tab switching)
            if (_isDataLoaded) return;

            await LoadItemDataAsync();
            if (_isUnloaded) return; // Check if page was unloaded during async operation

            LoadQuests();
            PopulateTraderFilter();
            PopulateMapFilter();
            LoadFactionSelection();
            // First QuestListSettings access happens here (never in a constructor):
            // by Loaded the databases are initialized, so the settings actually load
            // instead of silently falling back to defaults.
            RestoreFilterSettings();
            RestoreDetailPanelWidth();
            _isInitializing = false;
            _isDataLoaded = true;
            ApplyFilters();
            UpdateRecommendations();

            // Process pending selection if any
            if (!string.IsNullOrEmpty(_pendingQuestSelection))
            {
                var pendingName = _pendingQuestSelection;
                _pendingQuestSelection = null;
                SelectQuestInternal(pendingName);
            }
        }

        private async Task LoadItemDataAsync()
        {
            // Load items data from DB for localized names and icons
            var itemDbService = ItemDbService.Instance;
            if (!itemDbService.IsLoaded)
            {
                await itemDbService.LoadItemsAsync();
            }
            _itemLookup = itemDbService.GetItemLookup();
        }

        private void OnLanguageChanged(object? sender, AppLanguage e)
        {
            RefreshQuestDisplayNames();
            ApplyFilters();
            UpdateDetailPanel();
        }

        /// <summary>
        /// The standard refresh sequence for a profile/progress state change, shared by
        /// every state-change handler — and by the public <see cref="RefreshDisplay"/>
        /// entry point MainWindow uses — so the sequence cannot drift between them.
        /// (OnDatabaseRefreshed is deliberately separate: it reloads data first.)
        /// </summary>
        private void RefreshAllForStateChange()
        {
            RefreshQuestStatuses();
            ApplyFilters();
            UpdateDetailPanel();
            UpdateRecommendations();
        }

        private void OnProgressChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(RefreshAllForStateChange);
        }

        private async void OnDatabaseRefreshed(object? sender, EventArgs e)
        {
            // DB 업데이트 후 데이터 다시 로드 (the lambda is fully synchronous — it was
            // needlessly async, which raised CS1998)
            await Dispatcher.InvokeAsync(() =>
            {
                // Item lookup 새로고침
                _itemLookup = ItemDbService.Instance.GetItemLookup();

                // Quest 데이터 다시 로드
                LoadQuests();
                PopulateTraderFilter();
                PopulateMapFilter();
                ApplyFilters();
                UpdateDetailPanel();
                UpdateRecommendations();
            });
        }

        // The seven profile-scoped settings events this page consumes all need the same refresh, so
        // they route through one coalesced request and differ only in delegate signature. Faction
        // additionally mirrors the selection into the radio buttons, which is cheap and has to land
        // before the refresh reads it, so that part stays inline.
        //
        // Player level and Scav Rep are subscribed HERE rather than pushed in by MainWindow's
        // drawer handlers: MainWindow's push ran a full refresh per event, outside this coalescer,
        // so a published reload rebuilt the whole list seven times over. The drawer still owns its
        // own controls; the list refresh is this page's business, exactly like the other five.

        private void OnProfileSettingChanged(object? sender, bool e) => _settingsRefresh.Request();

        private void OnPrestigeLevelChanged(object? sender, int e) => _settingsRefresh.Request();

        private void OnDspDecodeCountChanged(object? sender, int e) => _settingsRefresh.Request();

        private void OnPlayerLevelChanged(object? sender, int e) => _settingsRefresh.Request();

        private void OnScavRepChanged(object? sender, double e) => _settingsRefresh.Request();

        /// <summary>
        /// The refresh a profile-scoped settings change needs. Runs on the dispatcher, once per
        /// burst, scheduled by <see cref="_settingsRefresh"/>.
        /// </summary>
        private void RefreshForSettingsChange()
        {
            // The refresh is scheduled rather than inline, so it can land after Unloaded dropped the
            // subscriptions. Loaded re-runs the same sequence on the way back in, so skipping here
            // loses nothing.
            if (_isUnloaded) return;

            RefreshAllForStateChange();
        }

        private void OnPlayerFactionChanged(object? sender, string? e)
        {
            Dispatcher.Invoke(() =>
            {
                // Update radio button selection to match the new faction with the filter
                // handlers suppressed. The scope RESTORES the previous _isInitializing
                // rather than forcing false: this event can fire before Loaded finishes
                // (a game-mode switch during the Loaded await), and forcing false there
                // would arm every filter handler mid-initialization.
                using (SuppressFilterHandlers())
                {
                    if (e == "bear")
                    {
                        RbBear.IsChecked = true;
                        RbUsec.IsChecked = false;
                    }
                    else if (e == "usec")
                    {
                        RbUsec.IsChecked = true;
                        RbBear.IsChecked = false;
                    }
                    else
                    {
                        RbBear.IsChecked = false;
                        RbUsec.IsChecked = false;
                    }
                }
            });

            // Coalesced with the rest of the burst. The radio mirror ran inline above, so the
            // refresh this request books reads the new selection; and if an earlier event in the
            // same burst already ran its refresh, this request books a fresh pass rather than
            // joining the finished one.
            _settingsRefresh.Request();
        }

        /// <summary>
        /// Refreshes the whole page for an externally-driven progress change: MainWindow calls this
        /// after applying a quest event from the game logs and after the in-progress quest input
        /// dialog. Profile-scoped SETTINGS changes do not come through here - the page subscribes to
        /// those seven events itself and coalesces them (see <see cref="_settingsRefresh"/>).
        /// Runs the SAME sequence as the internal state-change handlers
        /// (<see cref="RefreshAllForStateChange"/>) rather than a shorter copy of it:
        /// level and karma flip quests between LevelLocked and Active, and the
        /// recommendations panel lists Active quests, so a sequence that skipped
        /// UpdateRecommendations left that panel and its count badge stale after every
        /// level edit.
        /// </summary>
        public void RefreshDisplay() => RefreshAllForStateChange();

        /// <summary>
        /// Reload all quest data from QuestProgressService
        /// Call this after data has been refreshed from API
        /// </summary>
        public async Task ReloadDataAsync()
        {
            // Reload map and item data
            await LoadItemDataAsync();

            // Reload quests from the updated progress service
            LoadQuests();

            // Repopulate filters
            PopulateTraderFilter();
            PopulateMapFilter();

            // Refresh display names with current locale
            RefreshQuestDisplayNames();

            // Apply filters to update the list
            ApplyFilters();
        }

        /// <summary>
        /// Select a quest by its normalized name (for cross-tab navigation)
        /// </summary>
        public void SelectQuest(string questNormalizedName)
        {
            // If data is not loaded yet, save for later
            if (!_isDataLoaded)
            {
                _pendingQuestSelection = questNormalizedName;
                return;
            }

            SelectQuestInternal(questNormalizedName);
        }

        /// <summary>
        /// Internal method to select a quest (called when data is ready). Never touches
        /// the filters (see feature-preserve-quest-filters-on-navigation.md): when the
        /// current filters hide the target, only the detail panel switches and the
        /// hidden-by-filters notice offers the explicit reset via BtnShowInList.
        /// </summary>
        private void SelectQuestInternal(string questNormalizedName)
        {
            var questVm = FindQuestViewModel(questNormalizedName);
            if (questVm == null) return;

            // Re-establish the precondition the pre-refactor code created by always
            // applying filters itself: the visibility probe below needs ItemsSource to
            // be the filtered list, or every target would silently count as hidden.
            // A search typed within the debounce window must land first, or the probe
            // would judge visibility against the pre-keystroke list and select a row the
            // pending filter is about to hide.
            FlushPendingSearch();
            if (CurrentFilteredList == null) ApplyFilters();

            if (IsVisibleUnderCurrentFilters(questVm))
            {
                // Visible under the current filters: a plain selection. SelectionChanged
                // renders the detail panel; when the quest is already selected the event
                // will not fire, so render explicitly in that case.
                if (ReferenceEquals(LstQuests.SelectedItem, questVm))
                {
                    UpdateDetailPanel(questVm);
                }
                else
                {
                    LstQuests.SelectedItem = questVm;
                }
                ScrollQuestIntoView(questVm);
                return;
            }

            // Hidden by the current filters: switch only the detail panel, leaving the
            // filters and the list untouched. Clear the selection (with the handler
            // suppressed so the panel is not collapsed by the null selection) so the
            // highlighted row and the panel cannot disagree about the current quest.
            using (SuppressSelectionChanged())
            {
                LstQuests.SelectedItem = null;
            }
            UpdateDetailPanel(questVm);
        }

        /// <summary>
        /// Find the view model for a quest by NormalizedName, or null when unknown.
        /// </summary>
        private QuestViewModel? FindQuestViewModel(string? questNormalizedName)
        {
            if (string.IsNullOrEmpty(questNormalizedName)) return null;
            return _allQuestViewModels.FirstOrDefault(vm =>
                string.Equals(vm.Task.NormalizedName, questNormalizedName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// The list ApplyFilters last assigned to LstQuests.ItemsSource, or null before
        /// the first ApplyFilters — the single place the filtered list is read back
        /// from the control.
        /// </summary>
        private List<QuestViewModel>? CurrentFilteredList => LstQuests.ItemsSource as List<QuestViewModel>;

        /// <summary>
        /// Whether the quest is in the currently filtered list — the one authoritative
        /// visibility probe behind the selection, notice, and navigation decisions.
        /// </summary>
        private bool IsVisibleUnderCurrentFilters(QuestViewModel? vm)
            => vm != null && CurrentFilteredList?.Contains(vm) == true;

        /// <summary>
        /// Detaches LstQuests_SelectionChanged for the duration of a programmatic list
        /// mutation (ItemsSource swap, selection rewrite) and reattaches on dispose, so
        /// SelectionChanged keeps meaning "the user picked a row" and the reattach
        /// cannot be forgotten at a call site.
        /// </summary>
        private SelectionChangedSuppression SuppressSelectionChanged() => new(this);

        private readonly struct SelectionChangedSuppression : IDisposable
        {
            private readonly QuestListPage _page;

            public SelectionChangedSuppression(QuestListPage page)
            {
                _page = page;
                page.LstQuests.SelectionChanged -= page.LstQuests_SelectionChanged;
            }

            public void Dispose()
                => _page.LstQuests.SelectionChanged += _page.LstQuests_SelectionChanged;
        }

        /// <summary>
        /// Suppresses the filter-bar handlers (they all early-return on
        /// <c>_isInitializing</c>) for the duration of a programmatic filter mutation,
        /// restoring the PREVIOUS value on dispose rather than forcing false. Restoring
        /// is what makes the scope nest safely: a reset or a faction sync that runs while
        /// Loaded is still initializing must not arm the handlers early, which would let
        /// the not-yet-restored filter bar fire a cascade of ApplyFilters passes.
        /// </summary>
        private FilterHandlerSuppression SuppressFilterHandlers() => new(this);

        private readonly struct FilterHandlerSuppression : IDisposable
        {
            private readonly QuestListPage _page;
            private readonly bool _wasInitializing;

            public FilterHandlerSuppression(QuestListPage page)
            {
                _page = page;
                _wasInitializing = page._isInitializing;
                page._isInitializing = true;
            }

            public void Dispose() => _page._isInitializing = _wasInitializing;
        }

        /// <summary>
        /// Scrolls the list to the quest after the virtualizing panel has had a layout
        /// pass over the current ItemsSource. A synchronous ScrollIntoView right after
        /// an ItemsSource swap can silently fail to scroll because the item containers
        /// are not generated yet, so the scroll is deferred to DispatcherPriority.Loaded
        /// with an explicit UpdateLayout first (the guard the pre-refactor code used).
        /// </summary>
        private void ScrollQuestIntoView(QuestViewModel questVm)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LstQuests.UpdateLayout();
                LstQuests.ScrollIntoView(questVm);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Sets every filter to its most-permissive value — note status becomes "All",
        /// not the page's initial "Active" default (the faction toggle is a profile
        /// setting, not a filter, so it stays). Invoked from BtnShowInList and from the
        /// empty state's BtnResetFilters — navigation itself never resets filters.
        /// Trader/map selection goes through tags, never item indices, so reordering
        /// those ComboBoxItems cannot silently change what "reset" means.
        /// </summary>
        private void ResetFilters()
        {
            // Outside the suppression scope on purpose: that scope exists to stop the
            // CONTROL handlers from firing on the programmatic writes below, and a plain
            // field write raises nothing. Wrapping it would imply a protection it neither
            // needs nor gets.
            _statusTag = QuestStatusTags.All;

            using (SuppressFilterHandlers())
            {
                // Clear search text
                TxtSearch.Text = "";

                // Reset other filters
                ChkKappaOnly.IsChecked = false;
                ChkItemRequired.IsChecked = false;
                SelectComboByTag(CmbTrader, AllTradersTag, AllTradersTag);
                SelectComboByTag(CmbMap, AllMapsTag, AllMapsTag);
            }
        }

        private void LoadQuests()
        {
            var tasks = _progressService.AllTasks;

            _allQuestViewModels = tasks.Select(t => CreateQuestViewModel(t)).ToList();
            _traders = tasks.Select(t => t.Trader).Where(t => !string.IsNullOrEmpty(t)).Distinct().OrderBy(t => t).ToList();
            _maps = tasks.Where(t => t.Maps != null).SelectMany(t => t.Maps!).Distinct().OrderBy(m => m).ToList();
            BuildUnlockRank();
        }

        /// <summary>
        /// Compute the forward progression (unlock) rank for each quest once per data load.
        /// The list is then ordered by this rank in <see cref="ApplyFilters"/>.
        /// </summary>
        private void BuildUnlockRank()
        {
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var order = QuestGraphService.Instance.GetUnlockOrder();
                for (int i = 0; i < order.Count; i++)
                {
                    var key = order[i].NormalizedName;
                    if (!string.IsNullOrEmpty(key) && !rank.ContainsKey(key))
                        rank[key] = i;
                }
            }
            catch
            {
                // QuestGraphService not initialized yet — fall back to no ranking (stable input order).
            }
            _unlockRank = rank;
        }

        private QuestViewModel CreateQuestViewModel(TarkovTask task)
        {
            var status = _progressService.GetStatus(task);
            var (displayName, subtitle, showSubtitle) = GetLocalizedNames(task);

            return new QuestViewModel
            {
                Task = task,
                DisplayName = displayName,
                SubtitleName = subtitle,
                SubtitleVisibility = showSubtitle ? Visibility.Visible : Visibility.Collapsed,
                TraderInitial = GetTraderInitial(task.Trader),
                Status = status,
                StatusText = GetStatusText(status, task),
                StatusBackground = GetStatusBrush(status),
                CompleteButtonVisibility = (status == QuestStatus.Active || status == QuestStatus.Locked || status == QuestStatus.LevelLocked)
                    && status != QuestStatus.Unavailable ? Visibility.Visible : Visibility.Collapsed,
                IsKappaRequired = task.ReqKappa
            };
        }

        private (string DisplayName, string Subtitle, bool ShowSubtitle) GetLocalizedNames(TarkovTask task)
            => _loc.GetQuestDisplayName(task);

        private static string GetTraderInitial(string trader)
        {
            if (string.IsNullOrEmpty(trader)) return "?";
            return trader.Length >= 2 ? trader[..2].ToUpper() : trader.ToUpper();
        }

        private string GetStatusText(QuestStatus status, TarkovTask? task = null)
        {
            if (status == QuestStatus.LevelLocked && task != null)
            {
                // Check if it's level-locked or karma-locked
                if (task.RequiredLevel.HasValue && !_progressService.IsLevelRequirementMet(task))
                {
                    return $"Lv.{task.RequiredLevel}";
                }
                if (task.RequiredScavKarma.HasValue && !_progressService.IsScavKarmaRequirementMet(task))
                {
                    return $"Rep {task.RequiredScavKarma:0.#}";
                }
            }

            if (status == QuestStatus.Unavailable && task != null)
            {
                // Show specific reason for unavailability
                if (!_progressService.IsEditionRequirementMet(task))
                {
                    // Show which edition is required
                    var requiredEdition = task.RequiredEdition?.ToLowerInvariant();
                    if (requiredEdition == "eod" || requiredEdition == "edge_of_darkness")
                        return "EOD";
                    if (requiredEdition == "unheard" || requiredEdition == "the_unheard")
                        return "Unheard";
                    // Check for excluded edition
                    var excludedEdition = task.ExcludedEdition?.ToLowerInvariant();
                    if (!string.IsNullOrEmpty(excludedEdition))
                        return "Edition";
                }
                if (!_progressService.IsPrestigeLevelRequirementMet(task))
                {
                    return $"P.{task.RequiredPrestigeLevel}";
                }
                // Show faction if quest is for different faction
                if (!_progressService.IsFactionRequirementMet(task))
                {
                    var faction = task.Faction?.ToLowerInvariant();
                    if (faction == "bear")
                        return "BEAR";
                    if (faction == "usec")
                        return "USEC";
                }
            }

            return status switch
            {
                QuestStatus.Locked => "Locked",
                QuestStatus.Active => "Active",
                QuestStatus.Done => "Done",
                QuestStatus.Failed => "Failed",
                QuestStatus.LevelLocked => "Level",
                QuestStatus.Unavailable => "N/A",
                _ => "Unknown"
            };
        }

        private static Brush GetStatusBrush(QuestStatus status)
        {
            return status switch
            {
                QuestStatus.Locked => LockedBrush,
                QuestStatus.Active => ActiveBrush,
                QuestStatus.Done => DoneBrush,
                QuestStatus.Failed => FailedBrush,
                QuestStatus.LevelLocked => LevelLockedBrush,
                QuestStatus.Unavailable => UnavailableBrush,
                _ => Brushes.Gray
            };
        }

        private void RefreshQuestDisplayNames()
        {
            foreach (var vm in _allQuestViewModels)
            {
                var (displayName, subtitle, showSubtitle) = GetLocalizedNames(vm.Task);
                vm.DisplayName = displayName;
                vm.SubtitleName = subtitle;
                vm.SubtitleVisibility = showSubtitle ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void RefreshQuestStatuses()
        {
            foreach (var vm in _allQuestViewModels)
            {
                var status = _progressService.GetStatus(vm.Task);
                vm.Status = status;
                vm.StatusText = GetStatusText(status, vm.Task);
                vm.StatusBackground = GetStatusBrush(status);
                vm.CompleteButtonVisibility = (status == QuestStatus.Active || status == QuestStatus.Locked || status == QuestStatus.LevelLocked)
                    && status != QuestStatus.Unavailable ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>
        /// The Tag of the combo's current selection, or the empty string when nothing is
        /// selected — the same reading ApplyFilters takes, so a repopulation can restore
        /// exactly what the filter snapshot would have recorded.
        /// </summary>
        private static string SelectedTag(ComboBox combo)
            => (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

        private void PopulateTraderFilter()
        {
            // Removing the selected item resets the ComboBox to SelectedIndex -1 and
            // raises SelectionChanged, so a bare repopulation (DB refresh, ReloadDataAsync)
            // would both widen the live filter to "All Traders" AND — via the
            // SelectionChanged → ApplyFilters → persist chain — overwrite the stored
            // trader with "". Capture the tag, rebuild with the handlers suppressed, and
            // re-select it; a trader that genuinely disappeared falls back to "All".
            var selectedTrader = SelectedTag(CmbTrader);

            using (SuppressFilterHandlers())
            {
                // Clear existing items except "All Traders"
                while (CmbTrader.Items.Count > 1)
                {
                    CmbTrader.Items.RemoveAt(1);
                }

                foreach (var trader in _traders)
                {
                    CmbTrader.Items.Add(new ComboBoxItem { Content = trader, Tag = trader });
                }

                SelectComboByTag(CmbTrader, selectedTrader, AllTradersTag);
            }
        }

        private void PopulateMapFilter()
        {
            // Selection preserved across the rebuild — see PopulateTraderFilter.
            var selectedMap = SelectedTag(CmbMap);

            using (SuppressFilterHandlers())
            {
                // Clear existing items except "All Maps"
                while (CmbMap.Items.Count > 1)
                {
                    CmbMap.Items.RemoveAt(1);
                }

                foreach (var mapNormalized in _maps)
                {
                    // Get localized map name
                    var mapName = GetLocalizedMapName(mapNormalized);
                    CmbMap.Items.Add(new ComboBoxItem { Content = mapName, Tag = mapNormalized });
                }

                SelectComboByTag(CmbMap, selectedMap, AllMapsTag);
            }
        }

        private string GetLocalizedMapName(string normalizedName)
        {
            // Simple formatting: capitalize first letter of each word
            if (string.IsNullOrEmpty(normalizedName)) return normalizedName;

            return System.Globalization.CultureInfo.CurrentCulture.TextInfo
                .ToTitleCase(normalizedName.Replace("-", " "));
        }

        private void ApplyFilters()
        {
            // An explicit apply supersedes any pending debounced search apply.
            _searchDebounceTimer?.Stop();

            var criteria = new QuestFilterCriteria(
                SearchText: TxtSearch.Text ?? string.Empty,
                KappaOnly: ChkKappaOnly.IsChecked == true,
                ItemRequired: ChkItemRequired.IsChecked == true,
                Trader: SelectedTag(CmbTrader),
                Map: SelectedTag(CmbMap),
                StatusTag: _statusTag,
                Faction: RbBear.IsChecked == true ? "bear" : (RbUsec.IsChecked == true ? "usec" : null));

            var filtered = _allQuestViewModels
                .Where(vm => QuestListFilter.Matches(vm, criteria))
                // Order by forward progression (unlock) rank so prerequisites appear before the
                // quests they unlock. Rank is language- and progress-independent (see BuildUnlockRank).
                .OrderBy(vm => _unlockRank.TryGetValue(vm.Task.NormalizedName ?? string.Empty, out var r) ? r : int.MaxValue)
                .ToList();

            // Swap ItemsSource with SelectionChanged suppressed: the swap always clears
            // the ListBox selection, and routing that non-user event around the handler
            // keeps SelectionChanged meaning "the user picked a row".
            // ReconcileDetailSelection then restores the selection/notice state explicitly.
            using (SuppressSelectionChanged())
            {
                LstQuests.ItemsSource = filtered;
                ReconcileDetailSelection();
            }

            // Only the filters can be blamed when there ARE quests to filter. With no
            // quests loaded (a pre-Loaded refresh, a failed or empty data load) the list
            // is empty for a reason no reset can fix, and MainWindow owns that message.
            UpdateEmptyState(isEmpty: filtered.Count == 0 && _allQuestViewModels.Count > 0);

            // Persist the filter-bar snapshot (one transaction; unchanged values are
            // skipped). Gated on _isDataLoaded, NOT _isInitializing: service events (a
            // game-mode switch raising PlayerFactionChanged, a progress or DB refresh)
            // can trigger an ApplyFilters before Loaded has restored the saved state,
            // and that early pass must not overwrite the store with the XAML defaults.
            if (_isDataLoaded) SaveFilterSettings(criteria);

            // Update statistics — the per-status counts (including the All chip's
            // total) live on the clickable chips.
            UpdateStatusChips(criteria);

            // Update Kappa progress gauge
            UpdateKappaGauge();
        }

        /// <summary>
        /// Shows the zero-results empty state over the quest list (with the explicit
        /// reset button as the exit), or hides it. Texts are re-applied on every show
        /// so a language change is picked up by the next ApplyFilters.
        /// </summary>
        private void UpdateEmptyState(bool isEmpty)
        {
            if (isEmpty)
            {
                TxtEmptyStateTitle.Text = _loc.QuestListEmptyTitle;
                TxtEmptyStateHint.Text = _loc.QuestListEmptyHint;
                BtnResetFilters.Content = _loc.ResetFiltersButton;
            }
            PnlEmptyState.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The empty state's escape hatch — the same reset BtnShowInList uses.</summary>
        private void BtnResetFilters_Click(object sender, RoutedEventArgs e)
        {
            ResetFilters();
            ApplyFilters();
        }

        /// <summary>
        /// Restores the persisted filter-bar state (search text is deliberately
        /// transient and never restored). A persisted trader/map that no longer exists
        /// after a DB update falls back to that combo's "All" entry, and an unknown
        /// status tag (a value written by another build, a hand-edited row) falls back
        /// to "All" — the permissive end, never the narrower "Active" default. A missing
        /// or blank row is NOT "unknown": QuestListSettings.StatusTag answers the
        /// "Active" fresh-install default for it, which is what a fresh profile wants.
        /// Runs under _isInitializing from Loaded — the first ApplyFilters happens after.
        /// </summary>
        private void RestoreFilterSettings()
        {
            var settings = QuestListSettings.Instance;
            ChkKappaOnly.IsChecked = settings.KappaOnly;
            ChkItemRequired.IsChecked = settings.ItemRequired;
            SelectComboByTag(CmbTrader, settings.Trader, AllTradersTag);
            SelectComboByTag(CmbMap, settings.Map, AllMapsTag);
            // The status combo's tag lookup used to absorb unknown persisted tags; with
            // chips the validation is explicit and lives with the tag table (Coerce):
            // an unknown tag widens to "All" (the permissive end, e2e-pinned), never
            // the narrower "Active" default.
            _statusTag = QuestStatusTags.Coerce(settings.StatusTag);
        }

        /// <summary>
        /// Persists the filter-bar snapshot ApplyFilters just used, as one transactional
        /// write — these five values change together (a reset changes all of them), and
        /// five independent writes could leave a half-reset combination behind.
        /// </summary>
        private static void SaveFilterSettings(QuestFilterCriteria criteria)
            => QuestListSettings.Instance.SaveFilterSnapshot(
                criteria.KappaOnly, criteria.ItemRequired,
                criteria.Trader, criteria.Map, criteria.StatusTag);

        /// <summary>
        /// Selects the ComboBoxItem whose Tag equals <paramref name="tag"/>. When no item
        /// carries that tag (a persisted value from another build, a trader/map dropped by
        /// a database update) it falls back to <paramref name="fallbackTag"/> — passed
        /// explicitly rather than assuming index 0, so reordering a combo's items can
        /// never silently change what the fallback means. Used by the trader and map
        /// combos; the status filter is chips, validated via
        /// <see cref="QuestStatusTags.Coerce"/> in RestoreFilterSettings instead.
        /// </summary>
        private static void SelectComboByTag(ComboBox combo, string tag, string fallbackTag)
        {
            if (TrySelectComboByTag(combo, tag)) return;
            if (TrySelectComboByTag(combo, fallbackTag)) return;

            // Neither tag exists (an empty or renamed combo) — leave the first entry
            // selected rather than an unselected, blank-rendering combo.
            combo.SelectedIndex = 0;
        }

        private static bool TrySelectComboByTag(ComboBox combo, string tag)
        {
            foreach (var item in combo.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag?.ToString() ?? string.Empty, tag, StringComparison.Ordinal))
                {
                    combo.SelectedItem = item;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Renders the status chips for the current filter snapshot: per-tag counts via
        /// <see cref="QuestListFilter.CountByStatusTag"/> (what the list would show if
        /// that chip were clicked). Selection is cued by each chip's own color — the
        /// selected chip gets its translucent fill and full-color border, unselected
        /// chips stay transparent with a dimmed color border — every brush coming from
        /// the entry's frozen snapshot, so no one visual can drift from the others.
        ///
        /// Selection is also published to UI Automation as ItemStatus (the e2e surface:
        /// the chips hold the only status-filter state, so the tests read it from here).
        /// That publish is gated on <see cref="_isDataLoaded"/> — the same flag
        /// StatusChip_Click honors — so "this chip reports a status" means "a click on
        /// it would be acted on". A pre-restore ApplyFilters (a service event arriving
        /// during Loaded's await) repaints the chips but leaves ItemStatus empty, which
        /// is exactly what the harness waits out before clicking.
        /// </summary>
        private void UpdateStatusChips(QuestFilterCriteria criteria)
        {
            var chips = StatusChips;
            var counts = QuestListFilter.CountByStatusTag(
                _allQuestViewModels, criteria, QuestStatusTags.ChipTags);

            foreach (var entry in chips)
            {
                // The chip's visible label IS its status tag — hardcoded English per the
                // recorded localization Non-Goal, and so it inherits the tag's single
                // declaration instead of being a second, unvalidated copy of it.
                entry.Chip.Content = $"{entry.Tag} {counts[entry.Tag]}";
                var isSelected = string.Equals(criteria.StatusTag, entry.Tag, StringComparison.Ordinal);
                entry.Chip.Background = isSelected ? entry.SelectedFill : Brushes.Transparent;
                entry.Chip.BorderBrush = isSelected ? entry.SelectedBorder : entry.UnselectedBorder;
                entry.Chip.FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal;
                if (_isDataLoaded)
                {
                    AutomationProperties.SetItemStatus(entry.Chip,
                        isSelected ? QuestStatusTags.ChipSelected : QuestStatusTags.ChipUnselected);
                }
            }
        }

        /// <summary>
        /// Chip click = status filter: applies the chip's status, or returns to "All"
        /// when the chip is already the active filter (<see cref="QuestStatusTags.NextTag"/>
        /// holds that rule). Clicking All while All is selected therefore resolves to the
        /// current tag, and the unchanged-tag guard below makes it a true no-op — no
        /// refilter, no ItemsSource swap, no settings write. Sets <see cref="_statusTag"/>
        /// — the single source of status-filter truth — and applies the filters directly.
        /// </summary>
        private void StatusChip_Click(object sender, RoutedEventArgs e)
        {
            // Ignore clicks that land before Loaded has restored the saved filters:
            // RestoreFilterSettings would overwrite the clicked tag right after, so
            // honoring the click would only flash a filter state the restore then
            // discards. UpdateStatusChips publishes ItemStatus under the same flag, so
            // a chip that reports a status is a chip whose clicks are honored.
            if (!_isDataLoaded) return;
            if (sender is not Button chip) return;

            // Route through the chip table rather than reading chip.Tag directly: the
            // table's tags are pinned equal to QuestStatusTags.ChipTags (BuildStatusChips),
            // so an unknown or typo'd tag cannot reach _statusTag — and from there the
            // persisted questList.statusTag — and strand the user on an empty list.
            var entry = Array.Find(StatusChips, c => ReferenceEquals(c.Chip, chip));
            if (entry.Chip == null) return;

            var targetTag = QuestStatusTags.NextTag(_statusTag, entry.Tag);
            if (string.Equals(targetTag, _statusTag, StringComparison.Ordinal)) return;

            _statusTag = targetTag;
            ApplyFilters();
        }

        /// <summary>
        /// Applies the persisted detail-panel width (saved on splitter drag end) and
        /// publishes the settings clamp to the column itself, so the width the user can
        /// drag to is exactly the width that persists — without MaxWidth the splitter
        /// accepts 1400px, the setter silently stores 800, and the panel "forgets" the
        /// size at the next launch.
        /// </summary>
        private void RestoreDetailPanelWidth()
        {
            DetailColumn.MinWidth = QuestListSettings.MinDetailPanelWidth;
            DetailColumn.MaxWidth = QuestListSettings.MaxDetailPanelWidth;
            ApplyDetailPanelWidth(QuestListSettings.Instance.DetailPanelWidth);
        }

        /// <summary>
        /// Sets the detail column to the bounded, page-fitting width
        /// (<see cref="QuestListLayout.ClampDetailPanelWidth"/> holds the rule and its
        /// tests). The persisted value is never rewritten here, so a panel narrowed to
        /// fit a small window returns to its full width on a wide one.
        /// </summary>
        private void ApplyDetailPanelWidth(double width)
        {
            DetailColumn.Width = new GridLength(QuestListLayout.ClampDetailPanelWidth(
                requestedWidth: width,
                pageWidth: ActualWidth,
                listMinWidth: QuestListMinWidth,
                splitterWidth: DetailSplitter.Width,
                minWidth: QuestListSettings.MinDetailPanelWidth,
                maxWidth: QuestListSettings.MaxDetailPanelWidth));
        }

        /// <summary>
        /// Re-fits the detail panel when the window width changes (monitor change,
        /// un-maximize, restore), so a saved width can never clip the quest list out of
        /// view. Re-applies the PERSISTED width rather than the column's current one:
        /// feeding back the already-capped value would ratchet the panel narrower and
        /// never let it grow back when the window widens again.
        /// </summary>
        private void QuestListPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged || !_isDataLoaded) return;
            ApplyDetailPanelWidth(QuestListSettings.Instance.DetailPanelWidth);
        }

        private void DetailSplitter_DragCompleted(object sender,
            System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            QuestListSettings.Instance.DetailPanelWidth = DetailColumn.ActualWidth;
        }

        private void UpdateKappaGauge()
        {
            try
            {
                var graphService = QuestGraphService.Instance;
                var (completed, total, percentage) = graphService.GetCollectorProgress(
                    normalizedName => _progressService.IsQuestCompleted(normalizedName));

                TxtKappaGauge.Text = $"{completed}/{total}";
                KappaGaugeBar.Width = (percentage / 100.0) * 120; // 120 is the gauge width
            }
            catch
            {
                // QuestGraphService not initialized yet
                TxtKappaGauge.Text = "0/0";
                KappaGaugeBar.Width = 0;
            }
        }

        private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isInitializing) return;

            // Debounce: a synchronous refilter per keystroke re-runs the full LINQ
            // pass, the chip counts, and the kappa gauge for every character typed.
            if (_searchDebounceTimer == null)
            {
                _searchDebounceTimer = new DispatcherTimer { Interval = SearchDebounceInterval };
                _searchDebounceTimer.Tick += (_, _) => ApplyFilters(); // ApplyFilters stops the timer
            }
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Start();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (!_isInitializing) ApplyFilters();
        }

        private void CmbTrader_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitializing) ApplyFilters();
        }

        private void CmbMap_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitializing) ApplyFilters();
        }

        private void Faction_Changed(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            // Save faction selection (setter automatically saves and notifies listeners)
            var faction = RbBear.IsChecked == true ? "bear" : (RbUsec.IsChecked == true ? "usec" : null);
            SettingsService.Instance.PlayerFaction = faction;

            ApplyFilters();
        }

        private void LoadFactionSelection()
        {
            var savedFaction = SettingsService.Instance.PlayerFaction;
            if (savedFaction == "bear")
            {
                RbBear.IsChecked = true;
            }
            else if (savedFaction == "usec")
            {
                RbUsec.IsChecked = true;
            }
            // If null, neither is selected (default state)
        }

        private void LstQuests_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Programmatic mutations run with this handler suppressed (see
            // SuppressSelectionChanged), so a null selection here is the user explicitly
            // deselecting (Ctrl+Click on the selected row) — collapse the panel instead
            // of letting UpdateDetailPanel's fallback resurrect the deselected quest.
            if (LstQuests.SelectedItem == null)
            {
                ClearDetailPanel();
                return;
            }
            UpdateDetailPanel();
        }

        /// <summary>
        /// After the filtered list changes, re-point the list selection at the quest the
        /// detail panel is showing (when it is still visible) and keep the hidden-by-filters
        /// notice truthful (when it is not). A shown quest that no longer exists in the
        /// loaded data (e.g. dropped by a DB reload) collapses the panel instead of
        /// leaving it stale. Runs with LstQuests.SelectionChanged suppressed (see
        /// ApplyFilters), so restoring the selection does not re-render the panel.
        /// </summary>
        private void ReconcileDetailSelection()
        {
            if (_currentDetailTask == null)
            {
                UpdateFilteredOutNotice(null);
                return;
            }

            var detailVm = FindQuestViewModel(_currentDetailTask.NormalizedName);
            if (detailVm == null)
            {
                // The shown quest vanished from the loaded data — nothing to keep showing.
                ClearDetailPanel();
                return;
            }

            LstQuests.SelectedItem = IsVisibleUnderCurrentFilters(detailVm) ? detailVm : null;
            UpdateFilteredOutNotice(detailVm);
        }

        /// <summary>
        /// Shows the hidden-by-filters notice when the detail panel displays a quest that
        /// is not in the filtered list; hides it otherwise. Notice visibility is always
        /// derived from this one rule, never toggled ad hoc.
        /// </summary>
        private void UpdateFilteredOutNotice(QuestViewModel? shownVm)
        {
            var hidden = shownVm != null
                && CurrentFilteredList != null
                && !IsVisibleUnderCurrentFilters(shownVm);

            if (hidden)
            {
                TxtFilteredOutNotice.Text = _loc.QuestHiddenByFilters;
                BtnShowInList.Content = _loc.ShowInList;
            }
            PnlFilteredOutNotice.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// The explicit escape hatch from the hidden-by-filters state: reproduces the old
        /// navigation behavior (reset every filter, highlight the quest in the list) as a
        /// user-invoked action instead of a silent side effect.
        /// </summary>
        private void BtnShowInList_Click(object sender, RoutedEventArgs e)
        {
            ResetFilters();
            ApplyFilters();

            // ReconcileDetailSelection re-selected the shown quest if the reset made it
            // visible. The faction toggle is not part of the reset, so an other-faction
            // quest can legitimately stay hidden — then the notice stays up, truthfully.
            if (LstQuests.SelectedItem is QuestViewModel vm)
            {
                ScrollQuestIntoView(vm);
            }
        }

        /// <summary>
        /// The quest the detail panel is showing/acting on: the list selection when
        /// present, otherwise the quest the panel keeps showing while the filters hide
        /// it (SelectedItem is null in that state — see SelectQuestInternal and
        /// ReconcileDetailSelection).
        /// </summary>
        private QuestViewModel? ShownQuestViewModel()
            => LstQuests.SelectedItem as QuestViewModel
               ?? FindQuestViewModel(_currentDetailTask?.NormalizedName);

        /// <summary>
        /// Returns the detail panel to the empty "select a quest" state and forgets the
        /// shown quest, so refresh paths cannot resurrect it through
        /// <see cref="ShownQuestViewModel"/>.
        /// </summary>
        private void ClearDetailPanel()
        {
            _currentDetailTask = null;
            DetailPanel.Visibility = Visibility.Collapsed;
            TxtSelectQuest.Visibility = Visibility.Visible;
            UpdateFilteredOutNotice(null);
        }

        private void UpdateDetailPanel(QuestViewModel? overrideVm = null)
        {
            // Refresh paths (progress/language/DB events) call this with no selection
            // while filters hide the shown quest — ShownQuestViewModel keeps showing it
            // rather than collapsing the panel (see ReconcileDetailSelection).
            var selectedVm = overrideVm ?? ShownQuestViewModel();

            if (selectedVm == null)
            {
                ClearDetailPanel();
                return;
            }

            DetailPanel.Visibility = Visibility.Visible;
            TxtSelectQuest.Visibility = Visibility.Collapsed;
            UpdateFilteredOutNotice(selectedVm);

            var task = selectedVm.Task;
            _currentDetailTask = task;
            var status = _progressService.GetStatus(task);

            // Show on Map button - hidden (Map feature removed)
            BtnShowOnMap.Visibility = Visibility.Collapsed;

            // Title
            var (displayName, subtitle, showSubtitle) = GetLocalizedNames(task);
            TxtDetailName.Text = displayName;
            TxtDetailSubtitle.Text = subtitle;
            TxtDetailSubtitle.Visibility = showSubtitle ? Visibility.Visible : Visibility.Collapsed;

            // Trader & Status
            TxtDetailTrader.Text = task.Trader;
            TxtDetailStatus.Text = GetStatusText(status);
            DetailStatusBadge.Background = GetStatusBrush(status);

            // Maps
            if (task.Maps != null && task.Maps.Count > 0)
            {
                var mapNames = task.Maps.Select(GetLocalizedMapName);
                TxtDetailMap.Text = string.Join(", ", mapNames);
                MapInfoPanel.Visibility = Visibility.Visible;
            }
            else
            {
                TxtDetailMap.Text = "-";
                MapInfoPanel.Visibility = Visibility.Visible;
            }

            // Kappa Progress Section (for Collector quest)
            UpdateKappaProgressSection(task);

            // Requirements - Level with current level comparison
            bool hasLevelRequirement = task.RequiredLevel.HasValue && task.RequiredLevel.Value > 0;
            bool hasScavKarmaRequirement = task.RequiredScavKarma.HasValue;

            if (hasLevelRequirement)
            {
                var playerLevel = SettingsService.Instance.PlayerLevel;
                var reqLevel = task.RequiredLevel!.Value;
                if (playerLevel >= reqLevel)
                {
                    TxtRequiredLevel.Text = $"Level {reqLevel} (Current: {playerLevel})";
                    TxtRequiredLevel.Foreground = (Brush)FindResource("TextPrimaryBrush");
                }
                else
                {
                    TxtRequiredLevel.Text = $"Level {reqLevel} (Current: {playerLevel})";
                    TxtRequiredLevel.Foreground = LevelLockedBrush;
                }
                TxtRequiredLevel.Visibility = Visibility.Visible;
            }
            else
            {
                TxtRequiredLevel.Visibility = Visibility.Collapsed;
            }

            // Requirements - Scav Karma (Fence reputation)
            if (hasScavKarmaRequirement)
            {
                var playerScavRep = SettingsService.Instance.ScavRep;
                var reqKarma = task.RequiredScavKarma!.Value;
                var isMet = _progressService.IsScavKarmaRequirementMet(task);
                var comparison = reqKarma < 0 ? "≤" : "≥";
                TxtRequiredScavKarma.Text = $"Scav Karma {comparison} {reqKarma:0.#} (Current: {playerScavRep:0.#})";
                TxtRequiredScavKarma.Foreground = isMet ? (Brush)FindResource("TextPrimaryBrush") : LevelLockedBrush;
                TxtRequiredScavKarma.Visibility = Visibility.Visible;
            }
            else
            {
                TxtRequiredScavKarma.Visibility = Visibility.Collapsed;
            }

            // Show requirements section if any requirement exists
            RequirementsSectionWrapper.Visibility = (hasLevelRequirement || hasScavKarmaRequirement)
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Prerequisites - show direct prerequisites with OR/AND grouping
            if (task.TaskRequirements != null && task.TaskRequirements.Count > 0)
            {
                var prereqGroups = new List<PrerequisiteGroupViewModel>();

                // Group by GroupId: 0 = AND (each as separate group), >0 = OR (same GroupId = same group)
                var andRequirements = task.TaskRequirements.Where(r => r.GroupId == 0).ToList();
                var orGroups = task.TaskRequirements
                    .Where(r => r.GroupId > 0)
                    .GroupBy(r => r.GroupId)
                    .ToList();

                // Add AND requirements (each as separate entry)
                foreach (var req in andRequirements)
                {
                    var reqTask = _progressService.ResolveRequirementTask(req);

                    if (reqTask == null) continue;

                    var pStatus = _progressService.GetStatus(reqTask);
                    var (pName, _, _) = GetLocalizedNames(reqTask);

                    prereqGroups.Add(new PrerequisiteGroupViewModel
                    {
                        GroupId = 0,
                        Items = new List<PrerequisiteItemViewModel>
                        {
                            new PrerequisiteItemViewModel
                            {
                                Task = reqTask,
                                DisplayName = pName,
                                StatusText = GetStatusText(pStatus),
                                StatusBackground = GetStatusBrush(pStatus),
                                IsOrItem = false
                            }
                        }
                    });
                }

                // Add OR groups (only show as OR group if 2+ items, otherwise treat as AND)
                foreach (var orGroup in orGroups)
                {
                    var orItems = orGroup.ToList();

                    // If OR group has only 1 item, treat it as a regular AND requirement
                    if (orItems.Count == 1)
                    {
                        var req = orItems[0];
                        var reqTask = _progressService.ResolveRequirementTask(req);

                        if (reqTask != null)
                        {
                            var pStatus = _progressService.GetStatus(reqTask);
                            var (pName, _, _) = GetLocalizedNames(reqTask);

                            prereqGroups.Add(new PrerequisiteGroupViewModel
                            {
                                GroupId = 0, // Treat as AND (not OR group)
                                Items = new List<PrerequisiteItemViewModel>
                                {
                                    new PrerequisiteItemViewModel
                                    {
                                        Task = reqTask,
                                        DisplayName = pName,
                                        StatusText = GetStatusText(pStatus),
                                        StatusBackground = GetStatusBrush(pStatus),
                                        IsOrItem = false
                                    }
                                }
                            });
                        }
                        continue;
                    }

                    // OR group with 2+ items - display as OR group
                    var groupVm = new PrerequisiteGroupViewModel
                    {
                        GroupId = orGroup.Key,
                        Items = new List<PrerequisiteItemViewModel>()
                    };

                    bool isFirst = true;
                    foreach (var req in orItems)
                    {
                        var reqTask = _progressService.ResolveRequirementTask(req);

                        if (reqTask == null) continue;

                        var pStatus = _progressService.GetStatus(reqTask);
                        var (pName, _, _) = GetLocalizedNames(reqTask);

                        groupVm.Items.Add(new PrerequisiteItemViewModel
                        {
                            Task = reqTask,
                            DisplayName = pName,
                            StatusText = GetStatusText(pStatus),
                            StatusBackground = GetStatusBrush(pStatus),
                            IsOrItem = !isFirst  // Show "OR" separator for 2nd item onwards
                        });
                        isFirst = false;
                    }

                    if (groupVm.Items.Count > 0)
                    {
                        prereqGroups.Add(groupVm);
                    }
                }

                PrerequisitesList.ItemsSource = prereqGroups;
                PrerequisitesSectionWrapper.Visibility = prereqGroups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                PrerequisitesList.ItemsSource = null;
                PrerequisitesSectionWrapper.Visibility = Visibility.Collapsed;
            }

            // Alternative Quests Section (Mutually Exclusive)
            var alternativeQuests = _progressService.GetAlternativeQuests(task);
            if (alternativeQuests.Count > 0)
            {
                var altVms = alternativeQuests.Select(alt =>
                {
                    var altStatus = _progressService.GetStatus(alt);
                    var (displayName, _, _) = GetLocalizedNames(alt);
                    return new
                    {
                        DisplayName = displayName,
                        TraderName = alt.Trader,
                        StatusText = GetStatusText(altStatus, alt),
                        StatusBackground = GetStatusBrush(altStatus)
                    };
                }).ToList();

                AlternativeQuestsList.ItemsSource = altVms;
                AlternativeQuestsSectionWrapper.Visibility = Visibility.Visible;
            }
            else
            {
                AlternativeQuestsList.ItemsSource = null;
                AlternativeQuestsSectionWrapper.Visibility = Visibility.Collapsed;
            }

            // Objectives Section
            UpdateObjectivesSection(task);

            // Guide Section
            UpdateGuideSection(task);

            // Required Items
            if (task.RequiredItems != null && task.RequiredItems.Count > 0)
            {
                LoadRequiredItems(task.RequiredItems);
                RequiredItemsSectionWrapper.Visibility = Visibility.Visible;
            }
            else
            {
                RequiredItemsList.ItemsSource = null;
                RequiredItemsSectionWrapper.Visibility = Visibility.Collapsed;
            }

            // Button states
            BtnComplete.Visibility = status == QuestStatus.Done ? Visibility.Collapsed : Visibility.Visible;
            BtnReset.Visibility = status == QuestStatus.Done || status == QuestStatus.Failed
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CompleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is QuestViewModel vm)
            {
                CompleteQuestWithConfirmation(vm.Task);
            }
        }

        /// <summary>
        /// Completes a quest, confirming first when the completion would cascade —
        /// auto-complete incomplete prerequisites or auto-fail mutually exclusive
        /// alternatives (see feature-quest-complete-cascade-confirm.md). An empty
        /// cascade keeps the old one-click completion. The confirmed cascade is
        /// applied verbatim — not recomputed — so what the dialog listed is exactly
        /// what changes, even if a background log-sync event landed while the modal
        /// was open.
        /// </summary>
        private void CompleteQuestWithConfirmation(TarkovTask task)
        {
            var cascade = _progressService.GetCompletionCascade(task);
            if (!cascade.IsEmpty &&
                !QuestCompleteConfirmDialog.Confirm(Window.GetWindow(this), task, cascade))
            {
                return;
            }

            _progressService.ApplyCompletionCascade(cascade);
        }

        private void BtnWiki_Click(object sender, RoutedEventArgs e)
        {
            // ShownQuestViewModel, not SelectedItem: while the filters hide the shown
            // quest, the selection is intentionally null but the panel (and its buttons)
            // still shows the quest — the button must act on it.
            var selectedVm = ShownQuestViewModel();
            if (selectedVm?.Task.Name == null) return;

            var wikiPageName = NormalizedNameGenerator.GetWikiPageName(selectedVm.Task.Name);
            var wikiUrl = $"https://escapefromtarkov.fandom.com/wiki/{Uri.EscapeDataString(wikiPageName.Replace(" ", "_"))}";

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = wikiUrl,
                    UseShellExecute = true
                });
            }
            catch { /* Ignore errors opening browser */ }
        }

        private void BtnShowOnMap_Click(object sender, RoutedEventArgs e)
        {
            // Map feature removed - this button is hidden
        }

        private void BtnComplete_Click(object sender, RoutedEventArgs e)
        {
            // ShownQuestViewModel, not SelectedItem — see BtnWiki_Click.
            var selectedVm = ShownQuestViewModel();
            if (selectedVm != null)
            {
                CompleteQuestWithConfirmation(selectedVm.Task);
            }
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            // ShownQuestViewModel, not SelectedItem — see BtnWiki_Click.
            var selectedVm = ShownQuestViewModel();
            if (selectedVm != null)
            {
                _progressService.ResetQuest(selectedVm.Task);
            }
        }

        #region Kappa Progress Section

        private void UpdateKappaProgressSection(TarkovTask task)
        {
            // Check if this is the Collector quest
            var isCollector = task.NormalizedName?.Equals("collector", StringComparison.OrdinalIgnoreCase) == true;

            if (!isCollector)
            {
                KappaProgressSection.Visibility = Visibility.Collapsed;
                return;
            }

            KappaProgressSection.Visibility = Visibility.Visible;

            // Get Kappa progress
            var graphService = QuestGraphService.Instance;
            var (completed, total, percentage) = graphService.GetCollectorProgress(
                normalizedName => _progressService.IsQuestCompleted(normalizedName));

            // Update progress text
            TxtKappaProgress.Text = $"Prerequisites: ({completed}/{total} completed)";
            TxtKappaProgressPercent.Text = $"{percentage}%";

            // Update progress bar width
            KappaProgressBar.Width = (percentage / 100.0) * (KappaProgressBar.Parent as Grid)?.ActualWidth ?? 0;

            // If parent grid not yet rendered, set it after layout
            if (KappaProgressBar.Width == 0 && percentage > 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var parentGrid = KappaProgressBar.Parent as Grid;
                    if (parentGrid != null)
                    {
                        KappaProgressBar.Width = (percentage / 100.0) * parentGrid.ActualWidth;
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void BtnShowKappaQuests_Click(object sender, RoutedEventArgs e)
        {
            var graphService = QuestGraphService.Instance;
            var kappaQuests = graphService.GetKappaRequiredQuestsWithStatus(
                normalizedName => _progressService.IsQuestCompleted(normalizedName));

            // Create a popup window to show all Kappa required quests
            var popupWindow = new Window
            {
                Title = "Kappa Required Quests",
                Width = 500,
                Height = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                Background = (Brush)FindResource("BackgroundDarkBrush")
            };

            var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var stackPanel = new StackPanel { Margin = new Thickness(16) };

            // Header
            var (completed, total, percentage) = graphService.GetCollectorProgress(
                normalizedName => _progressService.IsQuestCompleted(normalizedName));
            var headerText = new TextBlock
            {
                Text = $"Kappa Required Quests ({completed}/{total})",
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("AccentBrush"),
                Margin = new Thickness(0, 0, 0, 16)
            };
            stackPanel.Children.Add(headerText);

            // Quest list
            foreach (var (quest, isCompleted) in kappaQuests)
            {
                var (displayName, _, _) = GetLocalizedNames(quest);
                var questPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };

                // Status indicator
                var statusIndicator = new TextBlock
                {
                    Text = isCompleted ? "✓" : "○",
                    FontSize = 14,
                    Foreground = isCompleted ? DoneBrush : (Brush)FindResource("TextSecondaryBrush"),
                    Width = 24,
                    VerticalAlignment = VerticalAlignment.Center
                };

                // Quest name
                var questName = new TextBlock
                {
                    Text = displayName,
                    FontSize = 13,
                    Foreground = isCompleted ? (Brush)FindResource("TextSecondaryBrush") : (Brush)FindResource("TextPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextDecorations = isCompleted ? TextDecorations.Strikethrough : null
                };

                // Trader
                var traderText = new TextBlock
                {
                    Text = $"  ({quest.Trader})",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };

                questPanel.Children.Add(statusIndicator);
                questPanel.Children.Add(questName);
                questPanel.Children.Add(traderText);
                stackPanel.Children.Add(questPanel);
            }

            scrollViewer.Content = stackPanel;
            popupWindow.Content = scrollViewer;
            popupWindow.ShowDialog();
        }

        #endregion

        #region Objectives Section

        private void UpdateObjectivesSection(TarkovTask task)
        {
            ObjectivesList.Children.Clear();

            if (task.Objectives != null && task.Objectives.Count > 0)
            {
                for (int i = 0; i < task.Objectives.Count; i++)
                {
                    var objective = task.Objectives[i];
                    var isCompleted = task.NormalizedName != null &&
                        _progressService.IsObjectiveCompleted(task.NormalizedName, i);
                    var objectiveElement = CreateObjectiveElement(objective, i, isCompleted);
                    ObjectivesList.Children.Add(objectiveElement);
                }
                ObjectivesSection.Visibility = Visibility.Visible;
            }
            else
            {
                ObjectivesSection.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Create an objective element with checkbox and Optional badge if needed
        /// </summary>
        private FrameworkElement CreateObjectiveElement(string objective, int objectiveIndex, bool isCompleted)
        {
            // Check for (''Optional'') pattern in wiki markup
            var isOptional = WikiMarkupHelper.IsOptional(objective);

            // Remove the optional marker from text
            var cleanedObjective = WikiMarkupHelper.RemoveOptionalMarker(objective);

            // Main container with checkbox
            var mainContainer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2)
            };

            // Checkbox
            var checkBox = new CheckBox
            {
                IsChecked = isCompleted,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 8, 0),
                Tag = objectiveIndex
            };
            checkBox.Checked += ObjectiveCheckBox_Changed;
            checkBox.Unchecked += ObjectiveCheckBox_Changed;
            mainContainer.Children.Add(checkBox);

            // Get brushes for text styling. Font family/size are NOT snapshotted here:
            // CreateRichTextBlockWithoutBullet applies them as resource references so
            // objective text follows live language and base-font-size changes.
            var defaultBrush = (Brush)FindResource("TextPrimaryBrush");
            var accentBrush = (Brush)FindResource("AccentBrush");

            if (isOptional)
            {
                // Create horizontal layout with Optional badge + text
                var contentContainer = new StackPanel
                {
                    Orientation = Orientation.Horizontal
                };

                // Optional badge
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(60, 255, 193, 7)), // Amber/yellow with transparency
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };

                var badgeText = new TextBlock
                {
                    Text = "Optional",
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)) // Amber color
                };

                badge.Child = badgeText;
                contentContainer.Children.Add(badge);

                // Create text block without bullet (badge replaces the bullet indicator)
                var textBlock = WikiMarkupHelper.CreateRichTextBlockWithoutBullet(
                    cleanedObjective, defaultBrush, accentBrush, isCompleted);
                contentContainer.Children.Add(textBlock);

                mainContainer.Children.Add(contentContainer);
            }
            else
            {
                // Create text block without bullet (checkbox replaces the bullet)
                var textBlock = WikiMarkupHelper.CreateRichTextBlockWithoutBullet(
                    cleanedObjective, defaultBrush, accentBrush, isCompleted);
                mainContainer.Children.Add(textBlock);
            }

            return mainContainer;
        }

        private void ObjectiveCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_currentDetailTask?.NormalizedName == null) return;

            var checkBox = sender as CheckBox;
            if (checkBox?.Tag is int objectiveIndex)
            {
                var isCompleted = checkBox.IsChecked ?? false;

                _progressService.SetObjectiveCompleted(
                    _currentDetailTask.NormalizedName,
                    objectiveIndex,
                    isCompleted,
                    null);

                // Update the text style (strikethrough)
                var parent = checkBox.Parent as StackPanel;
                if (parent != null)
                {
                    UpdateObjectiveTextStyle(parent, isCompleted);
                }
            }
        }

        private void UpdateObjectiveTextStyle(StackPanel container, bool isCompleted)
        {
            foreach (var child in container.Children)
            {
                if (child is TextBlock textBlock)
                {
                    textBlock.TextDecorations = isCompleted ? TextDecorations.Strikethrough : null;
                    textBlock.Opacity = isCompleted ? 0.6 : 1.0;
                }
                else if (child is StackPanel innerPanel)
                {
                    UpdateObjectiveTextStyle(innerPanel, isCompleted);
                }
            }
        }

        #endregion

        #region Guide Section

        private void UpdateGuideSection(TarkovTask task)
        {
            var hasGuideText = !string.IsNullOrEmpty(task.GuideText);
            var hasGuideImages = task.GuideImages != null && task.GuideImages.Count > 0;

            if (!hasGuideText && !hasGuideImages)
            {
                GuideSection.Visibility = Visibility.Collapsed;
                return;
            }

            GuideSection.Visibility = Visibility.Visible;

            // Guide text - show directly in scrollable text block
            if (hasGuideText)
            {
                TxtGuideText.Text = task.GuideText;
                GuideTextSection.Visibility = Visibility.Visible;
            }
            else
            {
                GuideTextSection.Visibility = Visibility.Collapsed;
            }

            // Guide images - lazy load when expander is opened
            if (hasGuideImages)
            {
                _pendingGuideImages = task.GuideImages;
                _guideImagesLoaded = false;
                GuideImagesList.ItemsSource = null; // Clear previous images
                GuideImagesExpander.IsExpanded = false; // Reset to collapsed
                GuideImagesExpander.Visibility = Visibility.Visible;
                TxtGuideImagesHeader.Text = $"View Images ({task.GuideImages!.Count})";
            }
            else
            {
                _pendingGuideImages = null;
                GuideImagesExpander.Visibility = Visibility.Collapsed;
                GuideImagesList.ItemsSource = null;
            }
        }

        private void GuideImagesExpander_Expanded(object sender, RoutedEventArgs e)
        {
            // Load images only when expander is opened for the first time
            if (!_guideImagesLoaded && _pendingGuideImages != null)
            {
                _guideImagesLoaded = true;
                _ = LoadGuideImagesAsync(_pendingGuideImages);
            }
        }

        private async Task LoadGuideImagesAsync(List<GuideImage> guideImages)
        {
            // Create ViewModels with loading state first
            var imageVms = new System.Collections.ObjectModel.ObservableCollection<GuideImageViewModel>();

            foreach (var guideImage in guideImages)
            {
                imageVms.Add(new GuideImageViewModel
                {
                    FileName = guideImage.FileName,
                    Caption = guideImage.Caption,
                    IsLoading = true
                });
            }

            // Set ItemsSource immediately to show placeholders
            if (!_isUnloaded)
            {
                Dispatcher.Invoke(() =>
                {
                    if (!_isUnloaded)
                        GuideImagesList.ItemsSource = imageVms;
                });
            }

            // Load images in parallel for better performance
            var loadTasks = imageVms.Select(async (vm, index) =>
            {
                if (_isUnloaded) return;

                var image = await _imageCache.GetWikiImageAsync(vm.FileName);

                // Update on UI thread
                if (!_isUnloaded)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!_isUnloaded)
                        {
                            vm.ImageSource = image;
                            vm.IsLoading = false;
                        }
                    });
                }
            });

            await Task.WhenAll(loadTasks);
        }

        private void GuideImage_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is GuideImageViewModel vm)
            {
                // Open wiki image in browser
                var encodedFileName = Uri.EscapeDataString(vm.FileName.Replace(" ", "_"));
                var url = $"https://escapefromtarkov.fandom.com/wiki/File:{encodedFileName}";

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Ignore errors opening browser
                }
            }
        }

        #endregion

        #region Required Items with Localization

        private void LoadRequiredItems(List<QuestItem> requiredItems)
        {
            var itemVms = new List<RequiredItemViewModel>();

            foreach (var item in requiredItems)
            {
                if (_isUnloaded) return; // Check if page was unloaded

                // Calculate fulfillment status
                var requiredFir = item.FoundInRaid ? item.Amount : 0;
                var fulfillmentInfo = _inventoryService.GetFulfillmentInfo(
                    item.ItemNormalizedName, item.Amount, requiredFir);
                var isFulfilled = fulfillmentInfo.Status == Models.ItemFulfillmentStatus.Fulfilled;

                // Get item from lookup (for ItemId and localized name)
                var tarkovItem = GetItemByNormalizedName(item.ItemNormalizedName, item.ItemDisplayName);

                var vm = new RequiredItemViewModel
                {
                    FoundInRaid = item.FoundInRaid,
                    RequirementType = item.Requirement,
                    ItemId = tarkovItem?.Id ?? string.Empty, // Use ItemId for navigation
                    IsFulfilled = isFulfilled
                };

                // Get localized item name (with display name fallback)
                var localizedName = GetLocalizedItemName(item.ItemNormalizedName, item.ItemDisplayName);
                vm.DisplayText = $"{localizedName} x{item.Amount}";

                // Get item icon
                if (!string.IsNullOrEmpty(tarkovItem?.Id))
                {
                    var icon = _imageCache.GetLocalItemIcon(tarkovItem.Id);
                    vm.IconSource = icon;
                }

                itemVms.Add(vm);
            }

            RequiredItemsList.ItemsSource = itemVms;
        }

        /// <summary>
        /// Handle click on item name to navigate to Items tab
        /// </summary>
        private void ItemName_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is RequiredItemViewModel vm)
            {
                if (string.IsNullOrEmpty(vm.ItemId)) return;

                var mainWindow = Window.GetWindow(this) as MainWindow;
                mainWindow?.NavigateToItem(vm.ItemId);
            }
        }

        private string GetLocalizedItemName(string normalizedName, string? displayNameFallback = null)
        {
            var item = GetItemByNormalizedName(normalizedName, displayNameFallback);
            if (item == null)
                return !string.IsNullOrEmpty(displayNameFallback) ? displayNameFallback : normalizedName;

            return _loc.CurrentLanguage switch
            {
                AppLanguage.KO => item.NameKo ?? item.Name,
                AppLanguage.JA => item.NameJa ?? item.Name,
                _ => item.Name
            };
        }

        private TarkovItem? GetItemByNormalizedName(string normalizedName, string? displayName = null)
        {
            if (_itemLookup == null)
                return null;

            // Strategy 1: Direct lookup
            if (_itemLookup.TryGetValue(normalizedName, out var item))
                return item;

            // Strategy 2: Try alternatives from display name
            if (!string.IsNullOrEmpty(displayName))
            {
                var displayAlternatives = NormalizedNameGenerator.GenerateAlternatives(displayName);
                foreach (var alt in displayAlternatives)
                {
                    if (_itemLookup.TryGetValue(alt, out item))
                        return item;
                }
            }

            // Strategy 3: Try with alternative names from normalized name (fuzzy match)
            var alternatives = NormalizedNameGenerator.GenerateAlternatives(normalizedName);
            foreach (var alt in alternatives)
            {
                if (_itemLookup.TryGetValue(alt, out item))
                    return item;
            }

            return null;
        }

        #endregion

        #region Nested ScrollViewer Scroll Propagation

        /// <summary>
        /// Handle mouse wheel events on nested ScrollViewers to propagate to parent when at scroll limits
        /// </summary>
        private void NestedScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer) return;

            // Check if the nested ScrollViewer can handle this scroll
            var canScrollDown = scrollViewer.VerticalOffset < scrollViewer.ScrollableHeight;
            var canScrollUp = scrollViewer.VerticalOffset > 0;
            var scrollingDown = e.Delta < 0;
            var scrollingUp = e.Delta > 0;

            // If the nested ScrollViewer cannot scroll in the direction of the wheel,
            // or if there's no scrollable content, propagate to parent
            bool shouldPropagate = false;

            if (scrollViewer.ScrollableHeight <= 0)
            {
                // No scrollable content, propagate to parent
                shouldPropagate = true;
            }
            else if (scrollingDown && !canScrollDown)
            {
                // At bottom, trying to scroll down - propagate
                shouldPropagate = true;
            }
            else if (scrollingUp && !canScrollUp)
            {
                // At top, trying to scroll up - propagate
                shouldPropagate = true;
            }

            if (shouldPropagate)
            {
                // Don't handle the event, let it bubble up to parent ScrollViewer
                e.Handled = false;

                // Manually raise the event on the parent ScrollViewer
                var parentScrollViewer = DetailScrollViewer;
                if (parentScrollViewer != null)
                {
                    var newEventArgs = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                    {
                        RoutedEvent = UIElement.MouseWheelEvent,
                        Source = sender
                    };
                    parentScrollViewer.RaiseEvent(newEventArgs);
                    e.Handled = true;
                }
            }
        }

        #endregion

        #region Quest Recommendations

        private void UpdateRecommendations()
        {
            // Set up localization function for the recommendations panel
            RecommendationsPanel.GetLocalizedNames = GetLocalizedNames;
            RecommendationsPanel.UpdateRecommendations();
        }

        private void RecommendationsPanel_RecommendationClicked(object? sender, string questNormalizedName)
        {
            SelectQuestInternal(questNormalizedName);
        }

        #endregion

        #region Prerequisite Quest Navigation

        /// <summary>
        /// Handle click on prerequisite quest name to navigate to that quest
        /// </summary>
        private void PrerequisiteQuest_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PrerequisiteItemViewModel vm)
            {
                if (vm.Task?.NormalizedName != null)
                {
                    SelectQuestInternal(vm.Task.NormalizedName);
                }
            }
        }

        #endregion
    }
}
