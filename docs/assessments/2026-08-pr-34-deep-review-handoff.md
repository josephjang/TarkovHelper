# PR #34 Deep Review Handoff

- Review date: 2026-08-10
- Pull request: <https://github.com/josephjang/TarkovHelper/pull/34>
- PR title: `feat(profile): add seasonal profile and log-based switching`
- Base: `main` at `72c7b20fdc4ffafe4b7e644ff517d6ff9f45c28e`
- Reviewed head: `feat/seasonal-profile` at `5ba36f130f30a511c7b2ffb9d2fe0538c30125ac`
- Procedure: `C:\Users\josep\.claude\commands\deep-review.md`

This is an evidence and continuation document, not an implementation-status record.
Line numbers refer to the reviewed PR head and may drift after fixes.

## Continuation Warning

The review was interrupted after draft fixes had already begun.

- Current local branch: `fix/seasonal-profile-deep-review`
- The branch has uncommitted changes in application, test, and XAML files.
- The draft has not been committed or pushed.
- No stacked pull request was created.
- No comment was posted to PR #34.
- The last complete build succeeded before the final, partially applied selector-fit
  refactor. Do not assume the current worktree builds or that the draft fixes are
  correct as a set.

A continuing agent should first choose one of these paths:

1. Inspect and salvage the draft with `git diff`, then build before making further
   edits.
2. With explicit user approval, discard only the draft changes listed by
   `git status` and restart from reviewed head `5ba36f1`.

Do not use a broad destructive reset without resolving the exact intended files and
confirming that no user changes are mixed into the draft.

## Review Coverage

All ten deep-review angles ran. No angle was skipped.

| Stage | Count |
|---|---:|
| Finder candidates | 65 |
| Gap-sweep additions | 7 |
| Raw total | 72 |
| Canonical candidates after same-root deduplication | 58 |
| Confirmed | 33 |
| Plausible | 13 |
| Refuted | 12 |

Every canonical candidate and every gap-sweep candidate received a per-location
verification verdict. The actionable candidates below are merged into 17 root
issues so fixes and tests can be planned coherently.

## Actionable Findings

### DR-01 — Profile transitions are not an atomic state change

- Verdict: Confirmed/Plausible
- Primary locations:
  - `TarkovHelper/Services/ProfileService.cs:49-60`
  - `TarkovHelper/Services/QuestProgressService.cs:16,951,1534`
  - `TarkovHelper/Services/HideoutProgressService.cs:20,351`
  - `TarkovHelper/Services/ItemInventoryService.cs:33,306`
- Source candidates: SCAN-3, SCAN-4, SCAN-5, RIPPLE-2, RIPPLE-3,
  EFFICIENCY-1, DESIGN-8

`SetActiveProfile` changes global identity synchronously, starts persistence without
awaiting it, then raises an event whose reload subscribers start unversioned async
work. Those reloads repeatedly consult the ambient `ActiveProfileId` rather than a
captured transition destination.

Failure scenarios:

- A log hint changes PvP Zone to Season. Before the Season reload finishes, the
  independent quest watcher applies an event to the old in-memory dictionary, while
  the save reads the already changed global id and writes that state to `season`.
- A slower reload for transition A completes after transition B and publishes A's
  data into the UI while B is active.
- Rapid transitions enqueue writes to `app.activeGameMode` that can complete out of
  order. Immediate shutdown can abandon the latest write.
- Repeated evidence for the already active profile changes only provenance but still
  triggers all settings/progress reloads.

Recommended fix shape:

- Publish one immutable transition snapshot containing destination, profile id,
  cause, and monotonic revision.
- Serialize transition dispatch and profile-setting persistence.
- Pass the captured profile id and revision to every reload and save operation.
- Apply a reload result only if its revision is still current.
- Give log-driven mutation paths a readiness barrier so events following a session
  hint cannot mutate pre-transition caches.
