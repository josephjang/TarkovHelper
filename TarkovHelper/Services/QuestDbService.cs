using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// SQLite DB에서 퀘스트 데이터를 로드하는 서비스.
/// tarkov_data.db의 Quests, QuestRequirements, QuestObjectives, QuestRequiredItems 테이블 사용.
/// </summary>
public sealed class QuestDbService
{
    private static readonly ILogger _log = Log.For<QuestDbService>();
    private static QuestDbService? _instance;
    public static QuestDbService Instance => _instance ??= new QuestDbService();

    private readonly string _databasePath;
    private List<TarkovTask> _allQuests = new();
    private Dictionary<string, TarkovTask> _questsById = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, TarkovTask> _questsByNormalizedName = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoaded;

    public bool IsLoaded => _isLoaded;
    public int QuestCount => _allQuests.Count;

    /// <summary>
    /// 데이터가 새로고침되었을 때 발생하는 이벤트.
    /// UI 페이지들은 이 이벤트를 구독하여 화면을 갱신해야 함.
    /// </summary>
    public event EventHandler? DataRefreshed;

    private QuestDbService()
    {
        _databasePath = DatabaseUpdateService.Instance.DatabasePath;

        // 데이터베이스 업데이트 이벤트 구독
        DatabaseUpdateService.Instance.DatabaseUpdated += OnDatabaseUpdated;
    }

    /// <summary>
    /// 데이터베이스 업데이트 시 데이터 리로드
    /// </summary>
    private async void OnDatabaseUpdated(object? sender, EventArgs e)
    {
        _log.Info("Database updated, reloading data...");
        await RefreshAsync();
    }

    /// <summary>
    /// DB가 존재하는지 확인
    /// </summary>
    public bool DatabaseExists => File.Exists(_databasePath);

    /// <summary>
    /// 모든 퀘스트 반환
    /// </summary>
    public IReadOnlyList<TarkovTask> AllQuests => _allQuests;

    /// <summary>
    /// ID로 퀘스트 조회
    /// </summary>
    public TarkovTask? GetQuestById(string id)
    {
        return _questsById.TryGetValue(id, out var quest) ? quest : null;
    }

    /// <summary>
    /// NormalizedName으로 퀘스트 조회
    /// </summary>
    public TarkovTask? GetQuestByNormalizedName(string normalizedName)
    {
        return _questsByNormalizedName.TryGetValue(normalizedName, out var quest) ? quest : null;
    }

    /// <summary>
    /// DB에서 모든 퀘스트를 로드합니다.
    /// </summary>
    public async Task<bool> LoadQuestsAsync()
    {
        if (!DatabaseExists)
        {
            _log.Warning($"Database not found: {_databasePath}");
            return false;
        }

        try
        {
            var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            // Quests 테이블 존재 여부 확인
            if (!await TableExistsAsync(connection, "Quests"))
            {
                _log.Warning("Quests table not found");
                return false;
            }

            // 1. 기본 퀘스트 정보 로드
            var quests = await LoadBaseQuestsAsync(connection);
            var questLookup = quests.ToDictionary(q => q.Ids?.FirstOrDefault() ?? "", q => q, StringComparer.OrdinalIgnoreCase);

            // 2. 선행 퀘스트 요구사항 로드
            await LoadQuestRequirementsAsync(connection, questLookup);

            // 3. 퀘스트 목표 로드
            await LoadQuestObjectivesAsync(connection, questLookup);

            // 4. 필요 아이템 로드
            await LoadQuestRequiredItemsAsync(connection, questLookup);

            // 5. 대체 퀘스트 로드
            await LoadOptionalQuestsAsync(connection, questLookup);

            // 6. LeadsTo 역참조 구축
            BuildLeadsToReferences(quests);

            // 새 딕셔너리 빌드 (기존 데이터 유지하면서)
            var newQuestsById = new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase);
            var newQuestsByNormalizedName = new Dictionary<string, TarkovTask>(StringComparer.OrdinalIgnoreCase);

            foreach (var quest in quests)
            {
                var id = quest.Ids?.FirstOrDefault();
                if (!string.IsNullOrEmpty(id))
                {
                    newQuestsById[id] = quest;
                }
                if (!string.IsNullOrEmpty(quest.NormalizedName))
                {
                    newQuestsByNormalizedName[quest.NormalizedName] = quest;
                }
            }

            // Atomic swap - 모든 데이터가 준비된 후 한 번에 교체
            _allQuests = quests;
            _questsById = newQuestsById;
            _questsByNormalizedName = newQuestsByNormalizedName;
            _isLoaded = true;
            _log.Info($"Loaded {quests.Count} quests from DB");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Error loading quests: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        var sql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", tableName);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return count > 0;
    }

