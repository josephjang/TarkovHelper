using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace TarkovDBEditor.Services
{
    /// <summary>
    /// Owns the tarkov.dev cache files under <c>wiki_data/cache/</c>: what is in them, how old
    /// they are, and how they are refilled. The refresh pipeline reads only these files, never
    /// the network, so a regeneration is repeatable and a network outage cannot half-fill a
    /// database.
    /// <para>
    /// The transport is <see cref="TarkovDevJsonClient"/> (json.tarkov.dev). The GraphQL
    /// endpoint this service used to POST to has answered "GraphQL server unavailable" since
    /// about 2026-07-22 and its queries never requested the fields the 1.1 refresh needs
    /// (minimum level, Kappa flag, per-trader loyalty, prerequisites, faction), so it was
    /// replaced rather than kept as a fallback: a second transport that cannot be exercised
    /// is untested code on the critical path.
    /// </para>
    /// <para>
    /// Every part is refilled in isolation: a part that fails, or that upstream reports
    /// unchanged, keeps the file it already had, and the result says which.
    /// </para>
    /// </summary>
    public class TarkovDevDataService : IDisposable
    {
        private readonly TarkovDevJsonClient _jsonClient;

        private readonly string _cacheDir;
        private readonly string _itemsCachePath;
        private readonly string _questsCachePath;
        private readonly string _hideoutCachePath;
        private readonly string _tradersCachePath;

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public TarkovDevDataService(string? basePath = null, TarkovDevJsonClient? jsonClient = null)
        {
            basePath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wiki_data");
            _cacheDir = Path.Combine(basePath, "cache");
            _itemsCachePath = Path.Combine(_cacheDir, "tarkov_dev_items.json");
            _questsCachePath = Path.Combine(_cacheDir, "tarkov_dev_quests.json");
            _hideoutCachePath = Path.Combine(_cacheDir, "tarkov_dev_hideout.json");
            _tradersCachePath = Path.Combine(_cacheDir, "tarkov_dev_traders.json");

            Directory.CreateDirectory(_cacheDir);

            _jsonClient = jsonClient ?? new TarkovDevJsonClient(_cacheDir);
        }

        #region Cache Management

        public bool HasCachedItems() => File.Exists(_itemsCachePath);

        public bool HasCachedQuests() => File.Exists(_questsCachePath);

        public bool HasCachedHideout() => File.Exists(_hideoutCachePath);

        public bool HasCachedTraders() => File.Exists(_tradersCachePath);

        /// <summary>
        /// A tolerant status read for the UI: counts and ages, with an unreadable file
        /// reported as absent rather than thrown. The pipeline uses the Load methods below,
        /// which are not tolerant.
        /// <para>
        /// "Cached at" is the file's last write time, not the <c>cachedAt</c> field inside it,
        /// because a part upstream reports unchanged is re-stamped rather than rewritten: the
        /// question the age answers is "when did we last confirm this is current".
        /// </para>
        /// </summary>
        public TarkovDevCacheInfo GetCacheInfo()
        {
            var info = new TarkovDevCacheInfo();

            info.ItemsCachedAt = TryGetWriteTime(_itemsCachePath);
            info.ItemsCount = TryCount(_itemsCachePath, json =>
                JsonSerializer.Deserialize<TarkovDevItemsCache>(json)?.Items?.Count ?? 0);

            info.QuestsCachedAt = TryGetWriteTime(_questsCachePath);
            info.QuestsCount = TryCount(_questsCachePath, json =>
                JsonSerializer.Deserialize<TarkovDevQuestsCache>(json)?.Quests?.Count ?? 0);

            info.HideoutCachedAt = TryGetWriteTime(_hideoutCachePath);
            info.HideoutCount = TryCount(_hideoutCachePath, json =>
                JsonSerializer.Deserialize<TarkovDevHideoutCache>(json)?.Stations?.Count ?? 0);

            info.TradersCachedAt = TryGetWriteTime(_tradersCachePath);
            info.TradersCount = TryCount(_tradersCachePath, json =>
                JsonSerializer.Deserialize<TarkovDevTradersCache>(json)?.Traders?.Count ?? 0);

            return info;
        }

        private static DateTime? TryGetWriteTime(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).LastWriteTime : null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static int TryCount(string path, Func<string, int> count)
        {
            try
            {
                return File.Exists(path) ? count(File.ReadAllText(path)) : 0;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        /// <summary>
        /// Reads one cache file. A missing file returns null (the caller decides whether that
        /// is fatal); a file that is present but unreadable throws, because silently treating
        /// a damaged cache as "no cache" is how a refresh ends up writing English names or
        /// NULL external IDs over good data.
        /// </summary>
        private static async Task<T?> LoadCacheAsync<T>(string path, CancellationToken cancellationToken)
            where T : class
        {
            if (!File.Exists(path))
                return null;

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            try
            {
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"{path} is not a readable tarkov.dev cache file ({ex.Message}). " +
                    "Delete it and run 'Debug > Cache Tarkov Dev Data'.", ex);
            }
        }

        /// <summary>Cached items, keyed by the URL-decoded wiki page link.</summary>
        public async Task<Dictionary<string, TarkovDevMultiLangItem>?> LoadCachedItemsAsync(
            CancellationToken cancellationToken = default)
        {
            var cache = await LoadCacheAsync<TarkovDevItemsCache>(_itemsCachePath, cancellationToken);
            return cache?.Items;
        }

        /// <summary>
        /// Cached tasks. A list, not a dictionary keyed by <c>wikiLink</c>: ten wiki titles are
        /// the link of two or three tasks, and the old dictionary silently kept whichever it
        /// saw last. Choosing among them is <see cref="QuestIdentityResolver"/>'s job and it
        /// needs to see all of them.
        /// </summary>
        public async Task<List<TarkovDevQuestCacheItem>?> LoadCachedQuestsAsync(
            CancellationToken cancellationToken = default)
        {
            var cache = await LoadCacheAsync<TarkovDevQuestsCache>(_questsCachePath, cancellationToken);
            return cache?.Quests;
        }

        public async Task<List<TarkovDevHideoutStation>?> LoadCachedHideoutAsync(
            CancellationToken cancellationToken = default)
        {
            var cache = await LoadCacheAsync<TarkovDevHideoutCache>(_hideoutCachePath, cancellationToken);
            return cache?.Stations;
        }

        public async Task<List<TarkovDevTraderCacheItem>?> LoadCachedTradersAsync(
            CancellationToken cancellationToken = default)
        {
            var cache = await LoadCacheAsync<TarkovDevTradersCache>(_tradersCachePath, cancellationToken);
            return cache?.Traders;
        }

        public async Task SaveItemsCacheAsync(
            Dictionary<string, TarkovDevMultiLangItem> items,
            DateTime? sourceLastModified = null,
            CancellationToken cancellationToken = default)
        {
            await WriteCacheAsync(_itemsCachePath, new TarkovDevItemsCache
            {
                CachedAt = DateTime.UtcNow,
                SourceLastModified = sourceLastModified,
                Items = items
            }, cancellationToken);
        }

        public async Task SaveQuestsCacheAsync(
            List<TarkovDevQuestCacheItem> quests,
            DateTime? sourceLastModified = null,
            CancellationToken cancellationToken = default)
        {
            await WriteCacheAsync(_questsCachePath, new TarkovDevQuestsCache
            {
                CachedAt = DateTime.UtcNow,
                SourceLastModified = sourceLastModified,
                Quests = quests
            }, cancellationToken);
        }

        public async Task SaveHideoutCacheAsync(
            List<TarkovDevHideoutStation> stations,
            DateTime? sourceLastModified = null,
            CancellationToken cancellationToken = default)
        {
            await WriteCacheAsync(_hideoutCachePath, new TarkovDevHideoutCache
            {
                CachedAt = DateTime.UtcNow,
                SourceLastModified = sourceLastModified,
                Stations = stations
            }, cancellationToken);
        }

        public async Task SaveTradersCacheAsync(
            List<TarkovDevTraderCacheItem> traders,
            DateTime? sourceLastModified = null,
            CancellationToken cancellationToken = default)
        {
            await WriteCacheAsync(_tradersCachePath, new TarkovDevTradersCache
            {
                CachedAt = DateTime.UtcNow,
                SourceLastModified = sourceLastModified,
                Traders = traders
            }, cancellationToken);
        }

        private static async Task WriteCacheAsync<T>(string path, T cache, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(cache, WriteOptions);
            await File.WriteAllTextAsync(path, json, cancellationToken);
        }

        /// <summary>
        /// Records that a part upstream reported unchanged was confirmed current now, without
        /// rewriting its body (the items file is 16 MB). The staleness guard in the refresh
        /// reads this timestamp.
        /// </summary>
        private static void MarkVerified(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Costs one unconditional fetch on a later run; never correctness.
            }
        }

        /// <summary>
        /// Refills every cache file from json.tarkov.dev. Parts are independent: one that
        /// throws, and one upstream reports unchanged, both leave the existing file in place,
        /// and the result records which happened and how old the kept file is.
        /// <para>
        /// Order matters. Hideout levels reference items and traders by id only, so those two
        /// parts are read (fresh, or from the cache when unchanged) before the hideout part
        /// can name what a level requires.
        /// </para>
        /// </summary>
        public async Task<TarkovDevCacheResult> CacheAllDataAsync(
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var result = new TarkovDevCacheResult();

            var items = await RunPartAsync(result.Items, progress, cancellationToken, async () =>
            {
                var fetched = await _jsonClient.FetchItemsAsync(HasCachedItems(), progress, cancellationToken);
                if (fetched == null)
                {
                    MarkVerified(_itemsCachePath);
                    return (await LoadCachedItemsAsync(cancellationToken), true, (DateTime?)null);
                }

                await SaveItemsCacheAsync(fetched.Value, fetched.SourceLastModified, cancellationToken);
                return (fetched.Value, false, fetched.SourceLastModified);
            }, v => v?.Count ?? 0);

            var traders = await RunPartAsync(result.Traders, progress, cancellationToken, async () =>
            {
                var fetched = await _jsonClient.FetchTradersAsync(HasCachedTraders(), progress, cancellationToken);
                if (fetched == null)
                {
                    MarkVerified(_tradersCachePath);
                    return (await LoadCachedTradersAsync(cancellationToken), true, (DateTime?)null);
                }

                await SaveTradersCacheAsync(fetched.Value, fetched.SourceLastModified, cancellationToken);
                return (fetched.Value, false, fetched.SourceLastModified);
            }, v => v?.Count ?? 0);

            await RunPartAsync(result.Quests, progress, cancellationToken, async () =>
            {
                var fetched = await _jsonClient.FetchTasksAsync(HasCachedQuests(), progress, cancellationToken);
                if (fetched == null)
                {
                    MarkVerified(_questsCachePath);
                    return (await LoadCachedQuestsAsync(cancellationToken), true, (DateTime?)null);
                }

                await SaveQuestsCacheAsync(fetched.Value, fetched.SourceLastModified, cancellationToken);
                return (fetched.Value, false, fetched.SourceLastModified);
            }, v => v?.Count ?? 0);

            await RunPartAsync(result.Hideout, progress, cancellationToken, async () =>
            {
                if (items == null || traders == null)
                {
                    throw new InvalidOperationException(
                        "Hideout levels name their items and traders by id only, so the items and traders "
                        + "caches must be readable first. Fix those parts and re-run.");
                }

                var fetched = await _jsonClient.FetchHideoutAsync(
                    items.Values, traders, HasCachedHideout(), progress, cancellationToken);
                if (fetched == null)
                {
                    MarkVerified(_hideoutCachePath);
                    return (await LoadCachedHideoutAsync(cancellationToken), true, (DateTime?)null);
                }

                await SaveHideoutCacheAsync(fetched.Value, fetched.SourceLastModified, cancellationToken);
                return (fetched.Value, false, fetched.SourceLastModified);
            }, v => v?.Count ?? 0);

            result.CachedAt = DateTime.Now;
            return result;
        }

        /// <summary>
        /// Runs one cache part, recording success, an upstream-unchanged keep, or the failure
        /// that left the old file alone. Returns the part's value so a later part can use it.
        /// </summary>
        private async Task<T?> RunPartAsync<T>(
            TarkovDevCachePart part,
            Action<string>? progress,
            CancellationToken cancellationToken,
            Func<Task<(T? Value, bool Kept, DateTime? SourceLastModified)>> fetch,
            Func<T?, int> count)
            where T : class
        {
            try
            {
                var (value, kept, sourceLastModified) = await fetch();
                part.Success = true;
                part.Kept = kept;
                part.Count = count(value);
                part.CachedAt = TryGetWriteTime(CachePathFor(part.Name));
                part.SourceLastModified = sourceLastModified;
                progress?.Invoke(kept
                    ? $"{part.Name}: unchanged upstream, kept {part.Count} from {part.CachedAt:yyyy-MM-dd HH:mm}"
                    : $"{part.Name}: cached {part.Count}");
                return value;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                part.Success = false;
                part.Kept = File.Exists(CachePathFor(part.Name));
                part.Error = ex.Message;
                part.CachedAt = TryGetWriteTime(CachePathFor(part.Name));
                progress?.Invoke($"{part.Name}: failed ({ex.Message})"
                    + (part.Kept ? $"; kept the copy from {part.CachedAt:yyyy-MM-dd HH:mm}" : "; no cache to keep"));
                return null;
            }
        }

        private string CachePathFor(string partName) => partName switch
        {
            TarkovDevCacheResult.ItemsPart => _itemsCachePath,
            TarkovDevCacheResult.QuestsPart => _questsCachePath,
            TarkovDevCacheResult.HideoutPart => _hideoutCachePath,
            TarkovDevCacheResult.TradersPart => _tradersCachePath,
            _ => throw new ArgumentOutOfRangeException(nameof(partName), partName, "Unknown tarkov.dev cache part.")
        };

        #endregion

        /// <summary>
        /// Decides a quest's localized name. A missing, blank, or English-identical translation
        /// becomes NULL so an untranslated quest falls back at display time instead of storing
        /// the English string as if it were a translation (the same rule the trader and item
        /// paths use).
        /// </summary>
        public static string? ResolveLocalizedQuestName(string? localizedName, string englishName)
        {
            if (string.IsNullOrWhiteSpace(localizedName))
                return null;
            return localizedName == englishName ? null : localizedName;
        }

        /// <summary>
        /// Annotates the debug export <c>wiki_items.json</c> with the cached tarkov.dev item
        /// data and writes the two difference lists beside it. Reads the cache, never the
        /// network, so this export describes exactly the data a refresh would use.
        /// </summary>
        public async Task<EnrichmentResult> EnrichWikiItemsAsync(
            string wikiItemsPath,
            string outputPath,
            string missingOutputPath,
            string devOnlyOutputPath,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Invoke("Loading wiki_items.json...");

            var wikiJson = await File.ReadAllTextAsync(wikiItemsPath, cancellationToken);
            var wikiItemList = JsonSerializer.Deserialize<WikiItemList>(wikiJson);

            if (wikiItemList?.Items == null)
            {
                throw new InvalidOperationException($"Failed to load {wikiItemsPath}");
            }

            progress?.Invoke($"Loaded {wikiItemList.Items.Count} wiki items");

            var devItems = await LoadCachedItemsAsync(cancellationToken);
            if (devItems == null || devItems.Count == 0)
            {
                throw new InvalidOperationException(
                    "tarkov.dev item cache is empty or missing. Run 'Debug > Cache Tarkov Dev Data' first.");
            }

            progress?.Invoke($"Matching {wikiItemList.Items.Count} wiki items against {devItems.Count} cached tarkov.dev items...");

            var enrichedItems = new List<EnrichedWikiItem>();
            var missingItems = new List<MissingDevItem>();
            var matchedWikiLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchedCount = 0;

            foreach (var wikiItem in wikiItemList.Items)
            {
                var decodedWikiLink = TarkovDevJsonClient.NormalizeWikiLink(wikiItem.WikiPageLink);

                var enriched = new EnrichedWikiItem
                {
                    Id = wikiItem.Id,
                    Name = wikiItem.Name,
                    WikiPageLink = decodedWikiLink,
                    IconUrl = wikiItem.IconUrl,
                    Category = wikiItem.Category,
                    Categories = wikiItem.Categories
                };

                if (!string.IsNullOrEmpty(decodedWikiLink) &&
                    devItems.TryGetValue(decodedWikiLink, out var devItem))
                {
                    enriched.BsgId = devItem.BsgId;
                    enriched.NameEN = devItem.NameEN;
                    enriched.NameKO = devItem.NameKO;
                    enriched.NameJA = devItem.NameJA;
                    enriched.ShortNameEN = devItem.ShortNameEN;
                    enriched.ShortNameKO = devItem.ShortNameKO;
                    enriched.ShortNameJA = devItem.ShortNameJA;
                    matchedWikiLinks.Add(decodedWikiLink);
                    matchedCount++;
                }
                else
                {
                    enriched.NameEN = wikiItem.Name;
                    enriched.NameKO = wikiItem.Name;
                    enriched.NameJA = wikiItem.Name;

                    missingItems.Add(new MissingDevItem
                    {
                        WikiId = wikiItem.Id,
                        WikiName = wikiItem.Name,
                        WikiPageLink = decodedWikiLink,
                        Category = wikiItem.Category,
                        Categories = wikiItem.Categories
                    });
                }

                enrichedItems.Add(enriched);
            }

            var devOnlyItems = new List<DevOnlyItem>();
            foreach (var kvp in devItems)
            {
                if (matchedWikiLinks.Contains(kvp.Key))
                    continue;

                devOnlyItems.Add(new DevOnlyItem
                {
                    BsgId = kvp.Value.BsgId,
                    WikiLink = kvp.Value.WikiLink,
                    NameEN = kvp.Value.NameEN,
                    NameKO = kvp.Value.NameKO,
                    NameJA = kvp.Value.NameJA,
                    ShortNameEN = kvp.Value.ShortNameEN,
                    ShortNameKO = kvp.Value.ShortNameKO,
                    ShortNameJA = kvp.Value.ShortNameJA
                });
            }

            progress?.Invoke($"Matched {matchedCount}/{wikiItemList.Items.Count} items. Wiki missing: {missingItems.Count}, Dev only: {devOnlyItems.Count}");

            var enrichedResult = new EnrichedWikiItemList
            {
                ExportedAt = DateTime.UtcNow,
                TotalItems = enrichedItems.Count,
                MatchedItems = matchedCount,
                MissingItems = missingItems.Count,
                DevOnlyItems = devOnlyItems.Count,
                Items = enrichedItems
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var enrichedJson = JsonSerializer.Serialize(enrichedResult, options);
            await File.WriteAllTextAsync(outputPath, enrichedJson, Encoding.UTF8, cancellationToken);
            progress?.Invoke($"Saved enriched items to: {outputPath}");

            if (missingItems.Count > 0)
            {
                var missingResult = new MissingDevItemList
                {
                    ExportedAt = DateTime.UtcNow,
                    TotalMissing = missingItems.Count,
                    Items = missingItems
                };
                var missingJson = JsonSerializer.Serialize(missingResult, options);
                await File.WriteAllTextAsync(missingOutputPath, missingJson, Encoding.UTF8, cancellationToken);
                progress?.Invoke($"Saved wiki-only items to: {missingOutputPath}");
            }

            if (devOnlyItems.Count > 0)
            {
                var devOnlyResult = new DevOnlyItemList
                {
                    ExportedAt = DateTime.UtcNow,
                    TotalDevOnly = devOnlyItems.Count,
                    Items = devOnlyItems
                };
                var devOnlyJson = JsonSerializer.Serialize(devOnlyResult, options);
                await File.WriteAllTextAsync(devOnlyOutputPath, devOnlyJson, Encoding.UTF8, cancellationToken);
                progress?.Invoke($"Saved dev-only items to: {devOnlyOutputPath}");
            }

            return new EnrichmentResult
            {
                TotalItems = enrichedItems.Count,
                MatchedCount = matchedCount,
                MissingCount = missingItems.Count,
                DevOnlyCount = devOnlyItems.Count,
                OutputPath = outputPath,
                MissingOutputPath = missingOutputPath,
                DevOnlyOutputPath = devOnlyOutputPath
            };
        }

        public void Dispose()
        {
            _jsonClient.Dispose();
        }
    }

    #region tarkov.dev Models

    /// <summary>
    /// An item as the cache holds it: one row per wiki page, with the three languages merged.
    /// </summary>
    public class TarkovDevMultiLangItem
    {
        public string BsgId { get; set; } = "";
        public string WikiLink { get; set; } = "";
        public string NameEN { get; set; } = "";
        public string ShortNameEN { get; set; } = "";
        public string? NameKO { get; set; }
        public string? ShortNameKO { get; set; }
        public string? NameJA { get; set; }
        public string? ShortNameJA { get; set; }

        /// <summary>tarkov.dev's own slug. Carried so hideout requirements can name their item.</summary>
        public string? NormalizedName { get; set; }

        /// <summary>Remote icon URL. Carried for the same reason as <see cref="NormalizedName"/>.</summary>
        public string? IconLink { get; set; }
    }

    /// <summary>
    /// A Wiki item annotated with tarkov.dev data (the <c>wiki_items.json</c> debug export).
    /// </summary>
    public class EnrichedWikiItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("bsgId")]
        public string? BsgId { get; set; }

        [JsonPropertyName("nameEN")]
        public string? NameEN { get; set; }

        [JsonPropertyName("nameKO")]
        public string? NameKO { get; set; }

        [JsonPropertyName("nameJA")]
        public string? NameJA { get; set; }

        [JsonPropertyName("shortNameEN")]
        public string? ShortNameEN { get; set; }

        [JsonPropertyName("shortNameKO")]
        public string? ShortNameKO { get; set; }

        [JsonPropertyName("shortNameJA")]
        public string? ShortNameJA { get; set; }

        [JsonPropertyName("wikiPageLink")]
        public string WikiPageLink { get; set; } = "";

        [JsonPropertyName("iconUrl")]
        public string? IconUrl { get; set; }

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("categories")]
        public List<string> Categories { get; set; } = new();
    }

    public class EnrichedWikiItemList
    {
        [JsonPropertyName("exportedAt")]
        public DateTime ExportedAt { get; set; }

        [JsonPropertyName("totalItems")]
        public int TotalItems { get; set; }

        [JsonPropertyName("matchedItems")]
        public int MatchedItems { get; set; }

        [JsonPropertyName("missingItems")]
        public int MissingItems { get; set; }

        [JsonPropertyName("devOnlyItems")]
        public int DevOnlyItems { get; set; }

        [JsonPropertyName("items")]
        public List<EnrichedWikiItem> Items { get; set; } = new();
    }

    /// <summary>A wiki item with no tarkov.dev counterpart.</summary>
    public class MissingDevItem
    {
        [JsonPropertyName("wikiId")]
        public string WikiId { get; set; } = "";

        [JsonPropertyName("wikiName")]
        public string WikiName { get; set; } = "";

        [JsonPropertyName("wikiPageLink")]
        public string WikiPageLink { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("categories")]
        public List<string> Categories { get; set; } = new();
    }

    public class MissingDevItemList
    {
        [JsonPropertyName("exportedAt")]
        public DateTime ExportedAt { get; set; }

        [JsonPropertyName("totalMissing")]
        public int TotalMissing { get; set; }

        [JsonPropertyName("items")]
        public List<MissingDevItem> Items { get; set; } = new();
    }

    public class EnrichmentResult
    {
        public int TotalItems { get; set; }
        public int MatchedCount { get; set; }
        public int MissingCount { get; set; }
        public int DevOnlyCount { get; set; }
        public string OutputPath { get; set; } = "";
        public string MissingOutputPath { get; set; } = "";
        public string DevOnlyOutputPath { get; set; } = "";
    }

    /// <summary>A tarkov.dev item with no wiki page in the crawl.</summary>
    public class DevOnlyItem
    {
        [JsonPropertyName("bsgId")]
        public string BsgId { get; set; } = "";

        [JsonPropertyName("wikiLink")]
        public string WikiLink { get; set; } = "";

        [JsonPropertyName("nameEN")]
        public string NameEN { get; set; } = "";

        [JsonPropertyName("nameKO")]
        public string? NameKO { get; set; }

        [JsonPropertyName("nameJA")]
        public string? NameJA { get; set; }

        [JsonPropertyName("shortNameEN")]
        public string? ShortNameEN { get; set; }

        [JsonPropertyName("shortNameKO")]
        public string? ShortNameKO { get; set; }

        [JsonPropertyName("shortNameJA")]
        public string? ShortNameJA { get; set; }
    }

    public class DevOnlyItemList
    {
        [JsonPropertyName("exportedAt")]
        public DateTime ExportedAt { get; set; }

        [JsonPropertyName("totalDevOnly")]
        public int TotalDevOnly { get; set; }

        [JsonPropertyName("items")]
        public List<DevOnlyItem> Items { get; set; } = new();
    }

    #endregion

    #region Cache Models

    public class TarkovDevCacheInfo
    {
        public DateTime? ItemsCachedAt { get; set; }
        public int ItemsCount { get; set; }
        public DateTime? QuestsCachedAt { get; set; }
        public int QuestsCount { get; set; }
        public DateTime? HideoutCachedAt { get; set; }
        public int HideoutCount { get; set; }
        public DateTime? TradersCachedAt { get; set; }
        public int TradersCount { get; set; }

        public bool HasItemsCache => ItemsCachedAt.HasValue;
        public bool HasQuestsCache => QuestsCachedAt.HasValue;
        public bool HasHideoutCache => HideoutCachedAt.HasValue;
        public bool HasTradersCache => TradersCachedAt.HasValue;
    }

    /// <summary>What one cache part did on a refill.</summary>
    public class TarkovDevCachePart
    {
        public TarkovDevCachePart(string name) => Name = name;

        /// <summary>Display name, and the key <see cref="TarkovDevDataService"/> maps to a file.</summary>
        public string Name { get; }

        /// <summary>True when the part ended with a usable cache file, fresh or kept.</summary>
        public bool Success { get; set; }

        /// <summary>
        /// True when the existing file was kept rather than rewritten: either upstream reported
        /// it unchanged, or the fetch failed and the old copy is still there.
        /// </summary>
        public bool Kept { get; set; }

        public int Count { get; set; }
        public string? Error { get; set; }

        /// <summary>When this part was last confirmed current (the cache file's write time).</summary>
        public DateTime? CachedAt { get; set; }

        /// <summary>Upstream's own <c>Last-Modified</c>, when the part was fetched.</summary>
        public DateTime? SourceLastModified { get; set; }
    }

    /// <summary>The outcome of <see cref="TarkovDevDataService.CacheAllDataAsync"/>, part by part.</summary>
    public class TarkovDevCacheResult
    {
        public const string ItemsPart = "Items";
        public const string QuestsPart = "Quests";
        public const string HideoutPart = "Hideout";
        public const string TradersPart = "Traders";

        public DateTime CachedAt { get; set; }

        public TarkovDevCachePart Items { get; } = new(ItemsPart);
        public TarkovDevCachePart Quests { get; } = new(QuestsPart);
        public TarkovDevCachePart Hideout { get; } = new(HideoutPart);
        public TarkovDevCachePart Traders { get; } = new(TradersPart);

        public IReadOnlyList<TarkovDevCachePart> Parts => new[] { Items, Quests, Hideout, Traders };

        public bool AllSucceeded => Parts.All(p => p.Success);
    }

    public class TarkovDevItemsCache
    {
        [JsonPropertyName("cachedAt")]
        public DateTime CachedAt { get; set; }

        [JsonPropertyName("sourceLastModified")]
        public DateTime? SourceLastModified { get; set; }

        [JsonPropertyName("items")]
        public Dictionary<string, TarkovDevMultiLangItem> Items { get; set; } = new();
    }

    public class TarkovDevQuestsCache
    {
        [JsonPropertyName("cachedAt")]
        public DateTime CachedAt { get; set; }

        [JsonPropertyName("sourceLastModified")]
        public DateTime? SourceLastModified { get; set; }

        [JsonPropertyName("quests")]
        public List<TarkovDevQuestCacheItem> Quests { get; set; } = new();
    }

    /// <summary>
    /// One tarkov.dev task as the cache holds it: the identity and names the pipeline always
    /// used, plus the 1.1 game rules (level, Kappa, faction, loyalty, prerequisites) that the
    /// old GraphQL queries never asked for.
    /// </summary>
    public class TarkovDevQuestCacheItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("nameEN")]
        public string NameEN { get; set; } = "";

        [JsonPropertyName("normalizedName")]
        public string? NormalizedName { get; set; }

        [JsonPropertyName("nameKO")]
        public string? NameKO { get; set; }

        [JsonPropertyName("nameJA")]
        public string? NameJA { get; set; }

        /// <summary>Giving trader, as a trader id (resolved to a nickname through the traders cache).</summary>
        [JsonPropertyName("trader")]
        public string? Trader { get; set; }

        [JsonPropertyName("wikiLink")]
        public string? WikiLink { get; set; }

        /// <summary>Minimum player level; 0 means "none" and is stored as NULL.</summary>
        [JsonPropertyName("minPlayerLevel")]
        public int MinPlayerLevel { get; set; }

        [JsonPropertyName("kappaRequired")]
        public bool KappaRequired { get; set; }

        /// <summary>"Any", "BEAR" or "USEC" upstream; mapped to NULL/Bear/Usec on the way in.</summary>
        [JsonPropertyName("factionName")]
        public string? FactionName { get; set; }

        /// <summary>Delay before the quest becomes available, in seconds; 0 means none.</summary>
        [JsonPropertyName("availableDelaySecondsMin")]
        public int AvailableDelaySecondsMin { get; set; }

        /// <summary>Loyalty gates only; reputation gates the app cannot express are dropped.</summary>
        [JsonPropertyName("traderLevelRequirements")]
        public List<TarkovDevTaskTraderLevel> TraderLevelRequirements { get; set; } = new();

        /// <summary>Prerequisite tasks, AND semantics (the API has no OR groups).</summary>
        [JsonPropertyName("taskRequirements")]
        public List<TarkovDevTaskPrerequisite> TaskRequirements { get; set; } = new();
    }

    /// <summary>A "loyalty level N with trader T" gate on a task.</summary>
    public class TarkovDevTaskTraderLevel
    {
        [JsonPropertyName("traderId")]
        public string TraderId { get; set; } = "";

        [JsonPropertyName("level")]
        public int Level { get; set; }
    }

    /// <summary>A prerequisite task and the statuses that satisfy it.</summary>
    public class TarkovDevTaskPrerequisite
    {
        [JsonPropertyName("taskId")]
        public string TaskId { get; set; } = "";

        [JsonPropertyName("status")]
        public List<string> Status { get; set; } = new();
    }

    public class TarkovDevHideoutCache
    {
        [JsonPropertyName("cachedAt")]
        public DateTime CachedAt { get; set; }

        [JsonPropertyName("sourceLastModified")]
        public DateTime? SourceLastModified { get; set; }

        [JsonPropertyName("stations")]
        public List<TarkovDevHideoutStation> Stations { get; set; } = new();
    }

    public class TarkovDevTradersCache
    {
        [JsonPropertyName("cachedAt")]
        public DateTime CachedAt { get; set; }

        [JsonPropertyName("sourceLastModified")]
        public DateTime? SourceLastModified { get; set; }

        [JsonPropertyName("traders")]
        public List<TarkovDevTraderCacheItem> Traders { get; set; } = new();
    }

    public class TarkovDevTraderCacheItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("nameKO")]
        public string? NameKO { get; set; }

        [JsonPropertyName("nameJA")]
        public string? NameJA { get; set; }

        [JsonPropertyName("normalizedName")]
        public string? NormalizedName { get; set; }

        [JsonPropertyName("imageLink")]
        public string? ImageLink { get; set; }

        [JsonPropertyName("localIconPath")]
        public string? LocalIconPath { get; set; }
    }

    #endregion

    #region Hideout Models

    public class TarkovDevHideoutStation
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("nameKo")]
        public string? NameKo { get; set; }

        [JsonPropertyName("nameJa")]
        public string? NameJa { get; set; }

        [JsonPropertyName("normalizedName")]
        public string? NormalizedName { get; set; }

        [JsonPropertyName("imageLink")]
        public string? ImageLink { get; set; }

        [JsonPropertyName("levels")]
        public List<TarkovDevHideoutLevel> Levels { get; set; } = new();

        [JsonIgnore]
        public int MaxLevel => Levels?.Count ?? 0;
    }

    public class TarkovDevHideoutLevel
    {
        [JsonPropertyName("level")]
        public int Level { get; set; }

        [JsonPropertyName("constructionTime")]
        public int ConstructionTime { get; set; }

        [JsonPropertyName("itemRequirements")]
        public List<TarkovDevHideoutItemReq> ItemRequirements { get; set; } = new();

        [JsonPropertyName("stationLevelRequirements")]
        public List<TarkovDevHideoutStationReq> StationLevelRequirements { get; set; } = new();

        [JsonPropertyName("traderRequirements")]
        public List<TarkovDevHideoutTraderReq> TraderRequirements { get; set; } = new();

        [JsonPropertyName("skillRequirements")]
        public List<TarkovDevHideoutSkillReq> SkillRequirements { get; set; } = new();
    }

    public class TarkovDevHideoutItemReq
    {
        [JsonPropertyName("itemId")]
        public string ItemId { get; set; } = "";

        [JsonPropertyName("itemName")]
        public string ItemName { get; set; } = "";

        [JsonPropertyName("itemNameKo")]
        public string? ItemNameKo { get; set; }

        [JsonPropertyName("itemNameJa")]
        public string? ItemNameJa { get; set; }

        [JsonPropertyName("itemNormalizedName")]
        public string? ItemNormalizedName { get; set; }

        [JsonPropertyName("iconLink")]
        public string? IconLink { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("foundInRaid")]
        public bool FoundInRaid { get; set; }
    }

    public class TarkovDevHideoutStationReq
    {
        [JsonPropertyName("stationId")]
        public string StationId { get; set; } = "";

        [JsonPropertyName("stationName")]
        public string StationName { get; set; } = "";

        [JsonPropertyName("stationNameKo")]
        public string? StationNameKo { get; set; }

        [JsonPropertyName("stationNameJa")]
        public string? StationNameJa { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }
    }

    public class TarkovDevHideoutTraderReq
    {
        [JsonPropertyName("traderId")]
        public string TraderId { get; set; } = "";

        [JsonPropertyName("traderName")]
        public string TraderName { get; set; } = "";

        [JsonPropertyName("traderNameKo")]
        public string? TraderNameKo { get; set; }

        [JsonPropertyName("traderNameJa")]
        public string? TraderNameJa { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }
    }

    public class TarkovDevHideoutSkillReq
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("nameKo")]
        public string? NameKo { get; set; }

        [JsonPropertyName("nameJa")]
        public string? NameJa { get; set; }

        [JsonPropertyName("level")]
        public int Level { get; set; }
    }

    #endregion
}
