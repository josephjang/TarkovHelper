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
    public async Task Reads_the_fail_conditions_a_task_carries()
    {
        // What the refresh derives an exclusive pair from: a taskStatus condition naming the
        // quest whose completion fails this one. Stirrup carries the shape of both 1.1 pairs.
        using var server = FakeServer.WithTasks();
        using var client = server.CreateClient();

        var fetched = await client.FetchTasksAsync(conditional: false);
        var stirrup = fetched!.Value.Single(t => t.Id == "5c0be13186f7746309d759c8");

        var condition = Assert.Single(stirrup.FailConditions, c => c.Type == "taskStatus");
        Assert.Equal("5c51aac186f77432ea65c552", condition.TaskId);
        Assert.Equal(new[] { "complete" }, condition.Status);
    }

    [Fact]
    public async Task Carries_a_fail_condition_that_names_no_task_by_its_kind()
    {
        // A prerequisite the refresh cannot expand is reported with what does fail it, so the
        // kinds that name no task are carried rather than dropped: "failed by a Lightkeeper
        // standing" is the reading that tells a reviewer the omission is correct.
        using var server = FakeServer.WithTasks();
        using var client = server.CreateClient();

        var fetched = await client.FetchTasksAsync(conditional: false);
        var stirrup = fetched!.Value.Single(t => t.Id == "5c0be13186f7746309d759c8");

        var standing = Assert.Single(stirrup.FailConditions, c => c.Type == "traderStanding");
        Assert.Null(standing.TaskId);
        Assert.Empty(standing.Status);
    }

    [Fact]
    public async Task A_task_with_no_fail_conditions_reads_as_an_empty_list_not_null()
    {
        // The derivation enumerates this list without a null check, and most tasks have an
        // empty failConditions array.
        using var server = FakeServer.WithTasks();
        using var client = server.CreateClient();

        var fetched = await client.FetchTasksAsync(conditional: false);
        var collector = fetched!.Value.Single(t => t.Id == "5c51aac186f77432ea65c552");

        Assert.NotNull(collector.FailConditions);
        Assert.Empty(collector.FailConditions);
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
    public async Task A_task_without_a_wiki_link_is_carried_rather_than_failing_the_fetch()
    {
        // It cannot be matched to a page by link, but QuestIdentityResolver still matches it by
        // normalized name and reports it as a game record with no page when nothing claims it.
        // Failing the part would let one odd upstream record block every regeneration once the
        // task cache aged past the refresh guard.
        using var server = FakeServer.WithTasks();
        server.SetBody(TarkovDevJsonClient.TasksPath, """
            {"data":{"tasks":{
              "5c51aac186f77432ea65c552":{
                "id":"5c51aac186f77432ea65c552",
                "name":"5c51aac186f77432ea65c552 name",
                "normalizedName":"collector"
              },
              "5c0be13186f7746309d759c8":{
                "id":"5c0be13186f7746309d759c8",
                "name":"5c0be13186f7746309d759c8 name",
                "normalizedName":"stirrup",
                "wikiLink":"https://escapefromtarkov.fandom.com/wiki/Stirrup"
              }
            }}}
            """);
        using var client = server.CreateClient();

        var fetched = await client.FetchTasksAsync(conditional: false);

        Assert.Equal(2, fetched!.Value.Count);
        var collector = fetched.Value.Single(t => t.Id == "5c51aac186f77432ea65c552");
        Assert.Null(collector.WikiLink);
        Assert.Equal("Collector", collector.NameEN);
        Assert.Equal("collector", collector.NormalizedName);
    }

    [Fact]
    public async Task A_task_without_an_id_fails_the_fetch()
    {
        // The id is the identity everything downstream keys on; a record without one cannot be
        // carried, only refused.
        using var server = FakeServer.WithTasks();
        server.SetBody(TarkovDevJsonClient.TasksPath, """
            {"data":{"tasks":{"5c51aac186f77432ea65c552":{
              "name":"5c51aac186f77432ea65c552 name",
              "normalizedName":"collector",
              "wikiLink":"https://escapefromtarkov.fandom.com/wiki/Collector"
            }}}}
            """);
        using var client = server.CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchTasksAsync(conditional: false));
        Assert.Contains("has no id", ex.Message);
    }

    [Fact]
    public async Task Reads_every_loyalty_comparison_the_schema_has_a_reading_for()
    {
        // A stored level reads as "at least N", so ">=" and "=" are N and ">" is N + 1. Dropping
        // a gate the schema could hold shows a quest as available that the game gates.
        using var server = FakeServer.WithTasks();
        server.SetBody(TarkovDevJsonClient.TasksPath, FakeServer.TasksWithTraderRequirements(
            """
            {"requirementType":"level","compareMethod":">=","value":4,"trader":"t-at-least"},
            {"requirementType":"level","compareMethod":"=","value":3,"trader":"t-exactly"},
            {"requirementType":"level","compareMethod":">","value":2,"trader":"t-above"},
            {"requirementType":"level","value":1,"trader":"t-unstated"}
            """));
        using var client = server.CreateClient();

        var fetched = await client.FetchTasksAsync(conditional: false);

        var gates = fetched!.Value.Single().TraderLevelRequirements;
        Assert.Equal(4, gates.Count);
        Assert.Equal(4, gates.Single(g => g.TraderId == "t-at-least").Level);
        Assert.Equal(3, gates.Single(g => g.TraderId == "t-exactly").Level);
        Assert.Equal(3, gates.Single(g => g.TraderId == "t-above").Level);
        Assert.Equal(1, gates.Single(g => g.TraderId == "t-unstated").Level);
    }

    [Fact]
    public async Task Reports_the_trader_gates_it_could_not_read()
    {
        // The gates the schema cannot hold are counted and named rather than vanishing: an
        // upper bound has no "at least" reading, and a gate with no trader names nothing.
        using var server = FakeServer.WithTasks();
        server.SetBody(TarkovDevJsonClient.TasksPath, FakeServer.TasksWithTraderRequirements(
            """
            {"requirementType":"level","compareMethod":"<=","value":2,"trader":"t-upper-bound"},
            {"requirementType":"level","compareMethod":">=","value":2},
            {"requirementType":"reputation","compareMethod":">=","value":3,"trader":"t-reputation"}
            """));
        using var client = server.CreateClient();

        var progress = new List<string>();
        var fetched = await client.FetchTasksAsync(conditional: false, progress: progress.Add);

        Assert.Empty(fetched!.Value.Single().TraderLevelRequirements);
        Assert.Contains(progress, line => line.Contains("dropped 1 reputation gate"));
        Assert.Contains(progress, line =>
            line.Contains("dropped 2 trader requirement(s)") && line.Contains("t-upper-bound"));
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
    public async Task Two_items_on_one_wiki_page_keep_the_first_and_report_the_pair()
    {
        // A wiki page belongs to one item, so a second claimant is a defect the page-keyed
        // pipeline cannot resolve. The first entry keeps the page, so the winner does not depend
        // on where in the file the collision sits, and the operator is told which id lost.
        using var server = FakeServer.WithItems();
        server.SetBody(TarkovDevJsonClient.ItemsPath, """
            {"data":{"items":{
              "5449016a4bdc2d6f028b456f":{
                "id":"5449016a4bdc2d6f028b456f",
                "name":"5449016a4bdc2d6f028b456f Name",
                "shortName":"5449016a4bdc2d6f028b456f ShortName",
                "normalizedName":"roubles",
                "wikiLink":"https://escapefromtarkov.fandom.com/wiki/Roubles"
              },
              "6666016a4bdc2d6f028b4444":{
                "id":"6666016a4bdc2d6f028b4444",
                "name":"6666016a4bdc2d6f028b4444 Name",
                "shortName":"6666016a4bdc2d6f028b4444 ShortName",
                "normalizedName":"roubles-again",
                "wikiLink":"https://escapefromtarkov.fandom.com/wiki/Roubles"
              }
            }}}
            """);
        using var client = server.CreateClient();

        var progress = new List<string>();
        var fetched = await client.FetchItemsAsync(conditional: false, progress: progress.Add);

        var item = Assert.Single(fetched!.Value).Value;
        Assert.Equal("5449016a4bdc2d6f028b456f", item.BsgId);
        Assert.Contains(progress, line =>
            line.Contains("claimed by more than one item") && line.Contains("6666016a4bdc2d6f028b4444"));
    }

    [Fact]
    public async Task An_empty_item_set_fails_instead_of_emptying_the_cache()
    {
        // The January refusal is not the task endpoint's alone: an empty item cache is what
        // left every hideout requirement unresolvable for seven months.
        using var server = FakeServer.WithItems();
        server.SetBody(TarkovDevJsonClient.ItemsPath, """{"data":{"items":{}}}""");
        using var client = server.CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchItemsAsync(conditional: false));
        Assert.Contains("no items", ex.Message);
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
    public async Task An_empty_trader_set_fails_instead_of_emptying_the_cache()
    {
        // The traders endpoint answers with the records directly under "data", so an error body
        // reaches the refusal as a null payload rather than an empty one.
        using var server = FakeServer.WithTraders();
        server.SetBody(TarkovDevJsonClient.TradersPath, """{"data":{}}""");
        using var client = server.CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchTradersAsync(conditional: false));
        Assert.Contains("no traders", ex.Message);
    }

    [Fact]
    public async Task An_empty_station_set_fails_instead_of_emptying_the_cache()
    {
        using var server = FakeServer.WithHideout();
        server.SetBody(TarkovDevJsonClient.HideoutPath, """{"data":{}}""");
        using var client = server.CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchHideoutAsync(
            Array.Empty<TarkovDevMultiLangItem>(), Array.Empty<TarkovDevTraderCacheItem>(), conditional: false));
        Assert.Contains("no stations", ex.Message);
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

    [Fact]
    public async Task A_hideout_loyalty_gate_written_as_an_exact_level_is_still_a_gate()
    {
        // Dropping it would show the station as buildable when the game refuses to build it.
        using var server = FakeServer.WithHideout();
        server.SetBody(TarkovDevJsonClient.HideoutPath, FakeServer.HideoutWithTraderRequirements(
            """
            {"requirementType":"level","compareMethod":"=","value":2,"trader":"5ac3b934156ae10c4430e83c"},
            {"requirementType":"level","compareMethod":">","value":3,"trader":"5a7c2eca46aef81a7ca2145d"}
            """));
        using var client = server.CreateClient();

        var fetched = await client.FetchHideoutAsync(
            Array.Empty<TarkovDevMultiLangItem>(), Array.Empty<TarkovDevTraderCacheItem>(), conditional: false);

        var gates = Assert.Single(fetched!.Value).Levels[0].TraderRequirements;
        Assert.Equal(2, gates.Count);
        Assert.Equal(2, gates.Single(g => g.TraderId == "5ac3b934156ae10c4430e83c").Level);
        Assert.Equal(4, gates.Single(g => g.TraderId == "5a7c2eca46aef81a7ca2145d").Level);
    }

    [Fact]
    public async Task A_station_served_twice_under_one_id_fails_with_both_keys_named()
    {
        // Two records under one id would collapse into a single station carrying whichever
        // levels were read last, so the fetch has to name the collision rather than let a
        // dictionary throw about "an item with the same key".
        using var server = FakeServer.WithHideout();
        server.SetBody(TarkovDevJsonClient.HideoutPath, """
            {"data":{
              "5d494a0e5b56502f18c98a02":{
                "id":"5d494a0e5b56502f18c98a02","name":"hideout_area_13_name","levels":[]
              },
              "library-duplicate":{
                "id":"5d494a0e5b56502f18c98a02","name":"hideout_area_13_name","levels":[]
              }
            }}
            """);
        using var client = server.CreateClient();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.FetchHideoutAsync(
            Array.Empty<TarkovDevMultiLangItem>(), Array.Empty<TarkovDevTraderCacheItem>(), conditional: false));

        Assert.Contains("5d494a0e5b56502f18c98a02", ex.Message);
        Assert.Contains("library-duplicate", ex.Message);
    }

    [Fact]
    public async Task A_station_whose_key_is_not_its_id_is_named_by_its_id()
    {
        // Nothing requires the outer key to equal the record's own id, and the id is what a
        // level's station prerequisite points at.
        using var server = FakeServer.WithHideout();
        server.SetBody(TarkovDevJsonClient.HideoutPath, """
            {"data":{"some-other-key":{
              "id":"5d494a0e5b56502f18c98a02",
              "name":"hideout_area_13_name",
              "normalizedName":"library",
              "levels":[{"level":2,"constructionTime":0,"stationLevelRequirements":[
                {"station":"5d494a0e5b56502f18c98a02","level":1}
              ]}]
            }}}
            """);
        using var client = server.CreateClient();

        var fetched = await client.FetchHideoutAsync(
            Array.Empty<TarkovDevMultiLangItem>(), Array.Empty<TarkovDevTraderCacheItem>(), conditional: false);

        var station = Assert.Single(fetched!.Value);
        Assert.Equal("5d494a0e5b56502f18c98a02", station.Id);
        var prerequisite = Assert.Single(station.Levels[0].StationLevelRequirements);
        Assert.Equal("Library", prerequisite.StationName);
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
            var first = await client.FetchTasksAsync(conditional: true);
            Assert.NotNull(first);
            first!.CommitETags();
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
            (await client.FetchTasksAsync(conditional: true))!.CommitETags();
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

    [Fact]
    public async Task An_uncommitted_fetch_leaves_the_next_run_asking_unconditionally()
    {
        // Parsing is not the last step that can fail, so the fetch does not record its own
        // ETags. Until the caller says the value reached the disk, the store keeps naming the
        // revision the cache actually holds, which is nothing here.
        using var server = FakeServer.WithTasks();
        var cacheDir = NewCacheDir();
        using (var client = server.CreateClient(cacheDir))
        {
            Assert.NotNull(await client.FetchTasksAsync(conditional: true));
        }

        using var second = server.CreateClient(cacheDir);
        Assert.NotNull(await second.FetchTasksAsync(conditional: true));
        Assert.Empty(server.LastIfNoneMatch);
    }

    [Fact]
    public async Task An_etag_is_not_recorded_when_the_cache_file_could_not_be_written()
    {
        // The ETag store and the cache file are one claim in two places. If the store advanced
        // while the write failed, the next run would be told 304, re-stamp the kept file as
        // verified, and the refresh would publish pre-patch quests with every guard green.
        using var server = FakeServer.WithTasks();
        var baseDir = NewCacheDir();
        var cacheDir = Path.Combine(baseDir, "cache");
        Directory.CreateDirectory(cacheDir);
        // A directory where the cache file belongs fails the write the way a locked file does.
        Directory.CreateDirectory(Path.Combine(cacheDir, "tarkov_dev_quests.json"));

        using (var client = server.CreateClient(cacheDir))
        using (var service = new TarkovDevDataService(baseDir, client))
        {
            var result = await service.CacheAllDataAsync();
            Assert.False(result.Quests.Success);
        }

        server.Reset();
        using var second = server.CreateClient(cacheDir);
        Assert.NotNull(await second.FetchTasksAsync(conditional: true));
        Assert.False(server.LastIfNoneMatch.ContainsKey(TarkovDevJsonClient.TasksPath));
    }

    [Fact]
    public async Task A_cache_file_that_was_written_records_its_etag_for_the_next_run()
    {
        using var server = FakeServer.WithTasks();
        var baseDir = NewCacheDir();
        var cacheDir = Path.Combine(baseDir, "cache");

        using (var client = server.CreateClient(cacheDir))
        using (var service = new TarkovDevDataService(baseDir, client))
        {
            var result = await service.CacheAllDataAsync();
            Assert.True(result.Quests.Success);
            Assert.Equal(2, result.Quests.Count);
        }

        Assert.True(File.Exists(Path.Combine(cacheDir, "tarkov_dev_quests.json")));
        foreach (var suffix in new[] { "", "_en", "_ko", "_ja" })
            server.NotModifiedFor(TarkovDevJsonClient.TasksPath + suffix);

        using var second = server.CreateClient(cacheDir);
        Assert.Null(await second.FetchTasksAsync(conditional: true));
        Assert.Equal("\"tasks-v1\"", server.LastIfNoneMatch[TarkovDevJsonClient.TasksPath]);
    }

    [Fact]
    public async Task A_part_upstream_reports_unchanged_is_kept_counted_and_restamped()
    {
        // The keep is the branch the refresh's freshness guard depends on: the body is left
        // alone (the items file is 16 MB), the count still has to come from the kept file, and
        // the write time has to move so "when did we last confirm this is current" stays true.
        using var server = FakeServer.WithTasks();
        var baseDir = NewCacheDir();
        var cacheDir = Path.Combine(baseDir, "cache");

        using (var client = server.CreateClient(cacheDir))
        using (var service = new TarkovDevDataService(baseDir, client))
        {
            Assert.True((await service.CacheAllDataAsync()).Quests.Success);
        }

        var questsPath = Path.Combine(cacheDir, "tarkov_dev_quests.json");
        File.SetLastWriteTimeUtc(questsPath, DateTime.UtcNow.AddDays(-30));
        var stale = File.GetLastWriteTimeUtc(questsPath);
        var body = File.ReadAllBytes(questsPath);

        foreach (var suffix in new[] { "", "_en", "_ko", "_ja" })
            server.NotModifiedFor(TarkovDevJsonClient.TasksPath + suffix);

        using (var client = server.CreateClient(cacheDir))
        using (var service = new TarkovDevDataService(baseDir, client))
        {
            var part = (await service.CacheAllDataAsync()).Quests;
            Assert.True(part.Success);
            Assert.True(part.Kept);
            // Counted from the kept file: no fetch answered with a value to count.
            Assert.Equal(2, part.Count);
        }

        Assert.True(File.GetLastWriteTimeUtc(questsPath) > stale);
        Assert.Equal(body, File.ReadAllBytes(questsPath));
    }

    [Fact]
    public async Task Disposing_the_service_leaves_a_client_it_was_handed_usable()
    {
        // A client passed in belongs to the caller, the same rule the client applies to a
        // handler. Disposing it here would hand the caller an ObjectDisposedException.
        using var server = FakeServer.WithTasks();
        using var client = server.CreateClient();

        using (var service = new TarkovDevDataService(NewCacheDir(), client))
        {
            Assert.True(service.GetCacheInfo().QuestsCount == 0);
        }

        Assert.NotNull(await client.FetchTasksAsync(conditional: false));
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
                    "failConditions":[],
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
                    "failConditions":[
                      {"id":"f1","description":"f1","type":"taskStatus","count":null,"optional":false,
                       "task":"5c51aac186f77432ea65c552","status":["complete"],"zones":[],"maps":[]},
                      {"id":"f2","description":"f2","type":"traderStanding","optional":false,
                       "compareMethod":"<=","value":0,"trader":"638f541a29ffd1183d187f57"}
                    ],
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

        // JSON is mostly braces, so these bodies substitute a token rather than interpolate.
        private const string RequirementsToken = "<requirements>";

        /// <summary>One task carrying the given <c>traderRequirements</c> entries.</summary>
        public static string TasksWithTraderRequirements(string entries) => """
            {"data":{"tasks":{"5c51aac186f77432ea65c552":{
              "id":"5c51aac186f77432ea65c552",
              "name":"5c51aac186f77432ea65c552 name",
              "normalizedName":"collector",
              "wikiLink":"https://escapefromtarkov.fandom.com/wiki/Collector",
              "traderRequirements":[<requirements>]
            }}}}
            """.Replace(RequirementsToken, entries);

        /// <summary>One hideout level carrying the given <c>traderRequirements</c> entries.</summary>
        public static string HideoutWithTraderRequirements(string entries) => """
            {"data":{"5d494a0e5b56502f18c98a02":{
              "id":"5d494a0e5b56502f18c98a02",
              "name":"hideout_area_13_name",
              "normalizedName":"library",
              "levels":[{
                "level":1,
                "constructionTime":194400,
                "traderRequirements":[<requirements>]
              }]
            }}}
            """.Replace(RequirementsToken, entries);

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
