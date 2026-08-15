# Profile Settings Race - Technical Spec

- **Created**: 2026-08-15

> The sibling `fix-profile-settings-race.md`, if it exists, holds the product
> decision; a spec with no PRD is the normal shape for an internal change. Write
> this on the work's branch and merge it in the same PR as the work. Nothing is
> kept current: fields are written once, discoveries are appended. A later change
> that reverses a decision here appends `Superseded by <doc>` below this line, in
> the PR that reverses it. If this spec records a decision whose implementation is
> deliberately deferred, say so explicitly - a merged spec is otherwise read as
> shipped.

## Summary

`SettingsService` is the last `ActiveProfileChanged` subscriber without the
transition guard the other three data services grew: it reloads its eight
profile-scoped values through eight separate ambient-selection reads with no
revision, no cache identity and no lock, so a transition landing mid-reload
tears the cache across two profiles, and two transitions in flight can leave
the older one's values published last. The fix is the shape
`QuestProgressService` already proved: an immutable snapshot that carries its
profile id and transition revision, built off to the side from one bulk query
and published by reference swap only while its revision is still the latest.
Because writes then take their profile from the snapshot they were derived
from, the deferred write-attribution gap for settings (the `SettingsService`
slice of SPA-1) closes in the same move. This addresses SPA-2 of
`2026-08-seasonal-profile-amplified-issues.md`.

## Non-Goals

**Converting `HideoutProgressService` and `ItemInventoryService` to snapshots
(THR-1).** Both keep their lock-plus-`_loadedProfileId` shape. This change
touches `SettingsService` and the store method it needs; the future
unification of the four caches is recorded under Technical Decisions but not
started.

**Extracting the shared revision gate.** `ClaimRevision` exists identically in
three services and this change adds a fourth copy on purpose; see Technical
Decisions.

**`MainWindow.OnActiveProfileChanged`.** The header repaint has no revision
guard and can briefly render the losing transition. It holds no data, a
re-confirmation repaints it, and a previous attempt was intentionally kept
separate from SPA-2 scope. Unchanged here.

**Global (non-profile) settings.** `SaveSetting`/`GetValue` and the
`UserSettings` table are per-install, not per-profile, and are untouched.

## Current Behavior / Root Cause

Verified on branch `fix-spa-2` at commit `6de3d7e`.

### The publisher, and the guard three subscribers already carry

