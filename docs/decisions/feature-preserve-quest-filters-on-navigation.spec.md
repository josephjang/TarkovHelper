# Preserve Quest Filters on Navigation — Technical Spec

- **Created**: 2026-08-01

> The sibling `feature-preserve-quest-filters-on-navigation.md` holds the product
> decision. Written on the work's branch and merged in the same PR as the work.
> Nothing is kept current: fields are written once, discoveries are appended. A
> later change that reverses a decision here appends `Superseded by <doc>` below
> this line, in the PR that reverses it.

> Superseded in part by `2026-09-22-remove-quest-recommendations.md` (2026-09-22):
> the recommendations panel is removed, so `RecommendationsPanel_RecommendationClicked`
> is no longer a navigation entry point, and the E2E check (5), clicking a
> recommendation, is deleted with it. Every other decision here stands.

## Summary

`QuestListPage.SelectQuestInternal` stops resetting filters. It rests on two ideas:
the detail panel can already display a quest independently of the list
(`UpdateDetailPanel` takes an override view model, tracked in `_currentDetailTask`),
and the list never needs rebuilding on navigation because it already reflects the
current filters. Navigation becomes: select-and-scroll when the target passes the
current filters, otherwise show the details with the list deselected and a notice
offering an explicit "show in list" action that performs today's reset.

## Non-Goals

- No navigation/back stack, and no change to how the detail panel renders quest
  content — only to how navigation interacts with filters and selection.
- No change to `MapPage` / `MapQuestMarkerManager` quest markers: they raise
  `ObjectiveSelected` inside the map page and never enter this path.
- No change to the `_pendingQuestSelection` deferral in `SelectQuest` (navigation
  before data load); it continues to replay into `SelectQuestInternal`.

## Current Behavior / Root Cause

All four quest-navigation entry points converge on
`QuestListPage.SelectQuestInternal`:

- `PrerequisiteQuest_Click` (prerequisite links in the detail panel)
- `RecommendationsPanel_RecommendationClicked` (via
  `QuestRecommendationsPanel.RecommendationClicked`)
- `ItemsPage.QuestName_Click` and `CollectorPage.QuestName_Click`, both through
  `MainWindow.NavigateToQuest` → `QuestListPage.SelectQuest`

`SelectQuestInternal` starts with `ResetFiltersForNavigation()`, which sets
`CmbStatus` to *All*, clears `TxtSearch`, unchecks `ChkKappaOnly` and
`ChkItemRequired`, and resets `CmbTrader` and `CmbMap` — under an `_isInitializing`
guard so the per-control handlers don't each call `ApplyFilters`. It then runs
`ApplyFilters()` to rebuild the list (necessary only because the reset changed the
filters), and finally selects the target with `LstQuests.SelectionChanged` detached
and forces `UpdateDetailPanel(questVm)`.

The reset exists solely to guarantee the target appears in the left list; the
`UpdateDetailPanel(overrideVm)` mechanism it already uses proves the detail display
does not need it. Two side effects of the current shape: the `questVm == null`
early return sits *after* the reset, so navigating to an unknown quest wipes
filters and does nothing else; and because `ApplyFilters` replaces
`LstQuests.ItemsSource`, a plain filter change by the user clears the selection and
collapses the detail panel even when the selected quest is still in the list.

## Design

All changes are in `TarkovHelper` (no other project changes):

- `Pages/QuestListPage.xaml.cs` — navigation, filter, and notice logic
- `Pages/QuestListPage.xaml` — notice UI in the detail panel
- `Pages/QuestListFilter.cs` (new) — extracted filter predicate
- `Services/LocalizationService.Quest.cs` — notice strings (EN/KO/JA)
- `TarkovHelper.Tests/QuestListFilterTests.cs` (new) — unit guards
- `TarkovHelper.Tests/QuestNavigationE2ETests.cs` (new) — end-to-end coverage

**`SelectQuestInternal` reshape.** Resolve `questVm` first and return early if the
quest is unknown (nothing mutated). Do not reset filters and do not call
`ApplyFilters` — the list already reflects the current filters. Then branch on
whether `LstQuests.ItemsSource` (the filtered `List<QuestViewModel>`; same
instances as `_allQuestViewModels`, so reference `Contains` suffices) holds the
target:

- *Visible*: hide the notice, set `LstQuests.SelectedItem` and `ScrollIntoView` —
  `LstQuests_SelectionChanged` updates the panel as for a normal click. The
  detach/`Dispatcher.BeginInvoke` choreography existed to survive the
  `ItemsSource` swap and is no longer needed on this path.
