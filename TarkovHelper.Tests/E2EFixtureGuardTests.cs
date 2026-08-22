using System.IO;
using Microsoft.Data.Sqlite;

namespace TarkovHelper.Tests;

/// <summary>
/// Unit coverage for <see cref="E2EQuests"/>, the reader both quest-identity e2e suites choose
/// their fixture quest through.
/// <para>
/// Those suites need an interactive desktop, and the legacy smoke additionally needs an
/// extracted release that only exists on a release machine, so the selection rules themselves
/// were never exercised anywhere. They are the rules that decide whether the carry-over case
/// gets driven at all: a title misjudged as a carried rename is preferred over the real ones,
/// and the suite then goes green having tested an ordinary quest.
/// </para>
/// </summary>
public sealed class E2EQuestsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "TarkovHelperE2EQuests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_stored_key_that_no_longer_matches_its_title_is_a_carried_rename()
    {
        var db = NewDatabase(
            withNormalizedNameColumn: true,
            ("q1", "Sew it Good - Part 4", "sew-it-good---part-3"),
            ("q2", "Stirrup", "stirrup"));

        var catalogue = E2EQuests.Read(db);

        Assert.True(catalogue.HasNormalizedNameColumn);
        Assert.True(Quest(catalogue, "Sew it Good - Part 4").IsCarriedRename);
        Assert.False(Quest(catalogue, "Stirrup").IsCarriedRename);
    }

    /// <summary>
    /// The regression this reader exists to prevent. SQLite's LOWER is ICU-less and moves only
    /// A-Z, so a title carrying a cased non-ASCII letter normalizes to a key that still holds
    /// that letter. A reader that lowercased the title with ToLowerInvariant instead would
    /// disagree on 1,146 BMP code points and read every such quest as a carried rename.
    /// </summary>
    [Theory]
    // Latin-1 supplement, Cyrillic, Greek, fullwidth, and the dotted capital I whose invariant
    // lowering even changes the string's length.
    [InlineData("Ambush at Übersee", "ambush-at-Übersee")]
    [InlineData("Возврат Долга", "Возврат-Долга")]
    [InlineData("Το Κλειδί", "Το-Κλειδί")]
    [InlineData("Ｔｈｅ Ｇｕｉｄｅ", "Ｔｈｅ-Ｇｕｉｄｅ")]
    [InlineData("İzmir Run", "İzmir-run")]
    public void A_cased_non_ascii_title_is_not_mistaken_for_a_carried_rename(string name, string storedKey)
    {
        var db = NewDatabase(withNormalizedNameColumn: true, ("q1", name, storedKey));

        var catalogue = E2EQuests.Read(db);

        Assert.False(Quest(catalogue, name).IsCarriedRename);
    }

    /// <summary>
    /// The consequence of getting the rule wrong, which is what actually retires the test:
    /// both suites take the FIRST carried rename they are offered, so one false positive is
    /// enough to displace every real one.
    /// </summary>
    [Fact]
    public void A_real_carried_rename_is_the_only_one_offered_alongside_a_non_ascii_title()
    {
        var db = NewDatabase(
            withNormalizedNameColumn: true,
            // Sorts first, and is what a ToLowerInvariant reader would hand back instead.
            ("q1", "Ambush at Übersee", "ambush-at-Übersee"),
            ("q2", "Zibbo", "zibber"));

        var catalogue = E2EQuests.Read(db);

        Assert.Equal("Zibbo", Assert.Single(catalogue.UniquelySearchable.Where(q => q.IsCarriedRename)).Name);
    }

    [Fact]
    public void A_title_that_is_a_substring_of_another_is_not_offered_as_a_fixture()
    {
        var db = NewDatabase(
            withNormalizedNameColumn: true,
            ("q1", "Setup", "setup"),
            ("q2", "Setup Redux", "setup-redux"));

        var catalogue = E2EQuests.Read(db);

        // Searching "Setup" would leave two rows in the quest tab, so only the longer title is usable.
        Assert.Equal(new[] { "Setup Redux" }, catalogue.UniquelySearchable.Select(q => q.Name));
    }

    /// <summary>
    /// A title the reader rejects as a fixture is still a row the quest tab lists, so it still
    /// makes a search ambiguous. Counting only the fixture-eligible titles would offer "Setup"
    /// as unique and every wait on "filter down to one row" would then time out.
    /// </summary>
    [Fact]
    public void Uniqueness_counts_titles_that_are_not_themselves_fixtures()
    {
        var db = NewDatabase(
            withNormalizedNameColumn: true,
            ("q1", "Setup Redux", null),
            ("q2", "Setup", "setup"));

        var catalogue = E2EQuests.Read(db);

        Assert.Empty(catalogue.UniquelySearchable);
    }

    [Fact]
    public void Without_the_column_the_key_is_derived_and_nothing_reads_as_renamed()
    {
        var db = NewDatabase(
            withNormalizedNameColumn: false,
            ("q1", "Sew it Good - Part 2.5", null),
            ("q2", "Ambush at Übersee", null));

        var catalogue = E2EQuests.Read(db);

        Assert.False(catalogue.HasNormalizedNameColumn);
        Assert.All(catalogue.UniquelySearchable, q => Assert.False(q.IsCarriedRename));
        Assert.Equal("sew-it-good---part-25", Quest(catalogue, "Sew it Good - Part 2.5").NormalizedName);
        Assert.Equal("ambush-at-Übersee", Quest(catalogue, "Ambush at Übersee").NormalizedName);
    }

    [Fact]
    public void Rows_without_a_usable_id_title_or_key_are_skipped()
    {
        var db = NewDatabase(
            withNormalizedNameColumn: true,
            ("", "No Id", "no-id"),
            (null, "Null Id", "null-id"),
            ("q3", "", "empty-title"),
            ("q4", null, "null-title"),
            ("q5", "No Key", null),
            ("q6", "Keeper", "keeper"));

        var catalogue = E2EQuests.Read(db);

        Assert.Equal(new[] { "Keeper" }, catalogue.UniquelySearchable.Select(q => q.Name));
    }

    [Fact]
    public void A_missing_database_fails_by_name_rather_than_by_sqlite_error()
    {
        var missing = Path.Combine(_root, "nowhere", "tarkov_data.db");

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => E2EQuests.Read(missing));

        Assert.Contains(missing, failure.Message);
    }

    /// <summary>
    /// The shipped seed has to actually yield a fixture. Without this, a data update that left
    /// no searchable quest would surface as ProgressCarryOverE2ETests failing inside a launched
    /// app on a machine with a desktop, rather than here in a few milliseconds.
    /// </summary>
    [Fact]
    public void The_shipped_seed_database_offers_a_usable_fixture_quest()
    {
        var catalogue = E2EQuests.Read(TestSeed.DatabasePath);

        Assert.NotEmpty(catalogue.UniquelySearchable);
        Assert.All(catalogue.UniquelySearchable, quest =>
        {
            Assert.NotEmpty(quest.Id);
            Assert.NotEmpty(quest.NormalizedName);
        });
    }

    private static E2EQuests.Quest Quest(E2EQuests.Catalogue catalogue, string name)
        => Assert.Single(catalogue.UniquelySearchable.Where(q => q.Name == name));

    /// <summary>
    /// Writes a throwaway tarkov_data.db holding just the Quests columns the reader touches.
    /// The NormalizedName column is optional because databases published before the identity
    /// refresh do not have it, which is a case the reader has to keep handling.
    /// </summary>
    private string NewDatabase(
        bool withNormalizedNameColumn, params (string? Id, string? Name, string? NormalizedName)[] rows)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.db");

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();

            using (var create = connection.CreateCommand())
            {
                create.CommandText = withNormalizedNameColumn
                    ? "CREATE TABLE Quests (Id TEXT, Name TEXT, NormalizedName TEXT)"
                    : "CREATE TABLE Quests (Id TEXT, Name TEXT)";
                create.ExecuteNonQuery();
            }

            foreach (var row in rows)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = withNormalizedNameColumn
                    ? "INSERT INTO Quests (Id, Name, NormalizedName) VALUES ($id, $name, $normalized)"
                    : "INSERT INTO Quests (Id, Name) VALUES ($id, $name)";
                insert.Parameters.AddWithValue("$id", (object?)row.Id ?? DBNull.Value);
                insert.Parameters.AddWithValue("$name", (object?)row.Name ?? DBNull.Value);
                if (withNormalizedNameColumn)
                    insert.Parameters.AddWithValue("$normalized", (object?)row.NormalizedName ?? DBNull.Value);
                insert.ExecuteNonQuery();
            }
        }

        SqliteConnection.ClearAllPools();
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// Unit coverage for <see cref="StagedDatabase"/>, the legacy smoke's proof that the fielded
/// build read the candidate database rather than downloading the published one over it.
/// <para>
/// That smoke only runs on a machine holding an extracted release and a candidate, so its
/// guard has never executed and its failure paths never would. These tests stage the exact
/// on-disk evidence a v2026.7.0 DownloadDatabaseAsync leaves behind and require each one to be
/// reported, because a guard that cannot fail is the failure mode being guarded against.
/// </para>
/// </summary>
public sealed class StagedDatabaseTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "TarkovHelperStagedDb", Guid.NewGuid().ToString("N"));

    [Fact]
    public void An_untouched_staging_passes()
    {
        var (database, hash) = Stage("candidate bytes", versionToken: "1.0.10");

        StagedDatabase.AssertStillStaged(database, hash, "1.0.10");
    }

    [Fact]
    public void A_leftover_download_temp_file_is_reported()
    {
        var (database, hash) = Stage("candidate bytes", versionToken: "1.0.10");
        File.WriteAllText(database + ".tmp", "half a download");

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => StagedDatabase.AssertStillStaged(database, hash, "1.0.10"));

        Assert.Contains(".tmp", failure.Message);
    }

    [Fact]
    public void A_leftover_backup_of_the_replaced_file_is_reported()
    {
        var (database, hash) = Stage("candidate bytes", versionToken: "1.0.10");
        File.WriteAllText(database + ".bak", "the candidate, moved aside");

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => StagedDatabase.AssertStillStaged(database, hash, "1.0.10"));

        Assert.Contains(".bak", failure.Message);
    }

    [Fact]
    public void A_rewritten_version_token_is_reported()
    {
        var (database, hash) = Stage("candidate bytes", versionToken: "1.0.10");
        File.WriteAllText(VersionFile(database), "1.0.11");

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => StagedDatabase.AssertStillStaged(database, hash, "1.0.10"));

        Assert.Contains("1.0.11", failure.Message);
    }

    /// <summary>
    /// The failure that matters most: the bytes the app read are not the bytes staged. It is
    /// checked on its own here, with the version token left consistent, so it cannot pass by
    /// being masked by one of the cheaper checks above.
    /// </summary>
    [Fact]
    public void A_replaced_database_is_reported_even_when_every_other_trace_is_clean()
    {
        var (database, hash) = Stage("candidate bytes", versionToken: "1.0.10");
        File.WriteAllText(database, "the published database");

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => StagedDatabase.AssertStillStaged(database, hash, "1.0.10"));

        Assert.Contains(hash, failure.Message);
    }

    [Fact]
    public void A_build_folder_with_no_version_file_passes_when_none_was_pinned()
    {
        var (database, hash) = Stage("candidate bytes", versionToken: null);

        StagedDatabase.AssertStillStaged(database, hash, expectedVersionToken: null);
    }

    /// <summary>
    /// A completed download writes db_version.txt even where the release folder had none, so a
    /// file appearing out of nowhere is the same evidence as one being rewritten.
    /// </summary>
    [Fact]
    public void A_version_file_appearing_where_none_was_pinned_is_reported()
    {
        var (database, hash) = Stage("candidate bytes", versionToken: null);
        File.WriteAllText(VersionFile(database), "1.0.11");

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => StagedDatabase.AssertStillStaged(database, hash, expectedVersionToken: null));

        Assert.Contains("1.0.11", failure.Message);
    }

    [Fact]
    public void Sha256_reads_a_file_another_handle_holds_open()
    {
        var (database, hash) = Stage("candidate bytes", versionToken: "1.0.10");

        // The app keeps tarkov_data.db open for the whole run, so the hash has to be readable
        // through a shared handle rather than only on an idle file.
        using var held = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        Assert.Equal(hash, StagedDatabase.Sha256(database));
    }

    private static string VersionFile(string databasePath)
        => Path.Combine(Path.GetDirectoryName(databasePath)!, StagedDatabase.VersionFileName);

    /// <summary>Builds an Assets folder shaped like the one the legacy smoke stages.</summary>
    private (string DatabasePath, string Hash) Stage(string contents, string? versionToken)
    {
        var assets = Path.Combine(_root, Guid.NewGuid().ToString("N"), "Assets");
        Directory.CreateDirectory(assets);

        var database = Path.Combine(assets, "tarkov_data.db");
        File.WriteAllText(database, contents);
        if (versionToken != null)
            File.WriteAllText(Path.Combine(assets, StagedDatabase.VersionFileName), versionToken);

        return (database, StagedDatabase.Sha256(database));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
