using System.Collections.Concurrent;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// In-memory <see cref="IQuestProgressStore"/> that keeps one partition per profile id and
/// records every write with the profile it named.
/// <para>
/// The whole point is the profile: the defect these tests guard wrote correct rows to the
/// wrong partition, which no assertion could see while the only implementation was a real
/// SQLite file keyed by an ambient "current profile". Here a write to the wrong profile shows
/// up as a row under the wrong key.
/// </para>
/// </summary>
internal sealed class ProgressStoreFake : IQuestProgressStore
{
    private readonly ConcurrentDictionary<string, Dictionary<string, QuestStatus>> _quests = new();
    private readonly ConcurrentDictionary<string, Dictionary<string, bool>> _objectives = new();

    /// <summary>Every quest write in order, so a test can assert what landed where.</summary>
    public List<(string ProfileId, string Id, QuestStatus Status)> QuestWrites { get; } = new();

    /// <summary>Every objective write in order (true = saved, false = deleted).</summary>
    public List<(string ProfileId, string Key, bool IsCompleted)> ObjectiveWrites { get; } = new();

    /// <summary>
    /// Awaited by the loaders before they return. Lets a test hold a reload open and act in the
    /// window the old code left between "the selection changed" and "the cache caught up".
    /// </summary>
    public Func<string, Task>? LoadGate { get; set; }

    /// <summary>
    /// Awaited by every write before it lands. Lets a test hold a deferred save open across a
    /// completed profile switch, which is the shape the old bug took: the profile was looked up
    /// inside the fire-and-forget body, so by the time it ran it named the new profile.
    /// </summary>
    public Func<string, Task>? SaveGate { get; set; }

    private Task Gate(string profileId) => SaveGate?.Invoke(profileId) ?? Task.CompletedTask;

    private readonly object _sync = new();

    public Dictionary<string, QuestStatus> QuestsOf(string profileId)
        => _quests.TryGetValue(profileId, out var rows)
            ? new Dictionary<string, QuestStatus>(rows, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, QuestStatus>(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, bool> ObjectivesOf(string profileId)
        => _objectives.TryGetValue(profileId, out var rows)
            ? new Dictionary<string, bool>(rows, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Seeds a profile's stored quest rows without going through the write path.</summary>
    public void Seed(string profileId, params (string Id, QuestStatus Status)[] rows)
    {
        var partition = QuestPartition(profileId);
        lock (_sync)
        {
            foreach (var (id, status) in rows) partition[id] = status;
        }
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

    private Dictionary<string, QuestStatus> QuestPartition(string profileId)
        => _quests.GetOrAdd(profileId, _ => new Dictionary<string, QuestStatus>(StringComparer.OrdinalIgnoreCase));

    private Dictionary<string, bool> ObjectivePartition(string profileId)
        => _objectives.GetOrAdd(profileId, _ => new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase));

    public async Task<Dictionary<string, QuestStatus>> LoadQuestProgressAsync(string profileId)
    {
        if (LoadGate != null) await LoadGate(profileId);
        return QuestsOf(profileId);
    }

    public Task SaveQuestProgressAsync(string id, string? normalizedName, QuestStatus status, string profileId)
    {
        var partition = QuestPartition(profileId);
        lock (_sync)
        {
            partition[id] = status;
            QuestWrites.Add((profileId, id, status));
        }
        return Task.CompletedTask;
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
                partition[item.Id] = item.Status;
                QuestWrites.Add((profileId, item.Id, item.Status));
            }
        }
    }

    public async Task DeleteQuestProgressAsync(string id, string profileId)
    {
        await Gate(profileId);
        var partition = QuestPartition(profileId);
        lock (_sync)
        {
            partition.Remove(id);
        }
    }

    public Task ClearAllQuestProgressAsync(string profileId)
    {
        var partition = QuestPartition(profileId);
        lock (_sync)
        {
            partition.Clear();
        }
        return Task.CompletedTask;
    }

    public async Task<Dictionary<string, bool>> LoadObjectiveProgressAsync(string profileId)
    {
        if (LoadGate != null) await LoadGate(profileId);
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

    public Task ClearAllObjectiveProgressAsync(string profileId)
    {
        var partition = ObjectivePartition(profileId);
        lock (_sync)
        {
            partition.Clear();
        }
        return Task.CompletedTask;
    }
}
