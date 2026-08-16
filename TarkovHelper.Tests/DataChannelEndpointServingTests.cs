using System.IO;
using System.Text;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Drives the real <see cref="DatabaseUpdateService"/> fetch/compare/download path
/// against a local endpoint laid out like the repository, through the service's test
/// seam. Hermetic: a loopback socket and a temp folder, no network and no build output.
///
/// This is the automated stand-in for the roadmap's phase-2 e2e expectation that a build
/// without the channel keeps updating against the restructured repository. The previous
/// released binary cannot run inside this suite, so what is pinned here is the contract
/// both build generations depend on: the two format-1 endpoints serve identical bytes
/// under different path shapes, and an update completes the same way through either. The
/// real binary is covered by the manual smoke check in the spec's Verification.
/// </summary>
public sealed class DataChannelEndpointServingTests : IDisposable
{
    private const string VersionFile = "db_version.txt";
    private const string DatabaseFile = "tarkov_data.db";

    /// <summary>Path shape fielded builds hardcode; it must keep working after the restructure.</summary>
    private const string LegacyEndpointPath = "TarkovHelper/Assets";
    private const string ChannelEndpointPath = "data/v1";

    private static readonly byte[] PublishedDb = Encoding.UTF8.GetBytes("published-database-bytes");
    private static readonly byte[] InstalledDb = Encoding.UTF8.GetBytes("older-installed-database");

    private readonly TempStoreRoot _temp = new("datachannel");

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// Builds a served repository whose two format-1 endpoints carry identical bytes,
    /// exactly as the publish flow and the mirror guard require.
    /// </summary>
    private string NewServedRepo(string version, byte[] database, bool frozen = false)
    {
        var root = _temp.NewFolder("served-repo");
        foreach (var endpoint in new[] { LegacyEndpointPath, ChannelEndpointPath })
        {
            var dir = Path.Combine(root, endpoint.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, VersionFile), frozen ? version + "\nfrozen" : version);
            File.WriteAllBytes(Path.Combine(dir, DatabaseFile), database);
        }

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

