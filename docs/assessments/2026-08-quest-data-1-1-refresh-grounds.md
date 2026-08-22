# Quest Data 1.1 Refresh Grounds, August 2026

> Snapshot assessment. Analyzed at commit `a214a95` (2026-08-21, branch `triton`
> == `main`); upstream state captured live on 2026-08-21 with re-checks on
> 2026-08-22. This document records the verified facts, and the evidence for
> each, that the phase-3 decision documents `feature-quest-data-1-1-refresh.md`
> and `feature-quest-data-1-1-refresh.spec.md` rest on. The documents state the
> decisions; this assessment states the grounds, so a later reader can tell which
> decision falls if a fact changes. Frozen once merged. A PR that acts on a
> finding names its `QDR` ID in the PR body.

## Scope and method

The facts come from a research pass run before the documents were written: live
upstream probes, the TarkovDBEditor pipeline read in full, the app's data-reading
paths, publish and release infrastructure, the shipped database measured with a
read-only SQLite probe, and repository history, followed by a cross-check pass
that resolved contradictions and two review passes that attacked the written
documents. Every number below was produced by a query, a `jq` expression, a
`git` command or a code read, and cross-checked at least once.

This document is self-contained: the raw captures of that day are not part of
the repository, so every evidence entry names the public source, the query or
command that reproduces the number, the repository symbol, or quotes the text.
Upstream values drift; where a number was read from a live source, the capture
date is the one in the header. Commands assume the repository root, `jq` on the
path, and any SQLite client for the SQL (the repository ships none; a throwaway
console project on `Microsoft.Data.Sqlite` is what the research used). Run SQL
against a copy of `data/v1/tarkov_data.db`, never the file in the tree.

