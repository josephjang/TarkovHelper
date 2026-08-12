# Profile Data Attribution - Technical Spec

- **Created**: 2026-08-11

> The sibling `fix-profile-data-attribution.md` holds the product decision. Write
> this on the work's branch and merge it in the same PR as the work. Nothing is kept
> current: fields are written once, discoveries are appended. A later change that
> reverses a decision here appends `Superseded by <doc>` below this line, in the PR
> that reverses it.

## Summary

Today every write asks `ProfileService.ActiveProfileId` which partition it belongs
to. That question is answered by what the user has selected on screen, which is the
correct answer for hand entry and wrong for anything read from logs. This change
makes the write carry its own partition key: `QuestLogEvent` gains an `OwnerProfile`
derived from the `Session mode` timeline of the folder it was parsed from, and the
progress cache becomes an immutable snapshot that holds its own `ProfileId`. After
both, no write path reads `ProfileService`, and a source-scanning test locks that
down.

The design rests on two facts confirmed in the code. The evidence needed for log
attribution is already parsed by `EftRaidEventService` (`SessionModeRegex`,
`TryParseSessionProfile`, `ExtractTimestamp`); it is used to stamp one "latest hint"
and then discarded. And `QuestLogEvent.SourceFile` already records which file an
event came from, which is enough to find its session folder.

## Goals

No code on a write path reads `ProfileService`. The partition key arrives as a
parameter or as a field of the value being written.

The `Session mode` parser exists once. `EftRaidEventService` and the sync path share
it, so a correction to the pattern cannot land in only one of them.

The progress cache and the profile it belongs to change together, so a reader or a
writer cannot observe one without the other.

## Non-Goals

**Raid history attribution.** `RaidHistory.ProfileId` stores the in-game character
id, not an `AppProfile`. Adding a nullable app-profile column follows the same
timeline lookup this spec builds, and ships separately so this change stays
reviewable.

**A write queue.** Ordering pending writes against a profile reset is what SPA-3
needs. Because every persistence method in this change takes an explicit
`profileId`, a queue can be inserted later without touching call sites.

**Full dependency injection.** Only the store surface `QuestProgressService` uses is
extracted to an interface, enough to write the guard tests. `ARC-1` stays open.

**Correcting rows already stored under the wrong profile.** No repair pass, no
detector, no schema change for provenance. The PRD records why. A later repair will
want to know whether a row came from a log or from the user, and nothing here adds
that, so it starts from the same position this change does.

## Current Behavior / Root Cause

### Log data has no attribution and is written to the selected profile

`LogSyncService.ParseLogDirectoryAsync` enumerates `*push-notifications*.log` with
`SearchOption.AllDirectories`, so one run covers every session folder the game still
retains, across all game modes. `ParseJsonBlock` builds a `QuestLogEvent` carrying
`QuestId`, `EventType`, `TraderId`, `Timestamp`, `OriginalLine` and `SourceFile`.
There is no field for which profile the event belongs to.

`LogSyncService.ApplyQuestChangesAsync` then calls
`QuestProgressService.ApplyQuestChangesBatchAsync`, which passes
`ProfileService.Instance.ActiveProfileId` to `SaveQuestProgressBatchAsync`. Every
event in the run lands in one partition, chosen by what the user had on screen. This
is deterministic, not a race.

The same shape drives the live path: `MainWindow.OnQuestEventDetected` marshals to
the dispatcher and calls `CompleteQuest` / `FailQuest` / `CompleteQuestsBatch`, all
of which resolve the partition from the same global.

`MainWindow.PerformQuestSync` calls `SyncFromLogsAsync(logPath, progress)` and omits
the third parameter, so `daysRange` takes its default of `0` and no date filter
runs. `SettingsService.SyncDaysRange` persists a value that never reaches the
service.

### The evidence is present and already parsed

`EftRaidEventService` holds `SessionModeRegex` matching the complete token
(`Pve|PvpSeason|Pvp|Regular`), `TryParseSessionProfile`, `TimestampRegex` anchored at
the start of a log line, and `ExtractTimestamp`. `InitialScan` already pairs them:
it walks the application log and keeps `lastProfileHint` together with
`lastProfileHintAt = ExtractTimestamp(line)`. It keeps only the last pair and
discards the rest, because its job is to answer "what is the current mode".

