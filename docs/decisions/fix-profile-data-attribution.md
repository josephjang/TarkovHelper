# Profile Data Attribution - PRD

- **Created**: 2026-08-11

> The sibling `fix-profile-data-attribution.spec.md`, if it exists, holds the
> technical design. Write this on the work's branch and merge it in the same PR as
> the work. Nothing is kept current: fields are written once, discoveries are
> appended. A later change that reverses a decision here appends
> `Superseded by <doc>` below this line, in the PR that reverses it.

## Summary

Progress is stored separately for each game mode, but the app decides which mode a
new record belongs to by looking at whichever profile the user has on screen. That
is the right answer for exactly one of the four ways data arrives, so records from
game logs land under the wrong mode. This change makes every record carry its own
origin: progress the user enters by hand belongs to the profile they were looking
at, and progress read from game logs belongs to the session that produced it. It
stops the mixing from here on and does not attempt to unmix what is already
stored.

## Problem

A player who uses more than one game mode can find quest progress from one mode
recorded under another. There is no error, no warning, and afterwards no way to
tell which entries are wrong.

Three situations produce it today.

**Syncing from logs.** The sync reads every session the game still keeps logs for.
Those sessions can be a mix of PvE, permanent PvP and seasonal play. All of them
are written into whichever profile is selected at that moment. A player who opens
the app on the seasonal profile and syncs sees their PvE history appear in the
season. This happens every time, not occasionally, and syncing is how a new user
first fills the app with their history.

**Playing while the app shows a different profile.** Quests completed in a running
raid are detected live. They are recorded under the selected profile rather than
the mode of the raid. A player who has manually switched the view to compare
another mode has their raid progress written into the mode they are looking at.

**The moment right after an automatic switch.** The app switches profiles by itself
when it notices the game has started a different mode. For a short time afterwards
the screen still shows the previous mode's quests while the app already considers
the new one selected. A quest checked off in that window is recorded under the new
mode, and then vanishes from the screen once the switch finishes. The player sees
nothing recorded, while the wrong profile has gained an entry.

The permanent PvP profile carries progress accumulated over the life of an account,
so entries mixed into it are the most expensive to lose and the hardest to notice.

## Goals

Every piece of progress the app stores belongs to the game mode it actually came
from, whether it was typed in by the player or read from the game's logs.

A player never has to check which profile is selected before syncing or before
checking off a quest. Selecting a profile decides what they see, not where their
data goes.

## Non-Goals

**Raid history ownership.** Raid records identify the in-game character rather than
the app's profile, so they have the same problem from a different direction. The
fix follows the same design and ships separately.

**Changing what resetting a profile does.** Reset remains partial and stays as it is
in this change.

**Repairing progress that is already stored under the wrong mode.** This change stops
new records from being misfiled. It does not correct existing ones. See the decision
below for why, and Risks for what a player is left with.

**Recording where a piece of progress came from.** Stored progress does not say
whether a player typed it in or the app read it from a log, and this change does not
add that. Any later repair work will need it, which is noted here so a future reader
knows it was considered rather than missed.

## Requirements / Acceptance Criteria

**R1.** Syncing from logs records each quest under the game mode of the session that
produced it, regardless of which profile is on screen.

**R2.** Sync applies its result without asking the player to review individual
quests, and reports a summary: how many entries went to each profile, how many were
already up to date, and how many could not be attributed.

**R2a.** Sync still asks the player to choose when two quests are mutually exclusive
and the logs do not say which one they took. Only those choices are presented, not
the full list of changes.

**R3.** A quest event whose game mode cannot be determined from the logs is not
recorded under any profile. Its count appears in the sync summary.

**R4.** A quest completed during a running raid is recorded under the mode of that
raid, including when the player is viewing a different profile at the time.

**R5.** A quest the player checks off is recorded under the profile whose data was on
screen when they clicked, including during the moment right after an automatic
profile switch.

**R6.** A quest the player checks off stays visible after an automatic profile switch
finishes, if the switch returns to the profile they were looking at.

**R7.** The app continues to switch the visible profile by itself when it detects a
new game session, and this remains on by default. Switching changes what is shown
and nothing else.

**R8.** Sync honors the time range set in settings. When no range is set the range
behavior is unchanged.

## Product Decisions

### Attribution comes from the data, not from the selected profile

Every record carries the evidence for where it belongs. Progress the player enters
belongs to the profile whose data they were looking at. Progress read from logs
belongs to the session that produced it, which the game records in the same session
folder as the events themselves.

The alternative was to keep reading the selected profile but read it earlier and
more carefully, which is what the original defect report recommended. That closes
one of the three situations above and leaves the other two, because the selected
profile is simply not the right answer for log data no matter when it is read.

