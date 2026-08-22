using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataDiff;

/// <summary>
/// The JSON side of a refresh run: what the identity resolver decided, including the things a
/// database comparison cannot see because they never reached the database - pages held back for
/// lack of a game record, records with no page, pages several records claimed, where the wiki
/// and the game disagree about a quest's prerequisites, and which pages only matched because a
/// hand written alias said so.
/// <para>
/// It also carries the renames and title reuses the resolver itself observed. Those overlap with
/// what the database comparison derives, but not everywhere: the comparison infers a title reuse
/// from two external IDs, so against a previous database written before the backfill it finds
/// none, while the resolver saw every one of them.
/// </para>
/// </summary>
public sealed class RefreshLog
{
    [JsonPropertyName("writtenAt")]
    public DateTime WrittenAt { get; set; }

    [JsonPropertyName("counts")]
    public Dictionary<string, int>? Counts { get; set; }

    [JsonPropertyName("heldBackPages")]
    public List<HeldBackPageEntry>? HeldBackPages { get; set; }

    [JsonPropertyName("wikiOnlySeasonal")]
    public List<string>? WikiOnlySeasonal { get; set; }

    [JsonPropertyName("tasksWithoutPage")]
    public List<TaskWithoutPageEntry>? TasksWithoutPage { get; set; }

    [JsonPropertyName("collisions")]
    public List<CollisionEntry>? Collisions { get; set; }

    [JsonPropertyName("renames")]
    public List<RenameEntry>? Renames { get; set; }

    [JsonPropertyName("titleReuses")]
    public List<RenameEntry>? TitleReuses { get; set; }

    [JsonPropertyName("prerequisiteDisagreements")]
    public List<DisagreementEntry>? PrerequisiteDisagreements { get; set; }

    [JsonPropertyName("aliasesUsed")]
    public List<string>? AliasesUsed { get; set; }

    [JsonPropertyName("unusedAliases")]
    public List<AliasEntry>? UnusedAliases { get; set; }

    public static RefreshLog Read(string path) => Parse(File.ReadAllText(path), path);

    public static RefreshLog Parse(string json, string source)
    {
        try
        {
            return JsonSerializer.Deserialize<RefreshLog>(json)
                   ?? throw new InvalidOperationException($"{source} parsed to nothing.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{source} is not a readable refresh log: {ex.Message}", ex);
        }
    }