Out of scope: anything the phase-3 documents do not decide (loyalty gating UI,
Collector's 1.1 conditions, the runtime icon channel), performance, and a
review of the app's code quality (`2026-08-code-health.md` covers that).

## How to read the findings

Each finding has three parts: **Fact** (what is true at the snapshot), **Evidence**
(how to reproduce it), and **Bears on** (the decision in the PRD or spec that
rests on the fact, and what would have to change for that decision to be
revisited). Facts are grouped by where they were found, not by severity: a
ground is not a defect. Where a fact is also a defect the app or pipeline
carries today, the section says so; the phase-3 spec schedules the fix.

Notation for the API: `TASKS` stands for the body of
`GET https://json.tarkov.dev/regular/tasks`, `TASKS_EN`/`TASKS_KO`/`TASKS_JA` for
`.../regular/tasks_en` and so on, `ITEMS` for `.../regular/items`, `HIDEOUT` for
`.../regular/hideout`, `TRADERS_EN` for `.../regular/traders_en`. Wiki calls go
to `https://escapefromtarkov.fandom.com/api.php` and need a browser-like
`User-Agent` header (QDR-26).

## Findings index

| ID | Fact | Bears on |
| --- | --- | --- |
| QDR-1 | tarkov.dev GraphQL is down; json.tarkov.dev is the supported API | source decision |
| QDR-2 | The JSON task set carries every 1.1 gate as structured data | source decision, loyalty table, prerequisites |
| QDR-3 | Five tasks require loyalty with a trader other than their giver | loyalty table (roadmap reversal) |
| QDR-4 | The wiki's Kappa field is stale and its loyalty lines come in four spellings | source decision, no wiki loyalty parser |
| QDR-5 | The API still lists 35 removed quests; the wiki category holds 47 Arena pages | liveness rule |
| QDR-6 | The 18 seasonal quests are on the wiki only, marked by one requirement line | seasonal exception, its marker |
| QDR-7 | Ten pages are shared by two or three API records; three records link to a wrong page | collision order, alias list |
| QDR-8 | No published quest or item has carried an external ID since January | IDs in scope, carry-over bridge, log sync |
| QDR-9 | No full regeneration has succeeded in the fork era | guards, runbook, diff report |
| QDR-10 | Quest identity is the wiki URL, recomputed every run; no NormalizedName column exists | identity rule, NormalizedName column |
| QDR-11 | 1.1 renamed 91 published quests, removed 35, reused 8 titles; the 2025-12-19 snapshot bridges all but one | carry-over decision, spot checks |
| QDR-12 | Kappa is 248 today including Collector; Collector's rows hold one stale entry | Collector synthesis fix, expected 13 |
| QDR-13 | Recorded progress is keyed by the stored normalized name, so a rename orphans it | data-side carry-over, SQL-form pin |
| QDR-14 | The fielded build and main read the same columns the same way | format-1 verdict, five constraints |
| QDR-15 | Two value vocabularies break the old build outright | RequirementType and Faction constraints |
| QDR-16 | The app never decodes a quest ID and reloads quest rows only at start | pinning old IDs is safe, restart note |
| QDR-17 | Only two editor actions call tarkov.dev live; the wiki export works again | JSON client scope, runbook |
| QDR-18 | The pipeline has no loyalty parser, no pruning, and silent empty-cache paths | guards list |
| QDR-19 | Child-table upserts are table-global diffs; Collector rows are exempt from deletion; foreign keys are enforced | Collector fix, trader-requirement table, cascade wording |
| QDR-20 | Items enter only through Fetch Wiki Data; icons are wiki-keyed PNGs shipped in releases; traders are written only by the from-cache path | icon-pack release, runbook step 5 |
| QDR-21 | Hideout requirements join to items through `Items.BsgId` and resolve nothing today | hideout refresh in scope |
| QDR-22 | The only fielded build is v2026.7.0, pre-channel, polling the old repository name every five minutes | release coupling, legacy smoke design |
| QDR-23 | Publishing is a direct push today; CI runs after; drift baseline adoption is manual | publish via PR, baseline in the publish commit |
| QDR-24 | The release skill tags its own bump commit | release sequence wording |
| QDR-25 | No test guards the published content; the Korean guard is skipped | content guards, un-skipping |
| QDR-26 | Special:Export and the wiki API block curl's default user agent; Export answered 403 in June | export guard, fetch notes |

## Upstream sources

### QDR-1: tarkov.dev GraphQL is down; json.tarkov.dev is the supported API

**Fact.** `POST https://api.tarkov.dev/graphql` returns HTTP 422 with the body
`{"errors":["GraphQL server unavailable. Try again later."]}` and has since about
2026-07-22. The maintainers point at `https://json.tarkov.dev/`, which the
tarkov.dev front end itself reads: plain GET, no authentication. Data files are
language neutral (every translatable string is a key such as `"<taskId> name"`);
locale files sit at `{path}_{lang}`. The tasks file carried `ETag` and
`Last-Modified` (rebuilt 2026-08-21 01:33Z); locale files carry
`Cache-Control: max-age=691200`. Query-string or path variants for the language
(`?lang=ko`, `/ko/regular/tasks`) do nothing.

**Evidence.**
- `curl -s -o /dev/null -w '%{http_code}' -X POST https://api.tarkov.dev/graphql -H 'content-type: application/json' -d '{"query":"{ tasks { id } }"}'`
  prints `422`; the body is the quoted error.
- `curl -s https://json.tarkov.dev/endpoints` lists the endpoints with
  `"gameModes":["regular","pve","pvp-season"]` and a `languages` array of 19
  codes; each endpoint carries a `translations` flag.
- the-hideout/tarkov-api issue 474 (maintainer reply, 2026-07): "The GraphQL API
  is down for the moment, but you have the Json API who alive
  (https://json.tarkov.dev/endpoints). Tarkov.dev is based on this Json API and
  not on the GraphQL." the-hideout/tarkov-data-manager issue 851 (2026-08-05)
  relays the maintainers' recommendation to use the JSON API.
- `src/modules/api-request.mjs` on the-hideout/tarkov-dev `main` hardcodes
  `const apiUrlProd = "https://json.tarkov.dev/";` and fetches
  `${path}_${lang}` for locale data.
- `curl -sI https://json.tarkov.dev/regular/tasks` shows `ETag` and
  `Last-Modified`; `curl -sI https://json.tarkov.dev/regular/tasks_ko` shows
  `Cache-Control: max-age=691200`.

**Bears on.** The source decision and the spec's Design 1 (a JSON client replacing
the GraphQL transport outright). If GraphQL returned, nothing changes: the JSON
surface is the one the maintainers run.

### QDR-2: The JSON task set carries every 1.1 gate as structured data

**Fact.** `TASKS` holds 517 tasks keyed by id. Each task object has 24 keys:
`availableDelaySecondsMax, availableDelaySecondsMin, experience, factionName,
failConditions, failureOutcome, finishRewards, id, kappaRequired,
lightkeeperRequired, map, minPlayerLevel, name, neededKeys, normalizedName,
objectives, otherRequirements, restartable, startRewards, taskImageLink,
taskRequirements, trader, traderRequirements, wikiLink`. Counts on the capture:
`kappaRequired` true on 13 (Collector, Chemical 1-3, The Tarkov Shooter 1-4,
Postman Pat 1-2, Sew it Good 1-2, Shooter Born in Heaven); `minPlayerLevel` 0 on
282; `taskRequirements` empty on 296, otherwise `[{task, status[]}]` with
statuses complete 227 / active 22 / failed 6 and AND semantics only;
`traderRequirements` non-empty on 110 tasks, with 112 `level/>=` entries on 99
tasks (LL2 38 / LL3 36 / LL4 38) and 12 `reputation` entries on 12 tasks;
`otherRequirements` holds story gates (globalVariable 164, dialogue 12) the app
does not model; `factionName` Any 505 / BEAR 6 / USEC 6; objectives are typed
with stable ids. There is no `traderLevelRequirements` key, no task kind and no
edition key. `pvp-season` has 491 tasks, a strict subset of `regular`; `pve` has
514, differing only in the 23-task Arena chain's ids; one shared task
(provide-viewership) differs in field values across modes. Korean names: 289 of
the 517 task names are real Korean; Japanese: 0 contain Japanese script (14
differ from English only by casing or a stale English title).

**Evidence.** Over `TASKS`:
- `jq '.data.tasks | length'` = 517; `jq '.data.tasks | to_entries[0].value | keys'` for the key list.
- `jq '[.data.tasks[] | select(.kappaRequired)] | length'` = 13.
- `jq '[.data.tasks[] | select(.minPlayerLevel == 0)] | length'` = 282.
- `jq '[.data.tasks[] | select(.taskRequirements == [])] | length'` = 296;
  `jq '[.data.tasks[].taskRequirements[].status[]] | group_by(.) | map({(.[0]): length}) | add'`.
- `jq '[.data.tasks[] | select(.traderRequirements != [])] | length'` = 110;
  `jq '[.data.tasks[].traderRequirements[] | select(.requirementType == "level")] | length'` = 112;
  `jq '[.data.tasks[] | select(any(.traderRequirements[]; .requirementType == "level"))] | length'` = 99;
  `jq '[.data.tasks[].traderRequirements[] | select(.requirementType == "level") | .value] | group_by(.) | map({(.[0]|tostring): length}) | add'`.
- `jq '[.data.tasks[].factionName] | group_by(.) | map({(.[0]): length}) | add'`.
- Mode counts: the same `length` over `.../pvp-season/tasks` (491) and `.../pve/tasks` (514).
- Korean: with `TASKS_EN` and `TASKS_KO`, for every task id `k` compare
  `TASKS_KO.data["<k> name"]` with the English value and test it for Hangul
  (`test("[\\uac00-\\ud7a3]")`): 289 real. Japanese: the same over `TASKS_JA`
  with the script test `test("[\\u3040-\\u30ff\\u4e00-\\u9fff]")`: 0.

**Bears on.** The source decision (gates from the API), the `QuestTraderRequirements`
table (only `requirementType == "level"` is imported), the prerequisite mapping
(complete/active/failed onto the existing Complete/Accept/Fail vocabulary, GroupId
0), `MinLevel` 0 stored as NULL, the `regular`-only client, R6 (Korean yes,
Japanese still English), and the spec's Non-Goal on reputation requirements (the
app's karma gate cannot express `<=`).

### QDR-3: Five tasks require loyalty with a trader other than their giver

**Fact.** Of the 112 level entries, 15 entries on 5 tasks name a trader other
than the quest's giver: chemical-part-3 (a Skier quest requiring Jaeger LL2),
thirsty-hounds (three traders), broadcast-part-1 (two), the-good-times-part-1
(five), and collector (seven traders at LL4, plus Fence reputation). The roadmap
spec's premise that "every loyalty requirement names the quest's own giving
trader" holds for 94 of the 99 loyalty-gated tasks.

**Evidence.** Over `TASKS`:
`jq -r '.data.tasks[] | . as $t | select(any($t.traderRequirements[]; .requirementType == "level" and .trader != $t.trader)) | $t.normalizedName'`
prints the five names;
`jq '[.data.tasks[] | . as $t | select(any($t.traderRequirements[]; .requirementType == "level")) | select(all($t.traderRequirements[] | select(.requirementType == "level"); .trader == $t.trader))] | length'`
= 94. Trader ids resolve to nicknames through `TRADERS_EN` keys `"<id> Nickname"`
(Jaeger = `5c0647fdd443bc2504c2d371`, Skier = `58330581ace78e27b8b10cee`).

**Bears on.** The reversal of the roadmap spec's "column, not a table" decision
(superseded note appended in the phase-3 PR) and the content guard that pins the
Chemical - Part 3 row naming Jaeger at level 2. The roadmap itself named the
table as the fallback for exactly this case.

### QDR-4: The wiki's Kappa field is stale and its loyalty lines come in four spellings

**Fact.** `Template:Infobox quest` revision 348972 (2026-08-03 08:04Z, user
The3ncy, comment "Remove quest Kappa requirement as part of 1.1.0.0 task
changes") stopped rendering `reqkappa`; the parameter values on the pages were
never updated. Over the 854 pages in `Category:Quests` on the capture date,
`|reqkappa` read Yes on 246, No on 593, other on 15; Stirrup, Shortage, Debut,
Gunsmith - M4A1 and Sew it Good - Part 4 all still said Yes against the API's 13.
Requirements are free-text bullets under `==Requirements==` in four loyalty
phrasings: `* Must reach Loyalty Level N with [[Trader]] to obtain this quest.`
(34 pages), `* Obtain level N loyalty with [[Trader]]` (30), `* Must be Loyalty
Level N to start this quest` (20 plus spacing variants), and the level line
`* Must be level N to start this quest.` (157). `|previous` is empty on freed
quests and on at least one the game still chains (Sew it Good - Part 4, which the
API chains to Part 3). On the seven spot-check pages the wiki and the API agree
on 17 of 28 facts: kappa 2/7, minimum level 4/7 (Stirrup 8 vs 0, Collector none
vs 42, Gunsmith - M4A1 15 vs 26), loyalty 6/7 (the wiki omits Mechanic LL3 on
Gunsmith - M4A1), prerequisites 5/7 (Collector's list differs, Sew it Good -
Part 4's chain is missing). Corpus-wide prerequisite comparison: agree 310, wiki
superset 111, API superset 60, conflict 17. The `Quests` and `Collector` pages
are admin-locked indefinitely, Stirrup/Shortage/Debut until 2026-09-14T00:24Z,
and the week before capture saw 179 edits on 78 quest pages; Collector was
edited at 2026-08-21 08:59Z ("requirements change").

**Evidence.**
- Template history: `api.php?action=query&prop=revisions&titles=Template:Infobox%20quest&rvprop=ids|timestamp|user|comment&rvlimit=30&format=json`
  (revision 348972). The template's current source renders `image, icon,
  location, given by, reward, achievement, previous, leads to, related` and no
  `reqkappa` row: `api.php?action=parse&page=Template:Infobox%20quest&prop=wikitext&format=json`.
- Census: list members with `api.php?action=query&list=categorymembers&cmtitle=Category:Quests&cmtype=page&cmlimit=500&format=json`
  (with `cmcontinue`), fetch each page's wikitext with
  `action=parse&page=<Title>&prop=wikitext`, and count `\|reqkappa\s*=` values and
  the Requirements bullets above. Sample pages to read by hand: `Stirrup`
  (`|previous     =` empty, `|reqkappa     =<font color="red">Yes</font>`,
  `* Must be level 8 to start this quest.`), `Debut`
  (`* Must reach Loyalty Level 1 with [[Prapor]] to obtain this quest.`),
  `Collector` (`* Obtain level 4 loyalty with [[Therapist]]` and six more traders,
  `* [[Scavs#Scav karma|Scav karma]] of at least +3`).
- Cross-check: the same pages against `TASKS` entries matched by `wikiLink`
  (Stirrup `596b455186f77457cb50eccb`: `minPlayerLevel` 0, `kappaRequired` false,
  no requirements; Gunsmith - M4A1 `5ac244eb86f7741356335af1`: `minPlayerLevel`
  26, Mechanic level 3; Sew it Good - Part 4 `5ae4496986f774459e77beb6`: requires
  Part 3).
- Protection: `api.php?action=query&prop=info&inprop=protection&titles=Quests|Collector|Stirrup|Shortage|Debut&format=json`.
  Edit rate: `api.php?action=query&list=recentchanges&rcnamespace=0&rclimit=500&rcend=<7 days ago>&format=json`
  filtered to category members.

**Bears on.** The source decision (Kappa, level, loyalty and prerequisites from the
API), the spec's Non-Goal "no wiki loyalty parser", and keeping the wiki
`|previous` grammar only for the report's disagreement list. If the wiki updated
`reqkappa` and converged on one loyalty phrasing, the wiki path would become
viable again, but the API would still be the cheaper and id-stable source.

### QDR-5: The API still lists 35 removed quests; the wiki category holds 47 Arena pages

**Fact.** By external id, 35 published quests whose wiki pages went historical
(for example Loyalty Buyout, Spa Tour - Part 2, Cargo X - Part 3, Farming -
Part 2, Athlete) are still present in `TASKS` under their old titles: tarkov.dev
has not pruned them. In the other direction, `Category:Quests` contains 47 pages
of the separate Arena game's questline (Gladiator Life - Part 1..10, All for the
Show, Advertising Business - Part 1..6, No Limit to Perfection, and so on) that
no API record carries and the app has never shown; the editor's 2026-06-13 run
log shows such pages being inserted by the category crawl. Operational daily and
weekly tasks are described generically in the Quests page's
`==Operational Tasks==` section and are not category members.

**Evidence.**
- Removed set: for each published quest `Id` with a `BsgId` in the 1.0.7 snapshot
  (QDR-8), look the id up in `TASKS` and decode its `wikiLink` title; 35 ids still
  carry the title the database has while that title is absent from the category
  listing and carries `[[Category:Historical content]]` on the 5 pages sampled
  (`api.php?action=query&prop=categories&titles=Loyalty%20Buyout|Athlete&format=json`).
- Arena pages: category members whose title matches no `wikiLink` and no
  `normalizedName` in `TASKS` and carries no seasonal line (QDR-6): 47.
- `api.php?action=parse&page=Quests&prop=wikitext` shows the per-trader tables
  and the generic Operational Tasks section; no member of the category contains
  "Operational", "Daily" or "Weekly".

**Bears on.** The liveness rule (a quest ships only with a live page and a matching
record) and the PRD's Non-Goal on a story/side/operational flag. A pruned API or a
cleaned-up wiki category would make one half of the rule redundant, not wrong.

### QDR-6: The 18 seasonal quests are on the wiki only, marked by one requirement line

**Fact.** The 18 KORD BREACH quests (Uninvited Guests - Part 1 and 2, Break the
Chain, Cast the Net, Consequences of Our Decisions, Desperate Assault, Digital
Puzzle, Final Stretch, Forbidden Knowledge, Historical Perspectives, Key to
Understanding, Know Your Enemy, Reverse Gear, Riding the Wave, Sheep in Wolf's
Clothing, Stay Clear of Blast Zone, Timeout, Unanswered Calls) have wiki pages but
no record in any JSON game mode, `pvp-season` included. Every captured seasonal
page carries the requirement bullet
`* Must be playing in the [[Seasons#Season 1: KORD BREACH|Seasonal mode]].`; an
earlier census also recorded the bare `[[PvP Season]]` link form. The pages are
in `Category:Quests` only, while `WikiQuestService.ExcludeCategories` already
excludes a category named `Seasonal quests`.

**Evidence.**
- `jq -r '.data | to_entries[] | select(.key | endswith(" name")) | .value' TASKS_EN | grep -c 'Uninvited Guests'`
  = 0 (and 0 for each of the 18 names; Stirrup as the positive control returns 1);
  the same over the `pvp-season` and `pve` locale files.
- `api.php?action=parse&page=Break%20the%20Chain&prop=wikitext&format=json`: the
  Requirements section holds the quoted line; `[[Category:Quests]]` is the only
  category.
- `TarkovDBEditor/Services/WikiQuestService.cs`, `ExcludeCategories`
  (`"Event quests", "Seasonal quests", "Legacy quests", "Event content", "Historical content"`).

**Bears on.** The PRD's seasonal exception to the liveness rule, the spec's
`ExtractIsSeasonal` accepting both spellings, the guard that fails a run which
detects zero seasonal pages while pages mention a seasonal mode, and the content
guard pinning "Uninvited Guests - Part 1". The exception retires itself when the
API carries the quests (the page then matches by `wikiLink`).

### QDR-7: Ten pages are shared by two or three API records; three records link to a wrong page

**Fact.** Ten wiki titles are the `wikiLink` of two or three tasks: four BEAR/USEC
pairs (Drip-Out - Part 1 and 2, Textile - Part 1 and 2; neither variant is
`factionName: Any`), Make Amends (three), New Beginning (three), and single
duplicates for Battery Change, The Price of Independence, The Huntsman Path -
Administrator (`639136df4b15ca31f76bc31f` and `6a45208043b8d7604d00b8d5`, created
2026-06-29, identical in every field, neither required by another task) and The
Tarkov Shooter - Part 5 (`5bc4826c86f774106d22d88b`, old Part 5, required by
nothing, and `5bc4836986f7740c0152911c`, old Part 6, which
`the-tarkov-shooter-part-6` requires). Eight `wikiLink`s decode to titles that are
not pages; three of them are the prestige tasks `new-beginning-2/3/4`
(`6761ff17cdc36bd66102e9d0`, `6848100b00afffa81f09e365`,
`68481881f43abfdda2058369`) linking to `Neuanfang`, whose normalized names
(`new-beginning-2`) also do not match the pages (`New Beginning (Prestige 2)` ->
`new-beginning-prestige-2`). Two live records, The Huntsman Path - Control and
Secret Message, have no page at all.

**Evidence.** Over `TASKS`:
- `jq '[.data.tasks[].wikiLink] | group_by(.) | map(select(length > 1)) | length'` = 10;
  `jq -r '[.data.tasks[]] | group_by(.wikiLink) | map(select(length > 1)) | .[] | map("\(.id) \(.normalizedName) \(.factionName)") | join(" | ")'`
  lists the records per page.
- `jq -r '.data.tasks[] | select(.wikiLink | endswith("The_Tarkov_Shooter_-_Part_6")) | .taskRequirements[].task'`
  prints `5bc4836986f7740c0152911c`.
- `jq -r '.data.tasks[] | select(.wikiLink | endswith("/Neuanfang")) | "\(.id) \(.normalizedName) \(.minPlayerLevel)"'`
  prints the three prestige records (levels 30, 35, 40);
  `api.php?action=query&titles=Neuanfang&format=json` returns `missing`.
- Id creation time: the first eight hex digits of a 24-hex id are a Unix
  timestamp (`6a452080` = 2026-06-29).

**Bears on.** The spec's four-step collision order (pair -> lowest id with
`Faction` NULL; required-by; previous row's id; newest id), the alias list
`quest-match-overrides.json`, the PRD risk that only one record's log events match
per such page, and the decision to let New Beginning (Prestige 5) and (Prestige 6)
leave until the API carries them. A plain "lowest id" rule, the first draft, would
have given the Part 5 page a dead id and deleted the live Part 6 prerequisite.

## Published data

### QDR-8: No published quest or item has carried an external ID since January

**Fact.** In `data/v1/tarkov_data.db` (token 1.0.10, `PRAGMA user_version` 1)
`BsgId` is NULL on 488/488 quests and 4014/4014 items. The 1.0.7 snapshot (commit
`ebbc60c`, 2025-12-19) holds 473 quest and 2648 item ids keyed by the same
`Id`s; all 473 are live task ids in `TASKS`. The ids vanished with the 1.0.8
regeneration (`5065d35`, 2026-01-14). Consequences in the field:
`LogSyncService` resolves the log's template id through
`QuestProgressService.GetTaskById`, whose index holds wiki ids and `BsgId`s, so
no quest event has matched since; `HideoutDbService` joins
`HideoutItemRequirements.ItemId` to `Items.BsgId`, so 0 of 317 rows resolve.

**Evidence.**
- `SELECT COUNT(*) FROM Quests WHERE BsgId IS NULL OR BsgId = ''` = 488 and the
  same over `Items` = 4014, on `data/v1/tarkov_data.db`.
- `git show ebbc60c:TarkovHelper/Assets/tarkov_data.db > snapshot.db`, then
  `SELECT COUNT(*) FROM Quests WHERE length(BsgId) = 24` = 473 and over `Items`
  = 2648; `SELECT COUNT(*) FROM Quests WHERE length(BsgId) = 24` on
  `git show 5065d35:...` = 0.
- `SELECT COUNT(*) FROM HideoutItemRequirements h LEFT JOIN Items i ON h.ItemId = i.BsgId WHERE i.Id IS NOT NULL` = 0 (of 317).
- `TarkovHelper/Services/LogSyncService.cs` (the `templateId.Split(' ')[0]`
  parse and `GetTaskById`), `QuestProgressService.BuildTaskIndexes`,
  `HideoutDbService` (the `LEFT JOIN Items i ON h.ItemId = i.BsgId`).

**Bears on.** The PRD decision "Restoring external IDs is in scope, not an
enrichment", R5, the one-time backfill step and its guard (the refresh refuses an
unbackfilled database), and the hideout refresh. This is a live defect today,
independent of 1.1.

### QDR-9: No full regeneration has succeeded in the fork era

**Fact.** Every quest row carries `UpdatedAt` 2026-01-15T00:09:39Z; the 1.0.10
publish (`95b9b9a`, 2026-06-13) patched `NameKO`/`NameJA` onto the blob of
`ef71936`, itself a rewrite of the database in a docs-titled commit with no token
bump. The 2026-06-13 editor runs received HTTP 403 from every `Special:Export`
batch, left the wiki caches empty and reported success; the committed database
never received those rows (`fix-quest-name-localization.md` records the episode).
The only on-disk caches are in the main checkout's Debug output: tarkov.dev caches
from 2026-06-13 (502 tasks, 3946 items, 16 traders, 26 stations), an empty quest
cache, no icons.

**Evidence.**
- `SELECT MIN(UpdatedAt), MAX(UpdatedAt) FROM Quests` = one value,
  `2026-01-15T00:09:39.1777931Z`.
- `git rev-parse 549396a:TarkovHelper/Assets/tarkov_data.db ef71936:TarkovHelper/Assets/tarkov_data.db`
  prints two blobs while
  `git show 549396a:TarkovHelper/Assets/db_version.txt` and
  `git show ef71936:TarkovHelper/Assets/db_version.txt` both print `1.0.9`.
- `git show 95b9b9a --stat` touches only the database and `db_version.txt`; its
  body records "288/488 (~59%) KO".
- `TarkovDBEditor/bin/Debug/net8.0-windows/wiki_data/cache/quest_update.log` in
  the developer's main checkout (eleven `403 (Forbidden)` lines on 2026-06-13;
  `quest_cache.json` is `{"lastUpdated":..., "quests":{}}`).

**Bears on.** The spec's guard list (empty wiki cache, export batch failure, stale
task cache, NULL-rate and match-rate collapses), the runbook starting from the
published database, and the diff report as the review artefact: the refresh is
the first real regeneration since January and touches every row.

### QDR-10: Quest identity is the wiki URL, recomputed every run; no NormalizedName column exists

**Fact.** `Quests.Id` is base64 (with padding) of
`https://escapefromtarkov.fandom.com/wiki/<Title_with_underscores>`, rebuilt from
the cached title on every run; `Items.Id` is the url-safe variant without
padding. The `Quests` table has 32 physical columns, of which
`_schema_meta.SchemaJson` lists 19 (the 13 approval columns are omitted), and no
`NormalizedName`. Both the fielded build and main feature-detect such a column
and otherwise derive `LOWER(REPLACE(REPLACE(REPLACE(Name,' ','-'),'''',''),'.',''))`;
the editor's `NormalizeQuestName` is tarkov.dev-style (strips everything outside
`[a-z0-9-]`, collapses dashes), and 228 of 488 names differ between the two forms
(`sew-it-good---part-4` vs `sew-it-good-part-4`). One name carries a typographic
apostrophe (U+2019, "What's on the Flash Drive?"), which the SQL form keeps.

