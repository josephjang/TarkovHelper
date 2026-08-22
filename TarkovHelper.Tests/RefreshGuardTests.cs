using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TarkovDBEditor.Services;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The refusals a refresh makes before it writes anything, and what it writes when it does not
/// refuse.
/// <para>
/// Every guard here has a failure behind it. A wiki crawl that returned nothing used to report
/// success and delete every quest. A tarkov.dev cache overwritten with an empty set produced the
/// January regeneration that published 488 quests and 4014 items with no external ID, which is
/// why log sync has matched nothing since. And a refresh run from a database in that state
/// would mint a fresh row key for all 91 quests patch 1.1 renamed, detaching their recorded
/// progress in every build already installed.
/// </para>
/// </summary>
public sealed class RefreshGuardTests
{
    private const string CollectorId = "5c51aac186f77432ea65c552";
    private const string StirrupId = "5c0be13186f7746309d759c8";
    private const string PraporId = "54cb50c76803fa8b248b4571";
    private const string JaegerId = "5c0647fdd443bc2504c2d371";

    /// <summary>
    /// The local time to stamp the task cache file with. Whole seconds because the assertions
    /// read the value back out of the file system, and relative to now because a run's own
    /// staleness guard refuses a task cache confirmed more than a week ago.
    /// </summary>
    private static readonly DateTime TaskCacheConfirmedAt = TruncateToSecond(DateTime.Now.AddHours(-2));

    private static DateTime TruncateToSecond(DateTime value) =>
        new(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, value.Kind);

    /// <summary>
    /// Writes an unparseable <c>tarkov_dev_items.json</c> into the fixture's cache folder. The
    /// quest refresh takes its items from the database, so any code path that fails after this
    /// is a path that read the file it had no business reading.
    /// </summary>
    private static void DamageTheItemsCache(RefreshPipelineFixture fixture) =>
        File.WriteAllText(Path.Combine(fixture.CacheDir, "tarkov_dev_items.json"), "{ not json");

    #region Refusals

    [Fact]
    public async Task An_empty_wiki_cache_fails_the_run_instead_of_deleting_every_quest()
    {
        using var fixture = new RefreshPipelineFixture()
            .WithNoWikiPages()
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("no page content", result.ErrorMessage);
        Assert.Single(fixture.ReadQuestColumn("Name"));
    }

