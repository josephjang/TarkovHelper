using System.IO;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Tests;

[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class SeasonalProfileE2ETests : E2ETestBase
{
    private const string ActiveProfileSetting = "app.activeGameMode";

    [E2EFact]
    public void Three_way_selection_loads_season_rows_and_survives_restart()
    {
        var configDir = NewConfigDir();
        InitializeFullUserSchema(configDir);
        var data = SeasonalFixtureData.Load();
        SeedAllProfiles(configDir, data);

        using (var app = LaunchMaximized(configDir))
        {
            WaitForProfileControls(app);
            Assert.Equal("Selected", app.GetItemStatus("BtnPvpZone"));
            WaitUntil(() => app.GetElementText("TxtProfileChipLevel") == "Lv 12",
                "PvP ProfileSettings to load");

            ClickProfile(app, "BtnPvpSeason");
            WaitUntil(() => IsProfileSelected(app, "BtnPvpSeason"), "PvP Season to become selected");
            WaitUntil(() => app.GetElementText("TxtProfileChipLevel") == "Lv 32",
                "season ProfileSettings to load");

            AssertSeasonQuestPage(app, data);
            AssertSeasonHideoutPage(app, data);
            AssertSeasonItemsPage(app, data, "TabItems");
            AssertSeasonItemsPage(app, data, "TabCollector");
            AssertSeasonMapPage(app, data);

            WaitUntil(() => E2EDb.ReadSetting(configDir, ActiveProfileSetting) == "SEASON",
                "SEASON selection to persist");
            app.CloseAndWaitForExit();
        }

        AssertExistingPermanentRowsUnchanged(configDir, data);

        using var restarted = LaunchMaximized(configDir);
        WaitForProfileControls(restarted);
        WaitUntil(() => IsProfileSelected(restarted, "BtnPvpSeason"),
            "PvP Season selection to restore after restart");
        Assert.False(IsProfileSelected(restarted, "BtnPvpZone"));
        Assert.False(IsProfileSelected(restarted, "BtnPveZone"));
        Assert.Equal("Lv 32", restarted.GetElementText("TxtProfileChipLevel"));
    }

    [E2EFact]
    public void Log_detection_switches_symmetrically_between_all_profiles()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, ActiveProfileSetting, "PVP");

        var logRoot = Path.Combine(configDir, "eft-logs");
        var sessionDir = Path.Combine(logRoot, "log_2026.08.09_12-00-00_1.1.0.46657");
        Directory.CreateDirectory(sessionDir);
        var applicationLog = Path.Combine(sessionDir, "application.log");
        File.WriteAllText(applicationLog,
            "2026-08-09 12:00:00.000 | Session mode: PvpSeason" + Environment.NewLine);
        E2EDb.SeedSetting(configDir, "app.logFolderPath", logRoot);
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "True");

        using var app = LaunchMaximized(configDir);
        WaitForProfileControls(app);
        WaitUntil(() => app.GetElementText("TxtSyncStatus") == "Watching logs",
            "EFT log monitoring to start", timeoutSeconds: 60);
        WaitUntil(() => IsProfileSelected(app, "BtnPvpSeason"),
            "PvpSeason startup evidence to select PvP Season");
        WaitUntil(() => app.GetElementText("TxtProfileTransitionAnnouncement") ==
            "Profile changed to PvP Season from game logs",
            "the automatic season transition to be announced");

        // Exact known evidence replaces every current profile, including season.
        AppendSessionMode(applicationLog, "Pve");
        WaitUntil(() => IsProfileSelected(app, "BtnPveZone"),
            "Pve evidence to replace PvP Season", timeoutSeconds: 60);
        WaitUntil(() => app.GetElementText("TxtProfileTransitionAnnouncement") ==
            "Profile changed to PvE Zone from game logs",
            "the automatic PvE transition to be announced");

        AppendSessionMode(applicationLog, "PvpSeason");
        WaitUntil(() => IsProfileSelected(app, "BtnPvpSeason"),
            "PvpSeason evidence to replace PvE Zone", timeoutSeconds: 60);
        AppendSessionMode(applicationLog, "Regular");
        WaitUntil(() => IsProfileSelected(app, "BtnPvpZone"),
            "Regular evidence to replace PvP Season", timeoutSeconds: 60);

        app.ResizeWindow(900, 700);
        app.WaitForElementVisibility("BtnActiveProfileMenu", visible: true);

        // PRD R6: an automatic change gets a brief cue and an announcement, then the selector
        // returns to its neutral resting state. By now the 1400 ms cue has long expired, so no
        // lasting "Auto-selected from game logs" source label may survive.
        Assert.Equal(string.Empty, app.GetItemStatus("BtnActiveProfileMenu"));
        WaitUntil(() => app.GetElementText("TxtProfileTransitionAnnouncement") == string.Empty,
            "the transient transition announcement to clear");
    }

    /// <summary>
    /// AppDriver.Launch returns as soon as the titled window exists, which is before
    /// Window_Loaded finishes awaiting the user-DB and profile initialization. GetElementText is
    /// a one-shot read, so these must poll rather than assert immediately.
    /// </summary>
    private static void WaitForProfileControls(AppDriver app)
    {
        WaitUntil(() => app.GetElementText("BtnPvpZone") == "PvP Zone",
            "the PvP Zone label to render");
        WaitUntil(() => app.GetElementText("BtnPveZone") == "PvE Zone",
            "the PvE Zone label to render");
        WaitUntil(() => app.GetElementText("BtnPvpSeason") == "PvP Season",
            "the PvP Season label to render");
        WaitUntil(() => !string.IsNullOrEmpty(app.GetItemStatus("BtnPvpZone")),
            "the profile selection state to be published");
    }

    private static void ClickProfile(AppDriver app, string automationId)
        => app.SelectElement(automationId);

    // ItemStatus is localized; these tests launch a fresh config dir, so the app is in EN.
    private static bool IsProfileSelected(AppDriver app, string automationId)
        => app.GetItemStatus(automationId) == "Selected";

    private static void AssertSeasonQuestPage(AppDriver app, SeasonalFixtureData data)
    {
        app.SelectTab("TabQuests", "LstQuests");
        app.SetTextBoxValue("TxtSearch", data.QuestName);
        app.InvokeElement("ChipDone");
        WaitUntil(() => app.GetListItemCount("LstQuests") == 1,
            "the season Done quest to be the only filtered quest");
        app.SelectListItemAt("LstQuests", 0);
        WaitUntil(() => app.GetElementText("TxtDetailStatus") == "Done",
            "the season quest status to render as Done");
    }

    private static void AssertSeasonHideoutPage(AppDriver app, SeasonalFixtureData data)
    {
        app.SelectTab("TabHideout", "LstModules", bounceTabAutomationId: "TabQuests");
        app.SetTextBoxValue("TxtSearch", data.HideoutName);
        WaitUntil(() => app.GetListItemCount("LstModules") == 1,
            "the seeded hideout module to be the only filtered module");
        app.SelectListItemAt("LstModules", 0);
        WaitUntil(() => app.GetElementText("TxtCurrentLevel") == "1",
            "the season hideout level to render");
    }

    private static void AssertSeasonItemsPage(AppDriver app, SeasonalFixtureData data, string tabId)
    {
        app.SelectTab(tabId, "LstItems", bounceTabAutomationId: "TabQuests");
        app.SetTextBoxValue("TxtSearch", data.ItemName);
        WaitUntil(() => app.GetListItemCount("LstItems") == 1,
            $"the seeded item to be the only filtered row on {tabId}");
        app.SelectListItemAt("LstItems", 0);
        WaitUntil(() => app.GetTextBoxValue("TxtDetailOwnedFir") == "31",
            $"the season inventory quantity to render on {tabId}");
    }

    private static void AssertSeasonMapPage(AppDriver app, SeasonalFixtureData data)
    {
        app.SelectTab("TabMap", "CmbMapSelect", bounceTabAutomationId: "TabQuests");
        Assert.Equal(data.MapDisplayName, app.WaitForComboSelection("CmbMapSelect"));
        Thread.Sleep(3_000);
        Assert.StartsWith("1/", app.GetElementText("TxtMapProgressCount"), StringComparison.Ordinal);
    }

    private static void InitializeFullUserSchema(string configDir)
    {
        E2EDb.CreateUserDataDb(configDir);
        using var app = AppDriver.Launch(configDir);
        app.SelectTab("TabQuests", "LstQuests");
        app.CloseAndWaitForExit();
    }

    private static void AppendSessionMode(string path, string token)
        => File.AppendAllText(path,
            $"2026-08-09 12:00:{DateTime.UtcNow.Second:00}.000 | Session mode: {token}" +
            Environment.NewLine);

    private static void SeedAllProfiles(string configDir, SeasonalFixtureData data)
    {
        E2EDb.SeedSetting(configDir, ActiveProfileSetting, "PVP");
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "False");
        E2EDb.SeedSetting(configDir, "map.lastSelectedMap", data.MapKey);

        using var connection = OpenUserDb(configDir);
        using var transaction = connection.BeginTransaction();
        SeedProfile(connection, transaction, "pvp", 12, "Failed", 3, 11, false, data);
        SeedProfile(connection, transaction, "pve", 22, "Active", 2, 21, false, data);
        SeedProfile(connection, transaction, "season", 32, "Done", 1, 31, true, data);
        transaction.Commit();
    }

    private static void SeedProfile(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        int level,
        string questStatus,
        int hideoutLevel,
        int itemQuantity,
        bool objectiveCompleted,
        SeasonalFixtureData data)
    {
        Execute(connection, transaction,
            "INSERT OR REPLACE INTO ProfileSettings (ProfileId, Key, Value) VALUES ($profile, 'app.playerLevel', $value)",
            ("$profile", profileId), ("$value", level));
        Execute(connection, transaction,
            "INSERT OR REPLACE INTO QuestProgress (ProfileId, Id, NormalizedName, Status, UpdatedAt) " +
            "VALUES ($profile, $id, $name, $status, $now)",
            ("$profile", profileId), ("$id", data.QuestId), ("$name", data.QuestNormalizedName),
            ("$status", questStatus), ("$now", DateTime.UtcNow.ToString("o")));
        foreach (var prerequisiteId in data.ObjectivePrerequisiteIds)
        {
            Execute(connection, transaction,
                "INSERT OR IGNORE INTO QuestProgress " +
                "(ProfileId, Id, NormalizedName, Status, UpdatedAt) " +
                "VALUES ($profile, $id, NULL, 'Done', $now)",
                ("$profile", profileId), ("$id", prerequisiteId),
                ("$now", DateTime.UtcNow.ToString("o")));
        }
        Execute(connection, transaction,
            "INSERT OR REPLACE INTO ObjectiveProgress (ProfileId, Id, QuestId, IsCompleted, UpdatedAt) " +
            "VALUES ($profile, $id, $quest, $done, $now)",
            ("$profile", profileId), ("$id", $"id:{data.ObjectiveId}"), ("$quest", data.ObjectiveQuestId),
            ("$done", objectiveCompleted ? 1 : 0), ("$now", DateTime.UtcNow.ToString("o")));
        Execute(connection, transaction,
            "INSERT OR REPLACE INTO HideoutProgress (ProfileId, StationId, Level, UpdatedAt) " +
            "VALUES ($profile, $station, $level, $now)",
            ("$profile", profileId), ("$station", data.HideoutNormalizedName),
            ("$level", hideoutLevel), ("$now", DateTime.UtcNow.ToString("o")));
        Execute(connection, transaction,
            "INSERT OR REPLACE INTO ItemInventory " +
            "(ProfileId, ItemNormalizedName, FirQuantity, NonFirQuantity, UpdatedAt) " +
            "VALUES ($profile, $item, $fir, 0, $now)",
            ("$profile", profileId), ("$item", data.ItemId), ("$fir", itemQuantity),
            ("$now", DateTime.UtcNow.ToString("o")));
    }

    private static void AssertExistingPermanentRowsUnchanged(string configDir, SeasonalFixtureData data)
    {
        using var connection = OpenUserDb(configDir);
        Assert.Equal("12", Scalar(connection,
            "SELECT Value FROM ProfileSettings WHERE ProfileId='pvp' AND Key='app.playerLevel'"));
        Assert.Equal("22", Scalar(connection,
            "SELECT Value FROM ProfileSettings WHERE ProfileId='pve' AND Key='app.playerLevel'"));
        Assert.Equal("Failed", Scalar(connection,
            "SELECT Status FROM QuestProgress WHERE ProfileId='pvp' AND Id=$id", ("$id", data.QuestId)));
        Assert.Equal("Active", Scalar(connection,
            "SELECT Status FROM QuestProgress WHERE ProfileId='pve' AND Id=$id", ("$id", data.QuestId)));
        Assert.Equal("0", Scalar(connection,
            "SELECT IsCompleted FROM ObjectiveProgress WHERE ProfileId='pvp' AND Id=$id",
            ("$id", $"id:{data.ObjectiveId}")));
        Assert.Equal("0", Scalar(connection,
            "SELECT IsCompleted FROM ObjectiveProgress WHERE ProfileId='pve' AND Id=$id",
            ("$id", $"id:{data.ObjectiveId}")));
        Assert.Equal("3", Scalar(connection,
            "SELECT Level FROM HideoutProgress WHERE ProfileId='pvp' AND StationId=$id", ("$id", data.HideoutNormalizedName)));
        Assert.Equal("2", Scalar(connection,
            "SELECT Level FROM HideoutProgress WHERE ProfileId='pve' AND StationId=$id", ("$id", data.HideoutNormalizedName)));
        Assert.Equal("11", Scalar(connection,
            "SELECT FirQuantity FROM ItemInventory WHERE ProfileId='pvp' AND ItemNormalizedName=$id", ("$id", data.ItemId)));
        Assert.Equal("21", Scalar(connection,
            "SELECT FirQuantity FROM ItemInventory WHERE ProfileId='pve' AND ItemNormalizedName=$id", ("$id", data.ItemId)));
    }

    private static SqliteConnection OpenUserDb(string configDir)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(configDir, "user_data.db")}");
        connection.Open();
        return connection;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static string? Scalar(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToString(command.ExecuteScalar());
    }

    private sealed record SeasonalFixtureData(
        string QuestId,
        string QuestNormalizedName,
        string QuestName,
        string HideoutNormalizedName,
        string HideoutName,
        string ItemId,
        string ItemName,
        string ObjectiveId,
        string ObjectiveQuestId,
        string[] ObjectivePrerequisiteIds,
        string MapKey,
        string MapDisplayName)
    {
        public static SeasonalFixtureData Load()
        {
            var assetDb = TestSeed.DatabasePath;
            using var connection = new SqliteConnection($"Data Source={assetDb};Mode=ReadOnly");
            connection.Open();

            var quest = ReadRow(connection, @"
                SELECT Id,
                       lower(replace(replace(replace(Name, ' ', '-'), '''', ''), '.', '')),
                       Name
                FROM Quests q
                WHERE NOT EXISTS (SELECT 1 FROM QuestRequirements r WHERE r.QuestId = q.Id)
                  AND q.Faction IS NULL AND q.RequiredEdition IS NULL
                  AND (q.RequiredPrestigeLevel IS NULL OR q.RequiredPrestigeLevel = 0)
                  AND (q.RequiredDecodeCount IS NULL OR q.RequiredDecodeCount = 0)
                  AND (q.MinLevel IS NULL OR q.MinLevel <= 12)
                  AND q.MinScavKarma IS NULL
                ORDER BY q.Name LIMIT 1", 3, "standalone quest");

            var hideout = ReadRow(connection, @"
                SELECT s.NormalizedName, s.Name
                FROM HideoutStations s
                WHERE s.NormalizedName IS NOT NULL AND s.NormalizedName <> ''
                  AND (SELECT MAX(l.Level) FROM HideoutLevels l WHERE l.StationId=s.Id) >= 3
                ORDER BY s.Name LIMIT 1", 2, "hideout module with three levels");

            var item = ReadRow(connection, @"
                SELECT i.Id, i.Name
                FROM Quests q
                JOIN QuestRequiredItems r ON r.QuestId=q.Id
                JOIN Items i ON i.Id=r.ItemId
                WHERE lower(q.Name)='collector'
                  AND lower(ifnull(i.Category, '')) <> 'quest items'
                ORDER BY i.Name LIMIT 1", 2, "Collector item");

            var objective = ReadRow(connection, @"
                SELECT o.Id, q.Id, coalesce(nullif(o.MapName, ''), q.Location)
                FROM QuestObjectives o
                JOIN Quests q ON q.Id=o.QuestId
                WHERE lower(ifnull(coalesce(nullif(o.MapName, ''), q.Location), '')) IN
                      ('customs','woods','shoreline','interchange','reserve','lighthouse','factory','groundzero','labs')
                  AND ((o.LocationPoints IS NOT NULL AND o.LocationPoints <> '')
                       OR (o.OptionalPoints IS NOT NULL AND o.OptionalPoints <> ''))
                  AND q.Faction IS NULL AND q.RequiredEdition IS NULL
                  AND (q.RequiredPrestigeLevel IS NULL OR q.RequiredPrestigeLevel=0)
                  AND (q.RequiredDecodeCount IS NULL OR q.RequiredDecodeCount=0)
                  AND (q.MinLevel IS NULL OR q.MinLevel <= 32)
                  AND q.MinScavKarma IS NULL
                ORDER BY CASE lower(coalesce(nullif(o.MapName, ''), q.Location))
                           WHEN 'customs' THEN 0 ELSE 1 END, o.Id
                LIMIT 1", 3, "active located objective");

            var objectivePrerequisites = ReadStrings(connection, @"
                WITH RECURSIVE prerequisites(Id) AS (
                    SELECT RequiredQuestId FROM QuestRequirements WHERE QuestId=$questId
                    UNION
                    SELECT r.RequiredQuestId
                    FROM QuestRequirements r
                    JOIN prerequisites p ON r.QuestId=p.Id
                )
                SELECT Id FROM prerequisites", objective[1]);

            var mapKey = objective[2] switch
            {
                "GroundZero" => "GroundZero",
                var value => value
            };
            var displayName = mapKey switch
            {
                "GroundZero" => "Ground Zero",
                "StreetsOfTarkov" => "Streets of Tarkov",
                _ => mapKey
            };

            return new SeasonalFixtureData(
                quest[0], quest[1], quest[2],
                hideout[0], hideout[1],
                item[0], item[1],
                objective[0], objective[1], objectivePrerequisites, mapKey, displayName);
        }

        private static string[] ReadStrings(SqliteConnection connection, string sql, string questId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$questId", questId);
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read()) values.Add(reader.GetString(0));
            return values.ToArray();
        }

        private static string[] ReadRow(
            SqliteConnection connection,
            string sql,
            int fieldCount,
            string description)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read(), $"tarkov_data.db has no {description} matching the E2E constraints");
            return Enumerable.Range(0, fieldCount).Select(reader.GetString).ToArray();
        }
    }
}
