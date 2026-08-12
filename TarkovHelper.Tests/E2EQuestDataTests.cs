using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Unit-level guards that the E2EQuestData asset-db queries still find a row: a
/// tarkov_data.db update that silently empties any of them would otherwise only
/// surface as an e2e failure on an interactive desktop. These run in the plain
/// unit suite because the asset db is copied to this test output too.
///
/// Each fact also runs the found quests through the app's OWN cascade traversal
/// (<see cref="QuestProgressService.ComputeCompletionCascade"/>) so the SQL's
/// guarantees ("exactly one completion, zero failures", "empty cascade", "exactly
/// one failure") are checked against the real rules, not just re-stated in SQL.
/// </summary>
public sealed class E2EQuestDataTests
{
    /// <summary>
    /// The cascade the app itself would plan for the named quest on a fresh
    /// profile, computed from the loaded asset db.
    /// </summary>
    private static (IReadOnlyList<string> Prereqs, IReadOnlyList<string> Failures) Cascade(string questName)
    {
        Assert.True(QuestDbService.Instance.LoadQuestsAsync().GetAwaiter().GetResult(), "asset db did not load");
        var task = QuestDbService.Instance.AllQuests.Single(q => q.Name == questName);
        var plan = QuestProgressService.ComputeCompletionCascade(
            task, completePrerequisites: true,
            new QuestProgressService.CascadeLookups
            {
                TaskById = QuestDbService.Instance.GetQuestById,
                TaskByName = QuestDbService.Instance.GetQuestByNormalizedName,
                // Fresh profile: the real GetStatus reports Done/Failed only from recorded
                // progress, and the core distinguishes only Done/Failed, so Active is faithful.
                Status = _ => QuestStatus.Active,
                RecordedStatus = _ => null,
            });
        return (plan.Prerequisites.Select(p => p.Quest.Name!).ToList(),
                plan.AlternativesToFail.Select(p => p.Quest.Name!).ToList());
    }

    [Fact]
    public void Locked_quest_with_active_prereq_exists()
    {
        var (questName, prereqName) = E2EQuestData.FindLockedQuestWithActivePrereq();
        Assert.False(string.IsNullOrWhiteSpace(questName));
        Assert.False(string.IsNullOrWhiteSpace(prereqName));
        Assert.NotEqual(questName, prereqName);

        // The app's own traversal agrees with the SQL: completing the quest cascades
        // exactly the prerequisite and fails nothing (QuestCascadeConfirmE2ETests).
        var c = Cascade(questName);
        Assert.Equal(new[] { prereqName }, c.Prereqs);
        Assert.Empty(c.Failures);

        // ...and completing the prerequisite cascades nothing at all: the
        // dialog-free guarantee QuestNavigationE2ETests depends on.
        var p = Cascade(prereqName);
        Assert.Empty(p.Prereqs);
        Assert.Empty(p.Failures);
    }

    [Fact]
    public void Standalone_active_quest_exists()
    {
        var questName = E2EQuestData.FindStandaloneActiveQuest();
        Assert.False(string.IsNullOrWhiteSpace(questName));

        // Completing it must cascade nothing (no dialog in the one-click e2e flow).
        var c = Cascade(questName);
        Assert.Empty(c.Prereqs);
        Assert.Empty(c.Failures);
    }

    [Fact]
    public void Quest_with_single_alternative_exists_and_resolves_ids()
    {
        var (questName, altName, questId, altId) = E2EQuestData.FindQuestWithSingleAlternative();
        Assert.False(string.IsNullOrWhiteSpace(altName));
        Assert.Equal(questId, E2EQuestData.QuestIdByName(questName));
        Assert.Equal(altId, E2EQuestData.QuestIdByName(altName));

        // The app's traversal previews exactly the one guaranteed auto-failure.
        Assert.Equal(new[] { altName }, Cascade(questName).Failures);
    }
}
