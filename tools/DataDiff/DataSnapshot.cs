using System.Data;
using Microsoft.Data.Sqlite;

namespace DataDiff;

/// <summary>One quest row, as much of it as the report compares.</summary>
public sealed record QuestRow(
    string Id,
    string? BsgId,
    string Name,
    string? NameEN,
    string? NameKO,
    string? NameJA,
    string? Trader,
    string? Location,
    int? MinLevel,
    int? MinScavKarma,
    bool KappaRequired,
    string? Faction,
    string? RequiredEdition,
    string? ExcludedEdition,
    int? RequiredPrestigeLevel,
    int? RequiredDecodeCount,
    string? NormalizedName);

public sealed record ItemRow(string Id, string? BsgId, string Name);

/// <summary>A prerequisite edge, named by quest name so it survives a row-key change.</summary>
public sealed record RequirementEdge(string QuestId, string RequiredQuestId, string RequirementType, int GroupId);

public sealed record TraderGate(string QuestId, string TraderName, int RequiredLevel);

public sealed record ObjectiveRow(string QuestId, int SortOrder, string Description);

public sealed record HideoutItemRequirement(string StationId, int Level, string ItemId, int Count);

/// <summary>Column name to declared SQLite type, per table.</summary>
public sealed record TableSchema(string Table, SortedDictionary<string, string> Columns);

/// <summary>
/// Everything one database says, read once into memory so the comparison never queries.
/// <para>
/// Tables and columns are feature-detected throughout, because the whole point of the report
/// is to compare a database written before a schema change with one written after it: the
/// previous side will not have <c>Quests.NormalizedName</c> or <c>QuestTraderRequirements</c>
/// the first time this runs.
/// </para>
/// </summary>
public sealed class DataSnapshot
{
    public required string Path { get; init; }
    public required SortedDictionary<string, TableSchema> Schema { get; init; }
    public required SortedDictionary<string, int> RowCounts { get; init; }
    public required List<QuestRow> Quests { get; init; }
    public required List<ItemRow> Items { get; init; }
    public required List<RequirementEdge> Requirements { get; init; }
    public required List<TraderGate> TraderGates { get; init; }
    public required List<ObjectiveRow> Objectives { get; init; }
    public required List<HideoutItemRequirement> HideoutItemRequirements { get; init; }

    public static DataSnapshot Read(string databasePath)
    {
        if (!File.Exists(databasePath))
            throw new FileNotFoundException("Database not found.", databasePath);

        using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        var schema = ReadSchema(connection);
        var snapshot = new DataSnapshot
        {
            Path = databasePath,
            Schema = schema,
            RowCounts = ReadRowCounts(connection, schema.Keys),
            Quests = ReadQuests(connection, schema),
            Items = ReadItems(connection, schema),
            Requirements = ReadRequirements(connection, schema),
            TraderGates = ReadTraderGates(connection, schema),
            Objectives = ReadObjectives(connection, schema),
            HideoutItemRequirements = ReadHideoutItemRequirements(connection, schema),
        };

        // Microsoft.Data.Sqlite pools connections per connection string; without this the file
        // stays open and a caller that wants to move or delete it hits a locked file.
        SqliteConnection.ClearAllPools();
        return snapshot;
    }

    private static SortedDictionary<string, TableSchema> ReadSchema(SqliteConnection connection)
    {
        var tables = new List<string>();
        using (var cmd = new SqliteCommand(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", connection))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
                tables.Add(reader.GetString(0));
        }

        var schema = new SortedDictionary<string, TableSchema>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var columns = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using var cmd = new SqliteCommand($"PRAGMA table_info(\"{table}\")", connection);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                columns[reader.GetString(1)] = reader.GetString(2);

            schema[table] = new TableSchema(table, columns);
        }

        return schema;
    }

