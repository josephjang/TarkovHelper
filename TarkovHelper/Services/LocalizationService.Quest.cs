using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Quest-related localization strings for LocalizationService.
/// Includes: In-Progress Quest Input, Quest Recommendations, etc.
/// </summary>
public partial class LocalizationService
{
    #region Quest Name Localization

    /// <summary>
    /// Returns a quest's name in the current language, falling back to the English name when the
    /// localized name is missing/blank. Single entry point for quest-name display across the app.
    /// </summary>
    public string GetQuestName(TarkovTask task) => GetQuestName(CurrentLanguage, task);

    /// <summary>Pure, testable core of <see cref="GetQuestName(TarkovTask)"/>.</summary>
    public static string GetQuestName(AppLanguage lang, TarkovTask task) => lang switch
    {
        AppLanguage.KO => string.IsNullOrWhiteSpace(task.NameKo) ? task.Name : task.NameKo!,
        AppLanguage.JA => string.IsNullOrWhiteSpace(task.NameJa) ? task.Name : task.NameJa!,
        _ => task.Name
    };

    /// <summary>
    /// Returns the quest name plus an optional English subtitle for KO/JA list display.
    /// EN: (Name, "", false). KO/JA with a translation: (localized, Name, true). Otherwise (Name, "", false).
    /// </summary>
    public (string DisplayName, string Subtitle, bool ShowSubtitle) GetQuestDisplayName(TarkovTask task)
        => GetQuestDisplayName(CurrentLanguage, task);

    /// <summary>Pure, testable core of <see cref="GetQuestDisplayName(TarkovTask)"/>.</summary>
    public static (string DisplayName, string Subtitle, bool ShowSubtitle) GetQuestDisplayName(AppLanguage lang, TarkovTask task)
    {
        if (lang == AppLanguage.EN)
            return (task.Name, string.Empty, false);

        var localized = lang switch
        {
            AppLanguage.KO => task.NameKo,
            AppLanguage.JA => task.NameJa,
            _ => null
        };

        return string.IsNullOrWhiteSpace(localized)
            ? (task.Name, string.Empty, false)
            : (localized!, task.Name, true);
    }

    #endregion

    #region In-Progress Quest Input

    public string InProgressQuestInputButton => CurrentLanguage switch
    {
        AppLanguage.KO => "진행중 퀘스트 입력",
        AppLanguage.JA => "進行中クエスト入力",
        _ => "Enter In-Progress Quests"
    };

    public string InProgressQuestInputTitle => CurrentLanguage switch
    {
        AppLanguage.KO => "진행중 퀘스트 입력",
        AppLanguage.JA => "進行中クエスト入力",
        _ => "Enter In-Progress Quests"
    };

    public string QuestSelection => CurrentLanguage switch
    {
        AppLanguage.KO => "퀘스트 선택",
        AppLanguage.JA => "クエスト選択",
        _ => "Quest Selection"
    };

    public string SearchQuestsPlaceholder => CurrentLanguage switch
    {
        AppLanguage.KO => "퀘스트 검색...",
        AppLanguage.JA => "クエスト検索...",
        _ => "Search quests..."
    };

    public string TraderFilter => CurrentLanguage switch
    {
        AppLanguage.KO => "트레이더:",
        AppLanguage.JA => "トレーダー:",
        _ => "Trader:"
    };

    public string AllTraders => CurrentLanguage switch
    {
        AppLanguage.KO => "전체",
        AppLanguage.JA => "全て",
        _ => "All"
    };

    /// <summary>
    /// Detail-panel notice shown when the displayed quest is not in the filtered
    /// quest list (see QuestListPage.UpdateFilteredOutNotice).
    /// </summary>
    public string QuestHiddenByFilters => CurrentLanguage switch
    {
        AppLanguage.KO => "이 퀘스트는 현재 필터에서 목록에 표시되지 않습니다.",
        AppLanguage.JA => "このクエストは現在のフィルターではリストに表示されていません。",
        _ => "This quest is hidden by the current filters."
    };

    /// <summary>Button beside <see cref="QuestHiddenByFilters"/>: resets the filters.</summary>
    public string ShowInList => CurrentLanguage switch
    {
        AppLanguage.KO => "목록에 표시",
        AppLanguage.JA => "リストに表示",
        _ => "Show in list"
    };

