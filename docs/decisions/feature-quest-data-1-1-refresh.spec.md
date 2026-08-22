# Quest Data 1.1 Refresh - Technical Spec

- **Created**: 2026-08-21

> The sibling `feature-quest-data-1-1-refresh.md` holds the product decision.
> Write this on the work's branch and merge it in the same PR as the work.
> Nothing is kept current: fields are written once, discoveries are appended. A
> later change that reverses a decision here appends `Superseded by <doc>` below
> this line, in the PR that reverses it.

## Summary

Four ideas carry the design. First, a **json.tarkov.dev client** replaces the
dead GraphQL client in TarkovDBEditor and feeds the existing tarkov.dev cache
files, now carrying the task fields the old queries omitted (minimum level,
Kappa flag, per-trader loyalty requirements, prerequisites, faction) plus Korean
and Japanese names from the API's locale files. Second, **identity follows the
external ID**: a wiki page that matches a task whose ID the previous database
already holds keeps that row's `Id` and `NormalizedName` under its new name, and
a new `Quests.NormalizedName` column, pinned to the expression the app already
computes for itself, makes the carry-over effective in every fielded build.
Third, the schema stays **forward compatible with the July build**: two additive
pieces, `Quests.NormalizedName` and a `QuestTraderRequirements` table, under the
constraints listed in Design, so the publish goes out as data format 1.
Fourth, the regeneration is **reviewed through a generated diff report** and
guarded by the first content tests the published database has ever had, plus a
legacy-smoke run of the fielded release against the candidate data before it is
published.

The pipeline changes are extensions of the paths that exist: the wiki layer
keeps supplying page identity, objective text, required items, location,
editions, prestige and DSP exactly as today; the tarkov.dev enrichment that
already supplies `BsgId` and names grows to supply the game rules.

## Non-Goals

- No wiki loyalty parser. Loyalty comes from the JSON API; the wiki's four
  phrasings are not parsed (the roadmap's "wiki path" is not taken).
- No change to the app's quest or item primary-key scheme, and no app-side
  progress migration. Carry-over is achieved in the data.
- No app code for loyalty (phase 4) or Collector conditions (phase 5). The app
  change in this phase is limited to what the publish and release need: none is
  strictly required, and the one optional hardening (reading objective IDs) is
  declined below.
- No stable objective identity. `QuestObjectives` keeps its positional IDs;
  the report lists quests whose objective list changed shape.
- No mode-specific data: the client reads the `regular` game mode only. The
  `pvp-season` set is a strict subset of it, lacking the 23 Ref Arena-side tasks
  and the 3 New Beginning prestige tasks, and differs in field values on one
  shared task (provide-viewership); the PvP Season profile therefore shows those
  26 quests too.
- No hot-reload of quest rows on a data update; the progress services keep
  reloading at start, as today.
- No import of tarkov.dev reputation requirements into `MinScavKarma`; the
  wiki's Scav karma line stays the source, since the app's karma gate cannot
  express the API's "at most" comparisons.
- No change to the publish tool's channel mechanics (phase 2 owns them).

## Current Behavior

Verified in the working tree at HEAD a214a95 (branch triton == main), the fielded
tag v2026.7.0, the published `data/v1/tarkov_data.db` (token 1.0.10), and live
upstream probes on 2026-08-21.

### Upstream

- `POST https://api.tarkov.dev/graphql` returns HTTP 422
  `{"errors":["GraphQL server unavailable. Try again later."]}`; down since
  about 2026-07-22 (the-hideout/tarkov-api issue 474). The maintainers point at
  `https://json.tarkov.dev/` (tarkov-data-manager issue 851), which the
  tarkov.dev front end itself reads. It is plain GET, no authentication:
  `/endpoints` lists `/{regular|pve|pvp-season}/{tasks,items,hideout,traders,
  maps,barters,crafts,prices/{id}}`; data files are language neutral (every
  translatable string is a key such as `"<taskId> name"`) and locale files live
  at `{path}_{lang}` (`tasks_en`, `tasks_ko`, `tasks_ja`, likewise for items,
  traders, hideout); the data file carries `ETag` and `Last-Modified` (tasks
  rebuilt 2026-08-21 01:33Z), locale files `Cache-Control: max-age=691200`.
- `regular/tasks`: 517 tasks. Per task: `id`, `wikiLink` (517/517; 8 point at
  titles that are not wiki pages, 10 titles are shared by two or three tasks),
  `normalizedName`, `trader` (id), `minPlayerLevel` (282 are 0),
  `kappaRequired` (13 true: Collector, Chemical 1-3, The Tarkov Shooter 1-4,
  Postman Pat 1-2, Sew it Good 1-2, Shooter Born in Heaven),
  `lightkeeperRequired`, `traderRequirements` (entries
  `{requirementType, compareMethod, value, trader}`; 110 tasks carry at least
  one entry; `level/>=` entries: 112 on 99 tasks, of which 15 entries on 5 tasks
  name a trader other than the giver:
  chemical-part-3, thirsty-hounds, broadcast-part-1, the-good-times-part-1,
  collector; `reputation` entries on 12 tasks), `taskRequirements`
  (`[{task, status[]}]`, statuses complete/active/failed, AND semantics, no OR
  groups; 296 tasks empty), `availableDelaySecondsMin/Max`, `otherRequirements`
  (story flags the app does not model), `factionName` (Any 505 / BEAR 6 /
  USEC 6), typed `objectives` with stable ids. No task-kind or edition key.
  Korean names: 289/517 real; Japanese: 0/517 (English fallback).
- `regular/items`: 5,312 items, `wikiLink` on 5,145, six image links each;
  `regular/hideout`: 26 stations, level `traderRequirements` in the same shape
  (5 entries); `regular/traders`: 16 traders (new: Survivor); the 11 nicknames
  `Quests.Trader` uses today match the API strings byte for byte.
- Wiki: `Template:Infobox quest` stopped rendering `reqkappa` on 2026-08-03
  (rev 348972) and the values were never updated (246 pages still "Yes" vs 13
  kappa tasks). Requirements are free-text bullets under `==Requirements==` in
  four loyalty spellings; `|previous` is empty on freed quests, and on some that
  the game still chains (Sew it Good - Part 4). The `Quests` and `Collector`
  pages are admin-locked indefinitely, the spot-check pages until 2026-09-14,
  with 179 edits on 78 quest pages in the week before writing. `Special:Export`
  answers 200 again with the editor's user agent (it answered 403 throughout the
  2026-06-13 run, which is why that run produced an empty cache).