- *Hidden*: clear `LstQuests.SelectedItem` with `SelectionChanged` detached (so the
  panel isn't collapsed by the null selection), call `UpdateDetailPanel(questVm)`,
  and show the notice.

`ResetFiltersForNavigation` is kept but is no longer called from any navigation
path — only from the notice's button (below).

**Notice UI.** A `Border` (`PnlFilteredOutNotice`) at the top of `DetailPanel` in
`QuestListPage.xaml`, collapsed by default: a `TextBlock` (`TxtFilteredOutNotice`)
with the localized "hidden by current filters" text and a `Button`
(`BtnShowInList`). `BtnShowInList_Click` runs `ResetFiltersForNavigation()`,
`ApplyFilters()`, then selects and scrolls to `_currentDetailTask`'s view model and
hides the notice — exactly the old navigation behavior, now user-invoked. Strings
follow the existing `CurrentLanguage switch` pattern in
`LocalizationService.Quest.cs` (e.g. `QuestHiddenByFilters`, `ShowInList`) and are
applied wherever the page refreshes localized UI text.

**Filter-change reconciliation (R6).** `ApplyFilters` replaces
`LstQuests.ItemsSource` with `SelectionChanged` detached, then reconciles against
`_currentDetailTask`: if its view model is in the new filtered list, restore
`SelectedItem` to it and hide the notice; if not, leave the detail panel showing it
and show the notice (the panel no longer collapses on a filter change). A real user
click on a list row still flows through `LstQuests_SelectionChanged`, which updates
`_currentDetailTask` and hides the notice.

**Filter predicate extraction.** The inline `Where` lambda in `ApplyFilters` moves
to a pure static `QuestListFilter.Matches(QuestViewModel vm, QuestFilterCriteria
criteria)` in `Pages/QuestListFilter.cs`, where `QuestFilterCriteria` is a record
of the seven filter inputs (search text, Kappa-only, item-required, trader, map,
status tag, faction). `ApplyFilters` builds the criteria from the controls and
filters through it — no behavior change, but the predicate (status mapping, faction
exception, multi-language search) becomes unit-testable without WPF.

## Technical Decisions

**Membership check against `ItemsSource` instead of re-evaluating the predicate.**
The filtered list is already the source of truth for "visible under current
filters", and the view-model instances are shared with `_allQuestViewModels`, so a
reference `Contains` cannot drift from what the user sees. Re-running the predicate
for one item would duplicate the decision `ApplyFilters` already made.

**`ApplyFilters` swaps `ItemsSource` with `SelectionChanged` detached.** Today the
swap fires `SelectionChanged` with a null selection, which is why navigation needed
its own detach choreography and why filter changes collapse the detail panel.
Routing all `ItemsSource` swaps around the handler makes `SelectionChanged` mean
exactly "the user picked a row", and the reconciliation step decides panel state
explicitly. The alternative — keeping the event hot and special-casing null
selections inside the handler — spreads the same logic across two places.

**Restore selection after filter changes.** Reconciliation re-selecting
`_currentDetailTask` when it survives the new filters is a deliberate behavior
improvement beyond the PRD's minimum (today the selection is dropped and the panel
collapses on every filter change). It falls out of the same reconciliation needed
for R6, so implementing R6 without it would take extra code to preserve a worse
behavior.

**Predicate extraction over UI-level unit tests.** Driving `QuestListPage` in unit
tests would require a WPF dispatcher and page construction; extracting the pure
predicate gets the filter semantics under test cheaply, and the E2E tests cover the
wiring. The extraction is behavior-preserving by construction (same expression,
inputs captured into a record).

**(Appended during implementation) `ResetFiltersForNavigation` became
`ResetFilters`.** With no navigation path calling it, the "ForNavigation" suffix
described a caller that no longer exists; its sole caller is `BtnShowInList_Click`.

**(Appended) The reveal action does not force visibility past the faction filter.**
`ResetFilters` deliberately leaves the faction toggle alone — it is a profile
setting, not a filter default — so an other-faction quest can stay hidden after
"Show in list", in which case the notice simply stays up, truthfully. The old
navigation reset had the same hole (its highlight silently failed); the difference
is that the state is now visible instead of silent.

**(Appended) `UpdateDetailPanel` falls back to `_currentDetailTask`.** Refresh
paths (progress/language/DB events) call `UpdateDetailPanel()` with no override
while the shown quest may have no list selection; resolving the shown quest as
`override ?? selection ?? _currentDetailTask` (matched by `NormalizedName`, so DB
reloads with fresh instances still resolve) keeps the panel from collapsing. The
empty-state placeholder still appears until the first quest is viewed.

**(Appended) E2E probes work around missing UIA peers.** WPF `Border`/`StackPanel`
have no UI Automation peers, so `PnlFilteredOutNotice` itself is invisible to UIA —
the tests probe `BtnShowInList` instead. For the same reason the Items/Collector
detail `ScrollViewer`s gained `x:Name="DetailScrollViewer"` (test addressing only),
and the harness grew real-mouse clicking (`ClickElement` and scroll-and-retry
`ClickTextElementWithScroll`) because the app's TextBlock links are wired via
`MouseLeftButtonDown`, which exposes no `InvokePattern`.

