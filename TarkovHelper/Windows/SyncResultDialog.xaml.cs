using System.Windows;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Windows;

/// <summary>
/// Sync result dialog window.
/// <para>
/// Reports what a sync applied, per profile, and asks only the questions the logs cannot
/// answer: which of two mutually exclusive prerequisites the player actually took (PRD R2a).
/// The per-quest confirmation list this used to show is gone — once attribution comes from the
/// logs rather than from the selected profile, confirming quest names one by one asks the
/// player to second-guess a judgment they have no better information about than the app does,
/// over a list that now spans several profiles. See fix-profile-data-attribution.md.
/// </para>
/// </summary>
public partial class SyncResultDialog : Window
{
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly SyncResult _result;
    private List<AlternativeQuestGroupViewModel>? _alternativeGroups;

    /// <summary>
    /// The alternative-quest choices to apply, or null when the player skipped them. Never the
    /// derived changes: those are applied before this dialog opens and are not up for review.
    /// </summary>
    public List<QuestChangeInfo>? SelectedChanges { get; private set; }

    /// <summary>
    /// Gets the count of alternative quest groups that were processed.
    /// </summary>
    public int AlternativeGroupCount => _alternativeGroups?.Count ?? 0;

    public SyncResultDialog(SyncResult result)
    {
        InitializeComponent();
        _result = result;

        SetupUI();
        UpdateLocalizedText();
    }

    /// <summary>
    /// Show the sync summary and return the alternative-quest choices to apply.
    /// </summary>
    /// <param name="result">The sync result to display.</param>
    /// <param name="owner">Optional owner window for centering.</param>
    /// <returns>The alternative-quest choices to apply, or null if the player skipped them.</returns>
    public static List<QuestChangeInfo>? ShowResult(SyncResult result, Window? owner, out int alternativeCount)
    {
        var dialog = new SyncResultDialog(result);
        if (owner != null)
        {
            dialog.Owner = owner;
        }
        dialog.ShowDialog();
        alternativeCount = dialog.AlternativeGroupCount;
        return dialog.SelectedChanges;
    }

    /// <summary>
    /// One summary row: a profile that was written to, and how much landed there. Public because
    /// the XAML DataTemplate binds to it — WPF binding resolves members by reflection and a row
    /// type the templating engine cannot see renders blank instead of failing loudly.
    /// </summary>
    public sealed record AppliedProfileRow(string ProfileName, string AppliedText);

