# Code Health Assessment, August 2026

> Snapshot document. Analyzed at commit `d1734b4` (2026-08-08); frozen once merged.
> All counts and line numbers refer to that commit. The state of each finding
> (open, fixed, rejected) lives in GitHub PRs and issues, not here: a PR that
> addresses a finding names its ID (for example `THR-1`) in the PR body.

## Scope and method

This assessment reviews the TarkovHelper WPF application and solution-level
tooling from the perspective of standard C# desktop engineering practice. It was
produced by a full static read of the source (`Services/`, `Pages/`, `Windows/`,
startup code, the test project, CI workflows, and project files), plus one clean
`dotnet build TarkovHelper.sln -c Release -t:Rebuild` to verify the warning count.

Out of scope: TarkovDBEditor internals (its services are only mentioned where
solution-level findings touch them), runtime profiling or performance
measurement, a security audit, and any comparison against the upstream fork.

## How to read the findings

Each finding follows one template: **Principle** (the accepted rule or best
practice the finding rests on, with its source, so the norm can be verified
independently of this document; full references are collected in the Sources
section at the end), **Problem** (how this codebase violates it, written for a
reader who has never seen the code), **Evidence** (symbol-based code
references; line numbers are an aid, valid at the analyzed commit), **Failure
scenario** (the concrete conditions under which it bites), **Recommendation**
(direction and rationale, deliberately stopping short of detailed design, which
belongs to a spec at implementation time), and **Alternatives considered**
(omitted on small hygiene findings).

Severity describes impact if left unaddressed. Effort is a separate axis on
purpose: conflating them turns "easy to fix" into "important", which is how
hard important problems get ignored.

| Severity | Meaning |
|---|---|
| Critical | Can lose or corrupt user data, or crash/hang the app, under realistic conditions |
| High | Incorrect behavior, resource leaks, or UI stalls under realistic conditions; or a missing guard that lets defects land silently |
| Medium | Raises the cost and risk of future change; no immediate user-visible defect |
| Low | Hygiene and consistency |

| Effort | Meaning |
|---|---|
| S | Hours; a focused PR |
| M | Days; a small series of PRs |
| L | An ongoing direction executed incrementally |

Finding IDs are permanent addresses and are never renumbered. Areas: THR
(threading and async), ARC (architecture), RES (resource lifetime), STA
(startup and crash handling), DATA (data layer), TEST (testing), UI (UI layer),
TOOL (tooling and repository hygiene).

## What is already good

Criticism is only credible when the baseline is stated honestly. The following
are above average for a WPF codebase of this size (about 24,000 lines in
`Services/`, about 14,500 lines of code-behind):

- **Zero compiler warnings** at warning level 8 with `Nullable` enabled, across
  roughly 200 source files, with only two narrow, commented `#pragma` suppressions.
- **No sync-over-async in the UI layer**: no `.Result`, `.Wait()`, or
  `Thread.Sleep` anywhere under `Pages/` or `Windows/`.
- **Disciplined event lifecycle on the main pages**: constructor subscribe,
  `Unloaded` unsubscribe, `Loaded` re-subscribe with an `_isUnloaded` latch,
  with balanced counts on all four tab pages.
- **Clean SQLite resource handling**: scoped `await using` connections
  everywhere, no long-lived connection fields, and
  `SqliteConnection.ClearAllPools()` before swapping the database file.
- **Services marshal to the UI correctly**: all service-side dispatches use
  non-blocking `BeginInvoke` behind an `Application.Current?.Dispatcher` null
  guard, which also keeps services usable outside a WPF host.
- **List virtualization** is explicitly enabled with recycling on all four main
  list views.
- **A real shared theme**: one design system in `App.xaml` (palette, type scale,
  full control retemplates) with the font chain single-sourced from code.
- **A genuine e2e harness**: launches the real app with config isolation via
  `TARKOVHELPER_CONFIG_PATH`, drives it through UI Automation, and handles DPI
  pitfalls; 30 e2e cases exist.
- **A careful release pipeline**: tag-to-csproj version guard with CalVer
  string equality, and `update.xml` bumped last so clients never see a 404.
- **The decision-docs process itself**: append-only, born-final documents with
  the rationale for the format recorded in the format's own README.

## Findings index

| ID | Title | Severity | Effort |
|---|---|---|---|
| THR-1 | Progress services mutate unlocked shared state from two threads | Critical | M |
| THR-2 | Services raise events while holding locks; one subscriber blocks on the dispatcher | High | M |
| THR-3 | Synchronous DB accessors block on async init; deadlock is latent, not absent | High | M |
| THR-4 | Fourteen call sites block the UI thread on database writes | Medium | M |
| THR-5 | `Dispatcher.Invoke` with async lambdas silently detaches the continuation | High | S |
| THR-6 | User-data writes are fired and forgotten with no failure observer | High | M |
| THR-7 | Three timer mechanisms coexist; `System.Timers.Timer` swallows exceptions | Medium | S |
| THR-8 | Cross-thread guard flags are plain non-volatile bools | Medium | S |
| ARC-1 | Thirty-seven ambient singletons, no dependency injection, no test seam | High | L |
| ARC-2 | Service initialization order is implicit and self-compensating | High | M |
| ARC-3 | A third of the service layer lives in seven god classes | Medium | L |
| RES-1 | Shutdown disposes 4 of 11 disposable services; two timer owners are not disposable at all | Medium | S |
| RES-2 | Data-refresh paths recreate pages that then leak if never displayed | High | S |
| RES-3 | MapPage and app-lifetime lambdas hold unremovable subscriptions | Low | S |
| STA-1 | Migration and MainWindow construction run before crash handlers exist | High | S |
| STA-2 | Crash logging overwrites its own file and bypasses the app logger | Medium | S |
| STA-3 | Startup cleanup deletes Data/ files synchronously with swallowed errors | Medium | S |
| DATA-1 | user_data.db has no schema versioning; columns can never be added | High | M |
| DATA-2 | The profile-schema migration catch filter misroutes both failure classes | High | S |
| DATA-3 | Forty-two hand-built connections, no WAL, no busy timeout | High | S |
| DATA-4 | The largest data-access service has zero production logging | High | S |
| DATA-5 | Multi-row writes run without transactions; row mapping is ordinal | Medium | M |
| DATA-6 | Thirty-five empty catch blocks; DB errors are logged without stack traces | Medium | M |
| TEST-1 | 299 tests, none covering the data-access layer or the destructive migration | High | M |
| UI-1 | Localization is applied by ~250 manual control assignments per language change | High | L |
| UI-2 | `QuestObjectiveViewModel` exists twice; both copies drive the same screen | Medium | S |
| UI-3 | The map quest drawer realizes hundreds of items with no virtualization | Medium | S |
| UI-4 | Three of four search boxes refilter the full dataset on every keystroke | Medium | S |
| UI-5 | No page-level MVVM: all UI state is pushed imperatively into named controls | Medium | L |
| UI-6 | One 685-line App.xaml; styles duplicated verbatim between two pages | Low | S |
| TOOL-1 | The build is at zero warnings and nothing keeps it there | High | S |
| TOOL-2 | Build artifacts and scratch scripts are tracked in git | Medium | S |
| TOOL-3 | CI runs tests but no format check and no coverage collection | Low | S |
| TOOL-4 | CLAUDE.md documents patterns the code no longer uses | Low | S |

---

## Threading and async (THR)

#### THR-1: Progress services mutate unlocked shared state from two threads

Severity: Critical | Effort: M

**Principle.** .NET collection types are documented as not thread-safe for
concurrent mutation; shared mutable state must be confined to one thread, made
immutable, or synchronized. Reference assignment is atomic in .NET, which is
what makes publishing immutable snapshots by reference swap a sound lock-free
pattern. (Microsoft Learn, Managed Threading Best Practices; ECMA-335 memory
model)

**Problem.** `QuestProgressService` (1,677 lines, the largest service) holds
seven mutable collections: `_questProgress`, `_objectiveProgress`, three task
indexes, `_allTasks`, and `_progressDataV2` (`QuestProgressService.cs:26-35`).
The file contains no `lock` statement at all. A `Dictionary<K,V>` mutated
concurrently with enumeration throws `InvalidOperationException` at best and
corrupts its internal buckets at worst; .NET provides no protection unless the
code does. Both the UI thread and a threadpool thread touch these collections.

**Evidence.**
- The constructor subscribes with a fire-and-forget reload:
  `ProfileService.Instance.ActiveProfileChanged += (_, _) => _ = ReloadForProfileAsync();`
  (`QuestProgressService.cs:16`).
- `ActiveProfileChanged` is raised from `ProfileService.OnRaidEvent`, which runs
  on the log-poll threadpool thread of `EftRaidEventService` (a 1-second
  `System.Timers.Timer`, `EftRaidEventService.cs:584-587`).
- The reload clears and refills `_questProgress` while the UI thread reads the
  same dictionary through the eligibility filters
  (`QuestProgressService.cs:349-463`) during page refresh.
- `HideoutProgressService` has the same shape: `_progress.Modules` is replaced
  from the same background path (`HideoutProgressService.cs:311-316`) and read
  from the UI without synchronization.

**Failure scenario.** The player switches between PVP and PVE mid-session (the
app detects this automatically from game logs) while a quest or hideout list is
refreshing. The background reload mutates the dictionary the UI is enumerating.
The result is a crash dialog, or silently wrong quest state that the next save
persists to user_data.db.

**Recommendation.** Build-then-swap immutable snapshots: the reload constructs
new collections off-thread and publishes them with a single reference
assignment; readers capture a local reference before use. This removes the race
without adding a lock to roughly fifty read sites and keeps the service free of
WPF dispatcher affinity.

**Alternatives considered.**
- Lock every access: correct but invasive, and every future read site must
  remember the lock. This codebase already demonstrates that per-call-site
  discipline erodes (see THR-2).
- `ConcurrentDictionary`: protects individual operations but not consistency
  across the seven collections, which must agree within one refresh.
