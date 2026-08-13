# Complete Profile Reset - Technical Spec

- **Created**: 2026-08-13

> The sibling `feature-complete-profile-reset.md` holds the product decision.
> Write this on the work's branch and merge it in the same PR as the work.
> Nothing is kept current: fields are written once, discoveries are appended. A
> later change that reverses a decision here appends `Superseded by <doc>` below
> this line, in the PR that reverses it.

## Summary

The reset becomes one SQLite transaction: `UserDataDbService.ResetProfileAsync`
deletes every row the target profile owns across six tables and stamps a reset
watermark in the same commit. A per-profile write barrier
(`TrackedUserDataWrites`) drains the fire-and-forget persistence already in
flight before the transaction runs and holds new writes until it commits, so no
pending write can recreate deleted rows. Caches publish their cleared state only
after the commit. `RaidHistory` gains a nullable `AppProfileId` captured when the
raid object is created, which is what makes per-profile raid deletion possible.
The sync and live-event paths consult the watermark and drop log events from
before the reset. The two `MessageBox` calls become one localized dialog with a
confirm state and a result state.

## Goals

The sibling PRD states the user-facing goals. Technically:

- The removal is one transaction. No observer, in the app or in the file, can
  see a partially reset profile.
- No write scheduled before the reset can land after its deletes, and no write
  started during the reset can interleave with them. The guarantee is
  structural (a barrier every persistence helper passes through), not a timing
  argument.
- In-memory state changes only after durable success, reversing the current
  memory-first order.
- Every removal is scoped by an explicit profile id captured before the
  confirmation opens. Nothing in the reset path reads
  `ProfileService.Instance` after that point, with one deliberate exception:
  `SettingsService.HandleProfileReset` asks which profile is selected, because
  its cache is keyed by the selection rather than by a captured id, so the
  selection is the only identity it could compare the reset target against.
  See the settings note under Non-Goals.

## Non-Goals

**The general fire-and-forget audit (THR-6).** Only writes to profile-owned
user-data tables are routed through the new barrier. The other `_ = SomeAsync()`
sites (overlay settings, map state) and the missing
`TaskScheduler.UnobservedTaskException` handler stay as they are.

**SettingsService profile ownership, on both the write and the read side.**
`SettingsService.SaveProfileSetting` resolves
`ProfileService.Instance.ActiveProfileId` at call time, an ambient read on a
write path. It is synchronous and its only triggers are UI handlers, which the
modal reset dialog excludes while the reset runs, so reset ordering does not
depend on it. `GetProfileSetting` resolves the selection the same way, and
`LoadProfileSettings` goes through it once per key, so the profile-settings
cache has no identity of its own: whatever profile is selected when the load
runs is whose values are cached. That is why the reset hook has nothing to
guard against but the selection, and why it compares against
`ProfileService.Instance.ActiveProfileId` rather than a captured field. A
`_loadedProfileId` set in `LoadProfileSettings` would only add a second
identity that can disagree with the values actually in the cache. Rewriting the
`SaveProfileSetting`/`GetProfileSetting` pair to carry an explicit profile id,
which is what would give the cache an identity worth guarding, is SPA-1
territory.

**The wider snapshot work (SPA-2, THR-1).** `HideoutProgressService` keeps its
mutable `_progress`; this change only adds the loaded-profile field the reset
hook needs.

**Raid history UI and retention.** `UserDataDbService.CleanupRaidHistoryAsync`
remains uncalled; `GetRaidHistoryAsync` remains without a consumer.

**Backup or export before reset.** Declined in the PRD.

## Current Behavior / Root Cause

As of current `main`, after `fix-profile-data-attribution.spec.md` merged. The
SPA-3 assessment predates that work, so several of its citations have moved;
this section re-anchors them.

**The button clears three of the six owned tables.** `MainWindow.BtnResetProgress_Click`
shows a hardcoded Korean-plus-English `MessageBox` naming quest and hideout
progress only, then calls `QuestProgressService.Instance.ResetAllProgress()` and
`_hideoutProgressService.ResetAllProgress()`, then shows an unconditional
success box. `ItemInventoryService.ResetAllInventory()` exists but has no
callers anywhere in the solution, so inventory is never reset.
`ProfileSettings` rows are never deleted by any code path
(`UserDataDbService.DeleteProfileSettingAsync` has zero callers), so player
level, scav rep, faction, prestige, and DSP decode count all survive. Raid
history is untouched.