    [Fact]
    public async Task A_missing_task_cache_fails_the_run()
    {
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page()))
            .WithNoTaskCache()
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("Cache Tarkov Dev Data", result.ErrorMessage);
    }

    [Fact]
    public async Task An_empty_task_cache_fails_the_run()
    {
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks()
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("task cache is empty", result.ErrorMessage);
    }

    [Fact]
    public async Task A_missing_trader_cache_fails_the_run()
    {
        // Quests name their trader by id now, so without this cache every row would publish
        // with Trader NULL and the fielded build's list would lose its grouping.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithNoTraderCache()
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("trader cache is empty", result.ErrorMessage);
    }

    [Fact]
    public async Task A_task_cache_that_lags_the_crawl_fails_the_run()
    {
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId))
            .WithTaskCacheLastConfirmed(DateTime.Now.AddDays(-30));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("days ago", result.ErrorMessage);
    }

    [Fact]
    public void The_staleness_guard_reads_the_task_cache_write_time()
    {
        using var fixture = new RefreshPipelineFixture()
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTaskCacheLastConfirmed(TaskCacheConfirmedAt);

        using var devService = new TarkovDevDataService(fixture.WikiDataDir);

        Assert.Equal(TaskCacheConfirmedAt, devService.GetQuestsCacheVerifiedAt());
    }

    [Fact]
    public void The_staleness_guard_gets_no_timestamp_when_there_is_no_task_cache()
    {
        // Null means "cannot tell", and the guard lets the run through on it: an absent task
        // cache is the next check's business, not this one's.
        using var fixture = new RefreshPipelineFixture().WithNoTaskCache();

        using var devService = new TarkovDevDataService(fixture.WikiDataDir);

        Assert.Null(devService.GetQuestsCacheVerifiedAt());
    }

    [Fact]
    public void The_staleness_guard_does_not_need_the_items_cache_to_be_readable()
    {
        // The guard wants one timestamp. Taking it from GetCacheInfo() would count every cache
        // instead, and counting means reading and deserializing all four files, the items one
        // being about 16 MB that the quest path never looks inside.
        using var fixture = new RefreshPipelineFixture()
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTaskCacheLastConfirmed(TaskCacheConfirmedAt);
        DamageTheItemsCache(fixture);

        using var devService = new TarkovDevDataService(fixture.WikiDataDir);

        Assert.Equal(TaskCacheConfirmedAt, devService.GetQuestsCacheVerifiedAt());
    }

    [Fact]
    public async Task A_run_reports_when_the_task_cache_was_confirmed_and_never_opens_the_items_cache()
    {
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId))
            .WithTaskCacheLastConfirmed(TaskCacheConfirmedAt);
        // RefreshDataFromCacheAsync takes its items from the database, so a damaged items cache
        // must not reach the quest path at all.
        DamageTheItemsCache(fixture);

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains("1 tasks and 1 traders from the tarkov.dev cache")
                && m.Contains($"(verified {TaskCacheConfirmedAt:yyyy-MM-dd HH:mm})"));
    }

    [Fact]
    public async Task An_unbackfilled_previous_database_fails_the_run_and_names_the_fix()
    {
        // This is the guard the whole carry-over rests on, and the one the match-rate guard
        // cannot stand in for: the pages still match, so nothing else looks wrong.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", null), ("Collector", null));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("Backfill external IDs from snapshot", result.ErrorMessage);
    }

    [Fact]
    public async Task A_collapse_in_the_match_rate_fails_the_run()
    {
        // Ten published quests, a task set that only knows one: an outage serving a partial
        // file, not a patch.
        var previous = Enumerable.Range(1, 10)
            .Select(i => ($"Quest {i}", (string?)$"5c0be13186f7746309d759{i:00}"))
            .ToArray();
        var pages = Enumerable.Range(1, 10)
            .Select(i => ($"Quest {i}", RefreshPipelineFixture.Page()))
            .ToArray();

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(pages)
            .WithTasks(RefreshPipelineFixture.Task("5c0be13186f7746309d75901", "Quest 1"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(previous);

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("would lose their game record", result.ErrorMessage);
    }

    [Fact]
    public async Task A_crawl_whose_seasonal_marker_stopped_matching_fails_the_run()
    {
        // The pages talk about a seasonal mode but none matches the marker: the wiki's wording
        // moved, and importing zero seasonal quests in silence is the failure to prevent.
        var movedMarker = RefreshPipelineFixture.Page(
            extraRequirement: "* Must be playing in the current seasonal mode to start this quest.");

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Uninvited Guests - Part 1", movedMarker))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("ExtractIsSeasonal", result.ErrorMessage);
    }

    [Fact]
    public async Task A_faction_value_the_fielded_build_cannot_read_fails_the_run()
    {
        // The fielded build compares Faction for equality with the player's side, so an
        // unrecognised value hides the quest from everyone.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup", faction: "SCAV"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("SCAV", result.ErrorMessage);
    }

    [Fact]
    public async Task A_prerequisite_status_the_app_cannot_read_fails_the_run()
    {
        // An unknown requirement type is never satisfied in the fielded build, so the quest
        // would be locked forever with no way to fix it after the fact.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Collector", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(CollectorId, "Collector"),
                RefreshPipelineFixture.Task(StirrupId, "Stirrup", requires: new[] { (CollectorId, "abandoned") }))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId), ("Collector", CollectorId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("abandoned", result.ErrorMessage);
    }

    [Fact]
    public async Task Published_quests_losing_their_row_key_fail_the_run_even_with_no_external_id()
    {
        // The match-rate guard measures only the rows that have an external ID, and the
        // backfill guard tolerates a tenth of them without one. A row in that tenth cannot be
        // carried, so when its page goes it is deleted and its recorded progress is orphaned,
        // with both other guards reading green. Two of thirty here: 7% of the row keys.
        var previous = Enumerable.Range(1, 28)
            .Select(i => ($"Quest {i}", (string?)$"5c0be13186f7746309d7{i:0000}"))
            .Concat(new[] { ("Ghost Quest 1", (string?)null), ("Ghost Quest 2", (string?)null) })
            .ToArray();
        var pages = Enumerable.Range(1, 28)
            .Select(i => ($"Quest {i}", RefreshPipelineFixture.Page()))
            .ToArray();
        var tasks = Enumerable.Range(1, 28)
            .Select(i => RefreshPipelineFixture.Task($"5c0be13186f7746309d7{i:0000}", $"Quest {i}"))
            .ToArray();

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(pages)
            .WithTasks(tasks)
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(previous);

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("row key", result.ErrorMessage);
        Assert.Contains("Ghost Quest", result.ErrorMessage);
        // Nothing was written: the two rows are still there to be looked at.
        Assert.Equal(30, fixture.ReadQuestColumn("Name").Count);
    }

    [Fact]
    public async Task A_seasonal_marker_that_still_matches_one_page_fails_the_run_for_the_rest()
    {
        // The marker guard used to fire only when NOT ONE page matched, so a wiki edit that
        // spared a single page left the rest unmarked, unmatched, held back, and deleted with
        // their objectives and prerequisites. None of them carries an external ID, so the
        // match-rate guard cannot see them either.
        var movedMarker = RefreshPipelineFixture.Page(
            extraRequirement: "* Must be playing in the current seasonal mode to start this quest.");

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Uninvited Guests - Part 1", RefreshPipelineFixture.SeasonalPage()),
                ("Uninvited Guests - Part 2", movedMarker),
                ("Uninvited Guests - Part 3", movedMarker),
                ("Uninvited Guests - Part 4", movedMarker),
                ("Uninvited Guests - Part 5", movedMarker))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("ExtractIsSeasonal", result.ErrorMessage);
        Assert.Contains("4 of 5", result.ErrorMessage);
    }

    [Fact]
    public async Task Two_quests_that_would_share_a_row_key_are_named_by_the_error()
    {
        // The reachable collision: a renamed quest carries its old row key while a new quest
        // mints the key the freed title makes. Only this message names the pair; the
        // dictionary that used to fail first reported a base64 key and nothing else.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Sew it Good", RefreshPipelineFixture.Page()),
                ("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(StirrupId, "Sew it Good"),
                RefreshPipelineFixture.Task("5c0be13186f7746309d759cc", "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("Sew it Good", result.ErrorMessage);
        Assert.Contains("Stirrup", result.ErrorMessage);
        Assert.Contains("row key", result.ErrorMessage);
    }

    [Fact]
    public async Task Two_quests_that_would_share_a_normalized_name_are_named_by_the_error()
    {
        // The other half of the identity guard, and the half no other guard reaches: the
        // resolver's own row-key check cannot see this one, because the two quests keep
        // different row keys. A renamed quest carries the normalized name its old title made,
        // and a new quest whose title differs from that old one only by an apostrophe mints the
        // same value: the app drops the ASCII apostrophe, so both rows would answer to
        // "peacekeepers-task" and the progress recorded under it becomes ambiguous.
        const string RenamedId = "5c0be13186f7746309d759d1";
        const string NewcomerId = "5c0be13186f7746309d759d2";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Diplomatic Immunity", RefreshPipelineFixture.Page()),
                ("Peacekeepers Task", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(RenamedId, "Diplomatic Immunity"),
                RefreshPipelineFixture.Task(NewcomerId, "Peacekeepers Task"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Peacekeeper's Task", RenamedId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("normalized name", result.ErrorMessage);
        Assert.Contains("peacekeepers-task", result.ErrorMessage);
        Assert.Contains("Diplomatic Immunity", result.ErrorMessage);
        Assert.Contains("Peacekeepers Task", result.ErrorMessage);
        // Nothing was written: the one row the database started with is still the only one.
        Assert.Equal(new[] { "Peacekeeper's Task" }, fixture.ReadQuestColumn("Name").Keys);
    }

    [Fact]
    public async Task The_identity_guard_stops_the_run_before_anything_indexes_a_quest_by_its_key()
    {
        // Placement, not message. The guard is deliberately in front of the resolver's report
        // line, because everything past that line indexes quests by their row key
        // (ComputePrerequisiteDisagreements builds a Dictionary over it) and would otherwise
        // fail first with an anonymous duplicate-key error naming a base64 string and neither
        // quest. The progress log is where that ordering is observable: a run that reached the
        // "Resolved N quests" line got past the guard.
        const string RenamedId = "5c0be13186f7746309d759d1";
        const string NewcomerId = "5c0be13186f7746309d759d2";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Diplomatic Immunity", RefreshPipelineFixture.Page()),
                ("Peacekeepers Task", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(RenamedId, "Diplomatic Immunity"),
                RefreshPipelineFixture.Task(NewcomerId, "Peacekeepers Task"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Peacekeeper's Task", RenamedId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        // The guard runs, so the match-rate report before it was reached ...
        Assert.Contains(fixture.ProgressMessages, m => m.Contains("lost their row key"));
        // ... and the resolver report after it was not.
        Assert.DoesNotContain(fixture.ProgressMessages, m => m.StartsWith("Resolved "));
    }

    [Fact]
    public async Task A_task_set_that_stopped_flagging_kappa_fails_the_run_and_names_both_counts()
    {
        // Nothing else measures the Kappa set. The row counts hold, the vocabularies are clean,
        // the match rate is untouched, and the per-table delete budget cannot see it either:
        // Collector's prerequisites are under a third of QuestRequirements in the published
        // database, well inside the 80% budget. So a flag that stops arriving - a renamed field
        // upstream, a retyped value, a mapping that quietly reads false - would empty Collector's
        // list and the app's Kappa gauge with every guard green.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Collector", RefreshPipelineFixture.Page()),
                ("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(CollectorId, "Collector", kappaRequired: true),
                RefreshPipelineFixture.Task(StirrupId, "Stirrup", kappaRequired: true))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Collector", CollectorId), ("Stirrup", StirrupId));

        Assert.True((await fixture.RefreshAsync()).Success);
        Assert.Equal(2, fixture.Query("SELECT Id FROM Quests WHERE KappaRequired = 1").Count);

        // The same task set, with the flag gone.
        fixture.WithTasks(
            RefreshPipelineFixture.Task(CollectorId, "Collector"),
            RefreshPipelineFixture.Task(StirrupId, "Stirrup"));

        var second = await fixture.RefreshAsync();

        Assert.False(second.Success);
        Assert.Contains("No quest is flagged", second.ErrorMessage);
        // Both numbers, so a reviewer can tell an empty set from a shrinking one.
        Assert.Contains("the current database flags 2", second.ErrorMessage);
        // Refused before the write, so the published flags are still there.
        Assert.Equal(2, fixture.Query("SELECT Id FROM Quests WHERE KappaRequired = 1").Count);
    }

    [Fact]
    public async Task A_quest_collector_requires_that_is_not_in_the_kappa_set_fails_the_run()
    {
        // BuildRequirements skips Collector's own prerequisite list because every entry on it is
        // already in the Kappa set the synthesis writes. When one is not, the skip drops it and
        // nothing replaces it: Collector publishes unlocked by a prerequisite the game enforces,
        // and no count anywhere changes. This is the shape check the volume check cannot do.
        const string GrenadierId = "5c0be13186f7746309d759c9";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Collector", RefreshPipelineFixture.Page()),
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Grenadier", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(
                    CollectorId, "Collector", kappaRequired: true,
                    requires: new[] { (GrenadierId, "complete") }),
                RefreshPipelineFixture.Task(StirrupId, "Stirrup", kappaRequired: true),
                RefreshPipelineFixture.Task(GrenadierId, "Grenadier", kappaRequired: false))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Collector", CollectorId), ("Stirrup", StirrupId), ("Grenadier", GrenadierId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("not in the Kappa set", result.ErrorMessage);
        Assert.Contains("Grenadier", result.ErrorMessage);
        Assert.Empty(fixture.Query("SELECT Id FROM QuestRequirements"));
    }

    [Fact]
    public async Task A_run_that_would_delete_most_of_a_child_table_fails_instead_of_cascading()
    {
        // Every child table's only protection used to be "the new list is not empty", which
        // catches total loss and nothing else. A run that produced one prerequisite instead of
        // seven hundred passed, and the table-global diff then deleted the rest, cascading
        // through the foreign keys into every install that downloaded it.
        var pages = new List<(string, string)> { ("Collector", RefreshPipelineFixture.Page()) };
        var tasks = new List<TarkovDevQuestCacheItem>
        {
            RefreshPipelineFixture.Task(CollectorId, "Collector"),
        };
        var rows = new List<(string, string?)> { ("Collector", CollectorId) };

        for (var i = 0; i < 10; i++)
        {
            var title = $"Filler Quest {i}";
            var taskId = $"5c0be13186f7746309d7{i:d4}";
            pages.Add((title, RefreshPipelineFixture.Page()));
            tasks.Add(RefreshPipelineFixture.Task(taskId, title, requires: new[] { (CollectorId, "complete") }));
            rows.Add((title, taskId));
        }

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(pages.ToArray())
            .WithTasks(tasks.ToArray())
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(rows.ToArray());

        Assert.True((await fixture.RefreshAsync()).Success);
        Assert.Equal(10, fixture.Query("SELECT Id FROM QuestRequirements").Count);

        // The next run reports one prerequisite instead of ten: not an empty list, so the
        // emptiness check waves it through.
        var collapsed = tasks
            .Select(t => t.Id == tasks[1].Id || t.Id == CollectorId
                ? t
                : RefreshPipelineFixture.Task(t.Id, t.NameEN!))
            .ToArray();
        fixture.WithTasks(collapsed);

        var second = await fixture.RefreshAsync();

        Assert.False(second.Success);
        Assert.Contains("QuestRequirements", second.ErrorMessage);
        Assert.Contains("9 of 10", second.ErrorMessage);
        // The throw lands inside the write transaction, which is rolled back.
        Assert.Equal(10, fixture.Query("SELECT Id FROM QuestRequirements").Count);
    }

    #endregion

    #region The delete budget measures identity, not keys

    /// <summary>
    /// The published QuestRequirements table as it ships in <c>data/v1/tarkov_data.db</c>: the
    /// row key, the prerequisite edge under it, and the OR group it was published in.
    /// </summary>
    private static List<(string Id, string QuestId, string RequiredQuestId, int GroupId)> PublishedPrerequisites()
    {
        var path = Path.Combine(TestRepo.Root(), "data", "v1", "tarkov_data.db");
        Assert.True(File.Exists(path), $"{path} is missing, so there is no published table to measure against");

        var rows = new List<(string, string, string, int)>();
        using (var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly"))
        {
            connection.Open();
            using var cmd = new SqliteCommand(
                "SELECT Id, QuestId, RequiredQuestId, GroupId FROM QuestRequirements", connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        SqliteConnection.ClearAllPools();
        return rows;
    }

    [Fact]
    public void Not_one_published_prerequisite_row_keeps_its_key_under_the_new_scheme()
    {
        // The fact the rest of this region rests on, read off the shipped database rather than
        // asserted from memory. All 546 non-Collector rows are RowHash over a wiki GroupId of 1
        // or more and the pipeline now emits 0; all 248 Collector rows are the old
        // <collectorId>_<questId> concatenation and the synthesis now goes through RowHash. So
        // the first 1.1 run replaces every key in the table while the edges under them stand.
        var published = PublishedPrerequisites();
        Assert.Equal(794, published.Count);

        var keptTheirKey = published
            .Where(r => r.Id == NewRowKeyFor(r.QuestId, r.RequiredQuestId))
            .ToList();
        Assert.Empty(keptTheirKey);

        // And the edges are unique, which is what makes the pair a usable identity: the publish
        // guard refuses a second row for one pair, so the new set cannot collapse two into one
        // either.
        Assert.Equal(794, published.Select(r => (r.QuestId, r.RequiredQuestId)).Distinct().Count());
    }

    [Fact]
    public void The_delete_budget_passes_a_run_that_only_re_keys_the_published_prerequisite_table()
    {
        // The defect this measure exists to remove. Measured by row key, a run that keeps every
        // prerequisite edge and only renumbers the groups reads as 794 of 794 rows deleted and
        // aborts the regeneration. Measured by the edge, it deletes nothing.
        var published = PublishedPrerequisites();
        var edges = published.Select(r => NaturalIdOf(r.QuestId, r.RequiredQuestId)).ToList();

        AssertDeleteBudgetHeld("QuestRequirements", edges, edges, published.Count);

        var byKey = Assert.Throws<InvalidOperationException>(() => AssertDeleteBudgetHeld(
            "QuestRequirements",
            published.Select(r => r.Id).ToList(),
            published.Select(r => NewRowKeyFor(r.QuestId, r.RequiredQuestId)).ToList(),
            published.Count));
        Assert.Contains("794 of 794", byKey.Message);
    }

    [Fact]
    public void The_delete_budget_still_refuses_the_published_prerequisite_table_collapsing_to_one_edge()
    {
        // The measure keeps its meaning: the "one prerequisite instead of seven hundred" parse
        // is still 99.9% of the table gone however the survivor is keyed.
        var published = PublishedPrerequisites();
        var edges = published.Select(r => NaturalIdOf(r.QuestId, r.RequiredQuestId)).ToList();

        var collapsed = Assert.Throws<InvalidOperationException>(() => AssertDeleteBudgetHeld(
            "QuestRequirements", edges, edges.Take(1).ToList(), published.Count - 1));

        Assert.Contains("793 of 794", collapsed.Message);
        Assert.Contains("100%", collapsed.Message);
    }

    [Fact]
    public void The_delete_budget_counts_an_edge_that_is_genuinely_gone_even_when_the_rest_are_re_keyed()
    {
        // A re-key does not buy an amnesty for the rows that really did disappear: half the
        // table is dropped while the other half changes key, and the measure reports the half
        // that is gone rather than 0% or 100%.
        var published = PublishedPrerequisites();
        var edges = published.Select(r => NaturalIdOf(r.QuestId, r.RequiredQuestId)).ToList();
        var kept = edges.Take(edges.Count / 2).ToList();

        var reported = new List<string>();
        AssertDeleteBudgetHeld("QuestRequirements", edges, kept, published.Count, reported.Add);

        Assert.Contains(reported, m => m.Contains("397 of 794") && m.Contains("50.0%"));
    }

    [Fact]
    public async Task A_prerequisite_table_keyed_by_an_older_scheme_is_re_keyed_rather_than_deleted()
    {
        // The same fact through the whole pipeline. Twelve quests each require Collector, and the
        // database holds those twelve edges under the keys the wiki-fed publish wrote: GroupId 1
        // rather than 0, so every key moves while every edge stands. The budget must see a
        // re-key, not a table being emptied.
        var pages = new List<(string, string)> { ("Collector", RefreshPipelineFixture.Page()) };
        var tasks = new List<TarkovDevQuestCacheItem> { RefreshPipelineFixture.Task(CollectorId, "Collector") };
        var rows = new List<(string, string?)> { ("Collector", CollectorId) };
        var titles = new List<string>();

        for (var i = 0; i < 12; i++)
        {
            var title = $"Filler Quest {i}";
            titles.Add(title);
            pages.Add((title, RefreshPipelineFixture.Page()));
            tasks.Add(RefreshPipelineFixture.Task(
                $"5c0be13186f7746309d7{i:d4}", title, requires: new[] { (CollectorId, "complete") }));
            rows.Add((title, $"5c0be13186f7746309d7{i:d4}"));
        }

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(pages.ToArray())
            .WithTasks(tasks.ToArray())
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(rows.ToArray());

        foreach (var title in titles)
            fixture.WithQuestRequirement(title, "Collector", groupId: 1);

        var seeded = fixture.Query("SELECT Id FROM QuestRequirements").Select(r => r[0]).ToHashSet();
        Assert.Equal(12, seeded.Count);

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);

        // Every edge survives, at the group the game-derived pipeline writes, under a key none
        // of the seeded rows had.
        var after = fixture.Query("SELECT Id, GroupId FROM QuestRequirements");
        Assert.Equal(12, after.Count);
        Assert.All(after, r => Assert.Equal("0", r[1]));
        Assert.Empty(after.Where(r => seeded.Contains(r[0])));

        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains("QuestRequirements: 0 of 12 row identities are gone")
                 && m.Contains("12 rows deleted by key"));
    }

    [Fact]
    public async Task A_collector_list_keyed_by_the_old_concatenation_survives_the_move_onto_row_hashes()
    {
        // Collector's 248 published rows are keyed <collectorId>_<questId>, a scheme that
        // predates RowHash entirely, and the synthesis now writes them through the ordinary
        // upsert. Nothing about the Kappa set changes; only the keys do.
        var pages = new List<(string, string)> { ("Collector", RefreshPipelineFixture.Page()) };
        var tasks = new List<TarkovDevQuestCacheItem> { RefreshPipelineFixture.Task(CollectorId, "Collector") };
        var rows = new List<(string, string?)> { ("Collector", CollectorId) };
        var titles = new List<string>();

        for (var i = 0; i < 12; i++)
        {
            var title = $"Kappa Quest {i}";
            titles.Add(title);
            pages.Add((title, RefreshPipelineFixture.Page()));
            tasks.Add(RefreshPipelineFixture.Task($"5c0be13186f7746309d7{i:d4}", title, kappaRequired: true));
            rows.Add((title, $"5c0be13186f7746309d7{i:d4}"));
        }

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(pages.ToArray())
            .WithTasks(tasks.ToArray())
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(rows.ToArray());

        var collectorId = WikiQuestIdentity.IdFor("Collector");
        foreach (var title in titles)
        {
            fixture.WithQuestRequirement(
                "Collector", title, id: $"{collectorId}_{WikiQuestIdentity.IdFor(title)}");
        }

        var seeded = fixture.Query("SELECT Id FROM QuestRequirements").Select(r => r[0]).ToHashSet();
        Assert.Equal(12, seeded.Count);

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var after = fixture.Query("SELECT Id, RequiredQuestId FROM QuestRequirements");
        Assert.Equal(12, after.Count);
        Assert.Empty(after.Where(r => seeded.Contains(r[0])));
        Assert.Equal(
            titles.Select(WikiQuestIdentity.IdFor).OrderBy(id => id, StringComparer.Ordinal),
            after.Select(r => r[1]).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains("QuestRequirements: 0 of 12 row identities are gone"));
    }

    /// <summary>The row key the current scheme computes for a prerequisite edge, at GroupId 0.</summary>
    private static string NewRowKeyFor(string questId, string requiredQuestId) =>
        new DbQuestRequirement { QuestId = questId, RequiredQuestId = requiredQuestId, GroupId = 0 }.ComputeId();

    /// <summary>The natural identity of a prerequisite edge, as the upsert projects it.</summary>
    private static string NaturalIdOf(string questId, string requiredQuestId) =>
        new DbQuestRequirement { QuestId = questId, RequiredQuestId = requiredQuestId, GroupId = 0 }.NaturalId();

    /// <summary>
    /// Runs the delete budget over two identity sets. Reached by reflection for the same reason
    /// <see cref="AssertPublishConstraints"/> is: <c>RefreshGuards</c> is internal to
    /// TarkovDBEditor and the assembly grants no InternalsVisibleTo, and asserting the member
    /// exists keeps a rename from turning these facts into no-ops.
    /// </summary>
    private static void AssertDeleteBudgetHeld(
        string table,
        IReadOnlyList<string> existingIdentities,
        IReadOnlyList<string> newIdentities,
        int rowsToDelete,
        Action<string>? progress = null)
    {
        var guards = typeof(RefreshDataService).GetNestedType(
            "RefreshGuards", System.Reflection.BindingFlags.NonPublic);
        Assert.True(guards != null, "RefreshDataService has no nested type 'RefreshGuards'");

        var method = guards!.GetMethod(
            "AssertDeleteBudgetHeld",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.True(method != null, "RefreshGuards has no public static AssertDeleteBudgetHeld");

        try
        {
            method!.Invoke(
                null, new object?[] { table, existingIdentities, newIdentities, rowsToDelete, progress });
        }
        catch (System.Reflection.TargetInvocationException invocation) when (invocation.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(invocation.InnerException).Throw();
        }
    }

    #endregion

    #region What a clean run writes

    [Fact]
    public async Task A_clean_run_writes_the_normalized_name_the_app_computes()
    {
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Sew it Good - Part 2", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Sew it Good - Part 2"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Sew it Good - Part 2", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal("sew-it-good---part-2", fixture.ReadQuestColumn("NormalizedName")["Sew it Good - Part 2"]);
    }

    [Fact]
    public async Task A_renamed_quest_keeps_its_row_key_and_its_progress_key()
    {
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Shooter Born in Heaven", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Shooter Born in Heaven"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("A Shooter Born in Heaven", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);

        var rows = fixture.Query("SELECT Name, Id, NormalizedName FROM Quests");
        var row = Assert.Single(rows);
        Assert.Equal("Shooter Born in Heaven", row[0]);
        Assert.Equal(WikiQuestIdentity.IdFor("A Shooter Born in Heaven"), row[1]);
        Assert.Equal("a-shooter-born-in-heaven", row[2]);
    }

    [Fact]
    public async Task Loyalty_gates_reach_their_own_table_including_a_trader_other_than_the_giver()
    {
        // Chemical - Part 3 is gated on Jaeger while Prapor gives it. A single column on Quests
        // would have dropped that, which is why this is a table.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Chemical - Part 3", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(
                StirrupId, "Chemical - Part 3", traderId: PraporId, loyalty: new[] { (JaegerId, 2) }))
            .WithTraders((PraporId, "Prapor"), (JaegerId, "Jaeger"))
            .WithDatabase(("Chemical - Part 3", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);

        var gates = fixture.Query(
            "SELECT q.Name, t.TraderName, t.RequiredLevel FROM QuestTraderRequirements t JOIN Quests q ON q.Id = t.QuestId");
        var gate = Assert.Single(gates);
        Assert.Equal(new[] { "Chemical - Part 3", "Jaeger", "2" }, gate);
        Assert.Equal("Prapor", fixture.ReadQuestColumn("Trader")["Chemical - Part 3"]);
    }

    [Fact]
    public async Task Zero_minimum_level_is_stored_as_null()
    {
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page(minLevel: 5)))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup", minPlayerLevel: 0))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        // The game says there is no level gate, so the stale wiki line does not win.
        Assert.Null(fixture.ReadQuestColumn("MinLevel")["Stirrup"]);
    }

    [Fact]
    public async Task Prerequisites_come_from_the_game_not_the_stale_wiki_chain()
    {
        var chained = RefreshPipelineFixture.Page() + "\n|previous = [[Collector]]\n";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Stirrup", chained),
                ("Collector", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(CollectorId, "Collector"),
                RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId), ("Collector", CollectorId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(fixture.Query("SELECT Id FROM QuestRequirements"));
    }

    [Fact]
    public async Task Collector_gets_one_prerequisite_per_kappa_quest_and_no_stale_ones()
    {
        // The published data carries a Collector prerequisite for Grenadier, a quest whose Kappa
        // flag is 0: the old synthesis only ever inserted. Rebuilding the set is what removes it,
        // so the run starts from a database that holds exactly that stale row.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Collector", RefreshPipelineFixture.Page()),
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Grenadier", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(CollectorId, "Collector", kappaRequired: true),
                RefreshPipelineFixture.Task(StirrupId, "Stirrup", kappaRequired: true),
                RefreshPipelineFixture.Task("5c0be13186f7746309d759c9", "Grenadier", kappaRequired: false))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Collector", CollectorId),
                ("Stirrup", StirrupId),
                ("Grenadier", "5c0be13186f7746309d759c9"))
            .WithQuestRequirement("Collector", "Grenadier");

        // The stale row is in the database before the run, which is what makes "no stale ones"
        // an assertion rather than a restatement of an empty table.
        var seeded = Assert.Single(fixture.Query("SELECT RequiredQuestId FROM QuestRequirements"));
        Assert.Equal(WikiQuestIdentity.IdFor("Grenadier"), seeded[0]);

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);

        var collectorRequirements = fixture.Query(
            "SELECT r.RequiredQuestId, r.GroupId FROM QuestRequirements r "
            + "JOIN Quests q ON q.Id = r.QuestId WHERE q.Name = 'Collector'");

        var required = Assert.Single(collectorRequirements);
        Assert.Equal(WikiQuestIdentity.IdFor("Stirrup"), required[0]);
        Assert.Equal("0", required[1]);
    }

    [Fact]
    public async Task A_quest_that_loses_its_kappa_flag_loses_its_collector_prerequisite()
    {
        // The synthesis used to run against the database after the write, where it could only
        // insert; a de-flagged quest kept its Collector row forever. Building the rows in memory
        // puts them through the same table-global diff as every other requirement.
        const string GrenadierId = "5c0be13186f7746309d759c9";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Collector", RefreshPipelineFixture.Page()),
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Grenadier", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(CollectorId, "Collector", kappaRequired: true),
                RefreshPipelineFixture.Task(StirrupId, "Stirrup", kappaRequired: true),
                RefreshPipelineFixture.Task(GrenadierId, "Grenadier", kappaRequired: true))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Collector", CollectorId), ("Stirrup", StirrupId), ("Grenadier", GrenadierId));

        Assert.True((await fixture.RefreshAsync()).Success);
        Assert.Equal(2, fixture.Query("SELECT Id FROM QuestRequirements").Count);

        // The game drops Grenadier from the Kappa set on the next publish.
        fixture.WithTasks(
            RefreshPipelineFixture.Task(CollectorId, "Collector", kappaRequired: true),
            RefreshPipelineFixture.Task(StirrupId, "Stirrup", kappaRequired: true),
            RefreshPipelineFixture.Task(GrenadierId, "Grenadier", kappaRequired: false));

        var second = await fixture.RefreshAsync();

        Assert.True(second.Success, second.ErrorMessage);
        var remaining = fixture.Query(
            "SELECT q.Name FROM QuestRequirements r JOIN Quests q ON q.Id = r.RequiredQuestId");
        Assert.Equal(new[] { "Stirrup" }, remaining.Select(r => r[0]));
    }

    [Fact]
    public async Task A_patch_that_shrinks_the_kappa_set_still_publishes()
    {
        // The counterpart to the two Kappa refusals, and the case that decides their shape.
        // Patch 1.1 really did take the Kappa requirement off almost every quest: the wiki
        // template stopped rendering reqkappa ("Remove quest Kappa requirement as part of
        // 1.1.0.0 task changes") and the task set flags 13 where the published database flags
        // 248. A proportional threshold on the drop would refuse exactly that regeneration, so
        // there is none. The set shrinking is a patch; the set emptying, or contradicting
        // Collector's own chain, is a defect.
        const string GrenadierId = "5c0be13186f7746309d759c9";
        const string HuntsmanId = "5c0be13186f7746309d759cb";
        const string ChemicalId = "5c0be13186f7746309d759ca";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Collector", RefreshPipelineFixture.Page()),
                ("Chemical - Part 3", RefreshPipelineFixture.Page()),
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Grenadier", RefreshPipelineFixture.Page()),
                ("The Huntsman Path - Control", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(
                    CollectorId, "Collector", kappaRequired: true,
                    requires: new[] { (ChemicalId, "complete") }),
                RefreshPipelineFixture.Task(
                    ChemicalId, "Chemical - Part 3", kappaRequired: true,
                    requires: new[] { (StirrupId, "complete") }),
                RefreshPipelineFixture.Task(StirrupId, "Stirrup", kappaRequired: true),
                RefreshPipelineFixture.Task(GrenadierId, "Grenadier", kappaRequired: true),
                RefreshPipelineFixture.Task(HuntsmanId, "The Huntsman Path - Control", kappaRequired: true))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Collector", CollectorId), ("Chemical - Part 3", ChemicalId), ("Stirrup", StirrupId),
                ("Grenadier", GrenadierId), ("The Huntsman Path - Control", HuntsmanId));

        Assert.True((await fixture.RefreshAsync()).Success);
        Assert.Equal(5, fixture.Query("SELECT Id FROM Quests WHERE KappaRequired = 1").Count);
        Assert.Equal(4, CollectorPrerequisites(fixture).Count);

        // The patch frees two of the five. Collector's own chain keeps its members, so the
        // shape still holds and only the volume moved.
        fixture.WithTasks(
            RefreshPipelineFixture.Task(
                CollectorId, "Collector", kappaRequired: true,
                requires: new[] { (ChemicalId, "complete") }),
            RefreshPipelineFixture.Task(
                ChemicalId, "Chemical - Part 3", kappaRequired: true,
                requires: new[] { (StirrupId, "complete") }),
            RefreshPipelineFixture.Task(StirrupId, "Stirrup", kappaRequired: true),
            RefreshPipelineFixture.Task(GrenadierId, "Grenadier"),
            RefreshPipelineFixture.Task(HuntsmanId, "The Huntsman Path - Control"));

        var second = await fixture.RefreshAsync();

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal(3, fixture.Query("SELECT Id FROM Quests WHERE KappaRequired = 1").Count);
        Assert.Equal(
            new[] { "Chemical - Part 3", "Stirrup" },
            CollectorPrerequisites(fixture).Select(r => r[0]));

        // Reported either way, both numbers on one line, because the diff report's row counts
        // are the only other place the change shows and only if a human reads them.
        Assert.Contains(fixture.ProgressMessages, m => m == "Kappa quests: 3 (the current database flags 5)");
    }

    private static List<string[]> CollectorPrerequisites(RefreshPipelineFixture fixture) =>
        fixture.Query(
            "SELECT required.Name FROM QuestRequirements r "
            + "JOIN Quests q ON q.Id = r.QuestId "
            + "JOIN Quests required ON required.Id = r.RequiredQuestId "
            + "WHERE q.Name = 'Collector' ORDER BY required.Name");

    [Fact]
    public async Task A_run_that_produces_no_prerequisites_at_all_leaves_the_table_alone()
    {
        // Deliberate, and shared with Quests and Objectives: an empty list is a parse or fetch
        // failure far more often than it is a game with no chains left, and emptying the table
        // publishes that failure to every build in the field. Individually disappearing rows
        // are still deleted by the diff inside the upsert, which the Collector test covers.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page()), ("Collector", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(CollectorId, "Collector"),
                RefreshPipelineFixture.Task(StirrupId, "Stirrup", requires: new[] { (CollectorId, "complete") }))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId), ("Collector", CollectorId));

        Assert.True((await fixture.RefreshAsync()).Success);
        Assert.Single(fixture.Query("SELECT Id FROM QuestRequirements"));

        fixture.WithTasks(
            RefreshPipelineFixture.Task(CollectorId, "Collector"),
            RefreshPipelineFixture.Task(StirrupId, "Stirrup"));

        Assert.True((await fixture.RefreshAsync()).Success);
        Assert.Single(fixture.Query("SELECT Id FROM QuestRequirements"));

        // Left alone, but never in silence: the run says which table it kept and how many rows
        // are now older than the data published beside them.
        Assert.Contains(
            fixture.ProgressMessages,
            m => m.StartsWith("QuestRequirements: the refresh produced no rows")
                 && m.Contains("its 1 existing rows"));
    }

    [Theory]
    // Ten 1.1 prerequisites name "active or complete". Accept is satisfied by an active AND by a
    // completed prerequisite, so this collapses onto one type with nothing lost and nothing left
    // over: an alternate type here would be a repeat the guard refuses.
    [InlineData(new[] { "active", "complete" }, "Accept", null)]
    [InlineData(new[] { "complete", "active" }, "Accept", null)]
    // Four name "complete or failed", which no single type covers, so the failure becomes the
    // second type rather than being dropped.
    [InlineData(new[] { "complete", "failed" }, "Complete", "Fail")]
    [InlineData(new[] { "failed", "complete" }, "Complete", "Fail")]
    // Not upstream's shape today, but the mapping is total: Accept subsumes complete and leaves
    // only the failure over, however many statuses named it.
    [InlineData(new[] { "active", "complete", "failed" }, "Accept", "Fail")]
    [InlineData(new[] { "active", "failed" }, "Accept", "Fail")]
    // A repeated status is one status.
    [InlineData(new[] { "complete", "complete" }, "Complete", null)]
    [InlineData(new[] { "complete" }, "Complete", null)]
    [InlineData(new[] { "active" }, "Accept", null)]
    [InlineData(new[] { "failed" }, "Fail", null)]
    // An entry with no status at all is the ordinary "must be completed".
    [InlineData(new string[0], "Complete", null)]
    public void A_prerequisite_that_names_several_statuses_keeps_every_one_the_app_can_read(
        string[] statuses, string expectedType, string? expectedAltType)
    {
        var mapped = RefreshDataService.MapRequirementStatuses(statuses, "Some Quest");

        Assert.Equal(expectedType, mapped.RequirementType);
        Assert.Equal(expectedAltType, mapped.AltRequirementType);
    }

    [Fact]
    public void An_unknown_prerequisite_status_fails_the_run_rather_than_locking_the_quest()
    {
        // The app has no reading for a status it does not know and treats the row as never
        // satisfied, so a widened upstream vocabulary has to stop the run, not reach the table.
        var error = Assert.Throws<InvalidOperationException>(
            () => RefreshDataService.MapRequirementStatuses(new[] { "complete", "abandoned" }, "Some Quest"));

        Assert.Contains("abandoned", error.Message);
        Assert.Contains("Some Quest", error.Message);
    }

    [Fact]
    public void The_requirement_types_are_declared_least_to_most_permissive()
    {
        // The declaration order is the precedence two separate rules apply: the statuses one
        // prerequisite names, and duplicate rows for one pair. Reordering the members silently
        // reverses both, and nothing else would fail.
        Assert.True(RefreshDataService.RequirementStatus.Fail < RefreshDataService.RequirementStatus.Complete);
        Assert.True(RefreshDataService.RequirementStatus.Complete < RefreshDataService.RequirementStatus.Accept);

        // The member names are the values the published column holds and the guard allows.
        Assert.Equal(
            new[] { "Fail", "Complete", "Accept" },
            Enum.GetValues<RefreshDataService.RequirementStatus>().Select(s => s.ToString()));
    }

    [Fact]
    public async Task A_prerequisite_naming_several_statuses_produces_one_row_not_a_key_collision()
    {
        // A requirement row's key is the (quest, prerequisite, group) triple, so one row per
        // status would collide on the primary key and take the whole refresh down.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Chemical - Part 3", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(StirrupId, "Stirrup"),
                MultiStatusTask("5c0be13186f7746309d759ca", "Chemical - Part 3", StirrupId, "complete", "active"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId), ("Chemical - Part 3", "5c0be13186f7746309d759ca"));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(fixture.Query("SELECT RequirementType, AltRequirementType FROM QuestRequirements"));
        Assert.Equal("Accept", row[0]);
        // Accept is satisfied by an active and by a completed prerequisite, so "complete or
        // active" has nothing left over to record.
        Assert.Equal("", row[1]);
    }

    #endregion

    #region Prerequisites a failure also satisfies

    // The 1.1 records behind these cases, by game id. Four prerequisites are satisfied by
    // "complete or failed", and two of them are failed by completing one specific other quest,
    // which is what upstream's own failConditions say and what makes an either-or of them.
    private const string BuildingFoundationsId = "673f629c5b555b53460cf827";
    private const string SwiftRetributionId = "6745fcded0fbbc74ca0f721d";
    private const string InevitableResponseId = "673f6027352b4da8e00322d2";
    private const string DangerousRoadId = "63ab180c87413d64ae0ac20a";
    private const string SupplyPlansId = "596a0e1686f7741ddf17dbee";
    private const string KindOfSabotageId = "596a101f86f7741ddb481582";
    private const string MakeAmendsBuyoutId = "626148251ed3bb5bcc5bd9ed";
    private const string GettingAcquaintedId = "625d700cc48e6c62a440fab5";
    private const string ProtectTheSkyId = "6744ab1def61d56e020b5c56";

    // Two records, one wiki page (Battery_Change). The first is Protect the Sky's prerequisite;
    // the second is what fails it and is the one the resolver leaves without a page.
    private const string BatteryChangeId = "6744a728352b4da8e003eda9";
    private const string BatteryChangeTwinId = "6744a9dfef61d56e020b5c4a";

    // A second quest to be failed by, for the ambiguity case; nothing fails Swift Retribution
    // twice in 1.1.
    private const string OrderFromOutsideId = "673f61a066e6a521aa04b62b";

    /// <summary>Every prerequisite row, by quest name, prerequisite name, both types and group.</summary>
    private static List<string[]> PrerequisiteRows(RefreshPipelineFixture fixture) =>
        fixture.Query(
            "SELECT q.Name, required.Name, r.RequirementType, r.AltRequirementType, r.GroupId "
            + "FROM QuestRequirements r "
            + "JOIN Quests q ON q.Id = r.QuestId "
            + "JOIN Quests required ON required.Id = r.RequiredQuestId "
            + "ORDER BY q.Name, r.GroupId, required.Name");

    [Theory]
    // Both pairs, exactly as data/v1/tarkov_data.db ships them today: one OR group, both
    // branches, RequirementType 'Complete' on each. The API reports only the first branch, so
    // without this expansion the refresh would replace a working OR group with a single AND row
    // and lock the quest for every player who took the other branch.
    [InlineData(
        "Building Foundations", BuildingFoundationsId,
        "Swift Retribution", SwiftRetributionId,
        "Inevitable Response", InevitableResponseId)]
    [InlineData(
        "Dangerous Road", DangerousRoadId,
        "Supply Plans", SupplyPlansId,
        "Kind of Sabotage", KindOfSabotageId)]
    public async Task A_prerequisite_a_failure_satisfies_becomes_the_or_group_the_published_database_ships(
        string questName, string questId,
        string prerequisiteName, string prerequisiteId,
        string twinName, string twinId)
    {
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                (questName, RefreshPipelineFixture.Page()),
                (prerequisiteName, RefreshPipelineFixture.Page()),
                (twinName, RefreshPipelineFixture.Page()))
            .WithTasks(
                MultiStatusTask(questId, questName, prerequisiteId, "complete", "failed"),
                // The twin is named nowhere but here: upstream's own fail condition on the
                // prerequisite, which is what the refresh derives the pair from.
                RefreshPipelineFixture.Task(
                    prerequisiteId,
                    prerequisiteName,
                    failedBy: new[] { RefreshPipelineFixture.FailedByCompleting(twinId) }),
                RefreshPipelineFixture.Task(twinId, twinName))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase((questName, questId), (prerequisiteName, prerequisiteId), (twinName, twinId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var rows = PrerequisiteRows(fixture);
        Assert.Equal(2, rows.Count);

        // The prerequisite the game names, in a group of its own rather than as an AND term,
        // carrying the failure as the second type a newer build also reads.
        var named = Assert.Single(rows, r => r[1] == prerequisiteName);
        Assert.Equal(new[] { questName, prerequisiteName, "Complete", "Fail", "1" }, named);

        // The quest whose completion fails it, at Complete: the branch every build in the field
        // can already read, and the one the published database ships today.
        var twin = Assert.Single(rows, r => r[1] == twinName);
        Assert.Equal(new[] { questName, twinName, "Complete", "", "1" }, twin);
    }

    [Fact]
    public async Task The_column_arrives_on_a_database_published_before_it_existed()
    {
        // The fixture's database is shaped like a published one, which has no
        // AltRequirementType: CREATE TABLE IF NOT EXISTS does nothing to it, so the column can
        // only arrive through the PRAGMA-guarded ALTER.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Building Foundations", RefreshPipelineFixture.Page()),
                ("Swift Retribution", RefreshPipelineFixture.Page()),
                ("Inevitable Response", RefreshPipelineFixture.Page()))
            .WithTasks(
                MultiStatusTask(BuildingFoundationsId, "Building Foundations", SwiftRetributionId, "complete", "failed"),
                RefreshPipelineFixture.Task(
                    SwiftRetributionId,
                    "Swift Retribution",
                    failedBy: new[] { RefreshPipelineFixture.FailedByCompleting(InevitableResponseId) }),
                RefreshPipelineFixture.Task(InevitableResponseId, "Inevitable Response"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Building Foundations", BuildingFoundationsId),
                ("Swift Retribution", SwiftRetributionId),
                ("Inevitable Response", InevitableResponseId));

        Assert.Empty(fixture.Query(
            "SELECT name FROM pragma_table_info('QuestRequirements') WHERE name = 'AltRequirementType'"));

        Assert.True((await fixture.RefreshAsync()).Success);

        Assert.Single(fixture.Query(
            "SELECT name FROM pragma_table_info('QuestRequirements') WHERE name = 'AltRequirementType'"));

        // And a second run over the database the first one widened neither fails nor duplicates
        // the column.
        Assert.True((await fixture.RefreshAsync()).Success);
        Assert.Single(fixture.Query(
            "SELECT name FROM pragma_table_info('QuestRequirements') WHERE name = 'AltRequirementType'"));
    }

    [Fact]
    public async Task A_prerequisite_a_failure_satisfies_with_no_exclusive_quest_stays_one_row_and_is_named()
    {
        // Getting Acquainted fails on a Lightkeeper trader standing rather than on another quest
        // completing, so there is no second branch to write and no OR group to build. Its
        // failConditions are exactly what upstream serves: one traderStanding entry, no
        // taskStatus. The row records the failure in the second column, which only a build that
        // updates reads, and the run says so by name rather than leaving the over-lock to be
        // discovered.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Make Amends - Buyout", RefreshPipelineFixture.Page()),
                ("Getting Acquainted", RefreshPipelineFixture.Page()))
            .WithTasks(
                MultiStatusTask(MakeAmendsBuyoutId, "Make Amends - Buyout", GettingAcquaintedId, "complete", "failed"),
                RefreshPipelineFixture.Task(
                    GettingAcquaintedId,
                    "Getting Acquainted",
                    failedBy: new[] { ("traderStanding", (string?)null, Array.Empty<string>()) }))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Make Amends - Buyout", MakeAmendsBuyoutId),
                ("Getting Acquainted", GettingAcquaintedId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(PrerequisiteRows(fixture));
        Assert.Equal(
            new[] { "Make Amends - Buyout", "Getting Acquainted", "Complete", "Fail", "0" }, row);

        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains("Make Amends - Buyout <- Getting Acquainted (complete or failed -> Complete or Fail)"));
        // Named with what does fail it, which the hand-transcribed table it replaced could only
        // say by omission.
        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains("older builds keep the quest locked")
                 && m.Contains("Make Amends - Buyout <- Getting Acquainted "
                               + "(nothing that fails it is another quest completing "
                               + "(it is failed by: traderStanding))"));
    }

    [Fact]
    public async Task A_prerequisite_failed_by_a_record_this_run_did_not_import_stays_one_row_and_names_it()
    {
        // Protect the Sky, exactly as 1.1 serves it. Battery Change is satisfied by "complete or
        // failed" and IS failed by a quest completing, but that quest is a second Battery Change
        // record sharing the same wiki page, which QuestIdentityResolver.Claim gives to one of
        // the two. The loser becomes no row, so the OR group would have a foreign key pointing
        // at nothing. The derivation finds the twin and the import check refuses it, which is a
        // different refusal from "nothing fails it" and reads as one.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Protect the Sky", RefreshPipelineFixture.Page()),
                ("Battery Change", RefreshPipelineFixture.Page()))
            .WithTasks(
                MultiStatusTask(ProtectTheSkyId, "Protect the Sky", BatteryChangeId, "complete", "failed"),
                RefreshPipelineFixture.Task(
                    BatteryChangeId,
                    "Battery Change",
                    failedBy: new[] { RefreshPipelineFixture.FailedByCompleting(BatteryChangeTwinId) }))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Protect the Sky", ProtectTheSkyId), ("Battery Change", BatteryChangeId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(PrerequisiteRows(fixture));
        Assert.Equal(new[] { "Protect the Sky", "Battery Change", "Complete", "Fail", "0" }, row);

        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains($"the quest that fails it ({BatteryChangeTwinId}) was not imported by this run"));
    }

    [Fact]
    public async Task A_prerequisite_two_quests_can_fail_is_not_expanded_onto_either_of_them()
    {
        // Twelve 1.1 tasks are failed by two different quests each. None of them is a "complete
        // or failed" prerequisite today, but the derivation has to refuse rather than take the
        // first: an OR group naming one of two exclusive quests says something the game does not,
        // and it would lock the player who took the third branch.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Building Foundations", RefreshPipelineFixture.Page()),
                ("Swift Retribution", RefreshPipelineFixture.Page()),
                ("Inevitable Response", RefreshPipelineFixture.Page()),
                ("Order From Outside", RefreshPipelineFixture.Page()))
            .WithTasks(
                MultiStatusTask(BuildingFoundationsId, "Building Foundations", SwiftRetributionId, "complete", "failed"),
                RefreshPipelineFixture.Task(
                    SwiftRetributionId,
                    "Swift Retribution",
                    failedBy: new[]
                    {
                        RefreshPipelineFixture.FailedByCompleting(InevitableResponseId),
                        RefreshPipelineFixture.FailedByCompleting(OrderFromOutsideId),
                    }),
                RefreshPipelineFixture.Task(InevitableResponseId, "Inevitable Response"),
                RefreshPipelineFixture.Task(OrderFromOutsideId, "Order From Outside"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Building Foundations", BuildingFoundationsId),
                ("Swift Retribution", SwiftRetributionId),
                ("Inevitable Response", InevitableResponseId),
                ("Order From Outside", OrderFromOutsideId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(PrerequisiteRows(fixture));
        Assert.Equal(new[] { "Building Foundations", "Swift Retribution", "Complete", "Fail", "0" }, row);

        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains("the game records 2 different quests as failing it")
                 && m.Contains(InevitableResponseId)
                 && m.Contains(OrderFromOutsideId));
    }

    [Fact]
    public async Task A_prerequisite_failed_by_another_quest_merely_starting_is_not_expanded()
    {
        // The status test the 1.1 capture cannot exercise: all 35 taskStatus fail conditions
        // upstream serves read ["complete"]. A condition on the twin being merely started does
        // not make the two exclusive - both can be active at once - so "prerequisite failed" and
        // "twin complete" would not be the same state and the OR group would be a lie.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Building Foundations", RefreshPipelineFixture.Page()),
                ("Swift Retribution", RefreshPipelineFixture.Page()),
                ("Inevitable Response", RefreshPipelineFixture.Page()))
            .WithTasks(
                MultiStatusTask(BuildingFoundationsId, "Building Foundations", SwiftRetributionId, "complete", "failed"),
                RefreshPipelineFixture.Task(
                    SwiftRetributionId,
                    "Swift Retribution",
                    failedBy: new[] { ("taskStatus", (string?)InevitableResponseId, new[] { "active" }) }),
                RefreshPipelineFixture.Task(InevitableResponseId, "Inevitable Response"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Building Foundations", BuildingFoundationsId),
                ("Swift Retribution", SwiftRetributionId),
                ("Inevitable Response", InevitableResponseId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(PrerequisiteRows(fixture));
        Assert.Equal(new[] { "Building Foundations", "Swift Retribution", "Complete", "Fail", "0" }, row);

        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains("nothing that fails it is another quest completing (it is failed by: taskStatus)"));
    }

    [Fact]
    public async Task A_task_cache_written_before_fail_conditions_were_carried_still_runs()
    {
        // The cache file shape changes with this field, and the editor keeps its caches on disk
        // between runs. A file written by an older build has no failConditions key at all, which
        // has to read back as "nothing fails it" rather than throw: the run then publishes the
        // bare AND row it always did and names the prerequisite it could not expand, and
        // 'Debug > Cache Tarkov Dev Data' refills the field.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Building Foundations", RefreshPipelineFixture.Page()),
                ("Swift Retribution", RefreshPipelineFixture.Page()),
                ("Inevitable Response", RefreshPipelineFixture.Page()))
            .WithTasks(
                MultiStatusTask(BuildingFoundationsId, "Building Foundations", SwiftRetributionId, "complete", "failed"),
                RefreshPipelineFixture.Task(
                    SwiftRetributionId,
                    "Swift Retribution",
                    failedBy: new[] { RefreshPipelineFixture.FailedByCompleting(InevitableResponseId) }),
                RefreshPipelineFixture.Task(InevitableResponseId, "Inevitable Response"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Building Foundations", BuildingFoundationsId),
                ("Swift Retribution", SwiftRetributionId),
                ("Inevitable Response", InevitableResponseId));

        // Guard the guard: with the field present this run does build the OR group, so the
        // assertions below are about its absence and nothing else.
        Assert.True((await fixture.RefreshAsync()).Success);
        Assert.Equal(2, PrerequisiteRows(fixture).Count);

        RewriteFailConditionsInTheTaskCache(fixture, absent: true);
        Assert.DoesNotContain("failConditions", File.ReadAllText(fixture.TaskCachePath));
        fixture.ProgressMessages.Clear();

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(PrerequisiteRows(fixture));
        Assert.Equal(new[] { "Building Foundations", "Swift Retribution", "Complete", "Fail", "0" }, row);

        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains("the game records nothing as failing it")
                 && m.Contains("written before failConditions were carried"));

        // And the same for an explicit null, which a hand-edited cache file can hold and which
        // the derivation would dereference.
        RewriteFailConditionsInTheTaskCache(fixture, absent: false);
        Assert.Contains("\"failConditions\":null", File.ReadAllText(fixture.TaskCachePath));

        var third = await fixture.RefreshAsync();

        Assert.True(third.Success, third.ErrorMessage);
        Assert.Single(PrerequisiteRows(fixture));
    }

    [Fact]
    public async Task A_task_cache_holding_null_where_a_list_belongs_reads_as_empty_rather_than_crashing()
    {
        // JSON null is not the same absence as a missing key: it deserializes past every check
        // the loader makes and lands as a null field, which the pipeline then enumerates. The
        // task model's list properties absorb it, so the worst a damaged or hand-edited cache
        // can do is describe a quest with no gates and no prerequisites.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Building Foundations", RefreshPipelineFixture.Page()),
                ("Swift Retribution", RefreshPipelineFixture.Page()))
            .WithTasks(
                MultiStatusTask(BuildingFoundationsId, "Building Foundations", SwiftRetributionId, "complete", "failed"),
                RefreshPipelineFixture.Task(
                    SwiftRetributionId,
                    "Swift Retribution",
                    loyalty: new[] { (PraporId, 2) },
                    failedBy: new[] { RefreshPipelineFixture.FailedByCompleting(InevitableResponseId) }))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Building Foundations", BuildingFoundationsId),
                ("Swift Retribution", SwiftRetributionId));

        NullEveryListInTheTaskCache(fixture);

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(fixture.Query("SELECT Id FROM QuestRequirements"));
        // The loyalty table is created only when there is a gate to write, so its absence is
        // the "no gates arrived" reading here.
        Assert.False(fixture.TableExists("QuestTraderRequirements"));
        Assert.Equal(2, fixture.Query("SELECT Id FROM Quests").Count);
    }

    /// <summary>
    /// Rewrites the task cache the way a build that predates the field would have left it, with
    /// every <c>failConditions</c> property removed (<paramref name="absent"/>) or set to null.
    /// </summary>
    private static void RewriteFailConditionsInTheTaskCache(RefreshPipelineFixture fixture, bool absent)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.TaskCachePath))!;
        foreach (var quest in root["quests"]!.AsArray())
        {
            var task = quest!.AsObject();
            task.Remove("failConditions");
            if (!absent)
                task["failConditions"] = null;
        }

        File.WriteAllText(fixture.TaskCachePath, root.ToJsonString());
    }

    /// <summary>Sets every collection a task carries to JSON null, keeping the file valid JSON.</summary>
    private static void NullEveryListInTheTaskCache(RefreshPipelineFixture fixture)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.TaskCachePath))!;
        foreach (var quest in root["quests"]!.AsArray())
        {
            var task = quest!.AsObject();
            foreach (var field in new[] { "traderLevelRequirements", "taskRequirements", "failConditions" })
            {
                task.Remove(field);
                task[field] = null;
            }
        }

        File.WriteAllText(fixture.TaskCachePath, root.ToJsonString());
    }

    [Fact]
    public async Task The_or_group_is_not_built_when_the_quest_that_fails_the_prerequisite_was_not_imported()
    {
        // A row pointing at a quest this run did not import has no foreign key to satisfy, so
        // the expansion is refused rather than half-written, and the run names the quest it
        // could not reach.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Building Foundations", RefreshPipelineFixture.Page()),
                ("Swift Retribution", RefreshPipelineFixture.Page()))
            .WithTasks(
                MultiStatusTask(BuildingFoundationsId, "Building Foundations", SwiftRetributionId, "complete", "failed"),
                RefreshPipelineFixture.Task(
                    SwiftRetributionId,
                    "Swift Retribution",
                    failedBy: new[] { RefreshPipelineFixture.FailedByCompleting(InevitableResponseId) }))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Building Foundations", BuildingFoundationsId),
                ("Swift Retribution", SwiftRetributionId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var row = Assert.Single(PrerequisiteRows(fixture));
        Assert.Equal(new[] { "Building Foundations", "Swift Retribution", "Complete", "Fail", "0" }, row);

        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains($"({InevitableResponseId}) was not imported by this run"));
    }

    [Fact]
    public async Task The_or_group_is_not_built_when_the_quest_already_requires_the_other_branch()
    {
        // The shape the fielded reader cannot hold: it keys requirement rows by prerequisite
        // alone, so a quest that already requires Inevitable Response would keep that row and
        // drop the OR group's copy of it, leaving Swift Retribution alone in a group nothing
        // satisfies. Both rows stay AND terms instead, which is what the game says anyway.
        var quest = RefreshPipelineFixture.Task(BuildingFoundationsId, "Building Foundations");
        quest.TaskRequirements = new List<TarkovDevTaskPrerequisite>
        {
            new() { TaskId = SwiftRetributionId, Status = new List<string> { "complete", "failed" } },
            new() { TaskId = InevitableResponseId, Status = new List<string> { "complete" } },
        };

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Building Foundations", RefreshPipelineFixture.Page()),
                ("Swift Retribution", RefreshPipelineFixture.Page()),
                ("Inevitable Response", RefreshPipelineFixture.Page()))
            .WithTasks(
                quest,
                RefreshPipelineFixture.Task(
                    SwiftRetributionId,
                    "Swift Retribution",
                    failedBy: new[] { RefreshPipelineFixture.FailedByCompleting(InevitableResponseId) }),
                RefreshPipelineFixture.Task(InevitableResponseId, "Inevitable Response"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Building Foundations", BuildingFoundationsId),
                ("Swift Retribution", SwiftRetributionId),
                ("Inevitable Response", InevitableResponseId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var rows = PrerequisiteRows(fixture);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("0", r[4]));
        Assert.Equal(
            new[] { "Building Foundations", "Inevitable Response", "Complete", "", "0" },
            Assert.Single(rows, r => r[1] == "Inevitable Response"));
        Assert.Equal(
            new[] { "Building Foundations", "Swift Retribution", "Complete", "Fail", "0" },
            Assert.Single(rows, r => r[1] == "Swift Retribution"));

        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains("already a prerequisite, which the fielded reader would collapse onto one row"));
    }

    [Fact]
    public async Task A_second_row_for_one_quest_and_prerequisite_is_refused_before_the_write()
    {
        // The fielded reader keeps the first row for a quest/prerequisite pair and drops every
        // later one, group and all, so a pair with two rows publishes as one and which one it is
        // comes down to row order. A seasonal wiki page naming the same prerequisite twice is
        // the one source that can still produce it: the game data cannot, and the exclusive-pair
        // expansion refuses to.
        var page = RefreshPipelineFixture.SeasonalPage() + "\n|previous = [[Stirrup]]<br/>[[Stirrup]]\n";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Uninvited Guests - Part 1", page))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.False(result.Success);
        Assert.Contains("have more than one row", result.ErrorMessage);
        Assert.Contains("groups 1, 2", result.ErrorMessage);
    }

    [Fact]
    public void An_alternate_requirement_type_the_app_cannot_read_is_refused_before_the_write()
    {
        // The second column reaches the app as a second entry in the same status list and is read
        // by the same code, so it needs the same allow-list. Reached directly because the mapper
        // cannot produce a value outside it: the guard is what has to hold if a later source can.
        var refused = Assert.Throws<InvalidOperationException>(() => AssertPublishConstraints(
            new DbQuestRequirement
            {
                QuestId = "quest-1",
                RequiredQuestId = "quest-2",
                RequirementType = "Complete",
                AltRequirementType = "Abandoned",
            }));

        Assert.Contains("AltRequirementType outside {NULL, Complete, Accept, Fail}", refused.Message);
        Assert.Contains("Abandoned", refused.Message);
    }

    [Fact]
    public void An_alternate_requirement_type_that_repeats_the_first_is_refused_before_the_write()
    {
        // Not a second way to satisfy the row, just a mapping that lost track of what the first
        // type already covers. Cheap to catch here and invisible in the published data otherwise.
        var refused = Assert.Throws<InvalidOperationException>(() => AssertPublishConstraints(
            new DbQuestRequirement
            {
                QuestId = "quest-1",
                RequiredQuestId = "quest-2",
                RequirementType = "Complete",
                AltRequirementType = "Complete",
            }));

        Assert.Contains("AltRequirementType repeats RequirementType", refused.Message);
    }

    [Fact]
    public void A_row_with_no_alternate_requirement_type_satisfies_the_guard()
    {
        // The other half of the two facts above: the guard has to pass the shape almost every
        // published row has, or the two would be asserting nothing about the allow-list.
        AssertPublishConstraints(new DbQuestRequirement
        {
            QuestId = "quest-1",
            RequiredQuestId = "quest-2",
            RequirementType = "Complete",
            AltRequirementType = null,
        });
    }

    [Fact]
    public void A_row_naming_its_own_quest_as_its_prerequisite_is_refused_before_the_write()
    {
        // Every path that builds a row drops a self-reference already (the game's records and
        // the wiki's |previous field alike), so this is unreachable from the sources the run
        // reads today. The guard is what has to hold if a later one produces it, and it is the
        // same declaration the publish gate runs over the candidate file, where a hand edit can
        // still write one. A quest that requires itself is locked forever in every install.
        var refused = Assert.Throws<InvalidOperationException>(() => AssertPublishConstraints(
            new DbQuestRequirement
            {
                QuestId = "quest-1",
                RequiredQuestId = "quest-1",
                RequirementType = "Complete",
            }));

        Assert.Contains("are their own prerequisite", refused.Message);
        Assert.Contains("quest-1", refused.Message);
    }

    /// <summary>
    /// Runs the publish guard over a requirements-only result. <c>RefreshGuards</c> is internal
    /// to TarkovDBEditor and the assembly grants no InternalsVisibleTo, so the guard is reached
    /// the way <see cref="TestReflection"/> reaches a private field: by reflection, asserting the
    /// member exists so a rename fails loudly instead of turning the fact into a no-op.
    /// </summary>
    private static void AssertPublishConstraints(params DbQuestRequirement[] requirements)
    {
        var guards = typeof(RefreshDataService).GetNestedType(
            "RefreshGuards", System.Reflection.BindingFlags.NonPublic);
        Assert.True(guards != null, "RefreshDataService has no nested type 'RefreshGuards'");

        var method = guards!.GetMethod(
            "AssertPublishConstraints",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.True(method != null, "RefreshGuards has no public static AssertPublishConstraints");

        var result = new QuestsFetchResult();
        result.Requirements.AddRange(requirements);

        try
        {
            method!.Invoke(null, new object?[] { result, null });
        }
        catch (System.Reflection.TargetInvocationException invocation) when (invocation.InnerException != null)
        {
            // Reflection wraps whatever the guard threw; the tests assert on the guard's own
            // message, not on the wrapper.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo
                .Capture(invocation.InnerException).Throw();
        }
    }

    #endregion

    #region Collector

    [Fact]
    public async Task Collectors_own_game_prerequisites_do_not_duplicate_its_synthesized_ones()
    {
        // The API gives Collector five prerequisites of its own, and all five are already in the
        // Kappa set the synthesis builds. Taking both would write the same row twice.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Collector", RefreshPipelineFixture.Page()),
                ("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(
                    CollectorId, "Collector", kappaRequired: true,
                    requires: new[] { (StirrupId, "complete") }),
                RefreshPipelineFixture.Task(StirrupId, "Stirrup", kappaRequired: true))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Collector", CollectorId), ("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var rows = fixture.Query(
            "SELECT q.Name, r.RequiredQuestId FROM QuestRequirements r JOIN Quests q ON q.Id = r.QuestId");
        var row = Assert.Single(rows);
        Assert.Equal("Collector", row[0]);
        Assert.Equal(WikiQuestIdentity.IdFor("Stirrup"), row[1]);
    }

    [Fact]
    public async Task Collector_is_recognised_by_its_game_id_after_a_rename()
    {
        // Collector used to be recognised by its title alone, at both the site that skips its
        // API prerequisites and the site that synthesizes the Kappa set, so a rename flipped
        // both at once: the synthesis found nothing while the API's own list started shipping.
        //
        // The two sites are told apart by the requirement type. Collector's record names
        // Grenadier with status "active", which BuildRequirements maps to Accept, while the
        // synthesis writes Complete for every Kappa quest. Both rows carry the same key, so a
        // duplicate collapses onto the more permissive Accept: reading Complete back is proof
        // the API's list was skipped and the Kappa set owns the list, under the new title.
        //
        // (The membership itself is no longer free to differ: AssertCollectorsChainIsInTheKappaSet
        // refuses a run where a quest Collector's record requires is outside the Kappa set,
        // because that prerequisite would reach no row at all.)
        const string GrenadierId = "5c0be13186f7746309d759c9";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("The Collector", RefreshPipelineFixture.Page()),
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Grenadier", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(
                    CollectorId, "The Collector", kappaRequired: true,
                    requires: new[] { (GrenadierId, "active") }),
                RefreshPipelineFixture.Task(StirrupId, "Stirrup", kappaRequired: true),
                RefreshPipelineFixture.Task(GrenadierId, "Grenadier", kappaRequired: true))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Collector", CollectorId), ("Stirrup", StirrupId), ("Grenadier", GrenadierId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);

        var rows = fixture.Query(
            "SELECT required.Name, r.RequirementType FROM QuestRequirements r "
            + "JOIN Quests q ON q.Id = r.QuestId "
            + "JOIN Quests required ON required.Id = r.RequiredQuestId "
            + "WHERE q.Name = 'The Collector' ORDER BY required.Name");

        Assert.Equal(new[] { "Grenadier", "Stirrup" }, rows.Select(r => r[0]));
        // Complete, not Accept: the row came from the Kappa set, not from Collector's own record.
        Assert.Equal(new[] { "Complete", "Complete" }, rows.Select(r => r[1]));
    }

    [Fact]
    public async Task A_wiki_or_group_the_game_flattens_is_named_by_the_run()
    {
        // The game has no OR groups, so both alternatives ship as AND terms and the quest is
        // locked until both are done. The disagreement list cannot show it: the two sets hold
        // the same quests, so it reads "agree" while the meaning changed. The run names them.
        const string ChemicalId = "5c0be13186f7746309d759ca";
        var alternatives = RefreshPipelineFixture.Page()
            + "\n|previous = [[Stirrup]]<br/>or<br/>[[Collector]]\n";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Chemical - Part 3", alternatives),
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Collector", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(
                    ChemicalId, "Chemical - Part 3",
                    requires: new[] { (StirrupId, "complete"), (CollectorId, "complete") }),
                RefreshPipelineFixture.Task(StirrupId, "Stirrup"),
                RefreshPipelineFixture.Task(CollectorId, "Collector"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Chemical - Part 3", ChemicalId), ("Stirrup", StirrupId), ("Collector", CollectorId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);

        var groups = fixture.Query("SELECT GroupId FROM QuestRequirements").Select(r => r[0]).ToList();
        Assert.Equal(new[] { "0", "0" }, groups);
        Assert.Contains(
            fixture.ProgressMessages,
            m => m.Contains("alternative prerequisites") && m.Contains("Chemical - Part 3"));
    }

    [Fact]
    public async Task A_page_naming_itself_as_its_own_prerequisite_writes_no_row()
    {
        // Collector's own page does exactly this, and a self-reference is a quest locked
        // forever in every install: nothing downstream checks for one.
        var selfReferencing = RefreshPipelineFixture.SeasonalPage()
            + "\n|previous = [[Uninvited Guests - Part 1]]\n";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Uninvited Guests - Part 1", selfReferencing))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(fixture.Query("SELECT Id FROM QuestRequirements"));
    }

    [Fact]
    public async Task A_game_record_naming_itself_as_its_own_prerequisite_writes_no_row()
    {
        // The same drop on the other branch. The wiki branch above has Collector's page behind
        // it, but the game's own task list is a second source for the identical row, and a
        // self-reference from it locks the quest forever in every install just as hard.
        // Nothing upstream carries one today, which is exactly why only a test keeps the guard
        // honest.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(
                StirrupId, "Stirrup", requires: new[] { (StirrupId, "complete") }))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(fixture.Query("SELECT Id FROM QuestRequirements"));
    }

    [Fact]
    public async Task Two_loyalty_gates_naming_one_trader_collapse_instead_of_colliding()
    {
        // A loyalty row's key is the (quest, trader) pair, so a record naming one trader twice
        // used to insert the same key twice and abort the whole regeneration. The lower level
        // wins, for the reason the duplicate prerequisites collapse to the most permissive
        // type: a quest shown slightly early is a smaller harm than one gated too high.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Chemical - Part 3", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(
                StirrupId, "Chemical - Part 3", loyalty: new[] { (JaegerId, 3), (JaegerId, 2) }))
            .WithTraders((PraporId, "Prapor"), (JaegerId, "Jaeger"))
            .WithDatabase(("Chemical - Part 3", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        var gate = Assert.Single(fixture.Query(
            "SELECT TraderName, RequiredLevel FROM QuestTraderRequirements"));
        Assert.Equal(new[] { "Jaeger", "2" }, gate);
    }

    [Fact]
    public async Task A_record_whose_wiki_link_points_elsewhere_still_matches_by_normalized_name()
    {
        // The resolver's second pass. Every fixture task carried the page's own link until the
        // builder took an override, which left this pass and the alias pass below unreached.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(
                StirrupId, "Stirrup",
                wikiLink: "https://escapefromtarkov.fandom.com/wiki/Steigb%C3%BCgel"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(StirrupId, fixture.ReadQuestColumn("BsgId")["Stirrup"]);
    }

    [Fact]
    public async Task A_record_the_alias_list_names_matches_a_page_no_other_pass_can_reach()
    {
        // The resolver's third pass, driven by the committed quest-match-overrides.json: the
        // three prestige records link to the German title Neuanfang, which is not a page, and
        // their normalizedName does not spell the page's title either.
        const string PrestigeTwoId = "6761ff17cdc36bd66102e9d0";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("New Beginning (Prestige 2)", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(StirrupId, "Stirrup"),
                RefreshPipelineFixture.Task(
                    PrestigeTwoId, "New Beginning",
                    wikiLink: "https://escapefromtarkov.fandom.com/wiki/Neuanfang",
                    normalizedName: "new-beginning-2"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(PrestigeTwoId, fixture.ReadQuestColumn("BsgId")["New Beginning (Prestige 2)"]);
    }

    [Fact]
    public async Task A_seasonal_page_is_imported_without_a_game_record()
    {
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Uninvited Guests - Part 1", RefreshPipelineFixture.SeasonalPage()))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);

        var bsgIds = fixture.ReadQuestColumn("BsgId");
        Assert.Equal(StirrupId, bsgIds["Stirrup"]);
        Assert.Null(bsgIds["Uninvited Guests - Part 1"]);
        // The wiki's own parser fills in what the game would have said.
        Assert.Equal("Prapor", fixture.ReadQuestColumn("Trader")["Uninvited Guests - Part 1"]);
    }

    [Fact]
    public async Task A_page_with_no_game_record_is_left_out_of_the_database()
    {
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Arena: First Blood", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(StirrupId, "Stirrup"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { "Stirrup" }, fixture.ReadQuestColumn("Name").Keys);
    }

    [Fact]
    public async Task The_run_names_every_previous_row_no_imported_quest_kept()
    {
        // Eighteen seasonal quests out of 488 is 3.7%, permanently under the 5% row-key
        // threshold, so the guard reports and returns. Without a line per row the abandonment
        // reaches no durable artefact at all, and those rows are exactly the ones whose recorded
        // progress the write orphans in every install.
        var pages = new List<(string, string)>();
        var tasks = new List<TarkovDevQuestCacheItem>();
        var rows = new List<(string, string?)>();

        for (var i = 0; i < 24; i++)
        {
            var title = $"Filler Quest {i}";
            var taskId = $"5c0be13186f7746309d7{i:d4}";
            pages.Add((title, RefreshPipelineFixture.Page()));
            tasks.Add(RefreshPipelineFixture.Task(taskId, title));
            rows.Add((title, taskId));
        }

        // The seasonal quest came back under a new title with no game record, so nothing in a
        // run can tie it to the row it was published under, and that row is deleted.
        pages.Add(("Uninvited Guests - Part 1", RefreshPipelineFixture.SeasonalPage()));
        rows.Add(("KORD Breach - Part 1", null));

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(pages.ToArray())
            .WithTasks(tasks.ToArray())
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(rows.ToArray());

        var result = await fixture.RefreshAsync();

        // One orphan in twenty five is 4%: under the threshold, so the run publishes.
        Assert.True(result.Success, result.ErrorMessage);
        Assert.DoesNotContain("KORD Breach - Part 1", fixture.ReadQuestColumn("Name").Keys);

        var log = File.ReadAllText(result.LogPath!);
        Assert.Contains("[ROW ABANDONED] 'KORD Breach - Part 1'", log);
        Assert.Contains("no external ID, so nothing in a run could carry it", log);

        // And in the machine-readable log the diff report reads, not only the human one.
        var jsonLog = File.ReadAllText(Assert.Single(
            Directory.GetFiles(Path.Combine(fixture.BasePath, "wiki_data", "logs"), "refresh_*.json")));
        Assert.Contains("uncarriedPreviousRows", jsonLog);
        Assert.Contains("KORD Breach - Part 1", jsonLog);
    }

    [Fact]
    public async Task A_prerequisite_whose_target_was_not_imported_is_named_not_dropped()
    {
        // The row cannot be written: the foreign key has nothing to point at. Dropping it in
        // silence ships the quest with a shorter chain than the game enforces, and the app then
        // offers it to a player who has not met the real precondition. The disagreement list is
        // no substitute, because it filters the game's side through the same lookup.
        const string RemovedTaskId = "5c0be13186f7746309d759ff";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Stirrup", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(
                    StirrupId, "Stirrup", requires: new[] { (RemovedTaskId, "complete") }),
                // A record the API still lists with no page behind it: one of the 35 quests 1.1
                // removed.
                RefreshPipelineFixture.Task(RemovedTaskId, "Dressed to Kill"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId));

        var result = await fixture.RefreshAsync();

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(fixture.Query("SELECT Id FROM QuestRequirements"));

        var log = File.ReadAllText(result.LogPath!);
        Assert.Contains("[PREREQUISITE STRANDED]", log);
        Assert.Contains("'Stirrup' requires task " + RemovedTaskId, log);
        Assert.Contains("matched no wiki page this run imported", log);

        var jsonLog = File.ReadAllText(Assert.Single(
            Directory.GetFiles(Path.Combine(fixture.BasePath, "wiki_data", "logs"), "refresh_*.json")));
        Assert.Contains("strandedPrerequisites", jsonLog);
        Assert.Contains(RemovedTaskId, jsonLog);
    }

    [Fact]
    public async Task A_loyalty_gate_that_loses_its_approval_says_so_in_the_log()
    {
        // The refresh log is the artefact a regeneration is reviewed against, and a reviewer
        // reads it to find what they have to look at again. Four of the other child tables say
        // when an approved row's content changed and its approval was dropped; the loyalty table
        // is the newest copy of that loop and it left the line out.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(("Chemical - Part 3", RefreshPipelineFixture.Page()))
            .WithTasks(RefreshPipelineFixture.Task(
                "5c0be13186f7746309d759ca", "Chemical - Part 3", loyalty: new[] { (JaegerId, 2) }))
            .WithTraders((PraporId, "Prapor"), (JaegerId, "Jaeger"))
            .WithDatabase(("Chemical - Part 3", "5c0be13186f7746309d759ca"));

        Assert.True((await fixture.RefreshAsync()).Success);
        var gate = Assert.Single(fixture.Query("SELECT Id, RequiredLevel FROM QuestTraderRequirements"));
        Assert.Equal("2", gate[1]);

        // A reviewer approved the gate, then the game moved it.
        Execute(fixture, "UPDATE QuestTraderRequirements SET IsApproved = 1");
        fixture.WithTasks(RefreshPipelineFixture.Task(
            "5c0be13186f7746309d759ca", "Chemical - Part 3", loyalty: new[] { (JaegerId, 3) }));

        var second = await fixture.RefreshAsync();

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Equal("0", Assert.Single(fixture.Query("SELECT IsApproved FROM QuestTraderRequirements"))[0]);
        Assert.Contains(
            $"  [CHANGED] {gate[0]} - approval reset due to content change",
            File.ReadAllText(second.LogPath!));
    }

    private static void Execute(RefreshPipelineFixture fixture, string sql)
    {
        using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            connection.Open();
            using var cmd = new SqliteCommand(sql, connection);
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task The_run_writes_a_log_the_diff_report_can_read_back()
    {
        // The refresh and the report are the two ends of one contract, and nothing else checks
        // it: a property renamed on either side would leave the report's sections silently
        // empty, which reads exactly like "nothing was held back" on the artefact a publish is
        // reviewed against. So every section is produced here and read back THROUGH the
        // report's own model, never as text in the file.
        const string HuntsmanId = "5c0be13186f7746309d759cb";
        const string ChemicalId = "5c0be13186f7746309d759ca";
        const string OldThingId = "5c0be13186f7746309d759cd";
        const string ShooterFirstClaimId = "5c0be13186f7746309d759d1";
        const string ShooterSecondClaimId = "5c0be13186f7746309d759d2";

        // Chemical - Part 3's page still names a chain the game no longer reports.
        var staleChain = RefreshPipelineFixture.Page() + "\n|previous = [[Sew it Good]]\n";

        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Sew it Good", RefreshPipelineFixture.Page()),
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Chemical - Part 3", staleChain),
                ("The Tarkov Shooter - Part 5", RefreshPipelineFixture.Page()),
                ("Arena: First Blood", RefreshPipelineFixture.Page()),
                ("New Beginning (Prestige 2)", RefreshPipelineFixture.Page()),
                ("Uninvited Guests - Part 1", RefreshPipelineFixture.SeasonalPage()))
            .WithTasks(
                // Renamed, and the title it gave up is now another imported quest's: the
                // dangerous rename, which keying by page would have moved progress across.
                RefreshPipelineFixture.Task(StirrupId, "Sew it Good"),
                RefreshPipelineFixture.Task(OldThingId, "Stirrup"),
                RefreshPipelineFixture.Task(ChemicalId, "Chemical - Part 3"),
                RefreshPipelineFixture.Task(HuntsmanId, "The Huntsman Path - Control"),
                // Only the committed alias list can bridge this one: its link points at the
                // German title Neuanfang, which is not a page, and its normalizedName does not
                // spell the page either. Without it the "matched only by a hand written alias"
                // section is empty in every fixture, so renaming the key on either side of the
                // contract would blank that section of the report and fail nothing.
                RefreshPipelineFixture.Task(
                    "6761ff17cdc36bd66102e9d0", "New Beginning",
                    wikiLink: "https://escapefromtarkov.fandom.com/wiki/Neuanfang",
                    normalizedName: "new-beginning-2"),
                // Two records claiming one page, which only the log records.
                RefreshPipelineFixture.Task(
                    ShooterFirstClaimId, "The Tarkov Shooter - Part 5",
                    wikiLink: WikiQuestIdentity.PageLinkFor("The Tarkov Shooter - Part 5")),
                RefreshPipelineFixture.Task(
                    ShooterSecondClaimId, "The Tarkov Shooter - Part 5",
                    wikiLink: WikiQuestIdentity.PageLinkFor("The Tarkov Shooter - Part 5")))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId), ("Old Thing", OldThingId));

        var result = await fixture.RefreshAsync();
        Assert.True(result.Success, result.ErrorMessage);

        var logDir = System.IO.Path.Combine(fixture.BasePath, "wiki_data", "logs");
        var logFile = Assert.Single(System.IO.Directory.GetFiles(logDir, "refresh_*.json"));

        var log = DataDiff.RefreshLog.Read(logFile);

        Assert.Equal(2, log.Counts!["renames"]);
        Assert.Equal(1, log.Counts["titleReuses"]);
        Assert.Equal(1, log.Counts["collisions"]);

        var reuse = Assert.Single(log.TitleReuses!);
        Assert.Equal("Stirrup", reuse.PreviousName);
        Assert.Equal("Sew it Good", reuse.Title);
        Assert.Equal(StirrupId, reuse.BsgId);

        var collision = Assert.Single(log.Collisions!);
        Assert.Equal("The Tarkov Shooter - Part 5", collision.Title);
        Assert.Equal(ShooterSecondClaimId, collision.ChosenTaskId);
        Assert.Equal(CollisionRule.NewestId.ToString(), collision.Rule);
        Assert.Equal(
            new[] { ShooterFirstClaimId, ShooterSecondClaimId },
            collision.CandidateTaskIds!.OrderBy(id => id, StringComparer.Ordinal));

        var disagreement = Assert.Single(log.PrerequisiteDisagreements!);
        Assert.Equal("Chemical - Part 3", disagreement.Quest);
        Assert.Equal("wikiSuperset", disagreement.Verdict);
        Assert.Equal(new[] { "Sew it Good" }, disagreement.Wiki);
        Assert.Empty(disagreement.Game!);

        var heldBack = Assert.Single(log.HeldBackPages!);
        Assert.Equal("Arena: First Blood", heldBack.Title);
        Assert.Contains("no game record", heldBack.Reason);

        Assert.Equal(new[] { "Uninvited Guests - Part 1" }, log.WikiOnlySeasonal);
        Assert.Contains(log.TasksWithoutPage!, t => t.TaskId == HuntsmanId
            && t.NameEN == "The Huntsman Path - Control"
            && t.NormalizedName == "the-huntsman-path-control");

        // The one section with no end-to-end cover: a page that reached its game record only
        // because the alias list said so. Renaming the writer's "aliasesUsed" key, or the
        // reader's, would blank this section of the report and fail nothing.
        Assert.Equal(new[] { "New Beginning (Prestige 2)" }, log.AliasesUsed);

        // The other two committed entries go unused here, and the report is how one gets retired.
        var alias = Assert.Single(log.UnusedAliases!, a => a.PageTitle == "New Beginning (Prestige 3)");
        Assert.Equal("6848100b00afffa81f09e365", alias.TaskId);
        Assert.Contains("tarkov-data-manager", alias.UpstreamIssue);

        // The rendered markdown names each of them, which is what a reviewer actually reads.
        var rendered = log.Render();
        Assert.Contains("Arena: First Blood", rendered);
        Assert.Contains("Uninvited Guests - Part 1", rendered);
        Assert.Contains("The Huntsman Path - Control", rendered);
        Assert.Contains("The Tarkov Shooter - Part 5", rendered);
        Assert.Contains("Chemical - Part 3", rendered);
        Assert.Contains("Pages matched only by a hand written alias", rendered);
        Assert.Contains("New Beginning (Prestige 2)", rendered);
        Assert.Contains("New Beginning (Prestige 3)", rendered);
    }

    [Fact]
    public async Task A_page_the_quest_list_no_longer_names_leaves_the_cache()
    {
        // The exclusion categories are read once, at crawl time. A page admitted by a failed
        // exclusion fetch used to sit in quest_cache.json for good, and the from-cache refresh
        // republished it as a live quest every time. The cache now says what the crawl says.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Stirrup", RefreshPipelineFixture.Page()),
                ("Dressed to Kill", RefreshPipelineFixture.Page()));

        using var service = new WikiQuestService(fixture.WikiDataDir);
        await service.LoadCacheAsync();
        Assert.Equal(2, service.GetCachedQuests().Count);

        Assert.Equal(1, service.PruneCacheTo(new[] { "Stirrup" }));
        Assert.Equal(new[] { "Stirrup" }, service.GetCachedQuests().Keys);

        // A crawl that listed nothing is a failed crawl: pruning to it would empty the cache,
        // which is the one thing every guard downstream exists to prevent.
        Assert.Equal(0, service.PruneCacheTo(Array.Empty<string>()));
        Assert.Single(service.GetCachedQuests());
    }

    #endregion

    #region Row identity

    // Every child row's primary key is a hash of the fields that identify it, and its content
    // hash is what preserves a reviewer's approval across a publish. A changed key deletes and
    // reinserts every row of that table; a changed content hash silently drops every approval.
    // Neither shows up as a failure anywhere, so the exact strings are pinned here: these values
    // are the ones the published database already holds.

    [Fact]
    public void A_loyalty_gate_keeps_the_row_key_and_content_hash_it_has_always_had()
    {
        var gate = new DbQuestTraderRequirement
        {
            QuestId = "quest-1",
            TraderId = "trader-1",
            TraderName = "Prapor",
            RequiredLevel = 2,
        };

        Assert.Equal("7p6lQJt4MZZPgJ4Lq1kgFt", gate.ComputeId());
        Assert.Equal("BJpGuivfZbA0gZl4", gate.ComputeContentHash());
    }

    [Fact]
    public void A_prerequisite_keeps_the_row_key_it_has_always_had()
    {
        // The row key is the pair and the group, and nothing else. AltRequirementType arriving
        // must not move it: a moved key deletes the published row and inserts a new one, taking
        // its approval with it, for a column no build had asked about.
        var requirement = new DbQuestRequirement
        {
            QuestId = "quest-1",
            RequiredQuestId = "quest-2",
            RequirementType = "Accept",
            DelayMinutes = null,
            GroupId = 3,
        };

        Assert.Equal("hZqpnRG7VT1PdR4Cah1mR2", requirement.ComputeId());

        requirement.AltRequirementType = "Fail";
        Assert.Equal("hZqpnRG7VT1PdR4Cah1mR2", requirement.ComputeId());

        requirement.RequirementType = "Complete";
        Assert.Equal("hZqpnRG7VT1PdR4Cah1mR2", requirement.ComputeId());
    }

    [Fact]
    public void A_prerequisites_content_hash_covers_both_requirement_types()
    {
        // The content hash is what resets an approval when the data behind it changes, so a row
        // that starts being satisfied by a failure as well has to read as changed. DelayMinutes
        // is null here: a null int? renders as the empty string, which is part of the pinned value.
        var requirement = new DbQuestRequirement
        {
            QuestId = "quest-1",
            RequiredQuestId = "quest-2",
            RequirementType = "Accept",
            DelayMinutes = null,
            GroupId = 3,
        };

        Assert.Equal("M+zCCHWbAlrvDXzT", requirement.ComputeContentHash());

        requirement.AltRequirementType = "Fail";
        Assert.Equal("KPdhKZ4lSxD6etW0", requirement.ComputeContentHash());
    }

    [Fact]
    public void An_optional_quest_keeps_the_row_key_and_content_hash_it_has_always_had()
    {
        var optional = new DbOptionalQuest { QuestId = "quest-1", AlternativeQuestId = "quest-2" };

        Assert.Equal("Ee_i6yaRKy3xHBWdecwMKI", optional.ComputeId());
        Assert.Equal("y4t1OCl9MlB8h37V", optional.ComputeContentHash());
    }

    [Fact]
    public void An_objective_keeps_the_row_key_and_content_hash_it_has_always_had()
    {
        // The widest content hash of the five: nulls, an int?, and a bool, which renders "True".
        var objective = new DbQuestObjective
        {
            QuestId = "quest-1",
            SortOrder = 2,
            ObjectiveType = "Kill",
            Description = "Eliminate 10 Scavs",
            TargetCount = 10,
            RequiresFIR = true,
            MapName = "Customs",
        };

        Assert.Equal("R3wGbWADCGcTn0_07bfI2z", objective.ComputeId());
        Assert.Equal("pQjl9bRvAeiqmKRh", objective.ComputeContentHash());
    }

    [Fact]
    public void A_required_item_keeps_the_row_key_and_content_hash_it_has_always_had()
    {
        var required = new DbQuestRequiredItem
        {
            QuestId = "quest-1",
            ItemName = "Bronze pocket watch",
            Count = 2,
            RequiresFIR = false,
            RequirementType = "Handover",
            SortOrder = 1,
        };

        Assert.Equal("GLLtxWsJXmxfxfrQKOWT_s", required.ComputeId());
        Assert.Equal("a7JsBFyUBuhqYxDL", required.ComputeContentHash());
    }

    #endregion

    #region The crawl that feeds the wiki cache

    // GetAllQuestPagesAsync decides which pages exist at all, and it does it by subtracting five
    // exclusion categories from Category:Quests. The subtraction used to swallow every exception
    // and carry on with whatever it had, so one timed-out request turned into a crawl that
    // published a whole excluded category as live quests: Historical content alone holds 320
    // pages inside Category:Quests. These two tests drive the real crawl over a stubbed wiki,
    // one answering and one refusing.

    [Fact]
    public async Task A_crawl_excludes_the_pages_an_exclusion_category_lists()
    {
        using var wiki = new StubWiki();
        wiki.CategoryMembers["Quests"] = new[] { "Stirrup", "Kind of Sabotage", "Quests" };
        wiki.CategoryMembers["Historical content"] = new[] { "Kind of Sabotage" };

        using var crawl = wiki.NewService(out var basePath);
        try
        {
            var pages = await crawl.GetAllQuestPagesAsync();

            // "Quests" is the category overview page, which ExcludePages drops.
            Assert.Equal(new[] { "Stirrup" }, pages);
        }
        finally
        {
            DeleteDirectory(basePath);
        }
    }

    [Fact]
    public async Task A_crawl_that_cannot_read_an_exclusion_category_fails_instead_of_publishing_it()
    {
        using var wiki = new StubWiki();
        wiki.CategoryMembers["Quests"] = new[] { "Stirrup", "Kind of Sabotage" };
        wiki.CategoryMembers["Historical content"] = new[] { "Kind of Sabotage" };
        // The last of the five categories, so four are read in full before this one refuses:
        // a partial answer is what the crawl used to publish.
        wiki.FailingCategory = "Historical content";

        using var crawl = wiki.NewService(out var basePath);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => crawl.GetAllQuestPagesAsync());

            Assert.Contains("Category:Historical content", error.Message);
            Assert.Contains("live quest", error.Message);
        }
        finally
        {
            DeleteDirectory(basePath);
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    /// <summary>
    /// A MediaWiki categorymembers endpoint that answers from a dictionary, so the real crawl
    /// runs with no network. Stubbing at the transport is deliberate: the decision under test
    /// lives in <see cref="WikiQuestService.GetAllQuestPagesAsync"/> itself, and any fake that
    /// replaced the method would assert nothing about it.
    /// </summary>
    private sealed class StubWiki : IDisposable
    {
        private readonly List<HttpClient> _clients = new();

        /// <summary>Category title (without the "Category:" prefix) to its member pages.</summary>
        public Dictionary<string, string[]> CategoryMembers { get; } = new(StringComparer.Ordinal);

        /// <summary>The one category the wiki answers with a 503.</summary>
        public string? FailingCategory { get; set; }

        /// <summary>
        /// A service whose private HttpClient is this stub. Reflection because the class builds
        /// its own client in the constructor and has no seam; the alternative is not testing the
        /// crawl at all.
        /// </summary>
        public WikiQuestService NewService(out string basePath)
        {
            basePath = Path.Combine(Path.GetTempPath(), "wiki-crawl-" + Guid.NewGuid().ToString("N"));
            var service = new WikiQuestService(basePath);

            var field = typeof(WikiQuestService).GetField(
                "_httpClient",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.True(field != null, "WikiQuestService no longer has an _httpClient field to stub.");

            (field!.GetValue(service) as HttpClient)?.Dispose();
            var client = new HttpClient(new Handler(this));
            _clients.Add(client);
            field.SetValue(service, client);
            return service;
        }

        public void Dispose()
        {
            foreach (var client in _clients)
                client.Dispose();
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly StubWiki _wiki;

            public Handler(StubWiki wiki) => _wiki = wiki;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
                var category = (query["cmtitle"] ?? "").Replace("Category:", "");

                if (category == _wiki.FailingCategory)
                {
                    return Task.FromResult(new HttpResponseMessage(
                        System.Net.HttpStatusCode.ServiceUnavailable));
                }

                _wiki.CategoryMembers.TryGetValue(category, out var members);
                var body = System.Text.Json.JsonSerializer.Serialize(new
                {
                    query = new
                    {
                        categorymembers = (members ?? Array.Empty<string>())
                            .Select(title => new { title })
                            .ToArray(),
                    },
                });

                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                });
            }
        }
    }

    #endregion

    /// <summary>A task whose single prerequisite is satisfied by more than one status.</summary>
    private static TarkovDevQuestCacheItem MultiStatusTask(
        string id, string title, string requiredTaskId, params string[] statuses)
    {
        var task = RefreshPipelineFixture.Task(id, title);
        task.TaskRequirements = new List<TarkovDevTaskPrerequisite>
        {
            new() { TaskId = requiredTaskId, Status = statuses.ToList() },
        };
        return task;
    }
}