**Evidence.**
- `SELECT Id FROM Quests WHERE Name = 'A Shooter Born in Heaven'` decodes
  (`base64 -d`) to `https://escapefromtarkov.fandom.com/wiki/A_Shooter_Born_in_Heaven`.
- `PRAGMA table_info(Quests)` = 32 rows; `SELECT SchemaJson FROM _schema_meta WHERE TableName = 'Quests'` lists 19.
- `TarkovHelper/Services/QuestDbService.cs`, `LoadBaseQuestsAsync` (the
  `ColumnExistsAsync("NormalizedName")` branch and the SQL expression); identical
  in `git show v2026.7.0:TarkovHelper/Services/QuestDbService.cs`.
- `TarkovDBEditor/Services/RefreshDataService.cs`, `LoadQuestsFromCacheAsync`
  (the `Convert.ToBase64String` of the page URL) and `NormalizeQuestName`;
  `TarkovWikiDataService.GenerateWikiId`.
- Differing forms: `SELECT COUNT(*) FROM Quests WHERE LOWER(REPLACE(REPLACE(REPLACE(Name,' ','-'),'''',''),'.','')) <> <tarkov.dev-style form of Name>`
  = 228 (compute the second form in the client).

**Bears on.** The identity rule (an `Id` is the URL at first publication, no longer
recomputed), the `NormalizedName` column pinned to the SQL form, the guard
invariant `NormalizedName == SqlForm(TitleOf(Id))`, and the technical decision
that a tarkov.dev-style column would have been the one format-2 trigger.

