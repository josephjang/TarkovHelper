# Quest Data 1.1 Refresh - PRD

- **Created**: 2026-08-21

> The sibling `feature-quest-data-1-1-refresh.spec.md` holds the technical design.
> Write this on the work's branch and merge it in the same PR as the work. Nothing
> is kept current: fields are written once, discoveries are appended. A later
> change that reverses a decision here appends `Superseded by <doc>` below this
> line, in the PR that reverses it.

## Summary

Phase 3 of the EFT 1.1 adaptation roadmap (`feature-eft-1-1-roadmap.md`). The
app's quest, item, trader and hideout data is rebuilt for patch 1.1 and published
through the data channel, together with the app release that carries the new
item icons. The phase opens, as the roadmap required, with the data-source
decision: tarkov.dev's JSON API supplies the game rules (levels, loyalty,
prerequisites, Kappa, external IDs, Korean names) and the community wiki supplies
the pages, objective text and items. Three things the roadmap did not anticipate
shape the rest: the published data has carried no external quest or item IDs
since January, so log sync matches nothing today; patch 1.1 renamed about ninety
quest pages, which under the app's page-derived identity would silently detach
recorded progress; and the channel and seasonal-profile phases are merged but
unreleased, so every install in the field is still the July build. Each gets a
decision below.

## Problem

Since 2026-08-03 the app describes a game that no longer exists. Quests that 1.1
removed are still listed; quests it added are missing; about ninety quests carry
names the game no longer uses. Prerequisite chains the patch dissolved still show
quests as locked, and the Kappa gauge counts 248 quests where the game now
requires 13. Objectives are stale: Stirrup still asks for three PMC kills with a
pistol anywhere, where the game asks for ten kills of any target on Factory.

Two older defects make it worse. The database has carried no external quest IDs
since the January 2026 regeneration, so the log-based quest sync, the feature
that marks quests complete as the user plays, has matched no event for seven
months, and hideout item requirements cannot find their items or icons. And the
app's idea of which quest is which comes from the wiki page address, so when the
wiki renamed pages to follow the patch, a refresh done the old way would treat
"A Shooter Born in Heaven" and "Shooter Born in Heaven" as different quests and
the user's recorded completion would vanish from view.

## Goals

- Quests, their objectives, prerequisites, minimum levels and Kappa membership
  match the 1.1 game; new quests and items are present, removed ones are gone,
  renamed ones carry their 1.1 names.
- Progress recorded before the refresh stays attached to the same quest after
  it, renamed quests included, in every build in the field.
- Log sync matches quest events again, for every quest the game reports.
- The data carries each quest's trader loyalty requirement so the next phase can
  gate on it; nothing about loyalty changes on screen yet.
- Korean names reach every quest and item that has one upstream; hideout
  requirements show 1.1 data with their items resolved.
- New items have icons once the accompanying app release is installed.
- Builds already in the field keep working and keep receiving data.

## Non-Goals

- The loyalty gate, per-trader loyalty inputs and "LL2" badges are phase 4
  (`feature-quest-loyalty-gating`). This phase only ships the data they need.
- Collector's own 1.1 conditions (loyalty level 4 with seven traders, Fence
  reputation) and the Kappa wording pass are phase 5
  (`feature-kappa-collector-1-1`). The Collector prerequisite list keeps being
  computed from the Kappa flags, as the roadmap decided.
- No runtime icon download; icons still ship inside app releases (triggered
  backlog in `feature-eft-1-1-roadmap.spec.md`).
- No season-aware content. The eighteen KORD BREACH quests are shown in every
  profile: the wiki marks each with a "must be playing in the seasonal mode"
  requirement line, but the app has no per-profile content model, so they
  appear under PvP Zone and PvE Zone too; recorded as a risk.
- No story, side or operational task flag: neither source exposes the
  distinction as data (operational dailies and weeklies are not listed as quests
  anywhere), so the roadmap's trigger for that filter has not fired.
