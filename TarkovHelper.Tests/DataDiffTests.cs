using System.IO;
using DataDiff;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Tests;

/// <summary>
/// The comparison behind the review artefact.
/// <para>
/// A 1.1 refresh changes essentially every quest row, so the report is what gets read before a
/// publish instead of the database itself. That makes the report's own correctness load-bearing:
/// a rename that reads as one quest removed and another added would bury the eight title reuses
/// in three hundred rows of noise, and a removed column that went unmentioned would break every
/// build in the field.
/// </para>
/// </summary>
public sealed class DataDiffTests : IDisposable
{
    private readonly string _directory;

    public DataDiffTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "datadiff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    #region Quest membership

    [Fact]
    public void A_rename_reads_as_a_rename_not_as_a_removal_and_an_addition()
    {
        var previous = Database("previous.db", quests: new[]
        {
            Quest("key-1", "A Shooter Born in Heaven", bsgId: "5c0bde0986f77479cf22c2f8"),
        });
        var candidate = Database("candidate.db", quests: new[]
        {
            Quest("key-1", "Shooter Born in Heaven", bsgId: "5c0bde0986f77479cf22c2f8"),
        });

        var join = QuestJoin.Build(DataSnapshot.Read(previous), DataSnapshot.Read(candidate));

        Assert.Empty(join.Added);
        Assert.Empty(join.Removed);
        var renamed = Assert.Single(join.Renamed);
        Assert.Equal("A Shooter Born in Heaven", renamed.Previous.Name);
        Assert.Equal(QuestMatchKind.ExternalId, renamed.MatchedBy);
    }

    [Fact]
    public void A_quest_that_kept_its_row_key_but_has_no_external_id_still_matches()
    {
        // Every published quest is in this state before the backfill, and the seasonal quests
        // stay in it until the API carries them.
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Uninvited Guests - Part 1") });
        var candidate = Database("candidate.db", quests: new[] { Quest("key-1", "Uninvited Guests - Part 1") });

        var join = QuestJoin.Build(DataSnapshot.Read(previous), DataSnapshot.Read(candidate));

        Assert.Equal(QuestMatchKind.RowKey, Assert.Single(join.Pairs).MatchedBy);
    }

    [Fact]
    public void A_title_that_changed_owner_is_called_out()
    {
        // The Sew it Good rotation: the Part 4 page belongs to a different quest than before.
        var previous = Database("previous.db", quests: new[]
        {
            Quest("key-4", "Sew it Good - Part 4", bsgId: "5ae4497b86f7744cf402ed00"),
            Quest("key-3", "Sew it Good - Part 3", bsgId: "5ae4496986f774459e77beb6"),
        });
        var candidate = Database("candidate.db", quests: new[]
        {
            Quest("key-4", "Sew it Good - Part 2", bsgId: "5ae4497b86f7744cf402ed00"),
            Quest("key-3", "Sew it Good - Part 4", bsgId: "5ae4496986f774459e77beb6"),
        });

        var join = QuestJoin.Build(DataSnapshot.Read(previous), DataSnapshot.Read(candidate));

        var reuse = Assert.Single(join.TitleReuses);
        Assert.Equal("Sew it Good - Part 4", reuse.Name);
        Assert.Equal("5ae4497b86f7744cf402ed00", reuse.PreviousBsgId);
        Assert.Equal("5ae4496986f774459e77beb6", reuse.CandidateBsgId);
    }

    [Fact]
    public void Added_and_removed_quests_are_separated()
    {
        var previous = Database("previous.db", quests: new[] { Quest("gone", "Removed Quest", bsgId: "aaaaaaaaaaaaaaaaaaaaaaaa") });
        var candidate = Database("candidate.db", quests: new[] { Quest("fresh", "New Quest", bsgId: "bbbbbbbbbbbbbbbbbbbbbbbb") });

        var join = QuestJoin.Build(DataSnapshot.Read(previous), DataSnapshot.Read(candidate));

        Assert.Equal("New Quest", Assert.Single(join.Added).Name);
        Assert.Equal("Removed Quest", Assert.Single(join.Removed).Name);
        Assert.Empty(join.Pairs);
    }

    #endregion

    #region Report sections

