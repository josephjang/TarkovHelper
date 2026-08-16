# Versioned Data Channel - PRD

- **Created**: 2026-08-16

> The sibling `feature-versioned-data-channel.spec.md` holds the technical design.
> Write this on the work's branch and merge it in the same PR as the work. Nothing
> is kept current: fields are written once, discoveries are appended. A later
> change that reverses a decision here appends `Superseded by <doc>` below this
> line, in the PR that reverses it.

## Summary

Phase 2 of the EFT 1.1 adaptation roadmap (`feature-eft-1-1-roadmap.md`). The
app's automatic data updates gain a version-aware channel: every app build
fetches only data it was built to understand, and builds already in the field
stay on their current endpoint, which from then on only ever receives data
compatible with them, ending at its last compatible version when a publish
cannot stay compatible. Builds shipped from this phase on additionally learn to
tell the user when their data updates have ended, so staleness becomes visible
instead of silent, and they verify what they download before installing it.
During normal operation nothing changes for the user: updates stay automatic and
silent.

## Problem

Every install of the app, however old, downloads every data publish, and
installs already in the field cannot be taught new rules retroactively. If a
data publish ever changes the database in a way an older app cannot read, it
would reach and break every outdated install within minutes, with no way to undo
the damage selectively. The 1.1 quest-data refresh (the next phase) is plausible
as the first such publish, which is why the roadmap moved this mechanism from a
"build it when first needed" trigger into a phase of its own: by the time the
need arrives, it is exactly too late for every install already out there.

A second gap: an app whose data updates have stopped has no way to say so. It
would report "up to date" forever while its data drifts out of date, and the user
would have no signal that updating the app is the way back to current data.

A third, found while designing the first two: nothing checks what was
downloaded. A truncated download, or a version file and database that the
serving CDN happened to cache out of step with each other, is installed as-is
and then recorded as current, so the app keeps serving wrong data and never
retries.

## Goals

- No app build ever downloads data it cannot understand (the roadmap PRD's
  R10, realized).
- Builds that predate this phase keep updating and keep functioning; when
  data eventually moves beyond what they can read, they keep their last
  compatible data and keep working (the roadmap PRD's R9 and R10).
- A build whose data updates have ended says so in the app, and points at the
  app update as the way back to current data.
- Data that fails verification is never installed, and never recorded as
  installed.
- Normal operation is unchanged: data updates stay automatic and silent, a
  fresh install works immediately from its bundled data, and updating the app
  carries the install onto the new version's channel with no user action.

## Non-Goals

- No forced app updates and no data held back to push users onto new
  versions. Rejected in the roadmap PRD: it would punish every user to spare
  outdated installs.
- No user-facing channel choice. There is nothing to pick: each build has
  exactly one endpoint it is guaranteed to understand.
- No runtime icon or asset download channel; that stays a triggered-backlog
  item in `feature-eft-1-1-roadmap.spec.md`. This channel carries exactly
  what hot-updates today: the quest/item database and its version stamp.
- No change to the app self-update flow (`update.xml`, AutoUpdater) beyond how
  often it checks; its design is recorded in `feature-fork-release-process.md`.
- No retroactive help for builds already in the field. In particular they
  cannot show the ended-updates notice: no code in the field knows how to
  display it.
- No manual data-update check. The Settings "check for updates" button still
  checks only for app updates, so restarting the app is the way to force a data
  check. Deferred to the next settings UX pass.

## Requirements / Acceptance Criteria

- R1: During normal operation, a build with the channel receives data updates
  automatically and silently, with no new prompts or settings.
- R2: A fresh install works immediately from its bundled data and does not
  re-download the full database on first launch (preserving current
  behavior).
- R3: The previously released app build keeps updating and functioning
  against the restructured repository. Verified by running that actual build,
  not assumed.
- R4: When the data updates serving a build have ended, the app says so where
  it already offers the app update, and says that updating restores current
  data. The app otherwise keeps working normally with its last data.
- R5: Updating the app moves the install onto the new build's data channel
  with no user action and no loss of user data or progress.
- R6: A download that does not match what the publisher described is discarded,
  and the app keeps the data it already had.

## Product Decisions

**Ended data updates are visible; silent staleness is not acceptable for new
builds.** Once a build's endpoint stops receiving publishes, that build would
otherwise report "up to date" forever, which is confidently wrong in the same way
the roadmap rejects elsewhere (its Non-Goals reject estimates that would
"confidently show wrong availability"). The alternative considered was to stay
fully silent and rely on the app-update prompt alone; rejected because the two
signals answer different questions ("a new app exists" versus "your data has
stopped moving"), and the second is exactly what a tracker's user needs in order
to stop trusting stale availability. Builds that predate this phase cannot be
reached at all; their silent staleness is already accepted in the roadmap PRD's
Risks, with the existing app-update prompt as the path out.

**The notice is the app-update button, not a second one beside it.** A build can
only be left behind by data that ships with a newer app, so whenever this state
occurs the update button is already on screen. Adding a separate notice would
split one situation into two things to read and leave the user's only action in
the quieter of the two. The button instead says why the update matters (current
data, rather than just a version number) and changes from the green "available"
tone to a warning one. It stays one click, with no dialog and nothing that
interrupts play, because a build in this state still works and nagging would
punish a user who has a reason not to update yet.

**Nothing is ever written to an endpoint that has stopped.** An earlier shape had
each publish mark the endpoints it was leaving behind, so builds polling them
would notice. That means editing data the design otherwise treats as finished,
and it leaves a step someone can forget at exactly the moment they are busy.
Instead the project publishes, at one stable address, which data version it
currently serves; a build compares that against its own and draws its own
conclusion. Endpoints that have stopped are never touched again, and it is now
impossible to ship a change that forgets to announce itself.

**The channel ships before it is needed, even though it changes little that is
visible.** This phase's protective value only materializes at the first publish
that cannot stay compatible, which may be phase 3 or may be later. The roadmap
already records why waiting for that moment was rejected (the mechanism only
protects builds shipped after it exists); this phase inherits that decision. The
verification work does deliver immediately, since bad downloads are possible
today.

**Data now arrives within an hour rather than within minutes.** Five-minute
polling was 288 checks a day for something that changes a few times per game
patch, and it sat below the cache window of the service that serves it, so the
extra checks could not return anything new. The check when the app starts is
unchanged and immediate, and it is the one that matters for how people actually
use the app. The app-update check moves to the same interval for the same reason.

**The release vehicle stays a cut-time call.** The roadmap's release plan already
covers it: the channel reader must be in the field before or with the first 1.1
data publish, riding the quest-data phase's app release or shipping earlier in
its own release if ready well before the data. This phase records no new decision
there.

## Risks

- A user who keeps running a pre-channel build after its endpoint stops sees
  "up to date" while their data ages. Accepted in the roadmap PRD; the app's
  existing update prompt is the path out, and the build keeps working rather
  than breaking.
- Until the first incompatible publish, the channel runs in parallel with the
  old endpoint and its protective half is invisible to users. Mitigated by
  exercising the new endpoint on every routine publish from this phase on, so
  the first incompatible publish is not the mechanism's first real run.
- With a longer poll interval and no manual data check, a user who wants data
  sooner has to restart the app. Accepted until the settings pass; the startup
  check is immediate, and the data it fetches changes a few times per patch.
