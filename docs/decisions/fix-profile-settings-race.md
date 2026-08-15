# Profile Settings Race - PRD

- **Created**: 2026-08-15

> The sibling `fix-profile-settings-race.spec.md`, if it exists, holds the
> technical design. Write this on the work's branch and merge it in the same PR as
> the work. Nothing is kept current: fields are written once, discoveries are
> appended. A later change that reverses a decision here appends
> `Superseded by <doc>` below this line, in the PR that reverses it.

## Summary

Each game profile (PvP Zone, PvE Zone, PvP Season) keeps its own player
settings: level, scav reputation, faction, prestige, DSP decode count, and the
game editions owned. When a manual profile switch and an automatic one land
almost together, the settings on screen can come out as a mixture of two
profiles' values, and the mixture stays until the next switch. This change makes
the displayed settings always belong to exactly one profile, the selected one,
and makes an edit save into the profile whose value the player was editing. It
closes the last of the four services named by the SPA-2 finding.

## Problem

The app keeps a separate copy of the player settings for each of the three
profiles, and switches between them in two ways: the player picks one, or the
app notices from the game's logs which mode is being played and switches by
itself.

When those two kinds of switch happen almost at the same time, the settings
shown can end up mixed: level and scav reputation from the profile the player
just left, faction and prestige from the one just entered. Three things make
this worse than a cosmetic glitch:

- The mixture does not heal. It stays until the next profile switch, however
  long that is.
- These values drive filtering. Level, faction, prestige, editions and the DSP
  count decide which quests and items the lists show, so the lists themselves
  are wrong while the mixture stands.
- Corrections make it worse. A player who notices a wrong value and fixes it is
  editing a number that belonged to another profile, and the fix is saved into
  the currently selected one, silently overwriting a value that was correct.
  The player has no way to know what they overwrote.

The window is milliseconds wide and needs a manual and an automatic switch to
collide, so most sessions never hit it. The likeliest collision is not exotic,
though: at startup the app replays the last known game mode from the logs in
the same moment the player is free to click a profile, and the seasonal profile
has made switching a routine action rather than a rare correction.

This is the SPA-2 finding of `2026-08-seasonal-profile-amplified-issues.md`
(severity High). Of the four services that finding names, three have since been
fixed; the settings are the remainder, and they are the one place where a
player acts on the wrong values directly.

## Goals

After any sequence of profile switches, manual or automatic, the settings shown
are exactly the stored values of the selected profile: one profile's complete
set, never a mixture and never a stale set left by a slower switch.

A change the player makes to one of these settings lands in the profile whose
value was on screen, so a correction can never silently overwrite another
profile's data.

## Non-Goals

**The profile selector's own display lag.** The selector highlight is painted
from a queued callback and can briefly show the losing side of two
near-simultaneous switches. That is a separate rendering defect with no stored
data at stake, previously attempted on its own, and it stays out of this change.

**Removing the unused level-lock option.** One of the eight per-profile values
is an option to show or hide level-locked quests that no screen reads today. It
is stored per profile and travels through the same code, so it is covered by
the fix, but removing a stored user setting is its own product decision and is
not bundled into a correctness change.

**Repairing settings already overwritten.** A value that an earlier collision
caused a player to overwrite cannot be told apart from a deliberate edit
afterwards. Nothing is reconstructed, matching the decision in
`fix-profile-data-attribution.md` for progress data.

**Reworking the other services' caches.** The quest, hideout and inventory
services already guard against this race; unifying how all four caches are
built (recorded as THR-1 in `2026-08-code-health.md`) is future work and is
deliberately not started here.

## Requirements / Acceptance Criteria

**R1.** After any sequence of profile switches, manual or automatic, once
switching stops the profile-scoped settings shown (level, scav reputation,
faction, prestige, DSP decode count, editions) are the stored values of the
selected profile. No mixture of two profiles, and no complete-but-stale set
from an earlier switch, persists.

**R2.** A change the player makes to a profile-scoped setting is stored in the
profile whose value was on screen when they changed it, including in the moment
around an automatic switch.

**R3.** The quest and item lists that filter by these settings settle on the
selected profile's values after every switch, as they do today.

**R4.** Complete profile reset behaves as it does today: resetting the profile
whose settings are shown returns them to defaults immediately, except the
editions, which survive by design.

**R5.** If the stored settings cannot be read during a switch, the app shows
the new profile's defaults, never another profile's values under the new
profile's name.

## Product Decisions

### The window is closed rather than documented

Leaving the defect recorded but unfixed was a real option, since the collision
needs a manual and an automatic switch inside a few milliseconds. It was
rejected on three grounds. The failure is silent and self-sustaining: nothing
tells the player, nothing heals it, and the longer it stands the more filtered
lists and hand corrections it contaminates. The seasonal profile multiplies
both switch kinds, which is exactly why the SPA assessment flagged it. And the
other three services already carry a proven guard for the same race, so the
cost of extending it is small while an unguarded odd-one-out is where the next
report would land.

### An edit made during a switch belongs to the profile the player was looking at

When a player edits a value in the collision window, the app saves it into the
profile whose value was displayed, not into whichever profile the switch just
selected. The number on screen is the evidence for what the player meant: they
read it, judged it wrong, and corrected it. Saving into the newly selected
profile instead would record a correction derived from one profile's numbers
into another profile, which is the defect's own shape. The accepted cost is
that an edit made in that sliver of time can land in the profile the player
just left; the sliver is the same few milliseconds, and the value is at least
always a correction of the number that was actually on screen.

### The unused option rides along unchanged

The show-level-locked-quests option is stored per profile, kept current, and
read by nothing. It stays exactly as it is: carried by the fix like the other
seven values, not removed. Removal deletes a stored user setting and belongs to
a product cleanup, not a correctness fix; bundling it here would blur both.

## Risks

**The fix targets a window players cannot reliably see.** No ordinary session
can demonstrate the before/after difference on demand, so the change rides on
its automated tests rather than on a visible improvement. That is acceptable
because the failure it removes is precisely the kind a player cannot diagnose
when it does land.

**Settings can still lag the selector for a fraction of a second.** During a
switch the settings on screen may briefly be the previous profile's complete
set before the new profile's values land. What this change removes is mixtures
and stale sets that persist; the transient lag is bounded by one reload and
ends with the lists refreshing, as today.
