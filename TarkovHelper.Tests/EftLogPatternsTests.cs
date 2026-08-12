using System.Globalization;
using TarkovHelper.Models;
using TarkovHelper.Services.Eft;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the log-line readers shared by EftRaidEventService and SessionModeTimeline. The
/// timestamp half is culture-sensitive by default, and every SessionModeTimeline entry is
/// ordered by it, so a wrong calendar does not merely misread one line: it sorts every
/// transition centuries away from the events it is meant to attribute, and the session records
/// nothing.
/// </summary>
public sealed class EftLogPatternsTests
{
    private const string ModeLine =
        "2026-08-12 21:03:11.482 123|1.1.0.46657|Info|application|Session mode: PvpSeason";

    /// <summary>
    /// Runs <paramref name="body"/> with the ambient culture pinned, restoring it afterwards.
    /// CurrentCulture is per-thread, so this cannot leak into tests running in parallel.
    /// </summary>
    private static void WithCulture(string name, Action body)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(name);
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // Measured on .NET 8 with the ambient parse: year 1483 under th-TH (Buddhist calendar),
    // 2647 under fa-IR (Persian), and outright failure under ar-SA (Umm al-Qura).
    [Theory]
    [InlineData("th-TH")]
    [InlineData("fa-IR")]
    [InlineData("ar-SA")]
    [InlineData("en-US")]
    [InlineData("ko-KR")]
    public void A_timestamp_reads_the_same_under_any_ambient_culture(string culture)
    {
        WithCulture(culture, () =>
        {
            Assert.True(EftLogPatterns.TryExtractTimestamp(ModeLine, out var timestamp),
                $"the line failed to parse at all under {culture}");
            Assert.Equal(new DateTime(2026, 8, 12, 21, 3, 11, 482), timestamp);
        });
    }

    // The calendar a culture would have imposed is the whole point, so confirm the fixture line
    // really is one the ambient parse mangles. Without this the theory above could be passing
    // because the cultures under test happen to agree with the invariant one.
    [Fact]
    public void The_ambient_parse_this_guards_against_really_does_disagree()
    {
        WithCulture("th-TH", () =>
        {
            Assert.True(DateTime.TryParse("2026-08-12 21:03:11.482", out var ambient));
            Assert.NotEqual(2026, ambient.Year);
        });
    }

    [Fact]
    public void A_line_with_no_leading_timestamp_reports_no_timestamp()
    {
        Assert.False(EftLogPatterns.TryExtractTimestamp("Session mode: Pve", out var timestamp));
        Assert.Equal(default, timestamp);
    }

    // A malformed timestamp must not squeeze through the regex into a wrong DateTime: the regex
    // pins the shape, TryParseExact pins the values.
    [Fact]
    public void An_impossible_date_in_the_right_shape_reports_no_timestamp()
    {
        Assert.False(EftLogPatterns.TryExtractTimestamp(
            "2026-13-45 99:99:99.999 123|Info|application|Session mode: Pve", out _));
    }

    [Fact]
    public void Session_mode_tokens_map_to_their_profiles()
    {
        Assert.True(EftLogPatterns.TryParseSessionProfile(ModeLine, out var hint));
        Assert.Equal(SessionProfileHint.PvpSeason, hint);
    }

    // The prefix bug this pattern was corrected for once already: a truncated flush ending at
    // "Pvp" must not classify a seasonal session as permanent PvP.
    [Fact]
    public void A_pvp_season_line_is_not_read_as_plain_pvp()
    {
        Assert.True(EftLogPatterns.TryParseSessionProfile(
            "2026-08-12 21:03:11.482 123|Info|application|Session mode: PvpSeason", out var hint));
        Assert.NotEqual(SessionProfileHint.PvpZone, hint);
    }
}
