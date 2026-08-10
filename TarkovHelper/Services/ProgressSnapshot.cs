using System.Collections.Immutable;
using TarkovHelper.Models;

namespace TarkovHelper.Services;

/// <summary>
/// One profile's quest and objective progress, plus the identity of the profile it belongs
/// to, as a single immutable value.
/// <para>
/// The cache and its partition key used to be independent pieces of state: a mutable
/// dictionary here, and "whatever <see cref="ProfileService"/> currently reports" there.
/// <see cref="ProfileService.SetActiveProfile"/> raises its event synchronously from the log
/// watcher's thread pool, and subscribers answer with a fire-and-forget reload, so between
/// the assignment and the swap the selected profile named one partition while the loaded rows
/// were another's. An edit in that window was written to a profile whose data was never on
/// screen. Binding the two into one value that is replaced atomically makes that window
/// impossible to observe: a reader captures the field once and sees rows and profile that
/// belong together, and a writer persists under the ProfileId of the very snapshot it derived
/// its change from.
/// </para>
/// </summary>
/// <param name="ProfileId">Storage partition these rows came from and are written back to.</param>
/// <param name="Revision">
/// The <see cref="ProfileChangedEventArgs.Revision"/> this snapshot was loaded for, so a reload
/// that lost a race can tell it is stale.
/// </param>
/// <param name="Quests">Recorded quest status by progress key (quest Id, or NormalizedName for legacy rows).</param>
/// <param name="Objectives">Objective completion by key (<c>questName:index</c> or <c>id:objectiveId</c>).</param>
internal sealed record ProgressSnapshot(
    string ProfileId,
    long Revision,
    ImmutableDictionary<string, QuestStatus> Quests,
    ImmutableDictionary<string, bool> Objectives)
{
    // OrdinalIgnoreCase to match every other quest-key container (the task lookup dictionaries,
    // the traversal's visited/planned sets, and the dictionaries UserDataDbService's loaders
    // build -- whose comparer used to be silently dropped when their rows were copied into a
    // default-comparer dictionary): stored key casing was never canonical (legacy V2 data
    // compared NormalizedNames case-insensitively), so a case-drifted key must not hide a
    // recorded Done/Failed status or a completed objective.
    internal static readonly ImmutableDictionary<string, QuestStatus> EmptyQuests =
        ImmutableDictionary.Create<string, QuestStatus>(StringComparer.OrdinalIgnoreCase);

    internal static readonly ImmutableDictionary<string, bool> EmptyObjectives =
        ImmutableDictionary.Create<string, bool>(StringComparer.OrdinalIgnoreCase);

    /// <summary>A snapshot naming a profile whose rows have not been read yet.</summary>
    internal static ProgressSnapshot Empty(string profileId, long revision)
        => new(profileId, revision, EmptyQuests, EmptyObjectives);

    /// <summary>
    /// A snapshot for <paramref name="profileId"/> holding the rows just read for it. The two
    /// dictionaries are built together and published together, so a reader can never observe
    /// quest rows from one profile beside objective rows from another.
    /// </summary>
    internal static ProgressSnapshot From(
        string profileId,
        long revision,
        IReadOnlyDictionary<string, QuestStatus> quests,
        IReadOnlyDictionary<string, bool> objectives)
        => new(
            profileId,
            revision,
            EmptyQuests.SetItems(quests),
            EmptyObjectives.SetItems(objectives));
}
