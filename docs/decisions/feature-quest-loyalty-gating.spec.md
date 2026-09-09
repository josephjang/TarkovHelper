# Quest Loyalty Gating - Technical Spec

- **Created**: 2026-09-07

> The sibling `feature-quest-loyalty-gating.md` holds the product decision.
> Write this on the work's branch and merge it in the same PR as the work.
> Nothing is kept current: fields are written once, discoveries are appended. A
> later change that reverses a decision here appends `Superseded by <doc>` below
> this line, in the PR that reverses it.

## Summary

Three ideas carry the design. First, loyalty flows through the shape every
existing gate already has: `QuestDbService` reads the `QuestTraderRequirements`
table the 1.1 refresh published into a list on `TarkovTask`, the settings
snapshot gains a ninth profile-scoped value holding the entered levels, and
`QuestProgressService.GetStatus` gains one more check that folds into
`LevelLocked`, exactly as Scav karma does. Second, the entered levels are
per-trader rows under one key prefix in `ProfileSettings`, keyed by the
tarkov.dev trader id the requirement rows already carry, so the complete
profile reset wipes them without learning anything new and a data publish that
names a new trader needs no app change. Third, the drawer's trader roster and
every name it shows come from the loaded database, joined to `Traders` for the
localized name; the only new constant is the input's upper bound of four,
which a content test over the published data keeps honest. The bounds audit
the roadmap assigned to this phase changes two constants
(`SettingsService.MaxPrestigeLevel` to 6, `SettingsService.MinScavRep` to
-7.0) and confirms the rest. No schema, pipeline or data change.

## Non-Goals

- No new `QuestStatus` member and no change to the chip vocabulary or
  `QuestStatusTags` (the roadmap spec's decision; the chip filter's literal
  oracle stays as pinned in `feature-quest-chip-only-status-filter.spec.md`).
- No schema change, no `Traders.MaxLoyaltyLevel` column, no pipeline change in
  TarkovDBEditor, no data publish, no `DataFormatBaseline` movement.
- No per-quest loyalty override and no automatic derivation of loyalty.
- No import of the upstream `reputation` trader requirements (the 1.1 refresh's
  non-goal stands).
- No change to `QuestListPage.BuildUnlockRank` (the forward-progression sort).
- No change to the hideout page: `HideoutTraderRequirement` rows keep rendering
  as text, uncoloured against the entered loyalty.
- No hot reload beyond what the quest rows already get: the drawer's roster is
  computed inside the quest load, so it refreshes when the quest rows do.
- No Collector page work (phase 5).

## Current Behavior

Verified at HEAD e472945 (branch `pearlside` == `main`, release v2026.8.0), the
published `data/v1/tarkov_data.db` (db_version 1.1.0), and live upstream probes
on 2026-09-07.

### Published data

- `QuestTraderRequirements` (schema in `docs/database-schema.md`): `Id`,
  `QuestId`, `TraderId`, `TraderName`, `RequiredLevel`, editor bookkeeping
  columns, `FOREIGN KEY (QuestId) REFERENCES Quests(Id) ON DELETE CASCADE`,
  index on `QuestId`. 107 rows on 94 quests: 38 at level 2, 34 at level 3, 35 at
  level 4, none at 1 or above 4. Seven distinct traders (Prapor, Therapist,
  Skier, Peacekeeper, Mechanic, Ragman, Jaeger), every `TraderId` resolving to a
  `Traders` row. 92 rows name the quest's own `Quests.Trader` (byte-equal
  nickname strings, as the 1.1 refresh recorded); 15 rows on 5 quests name
  another trader: Broadcast - Part 1 (Mechanic; Jaeger 2), Chemical - Part 3
  (Skier; Jaeger 2, its only row), The Good Times - Part 1 (Prapor; Mechanic,
  Therapist, Skier, Ragman at 2), Thirsty - Hounds (Jaeger; Therapist, Skier at
  2), Collector (Fence; seven traders at 4). 19 quests have `BsgId` NULL (the 18
  wiki-only seasonal pages plus Historical Perspectives) and none of them has a
  row. 27 gated quests have no `QuestRequirements` row, `MinLevel` at or below
  `SettingsService.DefaultPlayerLevel` (15) and no other gate, so they read as
  Active on a fresh profile today; 19 gated quests also have prerequisites.
- `Traders`: 16 rows with `Id`, `Name`, `NameKO` (all 16), `NameJA` (3),
  `NormalizedName` (all 16), `ImageLink`. No sort-order or level-count column.
- Other bounds in the data: `MinLevel` maximum 55; `RequiredPrestigeLevel`
  values 1, 2, 3 (the New Beginning Prestige 2 to 4 rows; Prestige 5 and 6 left
  the app in the refresh); `MinScavKarma` values -3, -1, 1, 2, 3, 4;
  `RequiredDecodeCount` 1, 2, 3; `RequiredEdition` EOD on 12 rows,
  `ExcludedEdition` Unheard on 1.

### Upstream (json.tarkov.dev, `regular` mode, 2026-09-07)

- `traders`: 16 traders. `levels` arrays: Prapor, Therapist, Skier, Peacekeeper,
  Mechanic, Ragman, Jaeger and Ref have four (levels 1 to 4, each with
  `requiredPlayerLevel` and `requiredReputation`; Prapor 2 needs level 6 and
  reputation 0.7, Prapor 4 level 36 and 7.9); Fence has three (0 to 2, keyed on
  reputation) plus 14 `reputationLevels` bands whose `minimumReputation` runs
  from -7 to 6; Lightkeeper, BTR Driver, Taran, Radio station, Mr. Kerman,
  Voevoda and Survivor have one.
- `tasks`: the `prestige` list has six entries, named Prestige 1 to Prestige 6
  in `tasks_en`. The wiki's Prestige page has six `==Prestige N==` sections,
  each with a New Beginning (Prestige N) quest.
- `items`: `playerLevels` has 79 entries, the last `level` 79.
- `endpoints`: unchanged since the 1.1 refresh; `pvp-season/info` added.

### App

