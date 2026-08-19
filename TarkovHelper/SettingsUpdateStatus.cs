using TarkovHelper.Services;

namespace TarkovHelper;

/// <summary>
/// Pure text for the Settings overlay's update status line. Kept free of WPF types so it is
/// unit-testable (same pattern as <see cref="HeaderUpdatePill"/>, which words the title-bar
/// pill for the same two cases); MainWindow pairs the result with the status colour, which
/// stays a <see cref="UpdateStatusKind"/> decision.
/// </summary>
public static class SettingsUpdateStatus
{
    /// <summary>
    /// The status line for <see cref="UpdateStatusKind.UpdateAvailable"/>, the one status whose
    /// wording depends on more than the kind.
    /// </summary>
    /// <param name="loc">Localization source for the current UI language.</param>
    /// <param name="isSuperseded">
    /// Whether this build has been left behind by a newer data format. Then the update is not
    /// optional maintenance but the only thing that restores game data updates, so the status
    /// says that. The status vocabulary itself is unchanged: the kind stays UpdateAvailable and
    /// keeps its warning colour, so the pinned <see cref="UpdateService.GetStatusKind"/> oracle
    /// is untouched.
    /// </param>
    public static string AvailableText(LocalizationService loc, bool isSuperseded)
    {
        ArgumentNullException.ThrowIfNull(loc);

        return isSuperseded ? loc.UpdateStatusDataEnded : loc.UpdateStatusAvailable;
    }
}
