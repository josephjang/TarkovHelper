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
    /// ebbc60c, 2025-12-19) still holds 473 quest and 2648 item ids, and every one of those
    /// quest ids is a live task today. All 473 quest ids land; 2644 of the item ids do, the
    /// other four being keyed to rows the published database no longer has.
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
        /// Special Order. The 1.0.7 snapshot does hold that row, under the same key, but with
        /// <c>BsgId</c> NULL: it is one of the 14 snapshot rows whose December tarkov.dev
        /// matching resolved no id, so there is nothing to copy. Without the bridge it would be
        /// the one rename of the 92 that loses its progress. The task id is confirmed by the
        /// API's own <c>wikiLink</c> for <c>68ee1c18b4e5bc9a68018cd7</c> and by the wiki's move
        /// log.
        /// </para>
        /// <para>
        /// Why one and not fifteen: of the 488 published rows, 15 carry no id the snapshot can
        /// supply, and this is the only one of the 15 whose wiki page title moved. Thirteen of
        /// the others are snapshot rows with a NULL id that keep their titles, so their keys are
        /// recomputed to the same value and they stay themselves with no external id at all (two
        /// of those thirteen, New Beginning (Prestige 5) and (Prestige 6), leave the app anyway,
        /// for want of a game record). The fifteenth, Setting Priorities, is the row actually
        /// published between the snapshot and the January regeneration, and it keeps its title
        /// too. Only a row that is both renamed and id-less needs a bridge.
        /// </para>
        /// <para>
        /// This is a historical repair tied to one publish, not a list that grows: a row added
        /// after this refresh carries its id from the moment it is imported.
        /// </para>
        /// </summary>
        public static readonly IReadOnlyList<HandBridge> HandBridgedQuestIds = new[]
        {
            new HandBridge("No Questions Asked", "68ee1c18b4e5bc9a68018cd7"),
        };

        /// <summary>
        /// External ids the snapshot recorded against the wrong row, corrected in place.
        /// <para>
        /// The December 2025 matching gave the plain "Army cap" row the id of "Army cap
        /// (CADPAT)", a separate item with a page and a row of its own, and left CADPAT's row
        /// with no id at all. Both rows are in the published database that way. Copied forward
        /// untouched, the CADPAT page matches the Army cap row by that id and carries its key,
        /// which is the key the Army cap page mints for itself, so the two items collapse onto
        /// one primary key and <see cref="RefreshDataService"/> refuses the whole run.
        /// </para>
        /// <para>
        /// An audit of all 2644 backfilled item ids against today's tarkov.dev catalogue found
        /// seven whose page no longer matches the row's own. Six are ordinary wiki renames,
        /// which is what identity carry-over exists for (Radian to Radian Weapons, Ops-Core
        /// SLAAP to Velocity Systems SLAAP, the two Chiappa sights dropping "Red", and the two
        /// Arena posters). This is the seventh and the only one where the row's own page still
        /// exists as a separate item, which is what makes it a mis-assignment rather than a
        /// rename, and what makes it collide.
        /// </para>
        /// <para>
        /// Correcting rather than deleting: the id is real and belongs to CADPAT, so both rows
        /// end up with the id they should always have had, both keep the row key they have
        /// always had, and both keep their icon files. Entries are applied in order, so a
        /// correction that frees an id runs before the one that claims it.
        /// </para>
        /// </summary>
        public static readonly IReadOnlyList<SnapshotIdCorrection> MisrecordedItemIds = new[]
        {
            // https://escapefromtarkov.fandom.com/wiki/Army_cap
            new SnapshotIdCorrection(
                "Army cap",
                "aHR0cHM6Ly9lc2NhcGVmcm9tdGFya292LmZhbmRvbS5jb20vd2lraS9Bcm15X2NhcA",
                RecordedBsgId: "6040de02647ad86262233012",
                CorrectBsgId: "59e770f986f7742cbe3164ef"),

            // https://escapefromtarkov.fandom.com/wiki/Army_cap_%28CADPAT%29
            new SnapshotIdCorrection(
                "Army cap (CADPAT)",
                "aHR0cHM6Ly9lc2NhcGVmcm9tdGFya292LmZhbmRvbS5jb20vd2lraS9Bcm15X2NhcF8lMjhDQURQQVQlMjk",
                RecordedBsgId: null,
                CorrectBsgId: "6040de02647ad86262233012"),
        };

        /// <summary>
        /// Copies every <c>BsgId</c> the snapshot holds into the rows of
        /// <paramref name="workingDatabasePath"/> that have none, then applies the hand bridges.
        /// Rows that already carry an id are left alone: the working database is the newer
        /// source, and overwriting it would undo a correction made in the editor.
        /// <para>
        /// Every hand bridge lands in <see cref="BsgIdBackfillResult.HandBridges"/> whether or
        /// not it wrote anything, and a bridge that matched no row is also pushed through
        /// <paramref name="progress"/>. A bridge changes none of the counts, so a silent one
        /// would leave a run that saved nothing looking exactly like a run that worked.
        /// </para>
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

                foreach (var bridge in HandBridgedQuestIds)
                {
                    var report = await ApplyHandBridgeAsync(connection, transaction, bridge, cancellationToken);
                    result.HandBridges.Add(report);

                    // A bridge that did not apply is a finding, not a silence: the run that
                    // should have saved this row's progress is otherwise indistinguishable in
                    // the report from one that did, because the bridge does not move the
                    // QuestsFilled count the operator watches.
                    if (report.NeedsAttention)
                        progress?.Invoke(report.Summary);
                }

                foreach (var correction in MisrecordedItemIds)
                {
                    var report = await ApplyItemIdCorrectionAsync(connection, transaction, correction, cancellationToken);
                    result.ItemIdCorrections.Add(report);

                    // Like a hand bridge, a correction moves none of the counts above, so one
                    // that found nothing to correct has to say so or it reads as success. An
                    // uncorrected row stops the next refresh outright.
                    if (report.NeedsAttention)
                        progress?.Invoke(report.Summary);
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

        /// <summary>
        /// Writes one hand-bridged ID and reports what actually happened to the row.
        /// <para>
        /// The row is looked up by the name the bridge names <em>or</em> by the ID it would
        /// write, because the name is exactly what this refresh renames: "No Questions Asked"
        /// becomes "Special Order" the moment the 1.1 data publishes. Matching on the name alone
        /// would make every later run report <see cref="HandBridgeOutcome.NoMatchingRow"/> and
        /// warn that the quest is about to lose its progress, on a row that is in fact already
        /// bridged. A row that carries the ID is bridged, whatever it is called now.
        /// </para>
        /// <para>
        /// The outcome is worth distinguishing because "the UPDATE changed no rows" has two very
        /// different causes: the row already carries an ID (a re-run, nothing to do) or nothing
        /// in the database answers to the bridge at all (the bridge is dead, and the rename it
        /// exists for will take that quest's recorded progress with it).
        /// </para>
        /// </summary>
        private static async Task<HandBridgeReport> ApplyHandBridgeAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            HandBridge bridge,
            CancellationToken cancellationToken)
        {
            // The IDs carried by the rows that answer to the bridge's NAME, and separately
            // whether any row at all already carries the bridged ID (that row may well be under
            // the new title, which is the whole reason the lookup is not name-only).
            var idsOfNamedRows = new List<string>();
            var someRowCarriesTheBridgedId = false;

            await using (var read = new SqliteCommand(
                "SELECT Name, BsgId FROM Quests WHERE Name = @Name OR BsgId = @BsgId",
                connection, transaction))
            {
                read.Parameters.AddWithValue("@Name", bridge.QuestName);
                read.Parameters.AddWithValue("@BsgId", bridge.BsgId);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var name = reader.GetString(0);
                    var existingId = reader.IsDBNull(1) ? "" : reader.GetString(1);

                    if (string.Equals(existingId, bridge.BsgId, StringComparison.Ordinal))
                        someRowCarriesTheBridgedId = true;
                    else if (string.Equals(name, bridge.QuestName, StringComparison.Ordinal))
                        idsOfNamedRows.Add(existingId);
                }
            }

            // Checked before the empty case: after the rename the only row that answers is the
            // renamed one, matched by ID and not by name.
            if (someRowCarriesTheBridgedId)
                return new HandBridgeReport(bridge, HandBridgeOutcome.AlreadyBridged, bridge.BsgId);

            if (idsOfNamedRows.Count == 0)
                return new HandBridgeReport(bridge, HandBridgeOutcome.NoMatchingRow, null);

            await using var update = new SqliteCommand(
                "UPDATE Quests SET BsgId = @BsgId WHERE Name = @Name AND (BsgId IS NULL OR BsgId = '')",
                connection, transaction);
            update.Parameters.AddWithValue("@BsgId", bridge.BsgId);
            update.Parameters.AddWithValue("@Name", bridge.QuestName);

            if (await update.ExecuteNonQueryAsync(cancellationToken) > 0)
                return new HandBridgeReport(bridge, HandBridgeOutcome.Applied, bridge.BsgId);

            // Nothing was empty, so every row of that name already carried an ID and the bridge
            // left it alone: the working database is the newer source. None of them is the
            // bridged ID (that returned above), so this one is worth a look.
            return new HandBridgeReport(bridge, HandBridgeOutcome.IdAlreadyDiffers, idsOfNamedRows[0]);
        }

        /// <summary>
        /// Corrects one item's external id and reports what the row actually held.
        /// <para>
        /// The row is found by its key, not its name: the key is what the collision is about,
        /// and a name can be reused by another item exactly as it can for a quest. The write
        /// only happens when the row still holds the value the snapshot recorded, so a value an
        /// operator has already corrected in the editor is reported rather than overwritten,
        /// and a re-run of the backfill is a no-op instead of a second correction.
        /// </para>
        /// </summary>
        private static async Task<SnapshotIdCorrectionReport> ApplyItemIdCorrectionAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SnapshotIdCorrection correction,
            CancellationToken cancellationToken)
        {
            string? currentId;
            await using (var read = new SqliteCommand(
                "SELECT BsgId FROM Items WHERE Id = @Id", connection, transaction))
            {
                read.Parameters.AddWithValue("@Id", correction.RowKey);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    return new SnapshotIdCorrectionReport(correction, SnapshotIdCorrectionOutcome.NoMatchingRow, null);

                currentId = reader.IsDBNull(0) ? null : reader.GetString(0);
            }

            if (string.Equals(currentId, correction.CorrectBsgId, StringComparison.Ordinal))
                return new SnapshotIdCorrectionReport(correction, SnapshotIdCorrectionOutcome.AlreadyCorrect, currentId);

            // Empty string and NULL are the same "no id" to every reader of this column.
            var recorded = string.IsNullOrEmpty(correction.RecordedBsgId) ? null : correction.RecordedBsgId;
            var current = string.IsNullOrEmpty(currentId) ? null : currentId;
            if (!string.Equals(current, recorded, StringComparison.Ordinal))
                return new SnapshotIdCorrectionReport(correction, SnapshotIdCorrectionOutcome.CarriesAnotherId, currentId);

            await using var update = new SqliteCommand(
                "UPDATE Items SET BsgId = @BsgId WHERE Id = @Id", connection, transaction);
            update.Parameters.AddWithValue("@BsgId", correction.CorrectBsgId);
            update.Parameters.AddWithValue("@Id", correction.RowKey);
            await update.ExecuteNonQueryAsync(cancellationToken);

            return new SnapshotIdCorrectionReport(correction, SnapshotIdCorrectionOutcome.Applied, correction.CorrectBsgId);
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

        /// <summary>
        /// One entry per hand bridge, whether or not it changed anything. Every bridge is
        /// reported: a bridge that matched no row moves none of the counts above, so silence
        /// would read exactly like success.
        /// </summary>
        public List<HandBridgeReport> HandBridges { get; } = new();

        /// <summary>The bridges an operator has to act on before publishing.</summary>
        public IReadOnlyList<HandBridgeReport> HandBridgesNeedingAttention =>
            HandBridges.FindAll(b => b.NeedsAttention);

        /// <summary>
        /// One entry per snapshot id correction, whether or not it changed anything, for the
        /// same reason the bridges are all reported: a correction moves none of the counts.
        /// </summary>
        public List<SnapshotIdCorrectionReport> ItemIdCorrections { get; } = new();

        /// <summary>The corrections an operator has to act on before refreshing.</summary>
        public IReadOnlyList<SnapshotIdCorrectionReport> ItemIdCorrectionsNeedingAttention =>
            ItemIdCorrections.FindAll(c => c.NeedsAttention);
    }

    /// <summary>
    /// An external game ID the snapshot recorded against the wrong row.
    /// </summary>
    /// <param name="ItemName">The row's name, for the report. Not how the row is found.</param>
    /// <param name="RowKey">The row's primary key, which is how it is found.</param>
    /// <param name="RecordedBsgId">
    /// What the snapshot put there, or null where it left the row empty. The correction only
    /// writes over exactly this, so a value corrected by hand since is reported, not clobbered.
    /// </param>
    /// <param name="CorrectBsgId">The id that row should have.</param>
    public sealed record SnapshotIdCorrection(
        string ItemName,
        string RowKey,
        string? RecordedBsgId,
        string CorrectBsgId);

    /// <summary>What one snapshot id correction did to the database.</summary>
    public enum SnapshotIdCorrectionOutcome
    {
        /// <summary>The row held the recorded id (or none) and now holds the correct one.</summary>
        Applied,

        /// <summary>The row already held the correct id, so the run was a repeat.</summary>
        AlreadyCorrect,

        /// <summary>
        /// The row holds neither the recorded id nor the correct one, so it was left alone.
        /// Someone has changed it since, and whoever did should say which value is right.
        /// </summary>
        CarriesAnotherId,

        /// <summary>
        /// No row carries that key. The correction is dead, and the collision it prevents will
        /// stop the next refresh instead.
        /// </summary>
        NoMatchingRow,
    }

    /// <summary>What one correction did, in a form the completion dialog can print.</summary>
    public sealed record SnapshotIdCorrectionReport(
        SnapshotIdCorrection Correction,
        SnapshotIdCorrectionOutcome Outcome,
        string? BsgId)
    {
        /// <summary>True when an operator has to look at this row before refreshing.</summary>
        public bool NeedsAttention =>
            Outcome is SnapshotIdCorrectionOutcome.CarriesAnotherId or SnapshotIdCorrectionOutcome.NoMatchingRow;

        public string Summary => Outcome switch
        {
            SnapshotIdCorrectionOutcome.Applied =>
                $"{Correction.ItemName}: external ID corrected to {Correction.CorrectBsgId}",
            SnapshotIdCorrectionOutcome.AlreadyCorrect =>
                $"{Correction.ItemName}: already carries {Correction.CorrectBsgId}",
            SnapshotIdCorrectionOutcome.CarriesAnotherId =>
                $"{Correction.ItemName}: carries {BsgId ?? "no ID"}, expected {Correction.RecordedBsgId ?? "no ID"} "
                + $"or the corrected {Correction.CorrectBsgId}; left alone",
            _ =>
                $"{Correction.ItemName}: no row under that key, so the ID was not corrected",
        };
    }

    /// <summary>An external game ID no snapshot can supply, carried by hand.</summary>
    /// <param name="QuestName">
    /// The name the row carries in the database being repaired. The bridge finds its row by this
    /// name <em>or</em> by <c>BsgId</c>, so it keeps working once the rename it exists for has
    /// been published and the row answers to a different name.
    /// </param>
    /// <param name="BsgId">The task ID that row should have.</param>
    public sealed record HandBridge(string QuestName, string BsgId);

    /// <summary>What one hand bridge did to the database.</summary>
    public enum HandBridgeOutcome
    {
        /// <summary>The row had no ID and now carries the bridged one.</summary>
        Applied,

        /// <summary>
        /// A row already carried exactly this ID, so the run was a repeat. That row need not
        /// still be named <c>QuestName</c>: once the rename this bridge exists for is published
        /// it is named the new title, and the ID is what identifies it.
        /// </summary>
        AlreadyBridged,

        /// <summary>
        /// The row carries a different ID and was left alone. Deliberate (the working database
        /// is the newer source) but worth confirming, because this row is the one the bridge
        /// exists for and it was not supposed to have an ID at all.
        /// </summary>
        IdAlreadyDiffers,

        /// <summary>
        /// No row carries that name and none carries the bridged ID either, so the bridge did
        /// nothing. The rename it exists for will mint a fresh key and every user's recorded
        /// completion of that quest is dropped.
        /// </summary>
        NoMatchingRow,
    }

    /// <summary>What one hand bridge did, in a form the operator can read.</summary>
    public sealed record HandBridgeReport(HandBridge Bridge, HandBridgeOutcome Outcome, string? ExistingBsgId)
    {
        /// <summary>True when the run cannot be called successful without a human looking.</summary>
        public bool NeedsAttention =>
            Outcome is HandBridgeOutcome.NoMatchingRow or HandBridgeOutcome.IdAlreadyDiffers;

        public string Summary => Outcome switch
        {
            HandBridgeOutcome.Applied =>
                $"{Bridge.QuestName} -> {Bridge.BsgId}",
            HandBridgeOutcome.AlreadyBridged =>
                $"{Bridge.QuestName}: already carried {Bridge.BsgId}, nothing to do",
            HandBridgeOutcome.IdAlreadyDiffers =>
                $"{Bridge.QuestName}: left alone, it carries {ExistingBsgId} and not the bridged {Bridge.BsgId}",
            HandBridgeOutcome.NoMatchingRow =>
                $"{Bridge.QuestName}: NO ROW OF THAT NAME AND NONE CARRYING {Bridge.BsgId}, "
                + "so it was not written. "
                + "A refresh will mint a fresh key for that quest and drop every recorded completion of it.",
            _ => $"{Bridge.QuestName}: {Outcome}",
        };
    }
}
