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

    private static TarkovTask Task(string id, string name) => new()
    {
        Ids = new List<string> { id },
        Name = name,
        NormalizedName = name,
        Trader = "Prapor",
    };

    private static readonly TarkovTask PveQuest = Task(PveQuestId, "pve-quest");
    private static readonly TarkovTask SeasonQuest = Task(SeasonQuestId, "season-quest");
    private static readonly TarkovTask OrphanQuest = Task(OrphanQuestId, "orphan-quest");

    /// <summary>
    /// Writes one EFT session folder: an application log naming the session mode, and a
    /// push-notifications log holding one quest-completed notification.
    /// </summary>
    /// <param name="sessionMode">The Session mode token, or null to write no mode line at all.</param>
    private void WriteSession(string folderName, string? sessionMode, string questId, DateTime completedAt)
    {
        var folder = Path.Combine(_logRoot, folderName);
        Directory.CreateDirectory(folder);

        var appLogLines = sessionMode == null
            ? new[] { $"{completedAt.AddMinutes(-5):yyyy-MM-dd HH:mm:ss.fff} 1|Info|application|Init: pstrGameVersion:live" }
            : new[]
            {
                $"{completedAt.AddMinutes(-5):yyyy-MM-dd HH:mm:ss.fff} 1|Info|application|Session mode: {sessionMode}"
            };
        File.WriteAllLines(Path.Combine(folder, $"{folderName} application.log"), appLogLines);

        var unix = new DateTimeOffset(completedAt).ToUnixTimeSeconds();
        File.WriteAllText(
            Path.Combine(folder, $"{folderName} push-notifications_000.log"),
            $$"""
            {{completedAt:yyyy-MM-dd HH:mm:ss.fff}}|1.1.0|Info|push-notifications|Got notification | new_message
            {
              "type": "new_message",
              "eventId": "{{Guid.NewGuid():N}}",
              "dialogId": "54cb57776803fa99248b456e",
              "message": {
                "type": 12,
                "templateId": "{{questId}} successMessageText",
                "dt": {{unix}}
              }
            }
            """);
    }

    private LogSyncService NewSyncService() => new() { _store = _store };

    private static QuestGraphService NewGraph(params TarkovTask[] tasks)
    {
        var graph = new QuestGraphService();
        graph.Initialize(tasks.ToList());
        return graph;
    }

    /// <summary>
    /// A progress service whose loaded profile is PvP Zone — deliberately neither of the profiles
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

        Assert.Equal(1, applied[AppProfile.PveZone]);
        Assert.Equal(1, applied[AppProfile.PvpSeason]);

        // The PvE session's quest is in pve and NOWHERE else — least of all in the profile that
        // was selected the whole time.
        Assert.Equal(QuestStatus.Done, _store.QuestsOf(ProfileService.PveProfileId)[PveQuestId]);
        Assert.Equal(QuestStatus.Done, _store.QuestsOf(ProfileService.SeasonProfileId)[SeasonQuestId]);
        Assert.False(_store.QuestsOf(ProfileService.SeasonProfileId).ContainsKey(PveQuestId));
        Assert.False(_store.QuestsOf(ProfileService.PveProfileId).ContainsKey(SeasonQuestId));
        Assert.Empty(_store.QuestsOf(ProfileService.PvpProfileId));
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
            Assert.False(_store.QuestsOf(profileId).ContainsKey(OrphanQuestId),
                $"an unattributable event was written to '{profileId}'");
        }
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
        _store.Seed(ProfileService.SeasonProfileId, (SeasonQuestId, QuestStatus.Done));

        var result = await SyncAsync(daysRange: 0, SeasonQuest);

        Assert.Equal(1, result.AlreadyCurrentCount);
        Assert.Empty(result.QuestsToComplete);
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

        Assert.Equal(1, applied[AppProfile.PveZone]);
        Assert.Equal(1, applied[AppProfile.PvpSeason]);
        Assert.Equal(QuestStatus.Done, _store.QuestsOf(ProfileService.PveProfileId)[PveQuestId]);
        Assert.Equal(QuestStatus.Done, _store.QuestsOf(ProfileService.SeasonProfileId)[PveQuestId]);
    }
}
