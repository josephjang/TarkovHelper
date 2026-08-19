using System.Reflection;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Completeness guard for the header/tab/settings strings added by the top-bar
/// redesign (LocalizationService.Header.cs): every key must resolve to a non-empty,
/// non-placeholder string in all three languages, and format keys must keep their
/// {0} slot. Also pins how the title-bar update pill assembles those strings
/// (<see cref="HeaderUpdatePill"/>), where the spoken name and the visible label
/// have to agree.
/// </summary>
public class LocalizationHeaderStringsTests
{
    private static readonly string[] HeaderKeys =
    {
        // Tabs
        "TabQuests", "TabHideout", "TabItems", "TabCollector", "TabMap",
        // Title bar
        "HeaderPvpZone", "HeaderPveZone", "HeaderPvpSeason",
        "HeaderPvpTooltip", "HeaderPveTooltip", "HeaderPvpSeasonTooltip",
        "HeaderActiveProfile", "HeaderProfileMenuTooltip", "HeaderProfileChangedFromLogsFormat",
        "HeaderProfileSourceManual", "HeaderProfileSourceAutomatic",
        "HeaderProfileSelected", "HeaderProfileUnselected",
        "HeaderProfileTooltip", "HeaderProfileName", "HeaderLevelShort", "HeaderVersionTooltipIdle",
        "HeaderVersionTooltipInstall", "HeaderVersionTooltipCheckFailed",
        "HeaderUpdateAvailableFormat", "HeaderChecking",
        // Superseded-build escalation of the update pill (feature-versioned-data-channel.md)
        "HeaderUpdateForDataLabel", "HeaderUpdateForDataTooltipFormat", "UpdateStatusDataEnded",
        // Sync status chip
        "SyncStatusOff", "SyncStatusWatching", "SyncStatusMatching", "SyncStatusInRaid",
        "SyncStatusTooltip",
        // Profile drawer
        "ProfileLevelLabel", "ProfileScavRepLabel", "ProfileDspLabel",
        "ProfileEditionLabel", "ProfilePrestigeLabel",
        // Settings pre-existing rows (migrated from inline switches; the overlay
        // title reuses the Core "Settings" property)
        "Settings", "SettingsLogFolderLabel", "SettingsLogFolderDesc",
        "SettingsAutoDetectButton", "SettingsBrowseButton", "SettingsResetLogFolderButton",
        // Settings additions
        "SettingsLanguageLabel", "SettingsSupportLabel", "SettingsSupportDesc",
        "SettingsSupportButton", "SettingsUpdateLabel", "SettingsCurrentVersionFormat",
        "SettingsCheckUpdateButton", "SettingsUpdateToFormat", "UpdateStatusUpToDate",
        "UpdateStatusAvailable", "UpdateStatusFailed", "TimeJustNow",
        "TimeMinutesAgoFormat", "SettingsDangerZoneLabel", "SettingsResetProgressDesc",
        "SettingsResetProgressButton",
        // Profile reset dialog (feature-complete-profile-reset.md)
        "ProfileResetDialogTitle", "ProfileResetTargetFormat", "ProfileResetCategories",
        "ProfileResetSurvivorsNote", "ProfileResetRaidWarning", "ProfileResetConfirmButtonFormat",
        "ProfileResetWorking", "ProfileResetSuccessFormat", "ProfileResetFailedText",
        "ProfileResetAbandonedText",
    };

    private static readonly string[] FormatKeys =
    {
        "HeaderProfileChangedFromLogsFormat", "HeaderVersionTooltipInstall", "HeaderUpdateAvailableFormat",
        "HeaderUpdateForDataTooltipFormat",
        "SettingsCurrentVersionFormat", "SettingsUpdateToFormat", "TimeMinutesAgoFormat",
        "ProfileResetTargetFormat", "ProfileResetSuccessFormat", "ProfileResetConfirmButtonFormat",
    };

    private static string GetString(LocalizationService loc, string key)
    {
        var prop = typeof(LocalizationService).GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
        Assert.True(prop != null, $"LocalizationService has no public property '{key}'");
        return (string)prop!.GetValue(loc)!;
    }

    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void Every_header_key_is_nonempty_and_not_a_placeholder(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);
        foreach (var key in HeaderKeys)
        {
            var value = GetString(loc, key);
            Assert.False(string.IsNullOrWhiteSpace(value), $"'{key}' is empty for {language}");
            Assert.DoesNotContain("TBD", value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void Format_keys_keep_their_argument_slot(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);
        foreach (var key in FormatKeys)
        {
            Assert.Contains("{0}", GetString(loc, key));
        }
    }

    // The abandoned-reset text exists precisely because it cannot promise what the failed text
    // promises ("nothing was removed"). A translation that copied one into the other would put
    // that promise back in front of a player whose transaction may well have committed.
    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void The_abandoned_reset_text_is_not_a_copy_of_the_failed_text(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);

        Assert.NotEqual(loc.ProfileResetFailedText, loc.ProfileResetAbandonedText);
    }

    [Fact]
    public void Tab_labels_are_translated_for_korean_and_japanese()
    {
        var en = TestLocalization.WithLanguage(AppLanguage.EN);
        var ko = TestLocalization.WithLanguage(AppLanguage.KO);
        var ja = TestLocalization.WithLanguage(AppLanguage.JA);

        // Spot-check that the tab strip actually changes language (DSP/Lv are
        // legitimately language-invariant, so only tabs are asserted here).
        foreach (var key in new[] { "TabQuests", "TabHideout", "TabItems", "TabCollector", "TabMap" })
        {
            Assert.NotEqual(GetString(en, key), GetString(ko, key));
            Assert.NotEqual(GetString(en, key), GetString(ja, key));
        }
    }

