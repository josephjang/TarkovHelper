using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services;
// The test project also references WinForms (for TarkovDBEditor); disambiguate the colliding
// System.Drawing types this file measures geometry in, as FontAssetsTests does.
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Control = System.Windows.Controls.Control;
using Orientation = System.Windows.Controls.Orientation;

namespace TarkovHelper.Tests;

/// <summary>
/// The height sibling of <see cref="ProfileSelectorFitTests"/>: the profile drawer now carries a
/// loyalty stepper per gating trader on top of the five groups it already had, and it hangs from
/// the title bar with nothing bounding it. Measured against the app's own resources, the drawer
/// outgrows the 400 px minimum window from around base font 20 (where exactly depends on how much
/// the display's scaling leaves of the client area), and a row laid out past the window edge
/// cannot be clicked, tabbed to, or scrolled to: the controls are simply gone.
/// <para>
/// These cases arrange the real drawer markup at the minimum window and assert the invariant the
/// player depends on - every loyalty group is either inside the window or reachable by scrolling.
/// </para>
/// <para>
/// The drawer is parsed out of MainWindow.xaml and dressed in App.xaml's own resource dictionary
/// rather than measured through a running app: WPF allows one Application per AppDomain and
/// <see cref="AppFontSwapTests"/> already constructs it, so a second construction here would
/// break whichever of the two ran second. Nothing in this file needs an Application - a resource
/// dictionary in the element tree resolves StaticResource and DynamicResource just as well.
/// </para>
/// </summary>
// Each case joins an STA thread to lay the drawer out, which occupies a worker without yielding
// it; SchedulingSensitiveCollectionTests requires the attribute for exactly that.
[Collection(SchedulingSensitiveCollection.Name)]
public sealed class ProfileDrawerFitTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml2006 =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>MainWindow.xaml MinWidth/MinHeight: the smallest window a player can drag to.</summary>
    private const double MinWindowWidth = 600;
    private const double MinWindowHeight = 400;

    /// <summary>The x:Names MainWindow.xaml gives the two elements measured here.</summary>
    private const string TitleBarName = "TitleBar";
    private const string DrawerName = "ProfileDrawer";

    /// <summary>
    /// The client area of the smallest window the app allows: the minimum window less the
    /// caption and resize borders the OS takes at this display's scaling.
    /// <para>
    /// Deliberately optimistic even so. The real drawer hangs from the bottom of the in-window
    /// title bar (54 px at the default font size, 61 px at the largest), which this model hands
    /// it as free space. An overflow reported here is one the running app has by that much more,
    /// never the reverse.
    /// </para>
    /// </summary>
    private static Size MinimumClientArea()
    {
        var frame = SystemParameters.WindowNonClientFrameThickness;
        return new Size(
            MinWindowWidth - frame.Left - frame.Right,
            MinWindowHeight - frame.Top - frame.Bottom);
    }

    /// <summary>What one arrangement of the drawer measured, marshalled off the UI thread.</summary>
    private sealed record DrawerMeasurement(
        double BaseFontSize,
        int TraderCount,
        double AvailableHeight,
        double TitleBarHeight,
        double DrawerHeight,
        double LastPillBottom,
        bool HasScroller,
        double ScrollableHeight)
    {
        /// <summary>How far past the bottom of the window the last loyalty group is laid out.</summary>
        public double Overflow => LastPillBottom - AvailableHeight;

        /// <summary>All the room the drawer can ever have: the client area below the title bar.</summary>
        public double RoomBelowTitleBar => AvailableHeight - TitleBarHeight;

        /// <summary>The failure a player sees: controls below the window edge that nothing scrolls.</summary>
        public string Describe() =>
            $"at base font {BaseFontSize} the last of the {TraderCount} loyalty groups ends "
            + $"{Overflow:F0}px below the window ({LastPillBottom:F0}px into a "
            + $"{AvailableHeight:F0}px client area) "
            + (HasScroller
                ? $"and its scroller only offers {ScrollableHeight:F0}px of travel"
                : "and nothing scrolls");
    }

    /// <summary>
    /// Across the whole supported font range, at the smallest window the app allows, every
    /// loyalty group must be inside the window or reachable by scrolling. This is the assertion
    /// the bug breaks: the groups past the window edge were laid out, hit-testable in theory and
    /// unreachable in practice.
    /// </summary>
    [Theory]
    [MemberData(nameof(SupportedBaseFontSizes))]
    public void Every_loyalty_group_is_on_screen_or_scrollable_at_the_minimum_window(double baseFontSize)
    {
        var measurement = MeasureDrawer(baseFontSize);

        Assert.True(
            measurement.LastPillBottom <= measurement.AvailableHeight
                || measurement.ScrollableHeight > 0,
            measurement.Describe());
    }

    /// <summary>
    /// The guard is not vacuous: at the largest font the drawer really does need more room than
    /// the minimum window has, so the previous case passes only because something scrolls. If a
    /// future drawer shrinks below the window this fails loudly rather than letting the range
    /// case go green on a drawer that no longer exercises the overflow at all.
    /// </summary>
    [Fact]
    public void The_largest_font_overflows_the_minimum_window_and_scrolls_instead_of_clipping()
    {
        var measurement = MeasureDrawer(SettingsService.MaxFontSize);

        Assert.True(measurement.LastPillBottom > measurement.AvailableHeight,
            $"the drawer now fits the minimum window at base font {SettingsService.MaxFontSize} "
            + $"(last group ends at {measurement.LastPillBottom:F0}px of "
            + $"{measurement.AvailableHeight:F0}px), so this case no longer proves the overflow is "
            + "scrollable. Re-point it at the size that overflows.");

        // The mechanism behind the bug: the Grid clamps the drawer to the room below the title
        // bar whatever its content wants, so content laid out past that line is off the window.
        Assert.Equal(measurement.RoomBelowTitleBar, measurement.DrawerHeight, precision: 1);

        Assert.True(measurement.HasScroller, measurement.Describe());
        Assert.True(measurement.ScrollableHeight >= measurement.Overflow, measurement.Describe());
    }

    /// <summary>
    /// The common path is untouched: at the default font size the drawer fits the minimum window,
    /// so the scroller stays out of the way - no bar, no travel, the same drawer height as before
    /// it was wrapped.
    /// </summary>
    [Fact]
    public void The_default_font_size_drawer_fits_without_scrolling()
    {
        var measurement = MeasureDrawer(SettingsService.DefaultBaseFontSize);

        Assert.True(measurement.LastPillBottom <= measurement.AvailableHeight, measurement.Describe());
        Assert.Equal(0, measurement.ScrollableHeight);

        // Unclamped, and therefore the same drawer as before it was wrapped: it takes its
        // natural height rather than the room below the title bar, so nothing is hidden and no
        // scrollbar is taking width off the WrapPanels.
        Assert.True(measurement.DrawerHeight < measurement.RoomBelowTitleBar,
            $"at the default font size the drawer fills all {measurement.RoomBelowTitleBar:F0}px "
            + "below the title bar, which means it is being clamped rather than sized to content");
    }

    /// <summary>
    /// <see cref="ApplyBaseFontSize"/> restates App.ApplyBaseFontSize's arithmetic, so this
    /// pins the two together: a changed offset there without a changed offset here would leave
    /// every case above measuring a drawer at font sizes the app never renders.
    /// </summary>
    [Fact]
    public void The_mirrored_font_size_derivation_matches_App_xaml_cs()
    {
        const double probe = 21;
        var mirrored = new ResourceDictionary();
        ApplyBaseFontSize(mirrored, probe);

        var source = File.ReadAllText(Path.Combine(TestRepo.Root(), "TarkovHelper", "App.xaml.cs"));

        foreach (var key in mirrored.Keys.Cast<string>())
        {
            var offset = (double)mirrored[key]! - probe;
            var expected = offset switch
            {
                0 => $"Resources[\"{key}\"] = baseFontSize;",
                > 0 => $"Resources[\"{key}\"] = baseFontSize + {offset};",
                _ => $"Resources[\"{key}\"] = baseFontSize - {-offset};",
            };

            Assert.Contains(expected, source);
        }
    }

    /// <summary>Every base font size the settings slider offers, from 10 to 28.</summary>
    public static TheoryData<double> SupportedBaseFontSizes()
    {
        var data = new TheoryData<double>();
        for (var size = SettingsService.MinFontSize; size <= SettingsService.MaxFontSize; size++)
        {
            data.Add(size);
        }
        return data;
    }

    // ---- Arranging the real drawer ------------------------------------------------------

    private static DrawerMeasurement MeasureDrawer(double baseFontSize) =>
        OnStaThread(() =>
        {
            var root = (Grid)XamlReader.Parse(ComposeDrawerDocument());

            // App.xaml's compiled AppFont carries a pack URI that only resolves inside the
            // packaged app; the embedded faces are read from the source tree instead, exactly
            // as ProfileSelectorFitTests.MeasureLabel does.
            root.Resources["AppFont"] = new FontFamily(
                new Uri(Path.Combine(TestRepo.Root(), "TarkovHelper") + Path.DirectorySeparatorChar),
                FontStacks.ForLanguage(AppLanguage.EN));
            ApplyBaseFontSize(root.Resources, baseFontSize);

            var titleBar = (Border)root.FindName(TitleBarName)!;
            var drawer = (Border)root.FindName(DrawerName)!;
            var section = (FrameworkElement)root.FindName("LoyaltySection")!;
            var group = (WrapPanel)root.FindName("LoyaltyGroup")!;

            // In MainWindow the title bar occupies an Auto row; here both elements share one
            // cell, so it has to be told to take its natural height rather than stretch.
            titleBar.VerticalAlignment = VerticalAlignment.Top;

            // The drawer is Collapsed in markup and opened by the header button; the loyalty
            // section is Collapsed until BuildLoyaltyGroup finds a roster to fill it with.
            drawer.Visibility = Visibility.Visible;
            var traderCount = FillLoyaltyGroup(group, root);
            section.Visibility = Visibility.Visible;

            var available = MinimumClientArea();
            root.Measure(available);
            root.Arrange(new Rect(available));
            root.UpdateLayout();

            // MainWindow does exactly this from TitleBar.SizeChanged: the drawer slides out from the
            // bottom of the title bar, so what is left of the client area is all it ever gets.
            drawer.Margin = new Thickness(0, titleBar.ActualHeight, 0, 0);
            root.Measure(available);
            root.Arrange(new Rect(available));
            root.UpdateLayout();

            var lastPill = (FrameworkElement)group.Children[^1];
            var bottom = lastPill
                .TransformToAncestor(root)
                .Transform(new Point(0, lastPill.ActualHeight)).Y;
            var scroller = ScrollerBetween(lastPill, drawer);

            return new DrawerMeasurement(
                baseFontSize,
                traderCount,
                available.Height,
                titleBar.ActualHeight,
                drawer.ActualHeight,
                bottom,
                scroller != null,
                scroller?.ScrollableHeight ?? 0);
        });

    /// <summary>
    /// The title bar and the drawer, lifted out of MainWindow.xaml and wrapped in a client-area
    /// sized Grid that carries App.xaml's and MainWindow's own resource dictionaries, so every
    /// StaticResource brush, style and DynamicResource font size resolves to the value the
    /// running app gives it.
    /// <para>
    /// The title bar is here because the drawer's position depends on it: MainWindow sets the
    /// drawer's top margin from TitleBar.ActualHeight, so measuring the drawer without it would
    /// credit the drawer with 54 to 61 px of room it does not have.
    /// </para>
    /// </summary>
    private static string ComposeDrawerDocument()
    {
        var appResources = XDocument
            .Load(Path.Combine(TestRepo.Root(), "TarkovHelper", "App.xaml"))
            .Root!
            .Element(Presentation + "Application.Resources")!;

        // The AppFont entry is App.xaml's only clr-namespace reference, and an unqualified
        // mapping cannot be resolved outside the compiled assembly. MeasureDrawer installs the
        // same chain as a source-tree FontFamily once the tree is parsed.
        appResources
            .Elements(Xaml2006 + "Static")
            .Where(e => (string?)e.Attribute(Xaml2006 + "Key") == "AppFont")
            .ToList()
            .Remove();

        var window = XDocument.Load(Path.Combine(TestRepo.Root(), "TarkovHelper", "MainWindow.xaml"));
        var windowResources = window.Root!.Element(Presentation + "Window.Resources")!;

        var elements = new[] { TitleBarName, DrawerName }
            .Select(name => Named(window, name))
            .ToList();
        foreach (var element in elements) StripEventHandlers(element);

        var root = new XElement(Presentation + "Grid",
            new XAttribute(XNamespace.Xmlns + "x", Xaml2006.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=mscorlib"),
            // MainWindow's own resources last, as they are in the running app: they are looked up
            // before the application ones and may legitimately shadow a key.
            new XElement(Presentation + "Grid.Resources",
                appResources.Elements(), windowResources.Elements()),
            elements);

        return root.ToString();
    }

    /// <summary>The single element MainWindow.xaml gives this x:Name.</summary>
    private static XElement Named(XDocument document, string name) =>
        document.Descendants().Single(e => (string?)e.Attribute(Xaml2006 + "Name") == name);

    /// <summary>
    /// Drops the handler attributes (Click, LostFocus, ...) the drawer wires to MainWindow's
    /// code-behind: loose XAML has no code-behind to resolve them against, and no handler moves
    /// a control. Derived from the element type's own events rather than from a list of names
    /// here, so a handler added to the drawer tomorrow needs no edit in this file.
    /// </summary>
    private static void StripEventHandlers(XElement element)
    {
        foreach (var node in element.DescendantsAndSelf())
        {
            var type = ResolveWpfType(node.Name.LocalName);
            if (type == null) continue;

            node.Attributes()
                .Where(a => !a.IsNamespaceDeclaration
                            && a.Name.Namespace == XNamespace.None
                            && type.GetEvent(a.Name.LocalName) != null)
                .ToList()
                .Remove();
        }
    }

    /// <summary>The WPF type an unprefixed XAML element name stands for, or null for a property element.</summary>
    private static Type? ResolveWpfType(string elementName)
    {
        foreach (var assembly in new[] { typeof(FrameworkElement).Assembly, typeof(UIElement).Assembly })
        {
            foreach (var ns in new[]
                     {
                         "System.Windows.Controls",
                         "System.Windows.Controls.Primitives",
                         "System.Windows.Shapes",
                         "System.Windows.Documents",
                         "System.Windows.Media.Effects",
                         "System.Windows.Media",
                         "System.Windows",
                     })
            {
                if (assembly.GetType($"{ns}.{elementName}") is { } type) return type;
            }
        }
        return null;
    }

    /// <summary>
    /// Mirrors App.ApplyBaseFontSize, which derives every size resource from the one base size the
    /// user controls. Kept in step with it by
    /// <see cref="The_mirrored_font_size_derivation_matches_App_xaml_cs"/>.
    /// </summary>
    private static void ApplyBaseFontSize(ResourceDictionary resources, double baseFontSize)
    {
        resources["BaseFontSize"] = baseFontSize;
        resources["FontSizeTiny"] = baseFontSize - 6;
        resources["FontSizeXSmall"] = baseFontSize - 4;
        resources["FontSizeSmall"] = baseFontSize - 2;
        resources["FontSizeMedium"] = baseFontSize;
        resources["FontSizeLarge"] = baseFontSize + 2;
        resources["FontSizeXLarge"] = baseFontSize + 4;
        resources["FontSizeTitle"] = baseFontSize + 6;
        resources["FontSizeHeader"] = baseFontSize + 8;
    }

    /// <summary>
    /// Builds one bordered group per gating trader, as MainWindow.BuildLoyaltyGroup does from the
    /// roster QuestDbService derives, and returns how many it built. The roster comes from the
    /// bundled seed so the drawer under measurement is the width and the row count the shipped
    /// data produces, not a made-up one.
    /// </summary>
    private static int FillLoyaltyGroup(WrapPanel group, FrameworkElement resourceHost)
    {
        var traders = SeedLoyaltyRoster();
        Assert.NotEmpty(traders);

        foreach (var displayName in traders)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };

            var label = new TextBlock
            {
                Text = displayName,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)resourceHost.FindResource("TextSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            label.SetResourceReference(TextBlock.FontSizeProperty, "FontSizeXSmall");
            row.Children.Add(label);

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
                    Margin = level == SettingsService.MinTraderLoyaltyLevel
                        ? new Thickness(0)
                        : new Thickness(2, 0, 0, 0),
                };
                button.SetResourceReference(Control.FontSizeProperty, "FontSizeSmall");
                row.Children.Add(button);
            }

            group.Children.Add(new Border
            {
                Background = (Brush)resourceHost.FindResource("BackgroundMediumBrush"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 8, 8),
                VerticalAlignment = VerticalAlignment.Center,
                Child = row,
            });
        }

        return traders.Count;
    }

    /// <summary>
    /// The English label the drawer gives each trader the bundled seed gates a quest on, in
    /// QuestDbService.BuildLoyaltyTraders' display order. The labels are what decides how wide
    /// each group is and therefore how many rows the WrapPanel takes, which is the whole
    /// question here, so they come from the shipped data rather than from a fixture.
    /// </summary>
    private static IReadOnlyList<string> SeedLoyaltyRoster()
    {
        var roster = new List<(string TraderId, string DisplayName, string NormalizedName)>();

        using var connection = new SqliteConnection($"Data Source={TestSeed.DatabasePath};Mode=ReadOnly");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT r.TraderId,
                   COALESCE(NULLIF(t.Name, ''), NULLIF(r.TraderName, ''), r.TraderId) AS DisplayName,
                   COALESCE(t.NormalizedName, '') AS NormalizedName
            FROM QuestTraderRequirements r
            LEFT JOIN Traders t ON t.Id = r.TraderId
            GROUP BY r.TraderId";

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                roster.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        return roster
            .OrderBy(t => TraderDbService.DisplayRank(t.NormalizedName))
            .ThenBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.TraderId, StringComparer.Ordinal)
            .Select(t => t.DisplayName)
            .ToList();
    }

    /// <summary>
    /// The nearest ScrollViewer between <paramref name="element"/> and <paramref name="ancestor"/>,
    /// which is what "the player can reach this row" means. Walking up from the element rather
    /// than down from the drawer matters twice over: it does not care where in the drawer the
    /// viewport sits, and it cannot mistake the TextBox's own PART_ContentHost scroller - which
    /// scrolls nothing but the level field's text - for one that scrolls the loyalty rows.
    /// </summary>
    private static ScrollViewer? ScrollerBetween(DependencyObject element, DependencyObject ancestor)
    {
        for (var node = VisualTreeHelper.GetParent(element);
             node != null && node != ancestor;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer scroller) return scroller;
        }
        return null;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on an STA thread, as WPF element construction and layout
    /// require, and rethrows whatever it threw on the caller's thread with its stack intact.
    /// </summary>
    private static T OnStaThread<T>(Func<T> body)
    {
        var result = default(T);
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = body();
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
            Name = nameof(ProfileDrawerFitTests),
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "the layout thread never finished");
        failure?.Throw();
        return result!;
    }
}
