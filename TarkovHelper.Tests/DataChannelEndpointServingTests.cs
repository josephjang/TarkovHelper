using System.IO;
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

    /// <summary>
    /// The version the install already carries wherever a test asserts that a refusal
    /// changed nothing. Named because <see cref="AssertNothingWasInstalled"/> asserts
    /// against it: the constant is the pairing between what the fixture wrote and what
    /// the assertion expects to still be there.
    /// </summary>
    private const string InstalledVersion = "1.0.10";

    /// <summary>
    /// The bytes an endpoint serves. A real SQLite database, and stamped for this build's
    /// data format, because the install path opens the payload to read that stamp and
    /// refuses anything SQLite cannot open at all.
    /// </summary>
    private static readonly byte[] PublishedDb = NewStampedDatabase(DatabaseUpdateService.DataFormatVersion);

    /// <summary>
    /// Whatever the install already had. Never opened by the code under test, only
    /// compared against, so arbitrary bytes make a clearer "this file was left alone".
    /// </summary>
    private static readonly byte[] InstalledDb = Encoding.UTF8.GetBytes("older-installed-database");

    /// <summary>Bytes SQLite cannot open: a truncated download, or an error page served with a 200.</summary>
    private static readonly byte[] NotADatabase = Encoding.UTF8.GetBytes("<html>504 Gateway Timeout</html>");

    private readonly TempStoreRoot _temp = new("datachannel");

    /// <summary>The schema this build polls; the served fixture has to match it.</summary>
    private static int Pin => DatabaseUpdateService.DataFormatVersion;

    public void Dispose() => _temp.Dispose();

    /// <summary>
    /// A real SQLite database carrying a given user_version, returned as the bytes an
    /// endpoint would serve. Real rather than fabricated because the install path opens
    /// it with SQLite, both to read the stamp and to decide the payload is a database at
    /// all.
    /// </summary>
    private static byte[] NewStampedDatabase(int userVersion) => TestSqlite.BuildDatabase(
        $"CREATE TABLE Marker (Id INTEGER); PRAGMA user_version = {userVersion};");

    /// <summary>
    /// Builds a served channel root: index.json plus one endpoint directory.
    /// </summary>
    /// <param name="payloadFile">
    /// What the manifest calls the payload. Defaults to the file actually written, so
    /// only the tests probing an untrustworthy name have to think about it.
    /// </param>
    /// <param name="integrity">
    /// False publishes the payload with no digest and no size, the shape a manifest is
    /// allowed to take and the one where nothing verifies the content. Exclusive with
    /// <paramref name="digest"/> and <paramref name="size"/>: passing either alongside it
    /// is rejected rather than silently dropped, because a fixture that believes it
    /// published a digest would assert a refusal the manifest never caused.
    /// </param>
    private string NewServedChannel(
        string version,
        byte[] database,
        int? currentDataFormatVersion = null,
        string? digest = null,
        long? size = null,
        int manifestSchemaVersion = 1,
        int? dataFormatVersion = null,
        int indexSchemaVersion = 1,
        string? payloadFile = null,
        bool integrity = true)
    {
        // "Publish no integrity fields" and "publish this digest" are contradictory
        // instructions. Resolving one in favour of the other silently would let a test
        // assert a refusal that came from a different cause than the one it names.
        if (!integrity && (digest is not null || size is not null))
        {
            throw new ArgumentException(
                "integrity: false publishes no digest and no size, so a digest or size passed with it "
                + "would be discarded. Drop the digest/size, or publish with integrity.",
                nameof(integrity));
        }

        var root = _temp.NewFolder("served-channel");
        var endpoint = Path.Combine(root, $"v{Pin}");
        Directory.CreateDirectory(endpoint);

        File.WriteAllBytes(Path.Combine(endpoint, DatabaseFile), database);
        File.WriteAllText(Path.Combine(endpoint, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = manifestSchemaVersion,
            dataFormatVersion = dataFormatVersion ?? Pin,
            version,
            database = new
            {
                file = payloadFile ?? DatabaseFile,
                digest = integrity ? digest ?? TestDigest.Sha256Digest(database) : null,
                size = integrity ? size ?? database.Length : (long?)null,
            },
        }));
        File.WriteAllText(Path.Combine(root, "index.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = indexSchemaVersion,
            currentDataFormatVersion = currentDataFormatVersion ?? Pin,
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

    /// <summary>
    /// Everything "the payload was refused" has to mean, in one place: the check failed
    /// and says so, the working database is byte for byte what it was, the bookmark did
    /// not advance in memory or on disk, and the temp download is gone.
    /// <para>
    /// The bookmark half is the part a refusal test most easily forgets and the part that
    /// matters most: an advanced bookmark would make the very next check call the
    /// database it just refused current and never retry, so the install would sit on the
    /// old bytes under the new version number forever. Written once because five refusal
    /// paths assert it, and the copy that omitted two of these checks proved they drift.
    /// </para>
    /// Applies to any refusal reached with a <see cref="NewInstalledAssets"/> folder at
    /// <see cref="InstalledVersion"/> holding <see cref="InstalledDb"/>.
    /// </summary>
    private static async Task AssertNothingWasInstalled(
        string assets, DatabaseUpdateService service, UpdateCheckResult result)
    {
        Assert.False(result.Success);
        Assert.False(result.WasUpdated);
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        Assert.Equal(InstalledVersion, service.LocalVersion);
        Assert.Equal(InstalledVersion, await File.ReadAllTextAsync(Path.Combine(assets, VersionFile)));
        Assert.False(File.Exists(Path.Combine(assets, DatabaseFile + ".tmp")));
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
    public async Task A_matching_version_with_no_installed_database_installs_it_anyway()
    {
        // The bookmark says the served version is already installed, but the database it
        // vouches for is gone: quarantined by antivirus, half copied, or deleted by hand.
        // Trusting the bookmark alone would report "up to date" forever and never repair
        // the install.
        using var server = new LocalFileServer(NewServedChannel("1.0.10", PublishedDb));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        File.Delete(Path.Combine(assets, DatabaseFile));
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.True(result.WasUpdated);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        Assert.Equal("1.0.10", service.LocalVersion);
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
            NewServedChannel("2.0.0", PublishedDb, digest: TestDigest.Sha256Digest(Encoding.UTF8.GetBytes("different"))));
        var assets = NewInstalledAssets(InstalledVersion, InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        await AssertNothingWasInstalled(assets, service, result);
    }

    [Theory]
    [InlineData("sha512:0badc0de")]   // an algorithm this build does not implement
    [InlineData("blake3:0badc0de")]
    public async Task A_digest_this_build_cannot_check_installs_without_verifying(string digest)
    {
        // The reason the algorithm prefix exists: a build that only knows sha256 can
        // tell "I cannot check this" apart from "there is nothing to check", and says so
        // in the log instead of silently skipping. It still installs, because refusing
        // would turn a future hash upgrade into a breaking change for every build already
        // in the field, which is the outcome this channel exists to avoid.
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb, digest: digest));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    [Theory]
    [InlineData("0badc0de")]   // no algorithm named at all: a hex digest copied without its prefix
    [InlineData("sha256:")]    // named but empty
    [InlineData(":0badc0de")]  // a prefix with no name in it
    public async Task A_digest_that_does_not_name_an_algorithm_is_refused(string digest)
    {
        // A string that is not "<algorithm>:<hex>" is a malformed document, not a publish
        // from the future. Treating it as leniently as an unimplemented algorithm would
        // switch verification off for exactly the realistic hand-edit that causes it,
        // pasting a bare hex digest out of the manifest without the "sha256:" prefix,
        // which is the failure the prefix was introduced to make visible.
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb, digest: digest));
        var assets = NewInstalledAssets(InstalledVersion, InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        await AssertNothingWasInstalled(assets, service, result);
    }

    [Fact]
    public async Task A_digest_padded_with_whitespace_is_still_checked()
    {
        // A hand-edited manifest can easily carry a stray space around the digest. That
        // is a formatting slip, not a different algorithm, so the hash still has to be
        // computed and compared. Pinned with a digest that does not match, because a
        // padded matching digest cannot tell "checked and passed" apart from "skipped".
        var padded = $"  sha256: {TestDigest.Sha256Hex(Encoding.UTF8.GetBytes("different"))}  ";
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb, digest: padded));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.False(result.Success);
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        Assert.Equal("1.0.10", service.LocalVersion);
    }

    [Fact]
    public async Task A_padded_digest_that_matches_still_installs()
    {
        // The other half of the padding rule: trimming must not turn a good publish into
        // a refusal.
        var padded = $"  sha256: {TestDigest.Sha256Hex(PublishedDb)}  ";
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb, digest: padded));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    [Fact]
    public async Task A_sha256_digest_is_matched_case_insensitively()
    {
        // Hex case is not part of the contract, and a publisher writing uppercase must
        // not silently invalidate every download.
        // Only the hex is uppercased: the algorithm name is not the part under test, and
        // uppercasing it too would be probing a different rule.
        var upper = $"sha256:{TestDigest.Sha256Hex(PublishedDb).ToUpperInvariant()}";
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb, digest: upper));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        Assert.True((await service.CheckAndUpdateAsync()).Success);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
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
            $$"""{ "schemaVersion": 1, "dataFormatVersion": {{Pin}}, "version": "2.0.0", "database": { "file": "{{DatabaseFile}}" } }""");
        await File.WriteAllTextAsync(Path.Combine(root, "index.json"),
            $$"""{ "schemaVersion": 1, "currentDataFormatVersion": {{Pin}} }""");

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
            NewServedChannel("1.0.10", PublishedDb, currentDataFormatVersion: Pin + 1));
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
            NewServedChannel("2.0.0", PublishedDb, currentDataFormatVersion: Pin + 1));
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
        using var server = new LocalFileServer(NewServedChannel("1.0.10", PublishedDb, currentDataFormatVersion: Pin));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        Assert.False((await service.CheckAndUpdateAsync()).IsSuperseded);
    }

    [Fact]
    public async Task A_failed_index_fetch_does_not_clear_a_known_supersession()
    {
        // A build that stopped receiving data has more failing checks by nature, and a
        // transient failure must not flicker the notice off and back on.
        var root = NewServedChannel("1.0.10", PublishedDb, currentDataFormatVersion: Pin + 1);
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

    #region Data format stamp

    [Fact]
    public async Task A_database_stamped_for_another_data_format_is_refused()
    {
        // The payload contradicting its own endpoint. The manifest can be internally
        // consistent and still describe the wrong file, so the database has to be able
        // to speak for itself.
        var wrongFormat = NewStampedDatabase(Pin + 1);
        using var server = new LocalFileServer(NewServedChannel("2.0.0", wrongFormat));
        var assets = NewInstalledAssets(InstalledVersion, InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        await AssertNothingWasInstalled(assets, service, result);
    }

    [Fact]
    public async Task A_database_stamped_for_this_data_format_installs()
    {
        var rightFormat = NewStampedDatabase(Pin);
        using var server = new LocalFileServer(NewServedChannel("2.0.0", rightFormat));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(rightFormat, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
    }

    [Fact]
    public async Task An_unstamped_database_is_refused()
    {
        // user_version defaults to 0, so an unstamped file makes no claim at all. Every
        // publish stamps before hashing and aborts if it cannot, so a payload without a
        // stamp did not come from a publish: a directory populated by hand, a copy from
        // the wrong build, a half-finished bump. Those are exactly what this check is
        // for, and they are also the payloads whose manifest can carry no digest, so
        // accepting them installs a file nothing verified.
        var unstamped = NewStampedDatabase(0);
        using var server = new LocalFileServer(NewServedChannel("2.0.0", unstamped));
        var assets = NewInstalledAssets(InstalledVersion, InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        await AssertNothingWasInstalled(assets, service, result);
    }

    /// <param name="integrity">
    /// The three ways a payload reaches the install with nothing having checked its
    /// content: no digest published at all, a digest in an algorithm this build cannot
    /// compute, or a digest that matched because the publisher really did serve these
    /// bytes. Every one of them has to stop at "SQLite cannot open this".
    /// </param>
    /// <remarks>
    /// A different refusal from the unstamped one above: that file opens fine and only
    /// fails to say which format it is, while this one is not a database at all, so the
    /// stamp can never be read in the first place.
    /// </remarks>
    [Theory]
    [InlineData(true, null)]              // sha256 over exactly these bytes: it matches
    [InlineData(true, "sha512:0badc0de")] // an algorithm this build cannot check
    [InlineData(false, null)]             // no digest and no size published
    public async Task A_payload_that_is_not_a_database_is_refused(bool integrity, string? digest)
    {
        // This file is not a database at all, no reader could open it, and installing it
        // would leave the app with nothing to read while the version bookmark recorded it
        // as current until the next publish.
        using var server = new LocalFileServer(
            NewServedChannel("2.0.0", NotADatabase, digest: digest, integrity: integrity));
        var assets = NewInstalledAssets(InstalledVersion, InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        await AssertNothingWasInstalled(assets, service, result);
    }

    #endregion

    #region Swapping into place

    /// <summary>
    /// The swap is the only step that can destroy something. These drive it directly,
    /// because the Windows file-lock branch it exists to survive cannot be provoked
    /// through a whole check.
    /// </summary>
    private DatabaseUpdateService NewOfflineService(string assetsPath) =>
        // Port 1 is never listening: nothing here reaches the network.
        new("http://127.0.0.1:1", assetsPath);

    [Fact]
    public async Task A_swap_replaces_the_database_and_leaves_no_backup()
    {
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = NewOfflineService(assets);
        var databasePath = Path.Combine(assets, DatabaseFile);
        var tempPath = databasePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, PublishedDb);

        await service.SwapIntoPlaceAsync(tempPath);

        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(databasePath));
        Assert.False(File.Exists(tempPath));
        // Housekeeping is part of the swap, not left for the next run to trip over.
        Assert.False(File.Exists(databasePath + ".bak"));
    }

    [Fact]
    public async Task A_swap_onto_a_missing_database_installs_it()
    {
        // First install, or a working database somebody deleted. There is nothing to
        // back up, and the swap must not require one.
        var assets = _temp.NewFolder("fresh-install");
        using var service = NewOfflineService(assets);
        var databasePath = Path.Combine(assets, DatabaseFile);
        var tempPath = databasePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, PublishedDb);

        await service.SwapIntoPlaceAsync(tempPath);

        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(databasePath));
        Assert.False(File.Exists(tempPath));
        Assert.False(File.Exists(databasePath + ".bak"));
    }

    [Fact]
    public async Task A_swap_that_cannot_take_the_file_never_leaves_the_install_without_a_database()
    {
        // The failure this guards: a swap done as two moves has a window where the
        // database path holds neither the old file nor the new one, and a throw landing
        // in that window leaves every DbService with nothing to open on the next launch,
        // with no code anywhere that reads the backup back.
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = NewOfflineService(assets);
        var databasePath = Path.Combine(assets, DatabaseFile);
        var tempPath = databasePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, PublishedDb);

        using (new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => service.SwapIntoPlaceAsync(tempPath));
        }

        Assert.True(File.Exists(databasePath), "the swap failed and took the database with it");
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(databasePath));
    }

    [Fact]
    public async Task A_swap_onto_a_read_only_database_still_installs()
    {
        // A destination carrying the read-only attribute (an archive extracted with DOS
        // attributes preserved, a backup or antivirus tool, a user who set it) makes
        // File.Replace throw UnauthorizedAccessException, which is not an IOException and
        // so slips past any retry filter naming one. Left unhandled it is permanent: the
        // install downloads the whole payload and fails identically on every hourly
        // check, for as long as the attribute is there.
        var assets = NewInstalledAssets(InstalledVersion, InstalledDb);
        using var service = NewOfflineService(assets);
        var databasePath = Path.Combine(assets, DatabaseFile);
        var tempPath = databasePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, PublishedDb);
        File.SetAttributes(databasePath, File.GetAttributes(databasePath) | FileAttributes.ReadOnly);

        await service.SwapIntoPlaceAsync(tempPath);

        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(databasePath));
        Assert.False(File.Exists(tempPath));
        Assert.False(File.Exists(databasePath + ".bak"));
        // And the installed database is writable, so the next update is not wedged either.
        Assert.False(File.GetAttributes(databasePath).HasFlag(FileAttributes.ReadOnly));
    }

    [Fact]
    public async Task A_swap_gets_past_a_read_only_backup_left_behind_by_an_earlier_build()
    {
        // The wedge an upgraded install inherits. A backup written from a read-only
        // database inherits the attribute, and Windows then refuses both to delete it and
        // to replace through it, so File.Replace fails with IOException even onto a
        // perfectly normal destination. A backup cleanup that swallows its own failure
        // makes that state permanent: every later swap fails the same way.
        var assets = NewInstalledAssets(InstalledVersion, InstalledDb);
        using var service = NewOfflineService(assets);
        var databasePath = Path.Combine(assets, DatabaseFile);
        var backupPath = databasePath + ".bak";
        var tempPath = databasePath + ".tmp";
        await File.WriteAllBytesAsync(tempPath, PublishedDb);
        await File.WriteAllBytesAsync(backupPath, InstalledDb);
        File.SetAttributes(backupPath, File.GetAttributes(backupPath) | FileAttributes.ReadOnly);

        await service.SwapIntoPlaceAsync(tempPath);

        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(databasePath));
        Assert.False(File.Exists(backupPath));
        Assert.False(File.Exists(tempPath));
    }

    #endregion

    #region Refusals

    [Fact]
    public async Task A_manifest_from_a_newer_document_schema_is_refused()
    {
        // Someone published a shape this build was never taught to read at its own URL.
        // Refuse and change nothing; this is an operator error, not a user's problem.
        using var server = new LocalFileServer(
            NewServedChannel("2.0.0", PublishedDb, manifestSchemaVersion: DataChannel.MAX_SUPPORTED_SCHEMA_VERSION + 1));
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
            NewServedChannel("2.0.0", PublishedDb, dataFormatVersion: Pin + 1));
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

    [Theory]
    [InlineData("../v99/tarkov_data.db")]
    [InlineData("..\\v99\\tarkov_data.db")]
    [InlineData("sub/tarkov_data.db")]
    [InlineData("%2e%2e/v99/tarkov_data.db")]
    [InlineData("http://127.0.0.1:1/tarkov_data.db")]
    [InlineData("..")]
    public async Task A_payload_name_that_is_not_a_bare_file_is_refused(string payloadFile)
    {
        // The name is pasted onto the channel base URL, and URI normalization resolves a
        // ".." segment against that base before the request goes out, so an unchecked
        // name escapes this build's endpoint directory. Nothing downstream would catch
        // it either: the digest and size are optional.
        var root = NewServedChannel("2.0.0", PublishedDb, payloadFile: payloadFile);

        // A real file at the traversal target, so the test fails loudly if the fetch
        // ever reaches it rather than merely 404ing by accident.
        var neighbour = Path.Combine(root, "v99");
        Directory.CreateDirectory(neighbour);
        await File.WriteAllBytesAsync(Path.Combine(neighbour, DatabaseFile), NotADatabase);

        using var server = new LocalFileServer(root);
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        // An unusable manifest is a failed check: no download, no local state change.
        Assert.False(result.Success);
        Assert.DoesNotContain(server.RequestedPaths, p => p.EndsWith(DatabaseFile, StringComparison.Ordinal));
        Assert.Equal(InstalledDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        Assert.Equal("1.0.10", service.LocalVersion);
    }

    [Fact]
    public async Task A_payload_name_padded_with_whitespace_is_fetched_from_the_trimmed_name()
    {
        // A stray space around the name in a hand-edited manifest is a formatting slip,
        // and it is forgiven exactly the way the digest's is: refusing the document would
        // stop every install updating over one character nobody can see. The name that
        // then goes onto the URL has to be the trimmed one, or the request would carry
        // the padding the check was passed with.
        using var server = new LocalFileServer(
            NewServedChannel("2.0.0", PublishedDb, payloadFile: $"  {DatabaseFile}\n"));
        var assets = NewInstalledAssets(InstalledVersion, InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        Assert.Contains(server.RequestedPaths, p => p == $"/v{Pin}/{DatabaseFile}");
    }

    [Fact]
    public async Task An_index_from_a_newer_document_schema_still_reports_the_supersession()
    {
        // The combination this whole mechanism exists for, and the one that used to lose
        // the notice: index.json is the only part of the channel a publish rewrites, and
        // the publish that gives it a new shape is most likely the one that bumps the
        // data format, which is precisely the publish that strands this build. Treating
        // the newer shape as unreadable would leave the amber pill and the "no further
        // data updates" wording off for exactly the users they are for.
        using var server = new LocalFileServer(NewServedChannel(
            "1.0.10",
            PublishedDb,
            currentDataFormatVersion: Pin + 1,
            indexSchemaVersion: DataChannel.MAX_SUPPORTED_SCHEMA_VERSION + 1));
        var assets = NewInstalledAssets(InstalledVersion, InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.IsSuperseded);
        Assert.True(service.IsSuperseded);
        // The endpoint itself is still readable, so the check as a whole still works.
        Assert.True(result.Success, result.Message);
    }

    #endregion

    #region Local bookmark

    [Fact]
    public async Task Only_the_first_non_blank_line_of_the_version_file_is_the_bookmark()
    {
        // The publisher writes and reads this file the same way, so a further line is a
        // directive rather than part of the token. Reading the whole file would make the
        // bookmark compare unequal to every version the channel can ever publish, and
        // re-download the whole database on every check for the life of the install.
        using var server = new LocalFileServer(NewServedChannel("1.0.10", PublishedDb));
        var assets = NewInstalledAssets("\n\n  1.0.10  \nendpoint: not-part-of-the-token\n", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        Assert.Equal("1.0.10", service.LocalVersion);

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.False(result.WasUpdated);
        Assert.DoesNotContain(server.RequestedPaths, p => p.EndsWith(DatabaseFile, StringComparison.Ordinal));
    }

    [Fact]
    public void A_version_file_holding_no_token_leaves_the_bookmark_unset()
    {
        // Blank is "I do not know what I have", which must read as null and not as the
        // empty string: the empty string would compare unequal to every version, which is
        // right, but only by accident.
        var assets = NewInstalledAssets("\n   \n\n", InstalledDb);
        using var service = NewOfflineService(assets);

        Assert.Null(service.LocalVersion);
    }

    [Fact]
    public async Task An_installed_update_survives_a_version_file_that_cannot_be_written()
    {
        // The database is already in place by the time the bookmark is written, so a
        // failure there cannot undo the update. What it must also not do is leave the
        // in-memory bookmark behind: this process would then see a version mismatch it
        // can never resolve and re-download the whole payload on every hourly check.
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        UpdateCheckResult result;
        using (new FileStream(Path.Combine(assets, VersionFile), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await service.CheckAndUpdateAsync();
        }

        Assert.True(result.Success, result.Message);
        Assert.True(result.WasUpdated);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        Assert.Equal("2.0.0", service.LocalVersion);
        Assert.Contains("version file", result.Message, StringComparison.OrdinalIgnoreCase);

        // Proving the loop is gone: the next check finds nothing to do and never asks
        // for the payload again.
        var downloadsSoFar = PayloadRequestCount(server);
        var second = await service.CheckAndUpdateAsync();

        Assert.True(second.Success, second.Message);
        Assert.False(second.WasUpdated);
        Assert.Equal(downloadsSoFar, PayloadRequestCount(server));
    }

    private static int PayloadRequestCount(LocalFileServer server) =>
        server.RequestedPaths.Count(p => p.EndsWith(DatabaseFile, StringComparison.Ordinal));

    #endregion

    #region Check events

    [Fact]
    public async Task A_check_raises_started_once_and_completed_once()
    {
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var started = 0;
        var completed = 0;
        service.UpdateCheckStarted += (_, _) => started++;
        service.UpdateCheckCompleted += (_, _) => completed++;

        await service.CheckAndUpdateAsync();

        Assert.Equal(1, started);
        Assert.Equal(1, completed);
    }

    [Fact]
    public async Task A_throwing_completion_subscriber_neither_escapes_nor_doubles_the_event()
    {
        // This runs from an async void timer callback, where an escaped exception is an
        // unhandled one and ends the process. A subscriber marshalling onto a dispatcher
        // that is shutting down is the realistic thrower.
        using var server = new LocalFileServer(NewServedChannel("1.0.10", PublishedDb));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var raised = 0;
        service.UpdateCheckCompleted += (_, _) =>
        {
            raised++;
            throw new InvalidOperationException("subscriber blew up");
        };

        var result = await service.CheckAndUpdateAsync();

        Assert.Equal(1, raised);
        // The check itself finished; a broken listener does not make it a failed check.
        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task A_throwing_start_subscriber_neither_cancels_the_check_nor_wedges_the_service()
    {
        // A listener is told what is about to happen; it does not get a vote. Letting its
        // throw out of the raise aborted the whole check, so a single handler that throws
        // on every raise (a UI handler touching a disposed control, say) would end every
        // update this install ever attempts while the log blamed the check. The flag that
        // says "a check is running" has to survive it too, or every later check answers
        // "already in progress" for the rest of the process's life.
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb));
        var assets = NewInstalledAssets(InstalledVersion, InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var started = 0;
        var completed = 0;
        service.UpdateCheckStarted += (_, _) =>
        {
            started++;
            throw new InvalidOperationException("subscriber blew up");
        };
        service.UpdateCheckCompleted += (_, _) => completed++;

        var first = await service.CheckAndUpdateAsync();

        // The update ran to completion despite the subscriber, and was still reported.
        Assert.True(first.Success, first.Message);
        Assert.True(first.WasUpdated);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        Assert.Equal(1, started);
        Assert.Equal(1, completed);
        Assert.False(service.IsUpdating);

        var second = await service.CheckAndUpdateAsync();

        Assert.True(second.Success, second.Message);
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task A_throwing_reload_subscriber_neither_fails_the_install_nor_starves_the_rest()
    {
        // This raise happens AFTER the swap and the bookmark, so a throw that escapes it is
        // caught by the check's own catch and reports an install that really did finish as a
        // failed check: the UI then shows a failure over a database that was replaced. And
        // every *DbService reloads from this one event, so the throw has to stop at the
        // handler that threw instead of skipping every service behind it in the list.
        using var server = new LocalFileServer(NewServedChannel("2.0.0", PublishedDb));
        var assets = NewInstalledAssets("1.0.10", InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        var reloaded = 0;
        service.DatabaseUpdated += (_, _) => throw new InvalidOperationException("reload blew up");
        service.DatabaseUpdated += (_, _) => reloaded++;

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.True(result.WasUpdated);
        Assert.Equal(1, reloaded);
        Assert.Equal(PublishedDb, await File.ReadAllBytesAsync(Path.Combine(assets, DatabaseFile)));
        Assert.Equal("2.0.0", service.LocalVersion);
    }

    [Fact]
    public async Task A_completion_subscriber_that_starts_another_check_is_turned_away()
    {
        // Completion is raised while the check is still claimed, so a subscriber that
        // calls back in gets the answer any other caller would get mid-check. Releasing
        // the claim first instead would let every completion start the next check from
        // inside its own notification, back to back with nothing bounding it.
        using var server = new LocalFileServer(NewServedChannel("1.0.10", PublishedDb));
        var assets = NewInstalledAssets(InstalledVersion, InstalledDb);
        using var service = new DatabaseUpdateService(server.BaseUrl, assets);

        Task<UpdateCheckResult>? nested = null;
        var reentered = false;
        service.UpdateCheckCompleted += (_, _) =>
        {
            // Re-entering once is enough to prove it: an unguarded service would recurse
            // here until the test host gave up, which is the failure itself rather than
            // something worth sitting through.
            if (reentered) return;
            reentered = true;
            nested = service.CheckAndUpdateAsync();
        };

        var result = await service.CheckAndUpdateAsync();

        Assert.True(result.Success, result.Message);
        Assert.NotNull(nested);
        // Already finished: the re-entrant call was turned away at the claim, before it
        // reached its first await, rather than being a second check still running.
        Assert.True(nested.IsCompleted, "the re-entrant check started instead of being turned away");

        var reentrant = await nested;

        Assert.False(reentrant.Success);
        Assert.Equal("Update already in progress", reentrant.Message);
        // And the claim is released once the notification is over, so a later check runs.
        Assert.False(service.IsUpdating);
        Assert.True((await service.CheckAndUpdateAsync()).Success);
    }

    #endregion

    #region Fixture contract

    [Fact]
    public void Publishing_without_integrity_refuses_a_digest_it_would_discard()
    {
        // The fixture would otherwise publish "digest": null while the caller believed it
        // published a bad one, so any refusal the test then asserted would come from the
        // missing digest rather than the wrong one.
        var ex = Assert.Throws<ArgumentException>(() => NewServedChannel(
            "2.0.0", PublishedDb, digest: "sha256:0badc0de", integrity: false));

        Assert.Equal("integrity", ex.ParamName);
    }

    [Fact]
    public void Publishing_without_integrity_refuses_a_size_it_would_discard()
    {
        var ex = Assert.Throws<ArgumentException>(() => NewServedChannel(
            "2.0.0", PublishedDb, size: 42, integrity: false));

        Assert.Equal("integrity", ex.ParamName);
    }

    #endregion
}
