using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// Per-profile ordering barrier between user-data persistence and a profile reset
/// (feature-complete-profile-reset.spec.md). Every fire-and-forget or deferred write to a
/// profile-owned user-data table passes through <see cref="Run"/> (or the result-carrying
/// <see cref="RunAsync{T}"/>); a reset raises the barrier for its profile with
/// <see cref="BeginResetAsync"/>, which drains the writes already in flight and holds new ones
/// until the reset commits. The guarantee is structural: the barrier is acquired inside the
/// persistence helpers, not at their call sites, so a future caller of any helper is ordered
/// automatically and there is no call-site discipline to erode.
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
    /// Runs <paramref name="op"/> as a tracked write for <paramref name="profileId"/>: waits out
    /// any reset barrier first, registers so a later reset's drain awaits it, and logs any
    /// failure with the full exception instead of throwing (the shape every fire-and-forget
    /// persistence body needs; the returned task never faults). Await the returned task to keep
    /// a blocking call site's ordering, or discard it for fire-and-forget.
    /// </summary>
    public static async Task Run(string profileId, Func<Task> op)
    {
        var registration = await RegisterAsync(profileId).ConfigureAwait(false);
        try
        {
            await op().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error($"Tracked user-data write for {profileId} failed", ex);
        }
        finally
        {
            registration.Complete();
        }
    }

    /// <summary>
    /// The result-carrying twin of <see cref="Run"/> for awaited call sites that must observe
    /// the outcome: same barrier and registration, but a failure PROPAGATES to the caller,
    /// because a swallowed exception here would turn "this profile's sync apply failed" into
    /// "nothing needed writing" and the summary would misreport it.
    /// </summary>
    public static async Task<T> RunAsync<T>(string profileId, Func<Task<T>> op)
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
    public static async Task<IAsyncDisposable> BeginResetAsync(string profileId)
    {
        var state = StateOf(profileId);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        List<Task> inFlight;
        while (true)
        {
            Task? existing;
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
            await existing.ConfigureAwait(false);
        }

        // Registration tasks complete in the writes' finally blocks whatever the ops did, so
        // this never throws and a failing tracked write cannot wedge the barrier.
        await Task.WhenAll(inFlight).ConfigureAwait(false);
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
