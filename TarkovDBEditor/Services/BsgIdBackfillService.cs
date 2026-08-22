using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace TarkovDBEditor.Services
{
    /// <summary>
    /// Restores the external game ids that the published database lost.
    /// <para>
    /// <c>Quests.BsgId</c> and <c>Items.BsgId</c> have been NULL on every published row since
    /// the 2026-01-14 regeneration, which is why log sync has matched no quest event for seven
    /// months and why hideout item requirements resolve nothing. The 1.0.7 snapshot (commit
    /// ebbc60c, 2025-12-19) still holds 473 quest and 2648 item ids under the same row keys,
    /// and every one of those quest ids is a live task today.
    /// </para>
    /// <para>
    /// Restoring them is also what makes the 1.1 rename carry-over possible: the resolver
    /// recognises a renamed quest by its external id, so a regeneration from a database whose
    /// ids are still NULL would mint fresh keys for all 91 renamed quests while every page
    /// still matched. <see cref="RefreshDataService"/> refuses to start in that state and
    /// points here.
    /// </para>
    /// Run once, against a working copy of the published database, before the first 1.1
    /// regeneration. Later regenerations bridge through the ids this wrote.
    /// </summary>
    public sealed class BsgIdBackfillService
    {
        /// <summary>
        /// Ids no snapshot can supply, bridged by hand because the row never carried one.
        /// <para>
        /// Exactly one row is in this state: No Questions Asked, which patch 1.1 renamed to
        /// Special Order. It was published between the snapshot and the January regeneration,
        /// so it has no id to copy, and without the bridge it would be the one rename of the
        /// 92 that loses its progress. The task id is confirmed by the API's own
        /// <c>wikiLink</c> for <c>68ee1c18b4e5bc9a68018cd7</c> and by the wiki's move log.
        /// </para>
        /// <para>
        /// This is a historical repair tied to one publish, not a list that grows: a row added
        /// after this refresh carries its id from the moment it is imported.
        /// </para>
        /// </summary>
        private static readonly (string QuestName, string BsgId)[] HandBridgedQuestIds =
        {
            ("No Questions Asked", "68ee1c18b4e5bc9a68018cd7"),
        };

        /// <summary>
        /// Copies every <c>BsgId</c> the snapshot holds into the rows of
        /// <paramref name="workingDatabasePath"/> that have none, then applies the hand bridges.
        /// Rows that already carry an id are left alone: the working database is the newer
        /// source, and overwriting it would undo a correction made in the editor.
        /// </summary>
        public async Task<BsgIdBackfillResult> BackfillAsync(
            string workingDatabasePath,
            string snapshotDatabasePath,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(workingDatabasePath))
                throw new FileNotFoundException("Working database not found.", workingDatabasePath);
            if (!File.Exists(snapshotDatabasePath))
                throw new FileNotFoundException("Snapshot database not found.", snapshotDatabasePath);

            var result = new BsgIdBackfillResult
            {
                WorkingDatabasePath = workingDatabasePath,
                SnapshotDatabasePath = snapshotDatabasePath,
            };

            progress?.Invoke("Reading external IDs from the snapshot...");
            var snapshotQuests = await ReadSnapshotIdsAsync(snapshotDatabasePath, "Quests", cancellationToken);
            var snapshotItems = await ReadSnapshotIdsAsync(snapshotDatabasePath, "Items", cancellationToken);
            result.SnapshotQuestIds = snapshotQuests.Count;
            result.SnapshotItemIds = snapshotItems.Count;
            progress?.Invoke($"Snapshot holds {snapshotQuests.Count} quest and {snapshotItems.Count} item IDs.");

            await using var connection = new SqliteConnection($"Data Source={workingDatabasePath}");
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            try
            {
                result.QuestsFilled = await FillAsync(connection, transaction, "Quests", snapshotQuests, cancellationToken);
                result.ItemsFilled = await FillAsync(connection, transaction, "Items", snapshotItems, cancellationToken);

                foreach (var (questName, bsgId) in HandBridgedQuestIds)
                {
                    await using var cmd = new SqliteCommand(
                        "UPDATE Quests SET BsgId = @BsgId WHERE Name = @Name AND (BsgId IS NULL OR BsgId = '')",
                        connection, transaction);
                    cmd.Parameters.AddWithValue("@BsgId", bsgId);
                    cmd.Parameters.AddWithValue("@Name", questName);
                    if (await cmd.ExecuteNonQueryAsync(cancellationToken) > 0)
                        result.HandBridgesApplied.Add($"{questName} -> {bsgId}");
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }

            result.QuestsStillMissing = await CountMissingAsync(connection, "Quests", cancellationToken);
            result.ItemsStillMissing = await CountMissingAsync(connection, "Items", cancellationToken);
            result.QuestsTotal = await CountRowsAsync(connection, "Quests", cancellationToken);
            result.ItemsTotal = await CountRowsAsync(connection, "Items", cancellationToken);

            progress?.Invoke(
                $"Filled {result.QuestsFilled} quest and {result.ItemsFilled} item IDs; "
                + $"{result.QuestsStillMissing}/{result.QuestsTotal} quests and "
                + $"{result.ItemsStillMissing}/{result.ItemsTotal} items still have none.");

            return result;
        }

        private static async Task<Dictionary<string, string>> ReadSnapshotIdsAsync(
            string snapshotPath, string table, CancellationToken cancellationToken)
        {
            var ids = new Dictionary<string, string>(StringComparer.Ordinal);

            await using var connection = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken);

            await using var cmd = new SqliteCommand(
                $"SELECT Id, BsgId FROM {table} WHERE BsgId IS NOT NULL AND BsgId <> ''", connection);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids[reader.GetString(0)] = reader.GetString(1);

            return ids;
        }

        private static async Task<int> FillAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string table,
            Dictionary<string, string> snapshotIds,
            CancellationToken cancellationToken)
        {
            var filled = 0;
            await using var cmd = new SqliteCommand(
                $"UPDATE {table} SET BsgId = @BsgId WHERE Id = @Id AND (BsgId IS NULL OR BsgId = '')",
                connection, transaction);
            var bsgIdParameter = cmd.Parameters.Add("@BsgId", SqliteType.Text);
            var idParameter = cmd.Parameters.Add("@Id", SqliteType.Text);

            foreach (var (id, bsgId) in snapshotIds)
            {
                bsgIdParameter.Value = bsgId;
                idParameter.Value = id;
                filled += await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            return filled;
        }

        private static async Task<int> CountMissingAsync(
            SqliteConnection connection, string table, CancellationToken cancellationToken)
        {
            await using var cmd = new SqliteCommand(
                $"SELECT COUNT(*) FROM {table} WHERE BsgId IS NULL OR BsgId = ''", connection);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        }

        private static async Task<int> CountRowsAsync(
            SqliteConnection connection, string table, CancellationToken cancellationToken)
        {
            await using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM {table}", connection);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        }
    }

    /// <summary>What one backfill run changed, and what it could not reach.</summary>
    public sealed class BsgIdBackfillResult
    {
        public string WorkingDatabasePath { get; set; } = "";
        public string SnapshotDatabasePath { get; set; } = "";

        public int SnapshotQuestIds { get; set; }
        public int SnapshotItemIds { get; set; }

        public int QuestsFilled { get; set; }
        public int ItemsFilled { get; set; }

        public int QuestsStillMissing { get; set; }
        public int ItemsStillMissing { get; set; }
        public int QuestsTotal { get; set; }
        public int ItemsTotal { get; set; }

        public List<string> HandBridgesApplied { get; } = new();
    }
}
