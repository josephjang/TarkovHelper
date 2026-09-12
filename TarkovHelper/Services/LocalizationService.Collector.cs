namespace TarkovHelper.Services;

/// <summary>
/// Collector page strings. The page had no strings of its own until its unlock panel: its
/// filter row, column headers and item detail are English literals from before the app's
/// localization pass and stay so (feature-kappa-collector-1-1.md, Non-Goals). What this phase
/// adds or changes ships in all three languages, all of it (PD4, TD7). The Kappa strings the
/// panel shares with the quest tab's detail pane live in LocalizationService.Quest.cs.
/// </summary>
public partial class LocalizationService
{
    #region Collector page

    /// <summary>The unlock panel's heading, above Collector's status badge and its condition lines.</summary>
    public string CollectorUnlockHeading => CurrentLanguage switch
    {
        AppLanguage.KO => "Collector 해금",
        AppLanguage.JA => "Collector 解放",
        _ => "Collector unlock"
    };

    // The item list's scope, named for what the page does: Collector's own items, plus the items
    // of its PREREQUISITE quests when the option is on. The old labels ("Include Pre-Quest",
    // "Kappa Quests Only") were wrong twice over: the page never lists the thirteen flagged
    // quests' items as a set, and with the option off it lists Collector's and nobody else's
    // (feature-kappa-collector-1-1.md, R6). These five say "prerequisites" on purpose; the Kappa
    // strings in LocalizationService.Quest.cs say "Kappa quests" on purpose. The two sets are
    // different sets.

    /// <summary>The checkbox that adds the prerequisite quests' items to the list.</summary>
    public string CollectorIncludePrerequisites => CurrentLanguage switch
    {
        AppLanguage.KO => "선행 퀘스트 포함",
        AppLanguage.JA => "先行クエストを含む",
        _ => "Include prerequisites"
    };

    /// <summary>The checkbox's tooltip.</summary>
    public string CollectorIncludePrerequisitesTip => CurrentLanguage switch
    {
        AppLanguage.KO => "Collector의 선행 퀘스트에 필요한 아이템도 함께 표시합니다",
        AppLanguage.JA => "Collector の先行クエストに必要なアイテムも表示します",
        _ => "Also list the items Collector's prerequisite quests need"
    };

    /// <summary>
    /// The stats line under the filter bar. {0} = items shown, {1} = units needed in total,
    /// {2} = items fulfilled, {3} = items in progress, {4} = the scope
    /// (<see cref="CollectorScopeWithPrerequisites"/> or <see cref="CollectorScopeCollectorOnly"/>).
    /// One composed string per language rather than a localized suffix on an English sentence
    /// (feature-kappa-collector-1-1.spec.md, TD7).
    /// </summary>
    public string CollectorStatsFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "{0}개 표시 | 전체: {1} | 완료: {2} | 진행 중: {3} | {4}",
        AppLanguage.JA => "{0}件表示 | 合計: {1} | 達成: {2} | 進行中: {3} | {4}",
        _ => "Showing {0} items | Total: {1} | Fulfilled: {2} | In Progress: {3} | {4}"
    };

    /// <summary>The stats line's scope with the option on.</summary>
    public string CollectorScopeWithPrerequisites => CurrentLanguage switch
    {
        AppLanguage.KO => "선행 퀘스트 포함",
        AppLanguage.JA => "先行クエストを含む",
        _ => "Including prerequisites"
    };

    /// <summary>The stats line's scope with the option off: Collector's items and nobody else's.</summary>
    public string CollectorScopeCollectorOnly => CurrentLanguage switch
    {
        AppLanguage.KO => "Collector만",
        AppLanguage.JA => "Collector のみ",
        _ => "Collector only"
    };

    #endregion
}