A second alternative was to attribute log data to the profile whose data is
currently loaded in memory. That is correct for hand entry and wrong for logs in
the same way, so it was rejected for the same reason.

### Automatic profile switching stays on, by default

Once attribution no longer depends on the selection, switching the visible profile
becomes a convenience with no effect on stored data. Keeping it on preserves the
behavior players already have and keeps the screen showing the mode they are
actually playing.

Turning it off by default, replacing it with a prompt, and removing it outright were
all considered. Each of them trades away a working convenience to solve a problem
this change already solves at the source. Keeping it on also narrows the reach of
the silent-write decision below, because in ordinary play the visible profile is the
one being written to.

### Writes to a profile the player is not viewing happen silently

There is no badge, toast or other signal when data lands in a profile that is not on
screen. The data is correct, and with automatic switching on this is not the normal
case, so a signal would fire mostly when nothing is wrong.

A badge on the profile selector and a toast naming the profile were both considered.
The toast risks firing mid-raid, which is the worst possible moment. The badge is
quieter but still asks the player to act on something that needs no action. The
accepted cost is that a player who has manually overridden the view, and who then
completes a quest in another mode, sees nothing happen. Requirement R2 covers the
bulk case separately: the sync summary always names the profiles it wrote to.

### Sync applies its result without a review step

Sync writes what it derived and shows a summary rather than a list of quests to
confirm one by one.

The review step exists today because the player was the last defense against wrong
attribution. Once attribution comes from the logs, the review step asks the player
to confirm a judgment they have no better information about than the app does, over
a list that now spans several profiles. Grouping the list by profile and keeping
per-group selection was the main alternative, and it is a reasonable design; it was
rejected because the reason to review has been removed rather than reduced. The cost
is that a bad sync is harder to undo, which is why the summary is a requirement
rather than a nicety.

Writing the technical design surfaced that the review step was carrying a second job.
When a quest the player finished has two mutually exclusive prerequisites, the logs
record the finished quest but not which of the two alternatives the player took
earlier. No amount of evidence in the logs answers that, so the player has to. R2a
keeps a prompt for exactly those choices. The result is narrower than today's
dialog rather than wider: the player answers the questions only they can answer, and
sees a summary for everything else.

### Events that cannot be attributed are dropped, not guessed

An event from before the first mode marker in its session folder has no evidence for
where it belongs. It is not recorded, and its count is reported.

Assigning it to a default profile was rejected. The app has already had one defect
of exactly that shape, where a value that could not distinguish permanent PvP from
seasonal play was used to pick storage and merged the two. Guessing here would
reproduce the damage this change exists to stop. Measurement on one machine found
mode markers present in every retained session folder, so dropped events are
expected to be rare.

### Progress already stored under the wrong mode is left as it is

Nothing in this change corrects existing records. A player who has mixed data keeps
it, and the app does not offer to clean it up.

A repair action was planned and then cut. It would have re-read the retained logs,
written the derived state to each profile, and offered entries the logs could not
account for as removal candidates. Three things made it a poor fit for this change.
It only reaches as far as the game still keeps session folders, measured at three
days on one machine, so it would repair a sliver and leave the rest. It cannot tell
progress a player typed in from progress read from a log, so its removal list would
include entries the player has to recognize as their own. And it is a destructive
one-time action, which needs its own confirmation design and its own testing, on top
of a change that is already large.

Keeping it would have widened this change without finishing the job it implies. It
is better as a separate piece of work, with the provenance question settled first.

Doing something smaller was considered: detecting suspicious entries and telling the
player, without offering to act. That was rejected because a detector that cannot
distinguish hand entry from log data produces a list the player cannot act on with
confidence, which is worse than saying nothing.

## Risks

**Existing mixed data stays mixed, and syncing again makes it look more complete
rather than correct.** After this change a sync writes each quest to the right
profile, but the wrong entry written by an earlier sync is still there. A quest can
end up marked done under both the mode it belongs to and the mode it was misfiled
into. The player has no way to tell which entries are the stale ones. What makes
this acceptable is that the alternative was a partial repair over a three day
window that could also delete progress the player typed in; leaving the data alone
is at least predictable. Release notes should say plainly that existing data is not
corrected.

**A bad sync is harder to undo than before, and nothing walks it back.** Removing
the review step means an error in attribution lands without a person seeing it
first, and with no repair action there is no recovery path inside the app. What
makes this acceptable is that the review step was never able to catch this class of
error anyway, since the reviewer saw quest names and no profile information. The
summary is what a player has to notice something is wrong, which is why R2 states
it as a requirement.

**A player who overrides the view sees nothing happen.** With writes to other
profiles silent, a player who manually selects one profile while playing another
completes quests and sees no change on screen. Their data is correct, but the
absence of feedback can read as the app failing to record. Automatic switching being
on by default keeps this to the case where the player has deliberately overridden
the view.