- Flush the serialized persistence tail during orderly shutdown.
- Do not reload profile stores for same-destination evidence; it may still produce a
  transient cue if required by the product contract.

Minimum regression tests:

- Delayed transition A followed by fast transition B publishes only B.
- A save started under profile A remains scoped to A after the active profile changes.
- Rapid A -> B -> C persistence restores C on restart.
- Same-profile log evidence does not invoke store reloads.

### DR-02 — Monitoring startup races DB restore, initial scan, and live callbacks

- Verdict: Confirmed
- Primary location: `TarkovHelper/Services/EftRaidEventService.cs:260-315`
- Source candidates: SCAN-6, ALTITUDE-2

`StartMonitoring` enables watchers before initialization is complete, starts
`LoadProfileFromDbAsync` in the background, and performs `InitialScan` separately.
The initial scan is not protected by the same read lock as tail processing.

Failure scenarios:

- Initial scan discovers the newest PMC identity, then a slower DB load restores an
  older identity that later raid classification uses.
- A watcher callback processes a line while the initial scan is reading the same
  file, causing duplicate or out-of-order profile/session events.

Recommended fix shape:

- Construct and subscribe watchers with raising disabled.
- Load stored identity, scan, and initialize cursors under one initialization/read
  sequence.
- Enable watchers only after the stable prefix is scanned.
- Perform a catch-up read after enabling watchers to close the scan/enable gap.
- Keep the expensive scan off the WPF dispatcher.

### DR-03 — Unterminated EOF fragments are permanently lost

- Verdict: Confirmed
- Primary location: `TarkovHelper/Services/EftRaidEventService.cs:624-693`
- Source candidate: ALTITUDE-1

`StreamReader.ReadLine` returns the final unterminated fragment, and the implementation
then advances the cursor to the buffered stream position. If EFT completes that line
in a later append, the prefix is never read again and the completed event is missed.

Recommended fix shape:

- Frame appended bytes at the last complete newline.
- Advance the cursor only through that newline.
- Leave an unterminated byte suffix unread for the next callback.
- Apply the same framing to application and network logs.

Regression test: append `Session mode: PvpSea`, process, append `son\r\n`, and assert
exactly one `PvpSeason` event.

### DR-04 — Historical session files can replay stale state

- Verdict: Confirmed
- Primary location: `TarkovHelper/Services/EftRaidEventService.cs:560-693`
- Source candidate: SWEEP-4

The recursive watcher covers the entire EFT Logs tree, but callbacks do not verify
that a changed file belongs to the newest session folder. Cursor state is initialized
only for the latest files; an old file without a cursor is read from byte zero.

Failure scenario: touching or restoring an old application log replays an obsolete
`Session mode` line and automatically selects the wrong active storage profile.

Recommended fix shape:

- Normalize and compare the event file's parent folder with the currently resolved
  latest session folder under the session/read lock.
- Ignore changes from older session folders.
- When a new session folder becomes latest, explicitly initialize its cursors and
  scan current application state once.

### DR-05 — Partial monitoring failures leak active resources

- Verdict: Confirmed
- Primary location: `TarkovHelper/Services/EftRaidEventService.cs:260-325`
- Source candidate: SWEEP-6

If setup fails after the application watcher is enabled, the catch reports monitoring
as off but does not dispose already created watchers or a timer. Callbacks do not
guard on `_isWatching`, so parsing and automatic profile switching can continue.

Recommended fix: use one no-throw cleanup routine for both normal stop and every
startup failure path; construct disabled watchers and enable only after successful
initialization.

### DR-06 — Background persistence captures mutable profile and raid fields

- Verdict: Confirmed/Plausible
- Primary locations:
  - `TarkovHelper/Services/EftRaidEventService.cs:514-532,739-762`
  - `TarkovHelper/Services/EftRaidEventService.cs:709,947`
- Source candidates: SCAN-7, DUPLICATION-3, DUPLICATION-4, SWEEP-1

