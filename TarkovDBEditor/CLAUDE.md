# TarkovDBEditor Agent Guidance

This file contains project-specific guidance shared by coding agents working in
`TarkovDBEditor/`. For solution-wide commands, documentation rules, writing
conventions, and Git policy, also follow `../CLAUDE.md`.

## Build, Run, and Validate

Use PowerShell in this Windows repository.

```powershell
# Build this project from TarkovDBEditor/
dotnet build TarkovDBEditor.csproj

# Run the WPF GUI application
dotnet run --project TarkovDBEditor.csproj

# Match the repository CI gates from the repository root
dotnet build TarkovHelper.sln -c Release
dotnet test TarkovHelper.sln -c Release --no-build --filter "Category!=E2E"
```

## Project Overview

TarkovDBEditor is a WPF .NET 8 tool that creates and maintains
`tarkov_data.db`. It supports dynamic SQLite schemas and specialized import and
editing flows for Tarkov wiki, tarkov.dev, quest, hideout, and map data.

The main `TarkovHelper` application consumes the generated database. Consult
`../TarkovHelper/CLAUDE.md` only when a change crosses into the application.

## Critical Rules

### Data Source Policy

Never use the tarkov.dev API directly as a normal runtime data source. Normal
operation reads from:

1. Cached files under `wiki_data/`.
2. The local `tarkov_data.db` database.

API-backed services are for explicit population or synchronization workflows.
Do not introduce an API dependency into normal browsing, editing, validation,
or application runtime paths.

### WPF and Windows Forms Type Ambiguity

The project enables both WPF and Windows Forms. In WPF code-behind files, add
explicit aliases for conflicting graphics and input types that the file uses:

```csharp
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
```

Do not add unused aliases merely to copy the full list.

### Schema Changes

The DataGrid is driven by `_schema_meta.SchemaJson`, not only by SQLite table
metadata. When adding or changing a column, update every affected layer:

1. The `CREATE TABLE` statement.
2. An `ALTER TABLE` or equivalent migration for existing databases.
3. The corresponding `RegisterSchemaAsync` metadata.
4. The model property.
5. The insert or UPSERT query.
6. Parameter binding and read mapping.
7. `../docs/database-schema.md` when the persisted schema changes.

If schema registration is omitted, the column may exist in SQLite but remain
absent from the editor UI.

**Keep the change additive.** The published database is a contract with every
TarkovHelper build already installed, and those builds cannot be fixed after the
fact. Adding a table or column is free, because readers feature-detect. Removing
one, or changing its declared type, breaks them all, so `DataFormatDriftTests`
fails on it. When a break is genuinely intended it is a data format bump: publish
under a new `data/v<N+1>/` and raise `<TarkovDataFormat>` in the same PR. See
`../docs/decisions/feature-versioned-data-channel.spec.md`.

### Database Access

- Use `DatabaseService.Instance.DatabasePath` for the active database.
- Keep database operations asynchronous with `Microsoft.Data.Sqlite`.
- Use `await using` for connections and commands that implement async disposal.
- Prefer `ON CONFLICT(Id) DO UPDATE` for insert-or-update flows.
- Update schema metadata and indexes together with table changes.

### Dogtag Item Generation

`RefreshDataFromCacheAsync` creates `dogtag-bear` and `dogtag-usec` items when
quest requirements reference their factions. Dogtag level requirements belong
to `QuestRequiredItems.DogtagMinLevel` or
`QuestObjectives.DogtagMinLevel`, not to the shared item row.

## Architecture Navigation

The main flow is:

```text
MainWindow.xaml (.cs)
  -> MainViewModel.cs
  -> DatabaseService.cs and specialized services
  -> SQLite database and schema metadata
```

Important areas:

- `Services/DatabaseService.cs`: dynamic tables, CRUD, and `_schema_meta`.
- `Services/RefreshDataService.cs`: cached wiki/API data import.
- `Services/MapMarkerService.cs`: map marker persistence.
- `Services/ApiMarkerService.cs`: imported API marker persistence.
- `Services/HideoutDataService.cs`: hideout import and persistence.
- `ViewModels/MainViewModel.cs`: table selection and CRUD commands.
- `ViewModels/QuestRequirementsViewModel.cs`: quest validation workflows.
- `Views/`: WPF windows and dialogs.
- `Resources/Data/map_configs.json`: map transforms and floor definitions.

Use `rg` and the current implementation instead of relying on a static inventory
of every service, table, menu, or dependency in this file.

## Reference Documentation

- `../docs/database-schema.md`: complete persisted schema, relationships, JSON
  formats, and query examples. Keep this as the schema reference instead of
  duplicating DDL here.
- `docs/coordinate-analysis.md`: project-specific coordinate notes.
- `docs/QuestPreviousPatterns.md`: quest predecessor parsing patterns.
- `docs/test-cases-api-markers.md`: API marker behavior and manual test cases.
- `TarkovDBEditor.csproj`: current framework and package dependencies.

When behavior changes, update the closest living reference document rather
than expanding this instruction file with implementation snapshots.
