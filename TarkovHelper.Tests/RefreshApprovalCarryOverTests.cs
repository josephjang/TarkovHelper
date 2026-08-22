using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TarkovDBEditor.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// What the hash-based child-table upserts are FOR: a reviewer's approval survives a refresh
/// that did not change the row, is withdrawn by one that did, and the coordinates a reviewer
/// typed into an objective by hand are never regenerated away.
/// <para>
/// None of that is visible in the published database's data columns, so nothing else in this
/// suite would notice it disappearing. An approval silently reset on every run makes the
/// editor's review state worthless; a LocationPoints column silently emptied loses work no
/// refresh can rebuild, because no upstream source carries it.
/// </para>
/// <para>
/// Every fact here is read through a real refresh of a real database rather than by calling the
/// upserts, which are private: the run writes the rows, the test approves them the way the
/// editor's reviewer would, and the next run is what is under test.
/// </para>
/// </summary>
public sealed class RefreshApprovalCarryOverTests
{
    private const string StirrupId = "5c0be13186f7746309d759c8";
    private const string DebutId = "5936d90786f7742b1420ba5b";
    private const string PraporId = "54cb50c76803fa8b248b4571";

    /// <summary>
    /// The row key Stirrup is filed under, which every child row of Stirrup carries. Objectives
    /// are scoped by it rather than by their description: the fixture's stock page gives Debut
    /// the same objective line, and a query that matched on the text would be asserting about
    /// whichever of the two rows came back first.
    /// </summary>
    private static readonly string StirrupRowId = WikiQuestIdentity.IdFor("Stirrup");

    /// <summary>Every child table whose upsert carries an approval across a refresh.</summary>
    private static readonly string[] ApprovalCarryingTables =
    {
        "QuestRequirements",
        "QuestTraderRequirements",
        "OptionalQuests",
        "QuestRequiredItems",
        "QuestObjectives",
    };

    /// <summary>
    /// The approval timestamp a reviewer's click would have left. A fixed string, because the
    /// carry-over must return the stored value unchanged rather than a re-stamped one.
    /// </summary>
    private const string ApprovedAt = "2026-01-02T03:04:05.0000000Z";

    /// <summary>
    /// Wiki markup for Stirrup: the page that feeds three of the five tables at once. Its
    /// objective line becomes a QuestObjectives row, its <c>|related</c> field an OptionalQuests
    /// row, and its Related Quest Items table one QuestRequiredItems row per listed item.
    /// </summary>
    /// <param name="scavsToKill">
    /// Part of the objective description, so changing it changes the objective's content hash
    /// while its (quest, position) row key stands.
    /// </param>
    /// <param name="watchCount">
    /// The quantity column of the first required item: in the content hash, not in the row key.
    /// </param>
    /// <param name="includeLighter">
    /// Whether the second required item is listed at all. Dropping it is a row the new data no
    /// longer produces, which the upsert has to delete.
    /// </param>
    private static string StirrupPage(int scavsToKill = 10, int watchCount = 1, bool includeLighter = true)
    {
        // Built line by line rather than as an interpolated string: wiki markup is all braces
        // and pipes, which fight every interpolation delimiter C# has.
        var lines = new List<string>
        {
            "{{Infobox quest",
            "|given by = [[Prapor]]",
            "|location = [[Customs]]",
            "|related = [[Debut]]",
            "}}",
            "==Requirements==",
            "==Objectives==",
            "* Eliminate " + scavsToKill + " Scavs on [[Customs]]",
            "==Notes==",
            "{|class=\"wikitable\"",
            // The parser finds the table by looking backwards from this caption for the "{|"
            // that opens it, which is how the real pages are laid out.
            "|+Related Quest Items",
            "!Icon!!Name!!Amount!!Requirement!!Notes",
            "|-",
            "|[[File:Watch.png]]||[[Bronze pocket watch]]||" + watchCount + "||Handover item||",
        };

        if (includeLighter)
        {
            lines.Add("|-");
            lines.Add("|[[File:Zibbo.png]]||[[Golden Zibbo lighter]]||1||Handover item||");
        }

        lines.Add("|}");
        return string.Join("\n", lines);
    }

