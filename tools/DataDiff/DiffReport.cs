using System.Text;

namespace DataDiff;

/// <summary>Optional inputs that widen the report beyond the two databases.</summary>
public sealed class DiffOptions
{
    /// <summary>Folder of published item icons, checked for coverage against Items.</summary>
    public string? IconDirectory { get; init; }

    /// <summary>The refresh log the regeneration wrote, for what it held back and renamed.</summary>
    public RefreshLog? RefreshLog { get; init; }
}

/// <summary>
/// Renders the review artefact for a regeneration: every added, removed and renamed quest,
/// every field change, the prerequisite edges gained and lost, the loyalty gates, the objective
/// lists whose shape changed, the items and their icons, and the NULL rates.
/// <para>
/// It exists because a 1.1 refresh changes essentially every quest row, which nobody can review
/// in a database browser. The report is what gets read before a publish, and it is attached to
/// the publish PR.
/// </para>
/// <para>
/// Quests are joined by external ID first and row key second, which is what makes a rename read
/// as a rename rather than as one quest removed and another added.
/// </para>
/// </summary>
public static class DiffReport
{
    public static string Render(DataSnapshot previous, DataSnapshot candidate, DiffOptions? options = null)
    {
        options ??= new DiffOptions();
        var report = new StringBuilder();

        report.AppendLine("# Data diff report");
        report.AppendLine();
        report.AppendLine($"- Previous: `{previous.Path}`");
        report.AppendLine($"- Candidate: `{candidate.Path}`");
        report.AppendLine();

        RenderSchemaDelta(report, previous, candidate);
        RenderRowCounts(report, previous, candidate);

        var join = QuestJoin.Build(previous, candidate);
        RenderQuestMembership(report, join);
        RenderQuestFieldChanges(report, join);
        RenderPrerequisites(report, previous, candidate);
        RenderTraderGates(report, previous, candidate);
        RenderObjectiveShapeChanges(report, previous, candidate, join);
        RenderItems(report, previous, candidate);
        RenderIconCoverage(report, candidate, options.IconDirectory);
        RenderHideout(report, previous, candidate);
        RenderNullRates(report, previous, candidate);
        RenderRefreshLog(report, options.RefreshLog);

        return report.ToString();
    }

    private static void RenderSchemaDelta(StringBuilder report, DataSnapshot previous, DataSnapshot candidate)
    {
        report.AppendLine("## Schema delta");
        report.AppendLine();

        var changes = ComputeSchemaChanges(previous, candidate);
        foreach (var change in changes)
        {
            report.AppendLine(change.Kind switch
            {
                SchemaChangeKind.AddedTable =>
                    $"- Added table `{change.Table}` ({change.ColumnCount} columns)",
                SchemaChangeKind.RemovedTable =>
                    $"- **Removed table** `{change.Table}` (breaks every build reading this data format)",
                SchemaChangeKind.AddedColumn =>
                    $"- Added column `{change.Table}.{change.Column}` ({change.CandidateType})",
                SchemaChangeKind.RemovedColumn =>
                    $"- **Removed column** `{change.Table}.{change.Column}` (breaks every build reading this data format)",
                SchemaChangeKind.RetypedColumn =>
                    $"- **Retyped column** `{change.Table}.{change.Column}`: {change.PreviousType} -> {change.CandidateType}",
                _ => throw new InvalidOperationException($"Unhandled schema change kind: {change.Kind}"),
            });
        }

        // Decided from the empty list, not from the text already written. Reading the report back
        // to work out whether anything was found would make the markdown stand in for a result
        // this method already has.
        if (changes.Count == 0)
            report.AppendLine("No schema change.");

        report.AppendLine();
    }

