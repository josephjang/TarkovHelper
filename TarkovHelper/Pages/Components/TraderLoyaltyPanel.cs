using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using TarkovHelper.Services;

namespace TarkovHelper.Pages.Components;

/// <summary>
/// The profile drawer's trader loyalty inputs: one bordered group per trader the loaded quest
/// data gates on, each a trader label plus one button per level, in the shape the DSP control
/// already uses.
/// <para>
/// A PASSIVE panel, the way <see cref="QuestRecommendationsPanel"/> is: it subscribes to no
/// service and owns no lifecycle. MainWindow keeps the three subscriptions that drive it
/// (<c>SettingsService.TraderLoyaltyChanged</c>, <c>SettingsService.ProfileSettingsReloaded</c>
/// and <c>QuestDbService.DataRefreshed</c>) together with the matching detaches
/// <c>MainWindowTeardownTests</c> reads out of MainWindow.xaml.cs, and calls
/// <see cref="Rebuild"/> and <see cref="Repaint"/> from its own handlers. Nothing here reaches
/// back into the window: the parent's "these controls are being written, not clicked" guard
/// arrives as <see cref="IsInputSuppressed"/>.
/// </para>
/// <para>
/// A plain class rather than a UserControl because this widget has no markup to declare: the
/// roster is DATA, so every control below is built in code (the day a publish starts gating a
/// quest on a trader that is not in the roster today, the drawer grows on the next quest load
/// with no app change). What markup there is - the section that carries the heading, and the
/// WrapPanel the groups go into - stays in MainWindow.xaml, which is where
/// <c>ProfileDrawerFitTests</c> measures the real drawer at the minimum window.
/// </para>
/// </summary>
public sealed class TraderLoyaltyPanel
{
    /// <summary>
    /// One trader's input group: the trader it stands for and the level buttons whose highlight
    /// <see cref="Repaint"/> paints. Held rather than looked up by name because the controls do
    /// not exist in XAML.
    /// <para>
    /// Each button is paired with the level it stands for rather than left to be re-derived from
    /// its position, so the level a button paints is the level its click writes, by construction.
    /// </para>
    /// </summary>
    private sealed record LoyaltyInputGroup(
        LoyaltyTrader Trader, IReadOnlyList<(int Level, Button Button)> LevelButtons);

    /// <summary>Which trader and level a loyalty button stands for.</summary>
    private sealed record LoyaltyButtonTag(string TraderId, int Level);

    /// <summary>
    /// The container holding the heading and <see cref="_host"/>, shown and hidden as one: a
    /// heading left over an empty row would name controls that are not there.
    /// </summary>
    private readonly FrameworkElement _section;

    /// <summary>The panel the trader groups are built into, and the resource lookup host.</summary>
    private readonly Panel _host;

    private readonly SettingsService _settings;
    private readonly LocalizationService _loc;
    private readonly List<LoyaltyInputGroup> _groups = new();

