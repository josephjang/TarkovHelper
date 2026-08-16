# Seasonal Profile Amplified Issues, August 2026

> Snapshot assessment. Analyzed at commit `2f8d389` (2026-08-09). This document
> preserves problems that already existed before PvP Season but become more frequent,
> damaging, or blocking when users regularly switch to and reset a seasonal profile.
> They are deliberately outside `feature-seasonal-profile.md` and
> `feature-seasonal-profile.spec.md`. A later PR addressing one names its SPA ID and
> creates the focused PRD/spec required by the normal decision-doc process.

## Scope boundary

The seasonal-profile feature adds identity, selection, pinning, and compatibility.
It does not claim to repair the findings below. This assessment is the durable handoff
from that scope decision: it keeps evidence and intended follow-up visible without
making inherited data-layer defects completion conditions for the new profile.

These findings are not independent of user value. SPA-1/2 affect isolation under
timing races; SPA-3/4/6 define a future complete-reset feature; SPA-5 prevents a reset
from staying clean. They are deferred, not dismissed.

## Findings index

| ID | Finding | Severity | Suggested follow-up |
| --- | --- | --- | --- |
| SPA-1 | Async writes resolve profile ownership too late | Critical | focused correctness spec |
| SPA-2 | Profile reloads can apply stale state | High | focused reload/snapshot spec |
| SPA-3 | Reset is partial, non-atomic, and races pending writes | Critical | complete-profile-reset PRD/spec |
| SPA-4 | Raid history has no app-profile owner | High when reset ships | pair with SPA-3 |
| SPA-5 | Sync range is ignored and old events are re-imported | High | log-sync-range fix spec |
| SPA-6 | Reset confirmation is incomplete and not localized | Medium | pair with SPA-3 |

## SPA-1: Async writes resolve profile ownership too late

**Existing problem.** Several `QuestProgressService` fire-and-forget paths call an
async helper that reads `ProfileService.ActiveProfileId` only after the mutation has
already changed in-memory state. `SaveProgressBatchAsync`, objective batch helpers,
and multiple single-save/delete paths have this shape. A profile switch before the
continuation reads the singleton redirects the write.

`ItemInventoryService` already demonstrates the safer rule: `_pendingSaves` captures
the profile id when an item becomes dirty.

**Why seasonal amplifies it.** PvP Season makes manual profile switching a primary
workflow rather than an occasional PvP/PvE correction. A delayed seasonal quest write
can land in permanent PvP, the exact data direction the feature exists to avoid.

**Recommended boundary.** Capture an immutable profile id at every mutation entry
point and pass it through persistence. Track/observe background writes; do not read
ambient active state inside an existing operation. Decide separately whether one
write queue is warranted for SPA-3. This overlaps code-health finding THR-6.

**Guard tests.** Pause a queued quest/objective write, switch profiles, resume it, and
assert the original id. Cover save and delete paths, not only one batch helper.

## SPA-2: Profile reloads can apply stale state

**Existing problem.** `QuestProgressService`, `HideoutProgressService`,
`ItemInventoryService`, and `SettingsService` start reloads from
`ActiveProfileChanged`. The async work consults ambient active state and publishes
mutable service caches without a selection generation. A slow earlier request can
finish after a later switch. Rapid A -> B -> A is not protected by checking only the
target id.

The wider shared-state safety problem is also recorded as THR-1 in
`2026-08-code-health.md`.

**Why seasonal amplifies it.** A third button, a manual seasonal pin, and automatic
permanent-profile switching create more transitions and more opportunities for
out-of-order completion. Wrong visible state can then trigger a write under SPA-1.

**Recommended boundary.** Build immutable reload results off to the side. Carry a
monotonic profile-selection revision with the request and publish only if the revision
is still current. Coordinate this with the broader THR-1 snapshot-swap work rather
than adding a one-off UI lock.

**Guard tests.** Delay the first A load, complete B and the second A load, then release
the first load and prove it cannot overwrite the latest cache.

## SPA-3: Reset is partial, non-atomic, and races pending writes

**Existing problem.** `MainWindow.BtnResetProgress_Click` resets quest/objective and
hideout only. Each delete is already scoped to `ProfileService.ActiveProfileId`, so it
is not a global cross-profile reset. Inventory, `ProfileSettings`, and raid history
remain. Existing reset methods perform independent database operations, update memory
before durable success, and catch/log failures without returning them to the UI.

Pending writes are not ordered around deletion. Inventory's 500 ms debounce buffer
can write old quantities after a clear; quest/objective and raid fire-and-forget work
can recreate rows for the same reason.

**Why seasonal amplifies it.** A rolling seasonal profile creates a recurring,
high-expectation reset use case. Expanding the current button to more stores without
ordering and a transaction increases both the probability and blast radius of partial
success.

**Recommended boundary.** Create a separate complete-profile-reset PRD/spec. The PRD
defines what a fresh profile owns and what must survive. Its technical design should
capture an explicit target, order all prior target writes,
delete all owned tables in one SQLite transaction, propagate failure, and update
caches only after commit. Define behavior for reset during an active raid as a product
decision. This overlaps DATA-5, DATA-6, and THR-6.

**Guard tests.** Two-profile database isolation, forced mid-transaction failure and
rollback, pending-write-before-reset ordering, post-reset write survival, and unchanged
global settings.

## SPA-4: Raid history has no app-profile owner

**Existing problem.** `RaidHistory.ProfileId` stores the EFT PMC/SCAV character id
from `EftRaidInfo.ProfileId`; it is not the app's `pvp`/`pve` profile id. `GameMode`
cannot distinguish permanent PvP from PvP Season. Existing rows therefore have no
provable app-profile owner.