### QDR-11: 1.1 renamed 91 published quests, removed 35, reused 8 titles; the snapshot bridges all but one

**Fact.** Of the 488 published quest names, 127 have no wiki page under that
title. Joined to the 1.0.7 snapshot's `BsgId` by `Id` and then to `TASKS` by id:
91 resolve to a different title that exists in `Category:Quests` (renames, such
as A Shooter Born in Heaven -> Shooter Born in Heaven and Gunsmith - Part 7 ->
Gunsmith - M4A1; multi-part chains were de-numbered, none merged), 35 resolve to
the same title (removed, QDR-5), 1 is a punctuation redirect (Half Empty ->
Half-Empty), and 126 of the 127 have a snapshot id at all; the exception is No
Questions Asked (now Special Order, `68ee1c18b4e5bc9a68018cd7`), which never
carried a `BsgId`. Eight current titles now belong to a different task than the
row carrying them: Sew it Good - Part 2/3/4 rotated (old Part 2
`5ae4495c86f7744e87761355` is now titled Part 3, old Part 3
`5ae4496986f774459e77beb6` is now Part 4, old Part 4 `5ae4497b86f7744cf402ed00`
is now Part 2 and kappa-required), The Punisher - Part 1/2/3 rotated, The Tarkov
Shooter - Part 6/7 shifted. Of the 176 category titles the database lacks: 92
rename targets, 18 seasonal (QDR-6), 4 new 1.1 quests (Demonstration model, Fall
Ailment, Hiking, The Tarkov Butcher), 15 quests added between January and April
2026, 47 Arena pages (QDR-5). No snapshot id changed its page title between
2025-12-19 and 2026-01-15.

