namespace TarkovHelper.Services;

/// <summary>
/// Collector page strings. The page had no strings of its own until its unlock panel: its
/// filter row, column headers and item detail are English literals from before the app's
/// localization pass and stay so (feature-kappa-collector-1-1.md, Non-Goals). What this phase
/// adds or changes ships in all three languages, all of it (PD4, TD7). The Kappa strings the
/// panel shares with the quest tab's detail pane live in LocalizationService.Quest.cs.
/// </summary>
public partial class LocalizationService
{
    #region Collector page

    /// <summary>The unlock panel's heading, above Collector's status badge and its condition lines.</summary>
    public string CollectorUnlockHeading => CurrentLanguage switch
    {
        AppLanguage.KO => "Collector 해금",
        AppLanguage.JA => "Collector 解放",
        _ => "Collector unlock"
    };

    #endregion
}
