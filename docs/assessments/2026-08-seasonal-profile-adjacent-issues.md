# Seasonal Profile Adjacent Issues, August 2026

> Snapshot assessment. Analyzed at commit `2f8d389` (2026-08-09). This document
> preserves existing problems found while designing PvP Season that are not
> materially made worse by adding the profile. They are deliberately outside
> `feature-seasonal-profile.md` and `feature-seasonal-profile.spec.md`. A later PR
> addressing one names its SPT ID and creates the focused PRD/spec required by the
> normal decision-doc process.

## Scope boundary

The seasonal-profile feature adds one profile identity, a three-way selector, and
pinning behavior. None of the findings below is required to express or isolate that
identity. They remain worth fixing, but coupling them to this feature would expand its
completion criteria without a corresponding seasonal-specific benefit.

This differs from the SPA assessment: SPA findings become more likely, more damaging,
or necessary for a credible future seasonal reset. SPT findings are nearby engineering
work whose priority is substantially unchanged by a third profile.

## Findings index

| ID | Finding | Severity | Suggested follow-up |
| --- | --- | --- | --- |
| SPT-1 | Active-profile setting writes have no latest-wins ordering | Medium | profile-setting persistence fix |
| SPT-2 | Bounded log sync still parses every candidate file | Medium | log-sync performance spec |
| SPT-3 | Cutoff behavior lacks an injected clock | Low | pair with log-sync testability work |
| SPT-4 | Database singleton prevents in-process path isolation | Medium | coordinate with data-layer test/DI work |

## SPT-1: Active-profile setting writes have no latest-wins ordering

**Existing problem.** `ProfileService.SetActiveGameMode` starts
`SettingsService.SetSettingAsync` without awaiting or serializing it. Rapid selections
can therefore persist in a different order from the UI transitions, leaving an older
choice in `ActiveGameMode` after restart. This is the profile-setting instance of the
broader fire-and-forget risk recorded as THR-6 in `2026-08-code-health.md`.

**Why it is not specially amplified.** PvP Season adds one possible value, but the
same ordering bug already exists when users switch rapidly between PvP and PvE. The
number of values does not change the underlying race or its remedy.

**Recommended boundary.** Handle profile-setting durability in a focused change:
make the write awaitable at an owned lifecycle boundary or serialize/coalesce writes
with explicit latest-wins semantics. Do not introduce a seasonal-only persistence
mechanism.

**Guard tests.** Delay the first persistence operation, issue a second selection,
complete writes out of order, restart the profile service, and prove the last user
selection wins.

## SPT-2: Bounded log sync still parses every candidate file

**Existing problem.** Even when `LogSyncService.SyncFromLogsAsync` receives a positive
`daysRange`, it discovers and parses all matching log files before filtering events by
timestamp. On long-lived EFT installations, bounded sync can therefore do nearly the
same I/O and parsing work as all-history sync.

SPA-5 separately tracks the correctness bug in which the main UI path fails to pass
the configured range at all. SPT-2 begins only after that value reaches the service.

**Why it is not specially amplified.** Seasonal data isolation does not itself alter
the number or size of EFT log files. File preselection is a general sync performance
improvement for every profile.

**Recommended boundary.** In a log-sync performance spec, evaluate conservative file
preselection using metadata or directory dates while retaining event-level filtering
as the correctness boundary. Preserve range `0` as explicit all-history recovery;
filesystem timestamps alone must not become the only source of truth.

**Guard tests.** Prove eligible events at the cutoff are retained, obviously old files
can be skipped when metadata is trustworthy, misleading timestamps cannot silently
drop in-range events, and range `0` still scans all history.

## SPT-3: Cutoff behavior lacks an injected clock

**Existing problem.** Time-window logic derives the cutoff from the ambient current
time. Boundary tests must either construct dates around the test's execution time or
avoid exact rollover behavior, making them less deterministic.

**Why it is not specially amplified.** The cutoff and its clock are properties of log
sync, not profile identity. Adding PvP Season creates no new time source or cutoff
rule.

**Recommended boundary.** Introduce `TimeProvider` or a small clock abstraction when
the sync-range behavior is revised, rather than adding a dependency seam solely for
the seasonal-profile PR.

**Guard tests.** Fix the clock and cover just-before, exactly-at, and just-after cutoff
events, including a date-boundary case in the intended local/UTC interpretation.

## SPT-4: Database singleton prevents in-process path isolation

**Existing problem.** `UserDataDbService` is held by a private `Lazy` singleton and
captures the user-data path on first access. Changing `AppEnv` later in one test
process does not create a fresh database instance. This complicates focused data-layer
tests and encourages broader child-process tests. It overlaps ARC-1 and TEST-1 in
`2026-08-code-health.md`.

**Why it is not specially amplified.** The existing profile-aware tables already
accept arbitrary string profile ids, so validating `season` does not require a new
database architecture. The seasonal E2E can use the repository's child-process
isolation pattern just as existing tests do.

**Recommended boundary.** Add an explicit database-path construction seam as part of
data-layer testability or broader dependency-lifetime work. Coordinate it with ARC-1
and TEST-1 so the repository does not acquire a one-off seasonal test hook.

**Guard tests.** Run two database instances with distinct temporary paths in one
process and prove schema initialization, reads, and writes stay isolated.

## Suggested follow-up split

These findings should not be bundled into the seasonal-profile implementation. The
natural future work boundaries are:

1. Active-profile persistence ordering (SPT-1; coordinate with THR-6).
2. Log-sync performance and deterministic cutoff tests (SPT-2, SPT-3), preferably
   after SPA-5 restores the intended correctness path.
3. Database construction and in-process test isolation (SPT-4; coordinate with ARC-1
   and TEST-1).

Each future PR names these IDs and creates its focused decision documents. Closing or
rejecting work is recorded in the PR/issue, not by editing this frozen assessment.
