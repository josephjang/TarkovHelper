using System.Windows.Media;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Pages;

/// <summary>
/// The fill behind a quest's status badge, one per status, shared by every surface that paints
/// one: the quest list's rows, its detail pane, its prerequisite and alternative rows, the Kappa
/// quest list window, and the Collector page's unlock panel. Moved out of the quest page so the
/// Collector page reads the same six rather than a copy that could drift a shade
/// (feature-kappa-collector-1-1.spec.md, TD1).
/// <para>
/// Frozen at construction. A frozen brush is immutable and may be read from any thread, so the
/// first page to touch this class does not tie the brushes to its own dispatcher, and a window
/// built later on another thread could still paint with them.
/// </para>
/// </summary>
internal static class QuestStatusBrushes
{
    internal static readonly Brush Locked = Frozen(102, 102, 102);
    internal static readonly Brush Active = Frozen(76, 175, 80);
    internal static readonly Brush Done = Frozen(33, 150, 243);
    internal static readonly Brush Failed = Frozen(244, 67, 54);

    /// <summary>
    /// Orange. Level, Scav karma and trader loyalty all resolve to this one status, so this is
    /// also the colour of an unmet Requirements line: the line and the badge above it say
    /// "still holding" in the same hue.
    /// </summary>
    internal static readonly Brush LevelLocked = Frozen(255, 152, 0);

    internal static readonly Brush Unavailable = Frozen(158, 158, 158);

    /// <summary>
    /// The fill for <paramref name="status"/>. Gray for a value the switch does not know, so a
    /// status added to the enum before it is added here still paints something rather than
    /// throwing in a render pass.
    /// </summary>
    internal static Brush For(QuestStatus status) => status switch
    {
        QuestStatus.Locked => Locked,
        QuestStatus.Active => Active,
        QuestStatus.Done => Done,
        QuestStatus.Failed => Failed,
        QuestStatus.LevelLocked => LevelLocked,
        QuestStatus.Unavailable => Unavailable,
        _ => Brushes.Gray
    };

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