**Evidence.**
- Missing titles: `api.php?action=query&titles=<50 names joined by |>&format=json`
  returns `missing` for 127 of the 488 `Quests.Name` values.
- Bridge: `SELECT Id, BsgId, Name FROM Quests` on the snapshot (QDR-8) joined by
  `Id` to the current table; for each `BsgId` read `.data.tasks["<id>"].wikiLink`
  from `TASKS`, URL-decode the title, and test membership in the category listing.
- Rename pairs: `api.php?action=query&list=logevents&letype=move&lenamespace=0&lelimit=500&leend=2026-08-01T00:00:00Z&format=json`
  (115 quest-page moves after filtering File: and User:); the old titles also
  appear in the delete log (`letype=delete`, 96 of 127).
- Title reuse: for each current row, compare `Name` with the title the
  snapshot's task now links to; 8 rows differ while the row's own title is now
  another task's `wikiLink`. The Sew it Good ids above are read from `TASKS`
  (`jq -r '.data.tasks[] | select(.normalizedName | startswith("sew-it-good")) | "\(.normalizedName) \(.id) \(.kappaRequired)"'`)
  and from the snapshot (`SELECT BsgId, Name FROM Quests WHERE Name LIKE 'Sew it Good%'`).
- New 1.1 quests: `TASKS` records whose ids were created in July 2026 (first
  eight hex digits) and whose titles are not on the Quests overview page.

**Bears on.** The PRD's carry-over decision and R4, the spot-check wording (the
"Sew it Good - Part 4" title survives but the quest behind it changed), the
hand-bridged Special Order row, and the expected diff-report counts. Had the
snapshot lacked ids, the only bridge would have been the wiki move log.

### QDR-12: Kappa is 248 today including Collector; Collector's rows hold one stale entry

**Fact.** `KappaRequired = 1` on 248 quests including Collector itself. Collector's
synthesized `QuestRequirements` hold 248 rows (GroupId 0): 247 kappa quests plus
Grenadier, whose flag is 0. The row survives because
`UpsertQuestRequirementsAsync` skips Collector-owned rows in its delete loop and
`AddCollectorKappaRequirementsAsync` deletes only the self-reference. The app's
gauge (`QuestGraphService.GetCollectorProgress`) counts every `ReqKappa` task
including Collector; `CollectorPage` walks the synthesized `Previous` graph. The
1.1 set in `TASKS` is 13 including Collector, which the app would show as a gauge
total of 13 with 12 synthesized rows.

**Evidence.**
- `SELECT COUNT(*) FROM Quests WHERE KappaRequired = 1` = 248;
  `SELECT KappaRequired FROM Quests WHERE Name = 'Collector'` = 1.
- `SELECT q.Name, q.KappaRequired FROM QuestRequirements r JOIN Quests q ON q.Id = r.RequiredQuestId WHERE r.QuestId = (SELECT Id FROM Quests WHERE Name = 'Collector') AND q.KappaRequired = 0`
  = Grenadier; the row count for Collector = 248.
- `TarkovDBEditor/Services/RefreshDataService.cs`, `UpsertQuestRequirementsAsync`
  (the `if (collectorId != null && existingData[id].QuestId == collectorId) continue;`
  in the delete loop) and `AddCollectorKappaRequirementsAsync` (deletes only
  `QuestId = RequiredQuestId = collector`).
- `TarkovHelper/Services/QuestGraphService.cs`, `GetCollectorProgress`; the
  archived decision record `collector-quest.md` (2025-12; synthesis from flags,
  never curated).

**Bears on.** The spec's Collector synthesis fix (exemption dropped, rows rebuilt
from the flag set) and the content guards (13 flagged, 12 rows, gauge total 13).
The roadmap's "computed, not curated" decision stands.

## App compatibility

### QDR-13: Recorded progress is keyed by the stored normalized name, so a rename orphans it

**Fact.** `QuestProgress(ProfileId, Id, NormalizedName, Status)` is written with the
wiki `Id` as key and the normalized name beside it; the in-memory read dictionary
is keyed by the stored name when present; `GetStatus` tries `Ids[0]`, then
`NormalizedName`. A page rename changes both, so the row becomes an inert orphan
that no page lists; no alias, legacy-id or rename mechanism exists in the app.
`ObjectiveProgress` is keyed positionally (`"{normalizedName}:{index}"`). The
same code shape is at `v2026.7.0`. Every consumer of `NormalizedName` in the
app (the prerequisite graph, which is built through the id lookup; the literal
"collector" in `QuestGraphService.IsCollectorQuest` and `CollectorPage`;
objective keys; `QuestListPage.SelectQuest`; `ConfigMigrationService`) reads the
same value the column would carry, so a value that is no longer derivable from
the current `Name` breaks nothing.

**Evidence.** `TarkovHelper/Services/QuestProgressService.cs`: `ProgressKeyOf`,
`GetStatus`, `SetQuestRow` (whose comment states the two key policies do not
match); `UserDataDbService.LoadQuestProgressAsync` (`normalizedName ?? id` as the
dictionary key) and `SaveQuestProgressAsync`; `QuestListPage.SetObjectiveCompleted`;
`grep -rniE 'alias|LegacyId|OldId|renam' TarkovHelper/Services TarkovHelper/Models`
(only dogtag item aliases and map aliases); the same files under
`git show v2026.7.0:<path>`.

**Bears on.** The decision to carry identity over in the data rather than in app
code (it has to reach the fielded build), the SQL-form pin, the PRD risk on
positional objective ticks, and `LegacySmokeE2ETests` seeding progress rows the
way v2026.7.0 writes them.

### QDR-14: The fielded build and main read the same columns the same way

**Fact.** `QuestDbService` hard-requires ten `Quests` columns (Id, Name, NameKO,
NameJA, Trader, Location, MinLevel, MinScavKarma, KappaRequired, Faction) and
feature-detects seven (NormalizedName, BsgId, RequiredEdition, ExcludedEdition,
RequiredPrestigeLevel, RequiredDecodeCount, WikiPageLink); `ItemDbService`
hard-requires twelve `Items` columns (Id, BsgId, Name, NameEN, NameKO, NameJA,
ShortNameEN, ShortNameKO, ShortNameJA, WikiPageLink, IconUrl, Category); the
hideout tables are read unconditionally once they exist. A schema failure is
caught and leaves the previous in-memory list in place. Trader, map and faction
filters are data-derived; `QuestGraphService.TraderOrder` is only a sort
tie-break. All of this holds identically at `v2026.7.0` and at `a214a95`.