    public string PrerequisitesPreview => CurrentLanguage switch
    {
        AppLanguage.KO => "선행 퀘스트 미리보기",
        AppLanguage.JA => "先行クエストプレビュー",
        _ => "Prerequisites Preview"
    };

    public string PrerequisitesDescription => CurrentLanguage switch
    {
        AppLanguage.KO => "체크된 퀘스트의 선행 퀘스트가 여기에 표시됩니다.\n적용 시 자동으로 완료 처리됩니다.",
        AppLanguage.JA => "選択されたクエストの先行クエストがここに表示されます。\n適用時に自動完了されます。",
        _ => "Prerequisites of selected quests will be shown here.\nThese will be auto-completed on apply."
    };

    public string SelectedQuestsCount => CurrentLanguage switch
    {
        AppLanguage.KO => "선택된 퀘스트: {0}개",
        AppLanguage.JA => "選択されたクエスト: {0}件",
        _ => "Selected quests: {0}"
    };

    public string PrerequisitesToComplete => CurrentLanguage switch
    {
        AppLanguage.KO => "자동 완료될 선행 퀘스트: {0}개",
        AppLanguage.JA => "自動完了される先行クエスト: {0}件",
        _ => "Prerequisites to complete: {0}"
    };

    public string QuestDataNotLoaded => CurrentLanguage switch
    {
        AppLanguage.KO => "퀘스트 데이터가 로드되지 않았습니다. 먼저 데이터를 새로고침 해주세요.",
        AppLanguage.JA => "クエストデータがロードされていません。まずデータを更新してください。",
        _ => "Quest data is not loaded. Please refresh data first."
    };

    public string NoQuestsSelected => CurrentLanguage switch
    {
        AppLanguage.KO => "선택된 퀘스트가 없습니다.",
        AppLanguage.JA => "選択されたクエストがありません。",
        _ => "No quests selected."
    };

    public string QuestsAppliedSuccess => CurrentLanguage switch
    {
        AppLanguage.KO => "{0}개의 퀘스트가 Active로 설정되고, {1}개의 선행 퀘스트가 완료 처리되었습니다.",
        AppLanguage.JA => "{0}件のクエストがActiveに設定され、{1}件の先行クエストが完了処理されました。",
        _ => "{0} quest(s) set to Active, {1} prerequisite(s) marked as completed."
    };

    #endregion

    #region Quest List Page

    /// <summary>
    /// Empty-state title shown in the quest list when the current filters match zero
    /// quests (see QuestListPage.ApplyFilters / PnlEmptyState).
    /// </summary>
    public string QuestListEmptyTitle => CurrentLanguage switch
    {
        AppLanguage.KO => "조건에 맞는 퀘스트가 없습니다",
        AppLanguage.JA => "条件に一致するクエストがありません",
        _ => "No quests match the current filters"
    };

    /// <summary>Empty-state hint line under <see cref="QuestListEmptyTitle"/>.</summary>
    public string QuestListEmptyHint => CurrentLanguage switch
    {
        AppLanguage.KO => "검색어 또는 필터를 조정해 보세요",
        AppLanguage.JA => "検索語またはフィルターを調整してください",
        _ => "Adjust the search text or the filters above"
    };

    /// <summary>Empty-state button: resets every filter (QuestListPage.ResetFilters).</summary>
    public string ResetFiltersButton => CurrentLanguage switch
    {
        AppLanguage.KO => "필터 초기화",
        AppLanguage.JA => "フィルターをリセット",
        _ => "Reset Filters"
    };

    #endregion

    #region Quest Recommendations

    public string RecommendedQuests => CurrentLanguage switch
    {
        AppLanguage.KO => "추천 퀘스트",
        AppLanguage.JA => "おすすめクエスト",
        _ => "Recommended Quests"
    };

    public string ReadyToComplete => CurrentLanguage switch
    {
        AppLanguage.KO => "지금 완료 가능",
        AppLanguage.JA => "今すぐ完了可能",
        _ => "Ready to Complete"
    };

