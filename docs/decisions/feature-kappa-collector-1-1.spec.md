# Technical Design: Kappa and Collector under 1.1

Product Requirements: [feature-kappa-collector-1-1.md](feature-kappa-collector-1-1.md)

- **Created**: 2026-09-12

> The technical part of a Split Change Proposal, in the form described at
> https://github.com/josephjang/change-proposal (`docs/split-proposals.md`),
> kept in this folder's filename pairing. It merges with the implementation;
> the Verification section says what has actually run at the time of each
> revision, and until the implementation lands it records that nothing has.
> A later change that reverses a decision here appends `Superseded by <doc>`
> below this line, in the PR that reverses it.

## Summary

Three small pieces, all in the app. First, the Kappa count becomes one
function counted inside the same render pass as everything shown beside it,
fed by the game's flag rather than by Collector's requirement rows, and the
list window that shows those quests moves out of the quest page so two pages
can open it. Second, the Collector page gains an unlock panel above its item
list: Collector's status badge, one line per unlock condition, and the Kappa
count with the list button, all built from the loyalty phase's two helpers
(`QuestRequirementBadge.StatusText` and `RequirementLineViewModel.BuildFor`)
against a captured pass, refreshed by the same settings events the quest
page listens to. Third, the wording: the detail pane's Kappa section and the
Collector page's scope labels are reworded and localized, and the two dead
methods named after the retired Kappa Path achievement are deleted. No
schema, data or pipeline change.

## Non-Goals

- No change to `QuestProgressService.GetStatus`, its gate order, or
  `QuestRequirementBadge`; the panel reads their answers. A Collector-specific
  "met" rule anywhere would be the second copy of the gate rule the loyalty
  phase removed.
- No change to TarkovDBEditor, the Collector synthesis, `KappaRequired`, or
  the published database. `PublishedDataContentTests` already pins the
  thirteen flags, Collector's twelve rows and its seven trader rows.
- No `QuestStatus` member, no chip change, no change to the quest list's
  sort (`feature-quest-chip-only-status-filter.spec.md` pinned the status
  vocabulary; the roadmap spec's decision stands).
- No rewrite of the Collector page's item aggregation beyond sharing its
  quest-set computation and moving it onto a pass (Design 3). Its
  fulfillment model, sorting and detail panel are untouched.
- No localization of the Collector page's other literals (search
  placeholder, fulfillment and sort combo items, the FIR and Hide Fulfilled
  toggles, column headers, the item detail labels). The product side records
  the mixed look as accepted.
- No hideout-side loyalty colouring (the loyalty phase's recorded follow-on;
  still open).

## Context

Verified at HEAD `425ac55` (branch `docs-eft-1-1-2`, equal to `origin/main`;
working tree clean, no uncommitted changes used), the published
`data/v1/tarkov_data.db` (db_version 1.1.0, the manifest's sha256 starting
`742ac3f6`), and live probes of json.tarkov.dev and the wiki on 2026-09-12.
The database was read through the research archive's read-only probe
(`dbq`, rebuilt from source) against a copy, never in place.

### Published data

- 488 quests. `KappaRequired = 1` on exactly thirteen: Collector, Chemical -
  Part 1 to 3, The Tarkov Shooter - Part 1 to 4, Postman Pat - Part 1 and 2,
  Sew it Good - Part 1 and 2, Shooter Born in Heaven.
- Collector's row: `Trader` Fence, `MinLevel` 42, `MinScavKarma` 3,
  `KappaRequired` 1, `BsgId` `5c51aac186f77432ea65c552`, `Faction` NULL, no
  edition, prestige or decode requirement. So the Fence reputation threshold
  the roadmap assigned this phase is already published, in the column the
  roadmap named, and the app's karma gate already reads it.
- Collector's `QuestRequirements`: twelve rows, all `GroupId` 0 and
  `RequirementType` Complete, one per flagged quest other than Collector.
  Its transitive chain through `QuestRequirements` is exactly those twelve:
  every prerequisite of a flagged quest is itself flagged (seven edges inside
  the set; two are Accept-type, Chemical - Part 3 on Chemical - Part 2 and
  Postman Pat - Part 2 on Postman Pat - Part 1), so "include prerequisites"
  on the Collector page adds the items of the twelve and nothing else.
- Collector's `QuestTraderRequirements`: seven rows at `RequiredLevel` 4,
  `TraderId` resolving to Prapor, Therapist, Skier, Peacekeeper, Mechanic,
  Ragman and Jaeger; none names Fence, so every one is a non-giver row and
  the badge names the trader (`Prapor LL4`, the first in the game's trader
  order, which `QuestDbService.SortIntoBadgeOrder` applies at load).
- `MinScavKarma` is set on nine quests: Collector at 3, Establish Contact at
  4, Timeout (Mechanic) at 2, Is This a Reference? at 1, and five Compensation
  for Damage quests at -1 or -3 (upper bounds). Collector's line is the
  ordinary "at least" case.
- `Traders`: sixteen rows; `NameKO` on all, `NameJA` on three (BTR Driver,
  Radio station, Survivor). `LocalizationService.GetTraderDisplayName` falls
  back to the English name, so a Japanese badge reads `Prapor LL4`.