**Evidence.** `QuestDbService.LoadBaseQuestsAsync` (the SELECT list and the
`ColumnExistsAsync` calls), `ItemDbService.LoadItemsFromDbAsync`,
`HideoutDbService` (`TableExistsAsync` guards, unconditional columns),
`QuestListPage` filter construction (`tasks.Select(t => t.Trader).Distinct()`),
each compared with `git show v2026.7.0:<path>`.

**Bears on.** The format-1 verdict and the spec's five pre-write constraints
(vocabularies, no NULL in a hard-required column, `NormalizedName` invariant,
`Trader` non-NULL, `user_version` 1). The `DataFormatDriftTests` ratchet sees
tables and types only, so these constraints are the human half of the check.

### QDR-15: Two value vocabularies break the old build outright

**Fact.** `QuestProgressService.IsStatusSatisfied` recognizes only
active/start/accept, complete and failed/fail; any other
`QuestRequirements.RequirementType` never satisfies, locking the quest forever.
`SettingsService.ShouldIncludeTask` is case-insensitive string equality on
`Faction`, so a non-NULL value other than the player's faction (for example the
API's literal `Any`) hides the quest from every user who chose a faction. The
published data holds exactly Complete 769 / Accept 23 / Fail 2 and Faction NULL
484 / Bear 2 / Usec 2.

**Evidence.** `QuestProgressService.IsStatusSatisfied`, `SettingsService.ShouldIncludeTask`
(both revisions);
`SELECT RequirementType, COUNT(*) FROM QuestRequirements GROUP BY 1` and
`SELECT Faction, COUNT(*) FROM Quests GROUP BY 1`.

**Bears on.** The two strongest publish constraints (status mapping inside the
existing vocabulary; `Any` -> NULL, BEAR/USEC pairs -> NULL) and the PRD's
distinction between "degrade" (accepted legacy view) and "break" (forbidden).

### QDR-16: The app never decodes a quest ID and reloads quest rows only at start

**Fact.** No code under `TarkovHelper/` calls `FromBase64String` on a quest id;
`WikiPageLink` carries the URL. `DatabaseUpdated` reloads the DB services and
re-renders pages, but `QuestProgressService`, `QuestGraphService` and
`HideoutProgressService` are initialized only by
`MainWindow.LoadAndShowQuestListAsync` (startup, reset, applied log sync, data
migration), so quest rows on screen change after a restart. Same at `v2026.7.0`.

**Evidence.** `grep -rn FromBase64String TarkovHelper/` (no quest-id use);
`DatabaseUpdateService.RaiseDatabaseUpdated` and its subscribers;
`grep -rn 'DatabaseUpdated\|DataRefreshed' TarkovHelper/Services/QuestProgressService.cs TarkovHelper/Services/QuestGraphService.cs TarkovHelper/Services/HideoutProgressService.cs`
(none); callers of `MainWindow.LoadAndShowQuestListAsync`.

**Bears on.** Pinning an old `Id` on a renamed page is safe for the app; the PRD's
"visible after restart" Non-Goal; the legacy smoke being "launch, download,
relaunch".

## Pipeline (TarkovDBEditor)

### QDR-17: Only two editor actions call tarkov.dev live; the wiki export works again

**Fact.** Live GraphQL calls exist only in `TarkovDevDataService.CacheAllDataAsync`
(Cache Tarkov Dev Data; each of four parts fails independently and keeps its old
cache file) and in the tail of Export Wiki Quests
(`WikiQuestService.ExportQuestsAsync` -> `FetchTarkovDevQuestsAsync`, after the
wiki cache is already saved by `MainWindow.ExportWikiQuests_Click`).
`RefreshDataService` reads tarkov.dev from cache only; `HideoutDataService` goes
live only on an empty cache. The only task query ever sent was
`{ tasks(lang: en) { id tarkovDataId name normalizedName wikiLink trader { name } } ko: tasks(lang: ko) { id name } ja: tasks(lang: ja) { id name } }`.
`Special:Export` answered 200 on 2026-08-21 with the editor's `TarkovDBEditor/1.0`
user agent; `api.php` with `rvprop=content` works as an alternative body source.

**Evidence.** `grep -rn 'api.tarkov.dev' TarkovDBEditor/` (two constants:
`TarkovDevDataService.GraphQLEndpoint`, `WikiQuestService.TarkovDevApiUrl`);
`TarkovDevDataService.CacheAllDataAsync`, `FetchAllQuestsAsync` (the query
string); `WikiQuestService.ExportQuestsAsync`; `RefreshDataService` call sites
of `LoadCachedQuestsAsync`, `LoadCachedItemsAsync`, `LoadCachedTradersAsync`;
`HideoutDataService.RefreshHideoutDataAsync` (cache first);
`curl -s -o /dev/null -w '%{http_code}' -A 'TarkovDBEditor/1.0' -X POST https://escapefromtarkov.fandom.com/wiki/Special:Export --data-urlencode 'pages=Collector' --data 'curonly=1'`
= 200.

**Bears on.** Design 1 (the JSON client replaces both GraphQL copies; Export Wiki
Quests stops contacting tarkov.dev) and the runbook order.

### QDR-18: The pipeline has no loyalty parser, no pruning, and silent empty-cache paths

**Fact.** No code in TarkovDBEditor mentions loyalty or a trader-level field; the
Requirements section is scanned by eight single-pattern extractors. A wiki page
matched to no task is kept with `BsgId` NULL; a task with no page is never
materialized; stale quest ids are deleted on every non-empty run; prerequisites
pointing outside the cache are dropped silently; cache entries are never pruned.
Refresh Data (from Cache) returns success with zero quests on an empty wiki cache;
an empty page body yields NULL Trader/MinLevel with no gate; `Special:Export`
batch failures are counted, not thrown; the item enrichment continues with
`BsgId` NULL when the item cache is missing. `ParseObjectiveLine`'s hardcoded map
list lacks The Labyrinth.

**Evidence.** `grep -rni 'loyalty\|MinTraderLevel\|traderLevel' TarkovDBEditor/ --include=*.cs`
(hideout-only hits); `WikiQuestService` extractors (`ExtractMinLevel`,
`ExtractKappaRequired`, `ExtractMinScavKarma`, `ExtractFaction`,
`ExtractRequiredEdition`, `ExtractExcludedEdition`, `ExtractRequiredDecodeCount`,
`ExtractRequiredPrestigeLevel`) and `ExportPagesAsync` (per-batch catch);
`RefreshDataService.LoadQuestsFromCacheAsync` (the `cachedQuests.Count == 0`
early return, the unmatched branch, the dropped-prerequisite branch),
`UpsertQuestsAsync` (deletes), `FetchAndProcessItemsAsync` (enrichment
fallthrough); `grep -rn '_questCache.Remove' TarkovDBEditor/` (none);
`ParseObjectiveLine`'s map array.

**Bears on.** The spec's Design 6 guard list and the decision to take loyalty from
the API rather than write a four-spelling parser.

### QDR-19: Child-table upserts are table-global diffs; Collector rows are exempt; foreign keys are enforced

