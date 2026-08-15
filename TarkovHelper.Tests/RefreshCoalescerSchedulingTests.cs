using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Two things this file guards, both about how a settings burst reaches the UI.
/// <para>
/// First, the SCHEDULING contract of <see cref="RefreshCoalescer.OnDispatcher"/>. The collapsing
/// rule itself is covered by RefreshCoalescerTests through the injected-scheduler seam; what only
/// a real dispatcher can show is that the production factory posts at
/// <see cref="DispatcherPriority.Background"/> (so the burst finishes arriving before the refresh
/// runs) and that a refresh which throws still reaches
/// <see cref="Dispatcher.UnhandledException"/>, the error path App.xaml.cs installs. A callback
/// posted with <c>InvokeAsync</c> and its operation discarded loses that exception silently.
/// </para>
/// <para>
/// Second, the write-back rules in MainWindow that a settings echo would otherwise break. Those
/// live in a WPF Window whose handlers need a loaded application, an STA dispatcher and both
/// databases, so they are asserted structurally against the source - the same technique
/// <see cref="ProfileAttributionSourceTests"/> uses, and for the same reason: the defect is a
/// missing guard on one path, which no reachable behavioural test can prove absent.
/// </para>
/// </summary>
public sealed class RefreshCoalescerSchedulingTests
{
    #region OnDispatcher scheduling

    /// <summary>Minimal <see cref="DispatcherObject"/>, standing in for a page.</summary>
    private sealed class DispatcherOwner : DispatcherObject
    {
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a dedicated STA thread that owns its own dispatcher, and
    /// rethrows whatever it threw on the caller's thread so xunit reports it normally.
    /// </summary>
    private static void OnDispatcherThread(Action<Dispatcher> body)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                body(dispatcher);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                dispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The dispatcher thread never finished.");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>
    /// Runs everything already queued at Background priority or above. Called from the dispatcher's
    /// own thread, this queues a marker BELOW Background and waits for it, which pushes a nested
    /// frame: the queue drains down to the marker before the wait returns.
    /// </summary>
    private static void Drain(Dispatcher dispatcher)
        => dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    [Fact]
    public void OnDispatcher_collapses_a_burst_into_one_refresh()
    {
        OnDispatcherThread(dispatcher =>
        {
            var refreshes = 0;
            var coalescer = RefreshCoalescer.OnDispatcher(new DispatcherOwner(), () => refreshes++);

            // The seven settings events one published reload delivers, raised on the UI thread.
            for (var i = 0; i < 7; i++) coalescer.Request();

            // Nothing has run yet: Background is below the priority of the code doing the raising,
            // which is the property that makes the collapsing possible at all. A scheduler that ran
            // the callback inline would already be at 7 here.
            Assert.Equal(0, refreshes);

            Drain(dispatcher);

            Assert.Equal(1, refreshes);
        });
    }

    [Fact]
    public void OnDispatcher_runs_a_later_burst_again()
    {
        OnDispatcherThread(dispatcher =>
        {
            var refreshes = 0;
            var coalescer = RefreshCoalescer.OnDispatcher(new DispatcherOwner(), () => refreshes++);

            coalescer.Request();
            coalescer.Request();
            Drain(dispatcher);
            Assert.Equal(1, refreshes);

            // A separate change later on is a new burst, not a continuation of the finished one.
            coalescer.Request();
            Drain(dispatcher);

            Assert.Equal(2, refreshes);
        });
    }

    [Fact]
    public void OnDispatcher_runs_the_refresh_on_the_owning_thread()
    {
        OnDispatcherThread(dispatcher =>
        {
            var owner = new DispatcherOwner();
            var uiThreadId = Environment.CurrentManagedThreadId;
            var refreshThreadId = 0;
            var coalescer = RefreshCoalescer.OnDispatcher(
                owner, () => refreshThreadId = Environment.CurrentManagedThreadId);

            // Settings events are raised from whichever thread published the reload, so Request is
            // routinely called off the UI thread; the refresh still has to land on it.
            var requester = new Thread(() => coalescer.Request());
            requester.Start();
            Assert.True(requester.Join(TimeSpan.FromSeconds(10)), "The requesting thread hung.");

            Drain(dispatcher);

            Assert.Equal(uiThreadId, refreshThreadId);
        });
    }

    /// <summary>
    /// The regression guard for the swallowed error path: the scheduled refresh is fire-and-forget,
    /// so the only thing that can report its failure is the dispatcher itself. BeginInvoke's
    /// discarded operation raises UnhandledException (App.xaml.cs logs it to crash_log.txt);
    /// InvokeAsync's parks the exception on the operation nobody holds, and it is never seen.
    /// </summary>
    [Fact]
    public void OnDispatcher_reports_a_refresh_that_throws()
    {
        OnDispatcherThread(dispatcher =>
        {
            Exception? reported = null;
            dispatcher.UnhandledException += (_, args) =>
            {
                reported = args.Exception;
                // Left unhandled, WPF rethrows on the dispatcher thread and takes the test with it,
                // which is exactly what the real app's handler is there to decide about.
                args.Handled = true;
            };

            var boom = new InvalidOperationException("refresh blew up");
            var coalescer = RefreshCoalescer.OnDispatcher(
                new DispatcherOwner(), () => throw boom);

            coalescer.Request();
            Drain(dispatcher);

            Assert.Same(boom, reported);
        });
    }

