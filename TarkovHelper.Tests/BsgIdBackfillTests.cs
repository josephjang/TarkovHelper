using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TarkovDBEditor.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The one-time repair that makes the 1.1 carry-over possible.
/// <para>
/// External game IDs have been NULL on every published quest and item since the January 2026
/// regeneration, which is why log sync has matched nothing for seven months and hideout item
/// requirements resolve nothing. The 1.0.7 snapshot still holds them under the same row keys.
/// Restoring them is also what lets the refresh recognise a renamed quest, so a refresh refuses
/// to start until this has run.
/// </para>
/// </summary>
public sealed class BsgIdBackfillTests : IDisposable
{
    private readonly string _directory;

    public BsgIdBackfillTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "bsgid-backfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Fills_the_rows_that_have_no_external_id()
    {
        var snapshot = CreateDatabase("snapshot.db",
            quests: new[] { Row("q1", "Stirrup", "5c0be13186f7746309d759c8") },
            items: new[] { Row("i1", "Roubles", "5449016a4bdc2d6f028b456f") });
        var working = CreateDatabase("working.db",
            quests: new[] { Row("q1", "Stirrup") },
            items: new[] { Row("i1", "Roubles") });

        var result = await new BsgIdBackfillService().BackfillAsync(working, snapshot);

        Assert.Equal(1, result.QuestsFilled);
        Assert.Equal(1, result.ItemsFilled);
        Assert.Equal(0, result.QuestsStillMissing);
        Assert.Equal("5c0be13186f7746309d759c8", ReadScalar(working, "SELECT BsgId FROM Quests WHERE Id = 'q1'"));
        Assert.Equal("5449016a4bdc2d6f028b456f", ReadScalar(working, "SELECT BsgId FROM Items WHERE Id = 'i1'"));
    }

    [Fact]
    public async Task Leaves_an_id_the_working_database_already_carries()
    {
        // The working database is the newer source; overwriting it would undo a correction made
        // in the editor since the snapshot was taken.
        var snapshot = CreateDatabase("snapshot.db",
            quests: new[] { Row("q1", "Stirrup", "0000000000000000000000ff") });
        var working = CreateDatabase("working.db",
            quests: new[] { Row("q1", "Stirrup", "5c0be13186f7746309d759c8") });

        var result = await new BsgIdBackfillService().BackfillAsync(working, snapshot);

        Assert.Equal(0, result.QuestsFilled);
        Assert.Equal("5c0be13186f7746309d759c8", ReadScalar(working, "SELECT BsgId FROM Quests WHERE Id = 'q1'"));
    }

    [Fact]
    public async Task Treats_an_empty_string_as_missing()
    {
        var snapshot = CreateDatabase("snapshot.db",
            quests: new[] { Row("q1", "Stirrup", "5c0be13186f7746309d759c8") });
        var working = CreateDatabase("working.db", quests: new[] { Row("q1", "Stirrup", "") });

        var result = await new BsgIdBackfillService().BackfillAsync(working, snapshot);

        Assert.Equal(1, result.QuestsFilled);
    }

    [Fact]
    public async Task A_row_the_snapshot_does_not_know_is_reported_as_still_missing()
    {
        var snapshot = CreateDatabase("snapshot.db", quests: Array.Empty<(string, string, string?)>());
        var working = CreateDatabase("working.db",
            quests: new[] { Row("q1", "Stirrup"), Row("q2", "Collector") });

        var result = await new BsgIdBackfillService().BackfillAsync(working, snapshot);

        Assert.Equal(0, result.QuestsFilled);
        Assert.Equal(2, result.QuestsStillMissing);
        Assert.Equal(2, result.QuestsTotal);
    }

