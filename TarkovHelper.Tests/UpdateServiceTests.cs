using System.Net;
using System.Net.Http;
using System.Reflection;
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
        // Pin the full URLs, not a `Contains("/josephjang/.../")` substring: a substring
        // check would also pass for a wrong host like https://evil.example/josephjang/...,
        // which is exactly the drift this guard exists to catch.
        //
        // The manifest URL is pinned at v1 on purpose. It is derived from
        // <TarkovDataFormatVersion>, so bumping the data format fails this test until
        // someone updates it deliberately, which is the review moment a bump deserves.
        // DataChannelTests covers the format-agnostic half (URLs track the running
        // data format), so this pair keeps guarding the host and nothing else drifts.
        Assert.Equal(
            "https://raw.githubusercontent.com/josephjang/TarkovHelper/main/update.xml",
            UpdateService.UpdateXmlUrl);
        Assert.Equal(
            "https://raw.githubusercontent.com/josephjang/TarkovHelper/refs/heads/main/data/index.json",
            DataChannel.INDEX_URL);
        Assert.Equal(
            "https://raw.githubusercontent.com/josephjang/TarkovHelper/refs/heads/main/data/v1/manifest.json",
            DataChannel.MANIFEST_URL);
    }

    [Fact]
    public void The_production_instance_fetches_the_pinned_urls()
    {
        // The constants above are statics; every production fetch goes through the URLs the
        // constructor derives from the channel root the parameterless constructor hands it.
        // Nothing else observes that argument, so swapping it for another literal would
        // repoint every install while the pins above stayed green. Read the derived URLs
        // back off a production-shaped instance to close that gap.
        //
        // The instance is the private (production) constructor, not the singleton: it must
        // not install itself as DatabaseUpdateService.Instance for the rest of the run. It
        // starts no timer and issues no request, so constructing it is inert.
        using var production = (DatabaseUpdateService)Activator.CreateInstance(
            typeof(DatabaseUpdateService), nonPublic: true)!;

        Assert.Equal(DataChannel.INDEX_URL, DerivedUrl(production, "_indexUrl"));
        Assert.Equal(DataChannel.CHANNEL_BASE_URL, DerivedUrl(production, "_channelBaseUrl"));
    }

    /// <summary>
    /// Reads a URL the service derived for itself. Private on purpose: production exposes no
    /// accessor, and adding one only for a test would widen the surface this guard exists to
    /// keep narrow.
    /// </summary>
    private static string DerivedUrl(DatabaseUpdateService service, string fieldName)
    {
        var field = typeof(DatabaseUpdateService)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.True(field != null,
            $"DatabaseUpdateService no longer holds {fieldName}. Repoint this guard at whatever now "
            + "carries the URL a production instance fetches, so the channel root it is built with "
            + "stays pinned to this fork.");

        return (string)field!.GetValue(service)!;
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

    #region Event shape of one check

    // The pill the title bar shows for an available update is built by a subscriber that
    // formats localized strings and resolves brushes, so it can throw. It must not be able
    // to rewrite the outcome of the check that notified it: the success raise used to sit
    // inside the try that classifies failures, so a throwing subscriber was logged as a
    // failed check, recorded over a LastCheckError that had just been cleared, and got a
    // SECOND completion carrying (null, exception). GetStatusKind puts Failed ahead of
    // UpdateAvailable, so the update that really had been found disappeared from the UI.
    [Fact]
    public async Task A_throwing_completion_subscriber_cannot_hide_the_update_it_was_told_about()
    {
        var service = ServiceWithFeed(new StubFeed(FeedXml("2026.9.0")), new Version(2026, 7, 0));
        var completions = new List<UpdateCheckEventArgs>();
        service.UpdateCheckCompleted += (_, e) =>
        {
            completions.Add(e);
            throw new InvalidOperationException("a UI subscriber threw");
        };

        var found = await service.CheckForUpdateAsync();

        Assert.NotNull(found);
        Assert.Equal(new Version(2026, 9, 0), found.Version);
        Assert.Single(completions);
        Assert.Null(completions[0].Error);
        Assert.Same(found, completions[0].UpdateInfo);
        Assert.Null(service.LastCheckError);
        Assert.False(service.LastCheckFailed);
        Assert.False(service.IsChecking);
        Assert.Equal(UpdateStatusKind.UpdateAvailable, UpdateService.GetStatusKind(
            service.IsChecking, service.LastCheckFailed,
            service.AvailableUpdate != null, service.LastCheckTime.HasValue));
    }

    // The started raise used to sit outside the try, so a subscriber throwing there took the
    // check down with it AND left the in-progress flag set, which makes every later check
    // return "already in progress" for the rest of the process. Both callers fire and forget
    // the task (the hourly timer and the Settings button) and App.xaml.cs hooks no
    // TaskScheduler.UnobservedTaskException, so the throw would vanish without a log line.
    [Fact]
    public async Task A_throwing_started_subscriber_neither_aborts_the_check_nor_wedges_the_flag()
    {
        var service = ServiceWithFeed(new StubFeed(FeedXml("2026.9.0")), new Version(2026, 7, 0));
        var started = 0;
        service.UpdateCheckStarted += (_, _) =>
        {
            started++;
            throw new InvalidOperationException("a UI subscriber threw");
        };

        var found = await service.CheckForUpdateAsync();

        Assert.NotNull(found);
        Assert.Equal(1, started);
        Assert.False(service.IsChecking);
        Assert.Null(service.LastCheckError);

        // The flag really was released, not merely reported clear: a second check runs
        // instead of short-circuiting on "already in progress".
        Assert.NotNull(await service.CheckForUpdateAsync());
        Assert.Equal(2, started);
    }

    // The failure path needs the same containment: a throwing subscriber must not replace
    // the network error the check actually hit, must not add a second completion, and must
    // not escape to the caller, since one of them is an async void timer callback.
    [Fact]
    public async Task A_throwing_completion_subscriber_cannot_replace_the_error_that_failed_the_check()
    {
        var service = ServiceWithFeed(new StubFeed(body: null), new Version(2026, 7, 0));
        var completions = new List<UpdateCheckEventArgs>();
        service.UpdateCheckCompleted += (_, e) =>
        {
            completions.Add(e);
            throw new InvalidOperationException("a UI subscriber threw");
        };

        var found = await service.CheckForUpdateAsync();

        Assert.Null(found);
        Assert.Single(completions);
        Assert.IsType<HttpRequestException>(completions[0].Error);
        Assert.IsType<HttpRequestException>(service.LastCheckError);
        Assert.True(service.LastCheckFailed);
        Assert.False(service.IsChecking);
    }

    /// <summary>
    /// An update feed body offering <paramref name="version"/>.
    /// </summary>
    private static string FeedXml(string version)
        => $"<item><version>{version}</version><url>https://example.test/x.zip</url></item>";

    /// <summary>
    /// A service that answers from <paramref name="feed"/> instead of the network.
    /// Uninitialized on purpose, like the auto-check guard above: it installs no singleton
    /// and starts no timer, and only the two fields a check reads are filled in.
    /// </summary>
    private static UpdateService ServiceWithFeed(HttpMessageHandler feed, Version currentVersion)
    {
        var service = (UpdateService)RuntimeHelpers.GetUninitializedObject(typeof(UpdateService));
        SetPrivateField(service, "_httpClient", new HttpClient(feed));
        SetPrivateField(service, "_currentVersion", currentVersion);
        return service;
    }

    private static void SetPrivateField(UpdateService service, string fieldName, object value)
    {
        var field = typeof(UpdateService)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.True(field != null,
            $"UpdateService no longer holds {fieldName}. Repoint this fixture at whatever now "
            + "carries it, so these event-shape guards keep running offline.");

        field!.SetValue(service, value);
    }

    /// <summary>
    /// The update feed, offline: every request gets the same body, or a transport failure
    /// when that body is null.
    /// </summary>
    private sealed class StubFeed : HttpMessageHandler
    {
        private readonly string? _body;

        internal StubFeed(string? body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_body == null)
            {
                throw new HttpRequestException("the update feed is unreachable");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body)
            });
        }
    }

    #endregion
}
