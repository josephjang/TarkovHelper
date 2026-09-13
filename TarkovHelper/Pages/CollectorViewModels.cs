using System.Windows;
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

        /// <summary>
        /// The Kappa count, in the same words the detail pane uses, or empty when there was no
        /// count to show: the panel then says nothing rather than "0/0 Kappa quests completed",
        /// which would read as "nothing is flagged".
        /// </summary>
        public string CountText { get; }

        /// <summary>
        /// The panel for <paramref name="collector"/> as one render pass saw it. There is always a
        /// panel to build: loaded data with no Collector quest never reaches here, because the
        /// page has to collapse the panel itself in that case and returns before asking for one.
        /// </summary>
        /// <param name="collector">The Collector quest, which the caller has already found.</param>
        /// <param name="status">Collector's status in the pass.</param>
        /// <param name="gate">The gate the same walk stopped at: the condition the badge names.</param>
        /// <param name="settings">The pass's profile settings, which the lines are read against.</param>
        /// <param name="traderDisplayName">A trader's name in the app's language, given the requirement row.</param>
        /// <param name="brushes">The met and unmet colours the condition lines are painted in.</param>
        /// <param name="kappa">
        /// The Kappa count for the pass, or null when the graph is not built: the panel then
        /// shows no count rather than a zero one. An option and not two ints, because "0 of 0"
        /// is a reading the page does not have and must not state.
        /// </param>
        internal static CollectorUnlockViewModel BuildFor(
            TarkovTask collector,
            QuestStatus status,
            QuestGate gate,
            ProfileSettingsSnapshot settings,
            LocalizationService loc,
            Func<QuestTraderRequirement, string> traderDisplayName,
            (int Completed, int Total)? kappa,
            RequirementLineBrushes brushes)
        {
            return new CollectorUnlockViewModel(
                QuestRequirementBadge.StatusText(status, gate, collector, settings, traderDisplayName),
                status,
                RequirementLineViewModel.BuildFor(
                    collector, settings, loc, traderDisplayName, brushes),
                kappa is { } k ? string.Format(loc.KappaCountFormat, k.Completed, k.Total) : string.Empty);
        }
    }

    /// <summary>
    /// One row in the Collector page's item list: an item the Collector chain wants, and how
    /// much of it the player still has to find.
    /// <para>
    /// Everything a row shows lives on <see cref="ItemRowViewModel"/>, which the Items page's
    /// rows share; this type adds nothing, because the Collector list counts quest requirements
    /// only and has no hideout half to aggregate. It stays a type of its own so an Items row can
    /// never land in the Collector list, and because the page's quantity buttons pattern-match on
    /// it to find the row a click came from.
    /// </para>
    /// </summary>
    public sealed class CollectorItemViewModel : ItemRowViewModel
    {
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
}
