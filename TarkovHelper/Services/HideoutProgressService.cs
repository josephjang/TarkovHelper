using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services
{
    /// <summary>
    /// Service for managing hideout construction progress state
    /// </summary>
    public class HideoutProgressService
    {
        private static readonly ILogger _log = Log.For<HideoutProgressService>();
        private static HideoutProgressService? _instance;
        public static HideoutProgressService Instance => _instance ??= new HideoutProgressService();

        private readonly UserDataDbService _userDataDb = UserDataDbService.Instance;

        private HideoutProgressService()
        {
            // The event's own profile and revision are carried into the reload. Discarding them
            // and re-reading ProfileService inside the async body is how the quest cache used to
            // file one profile's rows under another's id: the reads happen after an await, by
            // which time a second transition may have landed.
            ProfileService.Instance.ActiveProfileChanged += (_, e) => _ = ReloadForProfileAsync(e.Profile, e.Revision);
        }

        /// <summary>
        /// The highest reload revision requested so far. A load that finishes after a newer one
        /// was requested discards its result instead of publishing rows for a profile the user
        /// has already navigated away from (the same guard QuestProgressService uses).
        /// </summary>
        private long _latestRevision;

        // Currency items should count by reference count, not total amount
        private static readonly HashSet<string> CurrencyItems = new(StringComparer.OrdinalIgnoreCase)
        {
            "roubles", "dollars", "euros"
        };

        private static bool IsCurrency(string normalizedName) => CurrencyItems.Contains(normalizedName);

        private HideoutProgress _progress = new();
        private Dictionary<string, HideoutModule> _modulesByNormalizedName = new();
        private List<HideoutModule> _allModules = new();

        public event EventHandler? ProgressChanged;

        /// <summary>
        /// Initialize service with hideout module data
        /// </summary>
        public void Initialize(List<HideoutModule> modules)
        {
            _allModules = modules;

            // Build dictionary by normalized name
            _modulesByNormalizedName = new Dictionary<string, HideoutModule>(StringComparer.OrdinalIgnoreCase);
            foreach (var module in modules.Where(m => !string.IsNullOrEmpty(m.NormalizedName)))
            {
                if (!_modulesByNormalizedName.ContainsKey(module.NormalizedName))
                {
                    _modulesByNormalizedName[module.NormalizedName] = module;
                }
            }

            LoadProgress();
        }

        /// <summary>
        /// Get all modules
        /// </summary>
        public IReadOnlyList<HideoutModule> AllModules => _allModules;

        /// <summary>
        /// Get module by normalized name
        /// </summary>
        public HideoutModule? GetModule(string normalizedName)
        {
            return _modulesByNormalizedName.TryGetValue(normalizedName, out var module) ? module : null;
        }

        /// <summary>
        /// Get current level for a module (0 = not built)
        /// </summary>
        public int GetCurrentLevel(HideoutModule module)
        {
            if (string.IsNullOrEmpty(module.NormalizedName))
                return 0;

            return _progress.Modules.TryGetValue(module.NormalizedName, out var level) ? level : 0;
        }

        /// <summary>
        /// Get current level for a module by normalized name
        /// </summary>
        public int GetCurrentLevel(string normalizedName)
        {
            return _progress.Modules.TryGetValue(normalizedName, out var level) ? level : 0;
        }

        /// <summary>
        /// Set current level for a module
        /// </summary>
        public void SetLevel(HideoutModule module, int level)
        {
            if (string.IsNullOrEmpty(module.NormalizedName))
                return;

            // Clamp level between 0 and max level
            level = Math.Max(0, Math.Min(level, module.MaxLevel));

            if (level == 0)
            {
                _progress.Modules.Remove(module.NormalizedName);
            }
            else
            {
                _progress.Modules[module.NormalizedName] = level;
            }

            _progress.LastUpdated = DateTime.UtcNow;
            SaveSingleModule(module.NormalizedName, level);
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SaveSingleModule(string normalizedName, int level)
        {
            // Resolved before the Task.Run body, not inside it: a profile switch between
            // scheduling and running would otherwise redirect this row to the new profile.
            var profileId = ProfileService.Instance.ActiveProfileId;
            Task.Run(async () =>
            {
                try
                {
                    await _userDataDb.SaveHideoutProgressAsync(normalizedName, level, profileId);
                }
                catch (Exception ex)
                {
                    _log.Error($"Save failed for {normalizedName} in {profileId}", ex);
                }
            }).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Increment level for a module
        /// </summary>
        public void IncrementLevel(HideoutModule module)
        {
            var currentLevel = GetCurrentLevel(module);
            if (currentLevel < module.MaxLevel)
            {
                SetLevel(module, currentLevel + 1);
            }
        }

        /// <summary>
        /// Decrement level for a module
        /// </summary>
        public void DecrementLevel(HideoutModule module)
        {
            var currentLevel = GetCurrentLevel(module);
            if (currentLevel > 0)
            {
                SetLevel(module, currentLevel - 1);
            }
        }

        /// <summary>
        /// Get next level requirements for a module
        /// </summary>
        public HideoutLevel? GetNextLevel(HideoutModule module)
        {
            var currentLevel = GetCurrentLevel(module);
            return module.Levels.FirstOrDefault(l => l.Level == currentLevel + 1);
        }

        /// <summary>
        /// Get total remaining item requirements for a module (all levels after current)
        /// </summary>
        public Dictionary<string, (HideoutItemRequirement Item, int TotalCount, int FIRCount)> GetRemainingItemRequirements(HideoutModule module)
        {
            var currentLevel = GetCurrentLevel(module);
            var result = new Dictionary<string, (HideoutItemRequirement Item, int TotalCount, int FIRCount)>(StringComparer.OrdinalIgnoreCase);

            foreach (var level in module.Levels.Where(l => l.Level > currentLevel))
            {
                foreach (var itemReq in level.ItemRequirements)
                {
                    if (result.TryGetValue(itemReq.ItemNormalizedName, out var existing))
                    {
                        var newFirCount = existing.FIRCount + (itemReq.FoundInRaid ? itemReq.Count : 0);
                        result[itemReq.ItemNormalizedName] = (existing.Item, existing.TotalCount + itemReq.Count, newFirCount);
                    }
                    else
                    {
                        var firCount = itemReq.FoundInRaid ? itemReq.Count : 0;
                        result[itemReq.ItemNormalizedName] = (itemReq, itemReq.Count, firCount);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Get total item requirements for all incomplete hideout modules
        /// </summary>
        public Dictionary<string, HideoutItemAggregate> GetAllRemainingItemRequirements()
        {
            var result = new Dictionary<string, HideoutItemAggregate>(StringComparer.OrdinalIgnoreCase);

            foreach (var module in _allModules)
            {
                var currentLevel = GetCurrentLevel(module);

                foreach (var level in module.Levels.Where(l => l.Level > currentLevel))
                {
                    foreach (var itemReq in level.ItemRequirements)
                    {
                        // For currency items, count by reference (1 per hideout level) instead of total amount
                        var countToAdd = IsCurrency(itemReq.ItemNormalizedName) ? 1 : itemReq.Count;
                        var firCountToAdd = itemReq.FoundInRaid ? countToAdd : 0;

                        if (result.TryGetValue(itemReq.ItemNormalizedName, out var existing))
                        {
                            existing.HideoutCount += countToAdd;
                            existing.TotalCount += countToAdd;
                            // Track FIR count separately
                            if (itemReq.FoundInRaid)
                            {
                                existing.HideoutFIRCount += countToAdd;
                                existing.TotalFIRCount += countToAdd;
                                existing.FoundInRaid = true;
                            }
                        }
                        else
                        {
                            result[itemReq.ItemNormalizedName] = new HideoutItemAggregate
                            {
                                ItemId = itemReq.ItemId,
                                ItemName = itemReq.ItemName,
                                ItemNameKo = itemReq.ItemNameKo,
                                ItemNameJa = itemReq.ItemNameJa,
                                ItemNormalizedName = itemReq.ItemNormalizedName,
                                IconLink = itemReq.IconLink,
                                HideoutCount = countToAdd,
                                HideoutFIRCount = firCountToAdd,
                                QuestCount = 0,
                                QuestFIRCount = 0,
                                TotalCount = countToAdd,
                                TotalFIRCount = firCountToAdd,
                                FoundInRaid = itemReq.FoundInRaid
                            };
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Check if all prerequisites are met for building a specific level
        /// </summary>
        public bool ArePrerequisitesMet(HideoutModule module, int targetLevel)
        {
            var level = module.Levels.FirstOrDefault(l => l.Level == targetLevel);
            if (level == null)
                return false;

            // Check station level requirements
            foreach (var stationReq in level.StationLevelRequirements)
            {
                var requiredStationLevel = GetCurrentLevel(stationReq.StationId);
                if (requiredStationLevel < stationReq.Level)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Get construction statistics
        /// </summary>
        public HideoutStatistics GetStatistics()
        {
            var stats = new HideoutStatistics();

            foreach (var module in _allModules)
            {
                var currentLevel = GetCurrentLevel(module);
                var maxLevel = module.MaxLevel;

                stats.TotalModules++;
                stats.TotalLevels += maxLevel;
                stats.CompletedLevels += currentLevel;

                if (currentLevel == 0)
                    stats.NotStarted++;
                else if (currentLevel >= maxLevel)
                    stats.FullyCompleted++;
                else
                    stats.InProgress++;
            }

            return stats;
        }

        /// <summary>
        /// Reset all hideout progress
        /// </summary>
        public void ResetAllProgress()
        {
            _progress = new HideoutProgress();

            // Resolved before the Task.Run body: a switch between scheduling and running would
            // otherwise clear the profile the user just moved to instead of the one they reset.
            var profileId = ProfileService.Instance.ActiveProfileId;
            Task.Run(async () =>
            {
                try
                {
                    await _userDataDb.ClearAllHideoutProgressAsync(profileId);
                }
                catch (Exception ex)
                {
                    _log.Error($"Reset failed for {profileId}", ex);
                }
            }).GetAwaiter().GetResult();
            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        #region Persistence

        /// <summary>
        /// Loads the selected profile's rows during startup initialization: the one load with no
        /// <see cref="ProfileService.ActiveProfileChanged"/> to learn the profile from. The
        /// profile and revision come from one atomic read, so a transition landing between them
        /// cannot pair the old profile with the new revision, and the load goes through the same
        /// staleness guard as every later reload.
        /// </summary>
        private void LoadProgress()
        {
            // Task.Run으로 데드락 방지
            // 마이그레이션은 MainWindow에서 먼저 수행됨
            var (profile, revision) = ProfileService.Instance.CurrentTransition;
            Task.Run(async () =>
            {
                await LoadProgressFromDbAsync(ProfileService.GetProfileId(profile), revision);
            }).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Reload hideout progress for <paramref name="profile"/> and notify the UI. Called when
        /// the user (or log detection) switches profiles.
        /// <para>
        /// The profile is a parameter and is turned into a storage id before the first await:
        /// re-reading the selection inside the async body made a reload that started for one
        /// profile finish by publishing another's rows. <paramref name="revision"/> is the
        /// transition this load serves, so a load that loses a race discards itself instead of
        /// publishing over the newer one.
        /// </para>
        /// </summary>
        public async Task ReloadForProfileAsync(AppProfile profile, long revision)
        {
            var profileId = ProfileService.GetProfileId(profile);
            if (!await LoadProgressFromDbAsync(profileId, revision)) return;

            ProgressChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Reads <paramref name="profileId"/>'s rows and publishes them, unless a newer
        /// transition was requested while the read was in flight. Returns false when the result
        /// was discarded as stale.
        /// </summary>
        private async Task<bool> LoadProgressFromDbAsync(string profileId, long revision)
        {
            ClaimRevision(revision);

            HideoutProgress loaded;
            try
            {
                var modules = await _userDataDb.LoadHideoutProgressAsync(profileId);
                loaded = new HideoutProgress
                {
                    Modules = new Dictionary<string, int>(modules, StringComparer.OrdinalIgnoreCase),
                    LastUpdated = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                // An unreadable store must not leave the previous profile's levels on screen
                // under the new profile's name: publish empty, which is what "nothing built"
                // means.
                _log.Error($"Load failed for {profileId}", ex);
                loaded = new HideoutProgress();
            }

            if (Interlocked.Read(ref _latestRevision) != revision)
            {
                _log.Debug($"Discarding stale hideout load for {profileId} (revision {revision})");
                return false;
            }

            _progress = loaded;
            return true;
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
    /// Aggregated item requirement from hideout
    /// </summary>
    public class HideoutItemAggregate
    {
        public string ItemId { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public string? ItemNameKo { get; set; }
        public string? ItemNameJa { get; set; }
        public string ItemNormalizedName { get; set; } = string.Empty;
        public string? IconLink { get; set; }
        public int QuestCount { get; set; }
        public int QuestFIRCount { get; set; }
        public int HideoutCount { get; set; }
        public int HideoutFIRCount { get; set; }
        public int TotalCount { get; set; }
        public int TotalFIRCount { get; set; }
        public bool FoundInRaid { get; set; }
    }

    /// <summary>
    /// Hideout construction statistics
    /// </summary>
    public class HideoutStatistics
    {
        public int TotalModules { get; set; }
        public int NotStarted { get; set; }
        public int InProgress { get; set; }
        public int FullyCompleted { get; set; }
        public int TotalLevels { get; set; }
        public int CompletedLevels { get; set; }
    }
}
