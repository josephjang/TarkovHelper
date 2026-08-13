using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards for the reset fence's boundary rule (PRD R6 of feature-complete-profile-reset.md).
/// The rule is exercised end to end through the sync scan (LogSyncAttributionTests) and the
/// tracked write (ProfileResetHooksTests); those tests reach it in minutes and seconds. These
/// pin the predicate itself at tick resolution, and pin the never-reset arm the doc comment
/// promises, so the two services cannot inherit a silently redefined boundary.
/// </summary>
public sealed class ResetFenceTests
{
    private static readonly DateTime ResetAt = new(2026, 8, 13, 12, 0, 0);

    // "Not after" is the boundary: an event stamped at the exact reset moment is fenced out.
    // A flip to a strict "<" would leave this one event resurrectable.
    [Fact]
    public void An_event_at_the_exact_reset_moment_is_fenced_out()
    {
        Assert.True(ResetFence.IsFencedOut(ResetAt, ResetAt));
    }

    // One tick either side, which is the finest distinction the rule can be asked to make.
    [Fact]
    public void One_tick_before_the_watermark_is_fenced_and_one_tick_after_is_not()
    {
        Assert.True(ResetFence.IsFencedOut(ResetAt.AddTicks(-1), ResetAt));
        Assert.False(ResetFence.IsFencedOut(ResetAt.AddTicks(1), ResetAt));
    }

    // A profile that was never reset has no watermark and fences nothing, however old the
    // event is. This is the arm every un-reset profile takes on every sync.
    [Fact]
    public void A_profile_that_was_never_reset_fences_nothing()
    {
        Assert.False(ResetFence.IsFencedOut(ResetAt, null));
        Assert.False(ResetFence.IsFencedOut(DateTime.MinValue, null));
        Assert.False(ResetFence.IsFencedOut(DateTime.MaxValue, null));
    }

    // The scan-time fence counts what it drops and keeps the complement (LogSyncService). The
    // two halves are the same predicate and its negation, so they must partition the input with
    // nothing double counted and nothing silently lost.
    [Fact]
    public void The_fenced_and_surviving_events_partition_the_input()
    {
        var events = new[]
        {
            ResetAt.AddDays(-1),
            ResetAt.AddTicks(-1),
            ResetAt,
            ResetAt.AddTicks(1),
            ResetAt.AddDays(1),
        };

        var fenced = events.Count(e => ResetFence.IsFencedOut(e, ResetAt));
        var surviving = events.Where(e => !ResetFence.IsFencedOut(e, ResetAt)).ToList();

        Assert.Equal(3, fenced);
        Assert.Equal(new[] { ResetAt.AddTicks(1), ResetAt.AddDays(1) }, surviving);
        Assert.Equal(events.Length, fenced + surviving.Count);
    }
}
