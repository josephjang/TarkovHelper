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
/// <see cref="Show"/> only, which takes the rows and the strings source and nothing else: the
/// header's count is counted FROM the rows (<see cref="HeaderText"/>) and each name is resolved
/// from the same strings source, so no caller can hand the window a count or a name that does not
/// belong to the list it is showing. The theme brushes are resolved through the window itself,
/// which walks up to App.xaml's dictionary the way the page's FindResource did.
/// </para>
/// </summary>
internal sealed class KappaQuestListWindow : Window
{
    private KappaQuestListWindow(
        Window? owner,
        IReadOnlyList<(TarkovTask Quest, bool IsDone)> quests,
        LocalizationService loc)
    {
        Title = loc.KappaProgressHeading;
        Width = 500;
        Height = 600;
        WindowStartupLocation = StartupLocationFor(owner);
        Owner = owner;
        Background = Theme("BackgroundDarkBrush");

        var stackPanel = new StackPanel { Margin = new Thickness(16) };

        stackPanel.Children.Add(new TextBlock
        {
            Text = HeaderText(quests, loc),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme("AccentBrush"),
            Margin = new Thickness(0, 0, 0, 16)
        });

        foreach (var (quest, isDone) in quests)
        {
            stackPanel.Children.Add(Row(quest, isDone, loc.GetQuestName(quest)));
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
    /// <see cref="QuestGraphService.GetKappaQuestsWithStatus"/> answers: still to do first). The
    /// header's count is these rows counted, so the caller passes no numbers of its own.
    /// </param>
    /// <param name="loc">
    /// The strings source, for the title, the header and each quest's name in the app's language.
    /// </param>
    internal static void Show(
        Window? owner,
        IReadOnlyList<(TarkovTask Quest, bool IsDone)> quests,
        LocalizationService loc)
    {
        ArgumentNullException.ThrowIfNull(quests);
        ArgumentNullException.ThrowIfNull(loc);

        new KappaQuestListWindow(owner, quests, loc).ShowDialog();
    }

    /// <summary>
    /// Where the window opens: centred on its owner, or centred on the screen when it has none.
    /// <para>
    /// WPF's CenterOwner is not owner-null tolerant. With no owner it does not fall back to
    /// centring on anything: the placement is left to the OS, which cascades each new window
    /// down and to the right of the last one, so two openings land in two different places. The
    /// owner is nullable here because a caller may not have a window (a detached control, a
    /// designer host), and that caller still gets a deliberately placed window.
    /// </para>
    /// </summary>
    internal static WindowStartupLocation StartupLocationFor(Window? owner)
        => owner == null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;

    /// <summary>
    /// The header over the rows: how many of them are done, out of how many there are, in the
    /// app's language.
    /// <para>
    /// Counted from the rows themselves. The pair used to be passed in beside them, having been
    /// asked of <see cref="QuestGraphService.GetKappaProgress"/> over the same pass and the same
    /// flagged set the rows came from, so the header agreed with the list only by the caller's
    /// good behaviour, and every caller walked the quests a second time to get it.
    /// </para>
    /// </summary>
    internal static string HeaderText(
        IReadOnlyList<(TarkovTask Quest, bool IsDone)> quests, LocalizationService loc)
        => string.Format(loc.KappaQuestListTitle, quests.Count(entry => entry.IsDone), quests.Count);

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