**Each reset is memory-first, independent, and lies on failure.**
`QuestProgressService.ResetAllProgress` publishes the emptied snapshot first,
then blocks on `Task.Run(...).GetAwaiter().GetResult()` running two autocommit
deletes (`ClearAllQuestProgressAsync`, then `ClearAllObjectiveProgressAsync`)
with a catch that logs and swallows. A failure between the two deletes leaves
objectives without their quests; a failure after the snapshot swap leaves the
UI empty while the database keeps every row, and the success box shows
regardless. `HideoutProgressService.ResetAllProgress` has the same shape with
one delete.

**Pending writes are not ordered against deletion.** The attribution change made
every write carry the correct profile id, but nothing orders them against a
reset of that profile:

- `QuestProgressService` schedules fire-and-forget persistence at four sites:
  `_ = SaveProgressBatchAsync(...)` (`QuestProgressService.cs:743`),
  `_ = SaveObjectiveProgressBatchAsync(...)` (`:1775`), and two
  `_ = Task.Run` delete bodies (`:1511`, `:1546`). A batch save scheduled
  before a reset can insert its rows after the reset's deletes.
- `ItemInventoryService` debounces through `_saveTimer` (500 ms,
  `AutoReset = false`); `SavePendingItems` snapshots `_pendingSaves` under
  `_lock`, clears it, and persists inside `Task.Run`, so even a "flush" returns
  before the rows are durable. `ResetAllInventory` neither stops the timer nor
  clears `_pendingSaves`, so a dirty quantity from before a reset would be
  rewritten up to half a second after the clear.
- `EftRaidEventService` saves raid rows with
  `Task.Run(() => SaveRaidHistoryAsync(endedRaid))`
  (`EftRaidEventService.cs:730`, `:972`).
- The sync apply path and `QuestProgressService.ApplyLogEventAsync` are awaited
  by their callers but run as dispatcher async flows, so they interleave freely
  with a reset started from the button handler.

**Raid rows cannot be deleted per profile.** `RaidHistory.ProfileId` stores the
EFT character id (`EftRaidInfo.ProfileId`), and `GameMode` cannot distinguish
PvP Zone from PvP Season, so no existing column proves app-profile ownership.
Raids are created at three sites in `EftRaidEventService`
(`EftRaidEventService.cs:835` from matching, `:872` from the scene-preset
fallback, `:927` from the transit fallback); none records the app profile,
although the service already tracks `_currentSessionProfileHint` from the same
log stream.

**The next sync restores what a reset removed.** The game retains session folders
for roughly three days (measured for the attribution work).
`LogSyncService.SyncFromLogsAsync` attributes their events to the profile that
produced them and knows nothing about a reset, so a reset seasonal profile is
repopulated by its own history on the next sync. `SettingsService.SyncDaysRange`
narrows the window but its default is 0, all history.

## Design

### One transaction in the store

`UserDataDbService.ResetProfileAsync(string profileId, DateTime resetAt,
IReadOnlyCollection<string> preservedSettingKeys)` opens one connection and one
transaction:

1. `DELETE FROM QuestProgress WHERE ProfileId = @p` (and the same for
   `ObjectiveProgress`, `HideoutProgress`, `ItemInventory`).
2. `DELETE FROM ProfileSettings WHERE ProfileId = @p AND Key NOT IN
   (preservedSettingKeys)`.
3. `DELETE FROM RaidHistory WHERE AppProfileId = @p`. `NULL` never matches, so
   legacy rows survive by construction (PRD R9).
4. `INSERT` the watermark row (`ProfileSettings`, key
   `UserDataDbService.ProgressResetAtKey = "app.progressResetAt"`, value
   `resetAt` in ISO-8601), `ON CONFLICT` update.
5. Commit. Exceptions propagate to the caller; this method has no
   catch-and-log.

The watermark is written inside the same transaction, after the settings
delete, so the fence and the removal commit atomically and the insert is not
swept by its own delete.

