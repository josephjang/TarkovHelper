# Versioned Data Channel - Technical Spec

- **Created**: 2026-08-16

> The sibling `feature-versioned-data-channel.md` holds the product decision.
> Write this on the work's branch and merge it in the same PR as the work.
> Nothing is kept current: fields are written once, discoveries are appended. A
> later change that reverses a decision here appends `Superseded by <doc>` below
> this line, in the PR that reverses it.

## Summary

Three ideas carry the design. First, an integer **data format version**
identifies the reader contract for `tarkov_data.db`, and endpoints are
**versioned URLs**: one `data/v<N>/` directory per data format version on raw
main. The pre-channel `TarkovHelper/Assets/` URLs, hardcoded in fielded builds,
become data format version 1's second address, kept byte-identical to `data/v1/`
by the publish flow and a guard test. Each app build compiles in the version it
reads from a single csproj property that also selects its bundled seed database,
so the pin, the polled URLs, and the seed cannot skew.

Second, each endpoint publishes a **`manifest.json`** naming the version it
serves and the digest and size of the database beside it, so a client verifies
what it downloaded before installing it, and the database itself carries the same
version in SQLite's `user_version`, so the payload can be checked against its own
claim rather than only against the publisher's.

Third, a single mutable pointer at **`data/index.json`** names the data format
version currently published. A build compares it against its own pin to learn it
has been left behind. Superseded endpoint directories are never rewritten, so
nothing about a freeze is a manual step that can be forgotten.

This settles the mechanism question `feature-eft-1-1-roadmap.spec.md`
deliberately left open (versioned URLs, a minimum-app-version marker, or a
manifest), in favor of versioned URLs carrying a manifest, for the reasons under
Technical Decisions.

## Non-Goals

- No minimum-app-version marker (rejected under Technical Decisions).
- No delta or incremental downloads; the whole-file replace stays (the
  database is about 7 MB).
- No back-publishing to superseded data format versions. The pipeline produces
  one current database per run; the roadmap policy promises a freeze at the last
  compatible version, not parallel maintenance of old ones.
- No git automation in TarkovDBEditor: the publish commit and push to main
  stay manual and reviewed, as today.
- No change to the app self-update feed (`update.xml`, `UpdateService`) beyond
  its polling interval.
- No change to what the channel carries: exactly the pair that hot-updates
  today. Map configs, SVGs, and icons keep shipping inside app releases; the
  runtime icon channel stays in the triggered backlog of
  `feature-eft-1-1-roadmap.spec.md`.
- No manual data-update check. The Settings "check for updates" button still
  checks only for app updates, so restarting the app is the only way to force a
  data check. Deferred to the next settings UX pass; `ForceUpdateCheckAsync`
  stays uncalled until then.

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
  endpoint is non-destructive. Nothing verifies what was downloaded.
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
  and installs pick the publish up within minutes. Nothing checks that the file
  it publishes is a database.
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

### Vocabulary

Four names, deliberately distinct, because three of these are integers and a
name has to say what its slot holds.

**data format** is the contract a build must satisfy to read `tarkov_data.db`
correctly. It covers the SQLite schema, **the meaning of each field, and the
range of values a field may take**.