    /// <summary>
    /// Every difference between the two schemas, in the order the section prints them: added
    /// tables, removed tables, then each shared table's added, removed and retyped columns.
    /// </summary>
    public static List<SchemaChange> ComputeSchemaChanges(DataSnapshot previous, DataSnapshot candidate)
    {
        var changes = new List<SchemaChange>();

        foreach (var table in candidate.Schema.Keys.Except(previous.Schema.Keys, StringComparer.Ordinal))
            changes.Add(new SchemaChange(SchemaChangeKind.AddedTable, table, ColumnCount: candidate.Schema[table].Columns.Count));
        foreach (var table in previous.Schema.Keys.Except(candidate.Schema.Keys, StringComparer.Ordinal))
            changes.Add(new SchemaChange(SchemaChangeKind.RemovedTable, table));

        foreach (var table in previous.Schema.Keys.Intersect(candidate.Schema.Keys, StringComparer.Ordinal))
        {
            var before = previous.Schema[table].Columns;
            var after = candidate.Schema[table].Columns;

            foreach (var column in after.Keys.Except(before.Keys, StringComparer.Ordinal))
                changes.Add(new SchemaChange(SchemaChangeKind.AddedColumn, table, column, CandidateType: after[column]));
            foreach (var column in before.Keys.Except(after.Keys, StringComparer.Ordinal))
                changes.Add(new SchemaChange(SchemaChangeKind.RemovedColumn, table, column));
            foreach (var column in before.Keys.Intersect(after.Keys, StringComparer.Ordinal))
            {
                if (before[column] != after[column])
                    changes.Add(new SchemaChange(SchemaChangeKind.RetypedColumn, table, column, before[column], after[column]));
            }
        }

        return changes;
    }

    private static void RenderRowCounts(StringBuilder report, DataSnapshot previous, DataSnapshot candidate)
    {
        report.AppendLine("## Row counts");
        report.AppendLine();
        report.AppendLine("| Table | Previous | Candidate | Change |");
        report.AppendLine("|---|---:|---:|---:|");

        foreach (var table in previous.RowCounts.Keys.Union(candidate.RowCounts.Keys, StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal))
        {
            previous.RowCounts.TryGetValue(table, out var before);
            candidate.RowCounts.TryGetValue(table, out var after);
            report.AppendLine($"| {table} | {before} | {after} | {Signed(after - before)} |");
        }

        report.AppendLine();
    }

    private static void RenderQuestMembership(StringBuilder report, QuestJoin join)
    {
        report.AppendLine("## Quests added, removed and renamed");
        report.AppendLine();
        report.AppendLine($"- Matched: {join.Pairs.Count} (of which {join.Pairs.Count(p => p.MatchedBy == QuestMatchKind.ExternalId)} by external ID)");
        report.AppendLine($"- Added: {join.Added.Count}");
        report.AppendLine($"- Removed: {join.Removed.Count}");
        report.AppendLine($"- Renamed: {join.Renamed.Count}");
        report.AppendLine($"- Titles now belonging to a different quest: {join.TitleReuses.Count}");
        report.AppendLine();

        if (join.TitleReuses.Count > 0)
        {
            report.AppendLine("### Title reuses");
            report.AppendLine();
            report.AppendLine("A title that now belongs to a different game record than before. Keying quests by "
                + "their page would have moved recorded progress onto the wrong quest here.");
            report.AppendLine();
            report.AppendLine("| Title | Previously task | Now task |");
            report.AppendLine("|---|---|---|");
            foreach (var reuse in join.TitleReuses.OrderBy(r => r.Name, StringComparer.Ordinal))
                report.AppendLine($"| {reuse.Name} | `{reuse.PreviousBsgId}` | `{reuse.CandidateBsgId}` |");
            report.AppendLine();
        }

        if (join.Renamed.Count > 0)
        {
            report.AppendLine("### Renamed");
            report.AppendLine();
            report.AppendLine("| Previous name | New name | Row key kept | Normalized name |");
            report.AppendLine("|---|---|:-:|---|");
            foreach (var pair in join.Renamed.OrderBy(p => p.Previous.Name, StringComparer.Ordinal))
            {
                var keyKept = pair.Previous.Id == pair.Candidate.Id ? "yes" : "**NO**";
                report.AppendLine($"| {pair.Previous.Name} | {pair.Candidate.Name} | {keyKept} | `{pair.Candidate.NormalizedName}` |");
            }

            report.AppendLine();
        }

        if (join.Added.Count > 0)
        {
            report.AppendLine("### Added");
            report.AppendLine();
            report.AppendLine("| Name | External ID | Trader | Min level |");
            report.AppendLine("|---|---|---|---:|");
            foreach (var quest in join.Added.OrderBy(q => q.Name, StringComparer.Ordinal))
                report.AppendLine($"| {quest.Name} | {Code(quest.BsgId)} | {quest.Trader} | {quest.MinLevel} |");
            report.AppendLine();
        }

        if (join.Removed.Count > 0)
        {
            report.AppendLine("### Removed");
            report.AppendLine();
            report.AppendLine("| Name | External ID |");
            report.AppendLine("|---|---|");
            foreach (var quest in join.Removed.OrderBy(q => q.Name, StringComparer.Ordinal))
                report.AppendLine($"| {quest.Name} | {Code(quest.BsgId)} |");
            report.AppendLine();
        }
    }

