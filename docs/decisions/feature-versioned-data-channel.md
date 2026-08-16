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
compatible with them, frozen at its last compatible version when a publish
cannot stay additive. Builds shipped from this phase on additionally learn to
tell the user when their data channel has been frozen, so staleness becomes
visible instead of silent. During normal operation nothing changes for the
user: updates stay automatic, silent, and arrive within minutes.

## Problem

Every install of the app, however old, downloads every data publish within
about five minutes of it going out, and installs already in the field cannot
be taught new rules retroactively. If a data publish ever changes the
database in a way an older app cannot read, it would reach and break every
outdated install in the field within minutes, with no way to undo the damage
selectively. The 1.1 quest-data refresh (the next phase) is plausible as the
first such publish, which is why the roadmap moved this mechanism from a
"build it when first needed" trigger into a phase of its own: by the time the
need arrives, it is exactly too late for every install already out there.

A second, smaller gap: an app whose data updates have stopped has no way to
say so. It would report "up to date" forever while its data drifts out of
date, and the user would have no signal that updating the app is the way back
to current data.

## Goals

- No app build ever downloads data it cannot understand (the roadmap PRD's
  R10, realized).
- Builds that predate this phase keep updating and keep functioning; when
  data eventually moves beyond what they can read, they keep their last
  compatible data and keep working (the roadmap PRD's R9 and R10).
- A build whose data channel has been frozen says so in the app, and points
  at the app update as the way back to current data.
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
- No change to the app self-update flow (`update.xml`, AutoUpdater); its
  design is recorded in `feature-fork-release-process.md`.
- No retroactive help for builds already in the field beyond the freeze
  policy itself. In particular they cannot show the frozen notice: no code in
  the field knows how to display it.

## Requirements / Acceptance Criteria

- R1: During normal operation, a build with the channel receives data updates
  exactly as before: automatically, silently, within about five minutes of a
  publish, with no new prompts or settings.
- R2: A fresh install works immediately from its bundled data and does not
  re-download the full database on first launch (preserving current
  behavior).
- R3: The previously released app build keeps updating and functioning
  against the restructured repository. Verified by running that actual build,
  not assumed.
- R4: When the data channel serving a build is frozen, the app shows a
  passive notice that data updates for this app version have ended and that
  updating the app restores current data. The app otherwise keeps working
  normally with its last data.
- R5: Updating the app moves the install onto the new build's data channel
  with no user action and no loss of user data or progress.

## Product Decisions

**A frozen channel is visible; silent staleness is not acceptable for new
builds.** Once a build's endpoint freezes, that build would otherwise report
"up to date" forever, which is confidently wrong in the same way the roadmap
rejects elsewhere (its Non-Goals reject estimates that would "confidently
show wrong availability"). The alternative considered was to keep the freeze
fully silent and rely on the app-update prompt alone; rejected because the
two signals answer different questions ("a new app exists" vs "your data has
stopped moving"), and the second is exactly what a tracker's user needs to
know to stop trusting stale availability. The notice is passive, a status
line rather than a dialog, because a frozen build still works and nagging
would punish a user who has a reason not to update yet. Builds that predate
this phase cannot be reached at all; their silent staleness is already
accepted in the roadmap PRD's Risks, with the existing app-update prompt as
the path out.

**The channel ships before it is needed, even though it changes nothing
visible.** This phase's user-visible value only materializes at the first
publish that cannot stay additive, which may be phase 3 or may be later. The
roadmap already records why waiting for that moment was rejected (the
mechanism only protects builds shipped after it exists); this phase inherits
that decision and adds one consequence: because nothing visible changes now,
the release carrying the channel reader needs no user-facing announcement
beyond its release notes.

**The release vehicle stays a cut-time call.** The roadmap's release plan
already covers it: the channel reader must be in the field before or with the
first 1.1 data publish, riding the quest-data phase's app release or shipping
earlier in its own release if ready well before the data. This phase records
no new decision there.

## Risks

- A user who keeps running a pre-channel build after its endpoint freezes
  sees "up to date" while their data ages. Accepted in the roadmap PRD; the
  app's existing update prompt is the path out, and the freeze keeps the
  build working rather than breaking it.
- At the moment a freeze is declared, pre-channel builds each re-download the
  database once even though its content has not changed (the freeze marker
  changes the version stamp those builds compare). Accepted: a one-time
  download of about 7 MB per install, with no behavior change.
- Until the first breaking publish, the channel runs entirely in parallel
  with the old endpoint and its correctness is invisible to users. Mitigated
  by exercising the new endpoint on every routine publish from this phase on,
  so the first breaking publish is not the mechanism's first real run.