`preservedSettingKeys` is `SettingsService.ProfileKeysSurvivingReset`, a new
internal list holding `app.hasEodEdition` and `app.hasUnheardEdition`, declared
next to the existing `ProfileSpecificKeys` array so a future profile key is
added in sight of the question "does this survive a reset?". Deletion is the
default: a key not on the survivor list is wiped, which is the safe direction
for progress-shaped data.

### Raid ownership (SPA-4)

`EftRaidInfo` gains `string? AppProfileId`, documented as null meaning "no
evidence", never a default. Each of the three raid creation sites sets it from
the session evidence current at creation: `_currentSessionProfileHint` mapped
through `ProfileService.TryResolveDetectedProfile` and
`ProfileService.GetProfileId`; an unknown or absent hint yields null. The value
is captured into the raid object at creation, so a profile switch between raid
start and the save cannot re-own the row, and the ambient selection is never
consulted (the source-scan test forbids it on write paths anyway).

The schema migration follows the `MigrateToProfileSchemaAsync` pattern: an
idempotent `pragma_table_info('RaidHistory')` check for `AppProfileId`, then
`ALTER TABLE RaidHistory ADD COLUMN AppProfileId TEXT NULL`, run from the
`CreateTablesAsync` path. Legacy rows keep `NULL`. `SaveRaidHistoryAsync`
includes the column in its `INSERT`.

### The write barrier

A new `TrackedUserDataWrites` (static, `Services/`):

- `Task Run(string profileId, Func<Task> op)`: if a reset barrier for
  `profileId` is up, first await its completion; then start `op`, register the
  task under the profile id, log any failure with the full exception, and
  unregister on completion. Returns the task so awaited call sites keep their
  shape.
- `Task<IAsyncDisposable> BeginResetAsync(string profileId)`: raises the
  barrier for the profile, awaits every registered task for it (the drain),
  and returns a handle whose disposal lowers the barrier.

The barrier is acquired inside the persistence helpers, not at their call
sites: `QuestProgressService`'s batch-save and delete helpers, the body of
`ApplyLogEventAsync`, `ItemInventoryService.SavePendingItemsAsync` (per entry,
with that entry's captured profile id, and the entry is claimed out of
`_pendingSaves` from inside its tracked write rather than snapshotted and
cleared up front, so an entry is either claimed before the barrier rises or
discarded by the reset), `HideoutProgressService`'s save helper, and
`EftRaidEventService`'s raid save (when `AppProfileId` is non-null; an unowned
raid row cannot conflict with any reset, so it is tracked for failure logging
only). A future caller of any helper is ordered automatically; there is no
call-site discipline to erode. The helpers' existing ad hoc try/catch wrappers
collapse into `Run`'s failure logging.

### Orchestration

A new `ProfileResetService` (singleton, matching the service pattern) owns the
sequence. `MainWindow` calls
`ProfileResetService.Instance.ResetAsync(AppProfile target)` and renders the
result; it no longer touches the progress services directly.

1. The target is the `AppProfile` captured from
   `ProfileService.Instance.CurrentTransition` when the dialog opened, turned
   into a profile id once. An automatic switch while the dialog is open does
   not move the target (PRD R1).
2. `resetAt = DateTime.Now`, local time, matching the log-timestamp convention
   recorded in `fix-profile-data-attribution.spec.md`.
3. `await TrackedUserDataWrites.BeginResetAsync(profileId)`: barrier up, in-
   flight writes drained.
4. `ItemInventoryService.DiscardPendingSaves(profileId)`: removes
   `_pendingSaves` entries whose captured profile id is the target. They
   describe quantities the transaction is about to delete; flushing them first
   would write rows only to remove them. Entries captured for other profiles
   stay. A flush already under way cannot put a discarded entry back:
   `SavePendingItemsAsync` claims each entry from inside that entry's own
   tracked write, after the write has passed the barrier, so an entry removed
   here is gone by the time the flush reaches it and that write finds nothing
   to do.
