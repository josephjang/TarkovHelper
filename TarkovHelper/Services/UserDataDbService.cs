using System.Data.Common;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Debug;
using TarkovHelper.Models;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Services;

/// <summary>
/// 사용자 데이터를 SQLite DB (user_data.db)에 저장/로드하는 서비스.
/// 퀘스트 진행, 목표 완료, 하이드아웃 진행, 아이템 인벤토리 등을 관리합니다.
/// </summary>
public sealed class UserDataDbService : IQuestProgressStore
{
    private static readonly ILogger _log = Log.For<UserDataDbService>();

    private static readonly Lazy<UserDataDbService> _instance = new(() => new UserDataDbService());
    public static UserDataDbService Instance => _instance.Value;

    private readonly string _databasePath;

    /// <summary>
    /// Serializes <see cref="InitializeAsync"/>. Table creation and the schema migrations are
    /// not safe to run twice at once (two callers would both see a column as missing and both
    /// try to add it), and roughly every method here starts by awaiting initialization from
    /// whatever thread it happens to run on.
    /// </summary>
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// Read and written through <see cref="Volatile"/> so the fast path outside
    /// <see cref="_initLock"/> cannot observe a stale or half-published value.
    /// </summary>
    private bool _isInitialized;

    public bool IsInitialized => Volatile.Read(ref _isInitialized);
    public string DatabasePath => _databasePath;

