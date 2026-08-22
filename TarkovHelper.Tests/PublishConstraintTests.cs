using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using TarkovDBEditor.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// The publish constraints read off a candidate <c>tarkov_data.db</c>, and the gate that stops a
/// publish that would fail them.
/// <para>
/// The refresh checks the same rules over the rows it builds, but the build phase is not the only
/// way a row reaches the file: a hand edit in the editor's DataGrid, a <c>BsgIdBackfillService</c>
/// run, or any correction made after a refresh writes straight to the database, and
/// <c>DatabaseService</c>'s generic UPDATE can rewrite <c>Quests.Name</c> without
/// <c>NormalizedName</c>. Everything that reached the file that way used to publish unchecked, and
/// a desynchronized normalized name un-keys that quest's recorded progress in every install,
/// silently, with no way to repair it afterwards.
/// </para>
/// </summary>
public sealed class PublishConstraintTests : IDisposable
{
    private const string DatabaseFile = "tarkov_data.db";
    private const string VersionFile = "db_version.txt";

    private readonly TempStoreRoot _temp = new("publishconstraints");

    public void Dispose() => _temp.Dispose();

    #region Fixtures

    /// <summary>
    /// The published format: <c>Quests.NormalizedName</c> and
    /// <c>QuestRequirements.AltRequirementType</c> both present. Only the columns the
    /// constraints read, because a candidate is checked through the same feature detection the
    /// app uses and nothing here depends on the columns it does not look at.
    /// </summary>
    private const string CurrentSchema = @"
        CREATE TABLE Quests (
            Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Faction TEXT, Trader TEXT, NormalizedName TEXT);
        CREATE TABLE QuestRequirements (
            Id TEXT PRIMARY KEY, QuestId TEXT NOT NULL, RequiredQuestId TEXT NOT NULL,
            RequirementType TEXT NOT NULL DEFAULT 'Complete', AltRequirementType TEXT,
            GroupId INTEGER NOT NULL DEFAULT 0);";

    /// <summary>
    /// The format the database in the field was published under: no <c>NormalizedName</c> column
    /// and no <c>AltRequirementType</c>. A candidate in this shape has to pass rather than fail
    /// for lacking a column, and still be checked on everything the app reads off it.
    /// </summary>
    private const string PreColumnSchema = @"
        CREATE TABLE Quests (
            Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Faction TEXT, Trader TEXT);
        CREATE TABLE QuestRequirements (
            Id TEXT PRIMARY KEY, QuestId TEXT NOT NULL, RequiredQuestId TEXT NOT NULL,
            RequirementType TEXT NOT NULL DEFAULT 'Complete', GroupId INTEGER NOT NULL DEFAULT 0);";

    /// <summary>A SQL string literal, or NULL.</summary>
    private static string Sql(string? value) =>
        value == null ? "NULL" : "'" + value.Replace("'", "''") + "'";

    /// <summary>
    /// A quest row as a clean run writes it: the row key minted from the title, and the
    /// normalized name the app computes from that same title.
    /// </summary>
    private static string Quest(
        string title,
        string? name = null,
        string? normalizedName = null,
        string? trader = "Prapor",
        string? faction = null,
        bool storesNormalizedName = true)
    {
        var id = WikiQuestIdentity.IdFor(title);
        var columns = storesNormalizedName ? "(Id, Name, Faction, Trader, NormalizedName)" : "(Id, Name, Faction, Trader)";
        var values =
            $"{Sql(id)}, {Sql(name ?? title)}, {Sql(faction)}, {Sql(trader)}"
            + (storesNormalizedName
                ? $", {Sql(normalizedName ?? QuestNormalizedName.SqlForm(title))}"
                : "");

        return $"INSERT INTO Quests {columns} VALUES ({values});";
    }