    [Theory]
    [InlineData(LegacyEndpointPath)]
    [InlineData(ChannelEndpointPath)]
    public async Task An_update_completes_through_either_format_one_endpoint(string endpointPath)
    {
        using var server = new LocalFileServer(NewServedRepo("2.0.0", PublishedDb));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService($"{server.BaseUrl}/{endpointPath}", assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.True(result.WasUpdated);
        Assert.False(result.IsEndpointFrozen);
        Assert.Equal("2.0.0", service.LocalVersion);
        Assert.Equal("2.0.0", service.RemoteVersion);
        // The served bytes actually landed, and the version file records only the token.
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        Assert.Equal("2.0.0", await File.ReadAllTextAsync(Path.Combine(assets, VersionFile)));
    }

    [Fact]
    public async Task A_matching_version_downloads_nothing()
    {
        using var server = new LocalFileServer(NewServedRepo("1.0.10", PublishedDb));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService($"{server.BaseUrl}/{ChannelEndpointPath}", assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success);
        Assert.False(result.WasUpdated);
        // Proving the negative: the check must never pull 7 MB to learn nothing changed.
        Assert.DoesNotContain(server.RequestedPaths, path => path.EndsWith(DatabaseFile, StringComparison.Ordinal));
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    [Fact]
    public async Task A_frozen_endpoint_is_reported_without_downloading()
    {
        using var server = new LocalFileServer(NewServedRepo("1.0.10", PublishedDb, frozen: true));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService($"{server.BaseUrl}/{ChannelEndpointPath}", assets);

        var result = await service.CheckAndUpdateAsync();

        // The freeze commit appends the directive without moving the token, so a channel
        // build learns its channel ended and still has nothing to download.
        Assert.True(result.Success);
        Assert.False(result.WasUpdated);
        Assert.True(result.IsEndpointFrozen);
        Assert.True(service.IsEndpointFrozen);
        Assert.DoesNotContain(server.RequestedPaths, path => path.EndsWith(DatabaseFile, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_frozen_endpoint_still_serves_data_the_install_is_missing()
    {
        // Freezing ends future publishes; it does not strip an install of the last
        // compatible version it has not caught up to yet.
        using var server = new LocalFileServer(NewServedRepo("2.0.0", PublishedDb, frozen: true));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService($"{server.BaseUrl}/{ChannelEndpointPath}", assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success);
        Assert.True(result.WasUpdated);
        Assert.True(result.IsEndpointFrozen);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        // Only the token is stored locally; the directive is endpoint state, not data state.
        Assert.Equal("2.0.0", await File.ReadAllTextAsync(Path.Combine(assets, VersionFile)));
    }

    [Fact]
    public async Task A_local_version_file_carrying_a_directive_is_read_as_its_token()
    {
        // The install an updating user arrives with: a pre-channel build polled a frozen
        // Assets endpoint and wrote the whole body, directive included, into its local
        // file. Reading only the token keeps that install from re-downloading data it has.
        var assets = _temp.NewFolder("legacy-install");
        await File.WriteAllTextAsync(Path.Combine(assets, VersionFile), "1.0.10\nfrozen");
        await File.WriteAllBytesAsync(Path.Combine(assets, DatabaseFile), InstalledDb);

        using var server = new LocalFileServer(NewServedRepo("1.0.10", PublishedDb));
        using var service = new DatabaseUpdateService($"{server.BaseUrl}/{ChannelEndpointPath}", assets);

        Assert.Equal("1.0.10", service.LocalVersion);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success);
        Assert.False(result.WasUpdated);
        Assert.DoesNotContain(server.RequestedPaths, path => path.EndsWith(DatabaseFile, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_endpoint_leaves_the_install_untouched()
    {
        // What a build whose format directory does not exist yet sees. It must degrade to
        // "no update available", never to a wiped or half-written install.
        using var server = new LocalFileServer(_temp.NewFolder("empty-repo"));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService($"{server.BaseUrl}/data/v99", assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.False(result.Success);
        Assert.False(result.WasUpdated);
        Assert.Equal("1.0.10", service.LocalVersion);
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    [Fact]
    public async Task An_empty_version_file_is_a_failed_check_not_a_new_version()
    {
        // A truncated or half-written stamp must not read as "different from local" and
        // trigger a download against whatever the database URL happens to hold.
        var root = _temp.NewFolder("empty-stamp-repo");
        var endpoint = Path.Combine(root, "data", "v1");
        Directory.CreateDirectory(endpoint);
        await File.WriteAllTextAsync(Path.Combine(endpoint, VersionFile), "   \n");
        await File.WriteAllBytesAsync(Path.Combine(endpoint, DatabaseFile), PublishedDb);

        using var server = new LocalFileServer(root);
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService($"{server.BaseUrl}/{ChannelEndpointPath}", assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.False(result.Success);
        Assert.DoesNotContain(server.RequestedPaths, path => path.EndsWith(DatabaseFile, StringComparison.Ordinal));
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    [Fact]
    public async Task A_failed_check_does_not_clear_a_known_freeze()
    {
        // A frozen build is offline-tolerant by nature: it has stopped receiving data, so
        // its checks fail more often, and a transient failure must not flicker the notice
        // off and back on.
        var servedRoot = NewServedRepo("1.0.10", PublishedDb, frozen: true);
        var assets = NewInstalledAssets("1.0.10", InstalledDb);

        using (var server = new LocalFileServer(servedRoot))
        {
            using var frozenService = new DatabaseUpdateService(
                $"{server.BaseUrl}/{ChannelEndpointPath}", assets);
            Assert.True((await frozenService.CheckAndUpdateAsync()).IsEndpointFrozen);

            // Same instance, endpoint now gone: the freeze is remembered, not re-derived
            // from a check that never reached the server.
            File.Delete(Path.Combine(servedRoot, ChannelEndpointPath.Replace('/', Path.DirectorySeparatorChar), VersionFile));

            var afterFailure = await frozenService.CheckAndUpdateAsync();

            Assert.False(afterFailure.Success);
            Assert.True(afterFailure.IsEndpointFrozen);
            Assert.True(frozenService.IsEndpointFrozen);
        }
    }
}