    /// <summary>
    /// A fixture whose first refresh fills all five approval-carrying tables. Debut requires
    /// Stirrup (QuestRequirements) behind a Prapor loyalty gate (QuestTraderRequirements);
    /// Stirrup's page supplies the objectives, the alternative and the required items.
    /// </summary>
    private static RefreshPipelineFixture Fixture(
        int scavsToKill = 10,
        int watchCount = 1,
        bool includeLighter = true,
        int loyaltyLevel = 2,
        string prerequisiteStatus = "complete") =>
        new RefreshPipelineFixture()
            .WithWikiPages(
                ("Stirrup", StirrupPage(scavsToKill, watchCount, includeLighter)),
                ("Debut", RefreshPipelineFixture.Page()))
            .WithTasks(
                RefreshPipelineFixture.Task(StirrupId, "Stirrup"),
                RefreshPipelineFixture.Task(
                    DebutId,
                    "Debut",
                    loyalty: new[] { (PraporId, loyaltyLevel) },
                    requires: new[] { (StirrupId, prerequisiteStatus) }))
            .WithTraders((PraporId, "Prapor"))
            .WithDatabase(("Stirrup", StirrupId), ("Debut", DebutId));

    /// <summary>
    /// Re-points the fixture's caches at a second run's inputs. Only the two caches a test
    /// varies are rewritten; the database is the one the first run just wrote.
    /// </summary>
    private static void Rewrite(
        RefreshPipelineFixture fixture,
        int scavsToKill = 10,
        int watchCount = 1,
        bool includeLighter = true,
        int loyaltyLevel = 2,
        string prerequisiteStatus = "complete")
    {
        fixture.WithWikiPages(
            ("Stirrup", StirrupPage(scavsToKill, watchCount, includeLighter)),
            ("Debut", RefreshPipelineFixture.Page()));
        fixture.WithTasks(
            RefreshPipelineFixture.Task(StirrupId, "Stirrup"),
            RefreshPipelineFixture.Task(
                DebutId,
                "Debut",
                loyalty: new[] { (PraporId, loyaltyLevel) },
                requires: new[] { (StirrupId, prerequisiteStatus) }));
    }

