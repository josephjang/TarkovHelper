# Seasonal Profile - Technical Spec

- **Created**: 2026-08-08

> The sibling `feature-seasonal-profile.md` holds the product decision. Write this
> on the work's branch and merge it in the same PR as the work. Nothing is kept
> current: fields are written once, discoveries are appended. A later change that
> reverses a decision here appends `Superseded by <doc>` below this line, in the PR
> that reverses it.

## Summary

Three ideas carry the change. First, "which profile am I on" stops being derived
from `GameMode`: `ProfileService` gains its own `AppProfile` concept with a third
member, and `GameMode` goes back to meaning only "what the game log said". Second,
the auto-switch suppression lives in exactly one place, a pure decision function
in `ProfileService` that the singleton and the unit tests both call, so the pinning
rule is testable without constructing the log-watching singleton. Third, the reset
gains the pieces it was missing rather than a new mechanism: the profile-scoped
clears already exist on the progress and inventory services, so the work is calling
them together, adding two profile-scoped deletes for `ProfileSettings` and
`RaidHistory`, and reloading the settings cache afterwards. `RaidHistory` needs a
new column before it can be reset by profile, for the reason in Current Behavior.

## Non-Goals

- No raid-history UI. This phase attributes rows so a reset can scope its delete;
  displaying them stays out of scope (nothing reads `RaidHistory` today).
- No new storage architecture. The seasonal profile is a third `ProfileId` value in
  the existing profile-keyed tables.
- No change to how quest events map to tasks in `LogSyncService`; only the sync
  window changes.
- No migration of any existing row to the seasonal profile. Pre-existing data stays
  exactly where it is.

## Current Behavior / Root Cause

Verified in this session against the working tree.

- **The app profile is a projection of `GameMode`.** `ProfileService.ActiveProfileId`
  is `_activeGameMode == GameMode.PVE ? "pve" : "pvp"`, and `GameMode` (in
  `Models/EftRaidEvent.cs`) has exactly `Unknown`, `PVP`, `PVE`. The active mode
  persists to the global `UserSettings` key `app.activeGameMode` as the literal
  `"PVE"` or `"PVP"`; `InitializeAsync` reads it back with `saved == "PVE" ? PVE :
  PVP`, so any unrecognized value already falls back to PvP.
- **The auto-switch is one branch.** `EftRaidEventService.ParseApplicationLogLine`
  matches `Session mode: (Pve|Pvp|Regular)` and raises `SessionModeDetected`;
  `ProfileService.OnRaidEvent` reacts with `SetActiveGameMode(mode, isAuto: true)`.
  `ScanLogFileForProfile` raises the same event during the startup scan, so the
  auto-switch also fires once at launch from the last session line in the newest
  log. `regular` maps to `PVP`, which is why a Kord Breach session lands in the PvP
  profile.
- **Profile-scoped stores already work.** `QuestProgress`, `ObjectiveProgress`,
  `ItemInventory`, `HideoutProgress`, and `ProfileSettings` all carry `ProfileId`
  in their primary key, and `QuestProgressService`, `HideoutProgressService`,
  `ItemInventoryService`, and `SettingsService` each subscribe to
  `ActiveProfileChanged` and reload. Adding a third `ProfileId` value needs no
  schema change for any of them.
- **`RaidHistory.ProfileId` is not the app profile.** It is written from
  `EftRaidInfo.ProfileId`, which `ParseApplicationLogLine` fills with the 24-hex
  EFT PMC or SCAV profile id parsed out of `TRACE-NetworkGameCreate` (or
  `SelectProfile` on the fallback paths). The table's only mode-ish column is
  `GameMode`, an int copied from `_currentGameMode`. So raid rows carry no app
  profile at all, and the column name that looks like attribution is taken by a
  different identifier. This corrects `feature-eft-1-1-roadmap.spec.md`, which
  reads `RaidHistory`'s nullable `ProfileId` as app-profile attribution; the
  roadmap's phase-1 scope is unchanged by the correction, but the mechanism is.
- **Nothing reads raid history.** `GetRaidHistoryAsync`, `GetRaidStatisticsAsync`,
  and `CleanupRaidHistoryAsync` have no callers outside `UserDataDbService`. Raid
  history is write-only today, which is why the PRD states attribution as a data
  guarantee rather than an on-screen one.
