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
/// into Assets\ and the URLs DatabaseUpdateService derives, so a build can only poll
/// the channel its own data belongs to; these tests fail if that wiring is cut.
/// </summary>
public sealed class DataChannelTests
{
    private static XDocument AppCsproj() =>
        XDocument.Load(Path.Combine(TestRepo.Root(), "TarkovHelper", "TarkovHelper.csproj"));

    private static string CsprojDataFormat() =>
        AppCsproj().Descendants("TarkovDataFormat").Single().Value.Trim();

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
        // mirror. Sourcing the seed from data/v$(TarkovDataFormat) is what makes
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
            Assert.Equal($"..\\data\\v$(TarkovDataFormat)\\{fileName}", include);
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
            DatabaseUpdateService.MANIFEST_URL, StringComparison.Ordinal);
        Assert.EndsWith("/manifest.json", DatabaseUpdateService.MANIFEST_URL, StringComparison.Ordinal);
    }

    [Fact]
    public void The_index_sits_above_the_format_directories()
    {
        // The index answers "which schema does the project publish now", so it must live
        // outside any one format's directory. If it were inside, a superseded build could
        // only ever read a copy that stopped being maintained with the rest of that
        // directory, which is precisely the state it needs to detect.
        Assert.Equal($"{DatabaseUpdateService.DATA_ROOT_URL}/index.json", DatabaseUpdateService.INDEX_URL);
        Assert.StartsWith(DatabaseUpdateService.DATA_ROOT_URL + "/v",
            DatabaseUpdateService.CHANNEL_BASE_URL, StringComparison.Ordinal);
    }

    #endregion

    #region Manifest document

    private const string ValidManifest = """
        {
          "schema": 1,
          "dataSchema": 1,
          "version": "1.0.10",
          "database": { "file": "tarkov_data.db", "sha256": "abc123", "size": 42 }
        }
        """;

    [Fact]
    public void A_valid_manifest_parses_into_its_fields()
    {
        var manifest = DatabaseUpdateService.ParseManifest(ValidManifest);

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.Schema);
        Assert.Equal(1, manifest.DataSchema);
        Assert.Equal("1.0.10", manifest.Version);
        Assert.Equal("tarkov_data.db", manifest.Database.File);
        Assert.Equal("abc123", manifest.Database.Sha256);
        Assert.Equal(42, manifest.Database.Size);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        // Forward compatibility is the point: an endpoint must be able to carry fields
        // for newer builds without disturbing the ones already in the field, which can
        // never be taught the new vocabulary.
        var manifest = DatabaseUpdateService.ParseManifest("""
            {
              "schema": 1, "dataSchema": 1, "version": "1.0.10",
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
        var manifest = DatabaseUpdateService.ParseManifest("""
            { "schema": 1, "dataSchema": 1, "version": "1.0.10",
              "database": { "file": "tarkov_data.db" } }
            """);

        Assert.NotNull(manifest);
        Assert.Null(manifest.Database.Sha256);
        Assert.Null(manifest.Database.Size);
    }

    [Fact]
    public void The_version_token_is_trimmed()
    {
        var manifest = DatabaseUpdateService.ParseManifest("""
            { "schema": 1, "dataSchema": 1, "version": "  1.0.10\n",
              "database": { "file": "tarkov_data.db" } }
            """);

        Assert.NotNull(manifest);
        Assert.Equal("1.0.10", manifest.Version);
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
    [InlineData("""{ "schema": 1, "dataSchema": 1, "database": { "file": "tarkov_data.db" } }""")]
    [InlineData("""{ "schema": 1, "dataSchema": 1, "version": "  ", "database": { "file": "x.db" } }""")]
    [InlineData("""{ "schema": 1, "dataSchema": 1, "version": "1.0.10" }""")]
    [InlineData("""{ "schema": 1, "dataSchema": 1, "version": "1.0.10", "database": {} }""")]
    [InlineData("""{ "dataSchema": 1, "version": "1.0.10", "database": { "file": "x.db" } }""")]
    [InlineData("""{ "schema": 1, "version": "1.0.10", "database": { "file": "x.db" } }""")]
    public void An_unusable_manifest_is_a_failed_check(string? content)
    {
        // Null, not a fabricated document: an unreadable manifest must behave like a
        // failed fetch (no download, no local state change), never like a new version.
        Assert.Null(DatabaseUpdateService.ParseManifest(content));
    }

    #endregion

    #region Index document

    [Fact]
    public void A_valid_index_parses_into_its_fields()
    {
        var index = DatabaseUpdateService.ParseIndex("""{ "schema": 1, "currentDataSchema": 3 }""");

        Assert.NotNull(index);
        Assert.Equal(1, index.Schema);
        Assert.Equal(3, index.CurrentDataSchema);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "schema": 1 }""")]
    [InlineData("""{ "currentDataSchema": 2 }""")]
    [InlineData("""{ "schema": 1, "currentDataSchema": 0 }""")]
    public void An_unusable_index_yields_nothing(string? content)
    {
        // The caller keeps its previous knowledge on null. Returning a default here
        // would let a broken index quietly declare every build current.
        Assert.Null(DatabaseUpdateService.ParseIndex(content));
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
