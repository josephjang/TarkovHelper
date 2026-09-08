using System.Globalization;
using Microsoft.Data.Sqlite;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// Every requirement the published data states can be entered in the profile drawer.
/// <para>
/// The drawer's inputs are hard-coded bounds and the data they are judged against is published
/// separately, so the two drift silently: patch 1.1 added prestige 5 and 6 while the prestige
/// stepper still stopped at 5, and dropped Fence reputation to -7 while the Scav Rep floor was
/// still -6. Neither was noticed until this phase audited them by hand. A requirement outside an
/// input's range is a quest the player can never unlock and never find out why, so the audit is
/// pinned here rather than repeated by hand next patch: a publish that outgrows an input fails
/// CI on the publish PR.
/// </para>
/// <para>
/// Read from the app's bundled seed (<see cref="TestSeed"/>), which is the database the app under
/// test actually loads.
/// </para>
/// </summary>
public sealed class ProfileBoundsCoverDataTests
{
    [Fact]
    public void Every_published_minimum_level_is_one_the_level_input_reaches()
    {
        var levels = Column("SELECT MinLevel FROM Quests WHERE MinLevel IS NOT NULL")
            .Select(v => int.Parse(v, CultureInfo.InvariantCulture))
            .ToList();

        Assert.NotEmpty(levels);
        Assert.All(levels, level =>
            Assert.InRange(level, SettingsService.MinPlayerLevel, SettingsService.MaxPlayerLevel));
    }

    [Fact]
    public void Every_published_prestige_requirement_is_one_the_prestige_input_reaches()
    {
        // The bound this phase moved from 5 to 6. The published rows stop at 3 today, because the
        // 1.1 refresh left Prestige 5 and 6 out of the app; the point of the bound is the player's
        // own prestige, which the data cannot tell us, so this only proves no row is unreachable.
        var levels = Column(
                "SELECT RequiredPrestigeLevel FROM Quests WHERE RequiredPrestigeLevel IS NOT NULL")
            .Select(v => int.Parse(v, CultureInfo.InvariantCulture))
            .ToList();

        Assert.NotEmpty(levels);
        Assert.All(levels, level =>
            Assert.InRange(level, SettingsService.MinPrestigeLevel, SettingsService.MaxPrestigeLevel));
    }

    [Fact]
    public void Every_published_scav_karma_requirement_is_one_the_scav_rep_input_reaches()
    {
        // The other bound this phase moved: the floor from -6.0 to -7.0. A quest asking for a
        // karma the input cannot be set to is one the player can never satisfy.
        var karma = Column("SELECT MinScavKarma FROM Quests WHERE MinScavKarma IS NOT NULL")
            .Select(v => double.Parse(v, CultureInfo.InvariantCulture))
            .ToList();

        Assert.NotEmpty(karma);
        Assert.All(karma, value =>
            Assert.InRange(value, SettingsService.MinScavRep, SettingsService.MaxScavRep));
    }

    [Fact]
    public void Every_published_decode_count_is_one_the_DSP_input_reaches()
    {
        // Exact-match gate: IsDspRequirementMet compares for equality, so a count outside the
        // range matches no Make Amends branch at all and silently locks every one of them.
        var counts = Column(
                "SELECT RequiredDecodeCount FROM Quests WHERE RequiredDecodeCount IS NOT NULL")
            .Select(v => int.Parse(v, CultureInfo.InvariantCulture))
            .ToList();

        Assert.NotEmpty(counts);
        Assert.All(counts, count =>
            Assert.InRange(count, SettingsService.MinDspDecodeCount, SettingsService.MaxDspDecodeCount));
    }

    /// <summary>
    /// Every edition value the data states is one the edition gate recognises. Unlike the numeric
    /// bounds, this one fails OPEN in the app: <c>IsEditionRequirementMet</c> ignores a value it
    /// does not know, so a new edition would silently show its quests to everyone rather than
    /// hiding them. That is the quieter failure of the two, which is why it is pinned here.
    /// </summary>
    [Fact]
    public void Every_published_edition_value_is_one_the_edition_gate_recognises()
    {
        var stated = Column(
                @"SELECT RequiredEdition FROM Quests WHERE RequiredEdition IS NOT NULL AND RequiredEdition <> ''
                  UNION
                  SELECT ExcludedEdition FROM Quests WHERE ExcludedEdition IS NOT NULL AND ExcludedEdition <> ''")
            .ToList();

        Assert.NotEmpty(stated);
        Assert.All(stated, edition =>
            Assert.True(
                EditionGateReads(edition),
                $"the published edition '{edition}' is not one QuestProgressService recognises, " +
                "so its quests are shown to every player whatever they own"));
    }

    /// <summary>
    /// Whether the edition gate actually branches on <paramref name="edition"/>, asked of the
    /// gate itself rather than of a list of strings copied out of it: a quest requiring the
    /// edition is hidden from a player who owns nothing, and shown to one who owns both.
    /// </summary>
    private static bool EditionGateReads(string edition)
    {
        var task = new TarkovTask
        {
            Ids = new List<string> { "q" },
            Name = "q",
            NormalizedName = "q",
            RequiredEdition = edition,
        };

        return !QuestProgressService.IsEditionRequirementMet(task, Owning(eod: false, unheard: false))
               && QuestProgressService.IsEditionRequirementMet(task, Owning(eod: true, unheard: true));
    }

    private static ProfileSettingsSnapshot Owning(bool eod, bool unheard)
        => ProfileSettingsSnapshot.Defaults("profile", 0)
            with { HasEodEdition = eod, HasUnheardEdition = unheard };

    /// <summary>The first column of every row the query returns, NULLs excluded by the query.</summary>
    private static List<string> Column(string sql)
    {
        var values = new List<string>();

        using (var connection = new SqliteConnection($"Data Source={TestSeed.DatabasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = new SqliteCommand(sql, connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0)) values.Add(reader.GetValue(0).ToString() ?? "");
            }
        }

        SqliteConnection.ClearAllPools();
        return values;
    }
}