    /// <summary>
    /// 컬럼이 존재하는지 확인
    /// </summary>
    private async Task<bool> ColumnExistsAsync(SqliteConnection connection, string tableName, string columnName)
    {
        var sql = $"PRAGMA table_info({tableName})";
        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var name = reader.GetString(1); // column name is at index 1
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 기본 퀘스트 정보 로드
    /// </summary>
    private async Task<List<TarkovTask>> LoadBaseQuestsAsync(SqliteConnection connection)
    {
        var quests = new List<TarkovTask>();

        // 동적으로 존재하는 컬럼 확인
        var hasNormalizedName = await ColumnExistsAsync(connection, "Quests", "NormalizedName");
        var hasBsgId = await ColumnExistsAsync(connection, "Quests", "BsgId");
        var hasRequiredEdition = await ColumnExistsAsync(connection, "Quests", "RequiredEdition");
        var hasExcludedEdition = await ColumnExistsAsync(connection, "Quests", "ExcludedEdition");
        var hasRequiredPrestigeLevel = await ColumnExistsAsync(connection, "Quests", "RequiredPrestigeLevel");
        var hasRequiredDecodeCount = await ColumnExistsAsync(connection, "Quests", "RequiredDecodeCount");
        var hasWikiPageLink = await ColumnExistsAsync(connection, "Quests", "WikiPageLink");
        _log.Debug($"BsgId column exists: {hasBsgId}");

        // NormalizedName이 없으면 Name에서 생성.
        // This expression is the normalized-name rule. GenerateNormalizedName below is its C#
        // twin, applied when the column exists but a row's value is NULL; change one and you
        // must change the other, or two builds will key the same quest's progress differently.
        var normalizedNameExpr = hasNormalizedName
            ? "NormalizedName"
            : "LOWER(REPLACE(REPLACE(REPLACE(Name, ' ', '-'), '''', ''), '.', ''))";

        var sql = $@"
            SELECT
                Id,
                {(hasBsgId ? "BsgId" : "NULL")} as BsgId,
                Name, NameKO, NameJA,
                Trader, Location, MinLevel, MinScavKarma,
                KappaRequired, Faction,
                {normalizedNameExpr} as NormalizedName,
                {(hasRequiredEdition ? "RequiredEdition" : "NULL")} as RequiredEdition,
                {(hasExcludedEdition ? "ExcludedEdition" : "NULL")} as ExcludedEdition,
                {(hasRequiredPrestigeLevel ? "RequiredPrestigeLevel" : "NULL")} as RequiredPrestigeLevel,
                {(hasRequiredDecodeCount ? "RequiredDecodeCount" : "NULL")} as RequiredDecodeCount,
                {(hasWikiPageLink ? "WikiPageLink" : "NULL")} as WikiPageLink
            FROM Quests
            ORDER BY Name";

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var bsgId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var name = reader.IsDBNull(2) ? "" : reader.GetString(2);

            // NormalizedName이 NULL이면 Name에서 생성 (있으면 안 되는 상황이므로 경고 로그)
            // The stored column is what recorded progress is keyed by, so a row that has none is
            // an anomaly worth seeing in the log: the value derived here is only as good as the
            // name, and a name the publisher changed derives a key the user's progress is not
            // filed under. GenerateNormalizedName reproduces normalizedNameExpr exactly, so the
            // derived value at least agrees with what every other build computes.
            string normalizedName;
            if (reader.IsDBNull(11))
            {
                normalizedName = GenerateNormalizedName(name);
                _log.Warning($"Quest '{name}' ({id}) has no NormalizedName; derived '{normalizedName}' from its name");
            }
            else
            {
                normalizedName = reader.GetString(11);
            }

            var quest = new TarkovTask
            {
                Ids = new List<string> { id },
                Name = name,
                NameKo = reader.IsDBNull(3) ? null : reader.GetString(3),
                NameJa = reader.IsDBNull(4) ? null : reader.GetString(4),
                Trader = reader.IsDBNull(5) ? "" : reader.GetString(5),
                Maps = reader.IsDBNull(6) ? null : ParseMaps(reader.GetString(6)),
                RequiredLevel = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                RequiredScavKarma = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                ReqKappa = !reader.IsDBNull(9) && reader.GetInt32(9) == 1,
                Faction = reader.IsDBNull(10) ? null : reader.GetString(10),
                NormalizedName = normalizedName,
                RequiredEdition = reader.IsDBNull(12) ? null : reader.GetString(12),
                ExcludedEdition = reader.IsDBNull(13) ? null : reader.GetString(13),
                RequiredPrestigeLevel = reader.IsDBNull(14) ? null : reader.GetInt32(14),
                RequiredDecodeCount = reader.IsDBNull(15) ? null : reader.GetInt32(15),
                WikiPageLink = reader.IsDBNull(16) ? null : reader.GetString(16)
            };

            // BsgId가 있으면 Ids에 추가
            if (!string.IsNullOrEmpty(bsgId) && bsgId != id)
            {
                quest.Ids.Add(bsgId);
            }

            quests.Add(quest);
        }

        // BsgId 통계 출력
        var questsWithBsgId = quests.Count(q => q.Ids != null && q.Ids.Count > 1);
        _log.Debug($"Quests with BsgId: {questsWithBsgId}/{quests.Count}");
        if (quests.Count > 0 && quests[0].Ids != null)
        {
            _log.Debug($"Sample quest IDs: {string.Join(", ", quests[0].Ids ?? [])} - {quests[0].Name}");
        }

        return quests;
    }

