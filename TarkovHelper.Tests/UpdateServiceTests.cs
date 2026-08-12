using System.Runtime.CompilerServices;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Covers the update.xml parsing contract (UpdateService.ParseUpdateXml) and pins the
/// update-feed URL constants to this fork. The URL guards exist because a bad merge from
/// upstream (Zeliper) could silently reintroduce its URLs, and an app built with those
/// would offer to replace itself with the upstream build.
/// </summary>
public sealed class UpdateServiceTests
{
    [Fact]
    public void Valid_item_xml_parses_into_update_info()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <item>
                <version>2026.7.0</version>
                <url>https://github.com/josephjang/TarkovHelper/releases/download/v2026.7.0/TarkovHelper.zip</url>
                <changelog>https://github.com/josephjang/TarkovHelper/releases/latest</changelog>
                <mandatory>false</mandatory>
            </item>
            """;

        var info = UpdateService.ParseUpdateXml(xml);

        Assert.NotNull(info);
        Assert.Equal(new Version(2026, 7, 0), info.Version);
        Assert.Equal(
            "https://github.com/josephjang/TarkovHelper/releases/download/v2026.7.0/TarkovHelper.zip",
            info.DownloadUrl);
        Assert.Equal("https://github.com/josephjang/TarkovHelper/releases/latest", info.ChangelogUrl);
    }

    [Fact]
    public void Missing_url_element_returns_null()
    {
        var info = UpdateService.ParseUpdateXml("<item><version>2026.7.0</version></item>");

        Assert.Null(info);
    }

    [Fact]
    public void Missing_version_element_returns_null()
    {
        var info = UpdateService.ParseUpdateXml("<item><url>https://example.test/x.zip</url></item>");

        Assert.Null(info);
    }

    [Fact]
    public void Unparseable_version_returns_null()
    {
        var info = UpdateService.ParseUpdateXml(
            "<item><version>not-a-version</version><url>https://example.test/x.zip</url></item>");

        Assert.Null(info);
    }

    [Fact]
    public void Wrong_root_element_returns_null()
    {
        // AutoUpdater feeds are often wrapped in <appcast>; this parser requires a bare <item>.
        var info = UpdateService.ParseUpdateXml(
            "<appcast><item><version>2026.7.0</version><url>https://example.test/x.zip</url></item></appcast>");

        Assert.Null(info);
    }

    [Fact]
    public void Malformed_xml_returns_null()
    {
        var info = UpdateService.ParseUpdateXml("<item><version>2026.7.0");

        Assert.Null(info);
    }

    [Fact]
    public void Update_feed_constants_point_at_fork()
    {
        // Pin the full URLs, not a `Contains("/josephjang/…/")` substring: a substring
        // check would also pass for a wrong host like https://evil.example/josephjang/…,
        // which is exactly the drift this guard exists to catch.
        Assert.Equal(
            "https://raw.githubusercontent.com/josephjang/TarkovHelper/main/update.xml",
            UpdateService.UpdateXmlUrl);
        Assert.Equal(
            "https://raw.githubusercontent.com/josephjang/TarkovHelper/refs/heads/main/TarkovHelper/Assets/db_version.txt",
            DatabaseUpdateService.VERSION_URL);
        Assert.Equal(
            "https://raw.githubusercontent.com/josephjang/TarkovHelper/refs/heads/main/TarkovHelper/Assets/tarkov_data.db",
            DatabaseUpdateService.DATABASE_URL);
    }

    [Fact]
    public void Auto_check_disabled_touches_neither_the_timer_nor_the_network()
    {
        // Uninitialized instance: _checkTimer and _httpClient are null, so reaching either
        // effect throws. Surviving the disabled call is the proof the gate short-circuits
        // before both; the throwing counterpart proves the assertion isn't vacuous.
        var disabled = (UpdateService)RuntimeHelpers.GetUninitializedObject(typeof(UpdateService));
        disabled.StartAutoCheck(disabled: true);
        Assert.Null(disabled.LastCheckTime);

        var enabled = (UpdateService)RuntimeHelpers.GetUninitializedObject(typeof(UpdateService));
        Assert.Throws<NullReferenceException>(() => enabled.StartAutoCheck(disabled: false));
    }

    [Fact]
    public void First_calver_release_outranks_the_inherited_semver_line()
    {
        // The migration linchpin: the first fork release (CalVer 2026.7.0) must compare
        // GREATER than the inherited 4.x line under System.Version, so a 4.3.1 fork
        // install is actually offered the first CalVer release (UpdateService compares
        // updateInfo.Version > _currentVersion). If this flips, old installs silently
        // stop updating and the dead v4.3.1 URL in update.xml would never be superseded.
        var release = UpdateService.ParseUpdateXml(
            "<item><version>2026.7.0</version><url>https://example.test/x.zip</url></item>");

        Assert.NotNull(release);
        Assert.True(release.Version > new Version(4, 3, 1));
    }

    [Fact]
    public void Feed_version_equal_to_the_running_build_is_not_offered_as_an_update()
    {
        // update.xml carries a 3-part CalVer version; the running app's version is the
        // 4-part AssemblyVersion (revision 0). The "don't offer myself to myself" outcome
        // relies on System.Version ordering 3-part BELOW the same 4-part-with-zero
        // (Version(2026,7,0) < Version(2026,7,0,0)). Pin it so a future switch to 4-part
        // feed versions can't silently turn an up-to-date client into a self-update loop.
        var feed = UpdateService.ParseUpdateXml(
            "<item><version>2026.7.0</version><url>https://example.test/x.zip</url></item>");
        var runningBuild = new Version(2026, 7, 0, 0);

        Assert.NotNull(feed);
        Assert.False(feed.Version > runningBuild);
    }
}
