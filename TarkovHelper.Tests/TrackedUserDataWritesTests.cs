using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards for the per-profile write barrier (feature-complete-profile-reset.spec.md): a reset's
/// drain waits out every write already in flight, new writes wait out the reset, failures are
/// contained, and profiles never wait on each other. Each test uses its own unique profile id
/// because the barrier's state is process-global by design (that is what makes it mechanically
/// unavoidable for every persistence helper).
/// </summary>
public sealed class TrackedUserDataWritesTests
{
    private static string NewProfileId() => "barrier-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_write_in_flight_completes_before_the_drain_returns()
    {
        var profileId = NewProfileId();
        var release = new TaskCompletionSource();
        var writeLanded = false;

        var write = TrackedUserDataWrites.Run(profileId, async () =>
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
        var write = TrackedUserDataWrites.Run(profileId, () =>
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

        // Run logs the failure instead of throwing: fire-and-forget call sites discard the
        // task, so a faulted one would surface nowhere.
        await TrackedUserDataWrites.Run(
            profileId, () => throw new InvalidOperationException("database is locked"));

        // The failed write unregistered itself: a reset drains promptly instead of waiting on
        // a task that will never complete.
        var handle = await TrackedUserDataWrites.BeginResetAsync(profileId)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task The_result_carrying_run_propagates_failures_to_the_caller()
    {
        var profileId = NewProfileId();

        // Awaited call sites (the sync apply) must observe the failure, or a thrown partition
        // would be reported as "nothing needed writing".
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TrackedUserDataWrites.RunAsync<int>(
                profileId, () => throw new InvalidOperationException("database is locked")));

        // And it unregistered itself on the way out.
        var handle = await TrackedUserDataWrites.BeginResetAsync(profileId)
            .WaitAsync(TimeSpan.FromSeconds(5));
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task A_reset_drains_only_its_own_profile()
    {
        var target = NewProfileId();
        var other = NewProfileId();
        var release = new TaskCompletionSource();

        var otherWrite = TrackedUserDataWrites.Run(other, () => release.Task);

        // The other profile's held write must not block this profile's reset: the barrier is
        // per profile, which is what keeps it free outside a reset.
        var handle = await TrackedUserDataWrites.BeginResetAsync(target)
            .WaitAsync(TimeSpan.FromSeconds(5));
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
}