    public string ItemHandInOnly => CurrentLanguage switch
    {
        AppLanguage.KO => "아이템 제출만",
        AppLanguage.JA => "アイテム提出のみ",
        _ => "Item Hand-in Only"
    };

    public string KappaPriority => CurrentLanguage switch
    {
        AppLanguage.KO => "카파 필수",
        AppLanguage.JA => "Kappa必須",
        _ => "Kappa Priority"
    };

    public string UnlocksMany => CurrentLanguage switch
    {
        AppLanguage.KO => "다수 해금",
        AppLanguage.JA => "複数解放",
        _ => "Unlocks Many"
    };

    public string EasyQuest => CurrentLanguage switch
    {
        AppLanguage.KO => "쉬운 퀘스트",
        AppLanguage.JA => "簡単なクエスト",
        _ => "Easy Quest"
    };

    public string NoRecommendations => CurrentLanguage switch
    {
        AppLanguage.KO => "현재 추천 퀘스트가 없습니다",
        AppLanguage.JA => "現在おすすめクエストはありません",
        _ => "No recommendations at this time"
    };

    public string ItemsOwned => CurrentLanguage switch
    {
        AppLanguage.KO => "보유",
        AppLanguage.JA => "所持",
        _ => "owned"
    };

    public string ItemsNeeded => CurrentLanguage switch
    {
        AppLanguage.KO => "필요",
        AppLanguage.JA => "必要",
        _ => "needed"
    };

    public string UnlocksQuests => CurrentLanguage switch
    {
        AppLanguage.KO => "개 퀘스트 해금",
        AppLanguage.JA => "クエスト解放",
        _ => "quest(s) unlock"
    };

    #endregion

    #region Quest Complete Cascade Confirmation

    // Strings for QuestCompleteConfirmDialog (shown before a completion whose
    // cascade would auto-complete prerequisites or auto-fail alternatives).
    // The dialog's Cancel button reuses the Core "Cancel" property.

    public string CascadeConfirmTitle => CurrentLanguage switch
    {
        AppLanguage.KO => "퀘스트 완료 확인",
        AppLanguage.JA => "クエスト完了の確認",
        _ => "Confirm Quest Completion"
    };

