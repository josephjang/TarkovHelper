namespace TarkovHelper.Services;

/// <summary>
/// Header (title bar, tab navigation, profile drawer) and Settings-overlay strings
/// for the sections added by the top-bar redesign. Part of the LocalizationService
/// partial class; follow the named-property pattern here instead of inline
/// CurrentLanguage switches in code-behind.
/// </summary>
public partial class LocalizationService
{
    #region Tab Navigation

    public string TabQuests => CurrentLanguage switch
    {
        AppLanguage.KO => "퀘스트",
        AppLanguage.JA => "クエスト",
        _ => "Quests"
    };

    public string TabHideout => CurrentLanguage switch
    {
        AppLanguage.KO => "은신처",
        AppLanguage.JA => "ハイドアウト",
        _ => "Hideout"
    };

    public string TabItems => CurrentLanguage switch
    {
        AppLanguage.KO => "아이템",
        AppLanguage.JA => "アイテム",
        _ => "Items"
    };

    public string TabCollector => CurrentLanguage switch
    {
        AppLanguage.KO => "수집가",
        AppLanguage.JA => "コレクター",
        _ => "Collector"
    };

    public string TabMap => CurrentLanguage switch
    {
        AppLanguage.KO => "맵",
        AppLanguage.JA => "マップ",
        _ => "Map"
    };

    #endregion

    #region Title Bar

    public string HeaderPvpZone => CurrentLanguage switch
    {
        AppLanguage.KO => "PvP 존",
        AppLanguage.JA => "PvP ゾーン",
        _ => "PvP Zone"
    };

    public string HeaderPveZone => CurrentLanguage switch
    {
        AppLanguage.KO => "PvE 존",
        AppLanguage.JA => "PvE ゾーン",
        _ => "PvE Zone"
    };

    public string HeaderPvpSeason => CurrentLanguage switch
    {
        AppLanguage.KO => "시즌 PvP",
        AppLanguage.JA => "PvP シーズン",
        _ => "PvP Season"
    };

    public string HeaderPvpTooltip => CurrentLanguage switch
    {
        AppLanguage.KO => "PvP 존으로 전환",
        AppLanguage.JA => "PvP ゾーンに切り替え",
        _ => "Switch to PvP Zone"
    };

    public string HeaderPveTooltip => CurrentLanguage switch
    {
        AppLanguage.KO => "PvE 존으로 전환",
        AppLanguage.JA => "PvE ゾーンに切り替え",
        _ => "Switch to PvE Zone"
    };

    public string HeaderPvpSeasonTooltip => CurrentLanguage switch
    {
        AppLanguage.KO => "시즌 PvP로 전환",
        AppLanguage.JA => "PvP シーズンに切り替え",
        _ => "Switch to PvP Season"
    };

    public string HeaderActiveProfile => CurrentLanguage switch
    {
        AppLanguage.KO => "활성 프로필",
        AppLanguage.JA => "アクティブプロフィール",
        _ => "Active profile"
    };

    public string HeaderProfileMenuTooltip => CurrentLanguage switch
    {
        AppLanguage.KO => "활성 프로필 선택",
        AppLanguage.JA => "アクティブプロフィールを選択",
        _ => "Select active profile"
    };

    public string HeaderProfileSourceManual => CurrentLanguage switch
    {
        AppLanguage.KO => "사용자 선택",
        AppLanguage.JA => "ユーザー選択",
        _ => "User selected"
    };

    public string HeaderProfileSourceAutomatic => CurrentLanguage switch
    {
        AppLanguage.KO => "게임 로그 자동 선택",
        AppLanguage.JA => "ゲームログによる自動選択",
        _ => "Auto-selected from game logs"
    };

