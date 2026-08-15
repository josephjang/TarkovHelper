using System.IO;
using System.Text.Json;
using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Services;

/// <summary>
/// 외부 Config 폴더에서 데이터를 마이그레이션하는 서비스
/// (이전 버전 TarkovHelper에서 현재 DB로 데이터 가져오기)
/// </summary>
public sealed class ConfigMigrationService
{
    private static readonly ILogger _log = Log.For<ConfigMigrationService>();

    private static ConfigMigrationService? _instance;
    public static ConfigMigrationService Instance => _instance ??= new ConfigMigrationService();

    // 매핑 실패한 항목 추적
    private List<string> _unmappedQuests = new();
    private List<string> _unmappedHideouts = new();

    private ConfigMigrationService() { }

    /// <summary>
    /// 현재 앱 Config 폴더에 마이그레이션이 필요한 JSON 파일이 있는지 확인
    /// </summary>
    public bool NeedsAutoMigration()
    {
        return IsValidConfigFolder(AppEnv.ConfigPath);
    }

    /// <summary>
    /// 현재 앱 Config 폴더에서 자동 마이그레이션 실행 (앱 시작 시)
    /// </summary>
    public async Task<MigrationResult> MigrateFromCurrentConfigAsync(IProgress<string>? progress = null)
    {
        var result = await MigrateFromConfigFolderAsync(AppEnv.ConfigPath, progress, deleteAfterMigration: true);
        return result;
    }

    /// <summary>
    /// 마이그레이션 결과
    /// </summary>
    public class MigrationResult
    {
        public bool Success { get; set; }
        public int QuestProgressCount { get; set; }
        public int HideoutProgressCount { get; set; }
        public int ItemInventoryCount { get; set; }
        public int SettingsCount { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();

        public int TotalCount => QuestProgressCount + HideoutProgressCount + ItemInventoryCount + SettingsCount;
        public bool HasErrors => Errors.Count > 0;
        public bool HasWarnings => Warnings.Count > 0;
    }

    /// <summary>
    /// Config 폴더가 유효한지 확인
    /// </summary>
    public bool IsValidConfigFolder(string path)
    {
        if (!Directory.Exists(path)) return false;

        // 최소한 하나의 알려진 파일이 있어야 함
        var knownFiles = new[]
        {
            "quest_progress.json",
            "hideout_progress.json",
            "item_inventory.json",
            "app_settings.json"
        };

        return knownFiles.Any(f => File.Exists(Path.Combine(path, f)));
    }

    /// <summary>
    /// Config 폴더에서 어떤 데이터가 있는지 미리보기
    /// </summary>
    public MigrationResult PreviewMigration(string configFolderPath)
    {
        var result = new MigrationResult { Success = true };

        if (!IsValidConfigFolder(configFolderPath))
        {
            result.Success = false;
            result.Errors.Add("Invalid Config folder. No recognized files found.");
            return result;
        }

        // Quest Progress
        var questPath = Path.Combine(configFolderPath, "quest_progress.json");
        if (File.Exists(questPath))
        {
            try
            {
                var json = File.ReadAllText(questPath);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                result.QuestProgressCount = data?.Count ?? 0;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Quest progress file error: {ex.Message}");
            }
        }

        // Hideout Progress
        var hideoutPath = Path.Combine(configFolderPath, "hideout_progress.json");
        if (File.Exists(hideoutPath))
        {
            try
            {
                var json = File.ReadAllText(hideoutPath);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("modules", out var modulesElement))
                {
                    result.HideoutProgressCount = modulesElement.EnumerateObject().Count();
                }
                else
                {
                    // Old format
                    var data = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    result.HideoutProgressCount = data?.Count ?? 0;
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Hideout progress file error: {ex.Message}");
            }
        }

        // Item Inventory
        var inventoryPath = Path.Combine(configFolderPath, "item_inventory.json");
        if (File.Exists(inventoryPath))
        {
            try
            {
                var json = File.ReadAllText(inventoryPath);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("items", out var itemsElement))
                {
                    result.ItemInventoryCount = itemsElement.EnumerateObject().Count();
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Item inventory file error: {ex.Message}");
            }
        }

        // App Settings
        var settingsPath = Path.Combine(configFolderPath, "app_settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                var json = File.ReadAllText(settingsPath);
                using var doc = JsonDocument.Parse(json);
                result.SettingsCount = doc.RootElement.EnumerateObject()
                    .Count(p => !p.Value.ValueKind.Equals(JsonValueKind.Null));
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Settings file error: {ex.Message}");
            }
        }

