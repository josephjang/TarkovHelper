using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// One quest status is decided from ONE profile settings snapshot.
/// <para>
/// The status walk consults six profile-scoped values (editions, prestige, faction, DSP decode
/// count, level, scav karma). While each of them re-read <c>SettingsService.Instance</c> for
/// itself, a profile switch landing between two of the reads produced a status derived half from
/// one profile and half from another - the exact tearing the immutable
/// <see cref="ProfileSettingsSnapshot"/> was introduced to make unobservable, reappearing in its
/// biggest consumer. These cases pin the snapshot as the single source for one evaluation.
/// </para>
/// </summary>
public class QuestStatusSettingsSnapshotTests
{
    /// <summary>
    /// A quest gated on level AND prestige, so a status can name which of the two it failed on and
    /// a snapshot that satisfies one but not the other has a distinguishable answer.
    /// </summary>
    private static TarkovTask GatedQuest() => new()
    {
        Ids = new List<string> { "q-gated" },
        Name = "gated",
        NormalizedName = "gated",
        Trader = "Prapor",
        RequiredLevel = 20,
        RequiredPrestigeLevel = 2,
    };

    private static ProfileSettingsSnapshot Settings(string profileId, int level, int prestige)
        => new(
            profileId, 0,
            PlayerLevel: level,
            ScavRep: null,
            ShowLevelLockedQuests: null,
            DspDecodeCount: null,
            PlayerFaction: null,
            HasEodEdition: null,
            HasUnheardEdition: null,
            PrestigeLevel: prestige);

    [Fact]
    public void A_status_answers_from_the_settings_snapshot_it_was_given()
    {
        var task = GatedQuest();
        var service = ProgressServiceHarness.Create(new ProgressStoreFake(), AppProfile.PvpSeason, task);

        // Prestige met, level short: the walk reaches the level gate and stops there.
        Assert.Equal(
            QuestStatus.LevelLocked,
            service.GetStatus(task, service.Snapshot, Settings("pvp", level: 5, prestige: 5)));

        // Level met, prestige short: the prestige gate runs first and wins.
        Assert.Equal(
            QuestStatus.Unavailable,
            service.GetStatus(task, service.Snapshot, Settings("pve", level: 50, prestige: 0)));
    }

    [Fact]
    public void A_torn_pair_of_profiles_cannot_produce_an_active_status()
    {
        var task = GatedQuest();
        var service = ProgressServiceHarness.Create(new ProgressStoreFake(), AppProfile.PvpSeason, task);

        // Neither profile unlocks this quest on its own. Only a status that took the level from
        // the second and the prestige from the first would come out Active, which is precisely
        // what a mid-walk publish used to be able to produce.
        var lowLevel = Settings("pvp", level: 5, prestige: 5);
        var lowPrestige = Settings("pve", level: 50, prestige: 0);

        Assert.NotEqual(QuestStatus.Active, service.GetStatus(task, service.Snapshot, lowLevel));
        Assert.NotEqual(QuestStatus.Active, service.GetStatus(task, service.Snapshot, lowPrestige));
    }

    [Fact]
    public void The_prerequisite_walk_carries_the_same_settings_snapshot()
    {
        // The prerequisite is itself settings-gated, so its status is decided by the same six
        // reads one level down. A recursion that dropped the snapshot would re-read the live one.
        var prereq = GatedQuest();
        var dependent = new TarkovTask
        {
            Ids = new List<string> { "q-dependent" },
            Name = "dependent",
            NormalizedName = "dependent",
            Trader = "Prapor",
            Previous = new List<string> { "gated" },
        };

        var service = ProgressServiceHarness.Create(
            new ProgressStoreFake(), AppProfile.PvpSeason, prereq, dependent);

        // The prerequisite is level-locked under this snapshot, so it is not Done, so the
        // dependent is Locked - decided entirely from the snapshot handed in.
        Assert.Equal(
            QuestStatus.Locked,
            service.GetStatus(dependent, service.Snapshot, Settings("pvp", level: 5, prestige: 5)));
    }
}