5. `await UserDataDbService.Instance.ResetProfileAsync(...)`, under a bounded
   wait (`ProfileResetService.StoreTimeout`): the caller is a modal that
   refuses to close while the reset runs, so waiting forever on a wedged
   connection would leave a window nothing can dismiss. On failure the barrier
   is lowered and the failure returned; no cache was touched, so the app still
   shows the surviving data (PRD R5). A wait that runs out of budget is its own
   outcome, not a failure: abandoning a wait does not cancel the transaction,
   so that one path cannot repeat R5's "nothing was removed" and says the
   outcome is unknown instead.
6. After the commit, each service applies its in-memory consequence through a
   new `HandleProfileReset(string profileId)` hook that no-ops when its loaded
   state belongs to a different profile: `QuestProgressService` publishes an
   empty snapshot through the existing CAS path only when
   `Snapshot.ProfileId` matches; `HideoutProgressService` and
   `ItemInventoryService` swap in empty state guarded by a loaded-profile
   field (`HideoutProgressService` gains one next to `_latestRevision`;
   the quest snapshot already carries its own); `SettingsService` re-runs its
   profile-settings load and re-raises its changed events, since its cached
   level, faction, and the rest are now stale. Each service raises its usual
   change event so pages refresh through the existing subscriptions.
7. Barrier down. Success returned.

Every exit returns a `ProfileResetOutcome`, a record with a private constructor
and three factories (`Succeeded()`, `Failed(message)`, `Abandoned()`) mapping
one to one onto `ProfileResetStatus`. The states that would render nonsense (a
success carrying a failure message, a failure carrying none) are therefore not
constructible, and the dialog switches on the status rather than re-deriving it
from two independent fields.

The whole flow is async end to end. A blocking wait on the dispatcher would
deadlock against tracked writes whose continuations return to it, which is also
why the current `GetAwaiter().GetResult()` reset shape cannot simply be
extended.

### The sync and live fences

`IQuestProgressStore` gains `Task<DateTime?> GetProgressResetAtAsync(string
profileId)`, implemented by `UserDataDbService` as a read of the watermark row.

- **Sync**: `LogSyncService.SyncFromLogsAsync` already groups attributed events
  by owner and reads each owner's stored rows before planning; it now also
  reads the owner's watermark and drops events whose `Timestamp` is not after
  it, counting them into a new `SyncResult` count that `SyncResultDialog`
  shows with a localized label.
- **Live**: `QuestProgressService.ApplyLogEventAsync` gains the event's
  timestamp as a parameter (`MainWindow.OnQuestEventDetected` passes it from
  the detected event). Inside the tracked write it consults the owner's
  watermark and ignores a stale event entirely: no row, no snapshot change.
  Running the check inside the barrier is what makes check-then-write atomic
  against a concurrent reset; checked outside, an event could pass a stale
  watermark read and write after the new one committed.

The boundary is "not after": an event stamped exactly at the reset moment is
dropped. Hand entry never passes through either fence.

### The dialog

A new `ProfileResetDialog` (`Windows/`) replaces both `MessageBox` calls: one
window with a confirm state (localized profile label via the existing
`LocalizationService.ProfileName(AppProfile)`, the enumerated category list, a
warning line when `EftRaidEventService.Instance.CurrentRaid?.State` is
`Matching`, `Connecting`, or `InRaid`) and a result state, whose headline
follows the outcome's status: success text, failure text naming that nothing
was removed, or, for an abandoned wait, text saying the outcome is unknown and
asking the player to restart and check the profile. A failure also renders the
underlying message ("database is locked") as a detail line below the headline;
the other two statuses have no detail to render. Buttons and the window title
come from `LocalizationService`, and every interactive element carries an
`AutomationId`, because the e2e harness drives owned WPF windows by title and
`InvokePattern` but cannot drive a native `MessageBox` at all
(`AppDriver.WaitForAppWindow`). The Danger Zone strings in the settings drawer
(`TxtDangerZoneLabel`, `TxtResetProgressDesc`, `BtnResetProgress`) move to
`LocalizationService` and describe the complete reset.

### Deletions and ripple

- `QuestProgressService.ResetAllProgress`,
  `HideoutProgressService.ResetAllProgress`, and
  `ItemInventoryService.ResetAllInventory` are deleted, replaced by the
  orchestrated flow plus per-service `HandleProfileReset` hooks.