    private static void RenderQuestFieldChanges(StringBuilder report, QuestJoin join)
    {
        report.AppendLine("## Quest field changes");
        report.AppendLine();

        var fields = new (string Name, Func<QuestRow, string?> Read, bool ListInFull)[]
        {
            ("KappaRequired", q => q.KappaRequired ? "1" : "0", true),
            ("MinLevel", q => q.MinLevel?.ToString(), true),
            ("Trader", q => q.Trader, true),
            ("Faction", q => q.Faction, true),
            ("RequiredEdition", q => q.RequiredEdition, true),
            ("ExcludedEdition", q => q.ExcludedEdition, true),
            ("Location", q => q.Location, false),
            ("MinScavKarma", q => q.MinScavKarma?.ToString(), false),
            ("RequiredPrestigeLevel", q => q.RequiredPrestigeLevel?.ToString(), false),
            ("RequiredDecodeCount", q => q.RequiredDecodeCount?.ToString(), false),
            ("NameKO", q => q.NameKO, false),
            ("NameJA", q => q.NameJA, false),
            ("BsgId", q => q.BsgId, false),
        };

        report.AppendLine("| Field | Changed |");
        report.AppendLine("|---|---:|");
        foreach (var (name, read, _) in fields)
            report.AppendLine($"| {name} | {join.Pairs.Count(p => read(p.Previous) != read(p.Candidate))} |");
        report.AppendLine();

        foreach (var (name, read, listInFull) in fields.Where(f => f.ListInFull))
        {
            var changed = join.Pairs.Where(p => read(p.Previous) != read(p.Candidate)).ToList();
            if (changed.Count == 0)
                continue;

            report.AppendLine($"### {name}");
            report.AppendLine();
            report.AppendLine("| Quest | Previous | Candidate |");
            report.AppendLine("|---|---|---|");
            foreach (var pair in changed.OrderBy(p => p.Candidate.Name, StringComparer.Ordinal))
                report.AppendLine($"| {pair.Candidate.Name} | {Value(read(pair.Previous))} | {Value(read(pair.Candidate))} |");
            report.AppendLine();
        }
    }

    private static void RenderPrerequisites(StringBuilder report, DataSnapshot previous, DataSnapshot candidate)
    {
        report.AppendLine("## Prerequisite edges");
        report.AppendLine();

        var changes = ComputePrerequisiteChanges(previous, candidate);

        report.AppendLine($"- Edges added: {changes.Sum(c => c.Added.Count)}");
        report.AppendLine($"- Edges removed: {changes.Sum(c => c.Removed.Count)}");
        report.AppendLine($"- Quests whose prerequisite list changed: {changes.Count}");
        report.AppendLine();

        if (changes.Count > 0)
        {
            report.AppendLine("| Quest | Added | Removed |");
            report.AppendLine("|---|---|---|");
            foreach (var change in changes)
                report.AppendLine($"| {change.Quest} | {Join(change.Added)} | {Join(change.Removed)} |");
            report.AppendLine();
        }
    }

