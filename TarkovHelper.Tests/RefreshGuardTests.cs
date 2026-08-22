using System.Threading.Tasks;
using TarkovDBEditor.Services;

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
        // flag is 0: the old synthesis only ever inserted. Rebuilding the set is what removes it.
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
                ("Grenadier", "5c0be13186f7746309d759c9"));

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
    }

    [Theory]
    // Ten 1.1 prerequisites name "active or complete". Accept is satisfied by an active AND by a
    // completed prerequisite, so this collapses onto one row with nothing lost.
    [InlineData(new[] { "active", "complete" }, "Accept")]
    [InlineData(new[] { "complete", "active" }, "Accept")]
    // Four name "complete or failed", which no single type covers. Complete is the path a player
    // normally follows; the alternative would be a quest locked forever on the other branch.
    [InlineData(new[] { "complete", "failed" }, "Complete")]
    [InlineData(new[] { "complete" }, "Complete")]
    [InlineData(new[] { "active" }, "Accept")]
    [InlineData(new[] { "failed" }, "Fail")]
    // An entry with no status at all is the ordinary "must be completed".
    [InlineData(new string[0], "Complete")]
    public void A_prerequisite_that_names_several_statuses_collapses_to_the_most_permissive(
        string[] statuses, string expected)
    {
        Assert.Equal(expected, RefreshDataService.MapRequirementStatuses(statuses, "Some Quest"));
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
        var row = Assert.Single(fixture.Query("SELECT RequirementType FROM QuestRequirements"));
        Assert.Equal("Accept", row[0]);
    }

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
    public async Task The_run_writes_a_log_the_diff_report_can_read_back()
    {
        // The refresh and the report are the two ends of one contract, and nothing else checks
        // it: a property renamed on either side would leave the report's sections silently
        // empty, which reads exactly like "nothing was held back" on the artefact a publish is
        // reviewed against.
        using var fixture = new RefreshPipelineFixture()
            .WithWikiPages(
                ("Shooter Born in Heaven", RefreshPipelineFixture.Page()),
                ("Arena: First Blood", RefreshPipelineFixture.Page()),
                ("Uninvited Guests - Part 1", RefreshPipelineFixture.SeasonalPage()))
            .WithTasks(
                RefreshPipelineFixture.Task(StirrupId, "Shooter Born in Heaven"),
                RefreshPipelineFixture.Task("5c0be13186f7746309d759cb", "The Huntsman Path - Control"))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("A Shooter Born in Heaven", StirrupId));

        Assert.True((await fixture.RefreshAsync()).Success);

        var logDir = System.IO.Path.Combine(fixture.BasePath, "wiki_data", "logs");
        var logFile = Assert.Single(System.IO.Directory.GetFiles(logDir, "refresh_*.json"));

        var log = DataDiff.RefreshLog.Read(logFile);
        var rendered = log.Render();

        Assert.Equal(1, log.Counts!["renames"]);
        Assert.Contains("A Shooter Born in Heaven", System.IO.File.ReadAllText(logFile));
        Assert.Contains("Arena: First Blood", rendered);
        Assert.Contains("Uninvited Guests - Part 1", rendered);
        Assert.Contains("The Huntsman Path - Control", rendered);
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
