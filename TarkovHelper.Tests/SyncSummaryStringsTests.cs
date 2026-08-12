using System.Reflection;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Completeness guard for the sync summary strings (LocalizationService.Quest.cs). The summary
/// is a PRD requirement, not decoration: with the per-quest review step gone it is the only
/// signal a player gets about which profiles a sync wrote to, so a missing translation or a
/// dropped format slot degrades the requirement rather than just the polish.
/// </summary>
public sealed class SyncSummaryStringsTests
{
    private static readonly string[] SummaryKeys =
    {
        "SyncSummaryTitle", "SyncAppliedHeader", "SyncAppliedCountFormat", "SyncAppliedNone",
        "SyncStatsFormat", "SyncAlternativesHeaderFormat", "SyncAlternativeGroupFormat",
        "SyncSummaryConfirmButton", "SyncSummarySkipButton", "SyncAlternativesAppliedFormat",
        "SyncApplyFailedFormat",
    };

    /// <summary>Key to the highest {n} slot its callers pass, so a dropped slot fails here.</summary>
    private static readonly (string Key, int Slots)[] FormatKeys =
    {
        ("SyncAppliedCountFormat", 1),
        ("SyncStatsFormat", 6),
        ("SyncAlternativesHeaderFormat", 1),
        ("SyncAlternativeGroupFormat", 2),
        ("SyncAlternativesAppliedFormat", 1),
        ("SyncApplyFailedFormat", 1),
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
    public void Every_summary_key_is_nonempty_and_not_a_placeholder(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);
        foreach (var key in SummaryKeys)
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
    public void Format_keys_keep_every_argument_slot(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);
        foreach (var (key, slots) in FormatKeys)
        {
            var value = GetString(loc, key);
            for (var slot = 0; slot < slots; slot++)
            {
                Assert.Contains($"{{{slot}}}", value);
            }
        }
    }

    // Every profile must have a display name in every language: the summary names the profiles
    // it wrote to, and an unnamed one would leave the player unable to act on the report.
    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void Every_profile_has_a_display_name(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);
        var names = Enum.GetValues<AppProfile>().Select(loc.ProfileName).ToArray();

        Assert.All(names, name => Assert.False(string.IsNullOrWhiteSpace(name)));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    // Matches ProfileService's profile-keyed maps: an unmapped profile must throw rather than
    // borrow another profile's name and tell the player their data went somewhere it did not.
    [Fact]
    public void An_unmapped_profile_has_no_display_name()
    {
        var loc = TestLocalization.WithLanguage(AppLanguage.EN);

        Assert.Throws<ArgumentOutOfRangeException>(() => loc.ProfileName((AppProfile)99));
    }
}