Profile and raid saves schedule lambdas that read `_currentProfile` or `_currentRaid`
later. The fields can be replaced or cleared before the worker runs.

The raid fallback is a deterministic loss case: it schedules
`SaveRaidHistoryAsync(_currentRaid)` and then immediately assigns `_currentRaid =
null`. A delayed worker passes null into the save path. The transit path can save a
subsequently created raid instead of the completed one.

Recommended fix shape:

- Build an immutable local snapshot at the completed event boundary.
- Pass that snapshot directly to persistence and event publication.
- Funnel startup/live profile selection through one `ApplySelectedProfile` helper.
- Serialize the three profile-setting writes so two profile snapshots cannot mix.
- Add close-time flushing if these writes are required to survive orderly shutdown.

### DR-07 — Profile identity validation and PMC-to-Scav arithmetic are incorrect

- Verdict: Confirmed
- Primary locations:
  - `TarkovHelper/Services/EftRaidEventService.cs:23`
  - `TarkovHelper/Services/EftRaidEventService.cs:1099`
  - `TarkovHelper/Models/EftRaidEvent.cs:82-120`
- Source candidates: SWEEP-2, SWEEP-5

The completed-profile regex accepts any positive number of hex characters even
though the established identity format is 24 hex characters. Accepted malformed
values replace and persist the durable profile identity.

`CalculateScavProfileId` increments only the final nibble. A PMC id ending in `f`
wraps that nibble to zero without carrying, and `IsScavProfile` independently assumes
the first 23 digits never change. A real Scav id after a carry is classified as
`Unknown`.

Recommended fix shape:

- Require exactly 24 hex characters in the completed-profile parser.
- Centralize a validated, fixed-width full-hex increment with carry.
- Treat full-width overflow as invalid/unknown.
- Compare a raid profile id with the stored derived Scav id case-insensitively.

Tests: 23/24/25-character parser cases, `...0e -> ...0f`, `...0f -> ...10`, malformed
hex, and all-`f` overflow.

### DR-08 — Session hint and game mode can form an impossible pair

- Verdict: Confirmed
- Primary location: `TarkovHelper/Services/EftRaidEventService.cs:723-736,958`
- Source candidates: DESIGN-3, DUPLICATION-4

The parser returns correlated values, but the service stores hint and game mode in
separate mutable fields. Startup scanning and live callbacks can interleave and leave
states such as `PveZone` with `GameMode.PVP`.

Recommended fix: store and publish one immutable `SessionProfileEvidence` value with
`GameMode` derived from the hint. Events should not expose independently settable
hint and mode properties.

### DR-09 — Profile mapping catch-alls silently target permanent PvP storage

- Verdict: Confirmed/Plausible
- Primary location: `TarkovHelper/Services/ProfileService.cs:70-110`
- Source candidates: REMOVALS-3, FOOTGUNS-6, DESIGN-7, ALTITUDE-7,
  DUPLICATION-6

Resolver, serialization, profile-id, and game-mode switches use catch-all PvP
fallbacks. An invalid cast or a future enum value can silently read or write permanent
PvP data. The public resolver currently accepts arbitrary enum casts.

Recommended fix shape:

- Define one exhaustive core profile catalog containing profile enum, database id,
  persisted token, game rules, and session hint.
- Validate that every `AppProfile` has one unique catalog definition.
- Throw at internal/programmer-error boundaries for unknown enum values.
- Preserve the deliberate user-data fallback only in `ParseStoredProfile`.
- Remove the unused, lossy `GetProfileId(GameMode)` overload.

### DR-10 — Selector callbacks can publish stale state and lasting provenance

- Verdict: Confirmed
- Primary locations:
  - `TarkovHelper/MainWindow.xaml.cs:678-795`
  - `TarkovHelper/MainWindow.xaml:25-95`
- Source candidates: SCAN-2, SCAN-12, FOOTGUNS-4, ALTITUDE-12