    /// <summary>Renders the log's sections as markdown, for embedding in the diff report.</summary>
    public string Render()
    {
        var report = new StringBuilder();

        if (Counts is { Count: > 0 })
        {
            report.AppendLine("| Count | Value |");
            report.AppendLine("|---|---:|");
            foreach (var (name, value) in Counts.OrderBy(c => c.Key, StringComparer.Ordinal))
                report.AppendLine($"| {name} | {value} |");
            report.AppendLine();
        }

        // Rendered from the log rather than left to the database comparison, which infers title
        // reuse from two external IDs and therefore finds none at all against a previous database
        // written before the backfill - the very run where a reuse is most likely to be missed.
        if (TitleReuses is { Count: > 0 })
        {
            report.AppendLine("### Titles the resolver saw change owner");
            report.AppendLine();
            report.AppendLine("Another imported quest now carries the old title. Keying by page would have moved "
                + "this quest's recorded progress onto that other quest.");
            report.AppendLine();
            report.AppendLine("| Previous title | Now titled | Game record | Row key kept |");
            report.AppendLine("|---|---|---|---|");
            foreach (var reuse in TitleReuses.OrderBy(r => r.PreviousName, StringComparer.Ordinal))
                report.AppendLine($"| {reuse.PreviousName} | {reuse.Title} | `{reuse.BsgId}` | `{reuse.Id}` |");
            report.AppendLine();
        }

        if (Renames is { Count: > 0 })
        {
            report.AppendLine("### Renames the resolver carried");
            report.AppendLine();
            report.AppendLine("The quest kept its row key and normalized name, so progress recorded under the old "
                + "title still resolves.");
            report.AppendLine();
            report.AppendLine($"<details><summary>{Renames.Count} renames</summary>");
            report.AppendLine();
            foreach (var rename in Renames.OrderBy(r => r.PreviousName, StringComparer.Ordinal))
                report.AppendLine($"- {rename.PreviousName} -> {rename.Title} (`{rename.BsgId}`, row key `{rename.Id}`)");
            report.AppendLine();
            report.AppendLine("</details>");
            report.AppendLine();
        }

        if (WikiOnlySeasonal is { Count: > 0 })
        {
            report.AppendLine("### Imported on the wiki's seasonal marker alone");
            report.AppendLine();
            report.AppendLine("These carry no game identifier, so log sync cannot mark them and their loyalty "
                + "requirements are unknown until the API adds them.");
            report.AppendLine();
            foreach (var title in WikiOnlySeasonal.OrderBy(t => t, StringComparer.Ordinal))
                report.AppendLine($"- {title}");
            report.AppendLine();
        }

        if (HeldBackPages is { Count: > 0 })
        {
            report.AppendLine("### Wiki pages held back");
            report.AppendLine();
            report.AppendLine($"<details><summary>{HeldBackPages.Count} pages</summary>");
            report.AppendLine();
            foreach (var page in HeldBackPages.OrderBy(p => p.Title, StringComparer.Ordinal))
                report.AppendLine($"- {page.Title}: {page.Reason}");
            report.AppendLine();
            report.AppendLine("</details>");
            report.AppendLine();
        }

        if (TasksWithoutPage is { Count: > 0 })
        {
            report.AppendLine("### Game records with no wiki page");
            report.AppendLine();
            report.AppendLine($"<details><summary>{TasksWithoutPage.Count} records</summary>");
            report.AppendLine();
            foreach (var task in TasksWithoutPage.OrderBy(t => t.NameEN ?? t.TaskId, StringComparer.Ordinal))
                report.AppendLine($"- {task.NameEN} (`{task.TaskId}`, {task.NormalizedName})");
            report.AppendLine();
            report.AppendLine("</details>");
            report.AppendLine();
        }

        if (Collisions is { Count: > 0 })
        {
            report.AppendLine("### Pages claimed by several game records");
            report.AppendLine();
            report.AppendLine("Only the chosen record's log events match this quest.");
            report.AppendLine();
            report.AppendLine("| Page | Chosen | By rule | Candidates |");
            report.AppendLine("|---|---|---|---|");
            foreach (var collision in Collisions.OrderBy(c => c.Title, StringComparer.Ordinal))
            {
                report.AppendLine($"| {collision.Title} | `{collision.ChosenTaskId}` | {collision.Rule} | "
                    + $"{string.Join(", ", (collision.CandidateTaskIds ?? new List<string>()).Select(id => $"`{id}`"))} |");
            }

            report.AppendLine();
        }

        if (PrerequisiteDisagreements is { Count: > 0 })
        {
            report.AppendLine("### Where the wiki and the game disagree about prerequisites");
            report.AppendLine();
            report.AppendLine("The game's list is what ships. `conflict` and `taskSuperset` are the rows worth "
                + "reading: the wiki lists a chain the game does not, or misses one it does.");
            report.AppendLine();

            foreach (var group in PrerequisiteDisagreements.GroupBy(d => d.Verdict).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                report.AppendLine($"<details><summary>{group.Key}: {group.Count()} quests</summary>");
                report.AppendLine();
                report.AppendLine("| Quest | Wiki says | Game says |");
                report.AppendLine("|---|---|---|");
                foreach (var entry in group.OrderBy(d => d.Quest, StringComparer.Ordinal))
                {
                    report.AppendLine($"| {entry.Quest} | {Join(entry.Wiki)} | {Join(entry.Game)} |");
                }

                report.AppendLine();
                report.AppendLine("</details>");
                report.AppendLine();
            }
        }

        if (AliasesUsed is { Count: > 0 })
        {
            report.AppendLine("### Pages matched only by a hand written alias");
            report.AppendLine();
            report.AppendLine("These pages do not reach their game record on their own. Each one is a standing "
                + "guess that the alias list still points at the right record.");
            report.AppendLine();
            foreach (var title in AliasesUsed.OrderBy(t => t, StringComparer.Ordinal))
                report.AppendLine($"- {title}");
            report.AppendLine();
        }

        if (UnusedAliases is { Count: > 0 })
        {
            report.AppendLine("### Alias entries that no longer fire");
            report.AppendLine();
            report.AppendLine("Their page matches without help now, so upstream may have fixed the link. Remove them.");
            report.AppendLine();
            foreach (var alias in UnusedAliases.OrderBy(a => a.PageTitle, StringComparer.Ordinal))
                report.AppendLine($"- {alias.PageTitle} (`{alias.TaskId}`, waiting on {alias.UpstreamIssue})");
            report.AppendLine();
        }

        return report.ToString();

        static string Join(List<string>? values) =>
            values is { Count: > 0 } ? string.Join("<br>", values) : "_(none)_";
    }

    public sealed class HeldBackPageEntry
    {
        [JsonPropertyName("Title")] public string Title { get; set; } = "";
        [JsonPropertyName("Reason")] public string Reason { get; set; } = "";
    }

    public sealed class TaskWithoutPageEntry
    {
        [JsonPropertyName("TaskId")] public string TaskId { get; set; } = "";
        [JsonPropertyName("NormalizedName")] public string? NormalizedName { get; set; }
        [JsonPropertyName("WikiLink")] public string? WikiLink { get; set; }
        [JsonPropertyName("NameEN")] public string? NameEN { get; set; }
    }

    public sealed class CollisionEntry
    {
        [JsonPropertyName("Title")] public string Title { get; set; } = "";
        [JsonPropertyName("CandidateTaskIds")] public List<string>? CandidateTaskIds { get; set; }
        [JsonPropertyName("ChosenTaskId")] public string ChosenTaskId { get; set; } = "";
        [JsonPropertyName("Rule")] public string Rule { get; set; } = "";
    }

    public sealed class RenameEntry
    {
        [JsonPropertyName("PreviousName")] public string PreviousName { get; set; } = "";
        [JsonPropertyName("Title")] public string Title { get; set; } = "";
        [JsonPropertyName("BsgId")] public string BsgId { get; set; } = "";

        /// <summary>The row key the quest kept, which is what the recorded progress hangs off.</summary>
        [JsonPropertyName("Id")] public string Id { get; set; } = "";

        /// <summary>True when another imported quest now carries this quest's old title.</summary>
        [JsonPropertyName("TitleReused")] public bool TitleReused { get; set; }
    }

    public sealed class DisagreementEntry
    {
        [JsonPropertyName("Quest")] public string Quest { get; set; } = "";
        [JsonPropertyName("Verdict")] public string Verdict { get; set; } = "";
        [JsonPropertyName("Wiki")] public List<string>? Wiki { get; set; }
        [JsonPropertyName("Game")] public List<string>? Game { get; set; }
    }

    public sealed class AliasEntry
    {
        [JsonPropertyName("PageTitle")] public string PageTitle { get; set; } = "";
        [JsonPropertyName("TaskId")] public string TaskId { get; set; } = "";
        [JsonPropertyName("UpstreamIssue")] public string UpstreamIssue { get; set; } = "";
    }
}
