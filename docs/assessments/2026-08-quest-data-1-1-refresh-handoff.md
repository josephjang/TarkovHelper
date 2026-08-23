# Quest Data 1.1 Refresh Handoff

- Handoff date: 2026-08-23
- Phase: 3 of the EFT 1.1 roadmap (quest data refresh)
- PR A (code): <https://github.com/josephjang/TarkovHelper/pull/50>,
  `feat/quest-data-1-1-refresh` at `8f353f9`, base `main` at `a214a95`, CI green, OPEN
- PR B (data): <https://github.com/josephjang/TarkovHelper/pull/53>,
  `data/quest-data-1-1-publish` at `6390d3c`, base `feat/quest-data-1-1-refresh`,
  CI green, OPEN
- Working branch: `data/quest-data-1-1-publish` in the `ondine` worktree, clean
- Documents: `docs/decisions/feature-quest-data-1-1-refresh.{md,spec.md}`,
  evidence in `docs/assessments/2026-08-quest-data-1-1-refresh-grounds.md`

The regeneration has been run and published. The two PRs are stacked and both green.
What is left is the release gate work described under "What remains".

## Read this first

The spec is a good design document and a **partly unreliable expectations document**.
Executing its runbook for real contradicted it in five places, each corrected in the
spec on PR A. Treat its predicted numbers as claims to verify, not as facts:

| Spec said | Reality |
|---|---|
| `Special:Export` answers 200 with the editor's user agent | Cloudflare challenge since 2026-08-23; the crawl uses `api.php` |
| Backfill fills 2648 item IDs | 2644; four snapshot rows have no row here |
| Set the "No Questions Asked" ID by hand in the grid | Code carries it, and the spec's own next paragraph already said so |
| Guards at 5% pass the run | Both refused it; the spec's own expectation of 35 removals exceeded its own bound |
| Tarkov Shooter Part 6 requires Part 5 as `Complete` | The game's record says `Accept` |

Nothing in the pipeline is trustworthy because a guard passed. Four of the five
defects found on this run were invisible to every existing guard.

## State of the data

`data/v1/tarkov_data.db` and the byte-identical `TarkovHelper/Assets/` mirror carry
`db_version` **1.1.0**, digest `742ac3f6...`. `data/index.json` is unchanged, correctly:
the data format is still 1 and this publish is additive.

Verified against the published file, not a fixture:

- 488 quests, Kappa exactly 13, Collector requiring 12 at GroupId 0
- Quest `BsgId` NULL 19/488 (all of them wiki-only seasonal rows, which is the only
  state allowed to have none); item `BsgId` NULL 211/4344
- Hideout item requirements joining to an item: **317/317**, from 0/317
- `QuestTraderRequirements` 107 rows; Traders 16
- Items 4344, every one with an icon, no icon without an item
- `NormalizedName` non-null, unique, and equal to what each row's own key spells

`PublishedDataContentTests` pins all of the above and runs in CI on every build. It
exists because the pipeline's guards are gone by the time anything ships, which is how
seven months of NULL external IDs went unnoticed.

## What remains

**1. The legacy smoke has not been run.** Runbook step 8 and spec section 10. It needs
an extracted v2026.7.0 release zip in `TARKOVHELPER_LEGACY_APP_DIR` and the candidate
database in `TARKOVHELPER_CANDIDATE_DB`:

```powershell
dotnet test --filter "FullyQualifiedName~LegacySmokeE2E"
```

This is a gate the spec puts before publishing, and it was skipped, not passed. Note
that six quest cascade/navigation E2E tests already fail on this desktop for
environmental reasons (see the `e2e-scroll-click-failures` memory), on `main` too, so
do not read those as regressions from this branch.

**2. Merge order matters.** PR A first, then PR B, then `/release 2026.8.0` with
nothing merged in between. The release skill adds its own version bump on top of the
merge commit, so the tag is one commit after PR B and the release build's seed and
icons are PR B's. `update.xml` moves last, and is currently still at `2026.7.0`, as is
`<Version>` in `TarkovHelper.csproj`.

**3. The post-hoc hot-swap confirmation.** After PR B merges and before `update.xml`
moves, launch the extracted v2026.7.0 once against real raw main and confirm its log
shows the download, the `DatabaseUpdated` event and a clean relaunch. The fielded
build's app-update check cannot be disabled, so doing this after `update.xml` moves
puts its dialog over the smoke.

**4. Not automated, and not yet done:** reviewing the full diff report against the
spec's named spot checks (the 92 renames, the 8 reuses, the 15 OR-group quests, the 35
removed names, the held-back and wiki-only lists, the disagreement list). The headline
counts were reviewed; the per-name lists were not read line by line.

