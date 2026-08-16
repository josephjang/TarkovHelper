using System.IO;
using System.Text;
using TarkovDBEditor.Services;

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

    private static readonly byte[] NewDb = Encoding.UTF8.GetBytes("freshly-built-database");
    private static readonly byte[] OldDb = Encoding.UTF8.GetBytes("previously-published-db");

    private readonly TempStoreRoot _temp = new("datapublish");

    public void Dispose() => _temp.Dispose();

    /// <summary>The editor's build output, i.e. what a publish reads from.</summary>
    private string NewSource(byte[]? database)
    {
        var dir = _temp.NewFolder("editor-output");
        if (database != null) File.WriteAllBytes(Path.Combine(dir, DatabaseFile), database);
        return dir;
    }

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

        Assert.Equal(10, service.GetLiveDataFormat());
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

        Assert.Equal(1, service.GetLiveDataFormat());
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
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();
        Assert.True(comparison.Success);
        Assert.True(comparison.DbChanged);
        Assert.True(comparison.MirrorsToAssets);
        Assert.Equal(1, comparison.LiveDataFormat);

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        AssertSameBytes(Path.Combine(ChannelDir(repo, 1), DatabaseFile), Path.Combine(AssetsDir(repo), DatabaseFile));
        AssertSameBytes(Path.Combine(ChannelDir(repo, 1), VersionFile), Path.Combine(AssetsDir(repo), VersionFile));
        Assert.Equal(NewDb, await File.ReadAllBytesAsync(Path.Combine(ChannelDir(repo, 1), DatabaseFile)));
        Assert.Equal("1.0.11", await File.ReadAllTextAsync(Path.Combine(ChannelDir(repo, 1), VersionFile)));
    }

    [Fact]
    public async Task A_database_only_mirror_drift_is_publishable_and_repaired()
    {
        // The half-published commit: one endpoint moved, the other did not. The tool has
        // to be able to fix it, which means treating it as a change even though the
        // editor's own database is already what the channel holds.
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10", NewDb);
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
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10", NewDb);
        WriteEndpoint(AssetsDir(repo), "0.9.0", NewDb);
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
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10", NewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10", NewDb);
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.True(comparison.Success);
        Assert.False(comparison.DbChanged);
        Assert.True(comparison.MirrorInSync);
        Assert.False(comparison.MirrorNeedsRepair);
        Assert.False(comparison.HasAnyChanges);
    }

    [Fact]
    public async Task The_version_token_is_read_past_a_frozen_directive()
    {
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10\nfrozen", NewDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10\nfrozen", NewDb);
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();

        Assert.Equal("1.0.10", comparison.CurrentVersion);
        Assert.Equal("1.0.11", comparison.NewVersion);
    }

    #endregion

    #region Publishing a later format

    [Fact]
    public async Task Publishing_a_later_format_leaves_the_frozen_endpoints_alone()
    {
        // Once format 2 is live, format 1 and its Assets mirror are frozen history: a
        // publish must not touch either, or the freeze would hand old builds new data.
        var repo = NewRepo();
        WriteEndpoint(ChannelDir(repo, 1), "1.0.10\nfrozen", OldDb);
        WriteEndpoint(AssetsDir(repo), "1.0.10\nfrozen", OldDb);
        WriteEndpoint(ChannelDir(repo, 2), "2.0.0", OldDb);
        using var service = new DataPublishService(NewSource(NewDb), repo);

        var comparison = await service.CompareAsync();
        Assert.Equal(2, comparison.LiveDataFormat);
        Assert.False(comparison.MirrorsToAssets);
        Assert.False(comparison.MirrorNeedsRepair);

        var published = await service.PublishAsync(comparison, "2.0.1");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Equal(NewDb, await File.ReadAllBytesAsync(Path.Combine(ChannelDir(repo, 2), DatabaseFile)));
        Assert.Equal("2.0.1", await File.ReadAllTextAsync(Path.Combine(ChannelDir(repo, 2), VersionFile)));

        // Frozen: same bytes, same stamp, directive intact.
        Assert.Equal(OldDb, await File.ReadAllBytesAsync(Path.Combine(ChannelDir(repo, 1), DatabaseFile)));
        Assert.Equal("1.0.10\nfrozen", await File.ReadAllTextAsync(Path.Combine(ChannelDir(repo, 1), VersionFile)));
        Assert.Equal(OldDb, await File.ReadAllBytesAsync(Path.Combine(AssetsDir(repo), DatabaseFile)));
        Assert.Equal("1.0.10\nfrozen", await File.ReadAllTextAsync(Path.Combine(AssetsDir(repo), VersionFile)));
    }

    #endregion
}
