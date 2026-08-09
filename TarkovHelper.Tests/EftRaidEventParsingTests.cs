using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

public class EftRaidEventParsingTests
{
    [Theory]
    [InlineData("Session mode: Pve", SessionProfileHint.PveZone, GameMode.PVE)]
    [InlineData("Session mode: Pvp", SessionProfileHint.PvpZone, GameMode.PVP)]
    [InlineData("Session mode: Regular", SessionProfileHint.PvpZone, GameMode.PVP)]
    [InlineData("2026-08-08 10:00:00.000 | Session mode: PvpSeason", SessionProfileHint.PvpSeason, GameMode.PVP)]
    public void Session_mode_tokens_map_to_profile_hint_and_game_mode(
        string line,
        SessionProfileHint expectedHint,
        GameMode expectedMode)
    {
        var parsed = EftRaidEventService.TryParseSessionProfile(line, out var hint, out var mode);

        Assert.True(parsed);
        Assert.Equal(expectedHint, hint);
        Assert.Equal(expectedMode, mode);
    }

    [Theory]
    [InlineData("Session mode: PvpSeasonal")]
    [InlineData("Session mode: Pvp extra")]
    [InlineData("Session mode: Arena")]
    public void Session_mode_parser_requires_an_exact_known_token(string line)
    {
        var parsed = EftRaidEventService.TryParseSessionProfile(line, out var hint, out var mode);

        Assert.False(parsed);
        Assert.Equal(SessionProfileHint.Unknown, hint);
        Assert.Equal(GameMode.Unknown, mode);
    }

    [Fact]
    public void Completed_profile_selection_accepts_legacy_and_new_completed_lines()
    {
        const string legacyId = "0123456789abcdef01234567";
        const string completedId = "89abcdef0123456701234567";

        Assert.True(EftRaidEventService.TryParseCompletedProfileSelection(
            $"SelectProfile ProfileId:{legacyId} AccountId:1234567",
            out var parsedLegacyId,
            out var legacyAccountId));
        Assert.Equal(legacyId, parsedLegacyId);
        Assert.Equal("1234567", legacyAccountId);

        Assert.True(EftRaidEventService.TryParseCompletedProfileSelection(
            $"CompleteSelectedProfile ProfileId:{completedId} AccountId:1234567",
            out var parsedCompletedId,
            out var completedAccountId));
        Assert.Equal(completedId, parsedCompletedId);
        Assert.Equal("1234567", completedAccountId);
        Assert.NotEqual(parsedLegacyId, parsedCompletedId);
    }

    [Theory]
    [InlineData("PrepareSelectedProfileLocally ProfileId:0123456789abcdef01234567 AccountId:1234567")]
    [InlineData("NotCompleteSelectedProfile ProfileId:0123456789abcdef01234567 AccountId:1234567")]
    public void Non_completed_profile_selection_is_not_published(string line)
    {
        Assert.False(EftRaidEventService.TryParseCompletedProfileSelection(
            line,
            out var profileId,
            out var accountId));
        Assert.Empty(profileId);
        Assert.Empty(accountId);
    }
}