    /// <summary>
    /// Every quest whose prerequisite list differs, in quest name order, with the edges gained
    /// and lost. A quest with no difference is left out, so the totals the section prints above
    /// the table are the sums over this list.
    /// </summary>
    public static List<PrerequisiteChange> ComputePrerequisiteChanges(DataSnapshot previous, DataSnapshot candidate)
    {
        var before = EdgesByQuestName(previous);
        var after = EdgesByQuestName(candidate);

        var names = before.Keys.Union(after.Keys, StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var changes = new List<PrerequisiteChange>();

        foreach (var name in names)
        {
            var beforeEdges = before.TryGetValue(name, out var b) ? b : new HashSet<string>(StringComparer.Ordinal);
            var afterEdges = after.TryGetValue(name, out var a) ? a : new HashSet<string>(StringComparer.Ordinal);

            var gained = afterEdges.Except(beforeEdges, StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal).ToList();
            var lost = beforeEdges.Except(afterEdges, StringComparer.Ordinal).OrderBy(e => e, StringComparer.Ordinal).ToList();
            if (gained.Count == 0 && lost.Count == 0)
                continue;

            changes.Add(new PrerequisiteChange(name, gained, lost));
        }

        return changes;
    }

    private static void RenderTraderGates(StringBuilder report, DataSnapshot previous, DataSnapshot candidate)
    {
        report.AppendLine("## Trader loyalty gates");
        report.AppendLine();

        var names = candidate.Quests.ToDictionary(q => q.Id, q => q.Name, StringComparer.Ordinal);
        var byQuest = candidate.TraderGates
            .GroupBy(g => g.QuestId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(x => x.TraderName, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);

        report.AppendLine($"- Previous: {previous.TraderGates.Count} rows on {previous.TraderGates.Select(g => g.QuestId).Distinct().Count()} quests");
        report.AppendLine($"- Candidate: {candidate.TraderGates.Count} rows on {byQuest.Count} quests");
        report.AppendLine();

        if (byQuest.Count == 0)
            return;

        report.AppendLine("| Quest | Gates |");
        report.AppendLine("|---|---|");
        foreach (var (questId, gates) in byQuest.OrderBy(kvp => names.TryGetValue(kvp.Key, out var n) ? n : kvp.Key, StringComparer.Ordinal))
        {
            var questName = names.TryGetValue(questId, out var name) ? name : questId;
            report.AppendLine($"| {questName} | {string.Join(", ", gates.Select(g => $"{g.TraderName} LL{g.RequiredLevel}"))} |");
        }

        report.AppendLine();
    }

    private static void RenderObjectiveShapeChanges(StringBuilder report, DataSnapshot previous, DataSnapshot candidate, QuestJoin join)
    {
        report.AppendLine("## Objective lists whose shape changed");
        report.AppendLine();
        report.AppendLine("Objective check marks are stored by position, so a quest whose objective list changed "
            + "count or order may show a tick on the wrong line until the user corrects it.");
        report.AppendLine();

        var changes = ComputeObjectiveShapeChanges(previous, candidate, join);

        report.AppendLine($"- Quests affected: {changes.Count}");
        report.AppendLine();

        if (changes.Count > 0)
        {
            report.AppendLine("| Quest | Previous objectives | Candidate objectives |");
            report.AppendLine("|---|---:|---:|");
            foreach (var change in changes)
                report.AppendLine($"| {change.Quest} | {change.PreviousCount} | {change.CandidateCount} |");
            report.AppendLine();
        }
    }

    /// <summary>
    /// Every matched quest whose objective list changed count or order, in quest name order.
    /// A pair whose descriptions match position for position is left out, which is why a row can
    /// carry two equal counts: the wording or the order moved, not the length.
    /// </summary>
    public static List<ObjectiveShapeChange> ComputeObjectiveShapeChanges(
        DataSnapshot previous, DataSnapshot candidate, QuestJoin join)
    {
        var before = previous.Objectives
            .GroupBy(o => o.QuestId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.SortOrder).Select(o => o.Description).ToList(), StringComparer.Ordinal);
        var after = candidate.Objectives
            .GroupBy(o => o.QuestId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.SortOrder).Select(o => o.Description).ToList(), StringComparer.Ordinal);

        var changes = new List<ObjectiveShapeChange>();
        foreach (var pair in join.Pairs.OrderBy(p => p.Candidate.Name, StringComparer.Ordinal))
        {
            var beforeList = before.TryGetValue(pair.Previous.Id, out var b) ? b : new List<string>();
            var afterList = after.TryGetValue(pair.Candidate.Id, out var a) ? a : new List<string>();

            if (beforeList.Count == afterList.Count && beforeList.SequenceEqual(afterList, StringComparer.Ordinal))
                continue;

            changes.Add(new ObjectiveShapeChange(pair.Candidate.Name, beforeList.Count, afterList.Count));
        }

        return changes;
    }

    private static void RenderItems(StringBuilder report, DataSnapshot previous, DataSnapshot candidate)
    {
        report.AppendLine("## Items");
        report.AppendLine();

        var previousById = previous.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var candidateById = candidate.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);

        var added = candidate.Items.Where(i => !previousById.ContainsKey(i.Id)).ToList();
        var removed = previous.Items.Where(i => !candidateById.ContainsKey(i.Id)).ToList();
        var renamed = candidate.Items
            .Where(i => previousById.TryGetValue(i.Id, out var before) && before.Name != i.Name)
            .Select(i => (Previous: previousById[i.Id].Name, Current: i.Name))
            .ToList();

