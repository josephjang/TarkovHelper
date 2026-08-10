using System.Collections.Immutable;
using TarkovHelper.Debug;
using TarkovHelper.Models;

namespace TarkovHelper.Services
{
    /// <summary>
    /// Service for managing quest progress state.
    /// <para>
    /// Progress lives in a single immutable <see cref="ProgressSnapshot"/> that carries the
    /// profile it belongs to. Readers capture the field once into a local and use that local
    /// throughout, which gives an internally consistent view without a lock; writers derive the
    /// next snapshot from the one they read, publish it with a compare-and-swap, and persist
    /// under <em>that</em> snapshot's ProfileId. Nothing on a write path asks
    /// <see cref="ProfileService"/> which profile is selected — see
    /// <c>fix-profile-data-attribution.spec.md</c> for why that question has the wrong answer
    /// for everything except hand entry, and <c>ProfileAttributionSourceTests</c> for the guard
    /// that keeps it out.
    /// </para>
    /// </summary>
    public class QuestProgressService
    {
        private static QuestProgressService? _instance;
        public static QuestProgressService Instance => _instance ??= new QuestProgressService();

        private QuestProgressService()
        {
            // Allowlisted selection read. The snapshot has to start naming *some* profile, and
            // before any rows are loaded the only honest answer is the one currently selected.
            // Every later destination arrives on ActiveProfileChanged; no write path repeats
            // this lookup.
            var profileService = ProfileService.Instance;
            _snapshot = ProgressSnapshot.Empty(
                ProfileService.GetProfileId(profileService.ActiveProfile), profileService.TransitionRevision);
            profileService.ActiveProfileChanged += OnActiveProfileChanged;
        }

        private ProgressSnapshot _snapshot;

        /// <summary>
        /// The highest reload revision requested so far. A load that finishes after a newer one
        /// was requested discards its result instead of publishing rows the user has already
        /// navigated away from.
        /// </summary>
        private long _latestRevision;

        /// <summary>
        /// Test seam: the live snapshot. Production code publishes only through
        /// <see cref="Mutate{T}"/> or <see cref="ReloadForProfileAsync"/>; tests seed and read
        /// it directly because <c>GetUninitializedObject</c> skips the constructor.
        /// </summary>
        internal ProgressSnapshot Snapshot
        {
            get => Volatile.Read(ref _snapshot);
            set => Volatile.Write(ref _snapshot, value);
        }

        private Dictionary<string, TarkovTask> _tasksByNormalizedName = new();
        private Dictionary<string, TarkovTask> _tasksByBsgId = new();
        private Dictionary<string, TarkovTask> _tasksById = new();
        private List<TarkovTask> _allTasks = new();

        // V2 진행 데이터 (이중 키 저장)
        private QuestProgressDataV2 _progressDataV2 = new();

        /// <summary>
        /// 데이터 소스 (JSON 또는 DB)
        /// </summary>
        public bool IsLoadedFromDb { get; private set; }

        public event EventHandler? ProgressChanged;
        public event EventHandler<ObjectiveProgressChangedEventArgs>? ObjectiveProgressChanged;

        private void OnActiveProfileChanged(object? sender, ProfileChangedEventArgs e)
        {
            // A provenance-only re-confirmation (EFT re-logs the session mode on every profile
            // screen visit) names the profile the snapshot already holds. Reloading it would
            // re-read identical rows and could republish a view taken before an edit made while
            // the read was in flight, so only a real destination change reloads.
            if (!e.ProfileChanged) return;

            _ = ReloadForProfileAsync(e.Profile, e.Revision);
        }

