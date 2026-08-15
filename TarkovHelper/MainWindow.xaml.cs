using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Pages;
using TarkovHelper.Pages.Map;
using TarkovHelper.Services;
using TarkovHelper.Services.Logging;
using TarkovHelper.Windows;

namespace TarkovHelper;

public partial class MainWindow : Window
{
    private static readonly ILogger _log = Log.For<MainWindow>();
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly HideoutProgressService _hideoutProgressService = HideoutProgressService.Instance;
    private readonly SettingsService _settingsService = SettingsService.Instance;
    private readonly LogSyncService _logSyncService = LogSyncService.Instance;

    // Serializes live quest events. LogSyncService raises QuestEventDetected in a tight loop
    // over one tail read, and the handler is asynchronous, so without this gate event N+1
    // would start planning while event N is still between its read and its write. Two events
    // for one quest (Completed then Failed) would then plan against the same pre-write rows
    // and the loser's status would stick: a quest the game failed left recorded as Done.
    private readonly SemaphoreSlim _questEventGate = new(1, 1);

    private readonly DispatcherTimer _profileTransitionCueTimer;
    private bool _isLoading;
    private bool _isUpdatingProfileUI;

    // Raised while the profile drawer's controls are being written FROM the settings service, so
    // an assignment that wakes a handler cannot be mistaken for a player edit. See
    // SuppressSettingsEcho for what that costs when it is missing.
    private bool _isUpdatingSettingsUI;

    // 시작 로딩(_isLoading) 중 눌린 탭: 로딩이 끝나면 Window_Loaded가 재생한다
    private object? _pendingTabDuringLoad;
    private QuestListPage? _questListPage;
    private HideoutPage? _hideoutPage;
    private ItemsPage? _itemsPage;
    private CollectorPage? _collectorPage;
    private MapPage? _mapTrackerPage;
    private List<HideoutModule>? _hideoutModules;
    private bool _isFullScreen;

    // Windows API for dark title bar
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowBounds();
        _loc.LanguageChanged += OnLanguageChanged;
        _settingsService.PlayerLevelChanged += OnPlayerLevelChanged;
        _settingsService.ScavRepChanged += OnScavRepChanged;
        _settingsService.DspDecodeCountChanged += OnDspDecodeCountChanged;
        _settingsService.HasEodEditionChanged += OnEditionChanged;
        _settingsService.HasUnheardEditionChanged += OnEditionChanged;
        _settingsService.PrestigeLevelChanged += OnPrestigeLevelChanged;

