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
methods named after the retired Kappa Path achievement are deleted (at the
review, following their chain took the whole of `ItemRequirementService` with
them, Design 5). No schema, data or pipeline change.

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
  fulfillment model, sorting and detail panel are untouched. The fulfillment
  half of that is superseded by TD15: the review pass found the rule reading
  a mixed requirement as satisfied on its FIR half alone, which is the shape
  this page's own aggregation produces, and replaced it with one shared rule.
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
  page did not resolve at publish. An `ItemId`-less row is dropped at load by
  `QuestDbService.LoadQuestRequiredItemsAsync` (it cannot be matched to the
  `Items` table), so it never enters `task.RequiredItems` and the Collector
  page's aggregation never sees it: the page lists 43 of the 44 published
  rows, and the unresolvable one is invisible rather than named. The other
  43 `ItemId`s all resolve in `Items`, so the list is 43 items. This phase
  changes nothing about it (a data gap for a data publish).

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
  is locked. With the option on, the graph's recursive prerequisite walk
  (`GetAllPrerequisites` when this was written, renamed to
  `GetPrerequisiteClosure` by the review, TD16) walks `Previous`
  transitively and adds each prerequisite that is not Done, Failed or
  Unavailable. The page subscribes to `ProgressChanged`,
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
  `GetKappaItems` is `GetKappaPath`'s only caller. A second chain is dead
  the same way, which this bullet first got wrong: it said "`TopologicalSort`
  has another caller (`GetOptimalPath`) and stays", counting direct
  references. `GetOptimalPath` does reference `TopologicalSort`, but
  `GetOptimalPath`'s own and only caller is
  `ItemRequirementService.GetItemsForQuestPath`, which nothing calls at all,
  so following the chain end to end leaves all three unreachable and they go
  with `GetKappaPath` and `GetKappaItems`. Corrected in place rather than
  superseded: this document is added by this PR and has not merged, so no
  `Superseded by` line is owed. Widening the sweep one step further settled
  the rest of that file too: nothing in the solution names
  `ItemRequirementService` outside its own file, so no code path reaches its
  `Initialize`/`InitializeAsync`, and its four result types
  (`AggregatedItemRequirement`, `QuestItemReference`, `ItemRequirementDetail`,
  `ItemRequirementStats`) have no user either. The whole file is deleted
  rather than the two members.
