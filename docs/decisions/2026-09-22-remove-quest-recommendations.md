# Change Proposal: Remove the quest recommendations panel

## Summary

The Quests tab's "Recommended Quests" expander, the `QuestRecommendationService`
behind it, its persisted expander state and its strings are removed. The panel
was built in 2025-12 (`smart-quest-recommendations.md`) for a game whose quests
formed long prerequisite chains and whose Kappa set was 248 quests. Under the
1.1 data three of its five scoring signals no longer distinguish anything and
the two that remain need hand-entered inventory, so the panel now shows the same
five Kappa chain heads, then eight multi-unlock quests, then an alphabetical
slice of a pool of more than two hundred Active quests. The quest list's filters,
status chips and progression sort stay as the way to choose what to play next.
No other part of the Quests tab changes, and no published data changes.

## Problem

`QuestRecommendationService.GetRecommendations` scores every Active quest into
one of five buckets, in this order, and the panel shows the top five: ready to
complete (every required item is in the tracked inventory), item hand-in only
with at least half the items owned, Kappa required, unlocks two or more quests,
and "easy" (no items required, a flat score of 40). Kappa membership and the
number of follow-ups add to every bucket's score. The design assumed the quest
graph of late 2025: a few dozen chain heads, a Kappa flag on half the quests,
and a follow-up count that told chain quests from leaf quests.

EFT 1.1 replaced most of that graph with player-level and trader-loyalty gates.
`feature-quest-data-1-1-refresh.md` records that 296 quests now have no
prerequisite and that Kappa requires 13 quests. Read-only queries on 2026-09-22
over the bundled 1.1 database (db_version 1.1.0) and the frozen v2026.7.0 copy
give the recommender's inputs before and after:

| Input the score reads | v2026.7.0 data | 1.1.0 data |
|---|---|---|
| Kappa-required quests | 248 | 13 |
| Quests with no prerequisite | 23 | 294 |
| Prerequisite rows | 794 | 216 |
| Quests that unlock two or more others | 221 | 30 |
| Active pool, fresh profile at level 15 | 33 | 224 |
| Active pool, fresh profile at level 79 and loyalty 4 | 46 | 316 |
| Quests that land in the flat "easy" bucket | 81 | 184 |

What the panel shows as a result, on a fresh profile with no tracked inventory:

- Five of the six Active Kappa quests, alphabetically (Chemical - Part 1,
  Postman Pat - Part 1, Sew it Good - Part 1, Shooter Born in Heaven, The Tarkov
  Shooter - Part 1), unchanged until those chains are done.
- Then the eight Active quests that unlock two or more others.
- Then, because every "easy" quest scores exactly 40 and the task list is loaded
  in name order, the alphabetically first no-item quests of a pool that is
  between 81 and 125 quests wide depending on level: Acquaintance, All This
  Filth..., Belka and Strelka, and so on.

The inventory-driven buckets only fire when the user maintains item counts on
the Items tab; that was true before 1.1 as well. The expander is collapsed by
default (`QuestListSettings.RecommendationsExpanded` defaults to false), so most
users see a count badge that always reads 5.

The signals were not mis-weighted; the inputs they read stopped carrying
information. Nothing on the panel can be explained to a 1.1 player as "why this
quest".

## Goals

- The Quests tab offers no "what to play next" list whose order cannot be
  explained from the 1.1 game.
- No part of the feature survives as dead code: service, panel, view model,
  strings, setting, tests, and the comments that cite it.
- A reader who finds the documents that specified the panel learns from them
  that it was removed, and why, without searching the history.

## Non-Goals

- Building a 1.1-aware replacement. The questions a 1.1 player actually has
  (what the next level or the next loyalty level unlocks, which Active quests
  share the map of the next raid, which hand-ins the stash already covers) need
  their own product decision and get their own proposal. This change makes room
  for one and presumes nothing about it.
- Changing the quest list's filters, status chips, sort, or the detail pane's
  follow-ups. `TarkovTask.LeadsTo`, `QuestGraphService` and
  `ItemInventoryService`, which the service read, keep their other consumers.
- Deleting the persisted expander row from user databases already in the field
  (D2).
- Changing published data or the data format.

## Requirements

- R1: The Quests tab has no Recommended Quests expander, and the layout row it
  occupied is gone rather than left as an empty gap.
- R2: `QuestRecommendationService`, `QuestRecommendationsPanel`,
  `RecommendationViewModel`, `QuestListSettings.RecommendationsExpanded` with
  its `questList.recommendationsExpanded` key, and the seven recommendation
  strings on `LocalizationService` (`RecommendedQuests`, `ReadyToComplete`,
  `ItemHandInOnly`, `KappaPriority`, `UnlocksMany`, `EasyQuest`,
  `NoRecommendations`) no longer exist, in all three languages.
