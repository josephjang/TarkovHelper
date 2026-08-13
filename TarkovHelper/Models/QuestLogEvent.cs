namespace TarkovHelper.Models
{
    /// <summary>
    /// Type of quest event from game logs
    /// </summary>
    public enum QuestEventType
    {
        /// <summary>
        /// Quest started (message.type == 10)
        /// </summary>
        Started,

        /// <summary>
        /// Quest completed successfully (message.type == 12)
        /// </summary>
        Completed,

        /// <summary>
        /// Quest failed (message.type == 11)
        /// </summary>
        Failed
    }

    /// <summary>
    /// Represents a quest event parsed from game logs
    /// </summary>
    public class QuestLogEvent
    {
        /// <summary>
        /// Quest ID from templateId (first token)
        /// </summary>
        public string QuestId { get; set; } = string.Empty;

        /// <summary>
        /// Type of event (Started, Completed, Failed)
        /// </summary>
        public QuestEventType EventType { get; set; }

        /// <summary>
        /// Trader ID from dialogId
        /// </summary>
        public string TraderId { get; set; } = string.Empty;

        /// <summary>
        /// Event timestamp from dt field (Unix timestamp converted to DateTime). When the log
        /// block carried no readable dt this holds the parse time as an ordering placeholder;
        /// see <see cref="HasTimestamp"/> before using it as evidence of anything.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Whether <see cref="Timestamp"/> came from the log's own dt field.
        /// <para>
        /// False means the block had none and the placeholder is the parse time. That placeholder
        /// must never reach the session-mode lookup: "now" resolves to the LAST transition in the
        /// folder, so an undated event from an old PvE session would be attributed to whatever
        /// mode that session happened to end in. An undated event is unattributed evidence and is
        /// counted as such.
        /// </para>
        /// </summary>
        public bool HasTimestamp { get; set; }

        /// <summary>
        /// Original log line for debugging
        /// </summary>
        public string? OriginalLine { get; set; }

        /// <summary>
        /// Source log file name
        /// </summary>
        public string? SourceFile { get; set; }

        /// <summary>
        /// The profile this event belongs to, derived from the <c>Session mode</c> timeline of
        /// the session folder it was parsed from.
        /// <para>
        /// Null means "no evidence" and never a default: an event from before the first mode
        /// marker in its folder (or one with no timestamp to look up) has nothing saying where it
        /// belongs, and assigning it to the selected profile (or to any default) is exactly the
        /// misfiling this attribution exists to stop.
        /// </para>
        /// <para>
        /// <see cref="TarkovHelper.Services.LogSyncService"/> enforces the drop rather than
        /// trusting each consumer to remember it: the live path never raises a null-owner event,
        /// and the sync path filters them out and reports
        /// <see cref="SyncResult.UnattributedEventCount"/>. A consumer holding one of these
        /// directly still has to check.
        /// </para>
        /// </summary>
        public AppProfile? OwnerProfile { get; set; }
    }

    /// <summary>
    /// Result of quest log synchronization
    /// </summary>
    public class SyncResult
    {
        /// <summary>
        /// Total quest events found in logs
        /// </summary>
        public int TotalEventsFound { get; set; }

        /// <summary>
        /// Quests marked as started
        /// </summary>
        public int QuestsStarted { get; set; }

        /// <summary>
        /// Quests marked as completed (from log events)
        /// </summary>
        public int QuestsCompleted { get; set; }

        /// <summary>
        /// Quests marked as failed
        /// </summary>
        public int QuestsFailed { get; set; }

        /// <summary>
        /// Prerequisites auto-completed due to started quests
        /// </summary>
        public int PrerequisitesAutoCompleted { get; set; }

        /// <summary>
        /// Quest IDs that couldn't be matched to TarkovTask
        /// </summary>
        public List<string> UnmatchedQuestIds { get; set; } = new();

        /// <summary>
        /// Errors encountered during sync
        /// </summary>
        public List<string> Errors { get; set; } = new();

        /// <summary>
        /// List of quests that will be completed (for confirmation dialog)
        /// </summary>
        public List<QuestChangeInfo> QuestsToComplete { get; set; } = new();

        /// <summary>
        /// List of quests that are currently in progress (started but not completed/failed)
        /// </summary>
        public List<TarkovTask> InProgressQuests { get; set; } = new();

        /// <summary>
        /// Groups of alternative quests (mutually exclusive) that need user selection
        /// Each group contains quests where one must be chosen as completed
        /// </summary>
        public List<AlternativeQuestGroup> AlternativeQuestGroups { get; set; } = new();

        /// <summary>
        /// Count of alternative quest groups that need user selection
        /// </summary>
        public int AlternativeQuestCount => AlternativeQuestGroups.Count;

        /// <summary>
        /// How many quest records were written to each profile, filled in by the apply step.
        /// This is what the summary reports (PRD R2): with the review step gone, naming the
        /// profiles that were written to is the only signal a player has that a sync went
        /// somewhere unexpected.
        /// </summary>
        public Dictionary<AppProfile, int> AppliedCountsByProfile { get; set; } = new();

        /// <summary>
        /// Profiles whose batch threw while being written. The apply step isolates each profile so
        /// one failure cannot cost the others their report, which leaves a failed partition simply
        /// absent from <see cref="AppliedCountsByProfile"/> and indistinguishable from one that
        /// needed no change. PRD R2 makes this report the player's only signal, so an
        /// attempted-and-failed partition has to be named.
        /// </summary>
        public List<AppProfile> FailedProfiles { get; set; } = new();

        /// <summary>
        /// Quests whose final state in the logs already matched what was stored for their own
        /// profile, so nothing was written for them.
        /// </summary>
        public int AlreadyCurrentCount { get; set; }

        /// <summary>
        /// Quest events whose game mode could not be determined from the logs. They are not
        /// recorded under any profile (PRD R3); the count is reported so a player can see that
        /// something was dropped rather than silently misfiled.
        /// </summary>
        public int UnattributedEventCount { get; set; }

        /// <summary>
        /// Quest events dropped because they are not after their owner profile's reset
        /// watermark. The game retains several days of session logs, so without this fence the
        /// first sync after a reset would re-import the removed progress into exactly the
        /// profile that was just reset (PRD R6 of feature-complete-profile-reset.md). Counted
        /// so the player can see the fence working rather than wonder where the events went.
        /// </summary>
        public int PreResetEventCount { get; set; }

        /// <summary>
        /// Whether the sync was successful overall
        /// </summary>
        public bool Success => Errors.Count == 0 || TotalEventsFound > 0;
    }

    /// <summary>
    /// A group of mutually exclusive quests where user must select one
    /// </summary>
    public class AlternativeQuestGroup
    {
        /// <summary>
        /// List of quests in this group (user selects one)
        /// </summary>
        public List<AlternativeQuestChoice> Choices { get; set; } = new();

        /// <summary>
        /// Whether any quest in this group needs to be selected
        /// (true if at least one is a prerequisite for a started/completed quest)
        /// </summary>
        public bool IsRequired { get; set; }

        /// <summary>
        /// The profile whose progress this choice resolves. The same either-or can be open in
        /// more than one profile at once, so the group carries its owner rather than inheriting
        /// whichever profile happens to be selected when the player answers.
        /// <para>
        /// Required, because the default of an <see cref="AppProfile"/> field is
        /// <see cref="AppProfile.PvpZone"/>: an initializer that simply forgot this would file the
        /// choice into the permanent PvP partition, the one that is most expensive to lose and
        /// hardest to notice. The partition key must never come from a default.
        /// </para>
        /// </summary>
        public required AppProfile OwnerProfile { get; set; }
    }

    /// <summary>
    /// A choice within an alternative quest group
    /// </summary>
    public class AlternativeQuestChoice
    {
        /// <summary>
        /// The quest
        /// </summary>
        public TarkovTask Task { get; set; } = null!;

        /// <summary>
        /// Whether this choice is selected
        /// </summary>
        public bool IsSelected { get; set; }

        /// <summary>
        /// Whether this quest is already completed
        /// </summary>
        public bool IsCompleted { get; set; }

        /// <summary>
        /// Whether this quest is already failed
        /// </summary>
        public bool IsFailed { get; set; }
    }

    /// <summary>
    /// ViewModel for alternative quest group display in sync dialog
    /// </summary>
    public class AlternativeQuestGroupViewModel
    {
        private static int _groupCounter = 0;

        /// <summary>
        /// Unique group name for RadioButton grouping
        /// </summary>
        public string GroupName { get; set; }

        /// <summary>
        /// Display label for the group
        /// </summary>
        public string GroupLabel { get; set; } = string.Empty;

        /// <summary>
        /// Choices in this group
        /// </summary>
        public List<AlternativeQuestChoiceViewModel> Choices { get; set; } = new();

        /// <summary>
        /// Original group data
        /// </summary>
        public AlternativeQuestGroup OriginalGroup { get; set; } = null!;

        public AlternativeQuestGroupViewModel()
        {
            GroupName = $"AltQuestGroup_{++_groupCounter}";
        }

        /// <summary>
        /// Reset group counter (call before creating new set of ViewModels)
        /// </summary>
        public static void ResetCounter() => _groupCounter = 0;
    }

    /// <summary>
    /// ViewModel for alternative quest choice display in sync dialog
    /// </summary>
    public class AlternativeQuestChoiceViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;

        /// <summary>
        /// Quest name for display
        /// </summary>
        public string QuestName { get; set; } = string.Empty;

        /// <summary>
        /// Group name for RadioButton grouping
        /// </summary>
        public string GroupName { get; set; } = string.Empty;

        /// <summary>
        /// Whether this choice is selected
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        /// <summary>
        /// Whether this quest is already completed
        /// </summary>
        public bool IsCompleted { get; set; }

        /// <summary>
        /// Whether this quest is already failed
        /// </summary>
        public bool IsFailed { get; set; }

        /// <summary>
        /// Whether this choice can be selected (not failed)
        /// </summary>
        public bool IsEnabled => !IsFailed;

        /// <summary>
        /// Original choice data
        /// </summary>
        public AlternativeQuestChoice OriginalChoice { get; set; } = null!;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Information about a quest change for the confirmation dialog
    /// </summary>
    public class QuestChangeInfo
    {
        /// <summary>
        /// Quest display name
        /// </summary>
        public string QuestName { get; set; } = string.Empty;

        /// <summary>
        /// Quest normalized name for identification
        /// </summary>
        public string NormalizedName { get; set; } = string.Empty;

        /// <summary>
        /// Trader name
        /// </summary>
        public string Trader { get; set; } = string.Empty;

        /// <summary>
        /// Whether this is a prerequisite auto-completion
        /// </summary>
        public bool IsPrerequisite { get; set; }

        /// <summary>
        /// The change type (Completed, Failed, Started)
        /// </summary>
        public QuestEventType ChangeType { get; set; }

        /// <summary>
        /// Whether this quest is selected for completion
        /// </summary>
        public bool IsSelected { get; set; } = true;

        /// <summary>
        /// The profile this change belongs to, carried from the event that produced it so the
        /// apply step and the summary never have to re-derive it (and can never disagree about
        /// it). Only attributed events become a change, so this is never "unknown".
        /// <para>
        /// Required, because the default of an <see cref="AppProfile"/> field is
        /// <see cref="AppProfile.PvpZone"/>: an initializer that simply forgot this would write
        /// the change into the permanent PvP partition, silently and irreversibly. Making the
        /// compiler ask for it is what stops the partition key coming from a default.
        /// </para>
        /// </summary>
        public required AppProfile OwnerProfile { get; set; }

        /// <summary>
        /// Event timestamp for chronological ordering
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Formatted timestamp for display
        /// </summary>
        public string FormattedTimestamp => Timestamp != default
            ? Timestamp.ToString("MM/dd HH:mm")
            : "";
    }
}
