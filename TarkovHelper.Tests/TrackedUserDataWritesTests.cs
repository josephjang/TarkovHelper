using System.IO;
using TarkovHelper.Services;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards for the per-profile write barrier (feature-complete-profile-reset.spec.md): a reset's
/// drain waits out every write already in flight, new writes wait out the reset, failures are
/// contained, the drain is bounded so a wedged write cannot hang the reset dialog forever, and
/// profiles never wait on each other. Each test uses its own unique profile id because the
/// barrier's state is process-global by design (that is what makes it mechanically unavoidable
/// for every persistence helper).
/// </summary>
[Collection(SchedulingSensitiveCollection.Name)]
public sealed class TrackedUserDataWritesTests
{
    private static string NewProfileId() => "barrier-test-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The outer bound on waits that are pure hang insurance: it fires only when the code under
    /// test is genuinely wedged, so its size costs a passing test nothing. Generous because a
    /// loaded CI runner can stall queued continuations for seconds, and a tight guard turns that
    /// stall into a flake in whichever test it happens to land on.
    /// </summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads the running process's error log until <paramref name="needle"/> shows up or the
    /// wait runs out. The writer batches to disk once a second, so a straight read right after
    /// the failure would race it; returning whatever is there on timeout lets the assertion
    /// report the real contents rather than an empty string.
    /// </summary>
    private static async Task<string> ReadErrorLogUntilAsync(string sessionFolder, string needle)
    {
        var path = Path.Combine(sessionFolder, "error.log");
        var deadline = DateTime.UtcNow + HangGuard;
        string text;
        while (true)
        {
            text = ReadShared(path);
            if (text.Contains(needle, StringComparison.Ordinal) || DateTime.UtcNow >= deadline) return text;
            await Task.Delay(100);
        }
    }