    [Fact]
    public void OnDispatcher_rejects_null_arguments()
    {
        OnDispatcherThread(_ =>
        {
            Assert.Throws<ArgumentNullException>(
                () => RefreshCoalescer.OnDispatcher(null!, () => { }));
            Assert.Throws<ArgumentNullException>(
                () => RefreshCoalescer.OnDispatcher(new DispatcherOwner(), null!));
        });
    }

    #endregion

    #region Source-level guards

    private static readonly string MainWindowSource =
        ReadSource("TarkovHelper", "MainWindow.xaml.cs");

    private static readonly string QuestListPageSource =
        ReadSource("TarkovHelper", "Pages", "QuestListPage.xaml.cs");

    private static readonly string ItemsPageSource =
        ReadSource("TarkovHelper", "Pages", "ItemsPage.xaml.cs");

    private static string ReadSource(params string[] relativeParts)
    {
        var path = Path.Combine(TestRepo.Root(), Path.Combine(relativeParts));
        Assert.True(File.Exists(path), $"Source file not found: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>
    /// The body of the member whose declaration contains <paramref name="signature"/>, braces
    /// included. Naive brace matching is enough for these members: none of them contains a brace
    /// inside a string or comment that is not itself balanced.
    /// </summary>
    private static string MemberBody(string source, string signature)
    {
        var declaration = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(declaration >= 0, $"'{signature}' no longer exists; update this test with it.");

        var open = source.IndexOf('{', declaration);
        Assert.True(open >= 0, $"'{signature}' has no body.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }

    /// <summary>
    /// Every method that repaints a profile-drawer control from the settings service. Assigning a
    /// control can raise the very handler a player edit raises (CheckBox.IsChecked raises
    /// Checked/Unchecked), so all of them run under the echo guard - including the ones whose
    /// controls do not raise anything today, so that adding such a control to one of them is safe
    /// by construction rather than by review.
    /// </summary>
    public static TheoryData<string> SettingsRepaintMethods() => new()
    {
        "private void UpdatePlayerLevelUI()",
        "private void UpdateScavRepUI()",
        "private void UpdateDspDecodeUI()",
        "private void UpdateEditionUI()",
        "private void UpdatePrestigeLevelUI()",
    };

    [Theory]
    [MemberData(nameof(SettingsRepaintMethods))]
    public void Settings_repaint_methods_suppress_the_echo(string signature)
    {
        var body = MemberBody(MainWindowSource, signature);

        Assert.Contains("SuppressSettingsEcho()", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The defect this pins: a load that could not read the store publishes that profile's
    /// DEFAULTS, HasEodEditionChanged(false) unchecks the box, and an unguarded ChkEdition_Changed
    /// writes False over the player's stored Edge of Darkness flag - permanently, with no player
    /// action at all.
    /// </summary>
    [Fact]
    public void Edition_checkbox_handler_ignores_a_programmatic_assignment()
    {
        var body = MemberBody(MainWindowSource, "private void ChkEdition_Changed(");

        var guard = body.IndexOf("_isUpdatingSettingsUI", StringComparison.Ordinal);
        var write = body.IndexOf("_settingsService.HasEodEdition =", StringComparison.Ordinal);

        Assert.True(guard >= 0,
            "ChkEdition_Changed must ignore assignments made by UpdateEditionUI, or a published "
            + "defaults snapshot overwrites the stored edition flags.");
        Assert.True(write > guard,
            "The echo guard must be checked BEFORE the edition write-back, not after it.");
        Assert.Contains("return", body[..write], StringComparison.Ordinal);
    }

    /// <summary>
    /// The two drawer text boxes echo the same way, one beat later: the service repaints the box
    /// while the caret sits in it, and the guard is already down by the time LostFocus applies what
    /// it finds there. Equality against the service's own value is what separates a repaint from an
    /// edit, so the write has to be conditional.
    /// </summary>
    [Theory]
    [InlineData("private void ApplyPlayerLevelFromTextBox()", "_settingsService.PlayerLevel", "_settingsService.PlayerLevel = level;")]
    [InlineData("private void ApplyScavRepFromTextBox()", "_settingsService.ScavRep", "_settingsService.ScavRep = scavRep;")]
    public void Drawer_text_boxes_write_back_only_a_changed_value(
        string signature, string property, string assignment)
    {
        var body = MemberBody(MainWindowSource, signature);

        var write = body.IndexOf(assignment, StringComparison.Ordinal);
        Assert.True(write >= 0, $"'{assignment}' no longer exists; update this test with it.");

        // A read of the property before the write is the comparison that makes the write
        // conditional. Without one, the setter derives an edit from a defaults snapshot and
        // persists a value the player never typed.
        var comparison = body[..write].IndexOf(property, StringComparison.Ordinal);
        Assert.True(comparison >= 0,
            $"{signature} must compare against {property} before writing to it.");
    }

    /// <summary>
    /// SettingsService raises all seven of its changed events per published reload. MainWindow
    /// handles the six the drawer renders, and used to push a full quest-list refresh from each
    /// one, outside the page's coalescer: seven full passes over every quest per profile switch,
    /// reset and self-heal, off-tab included. The window updates its own controls; the list
    /// refreshes itself, once.
    /// </summary>
    [Theory]
    [InlineData("private void OnPlayerLevelChanged(object? sender, int newLevel)")]
    [InlineData("private void OnScavRepChanged(object? sender, double newScavRep)")]
    [InlineData("private void OnDspDecodeCountChanged(object? sender, int newCount)")]
    [InlineData("private void OnEditionChanged(object? sender, bool value)")]
    [InlineData("private void OnPrestigeLevelChanged(object? sender, int newLevel)")]
    public void MainWindow_settings_handlers_do_not_push_a_quest_list_refresh(string signature)
    {
        var body = MemberBody(MainWindowSource, signature);

        Assert.DoesNotContain("RefreshDisplay", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same move: what MainWindow stopped pushing, the page now subscribes to
    /// itself, so player level and Scav Rep join the burst the coalescer collapses instead of
    /// bypassing it.
    /// </summary>
    [Fact]
    public void Quest_list_page_consumes_all_seven_settings_events()
    {
        var subscribe = MemberBody(QuestListPageSource, "private void SubscribeServiceEvents()");

        foreach (var settingsEvent in new[]
                 {
                     "PlayerLevelChanged", "ScavRepChanged", "DspDecodeCountChanged",
                     "PlayerFactionChanged", "HasEodEditionChanged", "HasUnheardEditionChanged",
                     "PrestigeLevelChanged",
                 })
        {
            Assert.Contains($"SettingsService.Instance.{settingsEvent} +=", subscribe,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Both pages keep one subscribe/unsubscribe pair rather than three hand-maintained copies of
    /// the same twelve lines, and the two halves must stay exact mirrors: an event added to one and
    /// forgotten in the other is a WPF leak (or a double subscription on the way back in).
    /// </summary>
    [Theory]
    [InlineData("QuestListPage")]
    [InlineData("ItemsPage")]
    public void Page_subscription_lists_are_mirrors(string page)
    {
        var source = page == "QuestListPage" ? QuestListPageSource : ItemsPageSource;

        var subscribed = HandlerWirings(
            MemberBody(source, "private void SubscribeServiceEvents()"), "+=");
        var unsubscribed = HandlerWirings(
            MemberBody(source, "private void UnsubscribeServiceEvents()"), "-=");

        Assert.NotEmpty(subscribed);
        Assert.Equal(subscribed, unsubscribed);
    }

    /// <summary>
    /// Every "<c>source.Event &lt;op&gt; Handler</c>" wiring in a body, normalized to
    /// "<c>source.Event Handler</c>" so the two lists compare directly.
    /// </summary>
    private static List<string> HandlerWirings(string body, string op)
    {
        var wirings = new List<string>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            var at = line.IndexOf(op, StringComparison.Ordinal);
            if (at < 0) continue;
            wirings.Add(line[..at].Trim() + " " + line[(at + op.Length)..].Trim().TrimEnd(';'));
        }
        wirings.Sort(StringComparer.Ordinal);
        return wirings;
    }

    /// <summary>
    /// All three sites that (un)wire a page's service events go through the pair, so none of them
    /// can drift from the other two.
    /// </summary>
    [Theory]
    [InlineData("QuestListPage", "public QuestListPage()", "private async void QuestListPage_Loaded(", "private void QuestListPage_Unloaded(")]
    [InlineData("ItemsPage", "public ItemsPage()", "private async void ItemsPage_Loaded(", "private void ItemsPage_Unloaded(")]
    public void Page_lifecycle_routes_through_the_subscription_pair(
        string page, string constructor, string loaded, string unloaded)
    {
        var source = page == "QuestListPage" ? QuestListPageSource : ItemsPageSource;

        Assert.Contains("SubscribeServiceEvents();", MemberBody(source, constructor),
            StringComparison.Ordinal);
        Assert.Contains("SubscribeServiceEvents();", MemberBody(source, loaded),
            StringComparison.Ordinal);
        Assert.Contains("UnsubscribeServiceEvents();", MemberBody(source, unloaded),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The scheduling rule lives in <see cref="RefreshCoalescer.OnDispatcher"/> and nowhere else.
    /// Retyped at a call site it is one word from being wrong in a way nothing announces: a
    /// <c>Dispatcher.Invoke</c> runs inline on the UI thread and coalesces nothing, and an
    /// <c>InvokeAsync</c> whose operation is discarded swallows the refresh's exceptions.
    /// </summary>
    [Theory]
    [InlineData("QuestListPage")]
    [InlineData("ItemsPage")]
    public void Pages_build_their_coalescer_through_the_factory(string page)
    {
        var source = page == "QuestListPage" ? QuestListPageSource : ItemsPageSource;

        Assert.Contains("RefreshCoalescer.OnDispatcher(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new RefreshCoalescer(", source, StringComparison.Ordinal);
    }

    #endregion
}