    /// <summary>
    /// Location 문자열을 맵 리스트로 파싱
    /// </summary>
    private List<string>? ParseMaps(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        // "any" 또는 복수 맵 처리
        if (location.Equals("any", StringComparison.OrdinalIgnoreCase))
            return null;

        // 쉼표로 구분된 경우 처리
        if (location.Contains(','))
        {
            return location.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(m => m.Trim().ToLowerInvariant())
                .ToList();
        }

        return new List<string> { location.ToLowerInvariant() };
    }

    /// <summary>
    /// Name에서 NormalizedName 생성.
    /// <para>
    /// The C# twin of <c>normalizedNameExpr</c> in <see cref="LoadBaseQuestsAsync"/>:
    /// <c>LOWER(REPLACE(REPLACE(REPLACE(Name, ' ', '-'), '''', ''), '.', ''))</c>. There is one
    /// normalized-name rule and this must be it, because recorded progress
    /// (<c>QuestProgress.NormalizedName</c>) is keyed by whatever the rule produces: a spelling
    /// only this method computes would file a quest's progress under a name no other component
    /// looks it up by, and nothing would report an error.
    /// </para>
    /// <para>
    /// So: spaces become dashes, the ASCII apostrophe (U+0027) and the period are dropped, and
    /// A-Z is lowered. Only A-Z, because that is all SQLite's <c>LOWER</c> does here (the
    /// bundled e_sqlite3 is built without ICU, so it leaves every non-ASCII letter alone), and
    /// nothing else is dropped, because SQLite's <c>REPLACE</c> chain drops nothing else. The
    /// typographic apostrophe U+2019 in "What's on the Flash Drive?" survives for that reason,
    /// as do the comma, question mark, colon and quote this used to strip.
    /// </para>
    /// <para>
    /// TarkovDBEditor writes the stored column from its own copy of this rule
    /// (<c>QuestNormalizedName.SqlForm</c>); the two cannot share code because the editor
    /// depends on nothing in this project. TarkovHelper.Tests pins all three spellings - this
    /// one, the editor's, and the SQL itself evaluated by SQLite - against each other.
    /// </para>
    /// </summary>
    public static string GenerateNormalizedName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "";