`ProfileService` guards the selection and a monotonic transition revision as
one unit under a static gate; `CurrentTransition` returns the pair atomically
and `ProfileChangedEventArgs` carries `Profile`, `ProfileChanged` and
`Revision`. The revision counts every raise, including provenance-only
re-confirmations, so it never repeats across two loads.
`ActiveProfileChanged` is deliberately raised outside the lock, from two kinds
of thread (the dispatcher on a click, the EFT log watcher's thread pool), so
two near-simultaneous transitions can deliver their events to a given
subscriber in either order.

The event has five subscribers: `QuestProgressService`,
`HideoutProgressService`, `ItemInventoryService`, `SettingsService`, and
`MainWindow`. The first three share one guard shape: the handler passes the
event's own `Profile` and `Revision` into the reload, `ClaimRevision` lifts
the latest-known revision, the DB read happens off to the side, the revision
is re-checked after the read, and a stale result is discarded instead of
published. Publication is atomic per service: `QuestProgressService` swaps an
immutable `ProgressSnapshot` (which embeds `ProfileId` and `Revision`); the
other two swap their mutable cache together with a `_loadedProfileId` string
under a lock. `ProfileReloadRaceTests` and `ProgressSnapshotTests` pin the
guard.

### SettingsService is the unguarded fourth

`SettingsService.OnActiveProfileChanged` discards both `e.Profile` and
`e.Revision` and calls `ReloadProfileSettingsAndNotify`, which runs
`LoadProfileSettings`: eight independent `GetProfileSetting` calls, one per
key, each re-reading `ProfileService.Instance.ActiveProfileId` at call time.
`UserDataDbService.GetProfileSetting` opens a fresh SQLite connection per
call, so one reload is eight sequential connection open/close cycles and the
window is not narrow relative to a transition. There is no revision, no field
naming which profile the cache holds, and no lock over the eight cache
fields.

Two distinct failure shapes follow:

- **Tear.** A transition between read k and read k+1 leaves keys 1..k holding
  profile A's values and keys k+1..8 holding profile B's. Nothing can detect
  the state, and it persists until the next transition.
- **Inversion.** Even if each reload were atomic, the events are raised
  outside the publisher's lock, so the handler for the older transition can
  run last and publish last. Without a revision the cache then holds the
  older profile's complete set under the newer selection.

### The write path has the same shape

`SaveProfileSetting` also resolves `ProfileService.Instance.ActiveProfileId`
at call time, so an edit races a switch the same way.
`ProfileAttributionSourceTests` allowlists exactly four selection reads in
`SettingsService` (the constructor subscription, `HandleProfileReset`,
`SaveProfileSetting`, `GetProfileSetting`) and its own failure message states
the doctrine this change adopts: the partition must arrive as a parameter or
on the snapshot the change was derived from. The Non-Goals of
`feature-complete-profile-reset.spec.md` recorded this gap explicitly and
deferred it as SPA-1 territory; this change is that deferred work, for
settings only.

### The reset hook leans on the missing identity

`SettingsService.HandleProfileReset` compares the reset target against the
ambient selection, unlike the other three hooks, which compare against their
captured loaded-profile identity. Its own doc comment gives the reason: every
profile-scoped read resolves the selection at call time, so the selection IS
the cache's identity and there is nothing else to guard. That premise is what
this change removes; once the cache carries its own profile id, the hook
compares against it like its three siblings. The hooks run as synchronous
`Action`s from `ProfileResetService.RunRefreshHooks`, strictly after the
store transaction commits, so the hook must remain synchronous and the cache
must be current when it returns. `ProfileResetHooksTests` pins the hook's
behavior, seeding the private cache fields by reflection.

### A bulk loader already exists, unused

`UserDataDbService.LoadProfileSettingsAsync(profileId)` reads all of one
profile's settings in a single query and has no callers.

## Design

### The snapshot

A new immutable type, `ProfileSettingsSnapshot`, mirroring `ProgressSnapshot`
in miniature: `ProfileId`, `Revision`, and the eight profile-scoped values as
nullables (`PlayerLevel`, `ScavRep`, `ShowLevelLockedQuests`,
`DspDecodeCount`, `PlayerFaction`, `HasEodEdition`, `HasUnheardEdition`,
`PrestigeLevel`). The eight nullable cache fields in `SettingsService` are
replaced by one volatile snapshot reference; property getters read the
current snapshot once and apply today's defaults for null. The public API of
`SettingsService` (the eight properties and seven changed events) does not
change, so no page or consumer is edited.

### Loading

`UserDataDbService` gains a synchronous `LoadProfileSettings(string
profileId)` returning the key/value dictionary from one query, following the
same fully-synchronous ADO shape as `GetProfileSetting`; the uncalled
`LoadProfileSettingsAsync` is folded into it rather than left as a second
copy. `SettingsService` parses the dictionary into a snapshot with exactly
today's per-key parsing rules (`TryParse` failure leaves the field null).

### The transition reload

`OnActiveProfileChanged` keeps the event's `Profile` and `Revision`. Like
`QuestProgressService.OnActiveProfileChanged`, a provenance-only
re-confirmation is skipped unless the snapshot needs healing: reload when
`e.ProfileChanged` is true, when the last load failed, or when the snapshot's
`ProfileId` differs from the event's profile.

The reload itself: `ClaimRevision(e.Revision)` (a fourth copy of the CAS
loop), one bulk read for `ProfileService.GetProfileId(e.Profile)`, then a
re-check that the claimed revision is still the latest; a stale result is
discarded without publishing or raising events. A current result is published
by swapping the snapshot reference and then raising all seven changed events
in today's order. The reload runs synchronously on the handler's thread, as
today; subscribers already marshal to the dispatcher.

If the bulk read throws, the reload publishes a snapshot of the new profile
with all values null (so getters answer defaults) and remembers the failure
in a `_lastLoadFailed` flag, exactly as the three sibling services publish
empty on failure: an unreadable store must not leave the previous profile's
values on screen under the new profile's name. The next re-confirmation heals
it via the skip conditions above.

### Startup and lazy load

`LoadSettings` (constructor and lazy getter path) reads
`ProfileService.Instance.CurrentTransition` for the atomic profile/revision
pair, mirroring the allowlisted startup reads in `HideoutProgressService`
and `ItemInventoryService`, and builds the initial snapshot from it. This is
the one load with no event to learn the transition from.

### Writes

Setters derive an updated snapshot from the current one and publish it with a
compare-and-swap retry in the shape of `QuestProgressService.Mutate`: the
update is re-run if another publisher won, and re-application stops if the
current snapshot's `ProfileId` no longer matches the profile the edit was
derived from (the persisted write below still stands; only the in-memory
graft is dropped, and the intervening reload already read the store).
Persistence calls `SetProfileSetting(snapshot.ProfileId, key, value)` with
the profile id of the snapshot the edit was derived from, never the ambient
selection. The changed event for the property is raised when the publish
succeeds, as today.

### The reset hook

`HandleProfileReset(profileId)` compares the target against the current
snapshot's `ProfileId` instead of the ambient selection, making it the fourth
hook with a captured identity. On a match it synchronously bulk-reads the
target's post-reset rows, publishes (guarded so that a transition that
intervened mid-hook wins; its own reload already read post-reset rows), and
re-raises the seven events in the pinned order. On a mismatch it does
nothing, as today.

