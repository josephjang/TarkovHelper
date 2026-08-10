using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Debug;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// 사용자 데이터를 SQLite DB (user_data.db)에 저장/로드하는 서비스.
/// 퀘스트 진행, 목표 완료, 하이드아웃 진행, 아이템 인벤토리 등을 관리합니다.
/// </summary>
public sealed class UserDataDbService : IQuestProgressStore
{
    private static readonly Lazy<UserDataDbService> _instance = new(() => new UserDataDbService());
    public static UserDataDbService Instance => _instance.Value;

    private readonly string _databasePath;
    private bool _isInitialized;

    public bool IsInitialized => _isInitialized;
    public string DatabasePath => _databasePath;

    /// <summary>
    /// 마이그레이션 진행 상황 이벤트
    /// </summary>
    public event Action<string>? MigrationProgress;

    /// <summary>
    /// 마이그레이션이 필요한지 확인
    /// </summary>
    public bool NeedsMigration()
    {
        var v2Path = Path.Combine(AppEnv.ConfigPath, "quest_progress_v2.json");
        var v1Path = Path.Combine(AppEnv.ConfigPath, "quest_progress.json");
        var objPath = Path.Combine(AppEnv.ConfigPath, "objective_progress.json");
        var hideoutPath = Path.Combine(AppEnv.ConfigPath, "hideout_progress.json");
        var inventoryPath = Path.Combine(AppEnv.ConfigPath, "item_inventory.json");

        return File.Exists(v2Path) || File.Exists(v1Path) || File.Exists(objPath) ||
               File.Exists(hideoutPath) || File.Exists(inventoryPath);
    }

    private void ReportProgress(string message)
    {
        MigrationProgress?.Invoke(message);
        System.Diagnostics.Debug.WriteLine($"[UserDataDbService] {message}");
    }

    private UserDataDbService()
    {
        _databasePath = Path.Combine(AppEnv.ConfigPath, "user_data.db");
    }

