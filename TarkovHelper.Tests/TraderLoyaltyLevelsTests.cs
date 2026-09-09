using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// The value semantics <see cref="ProfileSettingsSnapshot"/> depends on trader loyalty having.
/// <para>
/// The record's whole-value comparison is what the setter contract asserts ("the snapshot changed
/// in exactly one field"), and a member that compares by reference would turn every one of those
/// assertions into "same instance" and pass for the wrong reason. Structural equality and the
/// reference identity <see cref="TraderLoyaltyLevels.With"/> returns for an unchanged level are
/// therefore contract, not implementation detail, and they are pinned here.
/// </para>
/// <para>
/// The two invariants the value owns are pinned here as well, because it is the only place they
/// are enforced: every level it holds is inside
/// [<see cref="SettingsService.MinTraderLoyaltyLevel"/>,
/// <see cref="SettingsService.MaxTraderLoyaltyLevel"/>], and nothing it hands out reaches its
/// backing store.
/// </para>
/// </summary>
public sealed class TraderLoyaltyLevelsTests
{
    private const string Prapor = "54cb50c76803fa8b248b4571";
    private const string Jaeger = "5c0647fdd443bc2504c2d371";

    [Fact]
    public void Two_values_with_the_same_entries_are_equal_and_hash_alike()
    {
        var a = TraderLoyaltyLevels.Empty.With(Prapor, 2).With(Jaeger, 4);
        // Built in the other order: the entries are sorted, so order cannot leak into either.
        var b = TraderLoyaltyLevels.Empty.With(Jaeger, 4).With(Prapor, 2);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotSame(a, b);
    }

