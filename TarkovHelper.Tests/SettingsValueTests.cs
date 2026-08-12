using System.Globalization;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the culture-safe settings round-trip (SettingsValue): doubles are written in
/// the invariant format ("1.5") and read back identically on any locale, with a
/// current-culture fallback for legacy values written before the invariant convention
/// (the map view-state persistence and every MapSettings double go through these).
/// </summary>
public sealed class SettingsValueTests
{
    /// <summary>Runs an assertion under a specific thread culture and restores it after.</summary>
    private static void WithCulture(string cultureName, Action assertion)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            assertion();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")] // comma decimal separator
    [InlineData("ko-KR")]
    public void Doubles_round_trip_identically_on_any_locale(string culture)
    {
        WithCulture(culture, () =>
        {
            Assert.Equal("1.5", SettingsValue.FormatDouble(1.5));
            Assert.Equal("-250", SettingsValue.FormatDouble(-250));

            Assert.True(SettingsValue.TryParseDouble(SettingsValue.FormatDouble(-320.25), out var value));
            Assert.Equal(-320.25, value);
        });
    }

    [Fact]
    public void Invariant_reading_never_misparses_a_dot_via_group_separators()
    {
        // On de-DE a bare double.TryParse("1.5") reads the dot as a thousands
        // separator and yields 15, the exact bug that silently discarded saved views.
        WithCulture("de-DE", () =>
        {
            Assert.True(SettingsValue.TryParseDouble("1.5", out var value));
            Assert.Equal(1.5, value);
        });
    }

    [Fact]
    public void Legacy_comma_decimal_value_falls_back_to_the_current_culture()
    {
        // A value written by the pre-invariant code on a comma-decimal machine must
        // still load there after the fix.
        WithCulture("de-DE", () =>
        {
            Assert.True(SettingsValue.TryParseDouble("1,5", out var value));
            Assert.Equal(1.5, value);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a number")]
    public void Unparseable_values_return_false(string? value)
    {
        Assert.False(SettingsValue.TryParseDouble(value, out _));
    }
}
