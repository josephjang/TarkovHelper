# Freeze the v2026.7.0 Data Endpoint - Technical Spec

- **Created**: 2026-08-24

> The sibling `fix-freeze-v2026-7-data-endpoint.md` holds the product decision.
> Write this on the work's branch and merge it in the same PR as the work.
> Nothing is kept current: fields are written once, discoveries are appended. A
> later change that reverses a decision here appends `Superseded by <doc>` below
> this line, in the PR that reverses it.

## Summary

Restore `TarkovHelper/Assets/tarkov_data.db` and `db_version.txt` to the exact
v2026.7.0 seed, then remove database mirroring to that directory from
`DataPublishService`. Keep `data/v1` as the channel-aware endpoint and keep its
`db_version.txt` solely as the source for the local bookmark bundled into a
release.

## Current Behavior / Root Cause

Tag v2026.7.0's `DatabaseUpdateService.VERSION_URL` and `DATABASE_URL` point at
raw main under `TarkovHelper/Assets/`. The current `DataPublishService` treats
those paths as a byte-identical mirror of `data/v1`, and
`DataChannelMirrorTests` enforces that equality. The 1.1.0 publish therefore
replaced both endpoint pairs. Adding a minimum app version to the new manifest
cannot help because v2026.7.0 never requests or parses that manifest.

The new client fetches only `data/v<N>/manifest.json` and the database named
inside it. Its local `Assets/db_version.txt` is not a remote endpoint: the client
loads it at startup and rewrites it only after a verified database swap.
`TarkovHelper.csproj` currently sources that installed bookmark from
`data/v$(TarkovDataFormatVersion)/db_version.txt`.

## Design

- Restore the legacy Assets pair to version 1.0.10 and the exact database bytes
  from tag v2026.7.0.
- `DataPublishService` publishes databases, manifests and bookmark seeds only to
  the highest `data/v<N>/` directory. It continues publishing maps and icons to
  Assets because those are release assets, not hot-update endpoints.
- Remove mirror state and repair behavior from `ComparisonResult` and
  `DataPublishWindow`; the window names only the versioned database target.
- Replace the repository equality guard with an exact legacy freeze guard. It
  pins version 1.0.10, size 6,889,472 and SHA-256
  `e2854c6af84093f95d45e40cdada74d2cf82714a457d4f5a3f1e46f5255a22cc`.
- Keep the csproj seed wiring unchanged: the build copies both
  `data/v<N>/tarkov_data.db` and `data/v<N>/db_version.txt` into output Assets.
  The committed legacy Assets files are removed from the default item glob, so
  they are served from raw main but never enter a channel-aware release.

## Technical Decisions

**Freeze the only URL the old binary understands.** A new manifest field cannot
gate an old binary that never reads it. Restoring and pinning the hardcoded
legacy URL is the only server-side control available for v2026.7.0.

**Keep the channel's text version seed rather than package the manifest as local
state.** `manifest.json` is an atomic remote offer containing version, payload
name, digest and size. The local bookmark is a single token changed only after a
verified swap. Replacing it would add parsing, write and migration paths while
retaining two logically different states with the same filename. The text file
is redundant as a remote channel document, but it is committed beside the seed
so MSBuild can copy the correct initial local token without generating files.

## Test Strategy

- **Repository guard**: assert the legacy token, database size and digest exactly,
  and assert the channel-aware seed still matches `data/v1`.
- **Publisher unit tests**: publish format 1 data and assert both legacy files are
  byte-for-byte unchanged; keep the manifest, index and constraint tests green.
- **Legacy smoke**: run v2026.7.0 against raw main and observe version 1.0.10.

## Verification

- `dotnet build TarkovHelper.sln -c Release`
- `dotnet test TarkovHelper.sln -c Release --no-build --filter "Category!=E2E"`
- Fetch both raw legacy files and compare their token, size and digest to the
  pinned values after merge.

## Risks & Migration

Raw GitHub caches files independently, so the restored token and database may be
observed at different times immediately after merge. The old client may waste a
download during that interval, but repeated equality checks converge on 1.0.10.
No user-data migration is required. Reverting this fix means restoring publisher
mirroring and the equality guard, which would again expose every `data/v1`
publish to v2026.7.0.
