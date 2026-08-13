using System.Diagnostics;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Per-profile ordering barrier between user-data persistence and a profile reset
/// (feature-complete-profile-reset.spec.md). Every fire-and-forget or deferred write to a
/// profile-owned user-data table passes through <see cref="RunLoggingFailures"/> (or the
/// failure-propagating <see cref="RunPropagatingFailures{T}"/>); a reset raises the barrier for
/// its profile with <see cref="BeginResetAsync"/>, which drains the writes already in flight and
/// holds new ones until the reset commits.
/// <para>
/// The barrier is acquired inside the persistence helpers rather than at their call sites, so a
/// new caller of an already-wrapped helper is ordered automatically and there is no per-call-site
/// discipline to erode. That is a property of the wrapped helpers, not of the class: a write path
/// that never calls in here is not ordered, and one such path exists today. ProfileSettings is
/// profile-owned and its rows are deleted by <c>UserDataDbService.ResetProfileAsync</c>, but its
/// only writer (<c>SettingsService.SaveProfileSetting</c> to
/// <c>UserDataDbService.SetProfileSetting</c>) opens its own connection without passing through
/// here. No reachable post-reset subscriber writes a profile setting back today, so nothing is
/// resurrected in practice; a new one would have to be wrapped.
/// </para>
/// <para>
/// Outside a reset the barrier costs one dictionary lookup and one lock per write; writes for
/// different profiles never wait on each other, and nothing serializes writes against writes.
/// Nesting a tracked run inside another tracked run for the SAME profile would deadlock against
/// a concurrent reset (the outer registration blocks the drain the inner call waits on), so
/// each write path wraps exactly one level.
/// </para>
/// </summary>
public static class TrackedUserDataWrites
{
    private static readonly ILogger _log = Log.For("TrackedUserDataWrites");

