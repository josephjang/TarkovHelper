using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Drives the real <see cref="DatabaseUpdateService"/> fetch/compare/verify/download
/// path against a local channel laid out like the repository, through the service's
/// test seam. Hermetic: a loopback socket and a temp folder, no network and no build
/// output.
///
/// Three behaviors are pinned here that nothing else can prove: an update completes end
/// to end through a served channel, a payload that does not match its manifest never
/// replaces the working database, and a build learns from the channel index that it has
/// been left behind. The last one is the whole reason the index exists, since a
/// superseded endpoint is never rewritten to say so.
/// </summary>
public sealed class DataChannelEndpointServingTests : IDisposable
{
    private const string VersionFile = "db_version.txt";
    private const string DatabaseFile = "tarkov_data.db";

    private static readonly byte[] PublishedDb = Encoding.UTF8.GetBytes("published-database-bytes");
    private static readonly byte[] InstalledDb = Encoding.UTF8.GetBytes("older-installed-database");

    private readonly TempStoreRoot _temp = new("datachannel");

    /// <summary>The schema this build polls; the served fixture has to match it.</summary>
    private static int Pin => DatabaseUpdateService.DataFormatVersion;

    public void Dispose() => _temp.Dispose();

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>
    /// Builds a served channel root: index.json plus one endpoint directory.
    /// </summary>
    private string NewServedChannel(
        string version,
        byte[] database,
        int? currentDataFormat = null,
        string? sha256 = null,
        long? size = null,
        int manifestSchemaVersion = 1,
        int? dataFormat = null)
    {
        var root = _temp.NewFolder("served-channel");
        var endpoint = Path.Combine(root, $"v{Pin}");
        Directory.CreateDirectory(endpoint);

        File.WriteAllBytes(Path.Combine(endpoint, DatabaseFile), database);
        File.WriteAllText(Path.Combine(endpoint, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = manifestSchemaVersion,
            dataFormat = dataFormat ?? Pin,
            version,
            database = new
            {
                file = DatabaseFile,
                sha256 = sha256 ?? Sha256Hex(database),
                size = size ?? database.Length,
            },
        }));
        File.WriteAllText(Path.Combine(root, "index.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            currentDataFormat = currentDataFormat ?? Pin,
        }));

        return root;
    }

    /// <summary>An install's Assets folder, already carrying a database at some version.</summary>
    private string NewInstalledAssets(string version, byte[] database)
    {
        var dir = _temp.NewFolder("install-assets");
        File.WriteAllText(Path.Combine(dir, VersionFile), version);
        File.WriteAllBytes(Path.Combine(dir, DatabaseFile), database);
        return dir;
    }

    #region Updating

    [Fact]
    public async Task An_update_completes_end_to_end()
    {
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.True(result.WasUpdated);
        Assert.False(result.IsSuperseded);
        Assert.Equal("2.0.0", service.LocalVersion);
        Assert.Equal("2.0.0", service.RemoteVersion);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        // The local bookmark records the token, nothing else.
        Assert.Equal("2.0.0", await File.ReadAllTextAsync(Path.Combine(assets, VersionFile)));
    }

    [Fact]
    public async Task A_matching_version_downloads_nothing()
    {
        using var server = new LocalFileServer(NewServedChannel("1.0.10", PublishedDb));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success);
        Assert.False(result.WasUpdated);
        // Proving the negative: the check must never pull 7 MB to learn nothing changed.
        Assert.DoesNotContain(server.RequestedPaths, p => p.EndsWith(DatabaseFile, StringComparison.Ordinal));
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    [Fact]
    public async Task The_database_is_fetched_from_the_file_the_manifest_names()
    {
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        await service.CheckAndUpdateAsync();

        // The payload path is data, not a constant, which is what would later allow a
        // version-stamped filename without changing any reader.
        Assert.Contains(server.RequestedPaths, p => p == $"/v{Pin}/{DatabaseFile}");
        Assert.Contains(server.RequestedPaths, p => p == $"/v{Pin}/manifest.json");
        Assert.Contains(server.RequestedPaths, p => p == "/index.json");
    }

    #endregion

    #region Integrity

    [Fact]
    public async Task A_payload_whose_hash_disagrees_with_the_manifest_is_discarded()
    {
        // The CDN skew case: each file is cached separately, so a fresh manifest can be
        // served beside a stale database. The install must keep what it has.
        using var server = new LocalFileServer(
            NewServedChannel("2.0.0", PublishedDb, sha256: Sha256Hex(Encoding.UTF8.GetBytes("different"))));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.False(result.Success);
        Assert.False(result.WasUpdated);
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        // The bookmark must not advance either, or the next check would call the stale
        // database current and never retry.
        Assert.Equal("1.0.10", service.LocalVersion);
        Assert.Equal("1.0.10", await File.ReadAllTextAsync(Path.Combine(assets, VersionFile)));
        Assert.False(File.Exists(Path.Combine(assets, DatabaseFile + ".tmp")));
    }

    [Fact]
    public async Task A_truncated_payload_is_discarded()
    {
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb, size: PublishedDb.Length + 100));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.False(result.Success);
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    [Fact]
    public async Task A_manifest_without_integrity_fields_still_installs()
    {
        // Optional means optional: absence downgrades to the previous behavior rather
        // than blocking the update.
        var root = _temp.NewFolder("no-integrity");
        var endpoint = Path.Combine(root, $"v{Pin}");
        Directory.CreateDirectory(endpoint);
        await File.WriteAllBytesAsync(Path.Combine(endpoint, DatabaseFile), PublishedDb);
        await File.WriteAllTextAsync(Path.Combine(endpoint, "manifest.json"),
            $$"""{ "schemaVersion": 1, "dataFormat": {{Pin}}, "version": "2.0.0", "database": { "file": "{{DatabaseFile}}" } }""");
        await File.WriteAllTextAsync(Path.Combine(root, "index.json"),
            $$"""{ "schemaVersion": 1, "currentDataFormat": {{Pin}} }""");

        using var server = new LocalFileServer(root);
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    #endregion

    #region Superseded

    [Fact]
    public async Task The_index_tells_a_build_it_has_been_left_behind()
    {
        using var server = new LocalFileServer(
            NewServedChannel("1.0.10", PublishedDb, currentDataFormat: Pin + 1));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.IsSuperseded);
        Assert.True(service.IsSuperseded);
        // Superseded is not an error state: the endpoint still answered, and there was
        // simply nothing new on it.
        Assert.True(result.Success);
    }

    [Fact]
    public async Task A_superseded_build_still_receives_data_it_has_not_caught_up_to()
    {
        // Being left behind ends future publishes; it does not strip an install of the
        // last compatible version it never got.
        using var server = new LocalFileServer(
            NewServedChannel("2.0.0", PublishedDb, currentDataFormat: Pin + 1));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.True(result.WasUpdated);
        Assert.True(result.IsSuperseded);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    [Fact]
    public async Task A_current_build_is_not_superseded_by_its_own_schema()
    {
        using var server = new LocalFileServer(NewServedChannel("1.0.10", PublishedDb, currentDataFormat: Pin));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        Assert.False((await service.CheckAndUpdateAsync()).IsSuperseded);
    }

    [Fact]
    public async Task A_failed_index_fetch_does_not_clear_a_known_supersession()
    {
        // A build that stopped receiving data has more failing checks by nature, and a
        // transient failure must not flicker the notice off and back on.
        var root = NewServedChannel("1.0.10", PublishedDb, currentDataFormat: Pin + 1);
        var assets = NewInstalledAssets("1.0.10", InstalledDb);

        using var server = new LocalFileServer(root);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);
        Assert.True((await service.CheckAndUpdateAsync()).IsSuperseded);

        File.Delete(Path.Combine(root, "index.json"));
        var afterFailure = await service.CheckAndUpdateAsync();

        Assert.True(afterFailure.IsSuperseded);
        Assert.True(service.IsSuperseded);
    }

    #endregion

    #region Refusals

    [Fact]
    public async Task A_manifest_from_a_newer_document_schema_is_refused()
    {
        // Someone published a shape this build was never taught to read at its own URL.
        // Refuse and change nothing; this is an operator error, not a user's problem.
        using var server = new LocalFileServer(
            NewServedChannel("2.0.0", PublishedDb, manifestSchemaVersion: DatabaseUpdateService.MAX_SUPPORTED_SCHEMA_VERSION + 1));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.False(result.Success);
        Assert.False(result.IsSuperseded); // the index said this build's schema is current
        Assert.DoesNotContain(server.RequestedPaths, p => p.EndsWith(DatabaseFile, StringComparison.Ordinal));
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    [Fact]
    public async Task An_endpoint_serving_another_data_schema_is_refused()
    {
        // The directory is ours but the payload it describes is not: a mis-published
        // endpoint, and installing it would hand this build a database it cannot read.
        using var server = new LocalFileServer(
            NewServedChannel("2.0.0", PublishedDb, dataFormat: Pin + 1));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.False(result.Success);
        Assert.DoesNotContain(server.RequestedPaths, p => p.EndsWith(DatabaseFile, StringComparison.Ordinal));
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    [Fact]
    public async Task A_missing_endpoint_leaves_the_install_untouched()
    {
        using var server = new LocalFileServer(_temp.NewFolder("empty-channel"));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.False(result.Success);
        Assert.False(result.WasUpdated);
        Assert.Equal("1.0.10", service.LocalVersion);
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    #endregion
}
