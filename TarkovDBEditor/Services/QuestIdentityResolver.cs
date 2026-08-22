using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TarkovDBEditor.Services
{
    /// <summary>
    /// Decides, for one refresh, which wiki pages become quests, which game record each one
    /// is, and what row key it keeps.
    /// <para>
    /// Three problems meet here. The wiki category over-includes (47 Arena pages, pages kept
    /// for removed quests) and the API under-prunes (35 removed tasks), so a quest ships only
    /// when both sources have it, with one exception for the pages the wiki itself marks as
    /// seasonal. Ten wiki titles are the <c>wikiLink</c> of two or three tasks, so a page has
    /// to pick one by evidence rather than by luck. And patch 1.1 renamed 91 published quests
    /// and gave eight titles to a different quest than before, so identity has to follow the
    /// game's own id rather than the page address, or recorded progress detaches from a third
    /// of the quests a player is most likely to have finished, and lands on the wrong quest
    /// wherever a title was reused.
    /// </para>
    /// <para>
    /// Pure by design: everything it needs is passed in, so the whole decision is unit-tested
    /// against fixtures rather than against whatever upstream happens to serve today. See
    /// docs/decisions/feature-quest-data-1-1-refresh.spec.md, "Matching, identity carry-over
    /// and liveness".
    /// </para>
    /// </summary>
    public static class QuestIdentityResolver
    {
        /// <summary>
        /// Resolves the imported quest set. <paramref name="previousRows"/> are the rows of the
        /// database the refresh started from, read before the write transaction opens.
        /// </summary>
        public static QuestIdentityResolution Resolve(
            IReadOnlyList<WikiQuestPage> pages,
            IReadOnlyList<TarkovDevQuestCacheItem> tasks,
            IReadOnlyList<PreviousQuestRow> previousRows,
            IReadOnlyList<QuestMatchOverride>? overrides = null)
        {
            ArgumentNullException.ThrowIfNull(pages);
            ArgumentNullException.ThrowIfNull(tasks);
            ArgumentNullException.ThrowIfNull(previousRows);

            var resolution = new QuestIdentityResolution();
            var index = new TaskIndex(tasks);
            var previous = new PreviousRowIndex(previousRows);
            var overrideByTitle = BuildOverrideIndex(overrides ?? Array.Empty<QuestMatchOverride>());

            // Ordered so a run is reproducible: two pages competing for the same task must not
            // resolve differently because a crawl returned them in a different order.
            var ordered = pages.OrderBy(p => p.Title, StringComparer.Ordinal).ToList();
            var matches = new Dictionary<string, TarkovDevQuestCacheItem>(StringComparer.Ordinal);
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Pass 1: the page URL against wikiLink. The strongest evidence, so it claims first.
            foreach (var page in ordered)
            {
                var link = TarkovDevJsonClient.NormalizeWikiLink(WikiQuestIdentity.PageLinkFor(page.Title));
                Claim(page, index.ByWikiLink(link), MatchMethod.WikiLink);
            }

            // Pass 2: the page title normalized to the API's slug, for pages whose link differs.
            foreach (var page in ordered.Where(p => !matches.ContainsKey(p.Title)))
            {
                Claim(page, index.ByNormalizedName(NormalizeQuestName(page.Title)), MatchMethod.NormalizedName);
            }

            // Pass 3: the committed alias list, for API records that point at a page that does
            // not exist (today: the three prestige tasks linking to the German title Neuanfang).
            foreach (var page in ordered.Where(p => !matches.ContainsKey(p.Title)))
            {
                if (!overrideByTitle.TryGetValue(page.Title, out var entry))
                    continue;

                var task = index.ById(entry.TaskId);
                if (task == null || claimed.Contains(task.Id))
                    continue;

                Claim(page, new[] { task }, MatchMethod.Alias);
            }

            void Claim(WikiQuestPage page, IReadOnlyList<TarkovDevQuestCacheItem> candidates, MatchMethod method)
            {
                var available = candidates.Where(t => !claimed.Contains(t.Id)).ToList();
                if (available.Count == 0)
                    return;

                var (chosen, rule) = ChooseAmong(available, index, previous);
                matches[page.Title] = chosen;
                claimed.Add(chosen.Id);

                if (available.Count > 1)
                {
                    resolution.Collisions.Add(new PageCollision
                    {
                        Title = page.Title,
                        CandidateTaskIds = available.Select(t => t.Id).ToList(),
                        ChosenTaskId = chosen.Id,
                        Rule = rule,
                    });
                }

                if (method == MatchMethod.Alias)
                    resolution.AliasesUsed.Add(page.Title);
            }

            // Liveness, then identity.
            var carriedRows = CarryIdentities(ordered, matches, index, previous);

            foreach (var page in ordered)
            {
                matches.TryGetValue(page.Title, out var task);

                if (task == null && !page.IsSeasonal)
                {
                    resolution.HeldBackPages.Add(new HeldBackPage
                    {
                        Title = page.Title,
                        Reason = "no game record in the tarkov.dev task set and no seasonal requirement line",
                    });
                    continue;
                }

                carriedRows.TryGetValue(page.Title, out var carried);
                var factionPairShared = task != null && resolution.Collisions
                    .Any(c => c.ChosenTaskId == task.Id && c.Rule == CollisionRule.FactionPair);

                resolution.Quests.Add(new ResolvedQuest
                {
                    Title = page.Title,
                    WikiPageLink = WikiQuestIdentity.PageLinkFor(page.Title),
                    Id = carried?.Id ?? WikiQuestIdentity.IdFor(page.Title),
                    // A previous database without the NormalizedName column (every publish
                    // before this one) still pins the value both builds computed for that row
                    // themselves, which is what their stored progress is keyed by.
                    NormalizedName = carried == null
                        ? QuestNormalizedName.SqlForm(page.Title)
                        : carried.NormalizedName ?? QuestNormalizedName.SqlForm(carried.Name),
                    Task = task,
                    FactionPairShared = factionPairShared,
                    PreviousName = carried?.Name,
                });
            }

            AssertRowKeysAreUnique(resolution.Quests);

            // Every previous row no imported quest kept the key of: exactly the rows the write
            // deletes, and the rows whose recorded progress is orphaned in every install. A row
            // with no external ID is here because nothing in a run can tie its new title to it -
            // the eighteen seasonal quests are the live example - so the run names them rather
            // than leaving the loss to a count nobody reads.
            var keptRowKeys = new HashSet<string>(resolution.Quests.Select(q => q.Id), StringComparer.Ordinal);
            foreach (var row in previousRows.Where(r => !keptRowKeys.Contains(r.Id)))
                resolution.UncarriedPreviousRows.Add(row);

            var importedTitles = new HashSet<string>(resolution.Quests.Select(q => q.Title), StringComparer.Ordinal);
            foreach (var quest in resolution.Quests.Where(q => q.PreviousName != null && q.PreviousName != q.Title))
            {
                resolution.Renames.Add(new QuestRename
                {
                    PreviousName = quest.PreviousName!,
                    Title = quest.Title,
                    BsgId = quest.Task?.Id,
                    Id = quest.Id,
                    // The dangerous kind: the old title is now another quest's page, so keying
                    // by page would have moved this quest's progress onto that other quest.
                    TitleReused = importedTitles.Contains(quest.PreviousName!),
                });
            }

            foreach (var task in tasks.Where(t => !claimed.Contains(t.Id)))
            {
                resolution.TasksWithoutPage.Add(new TaskWithoutPage
                {
                    TaskId = task.Id,
                    NormalizedName = task.NormalizedName,
                    WikiLink = task.WikiLink,
                    NameEN = task.NameEN,
                });
            }

            // An alias whose page now matches on its own is upstream's fix landing; the report
            // names it so the entry leaves the list instead of quietly outliving its reason.
            foreach (var entry in overrideByTitle.Values.Where(e => !resolution.AliasesUsed.Contains(e.PageTitle)))
                resolution.UnusedAliases.Add(entry);

            return resolution;
        }

        private enum MatchMethod
        {
            WikiLink,
            NormalizedName,
            Alias,
        }

        /// <summary>
        /// Decides which previous row, if any, each imported page keeps the key and normalized
        /// name of, in two passes so that no row is carried onto two pages.
        /// <para>
        /// Pass one is the external game id, the strong evidence, and it claims first. Pass two
        /// is for a page the task set has no record for at all: a seasonal quest, which the API
        /// carries in no game mode while its season is off. Such a page has no id to match on,
        /// but the previous database does hold the row published under exactly its title, and
        /// that row is the one the player's progress is filed against. Without pass two the
        /// seasonal quests mint a fresh key from the current title on every run - the pre-1.1
        /// behaviour this whole resolver exists to remove - and their rows are deleted and
        /// re-inserted, taking the recorded progress with them.
        /// </para>
        /// <para>
        /// Two guards keep pass two from guessing. A row already carried by its game id is not
        /// available, or two quests would land on one primary key. And a row whose game record
        /// still exists somewhere in this task set is left alone even when nothing claimed it:
        /// its quest is still in the game, so a seasonal page wearing the same title is more
        /// likely one of the eight titles patch 1.1 handed to a different quest than the same
        /// quest, and attaching the row would move a completion onto the wrong quest.
        /// </para>
        /// <para>
        /// A page whose title changed while it had no game record cannot be carried at all:
        /// nothing in the run ties the new title to the old row. It mints a fresh key, and the
        /// abandoned row shows up in the diff report as a removed quest.
        /// </para>
        /// </summary>
        private static Dictionary<string, PreviousQuestRow> CarryIdentities(
            IReadOnlyList<WikiQuestPage> ordered,
            IReadOnlyDictionary<string, TarkovDevQuestCacheItem> matches,
            TaskIndex index,
            PreviousRowIndex previous)
        {
            var carried = new Dictionary<string, PreviousQuestRow>(StringComparer.Ordinal);
            var claimedRowIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var page in ordered)
            {
                if (!matches.TryGetValue(page.Title, out var task))
                    continue;

                var row = previous.ByBsgId(task.Id);
                if (row != null && claimedRowIds.Add(row.Id))
                    carried[page.Title] = row;
            }

            foreach (var page in ordered)
            {
                // Only pages that are imported without a game record, which is only the pages
                // the wiki marks seasonal; everything else with no record is held back.
                if (matches.ContainsKey(page.Title) || !page.IsSeasonal)
                    continue;

                var row = previous.ByExactName(page.Title);
                if (row == null)
                    continue;

                if (!string.IsNullOrEmpty(row.BsgId) && index.ById(row.BsgId!) != null)
                    continue;

                if (claimedRowIds.Add(row.Id))
                    carried[page.Title] = row;
            }

            return carried;
        }

        /// <summary>
        /// Refuses a resolve in which two quests would be published under one row key.
        /// <para>
        /// The shape that produces one is a title changing owner: the quest that used to hold
        /// the title keeps the key minted from it, while the quest that took the title has no
        /// previous row to carry and mints that very key for itself. Patch 1.1 handed eight
        /// titles to a different quest, so this is a live shape, not a hypothetical. Downstream
        /// it is <c>Quests.Id</c>, a primary key: the second row either overwrites the first or
        /// fails the insert with a constraint error halfway through the write.
        /// </para>
        /// <para>
        /// There is no second key to hand out. The key has to decode back to the title the
        /// normalized name was computed from, or the publish guard rejects the row and the
        /// fielded builds cannot find the recorded progress. So the refresh stops here, with
        /// both quests named, rather than publishing one of them over the other.
        /// </para>
        /// </summary>
        private static void AssertRowKeysAreUnique(IReadOnlyList<ResolvedQuest> quests)
        {
            var byRowKey = new Dictionary<string, ResolvedQuest>(StringComparer.Ordinal);
            foreach (var quest in quests)
            {
                if (byRowKey.TryGetValue(quest.Id, out var other))
                {
                    throw new InvalidOperationException(
                        $"'{other.Title}' and '{quest.Title}' would both be published under the row key minted from "
                        + $"'{WikiQuestIdentity.TitleOf(quest.Id)}', so one would overwrite the other and leave every "
                        + "install. There is no second key to hand out, so this needs a decision before the refresh "
                        + "can run: either the two pages matched the wrong game records (the report's collisions and "
                        + "aliases say which), or the title genuinely changed hands and the quest that took it has to "
                        + "be given a row key of its own in the database the refresh starts from.");
                }

                byRowKey[quest.Id] = quest;
            }
        }

        /// <summary>
        /// A page claims one of several tasks by a fixed order of evidence, so the choice is
        /// reproducible and reviewable rather than incidental. A plain "lowest id" rule, the
        /// first draft, would have given The Tarkov Shooter - Part 5 the dead record and
        /// dropped the prerequisite Part 6 actually has.
        /// </summary>
        private static (TarkovDevQuestCacheItem Task, CollisionRule Rule) ChooseAmong(
            IReadOnlyList<TarkovDevQuestCacheItem> candidates,
            TaskIndex index,
            PreviousRowIndex previous)
        {
            if (candidates.Count == 1)
                return (candidates[0], CollisionRule.Single);

            // 1. A BEAR/USEC pair behind one page: the page serves both factions, as the four
            //    published rows for Drip-Out and Textile do today. Recognised whenever the
            //    candidates hold both sides, not only when they are the entire set: a stale
            //    third record must not turn a shared page into a one-faction quest, because the
            //    row would then publish a Faction the other faction's players are filtered by
            //    and the quest would silently leave half the installs.
            var factionSide = FactionPairChoice(candidates, index);
            if (factionSide != null)
                return (factionSide, CollisionRule.FactionPair);

            // 2. The record the rest of the game data believes in: some other task requires it.
            var requiredByAnother = candidates.Where(c => index.IsRequiredByAnotherTask(c.Id)).ToList();
            if (requiredByAnother.Count > 0)
                return (LowestId(requiredByAnother), CollisionRule.RequiredByAnotherTask);

            // 3. The record a previous row already holds: the one the user's recorded
            //    progress and log events have been matching. Found by the game id, never by
            //    the page title, because the title is what a patch rotates - a title lookup
            //    misses every renamed page, and answers with the old owner's row wherever 1.1
            //    handed a title to a different quest. Several held candidates are still better
            //    evidence than none, so the newest of them wins, the tie-break step 4 uses.
            var previouslyHeld = candidates.Where(c => previous.ByBsgId(c.Id) != null).ToList();
            if (previouslyHeld.Count > 0)
                return (NewestId(previouslyHeld), CollisionRule.PreviousRow);

            // 4. Nothing to go on: the record the game created most recently.
            return (NewestId(candidates), CollisionRule.NewestId);
        }

        /// <summary>
        /// The record to take when one page stands for a BEAR record and a USEC record, or null
        /// when the candidates are not such a pair.
        /// <para>
        /// The side is always BEAR, and that is the point. Each page of a chain is decided on its
        /// own, so a rule that picked whichever id sorted first could take BEAR for Part 1 and
        /// USEC for Part 2 - and Part 2's prerequisite would then name the BEAR Part 1 record
        /// this refresh never imported. <c>BuildRequirements</c> drops a prerequisite it cannot
        /// resolve without a word, so the app would offer Part 2 to a player who has not started
        /// Part 1. Pinning the side keeps both halves on the same faction's records. BEAR is what
        /// all four published faction pages (Drip-Out and Textile, Parts 1 and 2) resolve to
        /// today, so pinning it moves nothing that ships.
        /// </para>
        /// </summary>
        private static TarkovDevQuestCacheItem? FactionPairChoice(
            IReadOnlyList<TarkovDevQuestCacheItem> candidates,
            TaskIndex index)
        {
            var bear = candidates.Where(c => IsFaction(c, "BEAR")).ToList();
            var usec = candidates.Where(c => IsFaction(c, "USEC")).ToList();
            if (bear.Count == 0 || usec.Count == 0)
                return null;

            return PreferLive(bear, index);
        }

        private static bool IsFaction(TarkovDevQuestCacheItem task, string faction) =>
            string.Equals(task.FactionName, faction, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Among records of one faction, the one the rest of the game data believes in: a record
        /// another task requires beats one nothing requires, so a dead duplicate of the pinned
        /// side never takes the page. Lowest id decides what is left, so the result does not
        /// depend on the order upstream served the records in.
        /// </summary>
        private static TarkovDevQuestCacheItem PreferLive(
            IReadOnlyList<TarkovDevQuestCacheItem> candidates,
            TaskIndex index)
        {
            var required = candidates.Where(c => index.IsRequiredByAnotherTask(c.Id)).ToList();
            return LowestId(required.Count > 0 ? required : candidates);
        }

        private static TarkovDevQuestCacheItem LowestId(IReadOnlyList<TarkovDevQuestCacheItem> candidates) =>
            candidates.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).First();

        private static TarkovDevQuestCacheItem NewestId(IReadOnlyList<TarkovDevQuestCacheItem> candidates) =>
            candidates
                .OrderByDescending(CreationTimeOf)
                .ThenByDescending(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .First();

        /// <summary>
        /// The first eight hex digits of a 24-hex game id are the record's creation time as a
        /// Unix timestamp. Ids that are not in that shape sort oldest, so they never win the
        /// tie-break by accident.
        /// </summary>
        internal static long CreationTimeOf(TarkovDevQuestCacheItem task)
        {
            if (task.Id.Length < 8)
                return long.MinValue;

            return long.TryParse(task.Id.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var seconds)
                ? seconds
                : long.MinValue;
        }

        /// <summary>
        /// Converts a wiki page title to tarkov.dev's <c>normalizedName</c> spelling: lower
        /// case, spaces to dashes, everything but ASCII letters, digits and dashes dropped, and
        /// the wiki's disambiguating "(quest)" suffix removed.
        /// </summary>
        public static string NormalizeQuestName(string questName)
        {
            ArgumentNullException.ThrowIfNull(questName);

            var normalized = questName.ToLowerInvariant();

            if (normalized.EndsWith(" (quest)", StringComparison.Ordinal))
                normalized = normalized[..^" (quest)".Length];

            normalized = normalized.Replace(" ", "-");
            normalized = Regex.Replace(normalized, @"[^a-z0-9\-]", "");
            normalized = Regex.Replace(normalized, @"-+", "-");
            return normalized.Trim('-');
        }

        private static Dictionary<string, QuestMatchOverride> BuildOverrideIndex(IReadOnlyList<QuestMatchOverride> overrides)
        {
            var index = new Dictionary<string, QuestMatchOverride>(StringComparer.Ordinal);
            foreach (var entry in overrides)
            {
                if (!index.TryAdd(entry.PageTitle, entry))
                {
                    throw new InvalidOperationException(
                        $"quest-match-overrides.json lists '{entry.PageTitle}' twice; one page can name only one task.");
                }
            }

            return index;
        }

        /// <summary>Lookups over the task set, built once per resolve.</summary>
        private sealed class TaskIndex
        {
            private readonly Dictionary<string, TarkovDevQuestCacheItem> _byId;
            private readonly Dictionary<string, List<TarkovDevQuestCacheItem>> _byWikiLink = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<TarkovDevQuestCacheItem>> _byNormalizedName = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _requiredByAnother = new(StringComparer.OrdinalIgnoreCase);

            public TaskIndex(IReadOnlyList<TarkovDevQuestCacheItem> tasks)
            {
                _byId = new Dictionary<string, TarkovDevQuestCacheItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var task in tasks)
                {
                    // A duplicate id is upstream serving the same record twice; the first wins
                    // and the second is unreachable, which the report shows as a task with no page.
                    _byId.TryAdd(task.Id, task);

                    if (!string.IsNullOrEmpty(task.WikiLink))
                        Add(_byWikiLink, TarkovDevJsonClient.NormalizeWikiLink(task.WikiLink), task);

                    if (!string.IsNullOrEmpty(task.NormalizedName))
                        Add(_byNormalizedName, task.NormalizedName!, task);

                    foreach (var requirement in task.TaskRequirements)
                    {
                        if (!string.IsNullOrEmpty(requirement.TaskId))
                            _requiredByAnother.Add(requirement.TaskId);
                    }
                }
            }

            private static void Add(Dictionary<string, List<TarkovDevQuestCacheItem>> index, string key, TarkovDevQuestCacheItem task)
            {
                if (!index.TryGetValue(key, out var list))
                    index[key] = list = new List<TarkovDevQuestCacheItem>();
                list.Add(task);
            }

            public TarkovDevQuestCacheItem? ById(string id) =>
                _byId.TryGetValue(id, out var task) ? task : null;

            public IReadOnlyList<TarkovDevQuestCacheItem> ByWikiLink(string link) =>
                _byWikiLink.TryGetValue(link, out var list) ? list : Array.Empty<TarkovDevQuestCacheItem>();

            public IReadOnlyList<TarkovDevQuestCacheItem> ByNormalizedName(string normalizedName) =>
                _byNormalizedName.TryGetValue(normalizedName, out var list) ? list : Array.Empty<TarkovDevQuestCacheItem>();

            public bool IsRequiredByAnotherTask(string taskId) => _requiredByAnother.Contains(taskId);
        }

        /// <summary>Lookups over the previous database's rows.</summary>
        private sealed class PreviousRowIndex
        {
            private readonly Dictionary<string, PreviousQuestRow> _byBsgId = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Row names only, without the titles keys were minted from; see <see cref="ByExactName"/>.</summary>
            private readonly Dictionary<string, PreviousQuestRow> _byRowName = new(StringComparer.Ordinal);

            public PreviousRowIndex(IReadOnlyList<PreviousQuestRow> rows)
            {
                foreach (var row in rows)
                {
                    if (!string.IsNullOrEmpty(row.BsgId))
                        _byBsgId.TryAdd(row.BsgId!, row);

                    _byRowName.TryAdd(row.Name, row);
                }
            }

            public PreviousQuestRow? ByBsgId(string bsgId) =>
                _byBsgId.TryGetValue(bsgId, out var row) ? row : null;

            /// <summary>
            /// The row published under exactly this name, for the seasonal pages that have no
            /// game id to match on. It is the only name lookup the resolver has, and it answers
            /// for the row's own name only, never for the title a row's key was minted under: a
            /// page bearing the old title of a row that has since been renamed is the
            /// title-reuse case, where carrying would move one quest's recorded progress onto
            /// another.
            /// </summary>
            public PreviousQuestRow? ByExactName(string name) =>
                _byRowName.TryGetValue(name, out var row) ? row : null;
        }
    }

    /// <summary>Carries item row keys across a wiki page rename, so an item keeps its icon file.</summary>
    public static class ItemIdentityResolver
    {
        /// <summary>
        /// Returns, per wiki item id, the row key it should keep. An item whose page matches a
        /// tarkov.dev item whose external id the previous database already holds keeps that
        /// row's key; everything else keeps the key the crawl minted from its page URL.
        /// <para>
        /// Icons are files named <c>{Items.Id}.png</c>, so an item that changes key silently
        /// loses its icon until the next release ships a copy under the new name.
        /// </para>
        /// </summary>
        public static ItemIdentityResolution Resolve(
            IReadOnlyList<WikiItemIdentity> wikiItems,
            IReadOnlyDictionary<string, TarkovDevMultiLangItem> devItemsByWikiLink,
            IReadOnlyList<PreviousItemRow> previousRows)
        {
            ArgumentNullException.ThrowIfNull(wikiItems);
            ArgumentNullException.ThrowIfNull(devItemsByWikiLink);
            ArgumentNullException.ThrowIfNull(previousRows);

            var previousByBsgId = new Dictionary<string, PreviousItemRow>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in previousRows)
            {
                if (!string.IsNullOrEmpty(row.BsgId))
                    previousByBsgId.TryAdd(row.BsgId!, row);
            }

            var resolution = new ItemIdentityResolution();
            var claimedPreviousIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in wikiItems)
            {
                var link = TarkovDevJsonClient.NormalizeWikiLink(item.WikiPageLink);
                if (string.IsNullOrEmpty(link) || !devItemsByWikiLink.TryGetValue(link, out var dev))
                    continue;

                if (!previousByBsgId.TryGetValue(dev.BsgId, out var previousRow))
                    continue;

                // One previous row can only be carried onto one page; a second claim would
                // collapse two items into one primary key.
                if (previousRow.Id == item.Id || !claimedPreviousIds.Add(previousRow.Id))
                    continue;

                resolution.CarriedIds[item.Id] = previousRow.Id;
                resolution.Renames.Add(new ItemRename
                {
                    PreviousName = previousRow.Name,
                    Name = item.Name,
                    BsgId = dev.BsgId,
                    Id = previousRow.Id,
                });
            }

            return resolution;
        }
    }

    #region Resolver inputs and outputs

    /// <summary>A quest page from the wiki crawl.</summary>
    public sealed class WikiQuestPage
    {
        public required string Title { get; init; }

        /// <summary>
        /// True when the page's Requirements section names a seasonal mode. Such a page is
        /// imported on the wiki's word alone, because the JSON API carries no record for the
        /// current season's quests in any game mode.
        /// </summary>
        public bool IsSeasonal { get; init; }
    }

    /// <summary>A quest row in the database the refresh started from.</summary>
    public sealed class PreviousQuestRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }

        /// <summary>Null on every database published before this refresh; the column is new.</summary>
        public string? NormalizedName { get; init; }

        public string? BsgId { get; init; }
    }

    /// <summary>An item row in the database the refresh started from.</summary>
    public sealed class PreviousItemRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? BsgId { get; init; }
    }

    /// <summary>An item as the wiki crawl produced it, before identity carry-over.</summary>
    public sealed class WikiItemIdentity
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string WikiPageLink { get; init; }
    }

    /// <summary>One imported quest: which page, which game record, and which row key.</summary>
    public sealed class ResolvedQuest
    {
        public required string Title { get; init; }
        public required string Id { get; init; }
        public required string NormalizedName { get; init; }
        public required string WikiPageLink { get; init; }

        /// <summary>Null for a page imported on the wiki's seasonal marker alone.</summary>
        public TarkovDevQuestCacheItem? Task { get; init; }

        /// <summary>
        /// True when two faction variants share this page, so the row must stay faction
        /// neutral rather than take the chosen record's side.
        /// </summary>
        public bool FactionPairShared { get; init; }

        /// <summary>The name the carried row had, or null when the row key is newly minted.</summary>
        public string? PreviousName { get; init; }

        public bool IsWikiOnly => Task == null;
        public bool IdentityCarried => PreviousName != null;
    }

    public enum CollisionRule
    {
        /// <summary>Only one candidate; recorded for completeness, never reported as a collision.</summary>
        Single,
        FactionPair,
        RequiredByAnotherTask,
        PreviousRow,
        NewestId,
    }

    public sealed class PageCollision
    {
        public required string Title { get; init; }
        public required List<string> CandidateTaskIds { get; init; }
        public required string ChosenTaskId { get; init; }
        public required CollisionRule Rule { get; init; }
    }

    public sealed class HeldBackPage
    {
        public required string Title { get; init; }
        public required string Reason { get; init; }
    }

    public sealed class TaskWithoutPage
    {
        public required string TaskId { get; init; }
        public string? NormalizedName { get; init; }
        public string? WikiLink { get; init; }
        public string? NameEN { get; init; }
    }

    public sealed class QuestRename
    {
        public required string PreviousName { get; init; }
        public required string Title { get; init; }

        /// <summary>
        /// The external game id the rename was recognised by, or null for a quest that carried
        /// its row without one (a seasonal page, matched to its previous row by title).
        /// </summary>
        public required string? BsgId { get; init; }

        public required string Id { get; init; }

        /// <summary>True when another imported quest now carries the old title.</summary>
        public bool TitleReused { get; init; }
    }

    public sealed class ItemRename
    {
        public required string PreviousName { get; init; }
        public required string Name { get; init; }
        public required string BsgId { get; init; }
        public required string Id { get; init; }
    }

    /// <summary>Everything one resolve decided, including what it chose not to import.</summary>
    public sealed class QuestIdentityResolution
    {
        public List<ResolvedQuest> Quests { get; } = new();
        public List<HeldBackPage> HeldBackPages { get; } = new();
        public List<TaskWithoutPage> TasksWithoutPage { get; } = new();
        public List<PageCollision> Collisions { get; } = new();
        public List<QuestRename> Renames { get; } = new();

        /// <summary>
        /// The previous rows no imported quest kept the key of. These rows are deleted by the
        /// write, and the progress recorded against them in every install is orphaned.
        /// </summary>
        public List<PreviousQuestRow> UncarriedPreviousRows { get; } = new();

        public List<string> AliasesUsed { get; } = new();
        public List<QuestMatchOverride> UnusedAliases { get; } = new();

        /// <summary>
        /// The pages imported on the wiki's seasonal marker alone. Derived from
        /// <see cref="Quests"/> rather than maintained beside it, so the two cannot disagree.
        /// </summary>
        public IReadOnlyList<string> WikiOnlyPages =>
            Quests.Where(q => q.IsWikiOnly).Select(q => q.Title).ToList();

        public IEnumerable<QuestRename> TitleReuses => Renames.Where(r => r.TitleReused);
    }

    public sealed class ItemIdentityResolution
    {
        /// <summary>Crawl-minted item id to the previous row key it should keep instead.</summary>
        public Dictionary<string, string> CarriedIds { get; } = new(StringComparer.Ordinal);

        public List<ItemRename> Renames { get; } = new();
    }

    #endregion

    #region Alias list

    /// <summary>
    /// One entry of the committed alias list: a page the API points at a title that is not a
    /// wiki page, so no normalization can bridge it.
    /// </summary>
    public sealed class QuestMatchOverride
    {
        [JsonPropertyName("pageTitle")]
        public string PageTitle { get; set; } = "";

        [JsonPropertyName("taskId")]
        public string TaskId { get; set; } = "";

        /// <summary>The upstream report this entry waits on, so it can be retired deliberately.</summary>
        [JsonPropertyName("upstreamIssue")]
        public string UpstreamIssue { get; set; } = "";

        [JsonPropertyName("note")]
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// Loads <c>Resources/Data/quest-match-overrides.json</c>. The file has to be there and
    /// every entry is validated on load, so a missing or malformed list fails immediately and
    /// visibly instead of silently matching nothing halfway through a regeneration.
    /// </summary>
    public static class QuestMatchOverrides
    {
        public const string FileName = "quest-match-overrides.json";

        private static readonly Regex TaskIdPattern = new("^[0-9a-f]{24}$", RegexOptions.Compiled);

        public static string DefaultPath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Resources", "Data", FileName);

        /// <summary>
        /// Reads the list from <paramref name="path"/>, or from <see cref="DefaultPath"/>.
        /// <para>
        /// A missing file is a failure, not an empty list. The file is deployed beside the
        /// editor by the <c>Resources\Data\*.json</c> copy rule; if it stops arriving, every
        /// entry silently matches nothing, the pages it bridges are held back, and the handful
        /// of quests involved are too few to trip the lost-match guard. They would simply
        /// disappear from every install. An intentionally empty list is a different thing and
        /// still loads: it is a file with no entries, which is a curator's decision on record.
        /// </para>
        /// </summary>
        public static List<QuestMatchOverride> Load(string? path = null)
        {
            path ??= DefaultPath;
            if (!File.Exists(path))
            {
                throw new InvalidOperationException(
                    $"{path} is missing. The alias list bridges the pages whose game record links to a title that is "
                    + "not a wiki page; without it those quests match nothing and leave the published database "
                    + "without anything else looking wrong. Restore the file (it ships from "
                    + $"TarkovDBEditor\\Resources\\Data\\{FileName}) before refreshing.");
            }

            return Parse(File.ReadAllText(path), path);
        }

        /// <summary>Parses and validates the list; exposed so tests can pin the committed file's shape.</summary>
        public static List<QuestMatchOverride> Parse(string json, string source)
        {
            QuestMatchOverrideFile? file;
            try
            {
                file = JsonSerializer.Deserialize<QuestMatchOverrideFile>(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{source} is not valid JSON: {ex.Message}", ex);
            }

            var entries = file?.Overrides ?? new List<QuestMatchOverride>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.PageTitle))
                    throw new InvalidOperationException($"{source}: an entry has no pageTitle.");

                if (!TaskIdPattern.IsMatch(entry.TaskId))
                {
                    throw new InvalidOperationException(
                        $"{source}: '{entry.PageTitle}' names taskId '{entry.TaskId}', which is not a 24-character game id.");
                }

                if (string.IsNullOrWhiteSpace(entry.UpstreamIssue))
                {
                    throw new InvalidOperationException(
                        $"{source}: '{entry.PageTitle}' has no upstreamIssue. Every alias names the report it waits on "
                        + "so it can be retired when upstream fixes the link.");
                }

                if (!seen.Add(entry.PageTitle))
                    throw new InvalidOperationException($"{source} lists '{entry.PageTitle}' twice.");
            }

            return entries;
        }

        private sealed class QuestMatchOverrideFile
        {
            [JsonPropertyName("overrides")]
            public List<QuestMatchOverride>? Overrides { get; set; }
        }
    }

    #endregion
}