        /// <summary>
        /// Derives the next snapshot from the current one and publishes it atomically, returning
        /// the published snapshot together with whatever the derivation produced (typically the
        /// list of rows to persist).
        /// <para>
        /// <paramref name="update"/> must be pure: it is re-run when another writer publishes
        /// first. Returning the same instance means "no change" and skips the swap.
        /// </para>
        /// <para>
        /// A plain assignment would be enough today, because mutation paths are in practice
        /// confined to the dispatcher. Nothing enforces that confinement, and a lost update from
        /// a second writer would be silent, so the retry loop makes the guarantee independent of
        /// a property no test checks. It will almost never spin.
        /// </para>
        /// </summary>
        private (ProgressSnapshot Published, T Payload) Mutate<T>(
            Func<ProgressSnapshot, (ProgressSnapshot Next, T Payload)> update)
        {
            while (true)
            {
                var current = Volatile.Read(ref _snapshot);
                var (next, payload) = update(current);

                if (ReferenceEquals(next, current)) return (current, payload);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, next, current), current))
                    return (next, payload);
            }
        }

        /// <summary>
        /// DB에서 퀘스트 데이터를 로드하고 초기화합니다.
        /// </summary>
        public async Task<bool> InitializeFromDbAsync()
        {
            var dbService = QuestDbService.Instance;

            if (!await dbService.LoadQuestsAsync())
            {
                System.Diagnostics.Debug.WriteLine("[QuestProgressService] Failed to load quests from DB, falling back to JSON");
                return false;
            }

            var tasks = dbService.AllQuests.ToList();
            Initialize(tasks);
            IsLoadedFromDb = true;

            // V2 형식으로 진행 데이터 reconcile
            ReconcileProgressWithDb();

            System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Initialized from DB with {tasks.Count} quests");
            return true;
        }

        /// <summary>
        /// DB 업데이트 후 진행 데이터를 reconcile합니다.
        /// ID 또는 NormalizedName으로 매핑하고 누락된 키를 채웁니다.
        /// </summary>
        private void ReconcileProgressWithDb()
        {
            var changed = false;

            // 완료 퀘스트 reconcile
            foreach (var entry in _progressDataV2.CompletedQuests)
            {
                TarkovTask? matched = null;

                // 1차: ID로 매핑
                if (!string.IsNullOrEmpty(entry.Id) && _tasksById.TryGetValue(entry.Id, out matched))
                {
                    // ID 매핑 성공 - NormalizedName 업데이트
                    if (matched.NormalizedName != null && entry.NormalizedName != matched.NormalizedName)
                    {
                        entry.NormalizedName = matched.NormalizedName;
                        changed = true;
                    }
                }
                // 2차: NormalizedName으로 폴백
                else if (!string.IsNullOrEmpty(entry.NormalizedName) &&
                         _tasksByNormalizedName.TryGetValue(entry.NormalizedName, out matched))
                {
                    // NormalizedName 매핑 성공 - ID 업데이트
                    var matchedId = matched.Ids?.FirstOrDefault();
                    if (!string.IsNullOrEmpty(matchedId) && entry.Id != matchedId)
                    {
                        entry.Id = matchedId;
                        changed = true;
                    }
                }
                else
                {
                    // 매핑 실패 - 고아 레코드로 유지 (삭제하지 않음)
                    System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Orphan completed entry: {entry.Id ?? entry.NormalizedName}");
                }
            }

            // 실패 퀘스트 reconcile
            foreach (var entry in _progressDataV2.FailedQuests)
            {
                TarkovTask? matched = null;

                if (!string.IsNullOrEmpty(entry.Id) && _tasksById.TryGetValue(entry.Id, out matched))
                {
                    if (matched.NormalizedName != null && entry.NormalizedName != matched.NormalizedName)
                    {
                        entry.NormalizedName = matched.NormalizedName;
                        changed = true;
                    }
                }
                else if (!string.IsNullOrEmpty(entry.NormalizedName) &&
                         _tasksByNormalizedName.TryGetValue(entry.NormalizedName, out matched))
                {
                    var matchedId = matched.Ids?.FirstOrDefault();
                    if (!string.IsNullOrEmpty(matchedId) && entry.Id != matchedId)
                    {
                        entry.Id = matchedId;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                SaveProgress();
            }
        }

        /// <summary>
        /// Initialize service with task data
        /// </summary>
        public void Initialize(List<TarkovTask> tasks)
        {
            _allTasks = tasks;

            // Build dictionaries, handling duplicates by keeping the first occurrence
            _tasksByNormalizedName = new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase);
            _tasksByBsgId = new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase);
            _tasksById = new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase);

            foreach (var task in tasks.Where(t => !string.IsNullOrEmpty(t.NormalizedName)))
            {
                if (!_tasksByNormalizedName.ContainsKey(task.NormalizedName!))
                {
                    _tasksByNormalizedName[task.NormalizedName!] = task;
                }

                // Build Id/BsgId lookup (task.Ids contains IDs)
                if (task.Ids != null)
                {
                    foreach (var id in task.Ids)
                    {
                        if (!string.IsNullOrEmpty(id))
                        {
                            if (!_tasksByBsgId.ContainsKey(id))
                            {
                                _tasksByBsgId[id] = task;
                            }
                            if (!_tasksById.ContainsKey(id))
                            {
                                _tasksById[id] = task;
                            }
                        }
                    }
                }
            }

            // One call loads both quest and objective rows: they are two halves of one snapshot
            // and must be published together (they used to be two independent loads that each
            // resolved the profile again, so the two caches could end up from different profiles).
            LoadProgress();
        }

        /// <summary>
        /// Get all tasks
        /// </summary>
        public IReadOnlyList<TarkovTask> AllTasks => _allTasks;

        /// <summary>
        /// Get task by normalized name (deprecated, use GetTaskById instead)
        /// </summary>
        public TarkovTask? GetTask(string normalizedName)
        {
            return _tasksByNormalizedName.TryGetValue(normalizedName, out var task) ? task : null;
        }

        /// <summary>
        /// Get task by database ID (primary lookup method)
        /// </summary>
        public TarkovTask? GetTaskById(string id)
        {
            return _tasksById.TryGetValue(id, out var task) ? task : null;
        }

        /// <summary>
        /// Get task by BSG ID (used for tarkov-market marker matching)
        /// </summary>
        public TarkovTask? GetTaskByBsgId(string bsgId)
        {
            return _tasksByBsgId.TryGetValue(bsgId, out var task) ? task : null;
        }

        /// <summary>
        /// Check if a task has alternative quests (mutually exclusive choices)
        /// These quests should not be auto-completed as user must choose one
        /// </summary>
        public bool HasAlternativeQuests(TarkovTask task) => task.HasAlternatives;

        /// <summary>
        /// Get all alternative quest groups (for sync selection UI)
        /// Returns groups of mutually exclusive quests that need user selection
        /// </summary>
        public List<List<TarkovTask>> GetAlternativeQuestGroups()
        {
            var groups = new List<List<TarkovTask>>();
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var task in _allTasks)
            {
                if (task.NormalizedName == null) continue;
                if (processed.Contains(task.NormalizedName)) continue;
                if (!HasAlternativeQuests(task)) continue;

                // Build a group of mutually exclusive quests
                var group = new List<TarkovTask> { task };
                processed.Add(task.NormalizedName);

                foreach (var altName in task.AlternativeQuests!)
                {
                    if (processed.Contains(altName)) continue;

                    var altTask = GetTask(altName) ?? GetTaskById(altName);
                    if (altTask != null)
                    {
                        group.Add(altTask);
                        if (altTask.NormalizedName != null)
                            processed.Add(altTask.NormalizedName);
                    }
                }

                if (group.Count > 1)
                {
                    groups.Add(group);
                }
            }

            return groups;
        }

        // Thread-local visited set for GetStatus to prevent circular reference during status check
        [ThreadStatic]
        private static HashSet<string>? _getStatusVisited;

        /// <summary>
        /// Get quest status for a task
        /// </summary>
        public QuestStatus GetStatus(TarkovTask task) => GetStatus(task, Snapshot);

        /// <summary>
        /// Status against an explicitly captured snapshot. Every recursive step of the
        /// prerequisite walk carries the same one, so a profile switch landing mid-walk cannot
        /// produce a status derived half from one profile's rows and half from another's.
        /// </summary>
        private QuestStatus GetStatus(TarkovTask task, ProgressSnapshot snapshot)
        {
            var taskId = task.Ids?.FirstOrDefault();
            var taskKey = taskId ?? task.NormalizedName;

            if (string.IsNullOrEmpty(taskKey)) return QuestStatus.Active;

            // Check if manually set to Done or Failed
            // Try by Id first, then by NormalizedName for backwards compatibility
            if (!string.IsNullOrEmpty(taskId) && snapshot.Quests.TryGetValue(taskId, out var statusById))
            {
                if (statusById == QuestStatus.Done || statusById == QuestStatus.Failed)
                    return statusById;
            }
            else if (!string.IsNullOrEmpty(task.NormalizedName) && snapshot.Quests.TryGetValue(task.NormalizedName, out var statusByName))
            {
                if (statusByName == QuestStatus.Done || statusByName == QuestStatus.Failed)
                    return statusByName;
            }

            // Circular reference protection for prerequisite checking
            bool isTopLevel = _getStatusVisited == null;
            _getStatusVisited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // If already checking this task (circular reference), treat as Active to break the cycle
            if (!_getStatusVisited.Add(taskKey))
            {
                return QuestStatus.Active;
            }

            try
            {
                // Check edition requirements first (Unavailable takes precedence)
                if (!IsEditionRequirementMet(task))
                    return QuestStatus.Unavailable;

                // Check prestige level requirement (also Unavailable)
                if (!IsPrestigeLevelRequirementMet(task))
                    return QuestStatus.Unavailable;

                // Check faction requirement (Unavailable if player chose different faction)
                if (!IsFactionRequirementMet(task))
                    return QuestStatus.Unavailable;

                // Check DSP Decode Count requirement (Locked, not Unavailable)
                if (!IsDspRequirementMet(task))
                    return QuestStatus.Locked;

                // Check prerequisites
                if (!ArePrerequisitesMet(task, snapshot))
                    return QuestStatus.Locked;

                // Check level requirement
                if (!IsLevelRequirementMet(task))
                    return QuestStatus.LevelLocked;

                // Check Scav Karma requirement
                if (!IsScavKarmaRequirementMet(task))
                    return QuestStatus.LevelLocked;  // Use LevelLocked status for karma-locked quests too

                return QuestStatus.Active;
            }
            finally
            {
                _getStatusVisited.Remove(taskKey);
                if (isTopLevel)
                {
                    _getStatusVisited = null;
                }
            }
        }

        /// <summary>
        /// Check if player level meets quest requirement
        /// </summary>
        public bool IsLevelRequirementMet(TarkovTask task)
        {
            // If no level requirement, always met
            if (!task.RequiredLevel.HasValue || task.RequiredLevel.Value <= 0)
                return true;

            var playerLevel = SettingsService.Instance.PlayerLevel;
            return playerLevel >= task.RequiredLevel.Value;
        }

        /// <summary>
        /// Check if Scav Karma (Fence reputation) meets quest requirement
        /// </summary>
        public bool IsScavKarmaRequirementMet(TarkovTask task)
        {
            // If no karma requirement, always met
            if (!task.RequiredScavKarma.HasValue)
                return true;

            var playerScavRep = SettingsService.Instance.ScavRep;
            var requiredKarma = task.RequiredScavKarma.Value;

            // Negative requirement means player karma must be <= that value (bad karma quests)
            // Positive requirement means player karma must be >= that value (good karma quests)
            if (requiredKarma < 0)
            {
                return playerScavRep <= requiredKarma;
            }
            else
            {
                return playerScavRep >= requiredKarma;
            }
        }

        /// <summary>
        /// Check if edition requirements are met for the quest
        /// Returns false if quest is unavailable due to edition restrictions
        /// </summary>
        public bool IsEditionRequirementMet(TarkovTask task)
        {
            var settings = SettingsService.Instance;

            // Check required edition (EOD and Unheard are independent)
            if (!string.IsNullOrEmpty(task.RequiredEdition))
            {
                var requiredEdition = task.RequiredEdition.ToLowerInvariant();

                // EOD edition requirement - only EOD checkbox matters
                if (requiredEdition == "eod" || requiredEdition == "edge_of_darkness")
                {
                    if (!settings.HasEodEdition)
                        return false;
                }
                // Unheard edition requirement - only Unheard checkbox matters
                else if (requiredEdition == "unheard" || requiredEdition == "the_unheard")
                {
                    if (!settings.HasUnheardEdition)
                        return false;
                }
            }

            // Check excluded edition
            if (!string.IsNullOrEmpty(task.ExcludedEdition))
            {
                var excludedEdition = task.ExcludedEdition.ToLowerInvariant();

                // Excluded from EOD edition
                if (excludedEdition == "eod" || excludedEdition == "edge_of_darkness")
                {
                    if (settings.HasEodEdition)
                        return false;
                }
                // Excluded from Unheard edition
                else if (excludedEdition == "unheard" || excludedEdition == "the_unheard")
                {
                    if (settings.HasUnheardEdition)
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Check if prestige level requirement is met for the quest
        /// </summary>
        public bool IsPrestigeLevelRequirementMet(TarkovTask task)
        {
            // If no prestige level requirement, always met
            if (!task.RequiredPrestigeLevel.HasValue || task.RequiredPrestigeLevel.Value <= 0)
                return true;

            var playerPrestige = SettingsService.Instance.PrestigeLevel;
            return playerPrestige >= task.RequiredPrestigeLevel.Value;
        }

        /// <summary>
        /// Check if faction requirement is met for the quest
        /// Returns false if player chose a faction and quest is for the opposite faction
        /// </summary>
        public bool IsFactionRequirementMet(TarkovTask task)
        {
            // If quest has no faction requirement, always available
            if (string.IsNullOrEmpty(task.Faction))
                return true;

            // Use SettingsService's existing faction check logic
            return SettingsService.Instance.ShouldIncludeTask(task.Faction);
        }

        /// <summary>
        /// Check if DSP Decode Count requirement is met for the quest.
        /// Uses the RequiredDecodeCount field from the database.
        /// </summary>
        public bool IsDspRequirementMet(TarkovTask task)
        {
            // If no decode count requirement, always met
            if (!task.RequiredDecodeCount.HasValue)
                return true;

            var dspCount = SettingsService.Instance.DspDecodeCount;

            // RequiredDecodeCount specifies the exact DSP decode count needed
            return dspCount == task.RequiredDecodeCount.Value;
        }

        /// <summary>
        /// Check if a quest is completed by its normalized name
        /// Used for Collector quest progress calculation
        /// </summary>
        public bool IsQuestCompleted(string normalizedName)
        {
            var task = GetTask(normalizedName);
            if (task == null) return false;

            return GetStatus(task) == QuestStatus.Done;
        }

        /// <summary>
        /// Check if all prerequisites are met based on taskRequirements or legacy Previous field.
        /// Supports OR groups: GroupId = 0 means AND condition, GroupId > 0 means OR condition within the same group.
        /// </summary>
        public bool ArePrerequisitesMet(TarkovTask task) => ArePrerequisitesMet(task, Snapshot);

        /// <summary>Prerequisite check against an explicitly captured snapshot (see <see cref="GetStatus(TarkovTask, ProgressSnapshot)"/>).</summary>
        private bool ArePrerequisitesMet(TarkovTask task, ProgressSnapshot snapshot)
        {
            // Use taskRequirements if available (more accurate status conditions with OR group support)
            if (task.TaskRequirements != null && task.TaskRequirements.Count > 0)
            {
                // Group requirements by GroupId
                var andRequirements = task.TaskRequirements.Where(r => r.GroupId == 0).ToList();
                var orGroups = task.TaskRequirements
                    .Where(r => r.GroupId > 0)
                    .GroupBy(r => r.GroupId)
                    .ToList();


                // Check AND requirements (GroupId = 0): ALL must be satisfied
                foreach (var req in andRequirements)
                {
                    var reqTask = ResolveRequirementTask(req);

                    if (reqTask == null)
                        continue;

                    var reqStatus = GetStatus(reqTask, snapshot);
                    var satisfied = IsStatusSatisfied(reqStatus, req.Status);
                    if (!satisfied)
                        return false;
                }

                // Check OR groups (GroupId > 0): ANY ONE in each group must be satisfied
                foreach (var group in orGroups)
                {
                    bool anyInGroupSatisfied = false;

                    foreach (var req in group)
                    {
                        var reqTask = ResolveRequirementTask(req);

                        if (reqTask == null)
                            continue;

                        var reqStatus = GetStatus(reqTask, snapshot);
                        var satisfied = IsStatusSatisfied(reqStatus, req.Status);
                        if (satisfied)
                        {
                            anyInGroupSatisfied = true;
                            break;
                        }
                    }

                    // If no requirement in this OR group is satisfied, prerequisites are not met
                    if (!anyInGroupSatisfied)
                        return false;
                }

                return true;
            }

            // Fallback to legacy Previous field (assumes 'complete' required)
            if (task.Previous == null || task.Previous.Count == 0)
                return true;

            foreach (var prevName in task.Previous)
            {
                var prevTask = GetTask(prevName);
                if (prevTask == null) continue;

                var prevStatus = GetStatus(prevTask, snapshot);
                if (prevStatus != QuestStatus.Done)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Check if the current quest status satisfies the required status conditions.
        /// Handles both tarkov.dev API values ("active", "complete", "failed") and
        /// DB/Wiki values ("Start", "Accept", "Complete", "Fail").
        /// </summary>
        private bool IsStatusSatisfied(QuestStatus currentStatus, List<string>? requiredStatuses)
        {
            if (requiredStatuses == null || requiredStatuses.Count == 0)
            {
                // Default: require 'complete'
                return currentStatus == QuestStatus.Done;
            }

            // Check each required status
            foreach (var required in requiredStatuses)
            {
                switch (required.ToLowerInvariant())
                {
                    case "active":
                    case "start":    // DB value: RequirementType = "Start"
                    case "accept":   // DB value: RequirementType = "Accept"
                        // Quest is active (started but not completed)
                        if (currentStatus == QuestStatus.Active)
                            return true;
                        // Also satisfied if quest is done (was active before completion)
                        if (currentStatus == QuestStatus.Done)
                            return true;
                        break;

                    case "complete":
                        if (currentStatus == QuestStatus.Done)
                            return true;
                        break;

                    case "failed":
                    case "fail":     // DB value: RequirementType = "Fail"
                        if (currentStatus == QuestStatus.Failed)
                            return true;
                        break;
                }
            }

            return false;
        }

        /// <summary>
        /// Mark quest as completed, optionally completing prerequisites
        /// Also automatically fails alternative quests (mutually exclusive quests)
        /// </summary>
        public void CompleteQuest(TarkovTask task, bool completePrerequisites = true)
        {
            var taskId = task.Ids?.FirstOrDefault();
            System.Diagnostics.Debug.WriteLine($"[QuestProgressService] CompleteQuest: {taskId} ({task.Name}), prerequisites: {completePrerequisites}");

            // Compute the full plan first, then apply it. The decision logic lives in
            // ComputeCompletionCascade, shared with GetCompletionCascade so the
            // confirmation preview cannot drift from what actually happens here.
            //
            // Plan and apply run inside one Mutate so the plan is derived from the very
            // snapshot it is published onto, and so the profile it persists under is that
            // snapshot's -- not whichever profile happens to be selected by the time the
            // fire-and-forget save runs (PRD R5).
            ApplyToSnapshot(snapshot =>
                ComputeCompletionCascade(task, completePrerequisites, LookupsFor(snapshot)));
        }

        /// <summary>
        /// Applies a cascade the user already confirmed in QuestCompleteConfirmDialog
        /// (or an empty-cascade preview from the one-click path). The plan captured by
        /// <see cref="GetCompletionCascade"/> is written verbatim — never recomputed —
        /// so the quests the dialog listed are exactly the quests whose progress
        /// changes, even when background log-sync events altered other state while
        /// the modal was open.
        /// </summary>
        public void ApplyCompletionCascade(QuestCompletionCascade cascade)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[QuestProgressService] ApplyCompletionCascade: {cascade.Plan.CompletionsInOrder.Count} completions, {cascade.Plan.AlternativesToFail.Count} failures");

            // The plan the dialog listed is written verbatim, never recomputed, so the quests
            // the user confirmed are exactly the quests whose progress changes.
            ApplyToSnapshot(_ => cascade.Plan);
        }

        /// <summary>
        /// The rows a plan writes: each planned quest Done, each planned alternative Failed —
        /// the same write order and keys the pre-refactor interleaved code produced.
        /// </summary>
        private static List<(string Id, string? NormalizedName, QuestStatus Status)> RowsOf(QuestCompletionPlan plan)
        {
            var changedQuests = new List<(string Id, string? NormalizedName, QuestStatus Status)>();

            foreach (var completion in plan.CompletionsInOrder)
            {
                changedQuests.Add((completion.Key, completion.Quest.NormalizedName, QuestStatus.Done));
            }

            // Fail alternative quests (mutually exclusive)
            foreach (var failure in plan.AlternativesToFail)
            {
                changedQuests.Add((failure.Key, failure.Quest.NormalizedName, QuestStatus.Failed));
                System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Auto-failed alternative quest: {failure.Key} ({failure.Quest.Name})");
            }

            return changedQuests;
        }

        /// <summary>
        /// Derives a completion plan from the live snapshot, publishes its rows onto that same
        /// snapshot, then saves under the published snapshot's profile and raises
        /// ProgressChanged once. The single write path for every hand-entered completion.
        /// </summary>
        private void ApplyToSnapshot(Func<ProgressSnapshot, QuestCompletionPlan> computePlan)
        {
            var (published, changedQuests) = Mutate(current =>
            {
                var rows = RowsOf(computePlan(current));
                return rows.Count == 0
                    ? (current, rows)
                    : (current with { Quests = WithRows(current.Quests, rows) }, rows);
            });

            if (changedQuests.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[QuestProgressService] No changes to save");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Saving {changedQuests.Count} changed quests (batch)");
            // Fire-and-forget async save - don't block UI
            _ = SaveProgressBatchAsync(changedQuests, published.ProfileId);
            System.Diagnostics.Debug.WriteLine("[QuestProgressService] Progress save initiated");
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Applies persisted rows to a quest map, last write per key winning.</summary>
        private static ImmutableDictionary<string, QuestStatus> WithRows(
            ImmutableDictionary<string, QuestStatus> quests,
            IEnumerable<(string Id, string? NormalizedName, QuestStatus Status)> rows)
        {
            var builder = quests.ToBuilder();
            foreach (var row in rows) builder[row.Id] = row.Status;
            return builder.ToImmutable();
        }

        /// <summary>
        /// Side-effect-free preview of <see cref="CompleteQuest"/> (with
        /// completePrerequisites: true, as both quest-list buttons call it): which
        /// incomplete prerequisites would be auto-completed and which mutually
        /// exclusive alternatives auto-failed. Runs the same traversal core
        /// CompleteQuest applies, and carries the resulting plan so
        /// <see cref="ApplyCompletionCascade"/> can write it verbatim on confirm.
        /// Used by QuestListPage to decide whether to show QuestCompleteConfirmDialog.
        /// </summary>
        public QuestCompletionCascade GetCompletionCascade(TarkovTask task)
        {
            var plan = ComputeCompletionCascade(task, completePrerequisites: true, LookupsFor(Snapshot));

            return new QuestCompletionCascade(plan);
        }

        /// <summary>Raw recorded progress for a key (quest Id or NormalizedName); null when unrecorded.</summary>
        private static QuestStatus? RecordedStatus(ProgressSnapshot snapshot, string key)
            => snapshot.Quests.TryGetValue(key, out var status) ? status : null;

        /// <summary>
        /// True when progress already records this quest Done under either key it may be
        /// stored under — its progress key or its NormalizedName (legacy migration rows).
        /// The snapshot twin of the cascade core's IsDoneRecordedOrPlanned, kept in one
        /// place so the batch paths cannot drift from the traversal's done-check.
        /// </summary>
        private static bool IsRecordedDone(ProgressSnapshot snapshot, TarkovTask task)
            => RecordedDoneUnder(snapshot, ProgressKeyOf(task)) || RecordedDoneUnder(snapshot, task.NormalizedName);

        private static bool RecordedDoneUnder(ProgressSnapshot snapshot, string? key)
            => !string.IsNullOrEmpty(key) && RecordedStatus(snapshot, key) == QuestStatus.Done;

        /// <summary>
        /// This service's lookups for the cascade core, bound to one captured snapshot — the
        /// single construction site.
        /// <para>
        /// <see cref="CascadeLookups.Status"/> is deliberately the recorded-only view rather
        /// than the derived <see cref="GetStatus(TarkovTask)"/>. The two are interchangeable
        /// here: the core consults Status only through gates that test <c>== Done</c> and
        /// <c>== Failed</c>, and GetStatus reports either of those exactly when progress records
        /// it. Using the recorded view keeps the cascade free of SettingsService (player level,
        /// faction, editions), which is profile-scoped state the core would otherwise read from
        /// the selected profile while planning a change for a different one.
        /// </para>
        /// </summary>
        private CascadeLookups LookupsFor(ProgressSnapshot snapshot) => new()
        {
            TaskById = GetTaskById,
            TaskByName = GetTask,
            Status = task => RecordedStatusOf(snapshot.Quests, task) ?? QuestStatus.Active,
            RecordedStatus = key => RecordedStatus(snapshot, key),
        };

        /// <summary>
        /// A quest's recorded terminal status under the key policy
        /// <see cref="GetStatus(TarkovTask, ProgressSnapshot)"/> uses — Id first, NormalizedName
        /// only when the Id lookup misses — or null when nothing terminal is recorded (a
        /// recorded non-terminal value reports null too, matching GetStatus, which ignores one
        /// and falls through to derivation).
        /// <para>
        /// Takes a raw map rather than a snapshot so the log-sync path can ask the same question
        /// of a profile whose rows were loaded straight from the store and never became a
        /// snapshot. One home for the key policy is what keeps the two from drifting.
        /// </para>
        /// </summary>
        internal static QuestStatus? RecordedStatusOf(
            IReadOnlyDictionary<string, QuestStatus> quests, TarkovTask task)
        {
            var taskId = task.Ids?.FirstOrDefault();

            QuestStatus? recorded = null;
            if (!string.IsNullOrEmpty(taskId) && quests.TryGetValue(taskId, out var byId))
            {
                recorded = byId;
            }
            else if (!string.IsNullOrEmpty(task.NormalizedName) &&
                     quests.TryGetValue(task.NormalizedName, out var byName))
            {
                recorded = byName;
            }

            return recorded is QuestStatus.Done or QuestStatus.Failed ? recorded : null;
        }

        /// <summary>
        /// A quest's status as the log-sync planner sees it for a given profile's stored rows:
        /// the recorded terminal state, or Active when nothing is recorded. Equivalent to
        /// <see cref="GetStatus(TarkovTask)"/> for the only comparisons sync makes
        /// (<c>== Done</c>, <c>== Failed</c>), and unlike GetStatus it can answer for a profile
        /// that is not the loaded one.
        /// </summary>
        internal static QuestStatus StoredStatusOf(
            IReadOnlyDictionary<string, QuestStatus> quests, TarkovTask task)
            => RecordedStatusOf(quests, task) ?? QuestStatus.Active;

        /// <summary>
        /// A requirement whose Status list names "complete" — or names nothing, the
        /// legacy default (see <see cref="IsStatusSatisfied"/>) — is one the game
        /// satisfies by completing the prerequisite. Fail- and Accept/Start-type
        /// requirements are satisfied by other player actions, so the completion
        /// cascade must never auto-complete their targets.
        /// </summary>
        private static bool RequiresCompletion(List<string>? requiredStatuses)
            => requiredStatuses == null || requiredStatuses.Count == 0
               || requiredStatuses.Any(s => string.Equals(s, "complete", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The progress key a quest is recorded and written under: first non-empty
        /// Ids entry, else NormalizedName; null/empty when the task carries neither.
        /// (An empty-string Id — a data anomaly — must fall through to the name, not
        /// become the literal key "".)
        /// </summary>
        private static string? ProgressKeyOf(TarkovTask quest)
        {
            var id = quest.Ids?.FirstOrDefault(i => !string.IsNullOrEmpty(i));
            return !string.IsNullOrEmpty(id) ? id : quest.NormalizedName;
        }

        /// <summary>
        /// The quest a requirement row points at: by TaskId when it carries one, else by
        /// the legacy TaskNormalizedName. The single home for the Id-first-else-name
        /// convention every requirement reader shares (ArePrerequisitesMet, the cascade
        /// core, QuestListPage's prerequisite rows); null when the row resolves to no known
        /// quest — each reader applies its own null policy, which is why this helper stops
        /// at resolution and does not judge satisfaction.
        /// </summary>
        internal static TarkovTask? ResolveRequirementTask(
            TaskRequirement req, Func<string, TarkovTask?> taskById, Func<string, TarkovTask?> taskByName)
            => !string.IsNullOrEmpty(req.TaskId) ? taskById(req.TaskId) : taskByName(req.TaskNormalizedName);

        /// <summary>Instance overload for the status engine and the quest-detail UI.</summary>
        public TarkovTask? ResolveRequirementTask(TaskRequirement req)
            => ResolveRequirementTask(req, GetTaskById, GetTask);

        /// <summary>
        /// The mutually exclusive alternatives a completion of <paramref name="task"/> would
        /// mark Failed: each listed alternative that resolves (by NormalizedName, Id fallback)
        /// and is neither Done nor already Failed, paired with the key its status is recorded
        /// under — first non-empty Id, else the raw listed name (the legacy fallback key).
        /// Shared by ComputeCompletionCascade and ApplyQuestChangesBatchAsync so the two
        /// auto-fail paths cannot drift; the caller supplies the status view it needs (the
        /// core's planned-aware EffectiveStatus, the batch's already-mutated GetStatus).
        /// </summary>
        internal static List<PlannedQuestChange> PlanAlternativeFailures(
            TarkovTask task, Func<string, TarkovTask?> taskById, Func<string, TarkovTask?> taskByName,
            Func<TarkovTask, QuestStatus> status)
        {
            var failures = new List<PlannedQuestChange>();
            if (task.AlternativeQuests == null) return failures;

            foreach (var altQuestName in task.AlternativeQuests)
            {
                // Try to find by NormalizedName (current data format) or by Id
                var altTask = taskByName(altQuestName) ?? taskById(altQuestName);
                if (altTask == null) continue;

                var altStatus = status(altTask);
                // Only fail if not already done or failed
                if (altStatus == QuestStatus.Done || altStatus == QuestStatus.Failed) continue;

                var altId = altTask.Ids?.FirstOrDefault(i => !string.IsNullOrEmpty(i));
                failures.Add(new PlannedQuestChange(altTask, !string.IsNullOrEmpty(altId) ? altId! : altQuestName));
            }
            return failures;
        }

        /// <summary>
        /// The read-only lookups <see cref="ComputeCompletionCascade"/> runs against.
        /// A named payload rather than four positional delegates: TaskById and TaskByName
        /// have identical types, so a swapped pair would compile and silently resolve
        /// nothing — every prerequisite would vanish from the plan with no error. Required
        /// members make each call site name what it passes.
        /// </summary>
        internal sealed class CascadeLookups
        {
            /// <summary>Quest by database Id — <see cref="GetTaskById"/>.</summary>
            public required Func<string, TarkovTask?> TaskById { get; init; }
            /// <summary>Quest by NormalizedName — <see cref="GetTask"/>.</summary>
            public required Func<string, TarkovTask?> TaskByName { get; init; }
            /// <summary>Derived quest status — <see cref="GetStatus"/>.</summary>
            public required Func<TarkovTask, QuestStatus> Status { get; init; }
            /// <summary>Raw recorded status for a progress key; null when unrecorded.</summary>
            public required Func<string, QuestStatus?> RecordedStatus { get; init; }
        }

        /// <summary>
        /// Pure decision core shared by <see cref="CompleteQuest"/> and
        /// <see cref="GetCompletionCascade"/>: computes the ordered plan of quests
        /// completing <paramref name="task"/> would newly mark Done (post-order —
        /// prerequisites before dependents, the clicked quest last when it is planned
        /// at all) and the alternatives it would mark Failed, mutating nothing.
        ///
        /// Traversal rules: a visited set keyed by first-Id-else-NormalizedName
        /// prevents cycles; a quest already recorded Done under its key or its
        /// NormalizedName is skipped; prerequisites come from TaskRequirements (by
        /// Id, name fallback per entry) or, only when TaskRequirements is null, the
        /// legacy Previous name list; a prerequisite that itself has
        /// AlternativeQuests is skipped entirely, subtree included (the user must
        /// choose which alternative to complete); alternatives already Done or
        /// Failed are left alone. The pre-refactor code wrote Done into
        /// _questProgress mid-traversal and later gates read those writes — the
        /// plannedDone set reproduces that, so e.g. an alternative the cascade
        /// itself completes is not also failed.
        ///
        /// Requirement semantics mirror <see cref="ArePrerequisitesMet"/>'s reading
        /// of the same rows (the pre-refactor traversal ignored both fields and
        /// over-completed): a requirement satisfied by something other than
        /// completing its target — Fail- or Accept/Start-type (<see
        /// cref="RequiresCompletion"/>) — is never auto-completed, and a multi-member
        /// OR group (GroupId &gt; 0, any one member satisfies) is never auto-completed
        /// either, because completing every branch of an either-or records quests the
        /// player didn't do; both follow the alternative-prerequisite precedent (the
        /// user must choose). A single-member "group" behaves as a plain requirement.
        ///
        /// Internal and lookup-driven so unit tests can drive it from plain
        /// dictionaries: <see cref="CascadeLookups.Status"/> is the derived
        /// <see cref="GetStatus"/>; <see cref="CascadeLookups.RecordedStatus"/> is the
        /// raw _questProgress lookup, needed because the entry check reads the
        /// NormalizedName key even for tasks that have an Id, which GetStatus does not.
        /// </summary>
        internal static QuestCompletionPlan ComputeCompletionCascade(
            TarkovTask task,
            bool completePrerequisites,
            CascadeLookups lookups)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var plannedDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completions = new List<PlannedQuestChange>();

            bool IsDoneRecordedOrPlanned(string? key)
                => !string.IsNullOrEmpty(key)
                   && (plannedDone.Contains(key) || lookups.RecordedStatus(key) == QuestStatus.Done);

            // Status as the old interleaved traversal would have observed it: planned
            // completions count as Done ahead of the real (unmutated) state.
            QuestStatus EffectiveStatus(TarkovTask quest)
            {
                var key = ProgressKeyOf(quest);
                if (!string.IsNullOrEmpty(key) && plannedDone.Contains(key))
                    return QuestStatus.Done;
                return lookups.Status(quest);
            }

            // The prerequisites this completion may auto-complete, resolved from
            // TaskRequirements or — only when TaskRequirements is null — the legacy
            // Previous name list (which always means "must be completed").
            // This walk stays separate from ArePrerequisitesMet's and QuestListPage's:
            // their questions differ (satisfaction vs auto-completability vs display;
            // ArePrerequisitesMet's unresolvable-row policy also differs by GroupId).
            IEnumerable<TarkovTask> AutoCompletablePrerequisites(TarkovTask node)
            {
                if (node.TaskRequirements != null)
                {
                    // Member counts per OR group: multi-member groups are excluded below.
                    var groupSizes = node.TaskRequirements
                        .Where(r => r.GroupId > 0)
                        .GroupBy(r => r.GroupId)
                        .ToDictionary(g => g.Key, g => g.Count());

                    foreach (var req in node.TaskRequirements)
                    {
                        // Any one member of a multi-member OR group satisfies it — the
                        // user must choose which; completing all of them over-records.
                        if (req.GroupId > 0 && groupSizes[req.GroupId] > 1) continue;

                        // Fail-/Accept-type requirements are not satisfied by completion.
                        if (!RequiresCompletion(req.Status)) continue;

                        var prevTask = ResolveRequirementTask(req, lookups.TaskById, lookups.TaskByName);

                        if (prevTask != null) yield return prevTask;
                    }
                }
                // Fallback to Previous list
                else if (node.Previous != null)
                {
                    foreach (var prevName in node.Previous)
                    {
                        if (lookups.TaskByName(prevName) is { } prevTask) yield return prevTask;
                    }
                }
            }

            void Visit(TarkovTask node, bool followPrerequisites)
            {
                var nodeKey = ProgressKeyOf(node);

                if (string.IsNullOrEmpty(nodeKey)) return;

                // Prevent circular reference - if already visiting this quest, skip
                if (!visited.Add(nodeKey)) return;

                // Skip if already done (check by both Id and NormalizedName)
                if (IsDoneRecordedOrPlanned(nodeKey)) return;
                if (IsDoneRecordedOrPlanned(node.NormalizedName)) return;

                // Complete prerequisites first (recursive)
                if (followPrerequisites)
                {
                    foreach (var prevTask in AutoCompletablePrerequisites(node))
                    {
                        if (EffectiveStatus(prevTask) == QuestStatus.Done) continue;

                        // Skip alternative quests - user must choose which one to complete
                        if (prevTask.HasAlternatives) continue;

                        Visit(prevTask, followPrerequisites: true);
                    }
                }

                plannedDone.Add(nodeKey);
                completions.Add(new PlannedQuestChange(node, nodeKey));
            }

            Visit(task, completePrerequisites);

            // Fail alternative quests of the clicked quest (mutually exclusive)
            var alternativesToFail = PlanAlternativeFailures(
                task, lookups.TaskById, lookups.TaskByName, EffectiveStatus);

            // Post-order: when the clicked quest was planned it is necessarily the
            // last completion; when it was skipped (already Done, keyless, or cycled)
            // nothing below it was planned either, so the list is empty.
            PlannedQuestChange? clickedQuest =
                completions.Count > 0 && ReferenceEquals(completions[^1].Quest, task)
                    ? completions[^1]
                    : null;

            return new QuestCompletionPlan(completions, clickedQuest, alternativesToFail);
        }

        /// <summary>
        /// Save changed quests in batch (fire-and-forget, doesn't block UI) under
        /// <paramref name="profileId"/> — always the ProfileId of the snapshot the changes were
        /// derived from, never the selection at the moment this happens to run.
        /// </summary>
        private async Task SaveProgressBatchAsync(
            List<(string Id, string? NormalizedName, QuestStatus Status)> changedQuests, string profileId)
        {
            try
            {
                await _store.SaveQuestProgressBatchAsync(changedQuests, profileId);
                System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Batch saved {changedQuests.Count} quest changes to {profileId}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Batch save failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The rows completing <paramref name="tasks"/> would write against
        /// <paramref name="snapshot"/>: each not-already-Done quest marked Done, alternative
        /// quests skipped (the user must choose which one they completed), duplicates collapsed.
        /// Pure — the caller publishes and persists.
        /// </summary>
        internal static List<(string Id, string? NormalizedName, QuestStatus Status)> PlanBatchCompletion(
            ProgressSnapshot snapshot, IEnumerable<TarkovTask> tasks, bool skipAlternativeQuests = true)
        {
            var changedQuests = new List<(string Id, string? NormalizedName, QuestStatus Status)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var task in tasks)
            {
                var taskKey = ProgressKeyOf(task);

                if (string.IsNullOrEmpty(taskKey)) continue;
                if (!visited.Add(taskKey)) continue;

                // Skip alternative quests - user must choose which one to complete
                if (skipAlternativeQuests && task.HasAlternatives)
                {
                    System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Skipping alternative quest in batch: {task.Name}");
                    continue;
                }

                // Skip if already done (check by both Id and NormalizedName)
                if (IsRecordedDone(snapshot, task)) continue;

                changedQuests.Add((taskKey, task.NormalizedName, QuestStatus.Done));
            }

            return changedQuests;
        }

        /// <summary>
        /// Apply multiple quest changes in batch (for sync operations) to
        /// <paramref name="owner"/>'s partition.
        /// <para>
        /// The profile is a parameter, not a lookup: one sync run distributes changes across
        /// every profile whose sessions the logs cover, so there is no single "current" profile
        /// this could resolve to (PRD R1). The in-memory snapshot is refreshed only when
        /// <paramref name="owner"/> is the profile it holds; a change for any other profile
        /// reaches the database and nothing else, which is what the PRD's silent-write decision
        /// requires.
        /// </para>
        /// </summary>
        public async Task ApplyQuestChangesBatchAsync(
            IEnumerable<(TarkovTask Task, QuestStatus Status)> changes, AppProfile owner)
        {
            var ownerId = ProfileService.GetProfileId(owner);
            var materialized = changes.ToList();

            var snapshot = Snapshot;
            if (!string.Equals(ownerId, snapshot.ProfileId, StringComparison.Ordinal))
            {
                var recorded = await LoadSnapshotForAsync(ownerId);
                var offscreen = PlanQuestChanges(recorded, materialized);
                if (offscreen.Count == 0) return;

                await _store.SaveQuestProgressBatchAsync(offscreen, ownerId);
                System.Diagnostics.Debug.WriteLine(
                    $"[QuestProgressService] Batch saved {offscreen.Count} quest changes to unloaded profile {ownerId}");
                return;
            }

            var (_, changedItems) = Mutate(current =>
            {
                // Re-check the owner inside the loop body: a profile switch can publish a
                // different snapshot between the read above and the swap, and writing this
                // profile's rows onto another's cache would show progress that is not theirs.
                // Bailing out here loses the write rather than misfiling it, which the next sync
                // recovers from; misfiling it is what nothing recovers from.
                if (!string.Equals(ownerId, current.ProfileId, StringComparison.Ordinal))
                    return (current, new List<(string Id, string? NormalizedName, QuestStatus Status)>());

                var rows = PlanQuestChanges(current, materialized);
                return rows.Count == 0
                    ? (current, rows)
                    : (current with { Quests = WithRows(current.Quests, rows) }, rows);
            });

            if (changedItems.Count == 0) return;

            // Save all changes in one batch transaction. Reaching here means the swap succeeded
            // against a snapshot for ownerId, so these rows are both stored and on screen.
            await _store.SaveQuestProgressBatchAsync(changedItems, ownerId);
            System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Batch saved {changedItems.Count} quest changes to {ownerId}");

            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The rows a set of sync changes writes against <paramref name="snapshot"/>: Done for
        /// each quest not already recorded Done, plus Failed for the mutually exclusive
        /// alternatives that completion rules out; Failed for each quest not already Failed.
        /// Pure, so the same planning serves the loaded profile and an unloaded one.
        /// </summary>
        private List<(string Id, string? NormalizedName, QuestStatus Status)> PlanQuestChanges(
            ProgressSnapshot snapshot, IEnumerable<(TarkovTask Task, QuestStatus Status)> changes)
        {
            var changedItems = new List<(string Id, string? NormalizedName, QuestStatus Status)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Rows planned so far, so a later change in the same batch sees the earlier ones —
            // reproducing what the pre-snapshot code got from mutating the cache in place.
            var pending = snapshot;

            foreach (var (task, status) in changes)
            {
                var taskKey = ProgressKeyOf(task);

                if (string.IsNullOrEmpty(taskKey)) continue;

                switch (status)
                {
                    case QuestStatus.Done:
                        if (visited.Add(taskKey) && !IsRecordedDone(pending, task))
                        {
                            changedItems.Add((taskKey, task.NormalizedName, QuestStatus.Done));
                            pending = pending with { Quests = pending.Quests.SetItem(taskKey, QuestStatus.Done) };

                            // Fail alternative quests (mutually exclusive) — same planning
                            // helper as the cascade core, against the already-planned state.
                            foreach (var failure in PlanAlternativeFailures(
                                         task, GetTaskById, GetTask,
                                         alt => StoredStatusOf(pending.Quests, alt)))
                            {
                                changedItems.Add((failure.Key, failure.Quest.NormalizedName, QuestStatus.Failed));
                                pending = pending with { Quests = pending.Quests.SetItem(failure.Key, QuestStatus.Failed) };
                            }
                        }
                        break;

                    case QuestStatus.Failed:
                        if (RecordedStatus(pending, taskKey) != QuestStatus.Failed)
                        {
                            changedItems.Add((taskKey, task.NormalizedName, QuestStatus.Failed));
                            pending = pending with { Quests = pending.Quests.SetItem(taskKey, QuestStatus.Failed) };
                        }
                        break;
                }
            }

            return changedItems;
        }

        /// <summary>
        /// Applies a quest event read from the game logs to the profile the log evidence names,
        /// which is not necessarily the profile on screen (PRD R1, R4).
        /// <para>
        /// When <paramref name="owner"/> is the loaded profile the change goes through the
        /// snapshot and refreshes the UI; otherwise it is written straight to that profile's
        /// rows, leaving the snapshot untouched. Callers must not invoke this for an
        /// unattributed event: an event with no evidence is dropped, never guessed at.
        /// </para>
        /// </summary>
        public async Task ApplyLogEventAsync(TarkovTask task, QuestEventType eventType, AppProfile owner)
        {
            var ownerId = ProfileService.GetProfileId(owner);
            var snapshot = Snapshot;

            if (!string.Equals(ownerId, snapshot.ProfileId, StringComparison.Ordinal))
            {
                var recorded = await LoadSnapshotForAsync(ownerId);
                var rows = PlanLogEvent(recorded, task, eventType);
                if (rows.Count == 0) return;

                await _store.SaveQuestProgressBatchAsync(rows, ownerId);
                System.Diagnostics.Debug.WriteLine(
                    $"[QuestProgressService] Log event {eventType} for {task.Name} saved to unloaded profile {ownerId}");
                return;
            }

            var (_, changed) = Mutate(current =>
            {
                // See ApplyQuestChangesBatchAsync: a switch landing between the read above and
                // the swap means these rows belong to a profile that is no longer loaded, and
                // dropping the write beats writing it onto the new profile's cache.
                if (!string.Equals(ownerId, current.ProfileId, StringComparison.Ordinal))
                    return (current, new List<(string Id, string? NormalizedName, QuestStatus Status)>());

                var rows = PlanLogEvent(current, task, eventType);
                return rows.Count == 0
                    ? (current, rows)
                    : (current with { Quests = WithRows(current.Quests, rows) }, rows);
            });

            if (changed.Count == 0) return;

            // Not the swallowing SaveProgressBatchAsync helper: this call is awaited by a caller
            // that logs, so a failed write should be reported rather than silently lost.
            await _store.SaveQuestProgressBatchAsync(changed, ownerId);
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// The rows one log event writes against <paramref name="snapshot"/>. Completed runs the
        /// same cascade a hand completion runs; Failed records the failure; Started completes the
        /// prerequisites the quest's existence implies without touching the quest itself, which
        /// stays Active.
        /// </summary>
        private List<(string Id, string? NormalizedName, QuestStatus Status)> PlanLogEvent(
            ProgressSnapshot snapshot, TarkovTask task, QuestEventType eventType)
        {
            switch (eventType)
            {
                case QuestEventType.Completed:
                    return RowsOf(ComputeCompletionCascade(
                        task, completePrerequisites: true, LookupsFor(snapshot)));

                case QuestEventType.Failed:
                {
                    var taskKey = ProgressKeyOf(task);
                    if (string.IsNullOrEmpty(taskKey)) return new();
                    if (RecordedStatus(snapshot, taskKey) == QuestStatus.Failed) return new();
                    return new() { (taskKey!, task.NormalizedName, QuestStatus.Failed) };
                }

                case QuestEventType.Started:
                {
                    // A started quest stays Active; only what it proves — its prerequisites —
                    // is recorded. Alternatives among them are skipped: the log says the quest
                    // started, not which of two mutually exclusive predecessors was taken.
                    if (string.IsNullOrEmpty(task.NormalizedName)) return new();

                    var prerequisites = QuestGraphService.Instance.GetAllPrerequisites(task.NormalizedName);
                    return PlanBatchCompletion(snapshot, prerequisites);
                }

                default:
                    return new();
            }
        }

        /// <summary>
        /// A detached snapshot of another profile's stored rows, for planning a change against a
        /// profile that is not loaded. Its Revision is -1 and it is never published: it exists
        /// only so the planning helpers can run against one shape of input.
        /// </summary>
        private async Task<ProgressSnapshot> LoadSnapshotForAsync(string profileId)
        {
            var quests = await _store.LoadQuestProgressAsync(profileId);
            return ProgressSnapshot.From(
                profileId, revision: -1, quests, ImmutableDictionary<string, bool>.Empty);
        }

        /// <summary>
        /// Mark quest as failed
        /// </summary>
        public void FailQuest(TarkovTask task)
        {
            var taskKey = ProgressKeyOf(task);

            if (string.IsNullOrEmpty(taskKey)) return;

            var (published, _) = Mutate(current =>
                (current with { Quests = current.Quests.SetItem(taskKey!, QuestStatus.Failed) }, 0));

            // Fire-and-forget async save - don't block UI. The profile is resolved here, from
            // the snapshot this edit was published onto, rather than inside the Task.Run body:
            // a profile switch between scheduling and running would otherwise redirect the row.
            var profileId = published.ProfileId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _store.SaveQuestProgressAsync(taskKey!, task.NormalizedName, QuestStatus.Failed, profileId);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Failed to save failed quest: {ex.Message}");
                }
            });
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Reset quest to active state
        /// </summary>
        public void ResetQuest(TarkovTask task)
        {
            var taskKey = ProgressKeyOf(task);

            if (string.IsNullOrEmpty(taskKey)) return;

            // Remove by both Id and NormalizedName for clean migration
            var alsoByName = !string.IsNullOrEmpty(task.NormalizedName) && task.NormalizedName != taskKey;
            var (published, _) = Mutate(current =>
            {
                var quests = current.Quests.Remove(taskKey!);
                if (alsoByName) quests = quests.Remove(task.NormalizedName!);
                return (current with { Quests = quests }, 0);
            });

            // Fire-and-forget async delete - don't block UI. Profile captured from the snapshot
            // the removal was published onto, not read inside the Task.Run body.
            var profileId = published.ProfileId;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _store.DeleteQuestProgressAsync(taskKey!, profileId);
                    // Also delete by NormalizedName for clean migration
                    if (alsoByName)
                    {
                        await _store.DeleteQuestProgressAsync(task.NormalizedName!, profileId);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Failed to delete quest progress: {ex.Message}");
                }
            });
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Reset all quest progress
        /// </summary>
        public void ResetAllProgress()
        {
            var (published, _) = Mutate(current =>
                (current with { Quests = ProgressSnapshot.EmptyQuests, Objectives = ProgressSnapshot.EmptyObjectives }, 0));

            // DB에서 모든 퀘스트 진행 데이터 삭제. Profile captured from the cleared snapshot,
            // not read inside the Task.Run body, so a switch cannot redirect the delete.
            var profileId = published.ProfileId;
            Task.Run(async () =>
            {
                try
                {
                    await _store.ClearAllQuestProgressAsync(profileId);
                    await _store.ClearAllObjectiveProgressAsync(profileId);
                    System.Diagnostics.Debug.WriteLine($"[QuestProgressService] All progress cleared from DB for {profileId}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Reset failed: {ex.Message}");
                }
            }).GetAwaiter().GetResult();

            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Get prerequisite quest chain for a task
        /// </summary>
        public List<TarkovTask> GetPrerequisiteChain(TarkovTask task)
        {
            var chain = new List<TarkovTask>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            CollectPrerequisites(task, chain, visited);

            return chain;
        }

        private void CollectPrerequisites(TarkovTask task, List<TarkovTask> chain, HashSet<string> visited)
        {
            if (task.Previous == null) return;

            foreach (var prevName in task.Previous)
            {
                if (visited.Contains(prevName)) continue;
                visited.Add(prevName);

                var prevTask = GetTask(prevName);
                if (prevTask != null)
                {
                    CollectPrerequisites(prevTask, chain, visited);
                    chain.Add(prevTask);
                }
            }
        }

        /// <summary>
        /// Get follow-up quests for a task
        /// </summary>
        public List<TarkovTask> GetFollowUpQuests(TarkovTask task)
        {
            var followUps = new List<TarkovTask>();

            if (task.LeadsTo != null)
            {
                foreach (var nextName in task.LeadsTo)
                {
                    var nextTask = GetTask(nextName);
                    if (nextTask != null)
                    {
                        followUps.Add(nextTask);
                    }
                }
            }

            return followUps;
        }

        /// <summary>
        /// Get alternative quests (mutually exclusive) for a task
        /// </summary>
        public List<TarkovTask> GetAlternativeQuests(TarkovTask task)
        {
            var alternatives = new List<TarkovTask>();

            if (task.AlternativeQuests != null)
            {
                foreach (var altName in task.AlternativeQuests)
                {
                    var altTask = GetTask(altName);
                    if (altTask != null)
                    {
                        alternatives.Add(altTask);
                    }
                }
            }

            return alternatives;
        }

        /// <summary>
        /// Get count statistics for quest statuses
        /// </summary>
        public (int Total, int Locked, int Active, int Done, int Failed, int LevelLocked, int Unavailable) GetStatistics()
        {
            int locked = 0, active = 0, done = 0, failed = 0, levelLocked = 0, unavailable = 0;

            // One snapshot for the whole tally: per-task captures would let a profile switch
            // land mid-loop and produce counts that sum two different profiles.
            var snapshot = Snapshot;

            foreach (var task in _allTasks)
            {
                var status = GetStatus(task, snapshot);
                switch (status)
                {
                    case QuestStatus.Locked: locked++; break;
                    case QuestStatus.Active: active++; break;
                    case QuestStatus.Done: done++; break;
                    case QuestStatus.Failed: failed++; break;
                    case QuestStatus.LevelLocked: levelLocked++; break;
                    case QuestStatus.Unavailable: unavailable++; break;
                }
            }

            return (_allTasks.Count, locked, active, done, failed, levelLocked, unavailable);
        }

        #region Objective Progress

        /// <summary>
        /// Get objective completion status
        /// </summary>
        public bool IsObjectiveCompleted(string questNormalizedName, int objectiveIndex)
        {
            var key = $"{questNormalizedName}:{objectiveIndex}";
            return Snapshot.Objectives.TryGetValue(key, out var completed) && completed;
        }

        /// <summary>
        /// Get objective completion status by objective ID
        /// </summary>
        public bool IsObjectiveCompletedById(string objectiveId)
        {
            var key = $"id:{objectiveId}";
            return Snapshot.Objectives.TryGetValue(key, out var completed) && completed;
        }

        /// <summary>
        /// Set objective completion status (index 기반 - Quests 탭용)
        /// ObjectiveId도 함께 저장하여 Map Tracker와 동기화
        /// </summary>
        public void SetObjectiveCompleted(string questNormalizedName, int objectiveIndex, bool completed, string? objectiveId = null)
        {
            var indexKey = $"{questNormalizedName}:{objectiveIndex}";
            var idKey = string.IsNullOrEmpty(objectiveId) ? null : $"id:{objectiveId}";

            // One edit, two keys (index for the Quests tab, id for the Map tracker). They are
            // planned and published together and saved under one profile id, so a transition
            // mid-batch can no longer split one objective across two partitions.
            ApplyObjectiveEdit(
                completed,
                (indexKey, questNormalizedName),
                idKey == null ? null : (idKey, (string?)null));

            ObjectiveProgressChanged?.Invoke(this, new ObjectiveProgressChangedEventArgs(questNormalizedName, objectiveIndex, completed));
        }

        /// <summary>
        /// Set objective completion status by objective ID (Map Tracker용)
        /// Index 기반 키도 함께 저장하여 Quests 탭과 동기화
        /// </summary>
        public void SetObjectiveCompletedById(string objectiveId, bool completed, string? questNormalizedName = null, int objectiveIndex = -1)
        {
            var idKey = $"id:{objectiveId}";
            var hasIndexKey = !string.IsNullOrEmpty(questNormalizedName) && objectiveIndex >= 0;

            ApplyObjectiveEdit(
                completed,
                (idKey, null),
                hasIndexKey ? ($"{questNormalizedName}:{objectiveIndex}", questNormalizedName) : null);

            ObjectiveProgressChanged?.Invoke(this, new ObjectiveProgressChangedEventArgs(objectiveId, objectiveIndex, completed));
        }

        /// <summary>
        /// Publishes an objective edit onto the snapshot under both of the keys it is recorded
        /// against, then persists both under that snapshot's profile.
        /// </summary>
        private void ApplyObjectiveEdit(
            bool completed,
            (string Key, string? QuestId) primary,
            (string Key, string? QuestId)? mirror)
        {
            var keys = mirror.HasValue ? new[] { primary, mirror.Value } : new[] { primary };

            var (published, _) = Mutate(current =>
            {
                var objectives = current.Objectives;
                foreach (var (key, _) in keys)
                {
                    objectives = completed ? objectives.SetItem(key, true) : objectives.Remove(key);
                }
                return (current with { Objectives = objectives }, 0);
            });

            // Fire-and-forget async save - don't block UI
            _ = SaveObjectiveProgressBatchAsync(
                keys.Select(k => (k.Key, k.QuestId, completed)).ToList(), published.ProfileId);
        }

        /// <summary>
        /// Save objective progress in batch (fire-and-forget, doesn't block UI). The profile is
        /// a parameter resolved once by the caller, not re-read per row: the loop awaits between
        /// rows, and a transition landing mid-loop used to split one edit across two partitions
        /// permanently (objective rows are written one connection at a time, with no transaction).
        /// </summary>
        private async Task SaveObjectiveProgressBatchAsync(
            List<(string Key, string? QuestId, bool IsCompleted)> items, string profileId)
        {
            try
            {
                foreach (var item in items)
                {
                    if (item.IsCompleted)
                    {
                        await _store.SaveObjectiveProgressAsync(item.Key, item.QuestId, true, profileId);
                    }
                    else
                    {
                        await _store.DeleteObjectiveProgressAsync(item.Key, profileId);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Failed to save objective progress: {ex.Message}");
            }
        }

        /// <summary>
        /// Get all completed objective indices for a quest
        /// </summary>
        public HashSet<int> GetCompletedObjectives(string questNormalizedName)
        {
            var result = new HashSet<int>();
            var prefix = $"{questNormalizedName}:";

            foreach (var kvp in Snapshot.Objectives)
            {
                // OrdinalIgnoreCase to match the dictionary's own comparer: an ignore-case map
                // whose prefix scan is ordinal would find a key by lookup but miss it here.
                if (kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && kvp.Value)
                {
                    var indexStr = kvp.Key.Substring(prefix.Length);
                    if (int.TryParse(indexStr, out var index))
                    {
                        result.Add(index);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Get objective completion count for a quest
        /// </summary>
        public (int Completed, int Total) GetObjectiveProgress(TarkovTask task)
        {
            if (task.NormalizedName == null || task.Objectives == null)
                return (0, 0);

            var completedSet = GetCompletedObjectives(task.NormalizedName);
            return (completedSet.Count, task.Objectives.Count);
        }

        #endregion

        #region Persistence

        /// <summary>
        /// Persistence, behind the narrow <see cref="IQuestProgressStore"/> surface rather than
        /// <see cref="UserDataDbService"/> directly, so tests can substitute a fake and see which
        /// profile each write landed in. Internal and non-readonly for the same reason:
        /// <c>GetUninitializedObject</c> skips field initializers, so a test assigns it.
        /// </summary>
        internal IQuestProgressStore _store = UserDataDbService.Instance;

        private void SaveProgress()
        {
            // DB에 저장 (Task.Run으로 데드락 방지)
            var snapshot = Snapshot;
            Task.Run(async () => await SaveProgressToDbAsync(snapshot)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Rewrites every row of <paramref name="snapshot"/> under its own ProfileId. The
        /// profile comes from the snapshot rather than being re-read per row: the loop awaits
        /// between rows, so a transition landing mid-loop used to scatter one rewrite across two
        /// partitions.
        /// </summary>
        private async Task SaveProgressToDbAsync(ProgressSnapshot snapshot)
        {
            try
            {
                foreach (var kvp in snapshot.Quests)
                {
                    var normalizedName = kvp.Key;
                    var status = kvp.Value;

                    // ID 조회
                    string id = normalizedName;
                    if (_tasksByNormalizedName.TryGetValue(normalizedName, out var task))
                    {
                        id = task.Ids?.FirstOrDefault() ?? normalizedName;
                    }

                    await _store.SaveQuestProgressAsync(id, normalizedName, status, snapshot.ProfileId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Failed to save progress to DB: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the selected profile's rows during startup initialization.
        /// <para>
        /// Allowlisted selection read: this is the one load that has to ask which profile is
        /// selected, because no <see cref="ProfileService.ActiveProfileChanged"/> has told the
        /// service yet. It goes through the same revision guard as every later reload, so a
        /// transition arriving while this load is in flight still wins.
        /// </para>
        /// </summary>
        private void LoadProgress()
        {
            // Task.Run으로 데드락 방지
            // 마이그레이션은 MainWindow에서 먼저 수행됨
            var profileService = ProfileService.Instance;
            var profile = profileService.ActiveProfile;
            var revision = profileService.TransitionRevision;

            // notify: false is load-bearing, not an optimization. This call blocks the dispatcher
            // thread on GetResult(), and ProgressChanged subscribers marshal their refresh back to
            // the dispatcher — raising it here deadlocks the app on startup and after every
            // in-place data reload. Initialize's callers redraw once they finish anyway, which is
            // why the pre-snapshot initial load never raised it either.
            Task.Run(async () => await ReloadForProfileAsync(profile, revision, notify: false))
                .GetAwaiter().GetResult();
        }

        /// <summary>
        /// Reload all progress (quest + objective) for <paramref name="profile"/> and notify the
        /// UI. Called when the user (or log detection) switches profiles.
        /// <para>
        /// Both dictionaries are read before either is published, so the two can never be
        /// observed half-swapped. <paramref name="revision"/> is the transition this load serves:
        /// if a newer one is requested while these reads are in flight, this load discards its
        /// result and lets the newer one publish, rather than leaving the snapshot naming the
        /// newer profile while holding this one's rows.
        /// </para>
        /// </summary>
        public Task ReloadForProfileAsync(AppProfile profile, long revision)
            => ReloadForProfileAsync(profile, revision, notify: true);

        /// <summary>
        /// The reload proper. <paramref name="notify"/> is false only for the startup load, which
        /// runs with the dispatcher blocked; see <see cref="LoadProgress"/>.
        /// </summary>
        private async Task ReloadForProfileAsync(AppProfile profile, long revision, bool notify)
        {
            var profileId = ProfileService.GetProfileId(profile);
            ClaimRevision(revision);

            Dictionary<string, QuestStatus> quests;
            Dictionary<string, bool> objectives;
            try
            {
                quests = await _store.LoadQuestProgressAsync(profileId);
                objectives = await _store.LoadObjectiveProgressAsync(profileId);
            }
            catch (Exception ex)
            {
                // An unreadable store must not leave the previous profile's rows on screen under
                // the new profile's name: publish empty, which is what "nothing recorded" means.
                System.Diagnostics.Debug.WriteLine($"[QuestProgressService] Failed to load progress from DB: {ex.Message}");
                quests = new Dictionary<string, QuestStatus>(StringComparer.OrdinalIgnoreCase);
                objectives = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            }

            if (Interlocked.Read(ref _latestRevision) != revision)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[QuestProgressService] Discarding stale load for {profileId} (revision {revision})");
                return;
            }

            Snapshot = ProgressSnapshot.From(profileId, revision, quests, objectives);
            System.Diagnostics.Debug.WriteLine(
                $"[QuestProgressService] Loaded {quests.Count} quest and {objectives.Count} objective rows for {profileId}");

            if (notify) ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Raises <see cref="_latestRevision"/> to <paramref name="revision"/> if it is newer.</summary>
        private void ClaimRevision(long revision)
        {
            while (true)
            {
                var current = Interlocked.Read(ref _latestRevision);
                if (revision <= current) return;
                if (Interlocked.CompareExchange(ref _latestRevision, revision, current) == current) return;
            }
        }

        #endregion
    }

    /// <summary>
    /// Event args for objective progress changes
    /// </summary>
    public class ObjectiveProgressChangedEventArgs : EventArgs
    {
        public string QuestNormalizedName { get; }
        public int ObjectiveIndex { get; }
        public bool IsCompleted { get; }

        public ObjectiveProgressChangedEventArgs(string questNormalizedName, int objectiveIndex, bool isCompleted)
        {
            QuestNormalizedName = questNormalizedName;
            ObjectiveIndex = objectiveIndex;
            IsCompleted = isCompleted;
        }
    }
}