- `ClearAllQuestProgressAsync` and `ClearAllObjectiveProgressAsync` lose their
  only production callers and are removed from `IQuestProgressStore`, the
  `UserDataDbService` implementation, and `ProgressStoreFake`;
  `ClearAllHideoutProgressAsync` and `ClearAllItemInventoryAsync` are removed
  from `UserDataDbService` the same way. The transactional reset does not go
  through per-table methods, and the attribution spec set the precedent of
  deleting unused store surface rather than keeping it plausible.
- `ProfileAttributionSourceTests`' allowlist entries naming
  `HideoutProgressService.ResetAllProgress` and
  `ItemInventoryService.ResetAllInventory` as permitted
  `ProfileService.Instance` readers are removed with the methods. The new
  reset path adds no ambient reads, so the allowlist shrinks rather than
  grows.
- `UserDataDbService` gains an internal constructor taking a database path, so
  the transactional behavior is testable against a real temp-file SQLite
  database. This mirrors the `IQuestProgressStore` seam precedent; the
  singleton path is unchanged.
- `ResetProfileAsync` gains an internal test-only hook invoked between the
  deletes and the commit, so the rollback guarantee is provable rather than
  asserted.

### Files

- `TarkovHelper/Models/EftRaidEvent.cs` (`EftRaidInfo.AppProfileId`)
- `TarkovHelper/Models/QuestLogEvent.cs` (`SyncResult` pre-reset skipped count)
- `TarkovHelper/Services/UserDataDbService.cs` (`ResetProfileAsync`, raid
  column migration and insert, `GetProgressResetAtAsync`, internal ctor,
  removed clears)
- `TarkovHelper/Services/IQuestProgressStore.cs` (add watermark read, remove
  clears)
- `TarkovHelper/Services/TrackedUserDataWrites.cs` (new)
- `TarkovHelper/Services/ProfileResetService.cs` (new)
- `TarkovHelper/Services/QuestProgressService.cs` (tracked helpers,
  `HandleProfileReset`, `ApplyLogEventAsync` timestamp and fence, delete
  `ResetAllProgress`)
- `TarkovHelper/Services/HideoutProgressService.cs` (tracked helper,
  loaded-profile field, `HandleProfileReset`, delete `ResetAllProgress`)
- `TarkovHelper/Services/ItemInventoryService.cs` (tracked saves,
  `DiscardPendingSaves`, `HandleProfileReset`, delete `ResetAllInventory`)
- `TarkovHelper/Services/SettingsService.cs` (`ProfileKeysSurvivingReset`,
  reset reload hook)
- `TarkovHelper/Services/EftRaidEventService.cs` (capture `AppProfileId` at
  the three creation sites, tracked raid saves)
- `TarkovHelper/Services/LogSyncService.cs` (sync fence, skipped count)
- `TarkovHelper/Services/LocalizationService.Header.cs` (dialog and Danger
  Zone strings in English, Korean, and Japanese; the Danger Zone strings
  already lived here) and `LocalizationService.Quest.cs` (the sync summary's
  skipped-count line)
- `TarkovHelper/Windows/ProfileResetDialog.xaml`, `.xaml.cs` (new)
- `TarkovHelper/Windows/SyncResultDialog.xaml`, `.xaml.cs` (skipped count row)
- `TarkovHelper/MainWindow.xaml`, `.xaml.cs` (rewire the button, localized
  Danger Zone, pass the live timestamp)
- `TarkovHelper.Tests`: new `ProfileResetStoreTests`,
  `TrackedUserDataWritesTests`, `ProfileResetHooksTests`,
  `ProfileResetOrchestrationTests`,
  `ProfileResetE2ETests`; updates to `ProgressStoreFake`,
  `ProgressStoreFakeTests`, `ProgressSnapshotTests`,
  `ProfileAttributionSourceTests`, `LogSyncAttributionTests` (sync fence),
  `LocalizationHeaderStringsTests`, `SyncSummaryStringsTests`,
  `E2ETestHarness` (profile-scoped seeding helpers)

## Technical Decisions

### One SQL transaction, not ordered per-table deletes

