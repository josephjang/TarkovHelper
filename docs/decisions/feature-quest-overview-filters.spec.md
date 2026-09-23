# Quest Overview & Filters — Technical Spec

- **Created**: 2026-08-03

> The sibling `feature-quest-overview-filters.md` holds the product decision.
> Write this on the work's branch and merge it in the same PR as the work. Nothing
> is kept current: fields are written once, discoveries are appended. A later
> change that reverses a decision here appends `Superseded by <doc>` below this
> line, in the PR that reverses it.

> Superseded by `feature-quest-chip-only-status-filter.spec.md` (2026-08-07), in
> part: `CmbStatus` is removed — the "Chips route through `CmbStatus`" decision and
> the `TxtStats` step of the data flow no longer hold; the chips own the status
> state directly. Everything else here stands.

> Superseded in part by `2026-09-22-remove-quest-recommendations.md` (2026-09-22):
> the recommendations panel is removed: the `questList.recommendationsExpanded`
> key, the `QuestRecommendationsPanel` tooltips and the note on the panel
> restoring its expander in its own Loaded no longer hold. Every other decision
> here stands.

## Summary

A new `QuestListSettings` service (the `MapSettings` pattern over
`UserDataDbService.GetSetting/SetSetting`) persists the Quest tab's UI state; the
statistics line is replaced by chip Buttons rendered from a new pure helper
`QuestListFilter.CountByStatusTag`; a zero-results overlay and a
`DispatcherTimer` search debounce round out the page changes. The one
non-obvious idea: persistence is gated on `_isDataLoaded` so that service events
racing ahead of the Loaded-time restore cannot overwrite the store with XAML
defaults.

## Non-Goals

- No re-architecture of `QuestListPage.ApplyFilters` — it stays the single
  apply/render pass; this change only extends it.
- Trader/map are persisted by their combo `Tag` values (raw trader string,
  normalized map name), not display names — display names are locale-dependent.

## Current Behavior / Root Cause

`ApplyFilters` built a one-line stats string into `TxtStats` from
`QuestProgressService.GetStatistics` (global counts, not click-previews). No
`questList.*` keys existed in the settings store — every filter reset per launch
(the faction radio persists separately as `SettingsService.PlayerFaction`, a
profile setting, not a filter). `TxtSearch_TextChanged` called `ApplyFilters`
synchronously per keystroke. Zero-result filtering left the ListBox blank.

## Design

Changed/new files:

- `TarkovHelper/Services/Settings/QuestListSettings.cs` (new) — keys
  `questList.kappaOnly/itemRequired/trader/map/statusTag/detailPanelWidth/recommendationsExpanded`,
  cached values, change-detected saves, clamped width (250–800, default 350).
- `TarkovHelper/Pages/QuestListFilter.cs` — `CountByStatusTag(viewModels,
  criteria, statusTags)`: per-tag counts via `criteria with { StatusTag = tag }`.
- `TarkovHelper/Pages/QuestListPage.xaml` — `StatusChipStyle` + five chip
  Buttons (`ChipActive`…`ChipUnavailable`) on the statistics bar; `PnlEmptyState`
  overlay (title/hint/`BtnResetFilters`) inside the list border; tooltips on the
  row name/subtitle; `DetailColumn` named + `DetailSplitter.DragCompleted`.
- `TarkovHelper/Pages/QuestListPage.xaml.cs` — restore in Loaded
  (`RestoreFilterSettings`, `RestoreDetailPanelWidth`), persistence + empty state
  + `UpdateStatusChips` in `ApplyFilters`, `StatusChip_Click`,
  `BtnResetFilters_Click`, debounced `TxtSearch_TextChanged`,
  `SelectComboByTag` helper.
- `TarkovHelper/Pages/Components/QuestRecommendationsPanel.xaml(.cs)` — tooltips
  on truncated texts; expander state restore/save.
- `TarkovHelper/Services/LocalizationService.Quest.cs` — `QuestListEmptyTitle`,
  `QuestListEmptyHint`, `ResetFiltersButton` (EN/KO/JA).
- Tests: `TarkovHelper.Tests/QuestListFilterTests.cs` (CountByStatusTag),
  `TarkovHelper.Tests/QuestOverviewFiltersE2ETests.cs` (new),
  `TarkovHelper.Tests/E2ETestHarness.cs` (`ToggleElement`, `GetToggleState`,
  `GetListItemCount`), `TarkovHelper.Tests/QuestNavigationE2ETests.cs`
  (debounce-safe wait in `NavigateToHiddenPrereq`).

