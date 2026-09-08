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
        // The hash combines the id WITH its level rather than accumulating them separately, so
        // this pair does not collide. Equality would catch the swap either way; the hash is what
        // a dictionary of snapshots would rely on.
        var a = TraderLoyaltyLevels.Empty.With(Prapor, 2).With(Jaeger, 4);
        var b = TraderLoyaltyLevels.Empty.With(Prapor, 4).With(Jaeger, 2);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.GetHashCode(), b.GetHashCode());
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
