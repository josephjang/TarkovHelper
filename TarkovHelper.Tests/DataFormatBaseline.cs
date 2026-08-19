using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using DataFormatSchema =
    System.Collections.Generic.SortedDictionary<string, TarkovHelper.Tests.DataFormatTableSchema>;

namespace TarkovHelper.Tests;

/// <summary>Column name to declared SQLite type for one table. Sorted so diffs are readable.</summary>
internal sealed record DataFormatTableSchema(SortedDictionary<string, string> Columns);

/// <summary>What the baseline ratchet did with the schema it was handed.</summary>
internal enum DataFormatBaselineOutcome
{
    /// <summary>The published schema is exactly what the baseline records.</summary>
    Unchanged,

    /// <summary>No baseline existed, so one was proposed from the published schema.</summary>
    Bootstrapped,

    /// <summary>The published schema only grew, so a widened baseline was proposed.</summary>
    Widened,

    /// <summary>The published schema dropped or retyped something the baseline records.</summary>
    Broken,

    /// <summary>A baseline file exists but is not a schema snapshot.</summary>
    Unreadable,
}

/// <summary>Outcome plus the specifics behind it, so the caller can explain itself.</summary>
internal sealed record DataFormatBaselineReport(
    DataFormatBaselineOutcome Outcome,
    IReadOnlyList<string> Breaks,
    IReadOnlyList<string> Additions);

/// <summary>
/// The ratchet behind <see cref="DataFormatDriftTests"/>: it compares a published schema
/// against the committed baseline and, when the schema only grew, writes a proposed
/// baseline beside the committed one. It refuses to propose anything when
/// something was removed or retyped, and it never writes the committed baseline itself.
/// <para>
/// Widening is the whole point. A column that a publish adds is invisible to the guard
/// until the baseline records it, so without the widening step the very next publish could
/// drop that same column and the comparison would still find nothing missing.
/// </para>
/// <para>
/// Adopting the proposal is a human act, and deliberately so: a baseline this suite wrote
/// is only a guard once someone reviewed and committed it. Writing the committed file
/// directly would let the very run that reported drift also erase the report, so a re-run
/// with nothing committed in between would pass and the new column would stay unguarded.
/// </para>
/// Kept separate from the facts so the ratchet can be exercised against synthesized
/// schemas rather than only against whatever the repo happens to publish today.
/// </summary>
internal static class DataFormatBaseline
{
    /// <summary>
    /// Indented so a committed baseline diffs line by line. Key order is not a setting here:
    /// it comes from the SortedDictionary the schema is held in.
    /// </summary>
    private static readonly JsonSerializerOptions FileOptions = new() { WriteIndented = true };

    /// <summary>
    /// Where a proposed baseline is written: the committed baseline's name with
    /// ".proposed" in front of its extension, so the two sort side by side and adopting
    /// the proposal is a rename. Never a path git tracks, so a test run cannot dirty
    /// the working tree's committed files.
    /// </summary>
    internal static string ProposedPathFor(string baselinePath) =>
        Path.ChangeExtension(baselinePath, null) + ".proposed" + Path.GetExtension(baselinePath);

    /// <summary>
    /// Reads the structure that matters for read compatibility: which tables exist and
    /// which columns they declare. Deliberately not indexes, views, or constraints,
    /// which a reader cannot notice, and not row contents, which change every publish.
    /// </summary>
    internal static DataFormatSchema ReadSchema(string databasePath)
    {
        var schema = new DataFormatSchema(StringComparer.Ordinal);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }

        foreach (var table in tables)
        {
            var columns = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using var command = connection.CreateCommand();
            // Table names come from sqlite_master, not from user input, and PRAGMA takes
            // no parameters; quoted to survive any table name that needs it.
            command.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns[reader.GetString(1)] = reader.GetString(2);
            }