- Collector's `QuestRequiredItems`: 44 rows, every one `RequiresFIR`,
  matching the wiki's 44 hand-overs. One row (LM KC-130 model aircraft) has
  no `ItemId`, one of thirteen such rows across the database where the item
  page did not resolve at publish; the Collector page lists it by name today
  and this phase changes nothing about it (a data gap for a data publish).

### Upstream (2026-09-12)

- json.tarkov.dev `regular/tasks`: 515 tasks (517 on 2026-08-21); the
  `kappaRequired` set is the same thirteen. Collector: `minPlayerLevel` 42,
  seven `traderRequirements` of `level >= 4`, one `reputation >= 3` naming
  Fence, and five `taskRequirements` (Shooter Born in Heaven, Sew it Good -
  Part 2, Postman Pat - Part 2, Chemical - Part 3, The Tarkov Shooter -
  Part 4, all `complete`). Twelve tasks carry a `reputation` entry; the 1.1
  refresh imports none of them as such (the loyalty phase's Non-Goal), and
  Collector's threshold reaches the app through the wiki-parsed
  `MinScavKarma` instead, where it agrees with the API's value.
- The wiki's Collector page (`api.php`, live): Requirements are "Reach
  Loyalty Level 4 with Prapor, Therapist, Skier, Peacekeeper, Mechanic,
  Ragman and Jaeger", "Scav karma of at least +3", and four quests (Chemical -
  Part 3, Sew it Good - Part 2, Shooter Born in Heaven, The Tarkov Shooter -
  Part 4); no player level, no Postman Pat - Part 2. Rewards: Secure
  container Kappa, the DEADSKUL armband, the achievement "Dawn of a New Era".
  The infobox still carries the stale `reqkappa = Yes` the template stopped
  rendering on 2026-08-03. The page is admin-locked indefinitely.
- The 1.1.0.0 changelog (research archive capture): "The Kappa Path
  achievement: starting with Season 1, this achievement can no longer be
  earned; instead, players who complete the Collector task will receive the
  Dawn of a New Era achievement." The Kappa container's own page still says
  it is obtained as a quest reward from Collector.

### App

- **The count.** `QuestGraphService.GetCollectorProgress(Func<string, bool>)`
  counts every task with `ReqKappa` set, Collector included, and returns
  `(completed, total, percentage)` with integer percentage;
  `GetKappaRequiredQuestsWithStatus(Func<string, bool>)` returns the same
  set as `(task, isCompleted)` ordered incomplete first, then trader, then
  name. Every caller passes `QuestProgressService.IsQuestCompleted(name)`,
  which resolves the task and runs the full `GetStatus(task)` walk against
  the live `Snapshot` and live `SettingsService.Instance.ProfileSettings`.
  The four call sites are in `QuestListPage`: `UpdateKappaGauge` (the
  quest-tab gauge, `TxtKappaGauge`, run at the end of every `ApplyFilters`),
  `UpdateKappaProgressSection` (the Collector detail pane) and
  `BtnShowKappaQuests_Click` (twice, for the list and its header).
  `IsQuestCompleted` has one other caller, `IntegratedItemService`.
- **The pass.** The loyalty phase made the quest page compute rows, chips
  and the detail pane against one captured `RenderPass(ProgressSnapshot,
  ProfileSettingsSnapshot)` (`QuestListPage.CapturePass`, `StatusIn`), so a
  profile switch mid-refresh cannot mix two profiles. The gauge is computed
  after the chips in the same `ApplyFilters` but against live state, so the
  chips and the gauge beside them can still come from different profiles in
  that window. The Collector page has no pass at all: `GetCollectorItemRequirements`
  and `GetQuestSources` each call `GetStatus(task)` live, and the two methods
  compute the same "quests in scope" set with near-identical code.
- **The Collector detail pane** (`QuestListPage.xaml`, `KappaProgressSection`):
  the literal heading "Kappa Progress", `TxtKappaProgress` set to
  `"Prerequisites: ({completed}/{total} completed)"`, a bar and a percentage,
  and `BtnShowKappaQuests` "Show All Required Quests", which builds a
  `Window` titled "Kappa Required Quests" inline in the click handler, with a
  header `"Kappa Required Quests ({completed}/{total})"` and one row per
  quest (check or circle glyph, localized name, trader). All of it English
  literals; the pane's Requirements section right below it is localized
  (`RequirementLevelFormat`, `RequirementScavKarmaFormat`,
  `RequirementLoyaltyFormat`).