    [Fact]
    public void Reports_a_new_column_and_a_new_table_as_additions()
    {
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", normalizedName: "stirrup") },
            withNormalizedName: true,
            withTraderRequirements: true);

        var report = Render(previous, candidate);

        Assert.Contains("Added column `Quests.NormalizedName`", report);
        Assert.Contains("Added table `QuestTraderRequirements`", report);
        Assert.DoesNotContain("**Removed column**", report);
    }

    [Fact]
    public void Calls_a_removed_column_out_as_breaking()
    {
        // The whole data channel rests on the published schema only ever growing, so a removal
        // has to be impossible to skim past.
        var previous = Database("previous.db",
            quests: new[] { Quest("key-1", "Stirrup", normalizedName: "stirrup") },
            withNormalizedName: true);
        var candidate = Database("candidate.db", quests: new[] { Quest("key-1", "Stirrup") });

        var report = Render(previous, candidate);

        Assert.Contains("**Removed column** `Quests.NormalizedName`", report);
        Assert.Contains("breaks every build", report);
    }

    [Fact]
    public void Lists_every_kappa_and_level_change_in_full()
    {
        var previous = Database("previous.db", quests: new[]
        {
            Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8", kappaRequired: true, minLevel: 15),
        });
        var candidate = Database("candidate.db", quests: new[]
        {
            Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8", kappaRequired: false, minLevel: null),
        });

        var report = Render(previous, candidate);

        Assert.Contains("### KappaRequired", report);
        Assert.Contains("| Stirrup | 1 | 0 |", report);
        Assert.Contains("### MinLevel", report);
        Assert.Contains("| Stirrup | 15 | _(none)_ |", report);
    }

    [Fact]
    public void Reports_prerequisite_edges_by_quest_name()
    {
        // Named rather than keyed so an edge that only moved because a row key was reissued does
        // not read as a change.
        var previous = Database("previous.db",
            quests: new[]
            {
                Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8"),
                Quest("key-2", "Collector", bsgId: "5c51aac186f77432ea65c552"),
            },
            requirements: new[] { ("key-1", "key-2", "Complete") });
        var candidate = Database("candidate.db",
            quests: new[]
            {
                Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8"),
                Quest("key-2", "Collector", bsgId: "5c51aac186f77432ea65c552"),
            });

        var report = Render(previous, candidate);

        Assert.Contains("Edges removed: 1", report);
        Assert.Contains("| Stirrup | - | Collector (Complete) |", report);
    }

    [Fact]
    public void Reports_objective_lists_whose_shape_changed()
    {
        var previous = Database("previous.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") },
            objectives: new[] { ("key-1", 0, "Eliminate 3 PMCs with a pistol") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") },
            objectives: new[]
            {
                ("key-1", 0, "Eliminate 10 targets with a pistol on Factory"),
                ("key-1", 1, "Hand over the items"),
            });

        var report = Render(previous, candidate);

        Assert.Contains("Quests affected: 1", report);
        Assert.Contains("| Stirrup | 1 | 2 |", report);
    }

    [Fact]
    public void Reports_the_loyalty_gates_the_candidate_carries()
    {
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Chemical - Part 3") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Chemical - Part 3") },
            withTraderRequirements: true,
            traderGates: new[] { ("key-1", "Jaeger", 2) });

        var report = Render(previous, candidate);

        Assert.Contains("| Chemical - Part 3 | Jaeger LL2 |", report);
    }

    [Fact]
    public void Reports_null_rates_for_the_columns_that_matter()
    {
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db",
            quests: new[] { Quest("key-1", "Stirrup", bsgId: "5c0be13186f7746309d759c8") });

        var report = Render(previous, candidate);

        Assert.Contains("| Quests.BsgId | 1/1 (100%) | 0/1 (0%) |", report);
    }

    [Fact]
    public void Reports_the_hideout_join_coverage()
    {
        // 0 of 317 rows joined in the published data, because every item's external ID was NULL.
        var previous = Database("previous.db",
            items: new[] { ("item-1", "Roubles", (string?)null) },
            hideoutRequirements: new[] { ("station-1", 1, "5449016a4bdc2d6f028b456f", 400000) });
        var candidate = Database("candidate.db",
            items: new[] { ("item-1", "Roubles", (string?)"5449016a4bdc2d6f028b456f") },
            hideoutRequirements: new[] { ("station-1", 1, "5449016a4bdc2d6f028b456f", 400000) });

        var report = Render(previous, candidate);

        Assert.Contains("0/1 (0%) -> 1/1 (100%)", report);
    }

    [Fact]
    public void Reports_renamed_items_because_they_lose_their_inventory_count()
    {
        var previous = Database("previous.db", items: new[] { ("item-1", "Old Widget", (string?)null) });
        var candidate = Database("candidate.db", items: new[] { ("item-1", "New Widget", (string?)null) });

        var report = Render(previous, candidate);

        Assert.Contains("Renamed (row key kept): 1", report);
        Assert.Contains("| Old Widget | New Widget |", report);
    }

    #endregion

    #region Icon coverage

    [Fact]
    public void Icon_coverage_separates_missing_icons_from_orphan_files()
    {
        var iconDirectory = Path.Combine(_directory, "icons");
        Directory.CreateDirectory(iconDirectory);
        File.WriteAllText(Path.Combine(iconDirectory, "item-1.png"), "");
        File.WriteAllText(Path.Combine(iconDirectory, "orphan.png"), "");
        // A download that produced something other than a PNG is invisible in the database and
        // shows as a blank icon in the app.
        File.WriteAllText(Path.Combine(iconDirectory, "item-2.webp"), "");

        var items = new[]
        {
            new ItemRow("item-1", null, "Has Icon"),
            new ItemRow("item-2", null, "Downloaded As WebP"),
        };

        var coverage = IconCoverage.Measure(items, iconDirectory);

        Assert.Equal(1, coverage.ItemsWithIcon);
        Assert.Equal(new[] { "Downloaded As WebP" }, coverage.ItemsWithoutIcon);
        Assert.Equal(new[] { "orphan.png" }, coverage.OrphanFiles);
        Assert.Equal(new[] { "item-2.webp" }, coverage.NonPngFiles);
    }

    [Fact]
    public void A_missing_icon_folder_reports_every_item_as_uncovered()
    {
        var coverage = IconCoverage.Measure(
            new[] { new ItemRow("item-1", null, "Widget") },
            Path.Combine(_directory, "no-such-folder"));

        Assert.Equal(0, coverage.ItemsWithIcon);
        Assert.Single(coverage.ItemsWithoutIcon);
    }

    [Fact]
    public void The_report_says_when_icons_were_not_checked()
    {
        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db", quests: new[] { Quest("key-1", "Stirrup") });

        Assert.Contains("Not checked (pass `--icons <dir>`)", Render(previous, candidate));
    }

    #endregion

    #region Refresh log

    [Fact]
    public void The_refresh_log_contributes_what_never_reached_the_database()
    {
        var log = RefreshLog.Parse("""
            {
              "writtenAt": "2026-08-22T00:00:00Z",
              "counts": {"quests": 480, "heldBackPages": 49},
              "heldBackPages": [{"Title":"Arena: First Blood","Reason":"no game record"}],
              "wikiOnlySeasonal": ["Uninvited Guests - Part 1"],
              "tasksWithoutPage": [{"TaskId":"5936d90786f7742b1420ba5b","NameEN":"The Huntsman Path - Control"}],
              "collisions": [{"Title":"The Tarkov Shooter - Part 5","CandidateTaskIds":["a","b"],"ChosenTaskId":"b","Rule":"RequiredByAnotherTask"}],
              "prerequisiteDisagreements": [{"Quest":"Sew it Good - Part 4","Verdict":"taskSuperset","Wiki":[],"Game":["Sew it Good - Part 3"]}],
              "unusedAliases": [{"PageTitle":"New Beginning (Prestige 2)","TaskId":"6761ff17cdc36bd66102e9d0","UpstreamIssue":"issue-851"}]
            }
            """, "test.json");

        var previous = Database("previous.db", quests: new[] { Quest("key-1", "Stirrup") });
        var candidate = Database("candidate.db", quests: new[] { Quest("key-1", "Stirrup") });

        var report = DiffReport.Render(
            DataSnapshot.Read(previous),
            DataSnapshot.Read(candidate),
            new DiffOptions { RefreshLog = log });

        Assert.Contains("Uninvited Guests - Part 1", report);
        Assert.Contains("Arena: First Blood", report);
        Assert.Contains("The Huntsman Path - Control", report);
        Assert.Contains("RequiredByAnotherTask", report);
        Assert.Contains("taskSuperset", report);
        Assert.Contains("New Beginning (Prestige 2)", report);
    }

    [Fact]
    public void A_refresh_log_that_is_not_json_fails_with_its_name()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RefreshLog.Parse("<html>", "refresh_1.json"));
        Assert.Contains("refresh_1.json", ex.Message);
    }

    #endregion

    #region Fixtures

    private static string Render(string previousPath, string candidatePath) =>
        DiffReport.Render(DataSnapshot.Read(previousPath), DataSnapshot.Read(candidatePath));

    private static (string Id, string Name, string? BsgId, bool Kappa, int? MinLevel, string? NormalizedName) Quest(
        string id,
        string name,
        string? bsgId = null,
        bool kappaRequired = false,
        int? minLevel = null,
        string? normalizedName = null) =>
        (id, name, bsgId, kappaRequired, minLevel, normalizedName);

    private string Database(
        string fileName,
        (string Id, string Name, string? BsgId, bool Kappa, int? MinLevel, string? NormalizedName)[]? quests = null,
        (string Id, string Name, string? BsgId)[]? items = null,
        (string QuestId, string RequiredQuestId, string Type)[]? requirements = null,
        (string QuestId, int SortOrder, string Description)[]? objectives = null,
        (string QuestId, string TraderName, int Level)[]? traderGates = null,
        (string StationId, int Level, string ItemId, int Count)[]? hideoutRequirements = null,
        bool withNormalizedName = false,
        bool withTraderRequirements = false)
    {
        var path = Path.Combine(_directory, fileName);
        withNormalizedName |= quests?.Any(q => q.NormalizedName != null) == true;
        withTraderRequirements |= traderGates != null;

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();

            Execute(connection,
                "CREATE TABLE Quests (Id TEXT PRIMARY KEY, BsgId TEXT, Name TEXT NOT NULL, NameEN TEXT, NameKO TEXT, "
                + "NameJA TEXT, Trader TEXT, Location TEXT, MinLevel INTEGER, MinScavKarma INTEGER, "
                + "KappaRequired INTEGER NOT NULL DEFAULT 0, Faction TEXT, RequiredEdition TEXT, ExcludedEdition TEXT, "
                + "RequiredPrestigeLevel INTEGER, RequiredDecodeCount INTEGER"
                + (withNormalizedName ? ", NormalizedName TEXT)" : ")"));
            Execute(connection, "CREATE TABLE Items (Id TEXT PRIMARY KEY, BsgId TEXT, Name TEXT NOT NULL)");
            Execute(connection,
                "CREATE TABLE QuestRequirements (Id TEXT PRIMARY KEY, QuestId TEXT NOT NULL, RequiredQuestId TEXT NOT NULL, "
                + "RequirementType TEXT NOT NULL, GroupId INTEGER NOT NULL DEFAULT 0)");
            Execute(connection,
                "CREATE TABLE QuestObjectives (Id TEXT PRIMARY KEY, QuestId TEXT NOT NULL, SortOrder INTEGER NOT NULL, "
                + "Description TEXT NOT NULL)");
            Execute(connection,
                "CREATE TABLE HideoutItemRequirements (Id TEXT PRIMARY KEY, StationId TEXT NOT NULL, Level INTEGER NOT NULL, "
                + "ItemId TEXT NOT NULL, Count INTEGER NOT NULL)");

            if (withTraderRequirements)
            {
                Execute(connection,
                    "CREATE TABLE QuestTraderRequirements (Id TEXT PRIMARY KEY, QuestId TEXT NOT NULL, TraderId TEXT NOT NULL, "
                    + "TraderName TEXT NOT NULL, RequiredLevel INTEGER NOT NULL)");
            }

            foreach (var quest in quests ?? Array.Empty<(string, string, string?, bool, int?, string?)>())
            {
                var columns = "Id, BsgId, Name, KappaRequired, MinLevel" + (withNormalizedName ? ", NormalizedName" : "");
                var values = "@Id, @BsgId, @Name, @Kappa, @MinLevel" + (withNormalizedName ? ", @NormalizedName" : "");
                using var cmd = new SqliteCommand($"INSERT INTO Quests ({columns}) VALUES ({values})", connection);
                cmd.Parameters.AddWithValue("@Id", quest.Id);
                cmd.Parameters.AddWithValue("@BsgId", (object?)quest.BsgId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Name", quest.Name);
                cmd.Parameters.AddWithValue("@Kappa", quest.Kappa ? 1 : 0);
                cmd.Parameters.AddWithValue("@MinLevel", (object?)quest.MinLevel ?? DBNull.Value);
                if (withNormalizedName)
                    cmd.Parameters.AddWithValue("@NormalizedName", (object?)quest.NormalizedName ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            foreach (var (id, name, bsgId) in items ?? Array.Empty<(string, string, string?)>())
            {
                using var cmd = new SqliteCommand("INSERT INTO Items (Id, BsgId, Name) VALUES (@Id, @BsgId, @Name)", connection);
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@BsgId", (object?)bsgId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.ExecuteNonQuery();
            }

            var requirementIndex = 0;
            foreach (var (questId, requiredQuestId, type) in requirements ?? Array.Empty<(string, string, string)>())
            {
                using var cmd = new SqliteCommand(
                    "INSERT INTO QuestRequirements (Id, QuestId, RequiredQuestId, RequirementType) "
                    + "VALUES (@Id, @QuestId, @RequiredQuestId, @Type)", connection);
                cmd.Parameters.AddWithValue("@Id", $"req-{requirementIndex++}");
                cmd.Parameters.AddWithValue("@QuestId", questId);
                cmd.Parameters.AddWithValue("@RequiredQuestId", requiredQuestId);
                cmd.Parameters.AddWithValue("@Type", type);
                cmd.ExecuteNonQuery();
            }

            var objectiveIndex = 0;
            foreach (var (questId, sortOrder, description) in objectives ?? Array.Empty<(string, int, string)>())
            {
                using var cmd = new SqliteCommand(
                    "INSERT INTO QuestObjectives (Id, QuestId, SortOrder, Description) VALUES (@Id, @QuestId, @SortOrder, @Description)",
                    connection);
                cmd.Parameters.AddWithValue("@Id", $"obj-{objectiveIndex++}");
                cmd.Parameters.AddWithValue("@QuestId", questId);
                cmd.Parameters.AddWithValue("@SortOrder", sortOrder);
                cmd.Parameters.AddWithValue("@Description", description);
                cmd.ExecuteNonQuery();
            }

            var gateIndex = 0;
            foreach (var (questId, traderName, level) in traderGates ?? Array.Empty<(string, string, int)>())
            {
                using var cmd = new SqliteCommand(
                    "INSERT INTO QuestTraderRequirements (Id, QuestId, TraderId, TraderName, RequiredLevel) "
                    + "VALUES (@Id, @QuestId, @TraderId, @TraderName, @Level)", connection);
                cmd.Parameters.AddWithValue("@Id", $"gate-{gateIndex++}");
                cmd.Parameters.AddWithValue("@QuestId", questId);
                cmd.Parameters.AddWithValue("@TraderId", "trader-" + traderName);
                cmd.Parameters.AddWithValue("@TraderName", traderName);
                cmd.Parameters.AddWithValue("@Level", level);
                cmd.ExecuteNonQuery();
            }

            var hideoutIndex = 0;
            foreach (var (stationId, level, itemId, count) in hideoutRequirements ?? Array.Empty<(string, int, string, int)>())
            {
                using var cmd = new SqliteCommand(
                    "INSERT INTO HideoutItemRequirements (Id, StationId, Level, ItemId, Count) "
                    + "VALUES (@Id, @StationId, @Level, @ItemId, @Count)", connection);
                cmd.Parameters.AddWithValue("@Id", $"hir-{hideoutIndex++}");
                cmd.Parameters.AddWithValue("@StationId", stationId);
                cmd.Parameters.AddWithValue("@Level", level);
                cmd.Parameters.AddWithValue("@ItemId", itemId);
                cmd.Parameters.AddWithValue("@Count", count);
                cmd.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = new SqliteCommand(sql, connection);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    #endregion
}