        _profileTransitionCueTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1400)
        };
        _profileTransitionCueTimer.Tick += ProfileTransitionCueTimer_Tick;

        // Reflect app-profile changes in both responsive selector variants.
        ProfileService.Instance.ActiveProfileChanged += OnActiveProfileChanged;

        // Paint the initial selection now rather than waiting for Window_Loaded: that
        // method only reaches UpdateProfileUI after awaiting the user-DB and profile
        // initialization, and the window is already visible (the loading overlay starts
        // collapsed), so the selector would otherwise show no selection for the whole
        // of a first-run schema migration. Safe here: the cue timer exists above, and
        // the restored profile still repaints this once InitializeAsync resolves.
        UpdateProfileUI(ProfileService.Instance.ActiveProfile);

        // Sync/raid status chip. Subscribed in the constructor (not Window_Loaded) so
        // events fired during AutoStartLogMonitoring aren't missed; named handlers so
        // OnWindowClosing can unsubscribe them from these app-lifetime singletons
        // (a background raise after close must not dispatch against a torn-down
        // window). InvokeAsync (never a blocking Invoke): MonitoringStatusChanged is
        // raised while LogSyncService holds _watcherLock, so a blocking dispatch from
        // a background raise can deadlock against a UI-thread Start/StopMonitoring
        // call taking the same lock.
        _logSyncService.MonitoringStatusChanged += OnLogMonitoringStatusChanged;
        EftRaidEventService.Instance.MonitoringStateChanged += OnRaidMonitoringStateChanged;
        EftRaidEventService.Instance.RaidEvent += OnRaidEvent;

        // Keep the profile drawer anchored just below the title bar; the bar's height
        // changes with the font-size setting, so the margin can't be hardcoded in XAML.
        TitleBar.SizeChanged += (_, _) => ProfileDrawer.Margin = new Thickness(0, TitleBar.ActualHeight, 0, 0);

        SourceInitialized += OnSourceInitialized;
        Closing += OnWindowClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        EnableDarkTitleBar();

        // Applied here (not in the constructor) so Windows maximizes onto the
        // monitor containing the restored bounds, not the primary monitor
        if (_restoreMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void EnableDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var useDarkMode = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
    }

    private void OnLanguageChanged(object? sender, AppLanguage e)
    {
        UpdateAllLocalizedText();

        // The language combo lives inside the Settings overlay, so that overlay is
        // typically open (and being looked at) when the language changes, so refresh its
        // strings immediately instead of leaving them stale until the next open.
        // Cheap and safe when the overlay is closed: it only assigns text properties.
        UpdateSettingsLocalizedText();
    }

    private void UpdateAllLocalizedText()
    {
        TxtWelcome.Text = _loc.Welcome;

        // Tab labels
        TxtTabQuests.Text = _loc.TabQuests;
        TxtTabHideout.Text = _loc.TabHideout;
        TxtTabItems.Text = _loc.TabItems;
        TxtTabCollector.Text = _loc.TabCollector;
        TxtTabMap.Text = _loc.TabMap;

        // UIA names for screen readers: panel content replaced the old string Content,
        // which is what previously supplied these controls' automation Name (WPF does
        // not synthesize a Name from TextBlocks inside panel content).
        AutomationProperties.SetName(TabQuests, _loc.TabQuests);
        AutomationProperties.SetName(TabHideout, _loc.TabHideout);
        AutomationProperties.SetName(TabItems, _loc.TabItems);
        AutomationProperties.SetName(TabCollector, _loc.TabCollector);
        AutomationProperties.SetName(TabMap, _loc.TabMap);
        AutomationProperties.SetName(BtnProfile, _loc.HeaderProfileName);
        AutomationProperties.SetName(BtnSettings, _loc.Settings);

        // Title bar profile labels and tooltips
        BtnPvpZone.Content = _loc.HeaderPvpZone;
        BtnPveZone.Content = _loc.HeaderPveZone;
        BtnPvpSeason.Content = _loc.HeaderPvpSeason;
        MenuPvpZone.Header = _loc.HeaderPvpZone;
        MenuPveZone.Header = _loc.HeaderPveZone;
        MenuPvpSeason.Header = _loc.HeaderPvpSeason;
        TxtActiveProfileLabel.Text = _loc.HeaderActiveProfile;
        BtnPvpZone.ToolTip = _loc.HeaderPvpTooltip;
        BtnPveZone.ToolTip = _loc.HeaderPveTooltip;
        BtnPvpSeason.ToolTip = _loc.HeaderPvpSeasonTooltip;
        AutomationProperties.SetName(BtnPvpZone, _loc.HeaderPvpZone);
        AutomationProperties.SetName(BtnPveZone, _loc.HeaderPveZone);
        AutomationProperties.SetName(BtnPvpSeason, _loc.HeaderPvpSeason);
        AutomationProperties.SetName(WideProfileSelector, _loc.HeaderActiveProfile);
        BtnActiveProfileMenu.ToolTip = _loc.HeaderProfileMenuTooltip;
        BtnProfile.ToolTip = _loc.HeaderProfileTooltip;
        BtnSettings.ToolTip = _loc.Settings;
        ChipSyncStatus.ToolTip = _loc.SyncStatusTooltip;

        // Profile drawer group labels
        TxtProfileLevelLabel.Text = _loc.ProfileLevelLabel;
        TxtScavRepLabel.Text = _loc.ProfileScavRepLabel;
        TxtDspLabel.Text = _loc.ProfileDspLabel;
        TxtEditionLabel.Text = _loc.ProfileEditionLabel;
        TxtPrestigeLabel.Text = _loc.ProfilePrestigeLabel;

        // Language-dependent composite texts (profile chip, status chip, version chip)
        UpdatePlayerLevelUI();
        UpdateProfileUI(ProfileService.Instance.ActiveProfile);
        UpdateSyncStatusChip();
        UpdateVersionChipUI();
    }

    private void CmbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading) return;
        if (CmbLanguage.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            _loc.CurrentLanguage = lang switch
            {
                "KO" => AppLanguage.KO,
                "JA" => AppLanguage.JA,
                _ => AppLanguage.EN
            };
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoading = true;

        // Ensure the user DB (profile schema migration) and active game mode are ready
        // before any progress/settings UI reads from them.
        await UserDataDbService.Instance.InitializeAsync();
        await ProfileService.Instance.InitializeAsync();

        // Reflect the loaded app profile in the title bar toggle.
        UpdateProfileUI(ProfileService.Instance.ActiveProfile);

        // Apply saved language setting to UI
        CmbLanguage.SelectedIndex = _loc.CurrentLanguage switch
        {
            AppLanguage.KO => 1,
            AppLanguage.JA => 2,
            _ => 0
        };

        // Initialize player level UI
        UpdatePlayerLevelUI();

        // Initialize Scav Rep UI
        UpdateScavRepUI();

        // Initialize DSP Decode Count UI
        UpdateDspDecodeUI();

        // Initialize Edition and Prestige Level UI
        UpdateEditionUI();
        UpdatePrestigeLevelUI();

        UpdateAllLocalizedText();

        _isLoading = false;

        // Replay a tab click swallowed while _isLoading was set (see Tab_Checked)
        if (_pendingTabDuringLoad != null)
        {
            var pendingTab = _pendingTabDuringLoad;
            _pendingTabDuringLoad = null;
            Tab_Checked(pendingTab, new RoutedEventArgs());
        }

        // Start database update check (initial check + background updates every 5 minutes)
        StartDatabaseUpdateService();

        // Start app update service (check every 3 minutes)
        StartAppUpdateService();

        // Load and show quest data from DB
        await CheckAndRefreshDataAsync();

        // Auto-start log monitoring if enabled
        AutoStartLogMonitoring();

        // Initial paint of the sync/raid status chip (belt-and-braces: the
        // constructor subscriptions already catch events raised during startup)
        UpdateSyncStatusChip();
    }

    /// <summary>
    /// 데이터베이스 업데이트 서비스 시작
    /// </summary>
    private void StartDatabaseUpdateService()
    {
        var dbUpdateService = DatabaseUpdateService.Instance;

        // 업데이트 완료 이벤트 구독 (UI 새로고침용)
        dbUpdateService.DatabaseUpdated += OnDatabaseUpdated;

        // 백그라운드 업데이트 체크 시작 (5분마다)
        dbUpdateService.StartBackgroundUpdates();

        _log.Info("Database update service started");
    }

    /// <summary>
    /// 데이터베이스 업데이트 완료 시 UI 새로고침
    /// </summary>
    private void OnDatabaseUpdated(object? sender, EventArgs e)
    {
        _log.Info("Database updated, all services will reload data automatically");

        // 서비스들이 이미 DatabaseUpdated 이벤트를 구독하고 있으므로
        // 각 서비스의 RefreshAsync()가 자동으로 호출됨
        // UI 페이지들은 서비스의 새로운 데이터를 사용하게 됨

        // 필요시 사용자에게 알림 표시 가능
        Dispatcher.Invoke(() =>
        {
            // 상태 표시줄이나 토스트 메시지로 업데이트 완료 알림 가능
            _log.Debug("Database update notification displayed");
        });
    }

    /// <summary>
    /// Automatically start log monitoring on app launch if enabled
    /// </summary>
    private void AutoStartLogMonitoring()
    {
        if (!_settingsService.LogMonitoringEnabled)
            return;

        // Try to get log folder path (auto-detect if not set)
        var logPath = _settingsService.LogFolderPath;

        // If no path and auto-detect failed, try to save auto-detected path
        if (string.IsNullOrEmpty(logPath))
        {
            logPath = _settingsService.AutoDetectLogFolder();
            if (!string.IsNullOrEmpty(logPath))
            {
                _settingsService.LogFolderPath = logPath;
            }
        }

        if (!string.IsNullOrEmpty(logPath) && Directory.Exists(logPath))
        {
            _logSyncService.StartMonitoring(logPath);
            _logSyncService.QuestEventDetected -= OnQuestEventDetected;
            _logSyncService.QuestEventDetected += OnQuestEventDetected;

            // App-wide raid/session monitoring so PvP/PvE auto-detect works on every tab,
            // not only while the Map page is open. ProfileService consumes SessionModeDetected.
            EftRaidEventService.Instance.StartMonitoring(logPath);

            _log.Info($"Auto-started log monitoring: {logPath}");
        }

        UpdateQuestSyncUI();
    }

    /// <summary>
    /// Re-points the log watchers (quest sync + raid events) at the current
    /// LogFolderPath. Called after the user changes the log folder in Settings:
    /// FileSystemWatchers bind to the path they were started with, so without a
    /// restart they keep watching the old folder until the app is relaunched.
    /// (StartMonitoring on both services stops any previous watcher first.)
    /// </summary>
    private void RestartLogMonitoring()
    {
        if (!_settingsService.LogMonitoringEnabled)
            return;

        var logPath = _settingsService.LogFolderPath;
        if (string.IsNullOrEmpty(logPath) || !Directory.Exists(logPath))
            return;

        _logSyncService.StartMonitoring(logPath);
        EftRaidEventService.Instance.StartMonitoring(logPath);
        _log.Info($"Log monitoring re-pointed to: {logPath}");
    }

    /// <summary>
    /// Load and show quest data from DB
    /// </summary>
    private async Task CheckAndRefreshDataAsync()
    {
        // Quest data is now bundled in tarkov_data.db, load directly
        await LoadAndShowQuestListAsync();
    }

    /// <summary>
    /// Show loading overlay with blur effect
    /// </summary>
    public void ShowLoadingOverlay(string status = "Loading...")
    {
        LoadingStatusText.Text = status;
        LoadingOverlay.Visibility = Visibility.Visible;

        var blurAnimation = new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(200));
        BlurEffect.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurAnimation);
    }

    /// <summary>
    /// Hide loading overlay
    /// </summary>
    public void HideLoadingOverlay()
    {
        var blurAnimation = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200));
        blurAnimation.Completed += (s, e) =>
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
        };
        BlurEffect.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurAnimation);
    }

    /// <summary>
    /// Update loading status text
    /// </summary>
    public void UpdateLoadingStatus(string status)
    {
        LoadingStatusText.Text = status;
    }

    /// <summary>
    /// 마이그레이션 진행 상황 업데이트
    /// </summary>
    private void OnMigrationProgress(string message)
    {
        // BeginInvoke를 사용하여 비동기로 UI 업데이트 (데드락 방지)
        Dispatcher.BeginInvoke(() => UpdateLoadingStatus(message));
    }

    /// <summary>
    /// Load task data and show Quest List page
    /// </summary>
    private async Task LoadAndShowQuestListAsync()
    {
        var progressService = QuestProgressService.Instance;
        var migrationService = ConfigMigrationService.Instance;

        List<TarkovTask>? tasks = null;
        ConfigMigrationService.MigrationResult? migrationResult = null;

        // 자동 마이그레이션 필요 여부 확인 (3.5 버전 등에서 업데이트 시)
        bool needsMigration = migrationService.NeedsAutoMigration();
        if (needsMigration)
        {
            ShowLoadingOverlay(_loc.CurrentLanguage switch
            {
                AppLanguage.KO => "데이터 마이그레이션 중...",
                AppLanguage.JA => "データ移行中...",
                _ => "Migrating data..."
            });

            try
            {
                var progress = new Progress<string>(message =>
                {
                    Dispatcher.BeginInvoke(() => UpdateLoadingStatus(message));
                });

                // ConfigMigrationService를 사용하여 마이그레이션 수행
                migrationResult = await migrationService.MigrateFromCurrentConfigAsync(progress);

                // 마이그레이션 결과 로깅
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "migration_log.txt");
                var logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Migration completed\n" +
                                 $"  Success: {migrationResult?.Success}\n" +
                                 $"  QuestProgress: {migrationResult?.QuestProgressCount}\n" +
                                 $"  HideoutProgress: {migrationResult?.HideoutProgressCount}\n" +
                                 $"  ItemInventory: {migrationResult?.ItemInventoryCount}\n" +
                                 $"  Settings: {migrationResult?.SettingsCount}\n" +
                                 $"  TotalCount: {migrationResult?.TotalCount}\n" +
                                 $"  Warnings: {string.Join(", ", migrationResult?.Warnings ?? [])}\n" +
                                 $"  Errors: {string.Join(", ", migrationResult?.Errors ?? [])}\n\n";
                File.AppendAllText(logPath, logContent);
            }
            catch (Exception ex)
            {
                // 마이그레이션 실패 시 로그 파일에 기록
                var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "migration_error.log");
                File.WriteAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Migration failed:\n{ex}\n\nStack trace:\n{ex.StackTrace}");
                _log.Error($"Migration failed: {ex.Message}");
            }
            finally
            {
                // LoadingOverlay만 숨기고, Blur는 마이그레이션 결과 팝업 표시 여부에 따라 처리
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }

        try
        {
            // DB에서 퀘스트 데이터 로드
            if (await progressService.InitializeFromDbAsync())
            {
                tasks = progressService.AllTasks.ToList();
                _log.Debug($"Loaded {tasks.Count} quests from DB");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load quests: {ex.Message}");
        }

        // Load hideout data from DB
        var hideoutDbService = HideoutDbService.Instance;
        var hideoutLoaded = await hideoutDbService.LoadStationsAsync();
        _log.Debug($"Hideout DB loaded: {hideoutLoaded}, StationCount: {hideoutDbService.StationCount}");
        if (hideoutLoaded)
        {
            _hideoutModules = hideoutDbService.AllStations.ToList();
            _log.Debug($"Hideout modules count: {_hideoutModules.Count}");
        }
        else
        {
            _log.Warning($"Hideout loading failed. DB exists: {hideoutDbService.DatabaseExists}");
        }

        _log.Debug($"Tasks count: {tasks?.Count ?? 0}");

        // Log diagnostic info to file
        try
        {
            var logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup_log.txt");
            var logContent = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Startup Diagnostics\n" +
                             $"  Hideout DB Loaded: {hideoutLoaded}\n" +
                             $"  Hideout Stations: {hideoutDbService.StationCount}\n" +
                             $"  Hideout Modules: {_hideoutModules?.Count ?? 0}\n" +
                             $"  Tasks Count: {tasks?.Count ?? 0}\n" +
                             $"  Database Path: {hideoutDbService.DatabaseExists}\n\n";
            System.IO.File.AppendAllText(logPath, logContent);
        }
        catch { /* Ignore logging errors */ }

        if (tasks != null && tasks.Count > 0)
        {
            // Initialize quest graph service for dependency tracking
            QuestGraphService.Instance.Initialize(tasks);

            // Initialize hideout progress service
            if (_hideoutModules != null && _hideoutModules.Count > 0)
            {
                _hideoutProgressService.Initialize(_hideoutModules);
            }

            // Check if pages already exist (refresh scenario)
            if (_questListPage != null)
            {
                // Reload data in existing pages to pick up new translations
                await _questListPage.ReloadDataAsync();
            }
            else
            {
                // Create pages for the first time
                _questListPage = new QuestListPage();
            }

            // Debug: Show hideout module status
            _log.Debug($"Creating HideoutPage: modules={_hideoutModules?.Count ?? 0}");
            _hideoutPage = _hideoutModules != null && _hideoutModules.Count > 0
                ? new HideoutPage()
                : null;
            _log.Debug($"HideoutPage created: {_hideoutPage != null}");
            _itemsPage = new ItemsPage();
            _collectorPage = new CollectorPage();

            // Show tab area with Quests selected
            TxtWelcome.Visibility = Visibility.Collapsed;
            TabContentArea.Visibility = Visibility.Visible;
            TabQuests.IsChecked = true;
            PageContent.Content = _questListPage;
        }
        else
        {
            TxtWelcome.Text = "No quest data available. Please refresh data.";
            TxtWelcome.Visibility = Visibility.Visible;
            TabContentArea.Visibility = Visibility.Collapsed;
        }

        // 마이그레이션 결과가 있으면 팝업 표시 (자동 마이그레이션 후)
        if (migrationResult != null && migrationResult.TotalCount > 0)
        {
            ShowMigrationResultDialog(migrationResult);
        }
        else if (needsMigration)
        {
            // 마이그레이션이 필요했지만 결과가 없는 경우 Blur 해제
            var blurAnimation = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200));
            BlurEffect.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurAnimation);
        }
    }

    /// <summary>
    /// Handle tab selection change
    /// </summary>
    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            // 시작 로딩 중의 탭 클릭은 라디오 버튼만 체크되고 페이지 전환은 무시된다.
            // 그대로 return만 하면 이미 체크된 탭을 다시 눌러도 Checked가 재발화하지 않아
            // "죽은 탭"이 된다. 클릭을 기억해 두었다가 로딩이 끝나는 즉시 반영한다.
            _pendingTabDuringLoad = sender;
            return;
        }

        // A tab switch navigates away from the header context, so dismiss the profile
        // drawer; otherwise the centered popover keeps floating over the newly
        // selected tab's content. Matches the close-on-Settings / close-on-full-screen
        // policy. Null check: TabQuests's IsChecked="True" fires this handler during
        // InitializeComponent, before ProfileDrawer (declared later in the XAML) exists.
        if (ProfileDrawer != null)
        {
            CloseProfileDrawer();
        }

        if (sender == TabQuests && _questListPage != null)
        {
            PageContent.Content = _questListPage;
        }
        else if (sender == TabHideout)
        {
            if (_hideoutPage != null)
            {
                PageContent.Content = _hideoutPage;
            }
            else
            {
                // Hideout data not available, show message or load it
                PageContent.Content = new TextBlock
                {
                    Text = "Hideout data not available. Please refresh data.",
                    Foreground = FindResource("TextSecondaryBrush") as System.Windows.Media.Brush,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
        }
        else if (sender == TabItems && _itemsPage != null)
        {
            PageContent.Content = _itemsPage;
        }
        else if (sender == TabCollector && _collectorPage != null)
        {
            PageContent.Content = _collectorPage;
        }
        else if (sender == TabMap)
        {
            _mapTrackerPage ??= new MapPage();
            PageContent.Content = _mapTrackerPage;
        }
    }

    #region App Profile

    /// <summary>
    /// Marker written to a selector RadioButton's Tag for the life of an automatic-transition
    /// cue. Must match the AutomaticCue MultiTrigger condition in MainWindow.xaml.
    /// </summary>
    private const string AutomaticCueTag = "AutomaticCue";

    private (AppProfile Profile, RadioButton Radio, MenuItem Item)[]? _profileControls;

    /// <summary>
    /// The three profiles paired with both selector variants' controls, so the profile-to-control
    /// mapping exists once instead of being re-spelled in every render and cue method.
    /// </summary>
    private (AppProfile Profile, RadioButton Radio, MenuItem Item)[] ProfileControls =>
        _profileControls ??=
        [
            (AppProfile.PvpZone, BtnPvpZone, MenuPvpZone),
            (AppProfile.PveZone, BtnPveZone, MenuPveZone),
            (AppProfile.PvpSeason, BtnPvpSeason, MenuPvpSeason),
        ];

    private void BtnPvpZone_Click(object sender, RoutedEventArgs e)
        => SelectProfileManually(AppProfile.PvpZone);

    private void BtnPveZone_Click(object sender, RoutedEventArgs e)
        => SelectProfileManually(AppProfile.PveZone);

    private void BtnPvpSeason_Click(object sender, RoutedEventArgs e)
        => SelectProfileManually(AppProfile.PvpSeason);

    // SelectionItemPattern is the native UI Automation pattern for WPF RadioButton.
    // Checked covers selection through accessibility clients; Click also covers an
    // explicit click on the already-selected profile.
    private void BtnPvpZone_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isUpdatingProfileUI)
            SelectProfileManually(AppProfile.PvpZone);
    }

    private void BtnPveZone_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isUpdatingProfileUI)
            SelectProfileManually(AppProfile.PveZone);
    }

    private void BtnPvpSeason_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isUpdatingProfileUI)
            SelectProfileManually(AppProfile.PvpSeason);
    }

    private void BtnActiveProfileMenu_Click(object sender, RoutedEventArgs e)
    {
        ActiveProfileContextMenu.PlacementTarget = BtnActiveProfileMenu;
        ActiveProfileContextMenu.IsOpen = true;
    }

    private void MenuPvpZone_Click(object sender, RoutedEventArgs e)
        => SelectProfileManually(AppProfile.PvpZone);

    private void MenuPveZone_Click(object sender, RoutedEventArgs e)
        => SelectProfileManually(AppProfile.PveZone);

    private void MenuPvpSeason_Click(object sender, RoutedEventArgs e)
        => SelectProfileManually(AppProfile.PvpSeason);

    private void SelectProfileManually(AppProfile profile)
    {
        // WPF raises Checked before Click on the same mouse press (ToggleButton.OnClick calls
        // OnToggle first), and both are wired here, so one click arrives twice. Selecting the
        // already-active manual profile is a no-op apart from re-syncing the controls, which
        // is still needed because a checkable MenuItem toggles its own IsChecked on click.
        var service = ProfileService.Instance;
        if (service.ActiveProfile == profile && !service.IsAutoDetected)
        {
            UpdateProfileUI(profile);
            return;
        }

        ClearAutomaticProfileTransitionCue();
        service.SetActiveProfile(profile);

        // Re-sync when the already-active manual profile was selected and no service
        // event was necessary.
        UpdateProfileUI(service.ActiveProfile);
    }

    private void OnActiveProfileChanged(object? sender, ProfileChangedEventArgs args)
    {
        Dispatcher.InvokeAsync(() =>
        {
            UpdateProfileUI(args.Profile);

            // Only cue and announce a real destination change. Repeated identical evidence
            // (EFT re-logs the session mode on every profile-screen visit, and the startup
            // scan replays the last line) flips only the provenance flag, and announcing
            // "Profile changed to X" for that tells the user something untrue.
            if (args.IsAutoDetected && args.ProfileChanged)
            {
                ShowAutomaticProfileTransitionCue(args.Profile);
            }
        });
    }

    private void ShowAutomaticProfileTransitionCue(AppProfile profile)
    {
        ClearAutomaticProfileTransitionCue();

        var selectedButton = GetProfileRadioButton(profile);
        selectedButton.Tag = AutomaticCueTag;

        // The compact trigger's bolt is swapped in only for the life of the cue; the wide
        // variant's swap is declarative (the AutomaticCue MultiTrigger).
        CompactProfileCheck.Visibility = Visibility.Hidden;
        CompactProfileAutomatic.Visibility = Visibility.Visible;
        CompactProfileAutomatic.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0.35,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(250),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2)
        });

        var profileName = GetProfileDisplayName(profile);
        var announcement = string.Format(_loc.HeaderProfileChangedFromLogsFormat, profileName);

        // A localized text alternative for the bolt, for the duration of the cue only. In the
        // wide (default) layout the bolt is otherwise the ONLY indication that the app, not the
        // user, changed the destination, and a glyph carries no accessible name.
        AutomationProperties.SetHelpText(selectedButton, _loc.HeaderProfileSourceAutomatic);

        AnnounceProfileTransition(announcement);

        _profileTransitionCueTimer.Start();
    }

    /// <summary>
    /// Publishes <paramref name="announcement"/> on the polite live region.
    /// <para>
    /// Setting Text alone is not enough: WPF raises only a Name property-changed event for it,
    /// and AutomationProperties.LiveSetting is purely declarative, so a screen reader is never
    /// told to speak. The provider has to raise LiveRegionChanged explicitly. Verified against
    /// a UIA client: text-only assignment delivers zero LiveRegionChanged events.
    /// </para>
    /// </summary>
    private void AnnounceProfileTransition(string announcement)
    {
        if (string.IsNullOrEmpty(announcement)) return;

        TxtProfileTransitionAnnouncement.Text = announcement;

        var peer = UIElementAutomationPeer.FromElement(TxtProfileTransitionAnnouncement)
                   ?? UIElementAutomationPeer.CreatePeerForElement(TxtProfileTransitionAnnouncement);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ProfileTransitionCueTimer_Tick(object? sender, EventArgs e)
        => ClearAutomaticProfileTransitionCue();

    private void ClearAutomaticProfileTransitionCue()
    {
        _profileTransitionCueTimer.Stop();
        CompactProfileAutomatic.BeginAnimation(OpacityProperty, null);

        // Restore the neutral, source-free resting state. PRD R6: Manual/Auto is transient
        // input feedback, not lasting selector state, so nothing here may re-read the
        // service's provenance flag and re-apply a durable "auto-selected" marker.
        foreach (var (_, radio, _) in ProfileControls)
        {
            radio.Tag = null;
            AutomationProperties.SetHelpText(radio, string.Empty);
        }

        CompactProfileCheck.Visibility = Visibility.Visible;
        CompactProfileAutomatic.Visibility = Visibility.Hidden;
        TxtProfileTransitionAnnouncement.Text = string.Empty;
    }

    // Delegates to LocalizationService so the selector, the transition announcement and the sync
    // summary cannot name the same profile differently.
    private string GetProfileDisplayName(AppProfile profile) => _loc.ProfileName(profile);

    private RadioButton GetProfileRadioButton(AppProfile profile)
    {
        foreach (var (candidate, radio, _) in ProfileControls)
        {
            if (candidate == profile) return radio;
        }
        throw new ArgumentOutOfRangeException(
            nameof(profile), profile, "No selector control is defined for this profile.");
    }

    /// <summary>
    /// Update the wide radio group and compact checked menu from one active profile.
    /// </summary>
    private void UpdateProfileUI(AppProfile profile)
    {
        _isUpdatingProfileUI = true;
        try
        {
            foreach (var (candidate, radio, item) in ProfileControls)
            {
                var isSelected = candidate == profile;
                radio.IsChecked = isSelected;
                item.IsChecked = isSelected;
                var status = isSelected ? _loc.HeaderProfileSelected : _loc.HeaderProfileUnselected;
                AutomationProperties.SetItemStatus(radio, status);
                AutomationProperties.SetItemStatus(item, status);
            }

            var profileName = GetProfileDisplayName(profile);
            TxtCompactActiveProfile.Text = profileName;
            AutomationProperties.SetName(
                BtnActiveProfileMenu, $"{_loc.HeaderActiveProfile}: {profileName}");
        }
        finally
        {
            _isUpdatingProfileUI = false;
        }
    }

    #endregion

    #region Settings Echo Suppression

    /// <summary>
    /// Marks the profile drawer's controls as being written BY the settings service for the life
    /// of the returned scope, so any handler those writes wake writes nothing back.
    /// <para>
    /// Assigning <c>CheckBox.IsChecked</c> raises Checked/Unchecked exactly as a click does, and
    /// <see cref="ChkEdition_Changed"/> cannot tell the two apart by itself. Without this guard a
    /// settings load that could not read the store published that profile's DEFAULTS, the
    /// HasEodEditionChanged(false) that follows unchecked the box, and the echo wrote False over
    /// the player's stored Edge of Darkness flag - permanently, and with no player action at all.
    /// </para>
    /// <para>
    /// <c>_isLoading</c> is not this guard: it is raised only for the startup pass in
    /// Window_Loaded, while these events also arrive long afterwards (profile switch, profile
    /// reset, settings self-heal). Every Update*UI method below opens this scope, so a control
    /// added to one of them is covered without anyone remembering to. The scope RESTORES the
    /// previous value instead of clearing it, so a nested update cannot lower a guard its caller
    /// raised.
    /// </para>
    /// </summary>
    private SettingsEchoSuppression SuppressSettingsEcho() => new(this);

    private readonly struct SettingsEchoSuppression : IDisposable
    {
        private readonly MainWindow _window;
        private readonly bool _wasUpdating;

        public SettingsEchoSuppression(MainWindow window)
        {
            _window = window;
            _wasUpdating = window._isUpdatingSettingsUI;
            window._isUpdatingSettingsUI = true;
        }

        public void Dispose() => _window._isUpdatingSettingsUI = _wasUpdating;
    }

    #endregion

    #region Player Level

    /// <summary>
    /// Update player level UI: the drawer stepper and the title-bar profile chip summary.
    /// </summary>
    private void UpdatePlayerLevelUI()
    {
        using var echoGuard = SuppressSettingsEcho();

        var level = _settingsService.PlayerLevel;
        TxtPlayerLevel.Text = level.ToString();
        TxtProfileChipLevel.Text = $"{_loc.HeaderLevelShort} {level}";

        // Disable buttons at min/max level
        BtnLevelDown.IsEnabled = level > SettingsService.MinPlayerLevel;
        BtnLevelUp.IsEnabled = level < SettingsService.MaxPlayerLevel;
    }

    /// <summary>
    /// Handle player level decrease
    /// </summary>
    private void BtnLevelDown_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.PlayerLevel--;
    }

    /// <summary>
    /// Handle player level increase
    /// </summary>
    private void BtnLevelUp_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.PlayerLevel++;
    }

    /// <summary>
    /// Handle player level change from settings service.
    /// <para>
    /// Updates this window's own controls and nothing else. The quest list subscribes to the same
    /// seven settings events itself and collapses the whole burst into ONE refresh; pushing a
    /// refresh from here as well ran that full pass once per event, off-tab included.
    /// </para>
    /// </summary>
    private void OnPlayerLevelChanged(object? sender, int newLevel)
    {
        Dispatcher.Invoke(UpdatePlayerLevelUI);
    }

    /// <summary>
    /// Only allow numeric input for player level
    /// </summary>
    private void TxtPlayerLevel_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !int.TryParse(e.Text, out _);
    }

    /// <summary>
    /// Apply level when losing focus
    /// </summary>
    private void TxtPlayerLevel_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyPlayerLevelFromTextBox();
    }

    /// <summary>
    /// Apply level when pressing Enter
    /// </summary>
    private void TxtPlayerLevel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyPlayerLevelFromTextBox();
            Keyboard.ClearFocus();
        }
    }

    /// <summary>
    /// Parse and apply player level from TextBox input
    /// </summary>
    private void ApplyPlayerLevelFromTextBox()
    {
        if (int.TryParse(TxtPlayerLevel.Text, out var level))
        {
            // Clamp to valid range
            level = Math.Clamp(level, SettingsService.MinPlayerLevel, SettingsService.MaxPlayerLevel);

            // Only a value that DIFFERS from what the service reports is a player edit. This box
            // can be losing focus over text the service itself wrote into it (a profile switch or
            // a failed load repaints the drawer while the caret sits here), and the echo guard is
            // already down by then, so equality is what tells the two apart.
            if (level != _settingsService.PlayerLevel)
            {
                _settingsService.PlayerLevel = level;
            }
        }

        // Always repaint from the service: unparsable text ("abc") must not be left in the box, and
        // an out-of-range entry ("999") must show the clamped value that was actually applied - the
        // setter raises no event when the clamp lands on the value already held.
        UpdatePlayerLevelUI();
    }

    #endregion

    #region Scav Rep

    /// <summary>
    /// Update Scav Rep UI
    /// </summary>
    private void UpdateScavRepUI()
    {
        using var echoGuard = SuppressSettingsEcho();

        var scavRep = _settingsService.ScavRep;
        TxtScavRep.Text = scavRep.ToString("0.0");

        // Disable buttons at min/max Scav Rep
        BtnScavRepDown.IsEnabled = scavRep > SettingsService.MinScavRep;
        BtnScavRepUp.IsEnabled = scavRep < SettingsService.MaxScavRep;
    }

    /// <summary>
    /// Handle Scav Rep decrease
    /// </summary>
    private void BtnScavRepDown_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.ScavRep -= SettingsService.ScavRepStep;
    }

    /// <summary>
    /// Handle Scav Rep increase
    /// </summary>
    private void BtnScavRepUp_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.ScavRep += SettingsService.ScavRepStep;
    }

    /// <summary>
    /// Handle Scav Rep change from settings service. Window controls only; the quest list refreshes
    /// itself once per burst (see <see cref="OnPlayerLevelChanged"/>).
    /// </summary>
    private void OnScavRepChanged(object? sender, double newScavRep)
    {
        Dispatcher.Invoke(UpdateScavRepUI);
    }

    /// <summary>
    /// Allow numeric input including decimal point and minus sign for Scav Rep
    /// </summary>
    private void TxtScavRep_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        var textBox = sender as TextBox;
        var currentText = textBox?.Text ?? "";
        var newChar = e.Text;

        // Allow minus sign only at the beginning
        if (newChar == "-")
        {
            e.Handled = currentText.Contains('-') || (textBox?.CaretIndex ?? 0) != 0;
            return;
        }

        // Allow decimal point only once
        if (newChar == "." || newChar == ",")
        {
            e.Handled = currentText.Contains('.') || currentText.Contains(',');
            return;
        }

        // Allow digits
        e.Handled = !char.IsDigit(newChar[0]);
    }

    /// <summary>
    /// Apply Scav Rep when losing focus
    /// </summary>
    private void TxtScavRep_LostFocus(object sender, RoutedEventArgs e)
    {
        ApplyScavRepFromTextBox();
    }

    /// <summary>
    /// Apply Scav Rep when pressing Enter
    /// </summary>
    private void TxtScavRep_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyScavRepFromTextBox();
            Keyboard.ClearFocus();
        }
    }

    /// <summary>
    /// Parse and apply Scav Rep from TextBox input
    /// </summary>
    private void ApplyScavRepFromTextBox()
    {
        var text = TxtScavRep.Text.Replace(',', '.');
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var scavRep))
        {
            // Clamp to valid range and round to 1 decimal place
            scavRep = Math.Round(Math.Clamp(scavRep, SettingsService.MinScavRep, SettingsService.MaxScavRep), 1);

            // Same reasoning as the player level box: a value equal to the one the service reports
            // is the service's own repaint coming back, not an edit. Half a step is the tolerance,
            // since every real edit moves by at least one step.
            if (Math.Abs(scavRep - _settingsService.ScavRep) >= SettingsService.ScavRepStep / 2)
            {
                _settingsService.ScavRep = scavRep;
            }
        }

        // Always repaint from the service, for the invalid and the clamped entry alike.
        UpdateScavRepUI();
    }

    #endregion

    #region DSP Decode Count

    /// <summary>
    /// Update DSP Decode Count UI - highlight the selected button
    /// </summary>
    private void UpdateDspDecodeUI()
    {
        using var echoGuard = SuppressSettingsEcho();

        var dspCount = _settingsService.DspDecodeCount;

        // Reset all buttons to default style
        var buttons = new[] { BtnDsp0, BtnDsp1, BtnDsp2, BtnDsp3 };
        foreach (var btn in buttons)
        {
            btn.Background = (Brush)FindResource("BackgroundMediumBrush");
            btn.Foreground = (Brush)FindResource("TextPrimaryBrush");
        }

        // Highlight the selected button
        var selectedBtn = buttons[dspCount];
        selectedBtn.Background = (Brush)FindResource("AccentBrush");
        selectedBtn.Foreground = (Brush)FindResource("BackgroundDarkBrush");
    }

    /// <summary>
    /// Handle DSP Decode button click
    /// </summary>
    private void BtnDsp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out var count))
        {
            _settingsService.DspDecodeCount = count;
        }
    }

    /// <summary>
    /// Handle DSP Decode Count change from settings service. Window controls only; the quest list
    /// refreshes itself once per burst (see <see cref="OnPlayerLevelChanged"/>).
    /// </summary>
    private void OnDspDecodeCountChanged(object? sender, int newCount)
    {
        Dispatcher.Invoke(UpdateDspDecodeUI);
    }

    #endregion

    #region Edition Settings

    /// <summary>
    /// Update Edition UI checkboxes. Both assignments raise Checked/Unchecked, which is why the
    /// echo guard is not optional here (see <see cref="SuppressSettingsEcho"/>).
    /// </summary>
    private void UpdateEditionUI()
    {
        using var echoGuard = SuppressSettingsEcho();

        ChkEodEdition.IsChecked = _settingsService.HasEodEdition;
        ChkUnheardEdition.IsChecked = _settingsService.HasUnheardEdition;
    }

    /// <summary>
    /// Handle edition checkbox change. Writes back only for a real player toggle: an assignment
    /// made by <see cref="UpdateEditionUI"/> raises this same event, and treating that echo as an
    /// edit overwrote the stored flag with whatever the service had just published.
    /// </summary>
    private void ChkEdition_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _isUpdatingSettingsUI) return;

        if (sender == ChkEodEdition)
        {
            _settingsService.HasEodEdition = ChkEodEdition.IsChecked == true;
        }
        else if (sender == ChkUnheardEdition)
        {
            _settingsService.HasUnheardEdition = ChkUnheardEdition.IsChecked == true;
        }
    }

    /// <summary>
    /// Handle edition change from settings service. Window controls only; the quest list refreshes
    /// itself once per burst (see <see cref="OnPlayerLevelChanged"/>).
    /// </summary>
    private void OnEditionChanged(object? sender, bool value)
    {
        Dispatcher.Invoke(UpdateEditionUI);
    }

    #endregion

    #region Prestige Level

    /// <summary>
    /// Update Prestige Level UI
    /// </summary>
    private void UpdatePrestigeLevelUI()
    {
        using var echoGuard = SuppressSettingsEcho();

        var prestigeLevel = _settingsService.PrestigeLevel;
        TxtPrestigeLevel.Text = prestigeLevel.ToString();

        // Disable buttons at min/max prestige level
        BtnPrestigeDown.IsEnabled = prestigeLevel > SettingsService.MinPrestigeLevel;
        BtnPrestigeUp.IsEnabled = prestigeLevel < SettingsService.MaxPrestigeLevel;
    }

    /// <summary>
    /// Handle prestige level decrease
    /// </summary>
    private void BtnPrestigeDown_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.PrestigeLevel--;
    }

    /// <summary>
    /// Handle prestige level increase
    /// </summary>
    private void BtnPrestigeUp_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.PrestigeLevel++;
    }

    /// <summary>
    /// Handle prestige level change from settings service. Window controls only; the quest list
    /// refreshes itself once per burst (see <see cref="OnPlayerLevelChanged"/>).
    /// </summary>
    private void OnPrestigeLevelChanged(object? sender, int newLevel)
    {
        Dispatcher.Invoke(UpdatePrestigeLevelUI);
    }

    #endregion

    /// <summary>
    /// Open Buy me a coffee page
    /// </summary>
    private void BtnCoffee_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://buymeacoffee.com/zeliperstap",
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore errors opening browser
        }
    }

    /// <summary>
    /// Opens the complete-profile-reset dialog (feature-complete-profile-reset.md). The target
    /// is the profile selected at this moment, captured ONCE into a local and carried through
    /// the dialog and the reset itself: an automatic profile switch while the dialog is open
    /// must not move the reset (PRD R1). The reset work is ProfileResetService's; this window
    /// only opens the dialog and refreshes its pages afterwards.
    /// </summary>
    private async void BtnResetProgress_Click(object sender, RoutedEventArgs e)
    {
        // The selection alone, read under ProfileService's own gate. Not CurrentTransition:
        // that pair exists for callers who carry the revision into an async load so a losing
        // load can be discarded, and nothing here reloads against a revision.
        var target = ProfileService.Instance.ActiveProfile;

        var dialog = new ProfileResetDialog(
            target, () => ProfileResetService.Instance.ResetAsync(target))
        {
            Owner = this
        };
        dialog.ShowDialog();

        if (dialog.ResetSucceeded)
        {
            // The services already published their cleared state and raised their change
            // events; this reloads the quest list page itself, matching what every other
            // whole-profile transition does.
            await LoadAndShowQuestListAsync();
        }
    }

    #region Profile Drawer

    private const string ChevronDownGlyph = "\uE70D";
    private const string ChevronUpGlyph = "\uE70E";

    /// <summary>
    /// Open-state is derived from the drawer's actual Visibility (compute-don't-store)
    /// so no tracking flag can fall out of sync with the control.
    /// </summary>
    private bool IsProfileDrawerOpen => ProfileDrawer.Visibility == Visibility.Visible;

    /// <summary>
    /// Toggle profile drawer visibility
    /// </summary>
    private void BtnProfile_Click(object sender, RoutedEventArgs e)
    {
        var open = !IsProfileDrawerOpen;
        ProfileDrawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        TxtProfileChipChevron.Text = open ? ChevronUpGlyph : ChevronDownGlyph;
    }

    /// <summary>
    /// Close the profile drawer (if open) and reset the chip chevron.
    /// </summary>
    private void CloseProfileDrawer()
    {
        ProfileDrawer.Visibility = Visibility.Collapsed;
        TxtProfileChipChevron.Text = ChevronDownGlyph;
    }

    #endregion

    #region Settings

    /// <summary>
    /// Open settings dialog
    /// </summary>
    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsOverlay();
    }

    /// <summary>
    /// Show settings overlay
    /// </summary>
    private void ShowSettingsOverlay()
    {
        // The drawer would otherwise stay open (with a stale up-chevron) underneath
        // the overlay scrim and still be open when Settings closes.
        CloseProfileDrawer();

        UpdateSettingsUI();
        SettingsOverlay.Visibility = Visibility.Visible;

        var blurAnimation = new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(200));
        BlurEffect.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurAnimation);
    }

    /// <summary>
    /// Hide settings overlay
    /// </summary>
    private void HideSettingsOverlay()
    {
        var blurAnimation = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200));
        blurAnimation.Completed += (s, e) =>
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
        };
        BlurEffect.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurAnimation);
    }

    /// <summary>
    /// Update settings UI with current values
    /// </summary>
    private void UpdateSettingsUI()
    {
        var logPath = _settingsService.LogFolderPath;
        var isValid = _settingsService.IsLogFolderValid;
        var method = _settingsService.DetectionMethod;

        // Update localized text
        UpdateSettingsLocalizedText();

        // Update quest sync section
        UpdateQuestSyncUI();

        // Update cache size display
        UpdateCacheSizeDisplay();

        // Update font size display
        UpdateFontSizeDisplay();

        // Update path display
        if (!string.IsNullOrEmpty(logPath))
        {
            TxtCurrentLogPath.Text = logPath;
            TxtCurrentLogPath.Foreground = (Brush)FindResource("TextPrimaryBrush");
        }
        else
        {
            TxtCurrentLogPath.Text = _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "설정되지 않음",
                AppLanguage.JA => "未設定",
                _ => "Not configured"
            };
            TxtCurrentLogPath.Foreground = (Brush)FindResource("TextSecondaryBrush");
        }

        // Update detection method
        if (!string.IsNullOrEmpty(method))
        {
            TxtDetectionMethod.Text = $"({method})";
        }
        else
        {
            TxtDetectionMethod.Text = "";
        }

        // Update status indicator
        if (isValid)
        {
            LogFolderStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
            TxtLogFolderStatus.Text = _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "유효한 경로",
                AppLanguage.JA => "有効なパス",
                _ => "Valid path"
            };
        }
        else
        {
            LogFolderStatusIndicator.Fill = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
            TxtLogFolderStatus.Text = _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "유효하지 않은 경로",
                AppLanguage.JA => "無効なパス",
                _ => "Invalid path"
            };
        }
    }

    /// <summary>
    /// Update settings dialog localized text
    /// </summary>
    private void UpdateSettingsLocalizedText()
    {
        // Pre-existing rows, migrated to named _loc properties so this whole method
        // uses one localization idiom and the completeness test guards every string.
        TxtSettingsTitle.Text = _loc.Settings;
        TxtLogFolderLabel.Text = _loc.SettingsLogFolderLabel;
        TxtLogFolderDesc.Text = _loc.SettingsLogFolderDesc;
        BtnAutoDetect.Content = _loc.SettingsAutoDetectButton;
        BtnBrowseLogFolder.Content = _loc.SettingsBrowseButton;
        BtnResetLogFolder.Content = _loc.SettingsResetLogFolderButton;

        // Sections added by the top-bar redesign (named _loc properties, no inline switches)
        TxtSettingsUpdateLabel.Text = _loc.SettingsUpdateLabel;
        TxtSettingsCheckUpdateLabel.Text = _loc.SettingsCheckUpdateButton;
        TxtLanguageLabel.Text = _loc.SettingsLanguageLabel;
        TxtSupportLabel.Text = _loc.SettingsSupportLabel;
        TxtSupportDesc.Text = _loc.SettingsSupportDesc;
        TxtSupportButtonLabel.Text = _loc.SettingsSupportButton;
        TxtDangerZoneLabel.Text = _loc.SettingsDangerZoneLabel;
        TxtResetProgressDesc.Text = _loc.SettingsResetProgressDesc;
        BtnResetProgress.Content = _loc.SettingsResetProgressButton;

        // UIA names for the buttons whose visible label lives in panel content
        // (string-Content buttons like BtnResetProgress get their Name for free)
        AutomationProperties.SetName(BtnCoffee, _loc.SettingsSupportButton);
        AutomationProperties.SetName(BtnCheckUpdateSettings, _loc.SettingsCheckUpdateButton);

        UpdateSettingsUpdateSectionUI();
    }

    /// <summary>
    /// Close settings overlay when clicking outside the dialog
    /// </summary>
    private void SettingsOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == SettingsOverlay)
        {
            HideSettingsOverlay();
        }
    }

    /// <summary>
    /// Close settings button click
    /// </summary>
    private void BtnCloseSettings_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsOverlay();
    }

    /// <summary>
    /// Auto detect Tarkov log folder
    /// </summary>
    private void BtnAutoDetect_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.ResetLogFolderPath();
        var detectedPath = _settingsService.AutoDetectLogFolder();

        if (!string.IsNullOrEmpty(detectedPath))
        {
            _settingsService.LogFolderPath = detectedPath;
            RestartLogMonitoring();
            UpdateSettingsUI();

            var message = _loc.CurrentLanguage switch
            {
                AppLanguage.KO => $"로그 폴더를 찾았습니다:\n{detectedPath}",
                AppLanguage.JA => $"ログフォルダが見つかりました:\n{detectedPath}",
                _ => $"Log folder detected:\n{detectedPath}"
            };

            MessageBox.Show(message,
                _loc.CurrentLanguage switch { AppLanguage.KO => "자동 감지", AppLanguage.JA => "自動検出", _ => "Auto Detect" },
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        else
        {
            UpdateSettingsUI();

            var message = _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "Tarkov 설치를 찾을 수 없습니다.\n수동으로 로그 폴더를 선택해주세요.",
                AppLanguage.JA => "Tarkovのインストールが見つかりませんでした。\n手動でログフォルダを選択してください。",
                _ => "Could not detect Tarkov installation.\nPlease select the log folder manually."
            };

            MessageBox.Show(message,
                _loc.CurrentLanguage switch { AppLanguage.KO => "자동 감지 실패", AppLanguage.JA => "自動検出失敗", _ => "Auto Detect Failed" },
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Browse for log folder
    /// </summary>
    private void BtnBrowseLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "Tarkov Logs 폴더 선택",
                AppLanguage.JA => "Tarkov Logsフォルダを選択",
                _ => "Select Tarkov Logs Folder"
            }
        };

        if (dialog.ShowDialog() == true)
        {
            var selectedPath = dialog.FolderName;

            // Check if it looks like a valid logs folder
            if (Directory.Exists(selectedPath))
            {
                _settingsService.LogFolderPath = selectedPath;
                RestartLogMonitoring();
                UpdateSettingsUI();
            }
        }
    }

    /// <summary>
    /// Reset log folder setting
    /// </summary>
    private void BtnResetLogFolder_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.ResetLogFolderPath();
        RestartLogMonitoring();
        UpdateSettingsUI();
    }

    #endregion

    #region Cross-Tab Navigation

    /// <summary>
    /// Navigate to Quests tab and select a specific quest
    /// </summary>
    public void NavigateToQuest(string questNormalizedName)
    {
        // Switch to Quests tab
        TabQuests.IsChecked = true;
        PageContent.Content = _questListPage;

        // Request quest selection
        _questListPage?.SelectQuest(questNormalizedName);
    }

    /// <summary>
    /// Navigate to Items tab and select a specific item by its ID
    /// </summary>
    public void NavigateToItem(string itemId)
    {
        // Switch to Items tab
        TabItems.IsChecked = true;
        PageContent.Content = _itemsPage;

        // Request item selection by ID
        _itemsPage?.SelectItem(itemId);
    }

    /// <summary>
    /// Navigate to Hideout tab and select a specific module
    /// </summary>
    public void NavigateToHideout(string stationId)
    {
        // Switch to Hideout tab
        TabHideout.IsChecked = true;
        PageContent.Content = _hideoutPage;

        // Request module selection
        _hideoutPage?.SelectModule(stationId);
    }

    #endregion

    #region Quest Log Sync

    /// <summary>
    /// Update quest sync UI elements
    /// </summary>
    private void UpdateQuestSyncUI()
    {
        // Update localized text
        TxtQuestSyncLabel.Text = _loc.CurrentLanguage switch
        {
            AppLanguage.KO => "퀘스트 로그 동기화",
            AppLanguage.JA => "クエストログ同期",
            _ => "Quest Log Sync"
        };

        TxtQuestSyncDesc.Text = _loc.CurrentLanguage switch
        {
            AppLanguage.KO => "게임 로그 파일에서 퀘스트 진행 상태를 동기화합니다. Tarkov 로그를 분석하여 완료된 퀘스트를 업데이트합니다.",
            AppLanguage.JA => "ゲームログファイルからクエストの進行状況を同期します。Tarkovログを分析して完了したクエストを更新します。",
            _ => "Synchronize quest progress from game log files. This will analyze your Tarkov logs and update completed quests."
        };

        BtnSyncQuest.Content = _loc.CurrentLanguage switch
        {
            AppLanguage.KO => "퀘스트 동기화",
            AppLanguage.JA => "クエスト同期",
            _ => "Sync Quest"
        };

        // Update monitoring status
        var isMonitoring = _logSyncService.IsMonitoring;
        MonitoringStatusIndicator.Fill = isMonitoring
            ? new SolidColorBrush(Color.FromRgb(76, 175, 80)) // Green
            : new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red

        TxtMonitoringStatus.Text = isMonitoring
            ? _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "모니터링 중",
                AppLanguage.JA => "監視中",
                _ => "Monitoring"
            }
            : _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "모니터링 안함",
                AppLanguage.JA => "監視していない",
                _ => "Not monitoring"
            };

        BtnToggleMonitoring.Content = isMonitoring
            ? _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "모니터링 중지",
                AppLanguage.JA => "監視停止",
                _ => "Stop Monitoring"
            }
            : _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "모니터링 시작",
                AppLanguage.JA => "監視開始",
                _ => "Start Monitoring"
            };

        // Disable sync button if log folder is not valid
        BtnSyncQuest.IsEnabled = _settingsService.IsLogFolderValid;
        BtnToggleMonitoring.IsEnabled = _settingsService.IsLogFolderValid;
    }

    /// <summary>
    /// Sync quest progress from logs
    /// </summary>
    private void BtnSyncQuest_Click(object sender, RoutedEventArgs e)
    {
        var logPath = _settingsService.LogFolderPath;
        if (string.IsNullOrEmpty(logPath) || !Directory.Exists(logPath))
        {
            MessageBox.Show(
                _loc.CurrentLanguage switch
                {
                    AppLanguage.KO => "로그 폴더가 설정되지 않았거나 존재하지 않습니다.",
                    AppLanguage.JA => "ログフォルダが設定されていないか、存在しません。",
                    _ => "Log folder is not configured or does not exist."
                },
                _loc.CurrentLanguage switch { AppLanguage.KO => "오류", AppLanguage.JA => "エラー", _ => "Error" },
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Hide settings overlay
        HideSettingsOverlay();

        // Show wipe warning if not hidden
        if (!_settingsService.HideWipeWarning)
        {
            if (!WipeWarningDialog.ShowWarning(logPath, this))
            {
                return; // User cancelled
            }
        }

        // Proceed with sync
        PerformQuestSync(logPath);
    }

    /// <summary>
    /// Perform the actual quest sync
    /// </summary>
    private async void PerformQuestSync(string logPath)
    {
        ShowLoadingOverlay(_loc.CurrentLanguage switch
        {
            AppLanguage.KO => "로그 파일 스캔 중...",
            AppLanguage.JA => "ログファイルをスキャン中...",
            _ => "Scanning log files..."
        });

        try
        {
            var progress = new Progress<string>(message =>
            {
                Dispatcher.Invoke(() => UpdateLoadingStatus(message));
            });

            // SyncDaysRange used to be dropped here: SyncFromLogsAsync's third parameter took its
            // default of 0, so the configured range never reached the filter and every sync
            // covered every retained log (PRD R8).
            var result = await _logSyncService.SyncFromLogsAsync(
                logPath, progress, _settingsService.SyncDaysRange);

            // Immediately hide LoadingOverlay to prevent animation collision
            // (HideLoadingOverlay animation may be cancelled by the sync result dialog's blur animation)
            LoadingOverlay.Visibility = Visibility.Collapsed;
            HideLoadingOverlay();

            if (result.TotalEventsFound == 0)
            {
                MessageBox.Show(
                    _loc.CurrentLanguage switch
                    {
                        AppLanguage.KO => "로그에서 퀘스트 이벤트를 찾지 못했습니다.",
                        AppLanguage.JA => "ログにクエストイベントが見つかりませんでした。",
                        _ => "No quest events found in logs."
                    },
                    _loc.SyncSummaryTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Apply first, report second (PRD R2). Each change goes to the profile its own log
            // evidence names, so there is nothing for the player to arbitrate before the write.
            await ApplyAndShowSyncResultAsync(result);
        }
        catch (Exception ex)
        {
            HideLoadingOverlay();
            MessageBox.Show(
                $"Error: {ex.Message}",
                "Sync Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Toggle log monitoring
    /// </summary>
    private void BtnToggleMonitoring_Click(object sender, RoutedEventArgs e)
    {
        if (_logSyncService.IsMonitoring)
        {
            _logSyncService.StopMonitoring();
        }
        else
        {
            var logPath = _settingsService.LogFolderPath;
            if (!string.IsNullOrEmpty(logPath) && Directory.Exists(logPath))
            {
                _logSyncService.StartMonitoring(logPath);

                // Subscribe to quest events
                _logSyncService.QuestEventDetected -= OnQuestEventDetected;
                _logSyncService.QuestEventDetected += OnQuestEventDetected;
            }
        }

        UpdateQuestSyncUI();
    }

    /// <summary>
    /// Handle real-time quest event detection. Raised on a file-watcher thread, one event after
    /// another with no wait between them, so the handler both serializes itself and marshals its
    /// work onto the UI thread before touching the progress service.
    /// </summary>
    private async void OnQuestEventDetected(object? sender, QuestLogEvent evt)
    {
        // async void on a file-watcher callback: an escaping exception would take the process
        // down with no handler above it, so the whole body is guarded.
        try
        {
            // One event at a time. The raise loop does not await this handler, and
            // ApplyLogEventAsync reads a profile's rows before it writes them, so overlapping
            // events for one quest would plan against the same stale rows and the last write
            // to land would win regardless of log order.
            await _questEventGate.WaitAsync();
            try
            {
                // On the UI thread, and not with a blocking Invoke: this runs on a thread-pool
                // thread, and QuestProgressService.ProgressChanged (raised inside the call
                // below) has subscribers such as IntegratedItemService that update UI state
                // without marshalling themselves.
                await Dispatcher.InvokeAsync(() => HandleQuestEventAsync(evt)).Task.Unwrap();
            }
            finally
            {
                _questEventGate.Release();
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to record quest event {evt.QuestId} ({evt.EventType})", ex);
        }
    }

    /// <summary>
    /// Records one log-derived quest event. Runs on the UI thread; its awaits resume there too,
    /// so every event the progress service raises from it reaches the UI on the right thread.
    /// </summary>
    private async Task HandleQuestEventAsync(QuestLogEvent evt)
    {
        // The owner is the mode the raid was actually played in, which is not necessarily the
        // profile on screen: a player comparing another mode while a raid runs must still have
        // their progress recorded where it belongs (PRD R4).
        if (evt.OwnerProfile is not { } owner)
        {
            // PRD R3: no session mode evidence means no destination. Recording it under the
            // selection is exactly the misfiling this change removes, so the event is dropped.
            _log.Warning(
                $"Ignoring quest event {evt.QuestId} ({evt.EventType}): no session mode evidence in its log folder");
            return;
        }

        var progressService = QuestProgressService.Instance;

        // The service already indexes every quest by every one of its Ids; a second lookup
        // built here would only be a copy that can fall out of step with it.
        var task = progressService.GetTaskById(evt.QuestId);
        if (task == null) return;

        // The event's own log timestamp rides along so the reset fence can judge it: an event
        // from before the owner's reset must never restore removed progress (PRD R6 of
        // feature-complete-profile-reset.md).
        await progressService.ApplyLogEventAsync(task, evt.EventType, owner, evt.Timestamp);

        // Refresh quest list if visible. ApplyLogEventAsync leaves the snapshot untouched for
        // a profile that is not loaded, so this is a no-op redraw in that case.
        _questListPage?.RefreshDisplay();
    }

    /// <summary>
    /// Applies everything the sync derived, then shows the summary of where it landed and, only
    /// when the logs left a genuine either-or open, the choices the player has to make.
    /// </summary>
    private async Task ApplyAndShowSyncResultAsync(SyncResult result)
    {
        var updatingMessage = _loc.CurrentLanguage switch
        {
            AppLanguage.KO => "퀘스트 진행도 업데이트 중...",
            AppLanguage.JA => "クエスト進捗を更新中...",
            _ => "Updating quest progress..."
        };

        if (result.QuestsToComplete.Count > 0)
        {
            var outcome = await ApplyWithOverlayAsync(result.QuestsToComplete, updatingMessage);
            result.AppliedCountsByProfile = outcome.AppliedByProfile;
            result.FailedProfiles = outcome.FailedProfiles;
        }

        var alternativeChoices = SyncResultDialog.ShowResult(result, this);

        if (alternativeChoices == null || alternativeChoices.Count == 0) return;

        var appliedChoices = await ApplyWithOverlayAsync(alternativeChoices, updatingMessage);

        // The choices land in the profile each group was asked about, which is not necessarily
        // the one on screen, so they get the same per-profile breakdown the summary dialog gives
        // the derived changes, failed profiles included (PRD R2). The dialog is closed by now,
        // hence the message box.
        MessageBox.Show(
            BuildAppliedSummary(appliedChoices),
            _loc.SyncSummaryTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>
    /// Applies a batch of quest changes behind the loading overlay and reloads the quest list,
    /// returning what landed in each profile and which profiles failed. The single place the
    /// overlay is put up and taken down for a sync write, so a failing apply cannot leave it
    /// stuck on screen.
    /// </summary>
    private async Task<LogSyncService.QuestApplyOutcome> ApplyWithOverlayAsync(
        List<QuestChangeInfo> changes, string overlayMessage)
    {
        ShowLoadingOverlay(overlayMessage);
        LogSyncService.QuestApplyOutcome outcome;
        try
        {
            outcome = await _logSyncService.ApplyQuestChangesAsync(changes);
        }
        finally
        {
            HideLoadingOverlay();
        }

        // Only the loaded profile's rows are on screen; the others changed silently, which is
        // what the per-profile summary is for.
        await LoadAndShowQuestListAsync();

        return outcome;
    }

    /// <summary>
    /// The applied total followed by one row per profile written to, in the same wording the
    /// summary dialog uses. Profiles missing from <c>outcome.AppliedByProfile</c> had nothing
    /// written to them and are left out rather than shown as a zero; a profile whose write THREW
    /// is missing for a different reason, so it is named separately rather than read as untouched.
    /// </summary>
    private string BuildAppliedSummary(LogSyncService.QuestApplyOutcome outcome)
    {
        var appliedByProfile = outcome.AppliedByProfile;
        var lines = new List<string>
        {
            string.Format(_loc.SyncAlternativesAppliedFormat, appliedByProfile.Values.Sum())
        };

        if (appliedByProfile.Count > 0)
        {
            // Deterministic order so two runs writing the same profiles read the same way.
            lines.Add(string.Join(Environment.NewLine, appliedByProfile
                .OrderBy(entry => entry.Key)
                .Select(entry =>
                    $"{_loc.ProfileName(entry.Key)}: {string.Format(_loc.SyncAppliedCountFormat, entry.Value)}")));
        }

        if (outcome.FailedProfiles.Count > 0)
        {
            lines.Add(string.Format(_loc.SyncApplyFailedFormat,
                string.Join(", ", outcome.FailedProfiles.OrderBy(p => p).Select(_loc.ProfileName))));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    #endregion

    #region In-Progress Quest Input

    /// <summary>
    /// Open in-progress quest input dialog
    /// </summary>
    private void BtnInProgressQuestInput_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsOverlay();

        var result = InProgressQuestInputDialog.ShowDialog(this);
        if (result == null) return;

        // Apply the result
        ApplyInProgressQuestResult(result);
    }

    /// <summary>
    /// Apply the in-progress quest selection result
    /// </summary>
    private void ApplyInProgressQuestResult(InProgressQuestInputResult result)
    {
        var progressService = QuestProgressService.Instance;

        // Complete all prerequisites
        var completedCount = 0;
        foreach (var prereqName in result.PrerequisitesToComplete)
        {
            var prereqTask = progressService.GetTask(prereqName);
            if (prereqTask != null && progressService.GetStatus(prereqTask) != QuestStatus.Done)
            {
                progressService.CompleteQuest(prereqTask, completePrerequisites: false);
                completedCount++;
            }
        }

        // Refresh quest list
        _questListPage?.RefreshDisplay();

        // Show success message
        MessageBox.Show(
            string.Format(_loc.QuestsAppliedSuccess, result.SelectedQuests.Count, completedCount),
            _loc.CurrentLanguage switch { AppLanguage.KO => "Applied", AppLanguage.JA => "Applied", _ => "Applied" },
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    #endregion

    #region Cache Management

    /// <summary>
    /// Calculate total cache size
    /// </summary>
    private long CalculateCacheSize()
    {
        long totalSize = 0;

        // Cache directory (wiki pages, images, etc.)
        var cachePath = AppEnv.CachePath;
        if (Directory.Exists(cachePath))
        {
            totalSize += GetDirectorySize(cachePath);
        }

        return totalSize;
    }

    /// <summary>
    /// Calculate total data size (JSON files)
    /// </summary>
    private long CalculateDataSize()
    {
        long totalSize = 0;

        // Data directory (JSON files)
        var dataPath = AppEnv.DataPath;
        if (Directory.Exists(dataPath))
        {
            totalSize += GetDirectorySize(dataPath);
        }

        return totalSize;
    }

    /// <summary>
    /// Get directory size recursively
    /// </summary>
    private long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            var dir = new DirectoryInfo(path);
            foreach (var file in dir.GetFiles("*", SearchOption.AllDirectories))
            {
                size += file.Length;
            }
        }
        catch
        {
            // Ignore errors (access denied, etc.)
        }
        return size;
    }

    /// <summary>
    /// Format bytes to human readable string
    /// </summary>
    private string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Update cache size display
    /// </summary>
    private void UpdateCacheSizeDisplay()
    {
        var cacheSize = CalculateCacheSize();
        var dataSize = CalculateDataSize();
        var totalSize = cacheSize + dataSize;
        TxtCacheSize.Text = $"{FormatBytes(totalSize)} (Cache: {FormatBytes(cacheSize)}, Data: {FormatBytes(dataSize)})";
    }

    /// <summary>
    /// Clear cache button click handler
    /// </summary>
    private void BtnClearCache_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "캐시를 삭제하시겠습니까?\n(Wiki 페이지, 이미지 등이 삭제됩니다)",
                AppLanguage.JA => "キャッシュを削除しますか？\n（Wikiページ、画像などが削除されます）",
                _ => "Clear cache?\n(Wiki pages, images, etc. will be deleted)"
            },
            _loc.CurrentLanguage switch { AppLanguage.KO => "캐시 삭제", AppLanguage.JA => "キャッシュ削除", _ => "Clear Cache" },
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            BtnClearCache.IsEnabled = false;
            BtnClearAllData.IsEnabled = false;

            var cachePath = AppEnv.CachePath;
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, true);
            }

            UpdateCacheSizeDisplay();

            MessageBox.Show(
                _loc.CurrentLanguage switch
                {
                    AppLanguage.KO => "캐시가 삭제되었습니다.\n데이터를 다시 가져오려면 Refresh 버튼을 누르세요.",
                    AppLanguage.JA => "キャッシュが削除されました。\nデータを再取得するにはRefreshボタンを押してください。",
                    _ => "Cache cleared.\nPress Refresh to re-download data."
                },
                _loc.CurrentLanguage switch { AppLanguage.KO => "완료", AppLanguage.JA => "完了", _ => "Done" },
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error clearing cache: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            BtnClearCache.IsEnabled = true;
            BtnClearAllData.IsEnabled = true;
        }
    }

    /// <summary>
    /// Clear all data button click handler
    /// </summary>
    private void BtnClearAllData_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "모든 데이터를 삭제하시겠습니까?\n(캐시, 퀘스트 데이터, 아이템 데이터 등이 삭제됩니다)\n\n⚠️ 퀘스트 진행 상태는 유지됩니다.",
                AppLanguage.JA => "すべてのデータを削除しますか？\n（キャッシュ、クエストデータ、アイテムデータなどが削除されます）\n\n⚠️ クエスト進行状況は保持されます。",
                _ => "Clear all data?\n(Cache, quest data, item data, etc. will be deleted)\n\n⚠️ Quest progress will be preserved."
            },
            _loc.CurrentLanguage switch { AppLanguage.KO => "데이터 초기화", AppLanguage.JA => "データ初期化", _ => "Clear All Data" },
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            BtnClearCache.IsEnabled = false;
            BtnClearAllData.IsEnabled = false;

            // Clear cache
            var cachePath = AppEnv.CachePath;
            if (Directory.Exists(cachePath))
            {
                Directory.Delete(cachePath, true);
            }

            // Clear data files (user data is now in Config/user_data.db, safe to delete all)
            var dataPath = AppEnv.DataPath;
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, true);
            }

            UpdateCacheSizeDisplay();

            // Hide settings overlay
            HideSettingsOverlay();

            // Show confirmation
            MessageBox.Show(
                _loc.CurrentLanguage switch
                {
                    AppLanguage.KO => "캐시가 삭제되었습니다.",
                    AppLanguage.JA => "キャッシュが削除されました。",
                    _ => "Cache cleared."
                },
                _loc.CurrentLanguage switch { AppLanguage.KO => "완료", AppLanguage.JA => "完了", _ => "Done" },
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Error clearing data: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            BtnClearCache.IsEnabled = true;
            BtnClearAllData.IsEnabled = true;
        }
    }

    private void BtnFontSizeDown_Click(object sender, RoutedEventArgs e)
    {
        var currentSize = SettingsService.Instance.BaseFontSize;
        if (currentSize > SettingsService.MinFontSize)
        {
            SettingsService.Instance.BaseFontSize = currentSize - 1;
            UpdateFontSizeDisplay();
        }
    }

    private void BtnFontSizeUp_Click(object sender, RoutedEventArgs e)
    {
        var currentSize = SettingsService.Instance.BaseFontSize;
        if (currentSize < SettingsService.MaxFontSize)
        {
            SettingsService.Instance.BaseFontSize = currentSize + 1;
            UpdateFontSizeDisplay();
        }
    }

    private void BtnResetFontSize_Click(object sender, RoutedEventArgs e)
    {
        SettingsService.Instance.BaseFontSize = SettingsService.DefaultBaseFontSize;
        UpdateFontSizeDisplay();
    }

    private void UpdateFontSizeDisplay()
    {
        TxtCurrentFontSize.Text = SettingsService.Instance.BaseFontSize.ToString("0");
    }

    #endregion

    #region Sync Status Chip & Header Layout

    private HeaderLayoutMode _currentHeaderMode = HeaderLayoutMode.Full;

    // Semantic status brushes resolved once from the App.xaml palette. Reusing the
    // shared brush instances makes repeated chip renders allocation-free: assigning
    // the same instance to Fill/Foreground is a no-op for WPF.
    private Brush? _successBrush, _warningBrush, _errorBrush, _neutralBrush, _accentBrush, _textSecondaryBrush;
    private Brush SuccessStatusBrush => _successBrush ??= (Brush)FindResource("SuccessBrush");
    private Brush WarningStatusBrush => _warningBrush ??= (Brush)FindResource("WarningBrush");
    private Brush ErrorStatusBrush => _errorBrush ??= (Brush)FindResource("ErrorBrush");
    private Brush NeutralStatusBrush => _neutralBrush ??= (Brush)FindResource("NeutralBrush");
    private Brush AccentStatusBrush => _accentBrush ??= (Brush)FindResource("AccentBrush");
    private Brush TextSecondaryStatusBrush => _textSecondaryBrush ??= (Brush)FindResource("TextSecondaryBrush");

    // Named handlers (not lambdas) so OnWindowClosing can unsubscribe them from the
    // app-lifetime singleton services.
    private void OnLogMonitoringStatusChanged(object? sender, bool isMonitoring)
        => Dispatcher.InvokeAsync(UpdateSyncStatusChip);

    private void OnRaidMonitoringStateChanged(object? sender, bool isMonitoring)
        => Dispatcher.InvokeAsync(UpdateSyncStatusChip);

    private void OnRaidEvent(object? sender, EftRaidEventArgs e)
        => Dispatcher.InvokeAsync(UpdateSyncStatusChip);

    /// <summary>
    /// Render the title-bar sync/raid status chip from live monitoring state
    /// (state mapping in <see cref="HeaderSyncStatus"/>, unit-tested).
    /// Keyed off IsMonitoring (not SettingsService.LogMonitoringEnabled) because the
    /// Settings toggle starts/stops the watcher without persisting that setting.
    /// </summary>
    private void UpdateSyncStatusChip()
    {
        var raidService = EftRaidEventService.Instance;
        var monitoring = _logSyncService.IsMonitoring || raidService.IsMonitoring;
        var state = HeaderSyncStatus.GetState(monitoring, raidService.CurrentRaid?.State);

        var text = state switch
        {
            SyncChipState.InRaid => _loc.SyncStatusInRaid,
            SyncChipState.Matching => _loc.SyncStatusMatching,
            SyncChipState.Watching => _loc.SyncStatusWatching,
            _ => _loc.SyncStatusOff,
        };

        SyncStatusDot.Fill = state switch
        {
            SyncChipState.InRaid => AccentStatusBrush, // gold, the "live" state
            SyncChipState.Matching => WarningStatusBrush,
            SyncChipState.Watching => SuccessStatusBrush,
            _ => NeutralStatusBrush,
        };
        TxtSyncStatus.Text = text;
        AutomationProperties.SetName(ChipSyncStatus, text);
    }

    /// <summary>
    /// Status chip click: open Settings (where monitoring is configured), so the
    /// "off" state is directly actionable.
    /// </summary>
    private void ChipSyncStatus_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsOverlay();
    }

    /// <summary>
    /// Degrade the header gracefully at narrow widths instead of clipping
    /// (thresholds in <see cref="HeaderLayout"/>).
    /// </summary>
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyHeaderLayout(HeaderLayout.GetMode(e.NewSize.Width));
    }

    // All tab glyphs, so narrow-width degradation toggles them as one set; a new
    // tab's glyph only needs to be added here to participate.
    private TextBlock[]? _tabGlyphs;

    private void ApplyHeaderLayout(HeaderLayoutMode mode)
    {
        if (mode == _currentHeaderMode) return;
        _currentHeaderMode = mode;

        // Compact: status text collapses to its dot (tooltip remains) and the tab
        // glyphs go (at the default font size, text-only tabs fit down to MinWidth 600;
        // JA is within a few px of the limit, and font sizes near the 28 max can
        // still clip at 600; with glyphs they clip below ~1000). Minimal: the brand
        // title goes too.
        var full = mode == HeaderLayoutMode.Full;
        TxtSyncStatus.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        WideProfileSelector.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        BtnActiveProfileMenu.Visibility = full ? Visibility.Collapsed : Visibility.Visible;
        if (full)
        {
            ActiveProfileContextMenu.IsOpen = false;
        }
        var glyphVisibility = full ? Visibility.Visible : Visibility.Collapsed;
        _tabGlyphs ??= new[] { IcoTabQuests, IcoTabHideout, IcoTabItems, IcoTabCollector, IcoTabMap };
        foreach (var glyph in _tabGlyphs)
        {
            glyph.Visibility = glyphVisibility;
        }
        TxtBrandTitle.Visibility = mode == HeaderLayoutMode.Minimal
            ? Visibility.Collapsed : Visibility.Visible;
    }

    #endregion

    #region Full Screen Mode

    /// <summary>
    /// 전체화면 모드를 설정합니다.
    /// Map 페이지에서 호출됩니다.
    /// </summary>
    /// <param name="fullScreen">true이면 전체화면 모드 진입, false이면 해제</param>
    public void SetFullScreenMode(bool fullScreen)
    {
        _isFullScreen = fullScreen;

        if (fullScreen)
        {
            // 타이틀 바와 탭 네비게이션 숨기기
            TitleBar.Visibility = Visibility.Collapsed;
            TabNavigation.Visibility = Visibility.Collapsed;

            // The drawer is a sibling of MainContent and would float over the
            // full-screen map if left open.
            CloseProfileDrawer();

            // 전체화면 모드 진입
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            // 타이틀 바와 탭 네비게이션 다시 표시
            TitleBar.Visibility = Visibility.Visible;
            TabNavigation.Visibility = Visibility.Visible;

            // 전체화면 모드 해제
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = WindowState.Normal;
        }
    }

    #endregion

    #region Data Migration

    /// <summary>
    /// Open folder dialog to select Config folder for migration
    /// </summary>
    private async void BtnDataMigration_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _loc.CurrentLanguage switch
            {
                AppLanguage.KO => "이전 버전 Config 폴더 선택",
                AppLanguage.JA => "以前のバージョンのConfigフォルダを選択",
                _ => "Select Previous Version Config Folder"
            }
        };

        if (dialog.ShowDialog() != true) return;

        var selectedPath = dialog.FolderName;
        var migrationService = ConfigMigrationService.Instance;

        // Validate folder
        if (!migrationService.IsValidConfigFolder(selectedPath))
        {
            MessageBox.Show(
                _loc.CurrentLanguage switch
                {
                    AppLanguage.KO => "유효한 Config 폴더가 아닙니다.\nquest_progress.json, hideout_progress.json, item_inventory.json 또는 app_settings.json 파일이 필요합니다.",
                    AppLanguage.JA => "有効なConfigフォルダではありません。\nquest_progress.json、hideout_progress.json、item_inventory.json、またはapp_settings.jsonファイルが必要です。",
                    _ => "Invalid Config folder.\nMust contain quest_progress.json, hideout_progress.json, item_inventory.json, or app_settings.json."
                },
                _loc.CurrentLanguage switch { AppLanguage.KO => "오류", AppLanguage.JA => "エラー", _ => "Error" },
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Preview migration
        var preview = migrationService.PreviewMigration(selectedPath);

        // Show confirmation
        var confirmMessage = _loc.CurrentLanguage switch
        {
            AppLanguage.KO => $"다음 데이터를 가져올 수 있습니다:\n\n" +
                              $"- 퀘스트 진행: {preview.QuestProgressCount}개\n" +
                              $"- 하이드아웃 진행: {preview.HideoutProgressCount}개\n" +
                              $"- 아이템 인벤토리: {preview.ItemInventoryCount}개\n" +
                              $"- 설정: {preview.SettingsCount}개\n\n" +
                              "가져오기를 진행하시겠습니까?\n(기존 데이터를 덮어씁니다)",
            AppLanguage.JA => $"以下のデータをインポートできます:\n\n" +
                              $"- クエスト進行: {preview.QuestProgressCount}件\n" +
                              $"- ハイドアウト進行: {preview.HideoutProgressCount}件\n" +
                              $"- アイテムインベントリ: {preview.ItemInventoryCount}件\n" +
                              $"- 設定: {preview.SettingsCount}件\n\n" +
                              "インポートを続行しますか？\n(既存のデータは上書きされます)",
            _ => $"The following data can be imported:\n\n" +
                 $"- Quest Progress: {preview.QuestProgressCount}\n" +
                 $"- Hideout Progress: {preview.HideoutProgressCount}\n" +
                 $"- Item Inventory: {preview.ItemInventoryCount}\n" +
                 $"- Settings: {preview.SettingsCount}\n\n" +
                 "Do you want to proceed?\n(Existing data will be overwritten)"
        };

        var confirmResult = MessageBox.Show(
            confirmMessage,
            _loc.CurrentLanguage switch { AppLanguage.KO => "데이터 가져오기", AppLanguage.JA => "データのインポート", _ => "Import Data" },
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmResult != MessageBoxResult.Yes) return;

        // Hide settings overlay
        HideSettingsOverlay();

        // Show loading overlay
        ShowLoadingOverlay(_loc.CurrentLanguage switch
        {
            AppLanguage.KO => "데이터 마이그레이션 중...",
            AppLanguage.JA => "データ移行中...",
            _ => "Migrating data..."
        });

        try
        {
            var progress = new Progress<string>(message =>
            {
                Dispatcher.Invoke(() => UpdateLoadingStatus(message));
            });

            var result = await migrationService.MigrateFromConfigFolderAsync(selectedPath, progress);

            // 즉시 LoadingOverlay 숨기기 (애니메이션 충돌 방지)
            LoadingOverlay.Visibility = Visibility.Collapsed;

            // Show result popup
            ShowMigrationResultDialog(result);

            // Reload pages to reflect new data
            await LoadAndShowQuestListAsync();
        }
        catch (Exception ex)
        {
            HideLoadingOverlay();
            MessageBox.Show(
                $"Migration failed: {ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Show migration result dialog
    /// </summary>
    private void ShowMigrationResultDialog(ConfigMigrationService.MigrationResult result)
    {
        MigrationResultDialog.Show(result, this);
    }

    #endregion

    #region App Update

    /// <summary>
    /// Start app update service
    /// </summary>
    private void StartAppUpdateService()
    {
        var updateService = UpdateService.Instance;

        // Initialize version displays (title-bar chip + Settings section)
        UpdateVersionChipUI();
        UpdateSettingsUpdateSectionUI();

        // Subscribe to update events
        updateService.UpdateCheckStarted += OnUpdateCheckStarted;
        updateService.UpdateCheckCompleted += OnUpdateCheckCompleted;

        // Start automatic update checking (every 3 minutes)
        updateService.StartAutoCheck();

        _log.Info("App update service started");
    }

    /// <summary>
    /// Update check started event handler
    /// </summary>
    private void OnUpdateCheckStarted(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            BtnCheckUpdateSettings.IsEnabled = false;

            // Both renderers read UpdateService.IsChecking themselves, so the whole
            // checking state (chip hint + Settings status) comes from one place.
            UpdateVersionChipUI();
            UpdateSettingsUpdateSectionUI();
        });
    }

    /// <summary>
    /// Update check completed event handler
    /// </summary>
    private void OnUpdateCheckCompleted(object? sender, UpdateCheckEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            BtnCheckUpdateSettings.IsEnabled = true;

            UpdateVersionChipUI();
            UpdateSettingsUpdateSectionUI();

            if (e.IsUpdateAvailable && e.UpdateInfo != null)
            {
                _log.Info($"Update available: {e.UpdateInfo.Version}");
            }
            else if (e.Error != null)
            {
                _log.Warning($"Update check failed: {e.Error.Message}");
            }
        });
    }

    /// <summary>
    /// Render the title-bar version display from UpdateService state: the single
    /// writer for the chip. Update available: the green "Update vX.Y.Z" install pill.
    /// Otherwise the passive chip: "Checking…" while a check runs, the version tinted
    /// red with an explanatory tooltip when the last check failed (the bar's only
    /// failure signal), or the plain version. Only the pill is interactive; manual
    /// checks live in Settings.
    /// </summary>
    private void UpdateVersionChipUI()
    {
        var updateService = UpdateService.Instance;
        var update = updateService.AvailableUpdate;

        if (update != null)
        {
            var version = UpdateService.FormatVersion(update.Version);
            TxtUpdatePillLabel.Text = string.Format(_loc.HeaderUpdateAvailableFormat, version);
            BtnVersionChip.ToolTip = string.Format(_loc.HeaderVersionTooltipInstall, version);
            AutomationProperties.SetName(BtnVersionChip, TxtUpdatePillLabel.Text);
            BtnVersionChip.Visibility = Visibility.Visible;
            ChipVersion.Visibility = Visibility.Collapsed;
            return;
        }

        if (updateService.IsChecking)
        {
            // Passive progress hint; the pill (handled above) is never disturbed by a
            // periodic re-check.
            TxtVersionChip.Text = _loc.HeaderChecking;
            TxtVersionChip.Foreground = TextSecondaryStatusBrush;
            ChipVersion.ToolTip = _loc.HeaderVersionTooltipIdle;
        }
        else if (updateService.LastCheckFailed)
        {
            TxtVersionChip.Text = UpdateService.FormatVersion(updateService.CurrentVersion);
            TxtVersionChip.Foreground = ErrorStatusBrush;
            ChipVersion.ToolTip = _loc.HeaderVersionTooltipCheckFailed;
        }
        else
        {
            TxtVersionChip.Text = UpdateService.FormatVersion(updateService.CurrentVersion);
            TxtVersionChip.Foreground = TextSecondaryStatusBrush;
            ChipVersion.ToolTip = _loc.HeaderVersionTooltipIdle;
        }
        ChipVersion.Visibility = Visibility.Visible;
        BtnVersionChip.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Render the Settings overlay's Application Update section. The install button
    /// is driven solely by update availability, while the status text reports the
    /// latest check outcome (via <see cref="UpdateService.GetStatusKind"/>), so a
    /// failed re-check stays visible without hiding a previously found update.
    /// </summary>
    private void UpdateSettingsUpdateSectionUI()
    {
        var updateService = UpdateService.Instance;
        TxtSettingsVersion.Text = string.Format(_loc.SettingsCurrentVersionFormat,
            UpdateService.FormatVersion(updateService.CurrentVersion));

        var update = updateService.AvailableUpdate;
        if (update != null)
        {
            TxtSettingsUpdateToLabel.Text = string.Format(_loc.SettingsUpdateToFormat,
                UpdateService.FormatVersion(update.Version));
            AutomationProperties.SetName(BtnUpdateAvailableSettings, TxtSettingsUpdateToLabel.Text);
            BtnUpdateAvailableSettings.Visibility = Visibility.Visible;
        }
        else
        {
            BtnUpdateAvailableSettings.Visibility = Visibility.Collapsed;
        }

        var kind = UpdateService.GetStatusKind(
            updateService.IsChecking, updateService.LastCheckFailed,
            update != null, updateService.LastCheckTime.HasValue);
        (TxtSettingsUpdateStatus.Text, TxtSettingsUpdateStatus.Foreground) = kind switch
        {
            UpdateStatusKind.Checking => (_loc.HeaderChecking, TextSecondaryStatusBrush),
            UpdateStatusKind.Failed => (_loc.UpdateStatusFailed, ErrorStatusBrush),
            UpdateStatusKind.UpdateAvailable => (_loc.UpdateStatusAvailable, WarningStatusBrush),
            UpdateStatusKind.UpToDate => (_loc.UpdateStatusUpToDate, SuccessStatusBrush),
            _ => ("", TextSecondaryStatusBrush), // no check has completed yet
        };

        UpdateLastCheckTimeDisplay();
    }

    /// <summary>
    /// Update the last-check time display in the Settings update section
    /// </summary>
    private void UpdateLastCheckTimeDisplay()
    {
        var lastCheck = UpdateService.Instance.LastCheckTime;
        if (lastCheck.HasValue)
        {
            var timeAgo = DateTime.Now - lastCheck.Value;
            string timeText;

            if (timeAgo.TotalSeconds < 60)
            {
                timeText = _loc.TimeJustNow;
            }
            else if (timeAgo.TotalMinutes < 60)
            {
                timeText = string.Format(_loc.TimeMinutesAgoFormat, (int)timeAgo.TotalMinutes);
            }
            else
            {
                // Include the date once the check is more than a day old so "(21:14)"
                // from yesterday can't read as twenty minutes ago today.
                timeText = lastCheck.Value.Date == DateTime.Now.Date
                    ? lastCheck.Value.ToString("HH:mm")
                    : lastCheck.Value.ToString("MM-dd HH:mm");
            }

            TxtSettingsLastCheck.Text = $"({timeText})";
        }
        else
        {
            TxtSettingsLastCheck.Text = "";
        }
    }

    /// <summary>
    /// Shared handler for both install buttons (title-bar pill and Settings section):
    /// installs the available update. One handler, so the null-guard and logging
    /// can't drift between the two entry points.
    /// </summary>
    private void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        var updateInfo = UpdateService.Instance.AvailableUpdate;
        if (updateInfo != null)
        {
            _log.Info($"User initiated update to version {updateInfo.Version}");
            UpdateService.Instance.StartUpdate();
        }
    }

    /// <summary>
    /// Settings "Check for Updates" button click
    /// </summary>
    private async void BtnCheckUpdateSettings_Click(object sender, RoutedEventArgs e)
    {
        _log.Debug("Manual update check triggered from Settings");
        await UpdateService.Instance.CheckForUpdateAsync();
    }

    #endregion

    #region Window Bounds Persistence

    private const string WindowBoundsKey = "app.mainWindowBounds";
    private bool _restoreMaximized;

    /// <summary>
    /// Restore the window bounds saved at last close. Called from the constructor,
    /// after InitializeComponent(), before the window is shown. On first run or
    /// invalid saved bounds, the XAML defaults (CenterScreen) stay in effect.
    /// </summary>
    private void RestoreWindowBounds()
    {
        try
        {
            var virtualScreen = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var bounds = WindowBoundsPersistence.ParseAndValidate(
                _settingsService.GetValue(WindowBoundsKey), MinWidth, MinHeight, virtualScreen);
            if (bounds == null) return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
            _restoreMaximized = bounds.IsMaximized; // applied in OnSourceInitialized
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to restore window bounds: {ex.Message}");
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // Detach the handlers this window added to app-lifetime singletons, so a
        // background raise (log watcher, raid poller, the 3-minute update timer)
        // during/after teardown can't dispatch UI work against a closed window.
        _logSyncService.MonitoringStatusChanged -= OnLogMonitoringStatusChanged;
        // Detached here too: the quest-event handler dispatches its whole body onto this
        // window's Dispatcher, so a tail read completing during teardown would otherwise
        // await an operation the shutting-down dispatcher never runs.
        _logSyncService.QuestEventDetected -= OnQuestEventDetected;
        EftRaidEventService.Instance.MonitoringStateChanged -= OnRaidMonitoringStateChanged;
        EftRaidEventService.Instance.RaidEvent -= OnRaidEvent;
        ProfileService.Instance.ActiveProfileChanged -= OnActiveProfileChanged;
        _profileTransitionCueTimer.Stop();
        _profileTransitionCueTimer.Tick -= ProfileTransitionCueTimer_Tick;
        UpdateService.Instance.UpdateCheckStarted -= OnUpdateCheckStarted;
        UpdateService.Instance.UpdateCheckCompleted -= OnUpdateCheckCompleted;

        // WPF does not guarantee Unloaded at shutdown, so the map page's view
        // state (map/zoom/pan) gets its close-time save here as a backstop.
        TrySaveOnClose("map view state", () => _mapTrackerPage?.PersistViewState());

        TrySaveOnClose("window bounds", () =>
        {
            var json = WindowBoundsPersistence.CreateSaveValue(
                WindowState, new Rect(Left, Top, ActualWidth, ActualHeight), RestoreBounds);
            if (json == null) return; // unusable geometry: keep the previously saved value

            _settingsService.SetValue(WindowBoundsKey, json);
        });
    }

    /// <summary>
    /// Runs a best-effort close-time save, logging (never rethrowing) on failure so one
    /// failed save can't abort the others or block shutdown. <paramref name="what"/> is
    /// interpolated into the warning to keep the sites' messages consistent.
    /// </summary>
    private void TrySaveOnClose(string what, Action save)
    {
        try
        {
            save();
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to save {what}: {ex.Message}");
        }
    }

    #endregion
}
