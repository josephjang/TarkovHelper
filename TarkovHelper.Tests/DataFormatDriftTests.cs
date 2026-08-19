using System.IO;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services;
using DataFormatSchema =
    System.Collections.Generic.SortedDictionary<string, TarkovHelper.Tests.DataFormatTableSchema>;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the promise the data channel is built on: within one data format, the
/// published database only ever grows. Additions are safe for readers because they
/// feature-detect (the ColumnExistsAsync pattern), but a removed table, a removed
/// column, or a retyped column breaks every build already reading that schema, and
/// those builds cannot be fixed after the fact.
///
/// This exists because that promise is otherwise pure discipline, and the pipeline is
/// regenerated wholesale from upstream during ordinary feature work. Turning it into a
/// mechanical check is the difference between "we intend to stay additive" and "we
/// cannot accidentally stop".
///
/// The baseline is a ratchet, not a snapshot taken once: an additive publish makes this
/// test write a widened baseline beside the committed one and fail, and it keeps failing
/// on every re-run until the maintainer reviews that proposal and commits it in its place.
/// A one-shot snapshot would guard only the columns that existed the day it was written,
/// and a baseline this test committed on its own behalf would guard nothing at all.
///
/// When a change really does need to break the contract, the fix is not to relax this
/// test: it is to publish under a new data format version (data/v&lt;N+1&gt;) and bump
/// TarkovDataFormatVersion in the same PR, which gives this test a new baseline file and
/// leaves the old builds on the endpoint they can still read.
/// </summary>
public sealed class DataFormatDriftTests
{
    private static int DataFormatVersion => DatabaseUpdateService.DataFormatVersion;

    private static string BaselinePath() => Path.Combine(
        TestRepo.Root(), "TarkovHelper.Tests", $"DataFormatBaseline.v{DataFormatVersion}.json");

    private static string PublishedDatabasePath() => Path.Combine(
        TestRepo.Root(), "data", $"v{DataFormatVersion}", "tarkov_data.db");

    /// <summary>
    /// Reads the published schema. Microsoft.Data.Sqlite pools connections per connection
    /// string, so the file stays open after the reader returns; other suites in this
    /// assembly swap database files and would hit a locked file.
    /// </summary>
    private static DataFormatSchema ReadPublishedSchema()
    {
        var databasePath = PublishedDatabasePath();
        Assert.True(File.Exists(databasePath),
            $"{databasePath} is missing, so data format {DataFormatVersion} publishes nothing to check");

        var current = DataFormatBaseline.ReadSchema(databasePath);
        SqliteConnection.ClearAllPools();
        return current;
    }

    /// <summary>
    /// Turns a ratchet outcome into the instruction the maintainer needs. Every outcome
    /// other than <see cref="DataFormatBaselineOutcome.Unchanged"/> is a failure, including
    /// the two that wrote a proposed baseline: a file this test wrote is only a guard once
    /// it is committed, so the instruction is always "review this, put it in place, commit
    /// it", and re-running without doing so cannot turn the run green.
    /// <para>
    /// Every declared outcome has an arm of its own and there is no wildcard, so a sixth
    /// outcome added to the enum stops this switch compiling clean (CS8509) rather than
    /// quietly shipping a "failure" message that names the enum value and nothing else.
    /// CS8524 is the leftover case of an undeclared value cast into the enum, which cannot
    /// arrive here: the outcome comes from <see cref="DataFormatBaseline.Ratchet"/>.
    /// </para>
    /// </summary>
#pragma warning disable CS8524 // no arm for an undeclared enum value; Ratchet never returns one
    private static string Explain(DataFormatBaselineReport report, string baselinePath) => report.Outcome switch
    {
        DataFormatBaselineOutcome.Unchanged =>
            $"The published database matches the baseline committed for data format {DataFormatVersion}.",

        DataFormatBaselineOutcome.Bootstrapped =>
            $"No baseline for data format {DataFormatVersion}, so one was written from the current "
            + $"database as a proposal:\n  {DataFormatBaseline.ProposedPathFor(baselinePath)}\n"
            + $"Review it, move it to\n  {baselinePath}\nthen commit it and re-run. Until it is in "
            + "place this test keeps failing: an uncommitted proposal guards nothing.",

        DataFormatBaselineOutcome.Unreadable =>
            $"{baselinePath} exists but is not a schema snapshot, so nothing is being guarded. "
            + "Restore it from git, or delete it and re-run to have a fresh one proposed from the "
            + "published database.",

        DataFormatBaselineOutcome.Widened =>
            "The published database grew, so a widened baseline was proposed to record the additions:\n  "
            + string.Join("\n  ", report.Additions)
            + $"\n\nReview\n  {DataFormatBaseline.ProposedPathFor(baselinePath)}\nmove it over\n  "
            + $"{baselinePath}\nand commit it together with the publish. Additions are safe for readers, "
            + "which feature-detect, but they are only guarded once the baseline knows about them: "
            + "until then a later publish could drop the same column and this test would still pass. "
            + "Re-running without moving the proposal into place stays red, on purpose.",

        DataFormatBaselineOutcome.Broken =>
            $"The published database no longer satisfies data format {DataFormatVersion}, so every build "
            + "reading it would break and none of them can be fixed after the fact:\n  "
            + string.Join("\n  ", report.Breaks)
            + "\n\nIf this removal or retype is intended, it is a data format bump: publish it as data/v"
            + (DataFormatVersion + 1)
            + ", raise <TarkovDataFormatVersion> in the same PR, and let this test propose the new baseline.",
    };
#pragma warning restore CS8524