`OnActiveProfileChanged` queues dispatcher work without a revision check. The queued
callback renders its captured destination but rereads later mutable provenance from
the singleton. An older automatic callback can overwrite a later manual choice or
restore its cue.

After the cue timer expires, `ClearAutomaticProfileTransitionCue` restores the
persistent `AutoSelected` bolt and source description. This contradicts R6 of the
changed PRD: Manual/Auto/Pinned is transient input feedback, not lasting selector
state. Existing E2E assertions encode the inverse contract.

Recommended fix shape:

- Include cause and revision in the immutable profile transition event.
- Discard a dispatcher callback whose revision is no longer current.
- Render entirely from the event snapshot; do not reread ambient source state.
- Clear all source-specific visual and UIA state when the transient cue expires.
- Invalidate an already queued auto cue when the user selects the same profile
  manually.
- Update E2E tests to assert native radio selection and the transition announcement,
  not a lasting source ItemStatus.

### DR-11 — Fixed selector sizing clips supported localization/font combinations

- Verdict: Confirmed
- Primary locations:
  - `TarkovHelper/MainWindow.xaml:310-400`
  - `TarkovHelper/MainWindow.xaml.cs:2229-2255`
  - `TarkovHelper/HeaderLayout.cs`
- Source candidates: SCAN-8, SCAN-11, REMOVALS-2, ALTITUDE-8

The compact trigger/context menu is fixed at 172 px and interactive targets are 30
px high. The supported JA label at maximum font size measured about 145.16 px before
marker, arrow, padding, and border, requiring roughly 213 px. The wide selector is
shown solely from a fixed 1000 px window breakpoint; a verifier reproduced clipping
at 1000 DIP under JA, maximum font, and 200% DPI.

Recommended fix shape:

- Use at least 36 px pointer targets.
- Give the compact trigger/menu a minimum width but allow content sizing.
- Decide selector shape separately from coarse brand/tab density.
- Compare the natural desired widths of both variants and visible sibling controls
  against available title-bar width.
- Add margin and hysteresis to avoid visibility/measurement oscillation.
- Re-evaluate after window resize, language change, and font-resource change.

Tests: pure fit-boundary/hysteresis tests and EN/KO/JA E2E coverage at minimum,
default, and maximum fonts with bounding rectangles inside the window.

### DR-12 — Profile selector UIA ItemStatus is hard-coded English

- Verdict: Plausible
- Primary location: `TarkovHelper/MainWindow.xaml.cs:785-795`
- Source candidate: SCAN-9

The selector publishes `Selected`/`Unselected` ItemStatus values even under KO and
JA. The property is observable in UI Automation; the exact spoken behavior depends
on the screen reader.

Recommended fix: prefer the native `SelectionItemPattern.IsSelected` state and
localized accessible names. If ItemStatus remains necessary, localize it.

### DR-13 — Startup can expose blank, unselected profile controls

- Verdict: Confirmed
- Primary locations:
  - `TarkovHelper/MainWindow.xaml:322-367`
  - `TarkovHelper/MainWindow.xaml:694`
  - `TarkovHelper/MainWindow.xaml.cs:176-225`
- Source candidate: REMOVALS-1

The three radio buttons and menu items have no initial content/selection. Their
values are assigned only after awaited DB initialization, while the loading overlay
starts collapsed. Users can briefly see an empty selector.

Recommended fix: provide safe PvP defaults in XAML and show the startup overlay until
profile restoration and initial data loading are complete.

### DR-14 — Wide and compact selector implementations duplicate one state machine

- Verdict: Confirmed/Plausible
- Primary locations:
  - `TarkovHelper/MainWindow.xaml:310-408`
  - `TarkovHelper/MainWindow.xaml.cs:623-795`
- Source candidates: DESIGN-10, DESIGN-12, DUPLICATION-1, DUPLICATION-2,
  ALTITUDE-9

The wide and compact variants duplicate option definitions, handlers, localized
labels, checks, cue mapping, and accessible state. There are nine profile-specific
handlers and repeated switches for the same three profiles.