- Churn, measured against the published 488 quests: 127 names have no wiki page
  under that title; by external ID, 91 are renames to a page that still exists
  (e.g. A Shooter Born in Heaven -> Shooter Born in Heaven, Gunsmith - Part 7 ->
  Gunsmith - M4A1), 35 are removed from the game (pages kept as historical; the
  API still lists all 35), 1 is a punctuation redirect. No chain was merged:
  multi-part chains were de-numbered. Eight titles now belong to a different
  task than the row that carries them (Sew it Good - Part 2/3/4 rotated, The
  Punisher - Part 1/2/3 rotated, The Tarkov Shooter - Part 6/7 shifted; the
  "Sew it Good - Part 4" page now belongs to task 5ae4496986f774459e77beb6,
  formerly Part 3). Of the 176 wiki titles the database lacks: 92 rename
  targets, 18 KORD BREACH seasonal quests, 4 new 1.1 quests, 15 quests added
  between January and April, and 47 pages of the Arena game's questline that
  the API does not carry.

### Published data and pipeline

- `Quests.Id` is base64 of the wiki page URL, recomputed from the title on
  every run (`RefreshDataService.LoadQuestsFromCacheAsync`); `Items.Id` is the
  url-safe variant (`TarkovWikiDataService.GenerateWikiId`). `BsgId` is NULL on
  488/488 quests and 4014/4014 items since the 2026-01-14 regeneration (1.0.8);
  the 1.0.7 snapshot (commit ebbc60c, 2025-12-19) holds 473 quest and 2648 item
  IDs keyed by the same `Id`s, and all 473 are live task IDs on the JSON API
  today. The last real regeneration is January's: every quest row carries
  `UpdatedAt` 2026-01-15T00:09:39Z; the 1.0.10 publish was a translate-only
  patch (`fix-quest-name-localization.md`).
- There is no `NormalizedName` column. Both the fielded build and main
  feature-detect one (`QuestDbService.LoadBaseQuestsAsync` via
  `ColumnExistsAsync`) and otherwise compute
  `LOWER(REPLACE(REPLACE(REPLACE(Name,' ','-'),'''',''),'.',''))`; 228 of 488
  names differ between that form and the tarkov.dev style the editor's
  `NormalizeQuestName` produces (`sew-it-good---part-4` vs `sew-it-good-part-4`).
- Progress identity: `QuestProgress(ProfileId, Id, NormalizedName, Status)`;
  the write key is the wiki `Id` with the normalized name stored beside it
  (`QuestProgressService.ProgressKeyOf`); the in-memory read dictionary is
  keyed by the stored name when present (`UserDataDbService.
  LoadQuestProgressAsync`), and `GetStatus` tries the id, then the name. A
  rename changes both, so the row orphans. No alias mechanism exists anywhere;
  the app never decodes a `Quests.Id` (`WikiPageLink` carries the URL).
  `ObjectiveProgress` is keyed `"{normalizedName}:{index}"`.
- Fielded-build hazards, identical on main and v2026.7.0:
  `QuestProgressService.IsStatusSatisfied` accepts only active/start/accept,
  complete, failed/fail, so an unknown `QuestRequirements.RequirementType`
  locks a quest forever; `SettingsService.ShouldIncludeTask` is string equality,
  so any non-NULL `Faction` other than the player's hides the quest;
  `QuestDbService` hard-requires ten `Quests` columns (Id, Name, NameKO, NameJA,
  Trader, Location, MinLevel, MinScavKarma, KappaRequired, Faction) and
  `ItemDbService` twelve `Items` columns; the hideout tables are read
  unconditionally (`HideoutDbService`).
- Kappa: `KappaRequired = 1` on 248 quests including Collector; Collector's
  synthesized `QuestRequirements` hold 248 rows, 247 kappa quests plus a stale
  Grenadier row, because `UpsertQuestRequirementsAsync` exempts Collector-owned
  rows from its delete loop and `AddCollectorKappaRequirementsAsync` deletes
  only the self-reference. `QuestGraphService.GetCollectorProgress` counts
  every `ReqKappa` task including Collector; `CollectorPage` walks the
  synthesized `Previous` graph.
- Pipeline entry points (`TarkovDBEditor/MainWindow.xaml.cs`): Export Wiki
  Quests (wiki category crawl + `Special:Export` into
  `wiki_data/cache/quest_cache.json`, then a live GraphQL call that now fails
  after the cache is saved), Cache Tarkov Dev Data (four live GraphQL fetches;
  on failure old cache files are kept), Fetch Wiki Data (`RefreshDataAsync`:
  item crawl + icons + quests, tarkov.dev from cache only), Refresh Data (from
  Cache) (quests only; Items untouched; returns success with zero quests when
  the wiki cache is empty), Refresh Hideout Data (cache first, live only when
  empty), Publish DB Update. The only tarkov.dev query ever sent for tasks is
  `{ tasks(lang: en) { id tarkovDataId name normalizedName wikiLink trader { name } } ... }`;
  no level, loyalty, kappa or prerequisite field was ever fetched.
- Matching: the tarkov.dev cache is a dictionary keyed by `wikiLink` (a later
  task silently overwrites an earlier one with the same link); a wiki quest is
  matched by `wikiLink`, else by `NormalizeQuestName(title)`; unmatched quests
  are kept with `BsgId` NULL and English names; a task without a wiki page is
  never materialized. Stale quest Ids are deleted on every non-empty run
  (`UpsertQuestsAsync`); prerequisites pointing at a page outside the cache are
  dropped silently; cache entries are never pruned.
- Child-table upserts (`UpsertQuestRequirementsAsync`,
  `UpsertQuestObjectivesAsync`, `UpsertOptionalQuestsAsync`,
  `UpsertQuestRequiredItemsAsync`) are table-global diffs by hashed identity
  with approval carried over on an unchanged content hash; `QuestObjectives`
  identity is `OBJ|QuestId|SortOrder`; `OptionalQuests` and
  `QuestRequiredItems` are emptied by an empty parse while Quests, Requirements
  and Objectives skip an empty list. Foreign keys are enforced on every
  Microsoft.Data.Sqlite connection by default (the bundled e_sqlite3 reports
  `PRAGMA foreign_keys` = 1 and rejects a dangling child insert with error 19),
  so stale-quest deletes already cascade and the published database has no
  dangling child row; a child insert that references a quest not yet in the
  transaction fails the whole refresh.
- Adding a `Quests` column touches `CreateQuestsTableIfNotExistsAsync`
  (`CREATE TABLE IF NOT EXISTS` only; no Quests column migration exists since
  commit 5d4e6e9 removed the ALTER list, and the shipped table was grown by it),
  `RegisterQuestsSchemaAsync` (`_schema_meta.SchemaJson`, 19 columns listed of
  32 physical), `DbQuest`, the INSERT/UPDATE lists and `AddQuestParameters`,
  the ordinal reader in `QuestRequirementsViewModel`, `TarkovDBEditor/CLAUDE.md`
  and `docs/database-schema.md`. The PRAGMA-guarded ALTER block in
  `CreateQuestObjectivesTableIfNotExistsAsync` is the surviving pattern.
