using System.Reflection;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// The shared scaffolding for the four suites that drive <see cref="SettingsService"/> against a
/// seeded <see cref="ProfileSettingsSnapshot"/>: <c>SettingsReloadRaceTests</c>,
/// <c>SettingsPublishOutcomeTests</c>, <c>SettingsSetterContractTests</c> and the settings region
/// of <c>ProfileResetHooksTests</c>.
/// <para>
/// Centralised for the reason <see cref="TestReflection"/> is: the seed, the uninitialized-service
/// construction and the seven-event recorder were copied into all of them, and the recorder's
/// copies are the dangerous kind. An eighth profile-scoped event compiles fine with only one list
/// updated, which would silently stop the other suites asserting on it and quietly weaken the
/// <see cref="AllChangedEvents"/> order contract. One list, one place.
/// </para>
/// <para>
/// Every helper here is meant to be reached through <c>using static</c>, so the call sites read the
/// way the local copies did.
/// </para>
/// </summary>
internal static class SettingsServiceTestSupport
{
    /// <summary>
    /// A profile id that cannot collide with the ambient selection, whatever it happens to be.
    /// That is what makes a "no other profile received a row" assertion exhaustive: the store
    /// starts empty and no other writer knows this id.
    /// </summary>
    internal static string NewProfileId(string label) => label + "-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// A snapshot whose every value differs from every default, so a value still standing after a
    /// reload, an edit or a reset hook can only have come from this seed and not from a
    /// coincidence. Revision 0 is the default <c>_latestRevision</c> of an uninitialized service,
    /// which is the state a reset hook publishes against.
    /// </summary>
    internal static ProfileSettingsSnapshot Seeded(string profileId, long revision = 0) => new(
        profileId,
        revision,
        PlayerLevel: 42,
        ScavRep: 5.5,
        ShowLevelLockedQuests: false,
        DspDecodeCount: 3,
        PlayerFaction: "bear",
        HasEodEdition: true,
        HasUnheardEdition: true,
        PrestigeLevel: 4);

    /// <summary>
    /// A <see cref="SettingsService"/> with no constructor run (see <see cref="TestReflection"/>):
    /// the real one loads every setting off SQLite and subscribes to <c>ProfileService</c>. Only
    /// the store, the "already loaded" flag, the snapshot and the claimed revision are seeded, so
    /// the property getters answer from the cache the test controls rather than re-entering
    /// <c>LoadSettings</c> and its migrations.
    /// </summary>
    /// <param name="store">
    /// Left null on purpose by the cases that want the bulk read to throw: the service's own catch
    /// turns that into "publish this profile's defaults", which is the outcome a stale load must
    /// never be allowed to produce.
    /// </param>
    internal static SettingsService NewService(
        ProfileSettingsSnapshot snapshot, long latestRevision = 0, UserDataDbService? store = null)
    {
        var service = TestReflection.Uninitialized<SettingsService>();
        TestReflection.SetPrivateField(service, "_settingsLoaded", true);
        TestReflection.SetPrivateField(service, "_profileSettings", snapshot);
        TestReflection.SetPrivateField(service, "_latestRevision", latestRevision);
        if (store != null) TestReflection.SetPrivateField(service, "_userDataDb", store);
        return service;
    }

    /// <summary>
    /// The seven events a published reload raises, in the order the reset contract pins. The one
    /// list the three suites compare against, so an event added to
    /// <see cref="Subscribe"/> without a place in this order fails them all at once.
    /// </summary>
    internal static readonly string[] AllChangedEvents =
    {
        "PlayerLevel", "ScavRep", "DspDecodeCount", "PlayerFaction",
        "HasEodEdition", "HasUnheardEdition", "PrestigeLevel",
    };

    /// <summary>
    /// Subscribes <paramref name="record"/> to every profile-scoped changed event, handing it the
    /// event's name and the value it carried. The single enumeration of those events: the two
    /// recorders below are projections of this one.
    /// </summary>
    private static void Subscribe(SettingsService service, Action<string, object?> record)
    {
        service.PlayerLevelChanged += (_, v) => record("PlayerLevel", v);
        service.ScavRepChanged += (_, v) => record("ScavRep", v);
        service.DspDecodeCountChanged += (_, v) => record("DspDecodeCount", v);
        service.PlayerFactionChanged += (_, v) => record("PlayerFaction", v);
        service.HasEodEditionChanged += (_, v) => record("HasEodEdition", v);
        service.HasUnheardEditionChanged += (_, v) => record("HasUnheardEdition", v);
        service.PrestigeLevelChanged += (_, v) => record("PrestigeLevel", v);
    }

    /// <summary>
    /// Records every profile-scoped changed event, with the value it carried, in the order it is
    /// raised. For the cases that assert on the values a reload republished as well as on the
    /// order.
    /// </summary>
    internal static List<(string Name, object? Value)> RecordEvents(SettingsService service)
    {
        var events = new List<(string Name, object? Value)>();
        Subscribe(service, (name, value) => events.Add((name, value)));
        return events;
    }

    /// <summary>
    /// Records the name of every profile-scoped changed event in the order it is raised, for the
    /// cases whose subject is which events fired rather than what they carried.
    /// </summary>
    internal static List<string> RecordEventNames(SettingsService service)
    {
        var names = new List<string>();
        Subscribe(service, (name, _) => names.Add(name));
        return names;
    }

    /// <summary>
    /// The gate every settings publish is decided and swapped under. Held directly by the tests
    /// that drive the window between "this publish is still wanted" and the swap itself: taking
    /// the gate is the only way to suspend a load, an edit or a reset hook exactly there. Static
    /// on the service because these tests build it with
    /// <see cref="TestReflection.Uninitialized{T}"/>, which runs no field initializer.
    /// </summary>
    internal static object PublishGate()
    {
        var field = typeof(SettingsService).GetField(
            "_publishGate", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(field != null, "SettingsService has no private static field '_publishGate'");
        var gate = field!.GetValue(null);
        Assert.NotNull(gate);
        return gate!;
    }

    /// <summary>
    /// Applies one profile-scoped edit through the service's own private edit path and hands back
    /// the outcome the eight property setters branch on. Reached by reflection because the method
    /// is private, and driven directly because it is the only way to hand it a derivation delegate
    /// the test controls: the setters wrap it with a fixed one, so the window between "read the
    /// snapshot" and "take the publish gate" is unreachable from them. A test that wants to land a
    /// competing publish in that window has to be inside the delegate when it happens.
    /// </summary>
    internal static SettingsService.EditPublishOutcome UpdateProfileSetting(
        SettingsService service,
        Func<ProfileSettingsSnapshot, ProfileSettingsSnapshot?> update,
        string key,
        string value)
    {
        var method = typeof(SettingsService).GetMethod(
            "UpdateProfileSetting", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(method != null, "SettingsService has no UpdateProfileSetting");

        var outcome = method!.Invoke(service, new object?[] { update, key, value });
        Assert.NotNull(outcome);
        return (SettingsService.EditPublishOutcome)outcome!;
    }
}