    private static SortedDictionary<string, int> ReadRowCounts(SqliteConnection connection, IEnumerable<string> tables)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            using var cmd = new SqliteCommand($"SELECT COUNT(*) FROM \"{table}\"", connection);
            counts[table] = Convert.ToInt32(cmd.ExecuteScalar());
        }

        return counts;
    }

    private static List<QuestRow> ReadQuests(SqliteConnection connection, SortedDictionary<string, TableSchema> schema)
    {
        var rows = new List<QuestRow>();
        if (!schema.ContainsKey("Quests"))
            return rows;

        using var cmd = new SqliteCommand(
            Select(schema, "Quests",
                "Id", "BsgId", "Name", "NameEN", "NameKO", "NameJA", "Trader", "Location", "MinLevel", "MinScavKarma",
                "KappaRequired", "Faction", "RequiredEdition", "ExcludedEdition", "RequiredPrestigeLevel",
                "RequiredDecodeCount", "NormalizedName"),
            connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new QuestRow(
                Text(reader, 0),
                Str(reader, 1),
                Text(reader, 2),
                Str(reader, 3),
                Str(reader, 4),
                Str(reader, 5),
                Str(reader, 6),
                Str(reader, 7),
                Int(reader, 8),
                Int(reader, 9),
                Int(reader, 10) is int kappa && kappa != 0,
                Str(reader, 11),
                Str(reader, 12),
                Str(reader, 13),
                Int(reader, 14),
                Int(reader, 15),
                Str(reader, 16)));
        }

        return rows;
    }

    private static List<ItemRow> ReadItems(SqliteConnection connection, SortedDictionary<string, TableSchema> schema)
    {
        var rows = new List<ItemRow>();
        if (!schema.ContainsKey("Items"))
            return rows;

        using var cmd = new SqliteCommand(Select(schema, "Items", "Id", "BsgId", "Name"), connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new ItemRow(Text(reader, 0), Str(reader, 1), Text(reader, 2)));

        return rows;
    }

    private static List<RequirementEdge> ReadRequirements(SqliteConnection connection, SortedDictionary<string, TableSchema> schema)
    {
        var rows = new List<RequirementEdge>();
        if (!schema.ContainsKey("QuestRequirements"))
            return rows;

        using var cmd = new SqliteCommand(
            Select(schema, "QuestRequirements", "QuestId", "RequiredQuestId", "RequirementType", "GroupId"), connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new RequirementEdge(
                Text(reader, 0),
                Text(reader, 1),
                Text(reader, 2),
                Int(reader, 3) ?? 0));
        }

        return rows;
    }

    private static List<TraderGate> ReadTraderGates(SqliteConnection connection, SortedDictionary<string, TableSchema> schema)
    {
        var rows = new List<TraderGate>();
        if (!schema.ContainsKey("QuestTraderRequirements"))
            return rows;

        using var cmd = new SqliteCommand(
            Select(schema, "QuestTraderRequirements", "QuestId", "TraderName", "RequiredLevel"), connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new TraderGate(
                Text(reader, 0),
                Text(reader, 1),
                Int(reader, 2) ?? 0));
        }

        return rows;
    }

    private static List<ObjectiveRow> ReadObjectives(SqliteConnection connection, SortedDictionary<string, TableSchema> schema)
    {
        var rows = new List<ObjectiveRow>();
        if (!schema.ContainsKey("QuestObjectives"))
            return rows;

        using var cmd = new SqliteCommand(
            Select(schema, "QuestObjectives", "QuestId", "SortOrder", "Description"), connection);
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(new ObjectiveRow(
                    Text(reader, 0),
                    Int(reader, 1) ?? 0,
                    Text(reader, 2)));
            }
        }

        // Sorted here rather than in SQL: an ORDER BY names its columns, and a database that
        // dropped one of them has to still read (see Select).
        rows.Sort((left, right) =>
        {
            var byQuest = string.CompareOrdinal(left.QuestId, right.QuestId);
            return byQuest != 0 ? byQuest : left.SortOrder.CompareTo(right.SortOrder);
        });
        return rows;
    }

    private static List<HideoutItemRequirement> ReadHideoutItemRequirements(
        SqliteConnection connection, SortedDictionary<string, TableSchema> schema)
    {
        var rows = new List<HideoutItemRequirement>();
        if (!schema.ContainsKey("HideoutItemRequirements"))
            return rows;

        using var cmd = new SqliteCommand(
            Select(schema, "HideoutItemRequirements", "StationId", "Level", "ItemId", "Count"), connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new HideoutItemRequirement(
                Text(reader, 0),
                Int(reader, 1) ?? 0,
                Text(reader, 2),
                Int(reader, 3) ?? 0));
        }

        return rows;
    }

    /// <summary>
    /// Builds a SELECT over the columns a table is expected to carry, substituting a literal
    /// NULL for every one this database does not have.
    /// <para>
    /// Naming a dropped column in the SQL would abort the read with
    /// <c>no such column</c>, and the report would never render - including the "Removed column"
    /// line of the schema delta, which is the single most important thing it can say. A schema
    /// removal has to be reported, not fatal, so every column this tool reads is optional and
    /// every reader below tolerates a NULL in its place.
    /// </para>
    /// </summary>
    private static string Select(
        SortedDictionary<string, TableSchema> schema, string table, params string[] columns)
    {
        var projection = columns.Select(column => HasColumn(schema, table, column) ? $"\"{column}\"" : "NULL");
        return $"SELECT {string.Join(", ", projection)} FROM \"{table}\"";
    }

    private static bool HasColumn(SortedDictionary<string, TableSchema> schema, string table, string column) =>
        schema.TryGetValue(table, out var t) && t.Columns.ContainsKey(column);

    /// <summary>A non-null string for the columns the records model as non-nullable.</summary>
    private static string Text(IDataRecord reader, int ordinal) => Str(reader, ordinal) ?? "";

    private static string? Str(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? Int(IDataRecord reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal));
}
