using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the promise the data channel is built on: within one data format, the
/// published database only ever grows. Additions are free because readers
/// feature-detect (the ColumnExistsAsync pattern), but a removed table, a removed
/// column, or a retyped column breaks every build already reading that schema, and
/// those builds cannot be fixed after the fact.
///
/// This exists because that promise is otherwise pure discipline, and the pipeline is
/// regenerated wholesale from upstream during ordinary feature work. Turning it into a
/// mechanical check is the difference between "we intend to stay additive" and "we
/// cannot accidentally stop".
///
/// When a change really does need to break the contract, the fix is not to relax this
/// test: it is to publish under a new data format (data/v&lt;N+1&gt;) and bump
/// TarkovDataFormat in the same PR, which gives this test a new baseline file and
/// leaves the old builds on the endpoint they can still read.
/// </summary>
public sealed class DataFormatDriftTests
{
    /// <summary>Column name to declared SQLite type, per table. Sorted so diffs are readable.</summary>
    private sealed record TableSchema(SortedDictionary<string, string> Columns);

    private static int DataFormat => DatabaseUpdateService.DataFormatVersion;

    private static string BaselinePath() => Path.Combine(
        TestRepo.Root(), "TarkovHelper.Tests", $"DataFormatBaseline.v{DataFormat}.json");

    private static string PublishedDatabasePath() => Path.Combine(
        TestRepo.Root(), "data", $"v{DataFormat}", "tarkov_data.db");

    /// <summary>
    /// Reads the structure that matters for read compatibility: which tables exist and
    /// which columns they declare. Deliberately not indexes, views, or constraints,
    /// which a reader cannot notice, and not row contents, which change every publish.
    /// </summary>
    private static SortedDictionary<string, TableSchema> ReadSchema(string databasePath)
    {
        var schema = new SortedDictionary<string, TableSchema>(StringComparer.Ordinal);

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

            schema[table] = new TableSchema(columns);
        }

        return schema;
    }

    [Fact]
    public void The_published_database_stays_readable_by_this_data_schema()
    {
        var databasePath = PublishedDatabasePath();
        Assert.True(File.Exists(databasePath), $"{databasePath} is missing");

        var current = ReadSchema(databasePath);
        // Microsoft.Data.Sqlite pools connections per connection string, so the file
        // stays open after the reader returns; other suites in this assembly swap
        // database files and would hit a locked file.
        SqliteConnection.ClearAllPools();

        var baselinePath = BaselinePath();
        var options = new JsonSerializerOptions { WriteIndented = true };

        if (!File.Exists(baselinePath))
        {
            // Bootstrap, then fail: a baseline that appears silently would let a run that
            // deleted it pass against whatever the database happens to hold today.
            File.WriteAllText(baselinePath, JsonSerializer.Serialize(current, options) + "\n");
            Assert.Fail(
                $"No baseline for data format {DataFormat}, so one was written from the current "
                + $"database:\n  {baselinePath}\nReview it, commit it, and re-run.");
        }

        var baseline = JsonSerializer.Deserialize<SortedDictionary<string, TableSchema>>(
            File.ReadAllText(baselinePath), options);
        Assert.True(baseline != null, $"{baselinePath} is not readable");

        var breaks = new List<string>();
        foreach (var (table, expected) in baseline!)
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

        Assert.True(breaks.Count == 0,
            $"The published database no longer satisfies data format {DataFormat}, so every build "
            + "reading it would break and none of them can be fixed after the fact:\n  "
            + string.Join("\n  ", breaks)
            + "\n\nAdditions are free and need no baseline change. If this removal or retype is "
            + "intended, it is a data format bump: publish it as data/v" + (DataFormat + 1)
            + ", raise <TarkovDataFormat> in the same PR, and let this test write the new baseline.");
    }

    [Fact]
    public void The_baseline_describes_a_database_that_actually_has_content()
    {
        // Keeps the guard from passing against an empty or truncated database, which
        // would satisfy "nothing was removed" only because nothing is there.
        var baseline = JsonSerializer.Deserialize<SortedDictionary<string, TableSchema>>(
            File.ReadAllText(BaselinePath()));

        Assert.True(baseline is { Count: > 5 },
            "The baseline lists too few tables to be a real schema snapshot.");
        Assert.All(baseline!, entry =>
            Assert.True(entry.Value.Columns.Count > 0, $"table '{entry.Key}' has no columns recorded"));
    }
}
