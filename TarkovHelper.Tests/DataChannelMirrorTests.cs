using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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
    private const int MirroredFormatVersion = 1;

    private static string ChannelDir() =>
        Path.Combine(TestRepo.Root(), "data", $"v{MirroredFormatVersion}");

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
            $"data/v{MirroredFormatVersion}/{DatabaseFile} is missing: the endpoint every format-1 build polls.");
        Assert.True(File.Exists(Path.Combine(ChannelDir(), VersionFile)),
            $"data/v{MirroredFormatVersion}/{VersionFile} is missing: without it no build can tell whether its data is current.");
    }

    [Fact]
    public void The_assets_mirror_matches_the_channel_database()
    {
        AssertSameBytes(
            Path.Combine(ChannelDir(), DatabaseFile),
            Path.Combine(AssetsDir(), DatabaseFile),
            $"TarkovHelper/Assets and data/v{MirroredFormatVersion} are two addresses for one data format "
            + "and must serve identical bytes. A publish writes both; if only one moved, publish again.");
    }

    [Fact]
    public void The_assets_mirror_matches_the_channel_version_stamp()
    {
        AssertSameBytes(
            Path.Combine(ChannelDir(), VersionFile),
            Path.Combine(AssetsDir(), VersionFile),
            $"The version stamps of TarkovHelper/Assets and data/v{MirroredFormatVersion} disagree, so the two "
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
    public void The_committed_manifest_describes_the_committed_database()
    {
        // The manifest is what clients trust to decide whether to download and whether
        // to keep what they downloaded. If its hash, size, or version drifts from the
        // files beside it, every install either re-downloads forever or rejects a
        // perfectly good database.
        var format = DatabaseUpdateService.DataFormatVersion;
        var channelDir = Path.Combine(TestRepo.Root(), "data", $"v{format}");
        var manifestPath = Path.Combine(channelDir, "manifest.json");

        Assert.True(File.Exists(manifestPath), $"data/v{format}/manifest.json is missing");
        var manifest = DatabaseUpdateService.ParseManifest(File.ReadAllText(manifestPath));
        Assert.True(manifest != null, $"data/v{format}/manifest.json does not satisfy the app's own reader");

        Assert.Equal(format, manifest!.DataFormatVersion);
        Assert.True(manifest.SchemaVersion <= DatabaseUpdateService.MAX_SUPPORTED_SCHEMA_VERSION,
            $"The committed manifest declares schema {manifest.SchemaVersion}, which this build cannot read.");

        var databasePath = Path.Combine(channelDir, manifest.Database.File);
        Assert.True(File.Exists(databasePath), $"The manifest names {manifest.Database.File}, which is not there");

        // Integrity fields are optional to the reader, but the repository must carry
        // them: shipping without a hash silently turns off download verification.
        Assert.False(string.IsNullOrWhiteSpace(manifest.Database.Sha256),
            "The committed manifest has no sha256, which would disable download verification for every client.");
        Assert.Equal(new FileInfo(databasePath).Length, manifest.Database.Size);
        Assert.Equal(Sha256Of(databasePath), manifest.Database.Sha256!.ToUpperInvariant());

        // The bookmark seeded into installs has to name the same version the manifest does.
        Assert.Equal(File.ReadAllText(Path.Combine(channelDir, VersionFile)).Trim(), manifest.Version);
    }

    [Fact]
    public void The_published_database_stamps_its_own_data_format()
    {
        // Read back through SQLite, not by peeking at the header, so this proves SQLite
        // itself agrees the stamp is set: the published database declares the contract
        // it was built for, and a client can check what it downloaded without having to
        // trust the manifest that arrived with it.
        var format = DatabaseUpdateService.DataFormatVersion;
        var databasePath = Path.Combine(TestRepo.Root(), "data", $"v{format}", DatabaseFile);

        int stamped;
        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            stamped = Convert.ToInt32(command.ExecuteScalar());
        }
        SqliteConnection.ClearAllPools();

        Assert.True(stamped == format,
            $"data/v{format}/{DatabaseFile} is stamped with data format {stamped}, expected {format}. "
            + "Publishing sets this; a hand-copied database will not have it.");
    }

    [Fact]
    public void The_channel_documents_use_the_agreed_field_names()
    {
        // The field names ARE the contract: once a build ships reading them, renaming one
        // breaks every install that already trusts it, and the app's own reader is
        // case-insensitive and ignores unknown fields, so a rename would sail through
        // every other test here while silently disabling whatever it renamed.
        //
        // The vocabulary is deliberate. schemaVersion is the shape of this document
        // (Docker's sense); dataFormatVersion is the contract of the database it describes,
        // which covers field meaning and permitted values, not just structure; version
        // is which publish this is. See feature-versioned-data-channel.spec.md.
        var root = TestRepo.Root();

        AssertTopLevelFields(
            Path.Combine(root, "data", $"v{DatabaseUpdateService.DataFormatVersion}", "manifest.json"),
            "schemaVersion", "dataFormatVersion", "version", "database");
        AssertTopLevelFields(Path.Combine(root, "data", "index.json"), "schemaVersion", "currentDataFormatVersion");

        using var manifest = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "data", $"v{DatabaseUpdateService.DataFormatVersion}", "manifest.json")));
        Assert.Equal(
            new[] { "file", "sha256", "size" },
            manifest.RootElement.GetProperty("database").EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    private static void AssertTopLevelFields(string path, params string[] expected)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(
            expected.OrderBy(n => n, StringComparer.Ordinal),
            document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void The_channel_index_covers_the_schema_this_build_polls()
    {
        // A build must never ship pointing at a schema the index does not acknowledge:
        // it would declare itself superseded from its first check.
        var indexPath = Path.Combine(TestRepo.Root(), "data", "index.json");
        Assert.True(File.Exists(indexPath), "data/index.json is missing: no build could tell whether it is current");

        var index = DatabaseUpdateService.ParseIndex(File.ReadAllText(indexPath));
        Assert.True(index != null, "data/index.json does not satisfy the app's own reader");

        Assert.True(index!.CurrentDataFormatVersion >= DatabaseUpdateService.DataFormatVersion,
            $"index.json publishes schema {index.CurrentDataFormatVersion}, below the "
            + $"{DatabaseUpdateService.DataFormatVersion} this build reads.");
        Assert.True(Directory.Exists(Path.Combine(TestRepo.Root(), "data", $"v{index.CurrentDataFormatVersion}")),
            $"index.json points at schema {index.CurrentDataFormatVersion}, which has no directory.");
    }
}
