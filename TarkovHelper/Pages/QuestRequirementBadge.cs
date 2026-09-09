using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Pages;

/// <summary>
/// The short text a quest's status badge carries: the status itself, and for the two statuses
/// that hide several causes behind one word, which gate is actually holding the quest and at what
/// value.
/// <para>
/// Level, Scav karma and trader loyalty all resolve to <see cref="QuestStatus.LevelLocked"/>, by
/// the recorded decision that the chip vocabulary stays as it is. The badge is therefore the only
/// thing that tells the player which one to go and fix, which is why it is a pure function with
/// its own tests rather than a branch inside the page: the list row, the detail pane, the
/// prerequisite rows and the alternative-quest rows must answer the same string for the same
/// quest, and before this existed the detail pane and the prerequisite rows read the literal
/// "Level" for every one of them.
/// </para>
/// <para>
/// It is a FORMATTER and nothing more. Which gate is holding a quest is decided once, by the
/// status walk in
/// <see cref="QuestProgressService.GetStatus(TarkovTask, ProgressSnapshot, ProfileSettingsSnapshot, out QuestGate)"/>,
/// and handed here as a <see cref="QuestGate"/>. This file used to re-walk the same predicates in
/// a hand-copied order with nothing tying the two orders together, so reordering the engine would
/// have left every test passing while the badge named a requirement the player did not have to
/// clear next.
/// </para>
/// </summary>
internal static class QuestRequirementBadge
{
    /// <summary>
    /// The badge for one quest the walk answered <paramref name="status"/> for, stopping at
    /// <paramref name="gate"/>: the specific gate for the two statuses that stand for several
    /// (<see cref="QuestStatus.LevelLocked"/> and <see cref="QuestStatus.Unavailable"/>), else
    /// the status word.
    /// <para>
    /// Every caller passes the task and the gate the same walk reported, so every pane that shows
    /// a badge shows the SAME badge. Both are required rather than optional for exactly that
    /// reason: the callers that omitted the task were not asking for a shorter badge, they were
    /// silently falling back to "Level" and "N/A" beside a row that named the gate.
    /// </para>
    /// </summary>
    /// <param name="settings">
    /// The one profile snapshot the whole render pass reads, so a publish landing mid-pass cannot
    /// give a quest a badge from one profile and a status from another.
    /// </param>
    internal static string StatusText(
        QuestStatus status,
        QuestGate gate,
        TarkovTask task,
        ProfileSettingsSnapshot settings,
        Func<QuestTraderRequirement, string> traderDisplayName)
    {
        var badge = For(gate, task, settings, traderDisplayName);
        if (badge != null) return badge;

        // No gate to name: the four single-cause statuses, and the defensive tail where a caller
        // hands a status the walk did not derive. The badge never goes blank.
        return status switch
        {
            QuestStatus.Locked => "Locked",
            QuestStatus.Active => "Active",
            QuestStatus.Done => "Done",
            QuestStatus.Failed => "Failed",
            QuestStatus.LevelLocked => "Level",
            QuestStatus.Unavailable => "N/A",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// The value behind <paramref name="gate"/> for <paramref name="task"/>, or null when the
    /// gate names no requirement the player can read off a badge (<see cref="QuestGate.None"/>,
    /// and the two gates whose cause is a whole quest rather than a value).
    /// <para>
    /// A switch, not a walk: the precedence that decides WHICH gate this is asked about belongs
    /// to the status engine.
    /// </para>
    /// </summary>
    /// <param name="traderDisplayName">
    /// The trader's name in the app's language, given the requirement row. Injected rather than
    /// looked up here so this stays pure and testable; the page passes the localization service's
    /// own resolver.
    /// </param>
    internal static string? For(
        QuestGate gate,
        TarkovTask task,
        ProfileSettingsSnapshot settings,
        Func<QuestTraderRequirement, string> traderDisplayName)
    {
        switch (gate)
        {
            case QuestGate.PlayerLevel:
                return $"Lv.{task.RequiredLevel}";

            case QuestGate.ScavKarma:
                // A "bad karma" quest wants a rep at MOST the value; the badge shows the value
                // either way, sign included.
                return $"Rep {task.RequiredScavKarma:0.#}";

            case QuestGate.TraderLoyalty:
            {
                var loyalty = QuestProgressService.FirstUnmetTraderLoyalty(task, settings);
                if (loyalty == null) return null;

                // The trader's name only when it is NOT the quest's own: eighty-nine of the
                // ninety-four gated quests name their own trader, whose initial the row already
                // shows, so "LL2" is complete on its own there and stays as narrow as "Lv.15".
                // The five that name someone else get the name, because "LL2" alone on a Skier
                // quest would send the player to raise Skier for nothing.
                return QuestProgressService.IsGivenBy(task, loyalty)
                    ? $"LL{loyalty.Level}"
                    : $"{traderDisplayName(loyalty)} LL{loyalty.Level}";
            }

            case QuestGate.RequiredEdition:
            {
                var requiredEdition = task.RequiredEdition?.ToLowerInvariant();
                if (requiredEdition == "eod" || requiredEdition == "edge_of_darkness")
                    return "EOD";
                if (requiredEdition == "unheard" || requiredEdition == "the_unheard")
                    return "Unheard";
                return null;
            }

            case QuestGate.ExcludedEdition:
                // The player owns an edition the quest bars, so neither edition name would be
                // right and the badge says which KIND of gate it is.
                return "Edition";

            case QuestGate.PrestigeLevel:
                return $"P.{task.RequiredPrestigeLevel}";

            case QuestGate.Faction:
            {
                var faction = task.Faction?.ToLowerInvariant();
                if (faction == "bear") return "BEAR";
                if (faction == "usec") return "USEC";
                return null;
            }

            default:
                return null;
        }
    }
}
