using TarkovHelper.Services;
using static TarkovHelper.Tests.QuestTabDriver;

namespace TarkovHelper.Tests;

/// <summary>
/// R4 on the build under test: progress recorded before a data refresh still shows against the
/// same quest afterwards, including a quest the refresh renamed.
/// <para>
/// The mechanism is the one thing the whole 1.1 identity design rests on, and it is invisible
/// from the outside: recorded progress is keyed by the quest's normalized name, the published
/// database now carries that value in a column, and a renamed quest keeps the value its
/// original title produced rather than the one its new title would. If the column ever drifted
/// to a different spelling, nothing would break, throw, or show red - the completions would
/// simply stop appearing, which is exactly the failure this test exists to make loud.
/// </para>
/// <para>
/// The quest is chosen from the shipped database rather than named, so the test survives every
/// data update. It prefers a quest whose stored normalized name no longer matches its title,
/// which is precisely a carried-over rename; that only exists once the 1.1 data ships, so
/// before then it falls back to an ordinary quest and still exercises the same read path.
/// </para>
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class ProgressCarryOverE2ETests : E2ETestBase
{
    [E2EFact]
    public void A_completion_keyed_by_the_stored_normalized_name_shows_against_its_quest()
    {
        var quest = PickQuest();
        var configDir = NewConfigDir();

        SeedFixedProfile(configDir);
        E2EDb.SeedQuestProgress(configDir, ProfileService.PvpProfileId, quest.Id, quest.NormalizedName, "Done");

        using var app = LaunchMaximized(configDir);

        ShowQuestDetail(app, quest.Name, "Done");

        Assert.Equal(quest.Name, app.GetElementText("TxtDetailName"));
    }

    [E2EFact]
    public void A_failure_keyed_the_same_way_shows_as_failed()
    {
        var quest = PickQuest();
        var configDir = NewConfigDir();

        SeedFixedProfile(configDir);
        E2EDb.SeedQuestProgress(configDir, ProfileService.PvpProfileId, quest.Id, quest.NormalizedName, "Failed");

        using var app = LaunchMaximized(configDir);

        ShowQuestDetail(app, quest.Name, "Failed");

        Assert.Equal(quest.Name, app.GetElementText("TxtDetailName"));
    }

    [E2EFact]
    public void Progress_recorded_under_a_row_key_alone_still_finds_its_quest()
    {
        // The fielded build writes the row key and the normalized name together, but a row
        // whose stored name is null (an older write path) has to keep resolving through the id.
        var quest = PickQuest();
        var configDir = NewConfigDir();

        SeedFixedProfile(configDir);
        E2EDb.SeedQuestProgress(configDir, ProfileService.PvpProfileId, quest.Id, normalizedName: null, "Done");

        using var app = LaunchMaximized(configDir);

        ShowQuestDetail(app, quest.Name, "Done");

        Assert.Equal(quest.Name, app.GetElementText("TxtDetailName"));
    }

    /// <summary>
    /// Creates the config dir's user_data.db and pins the profile the seeded progress belongs to.
    /// <para>
    /// Log monitoring is off because the app's profile auto-detection wins over the stored
    /// selection by design: on a machine with real EFT logs it reads them at startup and
    /// switches to whichever profile the last session used, leaving these tests asserting
    /// against an empty profile. That is correct app behaviour and a wrong test environment.
    /// </para>
    /// </summary>
    private static void SeedFixedProfile(string configDir)
    {
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, "app.logMonitoringEnabled", "False");
    }

    /// <summary>
    /// Picks a quest from the shipped database whose name is a unique search substring, so the
    /// quest list filters down to exactly one row. Prefers a quest whose stored normalized name
    /// differs from what its current title would produce, because that is a rename whose
    /// progress was carried across; falls back to any quest when the shipped data has none yet.
    /// <para>
    /// Both the "unique substring" and the "carried rename" judgements live in
    /// <see cref="E2EQuests"/>, which the legacy smoke reads its candidate through as well. The
    /// title-to-key rule is <c>QuestNormalizedName.SqlForm</c>, the pipeline's own pinned
    /// reproduction of the app's SQL expression: a copy spelled out here would not detect drift
    /// from it, it would be drift (see the E2EQuests remarks).
    /// </para>
    /// </summary>
    private static E2EQuests.Quest PickQuest()
    {
        var catalogue = E2EQuests.Read(TestSeed.DatabasePath);

        Assert.True(catalogue.UniquelySearchable.Count > 0,
            "no quest in the seed database has a name that is a unique search substring");

        return catalogue.UniquelySearchable.FirstOrDefault(q => q.IsCarriedRename)
            ?? catalogue.UniquelySearchable[0];
    }
}