        return result;
    }

    /// <summary>
    /// Config 폴더에서 데이터 마이그레이션 실행
    /// </summary>
    /// <param name="configFolderPath">Config 폴더 경로</param>
    /// <param name="progress">진행 상황 보고</param>
    /// <param name="deleteAfterMigration">마이그레이션 후 JSON 파일 삭제 여부</param>
    public async Task<MigrationResult> MigrateFromConfigFolderAsync(
        string configFolderPath,
        IProgress<string>? progress = null,
        bool deleteAfterMigration = false)
    {
        var result = new MigrationResult { Success = true };

        // 매핑 실패 목록 초기화
        _unmappedQuests.Clear();
        _unmappedHideouts.Clear();

        if (!IsValidConfigFolder(configFolderPath))
        {
            result.Success = false;
            result.Errors.Add("Invalid Config folder");
            return result;
        }

        var userDataDb = UserDataDbService.Instance;

        // 1. Quest Progress (NormalizedName → ID 매핑 필요)
        progress?.Report("Migrating quest progress...");
        var questMigrationResult = await MigrateQuestProgressAsync(configFolderPath, userDataDb);
        result.QuestProgressCount = questMigrationResult.count;
        if (questMigrationResult.error != null)
            result.Warnings.Add(questMigrationResult.error);

        // 2. Hideout Progress (NormalizedName 매핑)
        progress?.Report("Migrating hideout progress...");
        var hideoutMigrationResult = await MigrateHideoutProgressAsync(configFolderPath, userDataDb);
        result.HideoutProgressCount = hideoutMigrationResult.count;
        if (hideoutMigrationResult.error != null)
            result.Warnings.Add(hideoutMigrationResult.error);

        // 3. Item Inventory
        progress?.Report("Migrating item inventory...");
        var inventoryMigrationResult = await MigrateItemInventoryAsync(configFolderPath, userDataDb);
        result.ItemInventoryCount = inventoryMigrationResult.count;
        if (inventoryMigrationResult.error != null)
            result.Warnings.Add(inventoryMigrationResult.error);

        // The other half of the flush this step began with. ItemInventoryService caches
        // quantities and only reloads them on a profile switch, so without this it keeps
        // rendering the pre-import numbers - and AdjustFirQuantity persists cached + delta
        // ABSOLUTELY, so one nudge of a spinner would write the pre-import quantity back over the
        // row just imported. The partition comes from the step that wrote it, not from a second
        // hardcoding of the same constant.
        if (inventoryMigrationResult.profileWrittenTo != null)
        {
            await ItemInventoryService.Instance.ReloadAfterExternalWriteAsync(
                inventoryMigrationResult.profileWrittenTo);
        }

        // 4. App Settings
        progress?.Report("Migrating settings...");
        var settingsMigrationResult = await MigrateAppSettingsAsync(configFolderPath, userDataDb);
        result.SettingsCount = settingsMigrationResult.count;
        if (settingsMigrationResult.error != null)
            result.Warnings.Add(settingsMigrationResult.error);

        // Same question for the settings cache: rows were written behind its back, so it re-reads
        // them and re-raises the changed events when its snapshot names that partition. Once,
        // after the whole import, rather than one event per imported value.
        if (settingsMigrationResult.profileWrittenTo != null)
        {
            SettingsService.Instance.ReloadAfterExternalWrite(settingsMigrationResult.profileWrittenTo);
        }

        // 매핑 실패 항목 경고 추가
        if (_unmappedQuests.Count > 0)
        {
            var sample = _unmappedQuests.Take(5).ToList();
            var more = _unmappedQuests.Count > 5 ? $" and {_unmappedQuests.Count - 5} more" : "";
            result.Warnings.Add($"Could not match {_unmappedQuests.Count} quest(s): {string.Join(", ", sample)}{more}");
        }

        if (_unmappedHideouts.Count > 0)
        {
            result.Warnings.Add($"Could not match hideout station(s): {string.Join(", ", _unmappedHideouts)}");
        }

        // 마이그레이션 후 JSON 파일 삭제 (자동 마이그레이션 시)
        if (deleteAfterMigration && result.TotalCount > 0)
        {
            DeleteMigratedJsonFiles(configFolderPath);
        }

        progress?.Report("Migration complete!");

        return result;
    }

