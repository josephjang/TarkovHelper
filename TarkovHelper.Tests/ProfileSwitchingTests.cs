using System.Reflection;
using System.Runtime.CompilerServices;
using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

public class ProfileSwitchingTests
{
    /// <summary>
    /// A ProfileService with its state seeded directly, so no singleton ctor runs and no
    /// user_data.db is touched. Same technique as QuestCompletionCascadeTests.
    /// </summary>
    private static ProfileService CreateServiceWith(AppProfile profile, bool isAutoDetected)
    {
        var service = (ProfileService)RuntimeHelpers.GetUninitializedObject(typeof(ProfileService));
        Set("_activeProfile", profile);
        Set("_isAutoDetected", isAutoDetected);
        return service;

        void Set(string field, object value)
        {
            var f = typeof(ProfileService).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.True(f != null, $"ProfileService has no field '{field}'");
            f!.SetValue(service, value);
        }
    }

    // EFT re-logs the session mode on every profile-screen visit, and the startup scan replays
    // the last line, so after a manual pick the same evidence arrives again. That flips only the
    // provenance flag -- the destination is unchanged -- and a subscriber that treats it as a
    // transition announces "Profile changed to X" when nothing changed.
    [Fact]
    public void Repeated_identical_detection_reports_no_profile_change()
    {
        var service = CreateServiceWith(AppProfile.PvpSeason, isAutoDetected: false);
        var raised = new List<ProfileChangedEventArgs>();
        service.ActiveProfileChanged += (_, e) => raised.Add(e);

        service.SetActiveProfile(AppProfile.PvpSeason, isAuto: true);

        var args = Assert.Single(raised);
        Assert.True(args.IsAutoDetected);
        Assert.False(args.ProfileChanged);
        Assert.Equal(AppProfile.PvpSeason, args.Profile);
        Assert.True(service.IsAutoDetected);
    }

    [Fact]
    public void A_real_destination_change_reports_profile_changed()
    {
        var service = CreateServiceWith(AppProfile.PvpZone, isAutoDetected: false);
        var raised = new List<ProfileChangedEventArgs>();
        service.ActiveProfileChanged += (_, e) => raised.Add(e);

        service.SetActiveProfile(AppProfile.PvpSeason, isAuto: true);

        var args = Assert.Single(raised);
        Assert.True(args.ProfileChanged);
        Assert.Equal(AppProfile.PvpSeason, args.Profile);
        Assert.Equal(ProfileService.SeasonProfileId, service.ActiveProfileId);
    }

    // The pre-existing equality guard must still suppress a fully identical repeat.
    [Fact]
    public void Identical_detection_twice_raises_once()
    {
        var service = CreateServiceWith(AppProfile.PvpZone, isAutoDetected: false);
        var raised = 0;
        service.ActiveProfileChanged += (_, _) => raised++;

        service.SetActiveProfile(AppProfile.PvpSeason, isAuto: true);
        service.SetActiveProfile(AppProfile.PvpSeason, isAuto: true);

        Assert.Equal(1, raised);
    }

    // Enum.IsDefined guards the public boundary, so an out-of-range cast is ignored rather than
    // reaching the profile-keyed maps that now throw.
    [Fact]
    public void Undefined_profile_values_are_ignored()
    {
        var service = CreateServiceWith(AppProfile.PveZone, isAutoDetected: false);
        var raised = 0;
        service.ActiveProfileChanged += (_, _) => raised++;

        service.SetActiveProfile((AppProfile)99);

        Assert.Equal(0, raised);
        Assert.Equal(AppProfile.PveZone, service.ActiveProfile);
    }

    // Symmetry (PRD R1-R3, R5) is now structural rather than enumerated: the resolver does not
    // receive the current profile, so it CANNOT depend on it. That is why this replaced a
    // 12-row current-x-hint matrix -- the property the matrix guarded is guaranteed by the
    // signature, so only the hint mapping itself still needs asserting.
    [Theory]
    [InlineData(SessionProfileHint.PvpZone, AppProfile.PvpZone)]
    [InlineData(SessionProfileHint.PveZone, AppProfile.PveZone)]
    [InlineData(SessionProfileHint.PvpSeason, AppProfile.PvpSeason)]
    public void Every_known_hint_resolves_to_its_profile(
        SessionProfileHint hint,
        AppProfile expectedProfile)
    {
        Assert.True(ProfileService.TryResolveDetectedProfile(hint, out var profile));
        Assert.Equal(expectedProfile, profile);
    }

    // Unknown evidence, and any hint added later without a mapping, must NOT report a
    // destination: doing so silently moves the user's storage target and persists it.
    [Theory]
    [InlineData(SessionProfileHint.Unknown)]
    [InlineData((SessionProfileHint)99)]
    public void Unrecognized_evidence_reports_no_destination(SessionProfileHint hint)
    {
        Assert.False(ProfileService.TryResolveDetectedProfile(hint, out _));
    }

    // Every AppProfile must be reachable from some hint; otherwise a profile exists that log
    // evidence can never select.
    [Fact]
    public void Every_profile_is_reachable_from_some_hint()
    {
        var reachable = Enum.GetValues<SessionProfileHint>()
            .Select(hint => ProfileService.TryResolveDetectedProfile(hint, out var p)
                ? (AppProfile?)p
                : null)
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .Distinct()
            .ToArray();

        Assert.Equal(Enum.GetValues<AppProfile>().Length, reachable.Length);
    }

    // Profile-keyed maps must not silently alias an unmapped profile onto PvP: answering "pvp"
    // for a profile added later would merge its progress into the permanent PvP rows.
    [Theory]
    [InlineData((AppProfile)99)]
    public void Unmapped_profiles_are_rejected_rather_than_aliased(AppProfile profile)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileService.GetProfileId(profile));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileService.SerializeProfile(profile));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileService.GetGameMode(profile));
    }

    // Round-trip: whatever we persist must parse back to the same profile.
    [Fact]
    public void Serialized_profiles_round_trip()
    {
        foreach (var profile in Enum.GetValues<AppProfile>())
        {
            Assert.Equal(profile, ProfileService.ParseStoredProfile(ProfileService.SerializeProfile(profile)));
        }
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

    // The three profiles must map to three DISTINCT storage ids. This is the guard that the
    // lossy GameMode-keyed lookup (removed in this change) used to defeat: GameMode has two
    // values, so any GameMode-keyed storage lookup answers "pvp" for PvP Season and merges
    // seasonal progress into the permanent PvP rows.
    [Fact]
    public void Every_app_profile_has_a_distinct_storage_id()
    {
        var ids = Enum.GetValues<AppProfile>()
            .Select(ProfileService.GetProfileId)
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
    }

    // PvP Zone and PvP Season deliberately share PvP game rules while keeping separate
    // storage, so game mode must NOT be usable as a storage key.
    [Fact]
    public void Pvp_zone_and_pvp_season_share_game_rules_but_not_storage()
    {
        Assert.Equal(
            ProfileService.GetGameMode(AppProfile.PvpZone),
            ProfileService.GetGameMode(AppProfile.PvpSeason));
        Assert.NotEqual(
            ProfileService.GetProfileId(AppProfile.PvpZone),
            ProfileService.GetProfileId(AppProfile.PvpSeason));
    }
}
