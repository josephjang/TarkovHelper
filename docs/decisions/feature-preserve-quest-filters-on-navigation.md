# Preserve Quest Filters on Navigation — PRD

- **Created**: 2026-08-01

> The sibling `feature-preserve-quest-filters-on-navigation.spec.md` holds the
> technical design. Written on the work's branch and merged in the same PR as the
> work. Nothing is kept current: fields are written once, discoveries are appended.
> A later change that reverses a decision here appends `Superseded by <doc>` below
> this line, in the PR that reverses it.

> Superseded in part by `2026-09-22-remove-quest-recommendations.md` (2026-09-22):
> the recommendations panel is removed, so R2 now covers only the quest links on
> the Items and Collector tabs. Every other decision here stands.

## Summary

Clicking any quest link — a prerequisite in the quest detail panel, a quest in the
recommendations panel, or a quest name on the Items or Collector tab — currently
wipes every filter on the Quests tab (status, search text, trader, map, Kappa-only,
item-required) to force the target quest into the visible list. This change makes
quest navigation non-destructive: filters stay exactly as the user set them, the
target quest's details are shown regardless of whether it appears in the filtered
list, and when it does not appear, a notice in the detail panel offers an explicit
"show in list" action that resets the filters — turning today's silent side effect
into a user-invoked choice.

## Problem

A player working through their quest log typically keeps the status filter on
*Active*, often narrowed further by trader, map, or a search term — that filtered
list is their working set. Prerequisites of an active quest are usually *Done* or
*Locked*, so peeking at one via its link means navigating to a quest outside the
current filter.

Today that peek destroys the working set: the status filter jumps to *All*, the
search box is cleared, and the trader, map, Kappa-only, and item-required filters
all reset. Nothing tells the user this happened — the list just becomes long again —
and there is no way back; every filter must be rebuilt by hand. The same happens
when clicking a recommended quest and, more surprisingly, when clicking a quest
name from the Items or Collector tab: filters the user set on the Quests tab are
destroyed remotely, while they are not even looking at that tab.

The cost lands on the app's core loop — checking quest details while managing an
active quest list — so it recurs many times per session.

## Goals

- Viewing a quest's details never changes what the user has typed or selected in
  the quest list filters.
- The user can still get the target quest highlighted in the list when they want
  that, at the cost of resetting filters — but only by asking for it explicitly.
- All quest-navigation entry points behave by the same rule, so the outcome of
  clicking a quest link is predictable everywhere.

## Non-Goals

- **Navigation history / back button.** A back stack that restores the previous
  quest and filter state is a larger interaction-model change; the explicit
  reveal action removes the need for it here.
- **Peek popups.** Showing quest details in a flyout instead of navigating was
  considered and declined — it adds a second detail-rendering surface to maintain.
- **Partial filter widening.** Loosening only the filters that hide the target
  keeps the destruction, just smaller and harder to predict.
- **Map page quest markers.** They select quests inside the map's own drawer and
  never touch the Quests tab; nothing changes there.

## Requirements / Acceptance Criteria

- **R1** — With any combination of status filter, search text, trader, map,
  Kappa-only, and item-required set, clicking a prerequisite quest link shows that
  quest's details and every filter control still shows the value the user set.
- **R2** — R1 holds identically for the recommendations panel and for quest links
  on the Items and Collector tabs (which also switch to the Quests tab).
- **R3** — If the target quest is visible under the current filters, it is
  selected and scrolled into view, as today.
- **R4** — If the target quest is not visible under the current filters, its
  details are still shown, no row in the list appears selected, and the detail
  panel shows a notice that the quest is hidden by the current filters, alongside
  an action to show it in the list.
- **R5** — Invoking that action resets the filters (as navigation does today),
  after which the quest is selected and scrolled into view. This action is the
  only way navigation changes filters.
- **R6** — The notice disappears when it no longer applies: when the user selects
  a quest from the list, or when a filter change makes the shown quest visible.

## Product Decisions

**Preserve filters and decouple "show details" from "show in list".** The
alternatives were the status quo (silent full reset), partial widening (change only
the filters that hide the target), auto-restore (reset, then restore filters at
some later point), and a toast with an undo. Auto-restore fails because a
master-detail page has no "navigation ended" moment to restore at; a toast keeps
the destruction and apologizes for it; partial widening is still a silent mutation,
just less predictable. Preserving filters wins because the intent of clicking a
quest link is to *read* that quest, and a read action should not mutate state the
user built by hand. The detail panel can already display a quest independently of
the list, so nothing forces the coupling.

**One rule for all four entry points.** Cross-tab navigation (Items, Collector)
has a stronger "take me there" flavor than an in-page peek, which argues for
revealing the quest in the list. It was still folded into the same rule: the
filters on the Quests tab persist while the user is on other tabs, so wiping them
from another tab is *more* surprising, not less — the user cannot even see the
state being destroyed. The notice-plus-action covers the "take me there" intent at
the cost of one extra click, and a single rule keeps every quest link predictable.

**The reveal action resets all filters rather than minimally widening them.** It
reuses the exact reset navigation performs today, so the resulting list state is
familiar and describable in one sentence ("filters reset, quest highlighted").
A minimal widening would preserve more state but make the outcome depend on which
filters happened to hide the quest.

## Risks

Users accustomed to the old behavior may expect the quest to appear highlighted in
the list after clicking a link and briefly look for it there. The notice in the
detail panel names the situation and offers the old behavior in one click, which
bounds the confusion to a single encounter.

A quest shown in the detail panel while absent from the list is a state the page
rarely produced before. Clearing the list selection (no row highlighted) plus the
notice keeps the two panes from claiming different quests are "current".