A session folder can contain several transitions. A capture recorded in
`eft-1-1-profile-selection-log-analysis.md` and re-measured for this change shows
four in one folder within five minutes (`Pve`, `PvpSeason`, `Regular`, `Pve`).
Folder-level attribution is therefore not sufficient; the lookup has to be by
timestamp.

### Cache and partition key are two independent pieces of state

`ProfileService.SetActiveProfile` assigns `_activeProfile` and raises
`ActiveProfileChanged` synchronously, from the log watcher thread pool with no
dispatcher marshalling. Subscribers respond with `_ = ReloadForProfileAsync()` and
return immediately. Between the assignment and the cache swap,
`ActiveProfileId` names the new profile while `_questProgress` still holds the old
one's rows, so a hand edit in that window is attributed to a profile whose data was
never on screen.

`ReloadForProfileAsync` awaits `LoadProgressFromDbAsync` and
`LoadObjectiveProgressFromDbAsync` separately, each resolving the profile again, so
the two caches can end up from different profiles. `LoadProgressFromDbAsync` clears
and refills `_questProgress` from a thread pool thread while the dispatcher can be
enumerating it; `QuestProgressService.cs` contains no `lock`.

Deferred lookups widen both windows. Three shapes appear: a lookup inside a
`Task.Run` body (`FailQuest`, `ResetQuest`, `ResetAllProgress`,
`ItemInventoryService.ResetAllInventory`, `HideoutProgressService.SaveSingleModule`),
a lookup repeated inside an `await` loop (`SaveObjectiveProgressBatchAsync`,
`SaveProgressToDbAsync`, `SaveObjectiveProgressToDbAsync`,
`HideoutProgressService.SaveProgressToDbAsync`), and a lookup evaluated as a call
argument before the first suspending await (`SaveProgressBatchAsync`,
`ApplyQuestChangesBatchAsync`), which is the only one with no meaningful window. The
loop shape is the worst: `SetObjectiveCompleted` records one objective under two
keys, objective rows are written one connection at a time with no transaction, so a
transition mid-batch splits one edit across two partitions permanently.

## Design

The work divides into two independent halves. Log attribution is small, touches
parsing and the sync apply path, and closes the deterministic defect. The cache
snapshot is large, touches roughly fifty read sites in the biggest service, and
closes the timing defects. They can ship as separate PRs; if they do, log
attribution goes first, because the defect it closes fires on every run rather than
in a window.

### Shared log patterns

`SessionModeRegex`, `TryParseSessionProfile`, `TimestampRegex` and `ExtractTimestamp`
move out of `EftRaidEventService` into a new static `EftLogPatterns` under
`Services/Eft/`. `EftRaidEventService` keeps its behavior and calls the extracted
members. Nothing about the patterns changes in this move.

### Session mode timeline

A new `SessionModeTimeline` reads a session folder's application log once and returns
the ordered `(DateTime At, SessionProfileHint Hint)` pairs, in file order, which is
chronological. `Resolve(timeline, at)` walks to the last entry whose `At` is not
after `at` and maps the hint through `ProfileService.TryResolveDetectedProfile`. An
event earlier than the first entry, or a hint with no mapping, yields `null`.

`ParseLogDirectoryAsync` groups the discovered notification logs by parent folder,
builds one timeline per folder, and stamps each parsed event. Timelines are built
once per folder, not once per event.

### Event and result shape

`QuestLogEvent` gains `AppProfile? OwnerProfile`, documented as null meaning "no
evidence", never a default. `SyncResult` gains per-profile applied counts, an
already-current count, and an unattributed count. `QuestChangeInfo` carries the
owning profile so the summary can be built without re-deriving it.

### Sync apply path

`LogSyncService.ApplyQuestChangesAsync` groups changes by `OwnerProfile`, skips the
null group after counting it, and calls the batch save once per profile with that
profile's id. `QuestProgressService.ApplyQuestChangesBatchAsync` takes the profile as
a parameter instead of resolving it, and refreshes the in-memory cache only for the
group whose profile matches the currently loaded snapshot.

