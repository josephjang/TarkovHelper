using System.IO;
using System.Security.Cryptography;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the format-1 mirror invariant: TarkovHelper/Assets and data/v1 serve the same
/// bytes, forever. Both are endpoints for the same data format (Assets is the address
/// builds already in the field hardcode and can never be repointed away from), so they
/// advance together and freeze together.
///
/// This is also the tripwire for a half-published commit: the publish tool writes both
/// copies in one go, and if a commit ever reaches main with only one of them updated,
/// raw main serves two different version tokens for one format until someone notices.
/// CI noticing is the point.
///
/// Runs offline against the working tree, the same repo-root walk UpdateXmlTests and
/// DecisionDocsTests use.
/// </summary>
public sealed class DataChannelMirrorTests
{
    private const string DatabaseFile = "tarkov_data.db";
    private const string VersionFile = "db_version.txt";

    /// <summary>
    /// The mirrored format. Deliberately the literal 1, not DataFormatVersion: the
    /// mirror is a property of format 1 specifically, and once the app moves to format 2
    /// these files must keep matching each other while the app polls elsewhere.
    /// </summary>
    private const int MirroredFormat = 1;

    private static string ChannelDir() =>
        Path.Combine(TestRepo.Root(), "data", $"v{MirroredFormat}");

    private static string AssetsDir() =>
        Path.Combine(TestRepo.Root(), "TarkovHelper", "Assets");

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void AssertSameBytes(string expectedPath, string actualPath, string why)
    {
        Assert.True(File.Exists(expectedPath), $"{expectedPath} is missing");
        Assert.True(File.Exists(actualPath), $"{actualPath} is missing");

        var expectedLength = new FileInfo(expectedPath).Length;
        var actualLength = new FileInfo(actualPath).Length;
        Assert.True(expectedLength == actualLength,
            $"{why}\n  {expectedPath} is {expectedLength} bytes\n  {actualPath} is {actualLength} bytes");

        Assert.True(Sha256Of(expectedPath) == Sha256Of(actualPath),
            $"{why}\n  {expectedPath} and {actualPath} are the same size but differ.");
    }

    [Fact]
    public void The_channel_directory_holds_both_endpoint_files()
    {
        Assert.True(File.Exists(Path.Combine(ChannelDir(), DatabaseFile)),
            $"data/v{MirroredFormat}/{DatabaseFile} is missing: the endpoint every format-1 build polls.");
        Assert.True(File.Exists(Path.Combine(ChannelDir(), VersionFile)),
            $"data/v{MirroredFormat}/{VersionFile} is missing: without it no build can tell whether its data is current.");
    }

    [Fact]
    public void The_assets_mirror_matches_the_channel_database()
    {
        AssertSameBytes(
            Path.Combine(ChannelDir(), DatabaseFile),
            Path.Combine(AssetsDir(), DatabaseFile),
            $"TarkovHelper/Assets and data/v{MirroredFormat} are two addresses for one data format "
            + "and must serve identical bytes. A publish writes both; if only one moved, publish again.");
    }

    [Fact]
    public void The_assets_mirror_matches_the_channel_version_stamp()
    {
        AssertSameBytes(
            Path.Combine(ChannelDir(), VersionFile),
            Path.Combine(AssetsDir(), VersionFile),
            $"The version stamps of TarkovHelper/Assets and data/v{MirroredFormat} disagree, so the two "
            + "format-1 endpoints would hand different builds different answers about the same data.");
    }

    [Fact]
    public void The_bundled_seed_is_the_data_this_build_would_download()
    {
        // The copy in the build output (produced by the csproj seed item, and the file a
        // fresh install ships with) must be exactly what this build's endpoint serves.
        // If it is not, a fresh install disagrees with the first check it runs.
        var seedDir = Path.Combine(AppContext.BaseDirectory, "Assets");
        var channelDir = Path.Combine(
            TestRepo.Root(), "data", $"v{DatabaseUpdateService.DataFormatVersion}");

        AssertSameBytes(
            Path.Combine(channelDir, DatabaseFile),
            Path.Combine(seedDir, DatabaseFile),
            "The bundled seed database is not the one this build's channel serves; check the "
            + "csproj seed items and rebuild.");
        AssertSameBytes(
            Path.Combine(channelDir, VersionFile),
            Path.Combine(seedDir, VersionFile),
            "The bundled version stamp is not the one this build's channel serves, so a fresh "
            + "install would re-download the database it already has.");
    }

    [Fact]
    public void The_endpoint_this_build_polls_is_readable_and_live()
    {
        // The committed file must satisfy the reader that consumes it. Asserted against
        // the format this build actually polls, not the mirrored one: after a future
        // bump, data/v1 being frozen is the expected end state, while shipping a build
        // aimed at an already-frozen endpoint never is.
        var format = DatabaseUpdateService.DataFormatVersion;
        var parsed = DatabaseUpdateService.ParseVersionFile(
            File.ReadAllText(Path.Combine(TestRepo.Root(), "data", $"v{format}", VersionFile)));

        Assert.NotNull(parsed);
        Assert.False(string.IsNullOrWhiteSpace(parsed.Version));
        Assert.False(parsed.IsFrozen,
            $"data/v{format} is marked frozen while this build still polls it.");
    }
}
