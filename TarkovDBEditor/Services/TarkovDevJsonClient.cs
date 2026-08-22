using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TarkovDBEditor.Services
{
    /// <summary>
    /// Reads the game rules from json.tarkov.dev, the surface tarkov.dev's own front end runs
    /// on and the replacement its maintainers point at now that the GraphQL endpoint answers
    /// "GraphQL server unavailable" (down since about 2026-07-22).
    /// <para>
    /// Two shapes differ from the GraphQL responses this replaced. Data files are language
    /// neutral: every translatable string is a key such as <c>"&lt;taskId&gt; name"</c>, and
    /// the text lives in a sibling locale file (<c>tasks_en</c>, <c>tasks_ko</c>,
    /// <c>tasks_ja</c>). And collections arrive as objects keyed by id rather than arrays.
    /// </para>
    /// <para>
    /// The client is deliberately strict where the old transport was lenient: an empty task
    /// set, a record without an id, and an HTTP failure all throw. The path this closes is the
    /// one that produced the January regeneration: a 200 carrying an error body parsed to an
    /// empty set, which then overwrote the cache with <c>{}</c> and left every published quest
    /// without an external ID for seven months. The strictness is aimed at the whole set: a
    /// single odd record (a task with no <c>wikiLink</c>, an item whose wiki page another item
    /// already claimed) is carried or reported, never made to block a regeneration.
    /// </para>
    /// <para>
    /// Conditional requests are the caller's to complete: every fetch hands back a result whose
    /// <see cref="TarkovDevFetch{T}.CommitETags"/> the caller invokes once the value is on disk.
    /// Until then the client keeps asking unconditionally, so a cache file that never got
    /// written is never mistaken for one upstream has confirmed current.
    /// </para>
    /// See docs/decisions/feature-quest-data-1-1-refresh.spec.md, "json.tarkov.dev client".
    /// </summary>
    public sealed class TarkovDevJsonClient : IDisposable
    {
        public const string DefaultBaseUrl = "https://json.tarkov.dev/";

        /// <summary>
        /// The only game mode read. <c>pvp-season</c> is a strict subset of it and <c>pve</c>
        /// differs only in field values, so a single mode keeps one row per quest; see the
        /// spec's "No mode-specific data" non-goal.
        /// </summary>
        public const string GameMode = "regular";

        /// <summary>Locale used for <c>Name</c>, and the fallback for the other two.</summary>
        private const string EnglishLanguage = "en";

        private static readonly string[] LocalizedLanguages = { EnglishLanguage, "ko", "ja" };

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly string? _etagStorePath;
        private Dictionary<string, string> _etags = new(StringComparer.Ordinal);

        /// <param name="cacheDir">
        /// Where the ETag store lives, beside the cache files whose freshness it describes.
        /// Null disables conditional requests entirely, which is what the tests want.
        /// </param>
        /// <param name="baseUrl">Overridable so tests can point at a local server.</param>
        /// <param name="handler">
        /// Overridable so tests can answer from a captured fixture instead of the network.
        /// </param>
        public TarkovDevJsonClient(string? cacheDir = null, string? baseUrl = null, HttpMessageHandler? handler = null)
        {
            // A supplied handler is not disposed with the client: whoever passed it owns it.
            _httpClient = handler == null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            // The wiki and tarkov.dev both serve a Cloudflare challenge to curl's default
            // user agent; this one is served. Kept identical to the other editor clients.
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "TarkovDBEditor/1.0");
            _httpClient.Timeout = TimeSpan.FromMinutes(5);

            _baseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/') + "/";

            if (!string.IsNullOrEmpty(cacheDir))
            {
                _etagStorePath = Path.Combine(cacheDir, "tarkov_dev_etags.json");
                LoadETags();
            }
        }

        #region Endpoint paths

        public static string TasksPath => $"{GameMode}/tasks";
        public static string ItemsPath => $"{GameMode}/items";
        public static string TradersPath => $"{GameMode}/traders";
        public static string HideoutPath => $"{GameMode}/hideout";

        private static string LocalePath(string dataPath, string language) => $"{dataPath}_{language}";

        /// <summary>The data file and its three locale files, in the order the fetch reads them.</summary>
        private static IReadOnlyList<string> GroupFor(string dataPath) =>
            new[] { dataPath }.Concat(LocalizedLanguages.Select(l => LocalePath(dataPath, l))).ToArray();

        #endregion

        #region Fetches

        /// <summary>
        /// The 1.1 task set with every gate the app needs: minimum level, Kappa flag, faction,
        /// per-trader loyalty and the prerequisite list, plus Korean and Japanese names.
        /// Returns null when every file in the group answered 304, meaning the caller's
        /// existing cache is still current.
        /// <para>
        /// The caller must call <see cref="TarkovDevFetch{T}.CommitETags"/> once it has the
        /// result on disk; see that method for why the fetch cannot do it itself.
        /// </para>
        /// </summary>
        public async Task<TarkovDevFetch<List<TarkovDevQuestCacheItem>>?> FetchTasksAsync(
            bool conditional = true,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Invoke("Fetching tasks from json.tarkov.dev...");

            var group = await FetchGroupAsync(GroupFor(TasksPath), conditional, cancellationToken);
            if (group == null)
            {
                progress?.Invoke("Tasks unchanged upstream (304); keeping the cached copy.");
                return null;
            }

            var rawTasks = ReadCollection<JsonTasksData, JsonTask>(group, TasksPath, "tasks", d => d.Tasks);

            var locales = ReadLocales(group, TasksPath);
            var quests = new List<TarkovDevQuestCacheItem>(rawTasks.Count);
            var droppedGates = new DroppedTraderGates(TasksPath);

            // A task with no wikiLink is carried, not refused. It cannot be matched to a wiki
            // page by link, but QuestIdentityResolver still matches it by normalized name and
            // reports it as a game record with no page when nothing claims it. Refusing would
            // fail the whole part, and once the task cache went stale enough the refresh guard
            // would block every regeneration, on a data condition upstream can create at will.
            foreach (var (id, task) in rawTasks)
            {
                if (string.IsNullOrEmpty(task?.Id))
                    throw new InvalidOperationException($"{TasksPath}: task '{id}' has no id.");

                var nameEn = locales.Resolve(EnglishLanguage, task.Name) ?? "";

                quests.Add(new TarkovDevQuestCacheItem
                {
                    Id = task.Id,
                    NameEN = nameEn,
                    NormalizedName = task.NormalizedName,
                    NameKO = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ko", task.Name), nameEn),
                    NameJA = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ja", task.Name), nameEn),
                    Trader = task.Trader,
                    WikiLink = task.WikiLink,
                    MinPlayerLevel = task.MinPlayerLevel,
                    KappaRequired = task.KappaRequired,
                    FactionName = task.FactionName,
                    AvailableDelaySecondsMin = task.AvailableDelaySecondsMin,
                    TraderLevelRequirements = BuildTraderLevelRequirements(task.TraderRequirements, droppedGates),
                    TaskRequirements = BuildTaskRequirements(task.TaskRequirements),
                    FailConditions = BuildFailConditions(task.FailConditions),
                });
            }

            progress?.Invoke($"Fetched {quests.Count} tasks from json.tarkov.dev");
            droppedGates.Report(progress);
            return Fetched(quests, group);
        }

        /// <summary>
        /// The item catalogue, keyed by the normalized wiki page URL the item pipeline matches
        /// on. Items without a <c>wikiLink</c> cannot be matched to a wiki page and are skipped.
        /// </summary>
        public async Task<TarkovDevFetch<Dictionary<string, TarkovDevMultiLangItem>>?> FetchItemsAsync(
            bool conditional = true,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Invoke("Fetching items from json.tarkov.dev...");

            var group = await FetchGroupAsync(GroupFor(ItemsPath), conditional, cancellationToken);
            if (group == null)
            {
                progress?.Invoke("Items unchanged upstream (304); keeping the cached copy.");
                return null;
            }

            var rawItems = ReadCollection<JsonItemsData, JsonItem>(group, ItemsPath, "items", d => d.Items);

            var locales = ReadLocales(group, ItemsPath);
            var result = new Dictionary<string, TarkovDevMultiLangItem>(StringComparer.OrdinalIgnoreCase);
            var sharedPages = new List<string>();

            foreach (var (id, item) in rawItems)
            {
                if (string.IsNullOrEmpty(item?.Id))
                    throw new InvalidOperationException($"{ItemsPath}: item '{id}' has no id.");
                if (string.IsNullOrEmpty(item.WikiLink))
                    continue;

                var pageKey = NormalizeWikiLink(item.WikiLink);
                if (result.TryGetValue(pageKey, out var alreadyOnThePage))
                {
                    // Two items on one wiki page are a wiki defect the page-keyed item pipeline
                    // cannot resolve. The first entry keeps the page, so which item wins does
                    // not depend on how far down a 16 MB file the collision happens to sit, and
                    // the pair is reported rather than one silently overwriting the other.
                    sharedPages.Add($"{pageKey} (kept {alreadyOnThePage.BsgId}, dropped {item.Id})");
                    continue;
                }

                var nameEn = locales.Resolve(EnglishLanguage, item.Name) ?? "";
                var shortEn = locales.Resolve(EnglishLanguage, item.ShortName) ?? "";

                result[pageKey] = new TarkovDevMultiLangItem
                {
                    BsgId = item.Id,
                    WikiLink = item.WikiLink,
                    NameEN = nameEn,
                    ShortNameEN = shortEn,
                    NameKO = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ko", item.Name), nameEn),
                    ShortNameKO = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ko", item.ShortName), shortEn),
                    NameJA = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ja", item.Name), nameEn),
                    ShortNameJA = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ja", item.ShortName), shortEn),
                    NormalizedName = item.NormalizedName,
                    IconLink = item.IconLink,
                };
            }

            progress?.Invoke($"Fetched {result.Count} items from json.tarkov.dev");
            if (sharedPages.Count > 0)
            {
                progress?.Invoke(
                    $"{ItemsPath}: {sharedPages.Count} wiki page(s) claimed by more than one item: {Summarize(sharedPages)}");
            }

            return Fetched(result, group);
        }

        /// <summary>The trader list, including the sixteenth trader 1.1 added (Survivor).</summary>
        public async Task<TarkovDevFetch<List<TarkovDevTraderCacheItem>>?> FetchTradersAsync(
            bool conditional = true,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Invoke("Fetching traders from json.tarkov.dev...");

            var group = await FetchGroupAsync(GroupFor(TradersPath), conditional, cancellationToken);
            if (group == null)
            {
                progress?.Invoke("Traders unchanged upstream (304); keeping the cached copy.");
                return null;
            }

            var rawTraders = ReadCollection<Dictionary<string, JsonTrader>, JsonTrader>(
                group, TradersPath, "traders", d => d);

            var locales = ReadLocales(group, TradersPath);
            var traders = new List<TarkovDevTraderCacheItem>(rawTraders.Count);

            foreach (var (id, trader) in rawTraders)
            {
                if (string.IsNullOrEmpty(trader?.Id))
                    throw new InvalidOperationException($"{TradersPath}: trader '{id}' has no id.");

                var nameEn = locales.Resolve(EnglishLanguage, trader.Name) ?? "";
                traders.Add(new TarkovDevTraderCacheItem
                {
                    Id = trader.Id,
                    Name = nameEn,
                    NameKO = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ko", trader.Name), nameEn),
                    NameJA = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ja", trader.Name), nameEn),
                    NormalizedName = trader.NormalizedName,
                    ImageLink = trader.ImageLink,
                });
            }

            progress?.Invoke($"Fetched {traders.Count} traders from json.tarkov.dev");
            return Fetched(traders, group);
        }

        /// <summary>
        /// The hideout stations. The endpoint carries only ids for the items and traders a
        /// level requires, so both lookups are supplied by the caller (which has just fetched
        /// or loaded them) rather than fetched a second time here: the items file alone is
        /// 16 MB.
        /// </summary>
        public async Task<TarkovDevFetch<List<TarkovDevHideoutStation>>?> FetchHideoutAsync(
            IReadOnlyCollection<TarkovDevMultiLangItem> items,
            IReadOnlyCollection<TarkovDevTraderCacheItem> traders,
            bool conditional = true,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(traders);

            progress?.Invoke("Fetching hideout stations from json.tarkov.dev...");

            var group = await FetchGroupAsync(GroupFor(HideoutPath), conditional, cancellationToken);
            if (group == null)
            {
                progress?.Invoke("Hideout unchanged upstream (304); keeping the cached copy.");
                return null;
            }

            var rawStations = ReadCollection<Dictionary<string, JsonHideoutStation>, JsonHideoutStation>(
                group, HideoutPath, "stations", d => d);

            var locales = ReadLocales(group, HideoutPath);
            var itemsById = new Dictionary<string, TarkovDevMultiLangItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.BsgId))
                    itemsById[item.BsgId] = item;
            }

            var tradersById = new Dictionary<string, TarkovDevTraderCacheItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var trader in traders)
            {
                if (!string.IsNullOrEmpty(trader.Id))
                    tradersById[trader.Id] = trader;
            }

            // Identity first, before anything reads a station: the outer key is not required to
            // equal the record's own id, and a station served twice under one id would collapse
            // into a single row with whichever levels happened to be read last. Station names
            // are collected in the same pass and live under the station's own id, so a station
            // referenced only as another station's prerequisite still resolves to a name.
            var stationNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var stationKeysById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var rawStationList = new List<(string Id, JsonHideoutStation Station)>(rawStations.Count);
            foreach (var (key, station) in rawStations)
            {
                if (string.IsNullOrEmpty(station?.Id))
                    throw new InvalidOperationException($"{HideoutPath}: station '{key}' has no id.");

                if (!stationKeysById.TryAdd(station.Id, key))
                {
                    throw new InvalidOperationException(
                        $"{HideoutPath}: station id '{station.Id}' is served twice, under keys "
                        + $"'{stationKeysById[station.Id]}' and '{key}'.");
                }

                stationNames[station.Id] = station.Name;
                rawStationList.Add((station.Id, station));
            }

            var droppedGates = new DroppedTraderGates(HideoutPath);
            var stations = new List<TarkovDevHideoutStation>(rawStationList.Count);
            foreach (var (stationId, station) in rawStationList)
            {
                var stationNameEn = locales.Resolve(EnglishLanguage, station.Name) ?? "";
                var dbStation = new TarkovDevHideoutStation
                {
                    Id = stationId,
                    Name = stationNameEn,
                    NameKo = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ko", station.Name), stationNameEn),
                    NameJa = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ja", station.Name), stationNameEn),
                    NormalizedName = station.NormalizedName,
                    ImageLink = station.ImageLink,
                    Levels = new List<TarkovDevHideoutLevel>(),
                };

                foreach (var level in station.Levels ?? new List<JsonHideoutLevel>())
                {
                    var dbLevel = new TarkovDevHideoutLevel
                    {
                        Level = level.Level,
                        ConstructionTime = level.ConstructionTime,
                        ItemRequirements = new List<TarkovDevHideoutItemReq>(),
                        StationLevelRequirements = new List<TarkovDevHideoutStationReq>(),
                        TraderRequirements = new List<TarkovDevHideoutTraderReq>(),
                        SkillRequirements = new List<TarkovDevHideoutSkillReq>(),
                    };

                    foreach (var req in level.ItemRequirements ?? new List<JsonHideoutItemRequirement>())
                    {
                        if (string.IsNullOrEmpty(req.Item))
                            continue;

                        itemsById.TryGetValue(req.Item, out var item);
                        dbLevel.ItemRequirements.Add(new TarkovDevHideoutItemReq
                        {
                            ItemId = req.Item,
                            ItemName = item?.NameEN ?? "",
                            ItemNameKo = item?.NameKO,
                            ItemNameJa = item?.NameJA,
                            ItemNormalizedName = item?.NormalizedName,
                            IconLink = item?.IconLink,
                            Count = req.Count,
                            FoundInRaid = req.Attributes?.FoundInRaid == true,
                        });
                    }

                    foreach (var req in level.StationLevelRequirements ?? new List<JsonHideoutStationRequirement>())
                    {
                        if (string.IsNullOrEmpty(req.Station))
                            continue;

                        stationNames.TryGetValue(req.Station, out var requiredNameKey);
                        var requiredNameEn = locales.Resolve(EnglishLanguage, requiredNameKey) ?? "";
                        dbLevel.StationLevelRequirements.Add(new TarkovDevHideoutStationReq
                        {
                            StationId = req.Station,
                            StationName = requiredNameEn,
                            StationNameKo = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ko", requiredNameKey), requiredNameEn),
                            StationNameJa = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ja", requiredNameKey), requiredNameEn),
                            Level = req.Level,
                        });
                    }

                    foreach (var req in level.TraderRequirements ?? new List<JsonTraderRequirement>())
                    {
                        // The endpoint mixes loyalty and reputation gates in one list; the
                        // hideout schema only models loyalty levels. A gate it cannot hold is
                        // counted and reported: a dropped gate shows a station as buildable
                        // that the game refuses to build.
                        var loyaltyLevel = ReadLoyaltyLevel(req);
                        if (loyaltyLevel == null || string.IsNullOrEmpty(req.Trader))
                        {
                            droppedGates.Note(req);
                            continue;
                        }

                        tradersById.TryGetValue(req.Trader, out var trader);
                        dbLevel.TraderRequirements.Add(new TarkovDevHideoutTraderReq
                        {
                            TraderId = req.Trader,
                            TraderName = trader?.Name ?? "",
                            TraderNameKo = trader?.NameKO,
                            TraderNameJa = trader?.NameJA,
                            Level = loyaltyLevel.Value,
                        });
                    }

                    foreach (var req in level.SkillRequirements ?? new List<JsonHideoutSkillRequirement>())
                    {
                        if (string.IsNullOrEmpty(req.Skill))
                            continue;

                        var skillNameEn = locales.Resolve(EnglishLanguage, req.Skill) ?? req.Skill;
                        dbLevel.SkillRequirements.Add(new TarkovDevHideoutSkillReq
                        {
                            Name = skillNameEn,
                            NameKo = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ko", req.Skill), skillNameEn),
                            NameJa = TarkovDevDataService.ResolveLocalizedQuestName(locales.Resolve("ja", req.Skill), skillNameEn),
                            Level = req.Level,
                        });
                    }

                    dbStation.Levels.Add(dbLevel);
                }

                dbStation.Levels.Sort((a, b) => a.Level.CompareTo(b.Level));
                stations.Add(dbStation);
            }

            progress?.Invoke($"Fetched {stations.Count} hideout stations from json.tarkov.dev");
            droppedGates.Report(progress);
            return Fetched(stations, group);
        }

        #endregion

        #region Mapping helpers

        /// <summary>
        /// The loyalty level a trader requirement names, or null when the app's schema has no
        /// reading for it.
        /// <para>
        /// Only <c>level</c> entries are loyalty gates. The endpoint also carries
        /// <c>reputation</c> entries (12 tasks, Collector among them), which the schema has no
        /// column for. The app reads a stored level as "at least N", so <c>&gt;=</c> and
        /// <c>=</c> both map to N and <c>&gt;</c> maps to N + 1; a <c>&lt;</c> or <c>&lt;=</c>
        /// upper bound has no "at least" reading and is the one shape genuinely dropped.
        /// Dropping a gate the schema could have held would show a quest as available, or a
        /// station as buildable, that the game refuses.
        /// </para>
        /// </summary>
        private static int? ReadLoyaltyLevel(JsonTraderRequirement req)
        {
            if (!string.Equals(req.RequirementType, "level", StringComparison.OrdinalIgnoreCase))
                return null;

            var level = (int)Math.Round(req.Value);
            return (req.CompareMethod ?? "").Trim() switch
            {
                "" or ">=" or "=" => level,
                ">" => level + 1,
                _ => null,
            };
        }

        private static List<TarkovDevTaskTraderLevel> BuildTraderLevelRequirements(
            List<JsonTraderRequirement>? source,
            DroppedTraderGates dropped)
        {
            var result = new List<TarkovDevTaskTraderLevel>();
            if (source == null)
                return result;

            foreach (var req in source)
            {
                var level = ReadLoyaltyLevel(req);
                if (level == null || string.IsNullOrEmpty(req.Trader))
                {
                    dropped.Note(req);
                    continue;
                }

                result.Add(new TarkovDevTaskTraderLevel
                {
                    TraderId = req.Trader,
                    Level = level.Value,
                });
            }

            return result;
        }

        /// <summary>
        /// The trader requirements one fetch could not carry into the app's schema, kept so the
        /// fetch can report them instead of dropping them silently. Reputation gates are the
        /// known case the schema has no column for and are only counted; anything else is named,
        /// because an unreadable gate is either a schema shape nobody has modelled yet or an
        /// upstream change, and both want a human to look.
        /// </summary>
        private sealed class DroppedTraderGates
        {
            private readonly string _path;
            private readonly List<string> _unreadable = new();
            private int _reputation;

            public DroppedTraderGates(string path) => _path = path;

            public void Note(JsonTraderRequirement req)
            {
                if (string.Equals(req.RequirementType, "reputation", StringComparison.OrdinalIgnoreCase))
                {
                    _reputation++;
                    return;
                }

                _unreadable.Add(
                    $"{req.RequirementType ?? "(no type)"} {req.CompareMethod ?? "(no comparison)"} "
                    + $"{req.Value} with trader {(string.IsNullOrEmpty(req.Trader) ? "(none)" : req.Trader)}");
            }

            public void Report(Action<string>? progress)
            {
                if (progress == null)
                    return;

                if (_reputation > 0)
                    progress($"{_path}: dropped {_reputation} reputation gate(s); the schema holds loyalty levels only.");

                if (_unreadable.Count > 0)
                {
                    progress($"{_path}: dropped {_unreadable.Count} trader requirement(s) with no loyalty level "
                             + $"reading: {Summarize(_unreadable)}");
                }
            }
        }

        /// <summary>The first few entries of a report line, with a count when more were left out.</summary>
        private static string Summarize(IReadOnlyList<string> entries, int max = 5) =>
            entries.Count <= max
                ? string.Join("; ", entries)
                : string.Join("; ", entries.Take(max)) + $"; and {entries.Count - max} more";

        private static List<TarkovDevTaskPrerequisite> BuildTaskRequirements(List<JsonTaskRequirement>? source)
        {
            var result = new List<TarkovDevTaskPrerequisite>();
            if (source == null)
                return result;

            foreach (var req in source)
            {
                if (string.IsNullOrEmpty(req.Task))
                    continue;

                result.Add(new TarkovDevTaskPrerequisite
                {
                    TaskId = req.Task,
                    Status = req.Status ?? new List<string>(),
                });
            }

            return result;
        }

        /// <summary>
        /// What the game records as failing a task. Every kind is carried, not only the
        /// <c>taskStatus</c> one the pipeline acts on: a prerequisite that could not be turned
        /// into an OR group is reported with what does fail it, and "a Lightkeeper standing"
        /// says more to the reader than "not a quest". Only <c>taskStatus</c> carries a task id,
        /// so the others keep it null.
        /// </summary>
        private static List<TarkovDevTaskFailCondition> BuildFailConditions(List<JsonTaskFailCondition>? source)
        {
            var result = new List<TarkovDevTaskFailCondition>();
            if (source == null)
                return result;

            foreach (var condition in source)
            {
                result.Add(new TarkovDevTaskFailCondition
                {
                    Type = condition.Type ?? "",
                    TaskId = string.IsNullOrEmpty(condition.Task) ? null : condition.Task,
                    Status = condition.Status ?? new List<string>(),
                });
            }

            return result;
        }

        /// <summary>URL-decodes a wiki link so <c>%28</c> and <c>(</c> compare equal.</summary>
        internal static string NormalizeWikiLink(string wikiLink)
        {
            if (string.IsNullOrEmpty(wikiLink))
                return wikiLink;

            try
            {
                return Uri.UnescapeDataString(wikiLink);
            }
            catch (UriFormatException)
            {
                return wikiLink;
            }
        }

        /// <summary>
        /// The collection one endpoint's data file carries, or a refusal. Never an empty set: a
        /// 200 carrying an error body parsed to <c>{}</c> is what emptied the cache in January,
        /// so the check lives here once rather than in each fetch, where a fifth endpoint could
        /// omit it.
        /// </summary>
        /// <param name="noun">What the endpoint serves, as the refusal names it ("tasks").</param>
        /// <param name="select">
        /// The collection inside the envelope's <c>data</c>. Some endpoints nest it under a
        /// field, some serve the records as <c>data</c> itself.
        /// </param>
        private Dictionary<string, TRecord> ReadCollection<TData, TRecord>(
            EndpointGroup group, string path, string noun, Func<TData, Dictionary<string, TRecord>?> select)
            where TData : class
        {
            var payload = Deserialize<JsonEnvelope<TData>>(group.Bodies[0], path);
            var records = payload?.Data is { } data ? select(data) : null;
            if (records == null || records.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{_baseUrl}{path} returned no {noun}. Refusing to overwrite the cache with an empty set.");
            }

            return records;
        }

        private static T? Deserialize<T>(string body, string path)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(body, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{path}: response is not the expected JSON shape: {ex.Message}", ex);
            }
        }

        private static LocaleTable ReadLocales(EndpointGroup group, string path)
        {
            var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < LocalizedLanguages.Length; i++)
            {
                var language = LocalizedLanguages[i];
                var payload = Deserialize<JsonEnvelope<Dictionary<string, JsonElement>>>(
                    group.Bodies[i + 1], LocalePath(path, language));

                var table = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (key, value) in payload?.Data ?? new Dictionary<string, JsonElement>())
                {
                    if (value.ValueKind == JsonValueKind.String)
                    {
                        var text = value.GetString();
                        if (!string.IsNullOrEmpty(text))
                            table[key] = text;
                    }
                }

                tables[language] = table;
            }

            return new LocaleTable(tables);
        }

        /// <summary>
        /// The locale files for one endpoint. Resolution follows the front end: the requested
        /// language, then English. A key that resolves to nothing (or to itself, which is what
        /// an untranslated string looks like once the caller has substituted the key) is
        /// reported as missing rather than as text.
        /// </summary>
        private sealed class LocaleTable
        {
            private readonly Dictionary<string, Dictionary<string, string>> _tables;

            public LocaleTable(Dictionary<string, Dictionary<string, string>> tables) => _tables = tables;

            public string? Resolve(string language, string? key)
            {
                if (string.IsNullOrEmpty(key))
                    return null;

                if (_tables.TryGetValue(language, out var table) && table.TryGetValue(key, out var text) && text != key)
                    return text;

                if (language != EnglishLanguage
                    && _tables.TryGetValue(EnglishLanguage, out var english)
                    && english.TryGetValue(key, out var fallback)
                    && fallback != key)
                {
                    return fallback;
                }

                return null;
            }
        }

        #endregion

        #region HTTP

        /// <summary>One endpoint's data file plus its locale files, read as a set.</summary>
        private sealed class EndpointGroup
        {
            public required IReadOnlyList<string> Paths { get; init; }
            public required IReadOnlyList<string> Bodies { get; init; }
            public required IReadOnlyList<string?> ETags { get; init; }
            public DateTime? SourceLastModified { get; init; }
        }

        /// <summary>
        /// Fetches a data file and its locale files, conditionally when the caller has a cache
        /// to keep. Returns null only when every file answered 304, because a locale file that
        /// moved while the data file did not still has to reach the cache: a 304 carries no
        /// body, so any movement in the group forces the unchanged members to be re-requested.
        /// </summary>
        private async Task<EndpointGroup?> FetchGroupAsync(
            IReadOnlyList<string> paths,
            bool conditional,
            CancellationToken cancellationToken)
        {
            var responses = new ConditionalResponse[paths.Count];
            for (var i = 0; i < paths.Count; i++)
                responses[i] = await GetAsync(paths[i], conditional, cancellationToken);

            if (responses.All(r => r.NotModified))
                return null;

            for (var i = 0; i < responses.Length; i++)
            {
                if (responses[i].NotModified)
                    responses[i] = await GetAsync(paths[i], conditional: false, cancellationToken);
            }

            return new EndpointGroup
            {
                Paths = paths,
                Bodies = responses.Select(r => r.Body!).ToArray(),
                ETags = responses.Select(r => r.ETag).ToArray(),
                // The data file's timestamp is the age the refresh log reports; the locale
                // files carry their own and are not what "how old is this data" asks about.
                SourceLastModified = responses[0].LastModified?.UtcDateTime,
            };
        }

        private readonly record struct ConditionalResponse(bool NotModified, string? Body, string? ETag, DateTimeOffset? LastModified);

        private async Task<ConditionalResponse> GetAsync(string path, bool conditional, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + path);

            if (conditional && _etagStorePath != null && _etags.TryGetValue(path, out var etag)
                && EntityTagHeaderValue.TryParse(etag, out var parsed))
            {
                request.Headers.IfNoneMatch.Add(parsed);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotModified)
                return new ConditionalResponse(NotModified: true, null, null, null);

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"{_baseUrl}{path} returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new ConditionalResponse(
                NotModified: false,
                body,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified);
        }

        #endregion

        #region ETag store

        /// <summary>
        /// Hands the caller a fetch result whose ETags are recorded only when it says the cache
        /// file built from the value reached the disk. Parsing is not the last step that can
        /// fail, so the fetch cannot commit them itself; see
        /// <see cref="TarkovDevFetch{T}.CommitETags"/>.
        /// </summary>
        private TarkovDevFetch<T> Fetched<T>(T value, EndpointGroup group) =>
            new(value, group.SourceLastModified) { ETagCommit = () => CommitETags(group) };

        /// <summary>
        /// Records the group's ETags. Called from <see cref="TarkovDevFetch{T}.CommitETags"/>
        /// once the caller has the parsed models on disk, never before: an ETag that names an
        /// upstream revision no cache file holds makes the next run answer 304 and keep the very
        /// copy the failure was meant to replace, while every freshness check reads as green.
        /// </summary>
        private void CommitETags(EndpointGroup group)
        {
            if (_etagStorePath == null)
                return;

            for (var i = 0; i < group.Paths.Count; i++)
            {
                var etag = group.ETags[i];
                if (string.IsNullOrEmpty(etag))
                    _etags.Remove(group.Paths[i]);
                else
                    _etags[group.Paths[i]] = etag;
            }

            SaveETags();
        }

        private void LoadETags()
        {
            if (_etagStorePath == null || !File.Exists(_etagStorePath))
                return;

            try
            {
                var json = File.ReadAllText(_etagStorePath);
                _etags = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                         ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A damaged store only costs one unconditional fetch per endpoint.
                _etags = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private void SaveETags()
        {
            if (_etagStorePath == null)
                return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_etagStorePath)!);
                File.WriteAllText(_etagStorePath, JsonSerializer.Serialize(_etags, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Losing the store costs bandwidth on the next run, never correctness.
            }
        }

        #endregion

        public void Dispose()
        {
            _httpClient.Dispose();
        }

        #region Wire models

        /// <summary>Every endpoint wraps its payload in <c>{"data": ...}</c>.</summary>
        private sealed class JsonEnvelope<T>
        {
            [JsonPropertyName("data")]
            public T? Data { get; set; }
        }

        private sealed class JsonTasksData
        {
            [JsonPropertyName("tasks")]
            public Dictionary<string, JsonTask>? Tasks { get; set; }
        }

        private sealed class JsonTask
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("normalizedName")] public string? NormalizedName { get; set; }
            [JsonPropertyName("wikiLink")] public string? WikiLink { get; set; }
            [JsonPropertyName("trader")] public string? Trader { get; set; }
            [JsonPropertyName("minPlayerLevel")] public int MinPlayerLevel { get; set; }
            [JsonPropertyName("kappaRequired")] public bool KappaRequired { get; set; }
            [JsonPropertyName("factionName")] public string? FactionName { get; set; }
            [JsonPropertyName("availableDelaySecondsMin")] public int AvailableDelaySecondsMin { get; set; }
            [JsonPropertyName("taskRequirements")] public List<JsonTaskRequirement>? TaskRequirements { get; set; }
            [JsonPropertyName("traderRequirements")] public List<JsonTraderRequirement>? TraderRequirements { get; set; }
            [JsonPropertyName("failConditions")] public List<JsonTaskFailCondition>? FailConditions { get; set; }
        }

        private sealed class JsonTaskRequirement
        {
            [JsonPropertyName("task")] public string? Task { get; set; }
            [JsonPropertyName("status")] public List<string>? Status { get; set; }
        }

        /// <summary>
        /// A <c>failConditions</c> entry. Shares its <c>task</c>/<c>status</c> shape with
        /// <see cref="JsonTaskRequirement"/> but is a different relation: a task requirement
        /// says what has to be true to start, a fail condition what makes the game give up on it.
        /// The entries also carry zones, maps, counts and a description, none of which the
        /// schema has anywhere to put.
        /// </summary>
        private sealed class JsonTaskFailCondition
        {
            [JsonPropertyName("type")] public string? Type { get; set; }
            [JsonPropertyName("task")] public string? Task { get; set; }
            [JsonPropertyName("status")] public List<string>? Status { get; set; }
        }

        /// <summary>
        /// Shared by tasks and hideout levels: <c>{requirementType, compareMethod, value, trader}</c>.
        /// </summary>
        private sealed class JsonTraderRequirement
        {
            [JsonPropertyName("requirementType")] public string? RequirementType { get; set; }
            [JsonPropertyName("compareMethod")] public string? CompareMethod { get; set; }
            [JsonPropertyName("value")] public double Value { get; set; }
            [JsonPropertyName("trader")] public string? Trader { get; set; }
        }

        private sealed class JsonItemsData
        {
            [JsonPropertyName("items")]
            public Dictionary<string, JsonItem>? Items { get; set; }
        }

        private sealed class JsonItem
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("shortName")] public string? ShortName { get; set; }
            [JsonPropertyName("normalizedName")] public string? NormalizedName { get; set; }
            [JsonPropertyName("wikiLink")] public string? WikiLink { get; set; }
            [JsonPropertyName("iconLink")] public string? IconLink { get; set; }
        }

        private sealed class JsonTrader
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("normalizedName")] public string? NormalizedName { get; set; }
            [JsonPropertyName("imageLink")] public string? ImageLink { get; set; }
        }

        private sealed class JsonHideoutStation
        {
            [JsonPropertyName("id")] public string? Id { get; set; }
            [JsonPropertyName("name")] public string? Name { get; set; }
            [JsonPropertyName("normalizedName")] public string? NormalizedName { get; set; }
            [JsonPropertyName("imageLink")] public string? ImageLink { get; set; }
            [JsonPropertyName("levels")] public List<JsonHideoutLevel>? Levels { get; set; }
        }

        private sealed class JsonHideoutLevel
        {
            [JsonPropertyName("level")] public int Level { get; set; }
            [JsonPropertyName("constructionTime")] public int ConstructionTime { get; set; }
            [JsonPropertyName("itemRequirements")] public List<JsonHideoutItemRequirement>? ItemRequirements { get; set; }
            [JsonPropertyName("stationLevelRequirements")] public List<JsonHideoutStationRequirement>? StationLevelRequirements { get; set; }
            [JsonPropertyName("traderRequirements")] public List<JsonTraderRequirement>? TraderRequirements { get; set; }
            [JsonPropertyName("skillRequirements")] public List<JsonHideoutSkillRequirement>? SkillRequirements { get; set; }
        }

        private sealed class JsonHideoutItemRequirement
        {
            [JsonPropertyName("item")] public string? Item { get; set; }
            [JsonPropertyName("count")] public int Count { get; set; }
            [JsonPropertyName("attributes")] public JsonHideoutItemAttributes? Attributes { get; set; }
        }

        private sealed class JsonHideoutItemAttributes
        {
            [JsonPropertyName("foundInRaid")] public bool? FoundInRaid { get; set; }
        }

        private sealed class JsonHideoutStationRequirement
        {
            [JsonPropertyName("station")] public string? Station { get; set; }
            [JsonPropertyName("level")] public int Level { get; set; }
        }

        private sealed class JsonHideoutSkillRequirement
        {
            [JsonPropertyName("skill")] public string? Skill { get; set; }
            [JsonPropertyName("level")] public int Level { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// What one endpoint fetch produced, with the upstream <c>Last-Modified</c> so the refresh
    /// log can state the data's age rather than the cache file's.
    /// </summary>
    public sealed record TarkovDevFetch<T>(T Value, DateTime? SourceLastModified)
    {
        /// <summary>Supplied by the client that produced the fetch. See <see cref="CommitETags"/>.</summary>
        internal Action? ETagCommit { get; init; }

        /// <summary>
        /// Records this fetch's ETags, so the next run may be told 304 and keep what it has.
        /// <para>
        /// Call it only once <see cref="Value"/> is on disk. The ETag store and the cache file
        /// are one claim in two places ("the copy we hold is upstream revision X"), and the
        /// store must never be the fresher of the two: if it names a revision the cache file
        /// does not hold, the next run answers 304, re-stamps the stale file as verified, and
        /// every downstream freshness guard passes on data from before the patch. Not calling
        /// it costs one unconditional fetch, never correctness, so this is the safe direction
        /// for a caller to get wrong.
        /// </para>
        /// </summary>
        public void CommitETags() => ETagCommit?.Invoke();
    }
}