Recommended fix: extract a focused `ProfileSelector` control with one option
collection and shared selection/render/cue logic. Preserve real RadioButtons and the
existing automation ids. The detached `ContextMenu` and live region need explicit
ownership and cleanup inside the control.

### DR-15 — PR #34 currently fails the font-family policy test

- Verdict: Confirmed
- Primary location: `TarkovHelper/MainWindow.xaml:156`
- Source candidate: SCAN-1

`FontAssetsTests.Every_fontfamily_literal_in_the_app_is_an_approved_family` rejects
`FontFamily="{TemplateBinding FontFamily}"`. GitHub CI and a local fail-first run
both reproduced this exact failure.

Narrow fix: use the approved app font resource at that template text site, or change
the template in another way that preserves the repository's single font-stack
construction policy. Rerun the named test before the broad suite.

### DR-16 — Documentation and dead compatibility surfaces drift from repository rules

- Verdict: Confirmed/Plausible
- Primary locations:
  - `docs/decisions/feature-profile-log-auto-switch.md:90`
  - `docs/decisions/feature-profile-log-auto-switch.spec.md:120`
  - `TarkovHelper/Services/EftRaidEventService.cs:204,373`
  - `TarkovHelper/Services/ProfileService.cs:105-128`
- Source candidates: CONVENTIONS-1, CONVENTIONS-2, DUPLICATION-5,
  DUPLICATION-6, DUPLICATION-7

The two decision documents contain `Implementation Status` sections despite the
repository rule that current implementation state belongs in the PR. Several added
compatibility surfaces have no production consumers: `EftRaidEventService.ProfileChanged`,
`SetProfileAsync`, `ProfileChangedEventArgs.GameMode`, and `GetProfileId(GameMode)`.
`CurrentSessionProfileHint` is also unused, though retaining a single immutable
current-evidence accessor is a defensible API choice.

Recommended fix:

- Remove the two implementation-status sections while preserving durable decisions.
- Remove orphaned methods/events and update stale documentation examples.
- Re-audit a current-evidence accessor after introducing immutable evidence; keep it
  only if an actual consumer or explicit query contract remains.

### DR-17 — Remaining architecture candidates need scoped judgment

- Verdict: Plausible
- Primary location: `TarkovHelper/Services/EftRaidEventService.cs`
- Source candidates: SCAN-10, DESIGN-1, DESIGN-5

The service combines discovery, watching, incremental I/O, parsing, mutable raid
reduction, event publication, and persistence. Initial scanning currently runs from
a synchronous startup API and scales with the entire application log. Replacing the
optional-field `EftRaidEventArgs` bag with typed events could also make invalid event
payloads unrepresentable.

These are real maintainability concerns but broader than the minimum correctness
repair. Recommended decision:

- Move initialization/scanning off the UI thread as part of DR-02.
- Extract a small incremental log source/framer and profile/session application
  helpers where required by confirmed defects.
- Do not require a full service split or event-hierarchy rewrite unless the focused
  repair remains difficult to test or reason about afterward.

## Refuted Candidates