## Defects this run found, and where they landed

All five are fixed on PR A. They are listed because the shape of each one says
something about where this pipeline is still weak.

1. **`Special:Export` behind Cloudflare** (`07dede5`). 403 with
   `cf-mitigated: challenge` to every user agent, two days after the grounds doc
   recorded it working. `MediaWikiExportClient` now owns the request through
   `api.php`, which returns the same `export-0.11` document but caps a request at 50
   titles and answers a longer list with a *prefix of itself*. That silent truncation
   is the dangerous mode: pages that never arrive read downstream as quests that no
   longer exist. The cap is enforced, not trusted.

2. **A mis-assigned external ID** (`a449643`). December 2025's matching put the
   CADPAT item's ID on the plain `Army cap` row, so the CADPAT page carried that row's
   key and two items collapsed onto one. An audit of all 2644 backfilled IDs found
   seven rows whose page no longer matches their own; six are ordinary renames, and
   this was the only real error. `BsgIdBackfillService.MisrecordedItemIds` corrects
   both rows and writes only over the value the snapshot recorded.

3. **Two guards that could not pass their own spec** (`2e91b2d`). `MaxLostMatches`
   now counts only the IDs the task file has stopped carrying, which is the failure it
   is named for and which reads 0 here. `MaxLostRowKeys` went 5% to 10% to admit a
   patch that removes 35 quests. **Known cost:** that bound and the backfill guard's
   ID-less tolerance are now both a tenth, so a database sitting on that tolerance can
   lose every one of its ID-less rows without either guard speaking.

4. **Dogtag items missing** (`8f353f9`). The same path asymmetry the spec fixed for
   the trader upsert one method away: `EnsureDogtagItemsExist` ran only on the
   from-cache path, and the runbook uses the full one. **This one shipped into the
   published data before it was caught** and needed a re-run.
   `AssertDogtagRequirementsHaveTheirItems` now runs on both write paths.

5. **The icon path collision** (PR B). The folder on disk was `Icons` while git
   tracked 3933 files under `icons`. On Windows they are one folder, so new icons
   landed under whichever spelling git saw first. Consolidated onto the lower-case
   path the csproj actually packages.

## Accepted losses, already reviewed

Do not re-litigate these without new information; they were decided deliberately and
are recorded in the PR #53 body.

- **38 quests' recorded progress is orphaned**: 35 the patch removed, New Beginning
  (Prestige 5) and (Prestige 6), and the old Tarkov Shooter - Part 5, whose chain was
  renumbered so a player who finished Part 6 keeps that progress under the Part 5
  title.
- **21 hideout trader gates leave**, because tarkov.dev's hideout data no longer
  carries them. The cache has exactly 5. There is no better source.
- **7 items lose their inventory counts** to a rename; the app keys counts by name.
- **Prerequisites 794 rows to 216.** They come from the game's own task records now,
  and 1.1 flattened chains that used to be sequential, Gunsmith most of all. The
  chains the game still enforces (Chemical, Gunsmith Master) are intact.
- **Korean names 288 to 259.** 40 renamed quests await a translation for their new
  title. Self-heals on the next publish.

## Open follow-up

<https://github.com/josephjang/TarkovHelper/issues/52>: the refresh guards do not
generalize past the publish they were tuned for: coverage is by call site rather than
by table (every hideout table is unguarded, which is how a table lost 81% of its rows
in silence), the bounds encode one patch's shape, and raising one can retire another.
Cross-referenced from `RefreshDataService.RefreshGuards` and `HideoutDataService`.

## Environment left behind

The `ondine` worktree's editor build output still holds everything the regeneration
used, so a re-run does not have to re-crawl:

- Working database: `TarkovDBEditor/bin/Release/net8.0-windows/tarkov_data.db`,
  post-refresh
- 1.0.7 snapshot for the backfill, extracted from `ebbc60c`:
  `TarkovDBEditor/bin/Release/net8.0-windows/snapshot-1.0.7.db` (gitignored)
- tarkov.dev and wiki caches under `wiki_data/cache/`, 4344 icons under
  `wiki_data/icons/`, run logs under `logs/`

Two throwaway tools were used and are not committed: a SQLite query CLI and a
read-only harness that runs the real `QuestIdentityResolver` against the caches and
prints what the refresh would decide. Both are trivial to rebuild; the second was what
turned "42 quests lost their game record" from an unexplained number into a list.

The editor holds `TarkovDBEditor.exe` open while running, which makes
`dotnet build -c Release` fail on that project. A `--no-build` test run then silently
tests a stale binary. Close the editor before building, and check that the build
actually succeeded before handing the editor back.