    /// <summary>Reads a file the logger is still appending to, without locking it out.</summary>
    private static string ReadShared(string path)
    {
        if (!File.Exists(path)) return string.Empty;
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task A_write_in_flight_completes_before_the_drain_returns()
    {
        var profileId = NewProfileId();
        var release = new TaskCompletionSource();
        var writeLanded = false;

        var write = TrackedUserDataWrites.RunLoggingFailures(profileId, "held write", async () =>
        {
            await release.Task;
            writeLanded = true;
        });

        var drain = TrackedUserDataWrites.BeginResetAsync(profileId);
        await Task.Delay(100);
        Assert.False(drain.IsCompleted, "the drain returned while a registered write was still in flight");

        release.SetResult();
        var handle = await drain;
        // The write landed BEFORE the drain returned: nothing scheduled before the reset can
        // recreate rows after its deletes.
        Assert.True(writeLanded);
        await write;
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task A_write_attempted_while_the_barrier_is_up_lands_only_after_it_drops()
    {
        var profileId = NewProfileId();
        var handle = await TrackedUserDataWrites.BeginResetAsync(profileId);

        var opRan = false;
        var write = TrackedUserDataWrites.RunLoggingFailures(profileId, "held write", () =>
        {
            opRan = true;
            return Task.CompletedTask;
        });

        await Task.Delay(100);
        Assert.False(opRan, "a write ran while the reset barrier was up");
        Assert.False(write.IsCompleted);

        await handle.DisposeAsync();
        await write;
        Assert.True(opRan);
    }

    [Fact]
    public async Task A_failing_tracked_write_is_swallowed_and_does_not_wedge_the_barrier()
    {
        var profileId = NewProfileId();

        // RunLoggingFailures logs the failure instead of throwing: fire-and-forget call sites
        // discard the task, so a faulted one would surface nowhere.
        await TrackedUserDataWrites.RunLoggingFailures(
            profileId, "item inventory bitcoin",
            () => throw new InvalidOperationException("database is locked"));

        // The failed write unregistered itself: a reset drains promptly instead of waiting on
        // a task that will never complete.
        var handle = await TrackedUserDataWrites.BeginResetAsync(profileId)
            .WaitAsync(HangGuard);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_write_is_logged_with_the_call_sites_description()
    {
        var profileId = NewProfileId();
        var sessionFolder = LoggingService.Instance.SessionFolder;
        var description = "delete quest progress debut-" + Guid.NewGuid().ToString("N");

        await TrackedUserDataWrites.RunLoggingFailures(
            profileId, description,
            () => throw new InvalidOperationException("database is locked"));

        // Which row was lost has to be recoverable from the log: a failed write reverts silently
        // on the next load, and the profile id alone does not say what reverted.
        var logged = await ReadErrorLogUntilAsync(sessionFolder, description);
        Assert.Contains(description, logged);
        Assert.Contains(profileId, logged);
        // The exception travels with it, so the cause is diagnosable and not just the identity.
        Assert.Contains("database is locked", logged);
    }

    [Fact]
    public async Task The_propagating_run_propagates_failures_to_the_caller()
    {
        var profileId = NewProfileId();

        // Awaited call sites (the sync apply) must observe the failure, or a thrown partition
        // would be reported as "nothing needed writing".
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TrackedUserDataWrites.RunPropagatingFailures<int>(
                profileId, () => throw new InvalidOperationException("database is locked")));

        // And it unregistered itself on the way out.
        var handle = await TrackedUserDataWrites.BeginResetAsync(profileId)
            .WaitAsync(HangGuard);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task The_non_generic_propagating_run_propagates_failures_and_unregisters()
    {
        var profileId = NewProfileId();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TrackedUserDataWrites.RunPropagatingFailures(
                profileId, () => throw new InvalidOperationException("database is locked")));

        var handle = await TrackedUserDataWrites.BeginResetAsync(profileId)
            .WaitAsync(HangGuard);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task A_reset_drains_only_its_own_profile()
    {
        var target = NewProfileId();
        var other = NewProfileId();
        var release = new TaskCompletionSource();

        var otherWrite = TrackedUserDataWrites.RunLoggingFailures(other, "held write", () => release.Task);

        // The other profile's held write must not block this profile's reset: the barrier is
        // per profile, which is what keeps it free outside a reset.
        var handle = await TrackedUserDataWrites.BeginResetAsync(target)
            .WaitAsync(HangGuard);
        await handle.DisposeAsync();

        release.SetResult();
        await otherWrite;
    }

    [Fact]
    public async Task A_second_reset_for_the_same_profile_waits_for_the_first_to_release()
    {
        var profileId = NewProfileId();
        var first = await TrackedUserDataWrites.BeginResetAsync(profileId);

        var second = TrackedUserDataWrites.BeginResetAsync(profileId);
        await Task.Delay(100);
        Assert.False(second.IsCompleted, "two resets held the same profile at once");

        await first.DisposeAsync();
        var handle = await second;
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task A_fire_and_forget_write_does_not_run_on_the_callers_thread()
    {
        var profileId = NewProfileId();
        var callerThread = Environment.CurrentManagedThreadId;
        var opThread = 0;
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        // The op must be handed to the pool: Microsoft.Data.Sqlite's OpenAsync and
        // ExecuteNonQueryAsync are synchronous, so an inline op would block the dispatcher (a
        // click handler) or the log-polling thread that scheduled it.
        var write = TrackedUserDataWrites.RunLoggingFailures(profileId, "offloaded write", async () =>
        {
            opThread = Environment.CurrentManagedThreadId;
            entered.TrySetResult();
            await release.Task;
        });

        // The caller got control back with the op still running, which an inline op forbids.
        Assert.False(write.IsCompleted);
        await entered.Task.WaitAsync(HangGuard);
        Assert.NotEqual(callerThread, opThread);

        release.SetResult();
        await write;
    }

    [Fact]
    public async Task The_drain_gives_up_on_a_wedged_write_instead_of_hanging_forever()
    {
        var profileId = NewProfileId();
        var wedged = new TaskCompletionSource();

        // A write that never returns: the reset dialog refuses to close while the reset runs,
        // so an unbounded drain here would leave a window nothing can dismiss.
        var write = TrackedUserDataWrites.RunLoggingFailures(profileId, "wedged write", () => wedged.Task);

        // The outer WaitAsync is only a harness guard against a hang. The message assertions are
        // what prove the drain gave up on its own: the guard's own TimeoutException says merely
        // "The operation has timed out" and names neither the profile nor the drain.
        var timedOut = await Assert.ThrowsAsync<TimeoutException>(() =>
            TrackedUserDataWrites.BeginResetAsync(profileId, TimeSpan.FromMilliseconds(200))
                .WaitAsync(HangGuard));
        Assert.Contains(profileId, timedOut.Message);
        Assert.Contains("draining", timedOut.Message);

        // The barrier it raised was lowered on the way out: a later write must not wait on a
        // barrier nobody will ever release.
        var later = false;
        await TrackedUserDataWrites.RunLoggingFailures(profileId, "later write", () =>
        {
            later = true;
            return Task.CompletedTask;
        }).WaitAsync(HangGuard);
        Assert.True(later);

        wedged.SetResult();
        await write;
    }

    [Fact]
    public async Task A_reset_gives_up_when_an_earlier_reset_never_releases()
    {
        var profileId = NewProfileId();
        var first = await TrackedUserDataWrites.BeginResetAsync(profileId);

        // The second reset waits on the first's barrier, which is exactly as unbounded as the
        // drain unless it too is capped. As above, the message is what tells the helper's own
        // timeout apart from the harness guard's.
        var timedOut = await Assert.ThrowsAsync<TimeoutException>(() =>
            TrackedUserDataWrites.BeginResetAsync(profileId, TimeSpan.FromMilliseconds(200))
                .WaitAsync(HangGuard));
        Assert.Contains(profileId, timedOut.Message);
        Assert.Contains("earlier reset", timedOut.Message);

        // The first reset still holds the profile: the loser cleared nothing it did not own.
        await first.DisposeAsync();
        var handle = await TrackedUserDataWrites.BeginResetAsync(profileId)
            .WaitAsync(HangGuard);
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task A_drain_that_finishes_in_time_still_holds_the_barrier()
    {
        var profileId = NewProfileId();
        var release = new TaskCompletionSource();
        var write = TrackedUserDataWrites.RunLoggingFailures(profileId, "slow write", () => release.Task);

        var drain = TrackedUserDataWrites.BeginResetAsync(profileId, TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        release.SetResult();
        var handle = await drain.WaitAsync(HangGuard);
        await write;

        // The generous timeout did not turn into a lowered barrier: writes still wait.
        var blocked = TrackedUserDataWrites.RunLoggingFailures(
            profileId, "post-drain write", () => Task.CompletedTask);
        await Task.Delay(100);
        Assert.False(blocked.IsCompleted, "a write ran while the reset barrier was up");

        await handle.DisposeAsync();
        await blocked.WaitAsync(HangGuard);
    }
}
