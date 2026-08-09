# Symmetric Log-Based Profile Switching - PRD

- **Created**: 2026-08-09

> The sibling `feature-profile-log-auto-switch.spec.md` holds the technical design.
> Write this on the work's branch and merge it in the same PR as the work. Nothing
> is kept current: fields are written once, discoveries are appended. A later change
> that reverses a decision here appends `Superseded by <doc>` below this line, in the
> PR that reverses it.

## Summary

Known EFT session-profile tokens select PvP Zone, PvE Zone, or PvP Season from any
current app profile. PvP Season no longer acts as a pin. Unknown or incomplete log
evidence leaves the current selection unchanged, and automatic changes use brief,
layout-stable feedback instead of a persistent selection-source label.

## Problem

A controlled EFT 1.1 capture demonstrated distinct `Pve`, `Regular`, and
`PvpSeason` tokens for the three profile choices. The app parses those tokens, but
its switching policy ignores known PvP Zone and PvE Zone evidence whenever PvP
Season is selected.

This special case makes the visible selector behave differently depending on its
current value. A user who leaves the seasonal character in EFT can continue writing
permanent progress into the seasonal app profile until they also switch the app
manually. Explaining that exception requires pin and manual-source concepts that do
not help the user choose a data destination.

## Goals

- Keep the app profile aligned with every exact profile transition observed in EFT
  logs.
- Apply the same automatic-switch rule from all three current profiles.
- Preserve the current selection when evidence is unknown or incomplete.
- Make an automatic transition noticeable without adding persistent text or changing
  the selector's width.

## Non-Goals

- Inferring a profile from unknown future tokens or opaque EFT profile ids.
- Changing the three stored profile ids or copying progress between them.
- Repairing the async ownership and reload-ordering findings tracked as SPA-1 and
  SPA-2.
- Treating an automatic profile change as proof that every subsequently imported
  quest event belongs to that profile.

## Requirements / Acceptance Criteria

- R1: Exact `Pve` evidence selects PvE Zone from PvP Zone, PvE Zone, or PvP Season.
- R2: Exact `Regular` or compatible exact `Pvp` evidence selects PvP Zone from any
  current profile.
- R3: Exact `PvpSeason` evidence selects PvP Season from any current profile.
- R4: Unknown, malformed, or partial evidence does not change the selected profile.
- R5: A manual choice applies immediately, but a later exact known hint may replace it
  regardless of which profile was chosen.
- R6: The selector does not describe PvP Season as pinned and does not persist
  Manual, Auto, or Pinned source text. An applied automatic change may use a short
  visual cue and an accessible announcement without moving adjacent controls.

## Product Decisions

**Known profile evidence wins symmetrically.** The selected profile represents the
active EFT data destination, so an exact known hint has the same meaning whether the
app currently shows PvP Zone, PvE Zone, or PvP Season. A season-only override was
rejected because it can preserve a destination that no longer matches EFT.

**Only uncertain evidence preserves the current selection.** The parser already
requires a complete known token and classifies `PvpSeason` separately from PvP game
rules. Unknown or partial input remains the safe fallback; known `Pve` and `Regular`
input is not treated as ambiguous merely because the current app profile is seasonal.

**Selection source is transient feedback, not a lasting mode.** Manual describes an
input event, while pinned describes a future transition rule. Neither belongs beside
the three destination choices. A brief signal cue after an automatic transition
provides confirmation without asking users to learn a second state model.

## Risks

- The three tokens are evidenced by one account and EFT client build
  `1.1.0.0.46657`. A later client can change its contract; unrecognized input safely
  leaves the current profile unchanged until a new capture updates the parser.
- A future client could reuse a currently known token with different semantics. No
  local policy can identify that change without new evidence, so versioned captures
  and parser fixtures remain the guardrail.
- More automatic transitions expose the existing SPA-1 and SPA-2 timing defects more
  often. Those defects retain their separate remediation scope.