- Items: wiki category crawl only (`FetchAndProcessItemsAsync`), icons via
  `WikiCacheService.DownloadIconsAsync` into `wiki_data/icons/{Items.Id}{ext}`
  (skip if exists; extension from the URL), tarkov.dev enrichment from cache
  with no hard stop when the cache is missing. New items enter the database
  only through Fetch Wiki Data. `DataPublishService.ItemIconGroup` publishes
  `*.png` only into `TarkovHelper/Assets/icons`, where
  `ImageCacheService.GetLocalItemIcon` reads `{Items.Id}.png`. Git tracks the
  folder under two case spellings (`Assets/icons`, 3933 files, and
  `Assets/Icons`, 115 item pngs plus marker files); 4011/4014 items have an
  icon, 3 audio tapes do not, 11 pngs have no item. Hideout requirements are
  keyed by tarkov.dev item id and the app bridges them through `Items.BsgId`
  (`HideoutDbService`, `LEFT JOIN Items i ON h.ItemId = i.BsgId`): 0/317 rows
  resolve today.
- Publish and release: `DataPublishService` writes the highest `data/v<N>/`,
  stamps `user_version`, mirrors to `TarkovHelper/Assets/` while N is 1, writes
  manifest and index last, never creates a format directory; the window
  suggests token 1.0.11. `DataFormatDriftTests` goes red on a new column or
  table (Widened) and proposes `DataFormatBaseline.v1.proposed.json`. CI runs
  on pushes to main and on PRs; main is unprotected and every past data publish
  was a direct push. The only fielded build is v2026.7.0 (2026-07-24): it polls
  `TarkovHelper/Assets/{db_version.txt,tarkov_data.db}` under the old repository
  name (GitHub redirects) every five minutes, compares the token for equality,
  moves the download into place with no validation, and has no switch to disable
  the check. Phases 1 and 2 are merged and untagged.
- Tests: no test asserts quest count, Kappa count, NULL rates, a named quest,
  Collector's row count or icon coverage; `QuestDataCoverageTests` (30 percent
  Korean) is skipped with a reason naming a branch that never existed;
  `E2EQuestDataTests` (in CI) and the E2E fixtures derive rows from the seed,
  and the seasonal E2E fixture needs a quest named exactly "Collector" with a
  non-quest-item required item. No TarkovDBEditor service other than
  `DataPublishService` and `ResolveLocalizedQuestName` has a test. No
  legacy-smoke harness exists and no environment variable substitutes the asset
  database (`TARKOVHELPER_CONFIG_PATH` covers user data only).

## Design

### 1. json.tarkov.dev client (TarkovDBEditor)

`Services/TarkovDevJsonClient.cs` (new) replaces the GraphQL transport in
`TarkovDevDataService` and the copy in `WikiQuestService.FetchTarkovDevQuestsAsync`
(the latter is deleted; Export Wiki Quests no longer contacts tarkov.dev).

- Base URL `https://json.tarkov.dev/`, game mode fixed to `regular`; one
  `GetAsync(path)` with the existing user agent, a 5-minute timeout, and
  `If-None-Match` against the ETag stored beside each cache file so a repeat run
  costs one 304 per endpoint.
- `FetchTasksAsync` reads `regular/tasks` plus `tasks_en`, `tasks_ko`,
  `tasks_ja` and resolves every key through the locale files the way the front
  end does (primary language, then English, then the key itself); a name that
  resolves to its own key is treated as missing. `FetchItemsAsync`,
  `FetchTradersAsync`, `FetchHideoutAsync` do the same for their endpoints.
  The JSON shape is read through typed models (`JsonSerializer`) with unknown
  fields ignored; a missing required field (`id`, `wikiLink` on tasks) fails the
  fetch, and an empty task set is a failed fetch, never an empty cache (this
  closes the "200 with errors overwrites the cache with `{}`" path).
- `TarkovDevQuestCacheItem` grows by `minPlayerLevel`, `kappaRequired`,
  `factionName`, `traderLevelRequirements` (`[{traderId, traderName, level}]`,
  only `requirementType == "level"`), `taskRequirements` (`[{taskId, status[]}]`),
  `availableDelaySecondsMin`; `cachedAt` and the source `Last-Modified` are
  recorded so the refresh log states the data's age. The cache file keeps its
  name and location (`wiki_data/cache/tarkov_dev_quests.json`); the second,
  debug-only file of the same name written by `ExportQuestsAsync` is removed.
- The cache is no longer a dictionary keyed by `wikiLink` that drops
  collisions: it is a list, and matching (section 2) is one to one.
- Cache Tarkov Dev Data keeps its per-part isolation (a failed part keeps its
  old file) and now reports the age of every part it kept.

### 2. Matching, identity carry-over and liveness (RefreshDataService)

