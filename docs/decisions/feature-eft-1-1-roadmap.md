# EFT 1.1 Adaptation Roadmap — PRD

- **Created**: 2026-08-08

> The sibling `feature-eft-1-1-roadmap.spec.md` holds the technical design. Write
> this on the work's branch and merge it in the same PR as the work. Nothing is
> kept current: fields are written once, discoveries are appended. A later change
> that reverses a decision here appends `Superseded by <doc>` below this line, in
> the PR that reverses it.

> Superseded in part by `feature-quest-data-1-1-refresh.md` (2026-08-21): the
> release plan's first two releases (the seasonal profile alone, then the channel
> reader with the icon pack) merge into one release cut from the 1.1 data publish,
> and that publish goes out before the release because it stays additive; neither
> phase had been released when phase 3 was designed. The "trader loyalty becomes a
> first-class gate" decision stands, but its data is a per-trader requirement
> list, not one value per quest, because 1.1 gates four ordinary quests on
> loyalty with a trader other than their giver. Every other decision here stands.

## Summary

Escape from Tarkov patch 1.1.0.0 ("Kord Breach", 2026-08-03) rebuilds quest
unlocking around trader loyalty levels, shrinks the Kappa/Collector requirement
set, and introduces seasonal characters that reset every few months. Each of
those breaks an assumption this app is built on. This document is the roadmap for
the adaptation: it records the program-level product decisions, the split into
independently shippable phases, and the order they ship in. Each phase is
implemented on its own branch with its own PRD/spec pair
(`feature-seasonal-profile`, `feature-versioned-data-channel`,
`feature-quest-data-1-1-refresh`, `feature-quest-loyalty-gating`,
`feature-kappa-collector-1-1`); this roadmap merges with the PR that writes it
and carries no implementation.

## Problem

Since 2026-08-03 the app's core answer — "what can I do right now?" — is wrong.
Quests the game has unlocked through trader loyalty show as locked behind
prerequisite chains the patch dissolved, and quests the app shows as available
can be loyalty-locked in game. The Collector/Kappa progress gauge counts
hundreds of quests the patch no longer requires, so the endgame guidance
overstates the remaining work by a wide margin. A player on the new Kord Breach
seasonal character has no way to track it separately: seasonal raids are
detected as ordinary PvP sessions, so seasonal quest completions and raid
history are written into the permanent PvP profile — active data corruption that
gets worse with every seasonal session, and the app's primary user is playing
the season now. New 1.1 items are absent entirely, and once the data refreshes
they would render without icons until an app release ships them.

## Goals

- Quest availability is correct under the 1.1 loyalty-based unlock system.
- The Kappa/Collector guidance reflects the 1.1 requirement set.
- Seasonal play is tracked in a profile of its own, isolated from the permanent
  PvP/PvE profiles in both directions.
- 1.1 content is reflected: rebalanced quests, new items, hideout requirement
  changes.

## Non-Goals

- Battle Pass tracking — a separate progression system orthogonal to the app's
  quest/hideout/item helping; revisit only on real demand.
- Economy tooling — the app does not model prices, so 1.1's trader price and
  flea-fee changes change nothing here; recorded so a later reader knows it was
  considered.
- Group-shared progression UI — the app is solo-focused, and teammate-credited
  completions are expected to arrive through the same completion notifications
  as one's own (verified during the log work; see the spec's open questions).
- Spectator mode and character customization — no app surface touches them.
- Deriving trader loyalty automatically from level and reputation — an estimate
  would confidently show wrong availability whenever the in-game thresholds
  shift; manual entry is honest about what the app knows.
- Season-modifier-aware hideout display — v1 shows standard hideout data on the
  seasonal profile even though Kord Breach disables found-in-raid requirements
  for the hideout; recorded as an accepted limitation in Risks.
- Hideout pipeline redesign — 1.1's hideout changes flow through the existing
  data source unchanged once it is reachable again.

## Requirements / Acceptance Criteria

Program-level; each phase PRD refines its slice into its own acceptance
criteria.

- R1: A quest gated on trader loyalty shows as locked until the loyalty level
  entered for that trader meets the requirement, and the row says which loyalty
  level unlocks it.
- R2: The header profile drawer accepts a loyalty level per trader, per profile,
  alongside the existing level and reputation inputs.
- R3: The Collector page and the kappa gauge count only quests the 1.1 game
  requires.
- R4: A seasonal profile exists alongside PvP and PvE; switching to it shows
  only seasonal progress, and resetting it clears, in one action, everything
  the profile owns: quest progress, hideout progress, item inventory,
  per-profile settings (level, reputation, faction, editions, prestige, DSP
  count — and loyalty levels once those exist), and profile-attributed raid
  history.
- R5: Log sync works against the 1.1 client for permanent characters, and
  seasonal sessions never write into a permanent profile.
- R6: New 1.1 quests and items appear in the app with icons after the
  accompanying app release.
