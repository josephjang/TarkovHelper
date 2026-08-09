# Seasonal Profile - PRD

- **Created**: 2026-08-08

> The sibling `feature-seasonal-profile.spec.md` holds the technical design. Write
> this on the work's branch and merge it in the same PR as the work. Nothing is kept
> current: fields are written once, discoveries are appended. A later change that
> reverses a decision here appends `Superseded by <doc>` below this line, in the PR
> that reverses it.

## Summary

Escape from Tarkov 1.1 introduced seasonal characters, but the app offers only PvP
and PvE profiles. A seasonal session therefore looks like permanent PvP and the app
has no seasonal data view or destination.

This change adds one rolling profile, PvP Season, alongside PvP Zone and PvE Zone.
The user selects it from the title bar, every profile-aware page loads the third
profile id, and automatic PvP/PvE detection cannot move the app away while seasonal
is selected. Existing reset behavior is unchanged. Redefining the current partial,
active-profile reset as a complete profile reset is a separate product decision.

## Problem

`ProfileService` equates app profile with `GameMode`, so it can express only PvP and
PvE. Kord Breach currently reports a PvP-shaped session under the known log patterns,
which selects permanent PvP. The user cannot tell the app that seasonal progress
belongs elsewhere, and there is no third profile to inspect.

The existing profile-keyed tables can already store another `ProfileId`, so the
missing capability is identity and control rather than a new data architecture. The
main product risk is automatic switching: adding a button alone is insufficient if
the next `Pvp` or `Pve` log line can immediately select a permanent profile again.

## Goals

- Offer a seasonal profile whose stored rows are keyed separately from PvP and PvE.
- Make the visible profile selection the destination users can reason about.
- Keep PvP Season selected until the user manually leaves it, unless a real seasonal
  signature positively confirms the same selection.
- Preserve existing PvP/PvE rows and behavior on upgrade.

## Non-Goals

- **A general rewrite of profile persistence.** Late-bound async write ownership and
  stale reload ordering are tracked as SPA-1 and SPA-2 in
  `2026-08-seasonal-profile-amplified-issues.md`.
- **Changing Reset Progress.** The current action already targets the active profile
  but clears only quest, objective, and hideout progress. Defining and implementing a
  complete profile reset, including raid attribution and localized confirmation,
  requires a separate PRD/spec and is tracked as SPA-3, SPA-4, and SPA-6.
- **Fixing or optimizing log sync range.** The ignored setting and full-history file
  scan are tracked as SPA-5 and SPT-2 in the two linked assessments.
- **General persistence/test infrastructure.** Latest-wins active-profile storage,
  injectable clocks, and singleton test seams are tracked in
  `2026-08-seasonal-profile-adjacent-issues.md`.
- **Per-season archived profiles.** One rolling seasonal profile is reused each
  season; archive/export is a separate product decision.
- **User-created profiles.** The switcher remains the fixed three choices.
- **Season-aware content.** Quest, hideout, and item data changes belong to later
  `feature-eft-1-1-roadmap.md` phases.
- **Guaranteed automatic seasonal detection.** Manual selection plus pinning is the
  committed floor. Real log evidence may add positive detection in this phase.

## Requirements / Acceptance Criteria

- R1: The title-bar switcher offers PvP Zone, PvE Zone, and PvP Season. Labels match
  the game in EN/KO/JA.
- R2: Selecting PvP Season makes every existing profile-aware page use the `season`
  profile id. No existing PvP/PvE row is copied, moved, or re-keyed.
- R3: While PvP Season is selected, automatic permanent-PvP and PvE detections leave
  the selection unchanged. Manually selecting PvP Zone or PvE Zone restores current
  automatic switching behavior.
- R4: The selected profile persists across restart as `PVP`, `PVE`, or `SEASON`.
  An unknown stored value falls back to PvP Zone.
- R5: Updating the app has no data migration side effects for existing PvP/PvE
  profile tables. Until the user selects PvP Season, the visible profile and data are
  the same as before the update.

## Product Decisions

**Labels follow the game's profile selection screen.** The app uses PvE Zone, PvP
Zone, and PvP Season rather than generic PvE/PvP/Season labels. The seasonal title
such as Kord Breach is not used as the profile name because this is one rolling
container. Confirmed client labels (2026-08-08): Korean `PvE 존`, `PvP 존`,
`시즌 PvP`; Japanese `PvE ゾーン`, `PvP ゾーン`, `PvP シーズン`.

**Selecting PvP Season suspends permanent-profile auto-switching.** Suppressing only
PvP-shaped detection is insufficient: one PvE detection could move the app away,
after which the next seasonal PvP-shaped session would land in permanent PvP again.
A separate season-mode toggle was rejected because it would split what the user sees
from where data lands. The active seasonal profile is the pin.

**Positive seasonal detection remains possible.** If a real Kord Breach log contains
a stable signature, the app may automatically select PvP Season from either permanent
profile. An ambiguous PvP-shaped line never overrides an already-selected seasonal
profile. The technical model represents both outcomes without changing `GameMode`.

**Complete profile reset is a separate product decision.** The existing action is
already scoped to the active profile for the stores it clears. Expanding its meaning,
deciding every owned data category, attributing raid history, and defining legacy-row
preservation changes the reset product contract rather than merely accommodating a
third profile. SPA-3, SPA-4, and SPA-6 preserve the analysis for that later PRD/spec.

## Risks

- Forgetting to select PvP Season leaves the existing permanent-PvP contamination
  path unchanged. Manual selection is the known floor until log evidence proves a
  reliable seasonal signature.
- Forgetting to switch back writes permanent-profile play into seasonal under the
  same existing persistence behavior.
- The app inherits known async ownership and reload races. They are not newly caused
  by this feature, but more profile switching makes them more visible; see SPA-1 and
  SPA-2.
- Reset Progress remains the current active-profile, quest/objective/hideout-only
  action. The UI must not describe it as a complete seasonal reset or Start New Season.
- Historic and future raid rows remain unattributed to an app profile in this phase.
- PvP Season shows standard content even where seasonal economics differ. Content
  changes remain later roadmap work.