`LoadQuestsFromCacheAsync` and `FetchAndProcessQuestsAsync` share one new
resolver, `QuestIdentityResolver` (new class, pure, unit-tested), which takes the
cached wiki pages, the cached tasks and the previous database's `(Id,
NormalizedName, BsgId, Name)` rows and returns, per imported quest, its
identity and its task. The previous rows are read from the working database
before `UpdateDatabaseAsync` opens its transaction, the way
`LoadItemsFromDatabaseAsync` already reads items; the runbook's first step
makes that database the published one.

- **Match** a wiki page to a task by the page URL against `wikiLink` (after
  the existing `NormalizeWikiLink`), else by `NormalizeQuestName(title)` against
  `normalizedName`, else by the committed alias list
  `TarkovDBEditor/Resources/Data/quest-match-overrides.json` (page title ->
  task id, with the upstream issue each entry waits on). The alias list exists
  for API records that point at the wrong page: today the three prestige tasks
  `new-beginning-2/3/4` (6761ff17cdc36bd66102e9d0, 6848100b00afffa81f09e365,
  68481881f43abfdda2058369) all link to `Neuanfang`, a German title with no
  page, and no normalization maps `New Beginning (Prestige 2)` to
  `new-beginning-2`; without the alias those three published quests would leave
  the app. It follows the precedent of `TraderNameAliases` and
  `ItemNameAliases` in `WikiQuestService`; the report flags an alias whose page
  now matches without it so the entry can be removed.
- **One page, several tasks.** A task may be claimed by one page only. When
  several tasks share a page, the page takes one of them by this order of
  evidence, and the report prints every candidate with its required-by list:
  (1) a BEAR/USEC pair (Drip-Out 1 and 2, Textile 1 and 2) takes the lowest id
  and its `Faction` stays NULL, because the page serves both factions as those
  four rows do today; (2) otherwise the candidate that some other task lists in
  its `taskRequirements` wins, which is what decides The Tarkov Shooter - Part 5
  in favour of 5bc4836986f7740c0152911c (the record Part 6 requires) over
  5bc4826c86f774106d22d88b (required by nothing); (3) otherwise the candidate
  whose id the previous row for that page already holds, which keeps Battery
  Change, Make Amends, The Price of Independence and The Huntsman Path -
  Administrator on the record the user's log events already matched; (4)
  otherwise the newest id (the first eight hex digits of a game id are its
  creation time). `BsgId` holds one id, so for every such page only one
  record's log events match; the report says so per page, and a
  `QuestExternalIds` side table stays in the backlog for the day the app
  indexes several ids per quest. Pages with no task and tasks with no page are
  listed too.
- **Liveness**: a page is imported when it is in `Category:Quests`, not in an
  excluded category (the existing list, which includes `Historical content`),
  and either matched to a task or carrying the wiki's seasonal requirement line.
  That line is the Requirements bullet `Must be playing in the [[<target>]]`
  whose link target is `PvP Season` or starts with `Seasons#`; every captured
  KORD BREACH page today reads
  `* Must be playing in the [[Seasons#Season 1: KORD BREACH|Seasonal mode]].`,
  and the upstream census also saw the bare `[[PvP Season]]` form, so the new
  `WikiQuestService.ExtractIsSeasonal` accepts both and the tests pin both with
  the captured Break the Chain page as the fixture. The 47 Arena pages (no
  task, no seasonal line) and the 35 removed quests (page historical) fall out;
  the 18 KORD BREACH pages come in on the wiki's word, with `BsgId` NULL and
  their level and prerequisites from the existing wiki parsers until the API
  carries them; New Beginning (Prestige 5) and (Prestige 6) fall out because
  the API has no record for them (it stops at Prestige 4) and they carry no
  seasonal line. One hazard to watch: `ExcludeCategories` already lists a
  category named `Seasonal quests` (meant for event quests); the KORD BREACH
  pages are only in `Category:Quests` today, and the content guard that pins
  one of them by name catches the day a wiki edit moves them. The report names
  every page held back for lack of a task, and every page imported wiki-only,
  so a genuinely new quest the API has not picked up yet is visible either way.
- **Identity**: if the previous database has a row whose `BsgId` equals the
  matched task id, the quest keeps that row's `Id`, and its `NormalizedName`
  when the previous database has the column; when it does not (the first 1.1
  run, from the published 1.0.10), `NormalizedName = SqlForm(previous Name)`,
  which is the value both builds computed for that row before the column
  existed. `WikiPageLink` moves to the new URL and `Name` to the new title.
  Otherwise `Id = base64(wiki URL)` and
  `NormalizedName = QuestNormalizedName.SqlForm(title)` (a C# port of the app's
  SQL expression, see section 3). Title reuse therefore resolves by task: the
  row that was "Sew it Good - Part 4" is now named "Sew it Good - Part 2" and
  keeps its progress; the row that was Part 3 now carries the Part 4 page. The
  invariant the guard test pins: `NormalizedName == SqlForm(TitleOf(Id))`, where
  `TitleOf` decodes the URL the `Id` was minted from, holds for every row
  whether or not it was renamed.
- **One-time backfill**: `Debug > Backfill external IDs from snapshot...`
  (`BsgIdBackfillService`, new) copies `BsgId` by `Id` from a snapshot database
  into the working database where it is NULL, for Quests and Items, and reports
  counts. The snapshot is `git show ebbc60c:TarkovHelper/Assets/tarkov_data.db`
  (1.0.7, 473 quests, 2648 items). It runs once, before the first 1.1
  regeneration, against a working copy of the published 1.0.10 database. One
  rename has no snapshot id to copy: No Questions Asked (now Special Order,
  task 68ee1c18b4e5bc9a68018cd7, confirmed by the API's `wikiLink` and the wiki
  move log) never carried a `BsgId`; the runbook sets it by hand in the same
  step, and a content guard pins the row. After that run every later
  regeneration bridges through the `BsgId`s it wrote.
- Items get the same carry-over rule (`ItemIdentityResolver` inside the same
  class family): an item page matched by `wikiLink` to an item whose id the
  previous database holds keeps its `Id`, so its icon file keeps resolving.
  Inventory rows are keyed by the app-generated name and are not preserved
  (Non-Goals).

### 3. Per-field precedence and value mapping

For a matched quest:

| Column | Source | Mapping |
|---|---|---|
| `BsgId` | task `id` | as is |
| `Name`, `NameEN` | wiki title | unchanged |
| `NameKO`, `NameJA` | locale files | NULL when missing or equal to English (`ResolveLocalizedQuestName`) |
| `Trader` | task `trader` -> nickname | the 11 strings in use match byte for byte; Survivor is new |
| `Location` | wiki `location` | unchanged |
| `MinLevel` | `minPlayerLevel` | 0 stored as NULL (no published row has 0) |
| `MinScavKarma` | wiki | unchanged |
| `KappaRequired` | `kappaRequired` | 1/0 |
| `Faction` | `factionName` | Any -> NULL, BEAR -> Bear, USEC -> Usec; a BEAR/USEC pair behind one page -> NULL; any other value fails the run |
| `RequiredEdition`, `ExcludedEdition`, `RequiredPrestigeLevel`, `RequiredDecodeCount` | wiki | unchanged |
| `NormalizedName` (new) | identity resolver | `SqlForm` of the title the `Id` was minted from |
| `QuestRequirements` | `taskRequirements` | one row per entry, GroupId 0 on every row (the app's AND set, as the Collector synthesis already writes; the wiki parser's 1..n numbering was equivalent because the app reads a singleton group as AND); status complete -> Complete, active -> Accept, failed -> Fail; `DelayMinutes` from `availableDelaySecondsMin` / 60 when non-zero; any other status fails the run |
| `QuestTraderRequirements` (new) | `traderLevelRequirements` | one row per entry |
| `QuestObjectives`, `QuestRequiredItems`, `OptionalQuests` | wiki | unchanged parsers; `ParseObjectiveLine`'s map list is extended with The Labyrinth and any map name present in the API's objectives |

For a wiki-only seasonal quest (no task): `BsgId` NULL, `Trader`, `MinLevel`,
`MinScavKarma` and prerequisites from the existing wiki parsers as today,
`KappaRequired` 0, `Faction` from the wiki parser, no `QuestTraderRequirements`
rows, names English only.

The wiki `|previous` parser stays in the code, writes rows only for wiki-only
quests, and is otherwise consulted to compute the disagreement list for the
report (wiki set vs task set per quest: agree, wiki superset, task superset,
conflict; 310/111/60/17 at the time of writing). The 15 quests that had an OR
group in the published data are a named review item: the API has no OR groups,
so they collapse to the game's AND list, and the report shows before and after
for each.