- Marshal reloads to the UI thread: fixes the race but couples a data service
  to WPF and puts DB reads on the dispatcher.

#### THR-2: Services raise events while holding locks; one subscriber blocks on the dispatcher

Severity: High | Effort: M

**Principle.** Never call code you do not control while holding a lock. Event
handlers are arbitrary code, so raising an event inside a lock hands the
lock's scope to every present and future subscriber; calling out under a lock
is a classic deadlock cause in the deadlock-avoidance literature. (Microsoft
Learn, Managed Threading Best Practices)

**Problem.** Raising an event while holding a lock hands the lock's scope to
arbitrary subscriber code. If any subscriber does a blocking hop to the UI
thread while the UI thread is waiting for that same lock, the app deadlocks.
This codebase has already been bitten once, fixed the subscriber, and left the
pattern in place at the source, where it has since reappeared in a second
service.

**Evidence.**
- The known case: `LogSyncService.StartMonitoring` takes `_watcherLock`
  (`LogSyncService.cs:204`) and raises `MonitoringStatusChanged` inside it
  (`:246`, `:251`); the workaround is a comment plus `InvokeAsync` discipline in
  the subscriber (`MainWindow.xaml.cs:64-71`).
- The recurrence: `EftRaidEventService.ProcessApplicationLogChanges` takes
  `_readLock` (`EftRaidEventService.cs:622`) and raises `RaidEvent` and
  `ProfileChanged` from at least 14 sites inside it. A blocking subscriber
  exists: `MapPage.OnRaidEvent` wraps its body in `Dispatcher.Invoke`
  (`MapPage.xaml.cs:909-911`).
- The raise under `_readLock` also fans out synchronously into SQLite reads:
  `ProfileService.OnRaidEvent` → `SettingsService.OnActiveProfileChanged` →
  `UserDataDbService.GetProfileSetting`, all while the log-poll thread holds
  the lock.

**Failure scenario.** A raid event arrives on the poll thread while the UI
thread is inside `StartMonitoring`/`StopMonitoring` or otherwise contending for
the same lock. At minimum the 1-second poll thread stalls behind a blocked
dispatcher round-trip; in the worst case the same lock cycle that produced the
documented `LogSyncService` deadlock recurs. Every new subscriber written with
`Dispatcher.Invoke` re-arms the trap.

**Recommendation.** Fix at the source, not per subscriber: collect pending
event payloads inside the lock, release it, then raise. This is a mechanical
transform (snapshot the handler list or queue the raise) and removes the entire
class of failure regardless of how subscribers are written.

**Alternatives considered.**
- Keep the per-subscriber `InvokeAsync` rule: already failed once; it is
  documentation, not enforcement.
- Async event dispatch through a queue or channel: heavier redesign; worth it
  only if event ordering requirements grow.

#### THR-3: Synchronous DB accessors block on async init; deadlock is latent, not absent

Severity: High | Effort: M

**Principle.** Async all the way down: blocking on async code from a
context-bound thread (sync-over-async) is the canonical WPF/UI deadlock, and
service-layer code should use `ConfigureAwait(false)` so its correctness never
depends on the caller's synchronization context. (S. Cleary, Async/Await Best
Practices; D. Fowler, Async Guidance)

**Problem.** Five synchronous settings accessors in `UserDataDbService`
(`GetSetting`, `SetSetting`, `SetSettings`, and two profile-setting readers,
around `UserDataDbService.cs:1056-1243`) call
`InitializeAsync().GetAwaiter().GetResult()` directly on the UI thread.
`InitializeAsync` contains real awaits (`OpenAsync`, table creation). With a
live `DispatcherSynchronizationContext` and a blocked UI thread, the
continuation cannot be posted back: the textbook WPF deadlock. It does not hang
today only because `Microsoft.Data.Sqlite`'s async methods complete
synchronously, which is a provider implementation detail, not a contract.
Additionally, the repository contains zero uses of `ConfigureAwait(false)`, so
no service is defensive against this by construction.

**Evidence.**
- Reached during startup from the UI thread: `App.OnStartup`
  (`App.xaml.cs:46`) touches `SettingsService.Instance`, whose constructor
  (`SettingsService.cs:86`) calls `LoadSettings` → `GetSetting`.
- `_isInitialized` is a plain bool guarded by an unsynchronized check
  (`UserDataDbService.cs:19`, `:60-61`); concurrent first calls can both run
  `CreateTablesAsync` (see THR-8).

**Failure scenario.** A future `Microsoft.Data.Sqlite` release implements true
async I/O, or any real await is added to the init path. The app then deadlocks
on startup for every user, and nothing in the code points at why.

**Recommendation.** This defect is already recorded with a design in
`fix-userdata-init-deadlock.md`; this finding exists so the assessment is
complete on its own. Execute that decision: make initialization explicitly
awaited once at startup, and make the synchronous accessors either fail fast
when uninitialized or become async along their call chains. Adopt
`ConfigureAwait(false)` throughout `Services/` as part of the same effort.

#### THR-4: Fourteen call sites block the UI thread on database writes

Severity: Medium | Effort: M

**Principle.** The UI thread must never wait on I/O: latency beyond tens of
milliseconds is a perceptible freeze, and disk latency is unbounded from the
application's point of view (antivirus, cold media, cloud sync). I/O belongs
on async paths end to end. (Microsoft responsiveness guidance for desktop UI)

**Problem.** Fourteen sites use `Task.Run(async ...).GetAwaiter().GetResult()`
to run async DB work from synchronous code. The `Task.Run` wrapper escapes the
UI context, so it does not deadlock, but every call is a hard UI-thread block
on disk I/O.

**Evidence.**
- `HideoutProgressService.SaveSingleModule` (`HideoutProgressService.cs:114-127`)
  is called from `SetLevel`: the UI freezes on a SQLite write on every hideout
  level click. Comments acknowledge the pattern
  (`HideoutProgressService.cs:317`).
- Remaining sites: `HideoutProgressService.cs:310/319/344`,
  `ItemInventoryService.cs:85/271/298`,
  `QuestProgressService.cs:1155/1443/1527/1566/1634`, `LogSyncService.cs:1054`.

**Failure scenario.** Any slow disk moment (antivirus scan, cold HDD, cloud
sync touching the folder) turns a routine click into a visible UI freeze. With
SQLITE_BUSY contention (see DATA-3) the block can extend to the full busy wait.

**Recommendation.** Make the call chains async (`SetLevel` →
`SetLevelAsync`, awaited from event handlers). Where a synchronous signature is
genuinely forced, enqueue the write to a background worker instead of blocking
(the debounced save queue in `ItemInventoryService` is a local precedent).

#### THR-5: `Dispatcher.Invoke` with async lambdas silently detaches the continuation

Severity: High | Effort: S

**Principle.** An async lambda passed to an `Action` parameter compiles to
`async void`: the caller can neither await it, order against it, nor observe
its exceptions. Avoid `async void` outside event handlers, and pass async
lambdas only to Task-returning overloads. (S. Cleary, Async/Await Best
Practices)

**Problem.** `Dispatcher.Invoke(async () => ...)` binds to the `Action`
overload, so `Invoke` returns at the first `await`. Everything after that await
is fire-and-forget: completion is not observed, ordering guarantees the caller
assumes are void, and exceptions vanish into an orphaned task (and the app has
no `TaskScheduler.UnobservedTaskException` handler, see THR-6).

**Evidence.**
- At least eight sites: `ItemsPage.xaml.cs:330/421/433/446/458/470`,
  `CollectorPage.xaml.cs:163/174`. Example, the language-change handler
  (`ItemsPage.xaml.cs:330`): `await LoadItemsAsync()` and the four statements
  after it run detached; a failure there is silent.
- The correct pattern already exists in the same codebase:
  `await Dispatcher.InvokeAsync(async () => ...)` in
  `CollectorPage.OnDatabaseRefreshed` (`CollectorPage.xaml.cs:72-88`).

**Failure scenario.** A DB reload fails during a language switch. The page
shows stale or empty data, no log entry is produced, and the sequencing the
handler relies on (load, then filter, then update details) is not actually
guaranteed.

**Recommendation.** Replace all sites with `await Dispatcher.InvokeAsync(...)`
(making the enclosing handler async), matching the CollectorPage precedent.
This is a mechanical, low-risk sweep.

#### THR-6: User-data writes are fired and forgotten with no failure observer

Severity: High | Effort: M

**Principle.** Every task must end in an observer: awaited, continued, or
deliberately logged. A fire-and-forget write is a write the program cannot
know happened; `TaskScheduler.UnobservedTaskException` is the last-resort
safety net, not the mechanism. (Microsoft Learn, TPL exception handling;
D. Fowler, Async Guidance)

**Problem.** The codebase has 37 `_ = SomeAsync()` fire-and-forget sites, and
the ones that persist user data have no error handling anywhere in the chain.
There is also no `TaskScheduler.UnobservedTaskException` handler
(`App.xaml.cs` installs only `AppDomain.UnhandledException` and
`DispatcherUnhandledException`), so a faulted forgotten task is invisible even
in logs. Separately, several `async void` methods are not event handlers, so an
exception escaping them crashes the process outright.

**Evidence.**
- Progress writes: `_ = SaveProgressBatchAsync(...)` at
  `QuestProgressService.cs:663/998/1312/1349`.
- Settings writes: five `_ = SaveSettingsAsync()` in `OverlayMiniMapService`
  (`:160/168/263/272/320`); `_ = UserDataDbService.Instance.SetSettingAsync(...)`
  persisting the active game mode (`ProfileService.cs:54`).
- Constructor-lambda reloads with no catch around the event raise:
  `QuestProgressService.cs:16`, `HideoutProgressService.cs:20`,
  `ItemInventoryService.cs:33`; a subscriber exception escapes into an
  unobserved task.
- Non-handler `async void`: `HideoutPage.UpdateDetailPanel`
  (`HideoutPage.xaml.cs:400`), `InProgressQuestInputDialog.LoadTraders`
  (`:93`), `MainWindow.PerformQuestSync` (`:1456`),
  `MainWindow.ShowSyncResultDialog` (`:1636`).

