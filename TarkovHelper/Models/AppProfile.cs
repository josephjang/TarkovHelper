namespace TarkovHelper.Models;

/// <summary>
/// App-level progress destinations. PvP Zone and PvP Season share PvP game rules,
/// but their persisted progress must remain separate.
/// </summary>
public enum AppProfile
{
    PvpZone = 0,
    PveZone = 1,
    PvpSeason = 2
}

/// <summary>
/// Result of resolving log evidence against the currently selected app profile.
/// </summary>
public readonly record struct ProfileResolution(AppProfile Profile, bool DetectionApplied);