    /// <summary>
    /// How long <see cref="BeginResetAsync"/> waits, in total, for an earlier reset to release
    /// and for the in-flight writes to drain. A tracked write is a handful of short SQLite
    /// statements, so a wait anywhere near this bound means a wedged write rather than a slow
    /// one. The wait is bounded because its caller is a modal dialog that refuses to close while
    /// the reset runs: an unbounded wait there leaves a window the user cannot dismiss at all.
    /// </summary>
    public static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);

    private sealed class ProfileState
    {
        /// <summary>Guards both fields; never held across an await.</summary>
        public readonly object Gate = new();

        /// <summary>Registration tasks of the writes currently in flight for this profile.</summary>
        public readonly HashSet<Task> InFlight = new();

        /// <summary>Non-null while a reset holds this profile; completed when it releases.</summary>
        public TaskCompletionSource? Barrier;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProfileState> _states =
        new(StringComparer.Ordinal);

    private static ProfileState StateOf(string profileId)
        => _states.GetOrAdd(profileId, _ => new ProfileState());

    /// <summary>
    /// Runs <paramref name="op"/> as a tracked fire-and-forget write for
    /// <paramref name="profileId"/>: waits out any reset barrier first, registers so a later
    /// reset's drain awaits it, and logs any failure with the full exception instead of throwing
    /// (the returned task never faults). Await the returned task to keep a blocking call site's
    /// ordering, or discard it for fire-and-forget.
    /// <para>
    /// <paramref name="op"/> runs on the thread pool, never on the caller's thread. Every caller
    /// of this overload is either the WPF dispatcher (a click handler) or a log-polling thread
    /// holding a lock, and Microsoft.Data.Sqlite has no true async I/O: its OpenAsync and
    /// ExecuteNonQueryAsync run synchronously, so without the offload the whole open-and-write
    /// would block the caller inline.
    /// </para>
    /// </summary>
    /// <param name="description">
    /// Names the row this write is about (item name, quest key, module name, "batch of N rows")
    /// so the failure log identifies WHICH write was lost. A failed write reverts silently on the
    /// next load, and the profile id alone does not say what reverted.
    /// </param>
    public static async Task RunLoggingFailures(string profileId, string description, Func<Task> op)
    {
        try
        {
            await RunTracked<object?>(profileId, async () =>
            {
                await Task.Run(op).ConfigureAwait(false);
                return null;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"Tracked user-data write for {profileId} failed: {description}", ex);
        }
    }

    /// <summary>
    /// The failure-propagating twin of <see cref="RunLoggingFailures"/>, for awaited call sites
    /// that must observe the outcome: same barrier and registration, but a failure PROPAGATES to
    /// the caller, because a swallowed exception here would turn "this profile's sync apply
    /// failed" into "nothing needed writing" and the summary would misreport it. No description
    /// parameter: the exception reaches a caller that already has the context and reports it.
    /// <para>
    /// Unlike <see cref="RunLoggingFailures"/> this does NOT offload to the thread pool. Its
    /// callers are already async and awaiting the result, so an extra hop would only reorder
    /// their continuations.
    /// </para>
    /// </summary>
    public static Task RunPropagatingFailures(string profileId, Func<Task> op)
        => RunTracked<object?>(profileId, async () =>
        {
            await op().ConfigureAwait(false);
            return null;
        });

    /// <summary>
    /// The result-carrying <see cref="RunPropagatingFailures(string, Func{Task})"/>: identical
    /// ordering and error policy, for call sites that need the operation's value.
    /// </summary>
    public static Task<T> RunPropagatingFailures<T>(string profileId, Func<Task<T>> op)
        => RunTracked(profileId, op);

    /// <summary>
    /// The barrier-and-registration skeleton both public entry points share: wait out a reset,
    /// register, run, unregister whatever happened. Exactly one registration per call, because
    /// nesting two tracked runs for the same profile would deadlock against a concurrent reset.
    /// </summary>
    private static async Task<T> RunTracked<T>(string profileId, Func<Task<T>> op)
    {
        var registration = await RegisterAsync(profileId).ConfigureAwait(false);
        try
        {
            return await op().ConfigureAwait(false);
        }
        finally
        {
            registration.Complete();
        }
    }

    /// <summary>
    /// Raises the reset barrier for <paramref name="profileId"/> and drains: every write
    /// registered before the barrier went up has completed when this returns, and every write
    /// arriving while it is up waits. Dispose the handle to lower the barrier and release the
    /// waiters. A second reset for the same profile waits for the first to release.
    /// </summary>
    /// <param name="drainTimeout">
    /// Overrides <see cref="DefaultDrainTimeout"/> for the total wait. Throws
    /// <see cref="TimeoutException"/> when it elapses, having raised no lasting barrier, so the
    /// caller reports a failed reset instead of hanging.
    /// </param>
    /// <exception cref="TimeoutException">
    /// An earlier reset never released, or an in-flight write never completed, within the
    /// timeout. No barrier is left up and nothing has been removed.
    /// </exception>
    public static async Task<IAsyncDisposable> BeginResetAsync(string profileId, TimeSpan? drainTimeout = null)
    {
        var timeout = drainTimeout ?? DefaultDrainTimeout;
        var elapsed = Stopwatch.StartNew();

        TimeSpan Remaining()
        {
            var left = timeout - elapsed.Elapsed;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        var state = StateOf(profileId);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        List<Task> inFlight;
        while (true)
        {
            Task existing;
            lock (state.Gate)
            {
                if (state.Barrier == null)
                {
                    state.Barrier = barrier;
                    inFlight = state.InFlight.ToList();
                    break;
                }
                existing = state.Barrier.Task;
            }

            // Bounded like the drain below. Nothing is up yet that this call owns, so giving up
            // here only has to report the failure.
            try
            {
                await existing.WaitAsync(Remaining()).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for an earlier reset of {profileId} to release. " +
                    "Nothing was removed.");
            }
        }

        // Registration tasks complete in the writes' finally blocks whatever the ops did, so
        // this never faults and a FAILING tracked write cannot wedge the barrier. A write that
        // never returns at all still can, which is what the timeout is for.
        try
        {
            await Task.WhenAll(inFlight).WaitAsync(Remaining()).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The barrier is already up at this point. Lower it before reporting the failure, or
            // every later write for this profile would wait on a barrier nobody will release.
            await new ResetHandle(state, barrier).DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException(
                $"Timed out after {timeout} draining {inFlight.Count} in-flight write(s) for {profileId}. " +
                "Nothing was removed.");
        }

        return new ResetHandle(state, barrier);
    }

    /// <summary>
    /// Waits until no reset barrier is up for the profile, then registers a write. The check
    /// and the registration happen under one lock, so a reset that raises the barrier either
    /// sees this write in its drain list or holds it until release; there is no in-between.
    /// </summary>
    private static async Task<Registration> RegisterAsync(string profileId)
    {
        var state = StateOf(profileId);
        var registration = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        while (true)
        {
            Task? barrier;
            lock (state.Gate)
            {
                if (state.Barrier == null)
                {
                    state.InFlight.Add(registration.Task);
                    return new Registration(state, registration);
                }
                barrier = state.Barrier.Task;
            }
            await barrier.ConfigureAwait(false);
        }
    }

    private readonly struct Registration
    {
        private readonly ProfileState _state;
        private readonly TaskCompletionSource _completion;

        public Registration(ProfileState state, TaskCompletionSource completion)
        {
            _state = state;
            _completion = completion;
        }

        public void Complete()
        {
            _completion.TrySetResult();
            lock (_state.Gate)
            {
                _state.InFlight.Remove(_completion.Task);
            }
        }
    }

    private sealed class ResetHandle : IAsyncDisposable
    {
        private readonly ProfileState _state;
        private readonly TaskCompletionSource _barrier;

        public ResetHandle(ProfileState state, TaskCompletionSource barrier)
        {
            _state = state;
            _barrier = barrier;
        }

        public ValueTask DisposeAsync()
        {
            lock (_state.Gate)
            {
                if (ReferenceEquals(_state.Barrier, _barrier))
                {
                    _state.Barrier = null;
                }
            }
            // Released after the field is cleared, so a waiter that wakes re-checks against
            // a lowered barrier instead of the one it just waited out.
            _barrier.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