- **Loading.** `QuestDbService.LoadQuestsAsync` reads `Quests`
  (`LoadBaseQuestsAsync`, feature-detecting columns through
  `ColumnExistsAsync`), then `QuestRequirements`, `QuestObjectives`,
  `QuestRequiredItems`, `OptionalQuests` (each behind `TableExistsAsync`),
  builds `LeadsTo`, and swaps `_allQuests` and both lookups atomically. Nothing
  reads `QuestTraderRequirements`. `HideoutDbService.LoadTraderRequirementsAsync`
  reads the sibling `HideoutTraderRequirements` table into
  `HideoutTraderRequirement { TraderId, TraderName, TraderNameKo, TraderNameJa,
  Level }` behind `TableExistsAsync`: the shape to mirror. `TraderDbService`
  loads `Traders` into `TarkovTrader` (Id, Name, NameKo, NameJa,
  NormalizedName, ImageLink), indexed by id and by name, and reloads on
  `DatabaseUpdated`.
- **Model.** `TarkovTask` carries `RequiredLevel`, `RequiredScavKarma`,
  `RequiredPrestigeLevel` (its doc comment still says 0 to 5),
  `RequiredDecodeCount`, `RequiredEdition`, `ExcludedEdition`, `Faction`,
  `TaskRequirements`; no loyalty field.
- **Engine.** `QuestProgressService.GetStatus(task, snapshot, settings)` runs
  done/failed, then edition, prestige, faction (Unavailable), DSP and
  prerequisites (Locked), then level and Scav karma (both `LevelLocked`), then
  Active. `IsLevelRequirementMet` and `IsScavKarmaRequirementMet` are
  `internal static` over a `ProfileSettingsSnapshot`, each with a public
  instance overload reading `SettingsService.Instance.ProfileSettings`. The
  prerequisite walk (`ArePrerequisitesMet`) recurses with the same snapshot pair
  under a thread-static visited set. Consumers of the status: `QuestListPage`
  (rows, detail, `UpdateStatusChips` via `QuestListFilter.CountByStatusTag`,
  where the Locked chip counts `Locked` and `LevelLocked` together),
  `QuestRecommendationService` (Active only), `CollectorPage` (Collector and
  its prerequisites), `GetStatistics`.
- **Badges.** `QuestListPage.GetStatusText(status, task)` returns `Lv.{n}` when
  the level gate is unmet, else `Rep {n}` when karma is, else the literal
  `Level` for `LevelLocked`; the detail badge calls `GetStatusText(status)`
  without the task, so it reads `Level` for every level-locked quest. The
  detail pane's Requirements section (`RequirementsSectionWrapper`,
  `TxtRequiredLevel`, `TxtRequiredScavKarma`) formats English literals
  (`Level {0} (Current: {1})`, `Scav Karma {sign} {0} (Current: {1})`) and
  colours unmet lines with `LevelLockedBrush`. `HideoutPage` renders a trader
  requirement as `- {name} Lv.{level}` through `GetLocalizedTraderName`, which
  picks `TraderNameKo`/`TraderNameJa` by `LocalizationService.CurrentLanguage`.