    /// <summary>
    /// DB 초기화 (테이블 생성)
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_isInitialized) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

            var connectionString = $"Data Source={_databasePath}";
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            await CreateTablesAsync(connection);

            _isInitialized = true;
            System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Initialized: {_databasePath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Initialization failed: {ex.Message}");
            throw;
        }
    }

    private async Task CreateTablesAsync(SqliteConnection connection)
    {
        await MigrateToProfileSchemaAsync(connection);

        var createTablesSql = @"
            -- 퀘스트 진행 상태
            CREATE TABLE IF NOT EXISTS QuestProgress (
                ProfileId TEXT NOT NULL DEFAULT 'pvp',
                Id TEXT NOT NULL,
                NormalizedName TEXT,
                Status TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (ProfileId, Id)
            );

            -- 퀘스트 목표 진행 상태
            CREATE TABLE IF NOT EXISTS ObjectiveProgress (
                ProfileId TEXT NOT NULL DEFAULT 'pvp',
                Id TEXT NOT NULL,
                QuestId TEXT,
                IsCompleted INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (ProfileId, Id)
            );

            -- 아이템 인벤토리
            CREATE TABLE IF NOT EXISTS ItemInventory (
                ProfileId TEXT NOT NULL DEFAULT 'pvp',
                ItemNormalizedName TEXT NOT NULL,
                FirQuantity INTEGER NOT NULL DEFAULT 0,
                NonFirQuantity INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (ProfileId, ItemNormalizedName)
            );

            -- 하이드아웃 진행
            CREATE TABLE IF NOT EXISTS HideoutProgress (
                ProfileId TEXT NOT NULL DEFAULT 'pvp',
                StationId TEXT NOT NULL,
                Level INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (ProfileId, StationId)
            );

            -- 사용자 설정 (전역)
            CREATE TABLE IF NOT EXISTS UserSettings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            -- 프로필별 설정
            CREATE TABLE IF NOT EXISTS ProfileSettings (
                ProfileId TEXT NOT NULL,
                Key TEXT NOT NULL,
                Value TEXT NOT NULL,
                PRIMARY KEY (ProfileId, Key)
            );

            -- 레이드 히스토리
            CREATE TABLE IF NOT EXISTS RaidHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RaidId TEXT,
                SessionId TEXT,
                ShortId TEXT,
                ProfileId TEXT,
                RaidType INTEGER NOT NULL DEFAULT 0,
                GameMode INTEGER NOT NULL DEFAULT 0,
                MapName TEXT,
                MapKey TEXT,
                ServerIp TEXT,
                ServerPort INTEGER,
                IsParty INTEGER NOT NULL DEFAULT 0,
                PartyLeaderAccountId TEXT,
                StartTime TEXT,
                EndTime TEXT,
                DurationSeconds INTEGER,
                Rtt REAL,
                PacketLoss REAL,
                PacketsSent INTEGER,
                PacketsReceived INTEGER,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            -- 인덱스
            CREATE INDEX IF NOT EXISTS idx_quest_progress_profile ON QuestProgress(ProfileId);
            CREATE INDEX IF NOT EXISTS idx_quest_progress_normalized ON QuestProgress(NormalizedName);
            CREATE INDEX IF NOT EXISTS idx_objective_progress_profile ON ObjectiveProgress(ProfileId);
            CREATE INDEX IF NOT EXISTS idx_objective_progress_quest ON ObjectiveProgress(QuestId);
            CREATE INDEX IF NOT EXISTS idx_hideout_progress_profile ON HideoutProgress(ProfileId);
            CREATE INDEX IF NOT EXISTS idx_item_inventory_profile ON ItemInventory(ProfileId);
            CREATE INDEX IF NOT EXISTS idx_raid_history_start_time ON RaidHistory(StartTime);
            CREATE INDEX IF NOT EXISTS idx_raid_history_map_key ON RaidHistory(MapKey);
            CREATE INDEX IF NOT EXISTS idx_raid_history_raid_type ON RaidHistory(RaidType);
        ";

        await using var cmd = new SqliteCommand(createTablesSql, connection);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// ProfileId 복합 기본 키 스키마로 마이그레이션 (기존 단일 PK 스키마에서 업그레이드)
    /// </summary>
    private async Task MigrateToProfileSchemaAsync(SqliteConnection connection)
    {
        try
        {
            // QuestProgress 테이블이 존재하는지 확인
            var checkTableSql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='QuestProgress'";
            await using var checkTableCmd = new SqliteCommand(checkTableSql, connection);
            var tableExists = Convert.ToInt32(await checkTableCmd.ExecuteScalarAsync()) > 0;

            if (!tableExists) return; // 신규 설치: 마이그레이션 불필요

            // ProfileId 컬럼이 이미 있으면 마이그레이션 완료된 상태
            var checkColSql = "SELECT COUNT(*) FROM pragma_table_info('QuestProgress') WHERE name='ProfileId'";
            await using var checkColCmd = new SqliteCommand(checkColSql, connection);
            var hasProfileId = Convert.ToInt32(await checkColCmd.ExecuteScalarAsync()) > 0;

            if (hasProfileId) return; // 이미 마이그레이션됨

            System.Diagnostics.Debug.WriteLine("[UserDataDbService] Migrating to profile schema...");

            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                var migrateSql = @"
                    -- QuestProgress
                    ALTER TABLE QuestProgress RENAME TO QuestProgress_old;
                    CREATE TABLE QuestProgress (
                        ProfileId TEXT NOT NULL DEFAULT 'pvp',
                        Id TEXT NOT NULL,
                        NormalizedName TEXT,
                        Status TEXT NOT NULL,
                        UpdatedAt TEXT NOT NULL,
                        PRIMARY KEY (ProfileId, Id)
                    );
                    INSERT INTO QuestProgress (ProfileId, Id, NormalizedName, Status, UpdatedAt)
                        SELECT 'pvp', Id, NormalizedName, Status, UpdatedAt FROM QuestProgress_old;
                    DROP TABLE QuestProgress_old;

                    -- ObjectiveProgress
                    ALTER TABLE ObjectiveProgress RENAME TO ObjectiveProgress_old;
                    CREATE TABLE ObjectiveProgress (
                        ProfileId TEXT NOT NULL DEFAULT 'pvp',
                        Id TEXT NOT NULL,
                        QuestId TEXT,
                        IsCompleted INTEGER NOT NULL DEFAULT 0,
                        UpdatedAt TEXT NOT NULL,
                        PRIMARY KEY (ProfileId, Id)
                    );
                    INSERT INTO ObjectiveProgress (ProfileId, Id, QuestId, IsCompleted, UpdatedAt)
                        SELECT 'pvp', Id, QuestId, IsCompleted, UpdatedAt FROM ObjectiveProgress_old;
                    DROP TABLE ObjectiveProgress_old;

                    -- HideoutProgress
                    ALTER TABLE HideoutProgress RENAME TO HideoutProgress_old;
                    CREATE TABLE HideoutProgress (
                        ProfileId TEXT NOT NULL DEFAULT 'pvp',
                        StationId TEXT NOT NULL,
                        Level INTEGER NOT NULL DEFAULT 0,
                        UpdatedAt TEXT NOT NULL,
                        PRIMARY KEY (ProfileId, StationId)
                    );
                    INSERT INTO HideoutProgress (ProfileId, StationId, Level, UpdatedAt)
                        SELECT 'pvp', StationId, Level, UpdatedAt FROM HideoutProgress_old;
                    DROP TABLE HideoutProgress_old;

                    -- ItemInventory (covers any old schema variant)
                    ALTER TABLE ItemInventory RENAME TO ItemInventory_old;
                    CREATE TABLE ItemInventory (
                        ProfileId TEXT NOT NULL DEFAULT 'pvp',
                        ItemNormalizedName TEXT NOT NULL,
                        FirQuantity INTEGER NOT NULL DEFAULT 0,
                        NonFirQuantity INTEGER NOT NULL DEFAULT 0,
                        UpdatedAt TEXT NOT NULL,
                        PRIMARY KEY (ProfileId, ItemNormalizedName)
                    );
                    INSERT OR IGNORE INTO ItemInventory (ProfileId, ItemNormalizedName, FirQuantity, NonFirQuantity, UpdatedAt)
                        SELECT 'pvp', ItemNormalizedName, FirQuantity, NonFirQuantity, UpdatedAt FROM ItemInventory_old
                        WHERE ItemNormalizedName IS NOT NULL;
                    DROP TABLE ItemInventory_old;
                ";

                await using var migrateCmd = new SqliteCommand(migrateSql, connection, (SqliteTransaction)transaction);
                await migrateCmd.ExecuteNonQueryAsync();

                await transaction.CommitAsync();
                System.Diagnostics.Debug.WriteLine("[UserDataDbService] Profile schema migration completed");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Profile schema migration failed: {ex.Message}");
                throw;
            }
        }
        catch (Exception ex) when (ex is not SqliteException { SqliteErrorCode: 1 })
        {
            System.Diagnostics.Debug.WriteLine($"[UserDataDbService] MigrateToProfileSchemaAsync error: {ex.Message}");
        }
    }

    #region Quest Progress

    /// <summary>
    /// 모든 퀘스트 진행 상태 로드
    /// </summary>
    public async Task<Dictionary<string, QuestStatus>> LoadQuestProgressAsync(string profileId)
    {
        await InitializeAsync();

        var result = new Dictionary<string, QuestStatus>(StringComparer.OrdinalIgnoreCase);

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "SELECT Id, NormalizedName, Status FROM QuestProgress WHERE ProfileId = @profileId";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var normalizedName = reader.IsDBNull(1) ? null : reader.GetString(1);
            var statusStr = reader.GetString(2);

            if (Enum.TryParse<QuestStatus>(statusStr, out var status))
            {
                var key = normalizedName ?? id;
                result[key] = status;
            }
        }

        return result;
    }

    /// <summary>
    /// 퀘스트 진행 상태 저장
    /// </summary>
    public async Task SaveQuestProgressAsync(string id, string? normalizedName, QuestStatus status, string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = @"
            INSERT INTO QuestProgress (ProfileId, Id, NormalizedName, Status, UpdatedAt)
            VALUES (@profileId, @id, @normalizedName, @status, @updatedAt)
            ON CONFLICT(ProfileId, Id) DO UPDATE SET
                NormalizedName = @normalizedName,
                Status = @status,
                UpdatedAt = @updatedAt";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@normalizedName", normalizedName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@status", status.ToString());
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 여러 퀘스트 진행 상태를 배치로 저장 (트랜잭션 사용)
    /// </summary>
    public async Task SaveQuestProgressBatchAsync(IEnumerable<(string Id, string? NormalizedName, QuestStatus Status)> progressItems, string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var sql = @"
                INSERT INTO QuestProgress (ProfileId, Id, NormalizedName, Status, UpdatedAt)
                VALUES (@profileId, @id, @normalizedName, @status, @updatedAt)
                ON CONFLICT(ProfileId, Id) DO UPDATE SET
                    NormalizedName = @normalizedName,
                    Status = @status,
                    UpdatedAt = @updatedAt";

            var updatedAt = DateTime.UtcNow.ToString("o");

            foreach (var item in progressItems)
            {
                await using var cmd = new SqliteCommand(sql, connection, (SqliteTransaction)transaction);
                cmd.Parameters.AddWithValue("@profileId", profileId);
                cmd.Parameters.AddWithValue("@id", item.Id);
                cmd.Parameters.AddWithValue("@normalizedName", item.NormalizedName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@status", item.Status.ToString());
                cmd.Parameters.AddWithValue("@updatedAt", updatedAt);
                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// 퀘스트 진행 상태 삭제 (리셋)
    /// </summary>
    public async Task DeleteQuestProgressAsync(string id, string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "DELETE FROM QuestProgress WHERE (Id = @id OR NormalizedName = @id) AND ProfileId = @profileId";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@profileId", profileId);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 모든 퀘스트 진행 상태 삭제
    /// </summary>
    public async Task ClearAllQuestProgressAsync(string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var cmd = new SqliteCommand("DELETE FROM QuestProgress WHERE ProfileId = @profileId", connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        await cmd.ExecuteNonQueryAsync();
    }

    #endregion

    #region Objective Progress

    /// <summary>
    /// 모든 목표 진행 상태 로드
    /// </summary>
    public async Task<Dictionary<string, bool>> LoadObjectiveProgressAsync(string profileId)
    {
        await InitializeAsync();

        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "SELECT Id, IsCompleted FROM ObjectiveProgress WHERE ProfileId = @profileId";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var id = reader.GetString(0);
            var isCompleted = reader.GetInt32(1) == 1;
            result[id] = isCompleted;
        }

        return result;
    }

    /// <summary>
    /// 목표 진행 상태 저장
    /// </summary>
    public async Task SaveObjectiveProgressAsync(string id, string? questId, bool isCompleted, string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = @"
            INSERT INTO ObjectiveProgress (ProfileId, Id, QuestId, IsCompleted, UpdatedAt)
            VALUES (@profileId, @id, @questId, @isCompleted, @updatedAt)
            ON CONFLICT(ProfileId, Id) DO UPDATE SET
                QuestId = @questId,
                IsCompleted = @isCompleted,
                UpdatedAt = @updatedAt";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@questId", questId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@isCompleted", isCompleted ? 1 : 0);
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 목표 진행 상태 삭제
    /// </summary>
    public async Task DeleteObjectiveProgressAsync(string id, string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "DELETE FROM ObjectiveProgress WHERE Id = @id AND ProfileId = @profileId";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@profileId", profileId);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 퀘스트의 모든 목표 진행 상태 삭제
    /// </summary>
    public async Task DeleteObjectiveProgressByQuestAsync(string questId, string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "DELETE FROM ObjectiveProgress WHERE (QuestId = @questId OR Id LIKE @pattern) AND ProfileId = @profileId";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@questId", questId);
        cmd.Parameters.AddWithValue("@pattern", $"{questId}:%");
        cmd.Parameters.AddWithValue("@profileId", profileId);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 모든 목표 진행 상태 삭제
    /// </summary>
    public async Task ClearAllObjectiveProgressAsync(string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var cmd = new SqliteCommand("DELETE FROM ObjectiveProgress WHERE ProfileId = @profileId", connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        await cmd.ExecuteNonQueryAsync();
    }

    #endregion

    #region Hideout Progress

    /// <summary>
    /// 모든 하이드아웃 진행 상태 로드
    /// </summary>
    public async Task<Dictionary<string, int>> LoadHideoutProgressAsync(string profileId)
    {
        await InitializeAsync();

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "SELECT StationId, Level FROM HideoutProgress WHERE ProfileId = @profileId";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var stationId = reader.GetString(0);
            var level = reader.GetInt32(1);
            result[stationId] = level;
        }

        return result;
    }

    /// <summary>
    /// 하이드아웃 진행 상태 저장
    /// </summary>
    public async Task SaveHideoutProgressAsync(string stationId, int level, string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        if (level == 0)
        {
            var deleteSql = "DELETE FROM HideoutProgress WHERE StationId = @stationId AND ProfileId = @profileId";
            await using var deleteCmd = new SqliteCommand(deleteSql, connection);
            deleteCmd.Parameters.AddWithValue("@stationId", stationId);
            deleteCmd.Parameters.AddWithValue("@profileId", profileId);
            await deleteCmd.ExecuteNonQueryAsync();
            return;
        }

        var sql = @"
            INSERT INTO HideoutProgress (ProfileId, StationId, Level, UpdatedAt)
            VALUES (@profileId, @stationId, @level, @updatedAt)
            ON CONFLICT(ProfileId, StationId) DO UPDATE SET
                Level = @level,
                UpdatedAt = @updatedAt";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@stationId", stationId);
        cmd.Parameters.AddWithValue("@level", level);
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 모든 하이드아웃 진행 상태 삭제
    /// </summary>
    public async Task ClearAllHideoutProgressAsync(string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var cmd = new SqliteCommand("DELETE FROM HideoutProgress WHERE ProfileId = @profileId", connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        await cmd.ExecuteNonQueryAsync();
    }

    #endregion

    #region Item Inventory

    /// <summary>
    /// 모든 아이템 인벤토리 로드
    /// </summary>
    public async Task<Dictionary<string, (int FirQuantity, int NonFirQuantity)>> LoadItemInventoryAsync(string profileId)
    {
        await InitializeAsync();

        var result = new Dictionary<string, (int FirQuantity, int NonFirQuantity)>(StringComparer.OrdinalIgnoreCase);

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "SELECT ItemNormalizedName, FirQuantity, NonFirQuantity FROM ItemInventory WHERE ProfileId = @profileId";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var itemName = reader.GetString(0);
            var firQty = reader.GetInt32(1);
            var nonFirQty = reader.GetInt32(2);
            result[itemName] = (firQty, nonFirQty);
        }

        return result;
    }

    /// <summary>
    /// 아이템 인벤토리 저장
    /// </summary>
    public async Task SaveItemInventoryAsync(string itemNormalizedName, int firQuantity, int nonFirQuantity, string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        if (firQuantity == 0 && nonFirQuantity == 0)
        {
            var deleteSql = "DELETE FROM ItemInventory WHERE ItemNormalizedName = @itemName AND ProfileId = @profileId";
            await using var deleteCmd = new SqliteCommand(deleteSql, connection);
            deleteCmd.Parameters.AddWithValue("@itemName", itemNormalizedName);
            deleteCmd.Parameters.AddWithValue("@profileId", profileId);
            await deleteCmd.ExecuteNonQueryAsync();
            return;
        }

        var sql = @"
            INSERT INTO ItemInventory (ProfileId, ItemNormalizedName, FirQuantity, NonFirQuantity, UpdatedAt)
            VALUES (@profileId, @itemName, @firQty, @nonFirQty, @updatedAt)
            ON CONFLICT(ProfileId, ItemNormalizedName) DO UPDATE SET
                FirQuantity = @firQty,
                NonFirQuantity = @nonFirQty,
                UpdatedAt = @updatedAt";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@itemName", itemNormalizedName);
        cmd.Parameters.AddWithValue("@firQty", firQuantity);
        cmd.Parameters.AddWithValue("@nonFirQty", nonFirQuantity);
        cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 모든 아이템 인벤토리 삭제
    /// </summary>
    public async Task ClearAllItemInventoryAsync(string profileId)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var cmd = new SqliteCommand("DELETE FROM ItemInventory WHERE ProfileId = @profileId", connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        await cmd.ExecuteNonQueryAsync();
    }

    #endregion

    #region JSON Migration

    /// <summary>
    /// 기존 JSON 파일들을 DB로 마이그레이션
    /// </summary>
    public async Task<bool> MigrateFromJsonAsync()
    {
        if (!NeedsMigration())
        {
            return false;
        }

        ReportProgress("데이터 마이그레이션을 시작합니다...");
        var migrated = false;

        ReportProgress("퀘스트 진행 데이터 마이그레이션 중...");
        migrated |= await MigrateQuestProgressJsonAsync();

        ReportProgress("목표 진행 데이터 마이그레이션 중...");
        migrated |= await MigrateObjectiveProgressJsonAsync();

        ReportProgress("하이드아웃 진행 데이터 마이그레이션 중...");
        migrated |= await MigrateHideoutProgressJsonAsync();

        ReportProgress("아이템 인벤토리 데이터 마이그레이션 중...");
        migrated |= await MigrateItemInventoryJsonAsync();

        if (migrated)
        {
            ReportProgress("데이터 마이그레이션 완료!");
        }

        return migrated;
    }

    private async Task<bool> MigrateQuestProgressJsonAsync()
    {
        var v2Path = Path.Combine(AppEnv.ConfigPath, "quest_progress_v2.json");
        var v1Path = Path.Combine(AppEnv.ConfigPath, "quest_progress.json");

        if (File.Exists(v2Path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(v2Path);
                var v2Data = JsonSerializer.Deserialize<QuestProgressDataV2>(json);

                if (v2Data != null)
                {
                    await InitializeAsync();

                    foreach (var entry in v2Data.CompletedQuests)
                    {
                        if (entry.IsValid)
                            await SaveQuestProgressAsync(entry.Id ?? entry.NormalizedName!, entry.NormalizedName, QuestStatus.Done, ProfileService.PvpProfileId);
                    }

                    foreach (var entry in v2Data.FailedQuests)
                    {
                        if (entry.IsValid)
                            await SaveQuestProgressAsync(entry.Id ?? entry.NormalizedName!, entry.NormalizedName, QuestStatus.Failed, ProfileService.PvpProfileId);
                    }

                    File.Delete(v2Path);
                    System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Migrated and deleted: {v2Path}");

                    if (File.Exists(v1Path))
                    {
                        File.Delete(v1Path);
                        System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Deleted legacy: {v1Path}");
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UserDataDbService] V2 migration failed: {ex.Message}");
            }
        }
        else if (File.Exists(v1Path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(v1Path);
                var v1Data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

                if (v1Data != null)
                {
                    await InitializeAsync();

                    foreach (var kvp in v1Data)
                    {
                        if (Enum.TryParse<QuestStatus>(kvp.Value, out var status))
                            await SaveQuestProgressAsync(kvp.Key, kvp.Key, status, ProfileService.PvpProfileId);
                    }

                    File.Delete(v1Path);
                    System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Migrated and deleted: {v1Path}");

                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[UserDataDbService] V1 migration failed: {ex.Message}");
            }
        }

        return false;
    }

    private async Task<bool> MigrateObjectiveProgressJsonAsync()
    {
        var filePath = Path.Combine(AppEnv.ConfigPath, "objective_progress.json");

        if (!File.Exists(filePath))
            return false;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var data = JsonSerializer.Deserialize<Dictionary<string, bool>>(json);

            if (data != null)
            {
                await InitializeAsync();

                foreach (var kvp in data)
                {
                    string? questId = null;
                    if (kvp.Key.Contains(':'))
                    {
                        var parts = kvp.Key.Split(':');
                        if (parts[0] != "id")
                            questId = parts[0];
                    }

                    await SaveObjectiveProgressAsync(kvp.Key, questId, kvp.Value, ProfileService.PvpProfileId);
                }

                File.Delete(filePath);
                System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Migrated and deleted: {filePath}");

                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Objective migration failed: {ex.Message}");
        }

        return false;
    }

    private async Task<bool> MigrateHideoutProgressJsonAsync()
    {
        var filePath = Path.Combine(AppEnv.ConfigPath, "hideout_progress.json");

        if (!File.Exists(filePath))
            return false;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            Dictionary<string, int>? modules = null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("modules", out var modulesElement))
                {
                    modules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in modulesElement.EnumerateObject())
                    {
                        if (prop.Value.TryGetInt32(out var level))
                            modules[prop.Name] = level;
                    }
                }
            }
            catch
            {
                modules = JsonSerializer.Deserialize<Dictionary<string, int>>(json);
            }

            if (modules != null && modules.Count > 0)
            {
                await InitializeAsync();

                foreach (var kvp in modules)
                    await SaveHideoutProgressAsync(kvp.Key, kvp.Value, ProfileService.PvpProfileId);

                File.Delete(filePath);
                System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Migrated and deleted: {filePath}");

                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Hideout migration failed: {ex.Message}");
        }

        return false;
    }

    private async Task<bool> MigrateItemInventoryJsonAsync()
    {
        var filePath = Path.Combine(AppEnv.ConfigPath, "item_inventory.json");

        if (!File.Exists(filePath))
            return false;

        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var data = JsonSerializer.Deserialize<ItemInventoryData>(json, options);

            if (data != null && data.Items.Count > 0)
            {
                await InitializeAsync();

                foreach (var kvp in data.Items)
                {
                    var inventory = kvp.Value;
                    await SaveItemInventoryAsync(kvp.Key, inventory.FirQuantity, inventory.NonFirQuantity, ProfileService.PvpProfileId);
                }

                File.Delete(filePath);
                System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Migrated and deleted: {filePath}");

                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UserDataDbService] ItemInventory migration failed: {ex.Message}");
        }

        return false;
    }

    #endregion

    #region User Settings

    /// <summary>
    /// 설정 값 조회
    /// </summary>
    public async Task<string?> GetSettingAsync(string key)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "SELECT Value FROM UserSettings WHERE Key = @key";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@key", key);

        var result = await cmd.ExecuteScalarAsync();
        return result as string;
    }

    /// <summary>
    /// 설정 값 저장
    /// </summary>
    public async Task SetSettingAsync(string key, string value)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = @"
            INSERT INTO UserSettings (Key, Value)
            VALUES (@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value = @value";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 설정 값 삭제
    /// </summary>
    public async Task DeleteSettingAsync(string key)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "DELETE FROM UserSettings WHERE Key = @key";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@key", key);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 모든 설정 조회
    /// </summary>
    public async Task<Dictionary<string, string>> GetAllSettingsAsync()
    {
        await InitializeAsync();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "SELECT Key, Value FROM UserSettings";
        await using var cmd = new SqliteCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    /// <summary>
    /// 동기 버전: 설정 값 조회 (초기화 시 사용)
    /// </summary>
    public string? GetSetting(string key)
    {
        if (!_isInitialized)
            InitializeAsync().GetAwaiter().GetResult();

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var sql = "SELECT Value FROM UserSettings WHERE Key = @key";
        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@key", key);

        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// 동기 버전: 설정 값 저장 (초기화 시 사용)
    /// </summary>
    public void SetSetting(string key, string value)
    {
        if (!_isInitialized)
            InitializeAsync().GetAwaiter().GetResult();

        var connectionString = $"Data Source={_databasePath}";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var sql = @"
            INSERT INTO UserSettings (Key, Value)
            VALUES (@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value = @value";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);

        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 동기 버전: 여러 설정 값을 하나의 연결/트랜잭션으로 저장합니다.
    /// 함께 바뀌는 값(예: 맵 뷰 상태의 맵/줌/팬)이 부분적으로만 기록되는 것을 막고,
    /// 키마다 연결을 새로 여는 왕복 비용을 없앱니다.
    /// </summary>
    public void SetSettings(IEnumerable<KeyValuePair<string, string>> settings)
    {
        if (!_isInitialized)
            InitializeAsync().GetAwaiter().GetResult();

        var connectionString = $"Data Source={_databasePath}";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var sql = @"
            INSERT INTO UserSettings (Key, Value)
            VALUES (@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value = @value";

        foreach (var setting in settings)
        {
            using var cmd = new SqliteCommand(sql, connection, transaction);
            cmd.Parameters.AddWithValue("@key", setting.Key);
            cmd.Parameters.AddWithValue("@value", setting.Value);
            cmd.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    #endregion

    #region Profile Settings

    /// <summary>
    /// 프로필별 설정 값 조회 (비동기)
    /// </summary>
    public async Task<string?> GetProfileSettingAsync(string profileId, string key)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "SELECT Value FROM ProfileSettings WHERE ProfileId = @profileId AND Key = @key";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@key", key);

        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>
    /// 프로필별 설정 값 저장 (비동기)
    /// </summary>
    public async Task SetProfileSettingAsync(string profileId, string key, string value)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = @"
            INSERT INTO ProfileSettings (ProfileId, Key, Value)
            VALUES (@profileId, @key, @value)
            ON CONFLICT(ProfileId, Key) DO UPDATE SET Value = @value";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 프로필별 모든 설정 로드
    /// </summary>
    public async Task<Dictionary<string, string>> LoadProfileSettingsAsync(string profileId)
    {
        await InitializeAsync();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "SELECT Key, Value FROM ProfileSettings WHERE ProfileId = @profileId";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    /// <summary>
    /// 프로필별 설정 값 삭제
    /// </summary>
    public async Task DeleteProfileSettingAsync(string profileId, string key)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = "DELETE FROM ProfileSettings WHERE ProfileId = @profileId AND Key = @key";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@key", key);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 동기 버전: 프로필별 설정 값 조회 (초기화 시 사용)
    /// </summary>
    public string? GetProfileSetting(string profileId, string key)
    {
        if (!_isInitialized)
            InitializeAsync().GetAwaiter().GetResult();

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var sql = "SELECT Value FROM ProfileSettings WHERE ProfileId = @profileId AND Key = @key";
        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@key", key);

        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// 동기 버전: 프로필별 설정 값 저장 (초기화 시 사용)
    /// </summary>
    public void SetProfileSetting(string profileId, string key, string value)
    {
        if (!_isInitialized)
            InitializeAsync().GetAwaiter().GetResult();

        var connectionString = $"Data Source={_databasePath}";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var sql = @"
            INSERT INTO ProfileSettings (ProfileId, Key, Value)
            VALUES (@profileId, @key, @value)
            ON CONFLICT(ProfileId, Key) DO UPDATE SET Value = @value";

        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);

        cmd.ExecuteNonQuery();
    }

    #endregion

    #region Batch Operations

    /// <summary>
    /// 여러 퀘스트 진행 상태를 일괄 저장
    /// </summary>
    public async Task SaveQuestProgressBatchAsync(Dictionary<string, QuestStatus> progress, string profileId,
        Func<string, string?>? getNormalizedName = null)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            var sql = @"
                INSERT INTO QuestProgress (ProfileId, Id, NormalizedName, Status, UpdatedAt)
                VALUES (@profileId, @id, @normalizedName, @status, @updatedAt)
                ON CONFLICT(ProfileId, Id) DO UPDATE SET
                    NormalizedName = @normalizedName,
                    Status = @status,
                    UpdatedAt = @updatedAt";

            foreach (var kvp in progress)
            {
                await using var cmd = new SqliteCommand(sql, connection, (SqliteTransaction)transaction);
                var normalizedName = getNormalizedName?.Invoke(kvp.Key) ?? kvp.Key;

                cmd.Parameters.AddWithValue("@profileId", profileId);
                cmd.Parameters.AddWithValue("@id", kvp.Key);
                cmd.Parameters.AddWithValue("@normalizedName", normalizedName);
                cmd.Parameters.AddWithValue("@status", kvp.Value.ToString());
                cmd.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("o"));

                await cmd.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    #endregion

    #region Raid History

    /// <summary>
    /// 레이드 히스토리 저장
    /// </summary>
    public async Task SaveRaidHistoryAsync(Models.EftRaidInfo raid)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var sql = @"
            INSERT INTO RaidHistory (
                RaidId, SessionId, ShortId, ProfileId, RaidType, GameMode,
                MapName, MapKey, ServerIp, ServerPort, IsParty, PartyLeaderAccountId,
                StartTime, EndTime, DurationSeconds, Rtt, PacketLoss, PacketsSent, PacketsReceived
            ) VALUES (
                @raidId, @sessionId, @shortId, @profileId, @raidType, @gameMode,
                @mapName, @mapKey, @serverIp, @serverPort, @isParty, @partyLeaderId,
                @startTime, @endTime, @durationSeconds, @rtt, @packetLoss, @packetsSent, @packetsReceived
            )";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@raidId", raid.RaidId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sessionId", raid.SessionId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@shortId", raid.ShortId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@profileId", raid.ProfileId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@raidType", (int)raid.RaidType);
        cmd.Parameters.AddWithValue("@gameMode", (int)raid.GameMode);
        cmd.Parameters.AddWithValue("@mapName", raid.MapName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@mapKey", raid.MapKey ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@serverIp", raid.ServerIp ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@serverPort", raid.ServerPort);
        cmd.Parameters.AddWithValue("@isParty", raid.IsParty ? 1 : 0);
        cmd.Parameters.AddWithValue("@partyLeaderId", raid.PartyLeaderAccountId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@startTime", raid.StartTime?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@endTime", raid.EndTime?.ToString("o") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@durationSeconds", raid.Duration?.TotalSeconds ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@rtt", raid.Rtt ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@packetLoss", raid.PacketLoss ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@packetsSent", raid.PacketsSent ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@packetsReceived", raid.PacketsReceived ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 레이드 히스토리 조회 (최근 N개)
    /// </summary>
    public async Task<List<Models.EftRaidInfo>> GetRaidHistoryAsync(int limit = 100, Models.RaidType? raidType = null, string? mapKey = null)
    {
        await InitializeAsync();

        var result = new List<Models.EftRaidInfo>();

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var whereConditions = new List<string>();
        if (raidType.HasValue)
            whereConditions.Add("RaidType = @raidType");
        if (!string.IsNullOrEmpty(mapKey))
            whereConditions.Add("MapKey = @mapKey");

        var whereClause = whereConditions.Count > 0 ? $"WHERE {string.Join(" AND ", whereConditions)}" : "";

        var sql = $@"
            SELECT RaidId, SessionId, ShortId, ProfileId, RaidType, GameMode,
                   MapName, MapKey, ServerIp, ServerPort, IsParty, PartyLeaderAccountId,
                   StartTime, EndTime, Rtt, PacketLoss, PacketsSent, PacketsReceived
            FROM RaidHistory
            {whereClause}
            ORDER BY StartTime DESC
            LIMIT @limit";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (raidType.HasValue)
            cmd.Parameters.AddWithValue("@raidType", (int)raidType.Value);
        if (!string.IsNullOrEmpty(mapKey))
            cmd.Parameters.AddWithValue("@mapKey", mapKey);

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var raid = new Models.EftRaidInfo
            {
                RaidId = reader.IsDBNull(0) ? null : reader.GetString(0),
                SessionId = reader.IsDBNull(1) ? null : reader.GetString(1),
                ShortId = reader.IsDBNull(2) ? null : reader.GetString(2),
                ProfileId = reader.IsDBNull(3) ? null : reader.GetString(3),
                RaidType = (Models.RaidType)reader.GetInt32(4),
                GameMode = (Models.GameMode)reader.GetInt32(5),
                MapName = reader.IsDBNull(6) ? null : reader.GetString(6),
                MapKey = reader.IsDBNull(7) ? null : reader.GetString(7),
                ServerIp = reader.IsDBNull(8) ? null : reader.GetString(8),
                ServerPort = reader.GetInt32(9),
                IsParty = reader.GetInt32(10) == 1,
                PartyLeaderAccountId = reader.IsDBNull(11) ? null : reader.GetString(11),
                StartTime = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
                EndTime = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)),
                Rtt = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                PacketLoss = reader.IsDBNull(15) ? null : reader.GetDouble(15),
                PacketsSent = reader.IsDBNull(16) ? null : reader.GetInt64(16),
                PacketsReceived = reader.IsDBNull(17) ? null : reader.GetInt64(17)
            };
            result.Add(raid);
        }

        return result;
    }

    /// <summary>
    /// 레이드 통계 조회
    /// </summary>
    public async Task<(int TotalRaids, int PmcRaids, int ScavRaids, int PartyRaids)> GetRaidStatisticsAsync(DateTime? since = null)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var whereClause = since.HasValue ? "WHERE StartTime >= @since" : "";

        var sql = $@"
            SELECT
                COUNT(*) as TotalRaids,
                SUM(CASE WHEN RaidType = 1 THEN 1 ELSE 0 END) as PmcRaids,
                SUM(CASE WHEN RaidType = 2 THEN 1 ELSE 0 END) as ScavRaids,
                SUM(CASE WHEN IsParty = 1 THEN 1 ELSE 0 END) as PartyRaids
            FROM RaidHistory
            {whereClause}";

        await using var cmd = new SqliteCommand(sql, connection);
        if (since.HasValue)
            cmd.Parameters.AddWithValue("@since", since.Value.ToString("o"));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3)
            );
        }

        return (0, 0, 0, 0);
    }

    /// <summary>
    /// 오래된 레이드 히스토리 삭제
    /// </summary>
    public async Task CleanupRaidHistoryAsync(int keepDays = 30)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var cutoffDate = DateTime.Now.AddDays(-keepDays).ToString("o");

        var sql = "DELETE FROM RaidHistory WHERE StartTime < @cutoff";
        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@cutoff", cutoffDate);

        var deleted = await cmd.ExecuteNonQueryAsync();
        System.Diagnostics.Debug.WriteLine($"[UserDataDbService] Cleaned up {deleted} old raid history entries");
    }

    #endregion
}