        report.AppendLine($"- Added: {added.Count}");
        report.AppendLine($"- Removed: {removed.Count}");
        report.AppendLine($"- Renamed (row key kept): {renamed.Count}");
        report.AppendLine();
        report.AppendLine("A renamed item loses its inventory count: counts are keyed by the item's name inside the app.");
        report.AppendLine();

        if (renamed.Count > 0)
        {
            report.AppendLine("| Previous name | New name |");
            report.AppendLine("|---|---|");
            foreach (var (before, current) in renamed.OrderBy(r => r.Previous, StringComparer.Ordinal))
                report.AppendLine($"| {before} | {current} |");
            report.AppendLine();
        }

        if (added.Count > 0)
        {
            report.AppendLine("<details><summary>Added items</summary>");
            report.AppendLine();
            foreach (var item in added.OrderBy(i => i.Name, StringComparer.Ordinal))
                report.AppendLine($"- {item.Name} ({Code(item.BsgId)})");
            report.AppendLine();
            report.AppendLine("</details>");
            report.AppendLine();
        }
    }

    private static void RenderIconCoverage(StringBuilder report, DataSnapshot candidate, string? iconDirectory)
    {
        report.AppendLine("## Icon coverage");
        report.AppendLine();

        if (string.IsNullOrEmpty(iconDirectory))
        {
            report.AppendLine("Not checked (pass `--icons <dir>`).");
            report.AppendLine();
            return;
        }

        var coverage = IconCoverage.Measure(candidate.Items, iconDirectory);
        report.AppendLine($"- Icon folder: `{iconDirectory}`");

        // Distinguished from a real coverage of zero, which this section would otherwise render
        // identically: the reviewer would be reading a mistyped path as every icon having gone.
        if (!coverage.DirectoryExists)
        {
            report.AppendLine();
            report.AppendLine("**Not measured**: that folder does not exist, so this section says nothing about "
                + "icon coverage either way. Check the path passed to `--icons`.");
            report.AppendLine();
            return;
        }

        report.AppendLine($"- Items with a PNG: {coverage.ItemsWithIcon}/{candidate.Items.Count}");
        report.AppendLine($"- Items without a PNG: {coverage.ItemsWithoutIcon.Count}");
        report.AppendLine($"- PNG files with no item: {coverage.OrphanFiles.Count}");
        report.AppendLine($"- Non-PNG files in the folder: {coverage.NonPngFiles.Count}");
        report.AppendLine();

        if (coverage.ItemsWithoutIcon.Count > 0)
        {
            report.AppendLine("<details><summary>Items without an icon</summary>");
            report.AppendLine();
            foreach (var name in coverage.ItemsWithoutIcon.OrderBy(n => n, StringComparer.Ordinal))
                report.AppendLine($"- {name}");
            report.AppendLine();
            report.AppendLine("</details>");
            report.AppendLine();
        }

        if (coverage.OrphanFiles.Count > 0)
        {
            report.AppendLine("<details><summary>Icon files with no item</summary>");
            report.AppendLine();
            foreach (var file in coverage.OrphanFiles.OrderBy(f => f, StringComparer.Ordinal))
                report.AppendLine($"- {file}");
            report.AppendLine();
            report.AppendLine("</details>");
            report.AppendLine();
        }
    }

    private static void RenderHideout(StringBuilder report, DataSnapshot previous, DataSnapshot candidate)
    {
        report.AppendLine("## Hideout");
        report.AppendLine();

        report.AppendLine($"- Item requirement rows: {previous.HideoutItemRequirements.Count} -> {candidate.HideoutItemRequirements.Count}");
        report.AppendLine($"- Join to Items by external ID: {JoinCoverage(previous)} -> {JoinCoverage(candidate)}");
        report.AppendLine();
        report.AppendLine("Hideout requirements name their item by the game's own id and reach the Items table "
            + "through `Items.BsgId`, so a row that does not join shows a raw identifier and no icon.");
        report.AppendLine();

        static string JoinCoverage(DataSnapshot snapshot)
        {
            if (snapshot.HideoutItemRequirements.Count == 0)
                return "no rows";

            var itemBsgIds = new HashSet<string>(
                snapshot.Items.Where(i => !string.IsNullOrEmpty(i.BsgId)).Select(i => i.BsgId!),
                StringComparer.OrdinalIgnoreCase);
            var joined = snapshot.HideoutItemRequirements.Count(r => itemBsgIds.Contains(r.ItemId));
            var share = (double)joined / snapshot.HideoutItemRequirements.Count;
            return $"{joined}/{snapshot.HideoutItemRequirements.Count} ({share:P0})";
        }
    }

    private static void RenderNullRates(StringBuilder report, DataSnapshot previous, DataSnapshot candidate)
    {
        report.AppendLine("## NULL rates");
        report.AppendLine();
        report.AppendLine("| Column | Previous | Candidate |");
        report.AppendLine("|---|---|---|");

        AppendQuestNullRate("Quests.BsgId", q => string.IsNullOrEmpty(q.BsgId));
        AppendQuestNullRate("Quests.Trader", q => string.IsNullOrEmpty(q.Trader));
        AppendQuestNullRate("Quests.MinLevel", q => q.MinLevel == null);
        AppendQuestNullRate("Quests.NameKO", q => string.IsNullOrEmpty(q.NameKO));
        AppendQuestNullRate("Quests.NameJA", q => string.IsNullOrEmpty(q.NameJA));
        AppendQuestNullRate("Quests.NormalizedName", q => string.IsNullOrEmpty(q.NormalizedName));

        report.AppendLine($"| Items.BsgId | {Rate(previous.Items.Count(i => string.IsNullOrEmpty(i.BsgId)), previous.Items.Count)} "
            + $"| {Rate(candidate.Items.Count(i => string.IsNullOrEmpty(i.BsgId)), candidate.Items.Count)} |");
        report.AppendLine();

        void AppendQuestNullRate(string label, Func<QuestRow, bool> isNull)
        {
            report.AppendLine($"| {label} | {Rate(previous.Quests.Count(isNull), previous.Quests.Count)} "
                + $"| {Rate(candidate.Quests.Count(isNull), candidate.Quests.Count)} |");
        }

        static string Rate(int nulls, int total) =>
            total == 0 ? "no rows" : $"{nulls}/{total} ({(double)nulls / total:P0})";
    }

    private static void RenderRefreshLog(StringBuilder report, RefreshLog? log)
    {
        report.AppendLine("## Refresh log");
        report.AppendLine();

        if (log == null)
        {
            report.AppendLine("Not supplied (pass `--log <refresh.json>`).");
            report.AppendLine();
            return;
        }

        report.AppendLine($"- Written at: {log.WrittenAt:u}");
        report.AppendLine();
        report.Append(log.Render());
    }

    private static string Signed(int value) => value > 0 ? $"+{value}" : value.ToString();

    private static string Code(string? value) => string.IsNullOrEmpty(value) ? "-" : $"`{value}`";

    private static string Value(string? value) => string.IsNullOrEmpty(value) ? "_(none)_" : value;

    private static string Join(IReadOnlyCollection<string> values) => values.Count == 0 ? "-" : string.Join("<br>", values);

    /// <summary>
    /// Prerequisite edges keyed by the quest's display name on both sides, so an edge that
    /// only moved because a row key was reissued does not read as a change.
    /// </summary>
    private static Dictionary<string, HashSet<string>> EdgesByQuestName(DataSnapshot snapshot)
    {
        var names = snapshot.Quests.ToDictionary(q => q.Id, q => q.Name, StringComparer.Ordinal);
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var edge in snapshot.Requirements)
        {
            if (!names.TryGetValue(edge.QuestId, out var questName))
                continue;

            var requiredName = names.TryGetValue(edge.RequiredQuestId, out var n) ? n : edge.RequiredQuestId;
            if (!edges.TryGetValue(questName, out var set))
                edges[questName] = set = new HashSet<string>(StringComparer.Ordinal);

            set.Add($"{requiredName} ({edge.RequirementType})");
        }

        return edges;
    }
}

