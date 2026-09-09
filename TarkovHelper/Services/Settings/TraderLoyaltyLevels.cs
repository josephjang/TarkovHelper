namespace TarkovHelper.Services.Settings;

/// <summary>
/// The loyalty level the player has entered for each trader, as one immutable value with
/// structural equality.
/// <para>
/// A plain <see cref="Dictionary{TKey,TValue}"/> would have been the obvious member on
/// <see cref="ProfileSettingsSnapshot"/>, and it would have been wrong there: that record's value
/// equality is what <c>SettingsSetterContractTests</c> asserts whole ("the snapshot changed in
/// exactly one field") and what the setters' <c>with</c> expressions are compared through. A
/// dictionary member compares by REFERENCE, so every such comparison would quietly degrade into
/// "same instance" and pass for the wrong reason;
/// <c>ImmutableSortedDictionary</c> compares by reference too. Owning the comparison here is the
/// cheapest way to keep the record honest, and it gives <see cref="With"/> somewhere to return
/// the same instance for an unchanged level, which is what lets the setter's "value differs"
/// guard stay a reference check.
/// </para>
/// <para>
/// Keyed by the tarkov.dev trader id, never by the nickname: the requirement rows carry the id,
/// the gate compares by id, and a nickname can change upstream while the id cannot. See
/// docs/decisions/feature-quest-loyalty-gating.spec.md.
/// </para>
/// </summary>
internal sealed class TraderLoyaltyLevels : IEquatable<TraderLoyaltyLevels>
{
    /// <summary>
    /// Ordinal, like the ProfileSettings key comparison this is read out of: the table has no
    /// COLLATE NOCASE, so two ids differing in case are two rows and would be two entries.
    /// </summary>
    private static readonly StringComparer IdComparer = StringComparer.Ordinal;

    /// <summary>
    /// Sorted by id, so <see cref="Entries"/> and <see cref="ToString"/> are deterministic and
    /// two instances built in different orders hash alike.
    /// </summary>
    private readonly SortedDictionary<string, int> _levels;

    private TraderLoyaltyLevels(SortedDictionary<string, int> levels) => _levels = levels;

    /// <summary>No trader entered: every <see cref="LevelOf"/> answers the default.</summary>
    internal static readonly TraderLoyaltyLevels Empty =
        new(new SortedDictionary<string, int>(IdComparer));

    /// <summary>
    /// Every stored entry, ordered by trader id.
    /// <para>
    /// A copy, not the backing store. The store is a <see cref="SortedDictionary{TKey,TValue}"/>,
    /// whose runtime type keeps implementing <see cref="IDictionary{TKey,TValue}"/> however narrow
    /// the declared return type is, so handing it out would leave one cast in any caller able to
    /// add, clear or retarget entries in place. On <see cref="Empty"/>, whose instance is shared
    /// process-wide, that would corrupt every snapshot built from
    /// <see cref="ProfileSettingsSnapshot.Defaults"/> for the life of the process. The copy is one
    /// array of at most eleven pairs, built once per published fan-out.
    /// </para>
    /// </summary>
    internal IReadOnlyCollection<KeyValuePair<string, int>> Entries => _levels.ToArray();

    /// <summary>How many traders have an entry. Zero for <see cref="Empty"/>.</summary>
    internal int Count => _levels.Count;

    /// <summary>
    /// <paramref name="level"/> brought inside
    /// [<see cref="SettingsService.MinTraderLoyaltyLevel"/>,
    /// <see cref="SettingsService.MaxTraderLoyaltyLevel"/>]. Every level this value holds has been
    /// through here, because <see cref="With"/> and <see cref="From"/> are the only ways in and
    /// both clamp: an entry of 9 hand-written into the table would otherwise reach the gate as 9
    /// and unlock quests the drawer cannot express, and an entry of 0 would lock a requirement of
    /// 1 that the game has already met.
    /// <para>
    /// Exposed because <see cref="SettingsService.SetTraderLoyalty"/> needs the same clamped
    /// number for the row it writes and the event it raises, not only for the value it stores.
    /// Naming the bounds a second time over there is how the two would drift apart.
    /// </para>
    /// </summary>
    internal static int Clamp(int level) => Math.Clamp(
        level, SettingsService.MinTraderLoyaltyLevel, SettingsService.MaxTraderLoyaltyLevel);