    /// <summary>
    /// Loads the committed baseline, reporting a missing one exactly the way the drift fact
    /// does. Shared so a data format bump, which leaves every fact here without a baseline
    /// file, reports the same actionable message from either fact instead of a bare
    /// FileNotFoundException from whichever one xunit happens to run first.
    /// </summary>
    private static DataFormatSchema LoadCommittedBaseline()
    {
        var baselinePath = BaselinePath();
        if (!File.Exists(baselinePath))
        {
            var report = DataFormatBaseline.Ratchet(baselinePath, ReadPublishedSchema());
            Assert.Fail(Explain(report, baselinePath));
        }

        var baseline = DataFormatBaseline.Load(baselinePath);
        Assert.True(baseline != null, Explain(
            new DataFormatBaselineReport(DataFormatBaselineOutcome.Unreadable, [], []), baselinePath));
        return baseline!;
    }

    [Fact]
    public void The_published_database_stays_readable_by_this_data_schema()
    {
        var baselinePath = BaselinePath();
        var report = DataFormatBaseline.Ratchet(baselinePath, ReadPublishedSchema());

        Assert.True(report.Outcome == DataFormatBaselineOutcome.Unchanged, Explain(report, baselinePath));
    }

    [Fact]
    public void Checking_the_published_database_leaves_the_committed_baseline_alone()
    {
        // `dotnet test` must not edit a tracked file, and the guard must not be able to
        // clear itself: both come down to the committed baseline surviving a run byte for
        // byte, whatever the run concluded.
        var baselinePath = BaselinePath();
        var before = File.Exists(baselinePath) ? File.ReadAllBytes(baselinePath) : null;

        DataFormatBaseline.Ratchet(baselinePath, ReadPublishedSchema());

        if (before is null)
        {
            Assert.False(File.Exists(baselinePath),
                $"the run wrote {baselinePath}, which nobody has reviewed or committed");
            return;
        }

        Assert.Equal(before, File.ReadAllBytes(baselinePath));
    }

    [Fact]
    public void The_baseline_describes_a_database_that_actually_has_content()
    {
        // Keeps the guard from passing against an empty or truncated database, which
        // would satisfy "nothing was removed" only because nothing is there.
        var baseline = LoadCommittedBaseline();

        Assert.True(baseline.Count > 5, "The baseline lists too few tables to be a real schema snapshot.");
        Assert.All(baseline, entry =>
            Assert.True(entry.Value.Columns.Count > 0, $"table '{entry.Key}' has no columns recorded"));
    }
}

