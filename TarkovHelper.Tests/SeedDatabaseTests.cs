using System.IO;
using System.Xml.Linq;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the seed database this assembly tests against: it must be the payload of the data
/// format the app under test pins, not a copy of some other directory that happens to hold a
/// database today.
///
/// The fixtures the e2e tests derive (E2EQuestData, ProfileAttributionE2ETests,
/// SeasonalProfileE2ETests) are built by querying this file and then asserted against a
/// running app that loads its own bundled seed. If the two ever come from different data
/// formats, every one of those tests describes data the app is not showing, and the failures
/// point everywhere except at the cause.
/// </summary>
public sealed class SeedDatabaseTests
{
    private static string ChannelDatabasePath() => Path.Combine(
        TestRepo.Root(), "data", $"v{DatabaseUpdateService.DataFormatVersion}", "tarkov_data.db");

    [Fact]
    public void The_seed_database_is_this_builds_channel_payload()
    {
        var seedPath = TestSeed.DatabasePath;
        var channelPath = ChannelDatabasePath();
        Assert.True(File.Exists(seedPath), $"{seedPath} is missing, so no fixture can be derived");
        Assert.True(File.Exists(channelPath), $"{channelPath} is missing");

        // Byte equality, not row spot-checks: the point is that this is the same publish the
        // app bundles, and any difference at all means it is not.
        Assert.Equal(File.ReadAllBytes(channelPath), File.ReadAllBytes(seedPath));
    }

    [Fact]
    public void The_test_project_does_not_restate_where_the_seed_lives()
    {
        // TarkovHelper\Assets is the pre-channel endpoint, mirrored only while data format 1
        // is live. Including anything from there ties this assembly to a directory that stops
        // being republished at the next format bump, which is exactly the drift that reading
        // the app's own bundled seed (TestSeed.DatabasePath) avoids.
        var csproj = XDocument.Load(Path.Combine(
            TestRepo.Root(), "TarkovHelper.Tests", "TarkovHelper.Tests.csproj"));

        var frozenMirrorItems = FrozenMirrorIncludes(csproj);

        Assert.True(frozenMirrorItems.Count == 0,
            "TarkovHelper.Tests.csproj takes files from the frozen Assets mirror: "
            + string.Join(", ", frozenMirrorItems)
            + ". Take them from the app's build output (or from data/v<N>) instead.");
    }

    [Theory]
    [InlineData(@"..\TarkovHelper\Assets\tarkov_data.db")]
    [InlineData("../TarkovHelper/Assets/tarkov_data.db")]
    [InlineData(@"..\TarkovHelper/Assets\db_version.txt")]
    [InlineData("../tarkovhelper/assets/db_version.txt")]
    public void The_frozen_mirror_is_recognized_whichever_separator_spells_it(string include)
    {
        // MSBuild takes '/' and '\' interchangeably and keeps whichever was written in the
        // item's Identity, so a forward-slash Include reaches the very same frozen mirror file
        // as the backslash one. A guard that knows only one spelling stops only one of them.
        Assert.Equal(new[] { include }, FrozenMirrorIncludes(CsprojWithInclude(include)));
    }

    [Theory]
    [InlineData(@"..\data\v1\tarkov_data.db")]
    [InlineData("../data/v1/tarkov_data.db")]
    [InlineData(@"..\TarkovHelper\TarkovHelper.csproj")]
    [InlineData(@"..\TarkovHelperAssets\tarkov_data.db")]
    public void An_include_outside_the_frozen_mirror_is_left_alone(string include)
    {
        Assert.Empty(FrozenMirrorIncludes(CsprojWithInclude(include)));
    }

    /// <summary>
    /// Every Include attribute in the project that names the pre-channel TarkovHelper/Assets
    /// mirror, in whichever separator style it was written.
    /// </summary>
    private static List<string> FrozenMirrorIncludes(XDocument csproj) => csproj.Descendants()
        .Select(e => e.Attribute("Include")?.Value)
        .OfType<string>()
        .Where(NamesFrozenMirror)
        .ToList();

    /// <summary>
    /// Separators are normalized before matching: MSBuild resolves '/' and '\' to the same
    /// path on Windows but preserves the spelling, so matching only one of them would let the
    /// other reference slip past this guard.
    /// </summary>
    private static bool NamesFrozenMirror(string include) =>
        include.Replace('\\', '/').Contains("TarkovHelper/Assets", StringComparison.OrdinalIgnoreCase);

    private static XDocument CsprojWithInclude(string include) => XDocument.Parse(
        $"""
         <Project Sdk="Microsoft.NET.Sdk">
           <ItemGroup>
             <None Include="{include}" CopyToOutputDirectory="PreserveNewest" />
           </ItemGroup>
         </Project>
         """);
}
