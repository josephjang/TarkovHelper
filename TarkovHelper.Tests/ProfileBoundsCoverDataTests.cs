using System.Globalization;
using System.IO;
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
        var levels = Column(
            "SELECT MinLevel FROM Quests WHERE MinLevel IS NOT NULL", r => r.GetInt32(0));

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
            "SELECT RequiredPrestigeLevel FROM Quests WHERE RequiredPrestigeLevel IS NOT NULL",
            r => r.GetInt32(0));

        Assert.NotEmpty(levels);
        Assert.All(levels, level =>
            Assert.InRange(level, SettingsService.MinPrestigeLevel, SettingsService.MaxPrestigeLevel));
    }

    [Fact]
    public void Every_published_scav_karma_requirement_is_one_the_scav_rep_input_reaches()
    {
        // The other bound this phase moved: the floor from -6.0 to -7.0. A quest asking for a
        // karma the input cannot be set to is one the player can never satisfy.
        var karma = Column(
            "SELECT MinScavKarma FROM Quests WHERE MinScavKarma IS NOT NULL", r => r.GetDouble(0));

        Assert.NotEmpty(karma);
        Assert.All(karma, value =>
            Assert.InRange(value, SettingsService.MinScavRep, SettingsService.MaxScavRep));
    }

    /// <summary>
    /// Karma is the only bound here that is not a whole number, and reading it must not depend on
    /// the machine's culture. Every published row is a whole number today, so the round trip is
    /// pinned against a database carrying the fraction the first Fence gate would: rendered to
    /// text under a comma-decimal culture, -1.5 becomes "-1,5", which an invariant parse reads as
    /// -15 and this file then reports as a karma the input cannot reach. The bound would pass or
    /// fail on where CI happened to run.
    /// </summary>
    [Fact]
    public void A_fractional_karma_requirement_reads_the_same_under_a_comma_decimal_culture()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"karma-locale-{Guid.NewGuid():N}.db");
        var culture = CultureInfo.CurrentCulture;

        try
        {
            WriteKarmaRows(databasePath, -1.5);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            var karma = Column(
                databasePath,
                "SELECT MinScavKarma FROM Quests WHERE MinScavKarma IS NOT NULL",
                r => r.GetDouble(0));

            // The NULL row the fixture also writes is the query's job to drop, not the reader's.
            Assert.Equal(-1.5, Assert.Single(karma));
            Assert.InRange(karma[0], SettingsService.MinScavRep, SettingsService.MaxScavRep);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public void Every_published_decode_count_is_one_the_DSP_input_reaches()
    {
        // Exact-match gate: IsDspRequirementMet compares for equality, so a count outside the
        // range matches no Make Amends branch at all and silently locks every one of them.
        var counts = Column(
            "SELECT RequiredDecodeCount FROM Quests WHERE RequiredDecodeCount IS NOT NULL",
            r => r.GetInt32(0));

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
              SELECT ExcludedEdition FROM Quests WHERE ExcludedEdition IS NOT NULL AND ExcludedEdition <> ''",
            r => r.GetString(0));

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

    /// <summary>
    /// A throwaway database with just enough of the Quests table for the karma query: one row
    /// carrying <paramref name="karma"/> as REAL, and one carrying NULL.
    /// </summary>
    private static void WriteKarmaRows(string databasePath, double karma)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE Quests (MinScavKarma REAL); INSERT INTO Quests VALUES ($karma), (NULL);";
        command.Parameters.AddWithValue("$karma", karma);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The first column of every row the query returns, read from the bundled seed as
    /// <typeparamref name="T"/> and NULLs excluded by the query.
    /// </summary>
    private static List<T> Column<T>(string sql, Func<SqliteDataReader, T> read)
        => Column(TestSeed.DatabasePath, sql, read);

    /// <summary>
    /// The same read against an arbitrary database, which is what lets the locale guard below
    /// point at a value the published data does not carry yet.
    /// <para>
    /// The value is taken off the reader in its own type rather than rendered to text and parsed
    /// back. A round trip through <c>ToString()</c> formats under the machine's culture, so the
    /// first fractional karma to be published would come back as "-1,5" on a comma-decimal
    /// machine and parse invariantly to -15 - a bound this file exists to check, failing or
    /// passing depending on where CI runs.
    /// </para>
    /// </summary>
    private static List<T> Column<T>(string databasePath, string sql, Func<SqliteDataReader, T> read)
    {
        var values = new List<T>();

        using (var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly"))
        {
            connection.Open();
            using var command = new SqliteCommand(sql, connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0)) values.Add(read(reader));
            }
        }

        SqliteConnection.ClearAllPools();
        return values;
    }
}