    /// <summary>
    /// The level entered for <paramref name="traderId"/>, or
    /// <see cref="SettingsService.DefaultTraderLoyaltyLevel"/> when the profile has no entry for
    /// it. The default is a real answer rather than a missing one: an unentered trader reads as
    /// level 1, which is where every profile starts in the game.
    /// </summary>
    internal int LevelOf(string? traderId)
        => traderId != null && _levels.TryGetValue(traderId, out var level)
            ? level
            : SettingsService.DefaultTraderLoyaltyLevel;

    /// <summary>
    /// This value with <paramref name="traderId"/> set to <paramref name="level"/>, or THIS
    /// INSTANCE when it already holds that level. The reference identity is load bearing: the
    /// loyalty setter's no-op guard is <c>ReferenceEquals</c>, so an edit that changes nothing
    /// skips both the publish and the store write, exactly as the eight scalar settings do
    /// through their own "value differs" comparisons.
    /// </summary>
    /// <param name="traderId">
    /// Empty or null leaves the value untouched: there is no trader to key an entry under, and a
    /// blank key would be a row no reader could ever match to a requirement.
    /// </param>
    /// <param name="level">
    /// Clamped by <see cref="Clamp"/> before the "already holds that level" comparison, so that
    /// question is asked about the level actually going in: setting an out-of-range value onto a
    /// trader already at the bound is a no-op rather than a rewrite of the number already there.
    /// </param>
    internal TraderLoyaltyLevels With(string? traderId, int level)
    {
        if (string.IsNullOrEmpty(traderId)) return this;

        var clamped = Clamp(level);
        if (_levels.TryGetValue(traderId, out var current) && current == clamped) return this;

        var next = new SortedDictionary<string, int>(_levels, IdComparer) { [traderId] = clamped };
        return new TraderLoyaltyLevels(next);
    }

    /// <summary>
    /// The entries in <paramref name="levels"/> as one value, each level clamped by
    /// <see cref="Clamp"/>. Later duplicates win, which cannot happen through the store (one row
    /// per key) and keeps the builder total anyway.
    /// </summary>
    internal static TraderLoyaltyLevels From(IEnumerable<KeyValuePair<string, int>> levels)
    {
        var map = new SortedDictionary<string, int>(IdComparer);
        foreach (var (traderId, level) in levels)
        {
            if (string.IsNullOrEmpty(traderId)) continue;
            map[traderId] = Clamp(level);
        }
        return map.Count == 0 ? Empty : new TraderLoyaltyLevels(map);
    }

    public bool Equals(TraderLoyaltyLevels? other)
    {
        if (other == null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (_levels.Count != other._levels.Count) return false;

        foreach (var (traderId, level) in _levels)
        {
            if (!other._levels.TryGetValue(traderId, out var otherLevel) || otherLevel != level)
                return false;
        }
        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as TraderLoyaltyLevels);

    public override int GetHashCode()
    {
        // Order-independent by construction (the map is sorted), and combined rather than
        // accumulated with XOR so that swapping two traders' levels changes the hash.
        var hash = new HashCode();
        foreach (var (traderId, level) in _levels)
        {
            hash.Add(traderId, IdComparer);
            hash.Add(level);
        }
        return hash.ToHashCode();
    }

    /// <summary>Readable in a failed assertion: <c>[id=2, id=3]</c>, or <c>[]</c> when empty.</summary>
    public override string ToString()
        => "[" + string.Join(", ", _levels.Select(e => $"{e.Key}={e.Value}")) + "]";
}
