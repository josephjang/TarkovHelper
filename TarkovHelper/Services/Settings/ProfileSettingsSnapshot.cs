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
/// The <see cref="ProfileChangedEventArgs.Revision"/> this snapshot was loaded for: provenance,
/// not a gate. Staleness is decided against <c>SettingsService._latestRevision</c>, which is the
/// newest revision ANNOUNCED rather than the newest one published, and a reload that carries no
/// transition of its own (<c>SettingsService.ReloadAfterExternalWrite</c>) is gated on profile
/// identity instead and carries this value forward untouched. So a load must never compare
/// against this field: it says which transition the values on it were read for, which is what a
/// log line or a debugger needs to explain a publish after the fact.
/// </param>
/// <param name="PlayerLevel">Stored player level, or null when the profile has no row for it.</param>
/// <param name="ScavRep">Stored Fence reputation, or null when unset.</param>
/// <param name="ShowLevelLockedQuests">Stored level-locked quest visibility, or null when unset.</param>
/// <param name="DspDecodeCount">Stored DSP decode count, or null when unset.</param>
/// <param name="PlayerFaction">
/// Stored faction, always lower case ("bear"/"usec"): every writer lower-cases it
/// (<see cref="LegacyAppSettingsValues.PlayerFaction"/> for the two legacy importers, the
/// <c>PlayerFaction</c> setter for hand entry) and <see cref="From"/> lower-cases what it reads,
/// so a row an older build stored as "USEC" is repaired on the way in rather than reaching
/// <c>QuestListPage.LoadFactionSelection</c>, which compares ordinally and would select neither
/// radio button. Null means "no faction chosen", which is a real value here rather than a missing
/// one, so it has no default below.
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

    /// <summary>
    /// One profile's stored rows parsed into a snapshot, per key, with exactly the fallbacks the
    /// eight separate reads used before this type existed: an absent row and an unparsable one
    /// both leave the field null, which is what makes the property answer its default.
    /// <para>
    /// Every bounded value is CLAMPED here, not only parsed. The setters have always clamped, so
    /// no value the app itself wrote can be out of range; a row can still be, because
    /// ProfileSettings is a plain SQLite table a player can hand edit, because the legacy
    /// app_settings.json import carried its numbers through unchecked for years, and because an
    /// older build could mis-read its own comma-decimal scav rep. This read is the one place all
    /// of those funnel through, and the values fan out from here into quest filtering
    /// (QuestProgressService compares player level, prestige, scav karma and DSP decode count
    /// against task requirements) and into the settings panel's own bounded controls.
    /// </para>
    /// <para>
    /// Beside <see cref="Defaults"/> rather than on <see cref="SettingsService"/>, the way
    /// <c>ProgressSnapshot.From</c> sits beside <c>ProgressSnapshot.Empty</c>: reading a row set
    /// is the inverse of this record's own shape, and nothing about it needs the service's
    /// instance state. The key names it reads under are the service's, because the writers that
    /// produce those rows are.
    /// </para>
    /// </summary>
    /// <param name="values">
    /// One profile's rows keyed exactly as stored, ordinal: the ProfileSettings table has no
    /// COLLATE NOCASE, so a row differing only in case is a different row and not this setting.
    /// </param>
    internal static ProfileSettingsSnapshot From(
        string profileId, long revision, IReadOnlyDictionary<string, string> values)
    {
        string? Value(string key) => values.TryGetValue(key, out var stored) ? stored : null;

        var faction = Value(SettingsService.KeyPlayerFaction);

        return new ProfileSettingsSnapshot(
            profileId,
            revision,
            // Clamped like the three below it: a stored level of 9999 answers every quest's level
            // requirement, so the whole list reads as unlocked.
            PlayerLevel: int.TryParse(Value(SettingsService.KeyPlayerLevel), out var level)
                ? Math.Clamp(level, SettingsService.MinPlayerLevel, SettingsService.MaxPlayerLevel)
                : null,
            // The one double among the eight, so the one key that needs SettingsValue: it reads
            // the invariant format first and falls back to the current culture for rows written
            // before that convention reached this service. Clamped, because a row this cannot
            // vouch for (a hand edit, a legacy comma-decimal write read under another locale)
            // otherwise reaches Fence karma quest filtering unbounded.
            ScavRep: SettingsValue.TryParseDouble(Value(SettingsService.KeyScavRep), out var scavRep)
                ? Math.Clamp(scavRep, SettingsService.MinScavRep, SettingsService.MaxScavRep)
                : null,
            ShowLevelLockedQuests:
                bool.TryParse(Value(SettingsService.KeyShowLevelLockedQuests), out var showLocked) ? showLocked : null,
            // Clamped: the Make Amends branches are selected by an EXACT match against this
            // count (QuestProgressService.IsDspRequirementMet), so an out-of-range row matches no
            // branch at all and silently locks every one of them.
            DspDecodeCount:
                int.TryParse(Value(SettingsService.KeyDspDecodeCount), out var dspCount)
                    ? Math.Clamp(dspCount, SettingsService.MinDspDecodeCount, SettingsService.MaxDspDecodeCount)
                    : null,
            // Lower cased on the way in, not merely on the way out: a row an older build wrote in
            // the file's own casing ("USEC") otherwise matches neither of the ordinal comparisons
            // QuestListPage.LoadFactionSelection makes, so the faction radio reads as unset while
            // quest filtering (case-insensitive) still hides the other faction's quests.
            PlayerFaction: string.IsNullOrEmpty(faction) ? null : faction.ToLowerInvariant(),
            HasEodEdition:
                bool.TryParse(Value(SettingsService.KeyHasEodEdition), out var hasEod) ? hasEod : null,
            HasUnheardEdition:
                bool.TryParse(Value(SettingsService.KeyHasUnheardEdition), out var hasUnheard) ? hasUnheard : null,
            PrestigeLevel:
                int.TryParse(Value(SettingsService.KeyPrestigeLevel), out var prestige)
                    ? Math.Clamp(prestige, SettingsService.MinPrestigeLevel, SettingsService.MaxPrestigeLevel)
                    : null);
    }

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
