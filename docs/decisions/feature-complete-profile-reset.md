# Complete Profile Reset - PRD

- **Created**: 2026-08-13

> The sibling `feature-complete-profile-reset.spec.md` holds the technical design.
> Write this on the work's branch and merge it in the same PR as the work. Nothing
> is kept current: fields are written once, discoveries are appended. A later change
> that reverses a decision here appends `Superseded by <doc>` below this line, in
> the PR that reverses it.

This document is the complete-reset decision that `feature-seasonal-profile.md`
deliberately deferred, resolving SPA-3, SPA-4, and SPA-6 from
`2026-08-seasonal-profile-amplified-issues.md`. It does not reverse any earlier
decision: both the seasonal PRD and `fix-profile-data-attribution.md` recorded
reset as out of their scope and pointed here.

## Summary

Resetting a profile today clears quest and hideout progress and nothing else, in a
confirmation that does not say which profile it will clear, with failures the
player never sees. With PvP Season a rolling profile, resetting stops being a rare
recovery and becomes the routine way to start a season. This change redefines the
action: one confirmed reset removes everything the chosen profile owns, either
completely or not at all, leaves every other profile and every account-wide
setting untouched, and holds afterwards, so the app's own log import cannot
quietly restore what was removed.

## Problem

A player starting a new season resets the seasonal profile and expects a fresh
character. What they get keeps last season's item inventory, player level, scav
reputation, faction, prestige, and raid records. Only quests and hideout are
cleared, and the confirmation does not enumerate even that accurately for the
app's language, nor name the profile it is about to clear. A player with three
profiles cannot tell from the dialog which one is at risk.

The reset is also not trustworthy in either direction:

- If part of the removal fails, the app reports success anyway. The screen shows
  empty progress while the stored rows survive, and a restart brings the "reset"
  data back.
- Even a fully successful reset does not hold. The game keeps several days of
  session logs, and the next sync re-imports the removed progress. Since
  attribution now files each session's events under the profile that produced
  them, old seasonal sessions land exactly in the profile that was just reset,
  regardless of what is selected on screen.

Finally, raid records cannot be reset per profile at all. They identify the
in-game character, which says nothing about which app profile the raid belonged
to, so a complete per-profile reset has nothing safe to delete.

## Goals

One action returns the chosen profile to the state of a freshly created profile
for every category of data the app stores per profile.

The player can trust the outcome. Before confirming, they see exactly which
profile is targeted and everything that will be removed, in their language. After
confirming, success means everything named was removed and failure means nothing
was.

The reset holds. Nothing the app does afterwards, syncing logs or detecting live
quest events, restores progress from before the reset.

Everything the reset does not name survives: every other profile's data, the
account facts, and app-wide settings.

## Non-Goals

- **A global reset across all profiles.** The action stays per profile; clearing
  three profiles is three resets.
- **A selective, per-category reset.** Offering "quests only" would preserve
  exactly the partial-reset trap this change removes. The old behavior of
  clearing quests and hideout while keeping items is no longer available.
- **Backup, export, or undo.** A reset is destructive and final. An export or
  archive feature (also declined by `feature-seasonal-profile.md` for per-season
  data) is a separate product decision.
- **A raid history UI.** Raid records gain an owner so reset can remove them;
  showing them remains future work.
- **Repairing rows already stored under the wrong profile.**
  `fix-profile-data-attribution.md` records why not.
- **The remaining write-ownership and reload races** (SPA-1, SPA-2) beyond the
  ordering the reset itself requires.
- **Touching the game's files.** The retained session logs are not the app's to
  delete; the reset fences its own import instead.

## Requirements / Acceptance Criteria

**R1.** The confirmation names exactly one profile, the one selected when the
dialog opened, using its localized profile label. The reset applies to that
profile even if the app switches profiles while the dialog is open.

**R2.** The confirmation enumerates every category that will be removed, in
English, Korean, and Japanese. Declining changes nothing.

**R3.** After a successful reset the profile owns no quest progress, no objective
progress, no hideout progress, no item inventory, and no raid records of its own,
and its profile values (player level, scav reputation, faction, prestige, DSP
decode count) are back to their defaults.

**R4.** Game edition ownership (EOD, The Unheard Edition), the profile selection
itself, the app language, log sync settings, window layout, and every other
profile's data are unchanged by a reset.

**R5.** The reset either completes fully or changes nothing. On failure the
player is told the reset failed and that nothing was removed.

**R6.** Syncing from logs after a reset does not restore progress from before the
reset; the sync summary counts what it skipped for that reason. Live quest
detection likewise never records an event from before the reset.

**R7.** Progress created after the reset behaves normally: a quest completed
after the reset stays recorded, and no leftover pending write from before the
reset reappears.