- **Reset clears two stores out of five.** `MainWindow.BtnResetProgress_Click`
  calls `QuestProgressService.ResetAllProgress` (quest plus objective) and
  `HideoutProgressService.ResetAllProgress`, both already scoped to
  `ActiveProfileId`. `ItemInventoryService.ResetAllInventory` exists, is equally
  scoped, and is never called. Nothing clears `ProfileSettings` or `RaidHistory`.
  The confirmation is a `MessageBox` with a hardcoded Korean-plus-English string,
  bypassing `LocalizationService` entirely.
- **The sync range is collected and dropped.** `SettingsService.SyncDaysRange`
  persists to `app.syncDaysRange` and `LogSyncService.SyncFromLogsAsync` takes a
  `daysRange` parameter that filters events by `e.Timestamp >= cutoffDate`, but
  `MainWindow.PerformQuestSync` calls `SyncFromLogsAsync(logPath, progress)` and
  lets the parameter default to `0`, which means "all logs".
- **Profile-specific settings are already enumerated.**
  `SettingsService.ProfileSpecificKeys` lists the eight keys that live in
  `ProfileSettings` (`app.playerLevel`, `app.scavRep`, `app.showLevelLockedQuests`,
  `app.dspDecodeCount`, `app.playerFaction`, `app.hasEodEdition`,
  `app.hasUnheardEdition`, `app.prestigeLevel`); everything else is global in
  `UserSettings`. `LoadProfileSettings` refreshes the cache on
  `ActiveProfileChanged` and fires the per-value events the UI listens to.
- **The switcher is a two-button toggle.** `MainWindow.xaml` holds `BtnPvP` and
  `BtnPvE` (`ToggleButton`, `GameModeToggleStyle`) plus the `TxtAutoIndicator`
  badge; `UpdateGameModeUI` sets `IsChecked` from the mode. The button contents are
  the literal strings `PvP` and `PvE` in XAML, while their tooltips come from
  `LocalizationService.HeaderPvpTooltip` / `HeaderPveTooltip`.

## Design

### Profile identity

`Models/EftRaidEvent.cs` keeps `GameMode` exactly as it is: a parsed log fact with
`Unknown`, `PVP`, `PVE`. A new `AppProfile` enum (`Models/AppProfile.cs`) carries
the app-level choice: `PvpZone`, `PveZone`, `PvpSeason`.

`ProfileService` changes shape around it:

- `SeasonProfileId = "season"` joins `PvpProfileId` and `PveProfileId`.
- `ActiveProfile` (type `AppProfile`) replaces `_activeGameMode` as the stored
  state; `ActiveProfileId` maps it to the id string. `ActiveGameMode` stays as a
  computed property (`PvpSeason` and `PvpZone` both report `GameMode.PVP`), since
  a seasonal raid does run under PvP rules and callers that ask about game rules
  should keep getting that answer.
- `ProfileChangedEventArgs` gains an `AppProfile Profile` property and keeps its
  existing `GameMode` and `IsAutoDetected` members, so the four services that
  subscribe only to reload (`QuestProgressService`, `HideoutProgressService`,
  `ItemInventoryService`, `SettingsService`) need no change at all, and only the
  `MainWindow` handler, which actually renders the selection, reads the new
  property.
- `SetActiveProfile(AppProfile profile, bool isAuto = false)` is the single mutator.
  `SetActiveGameMode(GameMode, bool)` stays as the log-facing adapter, mapping a
  detected mode to a profile and applying the pinning rule below; its two
  `MainWindow` call sites are replaced by `SetActiveProfile`.
- `GetProfileId(GameMode)` gains an `AppProfile` overload; the `GameMode` overload
  stays for `ConfigMigrationService` and the JSON migration paths, which
  deliberately target `pvp`.
- Persistence keeps the key `app.activeGameMode` and writes `"PVP"`, `"PVE"`, or
  `"SEASON"`. `InitializeAsync` parses the three values and falls back to
  `PvpZone`, which is what an older build already does with a value it does not
  recognize.