| Candidate | Verdict rationale |
|---|---|
| Seasonal profile should remain pinned on `Regular`/`Pve` | Refuted. The later PRD/spec explicitly supersedes pinning and requires symmetric known-hint mapping. |
| Accept suffix-bearing `Session mode` tokens | Refuted. The observed/versioned contract ends at the token; exact matching is intentional fail-safe behavior. |
| Accept punctuation immediately after `AccountId` | Refuted. All captured evidence ends in digits, and preserving malformed input as unknown is safer. |
| Dispatcher queue retention is a material performance issue | Refuted. Transitions are infrequent and the queued work is small; stale correctness is covered separately by DR-10. |
| The clear/set live-region callback is wasteful | Refuted. Separate dispatcher turns are intentional so assistive technology observes a changed announcement. |
| Startup and live parsing need one shared reducer | Refuted. Startup chooses the last valid fact while live processing publishes each transition; forced shared flags obscure those semantics. |
| Completed profile selection needs a dedicated result record | Refuted. The regex already returns both values together or neither, and the record alone does not prevent swapping. |
| Lasting `ProfileSelection` source state should be introduced | Refuted. It conflicts with PRD R6; an immutable transition cause is sufficient. |
| Add a separate singleton coordination service | Refuted. Current singleton lifetime is not the defect; revisioned transition coordination can remain focused. |
| Replace two selector `Tag` strings with a typed dependency property | Refuted at the reviewed head. There was no collision; a focused selector control can avoid the issue without new infrastructure. |
| Consolidate the three debug SQL count queries | Refuted. The explicit queries are clearer and have no material runtime cost. |
| Initial scan must combine multiple application segments in one session folder | Refuted. Repository captures and eight inspected session folders each contain exactly one `application_000.log`; rotation is by session directory. Revisit only after new capture evidence. |

## Suggested Repair Order

1. Restore a buildable baseline and reproduce the current font test failure.
2. Fix DR-15 so ordinary CI can give useful signal.
3. Implement DR-01 and DR-10 together: immutable revisioned transition, serialized
   persistence, revision-gated reloads, readiness for log mutation, transient cue.
4. Implement DR-02 through DR-05 as one monitoring-initialization and incremental-I/O
   repair, with focused filesystem tests.
5. Implement DR-06 through DR-09: immutable snapshots/evidence, identity validation,
   full hex carry, exhaustive profile catalog.
6. Implement DR-11 through DR-14 as a focused selector control and measurement-driven
   fit change.
7. Remove DR-16 dead surfaces and document drift.
8. Re-audit DR-17 after the focused repairs; defer broad refactors unless they still
   provide concrete value.
9. Run all relevant tests and inspect the final diff against PR head.
10. Create a stacked branch/PR whose base is `feat/seasonal-profile`, then post the
    final review summary and stacked PR link to PR #34 if the user restores that scope.

## Verification Commands

Fail-first font guard:

```powershell
dotnet test TarkovHelper.Tests/TarkovHelper.Tests.csproj -c Release --filter "FullyQualifiedName~FontAssetsTests.Every_fontfamily_literal_in_the_app_is_an_approved_family"
```

Focused profile/parser coverage:

```powershell
dotnet test TarkovHelper.Tests/TarkovHelper.Tests.csproj -c Release --filter "FullyQualifiedName~ProfileSwitchingTests|FullyQualifiedName~EftRaidEventParsingTests"
```

Build and normal non-E2E suite:

```powershell
dotnet build TarkovHelper.sln -c Release
dotnet test TarkovHelper.sln -c Release --no-build --filter "Category!=E2E"
```

Targeted E2E coverage after a successful Release build:

```powershell
dotnet test TarkovHelper.Tests/TarkovHelper.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SeasonalProfileE2ETests|FullyQualifiedName~HeaderE2ETests"
```

## Original PR Contract Notes

- The current governing behavior is symmetric mapping of every exact known session
  hint from every current profile.
- `Unknown` is the only hint that preserves the current profile without applying a
  detection.
- A later exact known hint may replace a manual selection, including PvP Season.
- Manual/Auto/Pinned source is not persistent selector state. Automatic transitions
  may use a brief visual cue and an accessible announcement without moving adjacent
  controls.

These points come from the later `feature-profile-log-auto-switch` PRD/spec and
supersede the earlier seasonal-pin material still present in the older seasonal
profile spec.

## Verification note, appended 2026-08-16

Added on request after a code check at commit `ffa08d1` (branch head equal to
`origin/main`). The review record above is unchanged; this note maps each root
issue to where the code stands now. The hardening work for this review merged
into PR #34 as commit `f78c8d6`; later PRs (profile data attribution, complete
profile reset, profile settings race) closed more.

