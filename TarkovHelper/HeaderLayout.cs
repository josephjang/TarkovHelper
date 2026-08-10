namespace TarkovHelper;

/// <summary>
/// Density of the main-window header at the current window width.
/// </summary>
public enum HeaderLayoutMode
{
    /// <summary>Everything visible.</summary>
    Full,

    /// <summary>Sync-status chip shows only its colored dot; tab glyphs are hidden.</summary>
    Compact,

    /// <summary>Compact, plus the brand title is hidden.</summary>
    Minimal
}

/// <summary>
/// Pure width→mode mapping for the main-window header so it degrades gracefully
/// instead of clipping at narrow widths (window MinWidth is 600). Kept free of
/// WPF types so it is unit-testable; MainWindow applies the resulting mode.
/// </summary>
public static class HeaderLayout
{
    /// <summary>
    /// Below this width the sync-status text is hidden (dot + tooltip remain) and the
    /// tab glyphs are dropped — text-only tabs fit down to the window minimum.
    /// </summary>
    /// <remarks>
    /// Known limitation: this is a width-only decision, blind to language and to the user's
    /// BaseFontSize (10..28). Measured worst case is Japanese at the DEFAULT font size, where
    /// the title bar needs about 1001 px while Full mode already starts at 1000, and at base 28
    /// it needs about 1488 px. Fixing it properly means choosing the variant from the wide
    /// selector's measured DesiredSize against the available title-bar width, and re-running
    /// that from BaseFontSizeChanged and LanguageChanged (today ApplyHeaderLayout is reached
    /// only from Window_SizeChanged, so neither event re-evaluates the choice). Both halves have
    /// to land together, because re-subscribing alone changes nothing while GetMode stays
    /// width-only. The compact selector's labels use MinWidth + CharacterEllipsis so the
    /// current mechanism degrades visibly rather than silently.
    /// </remarks>
    public const double CompactThreshold = 1000;

    /// <summary>Below this width the brand title is hidden as well.</summary>
    public const double MinimalThreshold = 760;

    public static HeaderLayoutMode GetMode(double windowWidth)
    {
        if (windowWidth < MinimalThreshold) return HeaderLayoutMode.Minimal;
        if (windowWidth < CompactThreshold) return HeaderLayoutMode.Compact;
        return HeaderLayoutMode.Full;
    }
}