    /// <param name="section">The drawer section shown only while the roster has traders in it.</param>
    /// <param name="host">The panel the trader groups are built into.</param>
    /// <param name="settings">Where an entered level is read from and written to.</param>
    /// <param name="localization">Supplies the trader names and the tooltip format.</param>
    public TraderLoyaltyPanel(
        FrameworkElement section, Panel host, SettingsService settings, LocalizationService localization)
    {
        _section = section ?? throw new ArgumentNullException(nameof(section));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _loc = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    /// <summary>
    /// The parent's input guard: while it answers true, the buttons are being written BY the
    /// settings service (or by the startup load) rather than clicked by the player, so a click
    /// handler woken by those writes must write nothing back. Left null when the parent has no
    /// such state, which means "every click is the player's".
    /// </summary>
    public Func<bool>? IsInputSuppressed { get; set; }

    /// <summary>How many times <see cref="Repaint"/> has run.</summary>
    /// <remarks>
    /// The seam the coalescing test asserts on: a repaint is idempotent, so "the burst of loyalty
    /// events collapsed into ONE repaint" cannot be read off the controls themselves.
    /// </remarks>
    public int RepaintCount { get; private set; }

    /// <summary>Whether the last <see cref="Rebuild"/> found any trader to build a group for.</summary>
    public bool HasTraders => _groups.Count > 0;

    /// <summary>
    /// Rebuilds the inputs from <paramref name="traders"/>: one bordered group per trader, in the
    /// order given, each a label plus the buttons from
    /// <see cref="SettingsService.MinTraderLoyaltyLevel"/> to
    /// <see cref="SettingsService.MaxTraderLoyaltyLevel"/>, and then paints them.
    /// <para>
    /// The section is collapsed entirely when the roster is empty, which is what a database
    /// published before the 1.1 refresh looks like.
    /// </para>
    /// </summary>
    public void Rebuild(IReadOnlyList<LoyaltyTrader> traders)
    {
        ArgumentNullException.ThrowIfNull(traders);

        _host.Children.Clear();
        _groups.Clear();

        if (traders.Count == 0)
        {
            _section.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var trader in traders)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var label = new TextBlock
            {
                Text = _loc.GetTraderDisplayName(trader.TraderId, trader.TraderName),
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)_host.FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            label.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeXSmall");
            AutomationProperties.SetAutomationId(label, LabelAutomationId(trader));
            row.Children.Add(label);

            var buttons = new List<(int Level, Button Button)>();

            for (var level = SettingsService.MinTraderLoyaltyLevel;
                 level <= SettingsService.MaxTraderLoyaltyLevel;
                 level++)
            {
                var button = new Button
                {
                    Content = level.ToString(),
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    FontWeight = FontWeights.Bold,
                    // The trader and the level this button stands for, read back by the click
                    // handler: one handler for every button, as BtnDsp_Click already is.
                    Tag = new LoyaltyButtonTag(trader.TraderId, level),
                    Margin = level == SettingsService.MinTraderLoyaltyLevel
                        ? new Thickness(0)
                        : new Thickness(2, 0, 0, 0),
                    // No ToolTip here: it names the entered level, which changes after every
                    // edit, so Repaint composes it for every button on every pass. The Repaint()
                    // below runs before this panel is ever shown.
                };
                button.SetResourceReference(Control.FontSizeProperty, "FontSizeSmall");
                AutomationProperties.SetAutomationId(button, ButtonAutomationId(trader, level));
                button.Click += LevelButton_Click;

                buttons.Add((level, button));
                row.Children.Add(button);
            }

            _host.Children.Add(new Border
            {
                Background = (Brush)_host.FindResource("BackgroundMediumBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 8, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Child = row,
            });

            _groups.Add(new LoyaltyInputGroup(trader, buttons));
        }

        _section.Visibility = Visibility.Visible;

        // Paints the highlight and composes every button's tooltip, before the drawer is opened.
        Repaint();
    }

    /// <summary>
    /// Repaints every group: the entered level highlighted the way the DSP control's selection
    /// is, and published to UI Automation as ItemStatus Selected/Unselected, which is the chip
    /// convention and the only surface the e2e can read a button's selection from.
    /// <para>
    /// Repaints EVERY group rather than one named trader: a published reload announces one event
    /// per stored entry, so repainting only the named trader would leave a wiped trader's old
    /// highlight on screen beside a freshly painted one.
    /// </para>
    /// </summary>
    public void Repaint()
    {
        RepaintCount++;

        if (_groups.Count == 0) return;

        var accent = (Brush)_host.FindResource("AccentBrush");
        var medium = (Brush)_host.FindResource("BackgroundMediumBrush");
        var primaryText = (Brush)_host.FindResource("TextPrimaryBrush");
        var darkText = (Brush)_host.FindResource("BackgroundDarkBrush");

        foreach (var group in _groups)
        {
            var entered = _settings.GetTraderLoyalty(group.Trader.TraderId);

            foreach (var (level, button) in group.LevelButtons)
            {
                var isSelected = level == entered;

                button.Background = isSelected ? accent : medium;
                button.Foreground = isSelected ? darkText : primaryText;
                AutomationProperties.SetItemStatus(
                    button,
                    isSelected ? QuestStatusTags.ChipSelected : QuestStatusTags.ChipUnselected);
                button.ToolTip = string.Format(_loc.RequirementLoyaltyFormat,
                    _loc.GetTraderDisplayName(group.Trader.TraderId, group.Trader.TraderName),
                    level, entered);
            }
        }
    }

    /// <summary>The automation id of one trader's group label, as the e2e addresses it.</summary>
    private static string LabelAutomationId(LoyaltyTrader trader)
        => $"Loyalty_{trader.NormalizedName}";

    /// <summary>The automation id of one level button within a trader's group.</summary>
    private static string ButtonAutomationId(LoyaltyTrader trader, int level)
        => $"Loyalty_{trader.NormalizedName}_{level}";

    /// <summary>
    /// Handles a level button click. One handler for every trader's every button; the button's
    /// Tag says which.
    /// </summary>
    private void LevelButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsInputSuppressed?.Invoke() == true) return;

        if (sender is Button { Tag: LoyaltyButtonTag tag })
        {
            _settings.SetTraderLoyalty(tag.TraderId, tag.Level);
        }
    }
}
