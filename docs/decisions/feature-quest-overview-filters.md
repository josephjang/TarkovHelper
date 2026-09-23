# Quest Overview & Filters — PRD

- **Created**: 2026-08-03

> The sibling `feature-quest-overview-filters.spec.md` holds the technical design.
> Write this on the work's branch and merge it in the same PR as the work. Nothing is
> kept current: fields are written once, discoveries are appended. A later change
> that reverses a decision here appends `Superseded by <doc>` below this line, in
> the PR that reverses it.

> Superseded by `feature-quest-chip-only-status-filter.md` (2026-08-07), in part:
> the status combo is removed and the chips — plus a new leading All chip — are
> the only status filter. R2's "the status combo and the chips always agree", the
> "dedicated All chip rejected as noise" decision, R1's five-chip row with its
> "N/A" label, and the stated rationale for the localization Non-Goal ("localizing
> one without the other would make the bar disagree with the combo") no longer
> hold. Everything else here stands.

> Superseded in part by `2026-09-22-remove-quest-recommendations.md` (2026-09-22):
> the recommendations panel is removed, so R4 no longer persists an expander state
> and R6 no longer covers recommendation rows. A stored expander row in an existing
> user database is left in place and reads as nothing. Every other decision here
> stands.

## Summary

The Quest tab's read-only statistics line becomes a row of clickable status chips,
the quest list gets a zero-results empty state with an explicit reset button, and
the filter bar (plus the detail-panel width and the recommendations expander)
survives an app restart. Search input is debounced, and truncated quest names
reveal their full text via tooltips.

This implements proposals P4 (and parts of the polish items) from the session's
flow-driven Quest-tab UX review; P2 (complete-cascade confirmation) ships
separately as `feature-quest-complete-cascade-confirm.md`.

## Problem

The statistics line ("Lv.23 | Showing 214 of 247 quests | Active: 12 | …") is a
long string the user can only read, while the numbers it shows are exactly the
filters the user wants to click. When filters match zero quests the list is simply
blank, with no explanation and no way out but hunting through six filter controls.
Every filter resets on app restart — players re-select their trader/map/status
every session, while the Map tab already remembers ~45 view-state values. Long
quest names are cut off with an ellipsis and cannot be read at all, and each
keystroke in the search box re-filters the entire list synchronously.

## Goals

- The per-status numbers are controls, not text: clicking one filters the list.
- Zero results explain themselves and offer a one-click reset.
- The quest tab looks the way the player left it after a restart.
- Typing in search stays smooth; truncated text is always readable somewhere.

## Non-Goals

- Localizing the existing English filter-bar chrome. The chips mirror the status
  combo's hardcoded-English labels; localizing one without the other would make
  the bar disagree with the combo. A full Quest-tab localization pass is a
  separate effort (proposal P5 of the same UX review).
- Persisting the search text (see Product Decisions).
- Changing the list's sort order or the navigation filter-preservation rules —
  `feature-quest-unlock-sort.md` and
  `feature-preserve-quest-filters-on-navigation.md` stay authoritative.

## Requirements / Acceptance Criteria

- R1: The statistics bar shows one chip per status (Active, Locked, Done, Failed,
  N/A) with the number of quests the list would show if that chip were clicked,
  with all other filters applied as-is.
- R2: Clicking a chip applies that status filter; clicking the already-selected
  chip returns the filter to "All". The status combo and the chips always agree.
- R3: When the filters match zero quests, the list area shows "no quests match"
  text and a Reset Filters button; pressing it restores a populated list (status
  "All", search cleared).
- R4: Kappa/Item-Req checkboxes, trader, map, status, detail-panel width, and the
  recommendations-expander state survive an app restart. Search text does not.
- R5: Typing in the search box applies the filter after a short pause instead of
  once per keystroke.
- R6: Hovering a truncated quest name (list rows, recommendation rows) shows the
  full text in a tooltip.

## Product Decisions

**Chip counts are click-previews.** Each count is computed with every other filter
applied as-is — the number IS what clicking the chip shows. The alternative
(global per-status totals, what the old stats line showed) was rejected because
the numbers would stop matching the click result as soon as any other filter is
active. Consequence, accepted: counts need not sum to a fixed total (an
other-faction quest counts only under N/A, mirroring the list's own behavior).

**Clicking the selected chip returns to "All", not to the "Active" default.**
The gesture reads as "remove this status filter", and "All" is the state with
that filter removed. A dedicated All chip was rejected as noise — the combo
still offers "All" explicitly.

**Search text is deliberately not persisted.** A restored search would silently
empty the list at the next launch with the cause hidden in a small text box —
the exact failure mode the empty state exists to prevent. Restart is the one
reset a player expects to clear a query.

**The empty-state reset lands on "All".** It reuses the same reset as the
detail panel's "Show in list" button, whose landing on the most-permissive
"All" (not the session-start "Active") is already a recorded decision — two
reset buttons behaving differently would be worse than the asymmetry.

**New user-facing strings are localized (EN/KO/JA) from the start** (empty-state
texts, reset button), even though the surrounding chrome stays English until the
full localization pass — new strings never add to the hardcoded-English debt.

## Risks

- Chip counts that do not sum to the total can look wrong at a glance. Accepted:
  each number is truthful for its own click, which is the promise the control
  makes.
- A persisted trader/map value can vanish after a database update; the filter
  falls back to "All" silently. Accepted: the alternative (an error state for a
  stale dropdown value) is heavier than the harm.

## Appended: scope of the trader/map fallback (2026-08-04)

Recorded after a deep review of the implementing commit. The accepted risk above covers
only a trader or map that genuinely no longer exists in the new database. As first
shipped it was wider than intended: *every* database refresh dropped the selection, so a
trader that still existed also fell back to "All" mid-session — and, once the filter bar
became persistent, that widened value was written over the saved one. That was a defect,
not this accepted risk, and it is fixed (see the sibling spec). The silent fallback stands
as accepted only for values that really are gone.