    /// <summary>A prerequisite edge between two quests, named by their titles.</summary>
    private static string Requirement(
        string questTitle,
        string requiredTitle,
        string requirementType = "Complete",
        string? altRequirementType = null,
        int groupId = 0,
        string? rowKey = null,
        bool storesAltRequirementType = true)
    {
        var questId = WikiQuestIdentity.IdFor(questTitle);
        var requiredId = WikiQuestIdentity.IdFor(requiredTitle);
        var columns = storesAltRequirementType
            ? "(Id, QuestId, RequiredQuestId, RequirementType, AltRequirementType, GroupId)"
            : "(Id, QuestId, RequiredQuestId, RequirementType, GroupId)";
        var values =
            $"{Sql(rowKey ?? $"{questTitle}|{requiredTitle}|{groupId}")}, {Sql(questId)}, {Sql(requiredId)}, "
            + $"{Sql(requirementType)}"
            + (storesAltRequirementType ? $", {Sql(altRequirementType)}" : "")
            + $", {groupId}";

        return $"INSERT INTO QuestRequirements {columns} VALUES ({values});";
    }

    /// <summary>A candidate database file on disk, built from the schema and rows given.</summary>
    private string NewCandidate(string schema, params string[] rows)
    {
        var path = Path.Combine(_temp.NewFolder("candidate"), DatabaseFile);
        TestSqlite.BuildDatabaseAt(path, schema + string.Join("\n", rows));
        return path;
    }

    /// <summary>Two quests and one prerequisite, all of it exactly as a clean run writes it.</summary>
    private string NewCleanCandidate() => NewCandidate(
        CurrentSchema,
        Quest("Stirrup"),
        Quest("Debut"),
        Requirement("Debut", "Stirrup"));

    private static async Task<IReadOnlyList<string>> ProblemsOf(string databasePath) =>
        PublishConstraints.Problems(await PublishConstraints.ReadAsync(databasePath));

    /// <summary>The one problem this candidate holds, so a test cannot pass on a different one.</summary>
    private static async Task<string> TheProblemWith(string databasePath)
    {
        var problems = await ProblemsOf(databasePath);
        return Assert.Single(problems);
    }

    #endregion

    #region The rules, over a candidate file

    [Fact]
    public async Task A_clean_candidate_holds_no_problems()
    {
        // The other half of every refusal below: a guard nothing passes says nothing.
        Assert.Empty(await ProblemsOf(NewCleanCandidate()));
    }

    [Fact]
    public async Task A_requirement_type_the_app_cannot_read_is_named()
    {
        var candidate = NewCandidate(
            CurrentSchema,
            Quest("Stirrup"),
            Quest("Debut"),
            Requirement("Debut", "Stirrup", requirementType: "Abandoned"));

        var problem = await TheProblemWith(candidate);

        Assert.Contains("RequirementType outside {Complete, Accept, Fail}", problem);
        Assert.Contains("Abandoned", problem);
    }

    [Fact]
    public async Task An_alternate_requirement_type_the_app_cannot_read_is_named()
    {
        var candidate = NewCandidate(
            CurrentSchema,
            Quest("Stirrup"),
            Quest("Debut"),
            Requirement("Debut", "Stirrup", altRequirementType: "Abandoned"));

        var problem = await TheProblemWith(candidate);

        Assert.Contains("AltRequirementType outside {NULL, Complete, Accept, Fail}", problem);
    }

    [Fact]
    public async Task An_alternate_requirement_type_that_repeats_the_first_is_named()
    {
        var candidate = NewCandidate(
            CurrentSchema,
            Quest("Stirrup"),
            Quest("Debut"),
            Requirement("Debut", "Stirrup", requirementType: "Complete", altRequirementType: "Complete"));

        Assert.Contains("AltRequirementType repeats RequirementType", await TheProblemWith(candidate));
    }

    [Fact]
    public async Task A_second_row_for_one_quest_and_prerequisite_is_named()
    {
        // The fielded reader keys these rows by prerequisite alone and drops every later one,
        // group and all, so the second row publishes as nothing and can leave an OR group with
        // one branch nothing satisfies.
        var candidate = NewCandidate(
            CurrentSchema,
            Quest("Stirrup"),
            Quest("Debut"),
            Requirement("Debut", "Stirrup", groupId: 1),
            Requirement("Debut", "Stirrup", groupId: 2));

        var problem = await TheProblemWith(candidate);

        Assert.Contains("1 quest/prerequisite pairs have more than one row", problem);
        Assert.Contains("groups 1, 2", problem);
    }

