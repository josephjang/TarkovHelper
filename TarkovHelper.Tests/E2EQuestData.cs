using Microsoft.Data.Sqlite;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Quest test data derived from the app's bundled tarkov_data.db (read in place at
/// TestSeed.DatabasePath) instead of hard-coded quest names, so the e2e tests survive
/// database updates. Shared by QuestNavigationE2ETests and QuestCascadeConfirmE2ETests.
/// All queries assume a fresh profile: no progress, default player level.
/// The WHERE clauses are assembled from the alias-parameterized fragments below
/// so the availability rules cannot drift between queries.
/// </summary>
internal static class E2EQuestData
{
    /// <summary>
    /// Opens the asset db read-only, runs one command, and hands it to
    /// <paramref name="read"/>: the single home for the connection plumbing all
    /// five query methods share (parameters are added inside <paramref name="read"/>).
    /// </summary>
    private static T Query<T>(string sql, Func<SqliteCommand, T> read)
    {
        using var connection = new SqliteConnection($"Data Source={TestSeed.DatabasePath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return read(command);
    }

    /// <summary>
    /// The quest has no categorical availability gate on a fresh profile: no
    /// faction/edition restriction and no prestige/decode requirement.
    /// </summary>
    private static string Ungated(string alias) => $@"
              {alias}.Faction IS NULL AND {alias}.RequiredEdition IS NULL
              AND ({alias}.RequiredPrestigeLevel IS NULL OR {alias}.RequiredPrestigeLevel = 0)
              AND ({alias}.RequiredDecodeCount IS NULL OR {alias}.RequiredDecodeCount = 0)";

    /// <summary>
    /// The quest clears all three of the gates that answer LevelLocked on a fresh profile: it is
    /// reachable at the app's default player level, has no scav-karma gate, and is gated on no
    /// trader's loyalty.
    /// <para>
    /// The loyalty clause joined the other two when the gate shipped. A fresh profile reads every
    /// trader at level 1, so a quest carrying any published requirement (they run 2 to 4) is
    /// LevelLocked, not Active - which is what these fixtures are picked for.
    /// </para>
    /// </summary>
    private static string ReachableAtDefaultLevel(string alias) => $@"
              ({alias}.MinLevel IS NULL OR {alias}.MinLevel <= {SettingsService.DefaultPlayerLevel})
              AND {alias}.MinScavKarma IS NULL
              AND NOT EXISTS (SELECT 1 FROM QuestTraderRequirements tr
                              WHERE tr.QuestId = {alias}.Id)";

    /// <summary>
    /// The quest appears in no OptionalQuests row (as the quest or the alternative),
    /// so completing it can never auto-fail anything.
    /// </summary>
    private static string NoAlternatives(string alias) => $@"
              NOT EXISTS (SELECT 1 FROM OptionalQuests o
                          WHERE o.QuestId = {alias}.Id OR o.AlternativeQuestId = {alias}.Id)";

    /// <summary>
    /// The quest's English name is unique as a case-insensitive search substring
    /// across every quest's EN/KO/JA names (matching the quest-list search), so
    /// typing it filters the list down to exactly that quest.
    /// </summary>
    private static string UniqueSearchName(string alias) => $@"
              (SELECT COUNT(*) FROM Quests q2
                   WHERE instr(lower(q2.Name), lower({alias}.Name)) > 0
                      OR instr(lower(ifnull(q2.NameKO, '')), lower({alias}.Name)) > 0
                      OR instr(lower(ifnull(q2.NameJA, '')), lower({alias}.Name)) > 0) = 1";

    /// <summary>
    /// A quest that is Locked on a fresh profile (exactly one Complete-type
    /// prerequisite, nothing else gating it) whose prerequisite is Active on a fresh
    /// profile (no prerequisites of its own, no level/karma/edition/faction gate).
    /// The quest's English name must be unique as a search substring across all
    /// quest names so searching it filters the list down to that single quest.
    ///
    /// Neither the quest nor the prerequisite may appear in OptionalQuests (as
    /// QuestId or AlternativeQuestId): completing the PREREQUISITE must have an
    /// empty cascade so no quest-complete confirmation dialog appears (relied on by
    /// QuestNavigationE2ETests.Detail_buttons_act_on_the_shown_quest_while_it_is_hidden_by_filters),
    /// and completing the QUEST must cascade exactly one completion and zero
    /// failures (relied on by QuestCascadeConfirmE2ETests).
    /// </summary>
    public static (string QuestName, string PrereqName) FindLockedQuestWithActivePrereq()
    {
        var sql = $@"
            SELECT q.Name, p.Name
            FROM Quests q
            JOIN QuestRequirements r ON r.QuestId = q.Id
            JOIN Quests p ON p.Id = r.RequiredQuestId
            WHERE r.RequirementType = 'Complete'
              AND (SELECT COUNT(*) FROM QuestRequirements r2 WHERE r2.QuestId = q.Id) = 1
              AND {Ungated("q")}
              AND {Ungated("p")}
              AND {ReachableAtDefaultLevel("p")}
              AND NOT EXISTS (SELECT 1 FROM QuestRequirements pr WHERE pr.QuestId = p.Id)
              AND {NoAlternatives("q")}
              AND {NoAlternatives("p")}
              AND p.Name <> q.Name
              AND {UniqueSearchName("q")}
            ORDER BY q.Name
            LIMIT 1";

        return Query(sql, command =>
        {
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read(),
                "tarkov_data.db has no locked quest with a single active prerequisite matching the test constraints");
            return (reader.GetString(0), reader.GetString(1));
        });
    }