Status: 7 fixed (DR-03, DR-07, DR-08, DR-09, DR-12, DR-13, DR-15), 7 partial
(DR-01, DR-06, DR-10, DR-11, DR-14, DR-16, DR-17), 3 open (DR-02, DR-04, DR-05).

Remaining work, ranked by severity of what is left today (not the severity at
review time; later fixes shrank several blast radii). Effort scale: small is a
contained change plus tests, medium is a focused PR, large is multi-PR work.

| ID | Remaining item | Severity | Effort |
| --- | --- | --- | --- |
| DR-02 | Ordered watcher/scan/identity startup, off the UI thread | High | Medium; one lifecycle rewrite together with DR-04/05, and the filesystem tests are most of the work |
| DR-04 | Ignore events from non-latest session folders; seed cursors on rollover | Medium | Small inside the DR-02 rewrite |
| DR-05 | Shared no-throw cleanup; callbacks guard on the watching flag | Medium | Small inside the DR-02 rewrite |
| DR-06 | Serialize the `_currentProfile` writers | Medium | Rides with DR-02 |
| DR-10 | Revision-discard in the selector dispatcher callback | Medium | Small; `bf5287d` exists unmerged, rebase it and add a test |
| DR-01 | Latest-wins persistence for the active-profile setting | Medium | Small; tracked as SPT-1 |
| DR-01 | Skip store reloads on provenance-only flips (hideout, inventory) | Low | Small; copy the quest/settings guard plus two tests |
| DR-16 | Delete the `ProfileChanged` event and `SetProfileAsync` | Low | Small; also removes DR-06's dead post-await read |
| DR-11 | Measured selector fit, hysteresis, 36 px targets, non-tautological fit tests | Low | Medium |
| DR-14 | Extract one `ProfileSelector` control; single cue animation | Low | Medium |
| DR-17 | Service split beyond the framer | Low | Large; still deferred by design, revisit only if the DR-02 repair stays hard to test |

Severity rationale for the top rows: DR-02's identity race is in play on every
launch (misclassified Scav/PMC raids, whole-log replay on the live path), so it
is High even though each window is narrow. DR-04 and DR-05 have rare triggers
(an old file touched; a mid-startup failure) but silent, lasting consequences,
so Medium. DR-10 is UI-and-announcement only since the attribution work, and
DR-01's persistence race needs rapid switches plus a restart, so both sit at
Medium. Everything Low is wasted work, dead surface, or maintainability, with
no data at stake.

Fixed:

- **DR-03**: incremental reads are framed at the last complete newline by
  `EftLogPatterns.FrameCompletedLines`; cursors advance only through it.
- **DR-07**: the completed-profile parser requires exactly 24 hex characters and
  normalizes case at the boundary; `EftProfileInfo.NextProfileId` is the one
  full-width carry (an all-`f` id has no successor and returns null), recognition
  shares it and compares case-insensitively. Covered by `EftProfileIdentityTests`
  and `EftRaidEventParsingTests`.
- **DR-08**: one stored session hint; game mode is derived on read
  (`GameModeOf`); `EftRaidEventArgs` cannot carry an independently set pair.
- **DR-09**: profile-keyed switches throw on unknown values,
  `TryResolveDetectedProfile` reports no destination for unmapped hints,
  `ParseStoredProfile` keeps the only deliberate fallback, and
  `GetProfileId(GameMode)` is gone. `ProfileSwitchingTests` enumerates the enum
  so a new member cannot silently alias onto PvP.
- **DR-12**: the selection item status is localized (`HeaderProfileSelected` /
  `HeaderProfileUnselected`, EN/KO/JA).
- **DR-13**: the selector ships EN default labels in XAML and the constructor
  paints a selection before `Window_Loaded`'s awaits run.
- **DR-15**: the template-binding font literal is gone; `FontAssetsTests` still
  enforces the policy.

Partial:

