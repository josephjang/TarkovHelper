using TarkovHelper.Models;
using TarkovHelper.Services;

namespace TarkovHelper.Tests;

/// <summary>
/// Guards the PMC/SCAV identity relationship. Before these tests, derivation
/// (<c>CalculateScavProfileId</c>) incremented only the last hex nibble with wraparound while
/// recognition (<c>IsScavProfile</c>) required <c>raid == pmc + 1</c> on that nibble, so for the
/// ~1 in 16 accounts whose PMC id ends in 'f' the derived Scav id was rejected by the very
/// method meant to recognize it, and every Scav raid classified as <see cref="RaidType.Unknown"/>.
/// </summary>
public class EftProfileIdentityTests
{
    // The two pairs the repo actually captured (no carry) must keep working, and the carry cases
    // that no capture covers must now work too.
    [Theory]
    [InlineData("6655cef5899e7271740f41dc", "6655cef5899e7271740f41dd")]
    [InlineData("69193861844e4f097e00ec0e", "69193861844e4f097e00ec0f")]
    [InlineData("69193861844e4f097e00ec0f", "69193861844e4f097e00ec10")]
    [InlineData("69193861844e4f097e00ecff", "69193861844e4f097e00ed00")]
    public void Scav_id_is_the_pmc_id_plus_one_with_carry(string pmc, string expectedScav)
    {
        Assert.Equal(expectedScav, EftProfileInfo.NextProfileId(pmc));
        Assert.Equal(expectedScav, EftRaidEventService.CalculateScavProfileId(pmc));
    }

    // The single most important guard: whatever derivation produces, recognition must accept.
    // A wraparound derivation would fail this for the two carry ids.
    [Theory]
    [InlineData("6655cef5899e7271740f41dc")]
    [InlineData("69193861844e4f097e00ec0e")]
    [InlineData("69193861844e4f097e00ec0f")]
    [InlineData("69193861844e4f097e00ecff")]
    public void A_derived_scav_id_is_always_recognized_as_scav(string pmc)
    {
        var scav = EftRaidEventService.CalculateScavProfileId(pmc);
        Assert.NotNull(scav);

        var profile = new EftProfileInfo { PmcProfileId = pmc, ScavProfileId = scav };

        Assert.True(profile.IsScavProfile(scav!));
        Assert.Equal(RaidType.Scav, profile.GetRaidType(scav!));
        Assert.Equal(RaidType.PMC, profile.GetRaidType(pmc));
    }

    // The values the previous wraparound derivation produced must never be mistaken for Scav.
    [Theory]
    [InlineData("69193861844e4f097e00ec0f", "69193861844e4f097e00ec00")]
    [InlineData("69193861844e4f097e00ecff", "69193861844e4f097e00ecf0")]
    public void Wrapped_nibble_ids_are_not_scav(string pmc, string wrapped)
    {
        var profile = new EftProfileInfo { PmcProfileId = pmc };

        Assert.False(profile.IsScavProfile(wrapped));
        Assert.Equal(RaidType.Unknown, profile.GetRaidType(wrapped));
    }

    // Identity comparison is case-insensitive, matching the IgnoreCase capture pattern.
    [Theory]
    [InlineData("69193861844e4f097e00ec2d", "69193861844E4F097E00EC2E")]
    [InlineData("69193861844E4F097E00EC2D", "69193861844e4f097e00ec2e")]
    public void Scav_recognition_ignores_case(string pmc, string scavOtherCase)
    {
        var profile = new EftProfileInfo { PmcProfileId = pmc };

        Assert.Equal(RaidType.Scav, profile.GetRaidType(scavOtherCase));
    }

    [Fact]
    public void Pmc_recognition_ignores_case()
    {
        var profile = new EftProfileInfo { PmcProfileId = "69193861844e4f097e00ec2d" };

        Assert.Equal(RaidType.PMC, profile.GetRaidType("69193861844E4F097E00EC2D"));
    }

    // A full-width overflow has no representable successor, so it must not fabricate one.
    [Fact]
    public void All_f_identity_has_no_successor()
    {
        Assert.Null(EftProfileInfo.NextProfileId("ffffffffffffffffffffffff"));

        var profile = new EftProfileInfo { PmcProfileId = "ffffffffffffffffffffffff" };
        Assert.Equal(RaidType.Unknown, profile.GetRaidType("fffffffffffffffffffffff0"));
        Assert.Equal(RaidType.Unknown, profile.GetRaidType("000000000000000000000000"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("0123456789abcdef0123456")]   // 23
    [InlineData("0123456789abcdef012345678")] // 25
    [InlineData("0123456789abcdef0123456g")]  // 24 but not hex
    public void Malformed_identities_have_no_successor(string? id)
    {
        Assert.Null(EftProfileInfo.NextProfileId(id));
    }

    [Fact]
    public void Unknown_ids_and_missing_identity_never_classify()
    {
        var withoutIdentity = new EftProfileInfo();
        Assert.Equal(RaidType.Unknown, withoutIdentity.GetRaidType("69193861844e4f097e00ec2d"));

        var profile = new EftProfileInfo { PmcProfileId = "69193861844e4f097e00ec2d" };
        Assert.Equal(RaidType.Unknown, profile.GetRaidType(""));
        Assert.Equal(RaidType.Unknown, profile.GetRaidType("not-a-hex-identity-value"));
        Assert.Equal(RaidType.Unknown, profile.GetRaidType("ffffffffffffffffffffffff"));
    }
}