    /// <summary>
    /// A quest that is Active on a fresh profile with nothing to cascade: no
    /// prerequisites at all and no OptionalQuests involvement, so completing it must
    /// NOT show the confirmation dialog. Its English name must be unique as a search
    /// substring, like <see cref="FindLockedQuestWithActivePrereq"/>.
    /// </summary>
    public static string FindStandaloneActiveQuest()
    {
        var sql = $@"
            SELECT q.Name
            FROM Quests q
            WHERE NOT EXISTS (SELECT 1 FROM QuestRequirements r WHERE r.QuestId = q.Id)
              AND {NoAlternatives("q")}
              AND {Ungated("q")}
              AND {ReachableAtDefaultLevel("q")}
              AND {UniqueSearchName("q")}
            ORDER BY q.Name
            LIMIT 1";

        return Query(sql, command =>
        {
            var name = command.ExecuteScalar() as string;
            Assert.False(string.IsNullOrEmpty(name),
                "tarkov_data.db has no prerequisite-free, alternative-free quest matching the test constraints");
            return name!;
        });
    }

    /// <summary>
    /// A quest with exactly ONE mutually exclusive alternative (a single
    /// OptionalQuests row) and no availability gate, whose alternative is a
    /// different quest it does not directly require: completing it must preview
    /// exactly one auto-failed alternative ("Will be FAILED (1)") regardless of the
    /// length of its prerequisite chain, and Confirm must persist the alternative
    /// as Failed. The English name is a unique search substring like the other
    /// queries. Also returns both quest Ids (the keys QuestProgress rows are
    /// written under) for persistence assertions.
    /// </summary>
    public static (string QuestName, string AltName, string QuestId, string AltId) FindQuestWithSingleAlternative()
    {
        var sql = $@"
            SELECT q.Name, alt.Name, q.Id, alt.Id
            FROM Quests q
            JOIN OptionalQuests o ON o.QuestId = q.Id
            JOIN Quests alt ON alt.Id = o.AlternativeQuestId
            WHERE (SELECT COUNT(*) FROM OptionalQuests o2 WHERE o2.QuestId = q.Id) = 1
              AND {Ungated("q")}
              AND alt.Name <> q.Name
              AND NOT EXISTS (SELECT 1 FROM QuestRequirements qr
                              WHERE qr.QuestId = q.Id AND qr.RequiredQuestId = alt.Id)
              AND {UniqueSearchName("q")}
            ORDER BY q.Name
            LIMIT 1";

        return Query(sql, command =>
        {
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read(),
                "tarkov_data.db has no ungated quest with exactly one alternative matching the test constraints");
            return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
        });
    }

    /// <summary>
    /// A quest that is loyalty-locked and nothing else on a fresh profile: exactly one trader
    /// loyalty row, naming its own trader at level 2 or more, with no prerequisite, no
    /// alternative, no categorical gate, and a minimum level the default profile reaches. So it
    /// reads LevelLocked with an <c>LL{n}</c> badge before anything is entered and Active the
    /// moment that one trader's level is set, which is the whole flow
    /// <see cref="QuestLoyaltyE2ETests"/> drives.
    /// <para>
    /// Its own trader, not another's, so the badge is the narrow <c>LL{n}</c> form and one click
    /// in one drawer group clears it. The English name is a unique search substring like the
    /// other queries. Twenty-five candidates in the 1.1.0 seed.
    /// </para>
    /// </summary>
    /// <returns>
    /// The quest's name and id, the trader's NormalizedName (the drawer's automation ids are
    /// built from it) and the level the row asks for.
    /// </returns>
    public static (string QuestName, string QuestId, string TraderNormalizedName, int Level)
        FindLoyaltyGatedQuest()
    {
        var sql = $@"
            SELECT q.Name, q.Id, t.NormalizedName, r.RequiredLevel
            FROM Quests q
            JOIN QuestTraderRequirements r ON r.QuestId = q.Id
            JOIN Traders t ON t.Id = r.TraderId
            WHERE (SELECT COUNT(*) FROM QuestTraderRequirements r2 WHERE r2.QuestId = q.Id) = 1
              AND lower(r.TraderName) = lower(q.Trader)
              AND r.RequiredLevel >= 2
              AND t.NormalizedName IS NOT NULL AND t.NormalizedName <> ''
              AND NOT EXISTS (SELECT 1 FROM QuestRequirements qr WHERE qr.QuestId = q.Id)
              AND {Ungated("q")}
              -- ReachableAtDefaultLevel is not usable here: its third clause excludes exactly
              -- the quests this query looks for. Its other two are spelled out instead.
              AND (q.MinLevel IS NULL OR q.MinLevel <= {SettingsService.DefaultPlayerLevel})
              AND q.MinScavKarma IS NULL
              AND {NoAlternatives("q")}
              AND {UniqueSearchName("q")}
            ORDER BY q.Name
            LIMIT 1";

        return Query(sql, command =>
        {
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read(),
                "tarkov_data.db has no quest gated solely on its own trader's loyalty matching the test constraints");
            return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
        });
    }

    /// <summary>The asset-db Id of the quest with this exact English name (QuestProgress rows are keyed by it).</summary>
    public static string QuestIdByName(string name)
        => Query("SELECT Id FROM Quests WHERE Name = $name", command =>
        {
            command.Parameters.AddWithValue("$name", name);
            var id = command.ExecuteScalar() as string;
            Assert.False(string.IsNullOrEmpty(id), $"tarkov_data.db has no quest named '{name}'");
            return id!;
        });

    /// <summary>Every quest's English display name, for spotting quest links in templated lists.</summary>
    public static HashSet<string> AllQuestNames()
        => Query("SELECT Name FROM Quests WHERE Name IS NOT NULL AND Name <> ''", command =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            using var reader = command.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
            return names;
        });

    #region Kappa and Collector (feature-kappa-collector-1-1.spec.md)

    /// <summary>One flagged Kappa quest: the keys its progress row is written under, and its name.</summary>
    internal sealed record KappaQuest(string Id, string NormalizedName, string Name);

    /// <summary>One of Collector's loyalty rows: the trader, its Korean name, and the level required.</summary>
    internal sealed record CollectorTrader(
        string Id, string NormalizedName, string Name, string? NameKo, int Level);

    /// <summary>Collector's own row and its loyalty rows, in the order the badge and the unlock panel list them.</summary>
    internal sealed record CollectorQuest(
        string Id, string NormalizedName, int? MinLevel, double? MinScavKarma,
        IReadOnlyList<CollectorTrader> Traders);

    /// <summary>How many quests the seed flags as required for Kappa, Collector included: the {total} every Kappa count shows.</summary>
    public static int KappaFlagCount()
        => Query("SELECT COUNT(*) FROM Quests WHERE KappaRequired = 1",
            command => Convert.ToInt32(command.ExecuteScalar()));

    /// <summary>
    /// Every quest the seed flags as required for Kappa other than Collector itself, by name:
    /// the set the count runs over, and the ones the Kappa/Collector e2e seeds Done so that the
    /// prerequisite gate clears and the value gates show.
    /// </summary>
    public static IReadOnlyList<KappaQuest> KappaQuests()
        => Query(@"
            SELECT Id, NormalizedName, Name
            FROM Quests
            WHERE KappaRequired = 1 AND lower(NormalizedName) <> 'collector'
            ORDER BY Name", command =>
        {
            var quests = new List<KappaQuest>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                quests.Add(new KappaQuest(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            return quests;
        });

    /// <summary>
    /// Collector as the seed publishes it: its level and Scav karma thresholds, and its loyalty
    /// rows sorted the way the loader sorts them (the quest's own trader first, then the game's
    /// trader order, then by name; see <c>QuestDbService.SortIntoBadgeOrder</c>), so the first
    /// row is the trader the badge names once the level and karma are met.
    /// <see cref="E2EQuestDataTests"/> pins that order against the loader's own.
    /// </summary>
    public static CollectorQuest Collector()
    {
        var (id, normalizedName, giver, minLevel, minScavKarma) = Query(@"
            SELECT Id, NormalizedName, Trader, MinLevel, MinScavKarma
            FROM Quests
            WHERE lower(NormalizedName) = 'collector'", command =>
        {
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read(), "tarkov_data.db has no quest whose NormalizedName is 'collector'");
            return (
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3),
                reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4));
        });

        var traders = Query(@"
            SELECT t.Id, t.NormalizedName, r.TraderName, t.NameKO, r.RequiredLevel
            FROM QuestTraderRequirements r
            JOIN Traders t ON t.Id = r.TraderId
            WHERE r.QuestId = $id", command =>
        {
            command.Parameters.AddWithValue("$id", id);
            var rows = new List<CollectorTrader>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new CollectorTrader(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4)));
            }
            return rows;
        });

        var inBadgeOrder = traders
            .OrderByDescending(t => string.Equals(t.Name, giver, StringComparison.OrdinalIgnoreCase))
            .ThenBy(t => TraderDbService.DisplayRank(t.NormalizedName))
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CollectorQuest(id, normalizedName, minLevel, minScavKarma, inBadgeOrder);
    }

    #endregion
}