    /// <summary>{0} = localized profile name.</summary>
    public string HeaderProfileChangedFromLogsFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "게임 로그에 따른 프로필 전환: {0}",
        AppLanguage.JA => "ゲームログによりプロフィールを{0}に切り替えました",
        _ => "Profile changed to {0} from game logs"
    };

    public string HeaderProfileTooltip => CurrentLanguage switch
    {
        AppLanguage.KO => "플레이어 프로필 — 레벨, 스캐브 평판, DSP, 에디션, 프레스티지",
        AppLanguage.JA => "プレイヤープロフィール — レベル、スカーヴ評判、DSP、エディション、プレステージ",
        _ => "Player profile — level, Scav Rep, DSP, edition, prestige"
    };

    /// <summary>Concise UIA name for the profile chip (the tooltip is too verbose for a Name).</summary>
    public string HeaderProfileName => CurrentLanguage switch
    {
        AppLanguage.KO => "플레이어 프로필",
        AppLanguage.JA => "プレイヤープロフィール",
        _ => "Player profile"
    };

    /// <summary>Short level prefix shown on the profile chip, e.g. "Lv 15".</summary>
    public string HeaderLevelShort => CurrentLanguage switch
    {
        _ => "Lv"
    };

    public string HeaderVersionTooltipIdle => CurrentLanguage switch
    {
        AppLanguage.KO => "현재 버전 — 업데이트는 자동으로 확인됩니다",
        AppLanguage.JA => "現在のバージョン — 更新は自動的に確認されます",
        _ => "Current version — updates are checked automatically"
    };

    /// <summary>{0} = version, e.g. "v2026.8.0".</summary>
    public string HeaderVersionTooltipInstall => CurrentLanguage switch
    {
        AppLanguage.KO => "클릭하여 {0} 업데이트 설치",
        AppLanguage.JA => "クリックして更新 {0} をインストール",
        _ => "Click to install update {0}"
    };

    /// <summary>{0} = version, e.g. "v2026.8.0".</summary>
    public string HeaderUpdateAvailableFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "{0} 업데이트",
        AppLanguage.JA => "{0} に更新",
        _ => "Update {0}"
    };

    public string HeaderChecking => CurrentLanguage switch
    {
        AppLanguage.KO => "확인 중…",
        AppLanguage.JA => "確認中…",
        _ => "Checking…"
    };

    /// <summary>
    /// Tooltip on the (red-tinted) idle version chip when the most recent update
    /// check failed — the title bar's only failure signal.
    /// </summary>
    public string HeaderVersionTooltipCheckFailed => CurrentLanguage switch
    {
        AppLanguage.KO => "업데이트 확인 실패 — 자세한 내용은 설정에서 확인하세요",
        AppLanguage.JA => "更新の確認に失敗しました — 詳細は設定を確認してください",
        _ => "Update check failed — open Settings for details"
    };

    #endregion

    #region Sync Status Chip

    public string SyncStatusOff => CurrentLanguage switch
    {
        AppLanguage.KO => "동기화 꺼짐",
        AppLanguage.JA => "同期オフ",
        _ => "Sync off"
    };

    public string SyncStatusWatching => CurrentLanguage switch
    {
        AppLanguage.KO => "로그 감시 중",
        AppLanguage.JA => "ログ監視中",
        _ => "Watching logs"
    };

    public string SyncStatusMatching => CurrentLanguage switch
    {
        AppLanguage.KO => "매칭 중…",
        AppLanguage.JA => "マッチング中…",
        _ => "Matching…"
    };

    public string SyncStatusInRaid => CurrentLanguage switch
    {
        AppLanguage.KO => "레이드 중",
        AppLanguage.JA => "レイド中",
        _ => "In raid"
    };

    public string SyncStatusTooltip => CurrentLanguage switch
    {
        AppLanguage.KO => "게임 로그 모니터링 상태 — 클릭하면 설정이 열립니다",
        AppLanguage.JA => "ゲームログ監視状態 — クリックで設定を開きます",
        _ => "Game-log monitoring status — click to open Settings"
    };

    #endregion

    #region Profile Drawer

    public string ProfileLevelLabel => CurrentLanguage switch
    {
        AppLanguage.KO => "레벨",
        AppLanguage.JA => "レベル",
        _ => "Level"
    };

    public string ProfileScavRepLabel => CurrentLanguage switch
    {
        AppLanguage.KO => "스캐브 평판",
        AppLanguage.JA => "スカーヴ評判",
        _ => "Scav Rep"
    };

    public string ProfileDspLabel => CurrentLanguage switch
    {
        _ => "DSP"
    };

    public string ProfileEditionLabel => CurrentLanguage switch
    {
        AppLanguage.KO => "에디션",
        AppLanguage.JA => "エディション",
        _ => "Edition"
    };

    public string ProfilePrestigeLabel => CurrentLanguage switch
    {
        AppLanguage.KO => "프레스티지",
        AppLanguage.JA => "プレステージ",
        _ => "Prestige"
    };

    #endregion

    #region Settings — pre-existing rows (migrated from inline switches in MainWindow)

    // The overlay title reuses the Core "Settings" property — same string, one source.

    public string SettingsLogFolderLabel => CurrentLanguage switch
    {
        AppLanguage.KO => "Tarkov 로그 폴더",
        AppLanguage.JA => "Tarkovログフォルダ",
        _ => "Tarkov Log Folder"
    };

    public string SettingsLogFolderDesc => CurrentLanguage switch
    {
        AppLanguage.KO => "자동 퀘스트 완료 추적을 위해 Tarkov의 Logs 폴더 경로를 설정하세요.",
        AppLanguage.JA => "自動クエスト完了追跡のために、TarkovのLogsフォルダのパスを設定してください。",
        _ => "Set the path to Tarkov's Logs folder for automatic quest completion tracking."
    };

    public string SettingsAutoDetectButton => CurrentLanguage switch
    {
        AppLanguage.KO => "자동 감지",
        AppLanguage.JA => "自動検出",
        _ => "Auto Detect"
    };

    public string SettingsBrowseButton => CurrentLanguage switch
    {
        AppLanguage.KO => "찾아보기...",
        AppLanguage.JA => "参照...",
        _ => "Browse..."
    };

    public string SettingsResetLogFolderButton => CurrentLanguage switch
    {
        AppLanguage.KO => "초기화",
        AppLanguage.JA => "リセット",
        _ => "Reset"
    };

    #endregion

    #region Settings — Language / Support / Update / Danger Zone

    public string SettingsLanguageLabel => CurrentLanguage switch
    {
        AppLanguage.KO => "언어",
        AppLanguage.JA => "言語",
        _ => "Language"
    };

    public string SettingsSupportLabel => CurrentLanguage switch
    {
        AppLanguage.KO => "후원",
        AppLanguage.JA => "サポート",
        _ => "Support"
    };

    public string SettingsSupportDesc => CurrentLanguage switch
    {
        AppLanguage.KO => "Tarkov Helper가 도움이 되었다면 개발을 후원할 수 있습니다.",
        AppLanguage.JA => "Tarkov Helperが役に立ったなら、開発をサポートできます。",
        _ => "If Tarkov Helper is useful to you, you can support its development."
    };

    public string SettingsSupportButton => CurrentLanguage switch
    {
        AppLanguage.KO => "커피 한 잔 후원하기",
        AppLanguage.JA => "コーヒーをおごる",
        _ => "Buy me a coffee"
    };

    public string SettingsUpdateLabel => CurrentLanguage switch
    {
        AppLanguage.KO => "앱 업데이트",
        AppLanguage.JA => "アプリの更新",
        _ => "Application Update"
    };

    /// <summary>{0} = version, e.g. "v2026.7.0".</summary>
    public string SettingsCurrentVersionFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "현재 버전: {0}",
        AppLanguage.JA => "現在のバージョン: {0}",
        _ => "Current version: {0}"
    };

    public string SettingsCheckUpdateButton => CurrentLanguage switch
    {
        AppLanguage.KO => "업데이트 확인",
        AppLanguage.JA => "更新を確認",
        _ => "Check for Updates"
    };

    /// <summary>{0} = version, e.g. "v2026.8.0".</summary>
    public string SettingsUpdateToFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "{0}(으)로 업데이트",
        AppLanguage.JA => "{0} に更新",
        _ => "Update to {0}"
    };

    public string UpdateStatusUpToDate => CurrentLanguage switch
    {
        AppLanguage.KO => "최신 버전",
        AppLanguage.JA => "最新版",
        _ => "Up to date"
    };

    public string UpdateStatusAvailable => CurrentLanguage switch
    {
        AppLanguage.KO => "업데이트 있음",
        AppLanguage.JA => "更新あり",
        _ => "Update available"
    };

    public string UpdateStatusFailed => CurrentLanguage switch
    {
        AppLanguage.KO => "확인 실패",
        AppLanguage.JA => "確認失敗",
        _ => "Check failed"
    };

    public string TimeJustNow => CurrentLanguage switch
    {
        AppLanguage.KO => "방금 전",
        AppLanguage.JA => "たった今",
        _ => "just now"
    };

    /// <summary>{0} = whole minutes.</summary>
    public string TimeMinutesAgoFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "{0}분 전",
        AppLanguage.JA => "{0}分前",
        _ => "{0}m ago"
    };

    public string SettingsDangerZoneLabel => CurrentLanguage switch
    {
        AppLanguage.KO => "위험 구역",
        AppLanguage.JA => "危険な操作",
        _ => "Danger Zone"
    };

    public string SettingsResetProgressDesc => CurrentLanguage switch
    {
        AppLanguage.KO => "모든 퀘스트와 은신처 진행도를 초기화합니다. 되돌릴 수 없습니다.",
        AppLanguage.JA => "すべてのクエストとハイドアウトの進行状況をリセットします。元に戻せません。",
        _ => "Reset all quest and hideout progress. This cannot be undone."
    };

    public string SettingsResetProgressButton => CurrentLanguage switch
    {
        AppLanguage.KO => "모든 진행도 초기화",
        AppLanguage.JA => "全進行状況をリセット",
        _ => "Reset All Progress"
    };

    #endregion
}
