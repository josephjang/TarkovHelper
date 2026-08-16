using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards <see cref="RevisionGate.Claim"/>, the shared claim half of the profile-reload guard
/// that <see cref="SettingsService"/>, <see cref="QuestProgressService"/>,
/// <see cref="HideoutProgressService"/> and <see cref="ItemInventoryService"/> all call.
///
/// The property every caller depends on is monotonicity: after a claim, the counter is at least
/// the claimed revision, and it never moves backwards. Each service's post-read check is an exact
/// `Interlocked.Read(ref _latestRevision) != revision`, so a gate that let the counter regress
/// would make a stale load look current and publish another profile's data.
/// </summary>
[Collection(SchedulingSensitiveCollection.Name)]
public sealed class RevisionGateTests
{
    [Fact]
    public void A_newer_revision_is_claimed()
    {
        long latest = 3;

        RevisionGate.Claim(ref latest, 7);

        Assert.Equal(7, latest);
    }

    [Fact]
    public void An_equal_revision_leaves_the_counter_alone()
    {
        long latest = 5;

        // Not a no-op by accident: the guard is `revision <= current`, so an equal claim must not
        // re-enter the CAS. Two reloads sharing a revision are the same reload to every caller.
        RevisionGate.Claim(ref latest, 5);

        Assert.Equal(5, latest);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public void An_older_revision_never_lowers_the_counter(long stale)
    {
        long latest = 10;

        RevisionGate.Claim(ref latest, stale);

        // A regression here is the whole failure mode: the losing reload would re-become "latest"
        // and its already-read, wrong-profile result would pass the post-read check.
        Assert.Equal(10, latest);
    }

    [Fact]
    public void Claiming_from_zero_works()
    {
        // The field's initial state in all four services.
        long latest = 0;

        RevisionGate.Claim(ref latest, 1);

        Assert.Equal(1, latest);
    }

    [Fact]
    public void Concurrent_claims_settle_on_the_highest_revision()
    {
        long latest = 0;
        const int Claims = 200;

        // Every revision claimed at once, in no particular order. The CAS loop (rather than a
        // read-then-write) is what makes this safe: a thread whose exchange loses re-reads the
        // winner's value instead of stamping its own over it.
        Parallel.For(1, Claims + 1, i => RevisionGate.Claim(ref latest, i));

        Assert.Equal(Claims, latest);
    }

    [Fact]
    public void Concurrent_claims_of_a_lower_revision_cannot_undo_a_higher_one()
    {
        long latest = 0;

        Parallel.Invoke(
            () => RevisionGate.Claim(ref latest, 1_000_000),
            () => Parallel.For(1, 500, i => RevisionGate.Claim(ref latest, i)));

        Assert.Equal(1_000_000, latest);
    }

    [Fact]
    public void Separate_counters_do_not_interfere()
    {
        // Each service owns its own field; the gate is stateless. Passing by ref rather than
        // holding the counter is what keeps four callers independent.
        long first = 0;
        long second = 0;

        RevisionGate.Claim(ref first, 4);

        Assert.Equal(4, first);
        Assert.Equal(0, second);
    }
}