    /// <summary>
    /// ProfileSettings key holding the moment a profile was last reset (ISO-8601, local time,
    /// matching the log-timestamp convention). Written inside the reset transaction by
    /// <see cref="ResetProfileAsync"/>; the sync and live-event fences read it through
    /// <see cref="GetProgressResetAtAsync"/> and drop log events that are not after it.
    /// </summary>
    public const string ProgressResetAtKey = "app.progressResetAt";

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
        _log.Info(message);
    }

    private UserDataDbService()
        : this(Path.Combine(AppEnv.ConfigPath, "user_data.db"))
    {
    }

    /// <summary>
    /// Test seam: builds a service against an explicit database file, so the transactional
    /// reset, the schema migrations, and the watermark round-trip get real SQLite tests
    /// (temp file per test). The singleton path is unchanged; production code never calls
    /// this. Mirrors the <see cref="IQuestProgressStore"/> seam precedent.
    /// </summary>
    internal UserDataDbService(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>
    /// DB 초기화 (테이블 생성). Runs at most once: concurrent callers queue on
    /// <see cref="_initLock"/> and the winner's work is what every one of them observes.
    /// A failed attempt leaves the flag clear, so the next caller retries.
    /// Every await inside uses ConfigureAwait(false): the synchronous entry points
    /// (<see cref="GetSetting"/> and friends) block on this task, and resuming on a captured
    /// UI context while that thread waits would deadlock.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (Volatile.Read(ref _isInitialized)) return;

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _isInitialized)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);

            var connectionString = $"Data Source={_databasePath}";
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            await CreateTablesAsync(connection).ConfigureAwait(false);

            Volatile.Write(ref _isInitialized, true);
            _log.Info($"Initialized: {_databasePath}");
        }
        catch (Exception ex)
        {
            _log.Error($"Initialization failed: {ex.Message}", ex);
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task CreateTablesAsync(SqliteConnection connection)
    {
        await MigrateToProfileSchemaAsync(connection).ConfigureAwait(false);

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

            -- 레이드 히스토리. ProfileId is the EFT character id; AppProfileId is the app
            -- profile ('pvp'/'pve'/'season') of the session that produced the raid, captured
            -- at raid creation. NULL means no evidence (legacy row or unknown session mode)
            -- and such rows are never deleted by a profile reset (PRD R9).
            CREATE TABLE IF NOT EXISTS RaidHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RaidId TEXT,
                SessionId TEXT,
                ShortId TEXT,
                ProfileId TEXT,
                AppProfileId TEXT,
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
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);

        await MigrateRaidHistoryOwnerColumnAsync(connection).ConfigureAwait(false);
    }

    /// <summary>
    /// SQLITE_ERROR, the generic code SQLite returns for a statement it could not prepare or
    /// run: a duplicate column name in an ALTER, a missing table, a syntax error.
    /// </summary>
    private const int SqliteGenericError = 1;

    /// <summary>
    /// Adds the nullable RaidHistory.AppProfileId column to databases created before raid
    /// ownership existed. Idempotent in both the ordinary sense (pragma check first, same
    /// pattern as <see cref="MigrateToProfileSchemaAsync"/>) and under a race: a second process
    /// or connection can add the column between our check and our ALTER, and the resulting
    /// "duplicate column name" is the outcome we wanted, not a failure. Getting that wrong
    /// would throw out of the first launch after an upgrade, which callers such as
    /// <c>QuestProgressService.ReloadForProfileAsync</c> surface as an empty profile.
    /// A fresh database gets the column from the CREATE TABLE statement and the check finds it
    /// already present. Existing rows keep NULL, which is the "no evidence" value a profile
    /// reset never deletes.
    /// </summary>
    private static async Task MigrateRaidHistoryOwnerColumnAsync(SqliteConnection connection)
    {
        if (await HasRaidHistoryOwnerColumnAsync(connection).ConfigureAwait(false)) return;

        try
        {
            await using var alterCmd = new SqliteCommand(
                "ALTER TABLE RaidHistory ADD COLUMN AppProfileId TEXT NULL", connection);
            await alterCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            _log.Info("Added RaidHistory.AppProfileId column");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteGenericError)
        {
            // Re-check instead of matching the message text: if the column is there now,
            // someone else added it and the migration has arrived where it wanted to be.
            // Anything else (no such table, ...) shares the error code and must still throw.
            if (!await HasRaidHistoryOwnerColumnAsync(connection).ConfigureAwait(false)) throw;
            _log.Info($"RaidHistory.AppProfileId was added concurrently: {ex.Message}");
        }
    }

    private static async Task<bool> HasRaidHistoryOwnerColumnAsync(SqliteConnection connection)
    {
        const string sql = "SELECT COUNT(*) FROM pragma_table_info('RaidHistory') WHERE name='AppProfileId'";
        await using var cmd = new SqliteCommand(sql, connection);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync().ConfigureAwait(false)) > 0;
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
            var tableExists = Convert.ToInt32(await checkTableCmd.ExecuteScalarAsync().ConfigureAwait(false)) > 0;

            if (!tableExists) return; // 신규 설치: 마이그레이션 불필요

            // ProfileId 컬럼이 이미 있으면 마이그레이션 완료된 상태
            var checkColSql = "SELECT COUNT(*) FROM pragma_table_info('QuestProgress') WHERE name='ProfileId'";
            await using var checkColCmd = new SqliteCommand(checkColSql, connection);
            var hasProfileId = Convert.ToInt32(await checkColCmd.ExecuteScalarAsync().ConfigureAwait(false)) > 0;

            if (hasProfileId) return; // 이미 마이그레이션됨

            _log.Info("Migrating to profile schema...");

            await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
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
                await migrateCmd.ExecuteNonQueryAsync().ConfigureAwait(false);

                await transaction.CommitAsync().ConfigureAwait(false);
                _log.Info("Profile schema migration completed");
            }
            catch (Exception ex)
            {
                await RollbackSafelyAsync(transaction, nameof(MigrateToProfileSchemaAsync)).ConfigureAwait(false);
                _log.Error($"Profile schema migration failed: {ex.Message}", ex);
                throw;
            }
        }
        catch (Exception ex) when (ex is not SqliteException { SqliteErrorCode: SqliteGenericError })
        {
            _log.Error($"MigrateToProfileSchemaAsync error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Rolls a transaction back from a catch block without letting the rollback's own failure
    /// replace the exception that caused it. A rollback can legitimately fail - the transaction
    /// may already have been rolled back by SQLite (a full disk, a lost lock) or its connection
    /// may be gone - and an unguarded <c>RollbackAsync</c> in a <c>catch { ...; throw; }</c>
    /// never reaches its <c>throw</c>, so the caller (and the player, since these messages reach
    /// the reset dialog) is told about the rollback instead of the real fault. The caller keeps
    /// its bare <c>throw;</c>, which is now unconditional; the rollback failure is logged.
    /// Internal rather than private so the guard itself is testable.
    /// </summary>
    internal static async Task RollbackSafelyAsync(DbTransaction transaction, string context)
    {
        try
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
        catch (Exception rollbackEx)
        {
            _log.Error($"{context}: rollback failed after an earlier failure: {rollbackEx.Message}", rollbackEx);
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
            await RollbackSafelyAsync(transaction, nameof(SaveQuestProgressBatchAsync));
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

    #endregion

    #region Profile Reset

    /// <summary>
    /// Removes everything <paramref name="profileId"/> owns, atomically: one connection, one
    /// transaction across all six profile-keyed tables, with the reset watermark written in the
    /// same commit (feature-complete-profile-reset.spec.md). No observer, in the app or in the
    /// file, can see a partially reset profile; on any failure the transaction rolls back and
    /// the exception propagates to the caller, which is what makes "nothing was removed" a
    /// statement the UI can make truthfully (PRD R5).
    /// </summary>
    /// <param name="profileId">The storage partition to reset. Never resolved ambiently.</param>
    /// <param name="resetAt">
    /// The reset moment, local time (the log-timestamp convention). Stored as the
    /// <see cref="ProgressResetAtKey"/> watermark; log events not after it are fenced out.
    /// </param>
    /// <param name="preservedSettingKeys">
    /// ProfileSettings keys that survive the reset (the edition facts). Every key NOT listed is
    /// deleted: wiped-by-default is the safe direction for progress-shaped data.
    /// </param>
    public Task ResetProfileAsync(
        string profileId, DateTime resetAt, IReadOnlyCollection<string> preservedSettingKeys)
        => ResetProfileAsync(profileId, resetAt, preservedSettingKeys, beforeCommit: null);

    /// <inheritdoc cref="ResetProfileAsync(string, DateTime, IReadOnlyCollection{string})"/>
    /// <param name="profileId">The storage partition to reset. Never resolved ambiently.</param>
    /// <param name="resetAt">The reset moment, local time. See the public overload.</param>
    /// <param name="preservedSettingKeys">The keys that survive. See the public overload.</param>
    /// <param name="beforeCommit">
    /// Test seam: awaited between the deletes and the commit, so the rollback guarantee (PRD R5)
    /// is provable against a real SQLite file rather than asserted. It is a parameter rather
    /// than settable state on the service so no production caller can reach it and no test can
    /// leave it armed on the singleton.
    /// </param>
    internal async Task ResetProfileAsync(
        string profileId, DateTime resetAt, IReadOnlyCollection<string> preservedSettingKeys,
        Func<Task>? beforeCommit)
    {
        await InitializeAsync();

        var connectionString = $"Data Source={_databasePath}";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        try
        {
            await DeleteOwnedRowsAsync(connection, transaction, profileId);
            await DeleteProfileSettingsExceptAsync(connection, transaction, profileId, preservedSettingKeys);
            await DeleteOwnedRaidHistoryAsync(connection, transaction, profileId);

            // The watermark is written after the settings delete, inside the same transaction:
            // fence and removal commit atomically, and the insert is not swept by its own
            // delete. A second reset simply overwrites the previous watermark.
            await WriteResetWatermarkAsync(connection, transaction, profileId, resetAt);

            if (beforeCommit != null)
            {
                await beforeCommit();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            // The rollback must not become the failure the caller reports: ProfileResetService
            // shows this exception's message to the player, and "the transaction is completed"
            // would hide the disk-full or locked-database condition that actually stopped us.
            await RollbackSafelyAsync(transaction, nameof(ResetProfileAsync));
            throw;
        }
    }

    /// <summary>
    /// Deletes every row the profile owns in the plainly profile-keyed progress tables. Each
    /// table is keyed by a ProfileId column, so one parameterized DELETE per table is the whole
    /// step; the table names are literals in this method, never caller input.
    /// </summary>
    private static async Task DeleteOwnedRowsAsync(
        SqliteConnection connection, SqliteTransaction transaction, string profileId)
    {
        foreach (var table in new[] { "QuestProgress", "ObjectiveProgress", "HideoutProgress", "ItemInventory" })
        {
            await using var cmd = new SqliteCommand(
                $"DELETE FROM {table} WHERE ProfileId = @profileId", connection, transaction);
            cmd.Parameters.AddWithValue("@profileId", profileId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Profile values (level, scav rep, faction, prestige, DSP, ...) go back to their
    /// defaults by deleting the rows; only the named survivors stay (PRD R3, R4).
    /// An empty survivor collection deletes every one of the profile's settings, so the
    /// NOT IN clause is built only when there is something to keep: an empty IN list is not
    /// valid SQLite.
    /// </summary>
    private static async Task DeleteProfileSettingsExceptAsync(
        SqliteConnection connection, SqliteTransaction transaction, string profileId,
        IReadOnlyCollection<string> preservedSettingKeys)
    {
        var preserved = preservedSettingKeys.ToList();
        var placeholders = string.Join(", ", preserved.Select((_, i) => $"@keep{i}"));
        var settingsSql = preserved.Count == 0
            ? "DELETE FROM ProfileSettings WHERE ProfileId = @profileId"
            : $"DELETE FROM ProfileSettings WHERE ProfileId = @profileId AND Key NOT IN ({placeholders})";
        await using var cmd = new SqliteCommand(settingsSql, connection, transaction);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        for (var i = 0; i < preserved.Count; i++)
        {
            cmd.Parameters.AddWithValue($"@keep{i}", preserved[i]);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// NULL AppProfileId never matches the equality, so legacy rows and rows with no
    /// session evidence survive by construction (PRD R9).
    /// </summary>
    private static async Task DeleteOwnedRaidHistoryAsync(
        SqliteConnection connection, SqliteTransaction transaction, string profileId)
    {
        await using var cmd = new SqliteCommand(
            "DELETE FROM RaidHistory WHERE AppProfileId = @profileId", connection, transaction);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Raises the reset fence (PRD R6) by upserting <see cref="ProgressResetAtKey"/> for the
    /// profile. The caller decides where in the transaction this runs; see the ordering comment
    /// at the call site in <see cref="ResetProfileAsync(string, DateTime, IReadOnlyCollection{string}, Func{Task})"/>.
    /// </summary>
    private static async Task WriteResetWatermarkAsync(
        SqliteConnection connection, SqliteTransaction transaction, string profileId, DateTime resetAt)
    {
        await using var cmd = new SqliteCommand(@"
            INSERT INTO ProfileSettings (ProfileId, Key, Value)
            VALUES (@profileId, @key, @value)
            ON CONFLICT(ProfileId, Key) DO UPDATE SET Value = @value", connection, transaction);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        cmd.Parameters.AddWithValue("@key", ProgressResetAtKey);
        cmd.Parameters.AddWithValue("@value", resetAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The moment <paramref name="profileId"/> was last reset, or null when it never was.
    /// Read by the sync and live-event fences (PRD R6); see <see cref="ProgressResetAtKey"/>.
    /// </summary>
    public async Task<DateTime?> GetProgressResetAtAsync(string profileId)
    {
        var value = await GetProfileSettingAsync(profileId, ProgressResetAtKey);
        return DateTime.TryParse(
            value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var resetAt)
            ? resetAt
            : null;
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
                    _log.Info($"Migrated and deleted: {v2Path}");

                    if (File.Exists(v1Path))
                    {
                        File.Delete(v1Path);
                        _log.Info($"Deleted legacy: {v1Path}");
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"V2 migration failed: {ex.Message}", ex);
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
                    _log.Info($"Migrated and deleted: {v1Path}");

                    return true;
                }
            }
            catch (Exception ex)
            {
                _log.Error($"V1 migration failed: {ex.Message}", ex);
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
                _log.Info($"Migrated and deleted: {filePath}");

                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Objective migration failed: {ex.Message}", ex);
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
                _log.Info($"Migrated and deleted: {filePath}");

                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Hideout migration failed: {ex.Message}", ex);
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
                _log.Info($"Migrated and deleted: {filePath}");

                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"ItemInventory migration failed: {ex.Message}", ex);
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
        // InitializeAsync is idempotent and has its own synchronized fast path, so there is no
        // unsynchronized flag read out here.
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
        // InitializeAsync is idempotent and has its own synchronized fast path, so there is no
        // unsynchronized flag read out here.
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
        // InitializeAsync is idempotent and has its own synchronized fast path, so there is no
        // unsynchronized flag read out here.
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
    /// 동기 버전: 프로필별 설정 값 저장 (초기화 시 사용)
    /// </summary>
    public void SetProfileSetting(string profileId, string key, string value)
    {
        // InitializeAsync is idempotent and has its own synchronized fast path, so there is no
        // unsynchronized flag read out here.
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

    /// <summary>
    /// 동기 버전: 한 프로필의 모든 설정을 한 번의 쿼리로 조회
    /// <para>
    /// One query and one connection, not one per key. <c>SettingsService</c> publishes its eight
    /// profile-scoped values as a single immutable snapshot, and eight sequential single-key
    /// reads would leave a window between each pair wide enough for a profile switch to land in,
    /// tearing the snapshot across two profiles. Synchronous because its caller must have the
    /// values in hand when it returns: the property getters answer from them mid-startup, and
    /// the reset hook runs as a plain <c>Action</c>.
    /// See docs/decisions/fix-profile-settings-race.spec.md.
    /// </para>
    /// </summary>
    public Dictionary<string, string> LoadProfileSettings(string profileId)
    {
        // InitializeAsync is idempotent and has its own synchronized fast path, so there is no
        // unsynchronized flag read out here.
        InitializeAsync().GetAwaiter().GetResult();

        // Ordinal, which is the collation the storage itself matches under: ProfileSettings has
        // no COLLATE NOCASE, so (ProfileId, Key) treats "app.playerLevel" and "app.PlayerLevel"
        // as two legal rows and the per-key SELECT this replaced returned only the exact key.
        // A case-insensitive dictionary would collapse the pair last-row-wins, letting row order
        // decide the value and giving a hand-edited row an effect it never used to have.
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        var connectionString = $"Data Source={_databasePath};Mode=ReadOnly";
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var sql = "SELECT Key, Value FROM ProfileSettings WHERE ProfileId = @profileId";
        using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@profileId", profileId);
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
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
            await RollbackSafelyAsync(transaction, nameof(SaveQuestProgressBatchAsync));
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
                RaidId, SessionId, ShortId, ProfileId, AppProfileId, RaidType, GameMode,
                MapName, MapKey, ServerIp, ServerPort, IsParty, PartyLeaderAccountId,
                StartTime, EndTime, DurationSeconds, Rtt, PacketLoss, PacketsSent, PacketsReceived
            ) VALUES (
                @raidId, @sessionId, @shortId, @profileId, @appProfileId, @raidType, @gameMode,
                @mapName, @mapKey, @serverIp, @serverPort, @isParty, @partyLeaderId,
                @startTime, @endTime, @durationSeconds, @rtt, @packetLoss, @packetsSent, @packetsReceived
            )";

        await using var cmd = new SqliteCommand(sql, connection);
        cmd.Parameters.AddWithValue("@raidId", raid.RaidId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sessionId", raid.SessionId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@shortId", raid.ShortId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@profileId", raid.ProfileId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@appProfileId", raid.AppProfileId ?? (object)DBNull.Value);
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
                   StartTime, EndTime, Rtt, PacketLoss, PacketsSent, PacketsReceived,
                   AppProfileId
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
                // ServerPort is a nullable column (only SaveRaidHistoryAsync's own rows are
                // guaranteed to carry a value); an unchecked GetInt32 threw on such a row.
                ServerPort = reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
                IsParty = reader.GetInt32(10) == 1,
                PartyLeaderAccountId = reader.IsDBNull(11) ? null : reader.GetString(11),
                StartTime = reader.IsDBNull(12) ? null : DateTime.Parse(reader.GetString(12)),
                EndTime = reader.IsDBNull(13) ? null : DateTime.Parse(reader.GetString(13)),
                Rtt = reader.IsDBNull(14) ? null : reader.GetDouble(14),
                PacketLoss = reader.IsDBNull(15) ? null : reader.GetDouble(15),
                PacketsSent = reader.IsDBNull(16) ? null : reader.GetInt64(16),
                PacketsReceived = reader.IsDBNull(17) ? null : reader.GetInt64(17),
                AppProfileId = reader.IsDBNull(18) ? null : reader.GetString(18)
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
        _log.Info($"Cleaned up {deleted} old raid history entries");
    }

    #endregion
}