- R7: Hideout modules show 1.1 requirements.
- R8: Rebalanced quest details match the 1.1 game (e.g. Stirrup's new
  objective).
- R9: A data publish never crashes or otherwise breaks an app build released
  before any version-gating mechanism exists. The worst permitted effect on an
  old build is a semantic gap — data rendered through that build's older
  logic — and each publish is verified against the previous release before it
  goes out.
- R10: Each app build receives only data updates it is compatible with. When a
  publish cannot be made compatible with builds that predate the channel,
  those builds keep their last compatible data and keep working, rather than
  receiving breaking data.

## Product Decisions

**A roadmap PRD plus per-phase documents, under the unchanged docs process.**
This program is too large for one document (the process's own best practice is
1–2 weeks of work per doc) and too uncertain to specify fully up front (the
upstream data sources are still settling). So the decision record splits: this
roadmap holds the program-level decisions and the phase decomposition, and each
phase gets its own normal-sized PRD/spec on its branch. Live progress
deliberately gets no new home: as with every other change, an in-flight phase is
an open PR and a finished phase is a merged one. Two alternatives were
considered. A kept-current roadmap file (a checkbox list updated as phases land)
would reverse the append-only decision recorded in
`feature-decision-docs-process.md` and reintroduce exactly the rot that decision
exists to prevent, for the benefit of one program. A GitHub tracking issue would
keep documents clean but adds a second place to maintain, when the PR list
searched by the phase-doc names already answers "where are we". If following a
multi-phase program through PRs alone proves genuinely painful, that experience
is the trigger to revisit — this roadmap is the first data point.

**The 1.1 data source is deliberately left open; the quest-data phase decides.**
Both candidate sources are in motion: the community wiki adopted the 1.1 quest
format within two days of the patch (verified live on 2026-08-08) but its pages
are still settling under admin protection, while tarkov.dev — the API that
supplies task IDs, translations, and hideout data, and whose structured task
fields could carry the new quest semantics instead — has been unavailable since
late July (its GraphQL endpoint has an open outage issue and failed every probe
during this planning). Committing to either source today would bind the program
to a snapshot of a situation that may look different when the quest-data phase
actually starts, so the choice is recorded as open: that phase begins with the
source decision, judged against the upstream state at that time, and records it
in its own PRD/spec. What this roadmap does commit to is the consequence either
way: the pieces only tarkov.dev supplies — external IDs and KO/JA names for new
content, and hideout requirement updates — land only when its API is reachable,
whether inside the phase or as a follow-up refresh.

**Trader loyalty becomes a first-class, manually entered gate.** Loyalty is the
patch's primary unlock mechanism, so the app models it end to end: per-quest
requirement data, a per-trader loyalty input in the profile drawer, and a gate
in the availability engine. Entry is manual — one small input per
loyalty-leveled trader, changing rarely since loyalty only rises within a
season — because no automatic source exists: the game logs the app watches
carry no trader standing, and estimating loyalty from level and reputation is
rejected in Non-Goals. Which traders get an input is the loyalty phase's call:
the app's roster runs to ten, but Fence is already modeled as reputation and
Lightkeeper access is task-gated, so the roster is deliberately not pinned to
a number here.

**One rolling seasonal profile, shipped first.** Seasonal support reuses the
app's existing per-profile data isolation by adding a single seasonal profile
alongside PvP and PvE. A new profile per season (season 1, season 2, …) was
rejected: it preserves history the app's sole user does not need, at the cost of
a profile picker that grows forever. "Start the new season" is instead the
existing reset action, extended to actually clear everything the profile owns
— today it clears only quest and hideout progress, leaving item inventory,
per-profile settings, and raid history behind (R4 enumerates the full scope).
This phase ships first, before any data work: every
seasonal raid played today corrupts the permanent PvP profile, and stopping
active damage beats improving data that is merely stale. It is also the only
phase with no upstream dependency.

**Kappa/Collector stays computed, not curated.** The app derives Collector's
prerequisites from the per-quest Kappa flags, so the 1.1 rework flows through
data: refreshed flags shrink the computed set automatically, and the new
loyalty and reputation conditions extend the same computation. Hand-curating
the 1.1 Collector list in the editor was rejected — it would decouple the app
from upstream corrections and rot with every one of them.

**New-item icons ship by cutting an app release with the data publish.** The
data channel hot-updates the database, but icons only ship inside app releases,
so a data-only publish would surface the 1.1 items iconless. For this program
the two are cut together. A runtime icon-download fallback (fetching missing
icons on demand) is endorsed in principle but deferred to its own decision
document — it changes the update-channel architecture, not just this patch's
content.