- No repair of objective check marks on rebalanced quests. Check marks are
  stored by position, and a quest whose objective list changed may show a tick
  on the wrong line until the user corrects it; recorded as a risk.
- No item inventory carry-over across item renames. Inventory counts are keyed
  by the item's name inside the app, so a renamed item starts at zero; the
  refresh report counts how many items this affects.
- No change to how often or how visibly data updates arrive; a running app
  shows refreshed quests after its next start, as today.

## Requirements / Acceptance Criteria

- R1: The quest list contains the quests the 1.1 game has and none it removed.
  Quests the patch renamed appear once, under their 1.1 name. (Risks names the
  two prestige quests held back by a gap upstream.)
- R2: Each quest's objectives, prerequisites and minimum level match the 1.1
  game. Stirrup shows one objective, ten kills of any target with a pistol on
  Factory, no prerequisite, and no minimum level.
- R3: The Kappa gauge total and the Collector page's quest set are the 1.1
  requirement set: Collector plus its twelve prerequisite quests.
- R4: A quest recorded as done, failed or in progress before the refresh shows
  that same state after it, including the quests the patch renamed. This holds
  on the build already in the field, not only after an app update. In
  particular a completion recorded on the quest formerly titled "Sew it Good -
  Part 4" shows on that quest under its 1.1 title, "Sew it Good - Part 2", not on
  the quest that now carries the old title.
- R5: After the refresh, a log sync marks quests that the game reports as
  started, completed or failed, on every build in the field.
- R6: Quests and items that have a Korean name upstream show it in Korean; the
  rest fall back to English, as today. Japanese keeps falling back to English.
- R7: Hideout modules show their 1.1 requirements, and item requirements show
  the item's name and icon instead of a raw identifier.
- R8: New 1.1 items appear by name as soon as the data arrives, and with icons
  once the accompanying app release is installed.
- R9: The July release keeps launching, loading every page and completing a log
  sync against the refreshed data. What it shows differs only in the way the
  roadmap accepted: freed and loyalty-gated quests render as available, because
  that build has no loyalty gate.

## Product Decisions

