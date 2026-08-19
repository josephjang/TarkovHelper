using System.IO;
using System.Xml.Linq;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the versioned data channel (feature-versioned-data-channel.spec.md): the
/// format pin that ties this build's bundled seed database to the endpoint it polls,
/// and the two channel documents it reads.
///
/// The pin is the load-bearing part. One csproj property selects both the seed copied
/// into Assets\ and the URLs DataChannel derives, so a build can only poll
/// the channel its own data belongs to; these tests fail if that wiring is cut.
/// </summary>
public sealed class DataChannelTests
{
    private static XDocument AppCsproj() =>
        XDocument.Load(Path.Combine(TestRepo.Root(), "TarkovHelper", "TarkovHelper.csproj"));

    private static string CsprojDataFormat() =>
        AppCsproj().Descendants("TarkovDataFormatVersion").Single().Value.Trim();

    #region Format pin

    [Fact]
    public void Runtime_data_format_matches_the_csproj_property()
    {
        // The metadata item is the only bridge from the csproj property to runtime; if
        // it is dropped or renamed, the URLs below silently describe a different schema
        // than the bundled database.
        Assert.Equal(CsprojDataFormat(), DatabaseUpdateService.DataFormatVersion.ToString());
    }

    [Fact]
    public void Seed_data_is_sourced_from_this_builds_channel_directory()
    {
        // Assets\ must stop feeding the build: it is the pre-channel endpoint, kept as a
        // mirror. Sourcing the seed from data/v$(TarkovDataFormatVersion) is what makes
        // "bundled data belongs to the polled channel" true by construction rather than
        // by discipline.
        var seeds = AppCsproj().Descendants("None")
            .Where(e => e.Attribute("Link")?.Value is "Assets\\tarkov_data.db" or "Assets\\db_version.txt")
            .ToList();

        Assert.Equal(2, seeds.Count);
        foreach (var seed in seeds)
        {
            var include = seed.Attribute("Include")?.Value;
            var fileName = Path.GetFileName(seed.Attribute("Link")!.Value);
            Assert.Equal($"..\\data\\v$(TarkovDataFormatVersion)\\{fileName}", include);
            Assert.Equal("PreserveNewest", seed.Element("CopyToOutputDirectory")?.Value);
        }
    }

    [Fact]
    public void The_assets_pair_is_removed_from_the_default_glob()
    {
        // Without the Remove, the SDK's default None items and the linked channel items
        // both target Assets\ in the output: two items, one output path, and which one
        // wins is not something to leave to MSBuild ordering.
        var removed = AppCsproj().Descendants("None")
            .Select(e => e.Attribute("Remove")?.Value)
            .Where(v => v != null)
            .ToList();

        Assert.Contains("Assets\\tarkov_data.db", removed);
        Assert.Contains("Assets\\db_version.txt", removed);
    }

    #endregion

    #region Endpoint URLs

    [Fact]
    public void The_manifest_url_is_derived_from_the_running_data_format()
    {
        // A format bump must move the endpoint with it. Pinning the derived segment
        // rather than a literal is what makes a stale hardcoded path impossible.
        Assert.Contains($"/data/v{DatabaseUpdateService.DataFormatVersion}/",
            DataChannel.MANIFEST_URL, StringComparison.Ordinal);
        Assert.EndsWith("/manifest.json", DataChannel.MANIFEST_URL, StringComparison.Ordinal);
    }

    [Fact]
    public void The_index_sits_above_the_format_directories()
    {
        // The index answers "which schema does the project publish now", so it must live
        // outside any one format's directory. If it were inside, a superseded build could
        // only ever read a copy that stopped being maintained with the rest of that
        // directory, which is precisely the state it needs to detect.
        Assert.Equal($"{DataChannel.DATA_ROOT_URL}/index.json", DataChannel.INDEX_URL);
        Assert.StartsWith(DataChannel.DATA_ROOT_URL + "/v",
            DataChannel.CHANNEL_BASE_URL, StringComparison.Ordinal);
    }

    #endregion

    #region Manifest document

    private const string ValidManifest = """
        {
          "schemaVersion": 1,
          "dataFormatVersion": 1,
          "version": "1.0.10",
          "database": { "file": "tarkov_data.db", "digest": "sha256:abc123", "size": 42 }
        }
        """;