`MainWindow.PerformQuestSync` passes `_settingsService.SyncDaysRange`. The existing
filter inside `SyncFromLogsAsync` is unchanged; it simply starts receiving the
configured value.

`SyncResultDialog` becomes two things: a summary of what was applied per profile,
and, when `AlternativeQuestGroups` is non-empty, the choice list for mutually
exclusive prerequisites. The per-quest checkbox list is removed.

### Live event path

`LogSyncService.QuestEventDetected` carries the attributed profile.
`MainWindow.OnQuestEventDetected` no longer calls `CompleteQuest` / `FailQuest`
directly; it calls a new `QuestProgressService.ApplyLogEventAsync(task, eventType,
AppProfile owner)`. That method always persists under `owner`, and updates the
snapshot only when `owner` equals the snapshot's `ProfileId`. An event for a profile
that is not loaded changes the database and nothing else, which is what the PRD's
silent-write decision requires.

### Progress snapshot

`_questProgress` and `_objectiveProgress` are replaced by a single immutable
`ProgressSnapshot(string ProfileId, long Revision, ImmutableDictionary<string,
QuestStatus> Quests, ImmutableDictionary<string, bool> Objectives)` held in one
field. Mutations read the field into a local, derive the next snapshot, publish it
with `Interlocked.CompareExchange` and retry on contention, and pass the local's
`ProfileId` to persistence. Read sites capture the field into a local once and use
that local throughout, which gives an internally consistent view without a lock.

`ReloadForProfileAsync` takes the target profile and a revision, builds both
dictionaries off-thread, and publishes only if the revision is still the latest. The
two caches are therefore always swapped together. `ProfileService` gains a monotonic
revision that increments on each transition and travels with the event.

Every persistence helper takes `string profileId` as a required parameter. The five
methods with no callers are deleted rather than converted:
`SaveSingleQuestProgress`, `DeleteSingleQuestProgress`, `SaveSingleObjectiveProgress`,
`DeleteSingleObjectiveProgress`, and `ClearObjectiveProgress`. The last one also
removes keys from the cache and then persists only the survivors, leaving the removed
rows in the database; `UserDataDbService.DeleteObjectiveProgressByQuestAsync` already
does that job correctly and is likewise unused, so it is the one to call if the
behavior is ever wanted again.

### Test seam

The methods `QuestProgressService` calls on `UserDataDbService` are extracted to
`IQuestProgressStore`, implemented by `UserDataDbService`. The field becomes
`internal` so tests can substitute a fake. Existing tests already construct services
through `RuntimeHelpers.GetUninitializedObject` and reflection
(`ProfileSwitchingTests`), so no new pattern is introduced.

### Files

- `TarkovHelper/Services/Eft/EftLogPatterns.cs` (new, extracted)
- `TarkovHelper/Services/Eft/SessionModeTimeline.cs` (new)
- `TarkovHelper/Services/EftRaidEventService.cs` (call extracted patterns)
- `TarkovHelper/Models/QuestLogEvent.cs` (`OwnerProfile`, `SyncResult` counts, `QuestChangeInfo`)
- `TarkovHelper/Services/LogSyncService.cs` (attribute on parse, group on apply, carry owner on the event)
- `TarkovHelper/Services/QuestProgressService.cs` (snapshot, `ApplyLogEventAsync`, explicit profile parameters, delete unused helpers)
- `TarkovHelper/Services/IQuestProgressStore.cs` (new)
- `TarkovHelper/Services/UserDataDbService.cs` (implement the interface)
- `TarkovHelper/Services/ProfileService.cs` (transition revision)
- `TarkovHelper/MainWindow.xaml.cs` (pass the range, pass the owner)
- `TarkovHelper/Windows/SyncResultDialog.xaml`, `.xaml.cs` (summary plus alternatives only)
- `TarkovHelper/Services/LocalizationService.Quest.cs` (summary strings, EN/KO/JA)

## Technical Decisions

### Attribution is resolved during parsing, not at apply time

