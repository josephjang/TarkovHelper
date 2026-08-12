using System.IO;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the defect fix-profile-data-attribution.md exists for: a sync run covers every session
/// folder the game still retains, across all game modes, and used to write every event into
/// whichever profile happened to be selected. Each test here keeps the selected profile set to a
/// third value throughout, so any leak back to "whatever is on screen" shows up as a row in the
/// wrong partition.
/// </summary>
public sealed class LogSyncAttributionTests : IDisposable
{
    private const string PveQuestId = "5936d90786f7742b1420ba5b";
    private const string SeasonQuestId = "5936da9e86f7742d65037edf";
    private const string OrphanQuestId = "59674cd986f7744ab26e32f2";

    private readonly string _logRoot = Path.Combine(
        Path.GetTempPath(), "tarkovhelper-sync-" + Guid.NewGuid().ToString("N"));

    private readonly ProgressStoreFake _store = new();

    public void Dispose()
    {
        try { Directory.Delete(_logRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly TarkovTask PveQuest = TestTasks.Quest(PveQuestId, "pve-quest");
    private static readonly TarkovTask SeasonQuest = TestTasks.Quest(SeasonQuestId, "season-quest");
    private static readonly TarkovTask OrphanQuest = TestTasks.Quest(OrphanQuestId, "orphan-quest");

    /// <summary>
    /// Writes one EFT session folder: an application log naming the session mode, and a
    /// push-notifications log holding one quest-completed notification (message type 12).
    /// </summary>
    /// <param name="sessionMode">The Session mode token, or null to write no mode line at all.</param>
    /// <returns>The path of the push-notifications log written, for the live-path tests.</returns>
    private string WriteSession(string folderName, string? sessionMode, string questId, DateTime completedAt)
        => WriteSession(folderName, sessionMode, questId, completedAt,
            messageType: 12, templateSuffix: "successMessageText");

    /// <summary>
    /// Same session folder, but with a quest-STARTED notification (message type 10), so a test
    /// can produce a quest whose final state is in progress rather than done.
    /// </summary>
    private string WriteStartedSession(string folderName, string? sessionMode, string questId, DateTime startedAt)
        => WriteSession(folderName, sessionMode, questId, startedAt,
            messageType: 10, templateSuffix: "startedMessageText");

    /// <param name="messageType">The EFT message.type: 10 started, 11 failed, 12 completed.</param>
    /// <param name="templateSuffix">The templateId's trailing token, which the parser discards.</param>
    private string WriteSession(
        string folderName,
        string? sessionMode,
        string questId,
        DateTime eventAt,
        int messageType,
        string templateSuffix)
    {
        var folder = Path.Combine(_logRoot, folderName);
        Directory.CreateDirectory(folder);

        var appLogLines = sessionMode == null
            ? new[] { $"{eventAt.AddMinutes(-5):yyyy-MM-dd HH:mm:ss.fff} 1|Info|application|Init: pstrGameVersion:live" }
            : new[]
            {
                $"{eventAt.AddMinutes(-5):yyyy-MM-dd HH:mm:ss.fff} 1|Info|application|Session mode: {sessionMode}"
            };
        File.WriteAllLines(Path.Combine(folder, $"{folderName} application.log"), appLogLines);

        var unix = new DateTimeOffset(eventAt).ToUnixTimeSeconds();
        var pushLogPath = Path.Combine(folder, $"{folderName} push-notifications_000.log");
        File.WriteAllText(
            pushLogPath,
            $$"""
            {{eventAt:yyyy-MM-dd HH:mm:ss.fff}}|1.1.0|Info|push-notifications|Got notification | new_message
            {
              "type": "new_message",
              "eventId": "{{Guid.NewGuid():N}}",
              "dialogId": "54cb57776803fa99248b456e",
              "message": {
                "type": {{messageType}},
                "templateId": "{{questId}} {{templateSuffix}}",
                "dt": {{unix}}
              }
            }
            """);

        return pushLogPath;
    }

    private LogSyncService NewSyncService() => new() { Store = _store };

    private static QuestGraphService NewGraph(params TarkovTask[] tasks)
    {
        var graph = new QuestGraphService();
        graph.Initialize(tasks.ToList());
        return graph;
    }

    /// <summary>
    /// A progress service whose loaded profile is PvP Zone, deliberately neither of the profiles
    /// the fixture logs belong to, so nothing can pass by reading the selection.
    /// </summary>
    private QuestProgressService NewProgress(params TarkovTask[] tasks)
        => ProgressServiceHarness.Create(_store, AppProfile.PvpZone, tasks);

    private Task<SyncResult> SyncAsync(int daysRange = 0, params TarkovTask[] tasks)
        => NewSyncService().SyncFromLogsAsync(
            _logRoot, NewProgress(tasks), NewGraph(tasks), progress: null, daysRange: daysRange);

    [Fact]
    public async Task Events_carry_the_profile_of_the_session_that_produced_them()
    {
        var now = DateTime.Now;
        WriteSession("log_2026.08.10_10-00-00_1.1.0", "Pve", PveQuestId, now.AddHours(-3));
        WriteSession("log_2026.08.10_12-00-00_1.1.0", "PvpSeason", SeasonQuestId, now.AddHours(-1));

        var events = await NewSyncService().ParseLogDirectoryAsync(_logRoot);

        Assert.Equal(2, events.Count);
        Assert.Equal(AppProfile.PveZone, events.Single(e => e.QuestId == PveQuestId).OwnerProfile);
        Assert.Equal(AppProfile.PvpSeason, events.Single(e => e.QuestId == SeasonQuestId).OwnerProfile);
    }

    [Fact]
    public async Task A_sync_distributes_each_session_to_its_own_profile()
    {
        var now = DateTime.Now;
        WriteSession("log_2026.08.10_10-00-00_1.1.0", "Pve", PveQuestId, now.AddHours(-3));
        WriteSession("log_2026.08.10_12-00-00_1.1.0", "PvpSeason", SeasonQuestId, now.AddHours(-1));

        var sync = NewSyncService();
        var progress = NewProgress(PveQuest, SeasonQuest);
        var result = await sync.SyncFromLogsAsync(
            _logRoot, progress, NewGraph(PveQuest, SeasonQuest), progress: null, daysRange: 0);

        Assert.Equal(AppProfile.PveZone,
            result.QuestsToComplete.Single(c => c.NormalizedName == "pve-quest").OwnerProfile);
        Assert.Equal(AppProfile.PvpSeason,
            result.QuestsToComplete.Single(c => c.NormalizedName == "season-quest").OwnerProfile);

        var applied = await sync.ApplyQuestChangesAsync(result.QuestsToComplete, progress);

        Assert.Empty(applied.FailedProfiles);
        Assert.Equal(1, applied.AppliedByProfile[AppProfile.PveZone]);
        Assert.Equal(1, applied.AppliedByProfile[AppProfile.PvpSeason]);

        // The PvE session's quest is in pve and NOWHERE else, least of all in the profile that
        // was selected the whole time. Rows are asserted under the key a LOAD returns them by
        // (NormalizedName), which is the only shape any reader of the store ever sees.
        Assert.Equal(QuestStatus.Done, _store.QuestsOf(ProfileService.PveProfileId)["pve-quest"]);
        Assert.Equal(QuestStatus.Done, _store.QuestsOf(ProfileService.SeasonProfileId)["season-quest"]);
        Assert.False(_store.QuestsOf(ProfileService.SeasonProfileId).ContainsKey("pve-quest"));
        Assert.False(_store.QuestsOf(ProfileService.PveProfileId).ContainsKey("season-quest"));
        Assert.Empty(_store.QuestsOf(ProfileService.PvpProfileId));

        // ...and the row itself carries the Id the write named, so a later reset keyed by Id
        // still finds it.
        Assert.Equal(PveQuestId, _store.QuestRowsOf(ProfileService.PveProfileId)[PveQuestId].Id);
    }

    [Fact]
    public async Task An_event_with_no_session_mode_evidence_is_counted_and_never_written()
    {
        var now = DateTime.Now;
        WriteSession("log_2026.08.10_10-00-00_1.1.0", "Pve", PveQuestId, now.AddHours(-3));
        WriteSession("log_2026.08.10_12-00-00_1.1.0", sessionMode: null, OrphanQuestId, now.AddHours(-1));

        var sync = NewSyncService();
        var progress = NewProgress(PveQuest, OrphanQuest);
        var result = await sync.SyncFromLogsAsync(
            _logRoot, progress, NewGraph(PveQuest, OrphanQuest), progress: null, daysRange: 0);

        Assert.Equal(2, result.TotalEventsFound);
        Assert.Equal(1, result.UnattributedEventCount);
        Assert.DoesNotContain(result.QuestsToComplete, c => c.NormalizedName == "orphan-quest");

        await sync.ApplyQuestChangesAsync(result.QuestsToComplete, progress);

        // Not in any partition: a guess here is what merged PvE history into a season before.
        foreach (var profileId in new[]
                 { ProfileService.PvpProfileId, ProfileService.PveProfileId, ProfileService.SeasonProfileId })
        {
            Assert.False(_store.QuestsOf(profileId).ContainsKey("orphan-quest"),
                $"an unattributable event was written to '{profileId}'");
        }

        // The attributable half of the same run did land, so the assertion above is not passing
        // because the sync wrote nothing at all.
        Assert.Equal(QuestStatus.Done, _store.QuestsOf(ProfileService.PveProfileId)["pve-quest"]);
    }

    // The configured range used to be dropped at the call site, so every sync covered every
    // retained log (PRD R8).
    [Fact]
    public async Task A_configured_day_range_drops_events_older_than_the_window()
    {
        var now = DateTime.Now;
        WriteSession("log_2026.08.01_10-00-00_1.1.0", "Pve", PveQuestId, now.AddDays(-9));
        WriteSession("log_2026.08.10_12-00-00_1.1.0", "PvpSeason", SeasonQuestId, now.AddHours(-1));

        var result = await SyncAsync(daysRange: 3, PveQuest, SeasonQuest);

        Assert.Equal(1, result.TotalEventsFound);
        var change = Assert.Single(result.QuestsToComplete);
        Assert.Equal("season-quest", change.NormalizedName);
    }

    [Fact]
    public async Task A_zero_day_range_keeps_every_retained_event()
    {
        var now = DateTime.Now;
        WriteSession("log_2026.08.01_10-00-00_1.1.0", "Pve", PveQuestId, now.AddDays(-9));
        WriteSession("log_2026.08.10_12-00-00_1.1.0", "PvpSeason", SeasonQuestId, now.AddHours(-1));

        var result = await SyncAsync(daysRange: 0, PveQuest, SeasonQuest);

        Assert.Equal(2, result.TotalEventsFound);
        Assert.Equal(2, result.QuestsToComplete.Count);
    }

    // "Already up to date" has to be judged against the OWNING profile's rows. Judging it against
    // the loaded cache would report a season quest as new because PvP Zone has never seen it.
    [Fact]
    public async Task A_quest_already_recorded_in_its_own_profile_is_counted_not_rewritten()
    {
        var now = DateTime.Now;
        WriteSession("log_2026.08.10_12-00-00_1.1.0", "PvpSeason", SeasonQuestId, now.AddHours(-1));
        _store.Seed(ProfileService.SeasonProfileId, SeasonQuest, QuestStatus.Done);

        var result = await SyncAsync(daysRange: 0, SeasonQuest);

        Assert.Equal(1, result.AlreadyCurrentCount);
        Assert.Empty(result.QuestsToComplete);
    }

    // The same quest can be in progress in two modes at once; the summary reports a count of
    // DISTINCT quests, so the cross-pass dedup is what keeps "still in progress: N" from
    // exceeding the number of quests that exist.
    [Fact]
    public async Task A_quest_started_in_two_profiles_is_counted_in_progress_once()
    {
        var now = DateTime.Now;
        WriteStartedSession("log_2026.08.10_10-00-00_1.1.0", "Pve", PveQuestId, now.AddHours(-3));
        WriteStartedSession("log_2026.08.10_12-00-00_1.1.0", "PvpSeason", PveQuestId, now.AddHours(-1));

        var result = await SyncAsync(daysRange: 0, PveQuest);

        Assert.Equal(2, result.TotalEventsFound);
        Assert.Single(result.InProgressQuests);
    }

    // The loaded profile's rows live in the snapshot; hand edits reach the store through a
    // fire-and-forget save. Re-reading the store for the profile that is ON SCREEN plans against
    // rows that can be behind the cache, and disagrees with the apply step, which re-plans the
    // loaded profile from the snapshot. The off-screen half stays store-backed and is guarded by
    // A_quest_already_recorded_in_its_own_profile_is_counted_not_rewritten.
    [Fact]
    public async Task The_loaded_profile_is_judged_against_its_snapshot_not_a_fresh_store_read()
    {
        WriteSession("log_2026.08.10_10-00-00_1.1.0", "Pve", PveQuestId, DateTime.Now.AddHours(-1));

        var progressService = ProgressServiceHarness.Create(
            _store,
            ProgressSnapshot.From(
                ProfileService.PveProfileId, 0,
                new Dictionary<string, QuestStatus> { ["pve-quest"] = QuestStatus.Done },
                new Dictionary<string, bool>()),
            PveQuest);

        var result = await NewSyncService().SyncFromLogsAsync(
            _logRoot, progressService, NewGraph(PveQuest), progress: null, daysRange: 0);

        Assert.Empty(result.QuestsToComplete);
        Assert.Equal(1, result.AlreadyCurrentCount);
    }

    // Same quest, two modes: each session writes its own profile and neither borrows the other's
    // stored state.
    [Fact]
    public async Task The_same_quest_in_two_modes_lands_in_both_profiles_independently()
    {
        var now = DateTime.Now;
        WriteSession("log_2026.08.10_10-00-00_1.1.0", "Pve", PveQuestId, now.AddHours(-3));
        WriteSession("log_2026.08.10_12-00-00_1.1.0", "PvpSeason", PveQuestId, now.AddHours(-1));

        var sync = NewSyncService();
        var progress = NewProgress(PveQuest);
        var result = await sync.SyncFromLogsAsync(
            _logRoot, progress, NewGraph(PveQuest), progress: null, daysRange: 0);
        var applied = await sync.ApplyQuestChangesAsync(result.QuestsToComplete, progress);

        Assert.Empty(applied.FailedProfiles);
        Assert.Equal(1, applied.AppliedByProfile[AppProfile.PveZone]);
        Assert.Equal(1, applied.AppliedByProfile[AppProfile.PvpSeason]);
        Assert.Equal(QuestStatus.Done, _store.QuestsOf(ProfileService.PveProfileId)["pve-quest"]);
        Assert.Equal(QuestStatus.Done, _store.QuestsOf(ProfileService.SeasonProfileId)["pve-quest"]);
    }

    // Every planned change names the profile of the session it came from. PvP Zone is both the
    // enum's default AND the selected profile here, so a change that had simply never been given
    // an owner would be indistinguishable from a correct one if this asserted anything weaker.
    [Fact]
    public async Task Every_planned_change_carries_the_owner_of_its_session()
    {
        var now = DateTime.Now;
        WriteSession("log_2026.08.10_12-00-00_1.1.0", "PvpSeason", SeasonQuestId, now.AddHours(-1));

        var result = await SyncAsync(daysRange: 0, SeasonQuest);

        Assert.NotEmpty(result.QuestsToComplete);
        Assert.All(result.QuestsToComplete, change =>
            Assert.Equal(AppProfile.PvpSeason, change.OwnerProfile));
    }

    // The summary reports what was WRITTEN, not what was queued. Applying the same result twice
    // is the cheapest way to produce "queued but not written": the second pass finds every row
    // already recorded in its own profile and must report nothing.
    [Fact]
    public async Task A_second_apply_of_the_same_changes_reports_nothing_applied()
    {
        var now = DateTime.Now;
        WriteSession("log_2026.08.10_10-00-00_1.1.0", "Pve", PveQuestId, now.AddHours(-3));
        WriteSession("log_2026.08.10_12-00-00_1.1.0", "PvpSeason", SeasonQuestId, now.AddHours(-1));

        var sync = NewSyncService();
        var progress = NewProgress(PveQuest, SeasonQuest);
        var result = await sync.SyncFromLogsAsync(
            _logRoot, progress, NewGraph(PveQuest, SeasonQuest), progress: null, daysRange: 0);

        var first = await sync.ApplyQuestChangesAsync(result.QuestsToComplete, progress);
        Assert.Empty(first.FailedProfiles);
        Assert.Equal(1, first.AppliedByProfile[AppProfile.PveZone]);
        Assert.Equal(1, first.AppliedByProfile[AppProfile.PvpSeason]);

        var second = await sync.ApplyQuestChangesAsync(result.QuestsToComplete, progress);

        // Nothing written AND nothing failed: "reports nothing applied" must mean the rows were
        // already current, not that both partitions threw.
        Assert.Empty(second.FailedProfiles);
        Assert.All(second.AppliedByProfile.Values, count => Assert.Equal(0, count));
        Assert.Equal(2, _store.QuestWrites.Count);
    }

    // One profile's batch throwing must not cost the others their report, and must not leave the
    // player told nothing went wrong. Without a failure channel a thrown partition is simply
    // absent from the counts, indistinguishable from one that needed no change; with only one
    // profile in the run the dialog then reads "No quests changed."
    [Fact]
    public async Task A_profile_whose_batch_throws_is_reported_as_failed_and_the_others_still_apply()
    {
        var now = DateTime.Now;
        WriteSession("log_2026.08.10_10-00-00_1.1.0", "Pve", PveQuestId, now.AddHours(-3));
        WriteSession("log_2026.08.10_12-00-00_1.1.0", "PvpSeason", SeasonQuestId, now.AddHours(-1));

        var sync = NewSyncService();
        var progress = NewProgress(PveQuest, SeasonQuest);
        var result = await sync.SyncFromLogsAsync(
            _logRoot, progress, NewGraph(PveQuest, SeasonQuest), progress: null, daysRange: 0);

        _store.SaveGate = profileId => profileId == ProfileService.SeasonProfileId
            ? throw new InvalidOperationException("database is locked")
            : Task.CompletedTask;

        var outcome = await sync.ApplyQuestChangesAsync(result.QuestsToComplete, progress);

        Assert.Equal(1, outcome.AppliedByProfile[AppProfile.PveZone]);
        Assert.Equal(new[] { AppProfile.PvpSeason }, outcome.FailedProfiles);
        Assert.False(outcome.AppliedByProfile.ContainsKey(AppProfile.PvpSeason));
    }

    // PRD R3 on the LIVE path: an event with no session mode evidence has no destination, so it
    // is never raised. Dropping it at the source rather than at each subscriber is what makes the
    // rule enforceable - a consumer that forgot the null check would record it under whatever
    // profile happens to be selected.
    [Fact]
    public async Task A_live_event_with_no_session_mode_evidence_is_never_raised()
    {
        var pushLog = WriteSession(
            "log_2026.08.10_12-00-00_1.1.0", sessionMode: null, OrphanQuestId, DateTime.Now);

        var sync = NewSyncService();
        var raised = new List<QuestLogEvent>();
        sync.QuestEventDetected += (_, e) => raised.Add(e);

        await ProcessLatestLogEvents(sync, pushLog);

        Assert.Empty(raised);
    }

    // The other half of the same rule, and the proof that the test above is not passing because
    // the live path raises nothing at all.
    [Fact]
    public async Task A_live_event_from_an_attributed_session_is_raised_with_its_owner()
    {
        var pushLog = WriteSession(
            "log_2026.08.10_12-00-00_1.1.0", "PvpSeason", SeasonQuestId, DateTime.Now);

        var sync = NewSyncService();
        var raised = new List<QuestLogEvent>();
        sync.QuestEventDetected += (_, e) => raised.Add(e);

        await ProcessLatestLogEvents(sync, pushLog);

        Assert.Equal(AppProfile.PvpSeason, Assert.Single(raised).OwnerProfile);
    }

    /// <summary>
    /// Runs the watcher's own handler for one notification log. Private because nothing outside
    /// the file watcher calls it, and reached by reflection here because the alternative - a real
    /// FileSystemWatcher - would make the test a timing exercise.
    /// </summary>
    private static Task ProcessLatestLogEvents(LogSyncService sync, string pushLogPath)
    {
        var method = typeof(LogSyncService).GetMethod(
            "ProcessLatestLogEvents",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.True(method != null, "LogSyncService has no ProcessLatestLogEvents handler");

        return (Task)method!.Invoke(sync, new object[] { pushLogPath })!;
    }
}
