using System.IO;
using System.Xml.Linq;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the versioned data channel (feature-versioned-data-channel.spec.md): the
/// format pin that ties this build's bundled seed database to the endpoint it polls,
/// the db_version.txt reader, and the frozen directive that tells a build its channel
/// has ended.
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
        // it is dropped or renamed, the URLs below silently describe a different format
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
    public void Endpoint_urls_are_derived_from_the_running_data_format()
    {
        // A format bump must move the endpoint with it. Pinning the derived segment
        // (rather than a literal) is what makes a stale hardcoded path impossible.
        var expectedSegment = $"/data/v{DatabaseUpdateService.DataFormatVersion}/";

        Assert.Contains(expectedSegment, DatabaseUpdateService.VERSION_URL, StringComparison.Ordinal);
        Assert.Contains(expectedSegment, DatabaseUpdateService.DATABASE_URL, StringComparison.Ordinal);
        Assert.EndsWith("/db_version.txt", DatabaseUpdateService.VERSION_URL, StringComparison.Ordinal);
        Assert.EndsWith("/tarkov_data.db", DatabaseUpdateService.DATABASE_URL, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_endpoint_urls_share_one_channel_directory()
    {
        // The version stamp must describe the database served beside it; two directories
        // would let a check compare one endpoint's version against another's data.
        Assert.StartsWith(DatabaseUpdateService.CHANNEL_BASE_URL + "/",
            DatabaseUpdateService.VERSION_URL, StringComparison.Ordinal);
        Assert.StartsWith(DatabaseUpdateService.CHANNEL_BASE_URL + "/",
            DatabaseUpdateService.DATABASE_URL, StringComparison.Ordinal);
    }

    #endregion

    #region db_version.txt reader

    [Theory]
    [InlineData("1.0.10", "1.0.10")]
    [InlineData("1.0.10\n", "1.0.10")]
    [InlineData("1.0.10\r\n", "1.0.10")]
    [InlineData("  1.0.10  ", "1.0.10")]
    [InlineData("\n\n1.0.10\n", "1.0.10")]
    public void Version_token_is_the_first_non_blank_line(string content, string expected)
    {
        var parsed = DatabaseUpdateService.ParseVersionFile(content);

        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.Version);
        Assert.False(parsed.IsFrozen);
    }

    [Theory]
    [InlineData("1.0.10\nfrozen")]
    [InlineData("1.0.10\nfrozen\n")]
    [InlineData("1.0.10\r\nfrozen\r\n")]
    [InlineData("1.0.10\n\nfrozen\n")]
    [InlineData("1.0.10\nFrozen")]
    [InlineData("1.0.10\n  frozen  ")]
    public void Frozen_directive_is_recognized_after_the_token(string content)
    {
        var parsed = DatabaseUpdateService.ParseVersionFile(content);

        Assert.NotNull(parsed);
        Assert.Equal("1.0.10", parsed.Version);
        Assert.True(parsed.IsFrozen);
    }

    [Fact]
    public void Unknown_directives_are_ignored_without_disturbing_the_token()
    {
        // Forward compatibility is the whole point of the directive list: an endpoint
        // must be able to say new things to newer builds without breaking the builds
        // already in the field, which can never be taught the new vocabulary.
        var parsed = DatabaseUpdateService.ParseVersionFile("1.0.10\nsomething-from-2027\nfrozen");

        Assert.NotNull(parsed);
        Assert.Equal("1.0.10", parsed.Version);
        Assert.True(parsed.IsFrozen);
    }

    [Fact]
    public void A_token_that_merely_contains_frozen_is_not_a_directive()
    {
        // Directives are whole lines. A version token like "1.0.10-frozen-fix" must not
        // freeze the channel, and "frozen" on the first line is a token, not a directive.
        var versionLike = DatabaseUpdateService.ParseVersionFile("1.0.10-frozen-fix");
        Assert.NotNull(versionLike);
        Assert.False(versionLike.IsFrozen);

        var firstLine = DatabaseUpdateService.ParseVersionFile("frozen");
        Assert.NotNull(firstLine);
        Assert.Equal("frozen", firstLine.Version);
        Assert.False(firstLine.IsFrozen);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("\r\n \r\n")]
    public void Content_without_a_token_is_a_failed_check(string? content)
    {
        // Null, not a fabricated version: an empty body must behave like a failed fetch
        // (no download, no local state change), never like a version that differs.
        Assert.Null(DatabaseUpdateService.ParseVersionFile(content));
    }

    #endregion

    #region Frozen state on results

    [Fact]
    public void An_unflagged_check_result_is_not_frozen()
    {
        // The optional parameter's default must be the harmless one: a result built
        // without the flag must never tell a healthy build its data channel has ended.
        // (Propagation through a real check is covered by DataChannelEndpointServingTests,
        // which drives the service against a served frozen endpoint.)
        Assert.False(new UpdateCheckResult(true, false, "up to date").IsEndpointFrozen);
    }

    #endregion
}
