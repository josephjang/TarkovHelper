namespace TarkovHelper.Services.Settings;

/// <summary>
/// One profile's player settings, together with the identity of the profile they belong to and
/// the transition they were loaded for, as a single immutable value.
/// <para>
/// These eight values used to be eight independent nullable fields whose partition key was
/// "whatever <see cref="ProfileService"/> currently reports". Each field was filled by its own
/// query against the selection as it stood at that moment, so a profile switch landing between
/// two of the reads left the cache holding one profile's level beside another's faction, with
/// nothing able to detect the mixture; and because <see cref="ProfileService.ActiveProfileChanged"/>
/// is raised outside the publisher's lock, the handler for the OLDER transition could finish last
/// and park a complete but stale set under the newer selection. Binding the values, the profile
/// and the revision into one object that is only ever replaced by a single reference swap makes
/// both states impossible to observe: a reader captures the field once and sees values that
/// belong together, and a writer persists under the ProfileId of the very snapshot it derived its
/// edit from. See docs/decisions/fix-profile-settings-race.spec.md.
/// </para>
/// </summary>
/// <param name="ProfileId">Storage partition these values came from and are written back to.</param>
/// <param name="Revision">
/// The <see cref="ProfileChangedEventArgs.Revision"/> this snapshot was loaded for, so a reload
/// that lost a race can tell it is stale.
/// </param>
/// <param name="PlayerLevel">Stored player level, or null when the profile has no row for it.</param>
/// <param name="ScavRep">Stored Fence reputation, or null when unset.</param>
/// <param name="ShowLevelLockedQuests">Stored level-locked quest visibility, or null when unset.</param>
/// <param name="DspDecodeCount">Stored DSP decode count, or null when unset.</param>
/// <param name="PlayerFaction">
/// Stored faction ("bear"/"usec"), taken from the row as it stands. The setter lower-cases what
/// it writes, but the legacy JSON migration does not, so casing is not guaranteed and every
/// comparison against it (<see cref="SettingsService.ShouldIncludeTask"/>) is case-insensitive.
/// Null means "no faction chosen", which is a real value here rather than a missing one, so it
/// has no default below.
/// </param>
/// <param name="HasEodEdition">Stored Edge of Darkness ownership, or null when unset.</param>
/// <param name="HasUnheardEdition">Stored The Unheard ownership, or null when unset.</param>
/// <param name="PrestigeLevel">Stored prestige level, or null when unset.</param>
internal sealed record ProfileSettingsSnapshot(
    string ProfileId,
    long Revision,
    int? PlayerLevel,
    double? ScavRep,
    bool? ShowLevelLockedQuests,
    int? DspDecodeCount,
    string? PlayerFaction,
    bool? HasEodEdition,
    bool? HasUnheardEdition,
    int? PrestigeLevel)
{
    /// <summary>
    /// A snapshot naming a profile none of whose rows are known: every value null, so every
    /// getter answers its default. This is what an unreadable store publishes, deliberately
    /// under the NEW profile's name, because leaving the previous profile's values on screen
    /// under a different profile's name is the defect this type exists to remove.
    /// </summary>
    internal static ProfileSettingsSnapshot Defaults(string profileId, long revision)
        => new(profileId, revision, null, null, null, null, null, null, null, null);

    // "No stored row means the property answers its default" has one home per value, below,
    // rather than one at the property getter and another wherever the changed events are
    // raised from a snapshot. The four named constants live on SettingsService because they
    // are part of its public surface (the settings UI clamps and labels against them).

    /// <summary>Player level as callers see it.</summary>
    internal int PlayerLevelOrDefault => PlayerLevel ?? SettingsService.DefaultPlayerLevel;

    /// <summary>Scav reputation as callers see it.</summary>
    internal double ScavRepOrDefault => ScavRep ?? SettingsService.DefaultScavRep;

    /// <summary>Level-locked quest visibility as callers see it; shown unless stored otherwise.</summary>
    internal bool ShowLevelLockedQuestsOrDefault => ShowLevelLockedQuests ?? true;

    /// <summary>DSP decode count as callers see it.</summary>
    internal int DspDecodeCountOrDefault => DspDecodeCount ?? SettingsService.DefaultDspDecodeCount;

    /// <summary>Edge of Darkness ownership as callers see it; not owned unless stored otherwise.</summary>
    internal bool HasEodEditionOrDefault => HasEodEdition ?? false;

    /// <summary>The Unheard ownership as callers see it; not owned unless stored otherwise.</summary>
    internal bool HasUnheardEditionOrDefault => HasUnheardEdition ?? false;

    /// <summary>Prestige level as callers see it.</summary>
    internal int PrestigeLevelOrDefault => PrestigeLevel ?? SettingsService.DefaultPrestigeLevel;
}