**Failure scenario.** A quest progress save fails (locked DB, see DATA-3; disk
full; the silent post-migration state of DATA-2). The user keeps playing,
progress is not recorded, nothing is logged, and the loss is discovered much
later. This is the data-loss shape without a crash.

**Recommendation.** Add a `TaskScheduler.UnobservedTaskException` handler that
logs (one line of insurance). Then route fire-and-forget persistence through a
small helper (`FireAndLog(Task, ILogger)`) so every forgotten task has an
observer, and convert the four non-handler `async void` methods to `async Task`
awaited by their callers.

#### THR-7: Three timer mechanisms coexist; `System.Timers.Timer` swallows exceptions

Severity: Medium | Effort: S

**Principle.** Every background entry point (timer tick, watcher callback,
threadpool item) needs exactly one deliberate exception boundary, because the
framework defaults are extremes: `System.Timers.Timer` is documented to catch
and suppress all exceptions from `Elapsed` handlers, while an `async void`
callback turns the same exception into process death. (Microsoft Learn,
System.Timers.Timer remarks)

**Problem.** Services use `System.Threading.Timer`
(`DatabaseUpdateService.cs:29`), `System.Timers.Timer`
(`EftRaidEventService.cs:181`, `UpdateService.cs:23`,
`ItemInventoryService.cs:22`), and pages use `DispatcherTimer`. The UI/service
split is defensible, but `System.Timers.Timer` silently swallows any exception
thrown in `Elapsed`, and `async void` timer callbacks invert that into a
process crash. Correctness currently depends on each callback happening to
contain its own catch-all.

**Evidence.**
- `ItemInventoryService.cs:39-42`: the 500 ms save-debounce `Elapsed` handler
  calls `SavePendingItems()` with no try/catch; a throw silently discards the
  user's pending inventory write.
- `DatabaseUpdateService.OnUpdateTimerElapsed` (`:147-150`) is `async void`;
  it survives only because `CheckAndUpdateAsync` has a catch-all (`:213-219`).
  Nothing enforces that invariant.

**Failure scenario.** A transient failure inside a timer callback either
disappears without a log line (data silently not saved) or, on the `async
void` path, terminates the process on a background tick.

**Recommendation.** Standardize service timers on one mechanism (a
`PeriodicTimer` loop or `System.Threading.Timer`) wrapped in a shared
run-and-log helper so every tick has exactly one exception boundary.

#### THR-8: Cross-thread guard flags are plain non-volatile bools

Severity: Medium | Effort: S

**Principle.** Check-then-act on a shared flag is not atomic, and without
`volatile` or `Interlocked` the runtime guarantees neither atomicity nor
cross-thread visibility. Cross-thread guards belong to
`Interlocked.CompareExchange` or a semaphore, not a bool. (Microsoft Learn,
Managed Threading Best Practices; .NET memory model)

**Problem.** Re-entrancy guards written as `if (_flag) return; _flag = true;`
on plain bool fields are non-atomic check-then-act: two threads can both pass,
and without `volatile` there is no visibility guarantee.

**Evidence.**
- `DatabaseUpdateService._isUpdating` (`:30`, tested `:157`, set `:163`):
  written by the threadpool timer, raced by UI-initiated
  `ForceUpdateCheckAsync` (`:387`).
- `UpdateService._isChecking` (`:26`, `:150/154/194`): same pattern.
- `UserDataDbService._isInitialized` (`:19`, `:60`): concurrent first calls can
  both run `CreateTablesAsync`.
- The codebase knows the correct idiom: `EftRaidEventService._isWatching` is
  `volatile` with an explanatory comment (`EftRaidEventService.cs:169-171`).
  That care was applied once.

**Failure scenario.** Two update checks run concurrently and race the
download-and-swap of tarkov_data.db; or two initializers run DDL against the
same user_data.db simultaneously.

**Recommendation.** Replace with `Interlocked.CompareExchange` guards (or
`SemaphoreSlim(1,1)` where the section is async). Small, local, mechanical.

---

## Architecture (ARC)

#### ARC-1: Thirty-seven ambient singletons, no dependency injection, no test seam

Severity: High | Effort: L

**Principle.** The Explicit Dependencies Principle: a class should receive
what it needs through its constructor rather than reaching into ambient
statics; hidden dependencies are what make code untestable in isolation and
initialization order emergent. Microsoft's architecture guidance names this
the default for .NET applications. (Microsoft Learn, Architectural
principles; M. Seemann, Dependency Injection Principles, Practices, and
Patterns)

**Problem.** Every service is reached through a static `Instance` property; the
UI layer reads `.Instance` 205 times. There is no DI container anywhere (no
`Microsoft.Extensions.DependencyInjection` reference, no composition root).
The consequences are concrete rather than stylistic: 30 of the 37 singletons
initialize via non-thread-safe `_instance ??= new X()`, at least three of them
are demonstrably first-touched from background threads, and nothing in the
codebase can be instantiated alone. There are no interfaces over the DB
services and no reset hooks, so unit-testing any service or page means
materializing the entire service graph in-process, which is why the data layer
has zero tests (TEST-1).

**Evidence.**
- Init styles: 30 unguarded `??=` (`SettingsService.cs:19`,
  `QuestProgressService.cs:12`, `QuestDbService.cs:17`, and 27 more), 4
  `Lazy<T>`, 2 locked, 1 eager. Two singletons even have public constructors
  (`ImageCacheService.cs:31`, `LocalizationService.Core.cs:31`).
- Background first-touch of `??=` singletons: `LogSyncService.cs:797/1007`
  (`QuestProgressService.Instance` under `Task.Run`),
  `LoggingService.cs:117/139` (`SettingsService.Instance` from the cleanup
  worker).
- A racing double-construction is not benign: the losing instance has already
  run its constructor side effects (event subscriptions, timers) and is
  silently discarded.
- The counter-example exists in-repo: `Services/Map/` has two interfaces and
  the codebase's only constructor injection
  (`ScreenshotWatcherService(IScreenshotCoordinateParser)`,
  `ScreenshotWatcherService.cs:53`).

**Failure scenario.** Beyond the init races: every new feature inherits
untestability by default, and every test that touches a singleton pollutes
every later test in the process. The cost compounds; it does not stay constant.

**Recommendation.** Adopt `Microsoft.Extensions.Hosting` (Generic Host) with a
composition root in `Program.cs`, migrated incrementally: register services in
the container first, keep the existing `Instance` properties as facades that
resolve from the container (so no call site is forced to change), introduce
interfaces at the DB-service boundary for test seams, and require constructor
injection for new code. `Services/Map/` is the internal precedent to extend.

**Alternatives considered.**
- Keep singletons but make all initializers `Lazy<T>`: fixes only the races;
  testability and implicit init order (ARC-2) remain.
- A hand-rolled service locator: same coupling as today with extra
  indirection; containers are standard and free.

#### ARC-2: Service initialization order is implicit and self-compensating

Severity: High | Effort: M

**Principle.** Compose the object graph in one place, in one declared order
(the Composition Root pattern), and fail fast when construction goes wrong.
Initialization order that emerges from whichever code path touches a
singleton first is behavior nobody chose and nothing defends. (M. Seemann,
Dependency Injection Principles, Practices, and Patterns)

**Problem.** Startup order is an emergent property of which singleton happens
to be touched first, and the code already contains a compensation mechanism for
getting it wrong. `TarkovHelper/CLAUDE.md` documents a seven-step
"Service Initialization Order" that exists only as prose; nothing enforces it.

**Evidence.**
- One property read fans out into a construction cascade: `App.OnStartup`
  (`App.xaml.cs:46`) reads `SettingsService.Instance.BaseFontSize`, which
  constructs `SettingsService` → re-entrantly `ProfileService` (from inside
  `LoadSettings`, before the ctor's own `ProfileService.Instance` line at
  `SettingsService.cs:89`) → `EftRaidEventService` → `UserDataDbService`.
- Because `ProfileService._activeGameMode` still holds its field default at
  that moment, `SettingsService` loads PVP settings unconditionally; the real
  mode arrives later in `ProfileService.InitializeAsync`
  (`MainWindow.xaml.cs:179`), which must fire `ActiveProfileChanged` purely to
  undo the premature load, upon which `SettingsService` re-fires seven change
  events to repair the UI (`ProfileService.cs:36-38`,
  `SettingsService.cs:95-105`).
- A latent trap: `ProfileService` (Lazy) constructs `EftRaidEventService`
  (Lazy). If anyone adds a reverse reference to `EftRaidEventService`'s
  currently-empty constructor, `Lazy<T>` throws
  `InvalidOperationException` on recursive access: a hard startup crash whose
  cause is invisible at the call site.

**Failure scenario.** Any reordering of innocent-looking startup code (or a
new field initializer touching a singleton) changes construction order and
resurrects the wrong-profile-load class of bug, with no compiler or test
telling anyone.