    [Fact]
    public async Task Bridges_the_one_rename_no_snapshot_can_supply()
    {
        // No Questions Asked, renamed to Special Order by 1.1, is in the 1.0.7 snapshot under
        // the same key but with BsgId NULL: one of the 14 snapshot rows December's tarkov.dev
        // matching resolved no ID for, so there is nothing to copy. Without this it would be the
        // one rename of the 92 whose progress is lost.
        var snapshot = CreateDatabase("snapshot.db", quests: Array.Empty<(string, string, string?)>());
        var working = CreateDatabase("working.db",
            quests: new[] { Row("q1", "No Questions Asked") });

        var result = await new BsgIdBackfillService().BackfillAsync(working, snapshot);

        Assert.Equal("68ee1c18b4e5bc9a68018cd7", ReadScalar(working, "SELECT BsgId FROM Quests WHERE Id = 'q1'"));
        var bridge = Assert.Single(result.HandBridges);
        Assert.Equal(HandBridgeOutcome.Applied, bridge.Outcome);
        Assert.Equal("No Questions Asked", bridge.Bridge.QuestName);
        Assert.False(bridge.NeedsAttention);
        Assert.Empty(result.HandBridgesNeedingAttention);
        Assert.Equal(0, result.QuestsStillMissing);
    }

    [Fact]
    public async Task A_hand_bridge_does_not_overwrite_an_id_that_is_already_there()
    {
        var snapshot = CreateDatabase("snapshot.db", quests: Array.Empty<(string, string, string?)>());
        var working = CreateDatabase("working.db",
            quests: new[] { Row("q1", "No Questions Asked", "0000000000000000000000ff") });

        var result = await new BsgIdBackfillService().BackfillAsync(working, snapshot);

        // Left alone, but reported: this is the one row the bridge exists for, and it was not
        // supposed to carry an ID at all, so a different one is worth a human look.
        var bridge = Assert.Single(result.HandBridges);
        Assert.Equal(HandBridgeOutcome.IdAlreadyDiffers, bridge.Outcome);
        Assert.Equal("0000000000000000000000ff", bridge.ExistingBsgId);
        Assert.True(bridge.NeedsAttention);
        Assert.Equal("0000000000000000000000ff", ReadScalar(working, "SELECT BsgId FROM Quests WHERE Id = 'q1'"));
    }

    [Fact]
    public async Task A_hand_bridge_that_matches_no_row_is_reported_as_loudly_as_one_that_applies()
    {
        // The failure this catches: the bridge changes none of the counts the operator watches,
        // so a run where it matched nothing used to be indistinguishable from a successful one.
        // If the row is not bridged, the refresh mints a fresh key for that quest and every
        // user's recorded completion of it is deleted as stale.
        var snapshot = CreateDatabase("snapshot.db", quests: Array.Empty<(string, string, string?)>());
        var working = CreateDatabase("working.db", quests: new[] { Row("q1", "Stirrup") });

        var progressLines = new List<string>();
        var result = await new BsgIdBackfillService()
            .BackfillAsync(working, snapshot, progressLines.Add);

        var bridge = Assert.Single(result.HandBridges);
        Assert.Equal(HandBridgeOutcome.NoMatchingRow, bridge.Outcome);
        Assert.True(bridge.NeedsAttention);
        Assert.Single(result.HandBridgesNeedingAttention);
        Assert.Contains(progressLines, line => line.Contains("No Questions Asked"));
    }

    [Fact]
    public async Task Running_the_bridge_twice_is_reported_as_a_repeat_and_needs_no_attention()
    {
        var snapshot = CreateDatabase("snapshot.db", quests: Array.Empty<(string, string, string?)>());
        var working = CreateDatabase("working.db", quests: new[] { Row("q1", "No Questions Asked") });

        await new BsgIdBackfillService().BackfillAsync(working, snapshot);
        var second = await new BsgIdBackfillService().BackfillAsync(working, snapshot);

        var bridge = Assert.Single(second.HandBridges);
        Assert.Equal(HandBridgeOutcome.AlreadyBridged, bridge.Outcome);
        Assert.False(bridge.NeedsAttention);
        Assert.Empty(second.HandBridgesNeedingAttention);
    }