`ParseLogDirectoryAsync` stamps `OwnerProfile` while it still knows which folder each
event came from. The alternative was to attribute later, at apply time, from
`SourceFile`. Parsing time wins because the timeline is built once per folder there,
whereas at apply time the events are already flattened and each one would have to
find its folder again. It also means every consumer of a `QuestLogEvent`, present or
future, gets an attributed event rather than having to remember to resolve it.

### The parser is extracted rather than duplicated

`EftRaidEventService` keeps working through the extracted `EftLogPatterns`. Writing a
second `Session mode` matcher for the sync path was the obvious shortcut and was
rejected: the existing pattern has already been corrected once, for a prefix bug
where `Session mode: PvpSeason` matched the `Pvp` alternative and classified a
seasonal session as permanent PvP. A second copy is a second place for that class of
bug to survive a fix.

### Unattributable events are dropped at the apply step, not at parse

Parsing keeps the event with `OwnerProfile == null` so the count is available for the
summary and so a future consumer can decide differently. Only the apply step skips
them. Dropping them during parsing would make the reported count harder to produce
and would discard evidence a repair run might use later.

### Local timestamps are compared as-is, and the DST hole is accepted

`Session mode` timestamps are local-time strings with no offset. Quest event times
come from a Unix `dt` converted with `DateTimeOffset.FromUnixTimeSeconds(...)
.LocalDateTime`. Both are local, so they compare directly.

During a daylight-saving fall-back the same local hour occurs twice, so ordering
within that hour is ambiguous and an event can be attributed to the wrong side of a
transition that happened inside it. Converting the log strings to absolute time would
need the offset in force when the line was written, which the line does not carry.
The exposure is one hour per year in locales that observe DST, and only for
transitions inside that hour. It is recorded here rather than solved.

### Mutation uses compare-and-swap rather than a plain assignment

Publishing the next snapshot with `Interlocked.CompareExchange` and retrying costs a
loop that will almost never spin. A plain assignment would be enough today, because
mutation paths are in practice confined to the dispatcher:
`OnQuestEventDetected` marshals with `Dispatcher.Invoke`, and the sync path starts
from an `async void` handler on the dispatcher. Nothing enforces that confinement,
and a lost update from a second writer would be silent. The retry loop makes the
guarantee independent of a property no test checks.

### What a later repair would need, recorded before it is forgotten

The repair pass cut from this change was designed and is written down here so the
next attempt does not restart from nothing. It would re-parse and attribute over the
retained range, write the derived state to each profile, and then select rows whose
`UpdatedAt` falls inside the covered range that the derived state does not contain,
as removal candidates for the user to confirm. `UpdatedAt` already exists on
`QuestProgress` and is set by `SaveQuestProgressBatchAsync`, so that bound needs no
schema change, and it is what keeps legitimately older rows out of the list.

The part that has no answer today is provenance. Without knowing whether a row came
from a log or from the user, the candidate list mixes stale misfiled rows with hand
entry, and only the user can tell them apart. A source column added at the same time
as a repair pass would remove that ambiguity for everything written after it, though
not for anything already stored.

### The two halves may ship separately, log attribution first

Log attribution touches parsing and one apply path. The snapshot touches roughly
fifty read sites in the largest service in the codebase. Splitting is reasonable, and
if it happens the order is fixed: the defect log attribution closes fires on every
sync, while the defect the snapshot closes needs a transition to land inside a
specific window. Shipping the large half first and closing the issue would leave the
frequent defect in place.

## Test Strategy

- **Unit, timeline**: a folder with no `Session mode` line, one line, and the
  measured four-transition capture each produce the expected ordered pairs.
- **Unit, resolution**: an event before the first transition resolves to null; events
  exactly at, just before, and just after a transition resolve to the correct side.
- **Unit, unattributable**: a null owner is never written to any partition and is
  counted in the summary.
- **Unit, sync distribution**: events from a PvE folder and a seasonal folder in one
  run land in `pve` and `season` respectively, with the active profile set to a third
  value throughout.
- **Unit, range**: the configured `SyncDaysRange` reaches `SyncFromLogsAsync`; a
  range of `0` keeps current behavior.
