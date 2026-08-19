using System.IO;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Tests;

/// <summary>
/// The guard on the shared SQLite fixture builder, and it is the guard the three copies it
/// replaced never had: that the file is released before the bytes come back.
/// </summary>
public sealed class TestSqliteTests : IDisposable
{
    private readonly TempStoreRoot _temp = new("testsqlite-selftest");

    public void Dispose() => _temp.Dispose();

    /// <summary>The marker rows a fixture declares, so a test can tell one fixture from another.</summary>
    private static List<string> MarkersOf(string databasePath)
    {
        var markers = new List<string>();
        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Name FROM Marker ORDER BY Name";
            using var reader = command.ExecuteReader();
            while (reader.Read()) markers.Add(reader.GetString(0));
        }
        SqliteConnection.ClearAllPools();

        return markers;
    }

    /// <summary>Drops bytes on disk so they can be opened as the database they claim to be.</summary>
    private string AsFile(byte[] database)
    {
        var path = Path.Combine(_temp.NewFolder("built"), "under-test.db");
        File.WriteAllBytes(path, database);
        return path;
    }

    // The whole point of the helper: real bytes SQLite can open, carrying whatever the caller's
    // statements did. Byte arrays that merely look like databases would not survive the install
    // path these fixtures feed, which opens the payload.
    [Fact]
    public void A_built_database_carries_the_statements_it_was_given()
    {
        var bytes = TestSqlite.BuildDatabase(
            "CREATE TABLE Marker (Name TEXT); INSERT INTO Marker VALUES ('first'); PRAGMA user_version = 3;");

        var path = AsFile(bytes);
        Assert.Equal(["first"], MarkersOf(path));
        Assert.Equal(3, TestSqlite.ReadDataFormatStamp(path));
    }

    // The seed knob, which is what folds "stamp these existing bytes" into the same helper: the
    // statements are applied TO the seed, not instead of it.
    [Fact]
    public void A_seed_is_the_database_the_statements_are_applied_to()
    {
        var seed = TestSqlite.BuildDatabase("CREATE TABLE Marker (Name TEXT); INSERT INTO Marker VALUES ('seeded');");

        var stamped = TestSqlite.BuildDatabase("PRAGMA user_version = 7", seed: seed);

        var path = AsFile(stamped);
        Assert.Equal(["seeded"], MarkersOf(path));
        Assert.Equal(7, TestSqlite.ReadDataFormatStamp(path));
        // An unstamped seed reads as 0, so the stamp above is not something it already had.
        Assert.Equal(0, TestSqlite.ReadDataFormatStamp(AsFile(seed)));
    }

    // Zero bytes is a database with nothing in it, not a corrupt file: SQLite treats an empty
    // file as an empty database, so the empty seed has to behave like no seed at all.
    [Fact]
    public void An_empty_seed_builds_the_same_database_as_no_seed()
    {
        var fromEmpty = TestSqlite.BuildDatabase("CREATE TABLE Marker (Name TEXT);", seed: []);

        Assert.Empty(MarkersOf(AsFile(fromEmpty)));
        Assert.Equal(0, TestSqlite.ReadDataFormatStamp(AsFile(fromEmpty)));
    }

    // Not tautological, and the reason is the pooling: with ClearAllPools() removed from
    // BuildDatabaseAt this fails on Windows, because the connection the statements went through
    // is still in the pool holding the file open and Windows refuses to delete it. Every suite
    // that builds a fixture then replaces or deletes the file underneath it depends on this.
    [Fact]
    public void The_file_is_released_by_the_time_the_bytes_come_back()
    {
        var path = Path.Combine(_temp.NewFolder("released"), "fixture.db");

        var bytes = TestSqlite.BuildDatabaseAt(path, "CREATE TABLE Marker (Name TEXT);");

        Assert.NotEmpty(bytes);
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    // The same invariant on the reading side: the stamp reader opens the file too, and its
    // callers go on to overwrite or delete what they just read.
    [Fact]
    public void Reading_the_stamp_releases_the_file_too()
    {
        var path = AsFile(TestSqlite.BuildDatabase("PRAGMA user_version = 2"));

        Assert.Equal(2, TestSqlite.ReadDataFormatStamp(path));

        File.Delete(path);
        Assert.False(File.Exists(path));
    }
}
