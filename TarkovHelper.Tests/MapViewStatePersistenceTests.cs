using TarkovHelper.Models;
using TarkovHelper.Services.Map;
using static TarkovHelper.Services.Map.MapViewStatePersistence;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the pure decision core of the map view-state persistence
/// (see the feature-persist-map-view-state PRD): which map to show on first load
/// (raid > saved > default), raid-liveness detection, raid identity, and saved
/// zoom/pan normalization.
/// </summary>
public sealed class MapViewStatePersistenceTests
{
    // MinZoom/MaxZoom resolve to MapViewStatePersistence's public constants via the
    // `using static` import, asserting against the real bounds, not a copy that
    // could silently drift from the app's.

    private static readonly string[] Maps = { "Woods", "Customs", "Factory" };

    private static EftRaidInfo Raid(RaidState state, string? mapKey = "Customs") =>
        new() { State = state, MapKey = mapKey, RaidType = RaidType.PMC };

    #region DecideInitialMap

    [Fact]
    public void Saved_map_is_chosen_when_no_raid_is_live()
    {
        var choice = DecideInitialMap("Customs", Maps, activeRaidMapKey: null);

        Assert.NotNull(choice);
        Assert.Equal("Customs", choice!.MapKey);
        Assert.Equal(MapChoiceSource.Saved, choice.Source);
    }

    [Fact]
    public void Saved_map_matches_case_insensitively_and_returns_the_canonical_key()
    {
        var choice = DecideInitialMap("cUsToMs", Maps, activeRaidMapKey: null);

        // The canonical config key, not the saved spelling: combo Tag lookups are exact.
        Assert.Equal("Customs", choice!.MapKey);
        Assert.Equal(MapChoiceSource.Saved, choice.Source);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MapRemovedFromConfigs")]
    public void Missing_or_unknown_saved_map_falls_back_to_the_first_map(string? savedKey)
    {
        var choice = DecideInitialMap(savedKey, Maps, activeRaidMapKey: null);

        Assert.Equal("Woods", choice!.MapKey);
        Assert.Equal(MapChoiceSource.Default, choice.Source);
    }

    [Fact]
    public void Live_raid_map_beats_the_saved_map()
    {
        var choice = DecideInitialMap("Customs", Maps, activeRaidMapKey: "Factory");

        Assert.Equal("Factory", choice!.MapKey);
        Assert.Equal(MapChoiceSource.ActiveRaid, choice.Source);
    }

    [Fact]
    public void Raid_map_key_is_canonicalized_too()
    {
        var choice = DecideInitialMap(null, Maps, activeRaidMapKey: "factory");

        Assert.Equal("Factory", choice!.MapKey);
        Assert.Equal(MapChoiceSource.ActiveRaid, choice.Source);
    }

    [Fact]
    public void Unknown_raid_map_is_ignored_and_the_saved_map_wins()
    {
        var choice = DecideInitialMap("Customs", Maps, activeRaidMapKey: "NotAConfiguredMap");

        Assert.Equal("Customs", choice!.MapKey);
        Assert.Equal(MapChoiceSource.Saved, choice.Source);
    }

    [Fact]
    public void Empty_map_list_returns_null()
    {
        Assert.Null(DecideInitialMap("Customs", Array.Empty<string>(), "Customs"));
    }

    #endregion

    #region GetActiveRaidMapKey

    [Fact]
    public void Null_raid_is_not_live()
    {
        Assert.Null(GetActiveRaidMapKey(null));
    }

    [Theory]
    [InlineData(RaidState.Idle)]
    [InlineData(RaidState.Ended)]
    public void Idle_and_ended_raids_are_not_live(RaidState state)
    {
        Assert.Null(GetActiveRaidMapKey(Raid(state)));
    }

    [Theory]
    [InlineData(RaidState.Matching)]
    [InlineData(RaidState.Connecting)]
    [InlineData(RaidState.InRaid)]
    public void Matching_connecting_and_inraid_raids_are_live(RaidState state)
    {
        Assert.Equal("Customs", GetActiveRaidMapKey(Raid(state)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Live_raid_without_a_map_key_yields_null(string? mapKey)
    {
        Assert.Null(GetActiveRaidMapKey(Raid(RaidState.InRaid, mapKey)));
    }

    #endregion

    #region GetRaidIdentity

    [Fact]
    public void Null_raid_has_no_identity()
    {
        Assert.Null(GetRaidIdentity(null));
    }

    [Fact]
    public void Raid_id_is_preferred_as_the_identity()
    {
        var raid = Raid(RaidState.InRaid);
        raid.RaidId = "raid-1";
        raid.SessionId = "session-1";
        raid.StartTime = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal("raid-1", GetRaidIdentity(raid));
    }

    [Fact]
    public void Session_id_is_used_when_the_raid_id_is_missing()
    {
        var raid = Raid(RaidState.InRaid);
        raid.SessionId = "session-1";

        Assert.Equal("session-1", GetRaidIdentity(raid));
    }

    [Fact]
    public void Start_time_is_the_last_resort_identity()
    {
        var raid = Raid(RaidState.InRaid);
        var start = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        raid.StartTime = start;

        Assert.Equal(start.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GetRaidIdentity(raid));
    }

    [Fact]
    public void Raid_without_any_identifier_yields_null()
    {
        // Null identity means "cannot prove it's a new raid": reconciliation preserves
        // the trail rather than clearing it.
        Assert.Null(GetRaidIdentity(Raid(RaidState.InRaid)));
    }

    [Fact]
    public void Two_raids_with_different_ids_are_distinct()
    {
        var first = Raid(RaidState.InRaid);
        first.RaidId = "raid-1";
        var second = Raid(RaidState.InRaid);
        second.RaidId = "raid-2";

        Assert.NotEqual(GetRaidIdentity(first), GetRaidIdentity(second));
    }

    #endregion

    #region NormalizeSavedView

    [Fact]
    public void Valid_view_round_trips()
    {
        var view = NormalizeSavedView(1.5, -320.25, 48.0, MinZoom, MaxZoom);

        Assert.NotNull(view);
        Assert.Equal(1.5, view!.ZoomLevel);
        Assert.Equal(-320.25, view.TranslateX);
        Assert.Equal(48.0, view.TranslateY);
    }

    [Fact]
    public void Zero_translate_is_a_legitimate_pan()
    {
        var view = NormalizeSavedView(1.0, 0.0, 0.0, MinZoom, MaxZoom);

        Assert.NotNull(view);
        Assert.Equal(0.0, view!.TranslateX);
        Assert.Equal(0.0, view.TranslateY);
    }

    [Theory]
    [InlineData(0.01, MinZoom)]  // below range (e.g. hand-edited db value)
    [InlineData(0.0, MinZoom)]
    [InlineData(99.0, MaxZoom)]  // above range
    public void Out_of_range_zoom_is_clamped(double savedZoom, double expected)
    {
        var view = NormalizeSavedView(savedZoom, 10, 10, MinZoom, MaxZoom);

        Assert.Equal(expected, view!.ZoomLevel);
    }

    [Theory]
    [InlineData(double.NaN, 0, 0)]
    [InlineData(1.0, double.NaN, 0)]
    [InlineData(1.0, 0, double.NaN)]
    [InlineData(double.PositiveInfinity, 0, 0)]
    [InlineData(1.0, double.NegativeInfinity, 0)]
    [InlineData(1.0, 0, double.PositiveInfinity)]
    public void Non_finite_values_reject_the_whole_view(double zoom, double tx, double ty)
    {
        Assert.Null(NormalizeSavedView(zoom, tx, ty, MinZoom, MaxZoom));
    }

    #endregion
}
