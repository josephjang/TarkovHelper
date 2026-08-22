using System;
using System.Collections.Generic;
using System.Data.Common;
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
                var previousKappaQuests = await LoadPreviousKappaQuestCountAsync(databasePath, cancellationToken);
                logBuilder.AppendLine(
                    $"Previous quest rows: {previousQuests.Count} "
                    + $"({previousQuests.Count(q => !string.IsNullOrEmpty(q.BsgId))} with an external ID, "
                    + $"{previousKappaQuests} flagged Kappa)");

                // 캐시된 Quests 로드
                progress?.Invoke("Loading cached quests...");
                var questsResult = await LoadQuestsFromCacheAsync(
                    existingItems, previousQuests, previousKappaQuests, progress, cancellationToken);
                logBuilder.AppendLine($"Quests loaded from cache: {questsResult.Quests.Count} quests");
                logBuilder.AppendLine($"Requirements: {questsResult.Requirements.Count}");
                logBuilder.AppendLine($"TraderRequirements: {questsResult.TraderRequirements.Count}");
                logBuilder.AppendLine($"Objectives: {questsResult.Objectives.Count}");
                logBuilder.AppendLine($"OptionalQuests: {questsResult.OptionalQuests.Count}");
                logBuilder.AppendLine($"RequiredItems: {questsResult.RequiredItems.Count}");
                AppendIdentitySummary(logBuilder, questsResult);

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
                var previousKappaQuests = await LoadPreviousKappaQuestCountAsync(databasePath, cancellationToken);
                var previousItems = await LoadPreviousItemRowsAsync(databasePath, cancellationToken);
                logBuilder.AppendLine(
                    $"Previous rows: {previousQuests.Count} quests "
                    + $"({previousQuests.Count(q => !string.IsNullOrEmpty(q.BsgId))} with an external ID, "
                    + $"{previousKappaQuests} flagged Kappa), "
                    + $"{previousItems.Count} items "
                    + $"({previousItems.Count(i => !string.IsNullOrEmpty(i.BsgId))} with an external ID)");

                // The carry-over guard runs before the crawl, not after it: a run that cannot
                // preserve identity should cost the operator a message, not an hour of network.
                RefreshGuards.AssertPreviousDatabaseIsBackfilled(previousQuests);

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
                var questsResult = await FetchAndProcessQuestsAsync(
                    itemsResult.Items, previousQuests, previousKappaQuests, progress, cancellationToken);
                logBuilder.AppendLine($"Quests fetched: {questsResult.Quests.Count} quests");
                AppendIdentitySummary(logBuilder, questsResult);

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
                // The same decoding the task and item indexes are keyed by, from the one copy of
                // it: a second copy here would be a second answer to "is this the same page".
                var normalizedLink = TarkovDevJsonClient.NormalizeWikiLink(item.WikiPageLink);
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
        /// Writes the parts of a run a human reads in the run log: what was renamed, what was
        /// held back, which pages several game records claimed, which previous rows nothing
        /// carried, and which prerequisites reached no row. The full lists go to the JSON log
        /// the diff report consumes.
        /// </summary>
        private static void AppendIdentitySummary(StringBuilder logBuilder, QuestsFetchResult result)
        {
            var resolution = result.Identity;
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
            logBuilder.AppendLine($"Previous rows no imported quest kept: {resolution.UncarriedPreviousRows.Count}");
            logBuilder.AppendLine($"Game prerequisites with no row to point at: {result.StrandedPrerequisites.Count}");

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

            // Named one by one rather than counted: the eighteen seasonal quests are 3.7% of
            // 488, permanently under the match-rate guard's threshold, so without this line the
            // rows whose recorded progress the write orphans reach no durable artefact at all.
            foreach (var row in resolution.UncarriedPreviousRows)
            {
                logBuilder.AppendLine(
                    $"  [ROW ABANDONED] '{row.Name}' ({row.Id}) - no imported quest kept this key"
                    + (string.IsNullOrEmpty(row.BsgId)
                        ? "; it has no external ID, so nothing in a run could carry it"
                        : ""));
            }

            foreach (var prerequisite in result.StrandedPrerequisites)
            {
                logBuilder.AppendLine(
                    $"  [PREREQUISITE STRANDED] '{prerequisite.Quest}' requires task {prerequisite.TaskId}, "
                    + $"which no row can name: {prerequisite.Reason}.");
            }
        }

        /// <summary>
        /// The refusals a refresh makes before it writes anything, with the thresholds they
        /// measure against. Each one describes a way the pipeline has failed silently before: a
        /// wiki crawl that half arrived, a task cache that was overwritten with an empty set, or
        /// a previous database whose external IDs were gone. Crossing one is always a source
        /// problem, never something to publish.
        /// See docs/decisions/feature-quest-data-1-1-refresh.spec.md, "Pipeline guards", and
        /// RefreshGuardTests, which pins every one of them.
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

            /// <summary>
            /// Above this share of previously published quests whose row key no newly published
            /// row keeps, recorded progress is being orphaned in the field rather than carried.
            /// </summary>
            public const double MaxLostRowKeys = 0.05;

            // The trader-NULL share a publish refuses over is
            // PublishConstraints.MaxTradersMissing: it is measured over the candidate file too,
            // so it is declared beside the rule it bounds rather than here.

            /// <summary>
            /// Above this share of the things a child table describes disappearing in one run,
            /// the new list is a collapsed parse or a partial fetch rather than the game losing
            /// that much at once.
            /// <para>
            /// Measured over each row's natural identity (see
            /// <see cref="AssertDeleteBudgetHeld"/>), never over its computed row key, so a run
            /// that only re-keys a table costs nothing against the budget. Every one of the 794
            /// rows in the published QuestRequirements table is keyed by a scheme this pipeline
            /// no longer produces (546 by RowHash over a wiki-assigned GroupId of 1 or more, 248
            /// by Collector's old <c>&lt;collectorId&gt;_&lt;questId&gt;</c> concatenation), so a
            /// key measure would read 794 of 794 deleted and refuse the first 1.1 run outright.
            /// </para>
            /// <para>
            /// Loose on purpose even so, because 1.1 genuinely removes a lot of prerequisite
            /// edges: it replaces the wiki's chains with the game's, and it takes the Kappa flag
            /// off all but 13 quests, which shortens Collector's synthesized list from 248 rows
            /// to 12. Simulated against data/v1/tarkov_data.db and the live task set, the run
            /// loses 596 of the 794 published (QuestId, RequiredQuestId) pairs, 75%. Anything
            /// tighter refuses the run this pipeline was rebuilt for.
            /// </para>
            /// <para>
            /// What it still catches is what <c>Count &gt; 0</c> cannot see, because one row is
            /// not zero rows: a list that came back with a single prerequisite passes the
            /// emptiness check and then deletes every other row through the foreign keys. On the
            /// same table that reads 793 of 794 pairs gone, 99.9%.
            /// </para>
            /// <para>
            /// It does NOT catch the Kappa set collapsing on its own while the game's chains
            /// arrive intact: that is 620 of 794 pairs, 78%, under the limit. That loss has its
            /// own guards, above, and <see cref="AssertCollectorsChainIsInTheKappaSet"/> below.
            /// </para>
            /// </summary>
            public const double MaxRowsDeletedShare = 0.80;

            /// <summary>
            /// Below this many existing identities the share above says nothing, so the budget
            /// does not apply. A share needs a denominator: in a table describing one thing,
            /// losing it reads as 100% gone, and in a table of three, 67%. The published child
            /// tables hold hundreds to thousands (794 prerequisite edges, 488 quests, 4014
            /// items), so a table this small is a new one or a test one, never the collapse this
            /// guards against.
            /// </summary>
            public const int MinRowsForDeleteBudget = 10;

            /// <summary>
            /// Refuses a write that would lose more of a table than a patch plausibly can. Runs
            /// inside the write transaction, which the caller rolls back, so the file on disk is
            /// untouched either way.
            /// </summary>
            /// <param name="existingIdentities">
            /// The natural identity of every row the table already holds: the fields that say
            /// which thing in the game the row is about, NOT the row key. A prerequisite edge is
            /// its (QuestId, RequiredQuestId) pair however the key over it was computed, so an
            /// edge that survives a change of key scheme is not a deletion and must not read as
            /// one. Tables whose row key is upstream's own id (Items, Traders) pass that id;
            /// Quests passes its row key, which is the identity the whole carry-over preserves
            /// and which <see cref="MaxLostRowKeys"/> guards ten times tighter.
            /// </param>
            /// <param name="newIdentities">The same projection over the rows about to be written.</param>
            /// <param name="rowsToDelete">
            /// How many rows the delete loop will actually remove, reported alongside the
            /// measure so a re-key is visible as the gap between the two.
            /// </param>
            public static void AssertDeleteBudgetHeld(
                string table,
                IEnumerable<string> existingIdentities,
                IEnumerable<string> newIdentities,
                int rowsToDelete,
                Action<string>? progress)
            {
                var lost = new HashSet<string>(existingIdentities, StringComparer.Ordinal);
                var existingCount = lost.Count;
                if (existingCount < MinRowsForDeleteBudget)
                    return;

                lost.ExceptWith(newIdentities);

                var share = (double)lost.Count / existingCount;
                if (share <= MaxRowsDeletedShare)
                {
                    progress?.Invoke(
                        $"{table}: {lost.Count} of {existingCount} row identities are gone ({share:P1}); "
                        + $"{rowsToDelete} rows deleted by key");
                    return;
                }

                throw new InvalidOperationException(
                    $"{lost.Count} of {existingCount} row identities in {table} ({share:P0}) are gone from the new "
                    + $"list, over the {MaxRowsDeletedShare:P0} limit. A list that came back this much shorter is a "
                    + "collapsed parse or a partial fetch, not the game. (A run that only re-keys the table costs "
                    + "nothing here: the budget is measured over what each row is about, not over its computed key.)");
            }

            /// <summary>
            /// Above this share of the pages that talk about a seasonal mode failing to match
            /// the marker, the wiki's wording has moved rather than the game's content: the
            /// pages left unmatched are held back and their rows deleted.
            /// </summary>
            public const double MaxSeasonalPagesMissingTheMarker = 0.25;

            /// <summary>
            /// How far the task cache may lag the wiki crawl before the pair stops describing
            /// one moment in the game.
            /// </summary>
            public static readonly TimeSpan MaxTaskCacheLag = TimeSpan.FromDays(7);

            public static void AssertTaskCacheIsCurrent(DateTime? taskCacheVerifiedAt, Action<string>? progress)
            {
                if (!taskCacheVerifiedAt.HasValue)
                    return;

                var lag = DateTime.Now - taskCacheVerifiedAt.Value;
                if (lag > MaxTaskCacheLag)
                {
                    throw new InvalidOperationException(
                        $"The tarkov.dev task cache was last confirmed current {lag.TotalDays:F0} days ago, more than "
                        + $"{MaxTaskCacheLag.TotalDays:F0}. The wiki crawl and the game rules would describe "
                        + "different moments in the game. Run 'Debug > Cache Tarkov Dev Data' first.");
                }

                progress?.Invoke($"tarkov.dev task cache last confirmed {lag.TotalHours:F0} hours ago");
            }

            /// <summary>
            /// The guard the whole carry-over rests on. See the class remarks on
            /// <see cref="BsgIdBackfillService"/> for why a database without external IDs cannot
            /// be refreshed safely.
            /// </summary>
            public static void AssertPreviousDatabaseIsBackfilled(IReadOnlyList<PreviousQuestRow> previousQuests)
            {
                if (previousQuests.Count == 0)
                    return;

                var missing = previousQuests.Count(q => string.IsNullOrEmpty(q.BsgId));
                var share = (double)missing / previousQuests.Count;
                if (share <= MaxPreviousQuestsWithoutBsgId)
                    return;

                throw new InvalidOperationException(
                    $"{missing} of {previousQuests.Count} quests in the current database have no external ID "
                    + $"({share:P0}, over the {MaxPreviousQuestsWithoutBsgId:P0} limit). Refreshing now would "
                    + "mint a fresh row key for every quest patch 1.1 renamed, detaching the recorded progress of each one "
                    + "in every build in the field. Run 'Debug > Backfill external IDs from snapshot...' first.");
            }

            /// <summary>
            /// A published quest losing its game record is normal in a patch that removes quests;
            /// a lot of them losing it at once is an upstream problem.
            /// <para>
            /// Measured twice, because the two measurements miss different rows. The first reads
            /// the external IDs, and so can only see the rows that have one. The second reads the
            /// row key, which is what recorded progress is filed under and what
            /// <see cref="UpsertQuestsAsync"/> deletes a row by, so it covers the rows the
            /// backfill left without an ID as well: those cannot be carried at all, and the
            /// backfill guard above deliberately tolerates a tenth of them.
            /// </para>
            /// </summary>
            public static void AssertMatchRateHeld(
                IReadOnlyList<PreviousQuestRow> previousQuests,
                QuestIdentityResolution resolution,
                Action<string>? progress)
            {
                if (previousQuests.Count == 0)
                    return;

                var previouslyMatched = previousQuests.Where(q => !string.IsNullOrEmpty(q.BsgId)).ToList();
                if (previouslyMatched.Count > 0)
                {
                    var carriedBsgIds = new HashSet<string>(
                        resolution.Quests.Where(q => q.Task != null).Select(q => q.Task!.Id), StringComparer.OrdinalIgnoreCase);
                    var lost = previouslyMatched.Count(q => !carriedBsgIds.Contains(q.BsgId!));
                    var share = (double)lost / previouslyMatched.Count;

                    if (share > MaxLostMatches)
                    {
                        throw new InvalidOperationException(
                            $"{lost} of {previouslyMatched.Count} published quests ({share:P0}) would lose their game record, "
                            + $"over the {MaxLostMatches:P0} limit. A patch removes quests; it does not remove this "
                            + "many at once. Check that the task cache is complete before publishing.");
                    }

                    progress?.Invoke($"{lost} of {previouslyMatched.Count} published quests lost their game record ({share:P1})");
                }

                // The resolver's own list rather than a second computation of it: same
                // resolution.Quests keys, same previous rows, same ordinal comparison. Two
                // copies of one rule are two answers waiting to disagree, and this one is also
                // what the run log names row by row.
                var orphaned = resolution.UncarriedPreviousRows;
                var keyShare = (double)orphaned.Count / previousQuests.Count;

                if (keyShare > MaxLostRowKeys)
                {
                    throw new InvalidOperationException(
                        $"{orphaned.Count} of {previousQuests.Count} published quests ({keyShare:P0}) would lose their row key, "
                        + $"over the {MaxLostRowKeys:P0} limit: {string.Join(", ", orphaned.Take(10).Select(q => q.Name))}. "
                        + "Their rows are deleted and the progress recorded against them in every build in the field is "
                        + "orphaned. A row with no external ID cannot be carried at all, so check the backfill before "
                        + "checking the task cache.");
                }

                progress?.Invoke($"{orphaned.Count} of {previousQuests.Count} published quests lost their row key ({keyShare:P1})");
            }

            /// <summary>
            /// Refuses a crawl whose seasonal marker has stopped matching. Pages that talk about
            /// a seasonal mode while the marker recognises none of them means the wording moved
            /// upstream, and importing zero seasonal quests without saying so is exactly the kind
            /// of silence this pipeline keeps producing.
            /// <para>
            /// A share rather than "not one matched": a wiki edit that reworded seventeen of the
            /// eighteen KORD BREACH pages leaves one matching, and the other seventeen would then
            /// be held back and deleted with their objectives and prerequisites. None of them
            /// carries an external ID, so no other guard can see them.
            /// </para>
            /// </summary>
            public static void AssertSeasonalMarkerStillMatches(int seasonalPages, int pagesMissingTheMarker)
            {
                if (pagesMissingTheMarker == 0)
                    return;

                var talkingAboutSeason = seasonalPages + pagesMissingTheMarker;
                var share = (double)pagesMissingTheMarker / talkingAboutSeason;
                if (share <= MaxSeasonalPagesMissingTheMarker)
                    return;

                throw new InvalidOperationException(
                    $"{pagesMissingTheMarker} of {talkingAboutSeason} quest pages that mention a seasonal mode in their "
                    + $"Requirements section ({share:P0}, over the {MaxSeasonalPagesMissingTheMarker:P0} limit) do not "
                    + "match the marker ExtractIsSeasonal reads, so those seasonal quests would silently leave the app. "
                    + "The wiki's wording has moved; update ExtractIsSeasonal and its tests.");
            }

            /// <summary>
            /// Names what the Kappa set was before this run beside what it is after, and refuses
            /// a run that would empty it.
            /// <para>
            /// The set is unusually easy to lose without noticing. Nothing else measures it: the
            /// row counts hold, the vocabularies are clean, the match rate is untouched, and
            /// Collector's prerequisite list is derived from these flags, so losing them takes
            /// the list with it (<see cref="SynthesizeCollectorRequirements"/>). Yet it is
            /// deliberately NOT a proportional threshold. Patch 1.1 removed the Kappa
            /// requirement from almost every quest (wiki Template:Infobox quest revision 348972,
            /// "Remove quest Kappa requirement as part of 1.1.0.0 task changes"), and the API
            /// agrees: 248 flagged quests in the published database against 13 upstream. Any
            /// share small enough to catch a bad collapse would refuse that regeneration, which
            /// is the one this pipeline was rebuilt for. So the refusal is at the only point no
            /// patch can reach, an empty set, and the rest is reported for a human to read
            /// beside the diff report's row counts.
            /// </para>
            /// <para>
            /// The collapse that is a defect rather than a patch is caught by shape instead, in
            /// <see cref="AssertCollectorsChainIsInTheKappaSet"/>.
            /// </para>
            /// </summary>
            public static void AssertKappaSetDidNotVanish(
                int previousKappaQuests,
                IReadOnlyList<DbQuest> quests,
                Action<string>? progress)
            {
                var flagged = quests.Count(q => q.KappaRequired);

                if (previousKappaQuests > 0 && flagged == 0)
                {
                    throw new InvalidOperationException(
                        $"No quest is flagged as required for the Kappa container; the current database flags "
                        + $"{previousKappaQuests}. Collector's prerequisite list is derived from that flag, so "
                        + "publishing this would leave Collector with no prerequisites and the app's Kappa gauge "
                        + "with nothing to count. Check that the task cache carries kappaRequired before publishing.");
                }

                progress?.Invoke($"Kappa quests: {flagged} (the current database flags {previousKappaQuests})");
            }

            /// <summary>
            /// Every quest Collector's own game record requires, directly or through the chain,
            /// must be in the Kappa set.
            /// <para>
            /// This is the assumption <see cref="BuildRequirements"/> rests on when it skips
            /// Collector's API prerequisites: they are already members of the synthesized set, so
            /// taking both would write the same row twice. If one of them is not flagged, the
            /// skip drops it and no other row replaces it. Collector then ships unlocked by a
            /// prerequisite the game still enforces, and nothing else in the run can see it.
            /// </para>
            /// <para>
            /// It is also the shape check the count cannot do. Upstream derives kappaRequired
            /// from exactly this closure (13 flagged = Collector plus its 12 transitive
            /// prerequisites on the 1.1 capture), so a flag that stops arriving - a renamed
            /// field, a retyped value, a mapping that quietly reads false - leaves the chain
            /// naming quests that are no longer in the set, and this refuses. A patch that
            /// genuinely shortens Collector's chain moves both together and passes.
            /// </para>
            /// </summary>
            public static void AssertCollectorsChainIsInTheKappaSet(
                IReadOnlyList<TarkovDevQuestCacheItem> tasks,
                QuestIdentityResolution resolution,
                IReadOnlyList<DbQuest> quests,
                Action<string>? progress)
            {
                var taskById = new Dictionary<string, TarkovDevQuestCacheItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var task in tasks)
                {
                    if (!string.IsNullOrEmpty(task.Id))
                        taskById[task.Id] = task;
                }

                var collector = tasks.FirstOrDefault(t => IsCollectorTaskId(t.Id) || IsCollectorName(t.NameEN));
                if (collector == null)
                    return;

                // No Collector row means no prerequisite list to be wrong about. The synthesis
                // reports that case itself, and refusing here would name a quest this run is
                // not publishing.
                if (!quests.Any(q => IsCollector(q)))
                    return;

                // The transitive closure of Collector's own prerequisites, Collector excluded:
                // upstream's own definition of the Kappa set.
                var chain = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pending = new Stack<string>(collector.TaskRequirements.Select(r => r.TaskId));
                while (pending.Count > 0)
                {
                    var taskId = pending.Pop();
                    if (string.IsNullOrEmpty(taskId) || !chain.Add(taskId))
                        continue;
                    if (taskById.TryGetValue(taskId, out var task))
                    {
                        foreach (var prerequisite in task.TaskRequirements)
                            pending.Push(prerequisite.TaskId);
                    }
                }

                chain.Remove(collector.Id);
                if (chain.Count == 0)
                {
                    progress?.Invoke("Collector's game record names no prerequisites; nothing to cross-check the Kappa set against");
                    return;
                }

                // Only the chain members this run imported: one the run held back or never
                // matched has no row to flag, and BuildRequirements already names it as a
                // stranded prerequisite.
                var questIdByTaskId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var quest in resolution.Quests.Where(q => q.Task != null))
                    questIdByTaskId[quest.Task!.Id] = quest.Id;

                var rowById = quests.ToDictionary(q => q.Id, StringComparer.Ordinal);

                var imported = 0;
                var unflagged = new List<string>();
                foreach (var taskId in chain)
                {
                    if (!questIdByTaskId.TryGetValue(taskId, out var questId) ||
                        !rowById.TryGetValue(questId, out var row))
                    {
                        continue;
                    }

                    imported++;
                    if (!row.KappaRequired)
                        unflagged.Add($"{row.Name} ({taskId})");
                }

                if (unflagged.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"{unflagged.Count} of the {imported} imported quests Collector's own game record requires are "
                        + $"not in the Kappa set: {string.Join(", ", unflagged.Take(10))}. Collector's prerequisite list "
                        + "is built from that set and its own list is skipped to avoid writing each row twice, so these "
                        + "prerequisites would reach no row at all and Collector would publish unlocked by them. Either "
                        + "the kappaRequired flag stopped arriving or the game changed what Collector requires.");
                }

                progress?.Invoke(
                    $"Collector's game chain: {chain.Count} tasks, {imported} imported, all in the Kappa set");
            }

            /// <summary>
            /// The value vocabularies, row shapes and NULL rules the fielded build depends on. Each
            /// of these is a way an additive publish could still break a build already installed: an
            /// unknown requirement type locks a quest forever, an unknown faction hides it, a second
            /// row for one quest/prerequisite pair is silently dropped by the reader, a quest that
            /// requires itself is locked forever, and a normalized name that does not match what the
            /// app computes silently orphans recorded progress.
            /// <para>
            /// The rules themselves live on <see cref="PublishConstraints"/>, declared once and
            /// evaluated here over the rows this run built and again by <c>DataPublishService</c>
            /// over the candidate file a publish is about to copy. This call is the earlier of the
            /// two and the more informative, because it fails inside the run that produced the rows;
            /// it is not the last one, because a row can still reach the file by a hand edit after
            /// the run.
            /// </para>
            /// </summary>
            public static void AssertPublishConstraints(QuestsFetchResult result, Action<string>? progress)
            {
                var candidate = PublishConstraints.Of(result);
                var problems = PublishConstraints.Problems(candidate);

                if (problems.Count > 0)
                {
                    throw new InvalidOperationException(PublishConstraints.Describe(
                        "The refresh would publish data the builds in the field cannot read correctly",
                        problems));
                }

                progress?.Invoke(PublishConstraints.DescribeHeld(candidate));
            }

            /// <summary>
            /// Fails the moment two quests would share a row key or a normalized name, which is
            /// the reachable collision: a renamed quest carries its old key while a new quest
            /// mints the key the freed title makes. Run against the resolver's output rather than
            /// only at the last gate, because everything in between indexes quests by their key
            /// and would otherwise fail first with an anonymous duplicate-key error naming
            /// neither quest.
            /// </summary>
            public static void AssertQuestIdentitiesAreUnique(IReadOnlyList<ResolvedQuest> quests)
            {
                var problems = PublishConstraints
                    .DuplicateIdentityProblems(quests, q => q.Id, q => q.NormalizedName, q => q.Title)
                    .ToList();
                if (problems.Count == 0)
                    return;

                throw new InvalidOperationException(PublishConstraints.Describe(
                    "The refresh would publish data the builds in the field cannot read correctly",
                    problems));
            }
        }

        /// <summary>
        /// Collects the wiki pages, updates the wiki cache from the network, and builds the
        /// quest rows. The crawl is the only difference from the from-cache path.
        /// </summary>
        private async Task<QuestsFetchResult> FetchAndProcessQuestsAsync(
            List<DbItem> items,
            IReadOnlyList<PreviousQuestRow> previousQuests,
            int previousKappaQuests,
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

            return await BuildQuestsAsync(
                cached, items, previousQuests, previousKappaQuests, progress, cancellationToken);
        }

        /// <summary>
        /// Builds the quest rows from the caches on disk, with no network request.
        /// </summary>
        private async Task<QuestsFetchResult> LoadQuestsFromCacheAsync(
            List<DbItem> items,
            IReadOnlyList<PreviousQuestRow> previousQuests,
            int previousKappaQuests,
            Action<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using var questService = new WikiQuestService(_wikiDataDir);
            await questService.LoadCacheAsync(cancellationToken);

            return await BuildQuestsAsync(
                questService.GetCachedQuests(), items, previousQuests, previousKappaQuests, progress,
                cancellationToken);
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
            int previousKappaQuests,
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

            // The timestamp only, not GetCacheInfo(): that also counts all four caches, and
            // counting means reading and deserializing every one of their files. The items file
            // is about 16 MB and nothing here looks inside it, and the quests and traders files
            // were deserialized two calls above.
            var questCacheVerifiedAt = devService.GetQuestsCacheVerifiedAt();
            RefreshGuards.AssertTaskCacheIsCurrent(questCacheVerifiedAt, progress);
            RefreshGuards.AssertPreviousDatabaseIsBackfilled(previousQuests);

            progress?.Invoke(
                $"Loaded {tasks.Count} tasks and {traders.Count} traders from the tarkov.dev cache"
                + (questCacheVerifiedAt.HasValue ? $" (verified {questCacheVerifiedAt:yyyy-MM-dd HH:mm})" : ""));

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

            RefreshGuards.AssertMatchRateHeld(previousQuests, resolution, progress);
            // Before anything indexes a quest by its key. Everything below builds dictionaries
            // over Id, and a duplicate there would otherwise surface as an anonymous
            // duplicate-key error carrying a base64 string and neither quest's name.
            RefreshGuards.AssertQuestIdentitiesAreUnique(resolution.Quests);

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

            // A loyalty row's key is the (quest, trader) pair, so a record naming one trader
            // twice would insert the same key twice and abort the regeneration on the primary
            // key. Nothing upstream promises otherwise, and the prerequisites have carried the
            // same collapse since the multi-status entries were found the hard way.
            result.TraderRequirements = DeduplicateTraderRequirements(result.TraderRequirements, progress);

            var (requirementRows, stranded) = BuildRequirements(resolution, cachedQuests, questIdByTitle, progress);
            result.Requirements.AddRange(requirementRows);
            result.StrandedPrerequisites = stranded;
            result.Requirements.AddRange(SynthesizeCollectorRequirements(result.Quests, progress));
            // A requirement row's key is the (quest, prerequisite, group) triple, so two rows
            // describing the same pair collide on the primary key and take the whole refresh
            // down with a constraint error rather than a message anyone can act on. The two
            // sources that could produce one are handled above; this keeps a third from being
            // discovered the hard way, mid-regeneration.
            result.Requirements = DeduplicateRequirements(result.Requirements, progress);
            result.PrerequisiteDisagreements = ComputePrerequisiteDisagreements(resolution, cachedQuests, questIdByTitle);
            ReportFlattenedOrGroups(resolution, cachedQuests, progress);
            result.Objectives.AddRange(BuildObjectives(resolution, cachedQuests, itemLookup));
            result.OptionalQuests.AddRange(BuildOptionalQuests(resolution, cachedQuests, questIdByTitle));
            result.RequiredItems.AddRange(BuildRequiredItems(resolution, cachedQuests, itemLookup));

            RefreshGuards.AssertKappaSetDidNotVanish(previousKappaQuests, result.Quests, progress);
            RefreshGuards.AssertCollectorsChainIsInTheKappaSet(tasks, resolution, result.Quests, progress);
            RefreshGuards.AssertPublishConstraints(result, progress);

            progress?.Invoke(
                $"Built {result.Quests.Count} quests, {result.Requirements.Count} prerequisites, "
                + $"{result.TraderRequirements.Count} loyalty gates, {result.Objectives.Count} objectives, "
                + $"{result.RequiredItems.Count} required items");

            await WriteRefreshLogAsync(result, cancellationToken);

            result.Revision = $"{result.Quests.Count}_{DateTime.UtcNow:yyyyMMddHH}";
            return result;
        }

        /// <summary>
        /// Turns the cached pages into resolver input, and refuses a crawl whose seasonal marker
        /// has stopped matching (see
        /// <see cref="RefreshGuards.AssertSeasonalMarkerStillMatches"/>).
        /// </summary>
        private static List<WikiQuestPage> BuildWikiPages(
            IReadOnlyDictionary<string, CachedQuestInfo> cachedQuests,
            Action<string>? progress)
        {
            var pages = new List<WikiQuestPage>();
            var missingTheMarker = 0;

            foreach (var (title, cached) in cachedQuests)
            {
                if (string.IsNullOrEmpty(cached.PageContent))
                    continue;

                var isSeasonal = WikiQuestService.ExtractIsSeasonal(cached.PageContent);
                if (!isSeasonal && WikiQuestService.MentionsSeasonalMode(cached.PageContent))
                    missingTheMarker++;

                pages.Add(new WikiQuestPage { Title = title, IsSeasonal = isSeasonal });
            }

            var seasonal = pages.Count(p => p.IsSeasonal);
            RefreshGuards.AssertSeasonalMarkerStillMatches(seasonal, missingTheMarker);

            progress?.Invoke($"{pages.Count} quest pages with content, {seasonal} marked seasonal");
            return pages;
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
        /// game's AND list and the diff report shows each one. Two of them come back, rebuilt
        /// from the game's own fail conditions rather than from the wiki: see
        /// <see cref="ExpandExclusiveAlternatives"/>.
        /// </para>
        /// <para>
        /// A game prerequisite whose target this run did not import cannot become a row: the
        /// foreign key has nothing to point at. Dropping it silently ships a short list,
        /// so the app offers it to a player who has not met the real precondition. Every one is
        /// named instead, classified by kind. There is deliberately NO threshold on the count:
        /// the legitimate number varies per patch (35 in 1.1, the removed quests the API still
        /// lists), so a number chosen now would refuse a valid regeneration later. The two-way
        /// classification is what makes the illegitimate ones identifiable, by kind rather than
        /// by volume.
        /// </para>
        /// <para>
        /// The prerequisites the game reports as satisfied by more than one status are collected
        /// as they are built and named by <see cref="ReportMultiStatusPrerequisites"/>, together
        /// with what each one became. Fourteen of them is a list worth reading, not a count.
        /// </para>
        /// </summary>
        private static (List<DbQuestRequirement> Rows, List<StrandedPrerequisite> Stranded) BuildRequirements(
            QuestIdentityResolution resolution,
            IReadOnlyDictionary<string, CachedQuestInfo> cachedQuests,
            IReadOnlyDictionary<string, string> questIdByTitle,
            Action<string>? progress)
        {
            var rows = new List<DbQuestRequirement>();
            var stranded = new List<StrandedPrerequisite>();
            var multiStatus = new List<MultiStatusPrerequisite>();
            var expansions = new List<ExclusiveExpansion>();

            var questIdByBsgId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // The same set again, holding the game records themselves: the exclusive-alternative
            // expansion reads a PREREQUISITE's own fail conditions to find the quest whose
            // completion fails it.
            var taskByBsgId = new Dictionary<string, TarkovDevQuestCacheItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var quest in resolution.Quests.Where(q => q.Task != null))
            {
                questIdByBsgId[quest.Task!.Id] = quest.Id;
                taskByBsgId[quest.Task.Id] = quest.Task;
            }

            var taskIdsWithoutPage = new HashSet<string>(
                resolution.TasksWithoutPage.Select(t => t.TaskId), StringComparer.OrdinalIgnoreCase);

            foreach (var quest in resolution.Quests)
            {
                if (quest.Task != null)
                {
                    // Collector's prerequisite list is the Kappa set, synthesized from the flags
                    // (see SynthesizeCollectorRequirements). The API also gives it five of its
                    // own, and all five are already in that set, so taking both would emit the
                    // same row twice. Recognised by the same rule the synthesis uses, so exactly
                    // one of the two owns the list whatever the quest is called this patch.
                    if (IsCollector(quest))
                        continue;

                    // Collected per quest rather than appended straight to rows: the OR groups
                    // below are allocated per quest and have to see the whole list. Paired with
                    // the game id each row came from, which is how the expansion finds the
                    // prerequisite's own fail conditions; the id itself is never published.
                    var questRows = new List<SourcedRequirement>();

                    foreach (var prerequisite in quest.Task.TaskRequirements)
                    {
                        // A prerequisite pointing at a quest this refresh did not import (a
                        // removed record, or one held back) has nothing to reference, and the
                        // foreign key would reject the row.
                        if (!questIdByBsgId.TryGetValue(prerequisite.TaskId, out var requiredQuestId))
                        {
                            stranded.Add(new StrandedPrerequisite
                            {
                                Quest = quest.Title,
                                TaskId = prerequisite.TaskId,
                                // TasksWithoutPage holds every record no page claimed, which
                                // covers both the removed quests the API still lists and the
                                // losing side of a page two records claimed. Either way nothing
                                // imported it, which is the expected shape. The other branch is
                                // a record a page DID claim while no quest kept it, and that is
                                // a pipeline problem: nothing in the resolver can produce it
                                // today, so it is the canary rather than the common case.
                                Reason = taskIdsWithoutPage.Contains(prerequisite.TaskId)
                                    ? "the target record matched no wiki page this run imported"
                                    : "the target record was matched to a page but no imported quest holds it",
                            });
                            continue;
                        }

                        // A quest that requires itself can never be unlocked, and nothing
                        // downstream checks for one. Upstream has no such entry today; the row
                        // would be unrecoverable in the field if it ever did.
                        if (requiredQuestId == quest.Id)
                            continue;

                        var mapped = MapRequirementStatuses(prerequisite.Status, quest.Title);
                        if (prerequisite.Status.Count > 1)
                        {
                            multiStatus.Add(new MultiStatusPrerequisite(
                                quest.Title, requiredQuestId, prerequisite.Status.ToList(), mapped));
                        }

                        questRows.Add(new SourcedRequirement(prerequisite.TaskId, new DbQuestRequirement
                        {
                            QuestId = quest.Id,
                            RequiredQuestId = requiredQuestId,
                            RequirementType = mapped.RequirementType,
                            AltRequirementType = mapped.AltRequirementType,
                            // The API has no OR groups, so every row starts as one AND term. The
                            // app reads a singleton group as AND, which is what the wiki parser's
                            // 1..n numbering also produced. ExpandExclusiveAlternatives moves the
                            // handful that are really an either-or onto a group of their own.
                            GroupId = 0,
                            DelayMinutes = quest.Task.AvailableDelaySecondsMin > 0
                                ? quest.Task.AvailableDelaySecondsMin / 60
                                : null,
                        }));
                    }

                    ExpandExclusiveAlternatives(quest, questRows, questIdByBsgId, taskByBsgId, expansions);
                    rows.AddRange(questRows.Select(r => r.Row));

                    continue;
                }

                if (!cachedQuests.TryGetValue(quest.Title, out var cached) || string.IsNullOrEmpty(cached.PageContent))
                    continue;

                foreach (var parsed in WikiQuestService.ExtractPreviousQuests(cached.PageContent))
                {
                    if (!TryResolveQuestId(questIdByTitle, parsed.QuestName, out var requiredQuestId))
                        continue;

                    // Collector's own page points its |previous field at itself, and the wiki is
                    // edited by hand, so this is a page away at any time. A self-reference is a
                    // quest locked forever in every install that downloads it. BuildOptionalQuests
                    // has always dropped one; so does this.
                    if (requiredQuestId == quest.Id)
                        continue;

                    rows.Add(new DbQuestRequirement
                    {
                        QuestId = quest.Id,
                        RequiredQuestId = requiredQuestId,
                        RequirementType = parsed.RequirementType,
                        DelayMinutes = parsed.DelayMinutes,
                        GroupId = parsed.GroupId,
                    });
                }
            }

            ReportMultiStatusPrerequisites(multiStatus, expansions, resolution, progress);

            return (rows, stranded);
        }

        /// <summary>
        /// A row being built, with the game id of the prerequisite it came from. That id is how
        /// <see cref="ExpandExclusiveAlternatives"/> finds the prerequisite's own game record and
        /// reads its fail conditions, and it never reaches the table, so it rides alongside the
        /// row rather than on it.
        /// </summary>
        private sealed record SourcedRequirement(string TaskId, DbQuestRequirement Row);

        /// <summary>One prerequisite the game reports as satisfied by several statuses.</summary>
        private sealed record MultiStatusPrerequisite(
            string QuestTitle,
            string PrerequisiteQuestId,
            IReadOnlyList<string> Statuses,
            MappedRequirement Mapped);

        /// <summary>
        /// What became of one "complete or failed" prerequisite: the twin it was expanded into an
        /// OR group with, or the reason it stayed a single row.
        /// </summary>
        private sealed record ExclusiveExpansion(
            string QuestTitle,
            string PrerequisiteQuestId,
            string? TwinQuestId,
            string? SkippedBecause);

        /// <summary>The fail-condition kind that names another task; the only kind read here.</summary>
        private const string TaskStatusFailCondition = "taskStatus";

        /// <summary>
        /// The quest whose completion fails <paramref name="prerequisite"/>, read off upstream's
        /// own <c>failConditions</c>, or null and the reason there is none. Read: "this
        /// prerequisite counts as met once THAT quest is done", which is what makes an either-or
        /// out of a status a published row cannot hold.
        /// <para>
        /// The relation is <c>failConditions</c> of type <c>taskStatus</c> whose status list
        /// includes <c>complete</c>: the prerequisite fails exactly when the named task
        /// completes, so the two are exclusive and "prerequisite failed" and "twin complete" are
        /// the same state. All 35 <c>taskStatus</c> fail conditions on the 1.1 capture read
        /// <c>["complete"]</c>, so the status test changes nothing today; it is there because a
        /// condition on the twin being merely started would not make the pair exclusive and must
        /// not be read as if it did.
        /// </para>
        /// <para>
        /// Exactly one such condition, never "the first of several". Twelve 1.1 tasks are failed
        /// by two different quests each (Chemical - Part 4 and The Higher They Fly among them);
        /// none is a "complete or failed" prerequisite today, but if one became one there would
        /// be no single quest to name as the other branch, and picking one would publish a group
        /// that says something the game does not.
        /// </para>
        /// <para>
        /// This replaces a hand-transcribed pair table. The two pairs 1.1 has are the ones the
        /// derivation finds (Swift Retribution is failed by Inevitable Response, Supply Plans by
        /// Kind of Sabotage), and the two it does not expand are the two that would have been
        /// wrong to transcribe: Getting Acquainted, whose only fail condition is a Lightkeeper
        /// trader standing, and Battery Change, whose twin is a second Battery Change record
        /// that loses their shared wiki page and so never becomes a row to point at (that one is
        /// refused by the caller's own import check, not here).
        /// </para>
        /// </summary>
        private static (string? TwinTaskId, string Reason) ReadExclusiveTwin(TarkovDevQuestCacheItem prerequisite)
        {
            var twins = prerequisite.FailConditions
                .Where(c => string.Equals(c.Type, TaskStatusFailCondition, StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(c.TaskId)
                            && c.Status.Any(s => string.Equals(s, "complete", StringComparison.OrdinalIgnoreCase)))
                .Select(c => c.TaskId!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (twins.Count == 1)
                return (twins[0], "");

            if (twins.Count > 1)
            {
                return (null,
                    $"the game records {twins.Count} different quests as failing it "
                    + $"({string.Join(", ", twins.OrderBy(id => id, StringComparer.Ordinal))}), so no single one is "
                    + "the other branch");
            }

            var kinds = prerequisite.FailConditions
                .Select(c => string.IsNullOrEmpty(c.Type) ? "(no type)" : c.Type)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(kind => kind, StringComparer.Ordinal)
                .ToList();

            return (null, kinds.Count == 0
                ? "the game records nothing as failing it, so there is no other branch to name "
                  + "(a task cache written before failConditions were carried also reads this way)"
                : $"nothing that fails it is another quest completing (it is failed by: {string.Join(", ", kinds)})");
        }

        /// <summary>
        /// Turns a prerequisite that a failed status also satisfies into the OR group every build
        /// in the field can already read: "this prerequisite, or the quest whose completion fails
        /// it", both at Complete.
        /// <para>
        /// This is the half of the fix that reaches installs that never update. They read
        /// <c>RequirementType</c> and nothing else, so a row saying Complete locks the quest for a
        /// player who took the other branch; an OR group says the same thing in a vocabulary they
        /// have understood since before this change. It also restores what the published database
        /// ships today, where both pairs came in from the wiki's own OR groups.
        /// </para>
        /// <para>
        /// Which quest is the other branch is derived from upstream's own fail conditions
        /// (<see cref="ReadExclusiveTwin"/>), not transcribed. The expansion is refused, and
        /// reported, whenever the group would not be readable as written: the fielded reader
        /// keys incoming requirement rows by prerequisite alone and discards every later row
        /// naming the same one, so a twin the quest already requires would silently lose one of
        /// the two rows and leave the other branch as a lone unsatisfiable group.
        /// <see cref="RefreshGuards.AssertPublishConstraints"/> restates that over the finished
        /// set, whatever produced the rows.
        /// </para>
        /// </summary>
        /// <param name="taskByBsgId">
        /// Every imported quest's game record, by game id. The fail conditions read here are the
        /// PREREQUISITE's, not the quest's, so this looks up the record behind a row rather than
        /// the row's own.
        /// </param>
        private static void ExpandExclusiveAlternatives(
            ResolvedQuest quest,
            List<SourcedRequirement> questRows,
            IReadOnlyDictionary<string, string> questIdByBsgId,
            IReadOnlyDictionary<string, TarkovDevQuestCacheItem> taskByBsgId,
            List<ExclusiveExpansion> expansions)
        {
            // Rows arrive at GroupId 0, so the OR groups this quest gets are numbered from 1.
            var nextGroupId = 1;
            var alreadyRequired = new HashSet<string>(
                questRows.Select(r => r.Row.RequiredQuestId), StringComparer.Ordinal);
            var expanded = new List<SourcedRequirement>();

            foreach (var sourced in questRows)
            {
                // Only a prerequisite a failure also satisfies has a branch to express. Read off
                // the mapped row rather than the statuses so the two cannot disagree.
                if (sourced.Row.AltRequirementType != nameof(RequirementStatus.Fail))
                    continue;

                void Skip(string because) => expansions.Add(new ExclusiveExpansion(
                    quest.Title, sourced.Row.RequiredQuestId, null, because));

                // The prerequisite's own game record. Present for every row that reaches here:
                // a row exists only because its target resolved to an imported quest, and this
                // map is built over exactly that set.
                if (!taskByBsgId.TryGetValue(sourced.TaskId, out var prerequisiteTask))
                {
                    Skip($"this run read no game record for it ({sourced.TaskId})");
                    continue;
                }

                var (twinTaskId, whyNoTwin) = ReadExclusiveTwin(prerequisiteTask);
                if (twinTaskId == null)
                {
                    Skip(whyNoTwin);
                    continue;
                }

                if (!questIdByBsgId.TryGetValue(twinTaskId, out var twinQuestId))
                {
                    Skip($"the quest that fails it ({twinTaskId}) was not imported by this run");
                    continue;
                }

                // A quest that requires itself can never be unlocked, exactly as in the loop
                // that built these rows.
                if (twinQuestId == quest.Id)
                {
                    Skip("the quest that fails it is this quest");
                    continue;
                }

                if (!alreadyRequired.Add(twinQuestId))
                {
                    Skip("the quest that fails it is already a prerequisite, which the fielded "
                         + "reader would collapse onto one row");
                    continue;
                }

                var groupId = nextGroupId++;
                sourced.Row.GroupId = groupId;
                expanded.Add(new SourcedRequirement(twinTaskId, new DbQuestRequirement
                {
                    QuestId = quest.Id,
                    RequiredQuestId = twinQuestId,
                    // Complete, not the prerequisite's own type: what satisfies this branch is
                    // finishing the twin, and its own AltRequirementType would say nothing here.
                    RequirementType = nameof(RequirementStatus.Complete),
                    AltRequirementType = null,
                    GroupId = groupId,
                    // The same wait the quest imposes on the branch it was split from: both
                    // branches unlock the same quest.
                    DelayMinutes = sourced.Row.DelayMinutes,
                }));

                expansions.Add(new ExclusiveExpansion(
                    quest.Title, sourced.Row.RequiredQuestId, twinQuestId, null));
            }

            questRows.AddRange(expanded);
        }

        /// <summary>
        /// Names every prerequisite the game reports as satisfied by more than one status, and
        /// what each one became. Fourteen in 1.1, four of them the "complete or failed" case that
        /// no single requirement type covers, so the list is short enough to read and the ones
        /// that still over-lock an install that never updates are worth reading by name.
        /// </summary>
        private static void ReportMultiStatusPrerequisites(
            IReadOnlyList<MultiStatusPrerequisite> multiStatus,
            IReadOnlyList<ExclusiveExpansion> expansions,
            QuestIdentityResolution resolution,
            Action<string>? progress)
        {
            if (progress == null || multiStatus.Count == 0)
                return;

            var titleByQuestId = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var quest in resolution.Quests)
                titleByQuestId[quest.Id] = quest.Title;

            string Name(string questId) => titleByQuestId.TryGetValue(questId, out var title) ? title : questId;

            var named = multiStatus
                .Select(m => $"{m.QuestTitle} <- {Name(m.PrerequisiteQuestId)} "
                             + $"({string.Join(" or ", m.Statuses)} -> {m.Mapped.RequirementType}"
                             + (m.Mapped.AltRequirementType == null ? "" : $" or {m.Mapped.AltRequirementType}") + ")")
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList();

            progress.Invoke(
                $"{named.Count} prerequisites are satisfied by more than one quest status. The row keeps the most "
                + "permissive type and records anything that type does not already cover in AltRequirementType: "
                + string.Join("; ", named));

            if (expansions.Count == 0)
                return;

            var grouped = expansions
                .Where(e => e.TwinQuestId != null)
                .Select(e => $"{e.QuestTitle} ({Name(e.PrerequisiteQuestId)} or {Name(e.TwinQuestId!)})")
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList();
            var lonely = expansions
                .Where(e => e.TwinQuestId == null)
                .Select(e => $"{e.QuestTitle} <- {Name(e.PrerequisiteQuestId)} ({e.SkippedBecause})")
                .OrderBy(line => line, StringComparer.Ordinal)
                .ToList();

            var message = new StringBuilder(
                $"{expansions.Count} of them are also satisfied by failing the prerequisite, which no single "
                + "requirement type covers. ");
            if (grouped.Count > 0)
            {
                message.Append($"{grouped.Count} ship as an OR group naming the quest whose completion fails the "
                    + $"prerequisite, which every build in the field can read: {string.Join("; ", grouped)}. ");
            }

            if (lonely.Count > 0)
            {
                message.Append($"{lonely.Count} ship as one row, so only a build that reads AltRequirementType is "
                    + "satisfied by the failure and older builds keep the quest locked for a player who failed the "
                    + $"prerequisite: {string.Join("; ", lonely)}. ");
            }

            progress.Invoke(message.ToString().TrimEnd());
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
                if (IsCollector(quest))
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
        /// Names the matched quests whose wiki page records alternatives (an OR group) where the
        /// game reports one flat list.
        /// <para>
        /// Their rows come from the game now, and the API has no OR groups, so every term of one
        /// becomes an AND term unless <see cref="ExpandExclusiveAlternatives"/> rebuilt the group
        /// from upstream's own fail conditions. That is the intended trade (see
        /// <see cref="BuildRequirements"/>), but it is the one the prerequisite disagreement list
        /// cannot show: when the game names exactly the alternatives the wiki did, the two sets
        /// are equal and the comparison reads "agree" while the meaning changed from "either" to
        /// "both". The spec makes these a named review item, so the run names them.
        /// </para>
        /// </summary>
        private static void ReportFlattenedOrGroups(
            QuestIdentityResolution resolution,
            IReadOnlyDictionary<string, CachedQuestInfo> cachedQuests,
            Action<string>? progress)
        {
            var flattened = new List<string>();

            foreach (var quest in resolution.Quests)
            {
                // A quest with no game record still gets the wiki's groups verbatim.
                if (quest.Task == null)
                    continue;
                if (!cachedQuests.TryGetValue(quest.Title, out var cached) || string.IsNullOrEmpty(cached.PageContent))
                    continue;

                // The wiki parser numbers AND terms 1..n and gives the members of one OR group
                // the same number, so an alternative is a group with more than one member.
                var hasAlternatives = WikiQuestService.ExtractPreviousQuests(cached.PageContent)
                    .GroupBy(p => p.GroupId)
                    .Any(g => g.Count() > 1);
                if (hasAlternatives)
                    flattened.Add(quest.Title);
            }

            if (flattened.Count == 0)
                return;

            progress?.Invoke(
                $"{flattened.Count} quests list alternative prerequisites on the wiki that the game reports as one "
                + "flat list, so every alternative becomes a requirement unless the run rebuilt the group from the "
                + "game's own fail conditions (reported separately): "
                + string.Join(", ", flattened.OrderBy(t => t, StringComparer.Ordinal))
                + ". Read their prerequisite rows in the diff report before publishing.");
        }

        /// <summary>
        /// The three requirement types a published row can hold, declared least to most
        /// permissive. The declaration order IS the precedence, applied twice:
        /// <see cref="MapRequirementStatuses"/> takes the maximum of the statuses one
        /// prerequisite names, <see cref="DeduplicateRequirements"/> the maximum among duplicate
        /// rows. Written once so the two cannot drift apart.
        /// <para>
        /// The member names are the values written to QuestRequirements.RequirementType.
        /// <see cref="RefreshGuards.AssertPublishConstraints"/> deliberately does NOT read this
        /// enum: its allow-list restates what the build in the field can read, and a type added
        /// here must fail that guard until the app has a reading for it.
        /// </para>
        /// </summary>
        public enum RequirementStatus
        {
            Fail = 0,
            Complete = 1,
            Accept = 2,
        }

        /// <summary>
        /// The requirement types one prerequisite maps onto: the most permissive one, in
        /// <c>RequirementType</c>, plus whatever that one does not already cover, in
        /// <c>AltRequirementType</c> (NULL on the overwhelming majority of rows).
        /// </summary>
        public readonly record struct MappedRequirement(string RequirementType, string? AltRequirementType);

        /// <summary>
        /// Maps the statuses that satisfy one prerequisite onto the two type columns a row
        /// carries.
        /// <para>
        /// Fourteen 1.1 prerequisites name more than one status: ten are "active or complete"
        /// and four are "complete or failed". A row's identity is the (quest, prerequisite,
        /// group) triple, so one row per status would collide on the primary key rather than
        /// express an alternative; both types have to fit on one row.
        /// </para>
        /// <para>
        /// The most permissive type wins <c>RequirementType</c>, and only a status it does not
        /// already satisfy reaches <c>AltRequirementType</c>. "Accept" is satisfied by an active
        /// <em>and</em> by a completed prerequisite (<c>QuestProgressService.IsStatusSatisfied</c>),
        /// so "active or complete" collapses onto Accept alone with nothing left over and
        /// nothing lost. "Complete or failed" has no single equivalent, so it becomes
        /// Complete with Fail alongside it: a build that reads the second column is satisfied by
        /// either, exactly as the game is.
        /// </para>
        /// <para>
        /// The second column reaches only builds that update. Every build already in the field
        /// reads <c>RequirementType</c> alone and would lock a "complete or failed" quest for a
        /// player who failed the prerequisite, which is why
        /// <see cref="ExpandExclusiveAlternatives"/> also writes the same fact as an OR group
        /// those builds can read. Whatever it cannot express there is named in the run's report
        /// rather than left to be discovered.
        /// </para>
        /// </summary>
        // Public because it is a rule about the published data, not an implementation detail:
        // the guard tests pin it directly, and a change here changes what every build in the
        // field reads as a prerequisite.
        public static MappedRequirement MapRequirementStatuses(IReadOnlyList<string> statuses, string questTitle)
        {
            // An entry with no status at all means the ordinary "must be completed". This early
            // return is load-bearing twice over: it is also what keeps the Max() below off an
            // empty sequence, which would throw an InvalidOperationException naming nothing.
            if (statuses.Count == 0)
                return new MappedRequirement(RequirementStatus.Complete.ToString(), null);

            var named = statuses.Select(s => MapRequirementStatus(s, questTitle)).Distinct().ToList();
            var primary = named.Max();

            // Only what the primary type does not already satisfy needs a column of its own.
            var leftover = named.Where(s => !Satisfies(primary, s)).Distinct().ToList();
            if (leftover.Count > 1)
            {
                // Unreachable against the three statuses upstream defines (Accept covers itself
                // and Complete, so Fail is the only status that can ever be left over), and a
                // fourth status would already have thrown in MapRequirementStatus. Here so that
                // a widened vocabulary is refused rather than silently published half-recorded.
                throw new InvalidOperationException(
                    $"'{questTitle}' has a prerequisite satisfied by {string.Join(", ", statuses)}, which needs "
                    + $"{leftover.Count} requirement types beside {primary} and a row carries one. "
                    + "The published schema would have to grow another column before this can ship.");
            }

            return new MappedRequirement(
                primary.ToString(),
                leftover.Count == 1 ? leftover[0].ToString() : null);
        }

        /// <summary>
        /// Whether a row carrying <paramref name="type"/> is already satisfied in every state
        /// <paramref name="other"/> is. Mirrors <c>QuestProgressService.IsStatusSatisfied</c>:
        /// Accept is satisfied by an active and by a completed prerequisite, so it subsumes
        /// Complete; nothing else subsumes anything but itself.
        /// </summary>
        private static bool Satisfies(RequirementStatus type, RequirementStatus other) =>
            type == other || (type == RequirementStatus.Accept && other == RequirementStatus.Complete);

        private static RequirementStatus MapRequirementStatus(string status, string questTitle) =>
            status.ToLowerInvariant() switch
            {
                "complete" => RequirementStatus.Complete,
                "active" => RequirementStatus.Accept,
                "failed" => RequirementStatus.Fail,
                _ => throw new InvalidOperationException(
                    $"'{questTitle}' has a prerequisite with status '{status}', which the app has no reading for. "
                    + "It treats an unknown requirement type as never satisfied, which would lock the quest forever.")
            };

        /// <summary>
        /// Keeps one row per (quest, prerequisite, group), preferring the most permissive
        /// requirement type among the duplicates for the same reason
        /// <see cref="MapRequirementStatuses"/> does: a quest shown slightly early is a smaller
        /// harm than one locked forever. A row that also names an alternate type is more
        /// permissive than the same type without one, which is what breaks a tie: otherwise the
        /// row that happened to arrive first would win and could drop the alternate.
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
                if (Rank(requirement) > Rank(existing))
                    kept[key] = requirement;
            }

            if (collapsed > 0)
                progress?.Invoke($"Collapsed {collapsed} duplicate prerequisite rows onto their most permissive type");

            return kept.Values.ToList();

            // Permissiveness first, then whether a second type comes with it. Doubling leaves
            // the primary order intact and gives the tie a winner, so the comparison above is a
            // total order over the shapes a row can have rather than one that falls back to
            // arrival order.
            static int Rank(DbQuestRequirement requirement) =>
                Permissiveness(requirement.RequirementType) * 2
                + (string.IsNullOrEmpty(requirement.AltRequirementType) ? 0 : 1);

            // The same order <see cref="RequirementStatus"/> declares, read through nameof so a
            // renamed member breaks the build here instead of silently ranking everything equal.
            // Not Enum.TryParse: it accepts the underlying number as well, so the string "2"
            // would parse to Accept and rank a bogus type as the most permissive of all.
            // An unrecognised type ranks below Fail rather than tying with it, because the
            // comparison is strict and a tie would let whichever row arrived first win.
            static int Permissiveness(string requirementType) => requirementType switch
            {
                nameof(RequirementStatus.Accept) => (int)RequirementStatus.Accept,
                nameof(RequirementStatus.Complete) => (int)RequirementStatus.Complete,
                nameof(RequirementStatus.Fail) => (int)RequirementStatus.Fail,
                _ => -1,
            };
        }

        /// <summary>
        /// Keeps one loyalty gate per (quest, trader), which is what the row key is, so a record
        /// naming one trader twice collapses here instead of aborting the write on the primary
        /// key. The lower level wins, for the same reason
        /// <see cref="DeduplicateRequirements"/> prefers the most permissive type: a quest shown
        /// slightly early is a smaller harm than one gated behind a level the game never asked
        /// for.
        /// </summary>
        private static List<DbQuestTraderRequirement> DeduplicateTraderRequirements(
            List<DbQuestTraderRequirement> requirements,
            Action<string>? progress)
        {
            var kept = new Dictionary<string, DbQuestTraderRequirement>(StringComparer.Ordinal);
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
                if (requirement.RequiredLevel < existing.RequiredLevel)
                    kept[key] = requirement;
            }

            if (collapsed > 0)
                progress?.Invoke($"Collapsed {collapsed} duplicate loyalty gates onto their lowest required level");

            return kept.Values.ToList();
        }

        /// <summary>
        /// The game's own id for Collector. Keyed on rather than the title because the title
        /// gates two decisions that have to agree: whether the API's own prerequisite list is
        /// skipped (<see cref="BuildRequirements"/>) and whether the Kappa set is synthesized
        /// (<see cref="SynthesizeCollectorRequirements"/>). A patch that renames the quest, as
        /// 1.1 renamed 91 others, would otherwise flip both at once and reduce a 200-row list to
        /// the API's five with nothing to notice it.
        /// </summary>
        private const string CollectorTaskId = "5c51aac186f77432ea65c552";

        /// <summary>
        /// Collector, by its game id first and by any of the names the pipeline may know it
        /// under after. Its prerequisite list is derived from the Kappa flags rather than parsed
        /// or fetched, so both the wiki parser and the game data skip it.
        /// <para>
        /// The two overloads must agree exactly, or Collector's rows are both dropped or both
        /// written twice: a matched quest's row carries the same id and names it was resolved
        /// with, so they read the same three values.
        /// </para>
        /// </summary>
        private static bool IsCollector(ResolvedQuest quest) =>
            IsCollectorTaskId(quest.Task?.Id) || IsCollectorName(quest.Title) || IsCollectorName(quest.Task?.NameEN);

        private static bool IsCollector(DbQuest quest) =>
            IsCollectorTaskId(quest.BsgId) || IsCollectorName(quest.Name) || IsCollectorName(quest.NameEN);

        private static bool IsCollectorTaskId(string? taskId) =>
            string.Equals(taskId, CollectorTaskId, StringComparison.OrdinalIgnoreCase);

        private static bool IsCollectorName(string? questName) =>
            string.Equals(questName, "Collector", StringComparison.OrdinalIgnoreCase);

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
            var collector = quests.FirstOrDefault(IsCollector);

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
                    uncarriedPreviousRows = resolution.UncarriedPreviousRows.Count,
                    strandedPrerequisites = result.StrandedPrerequisites.Count,
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
                // The rows this write deletes, and the progress every install has recorded
                // against them. A count alone cannot be acted on; the names can.
                uncarriedPreviousRows = resolution.UncarriedPreviousRows.Select(r => new { r.Id, r.Name, r.BsgId }),
                strandedPrerequisites = result.StrandedPrerequisites,
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

        /// <summary>
        /// How many quests the database being refreshed flags as required for the Kappa
        /// container. Read separately from <see cref="LoadPreviousQuestRowsAsync"/> because
        /// identity carry-over has no use for it: it exists so a run can say what the number was
        /// before it, beside what it is after (see
        /// <see cref="RefreshGuards.AssertKappaSetDidNotVanish"/>).
        /// </summary>
        internal static async Task<int> LoadPreviousKappaQuestCountAsync(
            string databasePath,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(databasePath))
                return 0;

            await using var connection = new SqliteConnection($"Data Source={databasePath}");
            await connection.OpenAsync(cancellationToken);

            if (!await TableExistsAsync(connection, "Quests", cancellationToken))
                return 0;
            if (!await ColumnExistsAsync(connection, "Quests", "KappaRequired", cancellationToken))
                return 0;

            await using var cmd = new SqliteCommand(
                "SELECT COUNT(*) FROM Quests WHERE KappaRequired = 1", connection);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
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

            // A child table whose new list came back empty is deliberately left alone: a parse
            // or a fetch that produced nothing is a failure far more often than it is a game
            // with no such rows left, and emptying the table publishes that failure to every
            // build in the field. Silence is the other half of that bargain and the half this
            // pipeline keeps getting wrong, so every skip says which table it kept and how many
            // rows are now older than the revision they ship under.
            async Task ReportSkippedTableAsync(string table)
            {
                var kept = await CountRowsAsync(connection, transaction, table, cancellationToken);
                var message = kept == 0
                    ? $"{table}: the refresh produced no rows and the table holds none. Check the parser before publishing."
                    : $"{table}: the refresh produced no rows, so its {kept} existing rows are left as they are and are "
                      + "now older than the data published beside them. Check the parser before publishing.";
                progress?.Invoke(message);
                logBuilder?.AppendLine(message);
            }

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
                    var itemStats = await UpsertItemsAsync(connection, transaction, items, logBuilder, progress);

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
                    var questStats = await UpsertQuestsAsync(connection, transaction, quests, logBuilder, progress);

                    logBuilder?.AppendLine($"Inserted: {questStats.Inserted}, Updated: {questStats.Updated}, Deleted: {questStats.Deleted}");
                }

                // QuestRequirements 테이블 업데이트 (빈 리스트는 건너뜀, ReportSkippedTableAsync 참고)
                if (questRequirements is { Count: > 0 })
                {
                    progress?.Invoke($"Updating QuestRequirements table ({questRequirements.Count} requirements)...");
                    logBuilder?.AppendLine();
                    logBuilder?.AppendLine($"=== QuestRequirements Table Update ===");

                    await CreateQuestRequirementsTableIfNotExistsAsync(connection, transaction);
                    await RegisterQuestRequirementsSchemaAsync(connection, transaction);
                    var reqStats = await UpsertQuestRequirementsAsync(connection, transaction, questRequirements, logBuilder, progress);

                    logBuilder?.AppendLine($"Inserted: {reqStats.Inserted}, Updated: {reqStats.Updated}, Deleted: {reqStats.Deleted}");
                }
                else
                {
                    await ReportSkippedTableAsync("QuestRequirements");
                }

                // QuestTraderRequirements 테이블 업데이트.
                // An empty list is skipped and reported, like every other child table: a parse
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
                    var traderReqStats = await UpsertQuestTraderRequirementsAsync(connection, transaction, questTraderRequirements, logBuilder, progress);

                    logBuilder?.AppendLine($"Inserted: {traderReqStats.Inserted}, Updated: {traderReqStats.Updated}, Deleted: {traderReqStats.Deleted}");
                }
                else
                {
                    await ReportSkippedTableAsync("QuestTraderRequirements");
                }

                // QuestObjectives 테이블 업데이트 (빈 리스트는 건너뜀, ReportSkippedTableAsync 참고)
                if (questObjectives is { Count: > 0 })
                {
                    progress?.Invoke($"Updating QuestObjectives table ({questObjectives.Count} objectives)...");
                    logBuilder?.AppendLine();
                    logBuilder?.AppendLine($"=== QuestObjectives Table Update ===");

                    await CreateQuestObjectivesTableIfNotExistsAsync(connection, transaction);
                    await RegisterQuestObjectivesSchemaAsync(connection, transaction);
                    var objStats = await UpsertQuestObjectivesAsync(connection, transaction, questObjectives, logBuilder, progress);

                    logBuilder?.AppendLine($"Inserted: {objStats.Inserted}, Updated: {objStats.Updated}, Deleted: {objStats.Deleted}");
                }
                else
                {
                    await ReportSkippedTableAsync("QuestObjectives");
                }

                // OptionalQuests 테이블 업데이트.
                // Skips an empty list for the same reason the other child tables do: a parse
                // that returned nothing is a parse failure far more often than it is a game
                // that has no alternative quests left. The skip is reported, not silent.
                if (optionalQuests is { Count: > 0 })
                {
                    progress?.Invoke($"Updating OptionalQuests table ({optionalQuests.Count} optional quests)...");
                    logBuilder?.AppendLine();
                    logBuilder?.AppendLine($"=== OptionalQuests Table Update ===");

                    await CreateOptionalQuestsTableIfNotExistsAsync(connection, transaction);
                    await RegisterOptionalQuestsSchemaAsync(connection, transaction);
                    var optStats = await UpsertOptionalQuestsAsync(connection, transaction, optionalQuests, logBuilder, progress);

                    logBuilder?.AppendLine($"Inserted: {optStats.Inserted}, Updated: {optStats.Updated}, Deleted: {optStats.Deleted}");
                }
                else
                {
                    await ReportSkippedTableAsync("OptionalQuests");
                }

                // QuestRequiredItems 테이블 업데이트 (빈 리스트는 건너뜀, OptionalQuests와 동일한 이유)
                if (requiredItems is { Count: > 0 })
                {
                    progress?.Invoke($"Updating QuestRequiredItems table ({requiredItems.Count} required items)...");
                    logBuilder?.AppendLine();
                    logBuilder?.AppendLine($"=== QuestRequiredItems Table Update ===");

                    await CreateQuestRequiredItemsTableIfNotExistsAsync(connection, transaction);
                    await RegisterQuestRequiredItemsSchemaAsync(connection, transaction);
                    var itemStats = await UpsertQuestRequiredItemsAsync(connection, transaction, requiredItems, logBuilder, progress);

                    logBuilder?.AppendLine($"Inserted: {itemStats.Inserted}, Updated: {itemStats.Updated}, Deleted: {itemStats.Deleted}");
                }
                else
                {
                    await ReportSkippedTableAsync("QuestRequiredItems");
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

        /// <summary>
        /// How many rows a table holds, or 0 when it does not exist yet. Only ever called with a
        /// table name written in this file.
        /// </summary>
        private static async Task<long> CountRowsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string table,
            CancellationToken cancellationToken)
        {
            using (var exists = new SqliteCommand(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name", connection, transaction))
            {
                exists.Parameters.AddWithValue("@name", table);
                if (Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) == 0)
                    return 0;
            }

            using var count = new SqliteCommand($"SELECT COUNT(*) FROM \"{table}\"", connection, transaction);
            return Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
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
                    AltRequirementType TEXT,
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

            // CREATE TABLE IF NOT EXISTS does nothing to a table that already exists, and every
            // database this pipeline touches already has one, so a new column arrives through a
            // PRAGMA-guarded ALTER (the same pattern Quests.NormalizedName uses).
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var checkCmd = new SqliteCommand("PRAGMA table_info(QuestRequirements)", connection, transaction))
            using (var reader = await checkCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    existingColumns.Add(reader.GetString(1));
            }

            if (!existingColumns.Contains("AltRequirementType"))
            {
                using var alterCmd = new SqliteCommand(
                    "ALTER TABLE QuestRequirements ADD COLUMN AltRequirementType TEXT", connection, transaction);
                await alterCmd.ExecuteNonQueryAsync();
            }

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
                new() { Name = "AltRequirementType", DisplayName = "Alt Type", Type = ColumnType.Text, SortOrder = 4 },
                new() { Name = "DelayMinutes", DisplayName = "Delay (min)", Type = ColumnType.Integer, SortOrder = 5 },
                new() { Name = "GroupId", DisplayName = "Group ID", Type = ColumnType.Integer, IsRequired = true, SortOrder = 6 },
                new() { Name = "ContentHash", DisplayName = "Content Hash", Type = ColumnType.Text, SortOrder = 7 },
                new() { Name = "IsApproved", DisplayName = "Approved", Type = ColumnType.Boolean, IsRequired = true, SortOrder = 8 },
                new() { Name = "ApprovedAt", DisplayName = "Approved At", Type = ColumnType.DateTime, SortOrder = 9 },
                new() { Name = "UpdatedAt", DisplayName = "Updated At", Type = ColumnType.DateTime, SortOrder = 10 }
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

                var stats = await UpsertTradersAsync(connection, transaction, dbTraders, wikiCacheService, null, progress);

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
            StringBuilder? logBuilder,
            Action<string>? progress)
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
            // Traders.Id is upstream's own trader id, so the row key already IS the natural
            // identity: a trader that keeps its id is the same trader whatever it is called.
            RefreshGuards.AssertDeleteBudgetHeld("Traders", existingIds, newTraderIds, idsToDelete.Count, progress);
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

        #region The shared hash-keyed upsert

        /// <summary>
        /// The per-table half of <see cref="UpsertHashedRowsAsync"/>: everything that differs
        /// between the four hash-keyed child tables, and nothing that does not.
        /// </summary>
        private sealed class HashedTable<TRow> where TRow : IHashedRow
        {
            /// <summary>
            /// The SQL table name, which is also the name a delete-budget refusal reports.
            /// </summary>
            public required string Name { get; init; }

            /// <summary>
            /// What the run's log calls this table. Not always <see cref="Name"/>: the
            /// prerequisite and required-item tables have always logged as "Requirements" and
            /// "RequiredItems", and the log is read by hand against previous runs.
            /// </summary>
            public required string LogLabel { get; init; }

            /// <summary>
            /// The columns holding what each row is ABOUT, appended to the SELECT after
            /// ContentHash so <see cref="NaturalIdOf"/> reads them from ordinal 4 on. They are
            /// the columns the row's own <see cref="IHashedRow.NaturalId"/> projects, read back
            /// off the table: the delete budget compares the two sides, so they have to be the
            /// same projection or a re-key would read as a deletion.
            /// </summary>
            public required string IdentityColumns { get; init; }

            /// <summary>
            /// Reads <see cref="IdentityColumns"/> back as the same string the row's
            /// <see cref="IHashedRow.NaturalId"/> builds.
            /// </summary>
            public required Func<DbDataReader, string> NaturalIdOf { get; init; }

            /// <summary>The INSERT for a row the table does not hold yet.</summary>
            public required string InsertSql { get; init; }

            /// <summary>The UPDATE for a row it does, keyed by Id.</summary>
            public required string UpdateSql { get; init; }

            /// <summary>
            /// Binds every parameter both statements name, in the order (command, row, content
            /// hash, approval, approval time, write time).
            /// </summary>
            public required Action<SqliteCommand, TRow, string, bool, string?, string> Bind { get; init; }
        }

        /// <summary>
        /// Reads one identity column, a NULL reading as the empty string exactly as every
        /// hand-written copy of this read did.
        /// </summary>
        private static string IdentityText(DbDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);

        /// <summary>
        /// The upsert every hash-keyed child table shares: give each row its key, delete the
        /// rows the new list no longer produces (within the delete budget), then write the
        /// rest, keeping a reviewer's approval on a row whose content hash did not move and
        /// withdrawing it, out loud, on a row whose did.
        /// <para>
        /// One copy because four hand-written ones had already drifted: the loyalty-gate copy
        /// had lost the <c>[CHANGED]</c> line, which is the difference between a reviewer who
        /// knows which approvals a run took away and one who has to find out by reading the
        /// whole table again.
        /// </para>
        /// <para>
        /// <see cref="UpsertQuestObjectivesAsync"/> is deliberately not one of them: it also
        /// carries LocationPoints across an update, the one column no refresh can regenerate
        /// because no source it reads holds it. Bending this method around a fifth
        /// hand-entered column would bury that rule rather than share it.
        /// </para>
        /// </summary>
        private async Task<UpsertStats> UpsertHashedRowsAsync<TRow>(
            SqliteConnection connection,
            SqliteTransaction transaction,
            HashedTable<TRow> table,
            List<TRow> rows,
            StringBuilder? logBuilder,
            Action<string>? progress)
            where TRow : IHashedRow
        {
            var stats = new UpsertStats();
            var now = DateTime.UtcNow.ToString("o");

            // 기존 데이터 로드 (Id 기준으로 승인 상태 유지).
            // The identity columns come back beside the key because the delete budget is
            // measured over the thing a row is about, not over the key computed from it.
            var existingData =
                new Dictionary<string, (bool IsApproved, string? ApprovedAt, string? ContentHash)>(StringComparer.Ordinal);
            var existingIdentities = new List<string>();
            var selectSql =
                $"SELECT Id, IsApproved, ApprovedAt, ContentHash, {table.IdentityColumns} FROM {table.Name}";
            using (var selectCmd = new SqliteCommand(selectSql, connection, transaction))
            using (var reader = await selectCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    existingData[reader.GetString(0)] = (
                        !reader.IsDBNull(1) && reader.GetInt64(1) != 0,
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3));
                    existingIdentities.Add(table.NaturalIdOf(reader));
                }
            }

            // 새로 가져온 행의 ID 집합
            var newIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                row.Id = row.ComputeId();
                newIds.Add(row.Id);
            }

            // DB에 있지만 새 목록에 없는 항목 삭제
            var idsToDelete = existingData.Keys.Where(id => !newIds.Contains(id)).ToList();
            RefreshGuards.AssertDeleteBudgetHeld(
                table.Name, existingIdentities, rows.Select(r => r.NaturalId()), idsToDelete.Count, progress);
            foreach (var idToDelete in idsToDelete)
            {
                using var deleteCmd = new SqliteCommand(
                    $"DELETE FROM {table.Name} WHERE Id = @Id", connection, transaction);
                deleteCmd.Parameters.AddWithValue("@Id", idToDelete);
                await deleteCmd.ExecuteNonQueryAsync();
                stats.Deleted++;
            }

            // Upsert (기존 승인 상태 유지, 변경 시 승인 해제)
            foreach (var row in rows)
            {
                var newHash = row.ComputeContentHash();
                var exists = existingData.TryGetValue(row.Id, out var existing);

                var isApproved = false;
                string? approvedAt = null;

                if (exists)
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
                        // 승인되어 있었지만 내용이 변경됨. A reviewer reads this line to find what
                        // they have to look at again.
                        logBuilder?.AppendLine($"  [CHANGED] {row.Id} - approval reset due to content change");
                    }
                }

                using var cmd = new SqliteCommand(
                    exists ? table.UpdateSql : table.InsertSql, connection, transaction);
                table.Bind(cmd, row, newHash, isApproved, approvedAt, now);
                await cmd.ExecuteNonQueryAsync();

                if (exists) stats.Updated++; else stats.Inserted++;
            }

            logBuilder?.AppendLine(
                $"  {table.LogLabel}: {stats.Inserted} inserted, {stats.Updated} updated, "
                + $"{stats.Deleted} deleted, {stats.Unchanged} approvals preserved");
            return stats;
        }

        #endregion

        private Task<UpsertStats> UpsertQuestRequiredItemsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbQuestRequiredItem> requiredItems,
            StringBuilder? logBuilder,
            Action<string>? progress) =>
            UpsertHashedRowsAsync(
                connection,
                transaction,
                new HashedTable<DbQuestRequiredItem>
                {
                    Name = "QuestRequiredItems",
                    LogLabel = "RequiredItems",
                    // A quest, an item and what the quest wants done with it. SortOrder and the
                    // FIR flag are in the key but not here: see DbQuestRequiredItem.NaturalId.
                    IdentityColumns = "QuestId, ItemName, RequirementType",
                    NaturalIdOf = reader => RowHash.Natural(
                        IdentityText(reader, 4), IdentityText(reader, 5), IdentityText(reader, 6)),
                    InsertSql = @"
                        INSERT INTO QuestRequiredItems (Id, QuestId, ItemId, ItemName, Count, RequiresFIR, RequirementType, SortOrder, DogtagMinLevel, DogtagFaction, ContentHash, IsApproved, ApprovedAt, UpdatedAt)
                        VALUES (@Id, @QuestId, @ItemId, @ItemName, @Count, @RequiresFIR, @RequirementType, @SortOrder, @DogtagMinLevel, @DogtagFaction, @ContentHash, @IsApproved, @ApprovedAt, @UpdatedAt)",
                    UpdateSql = @"
                        UPDATE QuestRequiredItems SET
                            QuestId = @QuestId, ItemId = @ItemId, ItemName = @ItemName, Count = @Count,
                            RequiresFIR = @RequiresFIR, RequirementType = @RequirementType, SortOrder = @SortOrder,
                            DogtagMinLevel = @DogtagMinLevel, DogtagFaction = @DogtagFaction, ContentHash = @ContentHash,
                            IsApproved = @IsApproved, ApprovedAt = @ApprovedAt, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id",
                    Bind = AddRequiredItemParameters,
                },
                requiredItems,
                logBuilder,
                progress);

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

        private Task<UpsertStats> UpsertOptionalQuestsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbOptionalQuest> optionalQuests,
            StringBuilder? logBuilder,
            Action<string>? progress) =>
            UpsertHashedRowsAsync(
                connection,
                transaction,
                new HashedTable<DbOptionalQuest>
                {
                    Name = "OptionalQuests",
                    LogLabel = "OptionalQuests",
                    // A quest and the quest that can be done instead. The key hashes the same
                    // pair, so an alternative can never change without becoming a different
                    // row: the [CHANGED] branch is unreachable on this table alone, and the
                    // approval it preserves is the only half that runs.
                    IdentityColumns = "QuestId, AlternativeQuestId",
                    NaturalIdOf = reader => RowHash.Natural(IdentityText(reader, 4), IdentityText(reader, 5)),
                    InsertSql = @"
                        INSERT INTO OptionalQuests (Id, QuestId, AlternativeQuestId, ContentHash, IsApproved, ApprovedAt, UpdatedAt)
                        VALUES (@Id, @QuestId, @AlternativeQuestId, @ContentHash, @IsApproved, @ApprovedAt, @UpdatedAt)",
                    UpdateSql = @"
                        UPDATE OptionalQuests SET
                            QuestId = @QuestId, AlternativeQuestId = @AlternativeQuestId, ContentHash = @ContentHash,
                            IsApproved = @IsApproved, ApprovedAt = @ApprovedAt, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id",
                    Bind = AddOptionalQuestParameters,
                },
                optionalQuests,
                logBuilder,
                progress);

        private void AddOptionalQuestParameters(SqliteCommand cmd, DbOptionalQuest opt, string contentHash,
            bool isApproved, string? approvedAt, string now)
        {
            cmd.Parameters.AddWithValue("@Id", opt.Id);
            cmd.Parameters.AddWithValue("@QuestId", opt.QuestId);
            cmd.Parameters.AddWithValue("@AlternativeQuestId", opt.AlternativeQuestId);
            cmd.Parameters.AddWithValue("@ContentHash", contentHash);
            cmd.Parameters.AddWithValue("@IsApproved", isApproved ? 1 : 0);
            cmd.Parameters.AddWithValue("@ApprovedAt", (object?)approvedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedAt", now);
        }

        private async Task<UpsertStats> UpsertQuestObjectivesAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbQuestObjective> objectives,
            StringBuilder? logBuilder,
            Action<string>? progress)
        {
            var stats = new UpsertStats();
            var now = DateTime.UtcNow.ToString("o");

            // 기존 데이터 로드 (Id 기준으로 승인 상태 및 좌표 유지)
            var existingData = new Dictionary<string, (bool IsApproved, string? ApprovedAt, string? ContentHash, string? LocationPoints)>();
            var existingIds = new HashSet<string>();
            var existingObjectives = new List<string>();
            var selectSql =
                "SELECT Id, IsApproved, ApprovedAt, ContentHash, LocationPoints, QuestId, SortOrder FROM QuestObjectives";
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
                    existingObjectives.Add(RowHash.Natural(
                        reader.IsDBNull(5) ? "" : reader.GetString(5),
                        reader.IsDBNull(6) ? 0 : reader.GetInt32(6)));
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
            RefreshGuards.AssertDeleteBudgetHeld(
                "QuestObjectives", existingObjectives, objectives.Select(o => o.NaturalId()),
                idsToDelete.Count, progress);
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
            StringBuilder? logBuilder,
            Action<string>? progress)
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
            // The row-id fallback. Items.Id is base64 of the item's wiki page URL and nothing
            // is computed over it, so there is no key scheme here to migrate; the page an item
            // lives on is the only identity this table has.
            RefreshGuards.AssertDeleteBudgetHeld("Items", existingIds, newItemIds, idsToDelete.Count, progress);
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
            StringBuilder? logBuilder,
            Action<string>? progress)
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
            // The row-id fallback, and the deliberate one. A quest's natural identity is its
            // external game id, but the published database carries none on any of its 488 rows
            // (that is what BsgIdBackfillService repairs), so measuring on BsgId would read the
            // whole table as deleted on exactly the run this budget must not refuse. The row key
            // is what QuestIdentityResolver carries across a rename and is the identity in
            // practice; AssertMatchRateHeld guards the same loss ten times tighter, at 5%.
            RefreshGuards.AssertDeleteBudgetHeld("Quests", existingIds, newQuestIds, idsToDelete.Count, progress);
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
        private Task<UpsertStats> UpsertQuestTraderRequirementsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbQuestTraderRequirement> requirements,
            StringBuilder? logBuilder,
            Action<string>? progress) =>
            UpsertHashedRowsAsync(
                connection,
                transaction,
                new HashedTable<DbQuestTraderRequirement>
                {
                    Name = "QuestTraderRequirements",
                    LogLabel = "QuestTraderRequirements",
                    // Which gate this is: a quest and a trader. See
                    // DbQuestTraderRequirement.NaturalId.
                    IdentityColumns = "QuestId, TraderId",
                    NaturalIdOf = reader => RowHash.Natural(IdentityText(reader, 4), IdentityText(reader, 5)),
                    InsertSql = @"
                        INSERT INTO QuestTraderRequirements
                            (Id, QuestId, TraderId, TraderName, RequiredLevel, ContentHash, IsApproved, ApprovedAt, UpdatedAt)
                        VALUES (@Id, @QuestId, @TraderId, @TraderName, @RequiredLevel, @ContentHash, @IsApproved, @ApprovedAt, @UpdatedAt)",
                    UpdateSql = @"
                        UPDATE QuestTraderRequirements SET
                            QuestId = @QuestId, TraderId = @TraderId, TraderName = @TraderName,
                            RequiredLevel = @RequiredLevel, ContentHash = @ContentHash,
                            IsApproved = @IsApproved, ApprovedAt = @ApprovedAt, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id",
                    Bind = AddTraderRequirementParameters,
                },
                requirements,
                logBuilder,
                progress);

        private void AddTraderRequirementParameters(SqliteCommand cmd, DbQuestTraderRequirement req, string contentHash,
            bool isApproved, string? approvedAt, string now)
        {
            cmd.Parameters.AddWithValue("@Id", req.Id);
            cmd.Parameters.AddWithValue("@QuestId", req.QuestId);
            cmd.Parameters.AddWithValue("@TraderId", req.TraderId);
            cmd.Parameters.AddWithValue("@TraderName", req.TraderName);
            cmd.Parameters.AddWithValue("@RequiredLevel", req.RequiredLevel);
            cmd.Parameters.AddWithValue("@ContentHash", contentHash);
            cmd.Parameters.AddWithValue("@IsApproved", isApproved ? 1 : 0);
            cmd.Parameters.AddWithValue("@ApprovedAt", (object?)approvedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedAt", now);
        }

        private Task<UpsertStats> UpsertQuestRequirementsAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            List<DbQuestRequirement> requirements,
            StringBuilder? logBuilder,
            Action<string>? progress) =>
            UpsertHashedRowsAsync(
                connection,
                transaction,
                new HashedTable<DbQuestRequirement>
                {
                    Name = "QuestRequirements",
                    LogLabel = "Requirements",
                    // The prerequisite edge a row is about, without the GroupId the key adds.
                    // Every one of the 794 published rows changes key on the first 1.1 run
                    // while its edge stands, and the delete budget has to tell that re-key
                    // apart from a deletion. See DbQuestRequirement.NaturalId.
                    //
                    // Collector's rows used to be exempt from the delete pass so that
                    // AddCollectorKappaRequirementsAsync could own them, but that function only
                    // ever inserted, so a quest that lost its Kappa flag kept its Collector row
                    // forever: Collector shipped 248 prerequisites for 247 flagged quests, the
                    // extra being Grenadier. The synthesis now rebuilds the set itself, so the
                    // exemption is gone and the shared delete pass removes what the wiki parse
                    // no longer produces.
                    IdentityColumns = "QuestId, RequiredQuestId",
                    NaturalIdOf = reader => RowHash.Natural(IdentityText(reader, 4), IdentityText(reader, 5)),
                    InsertSql = @"
                        INSERT INTO QuestRequirements (Id, QuestId, RequiredQuestId, RequirementType, AltRequirementType, DelayMinutes, GroupId, ContentHash, IsApproved, ApprovedAt, UpdatedAt)
                        VALUES (@Id, @QuestId, @RequiredQuestId, @RequirementType, @AltRequirementType, @DelayMinutes, @GroupId, @ContentHash, @IsApproved, @ApprovedAt, @UpdatedAt)",
                    UpdateSql = @"
                        UPDATE QuestRequirements SET
                            QuestId = @QuestId, RequiredQuestId = @RequiredQuestId, RequirementType = @RequirementType,
                            AltRequirementType = @AltRequirementType,
                            DelayMinutes = @DelayMinutes, GroupId = @GroupId, ContentHash = @ContentHash,
                            IsApproved = @IsApproved, ApprovedAt = @ApprovedAt, UpdatedAt = @UpdatedAt
                        WHERE Id = @Id",
                    Bind = AddRequirementParameters,
                },
                requirements,
                logBuilder,
                progress);

        private void AddRequirementParameters(SqliteCommand cmd, DbQuestRequirement req, string contentHash,
            bool isApproved, string? approvedAt, string now)
        {
            cmd.Parameters.AddWithValue("@Id", req.Id);
            cmd.Parameters.AddWithValue("@QuestId", req.QuestId);
            cmd.Parameters.AddWithValue("@RequiredQuestId", req.RequiredQuestId);
            cmd.Parameters.AddWithValue("@RequirementType", req.RequirementType);
            // Empty is not a second type: it would reach the app as the status "", which
            // IsStatusSatisfied has no arm for, so it is stored as NULL like an absent one.
            cmd.Parameters.AddWithValue(
                "@AltRequirementType",
                string.IsNullOrEmpty(req.AltRequirementType) ? DBNull.Value : req.AltRequirementType);
            cmd.Parameters.AddWithValue("@DelayMinutes", (object?)req.DelayMinutes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@GroupId", req.GroupId);
            cmd.Parameters.AddWithValue("@ContentHash", contentHash);
            cmd.Parameters.AddWithValue("@IsApproved", isApproved ? 1 : 0);
            cmd.Parameters.AddWithValue("@ApprovedAt", (object?)approvedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UpdatedAt", now);
        }

        #endregion

        #region Helper Methods

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

        #endregion

        public void Dispose()
        {
            // Nothing to dispose currently
        }
    }

    #region Models

    /// <summary>
    /// What the shared child-table upsert needs of a row: the key it is filed under, the hash
    /// an approval covers, and the identity the delete budget is measured over.
    /// <para>
    /// Four tables carry all three and nothing else (QuestRequirements,
    /// QuestTraderRequirements, OptionalQuests, QuestRequiredItems), so
    /// <see cref="RefreshDataService.UpsertHashedRowsAsync"/> writes all four.
    /// QuestObjectives deliberately does not implement this: it carries a fifth thing across an
    /// update, the hand-entered LocationPoints, and keeps its own upsert saying so.
    /// </para>
    /// </summary>
    internal interface IHashedRow
    {
        /// <summary>
        /// The row key. The upsert assigns it from <see cref="ComputeId"/> before it writes,
        /// so a caller never has to.
        /// </summary>
        string Id { get; set; }

        /// <summary>What the row is FILED UNDER. See <see cref="RowHash.Key"/>.</summary>
        string ComputeId();

        /// <summary>
        /// Every field an approval covers, so a reviewer's approval survives a run that changed
        /// nothing about the row. See <see cref="RowHash.Content"/>.
        /// </summary>
        string ComputeContentHash();

        /// <summary>What the row is ABOUT, which is not what it is filed under. See <see cref="RowHash.Natural"/>.</summary>
        string NaturalId();
    }

    /// <summary>
    /// The row key and content hash every child table's identity rests on: SHA-256 over the
    /// fields joined with <c>|</c>, base64, truncated.
    /// <para>
    /// One copy, because these values are a contract with the published database rather than an
    /// implementation detail. A key that changes deletes and reinserts every row of its table on
    /// the next publish, and a content hash that changes drops every approval a reviewer made,
    /// neither of which surfaces as a failure. Five hand-written copies of the same six lines
    /// were five chances to drift; the exact strings are pinned in RefreshGuardTests.
    /// </para>
    /// <para>
    /// The 1.1 regeneration is exactly that reinsert, on purpose: all 794 published
    /// QuestRequirements rows change key and come back with <c>IsApproved</c> at 0, where every
    /// one of them is 1 today. That is the editor's own review state and nothing else. No code
    /// under <c>TarkovHelper/</c> reads the column, so no install can see it; the reviewer reads
    /// the whole table again for this regeneration in any case.
    /// </para>
    /// </summary>
    internal static class RowHash
    {
        /// <summary>
        /// What a row is ABOUT, as opposed to <see cref="Key"/>, which is what a row is FILED
        /// UNDER: the fields naming the thing in the game, with every field the key scheme adds
        /// for uniqueness or ordering left out.
        /// <para>
        /// The two are not the same and must not be confused. A key is recomputed whenever the
        /// scheme changes, and the 1.1 regeneration changes it twice over: prerequisite rows move
        /// from a wiki-assigned GroupId to 0, and Collector's rows move from a hand-built
        /// concatenation onto <see cref="Key"/>. Every row of the published table gets a new key
        /// while the prerequisite edge underneath it is unchanged, which is a re-key and not a
        /// deletion. <see cref="RefreshDataService.RefreshGuards.AssertDeleteBudgetHeld"/>
        /// measures on this so it cannot mistake one for the other.
        /// </para>
        /// <para>
        /// Joined with U+001F (unit separator), a character no wiki title, item name or id
        /// contains, so no two identities can collide by concatenation.
        /// </para>
        /// </summary>
        public static string Natural(params object?[] fields) =>
            string.Join(NaturalSeparator, fields.Select(f => f?.ToString() ?? ""));

        /// <summary>
        /// The unit separator, U+001F. Written as a code point rather than as a literal so it
        /// stays visible to a reader of this file.
        /// </summary>
        private const char NaturalSeparator = (char)0x1f;

        /// <summary>
        /// A row key: 22 url-safe base64 characters over a per-table tag and the fields that
        /// identify the row. A null field joins as the empty string, a bool as True/False.
        /// </summary>
        public static string Key(string tag, params object?[] fields) =>
            Truncate(string.Join("|", fields.Prepend<object?>(tag)), 22).Replace("/", "_").Replace("+", "-");

        /// <summary>
        /// A change marker over every field an approval covers. Not url-safe: it is compared,
        /// never put in a URL or a key.
        /// </summary>
        public static string Content(params object?[] fields) =>
            Truncate(string.Join("|", fields), 16);

        private static string Truncate(string raw, int length)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
            return Convert.ToBase64String(hash).Substring(0, length);
        }
    }

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

        /// <summary>
        /// The game prerequisites no row could be written for, because the quest they point at
        /// was not imported. See <see cref="RefreshDataService.BuildRequirements"/> for why
        /// these are named rather than counted against a threshold.
        /// </summary>
        public List<StrandedPrerequisite> StrandedPrerequisites { get; set; } = new();
    }

    /// <summary>
    /// One game prerequisite that could not become a row, and why. The quest ships with a
    /// shorter chain than the game describes, so each one is named in the run log and the
    /// refresh report.
    /// </summary>
    public class StrandedPrerequisite
    {
        /// <summary>The quest whose prerequisite list lost the entry.</summary>
        public string Quest { get; set; } = "";

        /// <summary>The external game id of the prerequisite that was dropped.</summary>
        public string TaskId { get; set; } = "";

        /// <summary>
        /// Expected when the target record has no wiki page (a quest the patch removed that the
        /// API still lists); a pipeline problem when the record exists and was still not
        /// imported.
        /// </summary>
        public string Reason { get; set; } = "";
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
    public class DbQuestTraderRequirement : IHashedRow
    {
        public string Id { get; set; } = "";
        public string QuestId { get; set; } = "";
        public string TraderId { get; set; } = "";
        public string TraderName { get; set; } = "";
        public int RequiredLevel { get; set; }

        public string ComputeId() => RowHash.Key("QTR", QuestId, TraderId);

        /// <summary>
        /// Which gate this is: a quest and a trader. The key hashes the same two fields today,
        /// so this is the projection stated rather than derived, and it stays correct if the key
        /// ever gains a third. See <see cref="RowHash.Natural"/>.
        /// </summary>
        public string NaturalId() => RowHash.Natural(QuestId, TraderId);

        public string ComputeContentHash() => RowHash.Content(QuestId, TraderId, TraderName, RequiredLevel);
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
    public class DbQuestRequirement : IHashedRow
    {
        public string Id { get; set; } = ""; // Hash-based ID (QuestId + RequiredQuestId + GroupId)
        public string QuestId { get; set; } = "";
        public string RequiredQuestId { get; set; } = "";
        public string RequirementType { get; set; } = "Complete"; // Complete, Accept, Fail

        /// <summary>
        /// A second requirement type the same row is also satisfied by, or NULL (the usual case).
        /// Same vocabulary as <see cref="RequirementType"/>, and never a repeat of it: it exists
        /// for the prerequisites the game reports as satisfied by two states no single type
        /// covers, "complete or failed" above all. Additive, so a build that predates the column
        /// simply never reads it (see MapRequirementStatuses).
        /// </summary>
        public string? AltRequirementType { get; set; }

        public int? DelayMinutes { get; set; } // 시간 지연 (분 단위)
        public int GroupId { get; set; } // OR 그룹 ID (같은 그룹 내에서는 OR 조건)
        public string? ContentHash { get; set; } // 변경 감지용 해시
        public bool IsApproved { get; set; } // 사용자 승인 여부
        public DateTime? ApprovedAt { get; set; } // 승인 시간

        /// <summary>
        /// 고유 ID 생성 (QuestId + RequiredQuestId + GroupId 기반 해시)
        /// <para>
        /// Deliberately blind to both type columns, as it always has been: the key names which
        /// pair a row is about, not what satisfies it. A prerequisite whose type changes is the
        /// same row updated in place, so no approval, no child row and no user-visible identity
        /// moves when AltRequirementType arrives.
        /// </para>
        /// </summary>
        public string ComputeId() => RowHash.Key("REQ", QuestId, RequiredQuestId, GroupId);

        /// <summary>
        /// Which prerequisite edge this row is: the quest and the quest it waits on. GroupId is
        /// deliberately left out, unlike in the key: the group number says how this edge is
        /// combined with the quest's other edges, not which edge it is, and it is assigned by
        /// whichever source produced the row. The wiki numbered its terms from 1; the game has
        /// no groups at all, so every row starts at 0 and only
        /// <see cref="RefreshDataService.ExpandExclusiveAlternatives"/> moves a pair off it.
        /// Every one of the 794 rows in the published table therefore changes key on the first
        /// 1.1 run while its edge survives, which is what
        /// <see cref="RefreshDataService.RefreshGuards.AssertDeleteBudgetHeld"/> has to be able
        /// to tell apart from a deletion. See <see cref="RowHash.Natural"/>.
        /// </summary>
        public string NaturalId() => RowHash.Natural(QuestId, RequiredQuestId);

        /// <summary>
        /// 현재 데이터의 해시 생성 (변경 감지용)
        /// </summary>
        public string ComputeContentHash() =>
            RowHash.Content(QuestId, RequiredQuestId, RequirementType, AltRequirementType, DelayMinutes, GroupId);
    }

    /// <summary>
    /// 선택적 퀘스트 (Other Choices) 데이터 모델
    /// 같은 아이템을 제출해 완료할 수 있는 대체 퀘스트들
    /// </summary>
    public class DbOptionalQuest : IHashedRow
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
        public string ComputeId() => RowHash.Key("OPT", QuestId, AlternativeQuestId);

        /// <summary>
        /// Which alternative this is: a quest and the quest that can be done instead. The key
        /// hashes the same pair today. See <see cref="RowHash.Natural"/>.
        /// </summary>
        public string NaturalId() => RowHash.Natural(QuestId, AlternativeQuestId);

        /// <summary>
        /// 현재 데이터의 해시 생성 (변경 감지용)
        /// </summary>
        public string ComputeContentHash() => RowHash.Content(QuestId, AlternativeQuestId);
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
        public string ComputeId() => RowHash.Key("OBJ", QuestId, SortOrder);

        /// <summary>
        /// Which objective this is: a quest and the objective's position in that quest's list.
        /// The row-id fallback, in effect. Nothing else on an objective identifies it - the
        /// description is free wiki text that a copy edit rewrites without the objective
        /// changing - so the position is what there is, and it is what the key already uses.
        /// See <see cref="RowHash.Natural"/>.
        /// </summary>
        public string NaturalId() => RowHash.Natural(QuestId, SortOrder);

        /// <summary>
        /// 현재 데이터의 해시 생성 (변경 감지용)
        /// </summary>
        public string ComputeContentHash() => RowHash.Content(
            QuestId, SortOrder, ObjectiveType, Description, TargetType, TargetCount, ItemName,
            RequiresFIR, MapName, LocationName, Conditions, DogtagMinLevel, DogtagFaction);
    }

    /// <summary>
    /// 퀘스트 필요 아이템 데이터 모델 (Related Quest Items 테이블에서 파싱)
    /// </summary>
    public class DbQuestRequiredItem : IHashedRow
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
        public string ComputeId() =>
            RowHash.Key("ITEM", QuestId, ItemName, RequirementType, RequiresFIR, SortOrder);

        /// <summary>
        /// Which item requirement this is: a quest, an item and what the quest wants done with
        /// it. SortOrder is left out, unlike in the key, where it is there only to keep two rows
        /// for one item apart; it is the position the wiki table happened to list the item in,
        /// so a row inserted above re-keys every row below it without any of them changing. The
        /// FIR flag is left out for the same reason it is in the content hash instead: a
        /// requirement that starts or stops demanding Found in Raid is the same requirement,
        /// changed. On the published table the projection is 632 identities over 638 rows.
        /// See <see cref="RowHash.Natural"/>.
        /// </summary>
        public string NaturalId() => RowHash.Natural(QuestId, ItemName, RequirementType);

        /// <summary>
        /// 현재 데이터의 해시 생성 (변경 감지용)
        /// </summary>
        public string ComputeContentHash() => RowHash.Content(
            QuestId, ItemName, Count, RequiresFIR, RequirementType, DogtagMinLevel, DogtagFaction);
    }

    #endregion
}