    [Fact]
    public async Task A_second_row_whose_prerequisite_differs_only_in_case_is_named_too()
    {
        // The reader compares prerequisite ids case-insensitively
        // (QuestDbService.LoadQuestRequirementsAsync), so it collapses these two rows even
        // though SQLite keeps them apart. A rule that grouped case-sensitively would wave
        // through exactly the shape the reader cannot hold.
        var stirrup = WikiQuestIdentity.IdFor("Stirrup");
        var debut = WikiQuestIdentity.IdFor("Debut");
        var candidate = NewCandidate(
            CurrentSchema,
            Quest("Stirrup"),
            Quest("Debut"),
            Requirement("Debut", "Stirrup", groupId: 1),
            $"INSERT INTO QuestRequirements (Id, QuestId, RequiredQuestId, RequirementType, GroupId) "
            + $"VALUES ('second', {Sql(debut)}, {Sql(stirrup.ToLowerInvariant())}, 'Complete', 2);");

        Assert.Contains("have more than one row", await TheProblemWith(candidate));
    }

    [Fact]
    public async Task A_quest_that_is_its_own_prerequisite_is_named()
    {
        // Nothing downstream checks for one, and no build can ever unlock it. The refresh drops
        // such a row wherever it builds one; this is what catches the one a hand edit made.
        var candidate = NewCandidate(
            CurrentSchema,
            Quest("Stirrup"),
            Requirement("Stirrup", "Stirrup"));

        Assert.Contains("are their own prerequisite", await TheProblemWith(candidate));
    }

    [Fact]
    public async Task A_faction_the_fielded_build_cannot_read_is_named()
    {
        // The app compares the string for equality, so any other spelling hides the quest.
        var candidate = NewCandidate(CurrentSchema, Quest("Stirrup", faction: "BEAR"));

        var problem = await TheProblemWith(candidate);

        Assert.Contains("Faction outside {NULL, Bear, Usec}", problem);
        Assert.Contains("Stirrup (BEAR)", problem);
    }

    /// <summary>
    /// <paramref name="withTrader"/> quests that name a trader, plus one that does not.
    /// </summary>
    private string NewCandidateMissingOneTrader(int withTrader)
    {
        var rows = new List<string>();
        for (var i = 0; i < withTrader; i++)
            rows.Add(Quest($"Quest {i}"));
        rows.Add(Quest("Traderless", trader: null));

        return NewCandidate(CurrentSchema, rows.ToArray());
    }

    [Fact]
    public async Task Traders_missing_on_more_than_the_permitted_share_are_named()
    {
        // One in nineteen is 5.3 percent, just over the limit.
        var problem = await TheProblemWith(NewCandidateMissingOneTrader(withTrader: 18));

        Assert.Contains("1 of 19 quests", problem);
        Assert.Contains("have no Trader", problem);
        Assert.Contains("Traderless", problem);
    }

    [Fact]
    public async Task Traders_missing_on_the_permitted_share_are_not_named()
    {
        // The boundary the rule is written on: one in twenty is 5 percent exactly, which is not
        // over the limit, and one missing trader is what a wiki page that never named one looks
        // like rather than a trader cache that failed.
        Assert.Empty(await ProblemsOf(NewCandidateMissingOneTrader(withTrader: 19)));
    }

    [Fact]
    public async Task An_empty_normalized_name_is_named()
    {
        var candidate = NewCandidate(CurrentSchema, Quest("Stirrup", normalizedName: ""));

        var problems = await ProblemsOf(candidate);

        Assert.Contains(problems, p => p.Contains("NormalizedName is empty on: Stirrup"));
    }

    [Fact]
    public async Task A_name_edited_without_its_normalized_name_is_named()
    {
        // The defect the whole gate exists for. DatabaseService's generic UPDATE writes the
        // columns the editor's grid changed and nothing else, so renaming a quest by hand
        // leaves NormalizedName behind. The row key still decodes to the old title, so the
        // guard can see the drift; the user's recorded progress cannot.
        var candidate = NewCandidate(
            CurrentSchema,
            Quest("Stirrup", name: "Stirrup Reforged", normalizedName: "stirrup"));

        // The rename alone is not the problem: this is exactly the shape a renamed quest has,
        // and it must still pass, or the carry-over the refresh exists for would be refused.
        Assert.Empty(await ProblemsOf(candidate));

        // What breaks it is writing the other half: the normalized name now follows the new
        // title, and no install has progress filed under it.
        await RunAsync(candidate, "UPDATE Quests SET NormalizedName = 'stirrup-reforged'");

        var problem = await TheProblemWith(candidate);

        Assert.Contains("NormalizedName does not match the value the app computes from the row key", problem);
        Assert.Contains("Stirrup Reforged (stirrup-reforged)", problem);
    }