/// <summary>
/// The other end of the contract the refresh writes against: what the shipped app makes of a
/// prerequisite a failure also satisfies, read out of a real database by the real reader.
/// <para>
/// Both halves of that fact matter and neither can be checked from the pipeline side. The reader
/// keys incoming requirement rows by prerequisite alone and silently drops a second row naming
/// the same one, so an OR group is only worth writing if it survives that; and the OR group and
/// the second type column each exist for one player state, which only
/// <c>QuestProgressService.ArePrerequisitesMet</c> can answer for.
/// </para>
/// </summary>
public sealed class ExclusivePrerequisiteReadbackTests
{
    private const string PraporId = "54cb50c76803fa8b248b4571";
    private const string BuildingFoundationsId = "673f629c5b555b53460cf827";
    private const string SwiftRetributionId = "6745fcded0fbbc74ca0f721d";
    private const string InevitableResponseId = "673f6027352b4da8e00322d2";

    /// <summary>
    /// A published database built by the real refresh, holding the OR group an exclusive 1.1
    /// pair produces.
    /// </summary>
    private static async Task<RefreshPipelineFixture> PublishedAsync()
    {
        var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Building Foundations", RefreshPipelineFixture.Page()),
                ("Swift Retribution", RefreshPipelineFixture.Page()),
                ("Inevitable Response", RefreshPipelineFixture.Page()))
            .WithTasks(
                MultiStatusTask(BuildingFoundationsId, "Building Foundations", SwiftRetributionId),
                RefreshPipelineFixture.Task(
                    SwiftRetributionId,
                    "Swift Retribution",
                    failedBy: new[] { RefreshPipelineFixture.FailedByCompleting(InevitableResponseId) }),
                RefreshPipelineFixture.Task(InevitableResponseId, "Inevitable Response"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(
                ("Building Foundations", BuildingFoundationsId),
                ("Swift Retribution", SwiftRetributionId),
                ("Inevitable Response", InevitableResponseId));

        var result = await fixture.RefreshAsync();
        Assert.True(result.Success, result.ErrorMessage);
        return fixture;
    }

    private static TarkovDevQuestCacheItem MultiStatusTask(string id, string title, string requiredTaskId)
    {
        var task = RefreshPipelineFixture.Task(id, title);
        task.TaskRequirements = new List<TarkovDevTaskPrerequisite>
        {
            new() { TaskId = requiredTaskId, Status = new List<string> { "complete", "failed" } },
        };
        return task;
    }

    /// <summary>
    /// Drops the column a build that predates it never had, which is what every install already
    /// in the field reads. Exercises the reader's ColumnExistsAsync branch against a real absence
    /// rather than against a NULL.
    /// </summary>
    private static void DropTheAlternateTypeColumn(RefreshPipelineFixture fixture)
    {
        using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            connection.Open();
            using var cmd = new SqliteCommand(
                "ALTER TABLE QuestRequirements DROP COLUMN AltRequirementType", connection);
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    /// <summary>
    /// The quests a real <see cref="QuestDbService"/> reads out of the database, by name. The
    /// service is a singleton whose constructor subscribes to DatabaseUpdateService and takes the
    /// installed database path, so it is built uninitialized (see <see cref="TestReflection"/>)
    /// with only the path seeded; everything the load touches is reassigned by the load itself.
    /// </summary>
    private static async Task<Dictionary<string, TarkovTask>> ReadQuestsAsync(RefreshPipelineFixture fixture)
    {
        var service = TestReflection.Uninitialized<QuestDbService>();
        TestReflection.SetPrivateField(service, "_databasePath", fixture.DatabasePath);

        Assert.True(await service.LoadQuestsAsync(), "the reader did not load the refreshed database");

        var quests = service.AllQuests.ToDictionary(q => q.Name ?? "", q => q, StringComparer.Ordinal);
        SqliteConnection.ClearAllPools();
        return quests;
    }

    /// <summary>
    /// <c>QuestProgressService.ArePrerequisitesMet(task, snapshot, settings)</c>, the overload
    /// that answers from captured state instead of from the singletons. It is private, so it is
    /// reached by reflection with the member asserted to exist: the public overload reads
    /// SettingsService and its SQLite-backed profile, which no unit test should depend on.
    /// </summary>
    private static bool ArePrerequisitesMet(
        TarkovTask quest, IReadOnlyDictionary<string, QuestStatus> recorded, params TarkovTask[] all)
    {
        var snapshot = ProgressSnapshot.From("test-profile", 1, recorded, new Dictionary<string, bool>());
        var service = ProgressServiceHarness.Create(new ProgressStoreFake(), snapshot, all);

        var method = typeof(QuestProgressService).GetMethod(
            "ArePrerequisitesMet",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.True(method != null, "QuestProgressService has no private ArePrerequisitesMet overload");

        return (bool)method!.Invoke(
            service,
            new object?[] { quest, snapshot, SettingsServiceTestSupport.Seeded("test-profile") })!;
    }

    [Fact]
    public async Task Both_branches_of_the_or_group_survive_the_readers_de_duplication()
    {
        // The stop condition for writing an OR group at all. LoadQuestRequirementsAsync matches
        // an incoming row against the ones already held by TaskId alone, GroupId ignored, and
        // discards every later row naming the same prerequisite. Two branches naming two
        // different quests is the shape that survives it, which is why the publish guard refuses
        // outright any quest that would name one prerequisite twice.
        using var fixture = await PublishedAsync();

        var quests = await ReadQuestsAsync(fixture);
        var dependent = quests["Building Foundations"];

        Assert.NotNull(dependent.TaskRequirements);
        Assert.Equal(2, dependent.TaskRequirements!.Count);
        Assert.All(dependent.TaskRequirements, r => Assert.Equal(1, r.GroupId));

        var named = Assert.Single(
            dependent.TaskRequirements,
            r => r.TaskId == WikiQuestIdentity.IdFor("Swift Retribution"));
        Assert.Equal(new[] { "complete", "fail" }, named.Status);

        var twin = Assert.Single(
            dependent.TaskRequirements,
            r => r.TaskId == WikiQuestIdentity.IdFor("Inevitable Response"));
        Assert.Equal(new[] { "complete" }, twin.Status);
    }

    [Fact]
    public async Task A_database_published_before_the_column_reads_exactly_as_it_always_did()
    {
        // The feature-detection branch. With no column there is no second status, and the rows
        // are the ones the published database has shipped all along.
        using var fixture = await PublishedAsync();
        DropTheAlternateTypeColumn(fixture);

        var quests = await ReadQuestsAsync(fixture);
        var dependent = quests["Building Foundations"];

        Assert.Equal(2, dependent.TaskRequirements!.Count);
        Assert.All(dependent.TaskRequirements, r => Assert.Equal(new[] { "complete" }, r.Status));
        Assert.All(dependent.TaskRequirements, r => Assert.Equal(1, r.GroupId));
    }

    [Theory]
    // The four states a player can be in for an exclusive pair, on the shape every install
    // already in the field reads: the OR group alone, no second type column.
    [InlineData(false, false, false, false)]  // neither branch done, which is where the game locks it too
    [InlineData(false, true, false, true)]    // the twin done, so the game failed the prerequisite
    [InlineData(true, false, false, true)]    // the prerequisite itself done
    [InlineData(true, true, false, true)]     // both recorded
    // And the same four on a build that also reads the second type column.
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    public async Task A_player_who_took_either_branch_is_not_locked_out(
        bool prerequisiteDone, bool twinDone, bool readsTheAlternateType, bool expected)
    {
        using var fixture = await PublishedAsync();
        if (!readsTheAlternateType)
            DropTheAlternateTypeColumn(fixture);

        var quests = await ReadQuestsAsync(fixture);
        var recorded = new Dictionary<string, QuestStatus>(StringComparer.OrdinalIgnoreCase);
        if (prerequisiteDone)
            recorded[WikiQuestIdentity.IdFor("Swift Retribution")] = QuestStatus.Done;
        if (twinDone)
        {
            recorded[WikiQuestIdentity.IdFor("Inevitable Response")] = QuestStatus.Done;
            // Completing the twin is what fails the prerequisite, in the game and in the app's
            // own handling of mutually exclusive quests.
            if (!prerequisiteDone)
                recorded[WikiQuestIdentity.IdFor("Swift Retribution")] = QuestStatus.Failed;
        }

        Assert.Equal(expected, ArePrerequisitesMet(
            quests["Building Foundations"], recorded, quests.Values.ToArray()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Only_a_build_that_reads_the_second_type_unlocks_on_a_bare_recorded_failure(
        bool readsTheAlternateType, bool expected)
    {
        // The residual the OR group cannot reach: a failure recorded without the twin also being
        // recorded as done. The game cannot produce that state for this pair, but log sync
        // records a failure straight from the game log, so an install can hold it. This is the
        // state the second type column exists for, and the honest limit of the OR group.
        using var fixture = await PublishedAsync();
        if (!readsTheAlternateType)
            DropTheAlternateTypeColumn(fixture);

        var quests = await ReadQuestsAsync(fixture);
        var recorded = new Dictionary<string, QuestStatus>(StringComparer.OrdinalIgnoreCase)
        {
            [WikiQuestIdentity.IdFor("Swift Retribution")] = QuestStatus.Failed,
        };

        Assert.Equal(expected, ArePrerequisitesMet(
            quests["Building Foundations"], recorded, quests.Values.ToArray()));
    }
}
