# Versioned Data Channel - Technical Spec

- **Created**: 2026-08-16

> The sibling `feature-versioned-data-channel.md` holds the product decision.
> Write this on the work's branch and merge it in the same PR as the work.
> Nothing is kept current: fields are written once, discoveries are appended. A
> later change that reverses a decision here appends `Superseded by <doc>` below
> this line, in the PR that reverses it.

## Summary

Three ideas carry the design. First, an integer **data format** identifies the
reader contract for `tarkov_data.db` (schema plus the semantics an app build
understands); the current family is format 1, and the number bumps only when a
publish cannot stay additive. Second, endpoints are **versioned URLs**: one
`data/v<N>/` directory per format on raw main, each holding the
`db_version.txt` + `tarkov_data.db` pair; the pre-channel
`TarkovHelper/Assets/` URLs, hardcoded in fielded builds, become format 1's
second address, kept byte-identical to `data/v1/` by the publish flow and a
guard test, and frozen in place the day the format moves past 1. Each app
build compiles in the format it reads from a single csproj property that also
selects its bundled seed database, so the pin, the polled URLs, and the seed
cannot skew. Third, `db_version.txt` becomes **line-oriented**: the first line
is the version token with today's exact-equality semantics, later lines are
directives; the one directive defined now, `frozen`, is how a build learns its
channel has ended and surfaces it to the user.

This settles the mechanism question `feature-eft-1-1-roadmap.spec.md`
deliberately left open (versioned URLs, a minimum-app-version marker, or a
manifest): versioned URLs win, for the reasons under Technical Decisions.

## Non-Goals

- No manifest file and no minimum-app-version marker (rejected under
  Technical Decisions).
- No delta or incremental downloads; the whole-file replace stays (the
  database is about 7 MB).
- No back-publishing to frozen formats. The pipeline produces one
  current-format database per run; the roadmap policy promises a freeze at
  the last compatible version, not parallel maintenance of old formats.
- No git automation in TarkovDBEditor: the publish commit and push to main
  stay manual and reviewed, as today.
- No change to the app self-update feed (`update.xml`, `UpdateService`,
  AutoUpdater).
- No change to what the channel carries: exactly the pair that hot-updates
  today. Map configs, SVGs, and icons keep shipping inside app releases; the
  runtime icon channel stays in the triggered backlog of
  `feature-eft-1-1-roadmap.spec.md`.

## Current Behavior

Verified in the working tree and against main before design.

- `DatabaseUpdateService` polls two hardcoded internal constants,
  `VERSION_URL` and `DATABASE_URL`, pointing at raw main
  `TarkovHelper/Assets/db_version.txt` and `tarkov_data.db`, every five
  minutes with an immediate first check (`StartBackgroundUpdates` passes
  dueTime 0). The remote version is `Trim()`ed and compared for exact string
  equality against the local `Assets/db_version.txt` next to the executable;
  any difference triggers a full database download (temp file, SQLite pool
  clearing, `.bak` swap with retries) and rewrites the local version file. A
  failed version fetch logs a warning and returns "Failed to get remote
  version" without touching local state, so a 404 on a not-yet-existing
  endpoint is non-destructive.
- `db_version.txt` holds a single opaque token (`1.0.10` at the time of
  writing); no ordering is ever computed. Rollback relies on this:
  re-publishing older content under a changed token is followed like any
  update (`feature-fork-release-process.md`).
- The same two Assets files are the build's seed: `TarkovHelper.csproj` has
  `None Update` items with `CopyToOutputDirectory` for both, `dotnet publish`
  output is what `build/Create-ReleasePackage.ps1` zips, and the bundled
  `db_version.txt` is what stops a fresh install from re-downloading the full
  database on its first check.
- Publishing is TarkovDBEditor's `DataPublishService` plus
  `DataPublishWindow`: it MD5-compares the editor's build output against
  `TarkovHelper/Assets`, copies changed files (database, map configs, SVGs,
  marker/item/hideout icons), suggests the next patch version, and writes
  `db_version.txt`. It never touches git; a human commits and pushes main,
  and installs pick the publish up within minutes.