**R8.** If a raid appears to be in progress, the confirmation says so before the
player confirms. The reset still proceeds if confirmed.

**R9.** Raid records from before this change, which have no provable owner, are
never deleted by a profile reset.

## Product Decisions

### Reset means complete, and completeness is defined by ownership

Everything stored under the profile's key is removed; everything account-wide or
app-wide survives. The player does not need to know the app's storage taxonomy to
predict the outcome: if it belongs to this profile, it goes.

The alternative was to keep growing the current partial reset one category at a
time. That was rejected because a recurring seasonal reset with a partial
category list is a standing trap: every category the reset silently skips is data
the player believes gone. The reverse failure, wiping account-wide state, is
prevented by the same ownership rule.

### The reset is all-or-nothing, and failure is reported

Today each store clears independently, the app's memory is cleared before the
database confirms, and errors go only to the log while the player is told
success. This change makes the removal a single transaction: a failure anywhere
leaves every category intact, the app's visible state untouched, and the player
told that nothing was removed. Best-effort deletion was rejected because a
partial reset is worse than no reset: the player cannot tell which half
happened, and the app cannot either.

### Game editions survive a reset

EOD and The Unheard Edition describe what the account owns, not what a character
progressed. In the game a new season does not remove an edition, and edition
ownership changes quest availability and hideout stash levels, so silently
clearing it would corrupt quest filtering until the player noticed the unticked
box. Wiping the editions with everything else was considered for uniformity and
rejected: a reset should never ask the player to restate a fact it could not
have changed.

### Raid records: delete provable ownership, never guess

From this change on, each raid record carries the app profile of the session
that produced it, captured when the raid is first observed. A reset deletes only
records that name the target profile exactly. Records from before this change
have no owner and are never deleted (R9).

Guessing owners for legacy records, by game mode and date, was rejected: game
mode cannot distinguish permanent PvP from PvP Season, which is the exact
ambiguity that created SPA-4, and a wrong deletion is unrecoverable. The cost is
that legacy raid records survive every reset; they are invisible today and a
future raid history UI will show them unowned.

### A reset fences the app's own log import

The game retains several days of session logs. Without a fence, the first sync
after a reset re-imports the removed progress, and attribution makes this worse,
not better: old seasonal events are filed into the seasonal profile no matter
what is selected. The reset therefore records its moment for the target profile,
and log-derived events from before that moment are never applied to it again.
The sync summary counts skipped pre-reset events so the player can see the fence
working. Progress the player enters by hand is never filtered.

Accepting the re-import and documenting it was considered and rejected: it turns
"reset" into "reset until the next sync", which is precisely the broken promise
this PRD exists to remove. Deleting the game's retained logs was rejected
because the app does not own the game's files, and other tools may need them.

### The confirmation is a first-class localized dialog

The confirmation names the captured target profile with its localized label,
enumerates every category to be removed, renders in English, Korean, and
Japanese, and has three
distinct outcomes: declining changes nothing, failure names the failure and
states that nothing was removed, and success confirms completion. This replaces
the current fixed Korean-plus-English message box. A message box with localized
strings was considered and rejected: it cannot carry the structured content
(profile name, category list, raid warning) and cannot be driven by the e2e
harness, which the sibling spec needs.

### A raid in progress warns, but does not block

When the app believes a raid is running, the confirmation says so and lets the
player decide. Blocking until the raid ends was rejected: raid detection can be
stale or wrong (the app may have started mid-raid), and the player decides when
their season ends, not the raid detector. A reset during a raid is safe for
data: everything after the reset moment is new progress and is recorded
normally under its own profile.

## Risks

- **A raid running at reset time ends after it, and its record lands in the
  just-reset profile.** This is the ownership rule working (the raid ends after
  the reset moment) but can read as "the reset missed one". The raid warning in
  the confirmation is what tells the player they are resetting mid-raid.
- **Legacy raid records survive every reset.** Invisible today; if a raid
  history UI ships, players will see unowned pre-reset raids. Accepted because
  deleting without proof risks another profile's records.
- **The fence shares attribution's daylight-saving limitation.** Log timestamps
  carry no offset, so during the one repeated hour per year an event can be
  judged on the wrong side of a reset that happened inside that hour.
- **The partial reset is gone.** A player who used the old action to clear
  quests and hideout while keeping items can no longer do that; the enumerating
  confirmation is what prevents surprise. Accepted because the partial shape is
  the defect, not a feature to preserve.
- **A reset is final.** There is no undo and no backup, and the app's data
  migration tool imports between install locations, not points in time. The
  all-or-nothing guarantee and the explicit enumeration are the mitigations.
