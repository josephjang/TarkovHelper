using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The check opens the payload it downloaded to read its data format stamp. That handle is
/// the service's own, and this pins that closing it stays that way: the process wide SQLite
/// pool, where every other service's connections live, is not this service's to empty.
/// <para>
/// Serialized with <see cref="SchedulingSensitiveCollection"/> for a reason of its own. What
/// it observes is process global pool state, and a dozen suites beside it flush that pool
/// (<see cref="TestSqlite"/>, <see cref="TempStoreRoot"/>); run in parallel with them this
/// would fail on their timing rather than on the behavior under test.
/// </para>
/// </summary>
[Collection(SchedulingSensitiveCollection.Name)]
public sealed class DataChannelConnectionPoolTests : IDisposable
{
    private const string DatabaseFile = "tarkov_data.db";
    private const string VersionFile = "db_version.txt";

    private readonly TempStoreRoot _temp = new("datachannel-pool");

    private static int Pin => DatabaseUpdateService.DataFormatVersion;

    public void Dispose()
    {
        // This suite deliberately leaves a connection pooled, so the folder cannot go until
        // the handle does.
        SqliteConnection.ClearAllPools();
        _temp.Dispose();
    }

    private static byte[] NewStampedDatabase(int userVersion) => TestSqlite.BuildDatabase(
        $"CREATE TABLE Marker (Id INTEGER); PRAGMA user_version = {userVersion};");

    private string NewServedChannel(string version, byte[] database)
    {
        var root = _temp.NewFolder("served-channel");
        var endpoint = Path.Combine(root, $"v{Pin}");
        Directory.CreateDirectory(endpoint);
        File.WriteAllBytes(Path.Combine(endpoint, DatabaseFile), database);
        File.WriteAllText(Path.Combine(endpoint, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            dataFormatVersion = Pin,
            version,
            database = new
            {
                file = DatabaseFile,
                digest = TestDigest.Sha256Digest(database),
                size = (long?)database.Length,
            },
        }));
        File.WriteAllText(Path.Combine(root, "index.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            currentDataFormatVersion = Pin,
        }));
        return root;
    }

    private string NewInstalledAssets(string version, byte[] database)
    {
        var dir = _temp.NewFolder("install-assets");
        File.WriteAllText(Path.Combine(dir, VersionFile), version);
        File.WriteAllBytes(Path.Combine(dir, DatabaseFile), database);
        return dir;
    }

    /// <summary>A read the way every *DbService does one: open, query, dispose, pool.</summary>
    private static void ReadPooled(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        command.ExecuteScalar();
    }

    [Fact]
    public async Task Reading_a_payload_stamp_leaves_another_services_connection_pooled()
    {
        var other = Path.Combine(_temp.NewFolder("another-service"), "another.db");
        await File.WriteAllBytesAsync(other, NewStampedDatabase(Pin));

        // A check that reads the payload's stamp and then refuses it: nothing swaps, so the
        // stamp read is the only step in the whole check that opens SQLite at all.
        using var server = new LocalFileServer(NewServedChannel("2.0.0", NewStampedDatabase(Pin + 1)));
        var assets = NewInstalledAssets("1.0.10", Encoding.UTF8.GetBytes("older-installed-database"));
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        // Opened last on purpose: every fixture built through TestSqlite flushes the pool on
        // its way out, so a reader opened before them would be closed by the fixture rather
        // than by the code under test.
        ReadPooled(other);

        // The premise, asserted rather than assumed: the pool holds the file open after the
        // reader is disposed, so the same assertion below means something.
        Assert.Throws<IOException>(() => File.Delete(other));

        var result = await service.CheckAndUpdateAsync();

        Assert.False(result.Success);
        // Untouched: the check let go of its own handle without emptying a pool it does not
        // own. Flushing it drops the connections of seven *DbServices that had nothing to do
        // with this check, and did so even on the checks that install nothing.
        Assert.Throws<IOException>(() => File.Delete(other));
        // And its own handle is genuinely gone, which is what the flush used to buy: the
        // refused payload was deleted rather than left behind locked.
        Assert.False(File.Exists(Path.Combine(assets, DatabaseFile + ".tmp")));
    }
}
