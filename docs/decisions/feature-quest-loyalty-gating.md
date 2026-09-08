# Quest Loyalty Gating - PRD

- **Created**: 2026-09-07

> The sibling `feature-quest-loyalty-gating.spec.md` holds the technical design.
> Write this on the work's branch and merge it in the same PR as the work. Nothing
> is kept current: fields are written once, discoveries are appended. A later
> change that reverses a decision here appends `Superseded by <doc>` below this
> line, in the PR that reverses it.

## Summary

Phase 4 of the EFT 1.1 adaptation roadmap (`feature-eft-1-1-roadmap.md`). The
data the app has shown since the 1.1 refresh already says which trader loyalty
level each quest needs; the app does not read it. This phase makes loyalty a
real gate: the profile drawer gains one loyalty input per trader, per profile;
a quest whose loyalty requirement is not met shows as locked with a badge that
names the level; the detail pane lists each loyalty requirement against the
value entered; and the complete profile reset clears the entries with the rest
of the profile. The same release carries the audit of the app's hard-coded
input bounds against patch 1.1: the prestige input grows to 6 and the Scav Rep
input floor drops to -7, the rest are confirmed unchanged. No data publish is
involved; everything the phase reads was published with the 1.1 refresh.

## Problem

Since the 1.1 refresh reached every install, the app describes the right quests
with the wrong availability. Patch 1.1 dissolved most quest chains and put the
freed quests behind trader loyalty levels instead, so loyalty is now the main
thing that decides whether a quest can be taken. The app knows nothing about the
player's loyalty and treats every loyalty requirement as met. Ninety-four quests
carry such a requirement. Twenty-seven of them have no other gate a fresh
profile fails (no prerequisite, and a minimum level at or below the app's
default of 15), so a new profile sees them as available on day one although the
game locks each behind loyalty level 2; the other sixty-seven surface as
available the moment their level or prerequisite clears, again regardless of
loyalty. Five of the ninety-four need loyalty with a trader other than the one
who gives them, which no one would guess from the row. The roadmap accepted this over-showing as
the legacy view for builds without a loyalty gate; this phase is the build that
has one.

There is also no way to tell the app about loyalty. The drawer takes level, Scav
Rep, DSP count, editions and prestige, and nothing else.

Two of those inputs are also out of date. Patch 1.1 added prestige levels 5 and
6 and the app's prestige input stops at 5, so a player past that point cannot
describe their character. Fence reputation can fall to -7 in the game and the
Scav Rep input stops at -6.

## Goals

- A quest with a loyalty requirement shows as locked until the loyalty entered
  for every trader it names meets the requirement, and the row says which level
  unlocks it.
- The player enters loyalty once per trader, per profile, in the drawer, and the
  list reacts immediately, the way it does for level today.
- Starting a new season, or resetting a profile, clears the loyalty entries with
  everything else the profile owns.
- The drawer's other inputs accept the values the 1.1 game can produce.

## Non-Goals

- Deriving loyalty from level and reputation. Rejected in the roadmap: the
  thresholds shift with patches and an estimate would show wrong availability
  with confidence. Entry stays manual.
- Trader reputation conditions beyond the Scav Rep gate. The upstream data
  carries reputation conditions on twelve quests, some of them "at most"
  comparisons the app's karma gate cannot express; the 1.1 refresh declined to
  import them, and this phase adds no per-trader reputation input. Fence
  reputation stays the Scav Rep input, read from the wiki as today.
- The Collector page and the Kappa wording. Collector's seven loyalty
  requirements are gated by this phase like any other quest's, but what the
  Collector page shows about them is phase 5 (`feature-kappa-collector-1-1`).
- Loyalty on the seasonal quests. The eighteen KORD BREACH quests and one more
  wiki-only quest carry no loyalty data yet, so they stay ungated until the
  upstream API adds them and a data publish fills the rows in.
- Loyalty-aware hideout display. The hideout page already lists a module's
  trader requirements as text; it does not colour them against the entered
  loyalty in this phase. A natural follow-on once the inputs exist, recorded so
  it is not mistaken for an oversight.
