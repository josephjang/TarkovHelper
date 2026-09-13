using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Pages
{
    /// <summary>
    /// Quest list view model for display
    /// </summary>
    public class QuestViewModel
    {
        public TarkovTask Task { get; set; } = null!;
        public string DisplayName { get; set; } = string.Empty;
        public string SubtitleName { get; set; } = string.Empty;
        public Visibility SubtitleVisibility { get; set; } = Visibility.Collapsed;
        public string TraderInitial { get; set; } = string.Empty;
        public QuestStatus Status { get; set; }
        public string StatusText { get; set; } = string.Empty;
        public Brush StatusBackground { get; set; } = Brushes.Gray;
        public Visibility CompleteButtonVisibility { get; set; } = Visibility.Visible;
        public bool IsKappaRequired { get; set; }
        public Visibility KappaBadgeVisibility => IsKappaRequired ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Required item view model
    /// </summary>
    public class RequiredItemViewModel
    {
        public string DisplayText { get; set; } = string.Empty;
        public bool FoundInRaid { get; set; }
        public Visibility FirVisibility => FoundInRaid ? Visibility.Visible : Visibility.Collapsed;
        public BitmapImage? IconSource { get; set; }
        public string RequirementType { get; set; } = string.Empty;
        public Visibility RequirementTypeVisibility =>
            string.IsNullOrEmpty(RequirementType) ? Visibility.Collapsed : Visibility.Visible;

        // Navigation identifier (use ItemId for cross-tab navigation)
        public string ItemId { get; set; } = string.Empty;

        // Fulfillment status
        public bool IsFulfilled { get; set; }
        public TextDecorationCollection? TextDecorations => IsFulfilled ? System.Windows.TextDecorations.Strikethrough : null;
        public double ItemOpacity => IsFulfilled ? 0.6 : 1.0;
        public Visibility FulfilledVisibility => IsFulfilled ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// One line in the detail pane's Requirements section: what the quest asks for against what
    /// the profile carries, in the unmet colour while it is not satisfied.
    /// <para>
    /// One shape for every requirement kind - player level, Scav karma and each trader loyalty
    /// row - so the section is one list bound to one ItemsControl. The level and karma lines used
    /// to be named TextBlocks with their own inline if/else, their own visibility bool and their
    /// own copy of the "met" rule, which is what made adding loyalty a third mechanism and grew
    /// the section wrapper's visibility a term at a time.
    /// </para>
    /// </summary>
    public class RequirementLineViewModel
    {
        public string DisplayText { get; set; } = string.Empty;
        public Brush Foreground { get; set; } = Brushes.White;

        /// <summary>
        /// The lines one quest shows under Requirements, against the values
        /// <paramref name="settings"/> carries: the player level, the Scav karma, then one per
        /// trader the quest names. Empty when it carries none of them, which is what collapses
        /// the section.
        /// <para>
        /// The order is the badge's own precedence order, so the first unmet line here is the
        /// requirement <c>QuestRequirementBadge</c> names and the top of the list and the badge
        /// tell one story. The loyalty rows keep the order the quest carries them in, established
        /// once by <see cref="QuestDbService.SortIntoBadgeOrder"/> at load: the quest's own trader
        /// first, then the game's trader order. That order does not depend on what the player has
        /// entered, so the list never reshuffles itself as levels are typed in; the met/unmet
        /// colouring is what tells a satisfied line from the one still holding the quest.
        /// </para>
        /// <para>
        /// Every "met" answer comes from <see cref="QuestProgressService"/>, never from a
        /// comparison written here. A line rendered in the met colour beside a locked badge is
        /// exactly what a second copy of the rule produces once the two drift, and the level line
        /// carried such a copy.
        /// </para>
        /// </summary>
        /// <param name="traderDisplayName">
        /// The trader's name in the app's language, given the requirement row. Injected for the
        /// reason the badge injects it: this stays a pure function of its arguments.
        /// </param>
        /// <param name="brushes">
        /// The met and unmet colours, as one value (see <see cref="RequirementLineBrushes"/>).
        /// Injected for the same reason as the resolver, and so a case can hand in two sentinels
        /// and assert by reference which colour a line got.
        /// </param>
        internal static List<RequirementLineViewModel> BuildFor(
            TarkovTask task,
            ProfileSettingsSnapshot settings,
            LocalizationService loc,
            Func<QuestTraderRequirement, string> traderDisplayName,
            RequirementLineBrushes brushes)
        {
            var lines = new List<RequirementLineViewModel>();

            void Add(string text, bool isMet) => lines.Add(new RequirementLineViewModel
            {
                DisplayText = text,
                Foreground = isMet ? brushes.Met : brushes.Unmet,
            });

            if (task.RequiredLevel.HasValue && task.RequiredLevel.Value > 0)
            {
                Add(
                    string.Format(
                        loc.RequirementLevelFormat,
                        task.RequiredLevel.Value, settings.PlayerLevelOrDefault),
                    QuestProgressService.IsLevelRequirementMet(task, settings));
            }

            if (task.RequiredScavKarma.HasValue)
            {
                var requiredKarma = task.RequiredScavKarma.Value;
                // A negative requirement is an upper bound (a "bad karma" quest), so the line
                // shows which way the comparison runs.
                var comparison = requiredKarma < 0 ? "≤" : "≥";
                Add(
                    string.Format(
                        loc.RequirementScavKarmaFormat,
                        comparison, requiredKarma.ToString("0.#"),
                        settings.ScavRepOrDefault.ToString("0.#")),
                    QuestProgressService.IsScavKarmaRequirementMet(task, settings));
            }

            if (task.HasTraderLoyaltyRequirements)
            {
                foreach (var requirement in task.TraderLoyaltyRequirements!)
                {
                    Add(
                        string.Format(
                            loc.RequirementLoyaltyFormat,
                            traderDisplayName(requirement), requirement.Level,
                            settings.TraderLoyalty.LevelOf(requirement.TraderId)),
                        QuestProgressService.IsTraderLoyaltyMet(requirement, settings));
                }
            }

            return lines;
        }
    }

    /// <summary>
    /// Prerequisite group view model for displaying OR/AND grouped prerequisites
    /// </summary>
    public class PrerequisiteGroupViewModel
    {
        public int GroupId { get; set; }
        public bool IsOrGroup => GroupId > 0;
        public string GroupLabel => IsOrGroup ? "OR" : "";
        public Visibility OrLabelVisibility => IsOrGroup ? Visibility.Visible : Visibility.Collapsed;
        public Brush OrGroupBackground => IsOrGroup ? new SolidColorBrush(Color.FromArgb(30, 33, 150, 243)) : Brushes.Transparent;
        public List<PrerequisiteItemViewModel> Items { get; set; } = new();
    }

    /// <summary>
    /// Single prerequisite item view model
    /// </summary>
    public class PrerequisiteItemViewModel
    {
        public TarkovTask? Task { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string StatusText { get; set; } = string.Empty;
        public Brush StatusBackground { get; set; } = Brushes.Gray;
        public bool IsOrItem { get; set; }
        public string OrSeparator => IsOrItem ? " OR " : "";
        public Visibility OrSeparatorVisibility => IsOrItem ? Visibility.Visible : Visibility.Collapsed;
        public string BulletText => IsOrItem ? "" : "• ";
    }

    /// <summary>
    /// Recommendation view model for display
    /// </summary>
    public class RecommendationViewModel
    {
        public QuestRecommendation Recommendation { get; set; } = null!;
        public string QuestName { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string TypeText { get; set; } = string.Empty;
        public Brush TypeBackground { get; set; } = Brushes.Gray;
        public string TraderInitial { get; set; } = string.Empty;
        public bool IsKappaRequired { get; set; }
        public Visibility KappaBadgeVisibility => IsKappaRequired ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Guide image view model with loading state
    /// </summary>
    public class GuideImageViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private BitmapImage? _imageSource;
        private bool _isLoading = true;

        public string FileName { get; set; } = string.Empty;
        public string? Caption { get; set; }

        public BitmapImage? ImageSource
        {
            get => _imageSource;
            set
            {
                _imageSource = value;
                OnPropertyChanged(nameof(ImageSource));
                OnPropertyChanged(nameof(ImageVisibility));
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                _isLoading = value;
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(LoadingVisibility));
                OnPropertyChanged(nameof(ImageVisibility));
            }
        }

        public Visibility CaptionVisibility =>
            string.IsNullOrEmpty(Caption) ? Visibility.Collapsed : Visibility.Visible;

        public Visibility LoadingVisibility =>
            IsLoading ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ImageVisibility =>
            IsLoading ? Visibility.Collapsed : Visibility.Visible;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }
}
