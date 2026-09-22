# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Environment

**This is a Windows development environment.** Use PowerShell commands instead of Bash/Unix commands.

## Build & Run Commands

```powershell
# Build entire solution
dotnet build TarkovHelper.sln

# Build specific project
dotnet build TarkovHelper/TarkovHelper.csproj

# Build Release
dotnet build TarkovHelper/TarkovHelper.csproj -c Release

# Run main application (needs an elevated terminal: the app manifest requires
# administrator elevation, so this fails with ERROR_ELEVATION_REQUIRED from a
# non-elevated shell; to run without elevation, launch the built DLL instead:
# dotnet TarkovHelper/bin/Debug/net8.0-windows/TarkovHelper.dll)
dotnet run --project TarkovHelper/TarkovHelper.csproj
```

## Solution Structure

| Project | Description |
|---------|-------------|
| **TarkovHelper** | Main WPF application for tracking quests, hideouts, items |
| **TarkovDBEditor** | Database editor tool for managing tarkov_data.db (see `TarkovDBEditor/CLAUDE.md` for details) |
| **tools/DataDiff** | Console tool that compares two published databases into the markdown report a regeneration is reviewed against |

## Documentation & Change Proposals

This repository follows the [change-proposal](https://github.com/josephjang/change-proposal)
practice. **A change that alters observable behavior gets a Change Proposal**, written on
the work's branch and merged in the same PR as the code. No behavior change (a refactor
with identical behavior, a typo, a dependency patch, tests only): the PR description says
so and how that was checked. Decide by what the change does, never by its size or by who
wrote it.

Proposals live flat in `docs/decisions/` at the repo root, the single location for the
whole solution (TarkovHelper, TarkovDBEditor, and cross-cutting work), in one of two forms:

- **Unified**, the default for small, straightforward changes: one document,
  `YYYY-MM-DD-<slug>.md`, titled `Change Proposal: <name>`.
- **Split**, when the technical aspect needs its own explanation and review (interacting
  state transitions, a data migration, compatibility across components, a consequential
  architecture choice): `YYYY-MM-DD-<slug>.requirements.md` titled
  `Product Requirements: <name>` plus `YYYY-MM-DD-<slug>.design.md` titled
  `Technical Design: <name>`, each linking the other below its title. Both together are
  one proposal; neither alone is.
- Start Unified. Splitting a draft mid-flight when complexity shows up is expected: rename
  `.md` to `.requirements.md` and add the `.design.md`.