- `UpdateServiceTests.Update_feed_constants_point_at_fork` pins both URL
  constants full-string to the fork, guarding against upstream-URL
  reintroduction.
- `DatabaseUpdateService.UpdateCheckCompleted` exists but has no consumer; DB
  updates are entirely silent in the UI today. `MainWindow` consumes only the
  app-update service's completion event and subscribes to `DatabaseUpdated`
  solely for logging (services reload themselves).
- The e2e harness disables the background checks via
  `TARKOVHELPER_DISABLE_DB_UPDATE` (`AppEnv.DisableDbUpdate`), and e2e
  expectations derive from the build-output Assets copy.
- No versioning concept exists anywhere in the channel: the roadmap spec's
  finding stands ("an exact string comparison with no minimum-app-version
  concept: every publish reaches every existing install within minutes, and
  builds already in the field cannot be retro-gated").

## Design

### Repository layout: one directory per data format

A **data format** is the contract an app build can read: the SQLite schema
plus the semantic conventions of its values. Additive changes (new columns or
tables, feature-detected on read via the `ColumnExistsAsync` pattern) do not
bump it; a change that would crash or mislead an older reader (rename,
repurpose, removal, semantic change of existing values) does. Whether a given
publish bumps is decided at that publish's review, per the roadmap's
cross-phase ground rules.

- `data/v1/db_version.txt` and `data/v1/tarkov_data.db` are created in this
  phase at the repo root, byte-identical to the Assets pair. Git stores blobs
  content-addressed, so the identical mirror adds two tree entries, not
  repository growth; the working tree grows by one ~7 MB copy.
- `TarkovHelper/Assets/db_version.txt` and `tarkov_data.db` stay committed
  solely as the pre-channel endpoint (fielded builds hardcode those URLs) and
  stop feeding builds (seed re-sourcing below). Permanent invariant: the
  Assets pair and the `data/v1/` pair are byte-identical, forever. Both are
  format 1's endpoint; they advance together and they freeze together. A
  guard test enforces it (Test Strategy).
- **Freeze procedure**, recorded now so the publish that first needs it is
  routine: the first publish that bumps the format to N+1 creates
  `data/v<N+1>/` with the new pair, stops writing every lower-format
  endpoint, and appends a `frozen` line to each lower-format endpoint's
  `db_version.txt` without changing its version token or database. Channel
  builds parse line 1, see no version change, and surface the notice.
  Pre-channel builds compare the whole string, see a change, and re-download
  the unchanged database once (harmless, accepted in the PRD's Risks).
- **Ordering at a bump**: `data/v<N+1>/` lands on main before or with the
  release tag of the app build that pins N+1. The new build's early checks
  would otherwise 404 (gracefully, but avoidably); its bundled seed already
  carries format-N+1 data either way. This mirrors the update.xml-last
  principle from `feature-fork-release-process.md`.

### Single-sourced format pin (TarkovHelper)

- `TarkovHelper/TarkovHelper.csproj` gains `<TarkovDataFormat>1</TarkovDataFormat>`.
  - The seed copy items are re-sourced: the `None Update` entries for
    `Assets\tarkov_data.db` and `Assets\db_version.txt` are replaced by
    `None Include="..\data\v$(TarkovDataFormat)\..."` items with
    `Link="Assets\..."` and `CopyToOutputDirectory=PreserveNewest`. The
    output layout is unchanged (runtime paths, packaging, and e2e
    expectations untouched); the repo Assets copies remain auto-included
    `None` items without a copy step, so there is no output collision.
  - `<AssemblyMetadata Include="TarkovDataFormat" Value="$(TarkovDataFormat)" />`
    exposes the number to code.
- `TarkovHelper/Services/DatabaseUpdateService.cs`: the two `const` URLs
  become `internal static readonly` values derived from
  `DataFormatVersion`, which is read once from the assembly metadata at type
  initialization. A missing or unparseable metadata value throws there: a
  build wired that badly must fail loudly, never fail soft onto some default
  endpoint. URL shape:
  `https://raw.githubusercontent.com/josephjang/TarkovHelper/refs/heads/main/data/v{N}/db_version.txt`
  and `.../tarkov_data.db`. Internal visibility for the guard tests stays.
- The pin, the polled URLs, and the bundled seed all derive from the one
  csproj property, so a format bump is a one-property change plus the new
  data directory, and skew between them is mechanically impossible.

### Version file parsing and the frozen directive

- Channel builds parse `db_version.txt` line-oriented: the first
  non-whitespace line, trimmed, is the version token; each later
  non-whitespace line is a directive. `frozen` is the only directive defined;
  unknown directives are ignored deliberately, so builds in the field stay
  tolerant of vocabulary added after them. An empty or whitespace-only file
  is a failed check, same as a fetch error.
- Comparison and download semantics are unchanged and apply to the token
  alone. The local `Assets/db_version.txt` stores only the token; the frozen
  state is endpoint state, not data state, and is re-derived on every check
  rather than persisted.
- `UpdateCheckResult` gains `IsEndpointFrozen`, and `DatabaseUpdateService`
  exposes the latest observed value.
- UI: `MainWindow` subscribes to the so-far-unconsumed
  `DatabaseUpdateService.UpdateCheckCompleted`; while the endpoint is frozen
  it shows a passive notice in the header area next to the existing
  app-update indicator ("Data updates for this version have ended - update
  the app"), localized EN/KO/JA via `LocalizationService.Core.cs`. No dialog,
  no toast. The notice clears if a later check reports the endpoint
  unfrozen (supported for operator error, not planned use).

### Publish flow (TarkovDBEditor)

- `DataPublishService` switches from the single Assets target to the channel
  layout for the database pair: the **live format is the highest
  `data/v<N>/` directory present in the repo**, a publish writes that
  directory, and while the live format is 1 it mirrors the identical pair
  into `TarkovHelper/Assets/`. The tool never creates a new format
  directory itself; creating `data/v<N+1>/` is a deliberate manual act in the
  reviewed PR that also teaches the app the new format, so a routine publish
  cannot bump the format by accident. Comparison, hashing, and the
  next-version suggestion read from the live format directory.
- Non-channel assets (map configs, SVGs, marker/item/hideout icons) keep
  publishing into `TarkovHelper/Assets/` subfolders as today; they ship via
  app releases and are not part of the channel pair.
- `DataPublishWindow` shows both database targets in the comparison and
  publish summaries.
- One-commit rule: a publish commit carries every endpoint copy it wrote, so
  raw main never serves a half-published mirror; the mirror guard test turns
  any slip red in CI on main (detection behind the tooling's prevention).

### Files touched

- `TarkovHelper/TarkovHelper.csproj` (format property, metadata, seed items)
- `TarkovHelper/Services/DatabaseUpdateService.cs` (derived URLs, parser,
  frozen state, internal test seam)
- `TarkovHelper/MainWindow.xaml` + `MainWindow.xaml.cs` (frozen notice)
- `TarkovHelper/Services/LocalizationService.Core.cs` (notice strings, three
  languages)
- `data/v1/db_version.txt`, `data/v1/tarkov_data.db` (new, identical to the
  Assets pair)
- `TarkovDBEditor/Services/DataPublishService.cs` (channel targets)
- `TarkovDBEditor/Views/DataPublishWindow.xaml` + `.xaml.cs` (target display)
- `TarkovHelper.Tests/UpdateServiceTests.cs` (updated full-string URL pins)
- `TarkovHelper.Tests/DataChannelTests.cs` (new: parse matrix, URL
  derivation, metadata agreement, frozen propagation)
- `TarkovHelper.Tests/DataChannelMirrorTests.cs` (new: repo-walk byte
  identity, seed-source identity)
- `TarkovHelper.Tests/DataChannelEndpointServingTests.cs` (new: local HTTP
  fixture through the internal seam)
- `docs/database-update-mechanism.md` (reference doc gains the channel
  layout, format pin, and freeze semantics)

## Technical Decisions

**Versioned URLs, not a manifest and not a minimum-app-version marker.** All
three candidates protect only builds shipped after them, so reach does not
differentiate; complexity and failure modes do. A minimum-app-version marker
gates on the wrong key (the app version, when the compatibility key is the
data format; an app release that does not touch the schema should not strand
anyone), forces ordering semantics onto what is today an opaque equality
token, and cannot work alone: pre-channel builds ignore any marker, so the
legacy endpoint still could never carry breaking data, and after a break the
older-but-marker-aware builds stop receiving even compatible corrections
unless per-version endpoints exist anyway. It degenerates into versioned URLs
plus parsing. A manifest (one JSON mapping formats to URLs) encodes the same
information the URL scheme carries for free, while adding a parse-negotiate
step to a five-minute client loop, a manifest-vs-blob skew failure mode with
its own publish-ordering rule, and flexibility nothing needs: at most two
formats will be alive at a time, and if a multi-file need (icon packs,
deltas) ever materializes, a manifest can be added inside a format directory
without breaking the URL scheme. Versioned URLs put the format where the
reader already looks, need no parsing, and make the freeze physical: a frozen
endpoint is simply a directory that stops changing.

**Format 1 gets its channel directory now; Assets becomes a frozen-in-place
mirror.** The alternative, leaving channel builds on the Assets URLs and
creating `data/v2/` only at the first actual break, avoids the mirror but
means the mechanism's first real run is the emergency publish: the seed
re-wiring, the multi-target publish flow, and the endpoint contract would all
execute for the first time under pressure. Creating `data/v1/` now means
every routine publish exercises exactly the path the breaking one will use,
and the roadmap's phase-2 e2e expectation ("the new build fetches from its
own endpoint") is testable immediately. The cost is near zero because git
stores content-addressed blobs.

**The pin lives in the csproj and everything derives from it.** The
alternative (a constant in `DatabaseUpdateService` beside hand-maintained
csproj copy paths) leaves the reader pin and the bundled seed free to skew,
which is precisely the class of mistake this phase exists to make
mechanically impossible. One property selects the seed copy source and, via
assembly metadata, the polled URLs; a bump is one reviewable diff.

**The version token stays an opaque equality token.** Introducing ordering
(parsing it as a version and comparing) was considered and rejected: equality
is what the entire field already runs, it is what makes
rollback-by-republish work, and per-format endpoints remove the only question
ordering could have answered. The line-oriented file is the extension point
instead: directives let an endpoint say new things to new builds without a
format bump, and ignoring unknown directives keeps that true for builds
already shipped.

**The frozen notice ships now, with the reader.** Defining the directive but
deferring the UI until a freeze actually happens was rejected: the notice
only exists on builds that already carry it when their channel freezes, so
deferring it recreates the retroactivity problem one level up, the same
argument that made the channel a phase instead of a trigger
(`feature-eft-1-1-roadmap.spec.md`, Technical Decisions).

**The live format is the highest data directory, and only a human creates
one.** Giving TarkovDBEditor its own format constant would add a second pin
(editor vs app) that can drift silently. The repo layout is self-describing:
the editor publishes to the highest `data/v<N>/` present and mirrors to
Assets while that is v1. Creating the next directory happens only in the
reviewed PR that bumps the app's pin, so the two sides of a format bump are
one diff.

### Appended during implementation (2026-08-16)

**The freeze notice strings live in `LocalizationService.Header.cs`, not
`Core.cs`.** The file list above named the wrong partial: every other title-bar
string (`HeaderVersionTooltipIdle`, `HeaderChecking`, the sync chip) is in
`Header.cs`, and `LocalizationHeaderStringsTests` is the completeness guard that
covers that file. The two new keys (`HeaderDataFrozen`,
`HeaderDataFrozenTooltip`) went there and into that test's key list.

**The publish tool repairs a drifted mirror, it does not only detect one.** The
design gave the tool the one-commit rule and left detection to the CI guard. In
implementation the tool also treats an out-of-sync Assets mirror as a
publishable change (`ComparisonResult.MirrorNeedsRepair`), so it copies the
database to both endpoints even when the database itself is unchanged.
Otherwise the guard could turn CI red with no in-tool way to fix it: with no
database change, `HasAnyChanges` was false and the Publish button stayed
disabled, leaving hand-copying as the only repair.

**The local version file is parsed by the same reader as the remote one.** Not
in the design, and it matters for exactly one install: a user whose pre-channel
build polled a frozen Assets endpoint wrote the whole body, `frozen` line
included, into its local `db_version.txt`. After updating to a channel build, a
raw string comparison would never match the remote token and would re-download
the database on every check, forever. Reading the token off both sides costs
nothing and closes that path.

**The endpoint test server is a raw `TcpListener`, not `HttpListener`.**
`HttpListener` goes through HTTP.sys, which needs elevation or a netsh URL
reservation, and this suite must run non-elevated. `LocalFileServer.cs`
implements just what the client under test uses (GET, Content-Length, 404, no
keep-alive) and records requested paths, which is what lets the tests prove the
negative that a frozen or up-to-date check never fetches the database.

**The publish side got tests and an explicit-path constructor.** The Test
Strategy above only covered the app side, which left the tool that produces the
repository state untested while it grew the rule that one publish leaves both
format-1 endpoints identical. `DataPublishService` now has a public
`(sourceBasePath, repoRootPath)` overload (the default one delegates to it), and
`DataPublishChannelTests` drives real publishes against throwaway trees:
highest-format resolution including the v10-beats-v9 numeric case, the
no-channel error, both drift directions repairing to byte-identical endpoints,
an in-sync pair having nothing to publish (so the drift tests cannot pass
vacuously), and a format-2 publish leaving the frozen format-1 endpoints and
their directives untouched.

**The default `None` glob has to give up the Assets pair explicitly.** The
design said the repo copies "remain auto-included `None` items without a copy
step". In practice that leaves two items targeting `Assets\tarkov_data.db` in
the output (the linked channel item and the default-glob one), and which wins is
MSBuild ordering rather than intent. The csproj now carries `<None Remove>` for
both, and `DataChannelTests` pins that.

## Open Questions

- Whether the 1.1 quest-data refresh publishes as format 1 (additive) or
  becomes format 2 is not this phase's call: it is settled by the quest-data
  phase's source decision and regeneration diff, already an open question in
  `feature-eft-1-1-roadmap.spec.md`. This design supports both outcomes
  without change: format 1 publishes flow to both mirrors; a format 2
  publish creates `data/v2/` and freezes them.

## Test Strategy

- **Unit** (`TarkovHelper.Tests`):
  - URL guards: `Update_feed_constants_point_at_fork` updated to pin the two
    v1 URLs full-string on the fork host (same wrong-host rationale as
    today), plus a derivation test that both URLs embed
    `/data/v{DataFormatVersion}/`, so a future bump cannot leave a stale
    hardcoded path behind.
  - Pin integrity: `DataFormatVersion` equals the csproj
    `<TarkovDataFormat>` value as seen through assembly metadata; the
    build-output `Assets/tarkov_data.db` and `Assets/db_version.txt` are
    byte-identical to the repo's `data/v1/` pair, which catches a mis-wired
    seed copy item.
  - Version-file parse matrix: single token; trailing LF/CRLF; token plus
    `frozen`; unknown directive ignored; blank and whitespace-only files fail
    the check; interior blank lines skipped.
  - Frozen propagation: `IsEndpointFrozen` flows into `UpdateCheckResult`;
    the notice string keys resolve in EN, KO, and JA.
  - Mirror integrity (offline repo walk, same pattern as `UpdateXmlTests`
    and `DecisionDocsTests`): the Assets pair is byte-identical to the
    `data/v1/` pair. This is also the CI tripwire for a half-published
    commit on main.
- **Integration** (hermetic, in the normal suite): a local static HTTP
  server serves a fixture with the full repository layout, and
  `DatabaseUpdateService` gains an internal constructor seam (base URL plus
  assets directory; `InternalsVisibleTo` already exists) so tests can point
  an instance at it. One pass polls the legacy Assets path shape, one polls
  `data/v1/`; both complete `CheckAndUpdateAsync` with a download and correct
  version bookkeeping, and a frozen fixture surfaces `IsEndpointFrozen`
  without downloading. This is the automated stand-in for the roadmap's
  phase-2 e2e ("a build without the channel keeps updating against the
  restructured repository"): the previous binary cannot run inside the unit
  suite, so the tests pin the served contract both build generations depend
  on, and the real binary is covered by the manual smoke below.
- **E2E**: the existing suite must stay green unchanged (the harness pins
  `TARKOVHELPER_DISABLE_DB_UPDATE`, and the output Assets layout does not
  change). No new UI e2e is added: exercising the frozen notice end-to-end
  would require the packaged app to honor a URL override, and a production
  escape hatch for the feed URLs is exactly what the full-string URL guards
  exist to forbid. A debug-gated override was considered and rejected for
  the same reason; the notice is covered at unit level (parse, propagation,
  localization) instead.
- **Manual smoke** (after merge; this PR is not a data publish, the Assets
  bytes do not change): fetch all four raw URLs and byte-compare the pairs;
  run the previous released build and confirm its DB check still reports up
  to date; run the new build and confirm from its log that the check hits
  `data/v1/` and reports up to date.

## Verification

- `dotnet build TarkovHelper.sln` - clean build.
- `dotnet test --filter "Category!=E2E"` - full non-E2E suite green,
  including `DecisionDocsTests` (this pair passes the format invariants),
  the updated URL pins, and the new channel tests.
- E2E suite on the development desktop - no new failures relative to main.
- The manual smoke steps from Test Strategy, after merge to main.

## Risks & Migration

- **Nothing migrates.** No schema change, no user-data touch, and the output
  layout is identical, so Debug, Release, and installed copies behave as
  before. An install crossing an app update lands on the new build's
  endpoint automatically: the bundled seed matches the new pin, and the
  immediate first check then syncs, the same self-heal already documented in
  `docs/database-update-mechanism.md`.
- **Half-publish skew.** If a publish updated one format-1 mirror and not
  the other, raw main would serve differing version tokens per endpoint
  until fixed. Prevented by the tool writing both in one commit; detected by
  the mirror guard in CI. Both mirrors always carry format-1 data, so the
  worst case is a token mismatch, not breakage.
- **Raw CDN per-file caching (~5 min) can skew a single check**: a client
  can fetch a fresh version token while the database URL still serves the
  cached previous blob, record the new token against the old data, and stay
  that way until the next publish changes the token again. This hazard
  exists today and is unchanged by this phase; it is recorded here because
  the design review surfaced it. The fix (a content check after download,
  e.g. a version stamp inside the database) is real work touching the DB
  build pipeline and earns its own decision if it ever bites in practice.
- **Repository growth** stays what it is today: each publish adds one
  database blob to history; the mirror adds none (content-addressed).
  Hosting blobs as GitHub Release assets instead was considered and
  rejected: heavier per-publish tooling, a second publish trust model, and
  raw main is proven; revisit only if repository size actually hurts.
- **Rollback of this phase**: revert the app-side changes and the csproj
  re-sourcing; the Assets endpoint was never repointed, so pre-channel
  builds never notice, and an inert `data/v1/` directory harms nothing.
  Within-format data rollback is unchanged: republish older content under a
  new token and every build follows it.