**Recommendation.** Make initialization explicit: a single async startup
sequence (natural once ARC-1's composition root exists) that constructs and
initializes services in declared order before the main window shows, replacing
the compensating event replay. Until then, at minimum move profile resolution
ahead of settings loading so the compensation becomes dead code.

#### ARC-3: A third of the service layer lives in seven god classes

Severity: Medium | Effort: L

**Principle.** Single Responsibility: a class should have one reason to
change. The practical test is whether a change can be written, reviewed, and
tested without loading unrelated concerns into working memory; a 1,700-line
class fusing five concerns fails that test by construction. (R. C. Martin,
SOLID; M. Fowler, Refactoring)

**Problem.** The top seven service files total 9,285 lines, 38% of the layer.
Size itself is not the defect; the defect is responsibilities fused so that no
piece can be reasoned about, tested, or replaced alone.

**Evidence.**
- `QuestProgressService.cs` (1,677 lines, 52 public members): in-memory
  progress state, a task index, DB persistence, quest eligibility rules
  (reading player level, faction, edition from `SettingsService`), and
  prerequisite graph traversal that duplicates the separate
  `QuestGraphService` (559 lines).
- `UserDataDbService.cs` (1,492): four unrelated tables, settings KV storage,
  schema creation, JSON-to-SQLite migration, and parallel sync/async APIs for
  the same operations.
- `Settings/MapSettings.cs` (1,477, 67 public members): mechanical
  key/backing-field/property repetition; a generic `SettingsValue<T>` already
  exists in-repo (31 lines) and is barely used.
- `LocalizationService`: 2,649 lines across six partial files; one type with
  hundreds of hand-written three-way switch properties (see UI-1).
- `EftRaidEventService.cs` (1,170): 16 regexes, a 40-entry map table, two
  file watchers, a poll timer, and a session state machine in one type.
- The contrast: `Services/Map/` splits the same amount of domain into 15
  focused files with a median around 200 lines.

**Failure scenario.** Slow, compounding: every change lands in a file where
five concerns interleave, reviews get harder, and extracting tests gets less
likely each month.

**Recommendation.** Decompose opportunistically, not as a big bang: split along
the seams named above when a change touches the area (persistence out of
`QuestProgressService` first, since THR-1 and TEST-1 both want that seam), and
collapse `MapSettings` onto `SettingsValue<T>` as a standalone mechanical PR.

---

## Resource lifetime (RES)

#### RES-1: Shutdown disposes 4 of 11 disposable services; two timer owners are not disposable at all

Severity: Medium | Effort: S

**Principle.** Ownership implies disposal: a type that owns disposable
resources implements `IDisposable`, and its owner calls it, transitively up
to application shutdown. The Dispose pattern exists so resource lifetime is
deterministic rather than left to process teardown. (Framework Design
Guidelines, Dispose pattern)

**Problem.** `App.OnExit` (`App.xaml.cs:57-106`) disposes
`DatabaseUpdateService`, `OverlayMiniMapService`, `GlobalKeyboardHookService`,
and `LoggingService`, each in its own try/catch (good). The other seven
`IDisposable` services are never disposed, and two services that own timers do
not implement `IDisposable` at all.

**Evidence.**
- Never disposed: `EftRaidEventService` (two `FileSystemWatcher`s plus the 1 s
  poll timer), `LogSyncService` (two watchers), `MapTrackerService` (owns
  `ScreenshotWatcherService`), `LogMapWatcherService`, `ImageCacheService`.
  Their `Dispose`/`StopMonitoring` implementations are individually correct;
  they are simply never called at shutdown.
- Not disposable despite owning timers: `UpdateService` (3-minute timer plus
  `HttpClient`; `StopAutoCheck` exists but has no caller at shutdown) and
  `ItemInventoryService` (500 ms save debounce; a pending save at exit is
  silently lost).

**Failure scenario.** At shutdown, watcher callbacks race teardown (today this
degrades to dropped log lines because `LoggingService` guards on `_disposed`).
The user-visible case is `ItemInventoryService`: change an item count and close
the app within half a second, and the change is gone.

**Recommendation.** Flush the inventory debounce on exit and give both timer
owners `IDisposable`. Then dispose all disposable services in `OnExit`;
once ARC-1's container exists, container-owned disposal replaces the hand-list.

#### RES-2: Data-refresh paths recreate pages that then leak if never displayed

Severity: High | Effort: S

**Principle.** An event subscription is a strong reference from publisher to
subscriber. When the publisher outlives the subscriber, unsubscription must
be as deterministic as subscription, or the subscriber's lifetime silently
becomes the publisher's; this is the classic .NET memory leak and the reason
WPF ships weak event patterns. (Microsoft Learn, Weak event patterns)

**Problem.** Pages detach their singleton event subscriptions in `Unloaded`.
`Unloaded` only ever fires for a page that entered the visual tree. Code that
recreates page instances therefore leaks every instance that was constructed
but never displayed: its constructor subscriptions root it (and its whole
visual tree) on app-lifetime services forever.

**Evidence.**
- `MainWindow.LoadAndShowQuestListAsync` unconditionally recreates three pages
  (`MainWindow.xaml.cs:506-511`): `new HideoutPage()`, `new ItemsPage()`,
  `new CollectorPage()`.
- It is called from four sites: startup (`:333`), progress reset (`:1016`),
  post-sync (`:1657`), post-migration (`:2213`).
- `_questListPage` is explicitly guarded against exactly this (`:493-502`);
  the other three are not.
- Cost per orphan: `ItemsPage` roots 12 delegates on app-lifetime singletons,
  `CollectorPage` 5, `HideoutPage` 3, each plus the page's visual tree and
  view-model lists.

**Failure scenario.** A user who runs quest sync several times in a session
accumulates orphaned page instances that still receive and process every
`DataRefreshed`/`ProgressChanged` event: growing memory and duplicated
background work, invisible until the app has been open for hours.

**Recommendation.** Apply the existing `_questListPage` guard pattern to the
other three pages (reuse and refresh instead of recreate). Longer term, give
pages a deterministic detach method called by `MainWindow` when it drops a
reference, so correctness does not depend on WPF's `Unloaded` semantics.

#### RES-3: MapPage and app-lifetime lambdas hold unremovable subscriptions

Severity: Low | Effort: S

**Principle.** Same rule as RES-2, plus its corollary: a subscription you
cannot name (a lambda with no stored delegate reference) is a subscription
you can never remove. Subscribe and unsubscribe must stay symmetric, and
symmetry requires named handlers wherever the host outlives the subscriber.

**Problem.** A handful of subscriptions can never be removed because no
delegate reference is retained, and `MapPage` unsubscribes only a subset of
what its constructor attaches. Impact is bounded (single cached page instance,
app-lifetime hosts), which is why this is Low rather than High; it is still
the kind of asymmetry that turns into RES-2 the day the lifetime assumptions
change.

**Evidence.**
- `MapPage` subscribes nine events in its constructor
  (`MapPage.xaml.cs:135-141`) and `MapTrackerPage_Unloaded` (`:385-407`)
  removes three; the four `_trackerService` handlers and `_loc.LanguageChanged`
  are never detached (the `DataRefreshed` omission is deliberate and
  commented).
- Unremovable lambdas on app-lifetime services: `App.xaml.cs:47/51`,
  `MainWindow.xaml.cs:61` (captures `this` into `ProfileService`; the window's
  own `OnWindowClosing` cleanup at `:2482-2491` cannot remove it).

**Recommendation.** Convert the lambdas to named handlers where a host outlives
the subscriber, and make `MapPage`'s unload symmetric with its constructor. A
shared base-class helper (see UI-5) makes the symmetry structural.

---

## Startup and crash handling (STA)

#### STA-1: Migration and MainWindow construction run before crash handlers exist

Severity: High | Effort: S

**Principle.** Last-chance exception handlers are worth exactly what they
cover: they must attach before the first risky operation, because startup
crashes cluster in precisely the code that runs first (migration, config
load, window construction). Transparency about failure is a design
obligation, not a debugging convenience. (M. Nygard, Release It!)

**Problem.** The two riskiest phases of startup execute before the global
exception handlers are installed. `Program.Main` runs the data migration and
constructs `MainWindow` (whose constructor performs settings reads and twelve
singleton subscriptions, transitively forcing `UserDataDbService`
initialization and the DATA-2 schema migration) before `app.Run` ever fires
`OnStartup`, which is where `AppDomain.UnhandledException` and
`DispatcherUnhandledException` are attached.

**Evidence.**
- `Program.cs:12` `MigrationService.RunMigrationIfNeeded()`; `:17`
  `new MainWindow()`; handlers attach in `App.OnStartup`
  (`App.xaml.cs:28`, `:35`), which runs inside `app.Run(mainWindow)` (`:31`).
- `MainWindow`'s constructor (`MainWindow.xaml.cs:46-83`) restores window
  bounds from settings, which walks the `SettingsService` →
  `UserDataDbService.InitializeAsync` chain described in ARC-2/THR-3.

**Failure scenario.** A migration failure on a user's machine (the population
where migrations actually fail) produces no crash_log.txt and no dialog:
the process dies silently, and the bug report says "the app does not start".

**Recommendation.** Install both handlers at the top of `Main` (they do not
need an `Application` instance), or move migration and window construction
into `OnStartup` after the handlers attach. Small, order-only change.

#### STA-2: Crash logging overwrites its own file and bypasses the app logger

Severity: Medium | Effort: S

**Principle.** Crash artifacts are evidence: append, never overwrite; write
where writes are permitted; and a crash handler must be the most defensive
code in the application because it runs when invariants are already broken.
Diagnostics belong in the same pipeline operators actually collect.

**Problem.** The crash handlers write with `File.WriteAllText`, so each crash
destroys the previous crash log; they write to
`AppDomain.CurrentDomain.BaseDirectory`, which may not be writable for an
installed copy; the write itself is not guarded, so a failing write throws
inside the exception handler; and none of it goes through the app's own
structured logger, so crashes are absent from `Logs/`.

**Evidence.** `App.xaml.cs:28-39`: both handlers, `WriteAllText`, unguarded,
`_log` (`App.xaml.cs:14`) unused on this path. No
`TaskScheduler.UnobservedTaskException` handler exists (THR-6).

**Failure scenario.** A user reports "it crashed twice"; the file contains only
the second, less interesting crash. Or the install-dir write fails and the
handler itself throws, taking the original exception's detail with it.

**Recommendation.** Append with a timestamp (or one file per crash), write
under the existing `Logs/` root, wrap the handler body in its own try/catch,
and log through `ILogger` first with the file write as fallback.

#### STA-3: Startup cleanup deletes Data/ files synchronously with swallowed errors

Severity: Medium | Effort: S

**Principle.** Fail-safe defaults: a destructive operation should enumerate
what to destroy, not what to spare, so anything unknown survives by default.
A closed-world keep-list inverts the safe direction for every file created
after the list was written. (Saltzer and Schroeder, fail-safe defaults)