- **Settings.** `ProfileSettingsSnapshot` is a positional record of eight
  values plus `ProfileId` and `Revision`; `From(profileId, revision, values)`
  parses one profile's rows by exact key and clamps every bounded value;
  `Defaults` is all-null; each value has an `...OrDefault` getter.
  `SettingsService` owns the keys (`KeyPlayerLevel` and the seven others),
  `ProfileSpecificKeys` (the list the one-time UserSettings-to-ProfileSettings
  migration walks, not the reset's list), `ProfileKeysSurvivingReset` (the two
  edition keys), the bounds (`MinPlayerLevel` 1, `MaxPlayerLevel` 79,
  `MinScavRep` -6.0, `MaxScavRep` 6.0, `MinDspDecodeCount` 0,
  `MaxDspDecodeCount` 3, `MinPrestigeLevel` 0, `MaxPrestigeLevel` 5), one
  property setter per value going through `ApplyProfileEdit` (compare-and-swap
  publish, `SaveProfileSetting(origin.ProfileId, key, value)`, then the
  value's changed event), and `RaiseProfileSettingsChanged`, which announces
  the seven events of a loaded snapshot under a reference-equality guard.
  `UserDataDbService.LoadProfileSettings(profileId)` returns every row of the
  profile in an ordinal dictionary, so a key the reader does not know is simply
  ignored. The complete reset (`feature-complete-profile-reset.spec.md`)
  deletes every `ProfileSettings` row whose key is not in
  `ProfileKeysSurvivingReset` (`DeleteProfileSettingsExceptAsync`): deletion
  is the default for a new key. `ConfigMigrationService` imports five legacy
  keys from `app_settings.json`; loyalty never existed there.
- **Drawer.** `MainWindow.xaml` `ProfileDrawer` is one horizontal `StackPanel`
  of five bordered groups (level stepper, Scav Rep stepper, DSP buttons
  `BtnDsp0..3` sharing `BtnDsp_Click` with a `Tag`, edition checkboxes,
  prestige stepper), centred, shown by `BtnProfile`. Every `Update*UI` repaint
  runs under `SuppressSettingsEcho`; `UpdateDspDecodeUI` highlights the
  selected button with `AccentBrush`; `UpdatePrestigeLevelUI` enables the
  steppers against `Min`/`MaxPrestigeLevel`. `MainWindow` subscribes to the
  settings events to repaint its own controls only; `QuestListPage` subscribes
  to the same seven events and collapses them through a `RefreshCoalescer`
  (`_settingsRefresh`) into one `RefreshForSettingsChange`. Window `MinWidth`
  is 600. `LocalizationService.Header` holds `ProfileLevelLabel`,
  `ProfileScavRepLabel`, `ProfileDspLabel`, `ProfileEditionLabel`,
  `ProfilePrestigeLabel`, `HeaderProfileTooltip` ("level, Scav Rep, DSP,
  edition, prestige") and `ProfileResetCategories` (the reset dialog's list of
  what is cleared, ending "Player level, Scav Rep, faction, prestige, and DSP
  decode count").
- **Tests.** `ProgressServiceHarness.Create` builds a `QuestProgressService`
  around explicit tasks and a snapshot; `QuestStatusSettingsSnapshotTests`
  constructs `ProfileSettingsSnapshot` by named arguments;
  `SettingsSetterContractTests.AllSetters` is a table of one `SetterCase` per
  profile setting (key, stored value, expected snapshot, event) driven by
  theories, with `SettingsServiceTestSupport` seeding snapshots;
  `ProfileResetStoreTests` and `ProfileResetHooksTests` assert which rows a
  reset clears; `PublishedDataContentTests` already pins the loyalty table
  (at least 100 rows, Collector's seven traders at 4, Chemical - Part 3 naming
  Jaeger at 2); `E2EQuestData` derives fixture quests from the seed by SQL
  fragments (`Ungated`, `ReachableAtDefaultLevel`, `NoAlternatives`,
  `UniqueSearchName`); `E2EDb.SeedProfileSetting` seeds a profile row before
  launch; `HeaderE2ETests.Profile_drawer_holds_the_level_stepper` and
  `QuestTabDriver` (chips by `Chip{Tag}` AutomationId, `ItemStatus`
  Selected/Unselected) are the drawer and quest-tab e2e surfaces;
  `LegacySmokeE2ETests` reads `TxtDetailStatus` for a quest's status text.

## Design

### 1. Reading the requirement rows (`QuestDbService`, `TarkovTask`)

- New model `QuestTraderRequirement` in `Models/TarkovTask.cs`:
  `TraderId`, `TraderName`, `NormalizedName`, `Level` (JSON names `traderId`,
  `traderName`, `normalizedName`, `level`, matching
  `HideoutTraderRequirement`). `NormalizedName` is stamped on the row by the
  loader from the `Traders` table rather than derived by a reader, and it is
  display order only; the gate compares `TraderId`. `TarkovTask` gains
  `List<QuestTraderRequirement>? TraderLoyaltyRequirements` (`[JsonPropertyName
  ("traderLoyaltyRequirements")]`) and `bool HasTraderLoyaltyRequirements =>
  TraderLoyaltyRequirements is { Count: > 0 }`, the `HasAlternatives` pattern.
- `QuestDbService.LoadQuestsAsync` gains step 5b,
  `LoadQuestTraderRequirementsAsync(connection, questLookup)`, after
  `LoadOptionalQuestsAsync`: behind `TableExistsAsync("QuestTraderRequirements")`
  (absent table means no rows, no warning, no failure: a database from before
  the refresh is a legal input), `SELECT QuestId, TraderId, TraderName,
  RequiredLevel FROM QuestTraderRequirements ORDER BY QuestId, TraderName`;
  a row whose `QuestId` is not in the lookup is skipped with a debug log, as
  the other child loaders do; a row whose `TraderId` or `TraderName` is empty
  or whose level is below 1 is skipped with a warning. The row-to-task
  attachment is a separate `internal static` method over an open connection
  and the lookup, so it is unit-testable against an in-memory database.
- `QuestDbService` exposes the roster the drawer needs, computed inside the
  same atomic swap: `IReadOnlyList<LoyaltyTrader> LoyaltyTraders`, where
  `LoyaltyTrader` is `(string TraderId, string TraderName, string
  NormalizedName)`, the distinct traders named by any loaded row, in the order
  given by `TraderDbService.DisplayRank` (section 4), ties broken by nickname
  and then by id so one database always builds one order. Empty when the table
  is absent.

### 2. Storing the entered levels (`SettingsService`, `ProfileSettingsSnapshot`)

- New immutable value type `TraderLoyaltyLevels` in `Services/Settings/`:
  wraps a sorted, read-only map from trader id to level; `Empty`;
  `LevelOf(traderId)` returns the stored level or
  `SettingsService.DefaultTraderLoyaltyLevel`; `With(traderId, level)`
  returns a new instance (the same instance when the value is unchanged, so
  the setter's "value differs" guard is a reference check); `Entries` for
  enumeration; structural `Equals`/`GetHashCode` and a readable `ToString`.
  Structural equality is what lets the record keep value semantics (section
  Technical Decisions).
- `ProfileSettingsSnapshot` gains a ninth positional value,
  `TraderLoyaltyLevels TraderLoyalty` (never null; `Defaults` uses `Empty`).
  `From` collects every row whose key starts with
  `SettingsService.TraderLoyaltyKeyPrefix` (`app.traderLoyalty.`), takes the
  remainder as the trader id, parses the value as an int and clamps it to
  [`MinTraderLoyaltyLevel`, `MaxTraderLoyaltyLevel`] (1 to 4), the way the
  other bounded values are clamped there and for the same reason (hand-edited
  rows reach the gate unbounded otherwise); an unparsable value or an empty id
  leaves no entry. Ids are not validated against the roster: a row for a
  trader the current data does not gate on is kept, harmless and ready for the
  publish that names that trader.
- `SettingsService` gains the constants `MinTraderLoyaltyLevel = 1`,
  `MaxTraderLoyaltyLevel = 4`, `DefaultTraderLoyaltyLevel = 1`,
  `TraderLoyaltyKeyPrefix`, and `internal static string TraderLoyaltyKey(
  string traderId)`; `int GetTraderLoyalty(string traderId)` (reads
  `ProfileSettings.TraderLoyalty.LevelOf`); `void SetTraderLoyalty(string
  traderId, int level)` (clamps, then `ApplyProfileEdit(s =>
  ReferenceEquals(next, s.TraderLoyalty) ? null : s with { TraderLoyalty =
  next }, TraderLoyaltyKey(traderId), clamped.ToString(), raise)`); the event
  `EventHandler<TraderLoyaltyChange>? TraderLoyaltyChanged` with
  `TraderLoyaltyChange(string TraderId, int Level)`; and one more `Announce`
  in `RaiseProfileSettingsChanged` raising it once per stored entry of the
  loaded snapshot (a load that finds no entries raises nothing, since every
  reader answers the default without being told). The prefix is declared
  beside `ProfileSpecificKeys` with a comment saying why it is not in that
  list (that list is the legacy migration walk, and loyalty has no legacy
  value) and not in `ProfileKeysSurvivingReset` (the reset deletes it, per
  the roadmap's R4).
- Nothing changes in `UserDataDbService`: the rows ride
  `LoadProfileSettings`, `SetProfileSettingAsync` and the reset's
  `DeleteProfileSettingsExceptAsync` as they are.

### 3. The gate and the badge (`QuestProgressService`, `QuestRequirementBadge`)

- `QuestProgressService` gains `IsTraderLoyaltyRequirementMet(task, settings)`
  (`internal static`, with the public instance overload the two neighbours
  have): true when the task has no rows, else true only when every row's
  `settings.TraderLoyalty.LevelOf(row.TraderId) >= row.Level`. `GetStatus`
  calls it after `IsScavKarmaRequirementMet` and returns `LevelLocked` on
  failure. The walk's order is otherwise untouched, and the prerequisite
  recursion carries the same snapshot and so evaluates a loyalty-locked
  prerequisite as not done, which is right.
- `QuestProgressService` also gains `FirstUnmetTraderLoyalty(task, settings)`
  (`internal static`), returning the row the badge should name or null: the
  first unmet entry, because the rows already arrive in badge order. That
  order is applied once, at load, by `QuestDbService.SortIntoBadgeOrder` - the
  giver's row first (giver means `row.TraderName` equals `task.Trader`
  case-insensitively), then `TraderDbService.DisplayRank`, then the nickname
  so unranked newcomers are alphabetical among themselves. One ordering rule,
  in one place, read by the row badge, the detail badge, the Requirements
  lines and the tests.
- The walk reports WHICH gate stopped it, so nothing downstream has to
  re-derive the precedence. `QuestProgressService` gains an internal
  `QuestGate` enum (`None`, `RequiredEdition`, `ExcludedEdition`,
  `PrestigeLevel`, `Faction`, `DecodeCount`, `Prerequisite`, `PlayerLevel`,
  `ScavKarma`, `TraderLoyalty`) and a `GetStatus(task, snapshot, settings, out
  QuestGate gate)` overload. The existing three-argument overload delegates to
  it with `out _`, so its call sites are untouched. `IsEditionRequirementMet`
  keeps its signature and its answer; the walk reads a private
  `UnmetEditionGate` beside it, which says which of the two edition rules
  failed.
- New pure helper `QuestRequirementBadge` (`Pages/QuestRequirementBadge.cs`,
  static): `string? For(QuestGate gate, TarkovTask task,
  ProfileSettingsSnapshot settings, Func<QuestTraderRequirement, string>
  traderDisplayName)` is a switch over the reported gate, answering `Lv.{n}`,
  `Rep {n:0.#}`, `LL{n}` or `{name} LL{n}` (the name only when the row is not
  the giver's), `EOD`/`Unheard`, `Edition`, `P.{n}`, `BEAR`/`USEC`, and null
  for a gate that names no value the player can read off a badge.
  `StatusText(status, gate, task, settings, traderDisplayName)` wraps it and
  falls back to the status word when `For` answers null, so the badge never
  goes blank.
- `QuestListPage.GetStatusText(status, gate, task, pass)` is a thin adapter
  over `StatusText`. The gate comes from the same `StatusIn` call as the
  status, never from a second walk, and both it and the task are required, so
  the detail badge (`TxtDetailStatus`), the prerequisite rows and the
  alternative-quest rows name the gate the way the row does instead of reading
  the literal `Level` or `N/A`.
- Trader display names for the badge and the detail pane come from
  `LocalizationService.GetTraderDisplayName(string traderId, string
  fallback)`: `TraderDbService.GetTraderById`, then `NameKo`/`NameJa` by
  `CurrentLanguage`, then the fallback (the row's `TraderName`). The same
  helper feeds the drawer labels.

### 4. Drawer, detail pane, strings (`MainWindow`, `QuestListPage`, `LocalizationService`)

- `ProfileDrawer`'s content becomes a vertical `StackPanel` of two rows, both
  of them wrapping. The existing group row (level, Scav Rep, DSP, edition,
  prestige) turns from a horizontal `StackPanel` into a `WrapPanel` as well:
  adding the loyalty row made it worth checking, and at the 600 pixel minimum
  window those five groups are already wider than the drawer, so the
  horizontal `StackPanel` centred them and clipped both ends, taking the level
  stepper off screen. Below it a `LoyaltySection` `StackPanel` (collapsed when
  the roster is empty, so the heading and the groups it names hide together)
  holds a second `WrapPanel` (`LoyaltyGroup`) with one bordered
  group per roster trader in the DSP control's shape: a label
  (`AutomationId` `Loyalty_{normalizedName}`) with the localized trader name,
  then four buttons `1` to `4` (`AutomationId` `Loyalty_{normalizedName}_{n}`,
  `Tag` = trader id and level) sharing one `LevelButton_Click`, which calls
  `SetTraderLoyalty`. The group label `TxtLoyaltyLabel` reads
  `ProfileLoyaltyLabel`. `normalizedName` is `Traders.NormalizedName`
  (falling back to the lower-cased `TraderName` when the trader row is
  missing). Both panels wrap, so the five upper groups and the seven loyalty
  groups each fit the 600 pixel minimum window over as many lines as they
  need; the drawer's existing top margin logic is untouched.
- Those controls belong to `Pages/Components/TraderLoyaltyPanel.cs`, which is
  passive the way `QuestRecommendationsPanel` is: it subscribes to nothing and
  owns no lifecycle, and the window's echo guard reaches it as an
  `IsInputSuppressed` callback instead of the panel reaching back into the
  window. `Rebuild(traders)` builds one group per roster trader and collapses
  `LoyaltySection` when the roster is empty; `Repaint()` paints the highlight
  from `GetTraderLoyalty` per trader, sets `AutomationProperties.ItemStatus`
  to `Selected` on the level button that holds the value and `Unselected` on
  the rest (the chip convention, for the e2e), and composes each button's
  tooltip. A plain class rather than a `UserControl`, because the roster is
  data and there is no markup to declare beyond the section and its
  `WrapPanel`; those stay in `MainWindow.xaml`, where `ProfileDrawerFitTests`
  measures the real drawer at the minimum window.
- `MainWindow` keeps the three subscriptions that drive the panel and calls it
  from its own handlers. `BuildLoyaltyGroup()` runs after the initial data
  load and again on `QuestDbService.DataRefreshed`, handing the panel
  `QuestDbService.LoyaltyTraders`; `UpdateLoyaltyUI()` repaints under
  `SuppressSettingsEcho`. The two handlers that repaint,
  `TraderLoyaltyChanged` and `ProfileSettingsReloaded`, book one shared
  `RefreshCoalescer` rather than repainting inline, so a published fan-out
  repaints the drawer once instead of once per stored entry.
- `TraderDbService.DisplayRank(string normalizedName)` returns the position of
  the name in the game's trader order (prapor, therapist, fence, skier,
  peacekeeper, mechanic, ragman, jaeger, ref, lightkeeper, btr-driver) and
  `int.MaxValue` for anything else, so newcomers sort after the known names
  alphabetically. The list is a static array with a comment naming this
  spec; it is display order only and gates nothing.
- `QuestListPage` subscribes to `TraderLoyaltyChanged` with the same
  `_settingsRefresh.Request()` the seven other events use; the comment
  counting "seven events" moves to "eight". The detail pane's Requirements
  section becomes ONE `ItemsControl` (`RequirementsList`) over a list of
  `RequirementLineViewModel`: the named `TxtRequiredLevel` and
  `TxtRequiredScavKarma` `TextBlock`s go, and the level line, the karma line
  and one line per row of `task.TraderLoyaltyRequirements` are built together
  by `RequirementLineViewModel.BuildFor` in badge order (the loyalty rows in
  the order they were sorted into at load), each coloured with
  `LevelLockedBrush` while unmet. Every "met" answer comes from
  `QuestProgressService` rather than a comparison written in the page, and
  `RequirementsSectionWrapper` shows on the line count instead of on a term
  per kind. While the section is rebuilt, its two English literals move to
  `LocalizationService.Quest` (`RequirementLevelFormat`,
  `RequirementScavKarmaFormat`) beside the new `RequirementLoyaltyFormat`, so
  the three lines read in one language.
- Strings (`LocalizationService.Header`, `LocalizationService.Quest`), each
  with KO and JA: `ProfileLoyaltyLabel` ("Loyalty"), `RequirementLevelFormat`,
  `RequirementScavKarmaFormat`, `RequirementLoyaltyFormat` ("{0} LL{1}
  (Current: {2})"); `HeaderProfileTooltip` gains "loyalty";
  `ProfileResetCategories` gains "trader loyalty" on its last line. The badge
  strings `LL{n}` and `{name} LL{n}` stay English like `Lv.` and `Rep`, per
  the chip localization non-goal already recorded.

### 5. Bounds audit

- `SettingsService.MaxPrestigeLevel` 5 to 6; the `TarkovTask.RequiredPrestigeLevel`
  comment to 0 to 6. `ProfileSettingsSnapshot.From` and
  `UpdatePrestigeLevelUI` read the constant and need no edit.
- `SettingsService.MinScavRep` -6.0 to -7.0. `From`, the setter's clamp,
  `LegacyAppSettingsValues` and `UpdateScavRepUI` read the constant.
- `MaxPlayerLevel` 79, `MaxDspDecodeCount` 3, the edition strings accepted by
  `IsEditionRequirementMet`: verified against the data and upstream, unchanged;
  the verification is recorded in Current Behavior and pinned by the new
  bounds test (Test Strategy) so the next patch's audit is a test run.

### Files touched

- `TarkovHelper/Models/TarkovTask.cs` (`QuestTraderRequirement`,
  `TraderLoyaltyRequirements`, prestige comment),
  `TarkovHelper/Services/QuestDbService.cs` (loader, `LoyaltyTraders`),
  `TarkovHelper/Services/TraderDbService.cs` (`DisplayRank`),
  `TarkovHelper/Services/Settings/TraderLoyaltyLevels.cs` (new),
  `TarkovHelper/Services/Settings/ProfileSettingsSnapshot.cs`,
  `TarkovHelper/Services/SettingsService.cs` (constants, prefix, getter,
  setter, event, fan-out, prestige and Scav Rep bounds),
  `TarkovHelper/Services/QuestProgressService.cs` (gate, first-unmet rule),
  `TarkovHelper/Pages/QuestRequirementBadge.cs` (new),
  `TarkovHelper/Pages/QuestListPage.xaml(.cs)` (badge delegation, the badge
  and its gate in every pane, the unified Requirements list, eighth
  subscription),
  `TarkovHelper/MainWindow.xaml(.cs)` (drawer rows, the loyalty section's
  markup, the three subscriptions and the coalesced repaint),
  `TarkovHelper/Services/LocalizationService.Header.cs`
  and `LocalizationService.Quest.cs` (strings, `GetTraderDisplayName`).
- `TarkovHelper.Tests/`: `QuestStatusLoyaltyTests` (new),
  `QuestRequirementBadgeTests` (new), `TraderLoyaltyLevelsTests` (new),
  `QuestDbServiceLoyaltyReadTests` (new), `ProfileBoundsCoverDataTests` (new),
  `QuestLoyaltyE2ETests` (new), `SettingsSetterContractTests`,
  `SettingsServiceTestSupport`, `QuestStatusSettingsSnapshotTests` and every
  other constructor of `ProfileSettingsSnapshot` (the ninth value),
  `ProfileResetStoreTests`, `ProfileResetHooksTests`, `ProfileResetE2ETests`,
  `HeaderE2ETests`, `E2EQuestData`, `E2EQuestDataTests`,
  `PublishedDataContentTests`, `LocalizationHeaderStringsTests`.
- `docs/decisions/feature-quest-loyalty-gating.md` and this file.

## Technical Decisions

**One `ProfileSettings` row per trader under a key prefix, keyed by trader
id.** Considered: one row holding a JSON map of all traders under a single
key, and rows keyed by the trader's normalized name. Per-trader rows need no
serialization format, cost one write per edit through the setter path every
other value already uses, read back through the bulk `LoadProfileSettings`
unchanged, are hand-readable in the table, and are wiped by the reset with no
change to it because deletion is that code's default. The id rather than the
name because the requirement rows carry the id, the gate compares by id, and
a nickname can change upstream (the API renders Mr. Kerman and Radio station
already) while the id cannot; the name is looked up for display through
`Traders`, which the drawer needs anyway for KO and JA.

**`TraderLoyaltyLevels` is a value type with structural equality, not a
dictionary on the record.** `ProfileSettingsSnapshot` is a record whose value
equality the setter contract tests assert whole (`Assert.Equal(expected,
service.ProfileSettings)`) and whose `with` expressions the setters rely on;
a `Dictionary` member compares by reference and would silently turn every
such comparison into "same instance", passing the tests for the wrong reason.
An `ImmutableSortedDictionary` compares by reference too. A small wrapper
owning the comparison is the cheapest way to keep the record honest, and it
gives `With` a place to return the same instance for an unchanged value so
the setter's no-op guard stays a reference check.

**The gate runs after level and karma, and the badge follows the check
order.** Loyalty levels themselves need player levels (Prapor 3 needs level
21 upstream), so a quest that is both under-level and under-loyalty is most
often waiting on level first; showing `Lv.` first also keeps every existing
badge exactly where it is. Checking loyalty first was considered because it
is the patch's primary mechanism and rejected because it would change the
badge on quests this phase does not otherwise touch.

**The roster is read off the loaded requirement rows, not off `Traders` and
not hard-coded.** `Traders` lists sixteen traders and carries no level count;
the requirement rows name exactly the traders the gate can ever lock on. A
publish that adds rows for a new trader therefore grows the drawer with no app
change, which is the case the PRD records for the seasonal quests and Ref. A
hard-coded seven was rejected for that reason; all sixteen was rejected as
noise.

**The upper bound is a constant guarded by a content test.** Recorded in the
PRD with its evidence. Technically, the bound is used only to clamp input and
to size the button row; the gate compares integers and would lock correctly on
a level 5 requirement even before the constant moved. The content test in
`PublishedDataContentTests` (every `RequiredLevel` between 2 and
`MaxTraderLoyaltyLevel`) turns a fifth level upstream into a red CI run on
the publish PR, which is earlier than any player would notice.

**Loyalty folds into `LevelLocked`.** The roadmap spec's decision, unchanged:
the chip vocabulary is pinned by a literal oracle and a new member would break
chips, counts and persistence for a distinction the badge already makes.

**Every pane starts naming the gate.** `TxtDetailStatus` reads the literal
`Level` today for every level-locked quest because the call omits the task, and
so do the prerequisite rows and the alternative-quest rows. The task becomes a
required parameter, so all four panes agree with the list row and the e2e has a
single element to read. It reaches the `Unavailable` vocabulary as well: those
panes said `N/A` while the task was missing and now say the gate the row already
named (`EOD`, `Unheard`, `Edition`, `P.{n}`, `BEAR`, `USEC`). That is the point
of the change rather than a side effect of it - a pane naming the gate is worth
more than one saying `N/A` beside a row that names it.

**The Requirements lines are localized while the section is rebuilt.** The two
existing lines are English literals from before the localization pass; adding
a third localized line beside two unlocalized ones would read as a defect in
KO and JA, and the section is being edited anyway. The badge abbreviations
stay English, as the chips do by recorded decision.

**Two constants move; nothing stored moves.** The prestige and Scav Rep
bounds are read at clamp time from the constants, so widening them changes
what the drawer accepts and what `From` lets through, and nothing else. A
build that predates the change clamps a stored -7.0 to -6.0 on read, which is
the pre-existing behaviour of that build and harmless; the row is not
rewritten by the read, so the value returns on upgrade.

**A profile-settings reload needs one signal that is raised whatever the
snapshot holds** (appended during implementation). The design above has the
drawer repaint its loyalty groups on `TraderLoyaltyChanged`, which
`RaiseProfileSettingsChanged` announces once per stored entry. That is enough
for an edit and not enough for a reload: a profile that has entered no levels,
which every profile is until the player fills the drawer once and which every
profile is again after a reset, announces no loyalty event at all, so a drawer
listening only to that event would keep the previous profile's highlights on
screen under the new profile's name. Every other profile value escapes this
because its event is announced on every publish, row or no row. So
`SettingsService` gains `ProfileSettingsReloaded`, raised last in
`RaiseProfileSettingsChanged` under the same liveness guard as the value events
and never for a single edit: the signal a reader that must repaint from the
snapshot rather than from a value it was handed can use. `MainWindow` repaints
the loyalty groups on it; `QuestListPage` does not need it, since
`PlayerLevelChanged` already books its coalesced refresh on every publish.
Considered instead: announcing loyalty for the union of the outgoing and
incoming snapshots' traders, which needs the previous snapshot threaded into
the fan-out for a result the reader can read off the snapshot anyway.

**Three files the "Files touched" list did not name** (appended during
implementation). `TarkovHelper/Pages/QuestListViewModels.cs` gains
`RequirementLineViewModel`, the item type of the `RequirementsList`
`ItemsControl` the design does call for; it sits with the other detail-pane
view models rather than in the page.
`TarkovHelper/Pages/Components/TraderLoyaltyPanel.cs` holds the drawer's
loyalty controls, for the reason recorded below.
`TarkovHelper.Tests/SettingsReloadRaceTests.cs`
follows the fan-out change above: its three "every event was raised"
assertions now derive the expected list from the snapshot that was published
instead of a fixed seven, since a snapshot with loyalty entries raises more.

**The badge names the gate the walk reports** (appended during
implementation). The design above has `QuestRequirementBadge.For` run the
level, karma and loyalty predicates itself, in the walk's order. That is a
second copy of the precedence with nothing tying the two orders together:
reordering `GetStatus` would have left every test green while the badge named
a requirement the player did not have to clear next. The copy also had to grow
by four more branches once `Unavailable` started naming its cause, since the
badge would have had to re-run the edition, prestige and faction checks too.
So the walk reports its stopping gate instead (`QuestGate`, an `out` parameter
on a new `GetStatus` overload) and the badge is a switch over it. The
precedence is stated once, in the engine, and no badge string changes.
Rejected alternative: a test asserting the two orders agree, which pins
today's copies without removing tomorrow's.

**The Requirements section is one list, not three mechanisms** (appended
during implementation). The design has the loyalty lines join two named
`TextBlock`s, each with its own inline formatting, its own visibility flag and
its own copy of the "met" comparison. That is what made loyalty a third
mechanism and grew the section wrapper's condition a term per kind, and the
level line's private copy of the rule is exactly what renders a line in the
met colour beside a locked badge once the two drift. One `ItemsControl` over
`RequirementLineViewModel.BuildFor` replaces all three, the wrapper shows on
the line count, and every "met" answer comes from `QuestProgressService`.
The lines the player reads are unchanged.

**The drawer's loyalty controls are a passive panel** (appended during
implementation). Build and repaint sat in `MainWindow.xaml.cs`, already the
largest file in the app, and they are the only drawer controls built from data
rather than declared in markup. `Pages/Components/TraderLoyaltyPanel.cs` takes
them, passive the way `QuestRecommendationsPanel` is: the panel subscribes to
nothing, so the window keeps all three subscriptions with their matching
detaches, together, where `MainWindowTeardownTests` reads them. While those
handlers were being moved, the two that repaint were given one shared
`RefreshCoalescer`, the collapse `QuestListPage` already uses: a seven-trader
profile switch announced seven `TraderLoyaltyChanged` events and then
`ProfileSettingsReloaded`, and the drawer repainted itself from the same
snapshot on each of the eight. It now repaints once.

## Open Questions

- Whether and when the upstream API adds the eighteen KORD BREACH quests with
  their loyalty rows; settled by the next data regeneration's report, which
  lists rows per quest. Nothing in this phase waits on it.
- Whether a prestige resets trader loyalty in the 1.1 game the way the wiki
  describes for earlier patches; settled by observation on the primary user's
  next prestige. Either way the entry is manual and the drawer is the fix.

## Test Strategy

- **Unit, engine** (`QuestStatusLoyaltyTests`, through
  `ProgressServiceHarness` and named-argument snapshots): a quest without rows
  is unaffected by any entered level; a giver row at 2 against the default is
  `LevelLocked` and against an entered 2 is `Active`; a cross-trader row
  (Chemical - Part 3's shape) stays `LevelLocked` with the giver at 4 and the
  named trader at 1, and unlocks when the named trader reaches 2; a quest with
  several rows needs all of them (Thirsty - Hounds' shape); a row at level 1 is
  met by the default; a loyalty-locked prerequisite blocks its dependant
  (`Locked`, not `LevelLocked`); precedence: a quest under level and under
  loyalty is `LevelLocked` either way and `FirstUnmetTraderLoyalty` names the
  giver's row before another trader's. The unmet cases are written and run
  first against the pre-gate engine and must fail there (they answer `Active`
  today), per the roadmap's test strategy.
- **Unit, badge** (`QuestRequirementBadgeTests`): `Lv.` beats `Rep` beats
  `LL`; `LL2` for a giver row; `Jaeger LL2` for a non-giver row, through the
  display-name function; null when everything is met. Over the whole badge:
  the four single-cause statuses read their own word whatever the task says;
  `Unavailable` names the edition, the prestige level or the faction barring
  it, in the status walk's order, and falls back to `N/A` when the quest
  carries none of them. That the row and the detail pane show the SAME string
  is not a unit case - both call this one function, so a unit test could only
  restate it; it is driven through the app in `QuestLoyaltyE2ETests`.
- **Unit, settings** (`TraderLoyaltyLevelsTests`; the existing suites): value
  equality and hash of two instances with the same entries; `With` returns the
  same instance for an unchanged level; `LevelOf` answers the default for an
  unknown id; `From` parses `app.traderLoyalty.<id>` rows, clamps 0 to 1 and 9
  to 4, ignores an unparsable value and an empty id, and keeps a row for a
  trader outside the roster. `SettingsSetterContractTests.AllSetters` gains
  the `TraderLoyalty` case (key `app.traderLoyalty.<id>`, stored `3`,
  expected `seed with { TraderLoyalty = seed.TraderLoyalty.With(id, 3) }`,
  event `TraderLoyalty`), which also drives the re-set-to-the-same-value
  theory; the seed in `SettingsServiceTestSupport` carries one loyalty entry
  so "the other fields were left alone" is meaningful for the ninth value.
  `RaiseProfileSettingsChanged`'s fan-out test gains the loyalty event.
- **Unit, reset** (`ProfileResetStoreTests`, `ProfileResetHooksTests`): a
  profile with loyalty rows has none after the reset while its edition rows
  survive; the other profile's loyalty rows are untouched; the survivor list
  contains no key with the loyalty prefix.
- **Unit, loader** (`QuestDbServiceLoyaltyReadTests`, in-memory SQLite): no
  table means no rows and no failure; rows attach to the right task with id,
  name and level; a row for an unknown quest is skipped; a row with an empty
  trader id is skipped; `LoyaltyTraders` is distinct and in display-rank
  order with an unknown trader last.
- **Content guards on the published database** (`PublishedDataContentTests`,
  extended): every `RequiredLevel` between 2 and `MaxTraderLoyaltyLevel`;
  every `TraderId` in `QuestTraderRequirements` resolves to a `Traders` row
  with a non-empty `NormalizedName`. New `ProfileBoundsCoverDataTests` over
  the seed: max `MinLevel` at most `MaxPlayerLevel`; every
  `RequiredPrestigeLevel` at most `MaxPrestigeLevel`; every `MinScavKarma`
  within [`MinScavRep`, `MaxScavRep`]; every `RequiredDecodeCount` at most
  `MaxDspDecodeCount`; every `RequiredEdition` and `ExcludedEdition` value one
  the gate recognises. These pin the audit so a future publish that outgrows an
  input fails CI.
- **Strings** (`LocalizationHeaderStringsTests`): the new strings have KO and
  JA; `ProfileResetCategories` mentions loyalty in all three.
- **E2E** (`QuestLoyaltyE2ETests`, desktop): fixture from a new
  `E2EQuestData.LoyaltyGatedQuest()` (a quest with exactly one loyalty row
  naming its own trader at level 2 or more, no `QuestRequirements` row,
  `Ungated`, `ReachableAtDefaultLevel`, `NoAlternatives`, `UniqueSearchName`;
  27 candidates in the 1.1.0 seed, Glory to CPSU among them), pinned by an
  `E2EQuestDataTests` case that the query returns a row. Flow: fresh config,
  launch, quest tab, search the fixture, select it, `TxtDetailStatus` reads
  `LL{n}` and the Locked and Active chip counts are recorded; open the drawer
  (`BtnProfile`), invoke `Loyalty_{trader}_{n}`, that button reports
  `Selected`; `TxtDetailStatus` reads `Active`, Active count up by one and
  Locked down by one; relaunch, the quest is still Active and the button still
  `Selected`; switch to the PvE profile through the profile menu, the same
  quest reads `LL{n}` again. A second flow, over a quest gated on its own trader
  plus exactly one other, reads the row's badge (`TxtRowStatus`, an
  AutomationId per row) beside `TxtDetailStatus`: the two are the same string
  while the giver holds the quest (`LL{n}`), the same string once the giver is
  entered and the badge names the other trader (`Jaeger LL2`), and the same
  string again after the language is switched to KO through `CmbLanguage`
  (`예거 LL2`) - the row caches its badge, so that last step is what catches a
  language change that refreshes only the quest names. The Requirements lines
  are read in the same pass to show they stay in badge order, the giver first,
  with the badge naming the first line that is not met.
  `ProfileResetE2ETests` seeds
  `app.traderLoyalty.<id>` on the season profile and asserts the row is gone
  after the reset and the drawer shows level 1.
  `HeaderE2ETests.Profile_drawer_holds_the_level_stepper` gains the loyalty
  group's visibility toggling with the drawer, and is renamed
  `Profile_drawer_holds_the_level_stepper_and_the_loyalty_inputs` for it.
- **Unit, the seams the restructures opened** (appended during
  implementation). `QuestRequirementLinesTests` over
  `RequirementLineViewModel.BuildFor`: a quest carrying none of the three
  kinds produces no lines, a required level of zero is not a line, the lines
  come in badge order, a met line is told from an unmet one by its colour
  alone, and the level line's colour follows the status engine rather than a
  rule of its own. `TraderLoyaltyPanelTests` over `TraderLoyaltyPanel`: the
  automation ids the e2e addresses, an empty roster collapsing its section, a
  rebuild replacing rather than appending, the highlight moving on every group
  and not only the one that changed, a click while the parent suppresses input
  recording nothing, the panel subscribing to nothing, and a published fan-out
  repainting the drawer once instead of once per stored entry.
  `MainWindowTeardownTests`: every subscription the constructor adds is
  detached on close. `QuestRequirementBadgeTests` gains a case walking every
  `QuestGate` the engine can report.
- **Drawer layout** (`ProfileDrawerFitTests`, appended during implementation).
  Written down below as a manual check and automated instead: the real drawer
  markup is parsed out of `MainWindow.xaml`, dressed in `App.xaml`'s resources
  and laid out at the 600 pixel minimum window, and every loyalty group must
  be on screen or reachable by scrolling at each base font size, with the
  largest font scrolling rather than clipping and the default font fitting
  without a scrollbar.
- **Not automated**: the colours and the spacing of the drawer's two wrapped
  rows, checked by eye before the release.

## Verification

- `dotnet build TarkovHelper.sln` clean, Debug and Release.
- `dotnet test --filter "FullyQualifiedName~QuestStatusLoyaltyTests"` red
  before the gate lands (the unmet cases answer Active), green after.
- `dotnet test --filter "Category!=E2E"` green, `DecisionDocsTests` included
  for this pair.
- `dotnet test --filter "Category=E2E"` on the desktop: the existing suites
  plus `QuestLoyaltyE2ETests` green, with the extended `ProfileResetE2ETests`
  and `HeaderE2ETests` cases among them.
- Manual: launch `dotnet TarkovHelper/bin/Debug/net8.0-windows/TarkovHelper.dll`,
  search Glory to CPSU: `LL2` in the row and the detail badge, one Prapor line
  in Requirements; open the drawer, click Prapor 2: the row reads Active and
  the Locked chip count drops; search Chemical - Part 3: `Jaeger LL2`; set
  Skier to 4: still locked; set Jaeger to 2: Active. Prestige steps to 6, Scav
  Rep steps to -7.0. Switch language to KO: trader names in the drawer and the
  Requirements lines are Korean.

## Risks & Migration

- **Record ripple.** Adding a positional value to `ProfileSettingsSnapshot`
  touches every constructor call: `Defaults`, `From`, the `with` expressions,
  and the test helpers that build snapshots by named arguments. The compiler
  finds all of them; the setter contract table is the guard that the ninth
  value is threaded through the setters correctly.
- **Compatibility.** No schema or data change. Older builds ignore the prefix
  rows on read and delete them on reset, and clamp a stored -7.0 Scav Rep to
  -6.0 on read without rewriting it. Newer data that adds rows for a new trader
  reaches this build's drawer after the quest reload, with no app change.
- **Startup cost.** One more table read of about a hundred rows per quest
  load; negligible next to `QuestObjectives`.
- **Drawer height.** The drawer grows by one wrapped row; it overlays the page
  as today and closes on tab switch, so no page layout depends on its height.
- **Rollback.** Release the prior build. Loyalty rows stay in
  `ProfileSettings` unread and are picked up again by the next upgrade; the
  bounds revert with the constants.