**(Appended after deep review) Scroll is deferred again on list-mutating paths.**
The claim above that the `Dispatcher.BeginInvoke` choreography "is no longer
needed" held only for plain in-list navigation: the deep-link replay (`Loaded` →
`ApplyFilters` → `SelectQuestInternal`) and `BtnShowInList_Click` both scroll
right after an `ItemsSource` swap, where a synchronous `ScrollIntoView` can
silently fail to scroll the virtualizing panel. Both paths now scroll via
`ScrollQuestIntoView` (`UpdateLayout` + `ScrollIntoView` deferred to
`DispatcherPriority.Loaded`) — the old guard, kept in one named place.
`SelectQuestInternal` also re-establishes the old precondition by running
`ApplyFilters()` first if `ItemsSource` is not yet the filtered list.

**(Appended after deep review) The detail-panel actions resolve the shown quest,
not the selection.** `BtnComplete_Click`/`BtnReset_Click`/`BtnWiki_Click` read
`LstQuests.SelectedItem`, which this design intentionally leaves null while the
shown quest is filtered out — so the buttons silently no-opped in exactly the
state the notice advertises. They now resolve through `ShownQuestViewModel()`
(`selection ?? _currentDetailTask`), the same rule `UpdateDetailPanel` uses;
covered by the `Detail_buttons_act_on_the_shown_quest_while_it_is_hidden_by_filters`
e2e test.

**(Appended after deep review) Explicit deselection collapses the panel.**
Because programmatic mutations run with `SelectionChanged` suppressed (a
`SuppressSelectionChanged` scope shared by `ApplyFilters` and
`SelectQuestInternal`), a null selection reaching the handler is the user
Ctrl+Clicking the selected row. The `_currentDetailTask` fallback would otherwise
resurrect the just-deselected quest and make the empty placeholder unreachable;
`LstQuests_SelectionChanged` now collapses via `ClearDetailPanel()` (which also
forgets `_currentDetailTask`), restoring the pre-change deselect behavior. A
shown quest that vanishes from the loaded data (DB reload) collapses the same
way. Covered by the `Ctrl_click_deselect_collapses_the_detail_panel` e2e test.

**(Appended after deep review) E2E launches disable DB auto-update.** The app's
immediate background check could download a newer `tarkov_data.db` over the
build-output Assets copy mid-test, silently diverging from the static copy the
tests derive their expectations from. `AppDriver.Launch` sets
`TARKOVHELPER_DISABLE_DB_UPDATE`, honored by
`DatabaseUpdateService.StartBackgroundUpdates` (see `AppEnv.DisableDbUpdate`).

## Test Strategy

- **Unit** (`QuestListFilterTests`): lock down the predicate invariants that the
  navigation change now leans on — the *Locked* tag matches both
  `QuestStatus.Locked` and `QuestStatus.LevelLocked`; *All* passes every status;
  faction-restricted quests are hidden except under the *Unavailable* tag; search
  matches `Name`/`NameKo`/`NameJa` case-insensitively; empty criteria pass
  everything; each single-criterion rejection (trader, map, Kappa, item-required).
- **E2E** (`QuestNavigationE2ETests`, `Category=E2E`, driven through `AppDriver`
  UI Automation; controls addressed by `x:Name` as AutomationId): with the status
  filter on *Active* plus a trader filter and search text set, (1) clicking a
  prerequisite link of an active quest shows the prerequisite's details, leaves
  every filter control unchanged, and shows the notice with no list row selected;
  (2) `BtnShowInList` resets the filters and selects the quest in the list;
  (3) navigation from the Items page and (4) from the Collector page switches tabs
  and preserves the Quests-tab filters; (5) clicking a recommendation preserves
  filters; (6) changing the status filter so the shown quest becomes visible
  selects it in the list and hides the notice.
- The `_pendingQuestSelection` replay (navigation before data load) is exercised
  implicitly by the cross-tab E2E launched fresh; it has no new logic of its own.

## Verification

- `dotnet build TarkovHelper.sln`
- `dotnet test --filter Category!=E2E` (unit + doc guards), then
  `dotnet test --filter Category=E2E` on an interactive desktop
- Manual: run via `dotnet TarkovHelper.dll` (bypasses the requireAdministrator
  manifest), set status *Active* + a search term, click a Done prerequisite —
  observable result: filters and search box unchanged, prerequisite details shown,
  notice visible; the notice button reproduces the old reset-and-highlight
  behavior.

## Risks & Migration

No data or settings migration — the change is confined to in-page interaction
state. Rollback is a straight revert; no persisted format changes. The main
regression surface is selection/panel state after `ItemsSource` swaps (the detach
choreography moves from navigation into `ApplyFilters`), which is exactly what the
E2E set pins down.