    [Fact]
    public void A_valid_manifest_parses_into_its_fields()
    {
        var manifest = DataChannel.ParseManifest(ValidManifest);

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal(1, manifest.DataFormatVersion);
        Assert.Equal("1.0.10", manifest.Version);
        Assert.Equal("tarkov_data.db", manifest.Database.File);
        Assert.Equal("sha256:abc123", manifest.Database.Digest);
        Assert.Equal(42, manifest.Database.Size);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        // Forward compatibility is the point: an endpoint must be able to carry fields
        // for newer builds without disturbing the ones already in the field, which can
        // never be taught the new vocabulary.
        var manifest = DataChannel.ParseManifest("""
            {
              "schemaVersion": 1, "dataFormatVersion": 1, "version": "1.0.10",
              "database": { "file": "tarkov_data.db", "signature": "from-2027" },
              "publishedBy": "someone", "notes": ["a", "b"]
            }
            """);

        Assert.NotNull(manifest);
        Assert.Equal("1.0.10", manifest.Version);
    }

    [Fact]
    public void Integrity_fields_are_optional()
    {
        // Absent hash means "install without verifying", not "reject". Capability lives
        // in the presence of a field, not in a version number.
        var manifest = DataChannel.ParseManifest("""
            { "schemaVersion": 1, "dataFormatVersion": 1, "version": "1.0.10",
              "database": { "file": "tarkov_data.db" } }
            """);

        Assert.NotNull(manifest);
        Assert.Null(manifest.Database.Digest);
        Assert.Null(manifest.Database.Size);
    }

    [Fact]
    public void The_version_token_is_trimmed()
    {
        var manifest = DataChannel.ParseManifest("""
            { "schemaVersion": 1, "dataFormatVersion": 1, "version": "  1.0.10\n",
              "database": { "file": "tarkov_data.db" } }
            """);

        Assert.NotNull(manifest);
        Assert.Equal("1.0.10", manifest.Version);
    }

    [Fact]
    public void A_payload_name_padded_with_whitespace_is_trimmed_rather_than_refused()
    {
        // The same slip the digest and the version are forgiven, forgiven the same way:
        // a stray space around a hand-edited value is formatting, not a different name,
        // and refusing the document over one would stop every install updating. The
        // allowlist stays strict; the trimming happens once, at the document boundary.
        var manifest = DataChannel.ParseManifest("""
            { "schemaVersion": 1, "dataFormatVersion": 1, "version": "1.0.10",
              "database": { "file": "  tarkov_data.db\n" } }
            """);

        Assert.NotNull(manifest);
        // The trimmed name is the one that travels on: it is pasted onto the channel base
        // URL, so a name validated here and used unpadded there would be two names.
        Assert.Equal("tarkov_data.db", manifest.Database.File);
    }

    [Theory]
    // Nothing at all.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Not JSON, or the wrong JSON.
    [InlineData("1.0.10")]
    [InlineData("{ \"schema\": 1, ")]
    [InlineData("[]")]
    // Required fields missing or unusable. A null version would compare unequal to every
    // local version and re-download the database on every check, forever.
    [InlineData("""{ "schemaVersion": 1, "dataFormatVersion": 1, "database": { "file": "tarkov_data.db" } }""")]
    [InlineData("""{ "schemaVersion": 1, "dataFormatVersion": 1, "version": "  ", "database": { "file": "x.db" } }""")]
    [InlineData("""{ "schemaVersion": 1, "dataFormatVersion": 1, "version": "1.0.10" }""")]
    [InlineData("""{ "schemaVersion": 1, "dataFormatVersion": 1, "version": "1.0.10", "database": {} }""")]
    [InlineData("""{ "dataFormatVersion": 1, "version": "1.0.10", "database": { "file": "x.db" } }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "1.0.10", "database": { "file": "x.db" } }""")]
    // A version token the local bookmark cannot carry. It is written verbatim and read
    // back as the first non-blank line, so an embedded line break comes back truncated,
    // compares unequal to the published version on every launch, and re-downloads the
    // whole database hourly forever.
    [InlineData("""{ "schemaVersion": 1, "dataFormatVersion": 1, "version": "1.0\n.0", "database": { "file": "x.db" } }""")]
    [InlineData("""{ "schemaVersion": 1, "dataFormatVersion": 1, "version": "1.0\r\n.0", "database": { "file": "x.db" } }""")]
    // A payload name that is not a bare file. The name is pasted onto the channel base
    // URL, so a traversal segment silently repoints the download at another endpoint.
    [InlineData("""{ "schemaVersion": 1, "dataFormatVersion": 1, "version": "1.0.10", "database": { "file": "../v2/x.db" } }""")]
    [InlineData("""{ "schemaVersion": 1, "dataFormatVersion": 1, "version": "1.0.10", "database": { "file": "sub/x.db" } }""")]
    [InlineData("""{ "schemaVersion": 1, "dataFormatVersion": 1, "version": "1.0.10", "database": { "file": "https://elsewhere.example/x.db" } }""")]
    public void An_unusable_manifest_is_a_failed_check(string? content)
    {
        // Null, not a fabricated document: an unreadable manifest must behave like a
        // failed fetch (no download, no local state change), never like a new version.
        Assert.Null(DataChannel.ParseManifest(content));
    }

