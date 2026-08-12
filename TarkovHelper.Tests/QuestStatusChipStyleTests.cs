using System.IO;
using System.Text.RegularExpressions;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the two properties of QuestListPage.xaml's StatusChipStyle that a reader
/// cannot verify from the C# side and that a screenshot would not obviously flag:
/// the hover cue is tinted from the chip's OWN status color, and it does not fade
/// the chip's label. Both matter more than usual now that the chip row is the quest
/// tab's only status filter (feature-quest-chip-only-status-filter.md).
///
/// Asserted against the XAML source text rather than a loaded ControlTemplate, the
/// same approach FontAssetsTests uses for asset declarations, and it keeps these
/// tests out of the E2E category (no app launch, no STA thread).
/// </summary>
public sealed class QuestStatusChipStyleTests
{
    private static string StatusChipStyleXaml()
    {
        var xaml = File.ReadAllText(
            Path.Combine(TestRepo.Root(), "TarkovHelper", "Pages", "QuestListPage.xaml"));
        var start = xaml.IndexOf("<Style x:Key=\"StatusChipStyle\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "StatusChipStyle not found in QuestListPage.xaml");
        var end = xaml.IndexOf("</Style>", start, StringComparison.Ordinal);
        Assert.True(end > start, "StatusChipStyle is not closed");
        return xaml[start..end];
    }

    [Fact]
    public void The_chip_hover_cue_is_tinted_from_the_chips_own_status_color()
    {
        var style = StatusChipStyleXaml();
        var ring = Regex.Match(style, "<Border x:Name=\"ChipHoverRing\".*?/>", RegexOptions.Singleline).Value;

        Assert.False(string.IsNullOrEmpty(ring), "the chip template should declare a ChipHoverRing layer");
        // TemplateBinding Foreground, not a neutral theme resource: the chip's
        // Foreground is the single declaration of its status color (UpdateStatusChips
        // never writes it), so the hover cue cannot drift away from the chip's identity,
        // and a neutral hover fill would visually "unfill" the selected chip.
        Assert.Contains("{TemplateBinding Foreground}", ring);
        // Hidden at rest; only the IsMouseOver trigger raises it.
        Assert.Contains("Opacity=\"0\"", ring);
    }

    [Fact]
    public void The_chip_hover_cue_never_fades_the_chip_label()
    {
        var trigger = Regex.Match(StatusChipStyleXaml(),
            "<Trigger Property=\"IsMouseOver\".*?</Trigger>", RegexOptions.Singleline).Value;

        Assert.False(string.IsNullOrEmpty(trigger), "the chip template should have an IsMouseOver trigger");
        // Opacity composites the whole visual subtree, so setting it on ChipBorder (the
        // ContentPresenter's ancestor) fades the label too: ChipLocked's already-low
        // 3.78:1 contrast drops to 3.09:1 and the chip reads as disabled rather than
        // hovered. The hover layers are siblings of the label instead.
        Assert.DoesNotContain("TargetName=\"ChipBorder\"", trigger);
        Assert.Contains("TargetName=\"ChipHover", trigger);
    }
}
