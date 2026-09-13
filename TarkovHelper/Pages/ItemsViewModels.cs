using System.Windows;
using TarkovHelper.Models;

namespace TarkovHelper.Pages
{
    /// <summary>
    /// One row in the Items page's item list: an item something in the game wants, and how much
    /// of it the player still has to find.
    /// <para>
    /// Everything a row shows lives on <see cref="ItemRowViewModel"/>, which the Collector page's
    /// rows share. What this page adds is the second source it aggregates: the hideout modules,
    /// counted alongside the quests into the row's totals, plus the categories its filters group
    /// rows by.
    /// </para>
    /// </summary>
    public sealed class AggregatedItemViewModel : ItemRowViewModel
    {
        public string? Category { get; set; }
        public string ParentCategory { get; set; } = "Other";

        // The hideout half of the row's totals. TotalCount is QuestCount + HideoutCount and
        // TotalFIRCount the same sum of the FIR halves, which is why a row can want more units
        // in all than it wants Found In Raid (see ItemRowViewModel.FulfillmentStatus).
        public int HideoutCount { get; set; }
        public int HideoutFIRCount { get; set; }

        public string HideoutDisplay => ItemCountDisplay.Required(HideoutCount, HideoutFIRCount);
    }

    /// <summary>
    /// Quest item source - shows which quest requires this item
    /// </summary>
    public class QuestItemSourceViewModel
    {
        public string QuestName { get; set; } = string.Empty;
        public string TraderName { get; set; } = string.Empty;
        public int Amount { get; set; }
        public bool FoundInRaid { get; set; }
        public string? WikiLink { get; set; }
        public TarkovTask? Task { get; set; }
        public string AmountDisplay => $"x{Amount}";
        public Visibility FirVisibility => FoundInRaid ? Visibility.Visible : Visibility.Collapsed;
        public Visibility WikiButtonVisibility => Task != null ? Visibility.Visible : Visibility.Collapsed;

        // Navigation identifier
        public string QuestNormalizedName { get; set; } = string.Empty;

        // Dogtag level requirement
        public int? DogtagMinLevel { get; set; }
        public bool HasDogtagLevel => DogtagMinLevel.HasValue;
        public Visibility DogtagLevelVisibility => HasDogtagLevel ? Visibility.Visible : Visibility.Collapsed;
        public string DogtagLevelDisplay => DogtagMinLevel.HasValue ? $"(Lv.{DogtagMinLevel}+)" : "";
    }

    /// <summary>
    /// Hideout item source - shows which hideout module requires this item
    /// </summary>
    public class HideoutItemSourceViewModel
    {
        public string ModuleName { get; set; } = string.Empty;
        public int Level { get; set; }
        public int Amount { get; set; }
        public bool FoundInRaid { get; set; }
        public string LevelDisplay => $"Level {Level}";
        public string AmountDisplay => $"x{Amount}";
        public Visibility FirVisibility => FoundInRaid ? Visibility.Visible : Visibility.Collapsed;

        // Navigation identifier
        public string StationId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Quest item aggregate for internal processing
    /// </summary>
    internal class QuestItemAggregate
    {
        public string ItemId { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public string? ItemNameKo { get; set; }
        public string? ItemNameJa { get; set; }
        public string ItemNormalizedName { get; set; } = string.Empty;
        public string? IconLink { get; set; }
        public string? WikiLink { get; set; }
        public string? Category { get; set; }
        public int QuestCount { get; set; }
        public int QuestFIRCount { get; set; }
        public bool FoundInRaid { get; set; }
    }
}