- R3: Every refresh path that called `QuestListPage.UpdateRecommendations`
  (page load, the shared state-change sequence, the database-refresh handler,
  and `RefreshDisplay`) still performs its remaining steps: the list, the chips
  and the detail pane refresh on progress, level, karma, loyalty and database
  events exactly as before.
- R4: Navigation from a prerequisite link and from the Items and Collector tabs
  still preserves the quest-list filters. The e2e test that exercised the
  recommendation click is removed with its entry point; the other navigation
  tests stay and pass.
- R5: `LocalizationHeaderStringsTests` no longer pins the recommendation
  wording (the `KappaPriority` theory and the source-text cases that read the
  panel markup and the service); the Kappa badge tooltip and Kappa filter pins
  stay.
- R6: No comment in the solution refers to the panel or the service, including
  the ones that cite it as the passive-panel precedent
  (`TraderLoyaltyPanel`), as a live-status caller (`RenderPass`), or as the
  screen area the debug toolbox obscured (`AppEnv`, `E2ETestHarness`).
- R7: No language variant of the root README (`README*.md`: today `README.md`,
  `README.ko.md` and `README.ja.md`) lists recommendations under the Quests
  feature.
- R8: Each merged document whose recorded requirement or wording decision this
  change reverses carries a `Superseded in part by` note naming it:
  `feature-preserve-quest-filters-on-navigation.md` and its spec (R2 and the
  recommendation-click verification), `feature-quest-overview-filters.md` and
  its spec (R4's expander state, R6's recommendation-row tooltips),
  `feature-quest-loyalty-gating.md` (R6's panel refresh) and its spec (the
  status-consumer list and the passive-panel precedent),
  `feature-kappa-collector-1-1.md` and its spec (R7's Kappa priority text), and
  the archived `smart-quest-recommendations.md` (D3).
- R9: A user database that still holds the old expander key opens and behaves
  as one without it.
- R10: The solution builds with no warning that names removed code, and the
  non-e2e test suite passes.

## Decisions

- **D1: Remove the panel instead of re-scoring it for 1.1.** Re-weighting was
  considered: drop the Kappa bonus, raise the unlock threshold, rank the "easy"
  bucket by level. It was rejected because the inputs, not the weights, are
  what 1.1 took away: with 294 of 488 quests having no prerequisite, position in
  the graph no longer separates quests, and 13 Kappa quests cannot carry a
  priority scheme. Whatever a 1.1 recommender ranks on (the next level or
  loyalty unlock, the raid map, the stash) is a different feature with a
  different question to answer first, so it gets its own proposal rather than
  inheriting this service's shape. Revisit when that proposal is written; if it
  wants a scoring service, it starts from its own inputs.

- **D2: Leave the persisted `questList.recommendationsExpanded` row in existing
  user databases.** Deleting it on the next settings load was considered and
  rejected: the settings table is a key-value store that tolerates unknown keys,
  one-shot cleanup code for an inert row would outlive its purpose in every
  build after the first. The row costs nothing and reads as nothing. Revisit if
  a settings-key audit or migration step ever exists; the key would join that
  list.

  Corrected during implementation: this decision first also said the complete
  profile reset already clears the row. It does not. `QuestListSettings` keeps
  its keys in the app-wide `UserSettings` table, and
  `UserDataDbService.ResetProfileAsync` deletes only the profile's
  `ProfileSettings` rows, so the row survives a reset like every other
  `questList.*` key. The decision stands on the two reasons above; the reset
  was never needed for it.

- **D3: The archived PRD gets the supersede note too.** `archive/` is frozen in
  format and location, and a blockquote under the title changes neither. The
  archived PRD is the one document that recorded the decision to build the
  panel, and the lifecycle allows exactly this write on a merged record. Leaving
  it silent was rejected because a reader who reaches it through the archive
  would find a feature described as shipped with no hint that it is gone.
  Revisit if the archive's rules are ever tightened to forbid the note; the
  record then lives only in this proposal.

## Risks

- Users who did open the panel lose it in the same release that removes it,
  with no replacement. Accepted: what it shows is not defensible, and the
  release note says the panel is gone and why.
- The Quests tab's content shifts up by the panel's row, so the interactive code
  guides under `docs/` that show the tab still picture the panel. Accepted:
  those guides are frozen records of their PRs, not living documentation.
- Removing one step from the shared refresh sequence in `QuestListPage` must not
  reopen the shortcut `feature-quest-chip-only-status-filter.spec.md` closed
  (a second, shorter copy of the sequence). Accepted: the sequence keeps one
  definition with one fewer step, and `RefreshDisplay` still delegates to it.
