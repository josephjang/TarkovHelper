using System.IO;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// End-to-end guard for fix-profile-data-attribution.md: syncing from a log tree that spans two
/// game modes writes each session's quests to its own profile, whatever profile is on screen.
/// <para>
/// Before this change the run below was the headline defect: a player who opens the app on the
/// seasonal profile and syncs sees their whole PvE history appear in the season, every time.
/// </para>
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class ProfileAttributionE2ETests : E2ETestBase
{
    private const string ActiveProfileSetting = "app.activeGameMode";

    [E2EFact]
    public void Syncing_while_the_season_is_selected_files_each_session_under_its_own_mode()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);

        var quests = AttributionFixtureQuests.Load();
        var logRoot = Path.Combine(configDir, "eft-logs");

        // The session that must NOT end up in the season, and the one that must.
        var pveAt = DateTime.Now.AddHours(-4);
        var seasonAt = DateTime.Now.AddHours(-2);
        WriteSession(logRoot, "log_2026.08.11_05-00-00_1.1.0.46657", "Pve", quests.PveQuestId, pveAt);
        WriteSession(logRoot, "log_2026.08.11_07-00-00_1.1.0.46657", "PvpSeason", quests.SeasonQuestId, seasonAt);

        // Seasonal profile selected, log monitoring off so nothing switches it mid-test, and the
        // wipe warning suppressed so the sync button goes straight to work.
        E2EDb.SeedSetting(configDir, ActiveProfileSetting, "SEASON");
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "False");
        E2EDb.SeedSetting(configDir, "app.hideWipeWarning", "True");
        E2EDb.SeedSetting(configDir, "app.logFolderPath", logRoot);
        E2EDb.SeedSetting(configDir, "app.syncDaysRange", "0");

        using (var app = LaunchMaximized(configDir))
        {
            WaitUntil(() => app.GetItemStatus("BtnPvpSeason") == "Selected",
                "PvP Season to be the selected profile");

            app.InvokeElement("BtnSettings");
            app.WaitForElementVisibility("BtnSyncQuest", visible: true);
            app.InvokeElement("BtnSyncQuest");

            // The summary dialog replaced the per-quest confirmation list: by the time it opens
            // the changes have already been applied, each to its own profile.
            var dialog = app.WaitForAppWindow("Quest Sync Result", timeoutSeconds: 120);

            // The summary names both profiles it wrote to AND how much landed in each (PRD R2).
            // With the review step gone this is the only signal a player gets that a sync went
            // somewhere unexpected, so a summary that named the right profiles with the wrong
            // counts would defeat the requirement without breaking anything else.
            //
            // Read by AutomationId, not by rendered wording: matching free text would make a copy
            // edit look like a regression, and would pass on any row that happened to contain the
            // words anywhere.
            var loc = TestLocalization.WithLanguage(AppLanguage.EN);
            var expected = new[]
            {
                new[] { loc.ProfileName(AppProfile.PveZone), string.Format(loc.SyncAppliedCountFormat, 1) },
                new[] { loc.ProfileName(AppProfile.PvpSeason), string.Format(loc.SyncAppliedCountFormat, 1) },
            };

            List<string[]> rows = null!;
            WaitUntil(
                () =>
                {
                    rows = AppDriver.RowsUnder(
                        dialog, "LstSyncAppliedByProfile", "TxtSyncProfileName", "TxtSyncProfileApplied");
                    return rows.Count == expected.Length;
                },
                $"the summary to list {expected.Length} profile rows");

            // Ordered by profile id in the dialog, so the pairing of name to count is asserted,
            // not just the presence of both.
            Assert.Equal(expected, rows.OrderBy(row => row[0], StringComparer.Ordinal).ToArray());

            AppDriver.Invoke(AppDriver.WaitForElementUnder(dialog, "BtnConfirm"));
            app.WaitForAppWindowClosed("Quest Sync Result");

            app.CloseAndWaitForExit();
        }

        Assert.Equal("Done", E2EDb.ReadQuestProgress(configDir, ProfileService.PveProfileId, quests.PveQuestId));
        Assert.Equal("Done", E2EDb.ReadQuestProgress(configDir, ProfileService.SeasonProfileId, quests.SeasonQuestId));

        // The whole point: the PvE session's quest never reaches the profile that was on screen.
        Assert.Null(E2EDb.ReadQuestProgress(configDir, ProfileService.SeasonProfileId, quests.PveQuestId));
        Assert.Null(E2EDb.ReadQuestProgress(configDir, ProfileService.PveProfileId, quests.SeasonQuestId));

        // ...nor the permanent PvP rows, which carry a whole account's history.
        Assert.Null(E2EDb.ReadQuestProgress(configDir, ProfileService.PvpProfileId, quests.PveQuestId));
        Assert.Null(E2EDb.ReadQuestProgress(configDir, ProfileService.PvpProfileId, quests.SeasonQuestId));
    }

    /// <summary>
    /// One live raid, recorded as EFT records it: the game notifies a completion and then, in the
    /// same flush, a failure of the same quest. LogSyncService raises both from one tail read
    /// with no wait between them, and the handler plans each event against the rows it reads
    /// first, so an unserialized handler lets the two plan against the same pre-write state and
    /// the loser's status stick. The failure is last, so the quest must end Failed.
    /// </summary>
    [E2EFact]
    public void Two_live_events_for_one_quest_are_recorded_in_log_order()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);

        var quests = AttributionFixtureQuests.Load();
        var logRoot = Path.Combine(configDir, "eft-logs");
        var folderName = "log_2026.08.11_09-00-00_1.1.0.46657";

        // A session with no notifications yet: the two below are appended once the app is up and
        // watching, so they arrive as live events rather than as history.
        var pushLog = WriteSession(logRoot, folderName, "Pve", questId: null, completedAt: DateTime.Now);

        // Selected profile is the season, which is NOT where these events belong: the live path
        // has to file them by the session's own mode (PRD R4).
        E2EDb.SeedSetting(configDir, ActiveProfileSetting, "SEASON");
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "True");
        E2EDb.SeedSetting(configDir, "app.hideWipeWarning", "True");
        E2EDb.SeedSetting(configDir, "app.logFolderPath", logRoot);

        using (var app = LaunchMaximized(configDir))
        {
            WaitUntil(() => app.IsElementVisible("BtnSettings"), "the main window to finish loading");

            var at = DateTime.Now;
            File.AppendAllText(pushLog,
                Notification(quests.PveQuestId, messageType: 12, at) +
                Notification(quests.PveQuestId, messageType: 11, at));

            WaitUntil(
                () => E2EDb.ReadQuestProgress(configDir, ProfileService.PveProfileId, quests.PveQuestId) != null,
                "the live events to reach the PvE partition");

            app.CloseAndWaitForExit();
        }

        Assert.Equal("Failed",
            E2EDb.ReadQuestProgress(configDir, ProfileService.PveProfileId, quests.PveQuestId));
        // ...and not into the profile that was on screen the whole time.
        Assert.Null(E2EDb.ReadQuestProgress(configDir, ProfileService.SeasonProfileId, quests.PveQuestId));
    }

    /// <summary>
    /// Writes one EFT session folder: an application log naming the session mode, and a
    /// push-notifications log holding one quest-completed notification (empty when
    /// <paramref name="questId"/> is null). Modelled on the capture in
    /// docs/eft-1-1-profile-selection-log-analysis.md with account and character ids removed.
    /// </summary>
    /// <returns>The path of the push-notifications log, so a test can append to it live.</returns>
    private static string WriteSession(
        string logRoot, string folderName, string sessionMode, string? questId, DateTime completedAt)
    {
        var folder = Path.Combine(logRoot, folderName);
        Directory.CreateDirectory(folder);

        File.WriteAllLines(
            Path.Combine(folder, $"{folderName} application.log"),
            new[]
            {
                $"{completedAt.AddMinutes(-10):yyyy-MM-dd HH:mm:ss.fff} 1|1.1.0.46657|Info|application|Init: pstrGameVersion:live",
                $"{completedAt.AddMinutes(-9):yyyy-MM-dd HH:mm:ss.fff} 1|1.1.0.46657|Info|application|Session mode: {sessionMode}",
            });

        var pushLog = Path.Combine(folder, $"{folderName} push-notifications_000.log");
        File.WriteAllText(
            pushLog,
            questId == null ? "" : Notification(questId, messageType: 12, completedAt));

        return pushLog;
    }

    /// <summary>
    /// One push-notification block. <paramref name="messageType"/> is EFT's own message type:
    /// 12 completes a quest, 11 fails it.
    /// </summary>
    private static string Notification(string questId, int messageType, DateTime at)
    {
        var suffix = messageType == 11 ? "failMessageText" : "successMessageText";
        return $$"""
            {{at:yyyy-MM-dd HH:mm:ss.fff}}|1.1.0.46657|Info|push-notifications|Got notification | new_message
            {
              "type": "new_message",
              "eventId": "{{Guid.NewGuid():N}}",
              "dialogId": "54cb57776803fa99248b456e",
              "message": {
                "type": {{messageType}},
                "templateId": "{{questId}} {{suffix}}",
                "dt": {{new DateTimeOffset(at).ToUnixTimeSeconds()}}
              }
            }

            """;
    }

    /// <summary>
    /// Two standalone quests from the asset DB (no prerequisites, no faction/edition/level gate)
    /// so a completion writes exactly one row per profile and the assertions stay about
    /// attribution rather than about the cascade.
    /// </summary>
    private sealed record AttributionFixtureQuests(string PveQuestId, string SeasonQuestId)
    {
        public static AttributionFixtureQuests Load()
        {
            var assetDb = Path.Combine(AppContext.BaseDirectory, "tarkov_data.db");
            using var connection = new SqliteConnection($"Data Source={assetDb};Mode=ReadOnly");
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT q.Id
                FROM Quests q
                WHERE NOT EXISTS (SELECT 1 FROM QuestRequirements r WHERE r.QuestId = q.Id)
                  AND NOT EXISTS (SELECT 1 FROM Quests a WHERE a.Id <> q.Id AND a.Name = q.Name)
                  AND q.Faction IS NULL AND q.RequiredEdition IS NULL
                  AND (q.RequiredPrestigeLevel IS NULL OR q.RequiredPrestigeLevel = 0)
                  AND (q.RequiredDecodeCount IS NULL OR q.RequiredDecodeCount = 0)
                  AND (q.MinLevel IS NULL OR q.MinLevel <= 1)
                  AND q.MinScavKarma IS NULL
                ORDER BY q.Name
                LIMIT 2";

            using var reader = command.ExecuteReader();
            var ids = new List<string>();
            while (reader.Read()) ids.Add(reader.GetString(0));

            Assert.True(ids.Count == 2,
                "tarkov_data.db has fewer than two standalone quests matching the E2E constraints");
            return new AttributionFixtureQuests(ids[0], ids[1]);
        }
    }
}