            schema[table] = new DataFormatTableSchema(columns);
        }

        return schema;
    }

    /// <summary>Reads a baseline file, or null when it is not a schema snapshot at all.</summary>
    internal static DataFormatSchema? Load(string baselinePath)
    {
        try
        {
            return JsonSerializer.Deserialize<DataFormatSchema>(File.ReadAllText(baselinePath), FileOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes a baseline file, newline-terminated like every other committed text file.</summary>
    internal static void Write(string baselinePath, DataFormatSchema schema) =>
        File.WriteAllText(baselinePath, JsonSerializer.Serialize(schema, FileOptions) + "\n");

    /// <summary>
    /// Everything the baseline records that the published schema no longer satisfies
    /// (breaks), and everything the published schema has that the baseline has never
    /// recorded (additions).
    /// </summary>
    internal static (List<string> Breaks, List<string> Additions) Compare(
        DataFormatSchema baseline, DataFormatSchema current)
    {
        var breaks = new List<string>();
        foreach (var (table, expected) in baseline)
        {
            if (!current.TryGetValue(table, out var actual))
            {
                breaks.Add($"table '{table}' is gone");
                continue;
            }

            foreach (var (column, declaredType) in expected.Columns)
            {
                if (!actual.Columns.TryGetValue(column, out var actualType))
                {
                    breaks.Add($"{table}.{column} is gone");
                }
                else if (!string.Equals(actualType, declaredType, StringComparison.OrdinalIgnoreCase))
                {
                    breaks.Add($"{table}.{column} changed type from {declaredType} to {actualType}");
                }
            }
        }

        var additions = new List<string>();
        foreach (var (table, actual) in current)
        {
            if (!baseline.TryGetValue(table, out var expected))
            {
                additions.Add($"table '{table}'");
                continue;
            }

            foreach (var (column, declaredType) in actual.Columns)
            {
                if (!expected.Columns.ContainsKey(column))
                {
                    additions.Add($"{table}.{column} ({declaredType})");
                }
            }
        }

        return (breaks, additions);
    }

    /// <summary>
    /// Compares the published schema against the committed baseline and, when (and only
    /// when) the schema grew, writes the baseline it would take to record that growth to
    /// <see cref="ProposedPathFor"/>. The committed baseline itself is never written here,
    /// so the run that reports drift cannot also clear it: every later run compares against
    /// the same committed file and reports the same thing until a maintainer adopts the
    /// proposal. A break proposes nothing at all, because a removal must not be able to
    /// launder itself by arriving alongside an addition.
    /// </summary>
    internal static DataFormatBaselineReport Ratchet(string baselinePath, DataFormatSchema current)
    {
        var proposedPath = ProposedPathFor(baselinePath);

        if (!File.Exists(baselinePath))
        {
            Write(proposedPath, current);
            return new DataFormatBaselineReport(DataFormatBaselineOutcome.Bootstrapped, [], []);
        }

        var baseline = Load(baselinePath);
        if (baseline is null)
        {
            return new DataFormatBaselineReport(DataFormatBaselineOutcome.Unreadable, [], []);
        }

        var (breaks, additions) = Compare(baseline, current);
        if (breaks.Count > 0)
        {
            // A stale proposal from an earlier addition is left alone: the run is red
            // either way, and it may still be the schema the maintainer meant to adopt.
            return new DataFormatBaselineReport(DataFormatBaselineOutcome.Broken, breaks, additions);
        }

        if (additions.Count > 0)
        {
            // The ratchet step: recording the addition is what lets a later publish that
            // drops the same column be seen as a removal at all. Proposed rather than
            // applied, because only a committed baseline guards anything.
            Write(proposedPath, current);
            return new DataFormatBaselineReport(DataFormatBaselineOutcome.Widened, breaks, additions);
        }

        // The committed baseline describes the published schema exactly, so any proposal
        // beside it is spent (adopted, or overtaken by a publish) and would only mislead
        // the next reader of the working tree.
        if (File.Exists(proposedPath)) File.Delete(proposedPath);
        return new DataFormatBaselineReport(DataFormatBaselineOutcome.Unchanged, breaks, additions);
    }
}