    #region Update pill (title bar)

    private const string PillVersion = "v2026.8.0";

    // WCAG 2.5.3 Label in Name: speech input activates a control by the words printed on
    // it, so the pill's UIA Name must be its visible label and not the sentence-long
    // tooltip. Both pill branches, all three languages.
    [Theory]
    [InlineData(AppLanguage.EN, false)]
    [InlineData(AppLanguage.EN, true)]
    [InlineData(AppLanguage.KO, false)]
    [InlineData(AppLanguage.KO, true)]
    [InlineData(AppLanguage.JA, false)]
    [InlineData(AppLanguage.JA, true)]
    public void The_update_pill_name_is_its_visible_label(AppLanguage language, bool isSuperseded)
    {
        var loc = TestLocalization.WithLanguage(language);

        var pill = HeaderUpdatePill.For(loc, PillVersion, isSuperseded);

        Assert.False(string.IsNullOrWhiteSpace(pill.Label));
        Assert.Equal(pill.Label, pill.AutomationName);
    }

    // The explanation is not dropped when it stops being the name: it keeps its version
    // slot and moves to HelpText, the UIA slot for text too long to be a name.
    [Theory]
    [InlineData(AppLanguage.EN, false)]
    [InlineData(AppLanguage.EN, true)]
    [InlineData(AppLanguage.KO, false)]
    [InlineData(AppLanguage.KO, true)]
    [InlineData(AppLanguage.JA, false)]
    [InlineData(AppLanguage.JA, true)]
    public void The_update_pill_explanation_becomes_help_text(AppLanguage language, bool isSuperseded)
    {
        var loc = TestLocalization.WithLanguage(language);

        var pill = HeaderUpdatePill.For(loc, PillVersion, isSuperseded);

        Assert.Equal(pill.Description, pill.HelpText);
        Assert.Contains(PillVersion, pill.HelpText);
        Assert.NotEqual(pill.Label, pill.HelpText);
    }

    // The exact spoken strings, including the two the tooltip-as-name shape got wrong: JA
    // announced "クリックして更新 ... をインストール" over a pill reading "v2026.8.0 に更新", and a
    // superseded EN pill announced a whole paragraph over "Update for latest data".
    [Theory]
    [InlineData(AppLanguage.EN, false, "Update v2026.8.0")]
    [InlineData(AppLanguage.KO, false, "v2026.8.0 업데이트")]
    [InlineData(AppLanguage.JA, false, "v2026.8.0 に更新")]
    [InlineData(AppLanguage.EN, true, "Update for latest data")]
    [InlineData(AppLanguage.KO, true, "데이터 갱신하려면 업데이트")]
    [InlineData(AppLanguage.JA, true, "最新データには更新が必要")]
    public void The_update_pill_announces_the_words_on_the_button(
        AppLanguage language, bool isSuperseded, string expectedName)
    {
        var loc = TestLocalization.WithLanguage(language);

        var pill = HeaderUpdatePill.For(loc, PillVersion, isSuperseded);

        Assert.Equal(expectedName, pill.AutomationName);
    }

    // A superseded build must not read like ordinary optional maintenance: the escalation
    // is the whole point of the branch, so label and explanation both have to change.
    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void The_superseded_pill_differs_from_the_ordinary_one(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);

        var ordinary = HeaderUpdatePill.For(loc, PillVersion, isSuperseded: false);
        var superseded = HeaderUpdatePill.For(loc, PillVersion, isSuperseded: true);

        Assert.NotEqual(ordinary.Label, superseded.Label);
        Assert.NotEqual(ordinary.Description, superseded.Description);
    }

    // The tone is the other half of that escalation and is decided here, beside the wording,
    // so MainWindow only renders it. Pinned in all three languages because it is the same
    // branch that picks the words: a future language-specific branch must not pick a tone
    // that disagrees with them.
    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void The_superseded_pill_asks_for_the_warning_tone(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);

        Assert.Equal(HeaderUpdatePillTone.Success,
            HeaderUpdatePill.For(loc, PillVersion, isSuperseded: false).Tone);
        Assert.Equal(HeaderUpdatePillTone.Warning,
            HeaderUpdatePill.For(loc, PillVersion, isSuperseded: true).Tone);
    }

    [Fact]
    public void The_update_pill_rejects_a_missing_localization_source()
        => Assert.Throws<ArgumentNullException>(
            () => HeaderUpdatePill.For(null!, PillVersion, isSuperseded: false));

    #endregion

    [Theory]
    [InlineData(AppLanguage.EN, "PvP Zone", "PvE Zone", "PvP Season")]
    [InlineData(AppLanguage.KO, "PvP 존", "PvE 존", "시즌 PvP")]
    [InlineData(AppLanguage.JA, "PvP ゾーン", "PvE ゾーン", "PvP シーズン")]
    public void Profile_labels_match_the_game(
        AppLanguage language,
        string expectedPvp,
        string expectedPve,
        string expectedSeason)
    {
        var loc = TestLocalization.WithLanguage(language);

        Assert.Equal(expectedPvp, loc.HeaderPvpZone);
        Assert.Equal(expectedPve, loc.HeaderPveZone);
        Assert.Equal(expectedSeason, loc.HeaderPvpSeason);
    }
}
