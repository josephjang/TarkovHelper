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
        // No Questions Asked, renamed to Special Order by 1.1, was published between the
        // snapshot and the January regeneration and so never carried an ID at all. Without this
        // it would be the one rename of the 92 whose progress is lost.
        var snapshot = CreateDatabase("snapshot.db", quests: Array.Empty<(string, string, string?)>());
        var working = CreateDatabase("working.db",
            quests: new[] { Row("q1", "No Questions Asked") });

        var result = await new BsgIdBackfillService().BackfillAsync(working, snapshot);

        Assert.Equal("68ee1c18b4e5bc9a68018cd7", ReadScalar(working, "SELECT BsgId FROM Quests WHERE Id = 'q1'"));
        Assert.Contains("No Questions Asked", Assert.Single(result.HandBridgesApplied));
        Assert.Equal(0, result.QuestsStillMissing);
    }

    [Fact]
    public async Task A_hand_bridge_does_not_overwrite_an_id_that_is_already_there()
    {
        var snapshot = CreateDatabase("snapshot.db", quests: Array.Empty<(string, string, string?)>());
        var working = CreateDatabase("working.db",
            quests: new[] { Row("q1", "No Questions Asked", "0000000000000000000000ff") });

        var result = await new BsgIdBackfillService().BackfillAsync(working, snapshot);

        Assert.Empty(result.HandBridgesApplied);
        Assert.Equal("0000000000000000000000ff", ReadScalar(working, "SELECT BsgId FROM Quests WHERE Id = 'q1'"));
    }

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
