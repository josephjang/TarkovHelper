using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
using TarkovDBEditor.Models;

namespace TarkovDBEditor.Services
{
    /// <summary>
    /// Wiki 데이터를 기반으로 .db 파일의 Items, Quests 테이블을 생성/업데이트하는 서비스
    /// Revision 체크를 통해 변경된 데이터만 업데이트하고 로그를 남김
    /// </summary>
    public class RefreshDataService : IDisposable
    {
        private readonly string _wikiDataDir;
        private readonly string _logDir;
        private readonly string _revisionPath;

        // 트레이더 본명 -> 일반 이름 매핑
        private static readonly Dictionary<string, string> TraderNameAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Pavel Yegorovich Romanenko", "Prapor" },
            { "Elvira Khabibullina", "Therapist" },
            { "Alexander Fyodorovich Kiselyov", "Skier" },
            { "Abramyan Arshavir Sarkisivich", "Ragman" },
            { "Arshavir Sarkisivich", "Ragman" }
        };

        public RefreshDataService(string? basePath = null)
        {
            // Every collaborator is pointed at this instance's own data directory rather than
            // letting each one default to the app base directory. They agree in the shipping
            // app, where basePath is that directory anyway, and only a service that can be
            // pointed somewhere else is testable at all.
            basePath ??= AppDomain.CurrentDomain.BaseDirectory;
            _wikiDataDir = Path.Combine(basePath, "wiki_data");
            _logDir = Path.Combine(basePath, "logs");
            _revisionPath = Path.Combine(_wikiDataDir, "revision.json");

            Directory.CreateDirectory(_wikiDataDir);
            Directory.CreateDirectory(_logDir);
        }

        #region Revision Management