The pinning rule is a pure static function so it can be tested without the
singleton:

```
AppProfile ResolveDetectedProfile(AppProfile current, GameMode detected)
```

It returns `current` unchanged when `current` is `PvpSeason` (the suspension) or
when `detected` is `Unknown`, and otherwise maps `PVP` to `PvpZone` and `PVE` to
`PveZone`. `OnRaidEvent` and `SetActiveGameMode` both route through it.

### Reset

A new `ProfileResetService` (`Services/ProfileResetService.cs`) owns the whole
scope in one place, so no caller has to remember the list:

1. `QuestProgressService.ResetAllProgress()` (quest plus objective, in-memory and DB)
2. `HideoutProgressService.ResetAllProgress()`
3. `ItemInventoryService.ResetAllInventory()`
4. `UserDataDbService.ClearProfileSettingsAsync(profileId)` (new)
5. `UserDataDbService.ClearRaidHistoryForProfileAsync(profileId)` (new)
6. `SettingsService.ReloadProfileSettings()` (new public entry point wrapping the
   existing private `LoadProfileSettings` plus the same change-event fan-out
   `OnActiveProfileChanged` already performs)

Step 6 is the ripple that makes step 4 correct: `SettingsService` caches the
profile values in fields, so deleting the rows without a reload leaves the drawer
showing the old level and reputation until the next profile switch.

`MainWindow.BtnResetProgress_Click` becomes a localized confirmation naming the
profile and listing the six categories, then one `ProfileResetService` call, then
the existing `LoadAndShowQuestListAsync` refresh.

### Raid attribution

`RaidHistory` gains `AppProfileId TEXT` (nullable, no default) via an additive
`ALTER TABLE` in `UserDataDbService.CreateTablesAsync`, guarded by the same
`pragma_table_info` check `MigrateToProfileSchemaAsync` already uses. Existing rows
keep `NULL`, which is what makes the PRD's R7 true by construction.
`SaveRaidHistoryAsync` writes `ProfileService.Instance.ActiveProfileId` into it,
and `ClearRaidHistoryForProfileAsync` deletes `WHERE AppProfileId = @profileId`,
so `NULL` rows can never be caught by a reset. An index on `AppProfileId` is not
added: the only query is a delete on a table nothing reads.

### Sync window

`MainWindow.PerformQuestSync` passes `_settingsService.SyncDaysRange` as the third
argument to `SyncFromLogsAsync`. No other call site exists.

### UI and localization

`MainWindow.xaml` replaces the two-button toggle with three `ToggleButton`s
(`BtnPvpZone`, `BtnPveZone`, `BtnPvpSeason`) in the same `Border`, keeping
`GameModeToggleStyle` and `TxtAutoIndicator`. Button contents move out of XAML into
`ApplyLocalization`, since the labels are now translated. `UpdateGameModeUI` becomes
`UpdateProfileUI(AppProfile, bool isAuto)` and sets the three `IsChecked` values
from the active profile.

