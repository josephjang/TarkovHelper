using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TarkovDBEditor.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Reads a trimmed capture of json.tarkov.dev the way the client will read the live endpoint.
/// <para>
/// The fixtures keep the shapes that matter and drop the bulk: collections arrive as objects
/// keyed by id, every translatable string is a key resolved through a sibling locale file, and
/// tasks and hideout levels share one <c>traderRequirements</c> shape that mixes loyalty and
/// reputation gates.
/// </para>
/// <para>
/// The refusals matter as much as the parsing. A 200 carrying an error body used to parse to an
/// empty set and overwrite the cache with <c>{}</c>, which is how the published database went
/// seven months with no external ID on any row.
/// </para>
/// </summary>
public sealed class TarkovDevJsonClientTests
{
    #region Tasks

    [Fact]
    public async Task Reads_the_game_rules_a_task_carries()
    {
        using var server = FakeServer.WithTasks();
        using var client = server.CreateClient();

        var fetched = await client.FetchTasksAsync(conditional: false);

        Assert.NotNull(fetched);
        var collector = fetched!.Value.Single(t => t.Id == "5c51aac186f77432ea65c552");
        Assert.Equal("Collector", collector.NameEN);
        Assert.Equal("collector", collector.NormalizedName);
        Assert.Equal(42, collector.MinPlayerLevel);
        Assert.True(collector.KappaRequired);
        Assert.Equal("Any", collector.FactionName);
        Assert.Equal("54cb50c76803fa8b248b4571", collector.Trader);
        Assert.Equal("https://escapefromtarkov.fandom.com/wiki/Collector", collector.WikiLink);
    }

    [Fact]
    public async Task Keeps_loyalty_gates_and_drops_reputation_gates()
    {
        // The app's schema can express "loyalty level N with trader T" and nothing else; the
        // endpoint also carries reputation gates (12 tasks, Collector among them).
        using var server = FakeServer.WithTasks();
        using var client = server.CreateClient();

        var fetched = await client.FetchTasksAsync(conditional: false);
        var collector = fetched!.Value.Single(t => t.Id == "5c51aac186f77432ea65c552");

        var gate = Assert.Single(collector.TraderLevelRequirements);
        Assert.Equal("54cb50c76803fa8b248b4571", gate.TraderId);
        Assert.Equal(4, gate.Level);
    }

    [Fact]
    public async Task Reads_prerequisites_with_their_statuses()
    {
        using var server = FakeServer.WithTasks();
        using var client = server.CreateClient();

        var fetched = await client.FetchTasksAsync(conditional: false);
        var stirrup = fetched!.Value.Single(t => t.Id == "5c0be13186f7746309d759c8");

        var prerequisite = Assert.Single(stirrup.TaskRequirements);
        Assert.Equal("5c51aac186f77432ea65c552", prerequisite.TaskId);
        Assert.Equal(new[] { "complete" }, prerequisite.Status);
    }

    [Fact]
    public async Task Resolves_a_korean_name_and_leaves_an_untranslated_one_null()
    {
        // A quest with no Korean entry falls back to English at display time rather than
        // storing the English string as if it were a translation.
        using var server = FakeServer.WithTasks();
        using var client = server.CreateClient();

        var fetched = await client.FetchTasksAsync(conditional: false);

        Assert.Equal("수집가", fetched!.Value.Single(t => t.Id == "5c51aac186f77432ea65c552").NameKO);
        Assert.Null(fetched.Value.Single(t => t.Id == "5c0be13186f7746309d759c8").NameKO);
    }

    [Fact]
    public async Task A_translation_identical_to_the_english_name_counts_as_missing()
    {
        using var server = FakeServer.WithTasks();
        using var client = server.CreateClient();

        var fetched = await client.FetchTasksAsync(conditional: false);

        // Japanese is untranslated upstream: every key resolves to the English string.
        Assert.All(fetched!.Value, task => Assert.Null(task.NameJA));
    }