    [Fact]
    public void A_different_level_for_the_same_trader_is_a_different_value()
    {
        var a = TraderLoyaltyLevels.Empty.With(Prapor, 2);
        var b = TraderLoyaltyLevels.Empty.With(Prapor, 3);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Two_traders_with_their_levels_swapped_are_a_different_value()
    {
        var a = TraderLoyaltyLevels.Empty.With(Prapor, 2).With(Jaeger, 4);
        var b = TraderLoyaltyLevels.Empty.With(Prapor, 4).With(Jaeger, 2);

        // Equality, not the hash: two different values are allowed to hash alike, and asserting
        // that this pair does not would pin an implementation detail on a randomly seeded
        // HashCode. Equal values hashing alike (the case above) is the half that is contract.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void An_entry_added_to_the_empty_value_leaves_the_empty_value_alone()
    {
        var withEntry = TraderLoyaltyLevels.Empty.With(Prapor, 3);

        Assert.Equal(3, withEntry.LevelOf(Prapor));
        // Empty is a shared static: a With that mutated in place would corrupt every snapshot
        // built from Defaults for the life of the process.
        Assert.Equal(0, TraderLoyaltyLevels.Empty.Count);
        Assert.Equal(
            SettingsService.DefaultTraderLoyaltyLevel, TraderLoyaltyLevels.Empty.LevelOf(Prapor));
    }

    [Fact]
    public void With_returns_the_same_instance_when_the_level_is_unchanged()
    {
        var value = TraderLoyaltyLevels.Empty.With(Prapor, 2);

        // The setter's no-op guard is a reference check, so this identity is what makes
        // re-setting a level skip both the publish and the store write.
        Assert.Same(value, value.With(Prapor, 2));
        Assert.NotSame(value, value.With(Prapor, 3));
    }

    [Fact]
    public void With_ignores_an_empty_trader_id()
    {
        var value = TraderLoyaltyLevels.Empty.With(Prapor, 2);

        Assert.Same(value, value.With("", 4));
        Assert.Same(value, value.With(null, 4));
        Assert.Equal(1, value.Count);
    }

    [Fact]
    public void LevelOf_answers_the_default_for_a_trader_with_no_entry()
    {
        var value = TraderLoyaltyLevels.Empty.With(Prapor, 4);

        Assert.Equal(SettingsService.DefaultTraderLoyaltyLevel, value.LevelOf(Jaeger));
        Assert.Equal(SettingsService.DefaultTraderLoyaltyLevel, value.LevelOf(null));
        // Ordinal, like the ProfileSettings key comparison this is read out of: the table has no
        // COLLATE NOCASE, so an id differing in case is a different row and not this entry.
        Assert.Equal(SettingsService.DefaultTraderLoyaltyLevel, value.LevelOf(Prapor.ToUpperInvariant()));
    }

    [Fact]
    public void Entries_are_ordered_by_trader_id_whatever_order_they_were_added_in()
    {
        var value = TraderLoyaltyLevels.Empty.With(Jaeger, 4).With(Prapor, 2);

        Assert.Equal(
            new[] { Prapor, Jaeger }.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            value.Entries.Select(e => e.Key).ToArray());
    }

    /// <summary>
    /// The reason <see cref="TraderLoyaltyLevels.Empty"/> can be a shared static at all: nothing
    /// handed out of the value reaches its backing store. The store is a SortedDictionary, whose
    /// runtime type keeps implementing <see cref="IDictionary{TKey,TValue}"/> however narrow the
    /// declared return type is, so returning it would leave one cast in one future caller able to
    /// clear or retarget Empty's entries in place and corrupt every snapshot built from Defaults
    /// for the life of the process.
    /// </summary>
    [Fact]
    public void Entries_hands_out_a_copy_rather_than_the_live_backing_store()
    {
        var value = TraderLoyaltyLevels.Empty.With(Prapor, 2).With(Jaeger, 4);

        Assert.IsNotAssignableFrom<IDictionary<string, int>>(value.Entries);
        Assert.IsNotAssignableFrom<IDictionary<string, int>>(TraderLoyaltyLevels.Empty.Entries);

        // A caller writing through the mutable interfaces the copy DOES expose writes into its
        // own copy, and the value reads exactly as it was built afterwards.
        var handedOut = value.Entries;
        var writable = Assert.IsAssignableFrom<IList<KeyValuePair<string, int>>>(handedOut);
        writable[0] = new KeyValuePair<string, int>(Jaeger, 1);

        Assert.Equal(
            new[] { Prapor, Jaeger }.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            value.Entries.Select(e => e.Key).ToArray());
        Assert.Equal(2, value.LevelOf(Prapor));
        Assert.Equal(4, value.LevelOf(Jaeger));
    }

    /// <summary>
    /// The bounds live on the value, not on its callers: a level out of range is clamped whichever
    /// door it came in through. An unclamped 9 would answer every published requirement and
    /// silently unlock all 94 gated quests; an unclamped 0 would lock a requirement of 1 the game
    /// has already met.
    /// </summary>
    [Theory]
    [InlineData(9, SettingsService.MaxTraderLoyaltyLevel)]
    [InlineData(5, SettingsService.MaxTraderLoyaltyLevel)]
    [InlineData(0, SettingsService.MinTraderLoyaltyLevel)]
    [InlineData(-4, SettingsService.MinTraderLoyaltyLevel)]
    [InlineData(3, 3)]
    public void A_level_is_clamped_whichever_door_it_comes_in_through(int level, int expected)
    {
        Assert.Equal(expected, TraderLoyaltyLevels.Clamp(level));
        Assert.Equal(expected, TraderLoyaltyLevels.Empty.With(Prapor, level).LevelOf(Prapor));
        Assert.Equal(
            expected,
            TraderLoyaltyLevels.From(new[] { new KeyValuePair<string, int>(Prapor, level) })
                .LevelOf(Prapor));
    }

    // The clamp runs BEFORE the "already holds that level" guard, so setting an out-of-range level
    // onto a trader already at the bound is the no-op the setter's ReferenceEquals guard reads as
    // "nothing to write", rather than a rewrite of the number already stored.
    [Fact]
    public void With_an_out_of_range_level_is_a_no_op_at_the_bound_it_clamps_to()
    {
        var atMax = TraderLoyaltyLevels.Empty.With(Prapor, SettingsService.MaxTraderLoyaltyLevel);
        var atMin = TraderLoyaltyLevels.Empty.With(Prapor, SettingsService.MinTraderLoyaltyLevel);

        Assert.Same(atMax, atMax.With(Prapor, 9));
        Assert.Same(atMin, atMin.With(Prapor, -4));
    }

    [Fact]
    public void From_drops_entries_with_no_trader_id_and_keeps_the_rest()
    {
        var value = TraderLoyaltyLevels.From(new[]
        {
            new KeyValuePair<string, int>(Prapor, 2),
            new KeyValuePair<string, int>("", 4),
            new KeyValuePair<string, int>(Jaeger, 3),
        });

        Assert.Equal(2, value.Count);
        Assert.Equal(2, value.LevelOf(Prapor));
        Assert.Equal(3, value.LevelOf(Jaeger));
    }

    [Fact]
    public void From_an_empty_sequence_is_the_empty_value()
    {
        Assert.Same(TraderLoyaltyLevels.Empty, TraderLoyaltyLevels.From(Array.Empty<KeyValuePair<string, int>>()));
    }

    [Fact]
    public void A_value_is_never_equal_to_null_or_to_another_type()
    {
        var value = TraderLoyaltyLevels.Empty.With(Prapor, 2);

        Assert.False(value.Equals(null));
        Assert.False(value.Equals("not a loyalty map"));
    }

    // The reason this type exists rather than a Dictionary member on the record: the snapshot's
    // own value equality has to see through it. A reference-comparing member would make the two
    // snapshots below "different" and the two after them "equal", which is the failure mode the
    // setter contract tests could not have caught on their own.
    [Fact]
    public void The_snapshot_compares_its_loyalty_map_by_value()
    {
        var profileId = "profile";
        ProfileSettingsSnapshot WithLoyalty(TraderLoyaltyLevels loyalty)
            => ProfileSettingsSnapshot.Defaults(profileId, 0) with { TraderLoyalty = loyalty };

        Assert.Equal(
            WithLoyalty(TraderLoyaltyLevels.Empty.With(Prapor, 2)),
            WithLoyalty(TraderLoyaltyLevels.Empty.With(Prapor, 2)));

        Assert.NotEqual(
            WithLoyalty(TraderLoyaltyLevels.Empty.With(Prapor, 2)),
            WithLoyalty(TraderLoyaltyLevels.Empty.With(Prapor, 3)));
    }
}