New `LocalizationService.Header.cs` strings, values fixed by the game's own client
in each language (see the PRD's label decision):

| Key | EN | KO | JA |
| --- | --- | --- | --- |
| `HeaderPvpZone` | PvP Zone | PvP 존 | PvP ゾーン |
| `HeaderPveZone` | PvE Zone | PvE 존 | PvE ゾーン |
| `HeaderPvpSeason` | PvP Season | 시즌 PvP | PvP シーズン |

`HeaderPvpTooltip` and `HeaderPveTooltip` keep their keys with their wording
updated to the new labels, and `HeaderPvpSeasonTooltip` joins them. The reset
dialog adds `ResetProfileTitle`, `ResetProfileConfirmFormat` (one `{0}` for the
profile label), `ResetProfileScopeList`, and `ResetProfileDoneFormat`. Every new
key joins the `HeaderKeys` array in `LocalizationHeaderStringsTests`, and the
format keys join `FormatKeys`.

### File list

- `TarkovHelper/Models/AppProfile.cs` (new)
- `TarkovHelper/Services/ProfileService.cs`
- `TarkovHelper/Services/ProfileResetService.cs` (new)
- `TarkovHelper/Services/UserDataDbService.cs` (`AppProfileId` column, its
  migration, `ClearProfileSettingsAsync`, `ClearRaidHistoryForProfileAsync`,
  `SaveRaidHistoryAsync`)
- `TarkovHelper/Services/SettingsService.cs` (`ReloadProfileSettings`)
- `TarkovHelper/Services/LocalizationService.Header.cs`
- `TarkovHelper/MainWindow.xaml`, `TarkovHelper/MainWindow.xaml.cs`
- `TarkovHelper.Tests/ProfileSwitchingTests.cs` (new)
- `TarkovHelper.Tests/ProfileResetTests.cs` (new)
- `TarkovHelper.Tests/LogSyncRangeTests.cs` (new)
- `TarkovHelper.Tests/SeasonalProfileE2ETests.cs` (new)
- `TarkovHelper.Tests/LocalizationHeaderStringsTests.cs`

## Technical Decisions

**A separate `AppProfile` enum instead of a third `GameMode` member.** Adding
`GameMode.Season = 3` would be a smaller diff and was the first instinct, but
`GameMode` is a parsed log fact that is also persisted as an integer in
`RaidHistory.GameMode`: a new member would let a value the log can never produce
be written into stored rows, and would make every `GameMode` switch in the parser
answer a question the parser cannot answer. Splitting the two concepts costs one
small enum and keeps `GameMode` meaning exactly what `Session mode:` said.

**The suspension is a pure function, not a flag.** The alternative was a
`_seasonPinned` boolean set when the user picks the seasonal profile and cleared
when they leave it, but that is derived state that can disagree with the active
profile, and it is the kind of thing a later refactor silently drops.
`ResolveDetectedProfile(current, detected)` has no state to fall out of sync: the
active profile is the pin. It is also directly unit-testable, which the singleton
is not: its constructor wires itself to the `EftRaidEventService` singleton, and
every mutation writes through `UserDataDbService`, so exercising the rule through
`SetActiveGameMode` would mean standing up a real user database to assert a
decision that touches no data.

**`AppProfileId` as a new column rather than reusing `RaidHistory.ProfileId`.**
Reusing the existing column would need the EFT profile id to move elsewhere and
would silently change the meaning of every stored row, including for anyone who
downgrades. A new nullable column is additive, leaves old rows untouched, and
makes "unattributed" a representable state rather than an inference.

**Reset is one service, not a widened button handler.** Chaining five calls in
`BtnResetProgress_Click` would work and would be less code, but the reset scope is
exactly the thing later phases extend (the roadmap's loyalty phase adds
profile-scoped trader levels, which land in `ProfileSettings` and are therefore
covered automatically). A named service is where that scope is discoverable, and
it gives the reset scope one test target instead of a UI handler.

**The sync window stays a single global setting.** Making `SyncDaysRange`
profile-scoped was considered, since a fresh seasonal profile wants a narrower
window than a long-lived PvP one. Declined for this phase: the setting is a
troubleshooting control, per-profile copies would need drawer UI that the PRD does
not ask for, and the honest fix is making the existing control work. Recorded here
so the option is on the record if season starts prove to need it.

## Open Questions

- Does a Kord Breach session leave a distinguishable signature in the client logs
  (a new `Session mode:` token, a distinct server address range, a different PMC
  profile id from `SelectProfile`)? Settled by capturing an application log from a
  real seasonal session and diffing it against a permanent-profile one. If a
  signature exists, `EftRaidEventService`'s regex and `ResolveDetectedProfile`
  learn it and the auto-switch selects the seasonal profile directly; if not, the
  pinning above is the shipped behavior. Either outcome is appended here.
- Does the seasonal character report a different PMC profile id? If it does, that
  id is a second, independent signature, and it would also mean
  `eft.pmcProfileId` in `UserSettings` flips between characters, which the SCAV id
  derivation in `EftProfileInfo.IsScavProfile` assumes is stable within a session.
  Checked as part of the same log capture.

## Test Strategy

- **Unit, `ProfileSwitchingTests`**: `ResolveDetectedProfile` as a matrix. A
  detected `PVP` or `PVE` while the active profile is `PvpSeason` returns
  `PvpSeason` (the pin, both directions, which is the bug the PRD's rejected
  half-suppression would reintroduce); a detected `Unknown` never changes the
  profile; from `PvpZone` or `PveZone` a detection maps as it does today
  (`Regular` and `Pvp` to `PvpZone`, `Pve` to `PveZone`). Plus round-tripping the
  three persisted strings through the `InitializeAsync` parse, including an
  unrecognized value falling back to `PvpZone`.
- **Unit, `ProfileResetTests`**: against a temp `user_data.db` (a fresh
  `TARKOVHELPER_CONFIG_PATH` per test class, as `E2EHarnessIsolationTests` does
  for its temp directory), seed all five stores plus raid rows under two profiles
  and one raid row with `NULL AppProfileId`; reset one profile; assert that
  profile's rows are gone, the other profile's rows are untouched (isolation in
  both directions), the `NULL` row survives, and the `UserSettings` rows are
  untouched. This is the guard that keeps a later "clear everything" refactor from
  widening past the active profile.
- **Unit, `LogSyncRangeTests`**: `SyncFromLogsAsync` against a log fixture
  spanning old and recent events returns only the in-window events for
  `daysRange > 0` and every event for `0`, including the boundary case of an
  event exactly at the cutoff. These are also the first fixture-based tests for
  `LogSyncService`, which the roadmap's phase-1 scope calls for.
- **E2E, `SeasonalProfileE2ETests`**: launch against a throwaway config, mark a
  quest done under PvP Zone, switch to PvP Season, assert the quest reads as not
  done, mark a different quest done, switch back, and assert the first is still
  done and the second is not. This is the PRD's R2 end to end.
- **Not automated**: two things, both stated rather than quietly skipped. The
  seasonal log signature needs a real Kord Breach session, so it is answered by
  capture and recorded in Open Questions; whatever it settles gets its own
  fixture-based parser test in the same PR. And the one-argument wiring in
  `PerformQuestSync` sits inside a private `async void` UI handler with no seam
  worth inventing for it, so it is covered by the manual check in Verification
  (a sync with the range set to a few days reports the filtered count in the
  progress line) rather than by a test that would only assert a mock.

## Verification

- `dotnet build TarkovHelper.sln`: clean.
- `dotnet test TarkovHelper.Tests --filter "Category!=E2E"`: full non-E2E suite
  green, including the new unit tests and the decision-doc invariants.
- `dotnet test TarkovHelper.Tests --filter "FullyQualifiedName~SeasonalProfileE2E"`:
  the isolation path on a real app launch.
- Manual, on a Debug build launched as
  `dotnet TarkovHelper/bin/Debug/net8.0-windows/TarkovHelper.dll`: the switcher
  shows three localized labels in each of EN, KO, and JA; selecting PvP Season and
  then starting the game does not move the selection off it; Reset Progress names
  the active profile and, after confirming, leaves the drawer at default level and
  reputation; and a quest sync with the range set to a few days reports the
  filtered event count in its progress line instead of scanning every log.

## Risks & Migration

- **Schema.** The `AppProfileId` column is added by `ALTER TABLE` on an
  app-owned database, guarded by a `pragma_table_info` check so a second launch is
  a no-op. Existing rows keep `NULL`.
- **Downgrade.** A build without this change reads `app.activeGameMode = "SEASON"`
  and falls back to PvP, so it shows the PvP profile and writes into it. Seasonal
  rows stay in the database untouched, and re-upgrading restores the view. Worth
  stating because the app auto-updates: the failure mode is invisible seasonal
  data, not lost seasonal data.
- **The startup auto-switch fires before the user can act.**
  `ScanLogFileForProfile` raises `SessionModeDetected` during monitoring startup,
  after `ProfileService.InitializeAsync` has restored the saved profile. The
  ordering matters: if the pin were applied only to live events, a restart while
  on the seasonal profile would silently switch to PvP from the last log line.
  Routing both paths through `ResolveDetectedProfile` is what closes this, and the
  unit matrix covers it.
- **Rollback.** The app side rolls back by releasing the prior build; the column
  and any `season` rows are inert to it (see Downgrade). No data publish is
  involved in this phase.
