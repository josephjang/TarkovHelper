using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Pages.Components;

/// <summary>
/// The popup listing every flagged Kappa quest with a done or undone glyph, its localized name
/// and its trader, under a header carrying the count. Opened from Collector's detail pane on the
/// quest tab and from the Collector page's unlock panel, each with its own render pass, so the
/// list is the same list from either place (feature-kappa-collector-1-1.spec.md, TD5).
/// <para>
/// Built in code rather than XAML, as the inline version in QuestListPage was: the rows are data,
/// and there is nothing to declare beyond a scroll viewer and a stack. Sealed and reached through
/// <see cref="Show"/> only, so a caller cannot open one without the count and the rows that go
/// with it. The theme brushes are resolved through the window itself, which walks up to
/// App.xaml's dictionary the way the page's FindResource did.
/// </para>
/// </summary>
internal sealed class KappaQuestListWindow : Window
{
    private KappaQuestListWindow(
        Window? owner,
        IReadOnlyList<(TarkovTask Quest, bool IsDone)> quests,
        int completed,
        int total,
        Func<TarkovTask, string> displayName,
        LocalizationService loc)
    {
        Title = loc.KappaProgressHeading;
        Width = 500;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Owner = owner;
        Background = Theme("BackgroundDarkBrush");

        var stackPanel = new StackPanel { Margin = new Thickness(16) };

        stackPanel.Children.Add(new TextBlock
        {
            Text = string.Format(loc.KappaQuestListTitle, completed, total),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme("AccentBrush"),
            Margin = new Thickness(0, 0, 0, 16)
        });

        foreach (var (quest, isDone) in quests)
        {
            stackPanel.Children.Add(Row(quest, isDone, displayName(quest)));
        }

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = stackPanel
        };
    }

    /// <summary>
    /// Opens the list modally over <paramref name="owner"/>.
    /// </summary>
    /// <param name="owner">The window to centre on; null centres on the screen.</param>
    /// <param name="quests">
    /// The flagged quests with their done state, in the order to list them (the order
    /// <see cref="QuestGraphService.GetKappaQuestsWithStatus"/> answers: still to do first).
    /// </param>
    /// <param name="completed">Done count for the header, from the same pass as the rows.</param>
    /// <param name="total">Flagged count for the header.</param>
    /// <param name="displayName">A quest's name in the app's language.</param>
    /// <param name="loc">The strings source, for the title and the header.</param>
    internal static void Show(
        Window? owner,
        IReadOnlyList<(TarkovTask Quest, bool IsDone)> quests,
        int completed,
        int total,
        Func<TarkovTask, string> displayName,
        LocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(loc);

        new KappaQuestListWindow(owner, quests, completed, total, displayName, loc).ShowDialog();
    }

    /// <summary>One quest's row: the glyph, the name (struck through when done), the trader.</summary>
    private StackPanel Row(TarkovTask quest, bool isDone, string displayName)
    {
        var secondary = Theme("TextSecondaryBrush");
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 4)
        };

        row.Children.Add(new TextBlock
        {
            Text = isDone ? "✓" : "○",
            FontSize = 14,
            Foreground = isDone ? QuestStatusBrushes.Done : secondary,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center
        });

        row.Children.Add(new TextBlock
        {
            Text = displayName,
            FontSize = 13,
            Foreground = isDone ? secondary : Theme("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextDecorations = isDone ? TextDecorations.Strikethrough : null
        });

        row.Children.Add(new TextBlock
        {
            Text = $"  ({quest.Trader})",
            FontSize = 11,
            Foreground = secondary,
            VerticalAlignment = VerticalAlignment.Center
        });

        return row;
    }

    private Brush Theme(string key) => (Brush)FindResource(key);
}
