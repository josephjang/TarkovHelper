# Freeze the v2026.7.0 Data Endpoint - PRD

- **Created**: 2026-08-24

> The sibling `fix-freeze-v2026-7-data-endpoint.spec.md` holds the technical
> design. Write this on the work's branch and merge it in the same PR as the
> work. Nothing is kept current: fields are written once, discoveries are
> appended. A later change that reverses a decision here appends `Superseded by
> <doc>` below this line, in the PR that reverses it.

## Summary

Database 1.1.0 is offered only through the versioned data channel used by the
next app release. The legacy endpoint hardcoded into v2026.7.0 is restored to
the database that release shipped with and stays frozen there.

## Problem

v2026.7.0 was released before the versioned data channel existed. It polls a
legacy address that was kept synchronized with the new channel, so publishing
database 1.1.0 delivered the quest refresh to v2026.7.0 before the app release
that presents it as a supported release unit. This defeats the intended release
gate and makes it impossible to hold database changes for a new app version.

## Goals

v2026.7.0 keeps using database 1.0.10. A channel-aware app can ship with and
continue updating from database 1.1.0. Future routine database publishes cannot
move the v2026.7.0 endpoint.

## Non-Goals

- No change to user progress storage or migration.
- No removal of automatic database updates from channel-aware builds.
- No removal of the installed database-version bookmark. It remains separate
  from the remote manifest because it records local mutable state.

## Requirements / Acceptance Criteria

- R1: A v2026.7.0 install polling its hardcoded URLs is offered database 1.0.10,
  not 1.1.0.
- R2: An install that already downloaded 1.1.0 through the legacy endpoint is
  offered 1.0.10 again and rolls back on its next successful check.
- R3: The next channel-aware release bundles database 1.1.0 and considers that
  bundled database current without downloading it again on first launch.
- R4: Publishing a later database changes `data/v<N>/` but leaves the legacy
  database and version token unchanged.

## Product Decisions

**The pre-channel release is frozen at its release seed.** Forward compatibility
is not enough to authorize delivering a database refresh outside the app release
that owns it. Release-scoped rollout wins for v2026.7.0; channel-aware builds keep
the data-format compatibility behavior already designed for their own endpoints.

**Installs retain a small local version bookmark.** The channel manifest says
what the server offers, while the bookmark says which verified payload is
installed. Shipping that bookmark beside the seed avoids downloading the same
7 MB database on a fresh install. Reusing the remote manifest as mutable local
state would require a client and on-disk protocol migration without changing the
user-visible outcome, so it is not part of this fix.

## Risks

Some v2026.7.0 installs may already have seen database 1.1.0. Restoring the
legacy token makes those installs roll back automatically. The master database
is read-only and user progress remains in a separate database, so the rollback
does not erase progress, though progress for quests absent from 1.0.10 may be
temporarily invisible until the app is updated.
