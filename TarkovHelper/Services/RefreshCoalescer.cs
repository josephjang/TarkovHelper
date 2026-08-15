using System.Windows.Threading;

namespace TarkovHelper.Services;

/// <summary>
/// Collapses a burst of "state changed, redraw" notifications into a single refresh.
/// <para>
/// The motivating case is <see cref="SettingsService"/>: one profile switch raises all seven
/// profile-scoped changed events in a row, and a page that wires five of them to the same full
/// rebuild used to run that rebuild five times, four of them pure waste. A coalescer turns the
/// burst into one refresh.
/// </para>
/// <para>
/// Production callers build one through <see cref="OnDispatcher"/>, which is the only place the
/// scheduling rule is written down: the refresh has to be posted to the owner's dispatcher at
/// <see cref="DispatcherPriority.Background"/>, because a callback that runs INLINE (what
/// <c>Dispatcher.Invoke</c> does when it is already on the UI thread) clears the pending flag
/// before the rest of the burst arrives and so coalesces nothing at all. The
/// <see cref="RefreshCoalescer(Action, Action{Action})"/> constructor stays public as the seam
/// the tests inject a hand-drained queue through, so the collapsing rule can be exercised
/// without a UI thread.
/// </para>
/// </summary>
public sealed class RefreshCoalescer
{
    private readonly Action _refresh;
    private readonly Action<Action> _schedule;

    // 0 = idle, 1 = a refresh is booked and has not started running yet.
    private int _pending;

    /// <param name="refresh">The work to run once per burst. Runs on whatever thread
    /// <paramref name="schedule"/> dispatches to.</param>
    /// <param name="schedule">Hands the given callback to the thread/queue the refresh belongs on.
    /// It must eventually invoke the callback exactly once per call.</param>
    public RefreshCoalescer(Action refresh, Action<Action> schedule)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
    }

    /// <summary>
    /// The production factory: a coalescer that runs <paramref name="refresh"/> on
    /// <paramref name="owner"/>'s dispatcher, once per burst.
    /// <para>
    /// BeginInvoke, not InvokeAsync: both queue the callback, but the
    /// <see cref="DispatcherOperation"/> InvokeAsync returns carries the callback's exception
    /// instead of rethrowing it, so discarding that operation (as a fire-and-forget schedule must)
    /// swallows the failure silently. BeginInvoke's operation raises
    /// <see cref="Dispatcher.UnhandledException"/>, which is the error path App.xaml.cs installs
    /// and logs through, so a refresh that throws is still reported.
    /// </para>
    /// <para>
    /// Call this from a page's CONSTRUCTOR BODY, not a field initializer: a field initializer runs
    /// before the base constructor, where <c>this.Dispatcher</c> is not yet available (CS0236).
    /// </para>
    /// </summary>
    /// <param name="owner">The control the refresh belongs to; supplies the dispatcher.</param>
    /// <param name="refresh">The work to run once per burst, on the dispatcher thread.</param>
    public static RefreshCoalescer OnDispatcher(DispatcherObject owner, Action refresh)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // Captured once: a DispatcherObject keeps the dispatcher of the thread that created it for
        // its whole life, so there is nothing to re-read per request.
        var dispatcher = owner.Dispatcher;

        return new RefreshCoalescer(
            refresh,
            action => dispatcher.BeginInvoke(action, DispatcherPriority.Background));
    }

    /// <summary>
    /// Asks for a refresh. Safe to call from any thread. Only the request that finds the
    /// coalescer idle schedules anything; the rest of the burst joins the refresh that one
    /// already booked.
    /// </summary>
    public void Request()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1)
            return;

        try
        {
            _schedule(Run);
        }
        catch
        {
            // A scheduler that failed to accept the callback never runs it, so the flag has to
            // come back down or this coalescer would swallow every future request.
            Interlocked.Exchange(ref _pending, 0);
            throw;
        }
    }

    private void Run()
    {
        // Cleared BEFORE the refresh, not after: a change landing WHILE the refresh runs books a
        // second pass instead of being swallowed by the pass that had already read the old state.
        Interlocked.Exchange(ref _pending, 0);
        _refresh();
    }
}
