using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// Whose items the Collector page lists, and how those items add up: the quest set one render of
/// the list covers, the (quest, required item) pairs inside it, and the per-item totals the rows
/// are built from.
/// <para>
/// These are domain rules, not page rules, so they live here rather than on
/// <c>CollectorPage</c>, where they were private instance members of a control the unit suite
/// cannot construct (its markup resolves App.xaml's brushes and its field initializers open the
/// databases). Every rule below is now reachable by a test that runs it -
/// <c>CollectorScopeTests</c> - instead of only by a guard that reads the page's source text.
/// </para>
/// <para>
/// Nothing here reads a service. The two questions that need one - what a quest's status is, and
/// what a quest's prerequisites are - arrive as delegates, so a caller answers them within its own
/// render pass (see <c>RenderPass</c>) and no rule here can reach past that pass for a live
/// reading.
/// </para>
/// </summary>
internal static class CollectorScope
{
    // Currency items should count by reference count, not total amount: a quest wanting 500000
    // roubles is one entry to satisfy, not half a million items to find.
    private static readonly HashSet<string> CurrencyItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "roubles", "dollars", "euros"
    };

    private static bool IsCurrency(string normalizedName) => CurrencyItems.Contains(normalizedName);

    /// <summary>A quest whose items are still worth listing: not done, not failed, not barred.</summary>
    internal static bool IsInScope(QuestStatus status)
        => status != QuestStatus.Done && status != QuestStatus.Failed && status != QuestStatus.Unavailable;

    /// <summary>
    /// The quests whose items the page lists: Collector itself unless it is Done, Failed or
    /// Unavailable, plus (with the option on) every transitive prerequisite in the same states.
    /// One computation per load for the item aggregation and the detail panel's quest sources,
    /// which used to carry two near-identical copies of it, each calling GetStatus live per quest.
    /// The set's content is unchanged; under the 1.1 data the transitive walk is the twelve
    /// flagged quests.
    /// </summary>
    /// <param name="collector">
    /// The Collector quest, or null when the loaded data has none: the scope is then empty, and so
    /// is the list.
    /// </param>
    /// <param name="statusOf">
    /// A quest's status, which the caller answers within one render pass so every quest in the
    /// walk is judged against the same snapshots.
    /// </param>
    /// <param name="prerequisitesOf">
    /// The transitive prerequisites of a quest by normalized name
    /// (<see cref="QuestGraphService.GetAllPrerequisites"/>). Read only when
    /// <paramref name="includePrerequisites"/> is set.
    /// </param>
    /// <param name="includePrerequisites">The page's "include prerequisite quests" option.</param>
    internal static HashSet<string> QuestsInScope(
        TarkovTask? collector,
        Func<TarkovTask, QuestStatus> statusOf,
        Func<string, IEnumerable<TarkovTask>> prerequisitesOf,
        bool includePrerequisites)
    {
        var quests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (collector == null || string.IsNullOrEmpty(collector.NormalizedName)) return quests;

        if (IsInScope(statusOf(collector)))
        {
            quests.Add(collector.NormalizedName);
        }

        if (includePrerequisites)
        {
            foreach (var prerequisite in prerequisitesOf(collector.NormalizedName))
            {
                if (string.IsNullOrEmpty(prerequisite.NormalizedName)) continue;
                if (!IsInScope(statusOf(prerequisite))) continue;
                quests.Add(prerequisite.NormalizedName);
            }
        }

        return quests;
    }

    /// <summary>
    /// Every (quest, required item) pair the given quest set covers: the one decision site for
    /// whose items count, shared by the item aggregation and the detail panel's quest sources,
    /// which used to carry a copy of these three exclusions each. A quest with no normalized name
    /// cannot be in a set keyed by name, a quest outside the set is not listed, and a quest with
    /// no required-item rows has nothing to contribute.
    /// </summary>
    /// <param name="tasks">Every loaded quest, which the set is a subset of.</param>
    /// <param name="quests">The normalized names <see cref="QuestsInScope"/> answered with.</param>
    internal static IEnumerable<(TarkovTask Task, QuestItem Item)> ItemsInScope(
        IEnumerable<TarkovTask> tasks, IReadOnlySet<string> quests)
    {
        foreach (var task in tasks)
        {
            if (string.IsNullOrEmpty(task.NormalizedName))
                continue;

            if (!quests.Contains(task.NormalizedName))
                continue;

            if (task.RequiredItems == null)
                continue;

            foreach (var questItem in task.RequiredItems)
            {
                yield return (task, questItem);
            }
        }
    }

    /// <summary>
    /// The per-item totals one item list is rendered from, keyed by normalized name: how many the
    /// quests in scope want, and how many of those have to be found in raid.
    /// <para>
    /// An item the Items table does not carry is dropped rather than listed under a name and an
    /// icon the page does not have. Currency counts one per asking quest (see
    /// <see cref="CurrencyItems"/>); everything else sums its amounts. The found-in-raid half
    /// counts the same amount again, but only for the rows that asked for it, so an item two
    /// quests want in raid and one wants anywhere reads "3, of which 2 in raid".
    /// </para>
    /// </summary>
    /// <param name="items">The pairs <see cref="ItemsInScope"/> yielded.</param>
    /// <param name="itemLookup">
    /// The Items table by normalized name, or null before it has loaded: nothing can be named
    /// then, so nothing is listed.
    /// </param>
    internal static Dictionary<string, CollectorQuestItemAggregate> Aggregate(
        IEnumerable<(TarkovTask Task, QuestItem Item)> items,
        IReadOnlyDictionary<string, TarkovItem>? itemLookup)
    {
        var result = new Dictionary<string, CollectorQuestItemAggregate>(StringComparer.OrdinalIgnoreCase);

        foreach (var (_, questItem) in items)
        {
            // Direct lookup by ItemId (QuestRequiredItems.ItemId -> Items.Id)
            TarkovItem? itemInfo = null;
            itemLookup?.TryGetValue(questItem.ItemNormalizedName, out itemInfo);

            // Skip if item not found in Items table
            if (itemInfo == null)
                continue;

            var itemName = itemInfo.Name;
            var iconLink = itemInfo.IconLink;
            var wikiLink = itemInfo.WikiLink;

            var countToAdd = IsCurrency(questItem.ItemNormalizedName) ? 1 : questItem.Amount;
            var firCountToAdd = questItem.FoundInRaid ? countToAdd : 0;

            if (result.TryGetValue(questItem.ItemNormalizedName, out var existing))
            {
                existing.QuestCount += countToAdd;
                if (questItem.FoundInRaid)
                {
                    existing.QuestFIRCount += countToAdd;
                    existing.FoundInRaid = true;
                }
            }
            else
            {
                result[questItem.ItemNormalizedName] = new CollectorQuestItemAggregate
                {
                    ItemId = itemInfo.Id ?? questItem.ItemNormalizedName,
                    ItemName = itemName,
                    ItemNameKo = itemInfo.NameKo,
                    ItemNameJa = itemInfo.NameJa,
                    ItemNormalizedName = questItem.ItemNormalizedName,
                    IconLink = iconLink,
                    WikiLink = wikiLink,
                    QuestCount = countToAdd,
                    QuestFIRCount = firCountToAdd,
                    FoundInRaid = questItem.FoundInRaid
                };
            }
        }

        return result;
    }
}