public enum QuestMatchKind
{
    ExternalId,
    RowKey,
}

/// <summary>Which kind of schema difference a <see cref="SchemaChange"/> records.</summary>
public enum SchemaChangeKind
{
    AddedTable,
    RemovedTable,
    AddedColumn,
    RemovedColumn,
    RetypedColumn,
}

/// <summary>
/// One difference between the two schemas. <paramref name="ColumnCount"/> belongs to
/// <see cref="SchemaChangeKind.AddedTable"/> alone, and the two type names to the column kinds,
/// so every other member is left at its default for the kinds that do not carry it.
/// </summary>
public sealed record SchemaChange(
    SchemaChangeKind Kind,
    string Table,
    string? Column = null,
    string? PreviousType = null,
    string? CandidateType = null,
    int ColumnCount = 0);

/// <summary>
/// One quest whose prerequisite list differs, with the edges it gained and the ones it lost.
/// Each edge is rendered as it will be read: "quest name (requirement type)".
/// </summary>
public sealed record PrerequisiteChange(string Quest, IReadOnlyList<string> Added, IReadOnlyList<string> Removed);

/// <summary>One matched quest whose objective list changed count or order.</summary>
public sealed record ObjectiveShapeChange(string Quest, int PreviousCount, int CandidateCount);