        var result = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            switch (c)
            {
                case ' ':
                    result.Append('-');
                    break;
                case '\'':
                case '.':
                    break;
                default:
                    result.Append(c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c);
                    break;
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// 선행 퀘스트 요구사항 로드
    /// </summary>
    private async Task<bool> LoadQuestRequirementsAsync(SqliteConnection connection, Dictionary<string, TarkovTask> questLookup)
    {
        if (!await TableExistsAsync(connection, "QuestRequirements"))
            return false;

        // A second requirement type the same row is also satisfied by ("complete or failed" is
        // the case it exists for). Feature-detected: a database published before the column
        // exists reads exactly as it always did.
        var hasAltRequirementType =
            await ColumnExistsAsync(connection, "QuestRequirements", "AltRequirementType");

        var sql = $@"
            SELECT QuestId, RequiredQuestId, RequirementType, GroupId{(hasAltRequirementType ? ", AltRequirementType" : "")}
            FROM QuestRequirements
            ORDER BY QuestId, GroupId";

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var questId = reader.GetString(0);
            var requiredQuestId = reader.GetString(1);
            var requirementType = reader.IsDBNull(2) ? "Complete" : reader.GetString(2);
            var groupId = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            var altRequirementType = hasAltRequirementType && !reader.IsDBNull(4) ? reader.GetString(4) : null;

            if (!questLookup.TryGetValue(questId, out var quest))
                continue;

            // 선행 퀘스트의 NormalizedName 찾기
            if (!questLookup.TryGetValue(requiredQuestId, out var requiredQuest))
                continue;

            var requiredNormalizedName = requiredQuest.NormalizedName;
            if (string.IsNullOrEmpty(requiredNormalizedName))
                continue;

            // Previous 리스트에 추가
            quest.Previous ??= new List<string>();
            if (!quest.Previous.Contains(requiredNormalizedName, StringComparer.OrdinalIgnoreCase))
            {
                quest.Previous.Add(requiredNormalizedName);
            }

            // Both type columns become entries in one Status list: QuestProgressService's
            // IsStatusSatisfied is satisfied when ANY entry matches, which is exactly what a row
            // naming two types means.
            var statuses = new List<string> { requirementType.ToLowerInvariant() };
            if (!string.IsNullOrEmpty(altRequirementType))
                statuses.Add(altRequirementType.ToLowerInvariant());

            // TaskRequirements에 상세 정보 추가 (GroupId 포함)
            // One row per prerequisite: a second row naming the same one is dropped, GroupId and
            // all. The publisher enforces the same rule (RefreshGuards.AssertPublishConstraints
            // refuses a quest with two rows for one prerequisite), because a row this drops would
            // be a row every installed build silently ignores.
            quest.TaskRequirements ??= new List<TaskRequirement>();
            var existing = quest.TaskRequirements.FirstOrDefault(r =>
                r.TaskId.Equals(requiredQuestId, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                quest.TaskRequirements.Add(new TaskRequirement
                {
                    TaskId = requiredQuestId,
                    TaskNormalizedName = requiredNormalizedName ?? "",
                    Status = statuses,
                    GroupId = groupId
                });
            }
        }

        return true;
    }

    /// <summary>
    /// 퀘스트 목표 로드
    /// </summary>
    private async Task<bool> LoadQuestObjectivesAsync(SqliteConnection connection, Dictionary<string, TarkovTask> questLookup)
    {
        if (!await TableExistsAsync(connection, "QuestObjectives"))
            return false;

        var sql = @"
            SELECT QuestId, Description
            FROM QuestObjectives
            ORDER BY QuestId, SortOrder";

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var questId = reader.GetString(0);
            var description = reader.IsDBNull(1) ? "" : reader.GetString(1);

            if (!questLookup.TryGetValue(questId, out var quest))
                continue;

            if (string.IsNullOrWhiteSpace(description))
                continue;

            quest.Objectives ??= new List<string>();
            quest.Objectives.Add(description);
        }

        return true;
    }

    /// <summary>
    /// 필요 아이템 로드
    /// </summary>
    private async Task<bool> LoadQuestRequiredItemsAsync(SqliteConnection connection, Dictionary<string, TarkovTask> questLookup)
    {
        if (!await TableExistsAsync(connection, "QuestRequiredItems"))
            return false;

        var sql = @"
            SELECT QuestId, ItemId, ItemName, Count, RequiresFIR, RequirementType, DogtagMinLevel
            FROM QuestRequiredItems
            ORDER BY QuestId, SortOrder";

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var questId = reader.GetString(0);
            var itemId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var itemName = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var count = reader.IsDBNull(3) ? 1 : reader.GetInt32(3);
            var requiresFir = !reader.IsDBNull(4) && reader.GetInt32(4) == 1;
            var requirementType = reader.IsDBNull(5) ? "Required" : reader.GetString(5);
            var dogtagMinLevel = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);