    [Fact]
    public async Task A_bridged_row_is_still_recognised_once_the_rename_it_exists_for_is_published()
    {
        // The name the bridge carries is the one this very refresh renames: from the first
        // published 1.1 database onwards the row is called "Special Order". Matched by name
        // alone the bridge would report NoMatchingRow on every later run and warn that a refresh
        // is about to drop the quest's recorded progress, which by then is the opposite of the
        // truth: the ID that saves that progress is already on the row. A signal that is
        // permanently loud and wrong is worse than none, because the operator learns to ignore
        // it and will ignore the real one.
        var snapshot = CreateDatabase("snapshot.db", quests: Array.Empty<(string, string, string?)>());
        var working = CreateDatabase("working.db",
            quests: new[] { Row("q1", "Special Order", "68ee1c18b4e5bc9a68018cd7") });

        var progressLines = new List<string>();
        var result = await new BsgIdBackfillService()
            .BackfillAsync(working, snapshot, progressLines.Add);

        var bridge = Assert.Single(result.HandBridges);
        Assert.Equal(HandBridgeOutcome.AlreadyBridged, bridge.Outcome);
        Assert.Equal("68ee1c18b4e5bc9a68018cd7", bridge.ExistingBsgId);
        Assert.False(bridge.NeedsAttention);
        Assert.Empty(result.HandBridgesNeedingAttention);
        Assert.DoesNotContain(progressLines, line => line.Contains("NO ROW"));
        Assert.Equal(0, result.QuestsStillMissing);
    }

    [Fact]
    public async Task The_bridged_id_is_never_written_onto_a_second_row_of_the_old_name()
    {
        // A database holding both the renamed row and a leftover under the old name. The ID
        // identifies one quest: writing it onto the second row would give two rows the same
        // external identity, and the resolver carries a row's key by that ID.
        var snapshot = CreateDatabase("snapshot.db", quests: Array.Empty<(string, string, string?)>());
        var working = CreateDatabase("working.db", quests: new[]
        {
            Row("q1", "Special Order", "68ee1c18b4e5bc9a68018cd7"),
            Row("q2", "No Questions Asked"),
        });

        var result = await new BsgIdBackfillService().BackfillAsync(working, snapshot);

        var bridge = Assert.Single(result.HandBridges);
        Assert.Equal(HandBridgeOutcome.AlreadyBridged, bridge.Outcome);
        Assert.False(bridge.NeedsAttention);
        Assert.Equal("68ee1c18b4e5bc9a68018cd7", ReadScalar(working, "SELECT BsgId FROM Quests WHERE Id = 'q1'"));
        Assert.Null(ReadScalar(working, "SELECT BsgId FROM Quests WHERE Id = 'q2'"));
    }

    /// <summary>
    /// The content guard the spec promises: every hand bridge matches exactly one row of the
    /// published database, matched the way the bridge matches it (named that, or already
    /// carrying that ID). Without this, a bridge whose name drifted would quietly do nothing,
    /// and the guard the refresh runs (<c>AssertPreviousDatabaseIsBackfilled</c>) tolerates one
    /// missing ID out of 488, so nothing downstream would notice either.
    /// <para>
    /// The ID half is not decoration. The one bridge in the list names the row this very refresh
    /// renames, so the published database stops holding a row of that name the moment the 1.1
    /// data ships; a name-only guard would go red on the publish it exists to protect, and the
    /// bridge would look dead while being perfectly alive.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_hand_bridge_matches_exactly_one_row_of_the_published_database()
    {
        var databasePath = PublishedDatabasePath();
        Assert.True(File.Exists(databasePath), $"{databasePath} is missing");

        var missing = new List<string>();
        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();
            foreach (var bridge in BsgIdBackfillService.HandBridgedQuestIds)
            {
                using var cmd = new SqliteCommand(
                    "SELECT COUNT(*) FROM Quests WHERE Name = @Name OR BsgId = @BsgId", connection);
                cmd.Parameters.AddWithValue("@Name", bridge.QuestName);
                cmd.Parameters.AddWithValue("@BsgId", bridge.BsgId);
                if (Convert.ToInt32(cmd.ExecuteScalar()) != 1)
                    missing.Add(bridge.QuestName);
            }
        }