`QuestNormalizedName.SqlForm` (new, in TarkovDBEditor) is the C# equivalent of
the app's expression: ASCII lower-casing only, spaces to dashes, the ASCII
apostrophe (U+0027) and the period removed, nothing else; the typographic
apostrophe U+2019 in "What's on the Flash Drive?" stays, as SQLite's `LOWER`
and `REPLACE` leave it. A test in `TarkovHelper.Tests` evaluates the SQL
expression over every `Name` in the published database and asserts equality with
the C# function, so the two cannot drift.

### 4. Schema (data format 1, additive)

- `Quests.NormalizedName TEXT` (nullable for the DDL, populated on every row),
  added through a PRAGMA-guarded `ALTER TABLE` block in
  `CreateQuestsTableIfNotExistsAsync` (copying the `QuestObjectives` pattern),
  registered in `RegisterQuestsSchemaAsync`, carried by `DbQuest`, the
  INSERT/UPDATE lists, `AddQuestParameters`, and appended to the SELECT in
  `QuestRequirementsViewModel` so the existing ordinals hold. A unique index
  `idx_quests_normalizedname`.
- `QuestTraderRequirements` (new table) mirrors `HideoutTraderRequirements`:
  `Id TEXT PRIMARY KEY` (hash of `QuestId|TraderId`), `QuestId TEXT NOT NULL`,
  `TraderId TEXT NOT NULL`, `TraderName TEXT NOT NULL`, `RequiredLevel INTEGER
  NOT NULL`, `ContentHash`, `IsApproved`, `ApprovedAt`, `UpdatedAt`; index on
  `QuestId`; upserted by `UpsertQuestTraderRequirementsAsync` with the same
  table-global diff as the other child tables, registered in `_schema_meta`.
  Rows carry only `requirementType == "level"`; level 1 entries do not occur in
  the source.
- Nothing is removed or retyped; `user_version` stays 1. `DataFormatDriftTests`
  reports Widened; the proposed `DataFormatBaseline.v1.json` is adopted in the
  publish commit.
- Five constraints the run enforces before writing (each a thrown
  `InvalidOperationException` naming the offending rows): `RequirementType` in
  {Complete, Accept, Fail}; `Faction` in {NULL, Bear, Usec}; every
  `NormalizedName` equals `SqlForm(TitleOf(Id))` and is unique; no hard-required
  column of the app (the ten on Quests, the twelve on Items) is NULL where it
  was not NULL before, except `MinLevel` by design; `Trader` non-NULL on every
  row.
- `docs/database-schema.md` gains both additions.

### 5. Collector synthesis and stale rows

`UpsertQuestRequirementsAsync` drops its Collector exemption and
`AddCollectorKappaRequirementsAsync` rebuilds Collector's rows from the current
flag set, deleting rows for quests no longer flagged. Expected after the
refresh: `KappaRequired = 1` on 13 quests including Collector; 12 Collector
rows, GroupId 0; the Grenadier row gone; the gauge total 13. Foreign keys are
already enforced on every connection (Current Behavior), so stale-quest deletes
cascade; `QuestTraderRequirements` declares
`FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE` and is upserted
after Quests inside the same transaction, and the resolver emits requirement
rows only for quests it imports.

### 6. Pipeline guards (silent failures closed)

- Refresh Data (from Cache) and Fetch Wiki Data fail, not succeed, when the
  wiki cache has zero pages with content (`WikiQuestService.GetCacheStats().withContent == 0`),
  when `Special:Export` returned a failure for any batch (the per-batch catch
  now aggregates and throws after the loop), when the tarkov.dev quest cache is
  older than the wiki cache by more than seven days, and when the item cache is
  missing on the full path (today it continues with `BsgId` NULL everywhere).
- The refresh refuses to start when the previous database has `BsgId` NULL on
  more than 10 percent of quests; the message names
  `Debug > Backfill external IDs from snapshot...`. This is the guard the
  carry-over depends on: a run from an unbackfilled database would mint fresh
  identities for every renamed quest while every page still matches.
- The refresh aborts when more than 5 percent of previously published quests
  would lose their task match, when any imported quest other than a wiki-only
  seasonal page would end with `BsgId` NULL, or when more than 5 percent of
  imported quests would end with `Trader` NULL. The thresholds are constants
  with a comment naming this spec.
- `OptionalQuests` and `QuestRequiredItems` skip an empty list like the other
  three tables.
- The refresh aborts when the crawl contains pages whose Requirements section
  mentions a seasonal mode but `ExtractIsSeasonal` marked none of them: the
  marker's spelling has moved, and importing zero seasonal quests silently is
  the failure the exception exists to prevent.
- Every run writes `wiki_data/logs/refresh_<ts>.json` with the counts the diff
  report consumes: matches, collisions, held-back pages, disagreements, carried
  identities, title reuses.

### 7. Regeneration procedure (operator runbook)

1. Start from the published database: copy `data/v1/tarkov_data.db` over the
   editor's working database (the carry-over and approvals are relative to what
   is in the field).
2. `Debug > Backfill external IDs from snapshot...` with the 1.0.7 snapshot
   extracted from git. Expected: 473 quests, 2648 items filled. Then set the
   one id the snapshot lacks in the editor grid: `BsgId =
   68ee1c18b4e5bc9a68018cd7` on the quest named "No Questions Asked".
3. `Debug > Cache Tarkov Dev Data` (JSON API; tasks, items, traders, hideout).
4. `Debug > Export Wiki Quests` (wiki crawl; `Special:Export`).
5. `Debug > Fetch Wiki Data` (items with icons, quests, traders, one
   transaction; `RefreshDataAsync` gains the trader upsert that today only the
   from-cache path runs, so the Traders table reaches 16 rows in this step).
6. `Debug > Refresh Hideout Data` (from the refreshed cache).
7. Run the diff report (section 8) against `data/v1/tarkov_data.db` and review
   it against the spot checks in Test Strategy.
8. Run the legacy smoke (section 10) and the test suite against the candidate.
9. `Tools > Publish DB Update` with token `1.1.0` (the suggestion is 1.0.11;
   the token is opaque to every client, and `1.1.x` marks the game patch the
   data describes), then adopt the proposed drift baseline.

### 8. Diff report tool