    private void SetupUI()
    {
        // Deterministic order so two runs writing the same profiles read the same way.
        AppliedByProfileList.ItemsSource = _result.AppliedCountsByProfile
            .OrderBy(entry => entry.Key)
            .Select(entry => new AppliedProfileRow(
                _loc.ProfileName(entry.Key),
                string.Format(_loc.SyncAppliedCountFormat, entry.Value)))
            .ToList();

        // Handle alternative quest groups
        if (_result.AlternativeQuestGroups.Count > 0)
        {
            AlternativeQuestGroupViewModel.ResetCounter();
            _alternativeGroups = _result.AlternativeQuestGroups
                .Select(CreateAlternativeGroupViewModel)
                .ToList();

            AlternativeQuestsList.ItemsSource = _alternativeGroups;
            AlternativeQuestsSection.Visibility = Visibility.Visible;
            // Skip is only meaningful when there is something to skip; with no groups the
            // dialog is a report and a lone OK is the honest control.
            BtnCancel.Visibility = Visibility.Visible;
        }
        else
        {
            _alternativeGroups = null;
            AlternativeQuestsSection.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateLocalizedText()
    {
        TxtTitle.Text = _loc.SyncSummaryTitle;
        TxtAppliedHeader.Text = _result.AppliedCountsByProfile.Count > 0
            ? _loc.SyncAppliedHeader
            : _loc.SyncAppliedNone;

        TxtStats.Text = string.Format(
            _loc.SyncStatsFormat,
            _result.TotalEventsFound,
            _result.AlreadyCurrentCount,
            _result.PrerequisitesAutoCompleted,
            // The dialog used to list in-progress quests by name in a column of their own. The
            // list went with the per-quest review; the count stays, so a player can still tell a
            // quest was seen and deliberately left Active rather than missed.
            _result.InProgressQuests.Count,
            _result.UnattributedEventCount,
            _result.UnmatchedQuestIds.Count);

        BtnCancel.Content = _loc.SyncSummarySkipButton;
        BtnConfirm.Content = _loc.SyncSummaryConfirmButton;

        if (_result.AlternativeQuestGroups.Count > 0)
        {
            TxtAlternativeHeader.Text = string.Format(
                _loc.SyncAlternativesHeaderFormat, _result.AlternativeQuestGroups.Count);
        }
    }

    private AlternativeQuestGroupViewModel CreateAlternativeGroupViewModel(AlternativeQuestGroup group)
    {
        var vm = new AlternativeQuestGroupViewModel
        {
            OriginalGroup = group,
            // The profile is part of the label: the same either-or can be open in more than one
            // profile, so two otherwise identical groups must be distinguishable.
            GroupLabel = string.Format(
                _loc.SyncAlternativeGroupFormat,
                _loc.ProfileName(group.OwnerProfile),
                string.Join(" / ", group.Choices.Select(c => _loc.GetQuestName(c.Task))))
        };

        foreach (var choice in group.Choices)
        {
            var choiceVm = new AlternativeQuestChoiceViewModel
            {
                GroupName = vm.GroupName,
                QuestName = _loc.GetQuestName(choice.Task),
                IsCompleted = choice.IsCompleted,
                IsFailed = choice.IsFailed,
                IsSelected = choice.IsSelected,
                OriginalChoice = choice
            };
            vm.Choices.Add(choiceVm);
        }

        // If none selected, select first enabled one
        if (!vm.Choices.Any(c => c.IsSelected) && vm.Choices.Any(c => c.IsEnabled))
        {
            vm.Choices.First(c => c.IsEnabled).IsSelected = true;
        }

        return vm;
    }

    private List<QuestChangeInfo> BuildSelectedChanges()
    {
        var selectedChanges = new List<QuestChangeInfo>();
        if (_alternativeGroups == null) return selectedChanges;

        foreach (var group in _alternativeGroups)
        {
            var selectedChoice = group.Choices.FirstOrDefault(c => c.IsSelected && c.IsEnabled);
            if (selectedChoice == null) continue;

            // Each change carries the group's profile, so the apply step writes the answer to
            // the profile the question was asked about rather than to whatever is on screen.
            var owner = group.OriginalGroup.OwnerProfile;

            var task = selectedChoice.OriginalChoice.Task;
            selectedChanges.Add(new QuestChangeInfo
            {
                QuestName = task.Name,
                NormalizedName = task.NormalizedName ?? "",
                Trader = task.Trader,
                IsPrerequisite = true,
                ChangeType = QuestEventType.Completed,
                OwnerProfile = owner,
                IsSelected = true,
                Timestamp = DateTime.Now
            });

            // Fail the other alternatives
            foreach (var otherChoice in group.Choices.Where(c => c != selectedChoice && !c.IsCompleted))
            {
                var otherTask = otherChoice.OriginalChoice.Task;
                selectedChanges.Add(new QuestChangeInfo
                {
                    QuestName = otherTask.Name,
                    NormalizedName = otherTask.NormalizedName ?? "",
                    Trader = otherTask.Trader,
                    IsPrerequisite = true,
                    ChangeType = QuestEventType.Failed,
                    OwnerProfile = owner,
                    IsSelected = true,
                    Timestamp = DateTime.Now
                });
            }
        }

        return selectedChanges;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        SelectedChanges = null;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedChanges = null;
        Close();
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e)
    {
        SelectedChanges = BuildSelectedChanges();
        Close();
    }
}