    /// <summary>
    /// Stamps every row of every approval-carrying table the way a reviewer working through the
    /// editor's grids would, and fails if a table is empty: an assertion about approvals
    /// surviving proves nothing over no rows.
    /// </summary>
    private static void ApproveEveryRow(RefreshPipelineFixture fixture)
    {
        using (var connection = new SqliteConnection($"Data Source={fixture.DatabasePath}"))
        {
            connection.Open();
            foreach (var table in ApprovalCarryingTables)
            {
                using var cmd = new SqliteCommand(
                    $"UPDATE {table} SET IsApproved = 1, ApprovedAt = @At", connection);
                cmd.Parameters.AddWithValue("@At", ApprovedAt);
                var stamped = cmd.ExecuteNonQuery();
                Assert.True(stamped > 0, $"{table} holds no rows to approve, so this test would prove nothing");
            }
        }

        SqliteConnection.ClearAllPools();
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

    /// <summary>The approval state of one table, as (IsApproved, ApprovedAt) per row key.</summary>
    private static Dictionary<string, (string IsApproved, string ApprovedAt)> Approvals(
        RefreshPipelineFixture fixture, string table) =>
        fixture.Query($"SELECT Id, IsApproved, ApprovedAt FROM {table}")
            .ToDictionary(r => r[0], r => (r[1], r[2]), StringComparer.Ordinal);

    private static string LogOf(RefreshResult result)
    {
        Assert.False(string.IsNullOrEmpty(result.LogPath), "the run wrote no log to read [CHANGED] out of");
        return File.ReadAllText(result.LogPath!);
    }

    [Fact]
    public async Task An_unchanged_row_keeps_the_approval_and_the_timestamp_a_reviewer_gave_it()
    {
        using var fixture = Fixture();
        Assert.True((await fixture.RefreshAsync()).Success);

        ApproveEveryRow(fixture);
        var before = ApprovalCarryingTables.ToDictionary(t => t, t => Approvals(fixture, t));

        // The same inputs a second time: not one row's content hash moves.
        var second = await fixture.RefreshAsync();
        Assert.True(second.Success, second.ErrorMessage);

        foreach (var table in ApprovalCarryingTables)
        {
            var after = Approvals(fixture, table);
            Assert.Equal(before[table].Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
            foreach (var (id, state) in after)
            {
                Assert.Equal("1", state.IsApproved);
                // The stored timestamp, not a fresh one: the approval is the reviewer's, and so
                // is the moment they gave it.
                Assert.Equal(ApprovedAt, state.ApprovedAt);
                Assert.Equal(before[table][id], state);
            }
        }

        // And nothing was reported as reset, because nothing was.
        Assert.DoesNotContain("approval reset due to content change", LogOf(second));
    }

    [Fact]
    public async Task A_prerequisite_whose_type_changed_loses_its_approval_and_the_log_names_it()
    {
        using var fixture = Fixture(prerequisiteStatus: "complete");
        Assert.True((await fixture.RefreshAsync()).Success);
        ApproveEveryRow(fixture);

        var before = Approvals(fixture, "QuestRequirements");
        var changed = Assert.Single(before).Key;

        // "active" maps to Accept where "complete" mapped to Complete: the same edge, a
        // different requirement type, so the content hash moves and the row key does not.
        Rewrite(fixture, prerequisiteStatus: "active");
        var second = await fixture.RefreshAsync();
        Assert.True(second.Success, second.ErrorMessage);

        var after = Approvals(fixture, "QuestRequirements");
        Assert.Equal("Accept", Assert.Single(fixture.Query("SELECT RequirementType FROM QuestRequirements"))[0]);
        Assert.Equal(changed, Assert.Single(after).Key);
        Assert.Equal("0", after[changed].IsApproved);
        Assert.Equal("", after[changed].ApprovedAt);
        Assert.Contains($"[CHANGED] {changed} - approval reset due to content change", LogOf(second));
    }

    [Fact]
    public async Task A_loyalty_gate_whose_level_changed_loses_its_approval_and_the_log_names_it()
    {
        using var fixture = Fixture(loyaltyLevel: 2);
        Assert.True((await fixture.RefreshAsync()).Success);
        ApproveEveryRow(fixture);

        var changed = Assert.Single(Approvals(fixture, "QuestTraderRequirements")).Key;

        Rewrite(fixture, loyaltyLevel: 3);
        var second = await fixture.RefreshAsync();
        Assert.True(second.Success, second.ErrorMessage);

        var after = Approvals(fixture, "QuestTraderRequirements");
        Assert.Equal("3", Assert.Single(fixture.Query("SELECT RequiredLevel FROM QuestTraderRequirements"))[0]);
        Assert.Equal(changed, Assert.Single(after).Key);
        Assert.Equal("0", after[changed].IsApproved);
        Assert.Equal("", after[changed].ApprovedAt);
        Assert.Contains($"[CHANGED] {changed} - approval reset due to content change", LogOf(second));
    }

    [Fact]
    public async Task A_required_item_whose_quantity_changed_loses_its_approval_and_the_log_names_it()
    {
        using var fixture = Fixture(watchCount: 1);
        Assert.True((await fixture.RefreshAsync()).Success);
        ApproveEveryRow(fixture);

        var before = Approvals(fixture, "QuestRequiredItems");
        var watch = Assert.Single(fixture.Query(
            "SELECT Id FROM QuestRequiredItems WHERE ItemName = 'Bronze pocket watch'"))[0];
        var lighter = Assert.Single(fixture.Query(
            "SELECT Id FROM QuestRequiredItems WHERE ItemName = 'Golden Zibbo lighter'"))[0];

        // The quantity is in the content hash and not in the row key, so this is one row
        // changed rather than one deleted and one inserted.
        Rewrite(fixture, watchCount: 4);
        var second = await fixture.RefreshAsync();
        Assert.True(second.Success, second.ErrorMessage);

        var after = Approvals(fixture, "QuestRequiredItems");
        Assert.Equal(before.Keys.OrderBy(k => k, StringComparer.Ordinal), after.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("0", after[watch].IsApproved);
        Assert.Equal("", after[watch].ApprovedAt);
        Assert.Contains($"[CHANGED] {watch} - approval reset due to content change", LogOf(second));

        // The untouched row beside it keeps everything: a content change withdraws one row's
        // approval, not the table's.
        Assert.Equal("1", after[lighter].IsApproved);
        Assert.Equal(ApprovedAt, after[lighter].ApprovedAt);
        Assert.DoesNotContain($"[CHANGED] {lighter}", LogOf(second));
    }

    [Fact]
    public async Task An_objective_whose_description_changed_loses_its_approval_and_the_log_names_it()
    {
        using var fixture = Fixture(scavsToKill: 10);
        Assert.True((await fixture.RefreshAsync()).Success);
        ApproveEveryRow(fixture);

        var changed = Assert.Single(fixture.Query(
            $"SELECT Id FROM QuestObjectives WHERE QuestId = '{StirrupRowId}'"))[0];

        Rewrite(fixture, scavsToKill: 12);
        var second = await fixture.RefreshAsync();
        Assert.True(second.Success, second.ErrorMessage);

        var after = Approvals(fixture, "QuestObjectives");
        Assert.Equal("0", after[changed].IsApproved);
        Assert.Equal("", after[changed].ApprovedAt);
        Assert.Contains($"[CHANGED] {changed} - approval reset due to content change", LogOf(second));
    }

    /// <summary>
    /// OptionalQuests has the same [CHANGED] branch as the other four, but nothing can reach it:
    /// the row's content hash is taken over exactly the two fields its row key is taken over, so
    /// a change to either produces a different row rather than a changed one. What is testable,
    /// and what matters, is that the approval survives.
    /// </summary>
    [Fact]
    public async Task An_alternative_quest_row_keeps_its_approval_when_the_pair_is_unchanged()
    {
        using var fixture = Fixture();
        Assert.True((await fixture.RefreshAsync()).Success);
        ApproveEveryRow(fixture);

        var before = Assert.Single(Approvals(fixture, "OptionalQuests"));

        // Every other table's content moves; this row's pair does not.
        Rewrite(fixture, scavsToKill: 12, watchCount: 4, loyaltyLevel: 3, prerequisiteStatus: "active");
        var second = await fixture.RefreshAsync();
        Assert.True(second.Success, second.ErrorMessage);

        var after = Assert.Single(Approvals(fixture, "OptionalQuests"));
        Assert.Equal(before.Key, after.Key);
        Assert.Equal("1", after.Value.IsApproved);
        Assert.Equal(ApprovedAt, after.Value.ApprovedAt);
    }

    [Fact]
    public async Task Coordinates_entered_by_hand_survive_an_update_that_rewrites_the_objective()
    {
        using var fixture = Fixture(scavsToKill: 10);
        Assert.True((await fixture.RefreshAsync()).Success);

        // The one column no refresh can regenerate: nothing upstream carries map coordinates, so
        // the only copy in the world is the one a reviewer typed into the editor.
        const string points = "[{\"x\":12.5,\"y\":-3.25,\"z\":7.0}]";
        var objective = Assert.Single(fixture.Query(
            $"SELECT Id FROM QuestObjectives WHERE QuestId = '{StirrupRowId}'"))[0];
        Execute(fixture, $"UPDATE QuestObjectives SET LocationPoints = '{points}' WHERE Id = '{objective}'");

        Rewrite(fixture, scavsToKill: 12);
        var second = await fixture.RefreshAsync();
        Assert.True(second.Success, second.ErrorMessage);

        var row = Assert.Single(fixture.Query(
            $"SELECT Description, LocationPoints FROM QuestObjectives WHERE Id = '{objective}'"));
        // The description was rewritten by the refresh, which is exactly the update that used to
        // be able to take the coordinates with it.
        Assert.Contains("12 Scavs", row[0]);
        Assert.Equal(points, row[1]);
    }

    [Fact]
    public async Task Coordinates_entered_by_hand_survive_a_refresh_that_changes_nothing()
    {
        using var fixture = Fixture();
        Assert.True((await fixture.RefreshAsync()).Success);

        const string points = "[{\"x\":1.0,\"y\":2.0,\"z\":3.0}]";
        var objective = Assert.Single(fixture.Query(
            $"SELECT Id FROM QuestObjectives WHERE QuestId = '{StirrupRowId}'"))[0];
        Execute(fixture, $"UPDATE QuestObjectives SET LocationPoints = '{points}' WHERE Id = '{objective}'");

        Assert.True((await fixture.RefreshAsync()).Success);

        Assert.Equal(points, Assert.Single(fixture.Query(
            $"SELECT LocationPoints FROM QuestObjectives WHERE Id = '{objective}'"))[0]);
    }

    [Fact]
    public async Task A_row_the_new_data_no_longer_produces_is_deleted_and_its_neighbours_are_not()
    {
        using var fixture = Fixture(includeLighter: true);
        Assert.True((await fixture.RefreshAsync()).Success);
        ApproveEveryRow(fixture);

        var watch = Assert.Single(fixture.Query(
            "SELECT Id FROM QuestRequiredItems WHERE ItemName = 'Bronze pocket watch'"))[0];
        var lighter = Assert.Single(fixture.Query(
            "SELECT Id FROM QuestRequiredItems WHERE ItemName = 'Golden Zibbo lighter'"))[0];

        // The wiki table stops listing the lighter. The list is still non-empty, so the
        // leave-the-table-alone rule does not apply and the stale row has to go.
        Rewrite(fixture, includeLighter: false);
        var second = await fixture.RefreshAsync();
        Assert.True(second.Success, second.ErrorMessage);

        var after = Approvals(fixture, "QuestRequiredItems");
        Assert.Equal(new[] { watch }, after.Keys.ToArray());
        Assert.DoesNotContain(lighter, after.Keys);
        // The survivor is untouched, approval and all.
        Assert.Equal("1", after[watch].IsApproved);
        Assert.Equal(ApprovedAt, after[watch].ApprovedAt);
    }
}