**Fact.** `UpsertQuestRequirementsAsync`, `UpsertQuestObjectivesAsync`,
`UpsertOptionalQuestsAsync` and `UpsertQuestRequiredItemsAsync` recompute hashed
ids, delete vanished ids, insert or update the rest, and preserve approval only on
an unchanged content hash; `QuestObjectives` identity is `OBJ|QuestId|SortOrder`
(positional). `OptionalQuests` and `QuestRequiredItems` are emptied by an empty
parse while the other three skip it. Foreign keys are enforced on every
Microsoft.Data.Sqlite 8.0.11 connection by default: `PRAGMA foreign_keys` reads 1,
a dangling child insert fails with SQLite error 19, and the published database
has no dangling child rows.

**Evidence.** The four upsert methods and `UpdateDatabaseAsync`'s gating in
`RefreshDataService`; `DbQuestObjective.ComputeId`. Enforcement: on any
`Microsoft.Data.Sqlite` 8.0.11 connection (`TarkovDBEditor.csproj` and
`TarkovHelper.csproj` pin that version), `PRAGMA foreign_keys;` returns 1 and
`INSERT INTO QuestRequirements (Id, QuestId, RequiredQuestId) VALUES ('x', 'nope', 'nope')`
fails with error 19 against a copy of the database;
`SELECT COUNT(*) FROM QuestRequirements r LEFT JOIN Quests q ON q.Id = r.RequiredQuestId WHERE q.Id IS NULL`
= 0, and likewise for `QuestObjectives`, `QuestRequiredItems`, `OptionalQuests`.

**Bears on.** The Collector fix, the new `QuestTraderRequirements` upsert (declared
`ON DELETE CASCADE`, written after Quests in the same transaction), and the
correction of an earlier research claim (last section).

### QDR-20: Items enter only through Fetch Wiki Data; icons are wiki-keyed PNGs shipped in releases; traders are written only by the from-cache path

**Fact.** Items come from the wiki category crawl in `RefreshDataAsync` ->
`FetchAndProcessItemsAsync`; nothing imports `wiki_items.json`. Icons are
downloaded by `WikiCacheService.DownloadIconsAsync` into
`wiki_data/icons/{Items.Id}{ext}` (skip if exists; extension from the URL),
published by `DataPublishService.ItemIconGroup` as `*.png` only into
`TarkovHelper/Assets/icons`, and read by `ImageCacheService.GetLocalItemIcon` as
`{Items.Id}.png` from the app folder; they reach users only inside an app release
(`TarkovHelper.csproj`, `<None Update="Assets\icons\*.png">`). Git tracks the
folder under two case spellings: `Assets/icons` (3933 files, 26 of them under
`hideout/`) and `Assets/Icons` (115 item pngs, 9 webp, 1 svg); 4011/4014 items
have an icon, 3 audio tapes do not, 11 pngs have no item.
`UpdateTradersFromCacheAsync` is called only from `RefreshDataFromCacheAsync`;
the full path never writes `Traders` (15 rows today; `TRADERS_EN` has 16, the
new one being Survivor, who gives no task in `TASKS`).

**Evidence.** `RefreshDataService.FetchAndProcessItemsAsync`,
`grep -rn UpdateTradersFromCacheAsync TarkovDBEditor/` (one caller);
`grep -rn wiki_items TarkovDBEditor/` (written by the export, read only by
`EnrichWikiItemsAsync`); `WikiCacheService.DownloadIconsAsync`;
`DataPublishService` asset groups (`ItemIconGroup` pattern `*.png`);
`ImageCacheService.GetLocalItemIcon`;
`git ls-files TarkovHelper/Assets/icons | wc -l` = 3933 and
`git ls-files TarkovHelper/Assets/Icons | wc -l` = 125; icon coverage by
comparing `SELECT Id FROM Items` with the png basenames;
`SELECT COUNT(*) FROM Traders` = 15;
`jq '[.data.tasks[] | select(.trader == "69e0d6cc77b63940375b9173")] | length' TASKS` = 0.

**Bears on.** The icon-pack release coupling (R8), the PR B icon move to the
lower-case path, runbook step 5 (trader upsert added to the full path), and the
icon coverage guard.

### QDR-21: Hideout requirements join to items through `Items.BsgId` and resolve nothing today

**Fact.** `HideoutItemRequirements.ItemId` is a tarkov.dev item id; the editor
registers it as a foreign key to `Items.Id` but nothing bridges the two id spaces;
the app bridges at read time through `Items.BsgId`, which is NULL everywhere
(QDR-8), so 0 of 317 rows resolve and hideout requirements render raw ids without
icons. The hideout tables date from 2025-12-19 (26 `HideoutTraderRequirements`
rows); `HIDEOUT` has 5 trader requirements for 1.1; station ids and normalized
names are identical between `HIDEOUT` and the published table (26 of 26), so
hideout progress keyed by station name survives a refresh.

**Evidence.** `HideoutDataService` (`UpsertHideoutItemRequirementsAsync`'s
`ItemId` source, `RegisterHideoutItemRequirementsSchemaAsync`'s foreign-key
claim), `HideoutDbService` (the join); the QDR-8 join count;
`SELECT MAX(UpdatedAt) FROM HideoutStations` = 2025-12-19;
`jq '[.data[].levels[].traderRequirements[]] | length' HIDEOUT` = 5;
`jq -r '.data[] | "\(.id) \(.normalizedName)"' HIDEOUT` against
`SELECT Id, NormalizedName FROM HideoutStations`.

**Bears on.** R7, the hideout refresh in the same pass, and the content guard that
at least 90 percent of hideout item requirements join.

## Publish, release and tests

### QDR-22: The only fielded build is v2026.7.0, pre-channel, polling the old repository name every five minutes

**Fact.** The latest tag is `v2026.7.0` (`e819471`, 2026-07-24); `update.xml`
points at it. The seasonal profile (`7465d1e`, 2026-08-09) and the data channel
(`a16e477`, 2026-08-16, hardened through `a214a95`) are on main and untagged. The
fielded build polls
`https://raw.githubusercontent.com/josephjang/Tarkov-Item-Helper/refs/heads/main/TarkovHelper/Assets/{db_version.txt,tarkov_data.db}`
(the pre-rename repository name; GitHub redirects and both names answer 200),
every five minutes with an immediate first check, compares the token for
equality, moves the download into place with no hash, size or SQLite check, and
reads no environment variable other than `TARKOVHELPER_CONFIG_PATH` (no switch
disables its data or app-update checks; the app check runs every three minutes).
Its quest tab has `LstQuests` and `TxtDetailStatus` but not the status chips the
current E2E driver waits on.

**Evidence.** `git tag --list 'v*'`; `update.xml`;
`git show v2026.7.0:TarkovHelper/Services/DatabaseUpdateService.cs`
(`VERSION_URL`, `DATABASE_URL`, `UPDATE_INTERVAL_MS = 5 * 60 * 1000`, the
`Change(0, ...)` timer start, the `LocalVersion == remoteVersion` compare, the
`File.Move` swap), `git show v2026.7.0:TarkovHelper/Services/UpdateService.cs`
(`CheckIntervalMinutes = 3`), `git show v2026.7.0:TarkovHelper/Debug/AppEnv.cs`,
`git show v2026.7.0:TarkovHelper/Pages/QuestListPage.xaml` (`CmbStatus`, no
`Chip*`); `curl -sI https://raw.githubusercontent.com/josephjang/Tarkov-Item-Helper/refs/heads/main/TarkovHelper/Assets/db_version.txt`
= 200; `git log --format='%h %ad %s' --date=short 7465d1e -1` and `a16e477 -1`.

**Bears on.** The format-1 decision (a breaking publish would reach every install
in minutes), the data-first release order, the statement that the phase-3 release
also ships phases 1 and 2, and every detail of the legacy smoke (token trick,
running before `update.xml` moves, waiting on `LstQuests`).

### QDR-23: Publishing is a direct push today; CI runs after; drift baseline adoption is manual