- A new status chip. The chip row keeps its vocabulary; a loyalty-locked quest
  counts under Locked, as level-locked quests do today.
- Any change to the quest list's unlock ordering.
- A data publish. Nothing in this phase changes the published database.

## Requirements / Acceptance Criteria

- R1: A quest that requires loyalty with one or more traders shows as locked
  until, for every trader it names, the loyalty level entered for that trader is
  at least the level required. It counts under the Locked chip. A trader other
  than the giver counts the same way: Chemical - Part 3, given by Skier, stays
  locked until Jaeger's entered loyalty is 2, whatever Skier's is.
- R2: The row of a loyalty-locked quest carries a badge naming the level, `LL2`
  when the requirement is with the quest's own trader and the trader's name
  before it otherwise (`Jaeger LL2`). When the quest is also below the player's
  level, the level badge shows, as it does today for Scav Rep.
- R3: The detail pane's Requirements section lists each loyalty requirement with
  the trader's name, the level required and the level entered, coloured the way
  the Level line is coloured when met or unmet, in the app's language.
- R4: The profile drawer gains a Loyalty group with one input per trader that at
  least one quest in the loaded data names: seven traders today (Prapor,
  Therapist, Skier, Peacekeeper, Mechanic, Ragman, Jaeger, in that order). Each
  input offers levels 1 to 4 and takes one click to set. Trader names are shown
  in the app's language.
- R5: Loyalty is per profile. Switching profiles shows that profile's entries;
  a trader with no entry reads as level 1 in every profile. The complete
  profile reset clears the entries, and the reset dialog's list of what is
  cleared names them.
- R6: An edit in the drawer takes effect at once: quest rows, chip counts, the
  detail pane and the recommendations panel refresh, and the value is there
  after a restart.
- R7: The prestige input accepts 0 to 6 and the Scav Rep input accepts -7.0 to
  6.0. Level (1 to 79), DSP count (0 to 3) and the edition checkboxes are
  unchanged.
- R8: Quests without a loyalty requirement, the seasonal quests included, are
  shown exactly as before.
- R9: No data publish accompanies this phase, and a build that predates it keeps
  the legacy view the roadmap already accepted.

## Product Decisions

**Loyalty is set with one click per level, not a stepper.** Each trader's input
is a row of four buttons, 1 to 4, with the current level highlighted, the shape
the DSP decode count control already has. A stepper like the level control was
considered and rejected: seven traders entered by stepping is up to twenty-one
clicks for a value that then rarely changes, while the buttons make the first
entry seven clicks and every later correction one. A free text box was rejected
for the same reason the DSP control is not one: four values do not need typing.

**The trader roster comes from the data, not from a list in the app.** The
drawer shows an input for every trader that at least one loyalty requirement in
the loaded database names, which is seven traders today. Fence is not among
them: its standing is the Scav Rep input, as the roadmap decided. Ref,
Lightkeeper, BTR Driver and Survivor give quests but gate none on loyalty, and
the seasonal quests carry no loyalty rows yet. A fixed list of the seven was
rejected because the day the upstream API adds loyalty rows for the seasonal
quests, or Ref gains a gated quest, the drawer would need an app release to
grow, and until then the gate would lock quests behind a trader the player
cannot enter. Showing all sixteen traders was rejected as nine inputs nobody
uses. With the roster in the data, a data publish alone grows the drawer.

**The maximum loyalty level is four, as a constant, guarded by a test.** Every
trader with loyalty levels has exactly four in the live catalogue on the day of
writing (eight traders; Fence has reputation bands instead and the other seven
traders have a single level), the game has never had a fifth, and no published
requirement asks for more than 4. The roadmap noted that no per-trader maximum
exists in the schema and that the bound is data the phase must source; the
phase sources it and finds a constant. Publishing a per-trader maximum in the
Traders table was rejected: it needs a pipeline change and a data publish to
carry a number that has been four for every trader since loyalty existed, and
the gate itself never needs the bound, only the input does. A test over the
published data fails the build if a requirement ever exceeds four, which is the
signal to revisit.