    #endregion

    #region Payload names

    [Theory]
    [InlineData("tarkov_data.db")]
    // The shapes a later publish could plausibly reach for, which must keep working:
    // a version-stamped name is the reason the payload path is data and not a constant.
    [InlineData("tarkov_data.1.0.11.db")]
    [InlineData("tarkov-data.db.gz")]
    [InlineData("A0.db")]
    public void A_bare_file_name_is_accepted(string file)
    {
        Assert.True(DataChannel.IsBarePayloadName(file));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // Traversal, in every spelling the URL layer would resolve against the endpoint.
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../v2/tarkov_data.db")]
    [InlineData("..\\v2\\tarkov_data.db")]
    [InlineData("sub/tarkov_data.db")]
    [InlineData("/tarkov_data.db")]
    // Percent-encoding, which URI normalization unescapes before the request goes out.
    [InlineData("%2e%2e/v2/tarkov_data.db")]
    // A name that stops being a name: another host, another drive, a query, a fragment.
    [InlineData("https://elsewhere.example/tarkov_data.db")]
    [InlineData("C:/windows/tarkov_data.db")]
    [InlineData("tarkov_data.db?raw=1")]
    [InlineData("tarkov_data.db#frag")]
    [InlineData("tarkov data.db")]
    [InlineData("tarkov_data.db\n")]
    public void A_name_that_is_not_a_bare_file_is_rejected(string? file)
    {
        // An allowlist rather than a blocklist of separators: the channel only ever
        // publishes plain names, and an allowlist cannot be walked around by an encoding
        // nobody thought of.
        Assert.False(DataChannel.IsBarePayloadName(file));
    }

    #endregion

    #region Version tokens

    [Theory]
    [InlineData("1.0.10")]
    // The shapes a later publish could plausibly reach for, which must keep working:
    // CalVer, a pre-release, semver build metadata.
    [InlineData("2026.7.0")]
    [InlineData("1.0.11-rc.2")]
    [InlineData("1.0.11+build.5")]
    [InlineData("v1_0")]
    public void A_bare_version_token_is_accepted(string version)
    {
        Assert.True(DataChannel.IsBareVersionToken(version));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // The failure this rule exists for: the token is written verbatim to db_version.txt
    // and read back as the first non-blank line, so anything past a line break is lost
    // and the truncated token compares unequal to every version the channel publishes.
    [InlineData("1.0\n.0")]
    [InlineData("1.0\r\n.0")]
    [InlineData("1.0.10\nendpoint: elsewhere")]
    // Whitespace inside the token, which trimming cannot reach.
    [InlineData("1.0 .0")]
    [InlineData("1.0\t0")]
    // A token that stops being a token, judged by the same allowlist as a payload name.
    [InlineData("../1.0.10")]
    [InlineData("1.0.10?raw=1")]
    public void A_version_token_the_bookmark_could_not_carry_is_rejected(string? version)
    {
        Assert.False(DataChannel.IsBareVersionToken(version));
    }

    #endregion

    #region Digest grammar

    [Theory]
    [InlineData("sha256:abc123", "sha256", "abc123")]
    // Padding around either half is a formatting slip in a hand-edited document, not a
    // different algorithm and not a different hash.
    [InlineData("  sha256: abc123  ", "sha256", "abc123")]
    // An algorithm this build cannot compute still parses; refusing it is a decision the
    // caller makes, not something the grammar can express.
    [InlineData("sha512:0badc0de", "sha512", "0badc0de")]
    // Hex case is preserved: the comparison is case-insensitive, and folding here would
    // hide which spelling the manifest actually carried when the comparison fails.
    [InlineData("sha256:ABC123", "sha256", "ABC123")]
    // The FIRST colon separates, so a hash notation that carries its own colons stays in
    // the hex half rather than truncating there.
    [InlineData("sha256:ab:cd", "sha256", "ab:cd")]
    public void A_digest_splits_into_the_algorithm_and_the_hex_it_expects(
        string digest, string algorithm, string hex)
    {
        var parsed = DataChannel.ParseDigest(digest);

        Assert.NotNull(parsed);
        Assert.Equal(algorithm, parsed.Value.Algorithm);
        Assert.Equal(hex, parsed.Value.Hex);
    }

    [Theory]
    // Absence is not this method's business (the caller decides what a missing digest
    // means, and that decision is what keeps "absent" and "malformed" apart), but it must
    // not be mistaken for a shape either.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    // No algorithm named at all: a hex digest pasted out of a manifest without its prefix,
    // which is the realistic hand-edit the prefix was introduced to make visible.
    [InlineData("0badc0de")]
    // Named but empty, or a prefix with no name in it: half a digest is not a usable one.
    [InlineData("sha256:")]
    [InlineData("sha256:   ")]
    [InlineData(":0badc0de")]
    [InlineData("   :0badc0de")]
    [InlineData(":")]
    public void A_digest_that_is_not_algorithm_and_hex_yields_nothing(string? digest)
    {
        Assert.Null(DataChannel.ParseDigest(digest));
    }

    #endregion

    #region Index document

    [Fact]
    public void A_valid_index_parses_into_its_fields()
    {
        var index = DataChannel.ParseIndex("""{ "schemaVersion": 1, "currentDataFormatVersion": 3 }""");

        Assert.NotNull(index);
        Assert.Equal(1, index.SchemaVersion);
        Assert.Equal(3, index.CurrentDataFormatVersion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "schemaVersion": 1 }""")]
    [InlineData("""{ "currentDataFormatVersion": 2 }""")]
    [InlineData("""{ "schemaVersion": 1, "currentDataFormatVersion": 0 }""")]
    public void An_unusable_index_yields_nothing(string? content)
    {
        // The caller keeps its previous knowledge on null. Returning a default here
        // would let a broken index quietly declare every build current.
        Assert.Null(DataChannel.ParseIndex(content));
    }

    [Fact]
    public void An_index_from_a_newer_document_schema_still_names_the_published_data_format()
    {
        // Deliberately the opposite of the manifest rule. Refusing a manifest this build
        // cannot read declines an install, which is always safe; refusing the index means
        // never learning this build was left behind, and the publish most likely to give
        // index.json a new shape is the one that bumps the data format, i.e. the very
        // publish that strands the builds running this code. So the one field every index
        // schema carries is read, and the fields this build has never heard of are
        // ignored the way the manifest ignores its own.
        var newer = DataChannel.MAX_SUPPORTED_SCHEMA_VERSION + 1;

        var index = DataChannel.ParseIndex(
            $$"""
              { "schemaVersion": {{newer}}, "currentDataFormatVersion": 7,
                "publishedAt": "2027-01-01", "channels": ["stable", "beta"] }
              """);

        Assert.NotNull(index);
        Assert.Equal(7, index.CurrentDataFormatVersion);
    }

    [Fact]
    public void A_newer_index_that_no_longer_publishes_a_data_format_yields_nothing()
    {
        // What makes reading a newer shape safe: currentDataFormatVersion is promised to
        // keep its name and meaning forever, because fielded builds derive their stranded
        // notice from it and can never be taught a replacement. A schema that drops or
        // renames it leaves this build reading 0, which is refused, so the caller keeps
        // its last known state rather than believing a document that no longer answers
        // the question it is asking.
        var newer = DataChannel.MAX_SUPPORTED_SCHEMA_VERSION + 1;

        Assert.Null(DataChannel.ParseIndex(
            $$"""{ "schemaVersion": {{newer}}, "publishedDataFormat": 9 }"""));
    }

    [Fact]
    public void An_index_at_the_highest_understood_schema_still_parses()
    {
        // The bound is an upper bound, not an equality: the shape this build was written
        // against has to keep being readable.
        var index = DataChannel.ParseIndex(
            $$"""{ "schemaVersion": {{DataChannel.MAX_SUPPORTED_SCHEMA_VERSION}}, "currentDataFormatVersion": 7 }""");

        Assert.NotNull(index);
        Assert.Equal(7, index.CurrentDataFormatVersion);
    }

    #endregion

    [Fact]
    public void An_unflagged_check_result_is_not_superseded()
    {
        // The optional parameter's default must be the harmless one: a result built
        // without the flag must never tell a healthy build its data has stopped.
        // (Propagation through a real check is covered by DataChannelEndpointServingTests.)
        Assert.False(new UpdateCheckResult(true, false, "up to date").IsSuperseded);
    }
}