`tools/DataDiff/` (new .NET 8 console project, added to `TarkovHelper.sln`;
`CheckDb/`, the untouched Hello World scaffold, is deleted and its row in the
root `CLAUDE.md` solution table replaced). Usage:
`dotnet run --project tools/DataDiff -- <previous.db> <candidate.db> [--icons <dir>] [--log <refresh.json>] > report.md`.
Sections, in order: schema delta; row counts per table; quests added, removed,
renamed (joined by `BsgId`, then by `Id`), with the carried identities and the
title reuses called out; per-field change counts and the full list for
`KappaRequired`, `MinLevel`, `Trader`, `Faction`, editions; prerequisite edges
added and removed per quest; `QuestTraderRequirements` per quest; objective
lists whose count or order changed (the positional drift list); items added,
removed, renamed; icon coverage against `--icons` (items without a file, files
without an item, non-png downloads); hideout requirement deltas and the item
join coverage; NULL-rate table (Trader, MinLevel, BsgId, NameKO, NameJA); the
source disagreements and held-back pages from `--log`. The report for the
publish is attached to the publish PR. The tool's comparison core is a library
class with unit tests over two small fixture databases; the console is a thin
shell.

### 9. Publish and release sequence

Two PRs, then a release, in this order:

- **PR A (code)**: everything above except the data and icons: the JSON
  client, resolver, schema, guards, tools/DataDiff, pipeline unit tests, the
  legacy-smoke E2E test, these documents, the superseded notes on both roadmap
  documents. CI stays green against the old data because the content guards
  that pin 1.1 facts land in PR B.
- **PR B (data)**: `data/v1/{tarkov_data.db,manifest.json,db_version.txt}`,
  `data/index.json`, the `TarkovHelper/Assets/` mirror, the adopted
  `DataFormatBaseline.v1.json`, the new item icons under
  `TarkovHelper/Assets/icons/` (lower-case path only; the 115 pngs under
  `Assets/Icons/` are moved to the lower-case path in the same PR with
  `git mv` through a temporary name, and the 11 orphan pngs are deleted), and
  the content guard tests pinned to the 1.1 facts. Subject in the 95b9b9a
  style: `data(db): ship the 1.1 quest refresh (db_version 1.1.0)`, body with
  the headline counts from the report. Merged through CI, never pushed
  directly: main is unprotected and raw main serves whatever lands.
- **Release**: `/release 2026.8.0` immediately after PR B merges, with nothing
  else merged in between. The skill adds its own version-bump commit on top of
  the merge commit and tags that, so the tag is one commit after PR B and the
  release build's seed and icons are PR B's; release notes cover phases 1, 2
  and 3; `update.xml` last. Fielded builds pull the data within five minutes of
  the merge and see the update prompt within their three-minute app check once
  `update.xml` moves. The post-hoc hot-swap confirmation of v2026.7.0 (section
  10) runs before `update.xml` moves, because that build's app-update check
  cannot be disabled and would show its dialog over the smoke.

### 10. Legacy smoke

`LegacySmokeE2ETests` (new, `Category=E2E`, desktop only) takes
`TARKOVHELPER_LEGACY_APP_DIR` (an extracted previous-release zip) and
`TARKOVHELPER_CANDIDATE_DB` (the candidate database). It copies the candidate
over `Assets/tarkov_data.db` in a scratch copy of the release, leaves
`Assets/db_version.txt` at the token currently live on raw main so the build's
undisableable first check reports up to date instead of overwriting the
candidate, sets `TARKOVHELPER_CONFIG_PATH` to a scratch directory, launches
through `dotnet TarkovHelper.dll` with an `AppDriver.Launch(dllPath, configDir)`
overload added to the harness. Before launch the scratch `Config/user_data.db`
is seeded with three `QuestProgress` rows written the way v2026.7.0 writes them
(old `Id`, old SQL-form `NormalizedName`): a Done on the old "Sew it Good -
Part 4" row, a Done on "A Shooter Born in Heaven", and a Failed on an unrenamed
quest. The test asserts: the window appears; the quest, hideout, item and
collector pages load with non-empty lists; the log shows no unhandled
exception; the detail view shows Done on "Sew it Good - Part 2", not started on
"Sew it Good - Part 4", Done on "Shooter Born in Heaven" and Failed on the
unrenamed quest (R4 on the fielded build); a log sync completes against a
fixture log and reports at least one matched quest. This exercises the reader
path R9 cares about (the same `QuestDbService`/`ItemDbService`/
`HideoutDbService` code the hot swap re-enters). The same seeded check runs
against the new build as `ProgressCarryOverE2ETests`. The hot swap itself is
confirmed post hoc: after PR B merges, the extracted v2026.7.0 is launched once
more against real raw main and its log shows the download, the
`DatabaseUpdated` event, and a clean relaunch.

What the fielded build does and does not honour, so the test makes no false
assumption: v2026.7.0 reads `TARKOVHELPER_CONFIG_PATH` and no other harness
variable; its data check is neutralised by the token trick above and its
three-minute app-update check by running both the smoke and the post-hoc
confirmation before `update.xml` is repointed. Its quest tab has `LstQuests`
and `TxtDetailStatus` but not the status chips `QuestTabDriver` waits on (that
build still has `CmbStatus`), so the smoke waits on the list and reads the
detail status text. The fixture log folder is seeded through the
`app.logFolderPath` setting in the scratch configuration.

### Files touched

- `TarkovDBEditor/Services/TarkovDevJsonClient.cs` (new),
  `TarkovDevDataService.cs` (transport swap, cache models),
  `WikiQuestService.cs` (`FetchTarkovDevQuestsAsync` removed, export batch
  failures thrown, `ExtractIsSeasonal`, map list), `RefreshDataService.cs` (resolver use, schema,
  precedence, guards, Collector synthesis, trader-requirement upsert, trader
  upsert in the full path), `QuestIdentityResolver.cs` (new),
  `QuestNormalizedName.cs` (new), `BsgIdBackfillService.cs` (new),
  `Resources/Data/quest-match-overrides.json` (new),
  `HideoutDataService.cs` (JSON cache), `MainWindow.xaml(.cs)` (backfill menu
  item),
  `ViewModels/QuestRequirementsViewModel.cs` (select list),
  `TarkovDBEditor/CLAUDE.md`.
- `tools/DataDiff/` (new), `TarkovHelper.sln`, `CheckDb/` (deleted), root
  `CLAUDE.md` (solution table).
- `TarkovHelper.Tests/`: `TarkovDevJsonClientTests`, `QuestIdentityResolverTests`,
  `QuestNormalizedNameTests`, `BsgIdBackfillTests`, `RefreshGuardTests`,
  `DataDiffTests`, `PublishedDataContentTests` (PR B), `LegacySmokeE2ETests`,
  `ProgressCarryOverE2ETests`, `E2ETestHarness` (launch overload);
  `QuestDataCoverageTests` deleted in PR B, its Korean-coverage assertion moving
  into `PublishedDataContentTests` at 50 percent; `DataFormatBaseline.v1.json`
  (PR B).
