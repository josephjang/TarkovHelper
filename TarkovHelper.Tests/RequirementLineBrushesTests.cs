using TarkovHelper.Pages;
using TarkovHelper.Models;
using Colors = System.Windows.Media.Colors;
using FrameworkElement = System.Windows.FrameworkElement;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace TarkovHelper.Tests;

/// <summary>
/// The palette a requirement line is painted from (<see cref="RequirementLineBrushes"/>): the
/// theme's text colour for a met condition, the badge's LevelLocked orange for an unmet one.
/// <para>
/// The quest page and the Collector page each spelled that pair at their own call site, naming
/// the same resource key and the same brush twice, in the right order twice. The value is what
/// makes a third surface unable to get it subtly wrong; these cases are what would notice if the
/// value itself did - a swapped pair or a renamed key reads perfectly in review.
/// </para>
/// </summary>
// StaThread.Run joins a thread to build the FrameworkElement WPF requires an STA thread for.
[Collection(SchedulingSensitiveCollection.Name)]
public sealed class RequirementLineBrushesTests
{
    [Fact]
    public void The_palette_reads_the_theme_text_colour_for_met_and_the_locked_orange_for_unmet()
    {
        var sentinel = new SolidColorBrush(Colors.Magenta);

        var brushes = StaThread.Run(() =>
        {
            var owner = new FrameworkElement();
            // The key both pages used to spell at their own call site.
            owner.Resources["TextPrimaryBrush"] = sentinel;
            return RequirementLineBrushes.FromResources(owner);
        });

        Assert.Same(sentinel, brushes.Met);
        // The unmet half is the status colour, not a second theme lookup: the line and the badge
        // above it say "still holding" in the same hue.
        Assert.Same(QuestStatusBrushes.For(QuestStatus.LevelLocked), brushes.Unmet);
    }

    [Fact]
    public void An_owner_whose_theme_has_no_such_key_fails_loudly_rather_than_painting_nothing()
    {
        // The error path, and the reason the met half cannot be a static readonly: it is resolved
        // against a live element. A missing key has to throw here rather than hand back a null
        // brush that paints every met line invisible.
        Assert.Throws<System.Windows.ResourceReferenceKeyNotFoundException>(
            () => StaThread.Run(() => RequirementLineBrushes.FromResources(new FrameworkElement())));
    }
}