**Fact.** `DataPublishService` publishes to the highest `data/v<N>/`, stamps
`user_version`, mirrors to `TarkovHelper/Assets/` while N is 1, writes manifest,
token and index last, never creates a format directory, and suggests token 1.0.11;
the publish window ends with "Commit every copied endpoint file together" and
runs no git. `.github/workflows/ci.yml` runs on pushes to main and on PRs
(`windows-latest`, `Category!=E2E`); main is not branch-protected; every past data
publish was a direct push, and raw main serves a commit before its CI run
finishes. `DataFormatDriftTests` reports Widened for a new column or table and
writes `DataFormatBaseline.v1.proposed.json`, which a human moves over the
committed baseline. `DataChannelMirrorTests` would catch a forgotten mirror, a
stale manifest digest and a missing stamp.

**Evidence.** `DataPublishService.PublishAsync`, `CompareAsync`,
`GetLiveDataFormatVersion`, `ComparisonResult.NewVersion`; `DataPublishWindow`
(`BtnPublish_Click` success text); `.github/workflows/ci.yml`;
`gh api repos/josephjang/TarkovHelper/branches/main/protection` (404, "Branch not
protected"); `git log --format='%h %ad %s' --date=short -- data/ TarkovHelper/Assets/tarkov_data.db TarkovHelper/Assets/db_version.txt`
(no merge commits or PR references before the 2026-08-16 channel work);
`TarkovHelper.Tests/DataFormatDriftTests.cs`, `DataFormatBaseline.cs` (`Ratchet`
outcomes), `DataChannelMirrorTests.cs`.

**Bears on.** The decision to publish through a PR, the baseline adoption in the
publish commit, the token `1.1.0`, and the bump procedure the spec deliberately
does not need (format 1).

### QDR-24: The release skill tags its own bump commit

**Fact.** `/release <version>` pulls main, edits `<Version>` in
`TarkovHelper.csproj`, commits `chore(release): bump version to <version>`, tags
that commit and pushes both; `release.yml` refuses a tag whose commit does not
carry the matching csproj version, builds, tests (non-E2E), packages with
`build/Create-ReleasePackage.ps1` (framework-dependent zip with the seed
database, `Assets/icons`, maps) and creates the GitHub release; `update.xml` is
repointed by hand last. `2026.8.0` is the correct CalVer counter (the only fork
tag is `v2026.7.0`).

**Evidence.** `.agents/skills/release/references/workflow.md` (Preflight and
Publish steps), `.github/workflows/release.yml` ("Verify tag matches project
version"), `build/Create-ReleasePackage.ps1`, `feature-fork-release-process.md`
(the two-step `update.xml` rule), `git tag --list 'v*'`.

**Bears on.** The spec's release wording (the tag is one commit after the PR B
merge, so nothing may merge in between) and the roadmap notes.

### QDR-25: No test guards the published content; the Korean guard is skipped

**Fact.** No test asserts quest count, Kappa count, NULL rates, a named quest,
Collector's row count or icon coverage. `QuestDataCoverageTests` (30 percent
Korean) is `[Fact(Skip = "Requires the regenerated tarkov_data.db (data/korean-quest-names-db branch).")]`;
that branch never existed (the real one was `data/quest-name-localization-db`)
and its 59-percent database has been on main since June. `E2EQuestDataTests`
(in CI) and the E2E fixtures derive rows from the seed by query; the seasonal E2E
fixture needs a quest named exactly "Collector" with a non-quest-item required
item. Among the editor's services only `DataPublishService` and
`TarkovDevDataService.ResolveLocalizedQuestName` have tests; `WikiQuestService`'s
extractors are public static and untested. No legacy-smoke harness exists and no
environment variable substitutes the asset database (`TARKOVHELPER_CONFIG_PATH`
covers user data only; `DatabaseUpdateService.DatabasePath` is base-relative).

**Evidence.** `grep -rn 'FROM Quests\|FROM Items\|FROM Hideout' TarkovHelper.Tests/`
(fixture queries and the skipped test only); `TarkovHelper.Tests/QuestDataCoverageTests.cs`;
`git branch -a --list '*korean*'` (none); `E2EQuestData.cs`,
`SeasonalProfileE2ETests.cs` (the `lower(q.Name)='collector'` fixture);
`grep -rln 'TarkovDBEditor.Services' TarkovHelper.Tests/`
(`DataPublishChannelTests`, `QuestNameMergeTests`); `E2ETestHarness.AppDriver.Launch`;
`TarkovHelper/Debug/AppEnv.cs`.

**Bears on.** `PublishedDataContentTests`, the first pipeline unit tests,
`LegacySmokeE2ETests` and `ProgressCarryOverE2ETests`, and the deletion of the
skipped test in PR B.

### QDR-26: Special:Export and the wiki API block curl's default user agent; Export answered 403 in June

**Fact.** Requests with curl's default user agent receive a Cloudflare "Just a
moment..." challenge page instead of wikitext or JSON; a browser user agent or the
editor's `TarkovDBEditor/1.0` is served. On 2026-06-13 every `Special:Export`
batch returned 403; on 2026-08-21 the same POST returned 200 with either user
agent, and `api.php` with `prop=revisions&rvprop=content&rvslots=main` also
returned page bodies.

**Evidence.** `curl -s 'https://escapefromtarkov.fandom.com/api.php?action=parse&page=Stirrup&prop=wikitext&format=json' | head -c 200`
(challenge HTML) against the same call with `-A 'Mozilla/5.0'` (JSON); the
QDR-17 `Special:Export` probe; the June log named in QDR-9.

**Bears on.** The export-failure guard (a 403 must fail the run, not count) and the
fetch notes in the spec.

## Claims refuted during review

Earlier research output contained these statements; each was disproved before
the documents were finalized and must not be cited as fact.

- "`PRAGMA foreign_keys` is never enabled, so the declared cascades do not fire."
  Disproved by probe: foreign keys are enforced by default (QDR-19).
- "The seasonal pages carry `Must be playing in the [[PvP Season]]`." The captured
  pages carry the `Seasons#...|Seasonal mode` form (QDR-6); the spec accepts both.
- "The Tarkov Shooter - Part 8 was renamed to Part 5." The move-log chain is
  6 -> 5, 7 -> 6, 8 -> 7, confirmed by the API's `wikiLink` for the old Part 8 task.
- "33 removed quests plus 2 ambiguous." By external id the removed set is 35
  (QDR-11); the two Gendarmerie pages keep their old titles on the API.
- "Loyalty level requirements sit on 110 tasks, 105 of which name the giver."
  110 is the count of tasks with any trader requirement; level requirements sit
  on 99 tasks, 94 of which name only the giver (QDR-2, QDR-3).
- "Fetch Wiki Data reads tarkov.dev live." Cache only (QDR-17).
- "The 125 files under `Assets/Icons/` are `.webp` marker icons." They are 115
  item pngs plus 9 webp and 1 svg (QDR-20).
- The trader nickname list once cited from a cached `traders_en` response was a
  Cloudflare block page; the nicknames were re-fetched live (QDR-3, QDR-20).
- "Every imported quest is matched to an API record." The seasonal exception makes
  wiki-only rows possible (QDR-6).
- "The page takes the lowest id when several records share it." Wrong for The
  Tarkov Shooter - Part 5 (QDR-7).

## Open items the research did not settle

- Whether `Historical content` covers all 35 removed pages (5 of 35 sampled do; one
  removed quest's page is uncategorised). The first regeneration's report settles
  it.
- Whether the API's `map` field and the maps endpoint should replace the wiki
  `location` field (the maps endpoint was not examined).
- How much of the parsed wiki text changes after the admin lock lifts on
  2026-09-14 (a re-run and a correction publish, if warranted).
- When the JSON API adds the 18 seasonal quests and New Beginning (Prestige 5)
  and (Prestige 6); each triggers a data-only publish.
