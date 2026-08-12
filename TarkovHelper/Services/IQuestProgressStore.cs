using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// The persistence surface <see cref="QuestProgressService"/> and the log-sync apply path
/// use, extracted from <see cref="UserDataDbService"/> so tests can substitute a fake and
/// observe which profile each write landed in. That observation is the whole point: the
/// defect this interface was extracted for wrote correct rows to the wrong partition, which
/// no test could see while the only implementation was a real SQLite file.
/// <para>
/// Every method takes an explicit <c>profileId</c>. None of them may consult
/// <see cref="ProfileService"/>: the partition key is an argument, never ambient state.
/// Full dependency injection is deliberately not attempted here (ARC-1 stays open). This is
/// the minimum seam that makes the guards writable.
/// </para>
/// </summary>
public interface IQuestProgressStore
{
    Task<Dictionary<string, QuestStatus>> LoadQuestProgressAsync(string profileId);

    Task SaveQuestProgressAsync(string id, string? normalizedName, QuestStatus status, string profileId);

    Task SaveQuestProgressBatchAsync(
        IEnumerable<(string Id, string? NormalizedName, QuestStatus Status)> progressItems, string profileId);

    Task DeleteQuestProgressAsync(string id, string profileId);

    Task ClearAllQuestProgressAsync(string profileId);

    Task<Dictionary<string, bool>> LoadObjectiveProgressAsync(string profileId);

    Task SaveObjectiveProgressAsync(string id, string? questId, bool isCompleted, string profileId);

    Task DeleteObjectiveProgressAsync(string id, string profileId);

    Task ClearAllObjectiveProgressAsync(string profileId);
}
