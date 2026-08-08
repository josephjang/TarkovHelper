# Seasonal Profile - PRD

- **Created**: 2026-08-08

> The sibling `feature-seasonal-profile.spec.md`, if it exists, holds the technical
> design. Write this on the work's branch and merge it in the same PR as the work.
> Nothing is kept current: fields are written once, discoveries are appended. A
> later change that reverses a decision here appends `Superseded by <doc>` below
> this line, in the PR that reverses it.

## Summary

Escape from Tarkov 1.1 introduced seasonal characters, and the app currently
records seasonal play into the permanent PvP profile: active data corruption
that worsens with every seasonal raid. This phase, the first of
`feature-eft-1-1-roadmap.md`, adds a third profile ("PvP Season") next to PvP
and PvE. Selecting it pins the app there: no automatic profile switching can pull
data back into a permanent profile. The reset action becomes a complete profile
reset ("start the new season" in one click), and the log sync range setting,
which today is silently ignored, takes effect so a fresh profile is not
re-polluted by old logs. This phase ships alone in its own release, before any
data work: it stops ongoing damage and depends on nothing upstream.

## Problem

The app decides which profile a raid belongs to by watching the game, and it
only knows PvP and PvE. A Kord Breach seasonal raid looks like ordinary PvP to
it, so seasonal quest completions are silently written into the permanent PvP
profile. The wrongness compounds: the app's primary user is playing the season
now, and every session mixes more seasonal progress into a profile that is
supposed to outlive the season. Recorded raids are worse off still: they carry
no app profile at all, so nothing distinguishes a seasonal raid from a
permanent one even in principle.

The season also starts from scratch in game, but the app has no blank slate to
offer. There is no third profile to switch to, and the reset button does not
actually produce one: it clears quest and hideout progress but leaves item
inventory, profile settings (level, reputation, faction, edition, prestige,
DSP count), and raid history behind. Even a user willing to sacrifice a
permanent profile for the season cannot get a clean start.

Finally, a quest sync always reads the entire log history, regardless of the
sync range setting in the app's settings. Even a freshly reset profile gets
its pre-reset completions re-imported by the next full sync.

## Goals

- Seasonal play is recorded in a profile of its own, isolated from PvP and PvE
  in both directions.
- Once the user has chosen the seasonal profile, no automatic behavior writes
  seasonal data into a permanent profile.
- Starting a new season is one action that yields a genuinely blank profile.
- A blank profile stays blank: log sync respects the configured range instead
  of resurrecting the entire history.

## Non-Goals

- **Per-season archived profiles.** One rolling seasonal profile, reset each
  season; decided in `feature-eft-1-1-roadmap.md`.
- **User-created profiles.** The switcher stays a fixed choice: PvE Zone,
  PvP Zone, PvP Season.
- **Season-aware content.** Quest, hideout, and item data changes belong to the
  later phases of the roadmap; this phase changes where progress is recorded,
  not what the data says.
- **Guaranteed automatic seasonal detection.** Whether the game's logs identify
  a seasonal session is an open question this phase answers by observation; the
  committed floor is manual selection that automation cannot override.
- **Backup or export before reset.** The enumerated confirmation is the guard;
  a data export feature would be its own decision.

## Requirements / Acceptance Criteria

- R1: The title-bar profile switcher offers "PvP Season" alongside the
  existing two profiles, and all three labels match the game's profile
  selection screen ("PvE Zone", "PvP Zone", "PvP Season") in each supported
  language (EN/KO/JA). Selecting it switches every page (quests, hideout,
  items, collector, raid history) to the seasonal profile's data.
- R2: Isolation holds in both directions: quest and objective progress, hideout
  progress, item inventory, and profile settings recorded under the seasonal
  profile are invisible under PvP and PvE, and vice versa.
- R3: While the seasonal profile is selected, the app never switches profiles
  on its own: a raid that would previously flip the switcher to PvP or PvE
  leaves the seasonal profile active, and that session's quest events are
  recorded under it. Manually selecting PvP or PvE restores today's automatic
  switching.
- R4: Every raid the app records from now on is attributed to the profile that
  was active when it happened, so a reset removes that profile's raids and
  leaves the other profiles' raids alone. No screen displays raid history
  today, so this is a data guarantee rather than something visible in the app;
  it exists so the reset in R5 can be scoped and a later raid-history view is
  correct by construction.
- R5: Reset Progress resets the active profile completely: quest and objective
  progress, hideout progress, item inventory, profile settings, and the
  profile's attributed raids are cleared in one action, leaving the profile as
  if freshly created. App-wide settings (language, window layout, sync options)
  and the other profiles are untouched.
- R6: The reset confirmation names the profile being reset and lists everything
  that will be cleared; declining changes nothing. The dialog is localized
  (EN/KO/JA).
- R7: Raid history recorded before the app attributed raids to profiles
  survives every reset.
- R8: The sync range setting takes effect: a full quest sync reads only logs
  within the configured window, so raids older than the window are not
  re-imported.