- `TarkovHelper/Assets/icons/` (PR B), `data/v1/*`, `data/index.json`,
  `TarkovHelper/Assets/{tarkov_data.db,db_version.txt}` (PR B).
- `docs/database-schema.md`, `docs/database-update-mechanism.md` (runbook
  pointer), `feature-eft-1-1-roadmap.md` and `feature-eft-1-1-roadmap.spec.md`
  (superseded notes).

## Technical Decisions

**The JSON API replaces GraphQL instead of sitting beside it.** Keeping the
GraphQL client as a fallback was considered and rejected: it cannot be tested
against a live endpoint, its queries never requested the fields this phase
needs, and the maintainers describe the JSON surface as the one tarkov.dev
runs on. A second transport would be untested code on the critical path.

**Identity follows the external ID, with the page URL as the first-sight key.**
`Quests.Id` stays base64 of a wiki URL, but of the URL the quest was first
published under; it is no longer recomputed from the current title. This is not
the PK migration the roadmap forbids: no existing key changes, and no app code
reads a URL out of an `Id`. The alternative, an alias table consumed by new app
code, cannot reach the fielded build before the data does and would leave the
eight title reuses attached to the wrong quest in that build.

**`NormalizedName` is pinned to the app's SQL expression, not the tarkov.dev
style.** Both builds switch to a `NormalizedName` column the moment one exists,
and their stored progress is keyed by the name the SQL expression produced. A
column in the editor's tarkov.dev style would silently un-key 228 quests in
every build: the one change found that is a meaning change invisible to the
drift test, and the only thing in this refresh that would have forced data
format 2. Pinned to the SQL form it is additive, and it is what makes a renamed
quest's progress survive.

**Loyalty is a `QuestTraderRequirements` table, not a `Quests.MinTraderLevel`
column.** Supersedes the roadmap spec's column decision. The premise that every
requirement names the giving trader holds for 94 of the 99 loyalty-gated tasks;
the table costs one additional child-table upsert in a pipeline that already
has four, mirrors `HideoutTraderRequirements`, and leaves phase 4 one read path
instead of a column plus an exception list. A column alongside the table was
rejected as a second copy that can skew.

**Liveness needs both sources, with two bounded exceptions.** The wiki category
over-includes (Arena, legacy pages) and the API under-prunes (35 removed
tasks). Requiring a match in both needs no hand-maintained list. The seasonal
exception is keyed on a requirement line the wiki itself writes, so it needs
none either; the alias list is the one hand-maintained piece, bounded to API
records that link to a wrong page and removed entry by entry as upstream fixes
them. A general wiki-only fallback for a prolonged API outage was considered
and rejected as speculative: the published database keeps serving during an
outage, and re-enabling wiki-only import would be its own recorded decision.

**Prerequisites come from the API; the wiki's grammar is kept for the report
only.** The 1.1 rework dissolved most chains and the wiki has both stale chains
(111 quests where it lists more than the game) and dropped ones (60 where it
lists fewer, Sew it Good - Part 4 among them). Taking the API's list loses the
wiki's OR groups on 15 quests; the review sees each one.

**Zero minimum level is stored as NULL.** The published data never held 0 and
the app's level gate and detail pane both treat 0 and NULL as "none"; storing
NULL keeps the field's documented range unchanged for the drift test's purpose.

**Data publishes first; the release follows immediately.** Releasing first was
rejected because the content tests that pin 1.1 facts must be green on the
release build, and they need the data in `data/v1` at build time; the icon-less
window on the old build lasts until the update prompt. Both orders are safe
under the format-1 constraints. This relaxes the roadmap's "reader in the field
before or with the first 1.1 publish" rule, which protected against a breaking
publish this one is not; both roadmap documents carry the superseded note.

**Publishes go through a PR.** Every previous data publish was a direct push;
CI runs after the push and raw main serves the commit immediately, so a red run
is post hoc. A PR costs nothing (the blob is content-addressed) and is the only
pre-serving gate.

**The diff tool is a solution project, not a scratch script.** The code-health
assessment already flags scratch scripts and the dead `CheckDb` scaffold; the
tool replaces the scaffold, joins the solution, and its core is unit-tested so
the review artefact is itself trustworthy.

**Objective IDs from the API are not stored.** Matching wiki bullets to API
objectives by position would be wrong exactly on the rebalanced quests the IDs
are meant to help; storing them unmatched helps nothing in this phase. A stable
objective identity remains backlog.

## Open Questions

- Whether the wiki's `Historical content` category covers all 35 removed
  quests; 5 of 35 sampled do, and one removed quest's page is uncategorised.
  Settled by the first regeneration's held-back list: a removed quest that
  survives shows up as a row the API lists and the diff report flags as "absent
  from the Quests overview page" only if that check is added; otherwise the 35
  known names are checked by hand against the report's retained set.
- Whether the API's `map` field and the maps endpoint should replace the wiki
  `location` field; not captured in this research. Settled by a single fetch
  when Location quality becomes a problem; Location stays wiki in this phase.
- The re-parse after the wiki's admin lock lifts (2026-09-14) may warrant a
  correction publish; settled by re-running the pipeline and reading the report.

## Test Strategy

- **Unit (TarkovDBEditor, first tests for the pipeline)**:
  `TarkovDevJsonClientTests` parse a trimmed real capture (tasks, locale files,
  items, hideout, traders) into the cache models, resolve names through the
  locale fallback chain, refuse an empty task set and a missing `wikiLink`;
  `QuestIdentityResolverTests` cover match by link, match by normalized name,
  one-to-one claiming through the four-step order (the BEAR/USEC pair keeping
  `Faction` NULL; the required-by step with the two Tarkov Shooter - Part 5
  records as the fixture; the previous-row step; the newest-id step), the
  alias list (the `Neuanfang` links), carry-over of `Id` and `NormalizedName`
  by `BsgId`, carry-over from a previous database that lacks the
  `NormalizedName` column, fresh identity for a new page, title reuse (the Sew
  it Good rotation as the fixture), liveness (Arena page without a task held
  back, historical page excluded, seasonal page without a task imported under
  both marker spellings, a prestige page with neither task nor marker held
  back); `QuestNormalizedNameTests` pin the
  SQL-form function and, in `TarkovHelper.Tests`, compare it against the SQL
  expression over every name in the published database; mapping tests for
  faction, zero level, status vocabulary, delay; `RefreshGuardTests` drive
  `RefreshDataFromCacheAsync` against throwaway caches and assert the
  unbackfilled previous database, the empty wiki cache, the export batch
  failure, the stale task cache, the match-rate collapse and the NULL-rate
  collapse each fail the run before any write; Collector synthesis
  removes a stale row; `BsgIdBackfillTests` fill only NULLs and report counts;
  `DataDiffTests` produce the expected sections from two fixture databases.