Ordered deletes with a compensating cleanup were considered and rejected: every
failure mode needs its own recovery story, and the database already offers
all-or-nothing for free. This is DATA-5's recommendation applied to the one
path where partial success is most damaging. The per-table `ClearAll*` methods
are removed rather than kept alongside, so there is no second, non-atomic way
to reset.

### A barrier plus drain, not a global write queue

SPA-3 suggested deciding whether one write queue is warranted. A queue
serializing all user-data writes at all times was rejected: it taxes every
write forever to solve an ordering problem that exists only around reset. The
barrier costs nothing outside a reset, and because it lives inside the
persistence helpers it is mechanically unavoidable, which is the property the
queue was meant to buy. The attribution change made this insertable without
touching call sites by giving every helper an explicit profile id, and this is
that insertion.

### The watermark lives in ProfileSettings, inside the transaction

A separate table and a `UserSettings` key were both considered. `ProfileSettings`
already is the per-profile key-value store, the row travels with the profile's
lifecycle, and writing it inside the reset transaction makes fence-and-removal
atomic: there is no window where the data is gone but the fence is down.
Deleting settings and inserting the watermark in the same transaction also
means a second reset simply overwrites the previous watermark.

### Editions survive via a named survivor list, wiped-by-default for the rest

The PRD decides editions survive. Technically the survivor list is an
allowlist next to `ProfileSpecificKeys`, and the delete excludes only listed
keys, so an unclassified future key is wiped by default. The opposite default
(survive unless listed) was rejected because forgetting to classify a
progress-shaped key would then leak it through every reset. A test pins the
survivor list to a subset of `ProfileSpecificKeys`.

### resetAt is local time, and the DST fold is accepted

Log-derived event timestamps are local (`fix-profile-data-attribution.spec.md`
records why), so the fence compares in local time. During the yearly
fall-back hour an event can be judged on the wrong side of a reset that
happened inside that hour. Converting to UTC would fix nothing, because the
log lines being compared carry no offset. Same exposure, same acceptance, as
attribution.

### The fence checks inside the barrier

`ApplyLogEventAsync` reads the watermark inside its tracked write. Checking
before entering would allow: read stale watermark, reset commits a newer one,
write lands anyway. Inside the barrier the read and the write are on the same
side of any reset. The sync path gets the same property from its per-owner
grouping: the barrier orders each owner group's plan-and-apply against a reset
of that owner.

### Capture at raid creation, from session evidence

SPA-4's recommendation, unchanged: the raid row's owner is decided by the
session that produced the raid, at the moment the raid object first exists.
Consulting the selection at save time was rejected because raid saves run in
`Task.Run` after the raid ended, when the user may have switched profiles, and
because the selection is not evidence of where the raid happened. An unknown
hint stays null; guessing was rejected in the PRD.

### A test seam over a singleton workaround

`UserDataDbService` has a private constructor and no in-process tests today;
the e2e harness reaches it only through a launched app. The internal
path-taking constructor is a deliberate, minimal seam so the transaction,
migration, and rollback behavior get real SQLite tests, following the
precedent of the `Store` property seam on `QuestProgressService`. Reflection
construction (`TestReflection.Uninitialized`) was rejected here because the
service's lazy init would have to be replicated field by field.

## Test Strategy

- **Unit, store (real SQLite via the internal ctor, temp file per test,
  `SqliteConnection.ClearAllPools()` on cleanup):**
  - Two-profile isolation: seed both profiles across all six tables, reset
    one, assert the other's rows byte-for-byte intact, including
    `ProfileSettings` and `RaidHistory`.
  - Survivors: edition keys and the watermark exist after reset; every other
    profile-settings row for the target is gone; `UserSettings` untouched.
  - Rollback: the test hook throws between deletes and commit; assert every
    table unchanged, no watermark written.
  - Raid scoping: reset deletes only exact `AppProfileId` matches; `NULL`
    rows and other profiles' rows survive.
  - Migration: a fresh database has the column; a pre-upgrade database gains
    it with existing rows preserved as `NULL`; running the migration twice is
    idempotent.
  - Watermark round-trip: `GetProgressResetAtAsync` returns the stored
    moment; a never-reset profile returns null.