    [Fact]
    public async Task Two_quests_sharing_a_normalized_name_are_named()
    {
        // Progress recorded under the shared name is ambiguous: whichever row the app reads
        // last wins, and the other quest's completions land on it.
        var candidate = NewCandidate(
            CurrentSchema,
            Quest("Stirrup"),
            Quest("Debut", normalizedName: "stirrup"));

        var problems = await ProblemsOf(candidate);

        Assert.Contains(problems, p => p.Contains("Two quests share the normalized name 'stirrup'"));
    }

    [Fact]
    public async Task A_row_key_that_is_not_a_wiki_page_url_is_named()
    {
        // A hand-typed or foreign key decodes to no title, so nothing can say what progress
        // recorded against it is filed under.
        var candidate = NewCandidate(
            CurrentSchema,
            "INSERT INTO Quests (Id, Name, Trader, NormalizedName) VALUES ('made-up-key', 'Stirrup', 'Prapor', 'stirrup');");

        Assert.Contains(
            "NormalizedName does not match the value the app computes from the row key",
            await TheProblemWith(candidate));
    }

    [Fact]
    public async Task Every_problem_in_a_candidate_is_named_at_once()
    {
        // Collected rather than thrown on the first: an operator who has to fix these by hand
        // should see the whole list once, not one per comparison.
        var candidate = NewCandidate(
            CurrentSchema,
            Quest("Stirrup", faction: "BEAR"),
            Quest("Debut"),
            Requirement("Debut", "Stirrup", requirementType: "Abandoned"));

        var problems = await ProblemsOf(candidate);

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("RequirementType outside"));
        Assert.Contains(problems, p => p.Contains("Faction outside"));
    }

    [Fact]
    public async Task The_same_violations_are_named_the_same_way_in_memory_and_on_disk()
    {
        // One declaration, two sources. The refresh checks the rows it built and the publish
        // path checks the file, and a rule taught to one of them alone is exactly the gap this
        // gate was added to close, so the two readings are pinned against each other rather
        // than each against its own expected text.
        var result = new QuestsFetchResult();
        result.Quests.Add(new DbQuest
        {
            Id = WikiQuestIdentity.IdFor("Debut"),
            Name = "Debut",
            Trader = "Prapor",
            Faction = "BEAR",
            NormalizedName = QuestNormalizedName.SqlForm("Debut"),
        });
        result.Quests.Add(new DbQuest
        {
            Id = WikiQuestIdentity.IdFor("Stirrup"),
            Name = "Stirrup",
            Trader = "Prapor",
            NormalizedName = QuestNormalizedName.SqlForm("Stirrup"),
        });
        result.Requirements.Add(new DbQuestRequirement
        {
            QuestId = WikiQuestIdentity.IdFor("Debut"),
            RequiredQuestId = WikiQuestIdentity.IdFor("Stirrup"),
            RequirementType = "Abandoned",
        });
        result.Requirements.Add(new DbQuestRequirement
        {
            QuestId = WikiQuestIdentity.IdFor("Stirrup"),
            RequiredQuestId = WikiQuestIdentity.IdFor("Stirrup"),
            RequirementType = "Complete",
        });

        var candidate = NewCandidate(
            CurrentSchema,
            Quest("Debut", faction: "BEAR"),
            Quest("Stirrup"),
            Requirement("Debut", "Stirrup", requirementType: "Abandoned"),
            Requirement("Stirrup", "Stirrup"));

        var fromMemory = PublishConstraints.Problems(PublishConstraints.Of(result));
        var fromDisk = await ProblemsOf(candidate);

        Assert.Equal(3, fromMemory.Count);
        Assert.Equal(fromMemory, fromDisk);
    }

    #endregion

    #region Candidates published before a column existed

    [Fact]
    public async Task A_candidate_published_before_the_columns_existed_passes()
    {
        // data/v1/tarkov_data.db is exactly this shape. Refusing it for lacking a column the
        // app itself feature-detects would make the gate unusable on the database in the field.
        var candidate = NewCandidate(
            PreColumnSchema,
            Quest("Stirrup", storesNormalizedName: false),
            Quest("Debut", storesNormalizedName: false),
            Requirement("Debut", "Stirrup", storesAltRequirementType: false));

        Assert.Empty(await ProblemsOf(candidate));
    }

    [Fact]
    public async Task A_candidate_published_before_the_column_is_still_checked_on_the_name_it_derives()
    {
        // The tolerance above must not become a hole: with no column the app derives the key
        // from Name (QuestDbService.LoadBaseQuestsAsync), so a name that no longer matches the
        // row key un-keys progress exactly as a drifted column would.
        var candidate = NewCandidate(
            PreColumnSchema,
            Quest("Stirrup", storesNormalizedName: false),
            $"UPDATE Quests SET Name = 'Stirrup Reforged' WHERE Id = {Sql(WikiQuestIdentity.IdFor("Stirrup"))};");

        var problem = await TheProblemWith(candidate);

        Assert.Contains("NormalizedName does not match the value the app computes from the row key", problem);
        Assert.Contains("stirrup-reforged", problem);
    }

    [Fact]
    public async Task A_candidate_with_no_quest_tables_at_all_holds_no_problems()
    {
        // Not every database the publish path meets is a tarkov_data.db mid-regeneration; what
        // makes a file the published database is settled by the manifest and by the schema
        // guard over the endpoint. There are simply no quest rows here to be wrong about.
        var candidate = NewCandidate("CREATE TABLE Marker (Name TEXT);");

        var problems = await ProblemsOf(candidate);

        Assert.Empty(problems);
    }

    #endregion

    #region The gate on the publish path

    /// <summary>A repo tree with a data channel holding a published database, as a publish finds it.</summary>
    private string NewRepo(byte[] endpointDatabase)
    {
        var root = _temp.NewFolder("repo");
        Directory.CreateDirectory(Path.Combine(root, "TarkovHelper", "Assets"));

        var channel = Path.Combine(root, "data", "v1");
        Directory.CreateDirectory(channel);
        File.WriteAllBytes(Path.Combine(channel, DatabaseFile), endpointDatabase);
        File.WriteAllText(Path.Combine(channel, VersionFile), "1.0.10");

        return root;
    }

    /// <summary>The editor's build output: the candidate a publish reads from.</summary>
    private string NewSource(string candidatePath)
    {
        var dir = _temp.NewFolder("editor-output");
        File.Copy(candidatePath, Path.Combine(dir, DatabaseFile));
        return dir;
    }

    private static string ChannelDatabase(string repoRoot) =>
        Path.Combine(repoRoot, "data", "v1", DatabaseFile);

    private static string MirrorDatabase(string repoRoot) =>
        Path.Combine(repoRoot, "TarkovHelper", "Assets", DatabaseFile);

    [Fact]
    public async Task A_candidate_that_fails_the_constraints_fails_the_comparison()
    {
        // A hard block, not a warning: the comparison reports no changes to publish and names
        // the rule, and the window never offers the Publish button for a failed comparison.
        var candidate = NewCandidate(CurrentSchema, Quest("Stirrup", faction: "BEAR"));
        var repo = NewRepo(TestSqlite.BuildDatabase("CREATE TABLE Marker (Name TEXT);"));

        using var service = new DataPublishService(NewSource(candidate), repo);
        var comparison = await service.CompareAsync();

        Assert.False(comparison.Success);
        Assert.Contains("Faction outside {NULL, Bear, Usec}", comparison.ErrorMessage);
        Assert.Contains("Stirrup (BEAR)", comparison.ErrorMessage);
    }

    [Fact]
    public async Task A_candidate_that_fails_the_constraints_publishes_nothing()
    {
        // The gate the comparison cannot be: a comparison can be minutes old, and the editor
        // writes to its own build output. Reached with a comparison taken while the candidate
        // was still clean, which is the only way this state occurs in the window.
        var candidate = NewCleanCandidate();
        var endpoint = TestSqlite.BuildDatabase("CREATE TABLE Marker (Name TEXT);");
        var repo = NewRepo(endpoint);
        var source = NewSource(candidate);

        using var service = new DataPublishService(source, repo);
        var comparison = await service.CompareAsync();
        Assert.True(comparison.Success, comparison.ErrorMessage);

        // The desync arrives after the comparison, exactly as a hand edit in another window
        // would.
        await RunAsync(
            Path.Combine(source, DatabaseFile),
            "UPDATE Quests SET Name = 'Stirrup Reforged', NormalizedName = 'stirrup-reforged' "
            + $"WHERE Id = '{WikiQuestIdentity.IdFor("Stirrup")}'");

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.False(published.Success);
        Assert.Contains("NormalizedName does not match the value the app computes from the row key", published.ErrorMessage);
        Assert.Contains(published.Errors, e => e.Contains("Stirrup Reforged"));
        Assert.Empty(published.CopiedFiles);
        // Nothing was written: the endpoint still holds the bytes it did, and the mirror was
        // never created.
        Assert.Equal(endpoint, File.ReadAllBytes(ChannelDatabase(repo)));
        Assert.False(File.Exists(MirrorDatabase(repo)));
        Assert.Equal("1.0.10", File.ReadAllText(Path.Combine(repo, "data", "v1", VersionFile)));
    }

    [Fact]
    public async Task An_endpoint_that_fails_the_constraints_is_not_mirrored_into_assets()
    {
        // With no build output, the endpoint copy is what a publish stamps and mirrors. The
        // mirror is an endpoint pre-channel builds poll, so this step can still be the moment
        // unreadable data reaches an install.
        var repo = NewRepo(File.ReadAllBytes(
            NewCandidate(CurrentSchema, Quest("Stirrup", faction: "BEAR"))));

        using var service = new DataPublishService(_temp.NewFolder("empty-editor-output"), repo);
        var comparison = await service.CompareAsync();
        // The comparison passes: there is no source database to check, and the endpoint is what
        // the publish will read.
        Assert.True(comparison.Success, comparison.ErrorMessage);

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.False(published.Success);
        Assert.Contains("Faction outside {NULL, Bear, Usec}", published.ErrorMessage);
        Assert.False(File.Exists(MirrorDatabase(repo)));
    }

    [Fact]
    public async Task A_clean_candidate_still_publishes()
    {
        // The gate has to let the ordinary run through, or every assertion above is about a
        // publish path nobody can use.
        var candidate = NewCleanCandidate();
        var repo = NewRepo(TestSqlite.BuildDatabase("CREATE TABLE Marker (Name TEXT);"));

        using var service = new DataPublishService(NewSource(candidate), repo);
        var comparison = await service.CompareAsync();
        Assert.True(comparison.Success, comparison.ErrorMessage);

        var published = await service.PublishAsync(comparison, "1.0.11");

        Assert.True(published.Success, published.ErrorMessage);
        Assert.Contains("data/v1/tarkov_data.db", published.CopiedFiles);
        Assert.True(File.Exists(MirrorDatabase(repo)));
    }

    #endregion

    #region The published database

    /// <summary>
    /// The gate, run against what is on the endpoint right now. It is the file every install
    /// already holds, so a rule it fails is a live defect rather than a latent one, and the
    /// answer to "would this gate have refused what we shipped" has to be read off the file
    /// rather than assumed.
    /// </summary>
    [Fact]
    public async Task The_published_database_passes_the_gate()
    {
        var path = Path.Combine(TestRepo.Root(), "data", "v1", DatabaseFile);
        Assert.True(File.Exists(path), $"{path} is missing, so there is no published database to check");

        var candidate = await PublishConstraints.ReadAsync(path);

        // Not vacuous: the published file must actually have been read, or an empty problem
        // list would say nothing at all.
        Assert.Equal(488, candidate.Quests.Count);
        Assert.Equal(794, candidate.Requirements.Count);
        // And it is the older format, so this is also the pre-column tolerance exercised
        // against the real thing rather than a fixture.
        Assert.False(candidate.StoresNormalizedNames);

        var problems = PublishConstraints.Problems(candidate);

        Assert.True(problems.Count == 0,
            "The published database would be refused by the publish gate:\n  - "
            + string.Join("\n  - ", problems));
    }

    #endregion

    /// <summary>Applies one statement to a candidate on disk, and lets go of the file.</summary>
    private static async Task RunAsync(string databasePath, string sql)
    {
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        // The caller goes on to read, hash or copy this file; a pooled connection would still
        // be holding it open on Windows.
        SqliteConnection.ClearAllPools();
    }
}