- **Unit, reload window**: with the store gated mid-load, a transition followed by an
  edit persists under the snapshot's profile, and the later swap does not drop the
  edit.
- **Unit, out-of-order reload**: a delayed reload for an earlier revision does not
  replace a snapshot published by a later one.
- **Unit, batch integrity**: a transition during an objective batch leaves both keys
  of one objective in the same partition.
- **Unit, live event**: an event whose owner is not the loaded profile writes to the
  database and leaves the snapshot untouched.
- **Source scan**: `ProfileService` is referenced only from an allowlist of
  selection and reload sites, never from a persistence path. Follows the existing
  `FontAssetsTests` and `DecisionDocsTests` pattern using `TestRepo.Root()`.
- **E2E**: sync from a fixture log tree containing one PvE session and one seasonal
  session while the seasonal profile is selected, then assert the PvE quests are
  absent from the seasonal profile and present in PvE. The fixture is the measured
  four-transition capture with account and character ids removed.

The reload-window and sync-distribution tests are written first and confirmed failing
against the current code, so the guard is known to reproduce the defect before the
fix lands.

## Discoveries During Implementation

**Line framing moved with the parser.** `EftLogPatterns` took `FrameCompletedLines` and
its two size constants along with the four members this spec named.
`SessionModeTimeline` reads a log EFT is still writing, so it needs the same
"complete lines only" framing, and leaving that behind would have reproduced the exact
duplication the extraction decision above rejects: a flush truncating at
`Session mode: Pvp` matches the anchored pattern and classifies a seasonal session as
permanent PvP. `EftRaidEventService` calls the extracted copy.

**`LogSyncService`'s application-log glob has never matched anything.** EFT names its
files `<date>_<time>_<version> application.log`, so `application*.log` (anchored at
"application") matches nothing. Three places in `LogSyncService` use it: the map
FileSystemWatcher filter and two `Directory.GetFiles` calls. That whole map path is
dead as a result; `MapDetected` and `FindLastMapFromLogs` have no subscribers anywhere
in the solution, and live map detection is `LogMapWatcherService`'s job, using
`*application*.log`. The glob was left as it is, with a comment recording the finding,
rather than "fixed" into making dead code do unused work. `SessionModeTimeline` uses
the correct pattern; nothing about attribution goes through the dead path.

**The startup load must not raise `ProgressChanged`.** Folding the two loads into one
`ReloadForProfileAsync` gave the startup path a notification it never used to have.
`QuestProgressService.Initialize` runs that load with `Task.Run(...).GetAwaiter()
.GetResult()`, blocking the dispatcher, while `ProgressChanged` subscribers marshal
their refresh back to it: an unconditional deadlock on startup and after every
in-place reload. The startup load therefore passes `notify: false`, matching what the
pre-snapshot initial load did by construction. The e2e sync test is what caught it;
no unit test could, since the deadlock needs a real dispatcher.

**The sync pass had to become per-profile, not just per-event.** Attribution alone was
not enough. "Is this quest already recorded?" was answered from the loaded cache, which
is correct for at most one of the profiles a run touches; every other group would have
been judged against the wrong profile's rows and either rewritten or skipped wrongly.
`SyncFromLogsAsync` now groups the attributed events by owner and reads each owner's
stored rows through `IQuestProgressStore` before planning its changes. That is also
where the already-current count comes from.

**The cascade never needed derived status.** `CascadeLookups.Status` is now the
recorded-only view rather than `GetStatus`. The core consults it solely through gates
testing `== Done` and `== Failed`, and `GetStatus` reports either of those exactly when
progress records it, so the two are interchangeable, and the recorded-only view keeps
`SettingsService` (player level, faction, editions, all profile-scoped) out of a
planner that has to run for a profile that is not the selected one.

**The source scan keys on `ProfileService.Instance`, not on the type name.** The static
members are pure maps that take their input as an argument (`GetProfileId`,
`TryResolveDetectedProfile`, the profile-id constants); it is the instance that reports
the selection. Matching the type name would have banned the very helpers this change
moved everything onto.