Raid-end persistence is also started through `Task.Run`, so consulting active profile
at save time would still be wrong after a manual switch.

**Why seasonal amplifies it.** Raid attribution is dormant while history has no UI
and reset does not touch it. It becomes a blocker when SPA-3 promises to remove only
one seasonal profile's raids.

**Recommended boundary.** Pair this with the complete-reset work: add a nullable
`AppProfileId`, preserve legacy rows as `NULL`, capture ownership when the raid object
is created, and save the snapshot rather than ambient state. A reset deletes only an
exact non-null owner. Do not guess ownership for legacy rows.

**Guard tests.** Fresh/upgrade schema, nullable legacy preservation, all raid creation
fallbacks, switch-before-save attribution, and reset isolation.

## SPA-5: Sync range is ignored and old events are re-imported

**Existing problem.** `SettingsService.SyncDaysRange` persists, and
`LogSyncService.SyncFromLogsAsync` accepts `daysRange`, but
`MainWindow.PerformQuestSync` omits the argument and receives the `0` all-history
default. When a bound is passed, events are filtered only after every matching log
file is parsed.

**Why seasonal amplifies it.** Any future complete seasonal reset can be immediately
repopulated by old quest completions on the next full sync. The setting currently
looks like a protection while providing none on the main path.

**Recommended boundary.** A focused fix first wires the saved value and proves old
events are not applied. File-level skipping is a separate optimization tracked as
SPT-2; do not make that optimization a prerequisite for the correctness fix.

**Guard tests.** Old/boundary/new event fixture, range `0` compatibility, and one
integration or manual UI-path check that the configured argument reaches the service.

## SPA-6: Reset confirmation is incomplete and not localized

**Existing problem.** Reset uses one hardcoded Korean-plus-English `MessageBox` and
lists only quest and hideout progress. It does not name the active profile.

**Why seasonal amplifies it.** A future complete reset is destructive across more
stores and will be used to clear a specific rolling seasonal profile. Ambiguous scope
or profile identity is no longer acceptable.

**Recommended boundary.** Keep this with the complete-reset PRD/spec. The confirmation
names the captured target, enumerates every deleted category, supports EN/KO/JA, and
has distinct decline, failure, and completion behavior.

## Suggested follow-up split

Do not implement all SPA findings in one PR. The natural work boundaries are:

1. Profile mutation ownership and reload publication (SPA-1, SPA-2; coordinate with
   THR-1 and THR-6).
2. Complete atomic profile reset and raid attribution (SPA-3, SPA-4, SPA-6).
3. Honor the existing sync range for event import (SPA-5).

Each future PR names these IDs and creates its focused decision documents. Closing or
rejecting work is recorded in the PR/issue, not by editing this frozen assessment.

## Verification note, appended 2026-08-16

Added on request after a code check at commit `ffa08d1`. The snapshot above is
unchanged; this note records where the code stands now and points at the PRs and
decision documents that are the actual closure records.

All six findings are resolved on `main`:

- **SPA-1**: fixed by the profile data attribution work
  (`fix-profile-data-attribution.md` / `.spec.md`; commits `54a3fbb`, `730bfb9`).
  Writes carry their own partition key, and `QuestProgressService` no longer reads
  `ActiveProfileId` at all. The settings slice landed with
  `fix-profile-settings-race`. `ProfileAttributionSourceTests` locks the rule in
  structurally: no write-path file may read `ProfileService.Instance` outside a
  member-anchored allowlist.
- **SPA-2**: all four named services (quest, hideout, inventory, settings) publish
  reloads through `RevisionGate.Claim` with a monotonic selection revision, the
  boundary recommended above. Settings was the last of the four, closed by
  `fix-profile-settings-race.md` / `.spec.md` (commits `b7abf87`, `167a6dc`).
- **SPA-3**: `ProfileResetService` and `UserDataDbService.ResetProfileAsync` delete
  all owned tables in one SQLite transaction with rollback on failure, take the
  captured target profile as a parameter (the source-scan test forbids the reset
  path from asking which profile is selected), and fence pending sync and
  live-event writes through `TrackedUserDataWrites`. Decision docs:
  `feature-complete-profile-reset.md` / `.spec.md` (commits `34a81c6`, `569daa1`).
- **SPA-4**: `RaidHistory.AppProfileId` exists as a nullable column with a
  migration for older databases. Legacy rows stay `NULL`, and a reset deletes only
  exact non-null owners, so ownership of legacy rows is never guessed.
- **SPA-5**: `MainWindow.PerformQuestSync` now passes
  `SettingsService.SyncDaysRange` to `SyncFromLogsAsync`, whose range parameter is
  required so the next omission is a compile error. Fixed as R8 of
  `fix-profile-data-attribution.md`; guard tests live in `LogSyncAttributionTests`.
- **SPA-6**: the hardcoded MessageBox is gone. `ProfileResetDialog` draws every
  string from `LocalizationService` (EN/KO/JA), names the target profile in both
  the title line and the confirm button, enumerates deleted categories and
  survivors, and has distinct working, failure and completion states.

Still open nearby, outside SPA scope by design: THR-1 (one shared pattern for how
the four caches build their snapshots; each carries its own guard today; low
severity, medium refactoring effort), the profile selector's stale highlight
(rendering and announcement only, excluded by `fix-profile-settings-race.md`;
medium severity, small effort, see DR-10 in the PR #34 handoff note), and the
SPT statuses recorded in the sibling
`2026-08-seasonal-profile-adjacent-issues.md` note.