    /// <summary>
    /// 마이그레이션된 JSON 파일 삭제
    /// </summary>
    private void DeleteMigratedJsonFiles(string configFolderPath)
    {
        var filesToDelete = new[]
        {
            "quest_progress.json",
            "quest_progress_v2.json",
            "objective_progress.json",
            "hideout_progress.json",
            "item_inventory.json",
            "app_settings.json"
        };

        var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "migration_log.txt");
        var deleteLog = new System.Text.StringBuilder();
        deleteLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Deleting migrated JSON files from: {configFolderPath}");

        foreach (var fileName in filesToDelete)
        {
            try
            {
                var filePath = Path.Combine(configFolderPath, fileName);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    deleteLog.AppendLine($"  Deleted: {fileName}");
                    System.Diagnostics.Debug.WriteLine($"[ConfigMigrationService] Deleted: {filePath}");
                }
            }
            catch (Exception ex)
            {
                deleteLog.AppendLine($"  Failed to delete {fileName}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[ConfigMigrationService] Failed to delete {fileName}: {ex.Message}");
            }
        }

        deleteLog.AppendLine();
        File.AppendAllText(logPath, deleteLog.ToString());
    }

    private async Task<(int count, string? error)> MigrateQuestProgressAsync(string configFolderPath, UserDataDbService userDataDb)
    {
        var filePath = Path.Combine(configFolderPath, "quest_progress.json");
        if (!File.Exists(filePath))
            return (0, null);

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            if (data == null || data.Count == 0)
                return (0, null);

            // QuestDbService에서 NormalizedName → Quest 매핑 가져오기
            var questDbService = QuestDbService.Instance;

            // QuestDbService가 로드되지 않았으면 로드
            if (!questDbService.IsLoaded)
            {
                await questDbService.LoadQuestsAsync();
            }
            var progressItems = new List<(string Id, string? NormalizedName, QuestStatus Status)>();

            foreach (var kvp in data)
            {
                if (!Enum.TryParse<QuestStatus>(kvp.Value, out var status))
                    continue;

                var normalizedName = kvp.Key;

                // NormalizedName으로 퀘스트 찾기
                var quest = questDbService.GetQuestByNormalizedName(normalizedName);

                if (quest != null)
                {
                    // 퀘스트 찾음 - 실제 ID 사용
                    var questId = quest.Ids?.FirstOrDefault() ?? normalizedName;
                    progressItems.Add((questId, normalizedName, status));
                }
                else
                {
                    // 퀘스트를 찾지 못함 - NormalizedName을 ID로 사용 (호환성 유지)
                    // 향후 reconcile에서 매핑 시도
                    progressItems.Add((normalizedName, normalizedName, status));
                    _unmappedQuests.Add(normalizedName);
                }
            }

            if (progressItems.Count > 0)
            {
                await userDataDb.SaveQuestProgressBatchAsync(progressItems, ProfileService.PvpProfileId);
            }

            return (progressItems.Count - _unmappedQuests.Count, null);
        }
        catch (Exception ex)
        {
            return (0, $"Quest progress migration error: {ex.Message}");
        }
    }

    private async Task<(int count, string? error)> MigrateHideoutProgressAsync(string configFolderPath, UserDataDbService userDataDb)
    {
        var filePath = Path.Combine(configFolderPath, "hideout_progress.json");
        if (!File.Exists(filePath))
            return (0, null);

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            Dictionary<string, int>? modules = null;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("modules", out var modulesElement))
            {
                modules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in modulesElement.EnumerateObject())
                {
                    if (prop.Value.TryGetInt32(out var level))
                    {
                        modules[prop.Name] = level;
                    }
                }
            }
            else
            {
                modules = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            }

            if (modules == null || modules.Count == 0)
                return (0, null);

            // HideoutDbService에서 NormalizedName → Station 매핑 가져오기
            var hideoutDbService = HideoutDbService.Instance;

            // HideoutDbService가 로드되지 않았으면 로드
            if (!hideoutDbService.IsLoaded)
            {
                await hideoutDbService.LoadStationsAsync();
            }

            var allStations = hideoutDbService.AllStations;

            // NormalizedName으로 Station 찾기 위한 룩업 생성
            var stationByNormalizedName = allStations
                .Where(s => !string.IsNullOrEmpty(s.NormalizedName))
                .ToDictionary(s => s.NormalizedName!, s => s, StringComparer.OrdinalIgnoreCase);

            var successCount = 0;

            foreach (var kvp in modules)
            {
                var normalizedName = kvp.Key;
                var level = kvp.Value;

                // NormalizedName으로 스테이션 찾기
                if (stationByNormalizedName.TryGetValue(normalizedName, out var station))
                {
                    // HideoutProgress는 StationId (NormalizedName)를 사용
                    await userDataDb.SaveHideoutProgressAsync(station.NormalizedName!, level, ProfileService.PvpProfileId);
                    successCount++;
                }
                else
                {
                    // 스테이션을 찾지 못함 - 그대로 저장 시도
                    await userDataDb.SaveHideoutProgressAsync(normalizedName, level, ProfileService.PvpProfileId);
                    _unmappedHideouts.Add(normalizedName);
                }
            }

            return (successCount, null);
        }
        catch (Exception ex)
        {
            return (0, $"Hideout progress migration error: {ex.Message}");
        }
    }

    /// <summary>
    /// legacy item_inventory.json 마이그레이션.
    /// <para>
    /// Like the three steps around it, the rows go to the PvP partition: the file predates
    /// profiles. <c>ItemInventoryService</c> is not consulted for the write - it would attribute
    /// the rows to whichever profile is loaded - but it does have to be told, in both directions:
    /// its debounced saves are flushed BEFORE the first write here, because they carry pre-import
    /// quantities and would land on top of the imported rows, and its cache is reloaded AFTER the
    /// last one by <see cref="MigrateFromConfigFolderAsync"/>.
    /// </para>
    /// </summary>
    /// <returns>
    /// The number of items imported, the partition they were written to (null when nothing was
    /// written, which is what tells the caller no cache refresh is due), and a warning message, or
    /// null when everything landed.
    /// </returns>
    private async Task<(int count, string? profileWrittenTo, string? error)> MigrateItemInventoryAsync(
        string configFolderPath, UserDataDbService userDataDb)
    {
        var filePath = Path.Combine(configFolderPath, "item_inventory.json");
        if (!File.Exists(filePath))
            return (0, null, null);

        // Before the first write, and never after it: a pending debounced save holds an absolute
        // quantity captured before this import, so flushing it later would overwrite an imported
        // row with the number it replaced.
        await ItemInventoryService.Instance.FlushPendingSavesAsync();

        // Declared outside the try for the reason MigrateAppSettingsAsync declares its own
        // counters outside: a failure halfway through leaves the rows before it committed, and
        // both the caller's cache refresh and its "delete the imported files" decision have to
        // see them.
        var count = 0;
        string? profileWrittenTo = null;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("items", out var itemsElement))
                return (0, null, null);

            foreach (var prop in itemsElement.EnumerateObject())
            {
                var itemName = prop.Name;
                var firQty = 0;
                var nonFirQty = 0;

                if (prop.Value.TryGetProperty("firQuantity", out var firElement))
                    firQty = firElement.GetInt32();
                if (prop.Value.TryGetProperty("nonFirQuantity", out var nonFirElement))
                    nonFirQty = nonFirElement.GetInt32();

                if (firQty > 0 || nonFirQty > 0)
                {
                    await userDataDb.SaveItemInventoryAsync(itemName, firQty, nonFirQty, ProfileService.PvpProfileId);
                    profileWrittenTo = ProfileService.PvpProfileId;
                    count++;
                }
            }

            return (count, profileWrittenTo, null);
        }
        catch (Exception ex)
        {
            return (count, profileWrittenTo, $"Item inventory migration error: {ex.Message}");
        }
    }

    /// <summary>
    /// legacy app_settings.json 마이그레이션.
    /// <para>
    /// The file predates profiles: it holds one flat set of values, so the profile-scoped ones
    /// (player level, scav rep, DSP decode count, level-locked quest visibility, faction) belong
    /// to PvP. They are written straight to the PvP partition, matching the three sibling
    /// migrations above, which all pass <see cref="ProfileService.PvpProfileId"/>, and matching
    /// <c>SettingsService.MigrateFromJsonIfNeeded</c>, which imports the same five.
    /// They deliberately do NOT go through the
    /// <c>SettingsService</c> properties any more: those setters persist under the profile the
    /// live settings snapshot names (docs/decisions/fix-profile-settings-race.spec.md), which is
    /// whichever profile the player has loaded when they press "Data Migration" - so a PvE player
    /// importing an old Config folder used to get quests, hideout and inventory under pvp and the
    /// level and Fence karma under pve.
    /// </para>
    /// <para>
    /// What the setters do to a value before storing it - clamp it, round the scav rep, format the
    /// doubles invariantly, lower case the faction - is NOT reproduced here but taken from
    /// <see cref="LegacyAppSettingsValues"/>, which the startup reader of the same file takes it
    /// from too. Raising the changed events is not reproduced per value either; the caller
    /// refreshes the cache once, after the whole import.
    /// </para>
    /// <para>
    /// The remaining keys are global (per install, UserSettings table), so no partition applies to
    /// them and they still go through the properties.
    /// </para>
    /// <para>
    /// Every write is guarded on its own. One value the store refuses (user_data.db locked by a
    /// log sync or a profile reset) must cost only that value, which is what the property setters
    /// this path replaced always did - they persist through <c>SettingsService.SaveProfileSetting</c>,
    /// which logs the failure and returns. The failed keys come back in the returned message so
    /// the caller can surface them as a warning.
    /// </para>
    /// </summary>
    /// <returns>
    /// The number of values imported, the partition the profile-scoped ones were written to (null
    /// when none was, which is what tells the caller no cache refresh is due), and a warning
    /// message naming what failed, or null when everything landed. The partition is REPORTED
    /// rather than assumed by the caller: naming <see cref="ProfileService.PvpProfileId"/> a
    /// second time at the refresh would be a copy that has to agree with the writes below by
    /// discipline alone.
    /// </returns>
    internal async Task<(int count, string? profileWrittenTo, string? error)> MigrateAppSettingsAsync(string configFolderPath, UserDataDbService userDataDb)
    {
        var filePath = Path.Combine(configFolderPath, "app_settings.json");
        if (!File.Exists(filePath))
            return (0, null, null);

        // Both are declared outside the try so a failure halfway through still reports the rows
        // that did land: they are already committed, the caller's refresh must see them, and
        // MigrateFromConfigFolderAsync deletes the imported JSON files only when the total is
        // non-zero - reporting zero for a partial import left the file behind to re-trigger the
        // auto migration on every launch.
        var count = 0;
        string? profileWrittenTo = null;

        // The JSON property names whose own write failed, reported together at the end. Named as
        // the file spells them, not as the store keys them: that is the one vocabulary all nine
        // values share (four of them are global, whose key names this class cannot see) and the
        // one the player can look up in the file they imported.
        var failedNames = new List<string>();

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            using var doc = JsonDocument.Parse(json);

            // Resolved on first use rather than up front, because only the global keys below need
            // it: constructing SettingsService opens user_data.db and subscribes to profile
            // changes. The running app has already paid that (MainWindow constructs the service in
            // a field initializer); what the laziness buys is that an import carrying only
            // profile-scoped values touches nothing but the store it was handed.
            SettingsService? settingsService = null;
            SettingsService Settings() => settingsService ??= SettingsService.Instance;

            // The one place a profile-scoped value is persisted, counted and guarded, so the
            // clamped arms below cannot drift apart in bounds, format or error handling.
            async Task WriteProfileValue(string name, string key, string value)
            {
                try
                {
                    await userDataDb.SetProfileSettingAsync(ProfileService.PvpProfileId, key, value);
                    profileWrittenTo = ProfileService.PvpProfileId;
                    count++;
                }
                catch (Exception ex)
                {
                    failedNames.Add(name);
                    _log.Error($"app_settings.json import failed for {name} ({key}): {ex.Message}");
                }
            }

            // Global values persist through the property setters, which swallow store failures
            // themselves; the guard here is for the rest of the setter, the lazy service
            // construction above included.
            void WriteGlobalValue(string name, Action write)
            {
                try
                {
                    write();
                    count++;
                }
                catch (Exception ex)
                {
                    failedNames.Add(name);
                    _log.Error($"app_settings.json import failed for {name}: {ex.Message}");
                }
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Null)
                    continue;

                switch (prop.Name)
                {
                    case "playerLevel":
                        if (prop.Value.TryGetInt32(out var level))
                        {
                            await WriteProfileValue(
                                prop.Name, SettingsService.KeyPlayerLevel,
                                LegacyAppSettingsValues.PlayerLevel(level));
                        }
                        break;

                    case "scavRep":
                        if (prop.Value.TryGetDouble(out var scavRep))
                        {
                            await WriteProfileValue(
                                prop.Name, SettingsService.KeyScavRep,
                                LegacyAppSettingsValues.ScavRep(scavRep));
                        }
                        break;

                    case "dspDecodeCount":
                        if (prop.Value.TryGetInt32(out var dspCount))
                        {
                            await WriteProfileValue(
                                prop.Name, SettingsService.KeyDspDecodeCount,
                                LegacyAppSettingsValues.DspDecodeCount(dspCount));
                        }
                        break;

                    case "showLevelLockedQuests":
                        if (TryGetBoolean(prop.Value, out var showLevelLockedQuests))
                        {
                            await WriteProfileValue(
                                prop.Name, SettingsService.KeyShowLevelLockedQuests,
                                LegacyAppSettingsValues.ShowLevelLockedQuests(showLevelLockedQuests));
                        }
                        break;

                    case "playerFaction":
                        // Dropping this value entirely, as this reader used to, left the faction
                        // unset, and ShouldIncludeTask then admits BOTH factions' quests.
                        var faction = LegacyAppSettingsValues.PlayerFaction(AsString(prop.Value));
                        if (faction != null)
                        {
                            await WriteProfileValue(
                                prop.Name, SettingsService.KeyPlayerFaction, faction);
                        }
                        break;

                    case "logFolderPath":
                        var logPath = AsString(prop.Value);
                        if (!string.IsNullOrEmpty(logPath))
                        {
                            WriteGlobalValue(prop.Name, () => Settings().LogFolderPath = logPath);
                        }
                        break;

                    case "baseFontSize":
                        if (prop.Value.TryGetDouble(out var fontSize))
                        {
                            WriteGlobalValue(prop.Name, () => Settings().BaseFontSize = fontSize);
                        }
                        break;

                    case "syncDaysRange":
                        if (prop.Value.TryGetInt32(out var syncDaysRange))
                        {
                            WriteGlobalValue(prop.Name, () => Settings().SyncDaysRange = syncDaysRange);
                        }
                        break;

                    case "hideWipeWarning":
                        if (TryGetBoolean(prop.Value, out var hideWipeWarning))
                        {
                            WriteGlobalValue(prop.Name, () => Settings().HideWipeWarning = hideWipeWarning);
                        }
                        break;
                }
            }

            return (count, profileWrittenTo, DescribeFailures(failedNames, null));
        }
        catch (Exception ex)
        {
            // Reading and parsing the file are what can still land here; every write above guards
            // itself. The count and the partition are returned as they stand rather than as zero
            // and null, so a throw can never understate an import that is already durable.
            return (count, profileWrittenTo, DescribeFailures(failedNames, $"Settings migration error: {ex.Message}"));
        }
    }

    /// <summary>
    /// The JSON value as a string, or null when it is not one. <c>JsonElement.GetString()</c>
    /// throws on any other kind, and one badly typed value used to abort the whole settings
    /// import and report zero for the values already written.
    /// </summary>
    private static string? AsString(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    /// <summary>
    /// A legacy flag as a bool. Accepts a JSON boolean and the 0/1 an older writer could store,
    /// which is the pair <c>hideWipeWarning</c> has always accepted here.
    /// </summary>
    private static bool TryGetBoolean(JsonElement element, out bool value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                value = false;
                return true;
            case JsonValueKind.Number when element.TryGetInt32(out var number):
                value = number == 1;
                return true;
            default:
                value = false;
                return false;
        }
    }

    /// <summary>
    /// The warning the settings import reports: <paramref name="error"/> on its own when every
    /// write landed, otherwise the failed values named alongside it.
    /// </summary>
    private static string? DescribeFailures(IReadOnlyList<string> failedNames, string? error)
    {
        if (failedNames.Count == 0)
            return error;

        var note = $"Settings migration could not import: {string.Join(", ", failedNames)}";
        return error == null ? note : $"{error}. {note}";
    }
}
