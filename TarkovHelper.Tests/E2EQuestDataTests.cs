using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

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
    private static async Task<(IReadOnlyList<string> Prereqs, IReadOnlyList<string> Failures)> Cascade(
        string questName)
    {
        // Awaited rather than blocked on: a GetResult() here parks one of xunit's few workers
        // while the load's own continuations queue for another, which on a small CI runner is
        // seconds of everyone else's budget.
        Assert.True(await QuestDbService.Instance.LoadQuestsAsync(), "asset db did not load");
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

    /// <summary>
    /// The status the app itself reports for the named quest on a fresh profile: default player
    /// level, no recorded progress, no trader loyalty entered.
    /// <para>
    /// The cascade checks above prove what completing a fixture does; this proves the fixture is
    /// in the STATE its e2e assumes before anything is clicked. The two are different questions,
    /// and only this one notices a new availability gate quietly moving a fixture out of the
    /// state its suite was written around, which is exactly what the trader loyalty gate did to
    /// twenty-seven candidates.
    /// </para>
    /// </summary>
    private static async Task<QuestStatus> FreshProfileStatus(string questName)
    {
        Assert.True(await QuestDbService.Instance.LoadQuestsAsync(), "asset db did not load");
        var tasks = QuestDbService.Instance.AllQuests.ToArray();
        var task = tasks.Single(q => q.Name == questName);

        var service = ProgressServiceHarness.Create(
            new ProgressStoreFake(), AppProfile.PvpSeason, tasks);
        return service.GetStatus(
            task, service.Snapshot, ProfileSettingsSnapshot.Defaults("fresh", 0));
    }

    [Fact]
    public async Task Locked_quest_with_active_prereq_exists()
    {
        var (questName, prereqName) = E2EQuestData.FindLockedQuestWithActivePrereq();
        Assert.False(string.IsNullOrWhiteSpace(questName));
        Assert.False(string.IsNullOrWhiteSpace(prereqName));
        Assert.NotEqual(questName, prereqName);

        // The states the two suites that use this pair are written around.
        Assert.Equal(QuestStatus.Locked, await FreshProfileStatus(questName));
        Assert.Equal(QuestStatus.Active, await FreshProfileStatus(prereqName));

        // The app's own traversal agrees with the SQL: completing the quest cascades
        // exactly the prerequisite and fails nothing (QuestCascadeConfirmE2ETests).
        var c = await Cascade(questName);
        Assert.Equal(new[] { prereqName }, c.Prereqs);
        Assert.Empty(c.Failures);

        // ...and completing the prerequisite cascades nothing at all: the
        // dialog-free guarantee QuestNavigationE2ETests depends on.
        var p = await Cascade(prereqName);
        Assert.Empty(p.Prereqs);
        Assert.Empty(p.Failures);
    }

    [Fact]
    public async Task Standalone_active_quest_exists()
    {
        var questName = E2EQuestData.FindStandaloneActiveQuest();
        Assert.False(string.IsNullOrWhiteSpace(questName));

        // Active before anything is clicked: the one-click flow starts from the Complete button.
        Assert.Equal(QuestStatus.Active, await FreshProfileStatus(questName));

        // Completing it must cascade nothing (no dialog in the one-click e2e flow).
        var c = await Cascade(questName);
        Assert.Empty(c.Prereqs);
        Assert.Empty(c.Failures);
    }

    [Fact]
    public async Task Loyalty_gated_quest_exists_and_is_locked_only_by_its_loyalty_row()
    {
        var (questName, questId, traderNormalizedName, level) = E2EQuestData.FindLoyaltyGatedQuest();

        Assert.False(string.IsNullOrWhiteSpace(questName));
        Assert.Equal(questId, E2EQuestData.QuestIdByName(questName));
        Assert.False(string.IsNullOrWhiteSpace(traderNormalizedName));
        Assert.InRange(level, 2, SettingsService.MaxTraderLoyaltyLevel);

        // Locked on a fresh profile, which is the state the e2e's first assertion reads...
        Assert.Equal(QuestStatus.LevelLocked, await FreshProfileStatus(questName));

        // ...and by the loyalty row alone: entering that one trader's level makes it Active. If
        // any other gate were also holding it, the e2e's single drawer click would not flip it.
        Assert.True(await QuestDbService.Instance.LoadQuestsAsync(), "asset db did not load");
        var task = QuestDbService.Instance.AllQuests.Single(q => q.Name == questName);
        var traderId = task.TraderLoyaltyRequirements!.Single().TraderId;
        var entered = ProfileSettingsSnapshot.Defaults("fresh", 0)
            with { TraderLoyalty = TraderLoyaltyLevels.Empty.With(traderId, level) };

        var service = ProgressServiceHarness.Create(
            new ProgressStoreFake(), AppProfile.PvpSeason,
            QuestDbService.Instance.AllQuests.ToArray());
        Assert.Equal(QuestStatus.Active, service.GetStatus(task, service.Snapshot, entered));

        // Completing it must cascade nothing: the e2e never opens a dialog for it.
        var c = await Cascade(questName);
        Assert.Empty(c.Prereqs);
        Assert.Empty(c.Failures);
    }

    [Fact]
    public async Task Quest_with_single_alternative_exists_and_resolves_ids()
    {
        var (questName, altName, questId, altId) = E2EQuestData.FindQuestWithSingleAlternative();
        Assert.False(string.IsNullOrWhiteSpace(altName));
        Assert.Equal(questId, E2EQuestData.QuestIdByName(questName));
        Assert.Equal(altId, E2EQuestData.QuestIdByName(altName));

        // The app's traversal previews exactly the one guaranteed auto-failure.
        Assert.Equal(new[] { altName }, (await Cascade(questName)).Failures);
    }

    #region Kappa and Collector (feature-kappa-collector-1-1.spec.md)

    [Fact]
    public void The_Kappa_fixtures_return_rows_and_the_set_is_the_seeds_flag_count()
    {
        var kappaQuests = E2EQuestData.KappaQuests();
        var collector = E2EQuestData.Collector();

        // The flagged set is Collector plus the rest, and the e2e seeds exactly the rest Done.
        Assert.NotEmpty(kappaQuests);
        Assert.Equal(E2EQuestData.KappaFlagCount(), kappaQuests.Count + 1);
        Assert.DoesNotContain(kappaQuests, q => q.NormalizedName == collector.NormalizedName);

        // The two value gates and the loyalty rows the panel lists; the e2e seeds the first two
        // to the thresholds and clicks the rows.
        Assert.NotNull(collector.MinLevel);
        Assert.NotNull(collector.MinScavKarma);
        Assert.NotEmpty(collector.Traders);
        Assert.All(collector.Traders, t => Assert.False(string.IsNullOrWhiteSpace(t.NormalizedName)));
        // The Korean half of the e2e reads the first trader's Korean name off the panel.
        Assert.False(string.IsNullOrWhiteSpace(collector.Traders[0].NameKo));
        Assert.NotEqual(collector.Traders[0].Name, collector.Traders[0].NameKo);
    }

    /// <summary>
    /// The fixture sorts Collector's rows the way it believes the loader does. Asserted against
    /// the loader's own output rather than restated, so the e2e's "{first trader} LL4" is the
    /// badge the app really shows and not the fixture's opinion of it.
    /// </summary>
    [Fact]
    public async Task Collectors_fixture_rows_are_in_the_order_the_loader_leaves_them()
    {
        Assert.True(await QuestDbService.Instance.LoadQuestsAsync(), "asset db did not load");
        var fixture = E2EQuestData.Collector();
        var loaded = QuestDbService.Instance.AllQuests.Single(q => q.NormalizedName == fixture.NormalizedName);
        var rows = loaded.TraderLoyaltyRequirements;
        Assert.NotNull(rows);

        Assert.Equal(
            rows!.Select(r => r.TraderId).ToArray(),
            fixture.Traders.Select(t => t.Id).ToArray());
        Assert.Equal(
            rows.Select(r => r.Level).ToArray(),
            fixture.Traders.Select(t => t.Level).ToArray());
    }

    /// <summary>
    /// The states the Kappa/Collector e2e drives through, proved on the real walk over the real
    /// data: Locked on a fresh profile; with the other flagged quests Done, held by the level,
    /// then by the karma, then by the fixture's first trader, then Active. If any other gate
    /// held Collector, the e2e's seeds and seven clicks would not flip it.
    /// </summary>
    [Fact]
    public async Task Collector_walks_Locked_then_level_then_karma_then_the_first_trader_then_Active()
    {
        Assert.True(await QuestDbService.Instance.LoadQuestsAsync(), "asset db did not load");
        var tasks = QuestDbService.Instance.AllQuests.ToArray();
        var fixture = E2EQuestData.Collector();
        var collector = tasks.Single(q => q.NormalizedName == fixture.NormalizedName);
        var defaults = ProfileSettingsSnapshot.Defaults("fresh", 0);

        var fresh = ProgressServiceHarness.Create(new ProgressStoreFake(), AppProfile.PvpSeason, tasks);
        Assert.Equal(QuestStatus.Locked, fresh.GetStatus(collector, fresh.Snapshot, defaults, out var freshGate));
        Assert.Equal(QuestGate.Prerequisite, freshGate);

        var twelveDone = ProgressSnapshot.From(
            "fresh", 0,
            E2EQuestData.KappaQuests().ToDictionary(q => q.Id, _ => QuestStatus.Done),
            new Dictionary<string, bool>());
        var service = ProgressServiceHarness.Create(new ProgressStoreFake(), twelveDone, tasks);

        service.GetStatus(collector, service.Snapshot, defaults, out var gate);
        Assert.Equal(QuestGate.PlayerLevel, gate);

        var atLevel = defaults with { PlayerLevel = fixture.MinLevel };
        service.GetStatus(collector, service.Snapshot, atLevel, out gate);
        Assert.Equal(QuestGate.ScavKarma, gate);

        var atKarma = atLevel with { ScavRep = fixture.MinScavKarma };
        service.GetStatus(collector, service.Snapshot, atKarma, out gate);
        Assert.Equal(QuestGate.TraderLoyalty, gate);
        Assert.Equal(
            fixture.Traders[0].Id,
            QuestProgressService.FirstUnmetTraderLoyalty(collector, atKarma)!.TraderId);

        var levels = TraderLoyaltyLevels.Empty;
        foreach (var trader in fixture.Traders) levels = levels.With(trader.Id, trader.Level);
        Assert.Equal(
            QuestStatus.Active,
            service.GetStatus(collector, service.Snapshot, atKarma with { TraderLoyalty = levels }, out _));
    }

    #endregion
}
