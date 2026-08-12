using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services
{
    /// <summary>
    /// Service for managing user's item inventory quantities (FIR/Non-FIR)
    /// </summary>
    public class ItemInventoryService
    {
        private static readonly ILogger _log = Log.For<ItemInventoryService>();
        private static ItemInventoryService? _instance;
        public static ItemInventoryService Instance => _instance ??= new ItemInventoryService();

        private readonly UserDataDbService _userDataDb = UserDataDbService.Instance;

        private ItemInventoryData _inventoryData = new();
        private readonly object _lock = new();

        // Debounce save timer
        private System.Timers.Timer? _saveTimer;
        // item normalized name -> profileId captured at dirty-time, so a pending save
        // always lands on the profile that was active when the change was made
        private readonly Dictionary<string, string> _pendingSaves = new(StringComparer.OrdinalIgnoreCase);

        public event EventHandler? InventoryChanged;

        /// <summary>
        /// The highest reload revision requested so far. A load that finishes after a newer one
        /// was requested discards its result instead of publishing quantities for a profile the
        /// user has already navigated away from (the same guard QuestProgressService uses).
        /// </summary>
        private long _latestRevision;

        private ItemInventoryService()
        {
            LoadInventory();
            InitializeSaveTimer();
            // The event's own profile and revision are carried into the reload. Discarding them
            // and re-reading ProfileService inside the async body is how the quest cache used to
            // file one profile's rows under another's id: the reads happen after an await, by
            // which time a second transition may have landed.
            ProfileService.Instance.ActiveProfileChanged += (_, e) => _ = ReloadForProfileAsync(e.Profile, e.Revision);
        }

        private void InitializeSaveTimer()
        {
            _saveTimer = new System.Timers.Timer(500); // 500ms debounce
            _saveTimer.AutoReset = false;
            _saveTimer.Elapsed += (s, e) =>
            {
                SavePendingItems();
            };
        }

        private void SavePendingItems()
        {
            List<KeyValuePair<string, string>> itemsToSave;
            lock (_lock)
            {
                if (_pendingSaves.Count == 0) return;
                itemsToSave = _pendingSaves.ToList();
                _pendingSaves.Clear();
            }

            Task.Run(async () =>
            {
                foreach (var entry in itemsToSave)
                {
                    var itemName = entry.Key;
                    var profileId = entry.Value;
                    try
                    {
                        int firQty, nonFirQty;
                        lock (_lock)
                        {
                            if (_inventoryData.Items.TryGetValue(itemName, out var inv))
                            {
                                firQty = inv.FirQuantity;
                                nonFirQty = inv.NonFirQuantity;
                            }
                            else
                            {
                                firQty = 0;
                                nonFirQty = 0;
                            }
                        }
                        await _userDataDb.SaveItemInventoryAsync(itemName, firQty, nonFirQty, profileId);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"Save failed for {itemName}: {ex.Message}");
                    }
                }
            }).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Get inventory for a specific item
        /// </summary>
        public ItemInventory GetInventory(string itemNormalizedName)
        {
            lock (_lock)
            {
                if (_inventoryData.Items.TryGetValue(itemNormalizedName, out var inventory))
                {
                    return inventory;
                }

                return new ItemInventory { ItemNormalizedName = itemNormalizedName };
            }
        }

        /// <summary>
        /// Get FIR quantity for an item
        /// </summary>
        public int GetFirQuantity(string itemNormalizedName)
        {
            return GetInventory(itemNormalizedName).FirQuantity;
        }

        /// <summary>
        /// Get Non-FIR quantity for an item
        /// </summary>
        public int GetNonFirQuantity(string itemNormalizedName)
        {
            return GetInventory(itemNormalizedName).NonFirQuantity;
        }

        /// <summary>
        /// Get total quantity for an item
        /// </summary>
        public int GetTotalQuantity(string itemNormalizedName)
        {
            return GetInventory(itemNormalizedName).TotalQuantity;
        }

        /// <summary>
        /// Set FIR quantity for an item
        /// </summary>
        public void SetFirQuantity(string itemNormalizedName, int quantity)
        {
            quantity = Math.Max(0, quantity);

            lock (_lock)
            {
                if (!_inventoryData.Items.TryGetValue(itemNormalizedName, out var inventory))
                {
                    inventory = new ItemInventory { ItemNormalizedName = itemNormalizedName };
                    _inventoryData.Items[itemNormalizedName] = inventory;
                }

                if (inventory.FirQuantity != quantity)
                {
                    inventory.FirQuantity = quantity;
                    CleanupEmptyInventory(itemNormalizedName);
                    ScheduleSave(itemNormalizedName);
                    InventoryChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Set Non-FIR quantity for an item
        /// </summary>
        public void SetNonFirQuantity(string itemNormalizedName, int quantity)
        {
            quantity = Math.Max(0, quantity);

            lock (_lock)
            {
                if (!_inventoryData.Items.TryGetValue(itemNormalizedName, out var inventory))
                {
                    inventory = new ItemInventory { ItemNormalizedName = itemNormalizedName };
                    _inventoryData.Items[itemNormalizedName] = inventory;
                }

                if (inventory.NonFirQuantity != quantity)
                {
                    inventory.NonFirQuantity = quantity;
                    CleanupEmptyInventory(itemNormalizedName);
                    ScheduleSave(itemNormalizedName);
                    InventoryChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Adjust FIR quantity by delta (can be positive or negative)
        /// </summary>
        public void AdjustFirQuantity(string itemNormalizedName, int delta)
        {
            var current = GetFirQuantity(itemNormalizedName);
            SetFirQuantity(itemNormalizedName, current + delta);
        }

        /// <summary>
        /// Adjust Non-FIR quantity by delta (can be positive or negative)
        /// </summary>
        public void AdjustNonFirQuantity(string itemNormalizedName, int delta)
        {
            var current = GetNonFirQuantity(itemNormalizedName);
            SetNonFirQuantity(itemNormalizedName, current + delta);
        }

        /// <summary>
        /// Remove inventory entry if both quantities are 0
        /// </summary>
        private void CleanupEmptyInventory(string itemNormalizedName)
        {
            if (_inventoryData.Items.TryGetValue(itemNormalizedName, out var inventory))
            {
                if (inventory.FirQuantity == 0 && inventory.NonFirQuantity == 0)
                {
                    _inventoryData.Items.Remove(itemNormalizedName);
                }
            }
        }

        /// <summary>
        /// Calculate fulfillment info for an item
        /// </summary>
        public ItemFulfillmentInfo GetFulfillmentInfo(string itemNormalizedName, int requiredTotal, int requiredFir)
        {
            var inventory = GetInventory(itemNormalizedName);

            return new ItemFulfillmentInfo
            {
                ItemNormalizedName = itemNormalizedName,
                RequiredTotal = requiredTotal,
                RequiredFir = requiredFir,
                OwnedFir = inventory.FirQuantity,
                OwnedNonFir = inventory.NonFirQuantity
            };
        }

        /// <summary>
        /// Get all items in inventory
        /// </summary>
        public IReadOnlyDictionary<string, ItemInventory> GetAllInventory()
        {
            lock (_lock)
            {
                return new Dictionary<string, ItemInventory>(_inventoryData.Items, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Get inventory statistics
        /// </summary>
        public (int TotalItems, int TotalFirCount, int TotalNonFirCount) GetStatistics()
        {
            lock (_lock)
            {
                var totalFir = _inventoryData.Items.Values.Sum(i => i.FirQuantity);
                var totalNonFir = _inventoryData.Items.Values.Sum(i => i.NonFirQuantity);
                return (_inventoryData.Items.Count, totalFir, totalNonFir);
            }
        }

        /// <summary>
        /// Reset all inventory data
        /// </summary>
        public void ResetAllInventory()
        {
            lock (_lock)
            {
                _inventoryData = new ItemInventoryData();
            }

            // Resolved before the Task.Run body: a switch between scheduling and running would
            // otherwise clear the profile the user just moved to instead of the one they reset.
            var profileId = ProfileService.Instance.ActiveProfileId;
            Task.Run(async () =>
            {
                try
                {
                    await _userDataDb.ClearAllItemInventoryAsync(profileId);
                }
                catch (Exception ex)
                {
                    _log.Error($"Reset failed for {profileId}", ex);
                }
            }).GetAwaiter().GetResult();

            InventoryChanged?.Invoke(this, EventArgs.Empty);
        }

        #region Persistence

        private void ScheduleSave(string itemNormalizedName)
        {
            lock (_lock)
            {
                // Capture the active profile at dirty-time so a later profile switch
                // can't redirect this save to the wrong profile.
                _pendingSaves[itemNormalizedName] = ProfileService.Instance.ActiveProfileId;
            }
            _inventoryData.LastUpdated = DateTime.UtcNow;
            _saveTimer?.Stop();
            _saveTimer?.Start();
        }

        /// <summary>
        /// Loads the selected profile's quantities during construction: the one load with no
        /// <see cref="ProfileService.ActiveProfileChanged"/> to learn the profile from. The
        /// profile and revision come from one atomic read, so a transition landing between them
        /// cannot pair the old profile with the new revision, and the load goes through the same
        /// staleness guard as every later reload.
        /// </summary>
        private void LoadInventory()
        {
            // Task.Run으로 데드락 방지
            // 마이그레이션은 MainWindow에서 먼저 수행됨
            var (profile, revision) = ProfileService.Instance.CurrentTransition;
            Task.Run(async () =>
            {
                await LoadInventoryFromDbAsync(ProfileService.GetProfileId(profile), revision);
            }).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Reload inventory for <paramref name="profile"/> and notify the UI.
        /// Pending debounced saves are flushed first (using their captured profile ids)
        /// so the previous profile's edits are persisted before its data is swapped out.
        /// <para>
        /// The profile is a parameter and is turned into a storage id before the first await:
        /// re-reading the selection inside the async body made a reload that started for one
        /// profile finish by publishing another's quantities. <paramref name="revision"/> is the
        /// transition this load serves, so a load that loses a race discards itself instead of
        /// publishing over the newer one.
        /// </para>
        /// </summary>
        public async Task ReloadForProfileAsync(AppProfile profile, long revision)
        {
            var profileId = ProfileService.GetProfileId(profile);

            _saveTimer?.Stop();
            SavePendingItems();

            if (!await LoadInventoryFromDbAsync(profileId, revision)) return;

            InventoryChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Reads <paramref name="profileId"/>'s quantities and publishes them, unless a newer
        /// transition was requested while the read was in flight. Returns false when the result
        /// was discarded as stale.
        /// </summary>
        private async Task<bool> LoadInventoryFromDbAsync(string profileId, long revision)
        {
            ClaimRevision(revision);

            ItemInventoryData newData;
            try
            {
                var items = await _userDataDb.LoadItemInventoryAsync(profileId);
                newData = new ItemInventoryData
                {
                    LastUpdated = DateTime.UtcNow,
                    Items = new Dictionary<string, ItemInventory>(StringComparer.OrdinalIgnoreCase)
                };

                foreach (var kvp in items)
                {
                    newData.Items[kvp.Key] = new ItemInventory
                    {
                        ItemNormalizedName = kvp.Key,
                        FirQuantity = kvp.Value.FirQuantity,
                        NonFirQuantity = kvp.Value.NonFirQuantity
                    };
                }
            }
            catch (Exception ex)
            {
                // An unreadable store must not leave the previous profile's quantities on screen
                // under the new profile's name: publish empty, which is what "nothing owned"
                // means.
                _log.Error($"Load failed for {profileId}", ex);
                newData = new ItemInventoryData();
            }

            if (Interlocked.Read(ref _latestRevision) != revision)
            {
                _log.Debug($"Discarding stale inventory load for {profileId} (revision {revision})");
                return false;
            }

            lock (_lock)
            {
                _inventoryData = newData;
            }
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
}