public sealed record QuestPair(QuestRow Previous, QuestRow Candidate, QuestMatchKind MatchedBy);

public sealed record TitleReuse(string Name, string PreviousBsgId, string CandidateBsgId);

/// <summary>
/// Matches the two quest sets. External ID first, because that is the identity a rename keeps;
/// row key second, for the quests that have no external ID on one side (every published quest
/// before the backfill, and the seasonal pages the API does not carry).
/// </summary>
public sealed class QuestJoin
{
    public required List<QuestPair> Pairs { get; init; }
    public required List<QuestRow> Added { get; init; }
    public required List<QuestRow> Removed { get; init; }
    public required List<QuestPair> Renamed { get; init; }
    public required List<TitleReuse> TitleReuses { get; init; }

    public static QuestJoin Build(DataSnapshot previous, DataSnapshot candidate)
    {
        var pairs = new List<QuestPair>();
        var usedPrevious = new HashSet<string>(StringComparer.Ordinal);
        var usedCandidate = new HashSet<string>(StringComparer.Ordinal);

        var previousByBsgId = new Dictionary<string, QuestRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in previous.Quests.Where(q => !string.IsNullOrEmpty(q.BsgId)))
            previousByBsgId.TryAdd(quest.BsgId!, quest);

        foreach (var quest in candidate.Quests.Where(q => !string.IsNullOrEmpty(q.BsgId)))
        {
            if (!previousByBsgId.TryGetValue(quest.BsgId!, out var before) || !usedPrevious.Add(before.Id))
                continue;

            usedCandidate.Add(quest.Id);
            pairs.Add(new QuestPair(before, quest, QuestMatchKind.ExternalId));
        }

        var previousById = previous.Quests.ToDictionary(q => q.Id, StringComparer.Ordinal);
        foreach (var quest in candidate.Quests.Where(q => !usedCandidate.Contains(q.Id)))
        {
            if (!previousById.TryGetValue(quest.Id, out var before) || usedPrevious.Contains(before.Id))
                continue;

            usedPrevious.Add(before.Id);
            usedCandidate.Add(quest.Id);
            pairs.Add(new QuestPair(before, quest, QuestMatchKind.RowKey));
        }

        var renamed = pairs.Where(p => p.Previous.Name != p.Candidate.Name).ToList();

        // A title that belonged to one game record before and belongs to another now. The
        // published data has eight of these after 1.1, and they are the reason identity follows
        // the external ID rather than the page.
        var previousByName = new Dictionary<string, QuestRow>(StringComparer.Ordinal);
        foreach (var quest in previous.Quests)
            previousByName.TryAdd(quest.Name, quest);

        var titleReuses = new List<TitleReuse>();
        foreach (var quest in candidate.Quests)
        {
            if (string.IsNullOrEmpty(quest.BsgId))
                continue;
            if (!previousByName.TryGetValue(quest.Name, out var before) || string.IsNullOrEmpty(before.BsgId))
                continue;
            if (string.Equals(before.BsgId, quest.BsgId, StringComparison.OrdinalIgnoreCase))
                continue;

            titleReuses.Add(new TitleReuse(quest.Name, before.BsgId!, quest.BsgId!));
        }

        return new QuestJoin
        {
            Pairs = pairs,
            Added = candidate.Quests.Where(q => !usedCandidate.Contains(q.Id)).ToList(),
            Removed = previous.Quests.Where(q => !usedPrevious.Contains(q.Id)).ToList(),
            Renamed = renamed,
            TitleReuses = titleReuses,
        };
    }
}
