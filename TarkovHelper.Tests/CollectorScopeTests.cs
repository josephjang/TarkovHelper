using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The Collector page's item-scope rules, run rather than read: which quests the list covers,
/// which (quest, item) pairs it takes from them, and how those pairs add up per item.
/// <para>
/// These rules used to be private instance members of <c>CollectorPage</c>, which this suite
/// cannot construct, so everything pinning them was source text: a guard that fails on a rename
/// and passes on a semantic break. <see cref="CollectorScope"/> takes the two questions that
/// needed a service - a quest's status and a quest's prerequisites - as delegates, so every case
/// below runs the real rule against hand-built quests, with no page and no singleton in sight.
/// </para>
/// </summary>
public sealed class CollectorScopeTests
{
    private const string Collector = "collector";

    private static TarkovTask Quest(string normalizedName, params QuestItem[] items)
        => new()
        {
            Ids = new List<string> { "id-" + normalizedName },
            Name = normalizedName,
            NormalizedName = normalizedName,
            Trader = "Fence",
            RequiredItems = items.Length == 0 ? new List<QuestItem>() : items.ToList(),
        };

    private static QuestItem Item(string normalizedName, int amount, bool foundInRaid = false)
        => new()
        {
            ItemNormalizedName = normalizedName,
            Amount = amount,
            FoundInRaid = foundInRaid,
            Requirement = "Handover",
        };

    private static TarkovItem Known(string normalizedName, string? id = null)
        => new()
        {
            Id = id ?? "id-" + normalizedName,
            Name = normalizedName,
            NormalizedName = normalizedName,
        };

