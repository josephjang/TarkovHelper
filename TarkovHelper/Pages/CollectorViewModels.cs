using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Pages
{
    /// <summary>
    /// The Collector page's unlock panel: Collector's status badge, one line per unlock
    /// condition, and the Kappa count, as one value built from one render pass.
    /// <para>
    /// COMPOSED, with no rule of its own (feature-kappa-collector-1-1.spec.md, TD1). The badge
    /// is <see cref="QuestRequirementBadge.StatusText"/> over the gate the status walk reported,
    /// the lines are <see cref="RequirementLineViewModel.BuildFor"/> in the badge's own
    /// precedence order, and the count is the graph service's. So which condition holds
    /// Collector is the engine's answer, carried in the gate, and the badge and the first unmet
    /// line agree because both derive from it; a Collector-specific "met" comparison here would
    /// be the second copy of the gate rule the loyalty phase removed. The conditions are shown
    /// met or unmet and never counted into the Kappa number (PD1): they are values the player
    /// typed into the drawer, not progress the app tracks.
    /// </para>
    /// </summary>
    public sealed class CollectorUnlockViewModel
    {
        private CollectorUnlockViewModel(
            string statusText,
            QuestStatus status,
            IReadOnlyList<RequirementLineViewModel> lines,
            string countText)
        {
            StatusText = statusText;
            Status = status;
            Lines = lines;
            CountText = countText;
        }

        /// <summary>The badge text, the same string the quest list shows for Collector.</summary>
        public string StatusText { get; }

        /// <summary>The status behind the badge, for its fill (<see cref="QuestStatusBrushes.For"/>).</summary>
        public QuestStatus Status { get; }

        /// <summary>
        /// One line per condition the data carries for Collector, in badge order: the player
        /// level, the Scav karma, then one per trader loyalty row. The first unmet line is the
        /// condition the badge names.
        /// </summary>
        public IReadOnlyList<RequirementLineViewModel> Lines { get; }

        /// <summary>The Kappa count, in the same words the detail pane uses.</summary>
        public string CountText { get; }

        /// <summary>
        /// The panel for <paramref name="collector"/> as one render pass saw it, or null when the
        /// loaded data has no Collector quest (the page then collapses the panel).
        /// </summary>
        /// <param name="status">Collector's status in the pass.</param>
        /// <param name="gate">The gate the same walk stopped at: the condition the badge names.</param>
        /// <param name="settings">The pass's profile settings, which the lines are read against.</param>
        /// <param name="traderDisplayName">A trader's name in the app's language, given the requirement row.</param>
        /// <param name="kappaDone">Flagged Kappa quests done in the pass.</param>
        /// <param name="kappaTotal">Flagged Kappa quests in the loaded data.</param>
        internal static CollectorUnlockViewModel? BuildFor(
            TarkovTask? collector,
            QuestStatus status,
            QuestGate gate,
            ProfileSettingsSnapshot settings,
            LocalizationService loc,
            Func<QuestTraderRequirement, string> traderDisplayName,
            int kappaDone,
            int kappaTotal,
            Brush metBrush,
            Brush unmetBrush)
        {
            if (collector == null) return null;

            return new CollectorUnlockViewModel(
                QuestRequirementBadge.StatusText(status, gate, collector, settings, traderDisplayName),
                status,
                RequirementLineViewModel.BuildFor(
                    collector, settings, loc, traderDisplayName, metBrush, unmetBrush),
                string.Format(loc.KappaCountFormat, kappaDone, kappaTotal));
        }
    }

    /// <summary>
    /// Aggregated item view model for Collector page display with inventory tracking
    /// </summary>
    public class CollectorItemViewModel : INotifyPropertyChanged
    {
        public string ItemId { get; set; } = string.Empty;
        public string ItemNormalizedName { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string SubtitleName { get; set; } = string.Empty;
        public Visibility SubtitleVisibility { get; set; } = Visibility.Collapsed;
        public int QuestCount { get; set; }
        public int QuestFIRCount { get; set; }
        public int TotalCount { get; set; }
        public int TotalFIRCount { get; set; }
        public bool FoundInRaid { get; set; }
        public Visibility FirVisibility => FoundInRaid ? Visibility.Visible : Visibility.Collapsed;

        private BitmapImage? _iconSource;
        public BitmapImage? IconSource
        {
            get => _iconSource;
            set
            {
                if (_iconSource != value)
                {
                    _iconSource = value;
                    OnPropertyChanged(nameof(IconSource));
                }
            }
        }
        public string? IconLink { get; set; }
        public string? WikiLink { get; set; }

        // Inventory quantities (user's owned items)
        private int _ownedFirQuantity;
        private int _ownedNonFirQuantity;

        public int OwnedFirQuantity
        {
            get => _ownedFirQuantity;
            set
            {
                if (_ownedFirQuantity != value)
                {
                    _ownedFirQuantity = value;
                    OnPropertyChanged(nameof(OwnedFirQuantity));
                    OnPropertyChanged(nameof(OwnedTotalQuantity));
                    OnPropertyChanged(nameof(FulfillmentStatus));
                    OnPropertyChanged(nameof(ProgressPercent));
                    OnPropertyChanged(nameof(IsFulfilled));
                    OnPropertyChanged(nameof(FulfilledVisibility));
                    OnPropertyChanged(nameof(ItemOpacity));
                    OnPropertyChanged(nameof(NameTextDecorations));
                    OnPropertyChanged(nameof(OwnedDisplay));
                }
            }
        }

        public int OwnedNonFirQuantity
        {
            get => _ownedNonFirQuantity;
            set
            {
                if (_ownedNonFirQuantity != value)
                {
                    _ownedNonFirQuantity = value;
                    OnPropertyChanged(nameof(OwnedNonFirQuantity));
                    OnPropertyChanged(nameof(OwnedTotalQuantity));
                    OnPropertyChanged(nameof(FulfillmentStatus));
                    OnPropertyChanged(nameof(ProgressPercent));
                    OnPropertyChanged(nameof(IsFulfilled));
                    OnPropertyChanged(nameof(FulfilledVisibility));
                    OnPropertyChanged(nameof(ItemOpacity));
                    OnPropertyChanged(nameof(NameTextDecorations));
                    OnPropertyChanged(nameof(OwnedDisplay));
                }
            }
        }

        public int OwnedTotalQuantity => OwnedFirQuantity + OwnedNonFirQuantity;

        // Fulfillment calculation
        public ItemFulfillmentStatus FulfillmentStatus
        {
            get
            {
                if (TotalFIRCount > 0)
                {
                    // FIR is required
                    if (OwnedFirQuantity >= TotalFIRCount)
                        return ItemFulfillmentStatus.Fulfilled;
                    if (OwnedTotalQuantity > 0)
                        return ItemFulfillmentStatus.PartiallyFulfilled;
                    return ItemFulfillmentStatus.NotStarted;
                }
                else
                {
                    // Non-FIR OK
                    if (OwnedTotalQuantity >= TotalCount)
                        return ItemFulfillmentStatus.Fulfilled;
                    if (OwnedTotalQuantity > 0)
                        return ItemFulfillmentStatus.PartiallyFulfilled;
                    return ItemFulfillmentStatus.NotStarted;
                }
            }
        }

        public double ProgressPercent
        {
            get
            {
                if (TotalCount == 0) return 100;

                if (TotalFIRCount > 0)
                {
                    return Math.Min(100, (double)OwnedFirQuantity / TotalFIRCount * 100);
                }
                else
                {
                    return Math.Min(100, (double)OwnedTotalQuantity / TotalCount * 100);
                }
            }
        }

        public bool IsFulfilled => FulfillmentStatus == ItemFulfillmentStatus.Fulfilled;
        public Visibility FulfilledVisibility => IsFulfilled ? Visibility.Visible : Visibility.Collapsed;
        public double ItemOpacity => IsFulfilled ? 0.5 : 1.0;
        public TextDecorationCollection? NameTextDecorations => IsFulfilled ? TextDecorations.Strikethrough : null;

        // Owned display string
        public string OwnedDisplay
        {
            get
            {
                if (OwnedTotalQuantity == 0)
                    return "0";
                if (OwnedNonFirQuantity == 0)
                    return $"{OwnedFirQuantity}F";
                if (OwnedFirQuantity == 0)
                    return OwnedNonFirQuantity.ToString();
                return $"{OwnedFirQuantity}F+{OwnedNonFirQuantity}";
            }
        }

        // Display strings for UI
        public string QuestCountDisplay => QuestCount > 0 ? FormatCountDisplay(QuestCount, QuestFIRCount) : "0";
        public string TotalDisplay => FormatCountDisplay(TotalCount, TotalFIRCount);

        private static string FormatCountDisplay(int total, int firCount)
        {
            if (firCount == 0)
                return total.ToString();
            if (firCount == total)
                return $"{total} (FIR)";
            var nonFirCount = total - firCount;
            return $"{firCount}F+{nonFirCount}";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Quest item source for Collector page - shows which quest requires this item
    /// </summary>
    public class CollectorQuestItemSourceViewModel
    {
        public string QuestName { get; set; } = string.Empty;
        public string TraderName { get; set; } = string.Empty;
        public int Amount { get; set; }
        public bool FoundInRaid { get; set; }
        public bool IsKappaRequired { get; set; }
        public string? WikiLink { get; set; }
        public TarkovTask? Task { get; set; }
        public string AmountDisplay => $"x{Amount}";
        public Visibility FirVisibility => FoundInRaid ? Visibility.Visible : Visibility.Collapsed;
        public Visibility KappaVisibility => IsKappaRequired ? Visibility.Visible : Visibility.Collapsed;
        public Visibility WikiButtonVisibility => Task != null ? Visibility.Visible : Visibility.Collapsed;
        public string QuestNormalizedName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Internal class for aggregating collector quest items
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
        public int QuestCount { get; set; }
        public int QuestFIRCount { get; set; }
        public bool FoundInRaid { get; set; }
    }
}