Data flow: `ApplyFilters` snapshots the filter bar into `QuestFilterCriteria`,
filters + sorts as before, then: empty-state visibility from the filtered count →
persist snapshot (gated, below) → `TxtStats` shrinks to `Lv.{n} | {shown}/{total}`
→ `UpdateStatusChips` renders counts + selected-chip visuals → kappa gauge.

## Technical Decisions

**Persistence is gated on `_isDataLoaded`, not `_isInitializing`.** Found via a
failing e2e relaunch test: at startup `ProfileService`'s auto-switch raises
`PlayerFactionChanged` before `QuestListPage_Loaded` (awaiting
`ItemDbService.LoadItemsAsync`) has restored the saved filters, and
`OnPlayerFactionChanged` used to force `_isInitializing = false` before calling
`RefreshAllForStateChange` — so an early `ApplyFilters` persisted the XAML
defaults over the saved state, and the later restore read back the clobbered
values. The save now requires `_isDataLoaded` (set true only after the restore),
and `OnPlayerFactionChanged` restores the prior `_isInitializing` instead of
forcing false.

**Chips route through `CmbStatus`.** A chip click only changes the combo
selection (`SelectComboByTag`); the combo's `SelectionChanged` applies filters,
and `UpdateStatusChips` derives every chip visual from the criteria snapshot.
There is no separate chip state to drift out of sync.

**Counts use `criteria with { StatusTag = tag }`.** The record's `with` copy
carries the precomputed `NormalizedSearchText` backing field (the record doc
already warns a `with` on `SearchText` would not recompute it — here that copy
semantics is exactly right, and a unit test pins it).

**`QuestListSettings` is first touched from Loaded handlers, never
constructors.** `MainWindow` constructs the pages while `UserDataDbService` may
not be initialized; a constructor-time singleton would cache defaults forever.
The recommendations panel restores its expander in its own Loaded with a
once-guard (`_expanderStateRestored`), because the e2e harness's tab-bounce
detaches/reattaches the page and re-fires Loaded.

**Search debounce is 250 ms and any explicit `ApplyFilters` cancels the pending
tick** — a combo/checkbox change never races a stale pending search apply; the
snapshot it takes already contains the latest text.

**The e2e harness waits for the filtered list, not the keystroke.**
`NavigateToHiddenPrereq` used `SetTextBoxValue` + `SelectListItemAt(0)`
back-to-back; with a debounce that selects row 0 of the still-unfiltered list.
The helper now waits for the list to reach the expected filtered count
(`GetListItemCount`), which is timing-independent.

## Test Strategy

- **Unit** (`QuestListFilterTests`): CountByStatusTag groups by tag with
  "Locked" including LevelLocked; non-status criteria constrain counts; counts
  are independent of the currently selected tag; other-faction quests count only
  under "Unavailable"; normalized search text survives the `with` copy.
- **E2E** (`QuestOverviewFiltersE2ETests`): empty state appears at zero results
  and reset repopulates (status "All", search cleared); chip click applies the
  status and re-click returns to "All" (combo probed); filter state persists
  across a relaunch against the same Config dir, including direct
  `user_data.db` assertions and search-text transience.
- **Not automated**: the splitter-drag save path (`DetailSplitter_DragCompleted`)
  — dragging an 8px transparent GridSplitter with the real mouse is too brittle
  for UIA; the restore path runs at every launch and the property's clamp logic
  is trivial. Verified manually.

## Verification

- `dotnet build TarkovHelper.sln` — clean, 0 warnings.
- `dotnet test --filter "Category!=E2E"` — 164 passed.
- `dotnet test --filter "Category=E2E"` — 22 passed (10 quest, 12 header/bounds/map).
- Manual: chips filter and toggle; empty state + reset; restart restores
  filters, panel width, and expander state; KO/JA empty-state strings.

## Risks & Migration

- New `questList.*` keys in the `UserSettings` table; absent keys mean defaults,
  so no migration is needed and rollback merely orphans the keys (harmless).
- The `GetListItemCount` harness helper counts realized ListItem peers only —
  exact for the small counts (0/1) the tests assert, not for virtualized
  hundreds; documented on the helper.

## Appended: deep-review findings (2026-08-04)

Appended after a full-diff deep review of the commit above. Corrections to
already-written claims are recorded here rather than edited in place.

**Correction to "Persistence is gated on `_isDataLoaded`".** The mechanism named in
Technical Decisions is wrong, though the gate itself is right and stays. `ProfileService`'s
startup auto-switch cannot reach this page: `ProfileService.InitializeAsync()` is awaited
in `MainWindow` (`MainWindow.xaml.cs:179`) and `QuestListPage` is only constructed later
(`MainWindow.xaml.cs:501`), so nothing is subscribed when that raise happens. The
reachable raisers of `PlayerFactionChanged` during the `QuestListPage_Loaded` await window
are `ProfileService.SetActiveGameMode` — from the raid-log auto-detect
(`ProfileService.OnRaidEvent`) or the PVP/PVE buttons — and any other write to
`SettingsService.PlayerFaction`. Anyone re-deriving the race from the original wording
would conclude it is impossible and delete the gate.