- R9: Updating the app changes nothing for existing PvP/PvE data until the user
  selects the seasonal profile or runs a reset; there are no migration side
  effects.

## Product Decisions

**Labels follow the game's profile selection screen.** The 1.1 profile
selection names the three characters "PvE Zone", "PvP Zone", and "PvP Season",
and shows the current season's title ("Kord Breach") separately as a subtitle.
The app adopts the same vocabulary: the new profile is "PvP Season", and the
existing PvP/PvE switcher labels become "PvP Zone" and "PvE Zone", so the
user never maps between the app's words and the game's. A generic app-invented
label ("Season") was the earlier draft and is dropped for the same reason.
Naming the profile after the season ("Kord Breach") stays rejected: the
profile is a rolling container that outlives any one season
(`feature-eft-1-1-roadmap.md` decided rolling reuse), and the game itself
treats the season name as a subtitle, not the mode name. KO/JA labels follow
the game's own localization of these terms, confirmed from the 1.1 client's
profile selection screen in each language (2026-08-08). Korean: "PvE 존",
"PvP 존", "시즌 PvP". Japanese: "PvE ゾーン", "PvP ゾーン", "PvP シーズン".
Note the Korean client reverses the word order of "PvP Season" while the
Japanese client keeps it, so the labels are per-language strings taken from
the game, not translations of one English pattern.

**Selecting the seasonal profile suspends all automatic switching.** The app
cannot tell a seasonal raid from a permanent PvP raid in the logs (as known
today), so any automation that reacts to a PvP-shaped session while the user
plays the season is a corruption path. Two alternatives were rejected.
Suppressing only the PvP-shaped detection while keeping the PvE auto-switch
reintroduces the bug it exists to fix: one detected PvE session moves the app
off the seasonal profile, and the next seasonal raid then auto-switches to PvP
and contaminates it again. A separate "season mode" toggle that redirects
PvP-shaped sessions independently of the visible profile splits "what am I
looking at" from "where does data land" into two controls that can disagree.
Full suspension gives one rule a user can hold: seasonal is manual, and while
you are there, nothing moves you. The risk direction is deliberate: with the
profile pinned, a forgotten switch writes permanent progress into the seasonal
profile, which the next season reset wipes anyway; the old behavior wrote
seasonal progress into a permanent profile, which lives forever. Prefer the
recoverable mistake.

**Whether seasonal sessions are log-identifiable is answered, not assumed.**
This phase captures logs from a real Kord Breach session (the primary user is
playing it) and records the finding in the sibling spec. If a seasonal
signature exists, automatic switching learns it: a seasonal session then
selects the seasonal profile the way PvP and PvE sessions do today, and the
pinning above becomes a safety net rather than the primary mechanism. If not,
R3's pinning is the shipped behavior and this stays a known limitation.

**Reset means "factory-new profile", uniformly for every profile.** Widening
the reset only for the seasonal profile was rejected: one button with two
meanings, and the current partial clear is a gap, not a feature. Nobody asks
to reset their progress but keep the inventory that progress earned; the
roadmap already scopes the full clear (its R4 there). The scope is structural,
"everything the profile owns", so per-profile data added by later phases
(trader loyalty levels) is covered without revisiting this decision. The
confirmation dialog enumerates the scope and, unlike today's hardcoded
Korean-plus-English text, is properly localized.

**Unattributable history is never destroyed.** Raid history recorded before
the app attributed raids to profiles has no owner on record, and since
attribution starts with this change, that is every raid recorded so far. A
reset deletes only what the profile provably owns; deleting unattributed
history would be irreversible guesswork, and assigning it all to PvP would be
a guess that quietly becomes wrong for anyone who played PvE. The consequence:
the first reset after this update deletes no raid history at all, and those
rows keep surviving later resets.

**The existing sync range setting is honored, not replaced by a hidden bound.**
An automatic bound at the profile's last reset time was considered and
declined: it is hidden state, and it would surprise a user intentionally
re-syncing further back. The range setting already exists in the settings UI
and is silently ignored on the main sync path; making it effective is the
smaller, honest fix. If season boundaries prove to need more than a day-based
window, that experience reopens this.

## Risks

- Forgetting to select the seasonal profile at season start corrupts PvP
  exactly as today: the pinning only protects after the first manual switch.
  Accepted: one manual action per season is the floor, and the log-capture
  question above may remove even that.
- Forgetting to switch back writes permanent-profile play into the seasonal
  profile. Accepted as the deliberately chosen risk direction: the next season
  reset wipes it.
- The widened reset destroys more than the old button did. Mitigated by the
  confirmation that names the profile and enumerates the scope; accepted
  beyond that, since a reset that keeps half the data is the bug this phase
  fixes.
- Historic seasonal sessions already in the log window are PvP-shaped, so a
  full sync can still re-import them into the PvP profile. Bounded by the now
  effective range setting; accepted until the seasonal-signature question is
  answered.
- The seasonal profile shows standard hideout data even though the season
  changes hideout economics (found-in-raid requirements disabled). Accepted
  v1 limitation, recorded in `feature-eft-1-1-roadmap.md`.
