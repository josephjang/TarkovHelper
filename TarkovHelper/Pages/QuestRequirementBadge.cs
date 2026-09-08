using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Pages;

/// <summary>
/// The short text a level-locked quest's status badge carries: which of the three gates is
/// actually holding it, and at what value.
/// <para>
/// Level, Scav karma and trader loyalty all resolve to <see cref="QuestStatus.LevelLocked"/>, by
/// the recorded decision that the chip vocabulary stays as it is. The badge is therefore the only
/// thing that tells the player which one to go and fix, which is why it is a pure function with
/// its own tests rather than a branch inside the page: the list row and the detail pane must
/// answer the same string for the same quest, and before this existed the detail pane read the
/// literal "Level" for every one of them.
/// </para>
/// </summary>
internal static class QuestRequirementBadge
{
    /// <summary>
    /// The badge for <paramref name="task"/> under <paramref name="settings"/>, or null when no
    /// gate of this kind is unmet.
    /// <para>
    /// The precedence is the status walk's own order, so the badge names the gate the engine
    /// stopped at: level, then karma, then loyalty. Anything else would point the player at a
    /// requirement that is not the next one they have to clear.
    /// </para>
    /// </summary>
    /// <param name="traderDisplayName">
    /// The trader's name in the app's language, given the requirement row. Injected rather than
    /// looked up here so this stays pure and testable; the page passes the localization service's
    /// own resolver.
    /// </param>
    internal static string? For(
        TarkovTask task,
        ProfileSettingsSnapshot settings,
        Func<QuestTraderRequirement, string> traderDisplayName)
    {
        if (task.RequiredLevel.HasValue
            && !QuestProgressService.IsLevelRequirementMet(task, settings))
        {
            return $"Lv.{task.RequiredLevel}";
        }

        if (task.RequiredScavKarma.HasValue
            && !QuestProgressService.IsScavKarmaRequirementMet(task, settings))
        {
            return $"Rep {task.RequiredScavKarma:0.#}";
        }

        var loyalty = QuestProgressService.FirstUnmetTraderLoyalty(task, settings);
        if (loyalty != null)
        {
            // The trader's name only when it is NOT the quest's own: eighty-nine of the
            // ninety-four gated quests name their own trader, whose initial the row already
            // shows, so "LL2" is complete on its own there and stays as narrow as "Lv.15". The
            // five that name someone else get the name, because "LL2" alone on a Skier quest
            // would send the player to raise Skier for nothing.
            return QuestProgressService.IsGivenBy(task, loyalty)
                ? $"LL{loyalty.Level}"
                : $"{traderDisplayName(loyalty)} LL{loyalty.Level}";
        }

        return null;
    }
}
