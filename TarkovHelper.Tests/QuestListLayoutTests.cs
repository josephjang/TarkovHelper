using TarkovHelper.Pages;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the quest page's detail-panel width rule (see
/// feature-quest-overview-filters.spec.md): the persisted width is held inside the
/// settings bounds AND capped to what the current window can show. This is the unit
/// cover for the splitter save/restore path the spec records as not e2e-automatable:
/// dragging an 8px transparent GridSplitter with a real mouse is too brittle for UIA.
/// </summary>
public sealed class QuestListLayoutTests
{
    // The real page geometry: the list column's MinWidth and the splitter's Width from
    // QuestListPage.xaml, and the bounds QuestListSettings clamps saves to.
    private const double ListMinWidth = 300;
    private const double SplitterWidth = 8;

    private static double Clamp(double requested, double pageWidth)
        => QuestListLayout.ClampDetailPanelWidth(
            requested, pageWidth, ListMinWidth, SplitterWidth,
            QuestListSettings.MinDetailPanelWidth, QuestListSettings.MaxDetailPanelWidth);

    [Fact]
    public void A_width_that_fits_is_applied_unchanged()
    {
        Assert.Equal(350, Clamp(350, pageWidth: 1600));
    }

    [Fact]
    public void A_width_saved_on_a_wide_window_is_capped_to_fit_a_narrow_one()
    {
        // 800 saved while maximized on an ultrawide; relaunched at 1024 the panel must
        // leave the 300px list and the 8px splitter reachable, or the user cannot drag
        // it back: 1024 - 300 - 8 = 716.
        Assert.Equal(716, Clamp(800, pageWidth: 1024));
    }

    [Fact]
    public void The_settings_maximum_bounds_a_larger_request()
    {
        // A drag beyond the persisted maximum is held at it, so what the user can reach
        // and what survives a restart are the same number.
        Assert.Equal(QuestListSettings.MaxDetailPanelWidth, Clamp(1400, pageWidth: 3840));
    }

    [Fact]
    public void The_settings_minimum_bounds_a_smaller_request()
    {
        Assert.Equal(QuestListSettings.MinDetailPanelWidth, Clamp(10, pageWidth: 1600));
    }

    [Fact]
    public void A_window_too_narrow_for_both_columns_keeps_the_panel_at_its_minimum()
    {
        // 700 - 300 - 8 = 392 of usable space, but a 250 minimum is not negotiable:
        // the panel stays usable and WPF clips, rather than collapsing to nothing.
        Assert.Equal(QuestListSettings.MinDetailPanelWidth, Clamp(200, pageWidth: 700));

        // Even when the available space drops below the minimum entirely.
        Assert.Equal(QuestListSettings.MinDetailPanelWidth, Clamp(350, pageWidth: 500));
    }

    [Fact]
    public void An_unmeasured_page_applies_the_bounded_width_without_capping()
    {
        // ActualWidth is 0 before the first layout pass (restore runs in Loaded), which
        // must not be read as "no space available" and collapse the panel.
        Assert.Equal(350, Clamp(350, pageWidth: 0));
    }

    [Fact]
    public void The_default_width_survives_a_round_trip_at_the_default_window_size()
    {
        Assert.Equal(QuestListSettings.DefaultDetailPanelWidth,
            Clamp(QuestListSettings.DefaultDetailPanelWidth, pageWidth: 1280));
    }
}
