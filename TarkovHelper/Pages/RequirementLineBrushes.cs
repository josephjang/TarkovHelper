using System.Windows;
using System.Windows.Media;

namespace TarkovHelper.Pages;

/// <summary>
/// The two colours a requirement line is painted in: the theme's text colour when the condition
/// is met, and the locked orange when it is still holding the quest. One value rather than a
/// <c>metBrush</c>/<c>unmetBrush</c> pair spelled at each call site, which is how the quest page
/// and the Collector page came to name the same resource key and the same brush twice.
/// <para>
/// A value read from an element, NOT static brushes beside
/// <see cref="QuestStatusBrushes"/>. Two reasons, and both are why the obvious home is the wrong
/// one: "TextPrimaryBrush" is an App.xaml resource, so resolving it needs a live element and a
/// dispatcher - <see cref="QuestStatusBrushes"/> is deliberately frozen constants readable from
/// any thread - and the builders take these brushes so a test can hand in two sentinels and
/// assert which one a line got by reference. Static resolution would destroy that seam.
/// </para>
/// </summary>
internal readonly record struct RequirementLineBrushes(Brush Met, Brush Unmet)
{
    /// <summary>
    /// The palette as <paramref name="owner"/>'s resource lookup answers it: the theme's primary
    /// text colour for a met line, and the badge's own LevelLocked orange for an unmet one, so a
    /// line and the badge above it say "still holding" in the same hue.
    /// </summary>
    internal static RequirementLineBrushes FromResources(FrameworkElement owner)
        => new((Brush)owner.FindResource("TextPrimaryBrush"), QuestStatusBrushes.LevelLocked);
}
