using System.Reflection;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Completeness guard for the header/tab/settings strings added by the top-bar
/// redesign (LocalizationService.Header.cs): every key must resolve to a non-empty,
/// non-placeholder string in all three languages, and format keys must keep their
/// {0} slot.
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
        "HeaderProfileTooltip", "HeaderProfileName", "HeaderLevelShort", "HeaderVersionTooltipIdle",
        "HeaderVersionTooltipInstall", "HeaderVersionTooltipCheckFailed",
        "HeaderUpdateAvailableFormat", "HeaderChecking",
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
    };

    private static readonly string[] FormatKeys =
    {
        "HeaderProfileChangedFromLogsFormat", "HeaderVersionTooltipInstall", "HeaderUpdateAvailableFormat",
        "SettingsCurrentVersionFormat", "SettingsUpdateToFormat", "TimeMinutesAgoFormat",
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