- **Wording inventory, kept.** "Required for Kappa Container" (row and
  recommendation badges), the `Kappa` filter checkbox, the `Kappa` badge on
  the Collector page's quest sources, `KappaPriority` and `GetKappaReason` (KO
  "카파", JA "Kappa"), `README.md` line 28 ("a dedicated checklist for the
  Collector quest's items"). All still true under 1.1.
- **Tests.** `PublishedDataContentTests` pins thirteen flags, Collector's
  twelve rows all flagged, and seven traders at 4. No unit test covers
  `GetCollectorProgress` or `GetKappaRequiredQuestsWithStatus`. Nor does one
  cover the Collector page's item-scope rules: which quests are in scope, which
  of their rows are listed, how currency counts and how the totals add up are
  private members of a page the unit suite cannot construct, so nothing runs
  them at all (TD18). E2E:
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
decides "done" against its own pass. The predicate is `pass.IsDone`, which
answers from the pass's own snapshots, so the gauge, the chips and the rows
agree on the profile; the detail pane and the list button use the pass of the
refresh that built the pane. `IsQuestCompleted` keeps its remaining caller
(`IntegratedItemService`) and is no longer used for Kappa.

A `RenderPass` and the status read over it are needed by two pages now, so the
pass moves out of `QuestListPage` into `Pages/RenderPass.cs` (internal), a
sealed class with a static `Capture(QuestProgressService)` and a
`Capture(QuestProgressService, QuestGraphService)` beside it for a caller that
holds a graph of its own. It carries the reads rather than leaving each page to
spell them: `StatusOf(task)` returns the status and the gate from one
`GetStatus(task, pass.Progress, pass.Settings, out gate)` call, `IsDone(task)`
is that status compared to `Done`, and the nullable `KappaProgress()` and
`KappaQuests()` answer the two graph readings with the built-yet guard inside
them (TD17). The private `StatusIn` adapter each page would otherwise have kept
is not written at all.

### 2. The Kappa quest list window (`Pages/Components/KappaQuestListWindow`)

For R3 and R4. The window `BtnShowKappaQuests_Click` builds inline moves to
`KappaQuestListWindow`, a `Window` subclass built in code as today, with one
static entry point: `Show(Window? owner, IReadOnlyList<(TarkovTask Quest,
bool IsDone)> quests, LocalizationService loc)`. It renders the header from
`loc.KappaQuestListTitle` ("Kappa quests ({0}/{1})") and the rows exactly as
before, and it takes nothing else: the header's pair is counted from the rows
it was given (`HeaderText`) and each row's name comes from
`loc.GetQuestName(quest)`, so no caller can hand the window a count or a name
that does not belong to the list it is showing, and neither click handler
walks the flagged quests a second time for a number. `owner` is nullable for
a caller that has no window, and the window then centres on the screen
(`StartupLocationFor`), because WPF's `CenterOwner` is not owner-null
tolerant and leaves an ownerless window wherever the OS cascades it. The
detail pane's button and the Collector page's button both call it, each with
its own pass, so the list is the same list from either place.

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
  the loaded data carries no Collector quest. Which row that is comes from
  `QuestGraphService.IsCollectorQuest` (static, over the service's own
  `CollectorNormalizedName` constant) rather than from an inline
  `NormalizedName` comparison: it is the one place the quest's identity is
  spelled, and the same rule the quest tab's Kappa section asks, so a publish
  that renamed the row cannot move one page and not the other.
- **The rule, pure and tested.** `CollectorUnlockViewModel` (in
  `CollectorViewModels.cs`, beside the page's other view models) with
  `internal static CollectorUnlockViewModel BuildFor(TarkovTask collector,
  QuestStatus status, QuestGate gate, ProfileSettingsSnapshot settings,
  LocalizationService loc, Func<QuestTraderRequirement, string>
  traderDisplayName, (int Completed, int Total)? kappa, RequirementLineBrushes
  brushes)` returning `StatusText` (from `QuestRequirementBadge.StatusText`),
  `Status` (for the brush), `Lines` (from `RequirementLineViewModel.BuildFor`)
  and `CountText` (from `loc.KappaCountFormat`). Non-nullable in and out
  except the count: "the loaded data has no Collector quest" is the page's
  case to answer, and `RebuildUnlockPanel` collapses the panel and returns
  before asking for a view model, so there is always a panel to build, while
  "the graph is not built yet" is an option the builder does take, because
  "0 of 0" is a reading the page does not have (TD17) and `CountText` is then
  empty. It contains no comparison
  of its own: which condition holds Collector is the engine's answer, carried
  in `gate`, and the badge and the first unmet line agree because both derive
  from it. The status brushes move from
  `QuestListPage` into `Pages/QuestStatusBrushes.cs` (internal static, the
  same six brushes and `For(QuestStatus)`), which both pages read; the two
  condition-line colours travel together as `Pages/RequirementLineBrushes.cs`
  (`Met`, `Unmet`, `FromResources(owner)`) rather than as a brush pair spelled
  at each call site, since `TextPrimaryBrush` is an App.xaml resource and needs
  a live element to resolve, which is exactly what the frozen
  `QuestStatusBrushes` must not need.
- **Building it.** `CollectorPage.RebuildUnlockPanel(RenderPass pass)` finds
  Collector, gets `(status, gate)` from `pass.StatusOf`, counts with
  `pass.KappaProgress()` (null when the graph is not built, which collapses
  the count and the button rather than painting "0/0"),
  calls `BuildFor`, and writes the panel's controls. It runs from the page's
  one load path (`LoadItems`, synchronous: it captures one pass, captures the
  list's scope from that pass, and hands the scope to the item aggregation
  and the pass to the panel, so the items and the panel describe one profile)
  and from a new settings path.
- **Settings events.** The page subscribes, in the same Loaded/Unloaded
  pairing it uses for its five existing subscriptions, to
  `SettingsService.Instance.PlayerLevelChanged`, `ScavRepChanged`,
  `TraderLoyaltyChanged` and `ProfileSettingsReloaded`, each requesting one
  `RefreshCoalescer.OnDispatcher(this, () => RebuildUnlockPanel(
  RenderPass.Capture(_questProgressService, _questGraphService)))`, so a published profile's
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
  becomes one `CollectorScope.QuestsInScope(collector, statusOf,
  prerequisitesOf, includePrerequisites)` returning the set, and neither caller
  calls `GetStatus(task)` live any more: the status arrives as a delegate the
  page answers from its pass (TD18). The set is walked once per load rather than
  once per selection: `CaptureListScope(pass)` stores it beside the pass and
  the option it was built under in a `ListScope(Pass, IncludePrerequisites,
  Quests)` record (`_listScope`), `GetCollectorItemRequirements(scope)` and
  `GetQuestSources` both read that stored scope through one
  `CollectorScope.ItemsInScope(tasks, quests)` walk over the (quest, required
  item) pairs, and the stats line reads the option from it rather than from the
  live checkbox (TD13). The set's content is unchanged (R6); under the 1.1 data
  the transitive walk is the twelve flagged quests.
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
caller, no test, no XAML binding references either. The "Kappa Path" the names
refer to is the achievement 1.1 retired.

The review's sweep extended the deletion by one more chain, three members
long: `ItemRequirementService.GetItemsForQuestPath` (nothing calls it), the
`QuestGraphService.GetOptimalPath` it was the only caller of, and the private
`TopologicalSort` that only `GetOptimalPath` reached. This section first said
that `AggregatedItemRequirement` and `TopologicalSort` "keep their other
users", which was a count of direct references; following the chain end to end
showed every one of `TopologicalSort`'s users to be dead itself, and the
sentence is corrected here rather than superseded because the document has not
merged yet. This section also said `AggregatedItemRequirement` "does stay", as
the return type of `ItemRequirementService`'s remaining members, and left open
whether that service is reachable at all. The review answered that question and
the answer deletes the file: `TarkovHelper/Services/ItemRequirementService.cs`
is unreachable in full, all 352 lines. A solution-wide search for the type name,
covering the four projects' `.cs`, `.xaml` and `.csproj` files, `nameof` uses,
the source-guard tests that read source by path, and `tools/DataDiff`, answers
only the file itself (plus build output and the 2025-12 todo under
`decisions/archive/`). Nothing constructs the singleton or calls
`Initialize`/`InitializeAsync`, and `AggregatedItemRequirement`,
`QuestItemReference`, `ItemRequirementDetail` and `ItemRequirementStats` have no
reference outside it, so there are no remaining members for
`AggregatedItemRequirement` to serve. Corrected in place on the same grounds as
above: the document has not merged, so no `Superseded by` line is owed.

The deletion is validated by the build and the existing suite staying green; no
test is added for it, because an assertion that a deleted member or type is
absent would be tautological.

### Files touched

- `TarkovHelper/Services/QuestGraphService.cs` (renamed count and list over
  tasks; `GetKappaPath` deleted, and at the review `GetOptimalPath` and
  `TopologicalSort`),
  `TarkovHelper/Services/ItemRequirementService.cs` (`GetKappaItems` and
  `GetItemsForQuestPath` deleted, then at the review the whole file, which
  nothing in the solution referenced),
  `TarkovHelper/Pages/RenderPass.cs` (new; moved out of the quest page, and at
  the review the status and Kappa readings moved onto it, TD17),
  `TarkovHelper/Pages/QuestStatusBrushes.cs` (new; moved out of the quest
  page), `TarkovHelper/Pages/Components/KappaQuestListWindow.cs` (new;
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
  moves. The review paid a little more of it than this estimate: the pass is
  read by three pages now (the Items page captures one too), the status
  brushes by two pages and the window, and the condition-line pair became a
  type of its own (`RequirementLineBrushes`) rather than two arguments spelled
  at each call. Revisit if a third page needs the same composition, at which
  point the composition itself (`CollectorUnlockViewModel.BuildFor`) is the
  thing to generalize.
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
  thread without a pass in hand. Appended at the review: the predicate is no
  longer written at the call sites either, since the pass answers it itself
  (TD17), so "inside the caller's render pass" is now structural rather than a
  convention every caller has to keep.
- **TD4: The Collector page subscribes to four settings events, not the
  quest page's eight.** The panel reads only the level, the Scav karma and
  the trader loyalty: those are the gates Collector itself carries, and the
  Kappa count moves on recorded progress alone. The item list is the part
  that needs stating carefully, because it DOES read the edition, prestige
  and faction values, transitively: its scope drops a quest whose status is
  `Unavailable`, and that is exactly what `QuestProgressService.GetStatus`
  returns for an unmet edition, prestige or faction gate, for Collector AND
  for every prerequisite in the scope. What keeps the list insensitive is the
  DATA and not this design: no quest in Collector's prerequisite closure
  carries one of those gates. The DSP count cannot matter either way, since
  its gate reads `Locked` and `Locked` stays in scope. So the five events
  this page skips (`HasEodEditionChanged`, `HasUnheardEditionChanged`,
  `PrestigeLevelChanged`, `DspDecodeCountChanged`, `PlayerFactionChanged`)
  cannot change anything it shows, and subscribing to all eight for symmetry
  was rejected as five handlers that can never fire; a publish that changed
  the data arrives through `DataRefreshed`, which rebuilds everything.
  Revisit when
  `PublishedDataContentTests.The_quests_Collector_depends_on_carry_no_edition_prestige_or_faction_gate`
  fails, which is what pins that data fact against the published file: a
  publish that gates one of those thirteen quests needs those five
  subscriptions here, and a settings refresh that reloads the ITEMS and not
  just the panel. (Corrected at the review, which found the original reason
  false: "the page's item list reads none of those values" is not why the
  decision holds.)
- **TD5: The list window is extracted, not duplicated and not turned into a
  page.** Two buttons on two pages open the same list; building it twice was
  rejected for the obvious reason, and a third tab or a page-level panel was
  rejected because the list is a glance, thirteen rows, and the existing
  popup already fits it. It stays code-built (as today) rather than XAML: the
  rows are data and there is nothing to declare beyond a scroll viewer and a
  stack.
- **TD6: The dead methods are deleted rather than kept for a future item
  planner.** They have had no caller since the 2025-12 todo that added them,
  they are named after an achievement the game removed, and the Collector
  page's "include prerequisites" option already lists the items across the
  twelve quests. Keeping them "in case" was rejected; if a planner is wanted,
  it is a new change with its own proposal. Appended at the review: the same
  grounds took the three members of the `GetItemsForQuestPath` ->
  `GetOptimalPath` -> `TopologicalSort` chain, which Design 5 first recorded
  as staying (see there for why that reading was wrong).
- **TD7: Only the strings this phase adds or changes are localized, and
  they all are.** The Collector page's other literals stay, per the product
  Non-Goal. Within the touched set the rule is all-or-nothing: the
  stats line is one composed string in three languages rather than a
  localized suffix on an English sentence.
- **TD8 (appended by the implementation): the quest tab's gauge counts within
  the remembered row pass.** Design 1 says the gauge counts "from the pass
  `ApplyFilters` already captured"; `ApplyFilters` captures no pass of its own,
  and the chips beside the gauge count from the `Status` each row cached when
  `LoadQuests` or `RefreshQuestStatuses` last captured one. So the pass those
  two producers capture is remembered, in `_rowPass` through the one
  `CaptureRowPass()` seam they both call, and the gauge counts within it through
  the same `pass.KappaProgress()` reading the detail pane and the quest window
  ask for (a per-page `KappaProgressIn` helper in the implementation, moved onto
  the pass itself at the review, TD17). That keeps Design 1's rationale - the gauge and the chips are one
  reading of one profile - without giving the gauge a done-source of its own.
  Counting the statuses cached on the row view models instead was tried and
  rejected: it answered for the flagged quests that happen to have a row rather
  than for the flagged set, and an `ApplyFilters` that ran before the first
  `LoadQuests` (the page subscribes to its service events in the constructor,
  while the load sits behind an await) read an empty row list as "none done"
  for a profile that had done some. The detail pane counts within the pass that
  built it, and the list button captures one pass at the click for its header
  and its rows rather than reusing the pane's, so a stale pane pass can never
  feed a fresh list. Pinned by `KappaProgressTests`, `KappaGaugeSourceTests`
  and the two e2e flows.
- **TD9 (appended): `QuestGraphService.IsInitialized` replaces the gauge's
  catch-all.** The gauge used to swallow every exception into "0/0", which
  could not tell "no data yet" from "nothing is flagged"; it is asked now,
  once, by the pass every surface reads through (TD17), which answers null
  rather than a count when the graph is not built.
  No number answers "no data yet" either, so the quest tab's gauge paints none:
  empty text and a zero-width bar whenever no row pass has been captured yet or
  the pass has no reading to give (TD8's two preconditions, read as one guard), and the
  real count a moment later when Loaded's own `ApplyFilters` runs.
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
  the panel only and only while loaded, and that one load captures one pass and
  threads it to both the aggregation's scope and the panel. "Every status this
  page reads comes from a pass" outgrew the page at the review and became a
  guard over the whole directory (`RenderPassStatusGuardTests`, TD17). The page also joins the existing mirror,
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
- **TD13 (appended): the detail panel's quest sources read the list's whole
  scope, not half of it.** `GetQuestSources` reads what the listed items were
  aggregated under rather than capturing a fresh reading, so the detail panel
  names exactly the quests whose items the list shows; every change that
  alters the scope reloads the list, and the detail with it. What is
  remembered is the whole input, in `_listScope` (null before the first load).
  Storing the pass alone, as the implementation first did in `_listPass`, left
  the other half of the same decision - the "include prerequisites" option -
  re-read from the live checkbox wherever it was wanted again, which let the
  stats line and the quest sources describe an option the list had not been
  rebuilt under; the review pass replaced it with a `ListScope(Pass,
  IncludePrerequisites, Quests)` record captured once per load by
  `CaptureListScope`, the one place the checkbox is read.
  `CollectorPageFixSelectionTests` pins that, and the selection the row swap
  used to drop with it.
- **TD14 (appended): Design order.** The detail pane's four strings (Design 4)
  landed before the window extraction (Design 2), because the window's title
  reads `KappaQuestListTitle`; otherwise the slices follow the Design order.
  Files touched beyond the list above: `QuestStatusBrushesTests`,
  `SourceGuards`, `ProfileDrawerDriver` and `QuestStartedEventTests` (new);
  `RefreshCoalescerSchedulingTests`, `E2ETestHarness`, `QuestLoyaltyE2ETests`
  and `LoyaltyFixtures` (three trader ids) touched; `QuestProgressService`
  touched for the defect recorded under Verification.
- **TD15 (appended at the review): one item fulfillment rule, over both
  halves of a requirement.** The Non-Goal above kept the fulfillment model out
  of this phase; the review reopened it, because the model was wrong on
  exactly the rows this page aggregates. An item Collector wants FIR and a
  prerequisite quest wants plain has `TotalCount` above `TotalFIRCount`, and
  the old rule branched on "is any FIR required" and answered Fulfilled as
  soon as the FIR half was owned: a row of three units, two of them FIR, read
  complete at two FIR owned, struck through, with its bar at 100%. The rule is
  now one shared `TarkovHelper.Models.ItemFulfillment` (`RequiredUnits`,
  `IsFulfilled`, `StatusOf`, `ProgressPercent`) over both halves: the FIR
  units cover the FIR half, the units left over cover the remainder, a FIR
  unit counts toward the remainder because a raid-found item is still an item,
  and a plain unit never fills a FIR slot. The three copies of the old rule
  read it now - the Collector rows (`CollectorItemViewModel`), the Items page
  rows (`AggregatedItemViewModel`) and `ItemFulfillmentInfo` - so two pages
  cannot answer one requirement differently. Files touched beyond the lists
  above: `TarkovHelper/Models/ItemInventory.cs` and
  `TarkovHelper/Pages/ItemsViewModels.cs`. Pinned by
  `CollectorItemFulfillmentTests` and `ItemsViewModelFulfillmentTests`.
- **TD16 (appended at the review): the prerequisite walk is two members, and
  the name carries the contract.** The Started fix recorded under
  Verification dropped the target at the call site, with a `.Where` over the
  walk's answer. That left every other caller reading a method named
  `GetAllPrerequisites` that answers the target too, which is the comment
  defect one level up. The walk is now split in `QuestGraphService`:
  `GetPrerequisiteClosure` returns the closure INCLUDING the quest itself as
  its last entry, and `GetAllPrerequisites` returns what its name promises,
  that closure minus the target, matched `OrdinalIgnoreCase` because the
  graph's own task lookup is. Each caller then reads the member it meant. The
  one consumer of the inclusion is
  `LogSyncService.CollectAlternativeQuestGroups`, where a logged quest that
  itself has mutually exclusive siblings forms its own selection group only
  by appearing in its own walk, so that single call is repointed to
  `GetPrerequisiteClosure`. The rest keep the name and get the exclusive
  answer, and none of them wanted the target: the Started plan drops its
  `.Where`, the Collector page had folded the target into a set that already
  held Collector, `InProgressQuestInputDialog`'s removal loops strip every
  selected quest anyway, and the debug menu is diagnostics. A rename was
  preferred over leaving one inclusive walk documented in prose, because the
  prose is what failed here. Pinned by `PrerequisiteWalkTests` (the two
  answers, a root, an unknown quest, a cycle, a diamond, the case-insensitive
  exclusion and both walks throwing before `Initialize`) and, for the
  repointed call site, by
  `LogSyncAttributionTests.A_logged_quest_with_mutually_exclusive_siblings_forms_its_own_group`,
  which fails if that call is ever made exclusive.
- **TD17 (appended at the review): the render pass answers the readings, and
  the pages hold nothing but the pass.** Design 1 moved the pass out of the
  quest page and left each surface to spell its own reads over it: a private
  `StatusIn(pass, task)`, and a private `KappaProgressIn`/`KappaQuestsIn` pair
  carrying the `QuestGraphService.IsInitialized` guard. The implementation
  landed four copies of that pair across two pages, with nothing stopping a
  fifth from forgetting the guard and painting "0/0" over a graph that is not
  built, which is the one thing TD9 introduced the guard to prevent. The reads
  move onto the pass itself: `StatusOf`, `IsDone`, and the nullable
  `KappaProgress()`/`KappaQuests()` with the guard inside them, null meaning
  "no reading" and never a zero. The graph they are answered from is a private
  field of the pass, set at the capture, so a surface holding a pass cannot
  reach past it for a live one; `Capture(progress)` takes the singleton and
  `Capture(progress, graph)` takes a caller's own, which is how the Collector
  page proves its item scope and its Kappa count were walked with the same
  object. The pass stops being the `readonly record struct` it started as and
  becomes a sealed class: with two services in it, it is a read context rather
  than a value, equality over service references would mean nothing, and
  `default(RenderPass)` (a pass whose services are null) stops being spellable.
  Leaving the helpers on the pages and pinning them with a per-page source
  guard was rejected: the guard would have to be written once per page, which
  is the same defect one level up. The directory-wide
  `RenderPassStatusGuardTests` names the files under `Pages/` allowed to read a
  status: the pass itself, and the map page, which captures none and is on the
  list on purpose so the list can only shrink. A new page reading one live
  fails a test instead of passing review. Pinned by `RenderPassStatusTests`
  (5 cases over `StatusOf`, `IsDone` and the two Kappa readings) and
  `RenderPassStatusGuardTests` (1); `KappaGaugeSourceTests` reads the same way
  and now also pins that the built-yet precondition is asked inside the pass's
  readings and nowhere else in the page.
- **TD18 (appended at the review): the Collector page's item-scope rules are
  domain rules, and live in `Services/CollectorScope.cs`.** Design 3 made
  "which quests are in scope" one method, but left it and the four rules
  around it (which of their rows are listed, which items are currency, how the
  totals add up, and the per-item aggregate they add up into) private instance
  members of a WPF control. No unit test ran them: the page cannot be
  constructed in the unit suite, because its markup resolves App.xaml's brushes
  through StaticResource and its field initializers open the databases, so what
  covered those five rules was indirect, a source guard reading the page's text
  and e2e flows that assert a rendered list. They read no service and touch
  no control, so the page was never their home. `CollectorScope` (internal
  static) now holds `IsInScope`, `QuestsInScope`, `ItemsInScope`, the currency
  set with `IsCurrency`, and `Aggregate`, and the DTO moves to
  `Models/CollectorQuestItemAggregate.cs` beside the other models. The two
  service questions arrive as delegates (`statusOf`, `prerequisitesOf`), so
  each rule stays unable to reach past the caller's pass for a live reading
  (TD3, TD17). The page keeps what is genuinely its own: `ListScope` and
  `CaptureListScope` (the one place the checkbox is read, TD13), `FindCollector`,
  and two one-line wrappers that supply the page's own inputs. Behaviour is
  unchanged, which is the point of doing it here rather than in a later change:
  the twenty cases of `CollectorScopeTests` are the first to run these rules at
  all, and they pin the boundaries the page's own tests never reached (a
  Collector that is Done, Failed or Unavailable; an unavailable prerequisite
  against a Locked one; no Collector quest in the data; a quest with no
  normalized name, no item rows, or an item the Items table does not carry;
  currency counted once per asking quest; the found-in-raid half counted only
  for the rows that asked). Files touched beyond the lists above:
  `TarkovHelper/Services/CollectorScope.cs` and
  `TarkovHelper/Models/CollectorQuestItemAggregate.cs` (both new).
- **TD19 (appended at the review): a mid-session data publish republishes the
  task set.** Out of this phase's theme, but found by it: the Kappa
  denominator is read from `QuestGraphService`, and `QuestGraphService` and
  `QuestProgressService` are the only two caches that do NOT reload themselves
  when `DatabaseUpdateService` publishes. They are HANDED their tasks at
  startup, so an hourly publish left every quest surface (they all render
  `QuestProgressService.AllTasks`) and the Kappa count on the previous publish
  until restart, beside a freshly reloaded item table. MainWindow's
  `QuestDbService.DataRefreshed` handler, until now the loyalty roster's
  (`OnQuestDataRefreshedForLoyalty`), owns the republish and is renamed
  `OnQuestDataRefreshed` for it. It calls
  `QuestProgressService.PublishTasks(tasks)` and
  `QuestGraphService.Instance.Initialize(tasks)` before `BuildLoyaltyGroup()`,
  which reads the republished rows. `PublishTasks` is a new split out of
  `Initialize`, indexing only: a publish changes which quests exist, never
  which rows the player recorded, and the snapshot is keyed by
  Id/NormalizedName rather than by task instance, so re-reading progress here
  would block the dispatcher on a user-DB read and re-run the startup-only
  "which profile is selected" read for nothing. An empty reload is treated as a
  failed load and keeps the task set on screen. `Dispatcher.Invoke` and not a
  posted callback, since the pages queue their own reloads with
  BeginInvoke/InvokeAsync and the republish has to win whatever order the
  subscribers run in. Hideout modules deliberately stay out: `HideoutDbService`
  reloads on its own event and republishing from here would race it, and
  `HideoutProgressService.Initialize` has no progress-free half to call. That
  gap is recorded where the publish channel is designed
  (`feature-versioned-data-channel.spec.md`). Pinned by `PublishTasksTests` (4:
  the swap leaves the recorded rows untouched, a publish with no overlap keeps
  the rows of the quests it removed, an empty publish publishes as empty) and
  by `MainWindowDataUpdateHandlerTests.
  The_quest_data_refresh_republishes_the_task_set_before_rebuilding_the_roster`,
  which pins the order, the inline dispatch and that `Initialize` is not the
  member called. Files touched beyond the lists above:
  `TarkovHelper/MainWindow.xaml.cs` and
  `TarkovHelper/Services/QuestProgressService.cs`.
- **TD20 (appended at the review): the Found In Raid half is a value, not a
  pair of names.** Every quantity read, write and handler in the app was
  written twice, once per kind. `ItemInventoryService` held byte-identical
  twenty-line setters and three-line adjusters, and each item page held a
  per-kind spinner method, a per-kind detail method and its own pair of text
  handlers per box. `Models/ItemInventory.cs` now carries a `FirKind` enum,
  and `ItemInventory` the `QuantityOf`/`SetQuantityOf` pair that is the only
  place a kind becomes a field. The service writes each rule once, in
  `GetQuantity`, `SetQuantity` and `AdjustQuantity`; `ItemRowViewModel` does
  the same for a row, with `Owned(kind)` and `SetOwned(kind, quantity)`. Both
  pages collapse to one `AdjustRowQuantity(sender, kind, delta)` and one
  `AdjustDetailQuantity(kind, delta)`, neither of which branches on the kind,
  and their four text handlers to one `LostFocus`/`KeyDown` pair over a
  single linear `ApplyQuantityFromTextBox(box, kind)`, reached through one
  `ApplyQuantityFromSender` resolver. The shape is the decision: the kind is
  still spelled once per control at the XAML boundary, where the handler name
  says which half it edits (`BtnFirPlus1_Click`), so the kind is data a
  caller supplies at the edge and never a flag an inner caller can get wrong.
  The two text boxes, which already shared `PreviewTextInput`, are the one
  exception and are told apart by sender, in the resolver and nowhere else.
  The six per-kind service names stay as one-line delegations rather than
  being removed: they read well where a call site genuinely means one kind
  (`IntegratedItemService` fills both halves of a fulfillment row), every
  existing caller compiles untouched, and a one-line body is no second copy
  of the rule to drift. One behaviour changes with the merge, on the no-op
  path only: writing a quantity an item already holds no longer leaves a
  phantom empty entry behind for an unknown item, because the old setter
  created the entry before it tested for a change. Neither shape saves or
  raises `InventoryChanged` there, nothing is persisted either way, and the
  two members that could have observed such an entry, `GetAllInventory` and
  `GetStatistics`, have no caller anywhere in the solution. Pinned by
  `ItemInventoryKindTests`, which runs every case it can as a theory over
  both kinds: the write, the clamp at zero, the adjust past zero, the no-op
  write, `Writing_zero_for_an_unknown_item_leaves_no_entry_behind` for the
  delta above, that the per-kind names still read and write the same half
  through the same body, and the source guards that no page declares a
  per-kind quantity method and that each per-kind service name is one line.
  Files touched beyond the lists above:
  `TarkovHelper/Services/ItemInventoryService.cs`,
  `TarkovHelper/Pages/ItemsPage.xaml(.cs)`, and
  `TarkovHelper/Models/ItemInventory.cs` (already named by TD15).
- **TD21 (appended at the review): one item row, shared by both item pages.**
  `CollectorItemViewModel` and `AggregatedItemViewModel` were member for
  member the same class, typed twice: the same icon and subtitle, the same
  two quantity halves with their change notifications, the same fulfillment
  reading, the same count strings and the same dimmed, struck-through look
  once a row is satisfied. TD15 had already moved the rule itself into
  `ItemFulfillment` and the shapes into `ItemCountDisplay`, which left the
  two classes duplicating the plumbing around them. The row is now one
  abstract `TarkovHelper/Pages/ItemRowViewModel.cs`, and a page's own type
  adds only what that page aggregates: the Collector row adds nothing, and
  the Items row keeps `Category`, `ParentCategory`, `HideoutCount`,
  `HideoutFIRCount` and `HideoutDisplay`. XAML binds an inherited member
  exactly as it binds a declared one, so the move itself needed no markup
  change. The Collector row stays a distinct sealed type rather than becoming
  the base itself, because `CollectorPage` pattern-matches on it
  (`btn.DataContext is CollectorItemViewModel`) to tell its own rows from
  whatever else a handler may be handed. One member is renamed where the two
  names differed: the Collector row's `QuestCountDisplay` becomes the Items
  row's `QuestDisplay`, beside `TotalDisplay` and `OwnedDisplay`, and the
  Collector page's one binding and its detail-pane writer follow it. Pinned
  by `ItemRowViewModelTests`, which asserts every shared reading through the
  base type against both concrete rows, and by its
  `A_page_row_declares_only_what_that_page_aggregates`, which fails if either
  page grows a second copy of a shared member. Files touched beyond the lists
  above: `TarkovHelper/Pages/ItemRowViewModel.cs` (new),
  `TarkovHelper/Pages/CollectorViewModels.cs` and
  `TarkovHelper/Pages/ItemsViewModels.cs`.
- **TD22 (appended at the review): the Items page renders from a captured
  pass too.** The Items page is outside this phase's theme, but it held the
  defect TD3 and TD17 are about. Its list aggregation and its detail pane
  each walked the quests reading a live per-quest status, against the two
  snapshots that background work republishes atomically. So a publish landing
  mid-walk could leave the list mixing two profiles, and the pane naming
  quests the listed rows had not counted. The page captures one `RenderPass`
  per load into `_listPass` now, and asks the pass:
  `GetQuestItemRequirements(pass)` builds the rows, and the remembered pass
  answers the detail pane, which has no sources to name before the first
  load. That is what makes the pass a thing three pages capture (TD1), and it
  is why `RenderPassStatusGuardTests` exempts one file only,
  `Map/MapPage.xaml.cs`, which paints its markers off the live singletons and
  captures no pass. Converting the page was preferred to adding it to that
  exemption list: the list is written so that it can only ever shrink, and a
  page that renders both halves of itself from snapshots is what the pass is
  for. Pinned by `ItemsPageRenderPassTests` (one capture per load and one in
  the file, the pass threaded into the aggregation, the detail pane answering
  from the remembered pass and empty before the first load, and no live
  per-quest status read left on the page).

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
  line is met; a Done status reads `Done` with met lines. The missing-quest
  case belongs to the page rather than to the builder, which takes a non-null
  quest, so the page's own guard is pinned at the source instead, by
  `The_page_collapses_the_panel_when_the_data_has_no_Collector_quest`. The
  badge text is asserted against `QuestRequirementBadge.
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
- **R8 (existing)**: `DataFormatDriftTests` and `SeedDatabaseTests`
  untouched and green; `git diff --stat data/` empty in the PR.
  `PublishedDataContentTests` keeps its cases and gains one at the review,
  `The_quests_Collector_depends_on_carry_no_edition_prestige_or_faction_gate`,
  which walks Collector's prerequisite closure in the published file and
  asserts no row in it carries a `Faction`, `RequiredEdition`,
  `ExcludedEdition` or `RequiredPrestigeLevel`. It is the guard TD4 now
  rests on rather than on the page's code. It passes today by design, so
  there is no failing "before" for it; that the assertion is live was shown
  by running the same query against a copy of the file with one closure quest
  gated, which returns that row.
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
  prerequisite walk it plans over includes the target as its last entry (the
  walk's comment claimed otherwise, and is corrected). Own commit, own tests
  (`QuestStartedEventTests`); nothing had covered the Started path. The
  review then moved that correction out of the caller and into the walk's own
  name (TD16).
- Appended at the deep review of this PR (branch
  `fix/kappa-collector-1-1-review-fixes`): the review corrected the
  implementation where TD8, TD9, TD13 and TD15 to TD19 now record it, and added
  `KappaGaugeSourceTests`, `KappaWindowListTests`, `CollectorIdentityTests`,
  `CollectorItemFulfillmentTests`, `ItemsViewModelFulfillmentTests`,
  `CollectorPageFixSelectionTests`, `PrerequisiteWalkTests`,
  `RenderPassStatusTests`, `RenderPassStatusGuardTests` (TD17),
  `CollectorScopeTests` (TD18), `PublishTasksTests` (TD19),
  `ItemInventoryKindTests` (TD20), `ItemRowViewModelTests` (TD21),
  `ItemsPageRenderPassTests` (TD22) and `RequirementLineBrushesTests`,
  plus one case
  each in `LogSyncAttributionTests`, `MainWindowDataUpdateHandlerTests` and
  `PublishedDataContentTests`. It also
  corrected the claims this document made about the code rather than about a
  decision: the dead-code count (Design 5), TD4's reason, the name of the
  prerequisite walk (TD16), and the members and shapes Designs 1 and 3 name,
  which the moves under TD17 to TD19 changed. The suite counts above belong to `01ff489`
  and are not restated for the review's revision; that run is reported in the
  PR body with the review's own commits.
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
- **Three pages, one pass type.** The Collector page now captures snapshots the
  way the quest page does; a future page that reads status should do the
  same rather than call `GetStatus(task)` live. Written as a convention at the
  implementation, with the reason in `RenderPass`'s own comment; at the review
  it became a rule the suite enforces, since the pass answers the status itself
  and `RenderPassStatusGuardTests` names the files under `Pages/` allowed to
  ask the service (TD17). The map page is the one name on that list, and the
  list can only shrink. The live overload stays for callers that are not
  rendering and are right to read the current state: the quest recommendation
  service, the sync comparisons and the in-progress quest dialog.
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