- **DR-01**: the transition core landed via the later SPA-1/SPA-2 fixes
  (revisioned `ProfileChangedEventArgs`, `RevisionGate` in all four stores,
  captured write ownership). Still open: the active-profile persistence write is
  fire-and-forget with no latest-wins ordering (tracked as SPT-1), and on a
  provenance-only flip `HideoutProgressService` and `ItemInventoryService` still
  reload unconditionally (ItemInventory also flushes pending saves) while the
  quest and settings services correctly skip.
- **DR-06**: every raid and profile save path snapshots into a local before
  scheduling, so the deterministic null-raid save is gone. Still open:
  `_currentProfile` is last-writer-wins across four writers under three
  different locks (the DR-02 startup race), and the dead `SetProfileAsync` reads
  the field again after its await.
- **DR-10**: lasting provenance is gone; the cue and its announcement are fully
  transient and E2E-guarded. Still open: `OnActiveProfileChanged` never checks
  `args.Revision`, so out-of-order dispatcher enqueues can leave the selector
  rendering the losing profile until the next transition. The exact fix exists
  unmerged as commit `bf5287d` on `fix/seasonal-profile-deep-review` (written
  after PR #34 merged, never landed); `fix-profile-settings-race.md` records
  only the milder brief-lag reading of this defect as a non-goal.
- **DR-11**: the compact trigger and menu use MinWidth plus ellipsis instead of
  a fixed width. Still open: interactive targets are ~30 px, the wide/compact
  decision is the bare `HeaderLayout.CompactThreshold = 1000` with no
  measurement, hysteresis, or re-evaluation on language/font change (the file's
  own remark records JA at the default font needing ~1001 px), and one
  `ProfileSelectorFitTests` case asserts a `Math.Max` tautology.
- **DR-14**: no `ProfileSelector` control was extracted. The `ProfileControls`
  table removed the per-profile switches; nine one-line handlers, twelve
  localized-text assignments, and two hand-synchronized cue animations (the XAML
  storyboard and a code-behind `DoubleAnimation`) remain.
- **DR-16**: the implementation-status sections are gone, and
  `ProfileChangedEventArgs.GameMode`, `GetProfileId(GameMode)`, and the public
  hint accessor were removed. Still present with zero consumers:
  `EftRaidEventService.ProfileChanged` (raised at three sites) and
  `SetProfileAsync`.
- **DR-17**: `EftLogPatterns` (the framer plus shared session parsing) was
  extracted and `LogSyncService` shares it. The service itself is now 1248 lines
  and startup scanning still runs synchronously on the UI thread from
  `Window_Loaded`, the one DR-17 item meant to ride with DR-02.

Open, untouched since the reviewed head (a diff from `5ba36f1` to `ffa08d1`
never reaches these paths; no PRD, no tests):

- **DR-02**: watchers are constructed with `EnableRaisingEvents = true` before
  handlers attach, `_filePositions.Clear()` runs after they are live (an early
  callback replays a file from byte zero), the DB identity load stays
  fire-and-forget and can clobber the identity the initial scan just parsed,
  and the initial scan reads outside `_readLock` on the UI thread.
- **DR-04**: watcher callbacks never compare a changed file's folder with the
  latest session folder; a touched or restored old log still replays a stale
  `Session mode` line, switches the active profile, and can create raid rows.
  The attribution work confines the damage (log progress writes carry their own
  owner now), but profile selection and raid history remain exposed.
- **DR-05**: the `StartMonitoring` catch does not dispose already-enabled
  watchers or the timer, `StopMonitoring` is not no-throw, and callbacks do not
  guard on `_isWatching`, so a failed startup can keep parsing and switching for
  the process lifetime.

Continuation Warning status: resolved. The interrupted draft was salvaged and
merged into PR #34 as `f78c8d6` on 2026-08-10, so the recovery paths above are
moot. One loose end: `fix/seasonal-profile-deep-review` still exists locally and
on origin, carrying the single unmerged commit `bf5287d` named under DR-10.
