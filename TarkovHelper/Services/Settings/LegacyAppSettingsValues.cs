using System.Globalization;

namespace TarkovHelper.Services.Settings;

/// <summary>
/// The value transforms every reader of the legacy <c>app_settings.json</c> owes the store: clamp
/// to the setting's own bounds, round where the setter rounds, normalise where the setter
/// normalises, and format culture-safely.
/// <para>
/// There are two such readers and there always will be, because they answer different questions:
/// <c>SettingsService.MigrateFromJsonIfNeeded</c> imports the file this install left next to its
/// own executable on startup, and <c>ConfigMigrationService.MigrateAppSettingsAsync</c> imports
/// one out of a Config folder the player points at by hand. What they must NOT differ in is what
/// a given JSON value becomes as a row, and they did: the startup reader stored the faction in
/// whatever case the file spelled it (a stored "USEC" matches no radio button in
/// <c>QuestListPage.LoadFactionSelection</c>, which compares ordinally, while quest filtering
/// still honours it case-insensitively - so the player sees no faction selected and a filtered
/// list), it clamped no sync range at all, and it clamped the scav rep without rounding it, so it
/// could write a 2.37 no setter could ever produce.
/// </para>
/// <para>
/// Pure functions over one value each, deliberately: the two readers keep their own policies for
/// what to do with the result (which table, which partition, whether to count it, how to report a
/// failure), which is the part that legitimately differs.
/// </para>
/// <para>
/// This is only the value half. The FILE's shape - which JSON property names exist at all - is
/// pinned separately, by <c>ConfigMigrationProfileAttributionTests</c> scanning
/// <c>SettingsService.LegacyAppSettings</c> for a matching case arm in the other reader.
/// </para>
/// </summary>
internal static class LegacyAppSettingsValues
{
    /// <summary>Player level as a row: clamped to the levels the game has.</summary>
    internal static string PlayerLevel(int value)
        => Clamped(value, SettingsService.MinPlayerLevel, SettingsService.MaxPlayerLevel);

    /// <summary>
    /// Fence reputation as a row: clamped, then rounded to one decimal like the
    /// <c>SettingsService.ScavRep</c> setter, then written in the invariant format so a file
    /// imported on a comma-decimal machine does not read back as a different number.
    /// </summary>
    internal static string ScavRep(double value)
        => SettingsValue.FormatDouble(
            Math.Round(Math.Clamp(value, SettingsService.MinScavRep, SettingsService.MaxScavRep), 1));

    /// <summary>
    /// Level-locked quest visibility as a row: "True"/"False", the spelling
    /// <c>ProfileSettingsSnapshot.From</c> parses and the setter writes.
    /// </summary>
    internal static string ShowLevelLockedQuests(bool value) => value.ToString();

    /// <summary>DSP decode count as a row: clamped to the Make Amends branches that exist.</summary>
    internal static string DspDecodeCount(int value)
        => Clamped(value, SettingsService.MinDspDecodeCount, SettingsService.MaxDspDecodeCount);

    /// <summary>
    /// The player faction as a row, or null when the file names none. Lower cased, because that
    /// is the only spelling the whole app agrees on: the setter writes it, the quest list's radio
    /// buttons compare against it ordinally, and an empty string means "no faction chosen" rather
    /// than a faction named "".
    /// </summary>
    internal static string? PlayerFaction(string? value)
        => string.IsNullOrEmpty(value) ? null : value.ToLowerInvariant();

    /// <summary>
    /// Base font size as a row: clamped and invariant-formatted. An out-of-range or
    /// comma-decimal row reaches <c>Resources["BaseFontSize"]</c> in App.xaml.cs and renders
    /// every control at that many pixels.
    /// </summary>
    internal static string BaseFontSize(double value)
        => SettingsValue.FormatDouble(
            Math.Clamp(value, SettingsService.MinFontSize, SettingsService.MaxFontSize));

    /// <summary>
    /// Log look-back window as a row: clamped to the range the setter accepts, where 0 means
    /// "all logs".
    /// </summary>
    internal static string SyncDaysRange(int value)
        => Clamped(value, SettingsService.MinSyncDaysRange, SettingsService.MaxSyncDaysRange);

    /// <summary>An int clamped and written the one way ints are written to this store.</summary>
    private static string Clamped(int value, int min, int max)
        => Math.Clamp(value, min, max).ToString(CultureInfo.InvariantCulture);
}
