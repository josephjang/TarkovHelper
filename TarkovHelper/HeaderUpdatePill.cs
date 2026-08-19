using TarkovHelper.Services;

namespace TarkovHelper;

/// <summary>
/// How loudly the title-bar update pill should read. Semantic rather than a colour, so the
/// state that picks the wording picks the urgency with it and MainWindow only renders the
/// result, the same division <see cref="HeaderSyncStatus"/> uses for the sibling chip.
/// </summary>
public enum HeaderUpdatePillTone
{
    /// <summary>Optional maintenance: the reassuring green install pill.</summary>
    Success,

    /// <summary>
    /// Installing is the only thing that restores this build's game-data updates, so the
    /// pill trades green for amber.
    /// </summary>
    Warning
}

/// <summary>
/// The text the title-bar update pill publishes for one available update, and how urgently
/// it should read.
/// </summary>
/// <param name="Label">The words printed on the pill.</param>
/// <param name="Description">
/// The sentence behind the pill: shown as its tooltip and published as its UIA HelpText.
/// </param>
/// <param name="Tone">
/// The urgency MainWindow renders to the pill's brushes. Decided here, beside the wording it
/// has to agree with, so a new pill state cannot ship escalated words in the reassuring green.
/// </param>
public readonly record struct HeaderUpdatePillText(
    string Label, string Description, HeaderUpdatePillTone Tone)
{
    /// <summary>
    /// The pill's UIA Name, which is the visible label verbatim. WCAG 2.5.3 (Label in Name)
    /// requires the accessible name to contain the visible words, so speech input can
    /// activate the pill by reading it aloud and a screen reader announces a button instead
    /// of a paragraph. <see cref="Description"/> is a whole sentence and belongs in
    /// HelpText, never here.
    /// </summary>
    public string AutomationName => Label;

    /// <summary>The pill's UIA HelpText: the explanation that does not fit in a name.</summary>
    public string HelpText => Description;
}

/// <summary>
/// Pure text for the title-bar update pill. Kept free of WPF types so it is unit-testable
/// (same pattern as <see cref="HeaderLayout"/>); MainWindow applies the result to the
/// button's label, tooltip and automation properties.
/// </summary>
public static class HeaderUpdatePill
{
    /// <summary>
    /// Build the pill text for an available update.
    /// </summary>
    /// <param name="loc">Localization source for the current UI language.</param>
    /// <param name="version">Display version of the available update, e.g. "v2026.8.0".</param>
    /// <param name="isSuperseded">
    /// Whether this build has been left behind by a newer data format. Then the update is
    /// not optional maintenance but the only thing that restores game data updates, so the
    /// pill says that instead of naming a version the user has learned to ignore.
    /// </param>
    public static HeaderUpdatePillText For(LocalizationService loc, string version, bool isSuperseded)
    {
        ArgumentNullException.ThrowIfNull(loc);

        return isSuperseded
            ? new HeaderUpdatePillText(
                loc.HeaderUpdateForDataLabel,
                string.Format(loc.HeaderUpdateForDataTooltipFormat, version),
                HeaderUpdatePillTone.Warning)
            : new HeaderUpdatePillText(
                string.Format(loc.HeaderUpdateAvailableFormat, version),
                string.Format(loc.HeaderVersionTooltipInstall, version),
                HeaderUpdatePillTone.Success);
    }
}
