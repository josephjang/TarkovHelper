namespace TarkovHelper.Services;

/// <summary>
/// The reset fence's boundary rule (PRD R6 of feature-complete-profile-reset.md), in one place.
/// A log event is fenced out when it is not after the owning profile's reset watermark: it
/// describes progress the player deliberately removed, and the game retains their session logs
/// for days, so without the fence the next sync re-imports exactly what was just reset.
/// <para>
/// "Not after" is the boundary, so an event stamped exactly at the reset moment is fenced out
/// too. A profile that was never reset (null watermark) fences nothing.
/// </para>
/// <para>
/// Both fences call in here so they cannot drift apart: the scan-time fence in
/// <see cref="LogSyncService"/> uses the predicate and its complement in the same breath (the
/// events it counts and the events it keeps must partition the input exactly), and the
/// apply-time fence in <see cref="QuestProgressService"/> must agree with the count the player
/// was shown. Hand entry never reaches this predicate at all: only a log event carries a log
/// timestamp, and hand entry is never fenced (PRD R6).
/// </para>
/// </summary>
internal static class ResetFence
{
    /// <summary>
    /// Whether an event stamped <paramref name="eventTimestamp"/> falls behind the owning
    /// profile's <paramref name="resetAt"/> watermark and must be ignored. A null
    /// <paramref name="resetAt"/> means the profile was never reset, which fences nothing.
    /// </summary>
    public static bool IsFencedOut(DateTime eventTimestamp, DateTime? resetAt)
        => resetAt.HasValue && eventTimestamp <= resetAt.Value;
}
