# Product Requirements: Kappa and Collector under 1.1

Technical Design: [feature-kappa-collector-1-1.spec.md](feature-kappa-collector-1-1.spec.md)

- **Created**: 2026-09-12

> The product part of a Split Change Proposal, in the form described at
> https://github.com/josephjang/change-proposal (`docs/split-proposals.md`). It
> keeps this folder's filename pairing (`name.md` + `name.spec.md`) so the
> decision-docs checks and the deliver workflow read it like any other pair;
> the two documents together are the proposal. Write it on the work's branch
> and merge it in the same PR as the work. A later change that reverses a
> decision here appends `Superseded by <doc>` below this line, in the PR that
> reverses it.

## Summary

Phase 5 of the EFT 1.1 adaptation roadmap (`feature-eft-1-1-roadmap.md`), the
last one. The 1.1 data refresh already publishes Collector's 1.1 conditions
(seven traders at loyalty level 4, Scav karma of at least 3, player level 42,
twelve quests) and shrank the Kappa set to thirteen quests; the loyalty phase
already gates Collector on all of them. What is left is what the player sees:
the Collector page, the one page built around this quest, says nothing about
whether Collector is available or what still holds it, and the app's Kappa
counts and labels still describe the pre-1.1 shape. This phase puts Collector's
status and every unlock condition on the Collector page with met and unmet
values, makes every Kappa count in the app the same number with the same
label, and retires the Kappa wording 1.1 made wrong. No data publish; an
app-only release.

## Problem

Collector is the quest the app's Collector tab exists for, and since the 1.1
refresh it is the quest with the most conditions in the game: twelve quests,
player level 42, Scav karma of at least 3, and loyalty level 4 with Prapor,
Therapist, Skier, Peacekeeper, Mechanic, Ragman and Jaeger. The app knows all
of that today. The quest tab shows Collector locked with a badge naming the
first unmet condition, and the quest's detail pane lists every condition
against the profile's values. The Collector tab shows none of it: it lists
Collector's found-in-raid items and their inventory counts (43 of the quest's
44 hand-overs, because one published row carries no item to link and so never
reaches the list, which this phase does not change), and a player who has
finished the twelve quests keeps ticking items off there while the quest tab,
one click away, says `Prapor LL4`. The loyalty phase recorded this gap as an
accepted risk "until phase 5" (`feature-quest-loyalty-gating.md`, Risks);
this is phase 5.

The Kappa numbers the app shows are right but mislabeled. The quest tab's
gauge counts the thirteen quests the data flags as required for Kappa,
Collector among them, and that is the right set. Collector's own detail pane
then says "Prerequisites: (x/13 completed)" over the same thirteen. Collector
has twelve prerequisites; the thirteenth is Collector itself. Under the old
data (248 flagged quests) nobody noticed; at thirteen, a player who has done
all twelve reads "12/13" and looks for the prerequisite they missed. The
Collector page's scope label has the mirror problem: with prerequisites
excluded its stats line says "Kappa Quests Only" while listing Collector's
items and nobody else's, which under 1.1 reads as "the thirteen", a set the
page never shows.

Some Kappa wording the roadmap flagged for this phase turned out to still be
true under 1.1 and is deliberately kept: the Kappa container is still
Collector's reward (the live Collector page says so on 2026-09-12, and the
Kappa container's own page did on 2026-08-21), so "Required for Kappa
Container" on the Kappa badge
is right, and so is the Kappa filter and the Kappa priority in
recommendations. What 1.1 did retire is the "Kappa Path" achievement,
replaced by "Dawn of a New Era" on completing Collector; the app never showed
that achievement, but it still carries two unused code paths named after it
(the technical design deals with those).

## Goals

- On the Collector page a player can see whether Collector is available and,
  when it is not, which condition holds it and how far each condition is from
  met, without leaving the page.
- Every Kappa count in the app is the same number and says what it counts.
- The Kappa and Collector wording the app shows matches the 1.1 game.

## Non-Goals

- Which quests count as Kappa, and how Collector's prerequisite list is
  built. Both come from the published data (the game's flag, and the
  synthesis the 1.1 refresh rebuilt from it, `feature-quest-data-1-1-refresh.md`);
  this phase reads them and changes neither.
