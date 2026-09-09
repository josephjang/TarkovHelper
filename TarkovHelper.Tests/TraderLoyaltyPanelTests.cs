using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TarkovHelper.Pages;
using TarkovHelper.Pages.Components;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;
using static TarkovHelper.Tests.LoyaltyFixtures;
using static TarkovHelper.Tests.SettingsServiceTestSupport;
// The test project also references WinForms (for TarkovDBEditor); disambiguate the colliding
// System.Drawing and System.Windows.Forms types, as ProfileDrawerFitTests does.
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Orientation = System.Windows.Controls.Orientation;

namespace TarkovHelper.Tests;

/// <summary>
/// The profile drawer's trader loyalty inputs, now that they live in
/// <see cref="TraderLoyaltyPanel"/> rather than in MainWindow's code-behind: the controls it
/// builds from a roster, the highlight it paints from the settings snapshot, the click it turns
/// into an edit, and the one repaint a whole published fan-out is allowed to cost.
/// <para>
/// Reachable as a unit for the first time here. The panel is a plain class over two hosts it is
/// handed, so a WPF element tree on an STA thread is all it needs - no application, no window,
/// no database. The window's own wiring is a source scan at the bottom, for the same reason
/// <see cref="MainWindowTeardownTests"/> is one: MainWindow cannot be constructed in this suite.
/// </para>
/// </summary>
// Each case joins an STA thread, which occupies a worker without yielding it;
// SchedulingSensitiveCollectionTests requires the attribute for exactly that.
[Collection(SchedulingSensitiveCollection.Name)]
public sealed class TraderLoyaltyPanelTests
{
    /// <summary>A roster in the shape QuestDbService.LoyaltyTraders hands the drawer.</summary>
    private static IReadOnlyList<LoyaltyTrader> Roster(params (string Id, string Name)[] traders)
        => traders
            .Select(t => new LoyaltyTrader(t.Id, t.Name, t.Name.ToLowerInvariant()))
            .ToList();

    private static readonly (string Id, string Name) PraporTrader = (Prapor, "Prapor");
    private static readonly (string Id, string Name) SkierTrader = (Skier, "Skier");
    private static readonly (string Id, string Name) TherapistTrader = (Therapist, "Therapist");

    /// <summary>
    /// The panel over a drawer section built the way MainWindow.xaml declares it: a collapsed
    /// container holding the WrapPanel the groups go into. The brushes the panel resolves are
    /// App.xaml's, seeded on the container so the lookup walks up to them exactly as it does in
    /// the running app.
    /// </summary>
    private static (TraderLoyaltyPanel Panel, StackPanel Section, WrapPanel Host) NewPanel(
        SettingsService settings)
    {
        var host = new WrapPanel { Orientation = Orientation.Horizontal };
        var section = new StackPanel { Visibility = Visibility.Collapsed };
        section.Children.Add(host);

        section.Resources["AccentBrush"] = Brushes.Orange;
        section.Resources["BackgroundMediumBrush"] = Brushes.DimGray;
        section.Resources["BackgroundDarkBrush"] = Brushes.Black;
        section.Resources["TextPrimaryBrush"] = Brushes.White;
        section.Resources["TextSecondaryBrush"] = Brushes.LightGray;
        section.Resources["FontSizeXSmall"] = 10.0;
        section.Resources["FontSizeSmall"] = 12.0;

        var panel = new TraderLoyaltyPanel(
            section, host, settings, TestLocalization.WithLanguage(AppLanguage.EN));
        return (panel, section, host);
    }

    /// <summary>Every level button one group carries, in the order the panel built them.</summary>
    private static List<Button> ButtonsOf(WrapPanel host, int groupIndex)
        => ((StackPanel)((Border)host.Children[groupIndex]).Child)
            .Children.OfType<Button>()
            .ToList();

    private static TextBlock LabelOf(WrapPanel host, int groupIndex)
        => ((StackPanel)((Border)host.Children[groupIndex]).Child).Children.OfType<TextBlock>().First();

