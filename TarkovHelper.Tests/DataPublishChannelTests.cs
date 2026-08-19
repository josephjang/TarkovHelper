using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovDBEditor.Services;
// Aliased rather than importing the whole namespace: both projects have a Services
// namespace, and the published documents are deliberately read back through the app's
// own reader so the tool and the app cannot drift apart on the format.
using DataChannel = TarkovHelper.Services.DataChannel;

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
    private const string ManifestFile = "manifest.json";
    private const string IndexFile = "index.json";

    private readonly TempStoreRoot _temp = new("datapublish");

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Real SQLite databases, not stand-in byte arrays: a publish now stamps the source
    /// with its data format version, so the fixtures have to be openable the way the editor's
    /// actual output is. The marker table just makes the two distinguishable.
    /// </summary>
    private static byte[] NewDatabase(string marker) => TestSqlite.BuildDatabase(
        $"CREATE TABLE Marker (Name TEXT); INSERT INTO Marker VALUES ('{marker}');");

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

    private static byte[] Stamped(byte[] database, int dataFormatVersion) => TestSqlite.BuildDatabase(
        $"PRAGMA user_version = {dataFormatVersion}", seed: database);

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

    /// <summary>
    /// A channel endpoint as a completed publish leaves it: database, version stamp, and a
    /// manifest that describes them both. Fixtures that mean "this endpoint is fully
    /// published" have to use this, because a channel whose manifest does not match its
    /// database is itself a publishable change now.
    /// </summary>
    private static void WritePublishedChannel(string dir, int format, string version, byte[] database)
    {
        WriteEndpoint(dir, version, database);
        WriteManifest(dir, format, version, database);
    }

    /// <summary>
    /// <paramref name="version"/> is nullable so a manifest that records no version at
    /// all can be written: the app's reader rejects that document, so the tool has to
    /// report it rather than matching it against an equally absent db_version.txt.
    /// </summary>
    private static void WriteManifest(
        string dir, int format, string? version, byte[] database,
        string? digest = null, long? size = null, int schemaVersion = 1)
    {
        var manifest = new
        {
            schemaVersion,
            dataFormatVersion = format,
            version,
            database = new
            {
                file = DatabaseFile,
                digest = digest ?? TestDigest.Sha256Digest(database),
                size = size ?? database.LongLength,
            },
        };

        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, ManifestFile),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>
    /// data/index.json as a completed publish leaves it: the one channel document that
    /// lives above the endpoints, naming the format published right now. Fixtures that
    /// mean "this channel is fully published" have to write one, because an index that
    /// does not name the live format is itself a publishable change now.
    /// </summary>
    private static void WriteIndex(string repoRoot, int format, int schemaVersion = 1)
    {
        var index = new { schemaVersion, currentDataFormatVersion = format };

        var dir = Path.Combine(repoRoot, "data");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, IndexFile),
            JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string IndexPath(string repoRoot) => Path.Combine(repoRoot, "data", IndexFile);

    /// <summary>A map config in the build output, i.e. an asset that publishes to Assets only.</summary>
    private static void WriteMapConfig(string sourceDir, string json)
    {
        var path = Path.Combine(sourceDir, "Resources", "Data", "map_configs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>Reads a published manifest through the app's own reader, as a client would.</summary>
    private static DataChannel.Manifest ReadManifest(string channelDir)
    {
        var path = Path.Combine(channelDir, ManifestFile);
        Assert.True(File.Exists(path), $"{path} was not written");

        var manifest = DataChannel.ParseManifest(File.ReadAllText(path));
        Assert.True(manifest != null, $"{path} is not readable by the app's manifest reader");
        return manifest!;
    }

    private static string ChannelDir(string repoRoot, int format) =>
        Path.Combine(repoRoot, "data", $"v{format}");

    private static string AssetsDir(string repoRoot) =>
        Path.Combine(repoRoot, "TarkovHelper", "Assets");

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
        TestFiles.AssertSameBytes(Path.Combine(ChannelDir(repo, 1), DatabaseFile), Path.Combine(AssetsDir(repo), DatabaseFile));
        TestFiles.AssertSameBytes(Path.Combine(ChannelDir(repo, 1), VersionFile), Path.Combine(AssetsDir(repo), VersionFile));
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
        Assert.Equal(TestDigest.Sha256Digest(SourceBytes(source)), manifest.Database.Digest);
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

        var index = DataChannel.ParseIndex(
            await File.ReadAllTextAsync(IndexPath(repo)));

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
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        using var service = new DataPublishService(NewSource(PublishedNewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.DbChanged); // channel already holds these bytes
        Assert.False(comparison.ManifestNeedsRepair); // and the manifest still describes them
        Assert.Equal(MirrorSyncState.Drifted, comparison.Mirror);
        Assert.True(comparison.MirrorNeedsRepair);
        Assert.True(comparison.HasAnyChanges, "a drifted mirror must leave something to publish");

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        TestFiles.AssertSameBytes(Path.Combine(ChannelDir(repo, 1), DatabaseFile), Path.Combine(AssetsDir(repo), DatabaseFile));
        // Repairing a mirror republishes bytes the channel already served, so the token
        // that identifies those bytes must not move.
        Assert.Equal("1.0.10", published.NewVersion);
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(ChannelDir(repo, 1), VersionFile)));
    }

    [Fact]
    public async Task A_version_only_mirror_drift_is_publishable_and_repaired()
    {
        // Same failure, the other half: identical databases, disagreeing stamps, which
        // would hand two builds different answers about the same bytes.
        var repo = NewRepo();
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "0.9.0", PublishedNewDb);
        using var service = new DataPublishService(NewSource(PublishedNewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.DbChanged);
        Assert.True(comparison.MirrorNeedsRepair);

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        TestFiles.AssertSameBytes(Path.Combine(ChannelDir(repo, 1), VersionFile), Path.Combine(AssetsDir(repo), VersionFile));
        // The channel is the authority, and its data did not change: the drifted mirror is
        // pulled back to the token the channel already published rather than both being
        // bumped, which would make every install re-download an identical database.
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(AssetsDir(repo), VersionFile)));
        Assert.Equal("1.0.10", ReadManifest(ChannelDir(repo, 1)).Version);
    }

    [Fact]
    public async Task An_in_sync_pair_with_no_new_data_has_nothing_to_publish()
    {
        // The assertion that keeps the two above honest: MirrorNeedsRepair must not be
        // true by construction, or "a drifted mirror is publishable" would prove nothing.
        var repo = NewRepo();
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        WriteIndex(repo, 1);
        using var service = new DataPublishService(NewSource(PublishedNewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.True(comparison.Success);
        Assert.False(comparison.DbChanged);
        Assert.Equal(MirrorSyncState.InSync, comparison.Mirror);
        Assert.False(comparison.MirrorNeedsRepair);
        Assert.False(comparison.ManifestNeedsRepair);
        Assert.False(comparison.DbWillPublish);
        Assert.False(comparison.HasAnyChanges);
    }

    [Fact]
    public async Task A_source_that_still_has_to_be_stamped_counts_as_a_change()
    {
        // The database the editor just built declares no data format yet. Publishing will
        // stamp it, so the bytes the endpoint receives are not the bytes on disk now, and
        // reporting "no changes" would strand an unstamped database on the endpoint.
        var repo = NewRepo();
        var unstamped = NewDb;
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", unstamped);
        WriteEndpoint(AssetsDir(repo), "1.0.10", unstamped);
        var source = NewSource(unstamped);
        using var service = new DataPublishService(source, repo);

        var comparison = await service.CompareAsync();

        Assert.Equal(0, comparison.SourceDataFormatStamp);
        Assert.True(comparison.DbChanged, "an unstamped source is not what the endpoint will end up holding");

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal(1, TestSqlite.ReadDataFormatStamp(Path.Combine(ChannelDir(repo, 1), DatabaseFile)));
        Assert.Equal("1.0.11", published.NewVersion);
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

    [Fact]
    public async Task An_endpoint_with_no_version_token_reports_none_rather_than_a_placeholder()
    {
        // "0.0.0" would be indistinguishable from a real token, and a publish keeps the
        // current token whenever the database is not being replaced.
        var repo = NewRepo();
        Directory.CreateDirectory(ChannelDir(repo, 1));
        File.WriteAllBytes(Path.Combine(ChannelDir(repo, 1), DatabaseFile), PublishedNewDb);
        using var service = new DataPublishService(NewSource(PublishedNewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.Null(comparison.CurrentVersion);
        Assert.Equal("1.0.0", comparison.NewVersion);
        Assert.Equal("2.5.0", comparison.ResolvePublishVersion("2.5.0"));
    }

    #endregion

    #region Comparison writes nothing

    [Fact]
    public async Task Comparing_never_modifies_the_source_database()
    {
        // Opening or refreshing the publish window used to stamp the build output in
        // place, and SQLite bumps the header change counter on every commit, so the file
        // changed even when the stamp was already correct. That made a comparison a
        // change to publish, and a fresh multi-megabyte binary diff per publish.
        var repo = NewRepo();
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        var source = NewSource(PublishedNewDb);
        var before = SourceBytes(source);
        using var service = new DataPublishService(source, repo);

        await service.CompareAsync();
        await service.CompareAsync();
        await service.CompareAsync();

        Assert.Equal(before, SourceBytes(source));
    }

    [Fact]
    public async Task Comparing_never_modifies_an_unstamped_source_database()
    {
        // The other half: a source that genuinely needs stamping is still left alone
        // until a publish asks for it.
        var repo = NewRepo();
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", OldDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        var source = NewSource(NewDb);
        var before = SourceBytes(source);
        using var service = new DataPublishService(source, repo);

        var comparison = await service.CompareAsync();

        Assert.True(comparison.Success);
        Assert.Equal(before, SourceBytes(source));
        Assert.Equal(0, TestSqlite.ReadDataFormatStamp(Path.Combine(source, DatabaseFile)));
    }

    [Fact]
    public async Task Publishing_twice_leaves_nothing_to_publish()
    {
        // The bug this guards: the publish window re-compares as soon as a publish
        // finishes. A comparison that re-stamped the source made the freshly published
        // database look changed again, so the tool reported a pending change forever and
        // every publish committed a new copy of an identical database.
        var repo = NewRepo();
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", OldDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        var source = NewSource(NewDb);
        using var service = new DataPublishService(source, repo);

        var published = await service.PublishAsync(await service.CompareAsync(), "1.0.11");
        Assert.True(published.Success, published.ErrorMessage);

        var afterPublish = SourceBytes(source);
        var recomparison = await service.CompareAsync();

        Assert.True(recomparison.Success);
        Assert.False(recomparison.DbChanged, "the database that was just published is not a pending change");
        Assert.False(recomparison.MirrorNeedsRepair);
        Assert.False(recomparison.ManifestNeedsRepair);
        Assert.False(recomparison.HasAnyChanges);
        Assert.Equal("1.0.11", recomparison.CurrentVersion);

        // And a second publish of the same data is a no-op on disk, not another binary diff.
        Assert.True((await service.PublishAsync(recomparison, "1.0.12")).Success);
        Assert.Equal(afterPublish, SourceBytes(source));
        Assert.Equal(afterPublish, await File.ReadAllBytesAsync(Path.Combine(ChannelDir(repo, 1), DatabaseFile)));
        Assert.Equal("1.0.11", await File.ReadAllTextAsync(Path.Combine(ChannelDir(repo, 1), VersionFile)));
    }

    #endregion

    #region The version token describes the database

    [Fact]
    public async Task An_asset_only_publish_keeps_the_version_token()
    {
        // A map-config or icon change ships inside an app release; the database on the
        // endpoint is untouched. Bumping the token anyway would make every install in the
        // field download a byte-identical multi-megabyte database, because the client
        // decides on the token alone.
        var repo = NewRepo();
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        // The channel index is part of "already published": without it the database
        // section has a repair of its own to do, and this test is about the case where
        // it has none.
        WriteIndex(repo, 1);
        var source = NewSource(PublishedNewDb);
        WriteMapConfig(source, "{\"maps\":[]}");
        using var service = new DataPublishService(source, repo);

        var comparison = await service.CompareAsync();
        Assert.False(comparison.DbWillPublish);
        Assert.True(comparison.MapConfigsChanged);
        Assert.True(comparison.HasAnyChanges);

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal("1.0.10", published.NewVersion);
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(ChannelDir(repo, 1), VersionFile)));
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(AssetsDir(repo), VersionFile)));
        Assert.Equal("1.0.10", ReadManifest(ChannelDir(repo, 1)).Version);
        // The map config did get published; only the database's identity stayed put.
        Assert.Equal(
            "{\"maps\":[]}",
            await File.ReadAllTextAsync(Path.Combine(AssetsDir(repo), "DB", "Data", "map_configs.json")));
    }

    [Fact]
    public async Task A_publish_that_replaces_the_database_uses_the_requested_token()
    {
        var repo = NewRepo();
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", OldDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();
        Assert.True(comparison.DbChanged);

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal("1.0.11", published.NewVersion);
        Assert.Equal("1.0.11", await File.ReadAllTextAsync(Path.Combine(ChannelDir(repo, 1), VersionFile)));
        Assert.Equal("1.0.11", ReadManifest(ChannelDir(repo, 1)).Version);
    }

    [Fact]
    public async Task A_repair_keeps_the_token_the_manifest_publishes_when_the_version_file_is_gone()
    {
        // Half-applied publish, or a hand-deleted db_version.txt: the manifest still names
        // the token every install in the field is bookmarked against. Falling back to the
        // operator's suggestion would move the channel's version history backwards, make
        // the whole fleet re-download a byte-identical database, and re-issue tokens
        // clients have already seen.
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        WritePublishedChannel(channel, 1, "1.0.10", PublishedNewDb);
        File.Delete(Path.Combine(channel, VersionFile));
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        using var service = new DataPublishService(NewSource(PublishedNewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.DbChanged);
        Assert.Null(comparison.CurrentVersion);
        Assert.Equal("1.0.10", comparison.ManifestVersion);
        Assert.Equal("1.0.10", comparison.PublishedVersion);
        // The suggestion follows the token the endpoint actually publishes, not 1.0.0.
        Assert.Equal("1.0.11", comparison.NewVersion);
        Assert.Equal("1.0.10", comparison.ResolvePublishVersion("2.5.0"));
        Assert.True(comparison.ManifestNeedsRepair);

        var published = await service.PublishAsync(comparison, "2.5.0");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal("1.0.10", published.NewVersion);
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(channel, VersionFile)));
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(AssetsDir(repo), VersionFile)));
        Assert.Equal("1.0.10", ReadManifest(channel).Version);
        Assert.False((await service.CompareAsync()).HasAnyChanges);
    }

    [Fact]
    public async Task A_manifest_with_no_version_is_drift_even_when_the_endpoint_has_no_token()
    {
        // Two absent tokens compare equal, so this used to read as healthy while the app's
        // own reader rejected the same document and every install stopped updating.
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        WritePublishedChannel(channel, 1, "1.0.10", PublishedNewDb);
        File.Delete(Path.Combine(channel, VersionFile));
        WriteManifest(channel, 1, null, PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        using var service = new DataPublishService(NewSource(PublishedNewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.Null(comparison.CurrentVersion);
        Assert.Null(comparison.ManifestVersion);
        Assert.True(comparison.ManifestNeedsRepair, "a manifest the app cannot read must be publishable");
        Assert.Contains("no version", comparison.ManifestDriftReason);
        Assert.True(comparison.HasAnyChanges);

        // Neither document names a token, so this is the one case where the operator's
        // suggestion is all there is to go on.
        var published = await service.PublishAsync(comparison, "2.5.0");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal("2.5.0", published.NewVersion);
        Assert.Equal("2.5.0", ReadManifest(channel).Version);
        Assert.Equal("2.5.0", await File.ReadAllTextAsync(Path.Combine(channel, VersionFile)));
        Assert.False((await service.CompareAsync()).HasAnyChanges);
    }

    [Fact]
    public void The_suggested_token_always_follows_the_one_it_is_derived_from()
    {
        // Derived rather than stored: a caller that sets the current token cannot be left
        // holding a suggestion that no longer follows from it.
        var result = new DataPublishService.ComparisonResult { CurrentVersion = "1.2.3" };
        Assert.Equal("1.2.4", result.NewVersion);

        result.CurrentVersion = "2.0.9";
        Assert.Equal("2.0.10", result.NewVersion);

        // Nothing published anywhere, and an unparseable token, both start over.
        result.CurrentVersion = null;
        Assert.Equal("1.0.0", result.NewVersion);

        result.CurrentVersion = "not-a-version";
        Assert.Equal("1.0.0", result.NewVersion);
    }

    #endregion

    #region Manifest drift

    [Fact]
    public async Task A_manifest_that_no_longer_describes_the_database_is_publishable_and_repaired()
    {
        // CI asserts the committed manifest against the committed database. Without this,
        // a drifted manifest is a red check with the Publish button disabled: a failure
        // the operator is told about and given no way to clear.
        var repo = NewRepo();
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", PublishedNewDb);
        WriteManifest(ChannelDir(repo, 1), 1, "1.0.10", PublishedNewDb, digest: TestDigest.Sha256Digest(OldDb));
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        using var service = new DataPublishService(NewSource(PublishedNewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.DbChanged);
        Assert.False(comparison.MirrorNeedsRepair);
        Assert.True(comparison.ManifestNeedsRepair);
        Assert.Contains("digest", comparison.ManifestDriftReason);
        Assert.True(comparison.HasAnyChanges, "a manifest CI rejects must leave something to publish");

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        var manifest = ReadManifest(ChannelDir(repo, 1));
        Assert.Equal(TestDigest.Sha256Digest(PublishedNewDb), manifest.Database.Digest);
        Assert.Equal("1.0.10", manifest.Version); // repair, not a new release
        Assert.False((await service.CompareAsync()).HasAnyChanges);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("size")]
    [InlineData("version")]
    [InlineData("format")]
    [InlineData("schema")]
    [InlineData("no-digest")]
    [InlineData("no-version")]
    [InlineData("stamp")]
    public async Task Every_way_the_manifest_can_stop_matching_is_publishable(string drift)
    {
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        WritePublishedChannel(channel, 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);

        switch (drift)
        {
            case "missing":
                File.Delete(Path.Combine(channel, ManifestFile));
                break;
            case "unreadable":
                await File.WriteAllTextAsync(Path.Combine(channel, ManifestFile), "{ not json");
                break;
            case "size":
                WriteManifest(channel, 1, "1.0.10", PublishedNewDb, size: PublishedNewDb.LongLength + 1);
                break;
            case "version":
                WriteManifest(channel, 1, "0.9.0", PublishedNewDb);
                break;
            case "format":
                WriteManifest(channel, 7, "1.0.10", PublishedNewDb);
                break;
            case "schema":
                WriteManifest(channel, 1, "1.0.10", PublishedNewDb, schemaVersion: 9);
                break;
            case "no-digest":
                WriteManifest(channel, 1, "1.0.10", PublishedNewDb, digest: "");
                break;
            case "no-version":
                WriteManifest(channel, 1, null, PublishedNewDb);
                break;
            case "stamp":
                // The manifest describes the database faithfully, but the database
                // declares a data format no client of this endpoint reads.
                WritePublishedChannel(channel, 1, "1.0.10", Stamped(PublishedNewDb, 7));
                break;
        }

        using var service = new DataPublishService(NewSource(PublishedNewDb), repo);

        var comparison = await service.CompareAsync();
        Assert.True(comparison.ManifestNeedsRepair, $"{drift} drift went undetected");
        Assert.False(string.IsNullOrWhiteSpace(comparison.ManifestDriftReason));

        Assert.True((await service.PublishAsync(comparison, "1.0.11")).Success);
        Assert.False((await service.CompareAsync()).HasAnyChanges, $"{drift} drift survived a publish");
    }

    #endregion

    #region Channel index drift

    [Theory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("schema")]
    [InlineData("format")]
    [InlineData("renamed")]
    public async Task Every_way_the_channel_index_can_stop_matching_is_publishable(string drift)
    {
        // data/index.json is the one channel document above the endpoints, and CI guards
        // it the same way it guards the manifest. Without a check here, a drifted index is
        // a red build with the Publish button disabled: nothing else in the comparison
        // looks at it, so the tool would report the channel as fully healthy.
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        WritePublishedChannel(channel, 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        WriteIndex(repo, 1);

        switch (drift)
        {
            case "missing":
                File.Delete(IndexPath(repo));
                break;
            case "unreadable":
                await File.WriteAllTextAsync(IndexPath(repo), "{ not json");
                break;
            case "schema":
                WriteIndex(repo, 1, schemaVersion: 9);
                break;
            case "format":
                WriteIndex(repo, 7);
                break;
            case "renamed":
                // The field name is the contract. A renamed pointer deserializes to 0,
                // which is no format at all, so every build polling this channel would
                // read itself as superseded by nothing.
                await File.WriteAllTextAsync(
                    IndexPath(repo),
                    "{\n  \"schemaVersion\": 1,\n  \"currentSchema\": 1\n}\n");
                break;
        }

        using var service = new DataPublishService(NewSource(PublishedNewDb), repo);

        var comparison = await service.CompareAsync();
        // Nothing else in the channel drifted, so the index is what has to carry this
        // publish: without those three, HasAnyChanges below would prove nothing.
        Assert.False(comparison.DbChanged);
        Assert.False(comparison.MirrorNeedsRepair);
        Assert.False(comparison.ManifestNeedsRepair, comparison.ManifestDriftReason);
        Assert.True(comparison.IndexNeedsRepair, $"{drift} drift went undetected");
        Assert.False(string.IsNullOrWhiteSpace(comparison.IndexDriftReason));
        // The gate the operator actually meets: the Publish button follows HasAnyChanges,
        // and the database section's icon follows DbWillPublish.
        Assert.True(comparison.DbWillPublish);
        Assert.True(comparison.HasAnyChanges, "an index CI rejects must leave something to publish");

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        // Repairing the index republishes bytes the channel already served, so the token
        // that identifies those bytes must not move.
        Assert.Equal("1.0.10", published.NewVersion);
        var repaired = DataChannel.ParseIndex(await File.ReadAllTextAsync(IndexPath(repo)));
        Assert.True(repaired != null, "the repaired index is not readable by the app's index reader");
        Assert.Equal(1, repaired!.CurrentDataFormatVersion);
        Assert.False((await service.CompareAsync()).HasAnyChanges, $"{drift} drift survived a publish");
    }

    [Fact]
    public async Task An_index_naming_the_live_format_is_not_a_change()
    {
        // Keeps the theory above honest: IndexNeedsRepair must not be true by
        // construction, or "a drifted index is publishable" would prove nothing. Compared
        // by parsed fields rather than rendered bytes, so an index committed with the
        // other line ending is still in sync.
        var repo = NewRepo();
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        await File.WriteAllTextAsync(
            IndexPath(repo), "{\r\n  \"schemaVersion\": 1,\r\n  \"currentDataFormatVersion\": 1\r\n}\r\n");
        using var service = new DataPublishService(NewSource(PublishedNewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.Null(comparison.IndexDriftReason);
        Assert.False(comparison.IndexNeedsRepair);
        Assert.False(comparison.DbWillPublish);
        Assert.False(comparison.HasAnyChanges);
    }

    #endregion

    #region Version tokens the app can read

    [Theory]
    [InlineData("1.0 beta")]
    [InlineData("1.0.11 (hotfix)")]
    [InlineData("1.0.11\tfinal")]
    public async Task A_version_token_no_client_can_read_is_publishable_drift(string token)
    {
        // The premise, pinned through the app's own reader: a manifest carrying this token
        // is one every install refuses outright, so the endpoint stops updating the whole
        // fleet and CI goes red. Both endpoint documents agree on the token, so nothing
        // else in the comparison has anything to report.
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        WritePublishedChannel(channel, 1, token, PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), token, PublishedNewDb);
        WriteIndex(repo, 1);

        Assert.Null(DataChannel.ParseManifest(
            await File.ReadAllTextAsync(Path.Combine(channel, ManifestFile))));

        using var service = new DataPublishService(NewSource(PublishedNewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.DbChanged);
        Assert.False(comparison.MirrorNeedsRepair);
        Assert.True(comparison.ManifestNeedsRepair, "a token the app's own reader rejects must be publishable");
        Assert.Contains("no client can read", comparison.ManifestDriftReason);
        Assert.True(comparison.HasAnyChanges);
        // The broken token must not be carried forward, or every repair would republish
        // the same unreadable channel and the tool could never clear itself.
        Assert.Null(comparison.PublishedVersion);
        // No install ever accepted this token, so it is not version history to follow:
        // the suggestion starts over rather than incrementing something unreadable.
        Assert.Equal("1.0.0", comparison.NewVersion);
        Assert.Equal("1.0.11", comparison.ResolvePublishVersion("1.0.11"));

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal("1.0.11", published.NewVersion);
        Assert.Equal("1.0.11", ReadManifest(channel).Version);
        Assert.Equal("1.0.11", await File.ReadAllTextAsync(Path.Combine(channel, VersionFile)));
        Assert.Equal("1.0.11", await File.ReadAllTextAsync(Path.Combine(AssetsDir(repo), VersionFile)));
        Assert.False((await service.CompareAsync()).HasAnyChanges);
    }

    [Fact]
    public async Task A_publish_refuses_a_token_the_app_cannot_read()
    {
        // The publish window takes the token as free text, so this is one keystroke away.
        // Writing it would leave a manifest the app's reader rejects: every install stops
        // updating, CI goes red, and the tool would then report the channel it had just
        // written as healthy, with no way out of the editor.
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        WritePublishedChannel(channel, 1, "1.0.10", OldDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        WriteIndex(repo, 1);
        var source = NewSource(NewDb);
        using var service = new DataPublishService(source, repo);

        var comparison = await service.CompareAsync();
        Assert.True(comparison.DbChanged, "the requested token is only used when the database is replaced");
        var sourceBefore = SourceBytes(source);

        var published = await service.PublishAsync(comparison, "1.0 beta");

        Assert.False(published.Success);
        Assert.Contains("not a version token", published.ErrorMessage);
        Assert.Contains("1.0 beta", published.ErrorMessage);
        // Refused before the first byte: nothing copied, and even the source database is
        // left unstamped, so the tree is exactly the one the publish started with.
        Assert.Empty(published.CopiedFiles);
        Assert.Equal(sourceBefore, SourceBytes(source));
        Assert.Equal(OldDb, await File.ReadAllBytesAsync(Path.Combine(channel, DatabaseFile)));
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(channel, VersionFile)));

        // And a token the app can read still publishes, so the refusal is about the token
        // rather than about this comparison.
        Assert.True((await service.PublishAsync(comparison, "1.0.11")).Success);
        Assert.Equal("1.0.11", ReadManifest(channel).Version);
    }

    [Theory]
    [InlineData("1.0.11")]
    [InlineData("2026.7.0-rc.1+build.5")]
    [InlineData("1.0 beta")]
    [InlineData("1.0.11\n2.0.0")]
    [InlineData("../v2/1.0.0")]
    public async Task A_token_the_editor_publishes_is_a_token_the_app_can_read(string token)
    {
        // The two allowlists live in projects that cannot reference each other, so this is
        // what keeps them one rule: whatever the editor agrees to write, the app's reader
        // has to accept, and whatever the editor refuses, the reader has to reject. A copy
        // that learns a rule the other never hears about is how the channel breaks.
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        WritePublishedChannel(channel, 1, "1.0.10", OldDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        WriteIndex(repo, 1);
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();
        Assert.True(comparison.DbChanged);

        var published = await service.PublishAsync(comparison, token);

        Assert.Equal(DataChannel.IsBareVersionToken(token), published.Success);
        if (published.Success)
        {
            // ReadManifest goes through the app's reader, so an accepted token is proven
            // readable rather than merely written.
            Assert.Equal(token, ReadManifest(channel).Version);
        }
    }

    #endregion

    #region No source database

    [Fact]
    public async Task A_publish_with_no_database_at_either_end_is_refused_before_it_writes_anything()
    {
        // An empty format directory (which a format bump creates) plus a build output
        // without a database. The manifest step hashes the endpoint database, so this
        // publish cannot finish; failing at the end used to leave icons copied, no
        // manifest, no version stamp and no index.
        var repo = NewRepo();
        Directory.CreateDirectory(ChannelDir(repo, 1));
        var source = NewSource(null);
        WriteMapConfig(source, "{\"maps\":[]}");
        using var service = new DataPublishService(source, repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.Success);
        Assert.Contains("No database to publish", comparison.ErrorMessage);

        // Driven past the comparison with work queued that a publish would otherwise copy
        // first, the publish still refuses before touching the tree.
        var forced = new DataPublishService.ComparisonResult
        {
            Success = true,
            LiveDataFormatVersion = 1,
            ChannelDirPath = ChannelDir(repo, 1),
            MirrorsToAssets = true,
            DbExists = false,
            MapConfigsChanged = true,
        };
        var published = await service.PublishAsync(forced, "1.0.11");

        Assert.False(published.Success);
        Assert.Contains("No database to publish", published.ErrorMessage);
        // One rule, one wording: the comparison and the publish must not be able to
        // explain the same refusal differently.
        Assert.Equal(comparison.ErrorMessage, published.ErrorMessage);
        Assert.Empty(published.CopiedFiles);
        Assert.False(File.Exists(Path.Combine(ChannelDir(repo, 1), ManifestFile)));
        Assert.False(File.Exists(Path.Combine(ChannelDir(repo, 1), VersionFile)));
        Assert.False(File.Exists(IndexPath(repo)));
        Assert.False(File.Exists(Path.Combine(AssetsDir(repo), "DB", "Data", "map_configs.json")));
    }

    [Fact]
    public async Task A_source_without_a_database_can_still_repair_the_endpoint_documents()
    {
        // The endpoint already holds a database, so the documents that describe it can be
        // rewritten from it. Nothing reaches for a source database that is not there.
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        WriteEndpoint(channel, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        using var service = new DataPublishService(NewSource(null), repo);

        var comparison = await service.CompareAsync();

        Assert.True(comparison.Success);
        Assert.False(comparison.DbExists);
        Assert.False(comparison.DbChanged);
        Assert.True(comparison.ManifestNeedsRepair);
        // Both files compared belong to the repository, so the pair is still judged with
        // no build output present: here they genuinely hold the same bytes, and when they
        // do not the publish repairs the mirror from the endpoint.
        Assert.Equal(MirrorSyncState.InSync, comparison.Mirror);

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal(TestDigest.Sha256Digest(PublishedNewDb), ReadManifest(channel).Database.Digest);
        Assert.Equal(PublishedNewDb, await File.ReadAllBytesAsync(Path.Combine(channel, DatabaseFile)));
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(channel, VersionFile)));
    }

    [Fact]
    public async Task A_publish_without_a_source_stamps_the_endpoint_database()
    {
        // The endpoint copy is the database this publish describes, so it is the one that
        // has to carry the data format stamp. The app refuses a payload without one, on
        // the stated grounds that every publish writes one before hashing, so leaving it
        // unstamped makes every install re-fetch the whole payload hourly forever while
        // the tool reports nothing to publish.
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        var unstamped = NewDb;
        WritePublishedChannel(channel, 1, "1.0.10", unstamped);
        WriteEndpoint(AssetsDir(repo), "1.0.10", unstamped);
        using var service = new DataPublishService(NewSource(null), repo);

        var comparison = await service.CompareAsync();

        Assert.True(comparison.Success, comparison.ErrorMessage);
        Assert.False(comparison.DbExists);
        Assert.True(comparison.ManifestNeedsRepair, "an unstamped endpoint database is a payload every client refuses");
        Assert.Contains("data format stamp", comparison.ManifestDriftReason);
        Assert.True(comparison.HasAnyChanges);

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        var channelDb = Path.Combine(channel, DatabaseFile);
        Assert.Equal(1, TestSqlite.ReadDataFormatStamp(channelDb));
        // Stamping rewrites the file, so the manifest has to describe the stamped bytes
        // rather than the ones that were there when the comparison ran.
        var publishedBytes = await File.ReadAllBytesAsync(channelDb);
        Assert.Equal(TestDigest.Sha256Digest(publishedBytes), ReadManifest(channel).Database.Digest);
        Assert.Equal(publishedBytes.LongLength, ReadManifest(channel).Database.Size);
        // And the mirror still serves the same bytes, stamp included.
        TestFiles.AssertSameBytes(channelDb, Path.Combine(AssetsDir(repo), DatabaseFile));
        Assert.Equal("1.0.10", published.NewVersion); // a repair, not a new release
        Assert.False((await service.CompareAsync()).HasAnyChanges, "the stamped endpoint is fully described");
    }

    [Fact]
    public async Task A_database_mirror_drift_is_repaired_from_the_endpoint_without_a_source()
    {
        // A clone whose editor build output has no database still has to be able to clear
        // a red mirror check: the channel endpoint holds the bytes the mirror is missing,
        // so nothing about this repair needs the build output.
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        WritePublishedChannel(channel, 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        using var service = new DataPublishService(NewSource(null), repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.DbExists);
        Assert.Equal(MirrorSyncState.Drifted, comparison.Mirror);
        Assert.True(comparison.MirrorNeedsRepair);
        Assert.True(comparison.HasAnyChanges, "a drifted mirror the endpoint can repair must be publishable");

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        // The version token must never be stamped onto bytes the publish did not put
        // there: a fresh install seeded from Assets would then boot bookmarked as up to
        // date on stale data and never download anything again.
        TestFiles.AssertSameBytes(Path.Combine(channel, DatabaseFile), Path.Combine(AssetsDir(repo), DatabaseFile));
        TestFiles.AssertSameBytes(Path.Combine(channel, VersionFile), Path.Combine(AssetsDir(repo), VersionFile));
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(AssetsDir(repo), VersionFile)));
        Assert.False((await service.CompareAsync()).HasAnyChanges, "the repaired pair has nothing left to publish");
    }

    [Fact]
    public async Task A_version_only_mirror_drift_is_publishable_without_a_source()
    {
        // The other half of the same drift: identical bytes, disagreeing tokens, which
        // would hand two builds different answers about the same database.
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        WritePublishedChannel(channel, 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "0.9.0", PublishedNewDb);
        using var service = new DataPublishService(NewSource(null), repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.DbExists);
        Assert.Equal(MirrorSyncState.Drifted, comparison.Mirror);
        Assert.True(comparison.HasAnyChanges, "a stamp-only drift CI rejects must leave something to publish");

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(AssetsDir(repo), VersionFile)));
        Assert.False((await service.CompareAsync()).HasAnyChanges);
    }

    #endregion

    #region Unusable source databases

    [Fact]
    public async Task A_source_that_is_not_a_database_fails_the_comparison()
    {
        var repo = NewRepo();
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        var source = NewSource(null);
        await File.WriteAllTextAsync(Path.Combine(source, DatabaseFile), "this is not a database");
        using var service = new DataPublishService(source, repo);

        var comparison = await service.CompareAsync();

        Assert.False(comparison.Success);
        Assert.Contains("not a database SQLite can open", comparison.ErrorMessage);
        Assert.Contains("Rebuild the database", comparison.ErrorMessage);
    }

    [Fact]
    public async Task A_source_held_by_another_writer_says_so_instead_of_blaming_the_data()
    {
        // The editor itself opens this database from its own build output, so a lock is
        // routine and transient. Telling the operator to rebuild a perfectly good database
        // is the wrong remedy for it.
        var repo = NewRepo();
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", OldDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", OldDb);
        var source = NewSource(NewDb);
        using var service = new DataPublishService(source, repo);

        var comparison = await service.CompareAsync();
        Assert.True(comparison.Success, comparison.ErrorMessage);
        Assert.True(comparison.DbChanged);

        // Hold a write transaction open across the publish, which is what the stamp needs.
        await using var writer = new SqliteConnection($"Data Source={Path.Combine(source, DatabaseFile)}");
        await writer.OpenAsync();
        await using (var begin = writer.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE";
            await begin.ExecuteNonQueryAsync();
        }

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.False(published.Success);
        Assert.Contains("in use by another connection", published.ErrorMessage);
        Assert.DoesNotContain("Rebuild the database", published.ErrorMessage);
        // Refused before it wrote anything, so the endpoint still serves what it did.
        Assert.Equal(OldDb, await File.ReadAllBytesAsync(Path.Combine(ChannelDir(repo, 1), DatabaseFile)));
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(ChannelDir(repo, 1), VersionFile)));
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
        var index = DataChannel.ParseIndex(
            await File.ReadAllTextAsync(IndexPath(repo)));
        Assert.Equal(2, index!.CurrentDataFormatVersion);
    }

    #endregion

    #region Asset groups

    /// <summary>Writes one file into an asset folder, creating the folder if it is not there.</summary>
    private static void WriteAsset(string dir, string fileName, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    // Where each asset group lives on either side. Spelled out here rather than derived
    // from the service, so a test cannot follow the service into a wrong folder.
    private static string MapSvgSource(string sourceDir) => Path.Combine(sourceDir, "Resources", "Maps");
    private static string MapSvgTarget(string repoRoot) => Path.Combine(AssetsDir(repoRoot), "DB", "Maps");
    private static string MarkerIconSource(string sourceDir) => Path.Combine(sourceDir, "Resources", "Icons");
    private static string MarkerIconTarget(string repoRoot) => Path.Combine(AssetsDir(repoRoot), "DB", "Icons");
    private static string ItemIconSource(string sourceDir) => Path.Combine(sourceDir, "wiki_data", "icons");
    private static string ItemIconTarget(string repoRoot) => Path.Combine(AssetsDir(repoRoot), "icons");
    private static string HideoutIconSource(string sourceDir) => Path.Combine(sourceDir, "icons", "hideout");
    private static string HideoutIconTarget(string repoRoot) => Path.Combine(AssetsDir(repoRoot), "icons", "hideout");

    /// <summary>
    /// A repository whose database endpoint has nothing to publish, so the asset groups
    /// are the only thing a comparison can find. Returns the build output to write assets
    /// into.
    /// </summary>
    private string PublishedRepoWithSource(string repo)
    {
        WritePublishedChannel(ChannelDir(repo, 1), 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        WriteIndex(repo, 1);
        return NewSource(PublishedNewDb);
    }

    [Fact]
    public async Task An_asset_group_counts_what_is_added_updated_and_unchanged()
    {
        var repo = NewRepo();
        var source = PublishedRepoWithSource(repo);
        var svgSource = MapSvgSource(source);
        var svgTarget = MapSvgTarget(repo);

        WriteAsset(svgSource, "added.svg", "<svg>new</svg>");
        // Different length, so the size check alone decides this one.
        WriteAsset(svgSource, "resized.svg", "<svg>a much longer map than the published one</svg>");
        WriteAsset(svgTarget, "resized.svg", "<svg>old</svg>");
        // Same length, different bytes: only the hash tells these apart, and a comparison
        // that stopped at the size would publish nothing for a map that really changed.
        WriteAsset(svgSource, "rewritten.svg", "<svg>aaaa</svg>");
        WriteAsset(svgTarget, "rewritten.svg", "<svg>bbbb</svg>");
        WriteAsset(svgSource, "unchanged.svg", "<svg>same</svg>");
        WriteAsset(svgTarget, "unchanged.svg", "<svg>same</svg>");
        // Neither of these belongs to the group: one is not the group's file type, and the
        // other exists only on the target side, where this tool never deletes.
        WriteAsset(svgSource, "notes.txt", "not a map");
        WriteAsset(svgTarget, "retired.svg", "<svg>gone</svg>");

        using var service = new DataPublishService(source, repo);
        var comparison = await service.CompareAsync();

        Assert.True(comparison.Success, comparison.ErrorMessage);
        Assert.Equal(1, comparison.MapSvg.Added);
        Assert.Equal(2, comparison.MapSvg.Updated);
        Assert.Equal(1, comparison.MapSvg.Unchanged);
        // Unchanged files are counted but deliberately not listed, and the list is what a
        // publish copies from, so an unchanged file in it would be a needless rewrite.
        Assert.Equal(
            new[] { "added.svg", "resized.svg", "rewritten.svg" },
            comparison.MapSvg.Changes.Select(c => c.FileName).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(
            DataPublishService.ChangeType.Added,
            comparison.MapSvg.Changes.Single(c => c.FileName == "added.svg").Type);
        Assert.All(
            comparison.MapSvg.Changes.Where(c => c.FileName != "added.svg"),
            change => Assert.Equal(DataPublishService.ChangeType.Updated, change.Type));
        // The database endpoint is fully published here, so the asset group is the whole
        // answer: one count per changed file, and nothing for the unchanged one.
        Assert.True(comparison.HasAnyChanges);
        Assert.Equal(3, comparison.TotalChanges);
    }

    [Fact]
    public async Task Each_asset_group_reads_its_own_folder_and_file_type()
    {
        // One added file per group, each with a sibling of the wrong type beside it: a
        // group that read another group's folder, or dropped its pattern, would count more
        // than one.
        var repo = NewRepo();
        var source = PublishedRepoWithSource(repo);

        WriteAsset(MapSvgSource(source), "map.svg", "<svg/>");
        WriteAsset(MapSvgSource(source), "map.png", "not a map");
        WriteAsset(MarkerIconSource(source), "marker.webp", "marker");
        WriteAsset(MarkerIconSource(source), "marker.png", "not a marker icon");
        WriteAsset(ItemIconSource(source), "item.png", "item");
        WriteAsset(ItemIconSource(source), "item.webp", "not an item icon");
        WriteAsset(HideoutIconSource(source), "station.png", "station");
        WriteAsset(HideoutIconSource(source), "station.svg", "not a hideout icon");

        using var service = new DataPublishService(source, repo);
        var comparison = await service.CompareAsync();

        Assert.True(comparison.Success, comparison.ErrorMessage);
        Assert.Equal(1, comparison.MapSvg.Added);
        Assert.Equal(1, comparison.MarkerIcon.Added);
        Assert.Equal(1, comparison.ItemIcon.Added);
        Assert.Equal(1, comparison.HideoutIcon.Added);
        Assert.Equal(new[] { "map.svg" }, comparison.MapSvg.Changes.Select(c => c.FileName));
        Assert.Equal(new[] { "marker.webp" }, comparison.MarkerIcon.Changes.Select(c => c.FileName));
        Assert.Equal(new[] { "item.png" }, comparison.ItemIcon.Changes.Select(c => c.FileName));
        Assert.Equal(new[] { "station.png" }, comparison.HideoutIcon.Changes.Select(c => c.FileName));
        Assert.Equal(4, comparison.TotalChanges);
    }

    [Fact]
    public async Task A_group_whose_source_folder_is_missing_counts_nothing()
    {
        // The build output legitimately has no folder for a group until that group's
        // producer has run once. That is nothing to publish, not a comparison failure and
        // not a change.
        var repo = NewRepo();
        var source = PublishedRepoWithSource(repo);

        using var service = new DataPublishService(source, repo);
        var comparison = await service.CompareAsync();

        Assert.True(comparison.Success, comparison.ErrorMessage);
        Assert.Equal(0, comparison.MapSvg.Added);
        Assert.Equal(0, comparison.MapSvg.Updated);
        Assert.Equal(0, comparison.MapSvg.Unchanged);
        Assert.Empty(comparison.MapSvg.Changes);
        Assert.Equal(0, comparison.MarkerIcon.Added);
        Assert.Empty(comparison.MarkerIcon.Changes);
        Assert.Equal(0, comparison.ItemIcon.Added);
        Assert.Empty(comparison.ItemIcon.Changes);
        Assert.Equal(0, comparison.HideoutIcon.Added);
        Assert.Empty(comparison.HideoutIcon.Changes);
        Assert.False(comparison.HasAnyChanges);
        Assert.Equal(0, comparison.TotalChanges);
    }

    [Fact]
    public async Task A_publish_copies_the_changed_asset_files_and_leaves_the_rest_alone()
    {
        var repo = NewRepo();
        var source = PublishedRepoWithSource(repo);

        WriteAsset(MapSvgSource(source), "added.svg", "<svg>new</svg>");
        WriteAsset(MapSvgSource(source), "updated.svg", "<svg>rewritten</svg>");
        WriteAsset(MapSvgTarget(repo), "updated.svg", "<svg>old</svg>");
        WriteAsset(MapSvgSource(source), "unchanged.svg", "<svg>same</svg>");
        WriteAsset(MapSvgTarget(repo), "unchanged.svg", "<svg>same</svg>");
        WriteAsset(MarkerIconSource(source), "marker.webp", "marker");
        WriteAsset(HideoutIconSource(source), "station.png", "station");

        using var service = new DataPublishService(source, repo);
        var comparison = await service.CompareAsync();
        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal("<svg>new</svg>", await File.ReadAllTextAsync(Path.Combine(MapSvgTarget(repo), "added.svg")));
        Assert.Equal("<svg>rewritten</svg>", await File.ReadAllTextAsync(Path.Combine(MapSvgTarget(repo), "updated.svg")));
        Assert.Equal("marker", await File.ReadAllTextAsync(Path.Combine(MarkerIconTarget(repo), "marker.webp")));
        Assert.Equal("station", await File.ReadAllTextAsync(Path.Combine(HideoutIconTarget(repo), "station.png")));
        // Only the changed files are copied: an unchanged file rewritten anyway would be a
        // needless line in the commit that publishes this.
        Assert.Contains("DB/Maps/added.svg", published.CopiedFiles);
        Assert.Contains("DB/Maps/updated.svg", published.CopiedFiles);
        Assert.DoesNotContain("DB/Maps/unchanged.svg", published.CopiedFiles);
        Assert.Equal(2, published.IconsCopied); // the marker icon and the hideout icon

        // And the tool agrees it is done: nothing pending, in either direction.
        Assert.False((await service.CompareAsync()).HasAnyChanges);
    }

    #endregion

    #region Republishing under the token the channel already serves

    /// <summary>
    /// <see cref="PublishedNewDb"/> after an editor session that wrote to it and left the
    /// published data exactly as it found it: the row goes in and comes straight back out,
    /// so the rows are identical and the file's bytes are not. That is the shape of a
    /// re-import of unchanged cached data, and it is what makes the tool's byte comparison
    /// report a changed database with nothing new to say. Bumping the version token for it
    /// sends every install in the field after a database it already has.
    /// </summary>
    private byte[] RecommittedNewDb => _recommittedNewDb ??= TestSqlite.BuildDatabase(
        "INSERT INTO Marker VALUES ('scratch'); DELETE FROM Marker WHERE Name = 'scratch';", seed: PublishedNewDb);

    private byte[]? _recommittedNewDb;

    [Fact]
    public async Task Republishing_the_current_bytes_keeps_the_token_the_channel_already_serves()
    {
        var repo = NewRepo();
        var channel = ChannelDir(repo, 1);
        WritePublishedChannel(channel, 1, "1.0.10", PublishedNewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", PublishedNewDb);
        WriteIndex(repo, 1);
        var source = NewSource(RecommittedNewDb);
        // The fixture has to be a real byte change, or nothing below is being tested.
        Assert.NotEqual(PublishedNewDb, RecommittedNewDb);

        using var service = new DataPublishService(source, repo);
        var comparison = await service.CompareAsync();

        Assert.True(comparison.Success, comparison.ErrorMessage);
        Assert.True(comparison.DbChanged, "the bytes really did change");
        Assert.True(comparison.CanKeepPublishedVersion, "the operator has to be able to keep the token here");

        // What the window's "keep current version" choice publishes.
        var published = await service.PublishAsync(comparison, comparison.PublishedVersion!);

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal("1.0.10", published.NewVersion);
        // The database IS rewritten, on both endpoints, so the repository stops carrying
        // two answers about the same data.
        Assert.Equal(SourceBytes(source), await File.ReadAllBytesAsync(Path.Combine(channel, DatabaseFile)));
        TestFiles.AssertSameBytes(
            Path.Combine(channel, DatabaseFile), Path.Combine(AssetsDir(repo), DatabaseFile));
        // And the token every install decides on stays exactly where it was, which is the
        // whole point: no install downloads 6.89 MB of data it already has.
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(channel, VersionFile)));
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(AssetsDir(repo), VersionFile)));

        var manifest = ReadManifest(channel);
        Assert.Equal("1.0.10", manifest.Version);
        // The manifest still describes what is actually served, so a fresh install that
        // does download verifies against the bytes now there rather than the old ones.
        Assert.Equal(TestDigest.Sha256Digest(SourceBytes(source)), manifest.Database.Digest);
        Assert.Equal(SourceBytes(source).LongLength, manifest.Database.Size);

        // Nothing left pending: the republish is a complete publish, not a half one.
        Assert.False((await service.CompareAsync()).HasAnyChanges);
    }

    [Fact]
    public void Keeping_the_current_token_is_offered_only_when_there_is_one_to_keep()
    {
        // Nothing published anywhere: there is no token to carry forward, so the
        // operator's suggestion is all there is and the choice would be a lie.
        var firstPublish = new DataPublishService.ComparisonResult { DbChanged = true };
        Assert.False(firstPublish.CanKeepPublishedVersion);

        // The database is not being replaced, so the token is kept whatever the operator
        // types (ResolvePublishVersion), and offering the choice would suggest otherwise.
        var assetsOnly = new DataPublishService.ComparisonResult { CurrentVersion = "1.0.10" };
        Assert.False(assetsOnly.CanKeepPublishedVersion);
        Assert.Equal("1.0.10", assetsOnly.ResolvePublishVersion("1.0.11"));

        // New database bytes with a token behind them: both tokens are reachable, which is
        // what makes this a choice.
        var newData = new DataPublishService.ComparisonResult { DbChanged = true, CurrentVersion = "1.0.10" };
        Assert.True(newData.CanKeepPublishedVersion);
        Assert.Equal("1.0.11", newData.ResolvePublishVersion("1.0.11"));
        Assert.Equal("1.0.10", newData.ResolvePublishVersion(newData.PublishedVersion!));

        // The manifest's token counts too: a half-applied publish that lost
        // db_version.txt still has a token every install is bookmarked against.
        var versionFileGone = new DataPublishService.ComparisonResult { DbChanged = true, ManifestVersion = "1.0.10" };
        Assert.True(versionFileGone.CanKeepPublishedVersion);
        Assert.Equal("1.0.10", versionFileGone.ResolvePublishVersion(versionFileGone.PublishedVersion!));

        // A token no client can read is not one to carry forward, so there is nothing to
        // keep and the operator has to type a readable one.
        var unreadable = new DataPublishService.ComparisonResult { DbChanged = true, CurrentVersion = "1.0 (beta)" };
        Assert.False(unreadable.CanKeepPublishedVersion);
    }

    [Fact]
    public void A_group_with_nothing_to_survey_counts_and_lists_nothing()
    {
        var empty = DataPublishService.FileGroupComparison.Empty;

        Assert.Empty(empty.Changes);
        Assert.Equal(0, empty.ChangeCount);
        Assert.Equal(0, empty.Total);
        Assert.False(empty.HasChanges);

        // Unchanged files count toward the total the window reports, but never toward the
        // work a publish has to do.
        var unchangedOnly = new DataPublishService.FileGroupComparison(
            Array.Empty<DataPublishService.FileChangeInfo>(), Added: 0, Updated: 0, Unchanged: 7);
        Assert.False(unchangedOnly.HasChanges);
        Assert.Equal(0, unchangedOnly.ChangeCount);
        Assert.Equal(7, unchangedOnly.Total);

        var changed = new DataPublishService.FileGroupComparison(
            new[] { new DataPublishService.FileChangeInfo { FileName = "one.svg" } },
            Added: 1, Updated: 2, Unchanged: 4);
        Assert.True(changed.HasChanges);
        Assert.Equal(3, changed.ChangeCount);
        Assert.Equal(7, changed.Total);
    }

    #endregion
}