    private static Dictionary<string, TarkovItem> Lookup(params TarkovItem[] items)
        => items.ToDictionary(item => item.NormalizedName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Every quest Active unless this map says otherwise, the way a fresh profile reads.</summary>
    private static Func<TarkovTask, QuestStatus> Statuses(params (string Quest, QuestStatus Status)[] rows)
    {
        var byName = rows.ToDictionary(row => row.Quest, row => row.Status, StringComparer.OrdinalIgnoreCase);
        return task => byName.TryGetValue(task.NormalizedName ?? string.Empty, out var status)
            ? status
            : QuestStatus.Active;
    }

    /// <summary>The graph's answer for Collector, and nothing else: only Collector is ever walked.</summary>
    private static Func<string, IEnumerable<TarkovTask>> Prerequisites(params TarkovTask[] prerequisites)
        => name =>
        {
            Assert.Equal(Collector, name);
            return prerequisites;
        };

    #region Which quests the list covers

    [Fact]
    public void With_the_option_off_the_scope_is_Collector_alone()
    {
        // The prerequisite walk is not merely unused with the option off: it is never asked for,
        // so a graph that has not been built cannot be reached through this path either.
        var collector = Quest(Collector);

        var scope = CollectorScope.QuestsInScope(
            collector,
            Statuses(),
            _ => throw new InvalidOperationException("the prerequisites were walked with the option off"),
            includePrerequisites: false);

        Assert.Equal(new[] { Collector }, scope);
    }

    [Fact]
    public void With_the_option_on_the_whole_transitive_chain_joins_it()
    {
        // The walk handed in is GetAllPrerequisites, which is transitive: the page lists the
        // items of every quest standing between the player and Collector, not just its direct
        // unlocks.
        var collector = Quest(Collector);
        var direct = Quest("the-punisher-part-6");
        var indirect = Quest("the-punisher-part-1");

        var scope = CollectorScope.QuestsInScope(
            collector, Statuses(), Prerequisites(direct, indirect), includePrerequisites: true);

        Assert.Equal(
            new[] { Collector, "the-punisher-part-1", "the-punisher-part-6" },
            scope.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(QuestStatus.Done)]
    [InlineData(QuestStatus.Failed)]
    [InlineData(QuestStatus.Unavailable)]
    public void A_Collector_that_is_done_failed_or_barred_leaves_the_scope_while_its_prerequisites_stay(
        QuestStatus status)
    {
        // Collector's own items stop being worth listing, but the option still means "everything
        // on the way there": a prerequisite the player has not finished keeps its items listed.
        var collector = Quest(Collector);
        var prerequisite = Quest("chemical-part-4");

        var scope = CollectorScope.QuestsInScope(
            collector,
            Statuses((Collector, status)),
            Prerequisites(prerequisite),
            includePrerequisites: true);

        Assert.Equal(new[] { "chemical-part-4" }, scope);
    }

    [Fact]
    public void An_unavailable_prerequisite_leaves_the_scope_and_a_locked_one_stays()
    {
        // The three exclusions are about quests that can no longer be completed, which is why
        // Locked - "the prerequisites are not met YET" - is kept: its items are exactly what the
        // page is for. Unavailable is the edition, prestige or faction bar, which no amount of
        // playing this profile lifts, so its items are not the player's to find.
        var collector = Quest(Collector);
        var locked = Quest("psycho-sniper");
        var barred = Quest("the-punisher-part-7");

        var scope = CollectorScope.QuestsInScope(
            collector,
            Statuses(("psycho-sniper", QuestStatus.Locked), ("the-punisher-part-7", QuestStatus.Unavailable)),
            Prerequisites(locked, barred),
            includePrerequisites: true);

        Assert.Equal(new[] { Collector, "psycho-sniper" }, scope.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void No_Collector_quest_in_the_data_is_an_empty_scope_and_not_a_walk()
    {
        var scope = CollectorScope.QuestsInScope(
            null,
            _ => throw new InvalidOperationException("a status was read for a quest that is not there"),
            _ => throw new InvalidOperationException("the prerequisites of nothing were walked"),
            includePrerequisites: true);

        Assert.Empty(scope);
    }

    [Fact]
    public void A_quest_with_no_normalized_name_is_never_in_a_scope_keyed_by_name()
    {
        // The scope is a set of normalized names, so a quest without one could only ever join it
        // as an empty string that matches nothing - and for Collector itself, an empty name means
        // there is nothing to walk from either.
        var nameless = Quest(Collector);
        nameless.NormalizedName = string.Empty;

        Assert.Empty(CollectorScope.QuestsInScope(
            nameless, Statuses(), Prerequisites(), includePrerequisites: true));

        var namelessPrerequisite = Quest("prerequisite");
        namelessPrerequisite.NormalizedName = null;

        Assert.Equal(
            new[] { Collector },
            CollectorScope.QuestsInScope(
                Quest(Collector), Statuses(), Prerequisites(namelessPrerequisite),
                includePrerequisites: true));
    }

    [Fact]
    public void The_scope_matches_names_the_way_every_other_quest_lookup_matches()
    {
        // A set built with the ordinal comparer would drop a quest the data spells differently in
        // two tables, and the page would silently list fewer items than the chain wants.
        var scope = CollectorScope.QuestsInScope(
            Quest(Collector), Statuses(), Prerequisites(), includePrerequisites: false);

        Assert.Contains("Collector", scope);
    }

    #endregion

    #region Which items the scope covers

    [Fact]
    public void Only_the_quests_in_scope_contribute_their_required_items()
    {
        var inScope = Quest(Collector, Item("gas-analyzer", 1));
        var outOfScope = Quest("debut", Item("mp-133", 2));

        var pairs = CollectorScope.ItemsInScope(
            new[] { inScope, outOfScope }, new HashSet<string> { Collector }).ToList();

        Assert.Equal(new[] { "gas-analyzer" }, pairs.Select(pair => pair.Item.ItemNormalizedName));
        Assert.Same(inScope, pairs[0].Task);
    }

    [Fact]
    public void A_quest_with_no_item_rows_at_all_contributes_nothing_rather_than_throwing()
    {
        // RequiredItems is nullable in the model and null for the many quests that hand nothing
        // over, so this is the common case, not a defensive one.
        var noRows = Quest(Collector);
        noRows.RequiredItems = null;
        var nameless = Quest("nameless", Item("bronze-lion", 1));
        nameless.NormalizedName = string.Empty;

        Assert.Empty(CollectorScope.ItemsInScope(
            new[] { noRows, nameless }, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Collector, string.Empty,
            }));
    }

    #endregion

    #region How the items add up

    [Fact]
    public void A_non_currency_item_sums_the_amounts_every_quest_in_scope_asks_for()
    {
        var first = Quest(Collector, Item("gas-analyzer", 2));
        var second = Quest("chemical-part-4", Item("gas-analyzer", 3));

        var aggregate = Aggregated(
            Lookup(Known("gas-analyzer")), first, second)["gas-analyzer"];

        Assert.Equal(5, aggregate.QuestCount);
        Assert.Equal(0, aggregate.QuestFIRCount);
        Assert.False(aggregate.FoundInRaid);
    }

    [Fact]
    public void Currency_counts_one_per_asking_quest_rather_than_its_amount()
    {
        // Two quests wanting 500000 roubles each is two handovers to make, not a million roubles
        // to carry around: the row would otherwise read "1000000" and the fulfillment bar would
        // never move.
        var first = Quest(Collector, Item("roubles", 500000));
        var second = Quest("chemical-part-4", Item("roubles", 500000));

        var aggregate = Aggregated(Lookup(Known("roubles")), first, second)["roubles"];

        Assert.Equal(2, aggregate.QuestCount);
    }

    [Fact]
    public void The_found_in_raid_half_counts_only_the_rows_that_asked_for_it()
    {
        // One item, three quests, two of which want it found in raid: the row has to say "3, of
        // which 2 in raid", not "3 in raid" and not "3, none in raid".
        var firOne = Quest(Collector, Item("gas-analyzer", 1, foundInRaid: true));
        var firTwo = Quest("chemical-part-4", Item("gas-analyzer", 1, foundInRaid: true));
        var plain = Quest("psycho-sniper", Item("gas-analyzer", 1));

        var aggregate = Aggregated(
            Lookup(Known("gas-analyzer")), firOne, firTwo, plain)["gas-analyzer"];

        Assert.Equal(3, aggregate.QuestCount);
        Assert.Equal(2, aggregate.QuestFIRCount);
        Assert.True(aggregate.FoundInRaid);
    }

    [Fact]
    public void A_first_row_that_wants_it_in_raid_starts_both_counts_together()
    {
        // The "seen it before" branch and the "first time" branch each set the FIR half, and only
        // the second was ever exercised by the case above.
        var aggregate = Aggregated(
            Lookup(Known("gas-analyzer")),
            Quest(Collector, Item("gas-analyzer", 4, foundInRaid: true)))["gas-analyzer"];

        Assert.Equal(4, aggregate.QuestCount);
        Assert.Equal(4, aggregate.QuestFIRCount);
        Assert.True(aggregate.FoundInRaid);
    }

    [Fact]
    public void An_item_the_Items_table_does_not_carry_is_dropped_rather_than_listed_unnamed()
    {
        // There is nothing to render for it: no display name, no icon, no wiki link. It used to
        // be the only reason the aggregation touched the lookup at all.
        var quest = Quest(Collector, Item("gas-analyzer", 1), Item("mystery-item", 1));

        var aggregated = Aggregated(Lookup(Known("gas-analyzer")), quest);

        Assert.Equal(new[] { "gas-analyzer" }, aggregated.Keys);
    }

    [Fact]
    public void No_Items_table_yet_means_no_rows_at_all()
    {
        // The page aggregates before the item lookup is necessarily loaded, and a null lookup is
        // "nothing can be named yet", not a crash.
        Assert.Empty(Aggregated(null, Quest(Collector, Item("gas-analyzer", 1))));
    }

    [Fact]
    public void An_item_row_carries_the_names_links_and_id_the_Items_table_holds()
    {
        var known = Known("gas-analyzer", id: "5c0fa877d174af02a012e1cf");
        known.NameKo = "가스 분석기";
        known.NameJa = "ガス分析器";
        known.IconLink = "https://assets.tarkov.dev/gas-analyzer-icon.jpg";
        known.WikiLink = "https://escapefromtarkov.fandom.com/wiki/Gas_analyzer";

        var aggregate = Aggregated(
            Lookup(known), Quest(Collector, Item("gas-analyzer", 1)))["gas-analyzer"];

        Assert.Equal("5c0fa877d174af02a012e1cf", aggregate.ItemId);
        Assert.Equal("gas-analyzer", aggregate.ItemName);
        Assert.Equal("가스 분석기", aggregate.ItemNameKo);
        Assert.Equal("ガス分析器", aggregate.ItemNameJa);
        Assert.Equal("gas-analyzer", aggregate.ItemNormalizedName);
        Assert.Equal(known.IconLink, aggregate.IconLink);
        Assert.Equal(known.WikiLink, aggregate.WikiLink);
    }

    [Fact]
    public void An_item_with_no_id_falls_back_to_its_normalized_name()
    {
        // The id is what the icon cache and the detail pane key off, so a row without one would
        // share the empty key with every other such row.
        var idless = Known("gas-analyzer");
        idless.Id = null!;

        var aggregate = Aggregated(
            Lookup(idless), Quest(Collector, Item("gas-analyzer", 1)))["gas-analyzer"];

        Assert.Equal("gas-analyzer", aggregate.ItemId);
    }

    [Fact]
    public void The_rows_are_keyed_the_way_the_scope_and_the_inventory_are_keyed()
    {
        // Case-insensitively: the page looks a row up again by the name the inventory service
        // holds, and an ordinal key would list the same item twice.
        var aggregated = Aggregated(
            Lookup(Known("gas-analyzer")),
            Quest(Collector, Item("gas-analyzer", 1)),
            Quest("chemical-part-4", Item("Gas-Analyzer", 1)));

        Assert.Equal(2, Assert.Single(aggregated).Value.QuestCount);
    }

    #endregion

    /// <summary>The whole chain a load runs: every quest in scope, aggregated against a lookup.</summary>
    private static Dictionary<string, CollectorQuestItemAggregate> Aggregated(
        IReadOnlyDictionary<string, TarkovItem>? lookup, params TarkovTask[] quests)
        => CollectorScope.Aggregate(
            CollectorScope.ItemsInScope(
                quests,
                quests.Select(quest => quest.NormalizedName!).ToHashSet(StringComparer.OrdinalIgnoreCase)),
            lookup);
}
