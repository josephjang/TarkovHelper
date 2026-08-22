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

        /// <summary>
        /// True only when this service built the client. A client passed in belongs to whoever
        /// passed it, the same rule <see cref="TarkovDevJsonClient"/> applies to a handler.
        /// </summary>
        private readonly bool _ownsJsonClient;

        private readonly string _cacheDir;
        private readonly CacheFile<Dictionary<string, TarkovDevMultiLangItem>> _items;
        private readonly CacheFile<List<TarkovDevQuestCacheItem>> _quests;
        private readonly CacheFile<List<TarkovDevHideoutStation>> _hideout;
        private readonly CacheFile<List<TarkovDevTraderCacheItem>> _traders;

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public TarkovDevDataService(string? basePath = null, TarkovDevJsonClient? jsonClient = null)
        {
            basePath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wiki_data");
            _cacheDir = Path.Combine(basePath, "cache");
            Directory.CreateDirectory(_cacheDir);

            // Four files, one description each: the file name, the envelope that wraps the
            // collection on disk, and which of its fields the collection is.
            _items = Define(
                "tarkov_dev_items.json",
                (TarkovDevItemsCache c) => c.Items,
                (value, cachedAt, sourceLastModified) => new TarkovDevItemsCache
                {
                    CachedAt = cachedAt,
                    SourceLastModified = sourceLastModified,
                    Items = value
                });

            _quests = Define(
                "tarkov_dev_quests.json",
                (TarkovDevQuestsCache c) => c.Quests,
                (value, cachedAt, sourceLastModified) => new TarkovDevQuestsCache
                {
                    CachedAt = cachedAt,
                    SourceLastModified = sourceLastModified,
                    Quests = value
                });

            _hideout = Define(
                "tarkov_dev_hideout.json",
                (TarkovDevHideoutCache c) => c.Stations,
                (value, cachedAt, sourceLastModified) => new TarkovDevHideoutCache
                {
                    CachedAt = cachedAt,
                    SourceLastModified = sourceLastModified,
                    Stations = value
                });

            _traders = Define(
                "tarkov_dev_traders.json",
                (TarkovDevTradersCache c) => c.Traders,
                (value, cachedAt, sourceLastModified) => new TarkovDevTradersCache
                {
                    CachedAt = cachedAt,
                    SourceLastModified = sourceLastModified,
                    Traders = value
                });

            _ownsJsonClient = jsonClient == null;
            _jsonClient = jsonClient ?? new TarkovDevJsonClient(_cacheDir);
        }

        #region Cache Management

        /// <summary>
        /// One cache file: where it lives, how it is read, and how it is written. The count the
        /// accounting needs comes from the value itself, which is why <typeparamref name="TValue"/>
        /// is a collection: a dictionary and a list both answer <c>Count</c>, and neither can be
        /// counted by the wrong rule.
        /// </summary>
        private sealed class CacheFile<TValue> where TValue : class, System.Collections.ICollection
        {
            public required string Path { get; init; }

            /// <summary>Reads the file, or null when there is none. Throws on a damaged one.</summary>
            public required Func<CancellationToken, Task<TValue?>> LoadAsync { get; init; }

            /// <summary>Writes the file, stamped with upstream's own <c>Last-Modified</c>.</summary>
            public required Func<TValue, DateTime?, CancellationToken, Task> SaveAsync { get; init; }
        }

        /// <summary>
        /// Describes one cache file. The path is closed over rather than looked up later, so a
        /// file cannot be described by one path and written to another, and "cached at is now"
        /// is decided here rather than once per file.
        /// </summary>
        /// <param name="fileName">The cache file's name under <c>wiki_data/cache/</c>.</param>
        /// <param name="read">The collection inside the envelope the file holds.</param>
        /// <param name="wrap">Builds the envelope to write, given the value and the two stamps.</param>
        private CacheFile<TValue> Define<TCache, TValue>(
            string fileName,
            Func<TCache, TValue?> read,
            Func<TValue, DateTime, DateTime?, TCache> wrap)
            where TCache : class
            where TValue : class, System.Collections.ICollection
        {
            var path = Path.Combine(_cacheDir, fileName);
            return new CacheFile<TValue>
            {
                Path = path,
                LoadAsync = async cancellationToken =>
                {
                    var cache = await LoadCacheAsync<TCache>(path, cancellationToken);
                    return cache == null ? null : read(cache);
                },
                SaveAsync = (value, sourceLastModified, cancellationToken) =>
                    WriteCacheAsync(path, wrap(value, DateTime.UtcNow, sourceLastModified), cancellationToken),
            };
        }

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

            info.ItemsCachedAt = TryGetWriteTime(_items.Path);
            info.ItemsCount = TryCount(_items.Path, json =>
                JsonSerializer.Deserialize<TarkovDevItemsCache>(json)?.Items?.Count ?? 0);

            info.QuestsCachedAt = TryGetWriteTime(_quests.Path);
            info.QuestsCount = TryCount(_quests.Path, json =>
                JsonSerializer.Deserialize<TarkovDevQuestsCache>(json)?.Quests?.Count ?? 0);

            info.HideoutCachedAt = TryGetWriteTime(_hideout.Path);
            info.HideoutCount = TryCount(_hideout.Path, json =>
                JsonSerializer.Deserialize<TarkovDevHideoutCache>(json)?.Stations?.Count ?? 0);

            info.TradersCachedAt = TryGetWriteTime(_traders.Path);
            info.TradersCount = TryCount(_traders.Path, json =>
                JsonSerializer.Deserialize<TarkovDevTradersCache>(json)?.Traders?.Count ?? 0);

            return info;
        }

        /// <summary>
        /// When the task cache was last confirmed current: the quests file's last write time,
        /// the same value <see cref="GetCacheInfo"/> reports as
        /// <see cref="TarkovDevCacheInfo.QuestsCachedAt"/>, and null when there is no quests
        /// cache file. See that method's remarks for why the write time is the answer rather
        /// than the <c>cachedAt</c> field inside the file.
        /// <para>
        /// Separate from <see cref="GetCacheInfo"/> because the refresh wants this timestamp and
        /// nothing else, while the counts <see cref="GetCacheInfo"/> gathers alongside it cost a
        /// full read and deserialization of every cache file, the items one being about 16 MB.
        /// </para>
        /// </summary>
        public DateTime? GetQuestsCacheVerifiedAt() => TryGetWriteTime(_quests.Path);

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
        public Task<Dictionary<string, TarkovDevMultiLangItem>?> LoadCachedItemsAsync(
            CancellationToken cancellationToken = default) => _items.LoadAsync(cancellationToken);

        /// <summary>
        /// Cached tasks. A list, not a dictionary keyed by <c>wikiLink</c>: ten wiki titles are
        /// the link of two or three tasks, and the old dictionary silently kept whichever it
        /// saw last. Choosing among them is <see cref="QuestIdentityResolver"/>'s job and it
        /// needs to see all of them.
        /// </summary>
        public Task<List<TarkovDevQuestCacheItem>?> LoadCachedQuestsAsync(
            CancellationToken cancellationToken = default) => _quests.LoadAsync(cancellationToken);

        public Task<List<TarkovDevHideoutStation>?> LoadCachedHideoutAsync(
            CancellationToken cancellationToken = default) => _hideout.LoadAsync(cancellationToken);

        public Task<List<TarkovDevTraderCacheItem>?> LoadCachedTradersAsync(
            CancellationToken cancellationToken = default) => _traders.LoadAsync(cancellationToken);

        /// <summary>
        /// Writes one cache file, through a temporary file so the destination is only ever the
        /// whole old copy or the whole new one. A 16 MB write that dies halfway (cancellation, a
        /// full disk) would otherwise leave a truncated file that reads as a damaged cache, and
        /// the ETag commit that follows a successful write assumes exactly this: either the new
        /// bytes are there, or nothing changed.
        /// </summary>
        private static async Task WriteCacheAsync<T>(string path, T cache, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(cache, WriteOptions);
            var tempPath = path + ".tmp";

            try
            {
                await File.WriteAllTextAsync(tempPath, json, cancellationToken);
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A left-behind temporary file is replaced by the next write.
            }
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
        /// <para>
        /// Order matters within a part too, and <see cref="RunCachePartAsync{TValue}"/> is the one
        /// place it is decided: the fetch's ETags are committed only after its cache file is
        /// written, so a write that fails leaves the next run asking unconditionally rather than
        /// being told 304 about a revision this machine never stored.
        /// </para>
        /// </summary>
        public async Task<TarkovDevCacheResult> CacheAllDataAsync(
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var result = new TarkovDevCacheResult();

            var items = await RunCachePartAsync(_items, result.Items,
                conditional => _jsonClient.FetchItemsAsync(conditional, progress, cancellationToken),
                progress, cancellationToken);

            var traders = await RunCachePartAsync(_traders, result.Traders,
                conditional => _jsonClient.FetchTradersAsync(conditional, progress, cancellationToken),
                progress, cancellationToken);

            await RunCachePartAsync(_quests, result.Quests,
                conditional => _jsonClient.FetchTasksAsync(conditional, progress, cancellationToken),
                progress, cancellationToken);

            await RunCachePartAsync(_hideout, result.Hideout, conditional =>
            {
                if (items == null || traders == null)
                {
                    throw new InvalidOperationException(
                        "Hideout levels name their items and traders by id only, so the items and traders "
                        + "caches must be readable first. Fix those parts and re-run.");
                }

                return _jsonClient.FetchHideoutAsync(items.Values, traders, conditional, progress, cancellationToken);
            }, progress, cancellationToken);

            result.CachedAt = DateTime.Now;
            return result;
        }

        /// <summary>
        /// Runs one cache part end to end: fetch conditionally when there is already a file to
        /// keep, then either re-stamp what upstream reports unchanged or write the new value and
        /// only then commit the fetch's ETags. Records success, a keep, or the failure that left
        /// the old file alone, and returns the part's value so a later part can use it.
        /// <para>
        /// The write-before-commit order lives here once. An ETag committed before the bytes are
        /// on disk tells the next run 304 about a revision this machine never stored, and the
        /// refresh then publishes the old data with every freshness guard green.
        /// </para>
        /// </summary>
        /// <param name="fetch">
        /// Reads the part from upstream. Its argument is whether the request may be conditional,
        /// which it may only when there is a cache file a 304 would leave in place. Returns null
        /// when upstream reported the part unchanged.
        /// </param>
        private async Task<TValue?> RunCachePartAsync<TValue>(
            CacheFile<TValue> file,
            TarkovDevCachePart part,
            Func<bool, Task<TarkovDevFetch<TValue>?>> fetch,
            Action<string>? progress,
            CancellationToken cancellationToken)
            where TValue : class, System.Collections.ICollection
        {
            try
            {
                TValue? value;
                bool kept;
                DateTime? sourceLastModified;

                var fetched = await fetch(File.Exists(file.Path));
                if (fetched == null)
                {
                    MarkVerified(file.Path);
                    value = await file.LoadAsync(cancellationToken);
                    kept = true;
                    sourceLastModified = null;
                }
                else
                {
                    await file.SaveAsync(fetched.Value, fetched.SourceLastModified, cancellationToken);
                    fetched.CommitETags();
                    value = fetched.Value;
                    kept = false;
                    sourceLastModified = fetched.SourceLastModified;
                }

                part.Success = true;
                part.Kept = kept;
                part.Count = value?.Count ?? 0;
                part.CachedAt = TryGetWriteTime(file.Path);
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
                part.Kept = File.Exists(file.Path);
                part.Error = ex.Message;
                part.CachedAt = TryGetWriteTime(file.Path);
                progress?.Invoke($"{part.Name}: failed ({ex.Message})"
                    + (part.Kept ? $"; kept the copy from {part.CachedAt:yyyy-MM-dd HH:mm}" : "; no cache to keep"));
                return null;
            }
        }

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
            if (_ownsJsonClient)
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

        /// <summary>Display name. Nothing is keyed by it.</summary>
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

        /// <summary>
        /// Loyalty gates only; reputation gates the app cannot express are dropped. Never null,
        /// for the same reason as <see cref="FailConditions"/> below.
        /// </summary>
        [JsonPropertyName("traderLevelRequirements")]
        public List<TarkovDevTaskTraderLevel> TraderLevelRequirements
        {
            get => _traderLevelRequirements;
            set => _traderLevelRequirements = value ?? new List<TarkovDevTaskTraderLevel>();
        }

        private List<TarkovDevTaskTraderLevel> _traderLevelRequirements = new();

        /// <summary>
        /// Prerequisite tasks, AND semantics (the API has no OR groups). Never null, for the
        /// same reason as <see cref="FailConditions"/> below.
        /// </summary>
        [JsonPropertyName("taskRequirements")]
        public List<TarkovDevTaskPrerequisite> TaskRequirements
        {
            get => _taskRequirements;
            set => _taskRequirements = value ?? new List<TarkovDevTaskPrerequisite>();
        }

        private List<TarkovDevTaskPrerequisite> _taskRequirements = new();

        /// <summary>
        /// What the game records as failing this task. Read by
        /// <c>RefreshDataService.ExpandExclusiveAlternatives</c> to find the quest whose
        /// completion fails a prerequisite, which is what turns a "complete or failed"
        /// prerequisite into an OR group every build in the field can already read.
        /// <para>
        /// A cache file written before this field existed simply has no <c>failConditions</c>
        /// key, which deserializes to the empty list here rather than throwing. The run then
        /// reports every "complete or failed" prerequisite as un-expanded, by name, instead of
        /// silently publishing a bare AND row; 'Debug > Cache Tarkov Dev Data' refills it. The
        /// setter also absorbs an explicit <c>null</c>, because the derivation enumerates this
        /// list without a null check and a hand-edited cache file is a real thing.
        /// </para>
        /// </summary>
        [JsonPropertyName("failConditions")]
        public List<TarkovDevTaskFailCondition> FailConditions
        {
            get => _failConditions;
            set => _failConditions = value ?? new List<TarkovDevTaskFailCondition>();
        }

        private List<TarkovDevTaskFailCondition> _failConditions = new();
    }

    /// <summary>
    /// One of upstream's own fail conditions on a task: something that makes the game mark the
    /// task failed.
    /// <para>
    /// Only the <c>taskStatus</c> kind names another task, and it is the only kind the pipeline
    /// acts on. The others are carried by <see cref="Type"/> alone (on the 1.1 capture:
    /// traderStanding, shoot, extract, useItem, visit, plantItem) so that a run reporting a
    /// prerequisite it could not expand can say what does fail it rather than only that no quest
    /// does.
    /// </para>
    /// </summary>
    public class TarkovDevTaskFailCondition
    {
        /// <summary>The kind of condition: <c>taskStatus</c>, <c>traderStanding</c>, and so on.</summary>
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        /// <summary>The task this condition is about, on a <c>taskStatus</c> condition only.</summary>
        [JsonPropertyName("taskId")]
        public string? TaskId { get; set; }

        /// <summary>
        /// The states of <see cref="TaskId"/> that fail this task. Every one of the 35
        /// <c>taskStatus</c> fail conditions in the 1.1 capture reads <c>["complete"]</c>.
        /// Never null; see <see cref="TarkovDevQuestCacheItem.FailConditions"/>.
        /// </summary>
        [JsonPropertyName("status")]
        public List<string> Status
        {
            get => _status;
            set => _status = value ?? new List<string>();
        }

        private List<string> _status = new();
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

        /// <summary>
        /// Never null. The pipeline reads <c>Status.Count</c> without a null check
        /// (<c>RefreshDataService.BuildRequirements</c>), and a cache file holding
        /// <c>"status": null</c> deserializes past every check the loader makes.
        /// </summary>
        [JsonPropertyName("status")]
        public List<string> Status
        {
            get => _status;
            set => _status = value ?? new List<string>();
        }

        private List<string> _status = new();
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