**Problem.** On any version change, `App.CheckAndRefreshDataOnVersionChange`
(`App.xaml.cs:141-211`) deletes every file in `Data/` except a hardcoded
six-entry allowlist, as synchronous file I/O on the UI thread during
`OnStartup`, with two `catch { }` blocks (`:156`, `:206`) swallowing all
failures.

**Failure scenario.** Two distinct modes: a new cache file added by a future
feature is silently deleted on the next update because the allowlist is
closed-world (delete-by-default inverts the safe direction); and a slow disk
stretches app startup by the full deletion time.

**Recommendation.** Invert to an explicit delete-list (delete known
regenerable caches, keep everything else), move it off the startup critical
path, and log what was deleted and why.

---

## Data layer (DATA)

#### DATA-1: user_data.db has no schema versioning; columns can never be added

Severity: High | Effort: M

**Principle.** Persistent schemas evolve through versioned, ordered,
idempotent migration steps; probe-and-patch evolution re-derives schema state
on every run and cannot express changes to existing tables. SQLite ships
`PRAGMA user_version` for exactly this purpose. (P. Sadalage and M. Fowler,
evolutionary database design; sqlite.org)

**Problem.** There is no `PRAGMA user_version`, no migrations table, and not a
single `ALTER TABLE ... ADD COLUMN` in `UserDataDbService`. Schema management
is one big `CREATE TABLE IF NOT EXISTS` blob run on every startup
(`UserDataDbService.cs:82-178`). `IF NOT EXISTS` no-ops on existing tables, so
this path can create tables but can never evolve one. The single structural
change that ever happened (profile scoping) had to be a bespoke
probe-and-rebuild (DATA-2).

**Failure scenario.** The first feature that needs a new column on an existing
table (say, a timestamp on `ItemInventory`) has no supported path: it either
ships another bespoke rename-recreate-copy migration (the riskiest operation
in the app, currently untested, see TEST-1) or gets designed around, warping
the schema.

**Recommendation.** Introduce integer schema versioning: read
`PRAGMA user_version`, apply an ordered list of idempotent migration steps,
write the new version, all inside one transaction per step. Twenty lines of
infrastructure; the profile migration becomes retroactively step 1. This also
creates the natural seam for the migration tests TEST-1 calls for.

**Alternatives considered.**
- EF Core migrations: brings an ORM decision along; not required to solve
  versioning, and the app's hand-written SQL is otherwise serviceable.
- Continue probe-based evolution (`pragma_table_info` checks): every probe is
  bespoke logic that must be re-proven; a version number is one comparison.

#### DATA-2: The profile-schema migration catch filter misroutes both failure classes

Severity: High | Effort: S

**Principle.** Catch only what you can meaningfully handle, and never let a
destructive operation fail invisibly: a migration's failure is exactly the
case that must be loud. Swallowing broad `Exception` is a named .NET
anti-pattern with its own analyzer rule. (Microsoft Learn, exception best
practices; analyzer CA1031)

**Problem.** `MigrateToProfileSchemaAsync` performs a destructive
rename-create-copy-drop over all four user-data tables. The transactional
inner block is correct (rollback plus rethrow). The outer guard is not:

```csharp
catch (Exception ex) when (ex is not SqliteException { SqliteErrorCode: 1 })
{
    System.Diagnostics.Debug.WriteLine($"[UserDataDbService] MigrateToProfileSchemaAsync error: {ex.Message}");
}
```

SQLITE_ERROR (code 1) is the generic "SQL logic error / no such table" code,
exactly what a schema migration produces against an unexpected legacy schema.
So the failure class this migration is most likely to produce is the one that
escapes, while every other failure (I/O error, disk full, corruption) is
swallowed to a Debug-only line, after which initialization proceeds and marks
itself successful against the old schema. The filter reads as if it was
intended the other way around (ignore the benign case, surface real failures);
as written, both branches misbehave.

**Evidence.** `UserDataDbService.cs:278-281`; inner transaction `:204-276`;
init continues and sets `_isInitialized` after the swallow (`:57-80`).

**Failure scenario.**
- Swallowed branch: the migration fails with an I/O error. Init reports
  success, but every subsequent profile-scoped query fails against the old
  schema with "no such column: ProfileId", and those callers swallow and
  return false (DATA-4/DATA-6). Net effect: the app runs, and every progress
  write silently fails from that day on.
- Propagated branch: a legacy DB missing one of the four tables throws code 1,
  which escapes through `InitializeAsync`'s rethrow into the UI-thread
  accessors, potentially before crash handlers exist (STA-1): silent process
  death on the machines migrations exist for.

**Recommendation.** Split the outcomes explicitly: treat verified
already-migrated states as success, treat every failure as fatal-and-visible
(surface a dialog, do not mark initialized), and log through the real logger.
Then cover it with tests (TEST-1): legacy fixture DBs are cheap to build with
the plumbing the test project already has.

#### DATA-3: Forty-two hand-built connections, no WAL, no busy timeout

Severity: High | Effort: S

**Principle.** Cross-cutting infrastructure decisions (journal mode, busy
timeout, foreign-key enforcement) are made once, in a factory, not re-decided
implicitly at every call site. SQLite's own documentation recommends WAL plus
a busy timeout for any database with concurrent readers and writers.
(sqlite.org, Write-Ahead Logging)

**Problem.** The connection-string pair (`$"Data Source={_databasePath};..."`
plus `new SqliteConnection(...)`) is copy-pasted 42 times (35 inside
`UserDataDbService` alone), with the read-only versus read-write choice made
per method with no helper and no compiler support. Because no pragma is ever
set (zero hits for `journal_mode`, `busy_timeout`, or `foreign_keys` in the
project), user_data.db runs on the default rollback journal with a zero busy
timeout, while two writers exist: the UI thread and the log-sync background
path.

**Failure scenario.** A quest completes via log sync at the moment the user
clicks a checkbox: two writes collide, one gets `SQLITE_BUSY` immediately (no
busy wait), and that exception surfaces on paths that either crash (THR-6) or
swallow silently (DATA-6). Foreign keys, where declared, are silently not
enforced.

**Recommendation.** Extract one connection factory (`OpenReadAsync` /
`OpenWriteAsync`) used everywhere, and set `journal_mode=WAL`,
`busy_timeout`, and `foreign_keys=ON` there once. WAL also removes most
writer-blocks-reader stalls that the UI-thread accessors currently risk.

#### DATA-4: The largest data-access service has zero production logging

Severity: High | Effort: S

**Principle.** A failure that leaves no production artifact did not happen,
as far as diagnosis is concerned: error paths must write to the log pipeline
that ships with the app. `Debug.WriteLine` is developer telemetry that
compiles to nothing users run; it is not observability.

**Problem.** The codebase has a real logging system (`Log.For<T>()`,
structured, written to `Logs/`), and `UserDataDbService`, the 1,492-line
service that owns every user-data write, does not use it once: 30+
`System.Diagnostics.Debug.WriteLine` calls, which compile to nothing visible
in a Release build. The five JSON-to-SQLite migration paths swallow
exceptions and return false the same way.

**Evidence.** `UserDataDbService.cs` throughout (`:47/73/77/202/274/280/...`);
migration paths `:794-797`, `:822-825`, `:866-869`, `:917-920`, `:954-957`.
Channel inconsistency across services: `ItemDbService`/`QuestDbService` use
`Log.For<T>()`, `HideoutDbService.cs:146-150` uses `Debug.WriteLine`.

**Failure scenario.** Any user_data.db failure in a shipped build (including
the DATA-2 aftermath) leaves no trace. A user reports lost progress; the logs
directory has nothing to say.

**Recommendation.** Mechanical sweep: `Log.For<UserDataDbService>()`, replace
every `Debug.WriteLine`, log exceptions as objects (stack included), and align
`HideoutDbService`. This is the prerequisite for diagnosing every other DATA
finding in the field.

#### DATA-5: Multi-row writes run without transactions; row mapping is ordinal

Severity: Medium | Effort: M

**Principle.** A logical write unit is one transaction: atomicity is the
database's tool for all-or-nothing, and autocommit across dependent
statements silently chooses "some". Read columns by name, not position, so
code and schema are coupled by contract rather than by coincidental ordering.
(SQLite transaction semantics; ADO.NET data-access guidance)

**Problem.** Exactly four `BeginTransaction` sites exist; every other
multi-statement write is autocommit, so a failure mid-sequence leaves partial
state (the comment at `UserDataDbService.cs:1093-1096` records that
map-view-state writes tore in exactly this way before `SetSettings` got its
transaction). Independently, readers map columns by ordinal
(`reader.GetString(0)` through `reader.GetInt64(17)` at
`UserDataDbService.cs:1414-1428`), so any edit to a SELECT silently shifts
every column after it.

**Failure scenario.** A batch save interrupted by any of the failures above
persists half its rows; a reordered SELECT ships a subtle
wrong-column-in-wrong-field bug that no compiler and (today) no test catches.

**Recommendation.** Wrap every multi-row write in a transaction (prepare the
command once and rebind per row while there). Replace ordinal access with
name-based access (`reader.GetOrdinal` cached once per query, or a small
mapper; a micro-ORM like Dapper is the conventional answer if appetite
exists, but is not required).

#### DATA-6: Thirty-five empty catch blocks; DB errors are logged without stack traces

Severity: Medium | Effort: M

**Principle.** An exception carries a type, a message, and a stack; logging
only the message discards two-thirds of the evidence. Empty catch blocks are
the canonical error-handling smell because they convert failures into
unknowns. (Microsoft Learn, exception best practices; analyzers CA1031,
CA2200)

**Problem.** 35 of the 208 catch blocks in TarkovHelper are empty or
comment-only. Most carry an intent comment and guard genuinely ignorable
operations; six are fully bare, including one inside the database
download-and-replace path. The DB services' catch-all handlers log
`ex.Message` only, discarding the stack trace and exception type, and return
false to callers that often do not check.