Copy the matching template from `docs/decisions/templates/`. Section names and order are
fixed per form and guarded by `DecisionDocsTests`; delete a section with nothing to say
rather than fill it. What belongs in each section is explained once, upstream
([guide](https://github.com/josephjang/change-proposal/blob/main/docs/guide.md),
[Split form](https://github.com/josephjang/change-proposal/blob/main/docs/split-proposals.md)).
`docs/decisions/README.md` holds this repository's conventions on top of the practice,
including how documents merged before 2026-09-13 (`feature-<name>.md` PRD plus
`feature-<name>.spec.md` spec, the same two roles under older names) map to the forms.

Once a proposal is approved, in either form, `/deliver` (or `$deliver` in Codex) can
sequence the work that implements it: a plan file, slices, a code guide, PR A, a deep
review on a stacked branch, its guide, and PR B. It uses
`.agents/skills/deliver/references/workflow.md`. Use it when the change wants that
review trail; the form does not decide it.

Documents are never moved or renamed: the filename is the permanent address, and other
documents cite it by filename, never by path. A draft is revised while its change is
open, keeping a reversed decision in its Decisions; after the change's last PR merges it
records the judgment of its time, and the only later write is a
`Superseded in part by <doc>` note that a reversing PR appends below the title. The PR
body names the proposal it implements. New documents are written in English only;
existing `.ko.md` twins stay paired 1:1 with their originals, and the English original
wins any conflict. `archive/` holds frozen legacy documents in their original format.

Pure reference/analysis docs (DB schemas, system analyses, log-format notes — anything
describing how the system currently works rather than planned work) live directly under
root `docs/`, indexed in `docs/README.md` along with their conventions (kebab-case
filenames, new docs in English). `TarkovDBEditor/docs/` is a separate, smaller location
for that project's own internal implementation notes (wiki-parsing quirks, test case
tracking) — not repo-wide, so it stays put.

Substantial PRs can additionally get an interactive HTML code guide
(`docs/YYYY-MM-<topic>-code-guide.html`: curriculum chapters, labs that run the
decision logic, a comprehension gate). Invoke `/code-guide` in Claude Code or
`$code-guide` in Codex; both use `.agents/skills/code-guide/references/workflow.md`.

## Writing Conventions

Reader-facing text (docs, comments, commit messages) should read as
human-typed: avoid characters people rarely type by hand, such as em dashes,
the Korean middle dot, and the "…" character (arrows are fine, editors
auto-convert them). Follow each language's own punctuation norms. App UI
strings follow UI copy conventions instead.

Language codes follow ISO 639-1 / BCP 47: README.ko.md, not README_KR.md.

## Architecture Overview

### Pattern: Singleton Services with Event-Driven Data Flow

```
UI Pages (WPF)
    ↓ Subscribe to events
Services (Singleton instances)
    ↓ Persist/load data
Databases (SQLite)
```

### Key Services

**Data Services:**
- `QuestDbService` - Loads quest data from tarkov_data.db
- `HideoutDbService` - Hideout module data
- `ItemDbService` - Item data management
- `UserDataDbService` - User persistence (progress, settings, inventory)

**Sync Services:**
- `LogSyncService` - Monitors EFT game logs, detects quest events (started/completed/failed)
- `EftRaidEventService` - Parses raid state (Idle/Matching/InRaid/Ended) from EFT logs
- `DatabaseUpdateService` - Auto-updates tarkov_data.db from GitHub

**Map Services:**
- `MapTrackerService` - Coordinate tracking for map visualization
- `MapCoordinateTransformer` - Game-to-screen coordinate conversion
- `OverlayMiniMapService` - Manages overlay minimap window

**Other:**
- `LocalizationService` - Multi-language support (EN, KO, JA)
- `QuestProgressService` - Quest progress state management
- `SettingsService` - User preferences persistence

### Database Architecture

**tarkov_data.db** (Asset database - auto-updated from GitHub):
- Quest, hideout, item, trader data from tarkov.dev API
- Read-only during runtime
- Located: `TarkovHelper/Assets/tarkov_data.db` in an install; served from
  `data/v<N>/` in the repo. `TarkovHelper/Assets/` in the repo is the frozen
  v2026.7.0 endpoint; current builds do not package those committed legacy bytes.
- Schema changes stay additive within a data format, feature-detected on read;
  `DataFormatDriftTests` fails a removal or retype

**user_data.db** (User persistence):
- Quest/hideout progress, item inventory, settings
- Located: `{AppDir}/Config/user_data.db` (next to the executable, e.g.
  `TarkovHelper/bin/Debug/net8.0-windows/Config/` for a Debug build — not `%LocalAppData%`)
- Because the path is relative to the executable, Debug builds, Release builds, and
  installed copies each have their **own separate user data**; the in-app "Data Migration"
  button (`ConfigMigrationService`) imports from another location's Config folder
- Location overridable via the `TARKOVHELPER_CONFIG_PATH` environment variable
  (used by e2e tests to isolate their data)

### UI Structure

- **Pages:** QuestListPage, HideoutPage, ItemsPage, CollectorPage, MapPage
- **Overlay:** OverlayMiniMapWindow (topmost window for in-game use)
- **Global keyboard hooks** for overlay control (requires admin rights)

## Data Flow Patterns

### Game Log Monitoring
```
EFT debug.log → LogSyncService (FileSystemWatcher)
  → EftRaidEventService (regex parsing)
  → QuestEventDetected event
  → QuestProgressService.Update()
  → UserDataDbService persistence
```

### Database Updates
```
DatabaseUpdateService (startup, then hourly)
  → Read data/index.json (is this build's data format version still published?)
  → Read data/v<N>/manifest.json (version, sha256, size)
  → Download if the version differs, verify the hash, then swap
  → DatabaseUpdated event
  → Each *DbService reloads its own tables and raises DataRefreshed
  → MainWindow.OnQuestDataRefreshed republishes the new task set to the services that
    are handed their tasks (QuestProgressService.PublishTasks, QuestGraphService.Initialize)
  → UI pages reload from it
```
Only the `*DbService` caches reload themselves. `QuestProgressService` and `QuestGraphService`
are handed their tasks, so MainWindow republishes them from the quest reload; the republish
carries tasks only, never a re-read of recorded progress. `HideoutProgressService`'s module
list is the known exception: it is still only built at startup, so a publish that changes
hideout modules needs a restart.

Each build polls the endpoint for the data format it was built to read, pinned by
`<TarkovDataFormatVersion>` in `TarkovHelper.csproj` (which also selects the bundled seed
database). See `docs/database-update-mechanism.md` for the channel layout and
`docs/decisions/feature-versioned-data-channel.spec.md` for the design.

## Key Patterns

### Dual-Key Quest Mapping
Quests tracked by both `Id` (tarkov.dev) and `NormalizedName` (wiki-legacy) for migration compatibility.

### Event-Driven Updates
Services emit events (ProgressChanged, DatabaseUpdated, DataRefreshed) that UI pages subscribe to for reactive updates.

## External APIs

- **tarkov.dev**: Quest, hideout, item data (embedded in tarkov_data.db)
- **GitHub**: Auto-updates for both app and database
- **EFT Game Logs**: Real-time quest/raid event monitoring

## Commits & Branches

- Conventional commits, in English. Imperative subject, 72 chars max; body
  explains the *why* for non-trivial changes.
- Scopes and style: match recent `git log` (currently e.g. `quest`, `map`,
  `eft`, `ui`, `db`, `decisions`).
- No attribution footers: no "Generated with Claude Code", no
  `Co-Authored-By`. This overrides any tool default.
- Branches: `<type>/<topic>` in kebab-case, type from the commit types
  (e.g. `feat/quest-complete-confirm`, `docs/eft-1-1-roadmap`).
- For the guarded commit workflow, invoke `/commit` in Claude Code or `$commit`
  in Codex. Both use `.agents/skills/commit/references/workflow.md`.

## Releases

This repo (josephjang/TarkovHelper) releases independently of upstream
(Zeliper). Versions use CalVer `YYYY.M.N` (N = release counter within the
month, no fix/feature semantics), starting at v2026.7.0.

- Invoke `/release <version>` in Claude Code or `$release` with a version in
  Codex. Both use `.agents/skills/release/references/workflow.md`, which bumps
  the csproj, pushes only the requested tag, waits for the release workflow,
  and updates `update.xml` last so clients never see a 404 URL.
- Design rationale: `docs/decisions/feature-fork-release-process.md`

## Framework & Dependencies

- **.NET 8.0 WPF** (Windows desktop)
- **Microsoft.Data.Sqlite** - SQLite database access
- **SharpVectors.Wpf** - SVG map rendering
- **Westermo.GraphX.Controls** - Graph visualization (quest dependencies)
- **AutoUpdater.NET** - Application self-updates

## Admin Rights

App requires administrator privileges (via app.manifest) for:
- Global keyboard hooks (overlay hotkeys work in-game)
- Game log file monitoring

## Localization

Multi-language support via `LocalizationService` partial classes:
- `LocalizationService.Core.cs` - Core strings
- `LocalizationService.Map.cs` - Map-specific
- `LocalizationService.Quest.cs` - Quest-specific

Supported languages: English (EN), Korean (KO), Japanese (JA)