    /// <summary>{0} = the clicked quest's localized name.</summary>
    public string CascadeConfirmQuestFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "'{0}' 완료 시 아래 퀘스트도 함께 변경됩니다.",
        AppLanguage.JA => "「{0}」を完了すると、以下のクエストも変更されます。",
        _ => "Completing '{0}' will also change the quests below."
    };

    /// <summary>{0} = number of prerequisites that will be auto-completed.</summary>
    public string CascadeCompletedHeaderFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "함께 완료될 퀘스트 ({0}개)",
        AppLanguage.JA => "同時に完了されるクエスト ({0}件)",
        _ => "Will also be completed ({0})"
    };

    /// <summary>{0} = number of mutually exclusive alternatives that will be auto-failed.</summary>
    public string CascadeFailedHeaderFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "실패 처리될 퀘스트 ({0}개)",
        AppLanguage.JA => "失敗になるクエスト ({0}件)",
        _ => "Will be FAILED ({0})"
    };

    public string CascadeFailedNote => CurrentLanguage switch
    {
        AppLanguage.KO => "상호 배타적인 퀘스트이므로 실패로 처리됩니다.",
        AppLanguage.JA => "相互排他のクエストのため、失敗として処理されます。",
        _ => "These quests are mutually exclusive and will be marked as failed."
    };

    public string CascadeConfirmButton => CurrentLanguage switch
    {
        AppLanguage.KO => "퀘스트 완료",
        AppLanguage.JA => "クエストを完了",
        _ => "Complete Quest"
    };

    #endregion

    #region Log Sync Summary

    // The sync dialog reports what was written rather than asking the player to confirm it.
    // With attribution derived from the logs, the profiles a run wrote to are the one thing a
    // player cannot work out for themselves, so the summary always names them (PRD R2).

    public string SyncSummaryTitle => CurrentLanguage switch
    {
        AppLanguage.KO => "퀘스트 동기화 완료",
        AppLanguage.JA => "クエスト同期完了",
        _ => "Quest Sync Complete"
    };

    public string SyncAppliedHeader => CurrentLanguage switch
    {
        AppLanguage.KO => "프로필별 반영 결과",
        AppLanguage.JA => "プロフィール別の反映結果",
        _ => "Applied by profile"
    };

    /// <summary>
    /// {0} = number of quest records written to one profile. Phrased to avoid a singular/plural
    /// split, which none of the three languages can express from one format string.
    /// </summary>
    public string SyncAppliedCountFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "{0}개 반영",
        AppLanguage.JA => "{0}件 反映",
        _ => "{0} recorded"
    };

    public string SyncAppliedNone => CurrentLanguage switch
    {
        AppLanguage.KO => "변경된 퀘스트가 없습니다.",
        AppLanguage.JA => "変更されたクエストはありません。",
        _ => "No quests changed."
    };

    /// <summary>
    /// {0} = events found, {1} = already up to date, {2} = prerequisites auto-completed,
    /// {3} = quests still in progress, {4} = events with no game mode evidence,
    /// {5} = unmatched quest IDs.
    /// </summary>
    public string SyncStatsFormat => CurrentLanguage switch
    {
        AppLanguage.KO =>
            "로그에서 발견된 이벤트: {0}\n" +
            "이미 최신 상태인 퀘스트: {1}\n" +
            "자동 완료된 선행 퀘스트: {2}\n" +
            "아직 진행중인 퀘스트: {3}\n" +
            "게임 모드를 알 수 없어 제외된 이벤트: {4}\n" +
            "매칭 실패한 퀘스트 ID: {5}",
        AppLanguage.JA =>
            "ログで見つかったイベント: {0}\n" +
            "すでに最新のクエスト: {1}\n" +
            "自動完了した前提クエスト: {2}\n" +
            "まだ進行中のクエスト: {3}\n" +
            "ゲームモードが不明で除外したイベント: {4}\n" +
            "マッチング失敗したクエストID: {5}",
        _ =>
            "Events found in logs: {0}\n" +
            "Already up to date: {1}\n" +
            "Prerequisites auto-completed: {2}\n" +
            "Still in progress: {3}\n" +
            "Skipped, no game mode in logs: {4}\n" +
            "Unmatched quest IDs: {5}"
    };

    /// <summary>{0} = number of mutually exclusive groups awaiting a choice.</summary>
    public string SyncAlternativesHeaderFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "선택이 필요한 퀘스트 - 그룹당 하나 선택 ({0}개 그룹)",
        AppLanguage.JA => "選択が必要なクエスト - グループごとに1つ選択 ({0}グループ)",
        _ => "Choose one per group ({0} groups)"
    };

    /// <summary>{0} = localized profile name, {1} = the mutually exclusive quest names.</summary>
    public string SyncAlternativeGroupFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "{0}: {1} 중 하나 선택",
        AppLanguage.JA => "{0}: {1} から1つ選択",
        _ => "{0} - choose one: {1}"
    };

    public string SyncSummaryConfirmButton => CurrentLanguage switch
    {
        AppLanguage.KO => "확인",
        AppLanguage.JA => "確認",
        _ => "OK"
    };

    public string SyncSummarySkipButton => CurrentLanguage switch
    {
        AppLanguage.KO => "선택 건너뛰기",
        AppLanguage.JA => "選択をスキップ",
        _ => "Skip"
    };

    /// <summary>{0} = number of quest records written by the alternative-quest choices.</summary>
    public string SyncAlternativesAppliedFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "선택한 퀘스트 {0}개가 반영되었습니다.",
        AppLanguage.JA => "選択したクエスト {0}件を反映しました。",
        _ => "{0} quest records were applied from your choices."
    };

    /// <summary>{0} = the localized names of the profiles whose write failed.</summary>
    public string SyncApplyFailedFormat => CurrentLanguage switch
    {
        AppLanguage.KO => "저장하지 못한 프로필: {0}. 로그를 확인한 뒤 다시 동기화해 주세요.",
        AppLanguage.JA => "保存できなかったプロフィール: {0}。ログを確認して再度同期してください。",
        _ => "Could not save to: {0}. Check the log and run the sync again."
    };

    #endregion
}
