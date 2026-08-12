using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the id lookups <c>QuestProgressService.Initialize</c> publishes, which the live
/// quest-event handler and the log-sync path both resolve tasks through.
/// </summary>
public sealed class QuestTaskIndexTests
{
    // GetTaskById replaced two BuildQuestIdLookup copies that indexed EVERY task. Initialize's
    // index skipped tasks with no NormalizedName, which stopped MainWindow's live quest-event
    // handler recording such a quest at all (it resolves by id and returns on a miss) and made the
    // sync count its id as unmatched. Nothing enforces a non-empty name: tarkov_data.db has no
    // NormalizedName column, QuestDbService derives it from Name in SQL, and Name is only NOT NULL.
    [Fact]
    public void Every_task_with_an_id_is_reachable_by_id_even_without_a_normalized_name()
    {
        var unnamed = new TarkovTask
        {
            Ids = new List<string> { "q-unnamed" }, Name = "", NormalizedName = "", Trader = "Prapor",
        };
        var named = TestTasks.Quest("q-named", "named-quest");

        var indexes = QuestProgressService.BuildTaskIndexes(new[] { unnamed, named });

        Assert.Same(unnamed, indexes.ById["q-unnamed"]);
        Assert.Same(unnamed, indexes.ByBsgId["q-unnamed"]);
        Assert.Same(named, indexes.ById["q-named"]);
        // ...and the name index still refuses an empty key.
        Assert.False(indexes.ByNormalizedName.ContainsKey(""));
        Assert.Same(named, indexes.ByNormalizedName["named-quest"]);
    }

    // Duplicate ids and duplicate names both keep the FIRST occurrence, which is what the hand
    // written loop in Initialize did and what every id-resolving caller assumes: a re-indexed
    // second copy would silently change which task an id resolves to.
    [Fact]
    public void A_duplicate_id_or_name_keeps_the_first_occurrence()
    {
        var first = TestTasks.Quest("q-dup", "first-quest");
        var second = TestTasks.Quest("q-dup", "first-quest");

        var indexes = QuestProgressService.BuildTaskIndexes(new[] { first, second });

        Assert.Same(first, indexes.ById["q-dup"]);
        Assert.Same(first, indexes.ByBsgId["q-dup"]);
        Assert.Same(first, indexes.ByNormalizedName["first-quest"]);
        Assert.NotSame(second, indexes.ById["q-dup"]);
    }

    // The boundaries a task's Ids list can actually take: absent entirely, and carrying an empty
    // entry. Neither may land in an index, and neither may stop the task being name-indexed.
    [Fact]
    public void A_null_or_empty_id_never_becomes_a_key()
    {
        var noIds = new TarkovTask { Ids = null, Name = "No Ids", NormalizedName = "no-ids", Trader = "Prapor" };
        var emptyId = new TarkovTask
        {
            Ids = new List<string> { "" }, Name = "Empty Id", NormalizedName = "empty-id", Trader = "Prapor",
        };

        var indexes = QuestProgressService.BuildTaskIndexes(new[] { noIds, emptyId });

        Assert.Empty(indexes.ById);
        Assert.Empty(indexes.ByBsgId);
        Assert.Same(noIds, indexes.ByNormalizedName["no-ids"]);
        Assert.Same(emptyId, indexes.ByNormalizedName["empty-id"]);
    }

    // Ids are matched case-insensitively everywhere else in the service, so the indexes must be
    // too: a log line that spells an id in another case still has to resolve.
    [Fact]
    public void Lookups_are_case_insensitive()
    {
        var task = TestTasks.Quest("Q-Mixed", "Mixed-Quest");

        var indexes = QuestProgressService.BuildTaskIndexes(new[] { task });

        Assert.Same(task, indexes.ById["q-mixed"]);
        Assert.Same(task, indexes.ByBsgId["Q-MIXED"]);
        Assert.Same(task, indexes.ByNormalizedName["mixed-quest"]);
    }

    [Fact]
    public void An_empty_task_list_yields_three_empty_indexes()
    {
        var indexes = QuestProgressService.BuildTaskIndexes(Array.Empty<TarkovTask>());

        Assert.Empty(indexes.ById);
        Assert.Empty(indexes.ByBsgId);
        Assert.Empty(indexes.ByNormalizedName);
    }
}