        /// <summary>
        /// 현재 저장된 리비전 정보 로드
        /// </summary>
        public async Task<RevisionInfo> LoadRevisionAsync(CancellationToken cancellationToken = default)
        {
            if (File.Exists(_revisionPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_revisionPath, cancellationToken);
                    return JsonSerializer.Deserialize<RevisionInfo>(json) ?? new RevisionInfo();
                }
                catch
                {
                    return new RevisionInfo();
                }
            }
            return new RevisionInfo();
        }

        /// <summary>
        /// 리비전 정보 저장
        /// </summary>
        public async Task SaveRevisionAsync(RevisionInfo revision, CancellationToken cancellationToken = default)
        {
            var json = JsonSerializer.Serialize(revision, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_revisionPath, json, cancellationToken);
        }

        #endregion

        #region Refresh Data

        /// <summary>
        /// 캐시된 Wiki 데이터로 .db 파일의 Quests, Traders 테이블을 업데이트 (네트워크 요청 없음)
        /// Items는 기존 DB에서 로드하여 사용 (Items 테이블은 변경하지 않음)
        /// </summary>
        public async Task<RefreshResult> RefreshDataFromCacheAsync(
            string databasePath,
            TarkovDevDataService? tarkovDevService = null,
            WikiCacheService? wikiCacheService = null,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var result = new RefreshResult
            {
                StartedAt = DateTime.Now,
                DatabasePath = databasePath
            };

            var logBuilder = new StringBuilder();
            logBuilder.AppendLine($"=== RefreshData (from Cache) Started at {result.StartedAt:yyyy-MM-dd HH:mm:ss} ===");
            logBuilder.AppendLine($"Database: {databasePath}");
            logBuilder.AppendLine();

            try
            {
                // 기존 DB에서 Items 로드 (Items 테이블은 변경하지 않음)
                progress?.Invoke("Loading items from existing database...");
                var existingItems = await LoadItemsFromDatabaseAsync(databasePath, cancellationToken);
                logBuilder.AppendLine($"Items loaded from DB: {existingItems.Count} items");

                // Read before the write transaction opens: these rows are what a renamed quest
                // carries its identity across, so they have to describe the database as it was.
                var previousQuests = await LoadPreviousQuestRowsAsync(databasePath, cancellationToken);
                logBuilder.AppendLine(
                    $"Previous quest rows: {previousQuests.Count} "
                    + $"({previousQuests.Count(q => !string.IsNullOrEmpty(q.BsgId))} with an external ID)");

                // 캐시된 Quests 로드
                progress?.Invoke("Loading cached quests...");
                var questsResult = await LoadQuestsFromCacheAsync(existingItems, previousQuests, progress, cancellationToken);
                logBuilder.AppendLine($"Quests loaded from cache: {questsResult.Quests.Count} quests");
                logBuilder.AppendLine($"Requirements: {questsResult.Requirements.Count}");
                logBuilder.AppendLine($"TraderRequirements: {questsResult.TraderRequirements.Count}");
                logBuilder.AppendLine($"Objectives: {questsResult.Objectives.Count}");
                logBuilder.AppendLine($"OptionalQuests: {questsResult.OptionalQuests.Count}");
                logBuilder.AppendLine($"RequiredItems: {questsResult.RequiredItems.Count}");
                AppendIdentitySummary(logBuilder, questsResult.Identity);

                // Dogtag 아이템 자동 생성 (QuestRequiredItems/Objectives에서 필요한 경우)
                // EnsureDogtagItemsExist는 생성된 아이템을 existingItems에도 추가함
                var dogtagItems = EnsureDogtagItemsExist(existingItems, questsResult, logBuilder);

                // QuestRequiredItems/Objectives의 ItemId를 Dogtag 아이템과 연결
                LinkDogtagItemIds(questsResult, logBuilder);

                // Dogtag 아이템이 있으면 전체 Items 리스트 전달 (기존 아이템 삭제 방지)
                List<DbItem>? itemsToUpdate = dogtagItems.Count > 0 ? existingItems : null;

                // DB 업데이트
                progress?.Invoke("Updating database...");
                await UpdateDatabaseAsync(
                    databasePath,
                    itemsToUpdate, // Dogtag 아이템이 추가된 전체 Items 리스트
                    questsResult.Quests,
                    questsResult.Requirements,
                    questsResult.Objectives,
                    questsResult.OptionalQuests,
                    questsResult.RequiredItems,
                    questsResult.TraderRequirements,
                    logBuilder,
                    progress,
                    cancellationToken);

                result.ItemsUpdated = false;
                result.QuestsUpdated = true;
                result.ItemsCount = existingItems.Count;
                result.QuestsCount = questsResult.Quests.Count;

                // Traders 업데이트 (tarkovDevService가 제공된 경우에만)
                var tradersStats = (inserted: 0, updated: 0, deleted: 0);
                if (tarkovDevService != null)
                {
                    progress?.Invoke("Updating Traders table...");
                    tradersStats = await UpdateTradersFromCacheAsync(
                        databasePath,
                        tarkovDevService,
                        wikiCacheService,
                        progress,
                        cancellationToken);
                    logBuilder.AppendLine($"Traders: {tradersStats.inserted} inserted, {tradersStats.updated} updated, {tradersStats.deleted} deleted");
                }

                result.Success = true;
                result.CompletedAt = DateTime.Now;

                logBuilder.AppendLine();
                logBuilder.AppendLine($"=== RefreshData (from Cache) Completed at {result.CompletedAt:yyyy-MM-dd HH:mm:ss} ===");
                logBuilder.AppendLine($"Duration: {(result.CompletedAt - result.StartedAt).TotalSeconds:F1} seconds");
                logBuilder.AppendLine($"Items: {result.ItemsCount} (not updated, loaded from DB)");
                logBuilder.AppendLine($"Quests Updated: {result.QuestsUpdated} ({result.QuestsCount} quests)");
                if (tarkovDevService != null)
                {
                    logBuilder.AppendLine($"Traders: {tradersStats.inserted + tradersStats.updated} total");
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.CompletedAt = DateTime.Now;

                logBuilder.AppendLine();
                logBuilder.AppendLine($"=== ERROR ===");
                logBuilder.AppendLine($"Message: {ex.Message}");
                logBuilder.AppendLine($"StackTrace: {ex.StackTrace}");
            }

            // 로그 파일 저장
            var logFileName = $"refresh_cache_{result.StartedAt:yyyyMMdd_HHmmss}.log";
            var logPath = Path.Combine(_logDir, logFileName);
            await File.WriteAllTextAsync(logPath, logBuilder.ToString(), cancellationToken);
            result.LogPath = logPath;

            return result;
        }

        /// <summary>
        /// Wiki 데이터를 가져와 .db 파일에 Items, Quests 테이블을 생성/업데이트 (전체 새로고침)
        /// </summary>
        public async Task<RefreshResult> RefreshDataAsync(
            string databasePath,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var result = new RefreshResult
            {
                StartedAt = DateTime.Now,
                DatabasePath = databasePath
            };

            var logBuilder = new StringBuilder();
            logBuilder.AppendLine($"=== RefreshData Started at {result.StartedAt:yyyy-MM-dd HH:mm:ss} ===");
            logBuilder.AppendLine($"Database: {databasePath}");
            logBuilder.AppendLine();

            try
            {
                // 리비전 정보 로드
                var currentRevision = await LoadRevisionAsync(cancellationToken);
                logBuilder.AppendLine($"Current Revision - Items: {currentRevision.ItemsRevision ?? "N/A"}, Quests: {currentRevision.QuestsRevision ?? "N/A"}");

                // Read before anything writes: identity is carried from these rows, for items
                // (so a renamed item keeps the icon file named after its row key) as well as
                // for quests.
                var previousQuests = await LoadPreviousQuestRowsAsync(databasePath, cancellationToken);
                var previousItems = await LoadPreviousItemRowsAsync(databasePath, cancellationToken);
                logBuilder.AppendLine(
                    $"Previous rows: {previousQuests.Count} quests "
                    + $"({previousQuests.Count(q => !string.IsNullOrEmpty(q.BsgId))} with an external ID), "
                    + $"{previousItems.Count} items "
                    + $"({previousItems.Count(i => !string.IsNullOrEmpty(i.BsgId))} with an external ID)");

                // The carry-over guard runs before the crawl, not after it: a run that cannot
                // preserve identity should cost the operator a message, not an hour of network.
                AssertPreviousDatabaseIsBackfilled(previousQuests);

                // Wiki 데이터 수집 (Items)
                progress?.Invoke("Fetching Wiki item categories...");
                var itemsResult = await FetchAndProcessItemsAsync(previousItems, progress, cancellationToken);
                logBuilder.AppendLine($"Items fetched: {itemsResult.Items.Count} items");
                logBuilder.AppendLine($"Icons: {itemsResult.IconsDownloaded} downloaded, {itemsResult.IconsFailed} failed, {itemsResult.IconsCached} cached");

                // 실패한 아이콘 다운로드 로깅
                if (itemsResult.FailedIconDownloads.Count > 0)
                {
                    logBuilder.AppendLine();
                    logBuilder.AppendLine($"=== Failed Icon Downloads ({itemsResult.FailedIconDownloads.Count}) ===");
                    foreach (var (wikiId, (url, error)) in itemsResult.FailedIconDownloads.Take(50)) // 최대 50개만 로깅
                    {
                        logBuilder.AppendLine($"  [{wikiId}] {url}");
                        logBuilder.AppendLine($"    Error: {error}");
                    }
                    if (itemsResult.FailedIconDownloads.Count > 50)
                    {
                        logBuilder.AppendLine($"  ... and {itemsResult.FailedIconDownloads.Count - 50} more");
                    }
                }

                // Wiki 데이터 수집 (Quests)
                progress?.Invoke("Fetching Wiki quests...");
                var questsResult = await FetchAndProcessQuestsAsync(itemsResult.Items, previousQuests, progress, cancellationToken);
                logBuilder.AppendLine($"Quests fetched: {questsResult.Quests.Count} quests");
                AppendIdentitySummary(logBuilder, questsResult.Identity);

                // 새 리비전 생성
                var newRevision = new RevisionInfo
                {
                    ItemsRevision = itemsResult.Revision,
                    QuestsRevision = questsResult.Revision,
                    LastUpdated = DateTime.UtcNow
                };

                // 리비전 비교 (로그용)
                bool itemsChanged = currentRevision.ItemsRevision != newRevision.ItemsRevision;
                bool questsChanged = currentRevision.QuestsRevision != newRevision.QuestsRevision;

                logBuilder.AppendLine();
                logBuilder.AppendLine($"New Revision - Items: {newRevision.ItemsRevision}, Quests: {newRevision.QuestsRevision}");
                logBuilder.AppendLine($"Items Changed: {itemsChanged}, Quests Changed: {questsChanged}");

                // DB는 항상 초기화 및 업데이트 (Items, Quests, QuestRequirements, QuestTraderRequirements, QuestObjectives, OptionalQuests, QuestRequiredItems 테이블)
                progress?.Invoke("Updating database (Items, Quests, QuestRequirements, QuestTraderRequirements, QuestObjectives, OptionalQuests & QuestRequiredItems tables)...");
                await UpdateDatabaseAsync(
                    databasePath,
                    itemsResult.Items,
                    questsResult.Quests,
                    questsResult.Requirements,
                    questsResult.Objectives,
                    questsResult.OptionalQuests,
                    questsResult.RequiredItems,
                    questsResult.TraderRequirements,
                    logBuilder,
                    progress,
                    cancellationToken);

                // Traders were written only by the from-cache path, so a full refresh used to
                // leave the table describing whichever trader list the last from-cache run saw.
                // 1.1 adds a sixteenth trader, which would otherwise never arrive. The quest
                // build above has already refused to continue on an empty trader cache, so
                // there is always something to write by the time this runs.
                progress?.Invoke("Updating Traders table...");
                using (var traderCacheService = new TarkovDevDataService(_wikiDataDir))
                using (var traderIconCache = new WikiCacheService(_wikiDataDir))
                {
                    var traderStats = await UpdateTradersFromCacheAsync(
                        databasePath, traderCacheService, traderIconCache, progress, cancellationToken);
                    logBuilder.AppendLine(
                        $"Traders: {traderStats.inserted} inserted, {traderStats.updated} updated, {traderStats.deleted} deleted");
                }

                result.ItemsUpdated = true;
                result.QuestsUpdated = true;
                result.ItemsCount = itemsResult.Items.Count;
                result.QuestsCount = questsResult.Quests.Count;

                // 리비전 저장
                await SaveRevisionAsync(newRevision, cancellationToken);
                logBuilder.AppendLine();
                logBuilder.AppendLine("Revision info saved.");

                result.Success = true;
                result.CompletedAt = DateTime.Now;

                logBuilder.AppendLine();
                logBuilder.AppendLine($"=== RefreshData Completed at {result.CompletedAt:yyyy-MM-dd HH:mm:ss} ===");
                logBuilder.AppendLine($"Duration: {(result.CompletedAt - result.StartedAt).TotalSeconds:F1} seconds");
                logBuilder.AppendLine($"Items Updated: {result.ItemsUpdated} ({result.ItemsCount} items)");
                logBuilder.AppendLine($"Quests Updated: {result.QuestsUpdated} ({result.QuestsCount} quests)");
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.CompletedAt = DateTime.Now;

                logBuilder.AppendLine();
                logBuilder.AppendLine($"=== ERROR ===");
                logBuilder.AppendLine($"Message: {ex.Message}");
                logBuilder.AppendLine($"StackTrace: {ex.StackTrace}");
            }

            // 로그 파일 저장
            var logFileName = $"refresh_{result.StartedAt:yyyyMMdd_HHmmss}.log";
            var logPath = Path.Combine(_logDir, logFileName);
            await File.WriteAllTextAsync(logPath, logBuilder.ToString(), cancellationToken);
            result.LogPath = logPath;

            return result;
        }

        /// <summary>
        /// Wiki에서 아이템 데이터 수집 및 처리
        /// </summary>
        private async Task<ItemsFetchResult> FetchAndProcessItemsAsync(
            IReadOnlyList<PreviousItemRow> previousItems,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var wikiService = new TarkovWikiDataService();
            using var cacheService = new WikiCacheService(_wikiDataDir);

            // 캐시 로드
            await cacheService.LoadCacheAsync();

            // 제외할 아이템 가져오기
            var excludedItems = await wikiService.GetExcludedItemsAsync(progress);

            // 카테고리 데이터 가져오기
            var (categoryResult, tree, allCategoryDirectItems) = await wikiService.ExportAllCategoryDataAsync(progress);

            // 카테고리 구조 빌드
            var structure = wikiService.BuildCategoryStructure(tree, allCategoryDirectItems);

            // 모든 후보 아이템
            var allCandidateItems = structure.LeafCategories
                .SelectMany(lc => lc.Value.Items)
                .Distinct()
                .ToList();

            // 페이지 캐시 업데이트
            progress?.Invoke("Updating page cache...");
            var cacheUpdateResult = await cacheService.UpdatePageCacheAsync(allCandidateItems, progress);

            // Infobox 없는 페이지 필터링
            var pagesWithoutInfobox = cacheService.GetPagesWithoutInfoboxFromCache(allCandidateItems);

            // 아이템 목록 빌드
            var itemList = wikiService.BuildItemList(structure, tree, excludedItems, pagesWithoutInfobox);

            // tarkov.dev 데이터로 enrichment (캐시 우선)
            progress?.Invoke("Loading tarkov.dev item data (from cache)...");
            using var devService = new TarkovDevDataService(_wikiDataDir);
            var devItems = await devService.LoadCachedItemsAsync(cancellationToken);

            if (devItems == null || devItems.Count == 0)
            {
                // This used to continue with an empty dictionary, which is how the January
                // regeneration published 4014 items with BsgId NULL: hideout requirements join
                // to items through that column, so every one of them stopped resolving.
                throw new InvalidOperationException(
                    "tarkov.dev item cache is empty or missing. Run 'Debug > Cache Tarkov Dev Data' before "
                    + "refreshing; without it every item would be published with no external ID and hideout "
                    + "requirements would show raw identifiers instead of items.");
            }

            progress?.Invoke($"Loaded {devItems.Count} items from the tarkov.dev cache");

            // Identity carry-over runs before the icons are downloaded: an item whose page was
            // renamed keeps its previous row key, and the icon file is named after that key, so
            // resolving identity afterwards would download the icon under a name nothing reads.
            var identity = ItemIdentityResolver.Resolve(
                itemList.Items
                    .Select(i => new WikiItemIdentity { Id = i.Id, Name = i.Name, WikiPageLink = i.WikiPageLink })
                    .ToList(),
                devItems,
                previousItems);

            foreach (var item in itemList.Items)
            {
                if (identity.CarriedIds.TryGetValue(item.Id, out var carriedId))
                    item.Id = carriedId;
            }

            // A carried key can land on a key another page mints for itself: the item version of
            // a reused title. Two rows with one primary key would silently collapse into one on
            // the upsert, so the run stops instead.
            var duplicateItemIds = itemList.Items
                .GroupBy(i => i.Id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToList();
            if (duplicateItemIds.Count > 0)
            {
                throw new InvalidOperationException(
                    "Two items would share one row key after identity carry-over, which would collapse them "
                    + "into a single row: "
                    + string.Join("; ", duplicateItemIds.Take(10).Select(g => $"{g.Key} <- {string.Join(", ", g.Select(i => i.Name))}")));
            }

            if (identity.Renames.Count > 0)
            {
                progress?.Invoke($"{identity.Renames.Count} items kept their row key across a page rename");
            }

            // 아이콘 URL 가져오기
            var itemNames = itemList.Items.Select(i => i.Name).ToList();
            var iconUrls = await cacheService.GetIconUrlsAsync(itemNames, progress);
            foreach (var item in itemList.Items)
            {
                if (iconUrls.TryGetValue(item.Name, out var iconUrl))
                {
                    item.IconUrl = iconUrl;
                }
            }

            // 아이콘 이미지 다운로드 (캐시에 없는 것만)
            progress?.Invoke("Downloading missing icon images...");
            var iconItems = itemList.Items
                .Where(i => !string.IsNullOrEmpty(i.IconUrl))
                .Select(i => (i.Id, i.IconUrl))
                .ToList();
            var downloadResult = await cacheService.DownloadIconsAsync(iconItems, progress, cancellationToken);
            progress?.Invoke($"Icons: {downloadResult.Downloaded} downloaded, {downloadResult.Failed} failed, {downloadResult.AlreadyDownloaded} cached");

            var enrichedItems = new List<DbItem>();
            foreach (var item in itemList.Items)
            {
                var dbItem = new DbItem
                {
                    Id = item.Id,
                    Name = item.Name,
                    WikiPageLink = item.WikiPageLink,
                    IconUrl = item.IconUrl,
                    Category = item.Category,
                    Categories = string.Join("|", item.Categories)
                };

                // tarkov.dev 매칭
                var normalizedLink = NormalizeWikiLink(item.WikiPageLink);
                if (!string.IsNullOrEmpty(normalizedLink) && devItems.TryGetValue(normalizedLink, out var devItem))
                {
                    dbItem.BsgId = devItem.BsgId;
                    dbItem.NameEN = devItem.NameEN;
                    dbItem.NameKO = devItem.NameKO;
                    dbItem.NameJA = devItem.NameJA;
                    dbItem.ShortNameEN = devItem.ShortNameEN;
                    dbItem.ShortNameKO = devItem.ShortNameKO;
                    dbItem.ShortNameJA = devItem.ShortNameJA;
                }
                else
                {
                    dbItem.NameEN = item.Name;
                    dbItem.NameKO = item.Name;
                    dbItem.NameJA = item.Name;
                }

                enrichedItems.Add(dbItem);
            }

            // 실패한 다운로드 정보 가져오기
            var failedDownloads = cacheService.GetAndClearFailedDownloads();

            // 캐시 저장
            await cacheService.SaveCacheAsync();

            // 리비전 생성 (아이템 수 + 최종 수정 시간 해시)
            var revision = $"{enrichedItems.Count}_{DateTime.UtcNow:yyyyMMddHH}";

            return new ItemsFetchResult
            {
                Items = enrichedItems,
                Revision = revision,
                IconsDownloaded = downloadResult.Downloaded,
                IconsFailed = downloadResult.Failed,
                IconsCached = downloadResult.AlreadyDownloaded,
                FailedIconDownloads = failedDownloads
            };
        }

        #region Quest building

        /// <summary>
        /// Writes the parts of a resolve a human reads in the run log: what was renamed, what
        /// was held back, and which pages several game records claimed. The full lists go to
        /// the JSON log the diff report consumes.
        /// </summary>
        private static void AppendIdentitySummary(StringBuilder logBuilder, QuestIdentityResolution? resolution)
        {
            if (resolution == null)
                return;

            logBuilder.AppendLine();
            logBuilder.AppendLine("=== Quest identity ===");
            logBuilder.AppendLine($"Matched to a game record: {resolution.Quests.Count(q => q.Task != null)}");
            logBuilder.AppendLine($"Imported on the wiki's seasonal marker alone: {resolution.WikiOnlyPages.Count}");
            logBuilder.AppendLine($"Identities carried from the previous database: {resolution.Quests.Count(q => q.IdentityCarried)}");
            logBuilder.AppendLine($"Renamed: {resolution.Renames.Count} (of which {resolution.TitleReuses.Count()} gave their old title to another quest)");
            logBuilder.AppendLine($"Pages held back (no game record, not seasonal): {resolution.HeldBackPages.Count}");
            logBuilder.AppendLine($"Game records with no wiki page: {resolution.TasksWithoutPage.Count}");
            logBuilder.AppendLine($"Pages claimed by several game records: {resolution.Collisions.Count}");

            foreach (var reuse in resolution.TitleReuses)
                logBuilder.AppendLine($"  [TITLE REUSE] '{reuse.PreviousName}' -> '{reuse.Title}' (task {reuse.BsgId})");

            foreach (var collision in resolution.Collisions)
            {
                logBuilder.AppendLine(
                    $"  [COLLISION] {collision.Title}: chose {collision.ChosenTaskId} by {collision.Rule} "
                    + $"from {string.Join(", ", collision.CandidateTaskIds)}");
            }

            foreach (var alias in resolution.UnusedAliases)
            {
                logBuilder.AppendLine(
                    $"  [ALIAS UNUSED] '{alias.PageTitle}' no longer needs its override; upstream may have fixed "
                    + $"{alias.UpstreamIssue}. Remove the entry.");
            }
        }

        /// <summary>
        /// Thresholds a refresh refuses to cross. Each one describes a way the pipeline has
        /// failed silently before: a wiki crawl that half arrived, a task cache that was
        /// overwritten with an empty set, or a previous database whose external IDs were gone.
        /// Crossing one is always a source problem, never something to publish.
        /// See docs/decisions/feature-quest-data-1-1-refresh.spec.md, "Pipeline guards".
        /// </summary>
        internal static class RefreshGuards
        {
            /// <summary>
            /// Above this share of previous quests without an external ID, the carry-over
            /// cannot work: every renamed quest would be minted a fresh row key while its page
            /// still matched, so all 91 of them would lose their recorded progress. The
            /// match-rate guard does not catch this, because the pages do still match.
            /// </summary>
            public const double MaxPreviousQuestsWithoutBsgId = 0.10;

            /// <summary>
            /// Above this share of previously published quests losing their game record, the
            /// task set is wrong (an outage serving a partial file, a game mode with fewer
            /// tasks), not the game.
            /// </summary>
            public const double MaxLostMatches = 0.05;

            /// <summary>Above this share of imported quests without a trader, the trader cache is wrong.</summary>
            public const double MaxTradersMissing = 0.05;

            /// <summary>
            /// How far the task cache may lag the wiki crawl before the pair stops describing
            /// one moment in the game.
            /// </summary>
            public static readonly TimeSpan MaxTaskCacheLag = TimeSpan.FromDays(7);
        }

        /// <summary>
        /// Collects the wiki pages, updates the wiki cache from the network, and builds the
        /// quest rows. The crawl is the only difference from the from-cache path.
        /// </summary>
        private async Task<QuestsFetchResult> FetchAndProcessQuestsAsync(
            List<DbItem> items,
            IReadOnlyList<PreviousQuestRow> previousQuests,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var questService = new WikiQuestService(_wikiDataDir);
            await questService.LoadCacheAsync(cancellationToken);

            var questPages = await questService.GetAllQuestPagesAsync(progress, cancellationToken);

            progress?.Invoke("Updating quest cache...");
            await questService.UpdateQuestCacheAsync(questPages, progress, cancellationToken);
            await questService.SaveCacheAsync(cancellationToken);

            // Only the pages the category still lists: a page the crawl kept from an earlier
            // run but the category has since dropped is not part of the game any more.
            var crawled = new HashSet<string>(questPages, StringComparer.Ordinal);
            var cached = questService.GetCachedQuests()
                .Where(kvp => crawled.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

            return await BuildQuestsAsync(cached, items, previousQuests, progress, cancellationToken);
        }

        /// <summary>
        /// Builds the quest rows from the caches on disk, with no network request.
        /// </summary>
        private async Task<QuestsFetchResult> LoadQuestsFromCacheAsync(
            List<DbItem> items,
            IReadOnlyList<PreviousQuestRow> previousQuests,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var questService = new WikiQuestService(_wikiDataDir);
            await questService.LoadCacheAsync(cancellationToken);

            return await BuildQuestsAsync(
                questService.GetCachedQuests(), items, previousQuests, progress, cancellationToken);
        }

        /// <summary>
        /// Turns the two caches and the previous database into the rows a refresh writes.
        /// <para>
        /// The wiki supplies page identity, objective text, required items, location, editions,
        /// prestige and the DSP decode count. The tarkov.dev task set supplies the rules that
        /// decide availability (minimum level, Kappa, faction, prerequisites, per-trader
        /// loyalty) and the external id everything else hangs off. Where a page has no task the
        /// wiki's own parsers fill in, but only for the pages the wiki marks as seasonal; every
        /// other unmatched page is held back.
        /// </para>
        /// </summary>
        private async Task<QuestsFetchResult> BuildQuestsAsync(
            IReadOnlyDictionary<string, CachedQuestInfo> cachedQuests,
            List<DbItem> items,
            IReadOnlyList<PreviousQuestRow> previousQuests,
            Action<string>? progress,
            CancellationToken cancellationToken)
        {
            var result = new QuestsFetchResult();

            var pagesWithContent = cachedQuests.Values.Count(q => !string.IsNullOrEmpty(q.PageContent));
            if (pagesWithContent == 0)
            {
                // This used to return an empty result and report success, which meant a refresh
                // could delete every quest in the database and call it a day.
                throw new InvalidOperationException(
                    "The wiki quest cache holds no page content. Run 'Debug > Export Wiki Quests' first; "
                    + "refreshing from an empty cache would delete every quest in the database.");
            }

            progress?.Invoke($"Found {cachedQuests.Count} cached quest pages ({pagesWithContent} with content)");

            using var devService = new TarkovDevDataService(_wikiDataDir);
            var tasks = await devService.LoadCachedQuestsAsync(cancellationToken);
            if (tasks == null || tasks.Count == 0)
            {
                throw new InvalidOperationException(
                    "tarkov.dev task cache is empty or missing. Run 'Debug > Cache Tarkov Dev Data' before "
                    + "refreshing; without it no quest gets its external ID, level, Kappa flag or prerequisites.");
            }

            var traders = await devService.LoadCachedTradersAsync(cancellationToken);
            if (traders == null || traders.Count == 0)
            {
                throw new InvalidOperationException(
                    "tarkov.dev trader cache is empty or missing. Run 'Debug > Cache Tarkov Dev Data' before "
                    + "refreshing; quests name their trader by id, which only that cache can resolve.");
            }

            var cacheInfo = devService.GetCacheInfo();
            AssertTaskCacheIsCurrent(cacheInfo.QuestsCachedAt, progress);
            AssertPreviousDatabaseIsBackfilled(previousQuests);

            progress?.Invoke(
                $"Loaded {tasks.Count} tasks and {traders.Count} traders from the tarkov.dev cache"
                + (cacheInfo.QuestsCachedAt.HasValue ? $" (verified {cacheInfo.QuestsCachedAt:yyyy-MM-dd HH:mm})" : ""));

            var traderNamesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var trader in traders)
            {
                if (!string.IsNullOrEmpty(trader.Id))
                    traderNamesById[trader.Id] = trader.Name;
            }

            var pages = BuildWikiPages(cachedQuests, progress);
            var resolution = QuestIdentityResolver.Resolve(
                pages, tasks, previousQuests, QuestMatchOverrides.Load());
            result.Identity = resolution;

            AssertMatchRateHeld(previousQuests, resolution, progress);

            progress?.Invoke(
                $"Resolved {resolution.Quests.Count} quests: {resolution.Renames.Count} renamed, "
                + $"{resolution.WikiOnlyPages.Count} seasonal (wiki only), {resolution.HeldBackPages.Count} held back, "
                + $"{resolution.Collisions.Count} pages shared by several records");

            var itemLookup = new ItemLookup(items);
            var questIdByTitle = resolution.Quests.ToDictionary(q => q.Title, q => q.Id, StringComparer.Ordinal);

            foreach (var quest in resolution.Quests)
            {
                cachedQuests.TryGetValue(quest.Title, out var cached);
                result.Quests.Add(BuildQuestRow(quest, cached, traderNamesById));
                result.TraderRequirements.AddRange(BuildTraderRequirements(quest, traderNamesById));
            }

            result.Requirements.AddRange(BuildRequirements(resolution, cachedQuests, questIdByTitle));
            result.Requirements.AddRange(SynthesizeCollectorRequirements(result.Quests, progress));
            // A requirement row's key is the (quest, prerequisite, group) triple, so two rows
            // describing the same pair collide on the primary key and take the whole refresh
            // down with a constraint error rather than a message anyone can act on. The two
            // sources that could produce one are handled above; this keeps a third from being
            // discovered the hard way, mid-regeneration.
            result.Requirements = DeduplicateRequirements(result.Requirements, progress);
            result.PrerequisiteDisagreements = ComputePrerequisiteDisagreements(resolution, cachedQuests, questIdByTitle);
            result.Objectives.AddRange(BuildObjectives(resolution, cachedQuests, itemLookup));
            result.OptionalQuests.AddRange(BuildOptionalQuests(resolution, cachedQuests, questIdByTitle));
            result.RequiredItems.AddRange(BuildRequiredItems(resolution, cachedQuests, itemLookup));

            AssertPublishConstraints(result, progress);

            progress?.Invoke(
                $"Built {result.Quests.Count} quests, {result.Requirements.Count} prerequisites, "
                + $"{result.TraderRequirements.Count} loyalty gates, {result.Objectives.Count} objectives, "
                + $"{result.RequiredItems.Count} required items");

            await WriteRefreshLogAsync(result, cancellationToken);

            result.Revision = $"{result.Quests.Count}_{DateTime.UtcNow:yyyyMMddHH}";
            return result;
        }

        /// <summary>
        /// Turns the cached pages into resolver input, and refuses a crawl whose seasonal
        /// marker has stopped matching: pages that talk about a seasonal mode while none is
        /// recognised means the wording moved upstream, and importing zero seasonal quests
        /// without saying so is exactly the kind of silence this pipeline keeps producing.
        /// </summary>
        private static List<WikiQuestPage> BuildWikiPages(
            IReadOnlyDictionary<string, CachedQuestInfo> cachedQuests,
            Action<string>? progress)
        {
            var pages = new List<WikiQuestPage>();
            var mentionsSeasonal = 0;

            foreach (var (title, cached) in cachedQuests)
            {
                if (string.IsNullOrEmpty(cached.PageContent))
                    continue;

                var isSeasonal = WikiQuestService.ExtractIsSeasonal(cached.PageContent);
                if (!isSeasonal && WikiQuestService.MentionsSeasonalMode(cached.PageContent))
                    mentionsSeasonal++;

                pages.Add(new WikiQuestPage { Title = title, IsSeasonal = isSeasonal });
            }

            var seasonal = pages.Count(p => p.IsSeasonal);
            if (seasonal == 0 && mentionsSeasonal > 0)
            {
                throw new InvalidOperationException(
                    $"{mentionsSeasonal} quest pages mention a seasonal mode in their Requirements section, but none "
                    + "matches the marker ExtractIsSeasonal reads, so every seasonal quest would silently leave the "
                    + "app. The wiki's wording has moved; update ExtractIsSeasonal and its tests.");
            }

            progress?.Invoke($"{pages.Count} quest pages with content, {seasonal} marked seasonal");
            return pages;
        }

        private static void AssertTaskCacheIsCurrent(DateTime? taskCacheVerifiedAt, Action<string>? progress)
        {
            if (!taskCacheVerifiedAt.HasValue)
                return;

            var lag = DateTime.Now - taskCacheVerifiedAt.Value;
            if (lag > RefreshGuards.MaxTaskCacheLag)
            {
                throw new InvalidOperationException(
                    $"The tarkov.dev task cache was last confirmed current {lag.TotalDays:F0} days ago, more than "
                    + $"{RefreshGuards.MaxTaskCacheLag.TotalDays:F0}. The wiki crawl and the game rules would describe "
                    + "different moments in the game. Run 'Debug > Cache Tarkov Dev Data' first.");
            }

            progress?.Invoke($"tarkov.dev task cache last confirmed {lag.TotalHours:F0} hours ago");
        }

        /// <summary>
        /// The guard the whole carry-over rests on. See the class remarks on
        /// <see cref="BsgIdBackfillService"/> for why a database without external IDs cannot be
        /// refreshed safely.
        /// </summary>
        private static void AssertPreviousDatabaseIsBackfilled(IReadOnlyList<PreviousQuestRow> previousQuests)
        {
            if (previousQuests.Count == 0)
                return;

            var missing = previousQuests.Count(q => string.IsNullOrEmpty(q.BsgId));
            var share = (double)missing / previousQuests.Count;
            if (share <= RefreshGuards.MaxPreviousQuestsWithoutBsgId)
                return;

            throw new InvalidOperationException(
                $"{missing} of {previousQuests.Count} quests in the current database have no external ID "
                + $"({share:P0}, over the {RefreshGuards.MaxPreviousQuestsWithoutBsgId:P0} limit). Refreshing now would "
                + "mint a fresh row key for every quest patch 1.1 renamed, detaching the recorded progress of each one "
                + "in every build in the field. Run 'Debug > Backfill external IDs from snapshot...' first.");
        }

        /// <summary>
        /// A published quest losing its game record is normal in a patch that removes quests;
        /// a lot of them losing it at once is an upstream problem.
        /// </summary>
        private static void AssertMatchRateHeld(
            IReadOnlyList<PreviousQuestRow> previousQuests,
            QuestIdentityResolution resolution,
            Action<string>? progress)
        {
            var previouslyMatched = previousQuests.Where(q => !string.IsNullOrEmpty(q.BsgId)).ToList();
            if (previouslyMatched.Count == 0)
                return;

            var carriedBsgIds = new HashSet<string>(
                resolution.Quests.Where(q => q.Task != null).Select(q => q.Task!.Id), StringComparer.OrdinalIgnoreCase);
            var lost = previouslyMatched.Count(q => !carriedBsgIds.Contains(q.BsgId!));
            var share = (double)lost / previouslyMatched.Count;

            if (share > RefreshGuards.MaxLostMatches)
            {
                throw new InvalidOperationException(
                    $"{lost} of {previouslyMatched.Count} published quests ({share:P0}) would lose their game record, "
                    + $"over the {RefreshGuards.MaxLostMatches:P0} limit. A patch removes quests; it does not remove this "
                    + "many at once. Check that the task cache is complete before publishing.");
            }

            progress?.Invoke($"{lost} of {previouslyMatched.Count} published quests lost their game record ({share:P1})");
        }

        /// <summary>
        /// The value vocabularies and NULL rules the fielded build depends on. Each of these is
        /// a way an additive publish could still break a build already installed: an unknown
        /// requirement type locks a quest forever, an unknown faction hides it, and a normalized
        /// name that does not match what the app computes silently orphans recorded progress.
        /// </summary>
        private static void AssertPublishConstraints(QuestsFetchResult result, Action<string>? progress)
        {
            var problems = new List<string>();

            var badTypes = result.Requirements
                .Where(r => r.RequirementType is not ("Complete" or "Accept" or "Fail"))
                .Select(r => r.RequirementType)
                .Distinct()
                .ToList();
            if (badTypes.Count > 0)
            {
                problems.Add(
                    $"RequirementType outside {{Complete, Accept, Fail}}: {string.Join(", ", badTypes)}. "
                    + "The fielded build treats an unknown type as never satisfied, locking the quest forever.");
            }

            var badFactions = result.Quests
                .Where(q => q.Faction != null && q.Faction is not ("Bear" or "Usec"))
                .Select(q => $"{q.Name} ({q.Faction})")
                .ToList();
            if (badFactions.Count > 0)
            {
                problems.Add(
                    $"Faction outside {{NULL, Bear, Usec}}: {string.Join(", ", badFactions.Take(10))}. "
                    + "The fielded build compares the string for equality, so any other value hides the quest.");
            }

            var missingTrader = result.Quests.Where(q => string.IsNullOrEmpty(q.Trader)).ToList();
            if (result.Quests.Count > 0)
            {
                var share = (double)missingTrader.Count / result.Quests.Count;
                if (share > RefreshGuards.MaxTradersMissing)
                {
                    problems.Add(
                        $"{missingTrader.Count} of {result.Quests.Count} quests ({share:P0}) have no Trader, over the "
                        + $"{RefreshGuards.MaxTradersMissing:P0} limit: "
                        + string.Join(", ", missingTrader.Take(10).Select(q => q.Name)));
                }
            }

            var blankNormalized = result.Quests.Where(q => string.IsNullOrEmpty(q.NormalizedName)).ToList();
            if (blankNormalized.Count > 0)
                problems.Add($"NormalizedName is empty on: {string.Join(", ", blankNormalized.Take(10).Select(q => q.Name))}");

            var driftedNormalized = result.Quests
                .Where(q => !string.IsNullOrEmpty(q.NormalizedName))
                .Where(q =>
                {
                    var mintedTitle = WikiQuestIdentity.TitleOf(q.Id);
                    return mintedTitle == null || QuestNormalizedName.SqlForm(mintedTitle) != q.NormalizedName;
                })
                .ToList();
            if (driftedNormalized.Count > 0)
            {
                problems.Add(
                    "NormalizedName does not match the value the app computes from the row key on: "
                    + string.Join(", ", driftedNormalized.Take(10).Select(q => $"{q.Name} ({q.NormalizedName})"))
                    + ". Progress recorded against these quests would not be found.");
            }

            foreach (var duplicate in result.Quests.GroupBy(q => q.Id, StringComparer.Ordinal).Where(g => g.Count() > 1))
                problems.Add($"Two quests share the row key {duplicate.Key}: {string.Join(", ", duplicate.Select(q => q.Name))}");

            foreach (var duplicate in result.Quests
                .GroupBy(q => q.NormalizedName, StringComparer.Ordinal)
                .Where(g => g.Key.Length > 0 && g.Count() > 1))
            {
                problems.Add(
                    $"Two quests share the normalized name '{duplicate.Key}': "
                    + string.Join(", ", duplicate.Select(q => q.Name)));
            }

            if (problems.Count > 0)
            {
                throw new InvalidOperationException(
                    "The refresh would publish data the builds in the field cannot read correctly:\n  - "
                    + string.Join("\n  - ", problems));
            }

            progress?.Invoke("Publish constraints hold (requirement types, factions, traders, normalized names)");
        }

        /// <summary>
        /// Maps one resolved quest onto its database row, source by source. See the per-field
        /// precedence table in the spec.
        /// </summary>
        private static DbQuest BuildQuestRow(
            ResolvedQuest quest,
            CachedQuestInfo? cached,
            IReadOnlyDictionary<string, string> traderNamesById)
        {
            var content = cached?.PageContent ?? "";
            var row = new DbQuest
            {
                Id = quest.Id,
                Name = quest.Title,
                NormalizedName = quest.NormalizedName,
                WikiPageLink = quest.WikiPageLink,
                Location = ExtractLocationFromContent(content) ?? "Any",
                MinScavKarma = cached?.MinScavKarma ?? WikiQuestService.ExtractMinScavKarma(content),
                RequiredEdition = cached?.RequiredEdition ?? WikiQuestService.ExtractRequiredEdition(content),
                ExcludedEdition = cached?.ExcludedEdition ?? WikiQuestService.ExtractExcludedEdition(content),
                RequiredDecodeCount = cached?.RequiredDecodeCount ?? WikiQuestService.ExtractRequiredDecodeCount(content),
                RequiredPrestigeLevel = WikiQuestService.ExtractRequiredPrestigeLevel(content),
            };

            var wikiTrader = NormalizeTraderName(cached?.Trader) ?? ExtractTraderFromContent(content);

            if (quest.Task == null)
            {
                // A seasonal page the API does not carry: everything the game would have told
                // us comes from the wiki's own parsers, as it did before this refresh.
                row.NameEN = quest.Title;
                row.Trader = wikiTrader;
                row.MinLevel = cached?.MinLevel ?? WikiQuestService.ExtractMinLevel(content);
                row.KappaRequired = false;
                row.Faction = cached?.Faction ?? WikiQuestService.ExtractFaction(content);
                return row;
            }

            var task = quest.Task;
            row.BsgId = task.Id;
            row.NameEN = string.IsNullOrEmpty(task.NameEN) ? quest.Title : task.NameEN;
            row.NameKO = task.NameKO;
            row.NameJA = task.NameJA;
            // The API names the giving trader by id; the wiki's "given by" line stands in when
            // the traders cache does not know that id (a trader added since it was filled).
            row.Trader = (task.Trader != null && traderNamesById.TryGetValue(task.Trader, out var traderName)
                ? traderName
                : null) ?? wikiTrader;
            // 0 means "no level requirement" upstream, and no published row has ever held 0;
            // the app's level gate and detail pane both read 0 and NULL the same way.
            row.MinLevel = task.MinPlayerLevel > 0 ? task.MinPlayerLevel : null;
            row.KappaRequired = task.KappaRequired;
            row.Faction = quest.FactionPairShared ? null : MapFaction(task.FactionName, quest.Title);
            return row;
        }

        /// <summary>
        /// "Any" is no restriction at all; anything but the two factions would be a value the
        /// fielded build hides the quest for, so it fails the run instead.
        /// </summary>
        private static string? MapFaction(string? factionName, string questTitle) => factionName switch
        {
            null or "" or "Any" => null,
            "BEAR" => "Bear",
            "USEC" => "Usec",
            _ => throw new InvalidOperationException(
                $"'{questTitle}' has faction '{factionName}', which is not Any, BEAR or USEC. "
                + "The fielded build compares Faction for equality with the player's side, so an unknown value "
                + "would hide the quest from everyone.")
        };

        private static IEnumerable<DbQuestTraderRequirement> BuildTraderRequirements(
            ResolvedQuest quest,
            IReadOnlyDictionary<string, string> traderNamesById)
        {
            if (quest.Task == null)
                yield break;

            foreach (var gate in quest.Task.TraderLevelRequirements)
            {
                if (string.IsNullOrEmpty(gate.TraderId))
                    continue;

                yield return new DbQuestTraderRequirement
                {
                    QuestId = quest.Id,
                    TraderId = gate.TraderId,
                    TraderName = traderNamesById.TryGetValue(gate.TraderId, out var name) ? name : gate.TraderId,
                    RequiredLevel = gate.Level,
                };
            }
        }

        /// <summary>
        /// Prerequisites come from the game data for every matched quest, and from the wiki only
        /// for the seasonal pages the API does not carry.
        /// <para>
        /// The wiki's list is both stale (111 quests where it names chains 1.1 dissolved) and
        /// short (60 where it names fewer than the game does, Sew it Good - Part 4 among them),
        /// so it is no longer consulted for a quest the game describes. The cost is the wiki's
        /// OR groups on 15 quests, which the API has no equivalent for; they collapse to the
        /// game's AND list and the diff report shows each one.
        /// </para>
        /// </summary>
        private static IEnumerable<DbQuestRequirement> BuildRequirements(
            QuestIdentityResolution resolution,
            IReadOnlyDictionary<string, CachedQuestInfo> cachedQuests,
            IReadOnlyDictionary<string, string> questIdByTitle)
        {
            var questIdByBsgId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var quest in resolution.Quests.Where(q => q.Task != null))
                questIdByBsgId[quest.Task!.Id] = quest.Id;

            foreach (var quest in resolution.Quests)
            {
                if (quest.Task != null)
                {
                    // Collector's prerequisite list is the Kappa set, synthesized from the flags
                    // (see SynthesizeCollectorRequirements). The API also gives it five of its
                    // own, and all five are already in that set, so taking both would emit the
                    // same row twice. Matched on the same names the synthesis matches on, so
                    // exactly one of the two owns the list.
                    if (IsCollector(quest.Title) || IsCollector(quest.Task.NameEN))
                        continue;

                    foreach (var prerequisite in quest.Task.TaskRequirements)
                    {
                        // A prerequisite pointing at a quest this refresh did not import (a
                        // removed record, or one held back) has nothing to reference, and the
                        // foreign key would reject the row.
                        if (!questIdByBsgId.TryGetValue(prerequisite.TaskId, out var requiredQuestId))
                            continue;

                        yield return new DbQuestRequirement
                        {
                            QuestId = quest.Id,
                            RequiredQuestId = requiredQuestId,
                            RequirementType = MapRequirementStatuses(prerequisite.Status, quest.Title),
                            // The API has no OR groups, so every row is one AND term. The app
                            // reads a singleton group as AND, which is what the wiki parser's
                            // 1..n numbering also produced.
                            GroupId = 0,
                            DelayMinutes = quest.Task.AvailableDelaySecondsMin > 0
                                ? quest.Task.AvailableDelaySecondsMin / 60
                                : null,
                        };
                    }

                    continue;
                }

                if (!cachedQuests.TryGetValue(quest.Title, out var cached) || string.IsNullOrEmpty(cached.PageContent))
                    continue;

                foreach (var parsed in WikiQuestService.ExtractPreviousQuests(cached.PageContent))
                {
                    if (!TryResolveQuestId(questIdByTitle, parsed.QuestName, out var requiredQuestId))
                        continue;

                    yield return new DbQuestRequirement
                    {
                        QuestId = quest.Id,
                        RequiredQuestId = requiredQuestId,
                        RequirementType = parsed.RequirementType,
                        DelayMinutes = parsed.DelayMinutes,
                        GroupId = parsed.GroupId,
                    };
                }
            }
        }

        /// <summary>
        /// Compares, per matched quest, the prerequisite list the wiki still records against the
        /// one the game reports. Nothing here reaches the database: the game's list is what
        /// ships, and this is the review material for that decision.
        /// <para>
        /// The wiki parser is kept for exactly two jobs now: writing rows for the seasonal
        /// quests the API does not carry, and producing this list. Dropping it entirely would
        /// leave the refresh with no way to notice the game data going wrong.
        /// </para>
        /// </summary>
        private static List<PrerequisiteDisagreement> ComputePrerequisiteDisagreements(
            QuestIdentityResolution resolution,
            IReadOnlyDictionary<string, CachedQuestInfo> cachedQuests,
            IReadOnlyDictionary<string, string> questIdByTitle)
        {
            var nameByQuestId = resolution.Quests.ToDictionary(q => q.Id, q => q.Title, StringComparer.Ordinal);
            var questIdByBsgId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var quest in resolution.Quests.Where(q => q.Task != null))
                questIdByBsgId[quest.Task!.Id] = quest.Id;

            var disagreements = new List<PrerequisiteDisagreement>();

            foreach (var quest in resolution.Quests)
            {
                if (quest.Task == null)
                    continue;
                if (!cachedQuests.TryGetValue(quest.Title, out var cached) || string.IsNullOrEmpty(cached.PageContent))
                    continue;

                // Collector's own page points its |previous field at itself, which is why the
                // Kappa set is synthesized rather than parsed; comparing it here says nothing.
                if (IsCollector(quest.Title))
                    continue;

                var wiki = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parsed in WikiQuestService.ExtractPreviousQuests(cached.PageContent))
                {
                    if (TryResolveQuestId(questIdByTitle, parsed.QuestName, out var requiredQuestId))
                        wiki.Add(requiredQuestId);
                }

                var game = new HashSet<string>(StringComparer.Ordinal);
                foreach (var prerequisite in quest.Task.TaskRequirements)
                {
                    if (questIdByBsgId.TryGetValue(prerequisite.TaskId, out var requiredQuestId))
                        game.Add(requiredQuestId);
                }

                var verdict = (wiki.SetEquals(game), wiki.IsSupersetOf(game), game.IsSupersetOf(wiki)) switch
                {
                    (true, _, _) => "agree",
                    (false, true, _) => "wikiSuperset",
                    (false, false, true) => "taskSuperset",
                    _ => "conflict",
                };

                disagreements.Add(new PrerequisiteDisagreement
                {
                    Quest = quest.Title,
                    Verdict = verdict,
                    Wiki = wiki.Select(id => nameByQuestId.TryGetValue(id, out var n) ? n : id)
                        .OrderBy(n => n, StringComparer.Ordinal).ToList(),
                    Game = game.Select(id => nameByQuestId.TryGetValue(id, out var n) ? n : id)
                        .OrderBy(n => n, StringComparer.Ordinal).ToList(),
                });
            }

            return disagreements;
        }

        /// <summary>
        /// Collapses the statuses that satisfy one prerequisite into the single requirement type
        /// a row can hold.
        /// <para>
        /// Fourteen 1.1 prerequisites name more than one: ten are "active or complete" and four
        /// are "complete or failed". A row carries one type, and its identity is the
        /// (quest, prerequisite, group) triple, so emitting one row per status would collide on
        /// the primary key rather than express an alternative.
        /// </para>
        /// <para>
        /// The most permissive available type wins. "Accept" is satisfied by an active
        /// <em>and</em> by a completed prerequisite (<c>QuestProgressService.IsStatusSatisfied</c>),
        /// so "active or complete" collapses onto it with nothing lost. "Complete or failed" has
        /// no single equivalent and takes Complete, the path a player normally follows; the
        /// alternative is over-locking, which the refresh report lists so the handful of quests
        /// affected are reviewed rather than discovered.
        /// </para>
        /// </summary>
        // Public because it is a rule about the published data, not an implementation detail:
        // the guard tests pin it directly, and a change here changes what every build in the
        // field reads as a prerequisite.
        public static string MapRequirementStatuses(IReadOnlyList<string> statuses, string questTitle)
        {
            // An entry with no status at all means the ordinary "must be completed".
            if (statuses.Count == 0)
                return "Complete";

            var types = statuses.Select(s => MapRequirementStatus(s, questTitle)).ToList();

            if (types.Contains("Accept")) return "Accept";
            if (types.Contains("Complete")) return "Complete";
            return "Fail";
        }

        private static string MapRequirementStatus(string status, string questTitle) => status.ToLowerInvariant() switch
        {
            "complete" => "Complete",
            "active" => "Accept",
            "failed" => "Fail",
            _ => throw new InvalidOperationException(
                $"'{questTitle}' has a prerequisite with status '{status}', which the app has no reading for. "
                + "It treats an unknown requirement type as never satisfied, which would lock the quest forever.")
        };

        /// <summary>
        /// Keeps one row per (quest, prerequisite, group), preferring the most permissive
        /// requirement type among the duplicates for the same reason
        /// <see cref="MapRequirementStatuses"/> does: a quest shown slightly early is a smaller
        /// harm than one locked forever.
        /// </summary>
        private static List<DbQuestRequirement> DeduplicateRequirements(
            List<DbQuestRequirement> requirements,
            Action<string>? progress)
        {
            var kept = new Dictionary<string, DbQuestRequirement>(StringComparer.Ordinal);
            var collapsed = 0;

            foreach (var requirement in requirements)
            {
                var key = requirement.ComputeId();
                if (!kept.TryGetValue(key, out var existing))
                {
                    kept[key] = requirement;
                    continue;
                }

                collapsed++;
                if (Permissiveness(requirement.RequirementType) > Permissiveness(existing.RequirementType))
                    kept[key] = requirement;
            }

            if (collapsed > 0)
                progress?.Invoke($"Collapsed {collapsed} duplicate prerequisite rows onto their most permissive type");

            return kept.Values.ToList();

            static int Permissiveness(string requirementType) => requirementType switch
            {
                "Accept" => 2,
                "Complete" => 1,
                _ => 0,
            };
        }

        /// <summary>
        /// Collector by any of the names the pipeline may know it under. Its prerequisite list is
        /// derived from the Kappa flags rather than parsed or fetched, so both the wiki parser
        /// and the game data skip it.
        /// </summary>
        private static bool IsCollector(string questTitle) =>
            questTitle.Equals("Collector", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Collector's prerequisite list is the Kappa set, computed rather than curated: the
        /// roadmap keeps it derived so the gauge and the Collector page cannot disagree with
        /// the flags.
        /// <para>
        /// This used to run against the database after the write, which meant it could only
        /// insert: a quest that lost its Kappa flag kept its Collector row forever, and the
        /// published data carries one such leftover (Grenadier). Building the rows here puts
        /// them through the same table-global diff as every other requirement, so a quest
        /// leaving the Kappa set leaves Collector's list with it.
        /// </para>
        /// </summary>
        private static IEnumerable<DbQuestRequirement> SynthesizeCollectorRequirements(
            IReadOnlyList<DbQuest> quests,
            Action<string>? progress)
        {
            var collector = quests.FirstOrDefault(q => IsCollector(q.Name) || IsCollector(q.NameEN ?? ""));

            if (collector == null)
            {
                progress?.Invoke("Collector quest not found; skipping its Kappa prerequisites");
                yield break;
            }

            var kappaQuests = quests.Where(q => q.KappaRequired && q.Id != collector.Id).ToList();
            progress?.Invoke($"Collector: {kappaQuests.Count} Kappa prerequisites");

            foreach (var quest in kappaQuests)
            {
                yield return new DbQuestRequirement
                {
                    QuestId = collector.Id,
                    RequiredQuestId = quest.Id,
                    RequirementType = "Complete",
                    GroupId = 0,
                };
            }
        }

        private static IEnumerable<DbQuestObjective> BuildObjectives(
            QuestIdentityResolution resolution,
            IReadOnlyDictionary<string, CachedQuestInfo> cachedQuests,
            ItemLookup itemLookup)
        {
            foreach (var quest in resolution.Quests)
            {
                if (!cachedQuests.TryGetValue(quest.Title, out var cached) || string.IsNullOrEmpty(cached.PageContent))
                    continue;

                foreach (var parsed in WikiQuestService.ExtractObjectives(cached.PageContent))
                {
                    var objective = new DbQuestObjective
                    {
                        QuestId = quest.Id,
                        SortOrder = parsed.SortOrder,
                        ObjectiveType = parsed.Type.ToString(),
                        Description = parsed.Description,
                        TargetType = parsed.TargetType,
                        TargetCount = parsed.TargetCount,
                        ItemId = itemLookup.IdByName(parsed.ItemName),
                        ItemName = parsed.ItemName,
                        RequiresFIR = parsed.RequiresFIR,
                        MapName = parsed.MapName,
                        LocationName = parsed.LocationName,
                        Conditions = parsed.Conditions,
                        DogtagMinLevel = parsed.DogtagMinLevel,
                        DogtagFaction = parsed.DogtagFaction,
                    };
                    objective.Id = objective.ComputeId();
                    yield return objective;
                }
            }
        }

        private static IEnumerable<DbOptionalQuest> BuildOptionalQuests(
            QuestIdentityResolution resolution,
            IReadOnlyDictionary<string, CachedQuestInfo> cachedQuests,
            IReadOnlyDictionary<string, string> questIdByTitle)
        {
            foreach (var quest in resolution.Quests)
            {
                if (!cachedQuests.TryGetValue(quest.Title, out var cached) || string.IsNullOrEmpty(cached.PageContent))
                    continue;

                foreach (var relatedTitle in WikiQuestService.ExtractRelatedQuests(cached.PageContent))
                {
                    if (!TryResolveQuestId(questIdByTitle, relatedTitle, out var alternativeQuestId))
                        continue;
                    if (alternativeQuestId == quest.Id)
                        continue;

                    yield return new DbOptionalQuest
                    {
                        QuestId = quest.Id,
                        AlternativeQuestId = alternativeQuestId,
                    };
                }
            }
        }

        private static IEnumerable<DbQuestRequiredItem> BuildRequiredItems(
            QuestIdentityResolution resolution,
            IReadOnlyDictionary<string, CachedQuestInfo> cachedQuests,
            ItemLookup itemLookup)
        {
            foreach (var quest in resolution.Quests)
            {
                if (!cachedQuests.TryGetValue(quest.Title, out var cached) || string.IsNullOrEmpty(cached.PageContent))
                    continue;

                foreach (var parsed in WikiQuestService.ExtractRequiredItems(cached.PageContent))
                {
                    var (itemId, itemName) = itemLookup.Resolve(parsed.ItemId, parsed.ItemName);

                    var required = new DbQuestRequiredItem
                    {
                        QuestId = quest.Id,
                        ItemId = itemId,
                        ItemName = itemName,
                        Count = parsed.Count,
                        RequiresFIR = parsed.RequiresFIR,
                        RequirementType = parsed.RequirementType,
                        SortOrder = parsed.SortOrder,
                        DogtagMinLevel = parsed.DogtagMinLevel,
                        DogtagFaction = parsed.DogtagFaction,
                    };
                    required.Id = required.ComputeId();
                    yield return required;
                }
            }
        }

        /// <summary>
        /// Looks a quest up by the title a wiki link names, retrying with the "(quest)" suffix
        /// the wiki adds to disambiguate a title it shares with an item or a location.
        /// </summary>
        private static bool TryResolveQuestId(
            IReadOnlyDictionary<string, string> questIdByTitle,
            string title,
            out string questId)
        {
            return questIdByTitle.TryGetValue(title, out questId!)
                || questIdByTitle.TryGetValue($"{title} (quest)", out questId!);
        }

        /// <summary>
        /// Name and external-ID lookups over the item set, so objectives and required items can
        /// name a row in Items. Built once per refresh instead of once per quest.
        /// </summary>
        private sealed class ItemLookup
        {
            private readonly Dictionary<string, string> _idByName = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, (string Id, string Name)> _byBsgId = new(StringComparer.OrdinalIgnoreCase);

            public ItemLookup(IEnumerable<DbItem> items)
            {
                foreach (var item in items)
                {
                    if (!string.IsNullOrEmpty(item.Name))
                        _idByName.TryAdd(item.Name, item.Id);
                    if (!string.IsNullOrEmpty(item.NameEN))
                        _idByName.TryAdd(item.NameEN!, item.Id);
                    if (!string.IsNullOrEmpty(item.BsgId))
                        _byBsgId.TryAdd(item.BsgId!, (item.Id, item.Name));
                }
            }

            public string? IdByName(string? itemName) =>
                !string.IsNullOrEmpty(itemName) && _idByName.TryGetValue(itemName, out var id) ? id : null;

            /// <summary>
            /// Resolves a required item, preferring the wiki's <c>{{itemId}}</c> template (an
            /// external ID) over its display name, and filling a blank name from the match.
            /// </summary>
            public (string? ItemId, string ItemName) Resolve(string? bsgId, string itemName)
            {
                if (!string.IsNullOrEmpty(bsgId) && _byBsgId.TryGetValue(bsgId, out var match))
                    return (match.Id, string.IsNullOrEmpty(itemName) ? match.Name : itemName);

                return (IdByName(itemName), itemName);
            }
        }

        /// <summary>
        /// Writes the machine-readable side of a refresh: what matched, what was held back and
        /// what was renamed. The diff report reads it, and it is the record of a run nobody
        /// watched.
        /// </summary>
        private async Task WriteRefreshLogAsync(QuestsFetchResult result, CancellationToken cancellationToken)
        {
            var resolution = result.Identity;
            if (resolution == null)
                return;

            var logDir = Path.Combine(_wikiDataDir, "logs");
            Directory.CreateDirectory(logDir);
            var path = Path.Combine(logDir, $"refresh_{DateTime.Now:yyyyMMdd_HHmmss}.json");

            var payload = new
            {
                writtenAt = DateTime.UtcNow,
                counts = new
                {
                    quests = result.Quests.Count,
                    matched = resolution.Quests.Count(q => q.Task != null),
                    wikiOnlySeasonal = resolution.WikiOnlyPages.Count,
                    heldBackPages = resolution.HeldBackPages.Count,
                    tasksWithoutPage = resolution.TasksWithoutPage.Count,
                    collisions = resolution.Collisions.Count,
                    carriedIdentities = resolution.Quests.Count(q => q.IdentityCarried),
                    renames = resolution.Renames.Count,
                    titleReuses = resolution.TitleReuses.Count(),
                    kappaQuests = result.Quests.Count(q => q.KappaRequired),
                    prerequisites = result.Requirements.Count,
                    loyaltyGates = result.TraderRequirements.Count,
                    objectives = result.Objectives.Count,
                    requiredItems = result.RequiredItems.Count,
                    prerequisiteConflicts = result.PrerequisiteDisagreements.Count(d => d.Verdict != "agree"),
                },
                prerequisiteDisagreements = result.PrerequisiteDisagreements.Where(d => d.Verdict != "agree"),
                renames = resolution.Renames,
                titleReuses = resolution.TitleReuses,
                heldBackPages = resolution.HeldBackPages,
                wikiOnlySeasonal = resolution.WikiOnlyPages,
                tasksWithoutPage = resolution.TasksWithoutPage,
                collisions = resolution.Collisions.Select(c => new
                {
                    c.Title,
                    c.CandidateTaskIds,
                    c.ChosenTaskId,
                    Rule = c.Rule.ToString(),
                }),
                aliasesUsed = resolution.AliasesUsed,
                unusedAliases = resolution.UnusedAliases.Select(a => new { a.PageTitle, a.TaskId, a.UpstreamIssue }),
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            await File.WriteAllTextAsync(path, json, cancellationToken);
        }

        /// <summary>
        /// Reads the rows the refresh is starting from, before the write transaction opens.
        /// They are what identity is carried across: the row key, its normalized name where the
        /// column exists, and the external ID that recognises a renamed quest.
        /// </summary>
        internal static async Task<List<PreviousQuestRow>> LoadPreviousQuestRowsAsync(
            string databasePath,
            CancellationToken cancellationToken = default)
        {
            var rows = new List<PreviousQuestRow>();
            if (!File.Exists(databasePath))
                return rows;

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            if (!await TableExistsAsync(connection, "Quests", cancellationToken))
                return rows;

            var hasNormalizedName = await ColumnExistsAsync(connection, "Quests", "NormalizedName", cancellationToken);
            var sql = hasNormalizedName
                ? "SELECT Id, Name, BsgId, NormalizedName FROM Quests"
                : "SELECT Id, Name, BsgId, NULL FROM Quests";

            await using var cmd = new SqliteCommand(sql, connection);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new PreviousQuestRow
                {
                    Id = reader.GetString(0),
                    Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    BsgId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    NormalizedName = reader.IsDBNull(3) ? null : reader.GetString(3),
                });
            }

            return rows;
        }

        /// <summary>Item rows the refresh is starting from, for the item identity carry-over.</summary>
        internal static async Task<List<PreviousItemRow>> LoadPreviousItemRowsAsync(
            string databasePath,
            CancellationToken cancellationToken = default)
        {
            var rows = new List<PreviousItemRow>();
            if (!File.Exists(databasePath))
                return rows;

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            if (!await TableExistsAsync(connection, "Items", cancellationToken))
                return rows;

            await using var cmd = new SqliteCommand("SELECT Id, Name, BsgId FROM Items", connection);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new PreviousItemRow
                {
                    Id = reader.GetString(0),
                    Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    BsgId = reader.IsDBNull(2) ? null : reader.GetString(2),
                });
            }

            return rows;
        }

        private static async Task<bool> TableExistsAsync(
            SqliteConnection connection, string tableName, CancellationToken cancellationToken)
        {
            await using var cmd = new SqliteCommand(
                "SELECT name FROM sqlite_master WHERE type='table' AND name=@Name", connection);
            cmd.Parameters.AddWithValue("@Name", tableName);
            return await cmd.ExecuteScalarAsync(cancellationToken) != null;
        }

        private static async Task<bool> ColumnExistsAsync(
            SqliteConnection connection, string tableName, string columnName, CancellationToken cancellationToken)
        {
            await using var cmd = new SqliteCommand($"PRAGMA table_info({tableName})", connection);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        #endregion
        /// <summary>
        /// Dogtag 아이템이 필요하면 자동 생성
        /// QuestRequiredItems/QuestObjectives에서 DogtagFaction이 설정된 항목이 있으면
        /// BEAR Dogtag, USEC Dogtag를 Items 테이블에 추가
        /// 아이콘은 기존 Dogtag 아이콘을 좌/우로 잘라서 생성
        /// </summary>
        private List<DbItem> EnsureDogtagItemsExist(
            List<DbItem> existingItems,
            QuestsFetchResult questsResult,
            StringBuilder? logBuilder)
        {
            var result = new List<DbItem>();
            var existingItemNames = new HashSet<string>(existingItems.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
            var existingItemIds = new HashSet<string>(existingItems.Select(i => i.Id));

            // QuestRequiredItems에서 필요한 Dogtag 진영 수집
            var neededFactions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in questsResult.RequiredItems)
            {
                if (!string.IsNullOrEmpty(item.DogtagFaction))
                {
                    neededFactions.Add(item.DogtagFaction.ToUpper());
                }
            }

            // QuestObjectives에서 필요한 Dogtag 진영 수집
            foreach (var obj in questsResult.Objectives)
            {
                if (!string.IsNullOrEmpty(obj.DogtagFaction))
                {
                    neededFactions.Add(obj.DogtagFaction.ToUpper());
                }
            }

            if (neededFactions.Count == 0)
                return result;

            // 기존 아이템에서 원본 Dogtag 아이콘 찾기 (Name이 "Dogtag"인 항목)
            var baseDogtagItem = existingItems.FirstOrDefault(i =>
                i.Name.Equals("Dogtag", StringComparison.OrdinalIgnoreCase));

            // 아이콘 디렉토리
            var iconDir = Path.Combine(_wikiDataDir, "icons");
            Directory.CreateDirectory(iconDir);

            // 원본 Dogtag 아이콘 파일 찾기 (Items.Id로 검색)
            string? baseDogtagIconPath = null;
            if (baseDogtagItem != null && !string.IsNullOrEmpty(baseDogtagItem.Id))
            {
                var extensions = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
                foreach (var ext in extensions)
                {
                    var path = Path.Combine(iconDir, $"{baseDogtagItem.Id}{ext}");
                    if (File.Exists(path))
                    {
                        baseDogtagIconPath = path;
                        logBuilder?.AppendLine($"  [DOGTAG ICON] Found base icon: {baseDogtagItem.Id}{ext}");
                        break;
                    }
                }
                if (baseDogtagIconPath == null)
                {
                    logBuilder?.AppendLine($"  [DOGTAG ICON] Base icon not found for Id: {baseDogtagItem.Id}");
                }
            }
            else
            {
                logBuilder?.AppendLine("  [DOGTAG ICON] No 'Dogtag' item found in Items table");
            }

            // 진영별 아이콘 생성 (좌/우 자르기)
            var factionIcons = CreateDogtagFactionIcons(baseDogtagIconPath, iconDir, neededFactions, logBuilder);

            // 필요한 Dogtag 아이템 생성
            foreach (var faction in neededFactions)
            {
                var dogtagName = $"{faction} Dogtag";
                var dogtagId = $"dogtag-{faction.ToLower()}";

                // 이미 존재하는지 확인 (이름 또는 ID로)
                if (existingItemNames.Contains(dogtagName) || existingItemIds.Contains(dogtagId))
                {
                    // 기존 아이템 업데이트 (IsDogtagItem, DogtagFaction 설정)
                    var existing = existingItems.FirstOrDefault(i =>
                        i.Name.Equals(dogtagName, StringComparison.OrdinalIgnoreCase) ||
                        i.Id == dogtagId);
                    if (existing != null)
                    {
                        bool updated = false;
                        if (!existing.IsDogtagItem || string.IsNullOrEmpty(existing.DogtagFaction))
                        {
                            existing.IsDogtagItem = true;
                            existing.DogtagFaction = faction;
                            updated = true;
                        }
                        // 아이콘 경로 업데이트
                        if (factionIcons.TryGetValue(faction, out var iconPath) && existing.IconUrl != iconPath)
                        {
                            existing.IconUrl = iconPath;
                            updated = true;
                        }
                        if (updated)
                        {
                            result.Add(existing);
                            logBuilder?.AppendLine($"  [DOGTAG UPDATE] Updated existing: {dogtagName}");
                        }
                    }
                    continue;
                }

                // 진영별 아이콘 URL
                factionIcons.TryGetValue(faction, out var factionIconUrl);

                // 새 Dogtag 아이템 생성
                var newDogtag = new DbItem
                {
                    Id = dogtagId,
                    Name = dogtagName,
                    NameEN = dogtagName,
                    NameKO = faction == "BEAR" ? "BEAR 인식표" : "USEC 인식표",
                    NameJA = faction == "BEAR" ? "BEAR ドッグタグ" : "USEC ドッグタグ",
                    ShortNameEN = $"{faction} Tag",
                    ShortNameKO = $"{faction} 태그",
                    ShortNameJA = $"{faction} タグ",
                    WikiPageLink = "https://escapefromtarkov.fandom.com/wiki/Dogtag",
                    IconUrl = factionIconUrl ?? baseDogtagItem?.IconUrl,
                    Category = "Dogtag",
                    Categories = "[\"Dogtag\"]",
                    IsDogtagItem = true,
                    DogtagFaction = faction
                };

                result.Add(newDogtag);
                existingItems.Add(newDogtag); // 중복 생성 방지
                existingItemNames.Add(dogtagName);
                existingItemIds.Add(dogtagId);

                logBuilder?.AppendLine($"  [DOGTAG CREATE] Created new dogtag item: {dogtagName} (Id: {dogtagId})");
            }

            if (result.Count > 0)
            {
                logBuilder?.AppendLine($"Dogtag items processed: {result.Count}");
            }

            return result;
        }

        /// <summary>
        /// 원본 Dogtag 아이콘을 좌/우로 잘라서 진영별 아이콘 생성
        /// BEAR: 좌측 절반, USEC: 우측 절반
        /// </summary>
        private Dictionary<string, string> CreateDogtagFactionIcons(
            string? baseDogtagIconPath,
            string iconDir,
            HashSet<string> neededFactions,
            StringBuilder? logBuilder)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(baseDogtagIconPath) || !File.Exists(baseDogtagIconPath))
            {
                logBuilder?.AppendLine("  [DOGTAG ICON] Base dogtag icon not found, skipping icon generation");
                return result;
            }

            try
            {
                // WPF BitmapImage로 원본 이미지 로드
                var originalImage = new BitmapImage();
                originalImage.BeginInit();
                originalImage.UriSource = new Uri(baseDogtagIconPath, UriKind.Absolute);
                originalImage.CacheOption = BitmapCacheOption.OnLoad;
                originalImage.EndInit();
                originalImage.Freeze();

                int fullWidth = originalImage.PixelWidth;
                int halfWidth = fullWidth / 2;
                int height = originalImage.PixelHeight;

                logBuilder?.AppendLine($"  [DOGTAG ICON] Original image size: {fullWidth}x{height}");

                foreach (var faction in neededFactions)
                {
                    var iconFileName = $"dogtag-{faction.ToLower()}.png";
                    var iconPath = Path.Combine(iconDir, iconFileName);

                    // 이미 존재하면 스킵
                    if (File.Exists(iconPath))
                    {
                        result[faction] = iconPath;
                        logBuilder?.AppendLine($"  [DOGTAG ICON] {faction} icon already exists: {iconFileName}");
                        continue;
                    }

                    // BEAR: 좌측 절반 (x=0), USEC: 우측 절반 (x=halfWidth)
                    int srcX = faction.Equals("BEAR", StringComparison.OrdinalIgnoreCase) ? 0 : halfWidth;

                    // CroppedBitmap으로 이미지 자르기
                    var croppedBitmap = new CroppedBitmap(originalImage, new Int32Rect(srcX, 0, halfWidth, height));
                    croppedBitmap.Freeze();

                    // PNG로 저장
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(croppedBitmap));

                    using (var fileStream = new FileStream(iconPath, FileMode.Create))
                    {
                        encoder.Save(fileStream);
                    }

                    result[faction] = iconPath;
                    logBuilder?.AppendLine($"  [DOGTAG ICON] Created {faction} icon: {iconFileName}");
                }
            }
            catch (Exception ex)
            {
                logBuilder?.AppendLine($"  [DOGTAG ICON ERROR] Failed to create faction icons: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// QuestRequiredItems/QuestObjectives에서 DogtagFaction이 설정된 항목의 ItemId를 연결
        /// </summary>
        private void LinkDogtagItemIds(QuestsFetchResult questsResult, StringBuilder? logBuilder)
        {
            int linkedCount = 0;

            // QuestRequiredItems의 ItemId 연결
            foreach (var item in questsResult.RequiredItems)
            {
                if (!string.IsNullOrEmpty(item.DogtagFaction) && string.IsNullOrEmpty(item.ItemId))
                {
                    item.ItemId = $"dogtag-{item.DogtagFaction.ToLower()}";
                    linkedCount++;
                }
            }

            // QuestObjectives의 ItemId 연결
            foreach (var obj in questsResult.Objectives)
            {
                if (!string.IsNullOrEmpty(obj.DogtagFaction) && string.IsNullOrEmpty(obj.ItemId))
                {
                    obj.ItemId = $"dogtag-{obj.DogtagFaction.ToLower()}";
                    linkedCount++;
                }
            }

            if (linkedCount > 0)
            {
                logBuilder?.AppendLine($"Linked {linkedCount} dogtag item references");
            }
        }

        /// <summary>
        /// 기존 DB에서 Items 데이터 로드 (아이템 이름 → ID 매핑용)
        /// </summary>
        private async Task<List<DbItem>> LoadItemsFromDatabaseAsync(
            string databasePath,
            CancellationToken cancellationToken = default)
        {
            var items = new List<DbItem>();

            if (!File.Exists(databasePath))
            {
                return items;
            }

            var connectionString = $"Data Source={databasePath}";
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Items 테이블 존재 여부 확인
            await using var checkCmd = connection.CreateCommand();
            checkCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Items'";
            var tableExists = await checkCmd.ExecuteScalarAsync(cancellationToken);
            if (tableExists == null)
            {
                return items;
            }

            // Dogtag 컬럼 마이그레이션 (기존 DB 호환성)
            await MigrateItemsDogtagColumnsAsync(connection, cancellationToken);

            // Items 로드
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT Id, BsgId, Name, NameEN, NameKO, NameJA,
                       ShortNameEN, ShortNameKO, ShortNameJA,
                       WikiPageLink, IconUrl, Category, Categories,
                       IsDogtagItem, DogtagFaction
                FROM Items";

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new DbItem
                {
                    Id = reader.GetString(0),
                    BsgId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Name = reader.GetString(2),
                    NameEN = reader.IsDBNull(3) ? null : reader.GetString(3),
                    NameKO = reader.IsDBNull(4) ? null : reader.GetString(4),
                    NameJA = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ShortNameEN = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ShortNameKO = reader.IsDBNull(7) ? null : reader.GetString(7),
                    ShortNameJA = reader.IsDBNull(8) ? null : reader.GetString(8),
                    WikiPageLink = reader.IsDBNull(9) ? null : reader.GetString(9),
                    IconUrl = reader.IsDBNull(10) ? null : reader.GetString(10),
                    Category = reader.IsDBNull(11) ? null : reader.GetString(11),
                    Categories = reader.IsDBNull(12) ? null : reader.GetString(12),
                    IsDogtagItem = !reader.IsDBNull(13) && reader.GetInt32(13) != 0,
                    DogtagFaction = reader.IsDBNull(14) ? null : reader.GetString(14)
                });
            }

            return items;
        }

        /// <summary>
        /// Items 테이블에 Dogtag 관련 컬럼이 없으면 추가
        /// </summary>
        private async Task MigrateItemsDogtagColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            // 기존 컬럼 확인
            var existingColumns = new HashSet<string>();
            await using (var checkCmd = connection.CreateCommand())
            {
                checkCmd.CommandText = "PRAGMA table_info(Items)";
                await using var reader = await checkCmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            var columnsToAdd = new Dictionary<string, string>
            {
                { "IsDogtagItem", "INTEGER NOT NULL DEFAULT 0" },
                { "DogtagFaction", "TEXT" }
            };

            foreach (var (columnName, columnType) in columnsToAdd)
            {
                if (!existingColumns.Contains(columnName))
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = $"ALTER TABLE Items ADD COLUMN {columnName} {columnType}";
                    await cmd.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }

        /// <summary>
        /// 데이터베이스 업데이트
        /// </summary>
        private async Task UpdateDatabaseAsync(
            string databasePath,
            List<DbItem>? items,
            List<DbQuest>? quests,
            List<DbQuestRequirement>? questRequirements,
            List<DbQuestObjective>? questObjectives,
            List<DbOptionalQuest>? optionalQuests = null,
            List<DbQuestRequiredItem>? requiredItems = null,
            List<DbQuestTraderRequirement>? questTraderRequirements = null,
            StringBuilder? logBuilder = null,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            using var transaction = connection.BeginTransaction();

            try
            {
                // _schema_meta 테이블 확인/생성
                await EnsureSchemaMetaTableAsync(connection, transaction);

                // Items 테이블 업데이트
                if (items != null && items.Count > 0)
                {
                    progress?.Invoke($"Updating Items table ({items.Count} items)...");
                    logBuilder?.AppendLine();
                    logBuilder?.AppendLine($"=== Items Table Update ===");

                    await CreateItemsTableIfNotExistsAsync(connection, transaction);
                    await RegisterItemsSchemaAsync(connection, transaction);
                    var itemStats = await UpsertItemsAsync(connection, transaction, items, logBuilder);

                    logBuilder?.AppendLine($"Inserted: {itemStats.Inserted}, Updated: {itemStats.Updated}, Deleted: {itemStats.Deleted}");
                }

                // Quests 테이블 업데이트
                if (quests != null && quests.Count > 0)
                {
                    progress?.Invoke($"Updating Quests table ({quests.Count} quests)...");
                    logBuilder?.AppendLine();
                    logBuilder?.AppendLine($"=== Quests Table Update ===");

                    await CreateQuestsTableIfNotExistsAsync(connection, transaction);
                    await RegisterQuestsSchemaAsync(connection, transaction);
                    var questStats = await UpsertQuestsAsync(connection, transaction, quests, logBuilder);

                    logBuilder?.AppendLine($"Inserted: {questStats.Inserted}, Updated: {questStats.Updated}, Deleted: {questStats.Deleted}");
                }

                // QuestRequirements 테이블 업데이트
                if (questRequirements != null && questRequirements.Count > 0)
                {
                    progress?.Invoke($"Updating QuestRequirements table ({questRequirements.Count} requirements)...");
                    logBuilder?.AppendLine();
                    logBuilder?.AppendLine($"=== QuestRequirements Table Update ===");

                    await CreateQuestRequirementsTableIfNotExistsAsync(connection, transaction);
                    await RegisterQuestRequirementsSchemaAsync(connection, transaction);
                    var reqStats = await UpsertQuestRequirementsAsync(connection, transaction, questRequirements, logBuilder);

                    logBuilder?.AppendLine($"Inserted: {reqStats.Inserted}, Updated: {reqStats.Updated}, Deleted: {reqStats.Deleted}");
                }

                // QuestTraderRequirements 테이블 업데이트.
                // An empty list is skipped, like Quests, Requirements and Objectives: a parse
                // or fetch that produced nothing must not empty a table that describes 110
                // quests. Rows that individually disappear are still deleted by the diff inside
                // the upsert.
                if (questTraderRequirements is { Count: > 0 })
                {
                    progress?.Invoke($"Updating QuestTraderRequirements table ({questTraderRequirements.Count} loyalty gates)...");
                    logBuilder?.AppendLine();
                    logBuilder?.AppendLine($"=== QuestTraderRequirements Table Update ===");

                    await CreateQuestTraderRequirementsTableIfNotExistsAsync(connection, transaction);
                    await RegisterQuestTraderRequirementsSchemaAsync(connection, transaction);
                    var traderReqStats = await UpsertQuestTraderRequirementsAsync(connection, transaction, questTraderRequirements, logBuilder);

                    logBuilder?.AppendLine($"Inserted: {traderReqStats.Inserted}, Updated: {traderReqStats.Updated}, Deleted: {traderReqStats.Deleted}");
                }

                // QuestObjectives 테이블 업데이트
                if (questObjectives != null && questObjectives.Count > 0)
                {
                    progress?.Invoke($"Updating QuestObjectives table ({questObjectives.Count} objectives)...");
                    logBuilder?.AppendLine();
                    logBuilder?.AppendLine($"=== QuestObjectives Table Update ===");

                    await CreateQuestObjectivesTableIfNotExistsAsync(connection, transaction);
                    await RegisterQuestObjectivesSchemaAsync(connection, transaction);
                    var objStats = await UpsertQuestObjectivesAsync(connection, transaction, questObjectives, logBuilder);

                    logBuilder?.AppendLine($"Inserted: {objStats.Inserted}, Updated: {objStats.Updated}, Deleted: {objStats.Deleted}");
                }

                // OptionalQuests 테이블 업데이트.
                // Skips an empty list for the same reason the other child tables do: a parse
                // that returned nothing is a parse failure far more often than it is a game
                // that has no alternative quests left.
                if (optionalQuests is { Count: > 0 })
                {
                    progress?.Invoke($"Updating OptionalQuests table ({optionalQuests.Count} optional quests)...");
                    logBuilder?.AppendLine();
                    logBuilder?.AppendLine($"=== OptionalQuests Table Update ===");

                    await CreateOptionalQuestsTableIfNotExistsAsync(connection, transaction);
                    await RegisterOptionalQuestsSchemaAsync(connection, transaction);
                    var optStats = await UpsertOptionalQuestsAsync(connection, transaction, optionalQuests, logBuilder);

                    logBuilder?.AppendLine($"Inserted: {optStats.Inserted}, Updated: {optStats.Updated}, Deleted: {optStats.Deleted}");
                }

                // QuestRequiredItems 테이블 업데이트 (빈 리스트는 건너뜀, OptionalQuests와 동일한 이유)
                if (requiredItems is { Count: > 0 })
                {
                    progress?.Invoke($"Updating QuestRequiredItems table ({requiredItems.Count} required items)...");
                    logBuilder?.AppendLine();
                    logBuilder?.AppendLine($"=== QuestRequiredItems Table Update ===");

                    await CreateQuestRequiredItemsTableIfNotExistsAsync(connection, transaction);
                    await RegisterQuestRequiredItemsSchemaAsync(connection, transaction);
                    var itemStats = await UpsertQuestRequiredItemsAsync(connection, transaction, requiredItems, logBuilder);

                    logBuilder?.AppendLine($"Inserted: {itemStats.Inserted}, Updated: {itemStats.Updated}, Deleted: {itemStats.Deleted}");
                }

                transaction.Commit();
                progress?.Invoke("Database update completed.");
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private async Task EnsureSchemaMetaTableAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var sql = @"
                CREATE TABLE IF NOT EXISTS _schema_meta (
                    TableName TEXT PRIMARY KEY,
                    DisplayName TEXT,
                    SchemaJson TEXT NOT NULL,
                    CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP,
                    UpdatedAt TEXT DEFAULT CURRENT_TIMESTAMP
                )";

            using var cmd = new SqliteCommand(sql, connection, transaction);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task RegisterItemsSchemaAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var columns = new List<ColumnSchema>
            {
                new() { Name = "Id", DisplayName = "ID", Type = ColumnType.Text, IsPrimaryKey = true, IsRequired = true, SortOrder = 0 },
                new() { Name = "BsgId", DisplayName = "BSG ID", Type = ColumnType.Text, SortOrder = 1 },
                new() { Name = "Name", DisplayName = "Name", Type = ColumnType.Text, IsRequired = true, SortOrder = 2 },
                new() { Name = "NameEN", DisplayName = "Name (EN)", Type = ColumnType.Text, SortOrder = 3 },
                new() { Name = "NameKO", DisplayName = "Name (KO)", Type = ColumnType.Text, SortOrder = 4 },
                new() { Name = "NameJA", DisplayName = "Name (JA)", Type = ColumnType.Text, SortOrder = 5 },
                new() { Name = "ShortNameEN", DisplayName = "Short (EN)", Type = ColumnType.Text, SortOrder = 6 },
                new() { Name = "ShortNameKO", DisplayName = "Short (KO)", Type = ColumnType.Text, SortOrder = 7 },
                new() { Name = "ShortNameJA", DisplayName = "Short (JA)", Type = ColumnType.Text, SortOrder = 8 },
                new() { Name = "WikiPageLink", DisplayName = "Wiki Link", Type = ColumnType.Text, SortOrder = 9 },
                new() { Name = "IconUrl", DisplayName = "Icon URL", Type = ColumnType.Text, SortOrder = 10 },
                new() { Name = "Category", DisplayName = "Category", Type = ColumnType.Text, SortOrder = 11 },
                new() { Name = "Categories", DisplayName = "Categories", Type = ColumnType.Text, SortOrder = 12 },
                new() { Name = "IsDogtagItem", DisplayName = "Is Dogtag", Type = ColumnType.Boolean, IsRequired = true, SortOrder = 13 },
                new() { Name = "DogtagFaction", DisplayName = "Dogtag Faction", Type = ColumnType.Text, SortOrder = 14 },
                new() { Name = "UpdatedAt", DisplayName = "Updated At", Type = ColumnType.DateTime, SortOrder = 15 }
            };

            var schemaJson = JsonSerializer.Serialize(columns);
            await UpsertSchemaMetaAsync(connection, transaction, "Items", "Items", schemaJson);
        }

        private async Task RegisterQuestsSchemaAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var columns = new List<ColumnSchema>
            {
                new() { Name = "Id", DisplayName = "ID", Type = ColumnType.Text, IsPrimaryKey = true, IsRequired = true, SortOrder = 0 },
                new() { Name = "BsgId", DisplayName = "BSG ID", Type = ColumnType.Text, SortOrder = 1 },
                new() { Name = "Name", DisplayName = "Name", Type = ColumnType.Text, IsRequired = true, SortOrder = 2 },
                new() { Name = "NameEN", DisplayName = "Name (EN)", Type = ColumnType.Text, SortOrder = 3 },
                new() { Name = "NameKO", DisplayName = "Name (KO)", Type = ColumnType.Text, SortOrder = 4 },
                new() { Name = "NameJA", DisplayName = "Name (JA)", Type = ColumnType.Text, SortOrder = 5 },
                new() { Name = "WikiPageLink", DisplayName = "Wiki Link", Type = ColumnType.Text, SortOrder = 6 },
                new() { Name = "Trader", DisplayName = "Trader", Type = ColumnType.Text, SortOrder = 7 },
                new() { Name = "Location", DisplayName = "Location", Type = ColumnType.Text, SortOrder = 8 },
                new() { Name = "MinLevel", DisplayName = "Min Level", Type = ColumnType.Integer, SortOrder = 9 },
                new() { Name = "MinScavKarma", DisplayName = "Min Scav Karma", Type = ColumnType.Integer, SortOrder = 10 },
                new() { Name = "KappaRequired", DisplayName = "Kappa Required", Type = ColumnType.Boolean, SortOrder = 11 },
                new() { Name = "Faction", DisplayName = "Faction", Type = ColumnType.Text, SortOrder = 12 },
                new() { Name = "RequiredEdition", DisplayName = "Required Edition", Type = ColumnType.Text, SortOrder = 13 },
                new() { Name = "ExcludedEdition", DisplayName = "Excluded Edition", Type = ColumnType.Text, SortOrder = 14 },
                new() { Name = "RequiredDecodeCount", DisplayName = "Decode Count", Type = ColumnType.Integer, SortOrder = 15 },
                new() { Name = "RequiredPrestigeLevel", DisplayName = "Prestige Level", Type = ColumnType.Integer, SortOrder = 16 },
                new() { Name = "NormalizedName", DisplayName = "Normalized Name", Type = ColumnType.Text, SortOrder = 17 },
                new() { Name = "IsApproved", DisplayName = "Approved", Type = ColumnType.Boolean, SortOrder = 18 },
                new() { Name = "UpdatedAt", DisplayName = "Updated At", Type = ColumnType.DateTime, SortOrder = 19 }
            };

            var schemaJson = JsonSerializer.Serialize(columns);
            await UpsertSchemaMetaAsync(connection, transaction, "Quests", "Quests", schemaJson);
        }

        private async Task RegisterQuestTraderRequirementsSchemaAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var columns = new List<ColumnSchema>
            {
                new() { Name = "Id", DisplayName = "ID", Type = ColumnType.Text, IsPrimaryKey = true, SortOrder = 0 },
                new() { Name = "QuestId", DisplayName = "Quest ID", Type = ColumnType.Text, IsRequired = true, ForeignKeyTable = "Quests", ForeignKeyColumn = "Id", SortOrder = 1 },
                new() { Name = "TraderId", DisplayName = "Trader ID", Type = ColumnType.Text, IsRequired = true, SortOrder = 2 },
                new() { Name = "TraderName", DisplayName = "Trader", Type = ColumnType.Text, IsRequired = true, SortOrder = 3 },
                new() { Name = "RequiredLevel", DisplayName = "Loyalty Level", Type = ColumnType.Integer, IsRequired = true, SortOrder = 4 },
                new() { Name = "ContentHash", DisplayName = "Content Hash", Type = ColumnType.Text, SortOrder = 5 },
                new() { Name = "IsApproved", DisplayName = "Approved", Type = ColumnType.Boolean, IsRequired = true, SortOrder = 6 },
                new() { Name = "ApprovedAt", DisplayName = "Approved At", Type = ColumnType.DateTime, SortOrder = 7 },
                new() { Name = "UpdatedAt", DisplayName = "Updated At", Type = ColumnType.DateTime, SortOrder = 8 }
            };

            var schemaJson = JsonSerializer.Serialize(columns);
            await UpsertSchemaMetaAsync(connection, transaction, "QuestTraderRequirements", "Quest Trader Requirements", schemaJson);
        }

        private async Task UpsertSchemaMetaAsync(SqliteConnection connection, SqliteTransaction transaction, string tableName, string displayName, string schemaJson)
        {
            // Check if exists
            var checkSql = "SELECT COUNT(*) FROM _schema_meta WHERE TableName = @TableName";
            using var checkCmd = new SqliteCommand(checkSql, connection, transaction);
            checkCmd.Parameters.AddWithValue("@TableName", tableName);
            var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

            if (count == 0)
            {
                var insertSql = @"
                    INSERT INTO _schema_meta (TableName, DisplayName, SchemaJson, CreatedAt, UpdatedAt)
                    VALUES (@TableName, @DisplayName, @SchemaJson, @Now, @Now)";
                using var insertCmd = new SqliteCommand(insertSql, connection, transaction);
                insertCmd.Parameters.AddWithValue("@TableName", tableName);
                insertCmd.Parameters.AddWithValue("@DisplayName", displayName);
                insertCmd.Parameters.AddWithValue("@SchemaJson", schemaJson);
                insertCmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o"));
                await insertCmd.ExecuteNonQueryAsync();
            }
            else
            {
                var updateSql = @"
                    UPDATE _schema_meta SET SchemaJson = @SchemaJson, UpdatedAt = @Now
                    WHERE TableName = @TableName";
                using var updateCmd = new SqliteCommand(updateSql, connection, transaction);
                updateCmd.Parameters.AddWithValue("@TableName", tableName);
                updateCmd.Parameters.AddWithValue("@SchemaJson", schemaJson);
                updateCmd.Parameters.AddWithValue("@Now", DateTime.UtcNow.ToString("o"));
                await updateCmd.ExecuteNonQueryAsync();
            }
        }

        private async Task CreateItemsTableIfNotExistsAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var sql = @"
                CREATE TABLE IF NOT EXISTS Items (
                    Id TEXT PRIMARY KEY,
                    BsgId TEXT,
                    Name TEXT NOT NULL,
                    NameEN TEXT,
                    NameKO TEXT,
                    NameJA TEXT,
                    ShortNameEN TEXT,
                    ShortNameKO TEXT,
                    ShortNameJA TEXT,
                    WikiPageLink TEXT,
                    IconUrl TEXT,
                    Category TEXT,
                    Categories TEXT,
                    IsDogtagItem INTEGER NOT NULL DEFAULT 0,
                    DogtagFaction TEXT,
                    UpdatedAt TEXT
                )";

            using var cmd = new SqliteCommand(sql, connection, transaction);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task CreateQuestsTableIfNotExistsAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var sql = @"
                CREATE TABLE IF NOT EXISTS Quests (
                    Id TEXT PRIMARY KEY,
                    BsgId TEXT,
                    Name TEXT NOT NULL,
                    NameEN TEXT,
                    NameKO TEXT,
                    NameJA TEXT,
                    WikiPageLink TEXT,
                    Trader TEXT,
                    Location TEXT,
                    MinLevel INTEGER,
                    MinLevelApproved INTEGER NOT NULL DEFAULT 0,
                    MinLevelApprovedAt TEXT,
                    MinScavKarma INTEGER,
                    MinScavKarmaApproved INTEGER NOT NULL DEFAULT 0,
                    MinScavKarmaApprovedAt TEXT,
                    KappaRequired INTEGER NOT NULL DEFAULT 0,
                    Faction TEXT,
                    RequiredEdition TEXT,
                    RequiredEditionApproved INTEGER NOT NULL DEFAULT 0,
                    RequiredEditionApprovedAt TEXT,
                    ExcludedEdition TEXT,
                    ExcludedEditionApproved INTEGER NOT NULL DEFAULT 0,
                    ExcludedEditionApprovedAt TEXT,
                    RequiredDecodeCount INTEGER,
                    RequiredDecodeCountApproved INTEGER NOT NULL DEFAULT 0,
                    RequiredDecodeCountApprovedAt TEXT,
                    RequiredPrestigeLevel INTEGER,
                    RequiredPrestigeLevelApproved INTEGER NOT NULL DEFAULT 0,
                    RequiredPrestigeLevelApprovedAt TEXT,
                    NormalizedName TEXT,
                    IsApproved INTEGER NOT NULL DEFAULT 0,
                    ApprovedAt TEXT,
                    UpdatedAt TEXT
                )";

            using var cmd = new SqliteCommand(sql, connection, transaction);
            await cmd.ExecuteNonQueryAsync();

            // CREATE TABLE IF NOT EXISTS does nothing to a table that already exists, and every
            // database this pipeline touches already has one, so a new column arrives through
            // the PRAGMA-guarded ALTER below (the pattern QuestObjectives uses).
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var checkCmd = new SqliteCommand("PRAGMA table_info(Quests)", connection, transaction))
            using (var reader = await checkCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    existingColumns.Add(reader.GetString(1));
            }

            if (!existingColumns.Contains("NormalizedName"))
            {
                using var alterCmd = new SqliteCommand(
                    "ALTER TABLE Quests ADD COLUMN NormalizedName TEXT", connection, transaction);
                await alterCmd.ExecuteNonQueryAsync();
            }

            // Unique because it is a lookup key for recorded progress: two quests answering to
            // one normalized name would make a completion ambiguous in every build.
            using var indexCmd = new SqliteCommand(
                "CREATE UNIQUE INDEX IF NOT EXISTS idx_quests_normalizedname ON Quests(NormalizedName)",
                connection, transaction);
            await indexCmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Per-trader loyalty gates on a quest, mirroring HideoutTraderRequirements. Additive:
        /// a build that predates the table simply never reads it.
        /// </summary>
        private async Task CreateQuestTraderRequirementsTableIfNotExistsAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var sql = @"
                CREATE TABLE IF NOT EXISTS QuestTraderRequirements (
                    Id TEXT PRIMARY KEY,
                    QuestId TEXT NOT NULL,
                    TraderId TEXT NOT NULL,
                    TraderName TEXT NOT NULL,
                    RequiredLevel INTEGER NOT NULL,
                    ContentHash TEXT,
                    IsApproved INTEGER NOT NULL DEFAULT 0,
                    ApprovedAt TEXT,
                    UpdatedAt TEXT,
                    FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE
                )";

            using var cmd = new SqliteCommand(sql, connection, transaction);
            await cmd.ExecuteNonQueryAsync();

            var indexSql = "CREATE INDEX IF NOT EXISTS idx_questtraderreq_questid ON QuestTraderRequirements(QuestId)";
            using var indexCmd = new SqliteCommand(indexSql, connection, transaction);
            await indexCmd.ExecuteNonQueryAsync();
        }

        private async Task CreateQuestRequirementsTableIfNotExistsAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            // 기존 auto-increment 테이블이 있으면 마이그레이션
            await MigrateQuestRequirementsTableAsync(connection, transaction);

            var sql = @"
                CREATE TABLE IF NOT EXISTS QuestRequirements (
                    Id TEXT PRIMARY KEY,
                    QuestId TEXT NOT NULL,
                    RequiredQuestId TEXT NOT NULL,
                    RequirementType TEXT NOT NULL DEFAULT 'Complete',
                    DelayMinutes INTEGER,
                    GroupId INTEGER NOT NULL DEFAULT 0,
                    ContentHash TEXT,
                    IsApproved INTEGER NOT NULL DEFAULT 0,
                    ApprovedAt TEXT,
                    UpdatedAt TEXT,
                    FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE,
                    FOREIGN KEY (RequiredQuestId) REFERENCES Quests(Id) ON DELETE CASCADE
                )";

            using var cmd = new SqliteCommand(sql, connection, transaction);
            await cmd.ExecuteNonQueryAsync();

            // 인덱스 생성
            var indexSql = @"
                CREATE INDEX IF NOT EXISTS idx_questreq_questid ON QuestRequirements(QuestId);
                CREATE INDEX IF NOT EXISTS idx_questreq_requiredid ON QuestRequirements(RequiredQuestId)";
            using var indexCmd = new SqliteCommand(indexSql, connection, transaction);
            await indexCmd.ExecuteNonQueryAsync();
        }

        private async Task MigrateQuestRequirementsTableAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            // 테이블이 존재하고 Id가 INTEGER 타입이면 마이그레이션 필요
            try
            {
                using var checkCmd = new SqliteCommand("PRAGMA table_info(QuestRequirements)", connection, transaction);
                using var reader = await checkCmd.ExecuteReaderAsync();
                bool needsMigration = false;
                while (await reader.ReadAsync())
                {
                    var colName = reader.GetString(1);
                    var colType = reader.GetString(2);
                    if (colName == "Id" && colType.ToUpper() == "INTEGER")
                    {
                        needsMigration = true;
                        break;
                    }
                }
                reader.Close();

                if (needsMigration)
                {
                    // 기존 테이블 삭제 (새 스키마로 재생성)
                    using var dropCmd = new SqliteCommand("DROP TABLE IF EXISTS QuestRequirements", connection, transaction);
                    await dropCmd.ExecuteNonQueryAsync();
                }
            }
            catch { /* 테이블이 없으면 무시 */ }
        }

        private async Task RegisterQuestRequirementsSchemaAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var columns = new List<ColumnSchema>
            {
                new() { Name = "Id", DisplayName = "ID", Type = ColumnType.Text, IsPrimaryKey = true, SortOrder = 0 },
                new() { Name = "QuestId", DisplayName = "Quest ID", Type = ColumnType.Text, IsRequired = true, ForeignKeyTable = "Quests", ForeignKeyColumn = "Id", SortOrder = 1 },
                new() { Name = "RequiredQuestId", DisplayName = "Required Quest ID", Type = ColumnType.Text, IsRequired = true, ForeignKeyTable = "Quests", ForeignKeyColumn = "Id", SortOrder = 2 },
                new() { Name = "RequirementType", DisplayName = "Type", Type = ColumnType.Text, IsRequired = true, SortOrder = 3 },
                new() { Name = "DelayMinutes", DisplayName = "Delay (min)", Type = ColumnType.Integer, SortOrder = 4 },
                new() { Name = "GroupId", DisplayName = "Group ID", Type = ColumnType.Integer, IsRequired = true, SortOrder = 5 },
                new() { Name = "ContentHash", DisplayName = "Content Hash", Type = ColumnType.Text, SortOrder = 6 },
                new() { Name = "IsApproved", DisplayName = "Approved", Type = ColumnType.Boolean, IsRequired = true, SortOrder = 7 },
                new() { Name = "ApprovedAt", DisplayName = "Approved At", Type = ColumnType.DateTime, SortOrder = 8 },
                new() { Name = "UpdatedAt", DisplayName = "Updated At", Type = ColumnType.DateTime, SortOrder = 9 }
            };

            var schemaJson = JsonSerializer.Serialize(columns);
            await UpsertSchemaMetaAsync(connection, transaction, "QuestRequirements", "Quest Requirements", schemaJson);
        }

        private async Task CreateQuestObjectivesTableIfNotExistsAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            // 기존 auto-increment 테이블이 있으면 마이그레이션
            await MigrateQuestObjectivesTableAsync(connection, transaction);

            var sql = @"
                CREATE TABLE IF NOT EXISTS QuestObjectives (
                    Id TEXT PRIMARY KEY,
                    QuestId TEXT NOT NULL,
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    ObjectiveType TEXT NOT NULL DEFAULT 'Custom',
                    Description TEXT NOT NULL,
                    TargetType TEXT,
                    TargetCount INTEGER,
                    ItemId TEXT,
                    ItemName TEXT,
                    RequiresFIR INTEGER NOT NULL DEFAULT 0,
                    MapName TEXT,
                    LocationName TEXT,
                    LocationPoints TEXT,
                    OptionalPoints TEXT,
                    Conditions TEXT,
                    DogtagMinLevel INTEGER,
                    DogtagFaction TEXT,
                    ContentHash TEXT,
                    IsApproved INTEGER NOT NULL DEFAULT 0,
                    ApprovedAt TEXT,
                    UpdatedAt TEXT,
                    FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE,
                    FOREIGN KEY (ItemId) REFERENCES Items(Id) ON DELETE SET NULL
                )";

            using var cmd = new SqliteCommand(sql, connection, transaction);
            await cmd.ExecuteNonQueryAsync();

            // 인덱스 생성
            var indexSql = @"
                CREATE INDEX IF NOT EXISTS idx_questobj_questid ON QuestObjectives(QuestId);
                CREATE INDEX IF NOT EXISTS idx_questobj_itemid ON QuestObjectives(ItemId);
                CREATE INDEX IF NOT EXISTS idx_questobj_map ON QuestObjectives(MapName)";
            using var indexCmd = new SqliteCommand(indexSql, connection, transaction);
            await indexCmd.ExecuteNonQueryAsync();

            // 컬럼 마이그레이션 (기존 DB용) - 먼저 존재하는 컬럼 확인
            var existingColumns = new HashSet<string>();
            using (var checkCmd = new SqliteCommand("PRAGMA table_info(QuestObjectives)", connection, transaction))
            using (var reader = await checkCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingColumns.Add(reader.GetString(1));
                }
            }

            var columnsToAdd = new Dictionary<string, string>
            {
                { "OptionalPoints", "TEXT" },
                { "DogtagMinLevel", "INTEGER" },
                { "DogtagFaction", "TEXT" }
            };

            foreach (var (columnName, columnType) in columnsToAdd)
            {
                if (!existingColumns.Contains(columnName))
                {
                    using var alterCmd = new SqliteCommand(
                        $"ALTER TABLE QuestObjectives ADD COLUMN {columnName} {columnType}",
                        connection, transaction);
                    await alterCmd.ExecuteNonQueryAsync();
                }
            }
        }

        private async Task MigrateQuestObjectivesTableAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            // 테이블이 존재하고 Id가 INTEGER 타입이면 마이그레이션 필요
            try
            {
                using var checkCmd = new SqliteCommand("PRAGMA table_info(QuestObjectives)", connection, transaction);
                using var reader = await checkCmd.ExecuteReaderAsync();
                bool needsMigration = false;
                while (await reader.ReadAsync())
                {
                    var colName = reader.GetString(1);
                    var colType = reader.GetString(2);
                    if (colName == "Id" && colType.ToUpper() == "INTEGER")
                    {
                        needsMigration = true;
                        break;
                    }
                }
                reader.Close();

                if (needsMigration)
                {
                    // 기존 테이블 삭제 (새 스키마로 재생성)
                    using var dropCmd = new SqliteCommand("DROP TABLE IF EXISTS QuestObjectives", connection, transaction);
                    await dropCmd.ExecuteNonQueryAsync();
                }
            }
            catch { /* 테이블이 없으면 무시 */ }
        }

        private async Task RegisterQuestObjectivesSchemaAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var columns = new List<ColumnSchema>
            {
                new() { Name = "Id", DisplayName = "ID", Type = ColumnType.Text, IsPrimaryKey = true, SortOrder = 0 },
                new() { Name = "QuestId", DisplayName = "Quest ID", Type = ColumnType.Text, IsRequired = true, ForeignKeyTable = "Quests", ForeignKeyColumn = "Id", SortOrder = 1 },
                new() { Name = "SortOrder", DisplayName = "Order", Type = ColumnType.Integer, IsRequired = true, SortOrder = 2 },
                new() { Name = "ObjectiveType", DisplayName = "Type", Type = ColumnType.Text, IsRequired = true, SortOrder = 3 },
                new() { Name = "Description", DisplayName = "Description", Type = ColumnType.Text, IsRequired = true, SortOrder = 4 },
                new() { Name = "TargetType", DisplayName = "Target Type", Type = ColumnType.Text, SortOrder = 5 },
                new() { Name = "TargetCount", DisplayName = "Count", Type = ColumnType.Integer, SortOrder = 6 },
                new() { Name = "ItemId", DisplayName = "Item ID", Type = ColumnType.Text, ForeignKeyTable = "Items", ForeignKeyColumn = "Id", SortOrder = 7 },
                new() { Name = "ItemName", DisplayName = "Item Name", Type = ColumnType.Text, SortOrder = 8 },
                new() { Name = "RequiresFIR", DisplayName = "FIR", Type = ColumnType.Boolean, IsRequired = true, SortOrder = 9 },
                new() { Name = "MapName", DisplayName = "Map", Type = ColumnType.Text, SortOrder = 10 },
                new() { Name = "LocationName", DisplayName = "Location", Type = ColumnType.Text, SortOrder = 11 },
                new() { Name = "LocationPoints", DisplayName = "Location Points", Type = ColumnType.Json, SortOrder = 12 },
                new() { Name = "OptionalPoints", DisplayName = "Optional Points", Type = ColumnType.Json, SortOrder = 13 },
                new() { Name = "Conditions", DisplayName = "Conditions", Type = ColumnType.Text, SortOrder = 14 },
                new() { Name = "DogtagMinLevel", DisplayName = "Dogtag Level", Type = ColumnType.Integer, SortOrder = 15 },
                new() { Name = "DogtagFaction", DisplayName = "Dogtag Faction", Type = ColumnType.Text, SortOrder = 16 },
                new() { Name = "ContentHash", DisplayName = "Content Hash", Type = ColumnType.Text, SortOrder = 17 },
                new() { Name = "IsApproved", DisplayName = "Approved", Type = ColumnType.Boolean, IsRequired = true, SortOrder = 18 },
                new() { Name = "ApprovedAt", DisplayName = "Approved At", Type = ColumnType.DateTime, SortOrder = 19 },
                new() { Name = "UpdatedAt", DisplayName = "Updated At", Type = ColumnType.DateTime, SortOrder = 20 }
            };

            var schemaJson = JsonSerializer.Serialize(columns);
            await UpsertSchemaMetaAsync(connection, transaction, "QuestObjectives", "Quest Objectives", schemaJson);
        }

        private async Task CreateOptionalQuestsTableIfNotExistsAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var sql = @"
                CREATE TABLE IF NOT EXISTS OptionalQuests (
                    Id TEXT PRIMARY KEY,
                    QuestId TEXT NOT NULL,
                    AlternativeQuestId TEXT NOT NULL,
                    ContentHash TEXT,
                    IsApproved INTEGER NOT NULL DEFAULT 0,
                    ApprovedAt TEXT,
                    UpdatedAt TEXT,
                    FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE,
                    FOREIGN KEY (AlternativeQuestId) REFERENCES Quests(Id) ON DELETE CASCADE
                )";

            using var cmd = new SqliteCommand(sql, connection, transaction);
            await cmd.ExecuteNonQueryAsync();

            // 인덱스 생성
            var indexSql = @"
                CREATE INDEX IF NOT EXISTS idx_optquest_questid ON OptionalQuests(QuestId);
                CREATE INDEX IF NOT EXISTS idx_optquest_altid ON OptionalQuests(AlternativeQuestId)";
            using var indexCmd = new SqliteCommand(indexSql, connection, transaction);
            await indexCmd.ExecuteNonQueryAsync();
        }

        private async Task RegisterOptionalQuestsSchemaAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var columns = new List<ColumnSchema>
            {
                new() { Name = "Id", DisplayName = "ID", Type = ColumnType.Text, IsPrimaryKey = true, SortOrder = 0 },
                new() { Name = "QuestId", DisplayName = "Quest ID", Type = ColumnType.Text, IsRequired = true, ForeignKeyTable = "Quests", ForeignKeyColumn = "Id", SortOrder = 1 },
                new() { Name = "AlternativeQuestId", DisplayName = "Alternative Quest ID", Type = ColumnType.Text, IsRequired = true, ForeignKeyTable = "Quests", ForeignKeyColumn = "Id", SortOrder = 2 },
                new() { Name = "ContentHash", DisplayName = "Content Hash", Type = ColumnType.Text, SortOrder = 3 },
                new() { Name = "IsApproved", DisplayName = "Approved", Type = ColumnType.Boolean, IsRequired = true, SortOrder = 4 },
                new() { Name = "ApprovedAt", DisplayName = "Approved At", Type = ColumnType.DateTime, SortOrder = 5 },
                new() { Name = "UpdatedAt", DisplayName = "Updated At", Type = ColumnType.DateTime, SortOrder = 6 }
            };

            var schemaJson = JsonSerializer.Serialize(columns);
            await UpsertSchemaMetaAsync(connection, transaction, "OptionalQuests", "Optional Quests", schemaJson);
        }

        private async Task CreateQuestRequiredItemsTableIfNotExistsAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var sql = @"
                CREATE TABLE IF NOT EXISTS QuestRequiredItems (
                    Id TEXT PRIMARY KEY,
                    QuestId TEXT NOT NULL,
                    ItemId TEXT,
                    ItemName TEXT NOT NULL,
                    Count INTEGER NOT NULL DEFAULT 1,
                    RequiresFIR INTEGER NOT NULL DEFAULT 0,
                    RequirementType TEXT NOT NULL DEFAULT 'Required',
                    SortOrder INTEGER NOT NULL DEFAULT 0,
                    DogtagMinLevel INTEGER,
                    DogtagFaction TEXT,
                    ContentHash TEXT,
                    IsApproved INTEGER NOT NULL DEFAULT 0,
                    ApprovedAt TEXT,
                    UpdatedAt TEXT,
                    FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE,
                    FOREIGN KEY (ItemId) REFERENCES Items(Id) ON DELETE SET NULL
                )";

            using var cmd = new SqliteCommand(sql, connection, transaction);
            await cmd.ExecuteNonQueryAsync();

            // 인덱스 생성
            var indexSql = @"
                CREATE INDEX IF NOT EXISTS idx_questreqitem_questid ON QuestRequiredItems(QuestId);
                CREATE INDEX IF NOT EXISTS idx_questreqitem_itemid ON QuestRequiredItems(ItemId)";
            using var indexCmd = new SqliteCommand(indexSql, connection, transaction);
            await indexCmd.ExecuteNonQueryAsync();
        }

        private async Task RegisterQuestRequiredItemsSchemaAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var columns = new List<ColumnSchema>
            {
                new() { Name = "Id", DisplayName = "ID", Type = ColumnType.Text, IsPrimaryKey = true, SortOrder = 0 },
                new() { Name = "QuestId", DisplayName = "Quest ID", Type = ColumnType.Text, IsRequired = true, ForeignKeyTable = "Quests", ForeignKeyColumn = "Id", SortOrder = 1 },
                new() { Name = "ItemId", DisplayName = "Item ID", Type = ColumnType.Text, ForeignKeyTable = "Items", ForeignKeyColumn = "Id", SortOrder = 2 },
                new() { Name = "ItemName", DisplayName = "Item Name", Type = ColumnType.Text, IsRequired = true, SortOrder = 3 },
                new() { Name = "Count", DisplayName = "Count", Type = ColumnType.Integer, IsRequired = true, SortOrder = 4 },
                new() { Name = "RequiresFIR", DisplayName = "FIR", Type = ColumnType.Boolean, IsRequired = true, SortOrder = 5 },
                new() { Name = "RequirementType", DisplayName = "Type", Type = ColumnType.Text, IsRequired = true, SortOrder = 6 },
                new() { Name = "SortOrder", DisplayName = "Order", Type = ColumnType.Integer, IsRequired = true, SortOrder = 7 },
                new() { Name = "DogtagMinLevel", DisplayName = "Dogtag Level", Type = ColumnType.Integer, SortOrder = 8 },
                new() { Name = "DogtagFaction", DisplayName = "Dogtag Faction", Type = ColumnType.Text, SortOrder = 9 },
                new() { Name = "ContentHash", DisplayName = "Content Hash", Type = ColumnType.Text, SortOrder = 10 },
                new() { Name = "IsApproved", DisplayName = "Approved", Type = ColumnType.Boolean, IsRequired = true, SortOrder = 11 },
                new() { Name = "ApprovedAt", DisplayName = "Approved At", Type = ColumnType.DateTime, SortOrder = 12 },
                new() { Name = "UpdatedAt", DisplayName = "Updated At", Type = ColumnType.DateTime, SortOrder = 13 }
            };

            var schemaJson = JsonSerializer.Serialize(columns);
            await UpsertSchemaMetaAsync(connection, transaction, "QuestRequiredItems", "Quest Required Items", schemaJson);
        }

        #region Traders Table (Public)

        /// <summary>
        /// tarkov.dev 캐시에서 Traders 데이터를 DB에 업데이트
        /// Refresh Data 시 호출됨 (캐시된 데이터만 사용, 네트워크 요청 없음)
        /// </summary>
        public async Task<(int inserted, int updated, int deleted)> UpdateTradersFromCacheAsync(
            string databasePath,
            TarkovDevDataService tarkovDevService,
            WikiCacheService? wikiCacheService,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            progress?.Invoke("Loading cached Traders data...");

            // 캐시된 Traders 데이터 로드
            var cachedTraders = await tarkovDevService.LoadCachedTradersAsync(cancellationToken);
            if (cachedTraders == null || cachedTraders.Count == 0)
            {
                progress?.Invoke("No cached Traders data found. Run 'Cache Tarkov Dev Data' first.");
                return (0, 0, 0);
            }

            progress?.Invoke($"Loaded {cachedTraders.Count} traders from cache");

            // DbTrader로 변환
            var dbTraders = cachedTraders.Select(t => new DbTrader
            {
                Id = t.Id,
                Name = t.Name,
                NameKO = t.NameKO,
                NameJA = t.NameJA,
                NormalizedName = t.NormalizedName,
                ImageLink = t.ImageLink
            }).ToList();

            // DB 업데이트
            using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            using var transaction = connection.BeginTransaction();

            try
            {
                await EnsureSchemaMetaTableAsync(connection, transaction);
                await CreateTradersTableIfNotExistsAsync(connection, transaction);
                await RegisterTradersSchemaAsync(connection, transaction);

                var stats = await UpsertTradersAsync(connection, transaction, dbTraders, wikiCacheService, null);

                transaction.Commit();

                progress?.Invoke($"Traders update complete: {stats.Inserted} inserted, {stats.Updated} updated, {stats.Deleted} deleted");
                return (stats.Inserted, stats.Updated, stats.Deleted);
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        #endregion

        #region Traders Table (Private)

        private async Task CreateTradersTableIfNotExistsAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var sql = @"
                CREATE TABLE IF NOT EXISTS Traders (
                    Id TEXT PRIMARY KEY,
                    Name TEXT NOT NULL,
                    NameKO TEXT,
                    NameJA TEXT,
                    NormalizedName TEXT,
                    ImageLink TEXT,
                    LocalIconPath TEXT,
                    UpdatedAt TEXT
                )";

            using var cmd = new SqliteCommand(sql, connection, transaction);
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task RegisterTradersSchemaAsync(SqliteConnection connection, SqliteTransaction transaction)
        {
            var columns = new List<ColumnSchema>
            {
                new() { Name = "Id", DisplayName = "ID", Type = ColumnType.Text, IsPrimaryKey = true, IsRequired = true, SortOrder = 0 },
                new() { Name = "Name", DisplayName = "Name", Type = ColumnType.Text, IsRequired = true, SortOrder = 1 },
                new() { Name = "NameKO", DisplayName = "Name (KO)", Type = ColumnType.Text, SortOrder = 2 },
                new() { Name = "NameJA", DisplayName = "Name (JA)", Type = ColumnType.Text, SortOrder = 3 },
                new() { Name = "NormalizedName", DisplayName = "Normalized Name", Type = ColumnType.Text, SortOrder = 4 },
                new() { Name = "ImageLink", DisplayName = "Image Link", Type = ColumnType.Text, SortOrder = 5 },
                new() { Name = "LocalIconPath", DisplayName = "Local Icon Path", Type = ColumnType.Text, SortOrder = 6 },
                new() { Name = "UpdatedAt", DisplayName = "Updated At", Type = ColumnType.DateTime, SortOrder = 7 }
            };

            var schemaJson = JsonSerializer.Serialize(columns);
            await UpsertSchemaMetaAsync(connection, transaction, "Traders", "Traders", schemaJson);
        }

        private async Task<UpsertStats> UpsertTradersAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbTrader> traders,
            WikiCacheService? wikiCacheService,
            StringBuilder? logBuilder)
        {
            var stats = new UpsertStats();
            var now = DateTime.UtcNow.ToString("o");

            // 현재 DB에 있는 모든 Trader ID 조회
            var existingIds = new HashSet<string>();
            var selectAllSql = "SELECT Id FROM Traders";
            using (var selectAllCmd = new SqliteCommand(selectAllSql, connection, transaction))
            using (var reader = await selectAllCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingIds.Add(reader.GetString(0));
                }
            }

            // 새로 가져온 Trader ID 집합
            var newTraderIds = new HashSet<string>(traders.Select(t => t.Id));

            // DB에 있지만 새 목록에 없는 Trader 삭제
            var idsToDelete = existingIds.Except(newTraderIds).ToList();
            if (idsToDelete.Count > 0)
            {
                foreach (var idToDelete in idsToDelete)
                {
                    var deleteSql = "DELETE FROM Traders WHERE Id = @Id";
                    using var deleteCmd = new SqliteCommand(deleteSql, connection, transaction);
                    deleteCmd.Parameters.AddWithValue("@Id", idToDelete);
                    await deleteCmd.ExecuteNonQueryAsync();
                    stats.Deleted++;
                    logBuilder?.AppendLine($"  [DELETE] Id: {idToDelete}");
                }
            }

            foreach (var trader in traders)
            {
                bool exists = existingIds.Contains(trader.Id);

                // 로컬 아이콘 경로 확인
                var localIconPath = wikiCacheService?.GetTraderIconPath(trader.Id);

                if (!exists)
                {
                    var insertSql = @"
                        INSERT INTO Traders (Id, Name, NameKO, NameJA, NormalizedName, ImageLink, LocalIconPath, UpdatedAt)
                        VALUES (@Id, @Name, @NameKO, @NameJA, @NormalizedName, @ImageLink, @LocalIconPath, @UpdatedAt)";

                    using var insertCmd = new SqliteCommand(insertSql, connection, transaction);
                    AddTraderParameters(insertCmd, trader, localIconPath, now);
                    await insertCmd.ExecuteNonQueryAsync();
                    stats.Inserted++;
                    logBuilder?.AppendLine($"  [INSERT] {trader.Name}");
                }
                else
                {
                    var updateSql = @"
                        UPDATE Traders SET
                            Name = @Name, NameKO = @NameKO, NameJA = @NameJA,
                            NormalizedName = @NormalizedName, ImageLink = @ImageLink, LocalIconPath = @LocalIconPath, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id";

                    using var updateCmd = new SqliteCommand(updateSql, connection, transaction);
                    AddTraderParameters(updateCmd, trader, localIconPath, now);
                    await updateCmd.ExecuteNonQueryAsync();
                    stats.Updated++;
                }
            }

            return stats;
        }

        private void AddTraderParameters(SqliteCommand cmd, DbTrader trader, string? localIconPath, string now)
        {
            cmd.Parameters.AddWithValue("@Id", trader.Id);
            cmd.Parameters.AddWithValue("@Name", trader.Name);
            cmd.Parameters.AddWithValue("@NameKO", (object?)trader.NameKO ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@NameJA", (object?)trader.NameJA ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@NormalizedName", (object?)trader.NormalizedName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ImageLink", (object?)trader.ImageLink ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LocalIconPath", (object?)localIconPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedAt", now);
        }

        #endregion

        private async Task<UpsertStats> UpsertQuestRequiredItemsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbQuestRequiredItem> requiredItems,
            StringBuilder? logBuilder)
        {
            var stats = new UpsertStats();
            var now = DateTime.UtcNow.ToString("o");

            // 기존 데이터 로드 (Id 기준으로 승인 상태 유지)
            var existingData = new Dictionary<string, (bool IsApproved, string? ApprovedAt, string? ContentHash)>();
            var existingIds = new HashSet<string>();
            var selectSql = "SELECT Id, IsApproved, ApprovedAt, ContentHash FROM QuestRequiredItems";
            using (var selectCmd = new SqliteCommand(selectSql, connection, transaction))
            using (var reader = await selectCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var id = reader.GetString(0);
                    var isApproved = !reader.IsDBNull(1) && reader.GetInt64(1) != 0;
                    var approvedAt = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var contentHash = reader.IsDBNull(3) ? null : reader.GetString(3);
                    existingIds.Add(id);
                    existingData[id] = (isApproved, approvedAt, contentHash);
                }
            }

            // 새로 가져온 required item ID 집합
            var newIds = new HashSet<string>();
            foreach (var item in requiredItems)
            {
                item.Id = item.ComputeId();
                newIds.Add(item.Id);
            }

            // DB에 있지만 새 목록에 없는 항목 삭제
            var idsToDelete = existingIds.Except(newIds).ToList();
            foreach (var idToDelete in idsToDelete)
            {
                using var deleteCmd = new SqliteCommand("DELETE FROM QuestRequiredItems WHERE Id = @Id", connection, transaction);
                deleteCmd.Parameters.AddWithValue("@Id", idToDelete);
                await deleteCmd.ExecuteNonQueryAsync();
                stats.Deleted++;
            }

            // Upsert (기존 승인 상태 유지, 변경 시 승인 해제)
            foreach (var item in requiredItems)
            {
                var newHash = item.ComputeContentHash();
                bool exists = existingIds.Contains(item.Id);

                bool isApproved = false;
                string? approvedAt = null;

                // 기존 승인 상태 확인
                if (exists && existingData.TryGetValue(item.Id, out var existing))
                {
                    // 해시가 같으면 승인 상태 유지, 다르면 승인 해제
                    if (existing.ContentHash == newHash && existing.IsApproved)
                    {
                        isApproved = true;
                        approvedAt = existing.ApprovedAt;
                        stats.Unchanged++;
                    }
                    else if (existing.IsApproved)
                    {
                        logBuilder?.AppendLine($"  [CHANGED] {item.Id} - approval reset due to content change");
                    }
                }

                if (!exists)
                {
                    // INSERT
                    var insertSql = @"
                        INSERT INTO QuestRequiredItems (Id, QuestId, ItemId, ItemName, Count, RequiresFIR, RequirementType, SortOrder, DogtagMinLevel, DogtagFaction, ContentHash, IsApproved, ApprovedAt, UpdatedAt)
                        VALUES (@Id, @QuestId, @ItemId, @ItemName, @Count, @RequiresFIR, @RequirementType, @SortOrder, @DogtagMinLevel, @DogtagFaction, @ContentHash, @IsApproved, @ApprovedAt, @UpdatedAt)";

                    using var insertCmd = new SqliteCommand(insertSql, connection, transaction);
                    AddRequiredItemParameters(insertCmd, item, newHash, isApproved, approvedAt, now);
                    await insertCmd.ExecuteNonQueryAsync();
                    stats.Inserted++;
                }
                else
                {
                    // UPDATE
                    var updateSql = @"
                        UPDATE QuestRequiredItems SET
                            QuestId = @QuestId, ItemId = @ItemId, ItemName = @ItemName, Count = @Count,
                            RequiresFIR = @RequiresFIR, RequirementType = @RequirementType, SortOrder = @SortOrder,
                            DogtagMinLevel = @DogtagMinLevel, DogtagFaction = @DogtagFaction, ContentHash = @ContentHash,
                            IsApproved = @IsApproved, ApprovedAt = @ApprovedAt, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id";

                    using var updateCmd = new SqliteCommand(updateSql, connection, transaction);
                    AddRequiredItemParameters(updateCmd, item, newHash, isApproved, approvedAt, now);
                    await updateCmd.ExecuteNonQueryAsync();
                    stats.Updated++;
                }
            }

            logBuilder?.AppendLine($"  RequiredItems: {stats.Inserted} inserted, {stats.Updated} updated, {stats.Deleted} deleted, {stats.Unchanged} approvals preserved");
            return stats;
        }

        private void AddRequiredItemParameters(SqliteCommand cmd, DbQuestRequiredItem item, string contentHash,
            bool isApproved, string? approvedAt, string now)
        {
            cmd.Parameters.AddWithValue("@Id", item.Id);
            cmd.Parameters.AddWithValue("@QuestId", item.QuestId);
            cmd.Parameters.AddWithValue("@ItemId", (object?)item.ItemId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ItemName", item.ItemName);
            cmd.Parameters.AddWithValue("@Count", item.Count);
            cmd.Parameters.AddWithValue("@RequiresFIR", item.RequiresFIR ? 1 : 0);
            cmd.Parameters.AddWithValue("@RequirementType", item.RequirementType);
            cmd.Parameters.AddWithValue("@SortOrder", item.SortOrder);
            cmd.Parameters.AddWithValue("@DogtagMinLevel", (object?)item.DogtagMinLevel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DogtagFaction", (object?)item.DogtagFaction ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ContentHash", contentHash);
            cmd.Parameters.AddWithValue("@IsApproved", isApproved ? 1 : 0);
            cmd.Parameters.AddWithValue("@ApprovedAt", (object?)approvedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedAt", now);
        }

        private async Task<UpsertStats> UpsertOptionalQuestsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbOptionalQuest> optionalQuests,
            StringBuilder? logBuilder)
        {
            var stats = new UpsertStats();
            var now = DateTime.UtcNow.ToString("o");

            // 기존 데이터 로드 (Id 기준으로 승인 상태 유지)
            var existingData = new Dictionary<string, (bool IsApproved, string? ApprovedAt, string? ContentHash)>();
            var existingIds = new HashSet<string>();
            var selectSql = "SELECT Id, IsApproved, ApprovedAt, ContentHash FROM OptionalQuests";
            using (var selectCmd = new SqliteCommand(selectSql, connection, transaction))
            using (var reader = await selectCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var id = reader.GetString(0);
                    var isApproved = !reader.IsDBNull(1) && reader.GetInt64(1) != 0;
                    var approvedAt = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var contentHash = reader.IsDBNull(3) ? null : reader.GetString(3);
                    existingIds.Add(id);
                    existingData[id] = (isApproved, approvedAt, contentHash);
                }
            }

            // 새로 가져온 optional quest ID 집합
            var newIds = new HashSet<string>();
            foreach (var opt in optionalQuests)
            {
                opt.Id = opt.ComputeId();
                newIds.Add(opt.Id);
            }

            // DB에 있지만 새 목록에 없는 항목 삭제
            var idsToDelete = existingIds.Except(newIds).ToList();
            foreach (var idToDelete in idsToDelete)
            {
                using var deleteCmd = new SqliteCommand("DELETE FROM OptionalQuests WHERE Id = @Id", connection, transaction);
                deleteCmd.Parameters.AddWithValue("@Id", idToDelete);
                await deleteCmd.ExecuteNonQueryAsync();
                stats.Deleted++;
            }

            // Upsert (기존 승인 상태 유지, 변경 시 승인 해제)
            foreach (var opt in optionalQuests)
            {
                var newHash = opt.ComputeContentHash();
                bool exists = existingIds.Contains(opt.Id);

                bool isApproved = false;
                string? approvedAt = null;

                // 기존 승인 상태 확인
                if (exists && existingData.TryGetValue(opt.Id, out var existing))
                {
                    // 해시가 같으면 승인 상태 유지, 다르면 승인 해제
                    if (existing.ContentHash == newHash && existing.IsApproved)
                    {
                        isApproved = true;
                        approvedAt = existing.ApprovedAt;
                        stats.Unchanged++;
                    }
                    else if (existing.IsApproved)
                    {
                        logBuilder?.AppendLine($"  [CHANGED] {opt.Id} - approval reset due to content change");
                    }
                }

                if (!exists)
                {
                    // INSERT
                    var insertSql = @"
                        INSERT INTO OptionalQuests (Id, QuestId, AlternativeQuestId, ContentHash, IsApproved, ApprovedAt, UpdatedAt)
                        VALUES (@Id, @QuestId, @AlternativeQuestId, @ContentHash, @IsApproved, @ApprovedAt, @UpdatedAt)";

                    using var insertCmd = new SqliteCommand(insertSql, connection, transaction);
                    insertCmd.Parameters.AddWithValue("@Id", opt.Id);
                    insertCmd.Parameters.AddWithValue("@QuestId", opt.QuestId);
                    insertCmd.Parameters.AddWithValue("@AlternativeQuestId", opt.AlternativeQuestId);
                    insertCmd.Parameters.AddWithValue("@ContentHash", newHash);
                    insertCmd.Parameters.AddWithValue("@IsApproved", isApproved ? 1 : 0);
                    insertCmd.Parameters.AddWithValue("@ApprovedAt", (object?)approvedAt ?? DBNull.Value);
                    insertCmd.Parameters.AddWithValue("@UpdatedAt", now);
                    await insertCmd.ExecuteNonQueryAsync();
                    stats.Inserted++;
                }
                else
                {
                    // UPDATE
                    var updateSql = @"
                        UPDATE OptionalQuests SET
                            QuestId = @QuestId, AlternativeQuestId = @AlternativeQuestId, ContentHash = @ContentHash,
                            IsApproved = @IsApproved, ApprovedAt = @ApprovedAt, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id";

                    using var updateCmd = new SqliteCommand(updateSql, connection, transaction);
                    updateCmd.Parameters.AddWithValue("@Id", opt.Id);
                    updateCmd.Parameters.AddWithValue("@QuestId", opt.QuestId);
                    updateCmd.Parameters.AddWithValue("@AlternativeQuestId", opt.AlternativeQuestId);
                    updateCmd.Parameters.AddWithValue("@ContentHash", newHash);
                    updateCmd.Parameters.AddWithValue("@IsApproved", isApproved ? 1 : 0);
                    updateCmd.Parameters.AddWithValue("@ApprovedAt", (object?)approvedAt ?? DBNull.Value);
                    updateCmd.Parameters.AddWithValue("@UpdatedAt", now);
                    await updateCmd.ExecuteNonQueryAsync();
                    stats.Updated++;
                }
            }

            logBuilder?.AppendLine($"  OptionalQuests: {stats.Inserted} inserted, {stats.Updated} updated, {stats.Deleted} deleted, {stats.Unchanged} approvals preserved");
            return stats;
        }

        private async Task<UpsertStats> UpsertQuestObjectivesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbQuestObjective> objectives,
            StringBuilder? logBuilder)
        {
            var stats = new UpsertStats();
            var now = DateTime.UtcNow.ToString("o");

            // 기존 데이터 로드 (Id 기준으로 승인 상태 및 좌표 유지)
            var existingData = new Dictionary<string, (bool IsApproved, string? ApprovedAt, string? ContentHash, string? LocationPoints)>();
            var existingIds = new HashSet<string>();
            var selectSql = "SELECT Id, IsApproved, ApprovedAt, ContentHash, LocationPoints FROM QuestObjectives";
            using (var selectCmd = new SqliteCommand(selectSql, connection, transaction))
            using (var reader = await selectCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var id = reader.GetString(0);
                    var isApproved = !reader.IsDBNull(1) && reader.GetInt64(1) != 0;
                    var approvedAt = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var contentHash = reader.IsDBNull(3) ? null : reader.GetString(3);
                    var locationPoints = reader.IsDBNull(4) ? null : reader.GetString(4);
                    existingIds.Add(id);
                    existingData[id] = (isApproved, approvedAt, contentHash, locationPoints);
                }
            }

            // 새로 가져온 objective ID 집합
            var newIds = new HashSet<string>();
            foreach (var obj in objectives)
            {
                obj.Id = obj.ComputeId();
                newIds.Add(obj.Id);
            }

            // DB에 있지만 새 목록에 없는 항목 삭제
            var idsToDelete = existingIds.Except(newIds).ToList();
            foreach (var idToDelete in idsToDelete)
            {
                using var deleteCmd = new SqliteCommand("DELETE FROM QuestObjectives WHERE Id = @Id", connection, transaction);
                deleteCmd.Parameters.AddWithValue("@Id", idToDelete);
                await deleteCmd.ExecuteNonQueryAsync();
                stats.Deleted++;
            }

            // Upsert (기존 승인 상태 및 좌표 유지, 변경 시 승인 해제)
            foreach (var obj in objectives)
            {
                var newHash = obj.ComputeContentHash();
                bool exists = existingIds.Contains(obj.Id);

                bool isApproved = false;
                string? approvedAt = null;
                string? locationPoints = null;

                // 기존 데이터 확인
                if (exists && existingData.TryGetValue(obj.Id, out var existing))
                {
                    // 해시가 같으면 승인 상태 유지, 다르면 승인 해제
                    if (existing.ContentHash == newHash && existing.IsApproved)
                    {
                        isApproved = true;
                        approvedAt = existing.ApprovedAt;
                        stats.Unchanged++;
                    }
                    else if (existing.IsApproved)
                    {
                        logBuilder?.AppendLine($"  [CHANGED] {obj.Id} - approval reset due to content change");
                    }

                    // 좌표 정보는 항상 유지 (사용자가 입력한 값)
                    locationPoints = existing.LocationPoints;
                }

                if (!exists)
                {
                    // INSERT
                    var insertSql = @"
                        INSERT INTO QuestObjectives (
                            Id, QuestId, SortOrder, ObjectiveType, Description, TargetType, TargetCount,
                            ItemId, ItemName, RequiresFIR, MapName, LocationName, LocationPoints,
                            Conditions, DogtagMinLevel, DogtagFaction, ContentHash, IsApproved, ApprovedAt, UpdatedAt
                        ) VALUES (
                            @Id, @QuestId, @SortOrder, @ObjectiveType, @Description, @TargetType, @TargetCount,
                            @ItemId, @ItemName, @RequiresFIR, @MapName, @LocationName, @LocationPoints,
                            @Conditions, @DogtagMinLevel, @DogtagFaction, @ContentHash, @IsApproved, @ApprovedAt, @UpdatedAt
                        )";

                    using var insertCmd = new SqliteCommand(insertSql, connection, transaction);
                    AddObjectiveParameters(insertCmd, obj, newHash, isApproved, approvedAt, locationPoints, now);
                    await insertCmd.ExecuteNonQueryAsync();
                    stats.Inserted++;
                }
                else
                {
                    // UPDATE
                    var updateSql = @"
                        UPDATE QuestObjectives SET
                            QuestId = @QuestId, SortOrder = @SortOrder, ObjectiveType = @ObjectiveType,
                            Description = @Description, TargetType = @TargetType, TargetCount = @TargetCount,
                            ItemId = @ItemId, ItemName = @ItemName, RequiresFIR = @RequiresFIR,
                            MapName = @MapName, LocationName = @LocationName, LocationPoints = @LocationPoints,
                            Conditions = @Conditions, DogtagMinLevel = @DogtagMinLevel, DogtagFaction = @DogtagFaction,
                            ContentHash = @ContentHash, IsApproved = @IsApproved, ApprovedAt = @ApprovedAt, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id";

                    using var updateCmd = new SqliteCommand(updateSql, connection, transaction);
                    AddObjectiveParameters(updateCmd, obj, newHash, isApproved, approvedAt, locationPoints, now);
                    await updateCmd.ExecuteNonQueryAsync();
                    stats.Updated++;
                }
            }

            logBuilder?.AppendLine($"  Objectives: {stats.Inserted} inserted, {stats.Updated} updated, {stats.Deleted} deleted, {stats.Unchanged} approvals preserved");
            return stats;
        }

        private void AddObjectiveParameters(SqliteCommand cmd, DbQuestObjective obj, string contentHash,
            bool isApproved, string? approvedAt, string? locationPoints, string now)
        {
            cmd.Parameters.AddWithValue("@Id", obj.Id);
            cmd.Parameters.AddWithValue("@QuestId", obj.QuestId);
            cmd.Parameters.AddWithValue("@SortOrder", obj.SortOrder);
            cmd.Parameters.AddWithValue("@ObjectiveType", obj.ObjectiveType);
            cmd.Parameters.AddWithValue("@Description", obj.Description);
            cmd.Parameters.AddWithValue("@TargetType", (object?)obj.TargetType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@TargetCount", (object?)obj.TargetCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ItemId", (object?)obj.ItemId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ItemName", (object?)obj.ItemName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RequiresFIR", obj.RequiresFIR ? 1 : 0);
            cmd.Parameters.AddWithValue("@DogtagMinLevel", (object?)obj.DogtagMinLevel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DogtagFaction", (object?)obj.DogtagFaction ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MapName", (object?)obj.MapName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LocationName", (object?)obj.LocationName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@LocationPoints", (object?)locationPoints ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Conditions", (object?)obj.Conditions ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ContentHash", contentHash);
            cmd.Parameters.AddWithValue("@IsApproved", isApproved ? 1 : 0);
            cmd.Parameters.AddWithValue("@ApprovedAt", (object?)approvedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedAt", now);
        }

        private async Task<UpsertStats> UpsertItemsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbItem> items,
            StringBuilder? logBuilder)
        {
            var stats = new UpsertStats();
            var now = DateTime.UtcNow.ToString("o");

            // 현재 DB에 있는 모든 아이템 ID 조회
            var existingIds = new HashSet<string>();
            var selectAllSql = "SELECT Id FROM Items";
            using (var selectAllCmd = new SqliteCommand(selectAllSql, connection, transaction))
            using (var reader = await selectAllCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingIds.Add(reader.GetString(0));
                }
            }

            // 새로 가져온 아이템 ID 집합
            var newItemIds = new HashSet<string>(items.Select(i => i.Id));

            // DB에 있지만 새 목록에 없는 아이템 삭제
            var idsToDelete = existingIds.Except(newItemIds).ToList();
            if (idsToDelete.Count > 0)
            {
                foreach (var idToDelete in idsToDelete)
                {
                    var deleteSql = "DELETE FROM Items WHERE Id = @Id";
                    using var deleteCmd = new SqliteCommand(deleteSql, connection, transaction);
                    deleteCmd.Parameters.AddWithValue("@Id", idToDelete);
                    await deleteCmd.ExecuteNonQueryAsync();
                    stats.Deleted++;
                    logBuilder?.AppendLine($"  [DELETE] Id: {idToDelete}");
                }
            }

            foreach (var item in items)
            {
                bool exists = existingIds.Contains(item.Id);

                if (!exists)
                {
                    // INSERT
                    var insertSql = @"
                        INSERT INTO Items (Id, BsgId, Name, NameEN, NameKO, NameJA, ShortNameEN, ShortNameKO, ShortNameJA, WikiPageLink, IconUrl, Category, Categories, IsDogtagItem, DogtagFaction, UpdatedAt)
                        VALUES (@Id, @BsgId, @Name, @NameEN, @NameKO, @NameJA, @ShortNameEN, @ShortNameKO, @ShortNameJA, @WikiPageLink, @IconUrl, @Category, @Categories, @IsDogtagItem, @DogtagFaction, @UpdatedAt)";

                    using var insertCmd = new SqliteCommand(insertSql, connection, transaction);
                    AddItemParameters(insertCmd, item, now);
                    await insertCmd.ExecuteNonQueryAsync();
                    stats.Inserted++;
                }
                else
                {
                    // 항상 UPDATE (모든 필드 갱신)
                    var updateSql = @"
                        UPDATE Items SET
                            BsgId = @BsgId, Name = @Name, NameEN = @NameEN, NameKO = @NameKO, NameJA = @NameJA,
                            ShortNameEN = @ShortNameEN, ShortNameKO = @ShortNameKO, ShortNameJA = @ShortNameJA,
                            WikiPageLink = @WikiPageLink, IconUrl = @IconUrl, Category = @Category, Categories = @Categories,
                            IsDogtagItem = @IsDogtagItem, DogtagFaction = @DogtagFaction, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id";

                    using var updateCmd = new SqliteCommand(updateSql, connection, transaction);
                    AddItemParameters(updateCmd, item, now);
                    await updateCmd.ExecuteNonQueryAsync();
                    stats.Updated++;
                }
            }

            return stats;
        }

        private void AddItemParameters(SqliteCommand cmd, DbItem item, string now)
        {
            cmd.Parameters.AddWithValue("@Id", item.Id);
            cmd.Parameters.AddWithValue("@BsgId", (object?)item.BsgId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Name", item.Name);
            cmd.Parameters.AddWithValue("@NameEN", (object?)item.NameEN ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@NameKO", (object?)item.NameKO ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@NameJA", (object?)item.NameJA ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ShortNameEN", (object?)item.ShortNameEN ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ShortNameKO", (object?)item.ShortNameKO ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ShortNameJA", (object?)item.ShortNameJA ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@WikiPageLink", (object?)item.WikiPageLink ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@IconUrl", (object?)item.IconUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Category", (object?)item.Category ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Categories", (object?)item.Categories ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@IsDogtagItem", item.IsDogtagItem ? 1 : 0);
            cmd.Parameters.AddWithValue("@DogtagFaction", (object?)item.DogtagFaction ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedAt", now);
        }

        private async Task<UpsertStats> UpsertQuestsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbQuest> quests,
            StringBuilder? logBuilder)
        {
            var stats = new UpsertStats();
            var now = DateTime.UtcNow.ToString("o");

            // 현재 DB에 있는 모든 퀘스트 ID 조회
            var existingIds = new HashSet<string>();
            var selectAllSql = "SELECT Id FROM Quests";
            using (var selectAllCmd = new SqliteCommand(selectAllSql, connection, transaction))
            using (var reader = await selectAllCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingIds.Add(reader.GetString(0));
                }
            }

            // 새로 가져온 퀘스트 ID 집합
            var newQuestIds = new HashSet<string>(quests.Select(q => q.Id));

            // DB에 있지만 새 목록에 없는 퀘스트 삭제
            var idsToDelete = existingIds.Except(newQuestIds).ToList();
            if (idsToDelete.Count > 0)
            {
                foreach (var idToDelete in idsToDelete)
                {
                    var deleteSql = "DELETE FROM Quests WHERE Id = @Id";
                    using var deleteCmd = new SqliteCommand(deleteSql, connection, transaction);
                    deleteCmd.Parameters.AddWithValue("@Id", idToDelete);
                    await deleteCmd.ExecuteNonQueryAsync();
                    stats.Deleted++;
                    logBuilder?.AppendLine($"  [DELETE] Id: {idToDelete}");
                }
            }

            foreach (var quest in quests)
            {
                bool exists = existingIds.Contains(quest.Id);

                if (!exists)
                {
                    var insertSql = @"
                        INSERT INTO Quests (Id, BsgId, Name, NameEN, NameKO, NameJA, WikiPageLink, Trader, Location, MinLevel, MinScavKarma, KappaRequired, Faction, RequiredEdition, ExcludedEdition, RequiredDecodeCount, RequiredPrestigeLevel, NormalizedName, UpdatedAt)
                        VALUES (@Id, @BsgId, @Name, @NameEN, @NameKO, @NameJA, @WikiPageLink, @Trader, @Location, @MinLevel, @MinScavKarma, @KappaRequired, @Faction, @RequiredEdition, @ExcludedEdition, @RequiredDecodeCount, @RequiredPrestigeLevel, @NormalizedName, @UpdatedAt)";

                    using var insertCmd = new SqliteCommand(insertSql, connection, transaction);
                    AddQuestParameters(insertCmd, quest, now);
                    await insertCmd.ExecuteNonQueryAsync();
                    stats.Inserted++;
                    logBuilder?.AppendLine($"  [INSERT] {quest.Name}");
                }
                else
                {
                    // 항상 UPDATE (모든 필드 갱신, 단 승인 상태는 유지)
                    var updateSql = @"
                        UPDATE Quests SET
                            BsgId = @BsgId, Name = @Name, NameEN = @NameEN, NameKO = @NameKO, NameJA = @NameJA,
                            WikiPageLink = @WikiPageLink, Trader = @Trader, Location = @Location, MinLevel = @MinLevel, MinScavKarma = @MinScavKarma, KappaRequired = @KappaRequired, Faction = @Faction, RequiredEdition = @RequiredEdition, ExcludedEdition = @ExcludedEdition, RequiredDecodeCount = @RequiredDecodeCount, RequiredPrestigeLevel = @RequiredPrestigeLevel, NormalizedName = @NormalizedName, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id";

                    using var updateCmd = new SqliteCommand(updateSql, connection, transaction);
                    AddQuestParameters(updateCmd, quest, now);
                    await updateCmd.ExecuteNonQueryAsync();
                    stats.Updated++;
                }
            }

            return stats;
        }

        private void AddQuestParameters(SqliteCommand cmd, DbQuest quest, string now)
        {
            cmd.Parameters.AddWithValue("@Id", quest.Id);
            cmd.Parameters.AddWithValue("@BsgId", (object?)quest.BsgId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Name", quest.Name);
            cmd.Parameters.AddWithValue("@NameEN", (object?)quest.NameEN ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@NameKO", (object?)quest.NameKO ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@NameJA", (object?)quest.NameJA ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@WikiPageLink", (object?)quest.WikiPageLink ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Trader", (object?)quest.Trader ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Location", (object?)quest.Location ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MinLevel", (object?)quest.MinLevel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MinScavKarma", (object?)quest.MinScavKarma ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@KappaRequired", quest.KappaRequired ? 1 : 0);
            cmd.Parameters.AddWithValue("@Faction", (object?)quest.Faction ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RequiredEdition", (object?)quest.RequiredEdition ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ExcludedEdition", (object?)quest.ExcludedEdition ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RequiredDecodeCount", (object?)quest.RequiredDecodeCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@RequiredPrestigeLevel", (object?)quest.RequiredPrestigeLevel ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@NormalizedName", quest.NormalizedName);
            cmd.Parameters.AddWithValue("@UpdatedAt", now);
        }

        /// <summary>
        /// Table-global diff over the loyalty gates, the same shape as the other child tables:
        /// rows absent from the new set are deleted, and an approval survives an unchanged
        /// content hash.
        /// </summary>
        private async Task<UpsertStats> UpsertQuestTraderRequirementsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbQuestTraderRequirement> requirements,
            StringBuilder? logBuilder)
        {
            var stats = new UpsertStats();
            var now = DateTime.UtcNow.ToString("o");

            var existingData = new Dictionary<string, (bool IsApproved, string? ApprovedAt, string? ContentHash)>();
            using (var selectCmd = new SqliteCommand(
                "SELECT Id, IsApproved, ApprovedAt, ContentHash FROM QuestTraderRequirements", connection, transaction))
            using (var reader = await selectCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingData[reader.GetString(0)] = (
                        !reader.IsDBNull(1) && reader.GetInt64(1) != 0,
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3));
                }
            }

            var newIds = new HashSet<string>();
            foreach (var req in requirements)
            {
                req.Id = req.ComputeId();
                newIds.Add(req.Id);
            }

            foreach (var idToDelete in existingData.Keys.Where(id => !newIds.Contains(id)).ToList())
            {
                using var deleteCmd = new SqliteCommand(
                    "DELETE FROM QuestTraderRequirements WHERE Id = @Id", connection, transaction);
                deleteCmd.Parameters.AddWithValue("@Id", idToDelete);
                await deleteCmd.ExecuteNonQueryAsync();
                stats.Deleted++;
            }

            foreach (var req in requirements)
            {
                var newHash = req.ComputeContentHash();
                var exists = existingData.TryGetValue(req.Id, out var existing);

                var isApproved = false;
                string? approvedAt = null;
                if (exists && existing.ContentHash == newHash && existing.IsApproved)
                {
                    isApproved = true;
                    approvedAt = existing.ApprovedAt;
                    stats.Unchanged++;
                }

                var sql = exists
                    ? @"UPDATE QuestTraderRequirements SET
                            QuestId = @QuestId, TraderId = @TraderId, TraderName = @TraderName,
                            RequiredLevel = @RequiredLevel, ContentHash = @ContentHash,
                            IsApproved = @IsApproved, ApprovedAt = @ApprovedAt, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id"
                    : @"INSERT INTO QuestTraderRequirements
                            (Id, QuestId, TraderId, TraderName, RequiredLevel, ContentHash, IsApproved, ApprovedAt, UpdatedAt)
                        VALUES (@Id, @QuestId, @TraderId, @TraderName, @RequiredLevel, @ContentHash, @IsApproved, @ApprovedAt, @UpdatedAt)";

                using var cmd = new SqliteCommand(sql, connection, transaction);
                cmd.Parameters.AddWithValue("@Id", req.Id);
                cmd.Parameters.AddWithValue("@QuestId", req.QuestId);
                cmd.Parameters.AddWithValue("@TraderId", req.TraderId);
                cmd.Parameters.AddWithValue("@TraderName", req.TraderName);
                cmd.Parameters.AddWithValue("@RequiredLevel", req.RequiredLevel);
                cmd.Parameters.AddWithValue("@ContentHash", newHash);
                cmd.Parameters.AddWithValue("@IsApproved", isApproved ? 1 : 0);
                cmd.Parameters.AddWithValue("@ApprovedAt", (object?)approvedAt ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@UpdatedAt", now);
                await cmd.ExecuteNonQueryAsync();

                if (exists) stats.Updated++; else stats.Inserted++;
            }

            logBuilder?.AppendLine($"  QuestTraderRequirements: {stats.Inserted} inserted, {stats.Updated} updated, {stats.Deleted} deleted, {stats.Unchanged} approvals preserved");
            return stats;
        }

        private async Task<UpsertStats> UpsertQuestRequirementsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbQuestRequirement> requirements,
            StringBuilder? logBuilder)
        {
            var stats = new UpsertStats();
            var now = DateTime.UtcNow.ToString("o");

            // 기존 데이터 로드 (Id 기준으로 승인 상태 유지)
            var existingData = new Dictionary<string, (bool IsApproved, string? ApprovedAt, string? ContentHash)>();
            var existingIds = new HashSet<string>();
            var selectSql = "SELECT Id, IsApproved, ApprovedAt, ContentHash FROM QuestRequirements";
            using (var selectCmd = new SqliteCommand(selectSql, connection, transaction))
            using (var reader = await selectCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var id = reader.GetString(0);
                    var isApproved = !reader.IsDBNull(1) && reader.GetInt64(1) != 0;
                    var approvedAt = reader.IsDBNull(2) ? null : reader.GetString(2);
                    var contentHash = reader.IsDBNull(3) ? null : reader.GetString(3);
                    existingIds.Add(id);
                    existingData[id] = (isApproved, approvedAt, contentHash);
                }
            }

            // 새로 가져온 requirement ID 집합
            var newIds = new HashSet<string>();
            foreach (var req in requirements)
            {
                req.Id = req.ComputeId();
                newIds.Add(req.Id);
            }

            // DB에 있지만 새 목록에 없는 항목 삭제.
            // Collector's rows used to be exempt here so that AddCollectorKappaRequirementsAsync
            // could own them, but that function only ever inserted, so a quest that lost its
            // Kappa flag kept its Collector row forever: Collector shipped 248 prerequisites
            // for 247 flagged quests, the extra being Grenadier. The synthesis now rebuilds the
            // set itself (deleting what is no longer flagged), so the exemption is gone and
            // this delete loop is what removes rows the wiki parse no longer produces.
            var idsToDelete = existingIds.Except(newIds).ToList();
            foreach (var idToDelete in idsToDelete)
            {
                using var deleteCmd = new SqliteCommand("DELETE FROM QuestRequirements WHERE Id = @Id", connection, transaction);
                deleteCmd.Parameters.AddWithValue("@Id", idToDelete);
                await deleteCmd.ExecuteNonQueryAsync();
                stats.Deleted++;
            }

            // Upsert (기존 승인 상태 유지, 변경 시 승인 해제)
            foreach (var req in requirements)
            {
                var newHash = req.ComputeContentHash();
                bool exists = existingIds.Contains(req.Id);

                bool isApproved = false;
                string? approvedAt = null;

                // 기존 승인 상태 확인
                if (exists && existingData.TryGetValue(req.Id, out var existing))
                {
                    // 해시가 같으면 승인 상태 유지, 다르면 승인 해제
                    if (existing.ContentHash == newHash && existing.IsApproved)
                    {
                        isApproved = true;
                        approvedAt = existing.ApprovedAt;
                        stats.Unchanged++;
                    }
                    else if (existing.IsApproved)
                    {
                        // 승인되어 있었지만 내용이 변경됨
                        logBuilder?.AppendLine($"  [CHANGED] {req.Id} - approval reset due to content change");
                    }
                }

                if (!exists)
                {
                    // INSERT
                    var insertSql = @"
                        INSERT INTO QuestRequirements (Id, QuestId, RequiredQuestId, RequirementType, DelayMinutes, GroupId, ContentHash, IsApproved, ApprovedAt, UpdatedAt)
                        VALUES (@Id, @QuestId, @RequiredQuestId, @RequirementType, @DelayMinutes, @GroupId, @ContentHash, @IsApproved, @ApprovedAt, @UpdatedAt)";

                    using var insertCmd = new SqliteCommand(insertSql, connection, transaction);
                    AddRequirementParameters(insertCmd, req, newHash, isApproved, approvedAt, now);
                    await insertCmd.ExecuteNonQueryAsync();
                    stats.Inserted++;
                }
                else
                {
                    // UPDATE
                    var updateSql = @"
                        UPDATE QuestRequirements SET
                            QuestId = @QuestId, RequiredQuestId = @RequiredQuestId, RequirementType = @RequirementType,
                            DelayMinutes = @DelayMinutes, GroupId = @GroupId, ContentHash = @ContentHash,
                            IsApproved = @IsApproved, ApprovedAt = @ApprovedAt, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id";

                    using var updateCmd = new SqliteCommand(updateSql, connection, transaction);
                    AddRequirementParameters(updateCmd, req, newHash, isApproved, approvedAt, now);
                    await updateCmd.ExecuteNonQueryAsync();
                    stats.Updated++;
                }
            }

            logBuilder?.AppendLine($"  Requirements: {stats.Inserted} inserted, {stats.Updated} updated, {stats.Deleted} deleted, {stats.Unchanged} approvals preserved");
            return stats;
        }

        private void AddRequirementParameters(SqliteCommand cmd, DbQuestRequirement req, string contentHash,
            bool isApproved, string? approvedAt, string now)
        {
            cmd.Parameters.AddWithValue("@Id", req.Id);
            cmd.Parameters.AddWithValue("@QuestId", req.QuestId);
            cmd.Parameters.AddWithValue("@RequiredQuestId", req.RequiredQuestId);
            cmd.Parameters.AddWithValue("@RequirementType", req.RequirementType);
            cmd.Parameters.AddWithValue("@DelayMinutes", (object?)req.DelayMinutes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@GroupId", req.GroupId);
            cmd.Parameters.AddWithValue("@ContentHash", contentHash);
            cmd.Parameters.AddWithValue("@IsApproved", isApproved ? 1 : 0);
            cmd.Parameters.AddWithValue("@ApprovedAt", (object?)approvedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedAt", now);
        }

        #endregion

        #region Helper Methods

        private static string NormalizeWikiLink(string wikiLink)
        {
            if (string.IsNullOrEmpty(wikiLink))
                return wikiLink;

            try
            {
                return Uri.UnescapeDataString(wikiLink);
            }
            catch
            {
                return wikiLink;
            }
        }

        private static string NormalizeQuestName(string questName)
        {
            var normalized = questName.ToLowerInvariant();

            if (normalized.EndsWith(" (quest)"))
                normalized = normalized.Substring(0, normalized.Length - 8);

            normalized = normalized.Replace(" ", "-");
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"[^a-z0-9\-]", "");
            normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"-+", "-");
            normalized = normalized.Trim('-');

            return normalized;
        }

        /// <summary>
        /// PageContent에서 Trader (given by) 파싱 - 캐시 데이터에서 항상 실행
        /// </summary>
        private static string? ExtractTraderFromContent(string content)
        {
            // |given by = [[Ragman]] 또는 |givenby = [[Prapor]] 형식에서 트레이더 이름 추출
            var match = System.Text.RegularExpressions.Regex.Match(
                content, @"\|given\s*by\s*=\s*\[\[([^\]|]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
                return NormalizeTraderName(match.Groups[1].Value.Trim());

            // 링크 없이 직접 트레이더 이름만 있는 경우
            match = System.Text.RegularExpressions.Regex.Match(
                content, @"\|given\s*by\s*=\s*([^\|\}\[\]\n]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var trader = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(trader))
                    return NormalizeTraderName(trader);
            }

            return null;
        }

        /// <summary>
        /// 트레이더 본명을 일반적인 트레이더 이름으로 변환
        /// </summary>
        private static string? NormalizeTraderName(string? traderName)
        {
            if (string.IsNullOrEmpty(traderName))
                return traderName;

            // 본명 매핑에 있으면 일반 이름으로 변환
            if (TraderNameAliases.TryGetValue(traderName, out var normalizedName))
                return normalizedName;

            return traderName;
        }

        /// <summary>
        /// PageContent에서 Location 파싱 - 캐시 데이터에서 항상 실행
        /// </summary>
        private static string? ExtractLocationFromContent(string content)
        {
            if (string.IsNullOrEmpty(content))
                return null;

            // |location = [[Woods]] 또는 |location = [[Customs]], [[Woods]] 형식
            // 다음 필드(|) 또는 infobox 끝(}}) 전까지만 매칭
            var match = System.Text.RegularExpressions.Regex.Match(
                content, @"\|location\s*=\s*([^|\n\r]*?)(?=\n|\r|\||\}\}|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                var locationValue = match.Groups[1].Value.Trim();

                // 빈 값이면 null 반환
                if (string.IsNullOrEmpty(locationValue))
                    return null;

                // [[Location]] 형식에서 이름만 추출 (여러 개일 수 있음)
                var locations = new List<string>();
                var linkMatches = System.Text.RegularExpressions.Regex.Matches(
                    locationValue, @"\[\[([^\]|]+)(?:\|[^\]]+)?\]\]");

                foreach (System.Text.RegularExpressions.Match linkMatch in linkMatches)
                {
                    var loc = linkMatch.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(loc))
                        locations.Add(loc);
                }

                if (locations.Count > 0)
                    return string.Join(", ", locations);

                // 링크 없이 직접 텍스트만 있는 경우
                locationValue = System.Text.RegularExpressions.Regex.Replace(locationValue, @"\[\[|\]\]", "").Trim();
                if (!string.IsNullOrEmpty(locationValue))
                    return locationValue;
            }

            return null;
        }

        /// <summary>
        /// PageContent에서 Icon 파일명 파싱 - 캐시 데이터에서 항상 실행
        /// </summary>
        private static string? ExtractIconFromContent(string content)
        {
            if (string.IsNullOrEmpty(content))
                return null;

            var match = System.Text.RegularExpressions.Regex.Match(
                content, @"\|icon\s*=\s*([^\|\}\n]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var iconValue = match.Groups[1].Value.Trim();

                // 파일명만 추출 (File: 접두사 제거, [[]] 제거)
                iconValue = System.Text.RegularExpressions.Regex.Replace(iconValue, @"^\[\[(?:File:|Image:)?", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                iconValue = System.Text.RegularExpressions.Regex.Replace(iconValue, @"\]\]$", "");
                iconValue = System.Text.RegularExpressions.Regex.Replace(iconValue, @"^(?:File:|Image:)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // 파이프 이후 제거
                var pipeIndex = iconValue.IndexOf('|');
                if (pipeIndex > 0)
                    iconValue = iconValue.Substring(0, pipeIndex);

                iconValue = iconValue.Trim();

                if (!string.IsNullOrEmpty(iconValue) &&
                    (iconValue.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                     iconValue.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                     iconValue.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                     iconValue.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                     iconValue.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)))
                {
                    return iconValue;
                }
            }

            return null;
        }

        #endregion

        public void Dispose()
        {
            // Nothing to dispose currently
        }
    }

    #region Models

    public class RevisionInfo
    {
        [JsonPropertyName("itemsRevision")]
        public string? ItemsRevision { get; set; }

        [JsonPropertyName("questsRevision")]
        public string? QuestsRevision { get; set; }

        [JsonPropertyName("lastUpdated")]
        public DateTime? LastUpdated { get; set; }
    }

    public class RefreshResult
    {
        public bool Success { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public string? DatabasePath { get; set; }
        public string? LogPath { get; set; }
        public string? ErrorMessage { get; set; }
        public bool ItemsUpdated { get; set; }
        public bool QuestsUpdated { get; set; }
        public int ItemsCount { get; set; }
        public int QuestsCount { get; set; }
    }

    public class ItemsFetchResult
    {
        public List<DbItem> Items { get; set; } = new();
        public string Revision { get; set; } = "";
        public int IconsDownloaded { get; set; }
        public int IconsFailed { get; set; }
        public int IconsCached { get; set; }
        public Dictionary<string, (string Url, string Error)> FailedIconDownloads { get; set; } = new();
    }

    public class QuestsFetchResult
    {
        public List<DbQuest> Quests { get; set; } = new();
        public List<DbQuestRequirement> Requirements { get; set; } = new();
        public List<DbQuestTraderRequirement> TraderRequirements { get; set; } = new();
        public List<DbQuestObjective> Objectives { get; set; } = new();
        public List<DbOptionalQuest> OptionalQuests { get; set; } = new();
        public List<DbQuestRequiredItem> RequiredItems { get; set; } = new();
        public string Revision { get; set; } = "";

        /// <summary>What the identity resolver decided, for the refresh log and the diff report.</summary>
        public QuestIdentityResolution? Identity { get; set; }

        /// <summary>
        /// Per quest, whether the wiki and the game agree about its prerequisites. Review
        /// material only: the game's list is what ships.
        /// </summary>
        public List<PrerequisiteDisagreement> PrerequisiteDisagreements { get; set; } = new();
    }

    /// <summary>One quest's prerequisite list as each source reports it, and how they compare.</summary>
    public class PrerequisiteDisagreement
    {
        public string Quest { get; set; } = "";

        /// <summary>agree, wikiSuperset (the wiki lists more), taskSuperset, or conflict.</summary>
        public string Verdict { get; set; } = "";

        public List<string> Wiki { get; set; } = new();
        public List<string> Game { get; set; } = new();
    }

    public class DbItem
    {
        public string Id { get; set; } = "";
        public string? BsgId { get; set; }
        public string Name { get; set; } = "";
        public string? NameEN { get; set; }
        public string? NameKO { get; set; }
        public string? NameJA { get; set; }
        public string? ShortNameEN { get; set; }
        public string? ShortNameKO { get; set; }
        public string? ShortNameJA { get; set; }
        public string? WikiPageLink { get; set; }
        public string? IconUrl { get; set; }
        public string? Category { get; set; }
        public string? Categories { get; set; }
        public bool IsDogtagItem { get; set; }       // 도그태그 아이템 여부
        public string? DogtagFaction { get; set; }   // 도그태그 진영: "BEAR", "USEC", or null
    }

    public class DbQuest
    {
        public string Id { get; set; } = "";
        public string? BsgId { get; set; }
        public string Name { get; set; } = "";
        public string? NameEN { get; set; }
        public string? NameKO { get; set; }
        public string? NameJA { get; set; }
        public string? WikiPageLink { get; set; }
        public string? Trader { get; set; }
        public string? Location { get; set; }
        public int? MinLevel { get; set; }
        public int? MinScavKarma { get; set; }
        public bool KappaRequired { get; set; }
        public string? Faction { get; set; }
        public string? RequiredEdition { get; set; }  // EOD, Unheard 등 게임 에디션 필수 요구사항 (이 에디션만 가능)
        public string? ExcludedEdition { get; set; }  // Unheard, EOD 등 게임 에디션 제외 조건 (이 에디션은 불가)
        public int? RequiredDecodeCount { get; set; }  // DSP 라디오 해독 필요 횟수 (Make Amends 퀘스트 등)
        public int? RequiredPrestigeLevel { get; set; }  // Prestige 레벨 요구사항 (New Beginning 퀘스트 등)

        /// <summary>
        /// The key recorded progress is filed under. Pinned to the expression both TarkovHelper
        /// builds compute when this column is absent, which is what makes a renamed quest keep
        /// its progress in the field. See <see cref="QuestNormalizedName"/>.
        /// </summary>
        public string NormalizedName { get; set; } = "";
    }

    /// <summary>
    /// A "loyalty level N with trader T" gate on a quest. A table rather than a column on
    /// Quests because 1.1 gates five quests on a trader other than the one giving them, one of
    /// them on five traders at once, which a single column would silently drop.
    /// </summary>
    public class DbQuestTraderRequirement
    {
        public string Id { get; set; } = "";
        public string QuestId { get; set; } = "";
        public string TraderId { get; set; } = "";
        public string TraderName { get; set; } = "";
        public int RequiredLevel { get; set; }

        public string ComputeId()
        {
            var raw = $"QTR|{QuestId}|{TraderId}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
            return Convert.ToBase64String(hash).Substring(0, 22).Replace('+', '-').Replace('/', '_');
        }

        public string ComputeContentHash()
        {
            var raw = $"{QuestId}|{TraderId}|{TraderName}|{RequiredLevel}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
            return Convert.ToBase64String(hash).Substring(0, 16);
        }
    }

    public class DbTrader
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? NameKO { get; set; }
        public string? NameJA { get; set; }
        public string? NormalizedName { get; set; }
        public string? ImageLink { get; set; }
    }

    public class UpsertStats
    {
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Unchanged { get; set; }
        public int Deleted { get; set; }
    }

    /// <summary>
    /// 퀘스트 선행 조건 데이터 모델
    /// </summary>
    public class DbQuestRequirement
    {
        public string Id { get; set; } = ""; // Hash-based ID (QuestId + RequiredQuestId + GroupId)
        public string QuestId { get; set; } = "";
        public string RequiredQuestId { get; set; } = "";
        public string RequirementType { get; set; } = "Complete"; // Complete, Accept, Fail
        public int? DelayMinutes { get; set; } // 시간 지연 (분 단위)
        public int GroupId { get; set; } // OR 그룹 ID (같은 그룹 내에서는 OR 조건)
        public string? ContentHash { get; set; } // 변경 감지용 해시
        public bool IsApproved { get; set; } // 사용자 승인 여부
        public DateTime? ApprovedAt { get; set; } // 승인 시간

        /// <summary>
        /// 고유 ID 생성 (QuestId + RequiredQuestId + GroupId 기반 해시)
        /// </summary>
        public string ComputeId()
        {
            var data = $"REQ|{QuestId}|{RequiredQuestId}|{GroupId}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash).Substring(0, 22).Replace("/", "_").Replace("+", "-");
        }

        /// <summary>
        /// 현재 데이터의 해시 생성 (변경 감지용)
        /// </summary>
        public string ComputeContentHash()
        {
            var data = $"{QuestId}|{RequiredQuestId}|{RequirementType}|{DelayMinutes}|{GroupId}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash).Substring(0, 16);
        }
    }

    /// <summary>
    /// 선택적 퀘스트 (Other Choices) 데이터 모델
    /// 같은 아이템을 제출해 완료할 수 있는 대체 퀘스트들
    /// </summary>
    public class DbOptionalQuest
    {
        public string Id { get; set; } = ""; // Hash-based ID (QuestId + AlternativeQuestId)
        public string QuestId { get; set; } = "";           // 현재 퀘스트 ID
        public string AlternativeQuestId { get; set; } = ""; // 대체 퀘스트 ID
        public string? ContentHash { get; set; }            // 변경 감지용 해시
        public bool IsApproved { get; set; }                // 사용자 승인 여부
        public DateTime? ApprovedAt { get; set; }           // 승인 시간

        /// <summary>
        /// 고유 ID 생성 (QuestId + AlternativeQuestId 기반 해시)
        /// </summary>
        public string ComputeId()
        {
            var data = $"OPT|{QuestId}|{AlternativeQuestId}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash).Substring(0, 22).Replace("/", "_").Replace("+", "-");
        }

        /// <summary>
        /// 현재 데이터의 해시 생성 (변경 감지용)
        /// </summary>
        public string ComputeContentHash()
        {
            var data = $"{QuestId}|{AlternativeQuestId}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash).Substring(0, 16);
        }
    }

    /// <summary>
    /// 퀘스트 목표 데이터 모델
    /// </summary>
    public class DbQuestObjective
    {
        public string Id { get; set; } = ""; // Hash-based ID (QuestId + SortOrder)
        public string QuestId { get; set; } = "";
        public int SortOrder { get; set; }
        public string ObjectiveType { get; set; } = "Custom"; // Kill, Collect, HandOver, Visit, Marking, Stash, Survive, Build, Custom
        public string Description { get; set; } = "";

        // 타겟 정보
        public string? TargetType { get; set; }  // Scav, PMC, Boss, Item 등
        public int? TargetCount { get; set; }

        // 아이템 정보
        public string? ItemId { get; set; }      // FK: Items.Id
        public string? ItemName { get; set; }    // Wiki 아이템 이름 (매칭용)
        public bool RequiresFIR { get; set; }    // Found in Raid 필요 여부

        // 맵/위치 정보
        public string? MapName { get; set; }     // Customs, Factory, Shoreline 등
        public string? LocationName { get; set; } // 위치 설명 텍스트
        public double? LocationX { get; set; }   // X 좌표 (추후 입력)
        public double? LocationY { get; set; }   // Y 좌표
        public double? LocationZ { get; set; }   // Z 좌표
        public double? LocationRadius { get; set; } // 범위 반경 (추후 입력)

        // 조건
        public string? Conditions { get; set; }  // 추가 조건 (JSON 또는 텍스트)

        // 도그태그 관련 정보
        public int? DogtagMinLevel { get; set; }   // 도그태그 최소 레벨 (예: 15레벨 이상)
        public string? DogtagFaction { get; set; } // 도그태그 진영: "BEAR", "USEC", or null

        // 승인 상태
        public string? ContentHash { get; set; }
        public bool IsApproved { get; set; }
        public DateTime? ApprovedAt { get; set; }

        /// <summary>
        /// 고유 ID 생성 (QuestId + SortOrder 기반 해시)
        /// </summary>
        public string ComputeId()
        {
            var data = $"OBJ|{QuestId}|{SortOrder}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash).Substring(0, 22).Replace("/", "_").Replace("+", "-");
        }

        /// <summary>
        /// 현재 데이터의 해시 생성 (변경 감지용)
        /// </summary>
        public string ComputeContentHash()
        {
            var data = $"{QuestId}|{SortOrder}|{ObjectiveType}|{Description}|{TargetType}|{TargetCount}|{ItemName}|{RequiresFIR}|{MapName}|{LocationName}|{Conditions}|{DogtagMinLevel}|{DogtagFaction}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash).Substring(0, 16);
        }
    }

    /// <summary>
    /// 퀘스트 필요 아이템 데이터 모델 (Related Quest Items 테이블에서 파싱)
    /// </summary>
    public class DbQuestRequiredItem
    {
        public string Id { get; set; } = ""; // Hash-based ID
        public string QuestId { get; set; } = "";
        public string? ItemId { get; set; }      // FK: Items.Id (매칭된 경우)
        public string ItemName { get; set; } = ""; // Wiki 아이템 이름
        public int Count { get; set; } = 1;      // 필요 수량
        public bool RequiresFIR { get; set; }    // Found in Raid 필요 여부
        public string RequirementType { get; set; } = "Required"; // Handover, Required, Optional
        public int SortOrder { get; set; }       // 정렬 순서
        public int? DogtagMinLevel { get; set; } // 도그태그 최소 레벨
        public string? DogtagFaction { get; set; } // 도그태그 진영: "BEAR", "USEC", or null
        public string? ContentHash { get; set; } // 변경 감지용 해시
        public bool IsApproved { get; set; }     // 사용자 승인 여부
        public DateTime? ApprovedAt { get; set; } // 승인 시간

        /// <summary>
        /// 고유 ID 생성 (QuestId + ItemName + RequirementType + RequiresFIR + SortOrder 기반 해시)
        /// SortOrder를 포함하여 같은 퀘스트에서 같은 아이템이 여러 번 나와도 고유 ID 보장
        /// </summary>
        public string ComputeId()
        {
            var data = $"ITEM|{QuestId}|{ItemName}|{RequirementType}|{RequiresFIR}|{SortOrder}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash).Substring(0, 22).Replace("/", "_").Replace("+", "-");
        }

        /// <summary>
        /// 현재 데이터의 해시 생성 (변경 감지용)
        /// </summary>
        public string ComputeContentHash()
        {
            var data = $"{QuestId}|{ItemName}|{Count}|{RequiresFIR}|{RequirementType}|{DogtagMinLevel}|{DogtagFaction}";
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
            return Convert.ToBase64String(hash).Substring(0, 16);
        }
    }

    #endregion
}
