# Symmetric Log-Based Profile Switching - Technical Spec

- **Created**: 2026-08-09

> The sibling `feature-profile-log-auto-switch.md` holds the product decision. Write
> this on the work's branch and merge it in the same PR as the work. Nothing is kept
> current: fields are written once, discoveries are appended. A later change that
> reverses a decision here appends `Superseded by <doc>` below this line, in the PR
> that reverses it.
>
> Implementation is deliberately deferred in this documentation-only change. Until
> the corresponding code change lands, `ProfileService` and its tests still enforce
> the superseded seasonal pin policy.

## Summary

`ProfileService.ResolveDetectedProfile` becomes independent of the current profile
for every known `SessionProfileHint`. It maps each exact hint directly to its
`AppProfile` and reports the detection as applied. Only `Unknown` returns the current
profile without applying a transition. The exact-token parser remains unchanged.

## Non-Goals

- Changing `EftRaidEventService` token syntax or profile-id parsing.
- Changing profile persistence ids, database schemas, or stored rows.
- Solving profile-scoped async write ownership, stale reload publication, or raid
  attribution.
- Implementing the broader selector redesign described by
  `2026-08-game-mode-selector-ux-review.html`; this spec defines only the switching contract
  and the feedback invariant it must expose.

## Current Behavior / Root Cause

`EftRaidEventService.TryParseSessionProfile` already maps complete `Pve`, `Regular`,
`Pvp`, and `PvpSeason` tokens to distinct profile hints. Both startup scanning and
live tailing publish the same `SessionModeDetected` event.

`ProfileService.ResolveDetectedProfile` then adds a current-state exception: when
`current == AppProfile.PvpSeason`, `PvpZone` and `PveZone` hints return the seasonal
profile with `DetectionApplied == false`. `ProfileSwitchingTests` and
`SeasonalProfileE2ETests` encode that suppression. The parser can identify the
transition, but the resolver intentionally discards it.

## Design

The resolver is a direct mapping:

```text
Unknown   + any current -> current, false
PvpZone   + any current -> PvpZone, true
PveZone   + any current -> PveZone, true
PvpSeason + any current -> PvpSeason, true
```

`ProfileService.ApplyDetectedProfile` continues to call `SetActiveProfile` only when
`DetectionApplied` is true. `SetActiveProfile` continues to persist the selected
profile and publish `ActiveProfileChanged` with `isAuto: true`. Repeated evidence for
the same profile may change a prior manual selection to auto-detected state under the
existing equality rule.

The later implementation changes:

- `TarkovHelper/Services/ProfileService.cs`: remove the season-only suppression
  branch from `ResolveDetectedProfile`.
- `TarkovHelper.Tests/ProfileSwitchingTests.cs`: expect known permanent hints to
  resolve away from PvP Season with `DetectionApplied == true`.
- `TarkovHelper.Tests/SeasonalProfileE2ETests.cs`: replace the pin scenario with
  Season -> PvE Zone -> PvP Season -> PvP Zone log-driven transitions.
- Profile-selector UI delivery: remove pin copy and present applied automatic changes
  with a fixed-slot transient cue plus an accessible announcement.

No parser, model, storage, or migration file changes are required for this policy
change.

## Technical Decisions

**Resolution depends on evidence, not the current destination.** A known
`SessionProfileHint` is already the parser's semantic result. Adding a current-state
exception after classification makes the resolver asymmetric without adding new
evidence.

**Unknown remains the sole no-op hint.** Exact-token matching prevents
`PvpSeason` from falling through to `Pvp`, while unknown and partial input already
retain the last valid state. This is the fail-safe boundary established by
`eft-1-1-profile-selection-log-analysis.md`.

**Applied evidence keeps its automatic provenance.** Known evidence reports
`DetectionApplied == true`, including when it confirms the same profile. Suppression
must not be represented as an automatic change, but this design no longer suppresses
known hints.

## Test Strategy

- **Unit**: keep the complete current-profile by hint matrix. Every known hint maps to
  its profile with `DetectionApplied == true`; `Unknown` preserves each current
  profile with `false`.
- **Parser**: retain exact-token fixtures proving `PvpSeason` does not match the
  shorter `Pvp` alternative and that unknown suffixes remain unknown.
- **E2E**: start with PvP Season selected, append `Pve`, `PvpSeason`, and `Regular` in
  order, and assert the visible selection and automatic-transition feedback after
  each event.

## Verification

```powershell
dotnet test TarkovHelper.Tests/TarkovHelper.Tests.csproj --filter "FullyQualifiedName~ProfileSwitchingTests|FullyQualifiedName~EftRaidEventParsingTests"
dotnet test TarkovHelper.Tests/TarkovHelper.Tests.csproj --filter "FullyQualifiedName~SeasonalProfileE2ETests"
```

The unit matrix must have no current-profile exception for known hints. The E2E path
must visibly leave PvP Season on exact permanent-profile evidence and return on exact
seasonal evidence.

## Risks & Migration

There is no data or setting migration. Rollback restores the suppression branch and
its previous test expectations. The change increases the frequency of real profile
reloads, so SPA-1 and SPA-2 remain correctness risks until their focused fixes land.