### Enforcement updates

`ProfileAttributionSourceTests`' allowlist for `SettingsService` changes
shape: the `HandleProfileReset`, `SaveProfileSetting` and `GetProfileSetting`
entries are removed (the test fails on leftover entries, so this is forced),
and the startup `CurrentTransition` read is added alongside the constructor
subscription. `ProfileResetHooksTests` reseeds through the snapshot instead
of the eight private fields; its assertions (defaults after reset, editions
surviving, exact seven-event order, unselected-target no-op) hold unchanged.

### Files

- `TarkovHelper/Services/SettingsService.cs` - snapshot field, reload guard,
  snapshot-attributed writes, reset hook comparison
- `TarkovHelper/Services/Settings/ProfileSettingsSnapshot.cs` - new immutable
  snapshot type
- `TarkovHelper/Services/UserDataDbService.cs` - synchronous bulk
  `LoadProfileSettings`, replacing the uncalled async variant
- `TarkovHelper.Tests/ProfileAttributionSourceTests.cs` - allowlist reshaped
- `TarkovHelper.Tests/ProfileResetHooksTests.cs` - snapshot seeding
- `TarkovHelper.Tests/SettingsReloadRaceTests.cs` - new, mirroring
  `ProfileReloadRaceTests`

## Technical Decisions

### Snapshot plus revision gate, not a lock, a bulk load alone, or a new service

Four alternatives were considered. A lock around the cache plus a captured
`_loadedProfileId` (the `HideoutProgressService` shape) is a smaller diff but
adds a per-read lock discipline to a service whose events are raised
synchronously into arbitrary subscribers; this codebase has already
documented how that discipline erodes (THR-2 in `2026-08-code-health.md`).
Replacing the eight reads with one bulk query but no revision removes the
tear yet leaves the inversion: the older transition's handler can still
publish last and park the wrong profile's complete set under the new
selection. Extracting the eight values into a dedicated profile-settings
service raises cohesion but moves every consumer and test for no correctness
gain. The snapshot shape wins because it is the sharpest guard already proven
in this repo (`ProgressSnapshot`, `ProfileReloadRaceTests`), it changes no
public API, and it is the state model the future cache unification is
expected to standardize on, so this change is absorbed there as a pure move.

### Writes take their profile from the snapshot

`SaveProfileSetting` currently resolves the ambient selection, which
`ProfileAttributionSourceTests` tolerates only via allowlist. With a
snapshot, the value the user edited has a known owner, and the write follows
it. This is the doctrine the attribution test states verbatim (parameter or
snapshot, never a fresh selection read), it removes three of the four
allowlisted selection reads (a startup pair read arrives in their place),
and it completes the settings slice of SPA-1 that
`feature-complete-profile-reset.spec.md` explicitly deferred. That deferral
anticipated exactly this follow-up ("what would give the cache an identity
worth guarding"), so nothing recorded there is reversed; its premise (the
cache has no identity) is retired together with the code that made it true.

### The reload stays synchronous

The three progress services reload fire-and-forget; settings do not follow
them. The payload is one row set of eight values, the getters are synchronous
and must answer coherently mid-startup, and the reset hook must have the
cache current when it returns. An async reload would buy nothing but a new
window in which getters serve the old snapshot under the new selection. The
race protection does not depend on asynchrony: the guard exists because two
handler threads can interleave, and it works the same for synchronous
reloads. The future unification standardizes the state model (snapshot plus
revision), not the load transport, so this divergence does not block it.

### Failure publishes the new profile's defaults

When the bulk read throws, the published snapshot names the new profile and
holds nulls, so every getter answers its default. Keeping the previous values
instead would be the original defect with better manners: another profile's
values shown under the new profile's name. All three sibling services made
the same call, with the same self-heal on the next re-confirmation, and their
in-code rationale applies unchanged.

### All seven events still fire on every published reload

