using System.IO;
using Microsoft.Data.Sqlite;
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

            // The summary names both profiles it wrote to (PRD R2). With the review step gone
            // this is the only signal a player gets that a sync went somewhere unexpected, so a
            // silently blank summary would defeat the requirement without breaking anything else.
            WaitUntil(() => AppDriver.HasTextElementUnder(dialog, "PvE Zone"),
                "the summary to name the PvE Zone profile");
            Assert.True(AppDriver.HasTextElementUnder(dialog, "PvP Season"),
                "the summary did not name the PvP Season profile");
            Assert.True(AppDriver.HasTextElementUnder(dialog, "1 recorded"),
                "the summary did not report a per-profile applied count");

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
    /// Writes one EFT session folder: an application log naming the session mode, and a
    /// push-notifications log holding one quest-completed notification. Modelled on the capture
    /// in docs/eft-1-1-profile-selection-log-analysis.md with account and character ids removed.
    /// </summary>
    private static void WriteSession(
        string logRoot, string folderName, string sessionMode, string questId, DateTime completedAt)
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

        var unix = new DateTimeOffset(completedAt).ToUnixTimeSeconds();
        File.WriteAllText(
            Path.Combine(folder, $"{folderName} push-notifications_000.log"),
            $$"""
            {{completedAt:yyyy-MM-dd HH:mm:ss.fff}}|1.1.0.46657|Info|push-notifications|Got notification | new_message
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

    /// <summary>
    /// Two standalone quests from the asset DB — no prerequisites, no faction/edition/level gate —
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
