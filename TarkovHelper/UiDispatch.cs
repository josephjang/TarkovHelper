using System.Windows.Threading;

namespace TarkovHelper;

/// <summary>
/// Handing UI work to a dispatcher from a background thread without racing window
/// teardown. Kept free of window types so it is unit-testable (same pattern as
/// <see cref="HeaderLayout"/>).
/// </summary>
public static class UiDispatch
{
    /// <summary>
    /// Queue <paramref name="action"/> on <paramref name="dispatcher"/> and return
    /// immediately, or drop it and return false once that dispatcher is shutting down.
    /// </summary>
    /// <remarks>
    /// Two hazards this exists for. It never blocks: a background raise (an update timer,
    /// a file watcher) that waited on the UI thread would stall its own thread pool, and a
    /// blocking Dispatcher.Invoke from a foreign thread throws TaskCanceledException once
    /// the dispatcher shuts down, which then escapes an async void callback as an
    /// unhandled exception. And it tolerates the teardown race: a window unsubscribes its
    /// handlers when it closes, but a raise already past its delegate read still arrives,
    /// and posting to a dead dispatcher throws. Only that race is swallowed, and only while
    /// the dispatcher confirms it is shutting down. Every other failure propagates.
    /// <para>
    /// The rule for callers: every handler for a background-raised event routes through
    /// here, so no handler has to rediscover either hazard. Handlers for events raised on
    /// the UI thread do not, and settings events specifically must not: SettingsService
    /// raises them under a snapshot-identity guard that pairs the raise with the apply,
    /// and posting would split the two.
    /// </para>
    /// <para>
    /// Callers own their own failures. The action runs later, on the dispatcher, so an
    /// exception escaping it reaches DispatcherUnhandledException, which App.xaml.cs
    /// leaves unhandled, ending the process. This helper deliberately does not catch on
    /// the caller's behalf: a swallowed UI failure is invisible, so each posted body
    /// contains and logs what it can survive.
    /// </para>
    /// </remarks>
    /// <returns>True if the work was queued, false if the dispatcher is gone.</returns>
    public static bool Post(Dispatcher dispatcher, Action action)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(action);

        // Shutdown started (but not finished) still counts as gone: the queue accepts the
        // operation and then never runs it, so the work is dropped either way.
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return false;

        try
        {
            dispatcher.BeginInvoke(action);
            return true;
        }
        catch (InvalidOperationException) when (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            // Shutdown began between the check above and the post; nothing left to render.
            return false;
        }
    }
}
