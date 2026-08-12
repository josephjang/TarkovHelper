using System.Collections.Concurrent;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// One stored quest row, modelling a QuestProgress row in user_data.db: the table's primary key
/// is (ProfileId, Id), and it carries NormalizedName as a second, nullable spelling of the same
/// quest. Both spellings matter, because the real store keys its READS by
/// <c>NormalizedName ?? Id</c> while keying its WRITES by Id.
/// </summary>
/// <param name="Id">The Id column: what a write names the row, and the row's identity.</param>
/// <param name="NormalizedName">The NormalizedName column; null for a row saved without one.</param>
/// <param name="Status">The recorded status.</param>
internal sealed record QuestProgressRow(string Id, string? NormalizedName, QuestStatus Status)
{
    /// <summary>
    /// The key this row appears under when it is loaded back, mirroring
    /// <c>UserDataDbService.LoadQuestProgressAsync</c>'s <c>var key = normalizedName ?? id;</c>.
    /// </summary>
    public string LoadKey => NormalizedName ?? Id;
}

/// <summary>
/// In-memory <see cref="IQuestProgressStore"/> that keeps one partition per profile id and
/// records every write with the profile it named.
/// <para>
/// The whole point is the profile: the defect these tests guard wrote correct rows to the
/// wrong partition, which no assertion could see while the only implementation was a real
/// SQLite file keyed by an ambient "current profile". Here a write to the wrong profile shows
/// up as a row under the wrong key.
/// </para>
/// <para>
/// It also reproduces the real store's KEY POLICY, because that policy is not an implementation
/// detail the callers are insulated from. <c>UserDataDbService</c> writes rows keyed by Id
/// (<c>ON CONFLICT(ProfileId, Id)</c>) and returns them keyed by <c>NormalizedName ?? Id</c>, so
/// a quest written under its Id comes back under its name. A fake that stored and returned Id
/// keys would let a test assert a shape production never produces, and would hide every dedupe
/// bug that compares one spelling against the other.
/// </para>
/// </summary>
internal sealed class ProgressStoreFake : IQuestProgressStore
{
    private readonly ConcurrentDictionary<string, Dictionary<string, QuestProgressRow>> _quests = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, bool>> _objectives = new();

    /// <summary>Every quest save in order, so a test can assert what landed where.</summary>
    public List<(string ProfileId, string Id, QuestStatus Status)> QuestWrites { get; } = new();

    /// <summary>
    /// Every quest delete in order, with the key it was issued for. A delete is as much a write
    /// as a save is, and a reset that reaches the wrong partition is the same defect.
    /// </summary>
    public List<(string ProfileId, string Key)> QuestDeletes { get; } = new();

    /// <summary>Every profile whose quest rows were cleared wholesale, in order.</summary>
    public List<string> QuestClears { get; } = new();

    /// <summary>Every objective write in order (true = saved, false = deleted).</summary>
    public List<(string ProfileId, string Key, bool IsCompleted)> ObjectiveWrites { get; } = new();

    /// <summary>Every profile whose objective rows were cleared wholesale, in order.</summary>
    public List<string> ObjectiveClears { get; } = new();

    /// <summary>
    /// Awaited by the loaders before they return. Lets a test hold a reload open and act in the
    /// window the old code left between "the selection changed" and "the cache caught up".
    /// </summary>
    public Func<string, Task>? LoadGate { get; set; }

    /// <summary>
    /// Replaces <see cref="LoadGate"/> for the objective half of a reload. A reload reads quests
    /// and objectives as two calls, so a test needs to be able to fail one and not the other:
    /// one unreadable table must not blank out the one that read fine.
    /// </summary>
    public Func<string, Task>? ObjectiveLoadGate { get; set; }

    /// <summary>
    /// Awaited by every write before it lands - saves, deletes and clears alike. Lets a test hold
    /// a deferred save open across a completed profile switch, which is the shape the old bug
    /// took: the profile was looked up inside the fire-and-forget body, so by the time it ran it
    /// named the new profile. A write path that skipped this gate would silently make such a test
    /// pass without ever holding the window open.
    /// </summary>
    public Func<string, Task>? SaveGate { get; set; }

    private Task Gate(string profileId) => SaveGate?.Invoke(profileId) ?? Task.CompletedTask;

    private readonly object _sync = new();

    /// <summary>
    /// A profile's quest rows as <see cref="LoadQuestProgressAsync"/> returns them: keyed by
    /// <c>NormalizedName ?? Id</c>, which is the shape every caller of the real store sees.
    /// </summary>
    public Dictionary<string, QuestStatus> QuestsOf(string profileId)
    {
        var result = new Dictionary<string, QuestStatus>(StringComparer.OrdinalIgnoreCase);
        if (!_quests.TryGetValue(profileId, out var rows)) return result;

        lock (_sync)
        {
            foreach (var row in rows.Values) result[row.LoadKey] = row.Status;
        }

        return result;
    }

    /// <summary>
    /// A profile's stored rows keyed by Id, for the assertions that are about what was PERSISTED
    /// (the Id a write named, the NormalizedName it carried) rather than about what a load
    /// returns.
    /// </summary>
    public Dictionary<string, QuestProgressRow> QuestRowsOf(string profileId)
    {
        if (!_quests.TryGetValue(profileId, out var rows))
            return new Dictionary<string, QuestProgressRow>(StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            return new Dictionary<string, QuestProgressRow>(rows, StringComparer.OrdinalIgnoreCase);
        }
    }