- **The Collector page** (`CollectorPage.xaml(.cs)`): a search box, the
  `ChkIncludePreQuest` checkbox ("Include Pre-Quest", tooltip "Include items
  required for Collector's prerequisite quests"), fulfillment and sort
  combos, `TxtStats` composed as `"Showing {n} items | Total: {t} | Fulfilled:
  {f} | In Progress: {p}"` plus `" | Including Pre-Quests"` or
  `" | Kappa Quests Only"`, the `LstItems` list and a detail panel. Collector's
  items are listed unless Collector is Done, Failed or Unavailable, so a
  level-locked Collector lists its items with nothing on the page saying it
  is locked. With the option on, `QuestGraphService.GetAllPrerequisites`
  walks `Previous` transitively and adds each prerequisite that is not Done,
  Failed or Unavailable. The page subscribes to `ProgressChanged`,
  `InventoryChanged`, `LanguageChanged` and both `DataRefreshed` events, and
  to no settings event. Its own strings are English literals; only the tab
  name (`TabCollector`) is localized.
- **Helpers this design reuses.** `QuestRequirementBadge.StatusText(status,
  gate, task, settings, traderDisplayName)` (internal static, pure) is the
  one badge rule; `RequirementLineViewModel.BuildFor(task, settings, loc,
  traderDisplayName, metBrush, unmetBrush)` (internal static, pure) builds
  the level, karma and loyalty lines in badge order with met and unmet
  brushes; `QuestProgressService.GetStatus(task, snapshot, settings, out
  gate)` gives the status and the gate; `RefreshCoalescer.OnDispatcher(owner,
  refresh)` collapses a burst of events into one refresh (the loyalty phase
  found a published profile raises `TraderLoyaltyChanged` once per stored
  entry). `TraderLoyaltyPanel` is the precedent for a passive component
  class; `QuestListPage.GetStatusBrush` and its brushes are private to that
  page.
- **Gate order for Collector on a fresh profile.** Done/Failed, edition,
  prestige, faction, DSP, prerequisites, level, karma, loyalty, Active. So
  Collector reads `Locked` until its twelve rows are Done, then `Lv.42`
  (default level 15), then `Rep 3` (default Scav Rep 1.0), then `Prapor LL4`
  (default loyalty 1, Prapor first by `TraderDbService.DisplayRank`), then
  `Active`.
- **Dead code.** `QuestGraphService.GetKappaPath` and
  `ItemRequirementService.GetKappaItems` have no callers anywhere in the
  solution (the only mentions are the 2025-12 todo in `decisions/archive/`);
  `GetKappaItems` is `GetKappaPath`'s only caller. `TopologicalSort` has
  another caller (`GetOptimalPath`) and stays.
- **Wording inventory, kept.** "Required for Kappa Container" (row and
  recommendation badges), the `Kappa` filter checkbox, the `Kappa` badge on
  the Collector page's quest sources, `KappaPriority` and `GetKappaReason` (KO
  "카파", JA "Kappa"), `README.md` line 28 ("a dedicated checklist for the
  Collector quest's items"). All still true under 1.1.
- **Tests.** `PublishedDataContentTests` pins thirteen flags, Collector's
  twelve rows all flagged, and seven traders at 4. No unit test covers
  `GetCollectorProgress` or `GetKappaRequiredQuestsWithStatus`. E2E:
  `QuestOverviewFiltersE2ETests` toggles `ChkKappaOnly`;
  `QuestNavigationE2ETests.Collector_page_quest_link_preserves_quest_tab_filters`
  and the seasonal fixture (`AssertSeasonItemsPage` on `TabCollector`,
  `LstItems`, `TxtSearch`, `TxtDetailOwnedFir`) drive the Collector page. The
  harness offers `GetElementText` (UIA Name, a TextBlock's text),
  `SeedQuestProgress`, `SeedProfileSetting` (`app.playerLevel`,
  `app.scavRep`, `app.traderLoyalty.<traderId>`), the drawer's
  `Loyalty_{trader}_{n}` buttons, and `E2EQuestData`'s SQL-derived fixtures.
  WPF publishes an element's `x:Name` as its AutomationId when none is set,
  which is how the existing tests address `TxtDetailStatus` and `LstItems`.
  The `LegacySmokeE2ETests` target is the previously released build and needs
  no run here (no data publish).

## Design

### 1. One Kappa count, counted inside the pass (`QuestGraphService`, `QuestListPage`)

For R3 and R4. `GetCollectorProgress` becomes `GetKappaProgress(Func<TarkovTask,
bool> isDone)` and `GetKappaRequiredQuestsWithStatus` becomes
`GetKappaQuestsWithStatus(Func<TarkovTask, bool> isDone)`; both keep their
bodies (the flag decides membership, Collector included; incomplete first,
then trader, then name) and take the task instead of its name, so the caller
decides "done" against its own pass. `QuestListPage` passes
`task => StatusIn(pass, task).Status == QuestStatus.Done` from the pass
`ApplyFilters` already captured, so the gauge, the chips and the rows agree
on the profile; the detail pane and the list button use the pass of the
refresh that built the pane. `IsQuestCompleted` keeps its remaining caller
(`IntegratedItemService`) and is no longer used for Kappa.

A `RenderPass` and `StatusIn` are needed by two pages now, so the record
struct moves out of `QuestListPage` into `Pages/RenderPass.cs` (internal,
unchanged shape) with a static `Capture(QuestProgressService)`; `StatusIn`
stays a one-line private adapter on each page over
`GetStatus(task, pass.Progress, pass.Settings, out gate)`.

### 2. The Kappa quest list window (`Pages/Components/KappaQuestListWindow`)

For R3 and R4. The window `BtnShowKappaQuests_Click` builds inline moves to
`KappaQuestListWindow`, a `Window` subclass built in code as today, with one
static entry point: `Show(Window owner, IReadOnlyList<(TarkovTask Quest, bool
IsDone)> quests, int completed, int total, Func<TarkovTask, string>
displayName, LocalizationService loc)`. It renders the header from
`loc.KappaQuestListTitle` ("Kappa quests ({0}/{1})") and the rows exactly as
before. The detail pane's button and the Collector page's button both call
it, each with its own pass, so the list is the same list from either place.

### 3. The Collector page's unlock panel (`CollectorPage`, `CollectorViewModels`)

For R1, R2, R3, R5 and R6.

- **Markup.** `CollectorPage.xaml` gains a row above the filter row in
  `MainContent`: a `Border x:Name="UnlockPanel"` holding, left to right and
  wrapping at the 600 pixel minimum window, a heading `TxtUnlockHeading`
  (`loc.CollectorUnlockHeading`, "Collector unlock"), the status badge
  `TxtCollectorStatus` (a bordered `TextBlock` styled like the quest rows'
  badge, `Background` from the status brush), an `ItemsControl
  x:Name="CollectorRequirementsList"` over `RequirementLineViewModel` with
  the same `DataTemplate` the detail pane uses (`DisplayText`, `Foreground`)
  on a `WrapPanel`, the count `TxtKappaCount` (`loc.KappaCountFormat`,
  "{0}/{1} Kappa quests completed"), and `BtnCollectorKappaQuests`
  (`loc.ShowKappaQuests`, "Show Kappa quests"). The panel is collapsed when
  the loaded data has no quest whose `NormalizedName` is `collector`, the
  same lookup the page already does.
- **The rule, pure and tested.** `CollectorUnlockViewModel` (in
  `CollectorViewModels.cs`, beside the page's other view models) with
  `internal static CollectorUnlockViewModel? BuildFor(TarkovTask? collector,
  QuestStatus status, QuestGate gate, ProfileSettingsSnapshot settings,
  LocalizationService loc, Func<QuestTraderRequirement, string>
  traderDisplayName, int kappaDone, int kappaTotal, Brush metBrush, Brush
  unmetBrush)` returning `StatusText` (from `QuestRequirementBadge.StatusText`),
  `Status` (for the brush), `Lines` (from `RequirementLineViewModel.BuildFor`)
  and `CountText` (from `loc.KappaCountFormat`), or null when `collector` is
  null. It contains no comparison of its own: which condition holds Collector
  is the engine's answer, carried in `gate`, and the badge and the first
  unmet line agree because both derive from it. The status brushes move from
  `QuestListPage` into `Pages/QuestStatusBrushes.cs` (internal static, the
  same six brushes and `For(QuestStatus)`), which both pages read.
- **Building it.** `CollectorPage.RebuildUnlockPanel(RenderPass pass)` finds
  Collector, gets `(status, gate)` from `StatusIn`, counts with
  `GetKappaProgress(task => StatusIn(pass, task).Status == Done)`, calls
  `BuildFor`, and assigns the four controls. It runs from the page's existing
  reload path (`LoadItemsAsync` captures one pass and hands it to the item
  aggregation and to the panel, so the items and the panel describe one
  profile) and from a new settings path.
- **Settings events.** The page subscribes, in the same Loaded/Unloaded
  pairing it uses for its five existing subscriptions, to
  `SettingsService.Instance.PlayerLevelChanged`, `ScavRepChanged`,
  `TraderLoyaltyChanged` and `ProfileSettingsReloaded`, each requesting one
  `RefreshCoalescer.OnDispatcher(this, () => RebuildUnlockPanel(
  RenderPass.Capture(_questProgressService)))`, so a published profile's
  seven loyalty events repaint once. The edition, prestige, DSP and faction
  events are not subscribed: Collector carries none of those gates in the
  data, and the item list does not read them either; a data publish that
  added one arrives through `DataRefreshed`, which already rebuilds
  everything. `ProgressChanged` (a completion, a reset, a profile's progress
  reload) and `LanguageChanged` already reload the page; the panel is part of
  that reload. `MainWindowTeardownTests` has a Collector-page counterpart
  (Test Strategy) so the four new subscriptions are detached on unload.
- **The shared quest set.** `GetCollectorItemRequirements(bool)` and
  `GetQuestSources(string)` both compute the same set of quests in scope
  (Collector unless Done, Failed or Unavailable; plus its transitive
  prerequisites in the same states when the option is on). That computation
  becomes one private `QuestsInScope(RenderPass pass, bool
  includePrerequisites)` returning the set, and both callers take the pass
  instead of calling `GetStatus(task)` live. The set's content is unchanged
  (R6); under the 1.1 data the transitive walk is the twelve flagged quests.
- **Strings.** `ChkIncludePreQuest` reads `loc.CollectorIncludePrerequisites`
  ("Include prerequisites") with tooltip `loc.CollectorIncludePrerequisitesTip`
  ("Also list the items Collector's prerequisite quests need"); `TxtStats`
  is composed from `loc.CollectorStatsFormat` ("Showing {0} items | Total:
  {1} | Fulfilled: {2} | In Progress: {3} | {4}") with the scope slot from
  `loc.CollectorScopeWithPrerequisites` ("Including prerequisites") or
  `loc.CollectorScopeCollectorOnly` ("Collector only"). The page's
  `OnLanguageChanged` already reloads; it now also re-applies these labels.
  The element names `ChkIncludePreQuest` and `TxtStats` stay, so
  `QuestNavigationE2ETests` and the seasonal fixture are unaffected.

### 4. Wording and strings (`QuestListPage.xaml(.cs)`, `LocalizationService`)

For R4 and R7. In the detail pane's Kappa section the heading reads
`loc.KappaProgressHeading` ("Kappa quests"), `TxtKappaProgress` reads
`loc.KappaCountFormat` (the same string the Collector page shows), and
`BtnShowKappaQuests` reads `loc.ShowKappaQuests`; the bar and the percentage
stay. The strings live in `LocalizationService.Quest.cs` under the existing
"Quest detail" region: `KappaProgressHeading`, `KappaCountFormat`,
`ShowKappaQuests`, `KappaQuestListTitle`. The Collector page's strings live in
a new partial `LocalizationService.Collector.cs` (the page has had no strings
of its own until now): `CollectorUnlockHeading`,
`CollectorIncludePrerequisites`, `CollectorIncludePrerequisitesTip`,
`CollectorStatsFormat`, `CollectorScopeWithPrerequisites`,
`CollectorScopeCollectorOnly`. Every one has KO and JA; "Kappa" renders as
"카파" in KO and "Kappa" in JA, as `GetKappaReason` already does, and "LL" stays
English as the badge does. Nothing in R7's list changes. `README.md` and
`README.ko.md` line 28 grow four words, "and what still locks it", so the
feature list matches the page.

### 5. Dead code (`QuestGraphService`, `ItemRequirementService`)

`GetKappaPath` and `GetKappaItems` are deleted, with their XML comments. No
caller, no test, no XAML binding references either; `AggregatedItemRequirement`
and `TopologicalSort` keep their other users. The "Kappa Path" the names
refer to is the achievement 1.1 retired.

### Files touched

- `TarkovHelper/Services/QuestGraphService.cs` (renamed count and list over
  tasks; `GetKappaPath` deleted),
  `TarkovHelper/Services/ItemRequirementService.cs` (`GetKappaItems`
  deleted), `TarkovHelper/Pages/RenderPass.cs` (new; moved out of the quest
  page), `TarkovHelper/Pages/QuestStatusBrushes.cs` (new; moved out of the
  quest page), `TarkovHelper/Pages/Components/KappaQuestListWindow.cs` (new;
  the extracted window), `TarkovHelper/Pages/QuestListPage.xaml(.cs)` (count
  in the pass, Kappa section strings, window delegation, the moved pieces),
  `TarkovHelper/Pages/CollectorPage.xaml(.cs)` (unlock panel markup, pass,
  shared quest set, four subscriptions, strings),
  `TarkovHelper/Pages/CollectorViewModels.cs` (`CollectorUnlockViewModel`),
  `TarkovHelper/Services/LocalizationService.Quest.cs` (four strings),
  `TarkovHelper/Services/LocalizationService.Collector.cs` (new; six
  strings), `README.md`, `README.ko.md`.
- `TarkovHelper.Tests/`: `KappaProgressTests` (new),
  `CollectorUnlockViewModelTests` (new), `CollectorPageSubscriptionTests`
  (new), `KappaCollectorE2ETests` (new), `E2EQuestData` and
  `E2EQuestDataTests` (Kappa fixtures), `LocalizationHeaderStringsTests` (the
  ten new keys), `QuestRequirementLinesTests` (unchanged; cited).

## Technical Decisions

- **TD1: The unlock panel is composed from the loyalty phase's two pure
  helpers and the engine's gate, with no rule of its own.** A
  Collector-specific checklist with its own "met" comparisons was rejected:
  it would be the second copy of the gate rule, which is exactly what
  `RequirementLineViewModel.BuildFor` was written to remove, and it would
  drift from the badge the first time the engine's order changed. Cost: the
  brushes and the pass type have to be shared between two pages, two small
  moves. Revisit if a third page needs the same composition, at which point
  the composition itself (`CollectorUnlockViewModel.BuildFor`) is the thing
  to generalize.
- **TD2: The Kappa count is fed by the flag over the loaded tasks, not by
  Collector's requirement rows.** Counting Collector's twelve
  `TaskRequirements` plus Collector would give the same thirteen today, but
  it would make the number depend on the synthesis (a pipeline decision the
  refresh made and could remake) instead of on the game's flag, which is the
  published truth `PublishedDataContentTests` pins. The flag is also what the
  Kappa filter and the row badges read, so one source feeds every Kappa
  surface. Revisit only with PD2 in `feature-kappa-collector-1-1.md`.
- **TD3: The count is computed inside the caller's render pass.** The
  `Func<string, bool>` that ran a live `GetStatus` per quest was rejected in
  favour of `Func<TarkovTask, bool>` fed by the pass: the gauge then agrees
  with the chips beside it during a profile switch, the Collector page's
  panel and item list describe one profile, and the per-quest walk against
  live singletons goes away. The old shape could also have been kept with the
  page resolving names itself; taking the task removes a lookup the graph
  service already did. Revisit if the count ever needs to run off the UI
  thread without a pass in hand.
- **TD4: The Collector page subscribes to four settings events, not the
  quest page's eight.** Collector carries no edition, prestige, DSP or
  faction gate in the data, and the page's item list reads none of those
  values, so those four events cannot change anything the page shows; a
  publish that changed the data arrives through `DataRefreshed`, which
  rebuilds everything. Subscribing to all eight for symmetry was rejected as
  four handlers that can never observe a change. Revisit if the item list
  ever filters by faction or edition.
- **TD5: The list window is extracted, not duplicated and not turned into a
  page.** Two buttons on two pages open the same list; building it twice was
  rejected for the obvious reason, and a third tab or a page-level panel was
  rejected because the list is a glance, thirteen rows, and the existing
  popup already fits it. It stays code-built (as today) rather than XAML: the
  rows are data and there is nothing to declare beyond a scroll viewer and a
  stack.
- **TD6: The two dead methods are deleted rather than kept for a future item
  planner.** They have had no caller since the 2025-12 todo that added them,
  they are named after an achievement the game removed, and the Collector
  page's "include prerequisites" option already lists the items across the
  twelve quests. Keeping them "in case" was rejected; if a planner is wanted,
  it is a new change with its own proposal.
- **TD7: Only the strings this phase adds or changes are localized, and
  they all are.** The Collector page's other literals stay, per the product
  Non-Goal. Within the touched set the rule is all-or-nothing: the
  stats line is one composed string in three languages rather than a
  localized suffix on an English sentence.
- **TD8 (appended by the implementation): the quest tab's gauge counts from
  the statuses cached on the row view models.** Design 1 says the gauge counts
  "from the pass `ApplyFilters` already captured"; `ApplyFilters` captures no
  pass of its own, and the chips beside the gauge count from the `Status` each
  row cached when `LoadQuests` or `RefreshQuestStatuses` last captured one. The
  gauge now counts from that same cache (keyed by `NormalizedName`), which is
  what makes it agree with the chips. The detail pane counts within the pass
  that built it, and the list button captures one pass at the click for its
  header and its rows rather than reusing the pane's, so a stale pane pass can
  never feed a fresh list. Pinned by `KappaProgressTests` and the two e2e flows.
- **TD9 (appended): `QuestGraphService.IsInitialized` replaces the gauge's
  catch-all.** The gauge used to swallow every exception into "0/0", which
  could not tell "no data yet" from "nothing is flagged"; the pages now ask.
- **TD10 (appended): the shared status brushes are frozen, and tested.**
  `QuestStatusBrushes` freezes its six brushes so no page's dispatcher owns
  them; `QuestStatusBrushesTests` (not in Files touched) pins one distinct
  frozen brush per status and the Gray fallback.
- **TD11 (appended): the Collector page's subscription test is a source
  guard, and the page joins the shared page guards.** Test Strategy asked for
  the page "constructed on an STA thread" and a burst "asserted through the
  coalescer the page exposes for the test". The page cannot be constructed in
  the unit suite: its markup resolves App.xaml's brushes through
  StaticResource and its constructor reaches six singletons that open the
  databases, and no page in the suite is constructed that way. So
  `CollectorPageSubscriptionTests` reads the source, in the shape
  `MainWindowTeardownTests` and `RefreshCoalescerSchedulingTests` already use:
  which four events, that each handler is a one-liner over the coalescer, that
  the coalescer is built in the constructor body, that the refresh repaints
  the panel only and only while loaded, and that every status the page reads
  goes through the pass adapter. The page also joins the existing mirror,
  lifecycle and factory theories in `RefreshCoalescerSchedulingTests`, whose
  brace matcher moved to a shared `SourceGuards` helper rather than gaining a
  third copy. The burst-to-one-run rule itself is `RefreshCoalescerTests`.
- **TD12 (appended): the e2e drawer and language choreography is shared.**
  The loyalty suite kept its drawer helpers and its language switch private;
  the Kappa/Collector suite needs both, so they moved to `ProfileDrawerDriver`
  and `AppDriver.SelectLanguage`, and `QuestLoyaltyE2ETests` reads the same as
  before through them. Collector's detail pane is reached through the Kappa
  filter plus the search rather than the search alone, since "Collector" is
  not guaranteed to be a unique search substring across every quest name.
- **TD13 (appended): the detail panel's quest sources read the list's pass.**
  `GetQuestSources` uses the pass the listed items were aggregated under
  (`_listPass`, null before the first load) rather than capturing a fresh one,
  so the detail panel names exactly the quests whose items the list shows;
  every change that alters the scope reloads the list, and the detail with it.
- **TD14 (appended): Design order.** The detail pane's four strings (Design 4)
  landed before the window extraction (Design 2), because the window's title
  reads `KappaQuestListTitle`; otherwise the slices follow the Design order.
  Files touched beyond the list above: `QuestStatusBrushesTests`,
  `SourceGuards`, `ProfileDrawerDriver` and `QuestStartedEventTests` (new);
  `RefreshCoalescerSchedulingTests`, `E2ETestHarness`, `QuestLoyaltyE2ETests`
  and `LoyaltyFixtures` (three trader ids) touched; `QuestProgressService`
  touched for the defect recorded under Verification.

## Open Questions

- Whether the game really gates Collector on player level 42. The API says
  42, the wiki page lists no level, and the app has shown `Lv.42` since the
  1.1 refresh published it. Deferred: it is a data question, settled by a
  player at level 41 with every other condition met or by a wiki edit that
  cites patch notes, and either way the panel shows what the data says; a
  correction upstream is a data-only publish (the refresh's rule against
  hand patches applies).
- Whether Postman Pat - Part 2 belongs among Collector's quests. The API and
  the flag say yes, the wiki page omits it. Deferred on the same terms; the
  count follows the flag either way (TD2).

## Test Strategy

- **R1, R2 (unit, `CollectorUnlockViewModelTests`)** over
  `CollectorUnlockViewModel.BuildFor` with a Collector task shaped like the
  published row (level 42, karma 3, seven non-giver rows at 4, twelve
  prerequisites) and named-argument `ProfileSettingsSnapshot`s through the
  existing harness: with the prerequisites unmet the badge reads `Locked`
  and all nine lines are present and unmet; with them met and defaults the
  badge reads `Lv.42` and the level line is the first unmet; at level 42 it
  reads `Rep 3`; at Scav Rep 3 it reads `Prapor LL4` and the loyalty lines
  are in the loaded order; with all seven at 4 it reads `Active` and every
  line is met; a Done status reads `Done` with met lines; a null task gives
  a null panel. The badge text is asserted against `QuestRequirementBadge.
  StatusText`'s own output for the same inputs, since the test's claim is
  composition, not the badge rule (that rule's tests are
  `QuestRequirementBadgeTests`).
- **R3, R4 (unit, `KappaProgressTests`)** over `QuestGraphService`: the count
  runs over flagged tasks only, Collector included; a flag-less quest with a
  Done status is not counted; percentage is integer and 0 of 0 is 0; the list
  is incomplete first, then trader, then name; the `isDone` delegate is
  called once per flagged task with the task, never with a name. Written
  against the renamed methods; they fail to compile against the old
  signatures, which is the intended "before" state.
- **R3, R5 (e2e, `KappaCollectorE2ETests`, desktop)**. Fixtures from
  `E2EQuestData`: `KappaQuests()` (Id, NormalizedName, Name of every flagged
  quest other than Collector) and `Collector()` (Id, NormalizedName,
  MinLevel, MinScavKarma, and its trader rows in `TraderDbService.DisplayRank`
  order), pinned by `E2EQuestDataTests` cases that each returns rows and
  that the Kappa set's size equals the seed's flag count. Config: the quiet
  config the loyalty e2e uses (`app.logMonitoringEnabled` False, so this
  machine's EFT logs cannot switch the profile under the test). Flow one,
  "the Collector page names what holds Collector": seed the twelve as Done
  (`SeedQuestProgress`), seed `app.playerLevel` = MinLevel and
  `app.scavRep` = MinScavKarma, launch, open `TabCollector`, and
  `TxtCollectorStatus` reads `{first trader} LL4` and `TxtKappaCount`
  starts with `12/{total}`; click `Loyalty_{trader}_4` for each of the seven
  in the drawer without leaving the tab; `TxtCollectorStatus` reads `Active`;
  switch the language to KO through `CmbLanguage` and the first loyalty line
  in `CollectorRequirementsList` names the trader in Korean. Flow two, "one
  number": fresh profile, `TxtKappaGauge` on the quest tab reads `0/{total}`
  where `{total}` is the seed's flag count read by the test; seed one flagged
  quest Done and relaunch; the gauge reads `1/{total}`, the Collector page's
  `TxtKappaCount` starts with the same `1/{total}`, and Collector's detail
  pane (`QuestTabDriver.ShowQuestDetail`) shows `TxtKappaProgress` starting
  with `1/{total}` and no longer containing "Prerequisites".
- **R5 (unit, `CollectorPageSubscriptionTests`)**: the page constructed on
  an STA thread subscribes to the four settings events on Loaded and detaches
  every one on Unloaded, in the shape `MainWindowTeardownTests` uses; a burst
  of seven `TraderLoyaltyChanged` events rebuilds the panel once, asserted
  through the coalescer the page exposes for the test.
- **R6 (existing e2e, unchanged)**: `QuestNavigationE2ETests.
  Collector_page_quest_link_preserves_quest_tab_filters` and the seasonal
  fixture's Collector-page step still pass against the renamed labels, since
  they address `LstItems`, `TxtSearch` and `TxtDetailOwnedFir`. The stats
  line's scope word is read once in flow one (`TxtStats` ends with "Collector
  only" with the option off).
- **R4, R7 (unit, `LocalizationHeaderStringsTests`)**: the ten new keys join
  its key list, so each has non-empty KO and JA, and the three with slots
  (`KappaCountFormat`, `KappaQuestListTitle`, `CollectorStatsFormat`) join
  `FormatKeys` so every language keeps every slot; a new case asserts no
  string in the Kappa or Collector set contains "Prerequisites" or
  "Pre-Quest"; the three R7 texts are asserted byte-equal to today's.
- **R8 (existing)**: `PublishedDataContentTests`, `DataFormatDriftTests`,
  `SeedDatabaseTests` untouched and green; `git diff --stat data/` empty in
  the PR.
- **Not automated**: the panel's wrapping at the 600 pixel minimum window
  and the badge's colours, checked by eye before the release; the Japanese
  fallback to English trader names (recorded in the product Risks, and the
  same path `QuestLoyaltyE2ETests` already exercises for KO).

Commands, at implementation time:

```
dotnet build TarkovHelper.sln
dotnet test --filter "FullyQualifiedName~KappaProgressTests|FullyQualifiedName~CollectorUnlockViewModelTests"
dotnet test --filter "Category!=E2E"
dotnet test --filter "FullyQualifiedName~KappaCollectorE2ETests"
dotnet test --filter "Category=E2E"
```

## Verification

- Checked, at this revision (the proposal, before implementation):
  `dotnet test TarkovHelper.Tests --filter "FullyQualifiedName~DecisionDocsTests"`
  with the pair in the working tree at `425ac55`, 2026-09-12: 4 passed, 0
  failed (no kept-current field, the spec has its sibling, every referenced
  decision-doc path resolves). The data facts in Context were read from the
  published database and the live upstream on 2026-09-12 as described there;
  the `dbq` queries are reproducible from the research archive's README.
- Not checked: everything in Test Strategy. No implementation exists at this
  revision; the commands above have not run and no result is claimed. The
  implementation PR appends what ran, the tested revision, and what was not
  run and why.
- Ran, at the implementation (branch `feat/kappa-collector-1-1`, last code
  commit `01ff489`, 2026-09-12): `dotnet build TarkovHelper.sln` in Debug and
  Release, 0 warnings / 0 errors. `dotnet test --no-build --filter
  "Category!=E2E"`: 1672 passed / 0 failed / 0 skipped, from a baseline of
  1606 measured at `ffa33f9` before the first edit (+66: `KappaProgressTests`
  8, `QuestStatusBrushesTests` 4, `CollectorUnlockViewModelTests` 9,
  `CollectorPageSubscriptionTests` 13, `RefreshCoalescerSchedulingTests` +3
  rows, `LocalizationHeaderStringsTests` +24 rows, `E2EQuestDataTests` +3,
  `QuestStartedEventTests` 2). `dotnet test --no-build --filter
  "Category=E2E"` on the development desktop: 49 passed / 0 failed / 2
  skipped (the two legacy smoke cases, which need no run here: no data
  publish), from a baseline of 47 / 0 / 2; the two new cases are
  `KappaCollectorE2ETests`. The fail-first evidence is in the commit bodies:
  the count suite failed to compile against the old signatures (CS1061), the
  subscription guards failed 13 of 13 against the parent commit's page, and
  the started-event tests failed on "the started quest was recorded instead
  of left Active".
- Found and fixed in the same PR, outside this phase's scope: a "quest
  started" log event recorded the started quest itself as Done, because the
  prerequisite walk it plans over answers the target as its last entry (the
  walk's comment claimed otherwise, and is corrected). Own commit, own tests
  (`QuestStartedEventTests`); nothing had covered the Started path.
- Not run: the manual check of the panel's wrapping at the 600 pixel minimum
  window and of the badge colours is reported in the PR body with what it
  found. The Japanese fallback to English trader names was not driven; it is
  the same resolver path `QuestLoyaltyE2ETests` and `KappaCollectorE2ETests`
  drive for Korean.

## Risks & Migration

- **Compatibility.** No schema, data or settings-key change. Nothing is
  written that an older build reads differently. Rollback is releasing the
  prior build.
- **Signature ripple.** Renaming the two graph methods and taking a task
  instead of a name touches five call sites, all in `QuestListPage` and the
  new Collector code; the compiler finds them. Moving `RenderPass` and the
  brushes out of the quest page is mechanical, and `QuestListPage`'s existing
  tests cover the page after the move.
- **Two pages, one pass type.** The Collector page now captures snapshots the
  way the quest page does; a future page that reads status should do the
  same rather than call `GetStatus(task)` live. Not enforced by structure; it
  is the reason `RenderPass` gets a file of its own with the reason in its
  comment.
- **Refresh cost.** The panel rebuild is one status walk for Collector, one
  `BuildFor` over nine lines and a count over thirteen tasks, coalesced per
  burst. Negligible next to the item aggregation the same reload already
  does.
- **The panel shows what the data says.** The two upstream disagreements in
  Open Questions surface on the panel exactly as they do on the quest tab
  today; a data-only publish corrects either. No hand patch.
- **Release.** App-only, cut with the fork's release flow. The loyalty phase
  is merged and not yet tagged at this revision (the last tag is v2026.8.0),
  so this phase joins that release if it merges before the cut and follows in
  its own otherwise, as the roadmap's release plan allows. The legacy smoke
  is not required since the data endpoint does not change.