    /// <summary>
    /// Delivers a whole published fan-out the way a profile switch does. Reached by reflection
    /// because the raiser is private, and driven with the snapshot the service already holds
    /// because every announcement is guarded on that being the live one.
    /// </summary>
    private static void RaiseProfileSettingsChanged(
        SettingsService service, ProfileSettingsSnapshot snapshot)
    {
        var method = typeof(SettingsService).GetMethod(
            "RaiseProfileSettingsChanged", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(method != null, "SettingsService has no RaiseProfileSettingsChanged");
        method!.Invoke(service, new object?[] { snapshot });
    }

    #region The controls the roster builds

    [Fact]
    public void Rebuild_builds_one_group_per_trader_with_the_ids_the_e2e_addresses()
    {
        OnStaThread(() =>
        {
            var (panel, section, host) = NewPanel(NewService(Settings()));

            panel.Rebuild(Roster(PraporTrader, SkierTrader));

            Assert.True(panel.HasTraders);
            Assert.Equal(Visibility.Visible, section.Visibility);
            Assert.Equal(2, host.Children.Count);

            // The automation ids are a pure function of the trader's normalized name, and the
            // e2e addresses every one of them by hand. A scheme change is a broken e2e.
            Assert.Equal("Loyalty_prapor", AutomationProperties.GetAutomationId(LabelOf(host, 0)));
            Assert.Equal("Prapor", LabelOf(host, 0).Text);

            var buttons = ButtonsOf(host, 0);
            Assert.Equal(
                SettingsService.MaxTraderLoyaltyLevel - SettingsService.MinTraderLoyaltyLevel + 1,
                buttons.Count);
            for (var i = 0; i < buttons.Count; i++)
            {
                var level = SettingsService.MinTraderLoyaltyLevel + i;
                Assert.Equal(level.ToString(), buttons[i].Content);
                Assert.Equal(
                    $"Loyalty_prapor_{level}", AutomationProperties.GetAutomationId(buttons[i]));
            }

            Assert.Equal("Loyalty_skier_1", AutomationProperties.GetAutomationId(ButtonsOf(host, 1)[0]));
        });
    }

    [Fact]
    public void An_empty_roster_collapses_the_section_it_would_have_filled()
    {
        OnStaThread(() =>
        {
            var (panel, section, host) = NewPanel(NewService(Settings()));
            panel.Rebuild(Roster(PraporTrader));
            Assert.Equal(Visibility.Visible, section.Visibility);

            // What a database published before the 1.1 refresh looks like. The heading lives in
            // the same section, so it must go with the groups rather than be left naming nothing.
            panel.Rebuild(Roster());

            Assert.Equal(Visibility.Collapsed, section.Visibility);
            Assert.Empty(host.Children);
            Assert.False(panel.HasTraders);
        });
    }

    [Fact]
    public void Rebuild_replaces_the_previous_roster_rather_than_appending_to_it()
    {
        OnStaThread(() =>
        {
            var (panel, _, host) = NewPanel(NewService(Settings()));

            panel.Rebuild(Roster(PraporTrader, SkierTrader));
            panel.Rebuild(Roster(TherapistTrader));

            Assert.Single(host.Children);
            Assert.Equal(
                "Loyalty_therapist_1", AutomationProperties.GetAutomationId(ButtonsOf(host, 0)[0]));
        });
    }

    [Fact]
    public void Rebuild_rejects_a_null_roster()
    {
        OnStaThread(() =>
        {
            var (panel, _, _) = NewPanel(NewService(Settings()));
            Assert.Throws<ArgumentNullException>(() => panel.Rebuild(null!));
        });
    }

    #endregion

    #region The highlight

    [Fact]
    public void Repaint_selects_the_entered_level_and_leaves_the_rest_unselected()
    {
        OnStaThread(() =>
        {
            var service = NewService(Settings(loyalty: new[] { (Prapor, 3) }));
            var (panel, _, host) = NewPanel(service);

            // Painted by Rebuild itself, before the drawer is ever opened.
            panel.Rebuild(Roster(PraporTrader, SkierTrader));

            var prapor = ButtonsOf(host, 0);
            Assert.Equal(QuestStatusTags.ChipSelected, AutomationProperties.GetItemStatus(prapor[2]));
            Assert.Equal(Brushes.Orange, prapor[2].Background);
            foreach (var other in prapor.Where(b => b != prapor[2]))
            {
                Assert.Equal(QuestStatusTags.ChipUnselected, AutomationProperties.GetItemStatus(other));
                Assert.Equal(Brushes.DimGray, other.Background);
            }

            // A trader the profile has no row for reads the default, not "nothing selected".
            var skier = ButtonsOf(host, 1);
            Assert.Equal(
                QuestStatusTags.ChipSelected,
                AutomationProperties.GetItemStatus(skier[SettingsService.DefaultTraderLoyaltyLevel - 1]));
        });
    }

    [Fact]
    public void Repaint_moves_the_highlight_of_every_group_not_just_the_one_that_changed()
    {
        OnStaThread(() =>
        {
            var entered = Settings(loyalty: new[] { (Prapor, 4), (Skier, 3) });
            var service = NewService(entered);
            var (panel, _, host) = NewPanel(service);
            panel.Rebuild(Roster(PraporTrader, SkierTrader));

            // A profile switch to one that entered nothing: both rows have to fall back, or a
            // wiped trader keeps the previous profile's highlight beside a freshly painted one.
            TestReflection.SetPrivateField(service, "_profileSettings", Settings());
            panel.Repaint();

            foreach (var groupIndex in new[] { 0, 1 })
            {
                var buttons = ButtonsOf(host, groupIndex);
                Assert.Equal(
                    QuestStatusTags.ChipSelected,
                    AutomationProperties.GetItemStatus(
                        buttons[SettingsService.DefaultTraderLoyaltyLevel - 1]));
                Assert.Equal(QuestStatusTags.ChipUnselected,
                    AutomationProperties.GetItemStatus(buttons[^1]));
            }
        });
    }

    [Fact]
    public void Every_button_carries_a_tooltip_naming_its_trader_its_level_and_the_entered_one()
    {
        OnStaThread(() =>
        {
            var service = NewService(Settings(loyalty: new[] { (Prapor, 2) }));
            var (panel, _, host) = NewPanel(service);

            panel.Rebuild(Roster(PraporTrader));

            // Composed on every repaint rather than at build time, because it names a value that
            // changes with every edit.
            var tooltip = (string)ButtonsOf(host, 0)[3].ToolTip;
            Assert.Contains("Prapor", tooltip, StringComparison.Ordinal);
            Assert.Contains("4", tooltip, StringComparison.Ordinal);
            Assert.Contains("2", tooltip, StringComparison.Ordinal);
        });
    }

    #endregion

    #region The click

    [Fact]
    public void A_click_records_the_level_its_own_button_stands_for()
    {
        OnStaThread(() =>
        {
            // No store: the write path's own catch logs the failure and the in-memory graft still
            // runs, which is the half this asserts. Durability is SettingsSetterContractTests'.
            var service = NewService(Settings(loyalty: new[] { (Prapor, 1) }));
            var (panel, _, host) = NewPanel(service);
            panel.Rebuild(Roster(PraporTrader, SkierTrader));

            var changes = new List<TraderLoyaltyChange>();
            service.TraderLoyaltyChanged += (_, change) => changes.Add(change);

            ButtonsOf(host, 1)[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            // The trader and the level of the button that was clicked, not of the first group or
            // of the button's position in some other row.
            Assert.Equal(3, service.GetTraderLoyalty(Skier));
            Assert.Equal(1, service.GetTraderLoyalty(Prapor));
            var change = Assert.Single(changes);
            Assert.Equal(Skier, change.TraderId);
            Assert.Equal(3, change.Level);
        });
    }

    [Fact]
    public void A_click_raised_while_the_parent_suppresses_input_records_nothing()
    {
        OnStaThread(() =>
        {
            var service = NewService(Settings(loyalty: new[] { (Prapor, 1) }));
            var (panel, _, host) = NewPanel(service);
            panel.Rebuild(Roster(PraporTrader));

            // The window raises this while the drawer is being written FROM the settings service
            // and during the startup load, so a handler those writes wake writes nothing back.
            var suppressed = true;
            panel.IsInputSuppressed = () => suppressed;

            ButtonsOf(host, 0)[3].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(1, service.GetTraderLoyalty(Prapor));

            // ...and the same click lands once the guard comes down, so the zero above is the
            // guard and not a click that never reached the handler.
            suppressed = false;
            ButtonsOf(host, 0)[3].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(4, service.GetTraderLoyalty(Prapor));
        });
    }

    #endregion

    #region One published reload, one repaint

    /// <summary>
    /// Stands in for <c>Dispatcher.BeginInvoke(..., DispatcherPriority.Background)</c>: the
    /// callback is queued, not run, until the test drains it. That is the case that matters,
    /// since the whole point is that the burst finishes arriving before the repaint runs.
    /// </summary>
    private sealed class QueueingScheduler
    {
        private readonly List<Action> _queued = new();

        public int ScheduleCount { get; private set; }

        public void Schedule(Action callback)
        {
            ScheduleCount++;
            _queued.Add(callback);
        }

        public void Drain()
        {
            var pending = _queued.ToArray();
            _queued.Clear();
            foreach (var callback in pending) callback();
        }
    }

    /// <summary>
    /// The snapshot a seven-trader profile carries, cut down to the three the panel is built for.
    /// Every entry announces an event of its own on publish, which is the burst under test.
    /// </summary>
    private static ProfileSettingsSnapshot ThreeEnteredLevels()
        => Settings(loyalty: new[] { (Prapor, 3), (Skier, 2), (Therapist, 4) });

    [Fact]
    public void A_published_fan_out_repaints_the_drawer_once_not_once_per_stored_entry()
    {
        OnStaThread(() =>
        {
            var snapshot = ThreeEnteredLevels();
            var service = NewService(snapshot);
            var (panel, _, _) = NewPanel(service);
            panel.Rebuild(Roster(PraporTrader, SkierTrader, TherapistTrader));

            var scheduler = new QueueingScheduler();
            var coalescer = new RefreshCoalescer(panel.Repaint, scheduler.Schedule);
            var announced = 0;

            // MainWindow's two loyalty handlers, wired as MainWindow wires them.
            service.TraderLoyaltyChanged += (_, _) => { announced++; coalescer.Request(); };
            service.ProfileSettingsReloaded += (_, _) => { announced++; coalescer.Request(); };

            var paintsBefore = panel.RepaintCount;
            RaiseProfileSettingsChanged(service, snapshot);

            // The burst really is a burst: one event per stored entry, then the closing signal.
            Assert.Equal(4, announced);
            Assert.Equal(1, scheduler.ScheduleCount);
            // Nothing has repainted yet: the refresh is posted, so the rest of the burst arrives
            // first. A repaint that ran inline here would coalesce nothing at all.
            Assert.Equal(paintsBefore, panel.RepaintCount);

            scheduler.Drain();

            Assert.Equal(paintsBefore + 1, panel.RepaintCount);
        });
    }

    [Fact]
    public void The_same_fan_out_repaints_once_per_event_when_the_handlers_do_not_coalesce()
    {
        OnStaThread(() =>
        {
            // The shape this replaced, kept as the control: without the coalescer the four
            // announcements of ONE snapshot are four full repaints of the drawer.
            var snapshot = ThreeEnteredLevels();
            var service = NewService(snapshot);
            var (panel, _, _) = NewPanel(service);
            panel.Rebuild(Roster(PraporTrader, SkierTrader, TherapistTrader));

            service.TraderLoyaltyChanged += (_, _) => panel.Repaint();
            service.ProfileSettingsReloaded += (_, _) => panel.Repaint();

            var paintsBefore = panel.RepaintCount;
            RaiseProfileSettingsChanged(service, snapshot);

            Assert.Equal(paintsBefore + 4, panel.RepaintCount);
        });
    }

    [Fact]
    public void A_single_edit_still_repaints_once_through_the_coalescer()
    {
        OnStaThread(() =>
        {
            // The other half of the collapse: one player click raises one event, and coalescing
            // must not swallow it.
            var service = NewService(Settings(loyalty: new[] { (Prapor, 1) }));
            var (panel, _, host) = NewPanel(service);
            panel.Rebuild(Roster(PraporTrader));

            var scheduler = new QueueingScheduler();
            var coalescer = new RefreshCoalescer(panel.Repaint, scheduler.Schedule);
            service.TraderLoyaltyChanged += (_, _) => coalescer.Request();

            var paintsBefore = panel.RepaintCount;
            ButtonsOf(host, 0)[3].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            scheduler.Drain();

            Assert.Equal(paintsBefore + 1, panel.RepaintCount);
            Assert.Equal(
                QuestStatusTags.ChipSelected,
                AutomationProperties.GetItemStatus(ButtonsOf(host, 0)[3]));
        });
    }

    #endregion

    #region What MainWindow wires

    private static string MainWindowSource() =>
        File.ReadAllText(Path.Combine(TestRepo.Root(), "TarkovHelper", "MainWindow.xaml.cs"));

    /// <summary>
    /// The window itself cannot be constructed here (it needs the whole app), so the wiring the
    /// cases above prove correct is pinned over the source: both loyalty handlers have to book
    /// the SAME coalescer, or the burst they share splits back into a repaint each.
    /// </summary>
    [Theory]
    [InlineData("private void OnTraderLoyaltyChanged(")]
    [InlineData("private void OnProfileSettingsReloaded(")]
    public void Both_loyalty_handlers_book_the_shared_coalescer(string signature)
    {
        var source = MainWindowSource();
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"MainWindow.xaml.cs no longer contains '{signature}'");
        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of '{signature}'");
        var body = source[start..end];

        Assert.Contains("_loyaltyRefresh.Request();", body, StringComparison.Ordinal);
        // A blocking dispatch here is the shape that could not coalesce: it runs the repaint
        // inline on the UI thread, clearing the pending flag before the rest of the burst lands.
        Assert.DoesNotContain("Dispatcher.Invoke", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_coalescer_is_built_on_this_windows_dispatcher_around_the_drawer_repaint()
    {
        var source = MainWindowSource();

        Assert.Contains(
            "_loyaltyRefresh = RefreshCoalescer.OnDispatcher(this, UpdateLoyaltyUI);",
            source, StringComparison.Ordinal);

        // Built in the constructor BODY: a field initializer runs before the base constructor,
        // where this.Dispatcher is not yet available.
        var constructor = source.IndexOf("public MainWindow()", StringComparison.Ordinal);
        Assert.True(constructor >= 0, "MainWindow.xaml.cs no longer declares a parameterless constructor");
        Assert.True(
            source.IndexOf("_loyaltyRefresh = RefreshCoalescer", constructor, StringComparison.Ordinal) > 0,
            "the coalescer is built before the constructor body, where Dispatcher is not set yet");
    }

    /// <summary>
    /// The panel is passive: it subscribes to nothing, so the drawer's three subscriptions stay
    /// in MainWindow where <see cref="MainWindowTeardownTests"/> can see them detached. A panel
    /// that grew its own subscription would leave that guard reading a file the handler is no
    /// longer in.
    /// </summary>
    [Fact]
    public void The_panel_subscribes_to_nothing()
    {
        var source = File.ReadAllText(Path.Combine(
            TestRepo.Root(), "TarkovHelper", "Pages", "Components", "TraderLoyaltyPanel.cs"));

        var subscriptions = Regex.Matches(source, @"\w+\.\w+\s*\+=\s*\w+;")
            .Select(m => m.Value)
            .Where(s => !s.Contains("button.Click", StringComparison.Ordinal))
            .ToList();

        Assert.True(subscriptions.Count == 0,
            "TraderLoyaltyPanel subscribes to something other than its own buttons, so it now " +
            "owns a lifetime MainWindow's teardown does not cover:\n"
            + string.Join("\n", subscriptions));
    }

    #endregion

    /// <summary>
    /// Runs <paramref name="body"/> on an STA thread, as WPF element construction requires, and
    /// rethrows whatever it threw on the caller's thread with its stack intact.
    /// </summary>
    private static void OnStaThread(Action body)
    {
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                // Element construction spins up a dispatcher for this thread; without this it
                // outlives the thread and every case leaks one.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        failure?.Throw();
    }
}