            if (!questLookup.TryGetValue(questId, out var quest))
                continue;

            // ItemId가 NULL이면 Items 테이블과 매칭할 수 없으므로 스킵
            // Items 탭에서는 QuestRequiredItems.ItemId -> Items.Id로 직접 매칭
            if (string.IsNullOrEmpty(itemId))
                continue;

            quest.RequiredItems ??= new List<QuestItem>();
            quest.RequiredItems.Add(new QuestItem
            {
                ItemNormalizedName = itemId,  // tarkov.dev API ID (matches Items.Id)
                ItemDisplayName = itemName,   // Original item name for display fallback
                Amount = count,
                FoundInRaid = requiresFir,
                Requirement = requirementType,
                DogtagMinLevel = dogtagMinLevel
            });
        }

        return true;
    }

    /// <summary>
    /// 대체 퀘스트 로드
    /// </summary>
    private async Task<bool> LoadOptionalQuestsAsync(SqliteConnection connection, Dictionary<string, TarkovTask> questLookup)
    {
        if (!await TableExistsAsync(connection, "OptionalQuests"))
            return false;

        var sql = @"
            SELECT QuestId, AlternativeQuestId
            FROM OptionalQuests";

        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var questId = reader.GetString(0);
            var alternativeQuestId = reader.GetString(1);

            if (!questLookup.TryGetValue(questId, out var quest))
                continue;

            if (!questLookup.TryGetValue(alternativeQuestId, out var altQuest))
                continue;

            var altNormalizedName = altQuest.NormalizedName;
            if (string.IsNullOrEmpty(altNormalizedName))
                continue;

            quest.AlternativeQuests ??= new List<string>();
            if (!quest.AlternativeQuests.Contains(altNormalizedName, StringComparer.OrdinalIgnoreCase))
            {
                quest.AlternativeQuests.Add(altNormalizedName);
            }
        }

        return true;
    }

    /// <summary>
    /// LeadsTo 역참조 구축 (Previous의 역방향)
    /// </summary>
    private void BuildLeadsToReferences(List<TarkovTask> quests)
    {
        var questByName = quests
            .Where(q => !string.IsNullOrEmpty(q.NormalizedName))
            .ToDictionary(q => q.NormalizedName!, q => q, StringComparer.OrdinalIgnoreCase);

        foreach (var quest in quests)
        {
            if (quest.Previous == null || string.IsNullOrEmpty(quest.NormalizedName))
                continue;

            foreach (var prevName in quest.Previous)
            {
                if (questByName.TryGetValue(prevName, out var prevQuest))
                {
                    prevQuest.LeadsTo ??= new List<string>();
                    if (!prevQuest.LeadsTo.Contains(quest.NormalizedName, StringComparer.OrdinalIgnoreCase))
                    {
                        prevQuest.LeadsTo.Add(quest.NormalizedName);
                    }
                }
            }
        }
    }

    /// <summary>
    /// 데이터 새로고침 (기존 데이터를 유지하면서 새 데이터로 atomic swap)
    /// </summary>
    public async Task RefreshAsync()
    {
        _log.Debug("Refreshing quest data...");
        // 기존 데이터를 클리어하지 않음 - LoadQuestsAsync()에서 atomic swap으로 교체
        await LoadQuestsAsync();

        // 데이터 새로고침 완료 이벤트 발생
        OnDataRefreshed();
    }

    /// <summary>
    /// 데이터 새로고침 이벤트 발생
    /// </summary>
    private void OnDataRefreshed()
    {
        // UI 스레드에서 이벤트 발생
        if (System.Windows.Application.Current?.Dispatcher != null)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                DataRefreshed?.Invoke(this, EventArgs.Empty);
            });
        }
        else
        {
            DataRefreshed?.Invoke(this, EventArgs.Empty);
        }
    }
}