    [Fact]
    public async Task An_empty_task_set_fails_instead_of_emptying_the_cache()
    {
        using var server = FakeServer.WithTasks();
        server.SetBody(TarkovDevJsonClient.TasksPath, """{"data":{"tasks":{}}}""");
        using var client = server.CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchTasksAsync(conditional: false));
        Assert.Contains("no tasks", ex.Message);
    }

    [Fact]
    public async Task An_error_body_served_with_200_fails_instead_of_emptying_the_cache()
    {
        using var server = FakeServer.WithTasks();
        server.SetBody(TarkovDevJsonClient.TasksPath, """{"errors":["GraphQL server unavailable. Try again later."]}""");
        using var client = server.CreateClient();

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchTasksAsync(conditional: false));
    }

    [Fact]
    public async Task A_task_without_a_wiki_link_fails_the_fetch()
    {
        // Page identity comes from the wiki, so a task with no link cannot be matched to
        // anything and its absence has to be visible rather than silent.
        using var server = FakeServer.WithTasks();
        server.SetBody(TarkovDevJsonClient.TasksPath, """
            {"data":{"tasks":{"5c51aac186f77432ea65c552":{
              "id":"5c51aac186f77432ea65c552",
              "name":"5c51aac186f77432ea65c552 name",
              "normalizedName":"collector"
            }}}}
            """);
        using var client = server.CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchTasksAsync(conditional: false));
        Assert.Contains("wikiLink", ex.Message);
    }

    [Fact]
    public async Task An_http_failure_fails_the_fetch()
    {
        using var server = FakeServer.WithTasks();
        server.SetStatus(TarkovDevJsonClient.TasksPath, HttpStatusCode.ServiceUnavailable);
        using var client = server.CreateClient();

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FetchTasksAsync(conditional: false));
    }

    [Fact]
    public async Task A_response_that_is_not_json_fails_with_the_endpoint_named()
    {
        // The wiki and tarkov.dev both answer some user agents with a Cloudflare challenge page.
        using var server = FakeServer.WithTasks();
        server.SetBody(TarkovDevJsonClient.TasksPath, "<html><title>Just a moment...</title></html>");
        using var client = server.CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchTasksAsync(conditional: false));
        Assert.Contains(TarkovDevJsonClient.TasksPath, ex.Message);
    }

    [Fact]
    public async Task Reports_the_upstream_last_modified_time()
    {
        using var server = FakeServer.WithTasks();
        using var client = server.CreateClient();

        var fetched = await client.FetchTasksAsync(conditional: false);

        Assert.Equal(FakeServer.LastModified.UtcDateTime, fetched!.SourceLastModified);
    }

    #endregion

    #region Items, traders and hideout

    [Fact]
    public async Task Reads_items_keyed_by_their_wiki_page()
    {
        using var server = FakeServer.WithItems();
        using var client = server.CreateClient();

        var fetched = await client.FetchItemsAsync(conditional: false);

        var item = fetched!.Value["https://escapefromtarkov.fandom.com/wiki/Roubles"];
        Assert.Equal("5449016a4bdc2d6f028b456f", item.BsgId);
        Assert.Equal("Roubles", item.NameEN);
        Assert.Equal("RUB", item.ShortNameEN);
        Assert.Equal("루블", item.NameKO);
        Assert.Equal("roubles", item.NormalizedName);
        Assert.Equal("https://assets.tarkov.dev/5449016a4bdc2d6f028b456f-icon.webp", item.IconLink);
    }

    [Fact]
    public async Task Skips_items_with_no_wiki_page()
    {
        // 167 of the 5,312 items have no wikiLink, so the wiki-page-keyed pipeline can never
        // reach them.
        using var server = FakeServer.WithItems();
        using var client = server.CreateClient();

        var fetched = await client.FetchItemsAsync(conditional: false);

        Assert.DoesNotContain(fetched!.Value.Values, i => i.BsgId == "000000000000000000000000");
    }

    [Fact]
    public async Task Reads_traders_including_the_one_1_1_added()
    {
        using var server = FakeServer.WithTraders();
        using var client = server.CreateClient();

        var fetched = await client.FetchTradersAsync(conditional: false);

        Assert.Equal("Prapor", fetched!.Value.Single(t => t.Id == "54cb50c76803fa8b248b4571").Name);
        Assert.Equal("Survivor", fetched.Value.Single(t => t.Id == "69e0d6cc77b63940375b9173").Name);
    }

    [Fact]
    public async Task Names_the_items_and_traders_a_hideout_level_requires()
    {
        // The endpoint carries only ids for both, so the caller supplies the lookups; fetching
        // them again here would mean reading the 16 MB items file twice.
        using var server = FakeServer.WithHideout();
        using var client = server.CreateClient();

        var items = new[]
        {
            new TarkovDevMultiLangItem
            {
                BsgId = "5449016a4bdc2d6f028b456f",
                NameEN = "Roubles",
                NameKO = "루블",
                NormalizedName = "roubles",
                IconLink = "https://assets.tarkov.dev/5449016a4bdc2d6f028b456f-icon.webp",
            },
        };
        var traders = new[] { new TarkovDevTraderCacheItem { Id = "5ac3b934156ae10c4430e83c", Name = "Ragman" } };

        var fetched = await client.FetchHideoutAsync(items, traders, conditional: false);

        var station = Assert.Single(fetched!.Value);
        Assert.Equal("Library", station.Name);
        var level = Assert.Single(station.Levels);

        var itemRequirement = Assert.Single(level.ItemRequirements);
        Assert.Equal("Roubles", itemRequirement.ItemName);
        Assert.Equal("루블", itemRequirement.ItemNameKo);
        Assert.Equal(400000, itemRequirement.Count);
        Assert.False(itemRequirement.FoundInRaid);

        var traderRequirement = Assert.Single(level.TraderRequirements);
        Assert.Equal("Ragman", traderRequirement.TraderName);
        Assert.Equal(2, traderRequirement.Level);

        var skill = Assert.Single(level.SkillRequirements);
        Assert.Equal("Hideout Management", skill.Name);
        Assert.Equal(5, skill.Level);
    }

    [Fact]
    public async Task A_hideout_item_the_caller_does_not_know_still_keeps_its_identifier()
    {
        using var server = FakeServer.WithHideout();
        using var client = server.CreateClient();

        var fetched = await client.FetchHideoutAsync(
            Array.Empty<TarkovDevMultiLangItem>(), Array.Empty<TarkovDevTraderCacheItem>(), conditional: false);

        var requirement = Assert.Single(Assert.Single(fetched!.Value).Levels[0].ItemRequirements);
        Assert.Equal("5449016a4bdc2d6f028b456f", requirement.ItemId);
        Assert.Equal("", requirement.ItemName);
    }

    #endregion

    #region Conditional requests

    [Fact]
    public async Task A_repeat_fetch_sends_the_stored_etag_and_returns_nothing_when_upstream_agrees()
    {
        using var server = FakeServer.WithTasks();
        var cacheDir = NewCacheDir();
        using (var client = server.CreateClient(cacheDir))
        {
            Assert.NotNull(await client.FetchTasksAsync(conditional: true));
        }

        server.NotModifiedFor(TarkovDevJsonClient.TasksPath);
        server.NotModifiedFor(TarkovDevJsonClient.TasksPath + "_en");
        server.NotModifiedFor(TarkovDevJsonClient.TasksPath + "_ko");
        server.NotModifiedFor(TarkovDevJsonClient.TasksPath + "_ja");

        using var second = server.CreateClient(cacheDir);
        Assert.Null(await second.FetchTasksAsync(conditional: true));
        Assert.Equal("\"tasks-v1\"", server.LastIfNoneMatch[TarkovDevJsonClient.TasksPath]);
    }

    [Fact]
    public async Task A_locale_file_that_moved_forces_the_unchanged_files_to_be_re_read()
    {
        // A 304 carries no body, so the group has to be read as a set: a Korean-only correction
        // upstream must still reach the cache.
        using var server = FakeServer.WithTasks();
        var cacheDir = NewCacheDir();
        using (var client = server.CreateClient(cacheDir))
        {
            await client.FetchTasksAsync(conditional: true);
        }

        server.NotModifiedFor(TarkovDevJsonClient.TasksPath);
        server.NotModifiedFor(TarkovDevJsonClient.TasksPath + "_en");
        server.NotModifiedFor(TarkovDevJsonClient.TasksPath + "_ja");
        server.SetBody(TarkovDevJsonClient.TasksPath + "_ko", """
            {"data":{"5c51aac186f77432ea65c552 name":"새 수집가"}}
            """);

        using var second = server.CreateClient(cacheDir);
        var fetched = await second.FetchTasksAsync(conditional: true);

        Assert.NotNull(fetched);
        Assert.Equal("새 수집가", fetched!.Value.Single(t => t.Id == "5c51aac186f77432ea65c552").NameKO);
    }

    [Fact]
    public async Task A_failed_parse_does_not_advance_the_stored_etag()
    {
        // Otherwise the next run answers 304 and keeps the very cache the failure meant to
        // replace.
        using var server = FakeServer.WithTasks();
        var cacheDir = NewCacheDir();

        server.SetBody(TarkovDevJsonClient.TasksPath, """{"data":{"tasks":{}}}""");
        using (var client = server.CreateClient(cacheDir))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchTasksAsync(conditional: true));
        }

        server.Reset();
        using var second = server.CreateClient(cacheDir);
        Assert.NotNull(await second.FetchTasksAsync(conditional: true));
        Assert.False(server.LastIfNoneMatch.ContainsKey(TarkovDevJsonClient.TasksPath));
    }

    #endregion

    #region Fixtures

    private static string NewCacheDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "tarkovdev-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Answers the endpoints from a trimmed capture, records what was asked, and can be told to
    /// answer 304 or fail so the client's refusals can be exercised.
    /// </summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public static readonly DateTimeOffset LastModified = new(2026, 8, 21, 4, 23, 24, TimeSpan.Zero);

        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _originalBodies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HttpStatusCode> _statuses = new(StringComparer.Ordinal);
        private readonly HashSet<string> _notModified = new(StringComparer.Ordinal);

        public Dictionary<string, string> LastIfNoneMatch { get; } = new(StringComparer.Ordinal);

        public void SetBody(string path, string body) => _bodies[path] = body;

        public void SetStatus(string path, HttpStatusCode status) => _statuses[path] = status;

        public void NotModifiedFor(string path) => _notModified.Add(path);

        public void Reset()
        {
            _statuses.Clear();
            _notModified.Clear();
            LastIfNoneMatch.Clear();
            foreach (var (path, body) in _originalBodies)
                _bodies[path] = body;
        }

        public TarkovDevJsonClient CreateClient(string? cacheDir = null) =>
            new(cacheDir, "https://json.tarkov.dev/", handler: this);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');

            if (request.Headers.IfNoneMatch.Count > 0)
                LastIfNoneMatch[path] = request.Headers.IfNoneMatch.First().ToString();

            if (_statuses.TryGetValue(path, out var status))
                return Task.FromResult(new HttpResponseMessage(status));

            if (_notModified.Contains(path) && request.Headers.IfNoneMatch.Count > 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));

            if (!_bodies.TryGetValue(path, out var body))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("ETag", $"\"{EtagFor(path)}\"");
            response.Content.Headers.LastModified = LastModified;
            return Task.FromResult(response);
        }

        private static string EtagFor(string path) => path.Replace('/', '-').Replace(TarkovDevJsonClient.GameMode + "-", "") + "-v1";

        private void Seed(string path, string body)
        {
            _bodies[path] = body;
            _originalBodies[path] = body;
        }

        public static FakeServer WithTasks()
        {
            var server = new FakeServer();
            server.Seed(TarkovDevJsonClient.TasksPath, """
                {"data":{"tasks":{
                  "5c51aac186f77432ea65c552":{
                    "id":"5c51aac186f77432ea65c552",
                    "name":"5c51aac186f77432ea65c552 name",
                    "normalizedName":"collector",
                    "wikiLink":"https://escapefromtarkov.fandom.com/wiki/Collector",
                    "trader":"54cb50c76803fa8b248b4571",
                    "minPlayerLevel":42,
                    "kappaRequired":true,
                    "lightkeeperRequired":false,
                    "factionName":"Any",
                    "availableDelaySecondsMin":0,
                    "taskRequirements":[],
                    "traderRequirements":[
                      {"id":"a1","requirementType":"level","compareMethod":">=","value":4,"trader":"54cb50c76803fa8b248b4571"},
                      {"id":"a2","requirementType":"reputation","compareMethod":">=","value":3,"trader":"579dc571d53a0658a154fbec"}
                    ]
                  },
                  "5c0be13186f7746309d759c8":{
                    "id":"5c0be13186f7746309d759c8",
                    "name":"5c0be13186f7746309d759c8 name",
                    "normalizedName":"stirrup",
                    "wikiLink":"https://escapefromtarkov.fandom.com/wiki/Stirrup",
                    "trader":"5a7c2eca46aef81a7ca2145d",
                    "minPlayerLevel":0,
                    "kappaRequired":false,
                    "factionName":"Any",
                    "availableDelaySecondsMin":3600,
                    "taskRequirements":[{"task":"5c51aac186f77432ea65c552","status":["complete"]}],
                    "traderRequirements":[]
                  }
                }}}
                """);
            server.Seed(TarkovDevJsonClient.TasksPath + "_en", """
                {"data":{
                  "5c51aac186f77432ea65c552 name":"Collector",
                  "5c0be13186f7746309d759c8 name":"Stirrup"
                }}
                """);
            server.Seed(TarkovDevJsonClient.TasksPath + "_ko", """
                {"data":{"5c51aac186f77432ea65c552 name":"수집가"}}
                """);
            // Japanese is untranslated upstream: the file exists and repeats the English text.
            server.Seed(TarkovDevJsonClient.TasksPath + "_ja", """
                {"data":{
                  "5c51aac186f77432ea65c552 name":"Collector",
                  "5c0be13186f7746309d759c8 name":"Stirrup"
                }}
                """);
            return server;
        }

        public static FakeServer WithItems()
        {
            var server = new FakeServer();
            server.Seed(TarkovDevJsonClient.ItemsPath, """
                {"data":{"items":{
                  "5449016a4bdc2d6f028b456f":{
                    "id":"5449016a4bdc2d6f028b456f",
                    "name":"5449016a4bdc2d6f028b456f Name",
                    "shortName":"5449016a4bdc2d6f028b456f ShortName",
                    "normalizedName":"roubles",
                    "wikiLink":"https://escapefromtarkov.fandom.com/wiki/Roubles",
                    "iconLink":"https://assets.tarkov.dev/5449016a4bdc2d6f028b456f-icon.webp"
                  },
                  "000000000000000000000000":{
                    "id":"000000000000000000000000",
                    "name":"000000000000000000000000 Name",
                    "shortName":"000000000000000000000000 ShortName",
                    "normalizedName":"no-page-item"
                  }
                }}}
                """);
            server.Seed(TarkovDevJsonClient.ItemsPath + "_en", """
                {"data":{
                  "5449016a4bdc2d6f028b456f Name":"Roubles",
                  "5449016a4bdc2d6f028b456f ShortName":"RUB",
                  "000000000000000000000000 Name":"No Page Item",
                  "000000000000000000000000 ShortName":"NPI"
                }}
                """);
            server.Seed(TarkovDevJsonClient.ItemsPath + "_ko", """
                {"data":{"5449016a4bdc2d6f028b456f Name":"루블","5449016a4bdc2d6f028b456f ShortName":"RUB"}}
                """);
            server.Seed(TarkovDevJsonClient.ItemsPath + "_ja", """{"data":{}}""");
            return server;
        }

        public static FakeServer WithTraders()
        {
            var server = new FakeServer();
            server.Seed(TarkovDevJsonClient.TradersPath, """
                {"data":{
                  "54cb50c76803fa8b248b4571":{"id":"54cb50c76803fa8b248b4571","name":"54cb50c76803fa8b248b4571 Nickname","normalizedName":"prapor"},
                  "69e0d6cc77b63940375b9173":{"id":"69e0d6cc77b63940375b9173","name":"69e0d6cc77b63940375b9173 Nickname","normalizedName":"survivor"}
                }}
                """);
            server.Seed(TarkovDevJsonClient.TradersPath + "_en", """
                {"data":{
                  "54cb50c76803fa8b248b4571 Nickname":"Prapor",
                  "69e0d6cc77b63940375b9173 Nickname":"Survivor"
                }}
                """);
            server.Seed(TarkovDevJsonClient.TradersPath + "_ko", """{"data":{"54cb50c76803fa8b248b4571 Nickname":"프라포"}}""");
            server.Seed(TarkovDevJsonClient.TradersPath + "_ja", """{"data":{}}""");
            return server;
        }

        public static FakeServer WithHideout()
        {
            var server = new FakeServer();
            server.Seed(TarkovDevJsonClient.HideoutPath, """
                {"data":{"5d494a0e5b56502f18c98a02":{
                  "id":"5d494a0e5b56502f18c98a02",
                  "name":"hideout_area_13_name",
                  "normalizedName":"library",
                  "imageLink":"https://assets.tarkov.dev/library.webp",
                  "levels":[{
                    "id":"5d494a0e5b56502f18c98a02-1",
                    "level":1,
                    "constructionTime":194400,
                    "traderRequirements":[
                      {"id":"t1","requirementType":"level","compareMethod":">=","value":2,"trader":"5ac3b934156ae10c4430e83c"},
                      {"id":"t2","requirementType":"reputation","compareMethod":">=","value":1,"trader":"579dc571d53a0658a154fbec"}
                    ],
                    "stationLevelRequirements":[],
                    "itemRequirements":[
                      {"id":"i1","item":"5449016a4bdc2d6f028b456f","count":400000,"attributes":{"foundInRaid":false}}
                    ],
                    "skillRequirements":[{"id":"s1","level":5,"skill":"HideoutManagement"}]
                  }]
                }}}
                """);
            server.Seed(TarkovDevJsonClient.HideoutPath + "_en", """
                {"data":{"hideout_area_13_name":"Library","HideoutManagement":"Hideout Management"}}
                """);
            server.Seed(TarkovDevJsonClient.HideoutPath + "_ko", """{"data":{"hideout_area_13_name":"도서관"}}""");
            server.Seed(TarkovDevJsonClient.HideoutPath + "_ja", """{"data":{}}""");
            return server;
        }
    }

    #endregion
}