        SqliteConnection.ClearAllPools();

        Assert.True(missing.Count == 0,
            "these hand bridges match no single published row, so they would silently write nothing and "
            + $"the renames they exist for would lose every recorded completion: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The published database the backfill is run against: the app's own bundled seed, which is
    /// the live data channel's file linked into the test output. Read through TestSeed rather
    /// than by restating data/v1, so this guard follows a data format bump instead of pinning a
    /// version that has been left behind.
    /// </summary>
    private static string PublishedDatabasePath() => TestSeed.DatabasePath;

    [Fact]
    public async Task A_missing_snapshot_fails_before_anything_is_written()
    {
        var working = CreateDatabase("working.db", quests: new[] { Row("q1", "Stirrup") });

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new BsgIdBackfillService().BackfillAsync(working, Path.Combine(_directory, "no-such.db")));

        Assert.Null(ReadScalar(working, "SELECT BsgId FROM Quests WHERE Id = 'q1'"));
    }

    [Fact]
    public async Task Reports_how_many_ids_the_snapshot_had_to_offer()
    {
        var snapshot = CreateDatabase("snapshot.db",
            quests: new[] { Row("q1", "Stirrup", "5c0be13186f7746309d759c8"), Row("q2", "Collector", "5c51aac186f77432ea65c552") },
            items: new[] { Row("i1", "Roubles", "5449016a4bdc2d6f028b456f") });
        var working = CreateDatabase("working.db", quests: new[] { Row("q1", "Stirrup") });

        var result = await new BsgIdBackfillService().BackfillAsync(working, snapshot);

        Assert.Equal(2, result.SnapshotQuestIds);
        Assert.Equal(1, result.SnapshotItemIds);
        Assert.Equal(1, result.QuestsFilled);
    }

    #region Fixtures

    /// <summary>
    /// A row literal whose external ID is nullable. Spelling it out here rather than inline:
    /// an array of plain string tuples infers a non-nullable element type and will not convert.
    /// </summary>
    private static (string Id, string Name, string? BsgId) Row(string id, string name, string? bsgId = null) =>
        (id, name, bsgId);

    private string CreateDatabase(
        string fileName,
        (string Id, string Name, string? BsgId)[]? quests = null,
        (string Id, string Name, string? BsgId)[]? items = null)
    {
        var path = Path.Combine(_directory, fileName);

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            Execute(connection, "CREATE TABLE Quests (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, BsgId TEXT)");
            Execute(connection, "CREATE TABLE Items (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, BsgId TEXT)");

            Insert(connection, "Quests", quests ?? Array.Empty<(string, string, string?)>());
            Insert(connection, "Items", items ?? Array.Empty<(string, string, string?)>());
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private static void Insert(SqliteConnection connection, string table, (string Id, string Name, string? BsgId)[] rows)
    {
        foreach (var (id, name, bsgId) in rows)
        {
            using var cmd = new SqliteCommand($"INSERT INTO {table} (Id, Name, BsgId) VALUES (@Id, @Name, @BsgId)", connection);
            cmd.Parameters.AddWithValue("@Id", id);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@BsgId", (object?)bsgId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = new SqliteCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    private static string? ReadScalar(string databasePath, string sql)
    {
        string? value;
        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var cmd = new SqliteCommand(sql, connection);
            var result = cmd.ExecuteScalar();
            value = result == null || result == DBNull.Value ? null : result.ToString();
        }

        SqliteConnection.ClearAllPools();
        return value;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    #endregion
}
