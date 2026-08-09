using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

public class ProfileSwitchingTests
{
    [Theory]
    [InlineData(AppProfile.PvpZone, SessionProfileHint.Unknown, AppProfile.PvpZone, false)]
    [InlineData(AppProfile.PvpZone, SessionProfileHint.PvpZone, AppProfile.PvpZone, true)]
    [InlineData(AppProfile.PvpZone, SessionProfileHint.PveZone, AppProfile.PveZone, true)]
    [InlineData(AppProfile.PvpZone, SessionProfileHint.PvpSeason, AppProfile.PvpSeason, true)]
    [InlineData(AppProfile.PveZone, SessionProfileHint.Unknown, AppProfile.PveZone, false)]
    [InlineData(AppProfile.PveZone, SessionProfileHint.PvpZone, AppProfile.PvpZone, true)]
    [InlineData(AppProfile.PveZone, SessionProfileHint.PveZone, AppProfile.PveZone, true)]
    [InlineData(AppProfile.PveZone, SessionProfileHint.PvpSeason, AppProfile.PvpSeason, true)]
    [InlineData(AppProfile.PvpSeason, SessionProfileHint.Unknown, AppProfile.PvpSeason, false)]
    [InlineData(AppProfile.PvpSeason, SessionProfileHint.PvpZone, AppProfile.PvpZone, true)]
    [InlineData(AppProfile.PvpSeason, SessionProfileHint.PveZone, AppProfile.PveZone, true)]
    [InlineData(AppProfile.PvpSeason, SessionProfileHint.PvpSeason, AppProfile.PvpSeason, true)]
    public void Resolver_covers_every_profile_and_hint(
        AppProfile current,
        SessionProfileHint hint,
        AppProfile expectedProfile,
        bool expectedApplied)
    {
        var result = ProfileService.ResolveDetectedProfile(current, hint);

        Assert.Equal(expectedProfile, result.Profile);
        Assert.Equal(expectedApplied, result.DetectionApplied);
    }

    [Theory]
    [InlineData("PVP", AppProfile.PvpZone)]
    [InlineData("PVE", AppProfile.PveZone)]
    [InlineData("SEASON", AppProfile.PvpSeason)]
    [InlineData("season", AppProfile.PvpSeason)]
    [InlineData("unexpected", AppProfile.PvpZone)]
    [InlineData("", AppProfile.PvpZone)]
    [InlineData(null, AppProfile.PvpZone)]
    public void Stored_profile_parser_supports_three_values_and_falls_back_to_pvp(
        string? stored,
        AppProfile expected)
        => Assert.Equal(expected, ProfileService.ParseStoredProfile(stored));

    [Theory]
    [InlineData(AppProfile.PvpZone, "PVP")]
    [InlineData(AppProfile.PveZone, "PVE")]
    [InlineData(AppProfile.PvpSeason, "SEASON")]
    public void Stored_profile_serializer_uses_compatible_values(AppProfile profile, string expected)
        => Assert.Equal(expected, ProfileService.SerializeProfile(profile));

    [Theory]
    [InlineData(AppProfile.PvpZone, ProfileService.PvpProfileId, GameMode.PVP)]
    [InlineData(AppProfile.PveZone, ProfileService.PveProfileId, GameMode.PVE)]
    [InlineData(AppProfile.PvpSeason, ProfileService.SeasonProfileId, GameMode.PVP)]
    public void App_profiles_map_to_distinct_storage_and_expected_game_rules(
        AppProfile profile,
        string profileId,
        GameMode gameMode)
    {
        Assert.Equal(profileId, ProfileService.GetProfileId(profile));
        Assert.Equal(gameMode, ProfileService.GetGameMode(profile));
    }

    [Theory]
    [InlineData(GameMode.PVP, ProfileService.PvpProfileId)]
    [InlineData(GameMode.PVE, ProfileService.PveProfileId)]
    [InlineData(GameMode.Unknown, ProfileService.PvpProfileId)]
    public void Legacy_game_mode_mapping_never_targets_season(GameMode mode, string expected)
        => Assert.Equal(expected, ProfileService.GetProfileId(mode));
}