- **Content guards on the published database (`PublishedDataContentTests`, CI,
  PR B)**: quest count at least 450; `KappaRequired = 1` count exactly 13,
  including Collector; Collector has exactly 12 requirement rows, all GroupId 0,
  none for an unflagged quest; Stirrup has one objective whose description
  contains "pistols", `MapName` Factory, `TargetCount` 10, no requirement rows,
  `MinLevel` NULL; the row with `BsgId 5c0bde0986f77479cf22c2f8` is named
  "Shooter Born in Heaven" and its `NormalizedName` is
  `a-shooter-born-in-heaven`; the row with `BsgId 5ae4497b86f7744cf402ed00`
  (formerly Part 4) is named "Sew it Good - Part 2" and the "Sew it Good -
  Part 4" row carries `BsgId 5ae4496986f774459e77beb6`; `BsgId` NULL on at most
  5 percent of quests, every one of them a wiki-only seasonal row, and on at
  most 10 percent of items; `Trader` NULL on none; `Faction` in {NULL, Bear,
  Usec}, and NULL on the four BEAR/USEC-pair quests; `RequirementType` in
  {Complete, Accept, Fail}; `NormalizedName` non-null, unique, equal to
  `SqlForm(TitleOf(Id))`; `QuestTraderRequirements` has at least 100 rows, the
  Collector rows name seven traders at level 4, and the Chemical - Part 3 row
  names Jaeger at level 2, a trader other than its giver; the row named
  "Special Order" carries `BsgId 68ee1c18b4e5bc9a68018cd7` and `NormalizedName`
  `no-questions-asked`; The Tarkov Shooter - Part 5 carries
  `BsgId 5bc4836986f7740c0152911c` and Part 6 has exactly one Complete
  requirement, on that row; New Beginning (Prestige 2) carries
  `BsgId 6761ff17cdc36bd66102e9d0`; a quest named "Uninvited Guests - Part 1"
  exists (the seasonal exception fires); Korean real names on at least 50
  percent of quests (replaces the skipped test); no quest has `MinLevel = 0`;
  hideout item
  requirements join to Items on at least 90 percent of rows; every `Items` row
  except a committed three-item exception list has `Assets/icons/{Id}.png` in
  the repository and no png lacks an item.
- **Existing guards**: `DataFormatDriftTests` adopts the widened baseline;
  `DataChannelMirrorTests`, `SeedDatabaseTests`, `E2EQuestDataTests` and the
  seasonal fixture (Collector with a non-quest-item required item) stay green
  against the new data.
- **E2E (desktop)**: the nine existing suites rerun against the 1.1 seed;
  `LegacySmokeE2ETests` and `ProgressCarryOverE2ETests` as in Design 10 (R4 on
  the fielded build and on the new one); one navigation check that searching
  "Stirrup" shows the Factory pistol objective.
- **Not automated**: the review of the diff report against the named spot
  checks (the 92 renames, the 8 reuses, the 15 OR-group quests, the 35 removed
  names, the held-back and wiki-only lists, the disagreement list); the
  post-hoc hot-swap confirmation against raw main. The gauge total on screen
  (R3) is not asserted here; phase 5's E2E pins it against the real database,
  as the roadmap assigns.

## Verification

- `dotnet build TarkovHelper.sln` clean, Debug and Release.
- `dotnet test --filter "Category!=E2E"` green on PR A (old data) and PR B
  (new data, baseline adopted), including `DecisionDocsTests` for this pair.
- `dotnet run --project tools/DataDiff -- data/v1/tarkov_data.db <candidate>`
  produces the report; its headline counts match the PRD's expectations (13
  kappa, about 480 quests, 92 carried identities including the hand-bridged
  one, 35 removed plus the duplicate records the collision order drops, about
  37 added of which 18 are wiki-only seasonal rows, 4 new 1.1 quests and 15
  additions from before the patch, 47 Arena pages and New Beginning Prestige 5
  and 6 held back).
- `dotnet test --filter "FullyQualifiedName~LegacySmokeE2E"` with the
  extracted v2026.7.0 zip and the candidate database: green.
- After PR B merges: raw `data/v1/manifest.json` and the Assets mirror
  byte-match the commit; the fielded v2026.7.0 launched from the zip downloads
  1.1.0, relaunches, loads every page, and syncs a fixture log with matches.
- After the release: the new build's log shows its check hitting `data/v1/`
  and reporting up to date with the bundled seed.

## Risks & Migration

- **Carry-over depends on the backfill running first.** A regeneration from a
  working database whose `BsgId`s are still NULL mints fresh identities for
  every renamed quest; the match-rate guard (more than 5 percent of published
  quests losing their task match) does not catch this case because the pages
  still match. Mitigated by the first guard in Design 6 (the refresh refuses to
  start on an unbackfilled database), pinned by `RefreshGuardTests`.
- **Seasonal quests imported wiki-only** carry no `BsgId` until the API adds
  them; when it does, the page matches by `wikiLink`, the `Id` is unchanged
  (same page), and the next publish fills `BsgId`, names and
  `QuestTraderRequirements` in place.
- **The alias list is hand-maintained.** Each entry carries the upstream issue
  it waits on; the report flags an entry whose page now matches without it, and
  the resolver tests pin the list's shape so a malformed entry fails the build
  rather than the run.
- **Pages with several game records** keep one `BsgId`; the order of evidence
  is deterministic and printed, but a record the game re-creates under a new id
  after this publish is picked up only when the previous-row step no longer
  applies (the old id leaves the API) or when the required-by step decides.
- **The API lists removed quests and the wiki lists Arena quests.** Both are
  filtered by the liveness rule; a removed quest whose page is uncategorised
  would survive. The 35 known names are a named review item for this publish.
- **Title-reuse correctness rests on the snapshot's IDs being right.** They
  were produced by tarkov.dev's own `wikiLink` matching in December 2025; the
  report lists every reuse for a human to confirm against the game.
- **Fielded build and the new columns.** v2026.7.0 reads `NormalizedName`
  through feature detection and ignores the new table; confirmed by the legacy
  smoke before publishing.
- **Hot update does not refresh quest rows.** Both builds show the new data
  after a restart; the channel doc already says so. No change in this phase.
- **Raw CDN skew** between the version token and the database on the fielded
  build (no digest check there) is the pre-existing risk the channel fixed for
  newer builds; a wasted download at worst.
- **Rollback.** Data: republish the previous database under a new token (the
  fielded build follows any token change; carried identities do not matter to
  a rollback because the old data carries the old names). App: release the
  prior build. Pipeline code rolls back with PR A; the backfilled `BsgId`s in
  the working database are a local artefact.
- **Repository size** grows by one database blob and about a few hundred icon
  files, as every refresh has.
