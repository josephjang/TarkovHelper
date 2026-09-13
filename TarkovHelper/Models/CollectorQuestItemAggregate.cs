namespace TarkovHelper.Models;

/// <summary>
/// One item the Collector chain asks for, summed over every quest in scope: what the Collector
/// page's rows are built from.
/// <para>
/// A model rather than a page type, because the rule that fills it
/// (<see cref="TarkovHelper.Services.CollectorScope.Aggregate"/>) is a domain rule with no WPF in
/// it: the counts are "how many does the chain still want", and the view model layer only turns
/// them into text. <see cref="QuestCount"/> and <see cref="QuestFIRCount"/> are counts of ITEMS,
/// not of quests, except for currency, which counts one per quest that asks for it (a quest
/// wanting 500000 roubles contributes 1, not 500000).
/// </para>
/// </summary>
internal class CollectorQuestItemAggregate
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string? ItemNameKo { get; set; }
    public string? ItemNameJa { get; set; }
    public string ItemNormalizedName { get; set; } = string.Empty;
    public string? IconLink { get; set; }
    public string? WikiLink { get; set; }

    /// <summary>How many of the item the quests in scope want in total.</summary>
    public int QuestCount { get; set; }

    /// <summary>How many of that total have to be found in raid.</summary>
    public int QuestFIRCount { get; set; }

    /// <summary>Whether any quest in scope wants the item found in raid.</summary>
    public bool FoundInRaid { get; set; }
}