**Evidence.** Bare with no comment: `DatabaseUpdateService.cs:341` (inside
DB download/replace), `LogSyncService.cs:30`, `ImageCacheService.cs:145`,
`GlobalKeyboardHookService.cs:77`, `WikiMarkupHelper.cs:165`,
`CollectorPage.xaml.cs:818/838`. Message-only logging:
`ItemDbService.cs:159-163`, `QuestDbService.cs:145-149`,
`TraderDbService.cs:131-135`. Only four `throw;` statements exist in all of
`Services/`.

**Failure scenario.** A field failure in the DB update path produces either
nothing (bare catch) or a single message line with no stack, turning a
five-minute diagnosis into archaeology.

**Recommendation.** Pass the exception object to the logger everywhere (the
`ILogger` API already accepts it); give the six bare catches either a logged
handler or an intent comment naming what is safely ignorable; treat a
swallowed exception without a comment as a review-blocking smell going
forward (enforceable via analyzer, see TOOL-1).

---

## Testing (TEST)

#### TEST-1: 299 tests, none covering the data-access layer or the destructive migration

Severity: High | Effort: M

**Principle.** Test effort should be proportional to risk: the code whose
failure costs the most (destructive writes over user data) claims coverage
first. Legacy code becomes testable by finding seams, not by rewriting it
first. (M. Feathers, Working Effectively with Legacy Code)

**Problem.** The test suite is real (299 cases; xunit; a strong e2e harness)
but its coverage is inverted relative to risk. Fonts and theming have ~45
cases and pure logic cores ~90, while the entire data-access layer has zero:
not one test opens a user_data.db, writes progress, and reads it back, and
`MigrateToProfileSchemaAsync`, the only code that destructively rewrites all
four user-data tables (and whose guard is wrong today, DATA-2), has never been
executed by a test. More than twenty services have no test references at all,
including `UserDataDbService`, `LogSyncService` (48 KB of log parsing), and
`EftRaidEventService` (45 KB).

**Evidence.** Enumerated via `dotnet test --list-tests` plus grep of the test
project for every service name. The enabling plumbing already exists:
`TarkovHelper.Tests.csproj` references `Microsoft.Data.Sqlite` (`:17`) and
copies the asset DB to test output (`:29-33`); `InternalsVisibleTo` is already
granted (`TarkovHelper.csproj:77`).

**Failure scenario.** The next schema change is written like the last one:
against user data, destructively, verified only by whoever happens to run it
first on a legacy database.

**Recommendation.** Priority order, exploiting the existing plumbing:
(1) round-trip tests for each `UserDataDbService` table against a temp-file
DB; (2) `MigrateToProfileSchemaAsync` tests over legacy fixture schemas,
including the missing-table and mid-failure cases; (3) parser tests for
`EftRaidEventService`/`LogSyncService` over captured log fixtures. Interfaces
from ARC-1 make the rest of the services reachable, but these three need no
refactoring to start today.

---

## UI layer (UI)

#### UI-1: Localization is applied by ~250 manual control assignments per language change

Severity: High | Effort: L

**Principle.** UI state needs a single source of truth with an automatic
change-propagation mechanism; WPF's answer is data binding over
`INotifyPropertyChanged`. Manual fan-out assignment re-implements binding by
hand, at per-control cost forever, with omissions detected only by users.
(Microsoft Learn, WPF data binding overview)

**Problem.** `LocalizationService` exposes every string as a computed property
(a hand-written three-way switch), and no XAML binds to any of them: zero
`DataContext` assignments exist in the app. Instead, every screen has a
hand-written `Update*LocalizedText()` method that reassigns each control on a
`LanguageChanged` event, ~250 assignments across seven methods. The service
implements `INotifyPropertyChanged` (`LocalizationService.Core.cs:21/51`) that
nothing consumes. The failure mode is structural: any new control that misses
its manual line stays in the startup language forever, and nothing detects it.

**Evidence.**
- `MainWindow.UpdateAllLocalizedText` (`MainWindow.xaml.cs:113`, ~30
  assignments; `_loc.` appears 108 times in the file);
  `MapPage.UpdateLocalizedText` (`MapPage.xaml.cs:470-520`, ~40 assignments).
- Index-coupled updates: `((ComboBoxItem)CmbSource.Items[0]).Content = ...`
  guarded by an item-count check that silently no-ops on mismatch
  (`ItemsPage.xaml.cs:346-355`).
- Hardcoded Korean placeholder text ships in XAML and is only corrected at
  runtime (`MapPage.xaml:207/217`, 13 attributes in that file).
- Heavyweight side effect: `ItemsPage` reloads its entire dataset from the DB
  on every language change (`ItemsPage.xaml.cs:328-336`).

**Failure scenario.** Routine, not hypothetical: every UI addition risks a
partially-translated screen, discovered only by a user running Korean or
Japanese; and language switching does DB work that has nothing to do with
language.

**Recommendation.** Bind instead of assign: expose the existing singleton via
`{Binding Source={x:Static ...Instance}, Path=...}` (the INPC plumbing already
exists and `LanguageChanged` can raise a wholesale
`PropertyChanged(string.Empty)`), or move strings to `DynamicResource`
dictionaries swapped per language. Either path deletes the seven update
methods and makes "new control, no translation line" impossible rather than
undetected. Migrate screen by screen; do not attempt a big bang.

**Alternatives considered.**
- RESX resources with `x:Static`: standard, but static references cannot
  change language at runtime without extra machinery, which is this app's
  core requirement.
- Keep manual updates but add a checklist: process where a mechanism is
  available.

#### UI-2: `QuestObjectiveViewModel` exists twice; both copies drive the same screen

Severity: Medium | Effort: S

**Principle.** DRY is about knowledge, not text: every piece of behavior must
have a single authoritative representation. Two live copies of the same class
are guaranteed to diverge, and the divergence ships. (A. Hunt and D. Thomas,
The Pragmatic Programmer)

**Problem.** Two near-identical ~230-line copies of the same display class
coexist in different namespaces, and both are live: `MapPage` uses the copy
declared at the bottom of its own code-behind, while
`MapQuestMarkerManager` imports the standalone one. Any behavioral edit to one
silently misses the other half of the same screen.

**Evidence.** `MapPage.xaml.cs:3513` (namespace `TarkovHelper.Pages.Map`) and
`Pages/Map/ViewModels/QuestObjectiveViewModel.cs:13` (namespace
`...Pages.Map.ViewModels`); `MapQuestMarkerManager.cs:9` imports the latter;
`MapPage.xaml.cs` does not.

**Recommendation.** Diff the two, merge into the `ViewModels/` copy, delete
the in-code-behind one, and add the missing using. One sitting.

#### UI-3: The map quest drawer realizes hundreds of items with no virtualization

Severity: Medium | Effort: S

**Principle.** Controls bound to large collections must virtualize:
`ItemsControl` does not by default, and wrapping one in a `ScrollViewer`
defeats virtualization entirely by measuring children at unbounded height.
This is stated directly in WPF's performance documentation. (Microsoft Learn,
Optimizing WPF performance: controls)

**Problem.** `ItemsControl` does not virtualize by default, and wrapping one
in a `ScrollViewer` guarantees full realization of every container. The map
page's quest-objectives drawer does exactly that and is fed all objectives
across all quests when "this map only" is unchecked.

**Evidence.** `MapPage.xaml:225-226` (`ScrollViewer` wrapping
`QuestObjectivesList`), populated at `MapPage.xaml.cs:3178/3189`; same shape
at `InProgressQuestInputDialog.xaml:86` with the full filtered quest list.
The four main list pages get this right (virtualizing, recycling), so the fix
has in-repo precedent.

**Failure scenario.** Opening the drawer with the filter off realizes hundreds
of Border/Grid/TextBlock trees up front: a visible multi-second hitch on
weaker machines, growing with quest count every game patch.

**Recommendation.** Switch both to a `ListBox` styled chromeless (or set a
`VirtualizingStackPanel` items panel with `CanContentScroll=True`), matching
the settings already used on the main pages.

#### UI-4: Three of four search boxes refilter the full dataset on every keystroke

Severity: Medium | Effort: S

**Principle.** Work should be proportional to what changed: input events
arrive at keystroke rate, so any O(dataset) pipeline behind them needs
debouncing or incremental evaluation. Interactive latency budgets are spent
per event, not per intent.

**Problem.** `ApplyFilters` runs a full LINQ filter-sort-materialize over the
complete item list, then several more full passes for the stats line, and
reassigns `ItemsSource` (destroying and rebuilding all containers). Items,
Collector, and Hideout call it directly from `TextChanged`; only QuestListPage
debounces.

**Evidence.** `ItemsPage.xaml.cs:882-960` (pipeline; six extra passes at
`:950-956`), called un-debounced from `:970`; `CollectorPage.xaml.cs:552`;
`HideoutPage.xaml.cs:374`. The debounce precedent:
`QuestListPage.xaml.cs:1107-1120`.

**Recommendation.** Copy the QuestListPage debounce (~200 ms) to the other
three pages, and fold the stats passes into the single filtering pass. An
`ObservableCollection` refresh instead of `ItemsSource` reassignment is a
further step, but the debounce alone removes the felt lag.

#### UI-5: No page-level MVVM: all UI state is pushed imperatively into named controls

Severity: Medium | Effort: L

**Principle.** Separate presentation state and logic from the controls that
render them (Fowler's Presentation Model; WPF's MVVM): not for pattern
purity, but because logic fused to named controls can only be tested by
driving real UI and only be reviewed by mental simulation. (M. Fowler,
Presentation Model; Microsoft MVVM guidance)

**Problem.** There are no page ViewModels, zero `DataContext` assignments,
and zero `ICommand` usage; ~14,500 lines of code-behind hold all UI logic
(`MapPage.xaml.cs` alone is 3,771 lines with ~155 methods). List rows are
properly templated, but detail panels, headers, and filters are driven by
named-control assignment: 148 manual `.Visibility =` sites (a
`BoolToVisibilityConverter` already sits unused-for-this in `App.xaml:33`),
41 `ItemsSource =` reassignments, and whole dialog subtrees constructed in
C# with hand-wired event handlers (`QuestListPage.xaml.cs:1650-1725`,
`:1763-1820`). The cost is not aesthetics: logic fused to controls is why UI
behavior is untestable except through the 30 e2e cases (TEST-1), why UI-1's
manual localization exists, and why every page carries triplicated
subscribe/unsubscribe boilerplate.