**data format version** (`dataFormatVersion`, `currentDataFormatVersion`,
`<TarkovDataFormatVersion>`, `DataFormatVersion`, `data/v<N>/`) is the integer
identifying *which* data format. It increments only when forward compatibility
breaks. The test to apply is a single question: *would the previously released
build, reading this data with its existing code, show the user something wrong?*
If yes, it is a bump. If an older build simply ignores the change (new tables,
new columns, new rows, new values inside a field's documented range), it is not.

**schema version** (`schemaVersion`) is the shape of a JSON document in this
channel, and nothing else.

**version** (`version`, opaque string) is which publish the data is, compared
for equality only.

"Forward compatibility" is used in
[Confluent's sense](https://docs.confluent.io/platform/current/schema-registry/fundamentals/schema-evolution.html):
consumers on the old contract can read data written under the new one. That is
this project's situation exactly, since the readers that cannot be fixed are the
builds already installed. Wherever this repository previously said "additive",
forward compatibility is the precise name for the same rule.

### Repository layout

```
<repo>/
├── data/
│   ├── index.json                 # the data format version published right now
│   └── v1/                        # data format version 1 endpoint
│       ├── manifest.json          # what this endpoint serves
│       ├── tarkov_data.db
│       └── db_version.txt         # legacy-protocol token and the seed bookmark
└── TarkovHelper/Assets/           # the address pre-channel builds poll
    ├── tarkov_data.db             # byte-identical to data/v1
    └── db_version.txt
```

`TarkovHelper/Assets/` stays committed because builds already in the field
hardcode those URLs and cannot be repointed. Those builds compare the whole body
of `db_version.txt` as their version, so that file must keep holding a bare
token. Permanent invariant: the Assets pair and the `data/v1/` pair are
byte-identical, forever, enforced by `DataChannelMirrorTests`. Git stores blobs
content-addressed, so the mirror adds tree entries rather than repository growth.

**Bumping the data format version**, recorded now so the publish that first needs
it is routine: create `data/v<N+1>/` with the new database and manifest, stop
writing every lower endpoint, and point `data/index.json` at the new version. No
lower endpoint is edited, ever. The new directory lands on main before or with
the release tag of the app build that pins N+1, mirroring the update.xml-last
principle from `feature-fork-release-process.md`.

### Single-sourced data format version pin (TarkovHelper)

- `TarkovHelper/TarkovHelper.csproj` carries
  `<TarkovDataFormatVersion>1</TarkovDataFormatVersion>`.
  - The seed copy items source from it: `None Include="..\data\v$(TarkovDataFormatVersion)\..."`
    with `Link="Assets\..."` and `CopyToOutputDirectory=PreserveNewest`. The
    output layout is unchanged, so runtime paths, packaging, and e2e
    expectations still find `Assets\tarkov_data.db`. The repo's Assets copies
    are removed from the default `None` glob (`<None Remove>`), because
    otherwise two items target the same output path and MSBuild ordering, not
    intent, decides which wins.
  - `<AssemblyMetadata Include="TarkovDataFormatVersion" ... />` exposes the
    number to code.
- `DatabaseUpdateService.DataFormatVersion` reads that metadata once at type
  initialization: which data format a build reads identifies the build, not the
  wire, so it stays on the client. `DataChannel` derives `INDEX_URL`,
  `CHANNEL_BASE_URL`, and `MANIFEST_URL` from it. Missing or unparseable
  metadata throws on that read: a build wired that badly must fail loudly, never
  fail soft onto some default endpoint.

One property therefore selects the seed, the polled URLs, and the version the
app claims to read, so a bump is one reviewable diff and skew between them is
mechanically impossible.

### Channel documents

```json
// data/v1/manifest.json
{
  "schemaVersion": 1,
  "dataFormatVersion": 1,
  "version": "1.0.10",
  "database": { "file": "tarkov_data.db", "digest": "sha256:...", "size": 6889472 }
}

// data/index.json
{ "schemaVersion": 1, "currentDataFormatVersion": 1 }
```

- `version` keeps today's semantics exactly: an opaque token compared for
  equality, never ordered, so rollback-by-republish keeps working.
- `digest` is algorithm-qualified (`sha256:<hex>`) following OCI and Sigstore.
  The prefix is what lets a build that only knows sha256 tell "a digest I cannot
  check" apart from "no digest at all"; without it the two are indistinguishable
  and verification switches itself off silently.
- Integrity fields are **optional to the reader**: absent, or naming an
  algorithm this build does not implement, both mean "install without verifying",
  with a log line. Refusing an unimplemented algorithm would turn a future hash
  upgrade into a breaking change for every build in the field. A digest that is
  present but not in `<algorithm>:<hex>` form is different and is **refused**:
  a bare hex string is the shape the prefix exists to rule out, so reading it
  leniently would switch verification off in exactly the case the prefix was
  introduced to make visible. Integrity fields are **mandatory in the
  repository**, enforced by `DataChannelMirrorTests`, so they can only go missing
  deliberately.
- `database.file` is data, not a constant, which leaves room to later give the
  payload a version-stamped filename (immutable URLs) without changing a reader.
- Unknown fields are ignored, so an endpoint can carry information for newer
  builds without disturbing the ones already shipped.
- A document that cannot be read (empty body, broken JSON, a missing required
  field) is a failed check: no download, no local state change.
- `schemaVersion` above this build's maximum, or a `dataFormatVersion` that is
  not this build's pin, is refused with a log and **no user-facing message**.
  Both are publishing errors that the next publish fixes, and telling the user to
  update the app would be wrong advice.

### Verifying a download

After the bytes arrive and before they replace the working database:

1. `size`, when present, must match.
2. `digest`, when present and checkable, must match.
3. The database's own `PRAGMA user_version` must be this build's data format
   version.

Any failure discards the temp file and leaves both the working database and the
local bookmark untouched, so the next check retries rather than recording a
version it did not actually install.

`user_version` is the 32-bit slot SQLite reserves for the application and never
reads itself. An unstamped database reads 0, which is not "format 0" but "this
file makes no claim", and is refused. Every publish stamps the database before
hashing it and aborts if the stamp cannot be written, so a payload that arrives
unstamped did not come from a publish: it is a directory populated by hand, a
copy from the wrong build, or a half-finished bump, which is exactly what this
check exists to catch. It is also the payload whose manifest is most likely to
carry no digest, so accepting it would install a file that nothing verified.

Failing to read the stamp at all is likewise a rejection. A file SQLite cannot
open is not a database, and the integrity fields that would otherwise have
caught it are optional, so the refusal is the only thing standing between a
truncated download or an error page served with a 200 and a working database
replaced by bytes no reader can open.

### Being left behind

A build compares `index.json`'s `currentDataFormatVersion` against its own pin.
Higher means this build's endpoint will receive nothing further. The state is
re-derived on every check that reaches the index and deliberately left alone when
that fetch fails, so a network blip cannot flicker the notice off and on.

A newer data format version can only ship with the build that pins it, so being
left behind implies a newer app exists and its update affordance is already on
screen. The UI therefore **escalates the existing update pill** rather than
raising a second notice: the label names the data consequence instead of the
version, and the tone moves from success green to warning, with dark text because
white on amber is unreadable at that size. Settings shows the same fact in its
update status line. Being left behind still does not stop the endpoint serving
the last compatible data an install has not caught up to yet.

### Publish flow (TarkovDBEditor)

`DataPublishService` writes the **live data format version**, meaning the highest
`data/v<N>/` directory present in the repository, and mirrors into
`TarkovHelper/Assets` while that is 1. Per publish it:

1. Stamps the source database's `user_version` with the live version, before
   anything is hashed or copied, so both endpoints receive one stamped file and
   stay byte-identical. A source SQLite cannot open fails the publish here.
2. Copies the database to the endpoint, and to the Assets mirror while
   applicable, treating a drifted mirror as a publishable change so the tool can
   always repair what the CI guard reports.
3. Writes `manifest.json` with a freshly computed digest and size.
4. Writes the version stamp to every endpoint the live version serves.
5. Rewrites `data/index.json`.

The tool never creates a format directory: bumping the data format version is a
deliberate act in the same reviewed PR that teaches the app to read it, so a
routine publish cannot bump it by accident. A repository with no `data/` channel
fails the comparison rather than silently falling back to the Assets-only layout.
Non-channel assets (map configs, SVGs, icons) keep publishing to Assets only.

One-commit rule: a publish commit carries every endpoint file it wrote, so raw
main never serves a half-published mirror.

### Files touched

- `TarkovHelper/TarkovHelper.csproj` (version property, metadata, seed items)
- `TarkovHelper/Services/DataChannel.cs` (new: endpoint URLs and the document
  readers, the wire half of the channel)
- `TarkovHelper/Services/DatabaseUpdateService.cs` (data format pin, polling,
  download, verification, superseded state, test seam)
- `TarkovHelper/MainWindow.xaml.cs` (update pill escalation, Settings status)
- `TarkovHelper/Services/LocalizationService.Header.cs` (pill and status
  strings, three languages)
- `TarkovHelper/Services/UpdateService.cs` (app-update polling interval)
- `data/index.json`, `data/v1/manifest.json`, `data/v1/db_version.txt`,
  `data/v1/tarkov_data.db`
- `TarkovDBEditor/Services/DataPublishService.cs` (channel targets, stamping,
  manifest and index writing, explicit-path constructor)
- `TarkovDBEditor/Views/DataPublishWindow.xaml.cs` (target display)
- `TarkovHelper.Tests/`: `DataChannelTests`, `DataChannelMirrorTests`,
  `DataChannelEndpointServingTests`, `DataPublishChannelTests`,
  `DataFormatDriftTests` + `DataFormatBaseline.v1.json`, `LocalFileServer`,
  updated `UpdateServiceTests` and `LocalizationHeaderStringsTests`
- `docs/database-update-mechanism.md`, root and TarkovDBEditor `CLAUDE.md`

## Technical Decisions

**Versioned URLs, not a minimum-app-version marker.** Both protect only builds
shipped after them, so reach does not differentiate; failure modes do. A marker
gates on the wrong key: the app version, when the compatibility key is the data
format, so an app release that does not touch the data would strand people for
nothing. It also forces ordering semantics onto what is today an opaque equality
token, and it cannot work alone, because pre-channel builds ignore any marker and
after a break the older-but-marker-aware builds would stop receiving even
compatible corrections unless per-version endpoints existed anyway. It
degenerates into versioned URLs plus parsing. Versioned URLs put the version
where the reader already looks and make a freeze physical: a superseded endpoint
is a directory that stops changing.

**The document is JSON named for its role, not a line-oriented version file.**
The first design carried the version and a `frozen` directive in
`db_version.txt`. That name was already a misnomer the moment the file held
anything besides a version, and the format handles repeated structure badly,
which the deferred icon channel would need. Every mature channel
(electron-builder `latest.yml`, Squirrel `RELEASES`, APT `Release`, rustup's TOML
manifest, Docker and TUF manifests) names the document for its role and carries a
payload digest inside it. The line format was not a dead end, since unknown
directives were ignored, but JSON is the shape this problem already has
elsewhere.

**The digest travels in the same document as the version.** Risks below records
that raw GitHub caches each file separately, so a client can pair a fresh version
with a stale database and record the new version against the wrong bytes.
Verification after download makes that pair atomic, and it also catches a
truncated download, which the previous code installed happily. A sidecar hash
file would not work: it can be cached stale just as easily.

**A pointer, not a marker written into superseded endpoints.** The first design
had a publish append `frozen` to every lower endpoint at bump time. That mutates
documents this design calls immutable and leaves a step someone can forget.
`data/index.json` is rewritten on every publish, so superseded directories are
never touched again and the detection cannot be forgotten, because nobody
performs it. The index is the only mutable part of the channel; an unreadable one
leaves the last known state alone rather than declaring the build current.

**Data format version 1 gets its channel directory now.** Leaving channel builds
on the Assets URLs and creating `data/v2/` only at the first actual break avoids
the mirror, but it means the mechanism's first real run is the emergency publish,
with the seed re-wiring, the multi-target publish flow, and the endpoint contract
all executing for the first time under pressure. Creating `data/v1/` now means
every routine publish exercises exactly the path the breaking one will use. The
cost is near zero because git stores content-addressed blobs.

**The pin lives in the csproj and everything derives from it.** A constant in
`DatabaseUpdateService` beside hand-maintained csproj copy paths would leave the
reader pin and the bundled seed free to skew, which is precisely the class of
mistake this phase exists to make mechanically impossible.

**The version token stays an opaque equality token.** Introducing ordering was
considered and rejected: equality is what the entire field already runs, it is
what makes rollback-by-republish work, and per-version endpoints remove the only
question ordering could have answered.

**"Data format", not "data schema".** Schema versioning as practised (Avro, JSON
Schema, Confluent Schema Registry) compares **structure**, and is blind to a
field whose meaning or permitted value range changed. That blindness is exactly
what this version must not have, and "schema" is separately taken in this
repository for `_schema_meta` and DDL. The name follows [Apache Iceberg's
`format-version`](https://iceberg.apache.org/spec/), defined as incrementing when
older readers would no longer read newer tables correctly, with readers required
to refuse versions above what they support: the same concept, shape, and name.
Delta Lake calls its equivalent a protocol version
(`minReaderVersion`/`minWriterVersion`) and has since moved to named table
features because one integer proved too coarse across many engines; that is the
recorded escape hatch if one integer ever stops being enough here, though with a
single reader it is not close. `schemaVersion` for a document's own shape follows
Docker's manifest field of that name and TUF's `spec_version`. CloudEvents'
`dataschema` is deliberately not followed: that attribute is informational by
specification and is not a compatibility gate.

**The database states its own data format version.** Asserting it only from the
directory path and the manifest leaves the payload silent, and a manifest can be
internally consistent while describing the wrong file: a directory populated by
hand, a copy from the wrong build, a half-finished bump. `PRAGMA user_version` is
the slot SQLite reserves for exactly this, so the payload can be checked against
its own claim rather than only against the publisher's.

**The escalation reuses the update pill.** Adding a second notice beside a button
that already says "update" splits one situation into two things to read and
leaves the user's only action in the quieter of the two. Deferring the escalation
until a bump actually happens was also rejected: the notice only exists on builds
that already carry it, so deferring recreates the retroactivity problem one level
up, the same argument that made this channel a phase instead of a trigger
(`feature-eft-1-1-roadmap.spec.md`, Technical Decisions).

**Polling drops to hourly.** Five minutes was 288 checks a day against a payload
that changes a few times per game patch, and it sits below raw GitHub's own
per-file cache window, so the extra checks could not learn anything new even in
principle. The startup check is unchanged and immediate, and it is the one that
finds things. The app-update check moves from three minutes to the same interval
for the same reason, in its own commit.

**The live data format version is the highest data directory, and only a human
creates one.** Giving TarkovDBEditor its own version constant would add a second
pin that can drift silently. The repository layout is self-describing instead.

**The publish tool repairs a drifted mirror rather than only reporting it.**
Leaving repair to the CI guard means the guard can turn red with no in-tool fix:
with no database change `HasAnyChanges` was false, the Publish button stayed
disabled, and hand-copying was the only way out.

**A source SQLite cannot open fails the publish.** Stamping could have skipped
silently, but a file that is not a database should never reach the channel, and
nothing else in the tool was checking that what it ships is one.

## Open Questions

- Whether the 1.1 quest-data refresh publishes under data format version 1
  (forward-compatible) or becomes version 2 is not this phase's call: it is
  settled by the quest-data phase's source decision and regeneration diff,
  already an open question in `feature-eft-1-1-roadmap.spec.md`. This design
  supports both outcomes without change.

## Test Strategy

- **Unit** (`TarkovHelper.Tests`):
  - URL guards: `Update_feed_constants_point_at_fork` pins the index and
    manifest URLs full-string on the fork host (same wrong-host rationale as
    today), plus a derivation test that the manifest URL embeds
    `/data/v{DataFormatVersion}/`, so a bump cannot leave a stale hardcoded path.
  - Pin integrity: `DataFormatVersion` equals the csproj
    `<TarkovDataFormatVersion>` as seen through assembly metadata; the
    build-output `Assets/` pair is byte-identical to the repo's `data/v1/` pair,
    which catches a mis-wired seed copy item.
  - Document readers: valid manifest and index parse into their fields; unknown
    fields ignored; integrity fields optional; version trimmed; and a matrix of
    unusable documents (empty, malformed, missing required fields) all yielding
    a failed check rather than a fabricated one.
  - Wire field names pinned exactly. The reader is case-insensitive and ignores
    unknown fields, so a rename would otherwise pass every other test while
    silently disabling what it renamed.
  - Mirror integrity (offline repo walk, same pattern as `UpdateXmlTests` and
    `DecisionDocsTests`): the Assets pair is byte-identical to `data/v1/`, the
    committed manifest describes the committed database (digest, size, version),
    the index covers the version this build polls, and the published database
    carries its `user_version` stamp. This is also the CI tripwire for a
    half-published commit on main.
  - Localization: the pill and status keys resolve in EN, KO, and JA.
- **Data format drift** (`DataFormatDriftTests`): compares the published
  database's tables and declared column types against the committed
  `DataFormatBaseline.v<N>.json` and fails when a table or column disappears or
  is retyped. An addition is safe for readers but fails too, because a column
  the baseline does not record cannot be missed when a later publish drops it:
  the run writes the widened baseline beside the committed one as
  `DataFormatBaseline.v<N>.proposed.json`, and the maintainer reviews it, moves
  it into place, and commits it with the publish. A missing baseline is proposed
  the same way, and a break proposes nothing at all. The test never writes the
  committed baseline itself, so a re-run with nothing adopted in between stays
  red instead of passing against a file the run wrote for itself.
  - **What it cannot cover:** a field whose meaning changed or whose permitted
    range narrowed, because nothing in the file says what a value means. Those
    stay a human judgement against the question in Vocabulary, and a green run is
    therefore not evidence that forward compatibility holds.
- **Integration** (hermetic, in the normal suite): a loopback HTTP server serves
  a fixture laid out like the repository, and `DatabaseUpdateService` has an
  internal constructor seam (channel root plus assets directory;
  `InternalsVisibleTo` already exists). Covers a complete update, a digest
  mismatch, a truncated payload, a digest naming an algorithm this build cannot
  check, a digest that names no algorithm at all, absent integrity fields, a
  mismatched `user_version`, an unstamped payload, a payload SQLite cannot open,
  supersession including that a failed index fetch does not clear it, and the two
  document-level refusals.
  The server is a raw `TcpListener` rather than `HttpListener`, which needs
  elevation or a netsh URL reservation this suite cannot assume, and it records
  requested paths so a test can prove the negative that a check which found
  nothing never fetched the database.
- **Publish side** (`DataPublishChannelTests`): drives real publishes against
  throwaway trees through a public explicit-path constructor. Covers highest
  version resolution including v10 beating v9, the no-channel error, both mirror
  drift directions repairing to byte-identical endpoints, an in-sync pair having
  nothing to publish (so the drift tests cannot pass vacuously), the manifest
  round-tripping through the app's own reader, and a version-2 publish leaving
  the superseded endpoints untouched.
- **E2E**: the existing suite must stay green unchanged (the harness pins
  `TARKOVHELPER_DISABLE_DB_UPDATE`, and the output Assets layout does not
  change). No new UI e2e: exercising the escalated pill end-to-end would require
  the packaged app to honor a URL override, and a production escape hatch for the
  feed URLs is exactly what the full-string URL guards exist to forbid.
- **Manual smoke** (after merge; this PR is not a data publish): fetch the raw
  URLs and byte-compare the mirrored pair; run the previous released build and
  confirm its DB check still reports up to date; run the new build and confirm
  from its log that the check hits `data/v1/` and reports up to date.

## Verification

- `dotnet build TarkovHelper.sln` - clean build, zero warnings, Debug and
  Release.
- `dotnet test --filter "Category!=E2E"` - full non-E2E suite green, including
  `DecisionDocsTests` (this pair passes the format invariants), the updated URL
  pins, and the new channel tests.
- E2E suite on the development desktop - no new failures relative to main.
- `dotnet publish` output carries the seed pair at `Assets/`, so the release zip
  is unaffected by the re-sourcing.
- The manual smoke steps above, after merge to main.

## Risks & Migration

- **Nothing migrates.** No schema change, no user-data touch, and the output
  layout is identical, so Debug, Release, and installed copies behave as
  before. An install crossing an app update lands on the new build's
  endpoint automatically: the bundled seed matches the new pin, and the
  immediate first check then syncs, the same self-heal already documented in
  `docs/database-update-mechanism.md`.
- **Half-publish skew.** If a publish updated one format-1 endpoint and not the
  other, raw main would serve different bytes per address until fixed. Prevented
  by the tool writing both in one commit and repairing drift it finds; detected
  by the mirror guard in CI.
- **Raw CDN per-file caching can still skew a single check**, but no longer
  silently: a client can fetch a fresh manifest beside a cached older database,
  and the digest check now discards it and retries rather than recording the new
  version against the wrong bytes. The residual cost is a wasted download.
- **Repository growth** stays what it is today: each publish adds one database
  blob to history; the mirror adds none (content-addressed). Hosting blobs as
  GitHub Release assets instead was considered and rejected: heavier per-publish
  tooling, a second publish trust model, and raw main is proven; revisit only if
  repository size actually hurts.
- **A longer poll interval has no manual override**, because the Settings button
  remains app-only (Non-Goals). Restarting the app forces a check, which is the
  escape hatch until the settings pass lands.
- **Rollback of this phase**: revert the app-side changes and the csproj
  re-sourcing; the Assets endpoint was never repointed, so pre-channel builds
  never notice, and an inert `data/` directory harms nothing. Within-format data
  rollback is unchanged: republish older content under a new token and every
  build follows it.
