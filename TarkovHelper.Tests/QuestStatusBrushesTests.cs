using TarkovHelper.Models;
using TarkovHelper.Pages;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace TarkovHelper.Tests;

/// <summary>
/// The one set of status fills every badge on every page paints with, now that the Collector
/// page paints one too. A page with its own copy of six colours is a page whose badge can drift
/// a shade from the quest list's; the shared class is what makes that impossible.
/// </summary>
public sealed class QuestStatusBrushesTests
{
    private static readonly QuestStatus[] KnownStatuses =
    {
        QuestStatus.Locked, QuestStatus.Active, QuestStatus.Done,
        QuestStatus.Failed, QuestStatus.LevelLocked, QuestStatus.Unavailable,
    };

    [Fact]
    public void Every_known_status_has_its_own_brush()
    {
        var brushes = KnownStatuses.Select(QuestStatusBrushes.For).ToArray();

        Assert.Equal(KnownStatuses.Length, brushes.Distinct().Count());
        Assert.DoesNotContain(Brushes.Gray, brushes);
    }

    [Fact]
    public void Every_brush_is_frozen_so_any_thread_may_paint_with_it()
    {
        // A brush created unfrozen belongs to the dispatcher of the thread that created it; the
        // first page to touch the static class would then own every badge's fill.
        foreach (var status in KnownStatuses)
        {
            Assert.True(QuestStatusBrushes.For(status).IsFrozen, $"{status}'s brush is not frozen");
        }
    }

    [Fact]
    public void An_unknown_status_falls_back_to_gray_rather_than_throwing()
    {
        Brush brush = QuestStatusBrushes.For((QuestStatus)999);

        Assert.Same(Brushes.Gray, brush);
    }

    [Fact]
    public void The_unmet_requirement_colour_is_the_level_locked_badge_colour()
    {
        // The Requirements lines (on both pages) paint an unmet line with this brush, so the
        // line and the badge above it say "still holding" in one hue.
        Assert.Same(QuestStatusBrushes.LevelLocked, QuestStatusBrushes.For(QuestStatus.LevelLocked));
    }
}