/// <summary>
/// Exercises the ratchet itself against synthesized schemas. The repo facts above can only
/// ever see today's published database, so they cannot show what the guard does across a
/// sequence of publishes, which is where the interesting failure lives: a column added by
/// one publish and dropped by the next.
/// </summary>
public sealed class DataFormatBaselineRatchetTests
{
    /// <summary>
    /// A baseline file in the temp directory, with the proposal that sits beside it,
    /// both removed when the test ends.
    /// </summary>
    private sealed class TempBaseline : IDisposable
    {
        public string FilePath { get; } = Path.Combine(
            Path.GetTempPath(), $"TarkovHelperDataFormatBaseline-{Guid.NewGuid():N}.json");

        /// <summary>Where <see cref="DataFormatBaseline.Ratchet"/> proposes a new baseline.</summary>
        public string ProposedPath => DataFormatBaseline.ProposedPathFor(FilePath);

        public void Dispose()
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            if (File.Exists(ProposedPath)) File.Delete(ProposedPath);
        }
    }

    /// <summary>
    /// Does what a maintainer does with a red run: reviews the proposal and moves it into
    /// place. Only after this is the growth actually guarded.
    /// </summary>
    private static void AdoptProposal(TempBaseline temp)
    {
        Assert.True(File.Exists(temp.ProposedPath), $"no proposal was written to {temp.ProposedPath}");
        File.Move(temp.ProposedPath, temp.FilePath, overwrite: true);
    }

    /// <summary>
    /// Builds a schema. A column is "Name" for TEXT, or "Name:REAL" to declare another type.
    /// </summary>
    private static DataFormatSchema SchemaOf(params (string Table, string[] Columns)[] tables)
    {
        var schema = new DataFormatSchema(StringComparer.Ordinal);
        foreach (var (table, columns) in tables)
        {
            var declared = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                var parts = column.Split(':', 2);
                declared[parts[0]] = parts.Length == 2 ? parts[1] : "TEXT";
            }

            schema[table] = new DataFormatTableSchema(declared);
        }

        return schema;
    }

    [Fact]
    public void A_column_added_after_the_baseline_is_guarded_against_a_later_removal()
    {
        // The failure this whole ratchet exists for. Publish 1 adds a column, publish 2
        // drops it: with a write-once baseline the drop is invisible, because the baseline
        // never learned the column existed.
        using var temp = new TempBaseline();
        DataFormatBaseline.Write(temp.FilePath, SchemaOf(("Quests", ["Id"])));

        var afterAddition = DataFormatBaseline.Ratchet(temp.FilePath, SchemaOf(("Quests", ["Id", "WikiLink"])));
        Assert.Equal(DataFormatBaselineOutcome.Widened, afterAddition.Outcome);
        Assert.Contains("Quests.WikiLink (TEXT)", afterAddition.Additions);
        AdoptProposal(temp);

        var afterRemoval = DataFormatBaseline.Ratchet(temp.FilePath, SchemaOf(("Quests", ["Id"])));
        Assert.Equal(DataFormatBaselineOutcome.Broken, afterRemoval.Outcome);
        Assert.Contains("Quests.WikiLink is gone", afterRemoval.Breaks);
    }

    [Fact]
    public void A_table_added_after_the_baseline_is_guarded_against_a_later_removal()
    {
        using var temp = new TempBaseline();
        DataFormatBaseline.Write(temp.FilePath, SchemaOf(("Quests", ["Id"])));

        var afterAddition = DataFormatBaseline.Ratchet(
            temp.FilePath, SchemaOf(("Quests", ["Id"]), ("Prestige", ["Level"])));
        Assert.Equal(DataFormatBaselineOutcome.Widened, afterAddition.Outcome);
        Assert.Contains("table 'Prestige'", afterAddition.Additions);
        AdoptProposal(temp);

        var afterRemoval = DataFormatBaseline.Ratchet(temp.FilePath, SchemaOf(("Quests", ["Id"])));
        Assert.Equal(DataFormatBaselineOutcome.Broken, afterRemoval.Outcome);
        Assert.Contains("table 'Prestige' is gone", afterRemoval.Breaks);
    }

    [Fact]
    public void A_pure_addition_is_proposed_beside_the_baseline_rather_than_written_over_it()
    {
        using var temp = new TempBaseline();
        DataFormatBaseline.Write(temp.FilePath, SchemaOf(("Quests", ["Id"])));
        var committed = File.ReadAllText(temp.FilePath);

        var report = DataFormatBaseline.Ratchet(temp.FilePath, SchemaOf(("Quests", ["Id", "Name"])));

        Assert.Equal(DataFormatBaselineOutcome.Widened, report.Outcome);
        Assert.Empty(report.Breaks);
        Assert.Equal(committed, File.ReadAllText(temp.FilePath));
        var proposed = DataFormatBaseline.Load(temp.ProposedPath);
        Assert.NotNull(proposed);
        Assert.Equal(["Id", "Name"], proposed!["Quests"].Columns.Keys);
    }

    [Fact]
    public void An_addition_stays_red_on_a_re_run_until_the_proposal_is_adopted()
    {
        // The whole reason the proposal is not written into place: a guard that clears
        // itself is no guard. Re-running the suite with nothing committed in between must
        // report the same growth, not pass because the first run recorded it.
        using var temp = new TempBaseline();
        DataFormatBaseline.Write(temp.FilePath, SchemaOf(("Quests", ["Id"])));
        var committed = File.ReadAllText(temp.FilePath);
        var grown = SchemaOf(("Quests", ["Id", "Name"]));

        var first = DataFormatBaseline.Ratchet(temp.FilePath, grown);
        var second = DataFormatBaseline.Ratchet(temp.FilePath, grown);

        Assert.Equal(DataFormatBaselineOutcome.Widened, first.Outcome);
        Assert.Equal(DataFormatBaselineOutcome.Widened, second.Outcome);
        Assert.Equal(first.Additions, second.Additions);
        Assert.Equal(committed, File.ReadAllText(temp.FilePath));

        // And it does go green once the maintainer actually adopts what was proposed.
        AdoptProposal(temp);
        Assert.Equal(DataFormatBaselineOutcome.Unchanged,
            DataFormatBaseline.Ratchet(temp.FilePath, grown).Outcome);
    }

    [Fact]
    public void A_removal_arriving_beside_an_addition_still_breaks_and_leaves_the_baseline_alone()
    {
        // A removal must not be able to launder itself by shipping with an addition: if the
        // break rewrote the baseline, the removed column would be forgotten and a re-run
        // would go green.
        using var temp = new TempBaseline();
        DataFormatBaseline.Write(temp.FilePath, SchemaOf(("Quests", ["Id", "WikiLink"])));

        var report = DataFormatBaseline.Ratchet(temp.FilePath, SchemaOf(("Quests", ["Id", "Name"])));

        Assert.Equal(DataFormatBaselineOutcome.Broken, report.Outcome);
        Assert.Contains("Quests.WikiLink is gone", report.Breaks);
        Assert.Equal(["Id", "WikiLink"], DataFormatBaseline.Load(temp.FilePath)!["Quests"].Columns.Keys);
        Assert.False(File.Exists(temp.ProposedPath), "a break must not propose a baseline to adopt");

        var rerun = DataFormatBaseline.Ratchet(temp.FilePath, SchemaOf(("Quests", ["Id", "Name"])));
        Assert.Equal(DataFormatBaselineOutcome.Broken, rerun.Outcome);
    }

    [Fact]
    public void A_retyped_column_breaks()
    {
        using var temp = new TempBaseline();
        DataFormatBaseline.Write(temp.FilePath, SchemaOf(("MapMarkers", ["X:REAL"])));

        var report = DataFormatBaseline.Ratchet(temp.FilePath, SchemaOf(("MapMarkers", ["X:TEXT"])));

        Assert.Equal(DataFormatBaselineOutcome.Broken, report.Outcome);
        Assert.Contains("MapMarkers.X changed type from REAL to TEXT", report.Breaks);
    }

    [Fact]
    public void An_unchanged_schema_changes_nothing()
    {
        using var temp = new TempBaseline();
        var schema = SchemaOf(("Quests", ["Id", "Name"]), ("Items", ["Id"]));
        DataFormatBaseline.Write(temp.FilePath, schema);
        var before = File.ReadAllText(temp.FilePath);

        var report = DataFormatBaseline.Ratchet(temp.FilePath, schema);

        Assert.Equal(DataFormatBaselineOutcome.Unchanged, report.Outcome);
        Assert.Empty(report.Breaks);
        Assert.Empty(report.Additions);
        Assert.Equal(before, File.ReadAllText(temp.FilePath));
        Assert.False(File.Exists(temp.ProposedPath));
    }

    [Fact]
    public void A_proposal_the_baseline_has_caught_up_with_is_cleared_away()
    {
        // Once the committed baseline says what the proposal said, the leftover file only
        // misleads whoever finds it in the working tree next.
        using var temp = new TempBaseline();
        var schema = SchemaOf(("Quests", ["Id", "Name"]));
        DataFormatBaseline.Write(temp.FilePath, schema);
        DataFormatBaseline.Write(temp.ProposedPath, schema);

        var report = DataFormatBaseline.Ratchet(temp.FilePath, schema);

        Assert.Equal(DataFormatBaselineOutcome.Unchanged, report.Outcome);
        Assert.False(File.Exists(temp.ProposedPath));
    }

    [Fact]
    public void A_missing_baseline_is_proposed_rather_than_written_into_place()
    {
        using var temp = new TempBaseline();

        var report = DataFormatBaseline.Ratchet(temp.FilePath, SchemaOf(("Quests", ["Id"])));

        Assert.Equal(DataFormatBaselineOutcome.Bootstrapped, report.Outcome);
        Assert.False(File.Exists(temp.FilePath), "a baseline nobody reviewed must not appear on its own");
        Assert.Equal(["Id"], DataFormatBaseline.Load(temp.ProposedPath)!["Quests"].Columns.Keys);
    }

    [Fact]
    public void A_missing_baseline_stays_red_on_a_re_run_until_the_proposal_is_adopted()
    {
        // The data format bump case. The first run has nothing to compare against, so it
        // proposes one; a second run must not treat its own proposal as the guard.
        using var temp = new TempBaseline();
        var schema = SchemaOf(("Quests", ["Id"]));

        Assert.Equal(DataFormatBaselineOutcome.Bootstrapped,
            DataFormatBaseline.Ratchet(temp.FilePath, schema).Outcome);
        Assert.Equal(DataFormatBaselineOutcome.Bootstrapped,
            DataFormatBaseline.Ratchet(temp.FilePath, schema).Outcome);

        AdoptProposal(temp);
        Assert.Equal(DataFormatBaselineOutcome.Unchanged,
            DataFormatBaseline.Ratchet(temp.FilePath, schema).Outcome);
    }

    [Fact]
    public void An_unreadable_baseline_is_reported_rather_than_overwritten()
    {
        // Deleting the file is the documented way to get a fresh one. Silently rewriting a
        // corrupt one would let a bad merge erase the guard without anyone noticing.
        using var temp = new TempBaseline();
        File.WriteAllText(temp.FilePath, "{ not json");

        var report = DataFormatBaseline.Ratchet(temp.FilePath, SchemaOf(("Quests", ["Id"])));

        Assert.Equal(DataFormatBaselineOutcome.Unreadable, report.Outcome);
        Assert.Equal("{ not json", File.ReadAllText(temp.FilePath));
        Assert.False(File.Exists(temp.ProposedPath), "a corrupt baseline is restored from git, not replaced");
    }

    [Fact]
    public void An_empty_published_schema_breaks_against_every_recorded_table()
    {
        // The truncated-database case: "nothing was removed" must not be satisfiable by
        // there being nothing at all.
        using var temp = new TempBaseline();
        DataFormatBaseline.Write(temp.FilePath, SchemaOf(("Quests", ["Id"]), ("Items", ["Id"])));

        var report = DataFormatBaseline.Ratchet(temp.FilePath, SchemaOf());

        Assert.Equal(DataFormatBaselineOutcome.Broken, report.Outcome);
        Assert.Equal(2, report.Breaks.Count);
    }
}