**Game rules come from tarkov.dev's JSON API; pages, text and items come from the
wiki.** The roadmap left the source open until the upstream state could be
judged. Judged on 2026-08-21: tarkov.dev's GraphQL endpoint is still down
(every probe since late July returns "GraphQL server unavailable"), but its
maintainers' supported replacement, the JSON API at json.tarkov.dev, is the
surface tarkov.dev itself runs on. It serves the complete 1.1 task set (517
tasks, rebuilt twice on the day of writing) with the loyalty requirement stated
per trader as structured data, the minimum level, the Kappa flag (13 quests), the
prerequisite list (296 quests now have none) and Korean names for 289 quests,
keyed by stable game identifiers. The wiki, by contrast, records loyalty in four
different free-text phrasings, has not updated its Kappa field since the patch
(the template stopped displaying it on 2026-08-03 and 246 pages still say yes),
disagrees with the game data on 11 of 28 facts checked across seven spot-check
pages (the roadmap's among them), and its overview and Collector pages are
admin-locked indefinitely, the spot-check pages until mid-September, while quest
pages are still being edited daily. So the rules that decide availability, and the
identifiers the log sync needs, come from the JSON API. The wiki stays what it
has always been for the app: the page a quest is identified by, the objective
text the user reads, the required-item tables, and the item catalogue with its
icons, none of which the JSON API can replace without the identity rewrite the
roadmap rules out. Going wiki-only was rejected because it would ship stale Kappa
data and four regexes for one fact; going tarkov.dev-only was rejected because it
changes every quest and item identity in the app, which the roadmap's non-goals
forbid, and the JSON API still lists 35 quests the game removed. Where the two
sources disagree the refresh report shows both sides for review; disagreements
are reported upstream, never patched by hand in the editor, for the same reason
the roadmap keeps Collector computed rather than curated.

**A quest ships only when both sources agree it exists, with one exception for
the current season.** The wiki's quest category now also holds 47 pages for the
separate Arena game's questline, which the JSON API does not carry and the app
has never shown, plus pages for quests the game removed; the JSON API, for its
part, still lists 35 removed quests. A quest is therefore imported only when it
has a live wiki page and a matching game record. The exception: a page the wiki
marks with its "must be playing in the seasonal mode" requirement line is
imported on the wiki's word alone, because the JSON API has not picked up the
eighteen KORD BREACH quests in any game mode and the app's primary user is
playing that season now. The cost of the rule is that a brand-new permanent
quest appears only once both sources have it, which for the four new 1.1 quests
they already do; the cost of the exception is that seasonal quests carry no game
identifier until the API adds them, so log sync cannot mark them and their
loyalty requirements are unknown until then. Once the API catches up, a
data-only publish fills both in without an app release. Two upstream defects
are bridged rather than accepted: where the API lists one page under two game
records (a BEAR and a USEC variant, or an old and a re-created record), a
fixed order of evidence picks the record the game actually uses; and where the
API points a quest at the wrong page (the three prestige quests it links to a
German title), a short alias list in the editor, in the spirit of its existing
trader and item name aliases, supplies the page. Each alias names the upstream
report it waits on. Two quests the API does not carry at all, New Beginning
(Prestige 5) and (Prestige 6), leave the app until it does; Risks records it.

**Progress carries over across the 1.1 renames, in the data itself.** Ninety-one
published quests were renamed by the patch, and eight titles now belong to a
different quest than before (the Sew it Good, Punisher and Tarkov Shooter parts
were renumbered). A refresh that keyed quests by their page, as every previous
one did, would detach the user's recorded state from all of them and, worse,
attach it to the wrong quest where a title was reused. Instead the refresh
recognises a renamed quest by its game identifier, which the 2025-12-19 database
still holds for 473 of the 488 published quests, and keeps the quest's existing
identity under its new name. That covers 91 of the 92 renamed quests; the one
without an identifier on record, No Questions Asked (now Special Order), is
bridged by hand in the one-time step that restores the identifiers, so none of
the renames loses progress. Because this is a property of the published data,
not of new app code, it protects the July build in the field as well as the
next release. A user-visible migration step, or accepting the loss and
documenting it, were both rejected: the first cannot reach the fielded build
before the data does, and the second throws away completions on a third of the
quests the user is most likely to have finished.

**Restoring external IDs is in scope, not an enrichment.** The roadmap treated
external IDs as something tarkov.dev would "land when reachable". The research
found them absent from every published quest and item since the 2026-01-14
regeneration, which means the log sync has matched nothing and hideout item
requirements have resolved nothing for seven months. The refresh restores them
for every quest and item the game knows, which is also what makes the carry-over
above possible.

**Loyalty is stored per trader, not as one number per quest.** The roadmap
expected every loyalty requirement to name the quest's own giving trader and
chose a single column. The 1.1 data contradicts it: besides Collector, four
ordinary quests require loyalty with a trader other than their giver, one of
them with five traders. A single column would silently drop those gates, and the
roadmap itself named the per-trader table as the fallback should a patch gate
ordinary quests across traders, which 1.1 did. The table is additive, builds in
the field ignore it, and phase 4 reads it instead of a column. This reverses the
roadmap spec's "column, not a table" decision; that document gets a superseded
note in this PR.

**The publish stays compatible with the July build.** Every install in the field
runs the July release, which predates the data channel, checks for data every
five minutes and installs whatever it downloads without verification. Nothing in
the 1.1 data requires a breaking change, so the refresh publishes under the
current data format and reaches those installs within minutes. The price is the
legacy view the roadmap already accepted: with no loyalty gate, freed and
loyalty-locked quests look available on the old build. The publish constraints
the spec enforces follow from that: no new prerequisite type, no faction value
other than the two factions, no removed or retyped column, no empty value in a
column the old build requires, and a quest name key equal to what the old build
computes for itself.

**Data first, release within the hour.** The app release that carries the icons
is also, necessarily, the first release of the seasonal profile and the data
channel, which have been on main since August 9 and August 16 without a tag. It
is cut immediately after the data publish merges, with nothing merged in
between, so that its bundled database and its icons match and the tests that pin
the 1.1 facts are green on the release build. Between the publish and the
user's update, the old build shows the new
items without icons; accepted, because the alternative of releasing first would
delay the corrected quest data by the length of the release process for no
user-visible gain, and the icon-less window ends at the next update prompt. This
relaxes the roadmap's rule that the channel reader be in the field before or
with the first 1.1 publish, and collapses its first two planned releases into
one: the rule existed to protect fielded builds from a breaking publish, and this
publish is additive under the constraints above, so the reader's absence for the
hour between publish and release changes nothing for those builds. Both roadmap
documents get a superseded note in this PR.

**Hideout and trader data refresh in the same pass.** The roadmap made hideout
updates conditional on tarkov.dev being reachable; it is, so the refresh rebuilds
hideout requirements and the trader list (1.1 added a sixteenth trader,
Survivor, who gives no quests yet) from the same source, and the restored item
IDs let hideout requirements show their items and icons again.

**The refresh is reviewed against a generated report, not by eye.** About every
quest row changes in this refresh (identifiers, names, levels, prerequisites,
Kappa), which no one can review in a database browser. A comparison report of the
published database against the candidate, listing every added, removed and renamed
quest, every field and prerequisite change, the title reuses, the objective
lists that changed shape, the items without icons and the disagreements between
the two sources, is the artefact reviewed before publishing, and it is attached to
the publish PR.

## Risks

- Freed and loyalty-gated quests show as available on the July build until its
  users update to a release with the loyalty gate (phase 4). Accepted in the
  roadmap; the app's update prompt is the way out.
- The eighteen seasonal quests appear in every profile, including PvP Zone and
  PvE Zone where they cannot be taken. Accepted for now; a per-profile content
  model is its own decision, and the refresh report counts them so the
  limitation is measurable.
- Until the JSON API carries the seasonal quests, they have no game identifier:
  log sync cannot mark them, and once phase 4 gates on loyalty they will show as
  available regardless of loyalty. A data-only publish closes both gaps when the
  API catches up.
- A few quests exist as two game records behind one page: a BEAR and a USEC
  variant (Drip-Out 1 and 2, Textile 1 and 2), or an old and a re-created
  record (Battery Change, Make Amends, The Price of Independence, The Huntsman
  Path - Administrator, The Tarkov Shooter - Part 5). The app stores one game
  identifier per quest, so log sync recognises only one record's events for
  each; the quests themselves stay visible to both factions as today, and the
  refresh report names every such page.
- New Beginning (Prestige 5) and (Prestige 6) leave the app until the JSON API
  carries them (it stops at Prestige 4 today); recorded progress on them is
  kept in the user's data and reappears when they return.
- The alias list that bridges the API's wrong page links is a hand-maintained
  workaround; each entry names the upstream report it waits on, and the refresh
  report flags an entry that no longer fires so it can be removed.
- Objective check marks on rebalanced quests may land on the wrong line, since
  they are stored by position and the lists changed. The quest's own state is
  unaffected, the report lists the affected quests, and the user can correct a
  tick in one click.
- The Korean-only coverage is about 56 percent of quests and Japanese names are
  still English upstream, so Japanese users see English quest names as before.
- Quests that exist in the game but have no wiki page yet (two at the time of
  writing) do not appear until the wiki adds them; the report names them.
- Renamed items, if any, lose their inventory counts; the report counts them.
- The wiki is still being curated under admin lock. Objective text and
  required-item tables parsed now may be corrected upstream later; the refresh
  is repeatable and a correction publish needs no app release.
- Between the data publish and the user's app update, new items show without
  icons on the old build. Ends at the update prompt, which the release issued
  within the hour triggers.
