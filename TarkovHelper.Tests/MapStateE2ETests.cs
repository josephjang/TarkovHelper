using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TarkovHelper.Tests;

/// <summary>
/// End-to-end tests for map view-state persistence (see the
/// feature-persist-map-view-state PRD): launch the real app via the shared
/// <see cref="AppDriver"/> harness, drive the tab bar and read the map combo through
/// UI Automation, and assert the persisted user_data.db values.
///
/// Coverage gaps, on purpose: zoom/pan restore is asserted only via its persisted
/// round-trip (UIA cannot see render transforms; the on-screen values are guarded by
/// MapViewStatePersistenceTests plus manual checks), and raid precedence is unit-tested
/// only (driving a fake EFT log through the FileSystemWatcher is too fragile for CI).
/// </summary>
[Collection("E2E")]
[Trait("Category", "E2E")]
public sealed class MapStateE2ETests : E2ETestBase
{
    private const string MapKeySetting = "map.lastSelectedMap";
    private const string ZoomSetting = "map.lastZoomLevel";
    private const string TranslateXSetting = "map.lastTranslateX";
    private const string TranslateYSetting = "map.lastTranslateY";

    private const string MapTab = "TabMap";
    private const string QuestsTab = "TabQuests";
    private const string MapCombo = "CmbMapSelect";
    private const string QuestsPageMarker = "LstQuests";

    [E2EFact]
    public void Saved_map_and_view_are_restored_on_launch_and_not_clobbered_on_close()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, MapKeySetting, "Customs");
        E2EDb.SeedSetting(configDir, ZoomSetting, "1.5");
        E2EDb.SeedSetting(configDir, TranslateXSetting, "-250");
        E2EDb.SeedSetting(configDir, TranslateYSetting, "40");

        using var app = AppDriver.Launch(configDir);
        app.SelectTab(MapTab, MapCombo);

        // Pre-fix this showed Woods (index 0) and the close below then saved Woods back.
        Assert.Equal(MapDisplayName("Customs"), app.WaitForComboSelection(MapCombo));

        app.CloseAndWaitForExit();

        Assert.Equal("Customs", E2EDb.ReadSetting(configDir, MapKeySetting));
        // The seeded view was applied (not reset to 100%/centered) and re-saved as-is:
        // a failed restore would leave zoom 1.0 and a centered translate here.
        Assert.Equal(1.5, ReadDouble(configDir, ZoomSetting));
        Assert.Equal(-250, ReadDouble(configDir, TranslateXSetting));
        Assert.Equal(40, ReadDouble(configDir, TranslateYSetting));
    }

    [E2EFact]
    public void Map_selection_survives_switching_tabs_and_back()
    {
        var configDir = NewConfigDir();
        E2EDb.CreateUserDataDb(configDir);
        E2EDb.SeedSetting(configDir, MapKeySetting, "Customs");

        using var app = AppDriver.Launch(configDir);
        app.SelectTab(MapTab, MapCombo);
        Assert.Equal(MapDisplayName("Customs"), app.WaitForComboSelection(MapCombo));

        app.SelectTab(QuestsTab, QuestsPageMarker);
        app.SelectTab(MapTab, MapCombo);

        // The reported bug: returning to the Map tab reset the selection to Woods.
        Assert.Equal(MapDisplayName("Customs"), app.WaitForComboSelection(MapCombo));

        app.CloseAndWaitForExit();

        Assert.Equal("Customs", E2EDb.ReadSetting(configDir, MapKeySetting));
    }

    [E2EFact]
    public void First_run_shows_the_first_configured_map_and_saves_it()
    {
        var configDir = NewConfigDir();

        using var app = AppDriver.Launch(configDir);
        app.SelectTab(MapTab, MapCombo);

        // No saved state: today's default behavior: first map in map_configs.json.
        Assert.Equal(MapDisplayName("Woods"), app.WaitForComboSelection(MapCombo));

        app.CloseAndWaitForExit();

        Assert.Equal("Woods", E2EDb.ReadSetting(configDir, MapKeySetting));
    }

    #region Helpers

    private static double ReadDouble(string configDir, string key)
    {
        var value = E2EDb.ReadSetting(configDir, key);
        Assert.NotNull(value);
        return double.Parse(value!, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Expected combo text for a map key, resolved from the app's own map_configs.json.
    /// The combo shows displayName, not the key (WaitForComboSelection reads the UIA
    /// Name = ComboBoxItem.Content), equal for Woods/Customs today, but resolving it
    /// keeps these assertions correct for maps whose display name diverges
    /// (e.g. Labs → "The Lab") and for future renames.
    /// </summary>
    private static string MapDisplayName(string mapKey)
    {
        var appDir = Path.GetDirectoryName(AppUnderTest.DllPath!)!;
        var configPath = Path.Combine(appDir, "Assets", "DB", "Data", "map_configs.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));

        foreach (var map in doc.RootElement.GetProperty("maps").EnumerateArray())
        {
            if (string.Equals(map.GetProperty("key").GetString(), mapKey, StringComparison.OrdinalIgnoreCase))
            {
                return map.TryGetProperty("displayName", out var displayName)
                    ? displayName.GetString() ?? mapKey
                    : mapKey;
            }
        }
        return mapKey;
    }

    #endregion
}
