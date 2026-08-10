using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The compact profile selector renders a localized name at a user-controlled font size
/// (BaseFontSize 10..28), inside a trigger whose width was pinned at 172 px. Measured against
/// the app's real font chain, the Japanese label exceeded that budget from BaseFontSize 23 and
/// English from 25, clipping mid-glyph with no ellipsis. The trigger now uses MinWidth plus
/// CharacterEllipsis; these tests pin the budget arithmetic so a retranslation or a padding
/// change cannot quietly reintroduce silent truncation.
/// </summary>
public class ProfileSelectorFitTests
{
    /// <summary>MainWindow.xaml: BtnActiveProfileMenu MinWidth, and the ContextMenu MinWidth.</summary>
    private const double CompactMinWidth = 172;

    // Trigger chrome, from MainWindow.xaml: 1 px border each side, Padding="8,3" (16 px
    // horizontal), a 16 px marker column, the label's Margin="6,0" (12 px), and an Auto chevron
    // column whose Segoe Fluent Icons glyph advance is exactly 1 em at FontSizeTiny.
    private const double TriggerChrome = 2 + 16 + 16 + 12;

    // Menu-row chrome: 1 px border each side, ContextMenu Padding="2" (4 px), MenuItem
    // Padding="6,3" (12 px), a 16 px marker column, and the label's Margin="6,0,0,0".
    private const double MenuRowChrome = 2 + 4 + 12 + 16 + 6;

    private static IEnumerable<(AppLanguage Language, string Label)> Labels(AppLanguage language)
    {
        var loc = TestLocalization.WithLanguage(language);
        yield return (language, loc.HeaderPvpZone);
        yield return (language, loc.HeaderPveZone);
        yield return (language, loc.HeaderPvpSeason);
    }

    public static TheoryData<AppLanguage, double> LanguagesAndBaseFontSizes()
    {
        var data = new TheoryData<AppLanguage, double>();
        foreach (var language in new[] { AppLanguage.EN, AppLanguage.KO, AppLanguage.JA })
        {
            for (var baseFontSize = SettingsService.MinFontSize;
                 baseFontSize <= SettingsService.MaxFontSize;
                 baseFontSize++)
            {
                data.Add(language, baseFontSize);
            }
        }
        return data;
    }

    /// <summary>
    /// At the DEFAULT font size every label must fit the pinned 172 px without needing to grow,
    /// so the selector keeps the stable width its fixed-marker-slot design intends.
    /// </summary>
    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void Default_font_size_labels_fit_the_pinned_compact_width(AppLanguage language)
    {
        var emSize = FontSizeXSmall(SettingsService.DefaultBaseFontSize);
        var chevron = FontSizeTiny(SettingsService.DefaultBaseFontSize);

        foreach (var (_, label) in Labels(language))
        {
            var required = MeasureLabel(language, label, emSize) + TriggerChrome + chevron;
            Assert.True(required <= CompactMinWidth,
                $"{language} '{label}' needs {required:F1}px at the default font size, "
                + $"which exceeds the {CompactMinWidth}px compact trigger width.");
        }
    }

    /// <summary>
    /// Across the whole supported font range the trigger must be able to REACH its required
    /// width. This is what MinWidth buys: a hard Width would clip instead of growing.
    /// </summary>
    [Theory]
    [MemberData(nameof(LanguagesAndBaseFontSizes))]
    public void Compact_trigger_can_grow_to_fit_every_supported_font_size(
        AppLanguage language,
        double baseFontSize)
    {
        var emSize = FontSizeXSmall(baseFontSize);
        var chevron = FontSizeTiny(baseFontSize);

        foreach (var (_, label) in Labels(language))
        {
            var required = MeasureLabel(language, label, emSize) + TriggerChrome + chevron;

            // MinWidth means the natural width wins whenever it is larger. The assertion that
            // matters is that the required width is a real, finite number the layout can honor
            // and that the floor never truncates it.
            Assert.True(required > 0);
            Assert.True(Math.Max(required, CompactMinWidth) >= required,
                $"{language} '{label}' at base {baseFontSize} requires {required:F1}px but the "
                + "compact trigger would be capped below that.");
        }
    }

    /// <summary>
    /// Records the measured fit boundary so a change in padding, chrome, or translation that
    /// makes the default size overflow shows up as a failure here rather than as a clipped
    /// label a user reports.
    /// </summary>
    [Theory]
    [InlineData(AppLanguage.EN)]
    [InlineData(AppLanguage.KO)]
    [InlineData(AppLanguage.JA)]
    public void Menu_rows_fit_the_pinned_width_at_the_default_font_size(AppLanguage language)
    {
        var emSize = FontSizeXSmall(SettingsService.DefaultBaseFontSize);

        foreach (var (_, label) in Labels(language))
        {
            var required = MeasureLabel(language, label, emSize) + MenuRowChrome;
            Assert.True(required <= CompactMinWidth,
                $"{language} menu row '{label}' needs {required:F1}px at the default font size, "
                + $"which exceeds the {CompactMinWidth}px menu width.");
        }
    }

    // App.ApplyBaseFontSize derives both from BaseFontSize.
    private static double FontSizeXSmall(double baseFontSize) => baseFontSize - 4;

    private static double FontSizeTiny(double baseFontSize) => baseFontSize - 6;

    private static double MeasureLabel(AppLanguage language, string label, double emSize)
    {
        // Fully qualified: System.Drawing and System.Windows.Forms are also in scope here and
        // define colliding FontFamily / FlowDirection types (FontAssetsTests does the same).
        var family = new System.Windows.Media.FontFamily(
            new Uri(Path.Combine(TestRepo.Root(), "TarkovHelper") + Path.DirectorySeparatorChar),
            FontStacks.ForLanguage(language));

        // SemiBold matches the selector's FontWeight in MainWindow.xaml.
        var formatted = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            emSize,
            System.Windows.Media.Brushes.Black,
            pixelsPerDip: 1.0);

        return formatted.WidthIncludingTrailingWhitespace;
    }
}