**Repopulation no longer drops (or persists over) the trader/map selection.**
`PopulateTraderFilter`/`PopulateMapFilter` rebuild their items by removing the selected
one, which reset the combo to `SelectedIndex = -1` and raised `SelectionChanged`; with
persistence added, the next `ApplyFilters` wrote the widened `""` to `questList.trader` /
`questList.map`. A DB auto-update or `ReloadDataAsync` therefore destroyed a *still-valid*
saved filter — which the PRD's accepted risk does not cover, that one being about a value
that genuinely no longer exists. Both methods now capture the selected tag, rebuild inside
a `SuppressFilterHandlers()` scope, and re-select it, falling back to "All" only when the
value really is gone.

**`SelectComboByTag` takes an explicit fallback tag.** Its index-0 fallback was the
permissive entry for trader/map but *not* for status, whose index 0 is "Active" ("All" is
index 1) — an unknown persisted tag silently applied the narrowest filter and then
persisted it. `ResetFilters` also selects by tag now instead of by index.

**Settings load is retryable, and saves are one transaction.** `LoadSettings` set
`_settingsLoaded` before its `try`, so one failed read latched defaults for the session and
the next save wrote them over the stored values; the flag is now set only after a
successful read, and the setters `EnsureLoaded()` before their change check (an unloaded
cache is null, and `null != value` is always true). `SaveFilterSnapshot` replaces five
per-key writes with the batched `UserDataDbService.SetSettings` transaction that
`MapSettings.SaveLastView` already uses.

**Other corrections.** The debounced search is now *flushed* rather than cancelled on
`Unloaded` and before `SelectQuestInternal`'s visibility probe (a cancelled tick left the
list disagreeing with the search box permanently, because `Loaded` early-returns once
`_isDataLoaded`). The empty state requires `_allQuestViewModels.Count > 0`, so a load
failure no longer blames the user's filters. `CountByStatusTag` evaluates the
status-independent criteria once per quest instead of once per quest per tag.
`ApplyFilters` no longer calls `GetStatistics()` (a full status re-derivation) for a total
it already has. The detail column publishes the settings clamp as its own
`Min/MaxWidth` and is capped to the current window (`QuestListLayout.ClampDetailPanelWidth`)
so a width saved on a wide monitor cannot push the splitter off a narrow one.

**E2E status at review time, and a harness guard.** 17 of 24 e2e tests pass; the 7
failures are the whole of `QuestNavigationE2ETests` — the only file that drives a *real
mouse* (`ClickElement`/`CtrlClickElement`/`ClickTextElementWithScroll`). Every
UIA-pattern test passes, including the five in `QuestOverviewFiltersE2ETests`. The same
two failures reproduce on the pre-review commit `d7e0c5e` with none of these changes
present, so the cause is environmental: on a shared desktop `SetForegroundWindow` fails
silently when another process owns the foreground lock, and the click then lands on
whatever is actually on top. `ClickElement`/`CtrlClickElement` now confirm the window
really became foreground and refuse to click otherwise — a blind click can otherwise
reach another copy of this app and modify real quest progress. Re-run the file on a
desktop with no competing foreground window to confirm the suite green.

**Splitter width is now unit-tested after all.** The "Not automated" note above stands for
the *drag gesture*, but the save/restore rule it guarded is no longer untested: the width
decision moved to the pure `QuestListLayout.ClampDetailPanelWidth`, covered by
`QuestListLayoutTests`.

### Risk accepted, not fixed: persistence still rides the render pass

`SaveFilterSettings` is still called from `ApplyFilters`, so any event-driven apply
(progress change, language change, DB refresh, `RefreshDisplay` from MainWindow) rewrites
the filter snapshot even though the user changed nothing. With the two clobbering paths
above closed, what remains is write frequency and the design smell — a save means "the
list rendered", not "the user chose a filter". Moving the save to the user-intent handlers
(`Filter_Changed`, the `Cmb*_SelectionChanged` trio, the debounce tick, and the two reset
paths) would retire the `_isDataLoaded` gate entirely and make "the store mirrors the
user's choice" true by construction. It is deliberately NOT done here: this spec's
Non-Goals rule out re-architecting `ApplyFilters`, so the call is the author's. Reviewers
of a future change in this area should treat the gate as load-bearing until then.