- The availability gate itself. Collector is gated on its conditions by the
  loyalty phase (`feature-quest-loyalty-gating.md`, "The gate is uniform,
  Collector included"); this phase shows the gate's answer and adds no rule.
- Counting the unlock conditions as progress. The seven loyalty levels, the
  Scav karma and the player level are values the player types into the
  drawer, not progress the app tracks; they are shown as met or unmet, never
  folded into the Kappa gauge (see PD1).
- A full localization of the Collector page. The page's filter row, column
  headers and item detail are English literals from before the app's
  localization pass; this phase localizes the strings it adds or changes and
  leaves the rest, recorded in Risks.
- Achievement tracking. "Dawn of a New Era" is an achievement, and the app
  models none.
- A Kappa item planner across the twelve quests. The Collector page's
  "include prerequisites" option already lists those items; the unused code
  that once aimed at a separate planner is removed rather than revived.
- Any data publish, hideout change, or change to the quest list's chips,
  sort or filters.

## Requirements

- **R1: The Collector page states Collector's status.** The page shows the
  same status badge for Collector that the quest list shows for it in the
  same profile (`Locked`, `Lv.42`, `Rep 3`, `Prapor LL4`, `Active`, `Done`
  and so on), and it changes when the quest list's would.
- **R2: The Collector page lists every unlock condition.** Below the badge,
  one line per condition the data carries for Collector: the player level,
  the Scav karma, and one line per trader loyalty requirement, each with the
  required value and the profile's current value, coloured met or unmet the
  way the quest detail pane colours them, with trader names in the app's
  language. The lines are in the order the badge names them, so the first
  unmet line is the condition the badge shows.
- **R3: The Collector page shows the Kappa quest count and can open the list.**
  The page shows how many of the flagged Kappa quests are done out of the
  total, and a button opens the same list the Collector detail pane opens,
  each quest with its trader and whether it is done. The count on the
  Collector page and the gauge on the quest tab never differ.
- **R4: One Kappa number, one label.** Everywhere the app counts Kappa quests
  (the quest tab's gauge, the Collector detail pane, the Kappa quest list and
  the Collector page) the total is the number of quests the data flags as
  required for Kappa, Collector included, and the text calls them Kappa
  quests. No text calls the thirteen "prerequisites".
- **R5: Edits reach the Collector page at once.** Changing the player level,
  the Scav Rep or a trader's loyalty in the drawer, completing or resetting a
  quest, switching profile, and switching language each refresh the Collector
  page's badge, lines and count without leaving the page or reopening it.
- **R6: The item list keeps its content, and its scope is named honestly.**
  The Collector page lists Collector's own items, plus the items of its
  prerequisite quests when the option is on, exactly as today. The option and
  the stats line say "prerequisites", not "Kappa quests", in all three
  languages.
- **R7: Wording 1.1 kept true stays.** The Kappa badge and its "Required for
  Kappa Container" tooltip, the Kappa filter, and the Kappa priority
  recommendation keep their current text.
- **R8: App-only.** No published data changes. Builds already in the field
  are unaffected, and this build reads the same data they do.

## Product Decisions

- **PD1: Collector's unlock conditions are shown as met or unmet, never counted
  into the Kappa gauge.** Folding the seven loyalty levels, the karma and the
  level into the progress number (a 21-item gauge) was rejected: a click in
  the drawer would then move a "progress" bar, and a player who prestiges and
  loses loyalty would watch Kappa progress go backwards without failing a
  quest. Conditions are the player's own entries; quests are tracked
  progress; the gauge stays quests. Revisit if the game ever exposes trader
  loyalty in a form the app can read automatically, at which point loyalty
  would be tracked rather than typed.
- **PD2: One Kappa number app-wide: the flagged set, Collector included,
  labelled "Kappa quests".** Two alternatives were rejected. Counting twelve
  in Collector's pane (its prerequisites) and thirteen in the gauge would put
  two numbers on one thing a click apart. Dropping Collector from the count
  everywhere would make the gauge read 12/12 while the container is not yet
  earned; the thirteenth quest is the one that awards it, and the flag in the
  game's data includes it for that reason. The wording changes instead: the
  detail pane's "Prerequisites" becomes "Kappa quests", which is what the
  thirteen are. Revisit if a future publish's flag set stops including
  Collector.
- **PD3: The Collector page keeps listing Collector's items while Collector is
  locked, and explains the lock above them.** Hiding the list until Collector
  unlocks was rejected: players gather Collector's 44 hand-overs over months,
  long before level 42 or the seventh trader at 4, and the item checklist is
  the page's purpose. The status and conditions go above the list so the
  player sees both.
- **PD4: The wording pass covers what 1.1 made wrong and what this phase
  adds, and nothing else.** A broader "Kappa" audit was considered and found
  only two wrong texts (the "Prerequisites" count and the "Kappa Quests Only"
  scope) and one wrong name in dead code; the rest is still true (R7). The
  strings this phase touches ship in English, Korean and Japanese, matching
  the loyalty phase's rule that a new localized line beside an old English one
  reads as a defect; the Collector page's untouched literals are the recorded
  exception (Non-Goals, Risks). Revisit when the Collector page gets its own
  localization pass.
- **PD5: This phase is the roadmap's last, and it ships in an app release
  with no data publish.** The roadmap allowed Kappa/Collector to join the
  loyalty release or follow it. The loyalty phase is merged and, at the time
  of writing, not yet released (the last tag is v2026.8.0), so this phase
  joins that release if it is ready before the cut and follows in its own
  release otherwise; which one is decided at cut time, not here. Nothing
  here needs the pipeline: every value the Collector page shows is in the
  database every install already has.

## Risks

- Most players will see Collector locked on the Collector page for a long
  time, since twelve quests, level 42 and seven traders at 4 take a wipe to
  reach. Accepted: that is the game, and the lines say which conditions are
  still open and by how much, which is the information the page lacked.
- The upstream data disagrees with the wiki on two Collector facts: the API
  gates Collector on player level 42 and lists Postman Pat - Part 2 among its
  quests, the wiki page omits both (verified 2026-09-12). The app shows the
  API's values, as the 1.1 refresh decided for every disagreement. Accepted:
  if upstream corrects either, a data-only publish fixes the page with no app
  change; neither is patched by hand.
- In Japanese the trader names on the condition lines fall back to English
  for most traders, because the published trader table carries Japanese
  names for only three. Accepted: the same fallback the drawer and the quest
  detail pane already use; a data publish with the names fills it in.
- In Korean and Japanese the new status panel and the renamed option are
  localized while the rest of the Collector page stays English. Accepted as
  the cost of not widening this phase into a localization pass; recorded so
  the mixed look is known to be a choice.
- "Kappa Quests Only" and "Include Pre-Quest" are labels players have seen
  for a year, and both change wording. Accepted: both were wrong, and the new
  text says what the page does.