- **Unit, barrier:**
  - A write started before `BeginResetAsync` completes before the drain
    returns.
  - A write attempted while the barrier is up lands only after it drops.
  - A failing tracked write is logged, not thrown, and does not wedge the
    barrier.
- **Unit, orchestration (`ProfileResetOrchestrationTests`):**
  - One throwing refresh hook costs only its own hook; the later services
    still refresh, and nothing escapes to turn a committed reset into a
    reported failure.
  - A store call that never returns becomes an abandoned outcome inside the
    budget instead of hanging the modal; a throwing one keeps its own message,
    including when it throws before returning a task.
  - The outcome factories: no failure without a detail, no success with one.
  - The result headline per status, in particular that the abandoned one never
    repeats "nothing was removed".
  - The raid warning fires only while the watcher is running, so a raid state
    left over from a stopped watcher does not cry wolf.
- **Unit, services (via `ProgressServiceHarness` and `ProgressStoreFake`):**
  - `HandleProfileReset` publishes an empty snapshot only when
    `Snapshot.ProfileId` matches the target; another profile's loaded
    snapshot is untouched.
  - `ApplyLogEventAsync` with a timestamp not after the watermark writes
    nothing and leaves the snapshot alone; a later timestamp applies
    normally; the boundary-equal case is dropped.
  - Inventory: a pending save for the target is discarded, one for another
    profile survives the reset and persists afterwards.
  - Post-reset survival: a quest completed after the reset persists (PRD R7).
- **Unit, sync:** a fixture with events straddling the watermark applies only
  the newer ones and reports the skipped count.
- **Unit, raid capture:** a raid created under a season hint saves
  `AppProfileId = "season"` even when the selection changes before the save;
  an unknown hint saves null.
- **Unit, classification:** `ProfileKeysSurvivingReset` is a subset of
  `ProfileSpecificKeys`.
- **E2E** (`ProfileResetE2ETests`, driven by `AutomationId` through
  `AppDriver`): seed two profiles with `E2EDb`, reset the active one through
  the dialog, assert the result state appears, the quest list is empty, the
  other profile's rows survive in the database file, and the edition setting
  is retained. A decline path asserts nothing changed. The dialog replaces
  `MessageBox` precisely so this test can exist.

The pending-write ordering tests are written first against a harness without
the barrier to confirm they reproduce the resurrection (the fake's `SaveGate`
holds a batch save while the deletes run), then pass with it.

## Verification

- `dotnet build TarkovHelper.sln`
- `dotnet test TarkovHelper.sln -c Release --filter "Category!=E2E"` (CI shape)
- `dotnet test TarkovHelper.sln -c Release --filter "Category=E2E"` on an
  interactive desktop
- Manual, via `dotnet TarkovHelper/bin/Debug/net8.0-windows/TarkovHelper.dll`:
  set level/faction/items on the seasonal profile, reset it in each of
  English, Korean, and Japanese, confirm the enumerated dialog and fresh
  state, then run a log sync and
  confirm the summary reports skipped pre-reset events and no progress
  returns.

## Risks & Migration

- **Schema change is additive and nullable.** Older builds' `INSERT INTO
  RaidHistory` lists explicit columns, so a downgrade keeps working against
  the migrated file; rows an older build writes have `NULL` `AppProfileId`
  and are treated as legacy (never deleted). No data rewrite, no rollback
  step.
- **The fence covers log-derived data only.** Rows already misattributed
  before the attribution change are untouched by reset unless they sit under
  the target profile; the repair question stays where
  `fix-profile-data-attribution.md` left it.
- **Watermark trust.** The fence compares local timestamps; the DST fold hour
  is the accepted ambiguity. A machine-clock jump backwards past a reset
  would also mute the fence; not defended, same as every other timestamp in
  the log pipeline.
- **If EFT renames the session-mode token**, new raids fall back to a null
  owner: reset stops removing new raid rows until the pattern is updated,
  which fails toward preserving data rather than deleting the wrong rows.
- **Delete volume is small** (hundreds of rows per profile), so the single
  transaction holds its write lock for milliseconds; no vacuum or checkpoint
  handling is needed.
