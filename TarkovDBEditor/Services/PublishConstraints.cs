using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace TarkovDBEditor.Services
{
    /// <summary>
    /// The value vocabularies, row shapes and NULL rules every build already in the field
    /// depends on. One declaration, evaluated over either source: the rows a refresh has just
    /// built in memory (<see cref="RefreshDataService.RefreshGuards.AssertPublishConstraints"/>,
    /// which fails earlier and with the run's own context), and the candidate
    /// <c>tarkov_data.db</c> a publish is about to copy onto the endpoints
    /// (<c>DataPublishService</c>, which is the last gate a byte passes).
    /// <para>
    /// Both are needed because the build phase is not the only way a row reaches the file. A
    /// hand edit in the editor's DataGrid, a <see cref="BsgIdBackfillService"/> run, or any
    /// correction made after a refresh writes straight to the database through
    /// <c>DatabaseService</c>'s generic UPDATE, which can rewrite <c>Quests.Name</c> without
    /// <c>NormalizedName</c> and desynchronize the two. That desynchronization un-keys the
    /// quest's recorded progress in every install, silently, and cannot be repaired after the
    /// fact: installs poll every five minutes and install what they download without checking
    /// it.
    /// </para>
    /// <para>
    /// Rules are stated over the values the app reads, not over the columns that happen to
    /// hold them, so a candidate published before a column existed is checked the way the app
    /// reads it rather than refused for lacking it. See
    /// docs/decisions/feature-quest-data-1-1-refresh.spec.md, "Pipeline guards".
    /// </para>
    /// </summary>
    public static class PublishConstraints
    {
        /// <summary>
        /// Above this share of quests without a trader, the trader cache is wrong. A share
        /// rather than "none", because the run cannot tell a trader the wiki never named from
        /// one the cache failed to resolve.
        /// </summary>
        public const double MaxTradersMissing = 0.05;

        /// <summary>
        /// The requirement types every build in the field has a reading for. Literals on
        /// purpose, not <see cref="RefreshDataService.RequirementStatus"/>: this list restates
        /// what the INSTALLED build can read, so a type added to that enum must fail here until
        /// the app has a reading for it.
        /// </summary>
        private static readonly string[] ReadableRequirementTypes = { "Complete", "Accept", "Fail" };

        /// <summary>The factions the fielded build compares for equality.</summary>
        private static readonly string[] ReadableFactions = { "Bear", "Usec" };

        /// <summary>
        /// One quest as the constraints see it: the fields the fielded build reads to decide
        /// which quest a row is and whether it can show it.
        /// </summary>
        /// <param name="NormalizedName">
        /// The value stored in the column, which is NULL on a candidate published before the
        /// column existed and on a row nothing has written one for. What the app actually keys
        /// progress by is <see cref="EffectiveNormalizedName"/>.
        /// </param>
        public sealed record QuestRow(
            string Id, string Name, string? Faction, string? Trader, string? NormalizedName);

        /// <summary>One prerequisite edge as the constraints see it.</summary>
        public sealed record RequirementRow(
            string QuestId,
            string RequiredQuestId,
            string RequirementType,
            string? AltRequirementType,
            int GroupId);

        /// <summary>
        /// A database about to be published, or the in-memory rows that would become one.
        /// </summary>
        public sealed class Candidate
        {
            public IReadOnlyList<QuestRow> Quests { get; init; } = Array.Empty<QuestRow>();

            public IReadOnlyList<RequirementRow> Requirements { get; init; } =
                Array.Empty<RequirementRow>();

            /// <summary>
            /// Whether <c>Quests.NormalizedName</c> exists at all. False only for a candidate
            /// published before the column: every row then has no stored value, which is the
            /// documented older format rather than a row missing one, so the empty-value rule
            /// has nothing to say about it. The rules that compare what the app WILL key
            /// progress by still apply, because the app derives that value from the name.
            /// </summary>
            public bool StoresNormalizedNames { get; init; } = true;
        }

        /// <summary>
        /// The value the app will key this quest's recorded progress by: the stored column when
        /// it holds one, otherwise the value the app derives from the name. Mirrors
        /// <c>QuestDbService.LoadBaseQuestsAsync</c>, which falls back to the SQL expression
        /// when the column is absent and to <c>GenerateNormalizedName</c> when a row's value is
        /// NULL; both spell the same rule as <see cref="QuestNormalizedName.SqlForm"/>.
        /// </summary>
        public static string EffectiveNormalizedName(QuestRow quest) =>
            string.IsNullOrEmpty(quest.NormalizedName)
                ? QuestNormalizedName.SqlForm(quest.Name ?? "")
                : quest.NormalizedName;

        /// <summary>The same candidate, read off the rows a refresh built rather than a file.</summary>
        public static Candidate Of(QuestsFetchResult result)
        {
            ArgumentNullException.ThrowIfNull(result);

            return new Candidate
            {
                Quests = result.Quests
                    .Select(q => new QuestRow(q.Id, q.Name, q.Faction, q.Trader, q.NormalizedName))
                    .ToList(),
                Requirements = result.Requirements
                    .Select(r => new RequirementRow(
                        r.QuestId, r.RequiredQuestId, r.RequirementType, r.AltRequirementType, r.GroupId))
                    .ToList(),
                // A refresh always writes the column, so a blank value there is a defect rather
                // than an older format.
                StoresNormalizedNames = true,
            };
        }

        /// <summary>
        /// Reads a candidate database file the way the app reads it: read-only, feature
        /// detecting every table and column the app feature detects, so a database published
        /// before a column existed is checked rather than refused for lacking it.
        /// <para>
        /// A missing table reads as no rows. What a file with no Quests table at all is doing
        /// on the publish path is a different question, answered by the app's own reader and by
        /// the schema drift guard over the published file; there are simply no quest rows here
        /// to be wrong about.
        /// </para>
        /// </summary>
        public static async Task<Candidate> ReadAsync(string databasePath)
        {
            ArgumentNullException.ThrowIfNull(databasePath);

            try
            {
                await using var connection =
                    new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
                await connection.OpenAsync();

                var storesNormalizedNames =
                    await ColumnExistsAsync(connection, "Quests", "NormalizedName");

                return new Candidate
                {
                    Quests = await ReadQuestsAsync(connection, storesNormalizedNames),
                    Requirements = await ReadRequirementsAsync(connection),
                    StoresNormalizedNames = storesNormalizedNames,
                };
            }
            finally
            {
                // Pooled connections keep the file open, and the caller goes on to stamp, hash
                // and copy it.
                SqliteConnection.ClearAllPools();
            }
        }

        private static async Task<IReadOnlyList<QuestRow>> ReadQuestsAsync(
            SqliteConnection connection, bool storesNormalizedNames)
        {
            var quests = new List<QuestRow>();
            if (!await TableExistsAsync(connection, "Quests"))
                return quests;

            await using var command = connection.CreateCommand();
            // Ordered so the rows a refusal names come out in the same order every run.
            command.CommandText =
                "SELECT Id, Name, Faction, Trader"
                + (storesNormalizedNames ? ", NormalizedName" : ", NULL")
                + " FROM Quests ORDER BY Name, Id";

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                quests.Add(new QuestRow(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4)));
            }

            return quests;
        }

        private static async Task<IReadOnlyList<RequirementRow>> ReadRequirementsAsync(
            SqliteConnection connection)
        {
            var requirements = new List<RequirementRow>();
            if (!await TableExistsAsync(connection, "QuestRequirements"))
                return requirements;

            // Feature detected exactly as QuestDbService.LoadQuestRequirementsAsync does it: a
            // database published before the column simply carries no second type.
            var hasAltRequirementType =
                await ColumnExistsAsync(connection, "QuestRequirements", "AltRequirementType");

            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT QuestId, RequiredQuestId, RequirementType, GroupId"
                + (hasAltRequirementType ? ", AltRequirementType" : ", NULL")
                + " FROM QuestRequirements ORDER BY QuestId, GroupId, RequiredQuestId";

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                requirements.Add(new RequirementRow(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    // The app reads a NULL type as Complete, so the constraint reads it the same
                    // way rather than reporting a value the app never sees.
                    reader.IsDBNull(2) ? "Complete" : reader.GetString(2),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(3) ? 0 : reader.GetInt32(3)));
            }

            return requirements;
        }

        /// <summary>Whether a table exists, the way the app asks.</summary>
        private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
            command.Parameters.AddWithValue("@name", tableName);
            return await command.ExecuteScalarAsync() != null;
        }

        /// <summary>
        /// Whether a column exists, the way the app asks
        /// (<c>QuestDbService.ColumnExistsAsync</c>). PRAGMA takes no parameters and the table
        /// names passed here are literals in this file, never user input.
        /// </summary>
        private static async Task<bool> ColumnExistsAsync(
            SqliteConnection connection, string tableName, string columnName)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName})";

            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Every way this candidate would publish data a build in the field cannot read
        /// correctly, named one by one. Empty means it holds none of them.
        /// </summary>
        public static IReadOnlyList<string> Problems(Candidate candidate)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            var problems = new List<string>();
            problems.AddRange(RequirementProblems(candidate));
            problems.AddRange(QuestProblems(candidate));
            return problems;
        }

        private static IEnumerable<string> RequirementProblems(Candidate candidate)
        {
            var badTypes = candidate.Requirements
                .Where(r => !ReadableRequirementTypes.Contains(r.RequirementType, StringComparer.Ordinal))
                .Select(r => r.RequirementType)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (badTypes.Count > 0)
            {
                yield return
                    $"RequirementType outside {{Complete, Accept, Fail}}: {string.Join(", ", badTypes)}. "
                    + "The fielded build treats an unknown type as never satisfied, locking the quest forever.";
            }

            // The same vocabulary, because the second column reaches the app as a second entry
            // in the same status list and is read by the same code. NULL is the usual value and
            // means "nothing else satisfies this row".
            var badAltTypes = candidate.Requirements
                .Where(r => r.AltRequirementType != null
                            && !ReadableRequirementTypes.Contains(r.AltRequirementType, StringComparer.Ordinal))
                .Select(r => r.AltRequirementType!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (badAltTypes.Count > 0)
            {
                yield return
                    $"AltRequirementType outside {{NULL, Complete, Accept, Fail}}: {string.Join(", ", badAltTypes)}. "
                    + "A build that reads the column treats an unknown type as never satisfied, so the row would "
                    + "offer no second way to satisfy it.";
            }

            // A repeat of the primary type is not a second way to satisfy the row, it is a
            // mapping that lost track of what the primary already covers.
            var echoedAltTypes = candidate.Requirements
                .Where(r => r.AltRequirementType != null && r.AltRequirementType == r.RequirementType)
                .Select(r => $"{r.QuestId} <- {r.RequiredQuestId} ({r.RequirementType})")
                .ToList();
            if (echoedAltTypes.Count > 0)
            {
                yield return
                    "AltRequirementType repeats RequirementType on: "
                    + string.Join(", ", echoedAltTypes.Take(10))
                    + ". The second column exists for a state the first one does not already cover.";
            }

            // The fielded build keys incoming requirement rows by prerequisite alone
            // (QuestDbService.LoadQuestRequirementsAsync) and discards every later row naming
            // the same one, whatever group it is in. Two rows for one pair therefore publish as
            // one, and which one survives is the row order: an OR group can lose a branch and
            // become an unsatisfiable singleton. Grouped case-insensitively on the prerequisite
            // because that is how the reader compares it, so two rows it would collapse read as
            // one pair here too.
            var shadowed = candidate.Requirements
                .GroupBy(r => (r.QuestId, Required: r.RequiredQuestId.ToLowerInvariant()))
                .Where(g => g.Count() > 1)
                .Select(g => $"{g.Key.QuestId} <- {g.First().RequiredQuestId} "
                             + $"(groups {string.Join(", ", g.Select(r => r.GroupId).OrderBy(id => id))})")
                .ToList();
            if (shadowed.Count > 0)
            {
                yield return
                    $"{shadowed.Count} quest/prerequisite pairs have more than one row: "
                    + string.Join(", ", shadowed.Take(10))
                    + ". The fielded build keeps the first row for a pair and drops the rest, so the others "
                    + "would not be published in any readable sense.";
            }

            // A quest that requires itself can never be unlocked, and nothing downstream checks
            // for one. The refresh drops such a row wherever it builds one, from the game's
            // records and from the wiki's |previous field alike; this is what says the same
            // thing about a row that reached the file some other way, a hand edit above all.
            var selfReferencing = candidate.Requirements
                .Where(r => string.Equals(r.QuestId, r.RequiredQuestId, StringComparison.Ordinal))
                .Select(r => r.QuestId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (selfReferencing.Count > 0)
            {
                yield return
                    $"{selfReferencing.Count} quests are their own prerequisite: "
                    + string.Join(", ", selfReferencing.Take(10))
                    + ". No build can ever unlock a quest that requires itself.";
            }
        }

        private static IEnumerable<string> QuestProblems(Candidate candidate)
        {
            var badFactions = candidate.Quests
                .Where(q => q.Faction != null
                            && !ReadableFactions.Contains(q.Faction, StringComparer.Ordinal))
                .Select(q => $"{q.Name} ({q.Faction})")
                .ToList();
            if (badFactions.Count > 0)
            {
                yield return
                    $"Faction outside {{NULL, Bear, Usec}}: {string.Join(", ", badFactions.Take(10))}. "
                    + "The fielded build compares the string for equality, so any other value hides the quest.";
            }

            var missingTrader = candidate.Quests.Where(q => string.IsNullOrEmpty(q.Trader)).ToList();
            if (candidate.Quests.Count > 0)
            {
                var share = (double)missingTrader.Count / candidate.Quests.Count;
                if (share > MaxTradersMissing)
                {
                    yield return
                        $"{missingTrader.Count} of {candidate.Quests.Count} quests ({share:P0}) have no Trader, over the "
                        + $"{MaxTradersMissing:P0} limit: "
                        + string.Join(", ", missingTrader.Take(10).Select(q => q.Name));
                }
            }

            // Only where the column exists: a candidate published before it has no stored value
            // on any row by definition, and the app reads those rows off the name instead.
            if (candidate.StoresNormalizedNames)
            {
                var blankNormalized = candidate.Quests
                    .Where(q => string.IsNullOrEmpty(q.NormalizedName))
                    .ToList();
                if (blankNormalized.Count > 0)
                {
                    yield return "NormalizedName is empty on: "
                        + string.Join(", ", blankNormalized.Take(10).Select(q => q.Name));
                }
            }

            // The rule a hand edit breaks: Quests.Name can be rewritten on its own, and the row
            // key stays what the quest was first imported as, so the two disagree the moment
            // anything writes one without the other. Read off the effective value, which is what
            // the app will actually key progress by.
            var driftedNormalized = candidate.Quests
                .Where(q =>
                {
                    var mintedTitle = WikiQuestIdentity.TitleOf(q.Id);
                    return mintedTitle == null
                           || QuestNormalizedName.SqlForm(mintedTitle) != EffectiveNormalizedName(q);
                })
                .ToList();
            if (driftedNormalized.Count > 0)
            {
                yield return
                    "NormalizedName does not match the value the app computes from the row key on: "
                    + string.Join(", ", driftedNormalized.Take(10)
                        .Select(q => $"{q.Name} ({EffectiveNormalizedName(q)})"))
                    + ". Progress recorded against these quests would not be found.";
            }

            foreach (var problem in DuplicateIdentityProblems(
                candidate.Quests, q => q.Id, EffectiveNormalizedName, q => q.Name))
            {
                yield return problem;
            }
        }

        /// <summary>
        /// The two identity collisions, over whichever shape of quest the caller holds. Both are
        /// unpublishable: the row key is a primary key, and two rows sharing a normalized name
        /// make the progress recorded under it ambiguous.
        /// </summary>
        public static IEnumerable<string> DuplicateIdentityProblems<T>(
            IEnumerable<T> quests,
            Func<T, string> idOf,
            Func<T, string> normalizedNameOf,
            Func<T, string> nameOf)
        {
            var all = quests.ToList();

            foreach (var duplicate in all.GroupBy(idOf, StringComparer.Ordinal).Where(g => g.Count() > 1))
                yield return $"Two quests share the row key {duplicate.Key}: {string.Join(", ", duplicate.Select(nameOf))}";

            foreach (var duplicate in all
                .GroupBy(normalizedNameOf, StringComparer.Ordinal)
                .Where(g => g.Key.Length > 0 && g.Count() > 1))
            {
                yield return
                    $"Two quests share the normalized name '{duplicate.Key}': "
                    + string.Join(", ", duplicate.Select(nameOf));
            }
        }

        /// <summary>
        /// One refusal message out of a set of problems: the lead sentence, then a line per
        /// problem. Shared so a refusal reads the same wherever it is raised.
        /// </summary>
        public static string Describe(string lead, IReadOnlyList<string> problems) =>
            lead + ":\n  - " + string.Join("\n  - ", problems);

        /// <summary>
        /// What holding is worth saying when nothing is wrong: the rules that were actually
        /// evaluated, so a run cannot read as fully checked when a column the candidate lacks
        /// took two of them out.
        /// </summary>
        public static string DescribeHeld(Candidate candidate) =>
            "Publish constraints hold (requirement types, one row per quest/prerequisite pair, no quest its own "
            + "prerequisite, factions, traders, normalized names"
            + (candidate.StoresNormalizedNames
                ? ""
                : " derived from the names, this candidate storing none")
            + ")";
    }
}
