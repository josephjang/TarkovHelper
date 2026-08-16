using System.IO;
using Microsoft.Data.Sqlite;
using TarkovDBEditor.Services;
// Aliased rather than importing the whole namespace: both projects have a Services
// namespace, and the published documents are deliberately read back through the app's
// own reader so the tool and the app cannot drift apart on the format.
using DatabaseUpdateService = TarkovHelper.Services.DatabaseUpdateService;

namespace TarkovHelper.Tests;

/// <summary>
/// Covers the publish side of the versioned data channel
/// (feature-versioned-data-channel.spec.md): which endpoint a publish writes, and the
/// rule that both format-1 endpoints leave a publish byte-identical.
///
/// DataChannelMirrorTests guards the repository's committed state; these guard the tool
/// that produces it, because a mirror the tool cannot repair is a red CI check with no
/// in-app way out.
/// </summary>
public sealed class DataPublishChannelTests : IDisposable
{
    private const string DatabaseFile = "tarkov_data.db";
    private const string VersionFile = "db_version.txt";

    private readonly TempStoreRoot _temp = new("datapublish");

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Real SQLite databases, not stand-in byte arrays: a publish now stamps the source
    /// with its data format version, so the fixtures have to be openable the way the editor's
    /// actual output is. The marker table just makes the two distinguishable.
    /// </summary>
    private byte[] NewDatabase(string marker)
    {
        var path = Path.Combine(_temp.NewFolder("db"), "built.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE Marker (Name TEXT); INSERT INTO Marker VALUES ('{marker}');";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        return File.ReadAllBytes(path);
    }

    private byte[] NewDb => _newDb ??= NewDatabase("freshly-built");
    private byte[] OldDb => _oldDb ??= NewDatabase("previously-published");

    /// <summary>
    /// NewDb as a previous publish would have left it on an endpoint: stamped with its
    /// data format. Fixtures that mean "the channel already holds this exact data" have
    /// to use this, because an unstamped copy is genuinely different data now.
    /// </summary>
    private byte[] PublishedNewDb => _publishedNewDb ??= Stamped(NewDb, dataFormatVersion: 1);

    private byte[]? _newDb;
    private byte[]? _oldDb;
    private byte[]? _publishedNewDb;

    private byte[] Stamped(byte[] database, int dataFormatVersion)
    {
        var path = Path.Combine(_temp.NewFolder("stamped"), "db.sqlite");
        File.WriteAllBytes(path, database);

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {dataFormatVersion}";
            command.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        return File.ReadAllBytes(path);
    }

    /// <summary>The editor's build output, i.e. what a publish reads from.</summary>
    private string NewSource(byte[]? database)
    {
        var dir = _temp.NewFolder("editor-output");
        if (database != null) File.WriteAllBytes(Path.Combine(dir, DatabaseFile), database);
        return dir;
    }

    /// <summary>
    /// What the source holds now. A publish stamps it first, so this is what the
    /// endpoints must end up containing, and comparing against the original bytes would
    /// be comparing against a file that no longer exists.
    /// </summary>
    private static byte[] SourceBytes(string sourceDir) =>
        File.ReadAllBytes(Path.Combine(sourceDir, DatabaseFile));

    private string NewRepo()
    {
        var root = _temp.NewFolder("repo");
        Directory.CreateDirectory(Path.Combine(root, "TarkovHelper", "Assets"));
        return root;
    }

    private static void WriteEndpoint(string dir, string version, byte[] database)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, DatabaseFile), database);
        File.WriteAllText(Path.Combine(dir, VersionFile), version);
    }

    /// <summary>Reads a published manifest through the app's own reader, as a client would.</summary>
    private static DatabaseUpdateService.DataChannelManifest ReadManifest(string channelDir)
    {
        var path = Path.Combine(channelDir, "manifest.json");
        Assert.True(File.Exists(path), $"{path} was not written");

        var manifest = DatabaseUpdateService.ParseManifest(File.ReadAllText(path));
        Assert.True(manifest != null, $"{path} is not readable by the app's manifest reader");
        return manifest!;
    }

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant();

    private static string ChannelDir(string repoRoot, int format) =>
        Path.Combine(repoRoot, "data", $"v{format}");

    private static string AssetsDir(string repoRoot) =>
        Path.Combine(repoRoot, "TarkovHelper", "Assets");

    private static void AssertSameBytes(string expected, string actual)
    {
        Assert.True(File.Exists(expected), $"{expected} is missing");
        Assert.True(File.Exists(actual), $"{actual} is missing");
        Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
    }

    #region Live format resolution

    [Fact]
    public void The_live_format_is_the_highest_channel_directory()
    {
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.0", OldDb);
        WriteEndpoint(ChannelDir(repo, 2), "2.0.0", OldDb);
        // Numeric, not lexicographic: v10 must outrank v9, which string ordering reverses.
        WriteEndpoint(ChannelDir(repo, 10), "10.0.0", OldDb);
        WriteEndpoint(ChannelDir(repo, 9), "9.0.0", OldDb);

        using var service = new DataPublishService(NewSource(NewDb), repo);

        Assert.Equal(10, service.GetLiveDataFormatVersion());
    }

    [Fact]
    public void Directories_that_are_not_format_directories_are_ignored()
    {
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.0", OldDb);
        Directory.CreateDirectory(Path.Combine(repo, "data", "vNext"));
        Directory.CreateDirectory(Path.Combine(repo, "data", "v"));
        Directory.CreateDirectory(Path.Combine(repo, "data", "v2beta"));

        using var service = new DataPublishService(NewSource(NewDb), repo);

        Assert.Equal(1, service.GetLiveDataFormatVersion());
    }

    [Fact]
    public async Task A_repo_without_a_channel_fails_the_comparison()
    {
        // Must not silently fall back to the Assets-only layout: that would publish data
        // to an endpoint no current build polls.
        var repo = NewRepo();
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.Success);
        Assert.Contains("data/v<N>", comparison.ErrorMessage);
    }

    #endregion

    #region Publishing format 1

    [Fact]
    public async Task Publishing_format_one_leaves_both_endpoints_identical()
    {
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10", OldDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        var source = NewSource(NewDb);
        using var service = new DataPublishService(source, repo);

        var comparison = await service.CompareAsync();
        Assert.True(comparison.Success);
        Assert.True(comparison.DbChanged);
        Assert.True(comparison.MirrorsToAssets);
        Assert.Equal(1, comparison.LiveDataFormatVersion);

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        AssertSameBytes(Path.Combine(ChannelDir(repo, 1), DatabaseFile), Path.Combine(AssetsDir(repo), DatabaseFile));
        AssertSameBytes(Path.Combine(ChannelDir(repo, 1), VersionFile), Path.Combine(AssetsDir(repo), VersionFile));
        Assert.Equal(SourceBytes(source), await File.ReadAllBytesAsync(Path.Combine(ChannelDir(repo, 1), DatabaseFile)));
        Assert.Equal("1.0.11", await File.ReadAllTextAsync(Path.Combine(ChannelDir(repo, 1), VersionFile)));
    }

    [Fact]
    public async Task A_publish_writes_a_manifest_that_describes_what_it_published()
    {
        // Round trip through the real reader: the tool and the app have to agree about
        // the document, and the hash has to be of the bytes actually published.
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10", OldDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        var source = NewSource(NewDb);
        using var service = new DataPublishService(source, repo);

        var comparison = await service.CompareAsync();
        var published = await service.PublishAsync(comparison, "1.0.11");
        Assert.True(published.Success, published.ErrorMessage);

        var manifest = ReadManifest(ChannelDir(repo, 1));
        Assert.Equal(1, manifest.DataFormatVersion);
        Assert.Equal("1.0.11", manifest.Version);
        Assert.Equal(DatabaseFile, manifest.Database.File);
        // Hashed against what was actually published, which is the stamped source.
        Assert.Equal(Sha256Hex(SourceBytes(source)), manifest.Database.Sha256);
        Assert.Equal(SourceBytes(source).Length, manifest.Database.Size);
    }

    [Fact]
    public async Task A_publish_points_the_channel_index_at_the_live_schema()
    {
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10", OldDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();
        await service.PublishAsync(comparison, "1.0.11");

        var index = DatabaseUpdateService.ParseIndex(
            await File.ReadAllTextAsync(Path.Combine(repo, "data", "index.json")));

        Assert.True(index != null, "data/index.json is not readable by the app's index reader");
        Assert.Equal(1, index!.CurrentDataFormatVersion);
    }

    [Fact]
    public async Task A_database_only_mirror_drift_is_publishable_and_repaired()
    {
        // The half-published commit: one endpoint moved, the other did not. The tool has
        // to be able to fix it, which means treating it as a change even though the
        // editor's own database is already what the channel holds.
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.DbChanged); // channel already holds these bytes
        Assert.False(comparison.MirrorInSync);
        Assert.True(comparison.MirrorNeedsRepair);
        Assert.True(comparison.HasAnyChanges, "a drifted mirror must leave something to publish");

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        AssertSameBytes(Path.Combine(ChannelDir(repo, 1), DatabaseFile), Path.Combine(AssetsDir(repo), DatabaseFile));
    }

    [Fact]
    public async Task A_version_only_mirror_drift_is_publishable_and_repaired()
    {
        // Same failure, the other half: identical databases, disagreeing stamps, which
        // would hand two builds different answers about the same bytes.
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "0.9.0", PublishedNewDb);
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.DbChanged);
        Assert.True(comparison.MirrorNeedsRepair);

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        AssertSameBytes(Path.Combine(ChannelDir(repo, 1), VersionFile), Path.Combine(AssetsDir(repo), VersionFile));
        Assert.Equal("1.0.11", await File.ReadAllTextAsync(Path.Combine(AssetsDir(repo), VersionFile)));
    }

    [Fact]
    public async Task An_in_sync_pair_with_no_new_data_has_nothing_to_publish()
    {
        // The assertion that keeps the two above honest: MirrorNeedsRepair must not be
        // true by construction, or "a drifted mirror is publishable" would prove nothing.
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.True(comparison.Success);
        Assert.False(comparison.DbChanged);
        Assert.True(comparison.MirrorInSync);
        Assert.False(comparison.MirrorNeedsRepair);
        Assert.False(comparison.HasAnyChanges);
    }

    [Fact]
    public async Task The_version_token_is_read_from_the_first_line()
    {
        // Trailing content in the stamp must not become part of the token, or the
        // suggested next version would be derived from something unparseable.
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10\n", NewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10\n", NewDb);
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.Equal("1.0.10", comparison.CurrentVersion);
        Assert.Equal("1.0.11", comparison.NewVersion);
    }

    #endregion

    #region Publishing a later format

    [Fact]
    public async Task Publishing_a_later_schema_leaves_the_superseded_endpoints_alone()
    {
        // Once schema 2 is live, schema 1 and its Assets mirror are history: a publish
        // must not touch either, or builds pinned to 1 would be handed data built for 2.
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10", OldDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        WriteEndpoint(ChannelDir(repo, 2), "2.0.0", OldDb);
        var source = NewSource(NewDb);
        using var service = new DataPublishService(source, repo);

        var comparison = await service.CompareAsync();
        Assert.Equal(2, comparison.LiveDataFormatVersion);
        Assert.False(comparison.MirrorsToAssets);
        Assert.False(comparison.MirrorNeedsRepair);

        var published = await service.PublishAsync(comparison, "2.0.1");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal(SourceBytes(source), await File.ReadAllBytesAsync(Path.Combine(ChannelDir(repo, 2), DatabaseFile)));
        Assert.Equal("2.0.1", await File.ReadAllTextAsync(Path.Combine(ChannelDir(repo, 2), VersionFile)));
        Assert.Equal(2, ReadManifest(ChannelDir(repo, 2)).DataFormatVersion);

        // Superseded endpoints are history: byte for byte, nothing about v1 moves. This
        // is what lets a left-behind build keep serving its last compatible data, and
        // why the index rather than an edit here is how it learns it was left behind.
        Assert.Equal(OldDb, await File.ReadAllBytesAsync(Path.Combine(ChannelDir(repo, 1), DatabaseFile)));
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(ChannelDir(repo, 1), VersionFile)));
        Assert.Equal(OldDb, await File.ReadAllBytesAsync(Path.Combine(AssetsDir(repo), DatabaseFile)));
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(AssetsDir(repo), VersionFile)));

        // And the index now names the new schema, which is the only thing that changed
        // outside data/v2.
        var index = DatabaseUpdateService.ParseIndex(
            await File.ReadAllTextAsync(Path.Combine(repo, "data", "index.json")));
        Assert.Equal(2, index!.CurrentDataFormatVersion);
    }

    #endregion
}