A reload that publishes raises all seven changed events whether or not values
differ, in the same order as today. Raising only actual changes would be a
UI-timing behavior change (pages refresh less often) tangled into a
correctness fix, and `ProfileResetHooksTests` pins the full sequence as the
reset contract. The one fan-out reduction this change does make is upstream
and mirrors `QuestProgressService`: a provenance-only re-confirmation that
needs no healing skips the reload entirely, so EFT's habit of re-logging the
session mode on every profile-screen visit stops triggering seven events and
three page refreshes for nothing.

### The revision gate is duplicated a fourth time, deliberately

`ClaimRevision` now exists in four services. Extracting the small shared type
was declined here because this change's scope is `SettingsService` only;
touching the three guarded services, even mechanically, widens review for no
behavior change. The recorded direction for the THR-1 follow-up: keep the
immutable snapshot as the shared state model, extract only the revision gate
as a small common type, and do not build a generic reload framework over the
per-service flows, whose differences (notify flags, half-failures, debounced
saves) are real. Under that follow-up, this service's guard becomes a pure
move.

### The dead setting stays in the snapshot

`ShowLevelLockedQuests` is stored, cached and saved but read by nothing
outside `SettingsService` and the legacy JSON migration. It rides along as
the eighth snapshot field. Dropping it mid-fix would delete a stored user
setting inside a concurrency change; the removal question is recorded in the
sibling PRD's Non-Goals and stays separate.

## Test Strategy

- **Unit, reload race** (`SettingsReloadRaceTests`, new): mirror
  `ProfileReloadRaceTests` - a reload carrying a stale revision must not
  publish over the newer snapshot and must raise no events; the same reload
  for the current revision publishes and raises all seven. Built on an
  uninitialized service with a seeded snapshot, like the sibling tests.
- **Unit, atomicity**: a published reload replaces all eight values and the
  profile id as one reference; assert the snapshot after a reload never
  mixes seed values with loaded values.
- **Unit, write attribution**: with a snapshot seeded for profile A while the
  ambient selection names B, a setter persists to A. This is the test that
  fails on current code (which writes to B) and passes after; it is the
  reproducing test for the write half, and the practical repro for the whole
  defect, since the read half's guard seam (an injectable revision) does not
  exist before the fix. Driving the true eight-read tear end-to-end would
  require flipping the process-wide `ProfileService` singleton mid-load,
  which the test suite deliberately never does; that limitation is accepted
  and is why the race tests assert at the guard seam, as the sibling suites
  do.
- **Unit, failure path**: a store whose bulk read throws yields a snapshot
  naming the new profile with default values, and the next re-confirmation
  reloads (the `_lastLoadFailed` heal).
- **Unit, reset hooks** (`ProfileResetHooksTests`, updated): same
  assertions, reseeded through the snapshot; the target comparison now runs
  against the snapshot's profile id, so the unselected-target no-op test
  seeds a snapshot naming a different profile.
- **Unit, source scan** (`ProfileAttributionSourceTests`, updated): the
  reshaped allowlist enforces that the removed selection reads stay removed.
- **E2E**: the existing profile-switch e2e coverage must stay green; the
  millisecond collision itself is not e2e-reproducible, and no new e2e is
  claimed for it.

## Verification

- `dotnet build TarkovHelper.sln`
- `dotnet test TarkovHelper.Tests` (full suite; the race, reset-hook,
  attribution-scan and decision-docs tests are the ones this change moves)
- Manual: run the app, switch profiles from the selector and by starting a
  raid in another mode, and confirm level/faction/prestige track the
  selection; reset the selected profile and confirm settings return to
  defaults with editions kept.

## Risks & Migration

- **No schema or data migration.** The `ProfileSettings` table and its keys
  are unchanged; rollback is reverting the commit.
- **Failure granularity coarsens.** Today each of the eight reads fails
  alone (one unreadable key nulls one field); with one query, a read failure
  defaults all eight. In practice the store fails per-connection, not
  per-key, so eight reads failed together anyway; per-key parse failures
  keep their per-key fallback.
- **Reflection-coupled tests move with the fields.** `ProfileResetHooksTests`
  seeds `_playerLevel` and its seven siblings by name; the snapshot rework
  breaks those seams loudly (the reflection helper throws on a missing
  field), and the update is part of this change, not a follow-up.
- **Behavioral deltas are confined to the defect window and to redundant
  refreshes.** Outside a collision, reload results and write targets are
  identical to today; inside it, writes now follow the displayed value, and
  provenance-only re-confirmations that need no healing no longer re-raise
  seven events. Anything depending on those redundant refreshes would be
  depending on EFT's re-logging cadence, and no such dependency is known.
