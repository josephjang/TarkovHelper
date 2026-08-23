using System.IO;
using System.Text.Json;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the repository state of both database endpoints. TarkovHelper/Assets is frozen
/// at the seed shipped in v2026.7.0 because that release hardcodes those URLs. data/v1 is
/// the independent channel used by builds that shipped with channel support.
///
/// Runs offline against the working tree, the same repo-root walk UpdateXmlTests and
/// DecisionDocsTests use.
/// </summary>
public sealed class DataChannelRepositoryTests
{
    private const string DatabaseFile = "tarkov_data.db";
    private const string VersionFile = "db_version.txt";

    private const int ChannelFormatVersion = 1;
    private const string LegacyVersion = "1.0.10";
    private const string LegacyDatabaseDigest =
        "sha256:e2854c6af84093f95d45e40cdada74d2cf82714a457d4f5a3f1e46f5255a22cc";

    private static string ChannelDir() =>
        Path.Combine(TestRepo.Root(), "data", $"v{ChannelFormatVersion}");

    private static string AssetsDir() =>
        Path.Combine(TestRepo.Root(), "TarkovHelper", "Assets");

    [Fact]
    public void The_channel_directory_holds_both_endpoint_files()
    {
        Assert.True(File.Exists(Path.Combine(ChannelDir(), DatabaseFile)),
            $"data/v{ChannelFormatVersion}/{DatabaseFile} is missing: the endpoint every channel-aware format-1 build polls.");
        Assert.True(File.Exists(Path.Combine(ChannelDir(), VersionFile)),
            $"data/v{ChannelFormatVersion}/{VersionFile} is missing: without it a release cannot seed its local bookmark.");
    }

    [Fact]
    public void The_legacy_endpoint_stays_at_the_v2026_7_0_seed()
    {
        var versionPath = Path.Combine(AssetsDir(), VersionFile);
        var databasePath = Path.Combine(AssetsDir(), DatabaseFile);

        Assert.Equal(LegacyVersion, File.ReadAllText(versionPath).Trim());
        Assert.Equal(LegacyDatabaseDigest, TestDigest.Sha256Digest(databasePath));
        Assert.Equal(6_889_472, new FileInfo(databasePath).Length);

        Assert.NotEqual(LegacyVersion, File.ReadAllText(Path.Combine(ChannelDir(), VersionFile)).Trim());
        Assert.NotEqual(
            File.ReadAllBytes(Path.Combine(ChannelDir(), DatabaseFile)),
            File.ReadAllBytes(databasePath));
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

        TestFiles.AssertSameBytes(
            Path.Combine(channelDir, DatabaseFile),
            Path.Combine(seedDir, DatabaseFile),
            "The bundled seed database is not the one this build's channel serves; check the "
            + "csproj seed items and rebuild.");
        TestFiles.AssertSameBytes(
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
        var manifest = DataChannel.ParseManifest(File.ReadAllText(manifestPath));
        Assert.True(manifest != null, $"data/v{format}/manifest.json does not satisfy the app's own reader");

        Assert.Equal(format, manifest!.DataFormatVersion);
        Assert.True(manifest.SchemaVersion <= DataChannel.MAX_SUPPORTED_SCHEMA_VERSION,
            $"The committed manifest declares schema {manifest.SchemaVersion}, which this build cannot read.");

        var databasePath = Path.Combine(channelDir, manifest.Database.File);
        Assert.True(File.Exists(databasePath), $"The manifest names {manifest.Database.File}, which is not there");

        // Integrity fields are optional to the reader, but the repository must carry
        // them: shipping without a digest silently turns off download verification, and
        // shipping one the reader cannot check does the same thing more quietly still.
        Assert.False(string.IsNullOrWhiteSpace(manifest.Database.Digest),
            "The committed manifest has no digest, which would disable download verification for every client.");
        Assert.Equal(new FileInfo(databasePath).Length, manifest.Database.Size);
        Assert.Equal(
            TestDigest.Sha256Digest(databasePath),
            manifest.Database.Digest!.ToLowerInvariant());

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

        var stamped = TestSqlite.ReadDataFormatStamp(databasePath);

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
            new[] { "digest", "file", "size" },
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

        var index = DataChannel.ParseIndex(File.ReadAllText(indexPath));
        Assert.True(index != null, "data/index.json does not satisfy the app's own reader");

        Assert.True(index!.CurrentDataFormatVersion >= DatabaseUpdateService.DataFormatVersion,
            $"index.json publishes schema {index.CurrentDataFormatVersion}, below the "
            + $"{DatabaseUpdateService.DataFormatVersion} this build reads.");
        Assert.True(Directory.Exists(Path.Combine(TestRepo.Root(), "data", $"v{index.CurrentDataFormatVersion}")),
            $"index.json points at schema {index.CurrentDataFormatVersion}, which has no directory.");
    }
}