**Every trader starts at loyalty 1.** A fresh profile, and every existing
profile on the day this release installs, reads every trader at level 1, so
every loyalty-gated quest shows as locked until the player fills the drawer
once per profile. This is the honest default the roadmap chose: under-showing
availability names the fix in the badge, over-showing it does not. The
alternative, treating a trader with no entry as ungated, keeps today's wrong
answer and defeats the phase. A one-time prompt on first launch was considered
and rejected: a modal for an input that changes a few times a season is more
interruption than the badge, which already says `LL2`, and the drawer is one
click away in the title bar.

**The badge names the trader only when it is not the giver.** Eighty-nine of
the ninety-four gated quests require loyalty with their own trader, whose
initial the row already shows, so `LL2` is complete on its own and stays as
narrow as `Lv.15`. The five quests that name another trader get the name
(`Jaeger LL2`), because `LL2` alone on a Skier quest would send the player to
raise Skier for nothing. Always naming the trader was rejected as a wider badge
on eighty-nine rows for the benefit of five; two-letter initials were rejected
because `JA LL2` reads as a code.

**The gate is uniform, Collector included.** Collector's seven loyalty
requirements are ordinary rows in the same table and the engine treats them
like any other quest's, so Collector shows as loyalty-locked once its twelve
prerequisites are done until all seven traders are entered at level 4. That is
what the game does. The roadmap spec left "whether the availability engine
gates on Collector's cross-trader conditions" to phase 5 back when those
conditions were expected to need a shape of their own; the 1.1 refresh gave
them the same table as every other loyalty requirement, so a uniform gate
answers the engine half here at no cost. How the Collector page presents the
seven, and whether it should count them in its progress, stays phase 5's
question and is not decided here.

**The bounds audit bumps two inputs and confirms the rest.** The roadmap
assigned this phase the audit of the app's hard-coded caps against 1.1. Judged
on 2026-09-07 against the upstream game data and the wiki: prestige runs from 1
to 6 (six prestige entries upstream, six New Beginning quests on the wiki), so
the prestige input's maximum moves from 5 to 6; Fence reputation runs from -7.0
to 6.0 (the reputation bands start at -7), so the Scav Rep floor moves from
-6.0 to -7.0 and the ceiling stays; the level cap is still 79 (the upstream
level table has seventy-nine entries); DSP decode count stays 0 to 3 (the
published Make Amends branches need 1, 2 and 3); and the edition values the
data carries (EOD, Unheard) are the ones the app already recognises. The two
bumps ship in this release rather than waiting for a player to hit the wall.

**This phase is an app release with no data publish.** The 1.1 refresh
published the loyalty table so that this phase would need nothing from the
pipeline, and it needs nothing: the seven traders, their names in three
languages and every requirement row are in the database every install already
has. The release is the roadmap's third; phase 5 joins it if the two finish
together, as the roadmap's release plan allows, and follows in its own release
otherwise. Data-only publishes keep flowing underneath: one that adds loyalty
rows for a new trader grows the drawer on the next start with no app change.

## Risks

- On the day the release installs, up to ninety-four quests flip from available
  to loyalty-locked in every profile until the player fills in seven values per
  profile. Accepted: the badge on every affected row names the level, the
  drawer is one click away, and the flip is the app becoming right rather than
  wrong.
- The seasonal quests and any quest whose loyalty rows upstream has not
  published yet still show as available regardless of loyalty. Accepted, as in
  the 1.1 refresh: a data-only publish closes it when the API catches up.
- The twelve quests whose upstream reputation conditions the 1.1 refresh did
  not import stay ungated by those conditions. Accepted; recorded in Non-Goals.
- Once Collector's prerequisites are done it shows as loyalty-locked until
  seven traders are entered at 4, and until phase 5 the Collector page does not
  explain why. Accepted for the gap between the two releases; the quest's own
  detail pane lists the seven requirements.
- Loyalty drops in the game when a character prestiges or a season ends. The
  seasonal reset clears the entries; a prestige does not, and the player
  re-enters by hand. Accepted: prestige is already a manual input here.
- A profile settings row written at Scav Rep -7.0 by this release reads as
  -6.0 in a build that predates it, should a player downgrade. Accepted:
  harmless, and the value is restored on the next upgrade.