**Existing installs are a publish constraint, not an afterthought.** Every
install — updated or not — pulls each data publish within minutes, and builds
already in the field cannot be taught new rules retroactively, so the program
treats "what does this data look like in yesterday's app?" as part of every
publish. The commitment is a hard minimum bar (R9): until a version-gating
mechanism exists, no publish may crash or malfunction a build already in the
field — the only degradation ever accepted on an old build is semantic, data
rendered through that build's older logic, and even that is assessed per
phase. The additive schema rule in the spec enforces the bar; a pre-publish
check against the previous release verifies it. For the 1.1 refresh, the
semantic gap means an old build shows the dissolved quest chains as immediately
available because it has no loyalty gate, which over-shows availability but
still improves on what it had (objectives, the kappa set, and new quests are
correct). Holding the data back to force app updates was rejected: it would
punish every user to spare outdated installs, and old-app-plus-old-data is not
safer, just differently wrong. The version-aware data channel that makes a
truly incompatible publish safe is its own decision below.

**A version-aware data channel, in scope now.** Additive schema changes stay
the preferred shape for every publish, but a rework of this scale may not fit
them, so the program builds the means to publish incompatible data safely:
each app build fetches only data it can understand, and builds that predate
the mechanism stay on their current endpoint, which from then on only ever
receives data compatible with them — frozen at the last compatible version
when a publish cannot stay additive, stale but working. Waiting for the first
unavoidable breaking publish to trigger this (the earlier lean) was rejected
because the mechanism only protects builds shipped after it exists — it has to
predate the need, and the 1.1 refresh is plausible as that first need. How the
channel versions its data (versioned URLs, a minimum-app-version marker, or a
manifest) is the channel phase's design question, not this document's.

**Ordering: stop corruption, then secure the channel, then fix data, then
build features.** The phase order is itself a product decision: seasonal
isolation first (active damage), the data channel second (it must be in the
field before the first 1.1 publish, and the app release that publish already
requires is its natural vehicle), the quest-data refresh third (the app's core
answer is wrong), loyalty gating fourth (it needs the refreshed data to gate
on), Kappa/Collector last (it needs both). A side-task flag — 1.1 formally
distinguishes story from side and operational tasks — is deferred until
upstream data actually exposes the distinction; if it appears, it is a small
additive filter that earns its own phase document.

**Release plan: three to four app releases carry the program.** Releases
follow the fork's existing CalVer scheme and release flow (rationale in
`feature-fork-release-process.md`); version numbers are not pinned in advance
— CalVer assigns them at cut time. The mapping: the seasonal profile ships
alone in the first release, as soon as it is ready — it stops active damage
and waits for nothing. The second release carries the channel reader and the
refreshed icon pack, and accompanies the first 1.1 data publish; the coupling
is "channel in the field before or with the publish", so if the channel is
ready well before the data, it may ship earlier in its own release rather than
holding the bundle together. The third release carries loyalty gating;
Kappa/Collector joins it if the two finish together, or follows in its own
release otherwise. After the second release, compatible data corrections (wiki
settling, spot fixes) publish data-only with no app release. The tarkov.dev
recovery re-sync is data-only too, unless it introduces new items — their
icons then need either one more icon-pack release or the runtime icon fallback
from the backlog, whichever that moment justifies.

## Risks

- Until the quest-data refresh publishes, quest details and Kappa guidance
  stay wrong — and availability stays wrong until loyalty gating lands on top
  of it. Accepted: the roadmap exists to shorten that window, and the phase
  order minimizes harm by stopping corruption first.
- The wiki is still settling — the 1.1 pages were mass-edited within two days of
  the patch and are admin-protected until mid-August, so data parsed early may
  be corrected upstream afterward. Accepted: the refresh is repeatable, and
  re-running the pipeline plus re-publishing picks up corrections without app
  changes.
- Both upstream sources may stay unreliable longer than expected — the wiki
  churning, the API down — which would delay the quest-data phase and extend
  the wrongness window. Accepted: the phase starts as soon as either source is
  usable, and the seasonal phase is independent of both.
- Users who keep running a pre-channel app version get one of two outcomes,
  depending on whether the refresh stays additive: they see the refreshed data
  through their older logic (freed chains render as available, with no loyalty
  gate to say otherwise), or their data freezes at the last compatible
  version. Accepted: both outcomes keep the build working, and the app's
  existing update prompt is the path out.
- After the patch's loyalty recalculation, players must enter their trader
  loyalty levels by hand before availability is accurate; until then,
  loyalty-gated quests sit at the conservative default (everything above
  loyalty level 1 shows locked). Accepted as the honest default — the drawer
  is one click away, and under-showing availability beats confidently
  over-showing it.
- Whether seasonal sessions are distinguishable in the game logs is unknown
  until captured. If they are not, profile switching is manual, and a user who
  forgets to switch can still cross-contaminate. The seasonal phase records the
  outcome; manual switching that the PvP auto-detection cannot silently
  override is the floor.
- v1 shows standard hideout data on the seasonal profile even though the season
  changes hideout economics; accepted for now and recorded here so the
  limitation is a known one.