**Failure scenario.** Compounding drag: every UI change is a code-behind
change reviewable only by mental simulation, and regressions in the 3,700-line
files are found by users.

**Recommendation.** Do not attempt a rewrite; ratchet instead. (1) Extract a
common page base class for the subscription lifecycle (removes the 5x3
duplicated blocks and RES-3's asymmetry). (2) New or heavily-touched panels
get a ViewModel with bindings; the converter replaces `.Visibility =` sites
as files are touched. (3) The two structurally identical pages
(Items/Collector) are the natural first extraction since they already share
duplicated templates (UI-6). MVVM adoption is a direction enforced in review,
not a project.

#### UI-6: One 685-line App.xaml; styles duplicated verbatim between two pages

Severity: Low | Effort: S

**Principle.** Shared visual resources have one home, and WPF's
`MergedDictionaries` exist so a theme can stay modular without being
duplicated; per-page copies of shared styles are forks that drift. (WPF
resource organization guidance; DRY as in UI-2)

**Problem.** The shared theme is genuinely good but lives in a single
685-line `App.xaml` with zero merged dictionaries, and page-local resources
have started duplicating: `QuantityButtonStyle`, `ItemListTemplate`, and
`ItemListBoxItem` are copy-pasted between `CollectorPage.xaml` and
`ItemsPage.xaml` (`:15/:24/:143` vs `:15/:24/:154`), and
`BoolToVisibilityConverter` is redeclared in two dialog windows despite the
`App.xaml:33` original.

**Recommendation.** Split App.xaml into `Themes/Colors.xaml`,
`Typography.xaml`, `Controls.xaml` via `MergedDictionaries`, and promote the
three duplicated item-list resources to the shared theme.

---

## Tooling and repository hygiene (TOOL)

#### TOOL-1: The build is at zero warnings and nothing keeps it there

Severity: High | Effort: S

**Principle.** Quality gates ratchet: any standard the build does not enforce
regresses one broken window at a time, and a currently-clean build is the
cheapest possible moment to make clean the enforced floor. If the pipeline
does not fail on it, it is not a standard but a hope. (A. Hunt and
D. Thomas, broken windows; J. Humble and D. Farley, Continuous Delivery)

**Problem.** A clean Release rebuild produces zero warnings at level 8 with
`Nullable` enabled: rare discipline for a codebase this size. And it is
entirely unenforced: no `TreatWarningsAsErrors`, no `.editorconfig`, no
`Directory.Build.props`, no `AnalysisLevel`/`AnalysisMode` (so the built-in
NetAnalyzers mostly idle at suggestion severity), no `global.json` (CI floats
on whatever 8.0.x SDK the runner has), and no central package management
(`Microsoft.Data.Sqlite` is pinned separately in two csprojs). The first
regression will sail through CI unnoticed, and zero-warnings states decay
monotonically once they do.

**Failure scenario.** Someone merges the first nullable warning; three months
later there are forty and the signal is gone. This is the highest
value-to-effort ratio in the assessment: locking in an asset already paid for.

**Recommendation.** Add root `Directory.Build.props` with
`TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`, and
`EnforceCodeStyleInBuild`; an `.editorconfig` for style and analyzer severity;
`global.json` to pin the SDK; `Directory.Packages.props` for central package
versions. One PR, near-zero risk given the current clean state.

#### TOOL-2: Build artifacts and scratch scripts are tracked in git

Severity: Medium | Effort: S

**Principle.** A repository holds sources, not derived artifacts: build
output is reproducible from source, while git history is permanent, so every
committed artifact is paid for by every future clone forever. Scratch tooling
either graduates to a maintained location or leaves.

**Problem.** The repository tracks files that should never have been
committed, and two of them actively cause friction: the nested duplicate
solution already forces a documented workaround in the test suite, and a
14 MB zip of build output rides in every clone forever.

**Evidence** (all confirmed tracked via `git ls-files`):
- `TarkovHelper/bin.zip`: 14,909,288 bytes of zipped build output.
- `TarkovHelper/TarkovHelper.sln`: nested duplicate solution with a different
  project GUID; `TestRepo.cs:17-19` exists specifically to disambiguate it.
- `CheckDb/`: an untouched `dotnet new console` scaffold (Hello World)
  targeting net10.0, in no solution; it would not even restore on CI's 8.0 SDK.
- `TarkovHelper/tarkov_data.db`: a 0-byte accident shadowing the real
  `Assets/tarkov_data.db`.
- Scratch scripts inside the app project: four Python log-analysis scripts
  with hardcoded machine-local paths, `TestDecode.csx` (live-hits an external
  API), `check_db.ps1` (breaks on any TFM change), plus root
  `extract_quests.py`.
- `TarkovHelper/1764579946.ico`: the app icon named after a Unix timestamp.
- `.gitignore` covers `[Bb]in/` directories but nothing above (no `*.zip`
  rule, no policy line for `*.db`).

**Recommendation.** Delete `bin.zip`, the nested sln, `CheckDb/`, and the
0-byte db; move still-useful scripts to `tools/` with a README or delete
them; rename the icon to `app.ico` (two csproj lines); extend `.gitignore`
accordingly. History rewrite for the 14 MB blob is optional and only worth it
if clone size starts to matter.

#### TOOL-3: CI runs tests but no format check and no coverage collection

Severity: Low | Effort: S

**Principle.** Continuous integration is the definition of done that actually
holds: qualities that matter (formatting, coverage trend) belong in the
pipeline, because unmeasured qualities degrade invisibly. (M. Fowler,
Continuous Integration)

**Problem.** `ci.yml` builds and runs the ~269 non-e2e tests on every PR
(good; the e2e exclusion is the right call for hosted runners). Missing:
`dotnet format --verify-no-changes`, coverage collection (the
`coverlet.collector` package is referenced and never invoked), NuGet caching,
and test-result artifact upload.

**Recommendation.** Add the format gate and
`--collect:"XPlat Code Coverage"` with an artifact upload; wire
`actions/setup-dotnet` NuGet caching. Each is a few workflow lines. Coverage
numbers also make TEST-1's progress visible instead of anecdotal.

#### TOOL-4: CLAUDE.md documents patterns the code no longer uses

Severity: Low | Effort: S

**Principle.** Onboarding docs are executable in the sense that people
execute them: a stale example is not missing documentation but active
mis-training, worse than none. Docs that live next to code are updated in the
PRs that invalidate them.

**Problem.** `TarkovHelper/CLAUDE.md` is the onboarding document for both
humans and AI tooling, and it has drifted: the "Cross-Tab Navigation" section
teaches `Application.Current.MainWindow as MainWindow`, a pattern with zero
occurrences in the code (the real idiom is `Window.GetWindow(this) as
MainWindow`, 7 sites), and the "Service Initialization Order" prose implies an
enforcement that does not exist (ARC-2).

**Recommendation.** Fix the navigation example, and reword the initialization
section to describe the cascade as it actually happens (or fix ARC-2 and then
document the explicit order). Drifted onboarding docs train every future
contributor, human or not, to write the wrong pattern.

---

## Suggested sequencing

Severity says what matters; this section says what to do first, optimizing for
risk removed per unit of effort. Every item that becomes work follows the
normal decisions process (PRD/spec where warranted) and names its finding IDs
in the PR body.

**Wave 1, immediate (each a small, low-risk PR):**
TOOL-1 (lock in zero warnings), TOOL-2 (delete strays), STA-1 (handler
order), STA-2 (crash log), DATA-2 (fix the filter, first migration test),
DATA-4 (real logger in UserDataDbService), THR-5 (InvokeAsync sweep), RES-2
(page recreation guards), UI-2 (delete duplicate class), TOOL-4 (doc drift).

**Wave 2, short-term correctness (small series each):**
THR-1 (snapshot-swap in progress services), THR-2 (raise outside locks),
THR-6 (unobserved-task handler plus FireAndLog), THR-7/THR-8 (timer and guard
hygiene), DATA-3 (connection factory, WAL, busy_timeout), DATA-6 (catch
hygiene), RES-1 (shutdown disposal, flush inventory debounce), UI-3
(virtualize the drawer), UI-4 (debounce), STA-3 (cleanup allowlist), TEST-1
(user_data.db round-trip and migration suites).

**Wave 3, structural directions (incremental, enforced in review):**
ARC-1 (Generic Host composition root; Instance facades), ARC-2 (explicit
async startup sequence), THR-3 (execute `fix-userdata-init-deadlock.md`),
THR-4 (async call chains), DATA-1 (schema versioning), DATA-5 (transactions
and named mapping), ARC-3 (decompose god services along named seams), UI-1
(bound localization, screen by screen), UI-5 (MVVM ratchet plus page base
class), UI-6 (theme split).

The wave-1 items are deliberately independent of each other and of the
structural work: nothing blocks starting all ten tomorrow.

---

## Sources

Inline citations in the Principle fields refer to these works. Where a
Principle carries no citation, it states a widely held operational practice
rather than a single authoritative text.

- Microsoft Learn: Managed Threading Best Practices; Exception handling best
  practices (and analyzer rules CA1031, CA2200); TPL exception handling;
  Architectural principles (Explicit Dependencies Principle); WPF data
  binding overview; Weak event patterns; Optimizing WPF performance:
  controls. All under <https://learn.microsoft.com/dotnet/>.
- Stephen Cleary, "Async/Await - Best Practices in Asynchronous Programming",
  MSDN Magazine, March 2013.
- David Fowler, "ASP.NET Core Diagnostic Scenarios: Async Guidance",
  <https://github.com/davidfowl/AspNetCoreDiagnosticScenarios>.
- Krzysztof Cwalina and Brad Abrams, "Framework Design Guidelines" (Dispose
  pattern, event design).
- Mark Seemann and Steven van Deursen, "Dependency Injection Principles,
  Practices, and Patterns" (Manning, 2019): Composition Root, Constrained
  Construction anti-patterns.
- Robert C. Martin, the SOLID principles (Single Responsibility).
- Martin Fowler, "Refactoring" (2nd ed.); "Presentation Model" (2004);
  "Continuous Integration" (martinfowler.com).
- Pramod Sadalage and Martin Fowler, "Refactoring Databases: Evolutionary
  Database Design" (2006).
- SQLite documentation: "Write-Ahead Logging" (<https://sqlite.org/wal.html>),
  `PRAGMA user_version`, transaction semantics.
- Michael Feathers, "Working Effectively with Legacy Code" (2004): seams,
  characterization tests.
- Andrew Hunt and David Thomas, "The Pragmatic Programmer": DRY, broken
  windows.
- Jez Humble and David Farley, "Continuous Delivery" (2010).
- Jerome Saltzer and Michael Schroeder, "The Protection of Information in
  Computer Systems" (1975): fail-safe defaults.
- Michael Nygard, "Release It!" (2nd ed., 2018): transparency, fail fast.
- ECMA-335 (CLI specification): atomicity of native-word reference writes.

---

## Verification note, appended 2026-08-16

Added on request after a code check at commit `ffa08d1`. The snapshot above is
unchanged; this note records where each finding stands now. Since `d1734b4`,
39 commits have landed, all on the seasonal-profile line of work (profile data
attribution, revision-gated reloads, complete profile reset, the settings race
fix), and the verdicts fall exactly along that line. `App.xaml.cs`,
`Program.cs`, `MapPage.xaml.cs`, and `Settings/MapSettings.cs` have zero diff
since the snapshot, which mechanically accounts for many of the open verdicts.
The snapshot's severity and effort ratings still apply to every open finding;
adjustments for the partials are noted inline.

Status: 2 fixed, 11 partial, 21 open.

### Fixed

- **THR-1**: quest progress is now a single immutable `ProgressSnapshot`
  (ImmutableDictionary fields) published by `Volatile` read/write with a CAS
  retry loop (`QuestProgressService.Mutate`); reloads build off to the side,
  check `RevisionGate`, and publish once. `HideoutProgressService` guards
  mutation, reset, and load publication with a dedicated `_stateGate` lock.
  The exact build-then-swap recommendation shipped as part of the profile
  attribution work. The assessment's only Critical is closed.
- **TEST-1**: the suite roughly doubled (about 270 to about 660 cases) and
  the coverage inversion is corrected: `UserDataDbService` is exercised
  against real temp-file databases (`TempStoreRoot.NewStore` through the
  internal path constructor; `ProfileResetStoreTests` seeds every owned table
  and reads it back), log parsing has `LogSyncAttributionTests` plus the EFT
  parser suites, and the `AppProfileId` migration is covered including an
  eight-instance concurrent-migration race. One carve-out stands:
  `MigrateToProfileSchemaAsync`, the destructive rename-copy-drop migration
  DATA-2 worries about, has still never been executed by a test.

### Partial

- **THR-3**: the five synchronous accessors still block on
  `InitializeAsync().GetAwaiter().GetResult()`; init now has a synchronized
  `SemaphoreSlim` fast path, and `ConfigureAwait(false)` exists in exactly
  two files (`UserDataDbService`, `TrackedUserDataWrites`; 27 sites), zero
  elsewhere.
- **THR-4**: 14 blocking sites are down to 4: three startup loads plus
  `HideoutProgressService.SaveSingleModule`, the one remaining write that
  freezes the UI on every hideout level click. `LogSyncService` is clean.
- **THR-5**: 8 detached-continuation sites are down to 4 (the language-change
  and progress handlers in `ItemsPage` and `CollectorPage`).
- **THR-6**: still no `TaskScheduler.UnobservedTaskException` handler and
  still about 38 discard sites, but the data-loss shape is closed where it
  mattered most: progress and inventory writes flow through
  `TrackedUserDataWrites`, whose logging wrapper never faults the returned
  task, so those discards can no longer fault unobserved. Of the four named
  `async void` methods, one is gone, one gained a full try/catch, and
  `HideoutPage.UpdateDetailPanel` plus `InProgressQuestInputDialog.LoadTraders`
  remain unguarded.
- **THR-7**: the inventory save-debounce now flushes through an async method
  with a catch-all and tracked writes;
  `DatabaseUpdateService.OnUpdateTimerElapsed` is still a bare `async void`
  timer callback.
- **THR-8**: `UserDataDbService._isInitialized` became a `SemaphoreSlim` with
  a `Volatile` fast path; `DatabaseUpdateService._isUpdating` and
  `UpdateService._isChecking` are still plain non-volatile check-then-act
  bools.
- **RES-3**: the `MainWindow` profile lambda is now a named handler detached
  on close; the two `App.xaml.cs` lambdas and `MapPage`'s nine-subscribe,
  three-unsubscribe asymmetry are unchanged.
- **DATA-1**: still no `PRAGMA user_version`. The one schema change since
  (`RaidHistory.AppProfileId`) shipped as a second bespoke
  `pragma_table_info`-probed migration, exactly the "continue probe-based
  evolution" alternative this finding rejected. The "not a single ALTER
  TABLE" evidence line is now false; the missing mechanism is not.
- **DATA-2**: the swallow-versus-escape asymmetry is unchanged (the code-1
  filter survives, its magic number now a named constant), but the body logs
  through the real logger with the exception object, rollback is guarded, and
  `InitializeAsync` now rethrows and only marks initialized on success. Still
  zero tests over this migration.
- **DATA-4**: `UserDataDbService` is fully converted to `Log.For<T>` (zero
  `Debug.WriteLine` left); `HideoutDbService` is untouched (7 remain), and
  about 35 `Debug.WriteLine` persist across 11 other production files.
- **DATA-5**: one transaction was added (`ResetProfileAsync`; five
  `BeginTransaction` sites total). Objective and inventory batch saves are
  still per-row autocommit, and ordinal mapping not only survives: the new
  `AppProfileId` was appended as ordinal 18 to the big raid SELECT, extending
  exactly the fragility described.

### Open

- **THR-2**: both lock-held raise patterns are intact (`LogSyncService`
  raises `MonitoringStatusChanged` under `_watcherLock`;
  `EftRaidEventService` raises from about 14 sites under `_readLock`), and
  the blocking subscriber still exists (`MapPage.OnRaidEvent` wraps its body
  in `Dispatcher.Invoke`); only `MainWindow` moved to `InvokeAsync`. With
  THR-1 closed, this is the top remaining threading risk.
- **ARC-1**: no container; the `Instance` count grew to 38
  (`ProfileResetService`). One genuine seam appeared, `IQuestProgressStore`
  (every method takes an explicit profile id; injected into
  `QuestProgressService` and `LogSyncService`), whose own doc comment states
  that ARC-1 stays open.
- **ARC-2**: the construction cascade and the premature-PvP-load-then-repair
  shape survive; the new revision counter makes the repair race-safe, but the
  order is still implicit and the compensation still runs every startup.
- **ARC-3**: none of the five named files was split; four grew
  (`QuestProgressService` 1,677 to 2,042), and `SettingsService` (974 to
  1,606) joined the tier.
- **RES-1**: `App.OnExit` still disposes 4 of 11; the inventory debounce
  still has no exit flush, so a change made within half a second of closing
  the app is lost.
- **RES-2**: `HideoutPage`, `ItemsPage`, and `CollectorPage` are still
  recreated rather than reused at the same four call sites; only
  `_questListPage` (and now `_mapTrackerPage`) has the guard.
- **STA-1, STA-2, STA-3**: `Program.cs` and `App.xaml.cs` are byte-identical
  to the snapshot: migration and window construction still run before any
  crash handler exists, the crash log still overwrites itself, and the Data/
  wipe still runs synchronously with two empty catches.
- **DATA-3**: 38 inline connection constructions, zero pragmas anywhere, no
  factory.
- **DATA-6**: all six named bare catches remain, the empty-catch count moved
  from 35 to 36, and the DB services still log `ex.Message` without the
  exception object.
- **UI-1 through UI-6**: all open, and two moved backwards. UI-2 now has
  three duplicated classes, not one (`QuestGroupHeader` and
  `QuestDrawerTemplateSelector` are also byte-identical copies inside
  `MapPage.xaml.cs`), UI-5's imperative counts drifted up (`.Visibility =`
  sites 183 to 195), and the new profile UI (reset dialog, wide selector)
  follows the imperative pattern, including one-shot localization with no
  `LanguageChanged` subscription in the reset dialog.
- **TOOL-1 through TOOL-4**: nothing was added (no `Directory.Build.props`,
  `.editorconfig`, or `global.json`; `Microsoft.Data.Sqlite` is now pinned in
  three csprojs, not two), every TOOL-2 stray is still tracked including the
  14.9 MB `bin.zip`, `ci.yml` is unchanged, and both CLAUDE.md drifts stand.

### Reading the result against the sequencing section

The profile work fixed the two hardest wave-2 items outright (THR-1, TEST-1)
and dented THR-4/5/6/7/8 and DATA-4 along the way. Wave 1, the "each a small,
low-risk PR, nothing blocks starting all ten tomorrow" list, is nearly
untouched: of its ten items, DATA-4 is half done and DATA-2 got its logging
half; TOOL-1, TOOL-2, STA-1, STA-2, THR-5's remainder, RES-2, UI-2, and
TOOL-4 remain exactly as cheap and exactly as unfixed as the day they were
written. With THR-1 closed, no Critical finding remains open; the
highest-stakes leftovers are THR-2 (the deadlock class is re-armed by any
blocking subscriber, and `MapPage` is one today), DATA-3 (two writers, no
busy timeout), DATA-2's swallowed-failure branch, and STA-1.