    public Dictionary<string, bool> ObjectivesOf(string profileId)
    {
        if (!_objectives.TryGetValue(profileId, out var rows))
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        lock (_sync)
        {
            return new Dictionary<string, bool>(rows, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Seeds a profile's stored quest rows without going through the write path. Both spellings
    /// are explicit: a row's NormalizedName decides the key it loads back under, so a test that
    /// left it out would be seeding a legacy row by accident.
    /// </summary>
    public void Seed(string profileId, params (string Id, string? NormalizedName, QuestStatus Status)[] rows)
    {
        var partition = QuestPartition(profileId);
        lock (_sync)
        {
            foreach (var (id, normalizedName, status) in rows)
                partition[id] = new QuestProgressRow(id, normalizedName, status);
        }
    }

    /// <summary>
    /// Seeds the row a quest's own progress would be stored as: the Id-first progress key
    /// <c>QuestProgressService</c> writes under, carrying the quest's NormalizedName.
    /// </summary>
    public void Seed(string profileId, TarkovTask task, QuestStatus status)
        => Seed(profileId, (ProgressKeyOf(task), task.NormalizedName, status));

    /// <summary>
    /// The key a quest's progress row is written under, mirroring
    /// <c>QuestProgressService.ProgressKeyOf</c>: first non-empty Ids entry, else NormalizedName.
    /// </summary>
    private static string ProgressKeyOf(TarkovTask task)
    {
        var id = task.Ids?.FirstOrDefault(i => !string.IsNullOrEmpty(i));
        var key = !string.IsNullOrEmpty(id) ? id : task.NormalizedName;
        Assert.False(string.IsNullOrEmpty(key), $"Quest '{task.Name}' has neither an Id nor a NormalizedName");
        return key!;
    }

    /// <summary>Seeds a profile's stored objective rows without going through the write path.</summary>
    public void SeedObjective(string profileId, string key, bool isCompleted)
    {
        var partition = ObjectivePartition(profileId);
        lock (_sync)
        {
            partition[key] = isCompleted;
        }
    }

    private Dictionary<string, QuestProgressRow> QuestPartition(string profileId)
        => _quests.GetOrAdd(profileId, _ => new Dictionary<string, QuestProgressRow>(StringComparer.OrdinalIgnoreCase));

    private Dictionary<string, bool> ObjectivePartition(string profileId)
        => _objectives.GetOrAdd(profileId, _ => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

    public async Task<Dictionary<string, QuestStatus>> LoadQuestProgressAsync(string profileId)
    {
        if (LoadGate != null) await LoadGate(profileId);
        return QuestsOf(profileId);
    }

    public async Task SaveQuestProgressAsync(string id, string? normalizedName, QuestStatus status, string profileId)
    {
        await Gate(profileId);
        var partition = QuestPartition(profileId);
        lock (_sync)
        {
            partition[id] = new QuestProgressRow(id, normalizedName, status);
            QuestWrites.Add((profileId, id, status));
        }
    }

    public async Task SaveQuestProgressBatchAsync(
        IEnumerable<(string Id, string? NormalizedName, QuestStatus Status)> progressItems, string profileId)
    {
        await Gate(profileId);
        var partition = QuestPartition(profileId);
        lock (_sync)
        {
            foreach (var item in progressItems)
            {
                partition[item.Id] = new QuestProgressRow(item.Id, item.NormalizedName, item.Status);
                QuestWrites.Add((profileId, item.Id, item.Status));
            }
        }
    }

    /// <summary>
    /// Removes every row the key matches under EITHER spelling, mirroring the real store's
    /// <c>WHERE (Id = @id OR NormalizedName = @id)</c>. That clause is deliberate legacy-row
    /// cleanup: a quest recorded before Ids existed is stored under its name, so a reset issued
    /// with the Id has to find it anyway.
    /// </summary>
    public async Task DeleteQuestProgressAsync(string id, string profileId)
    {
        await Gate(profileId);
        var partition = QuestPartition(profileId);
        lock (_sync)
        {
            var doomed = partition.Values
                .Where(row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(row.NormalizedName, id, StringComparison.OrdinalIgnoreCase))
                .Select(row => row.Id)
                .ToList();
            foreach (var key in doomed) partition.Remove(key);
            QuestDeletes.Add((profileId, id));
        }
    }

    public async Task ClearAllQuestProgressAsync(string profileId)
    {
        await Gate(profileId);
        var partition = QuestPartition(profileId);
        lock (_sync)
        {
            partition.Clear();
            QuestClears.Add(profileId);
        }
    }

    public async Task<Dictionary<string, bool>> LoadObjectiveProgressAsync(string profileId)
    {
        var gate = ObjectiveLoadGate ?? LoadGate;
        if (gate != null) await gate(profileId);
        return ObjectivesOf(profileId);
    }

    public async Task SaveObjectiveProgressAsync(string id, string? questId, bool isCompleted, string profileId)
    {
        await Gate(profileId);
        var partition = ObjectivePartition(profileId);
        lock (_sync)
        {
            partition[id] = isCompleted;
            ObjectiveWrites.Add((profileId, id, true));
        }
    }

    public async Task DeleteObjectiveProgressAsync(string id, string profileId)
    {
        await Gate(profileId);
        var partition = ObjectivePartition(profileId);
        lock (_sync)
        {
            partition.Remove(id);
            ObjectiveWrites.Add((profileId, id, false));
        }
    }

    public async Task ClearAllObjectiveProgressAsync(string profileId)
    {
        await Gate(profileId);
        var partition = ObjectivePartition(profileId);
        lock (_sync)
        {
            partition.Clear();
            ObjectiveClears.Add(profileId);
        }
    }
}
