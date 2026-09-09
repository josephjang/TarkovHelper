using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Settings;

namespace TarkovHelper.Tests;

/// <summary>
/// The shared fixtures for the three suites that drive the trader loyalty gate:
/// <c>QuestStatusLoyaltyTests</c> (the status walk), <c>QuestRequirementBadgeTests</c> (the string
/// the row shows) and <c>QuestDbServiceLoyaltyReadTests</c> (the load that produces the shape).
/// <para>
/// Centralised for the reason <see cref="SettingsServiceTestSupport"/> is, and after the copies
/// had already drifted: the status suite's quest builder sorted its rows through
/// <see cref="QuestDbService.SortIntoBadgeOrder"/> and the badge suite's did not, so the badge was
/// asserted against a task shape the loader never produces and would have kept passing with the
/// sort deleted. One builder, one shape, one place.
/// </para>
/// <para>
/// Every helper here is meant to be reached through <c>using static</c>, so the call sites read
/// the way the local copies did.
/// </para>
/// </summary>
internal static class LoyaltyFixtures
{
    // The tarkov.dev ids of the traders these suites gate on, so a case that goes on to look a
    // trader up in the asset database or the display order finds it.
    internal const string Prapor = "54cb50c76803fa8b248b4571";
    internal const string Jaeger = "5c0647fdd443bc2504c2d371";
    internal const string Skier = "58330581ace78e27b8b10cee";
    internal const string Therapist = "54cb57776803fa99248b456e";

    /// <summary>
    /// A quest called "q" given by <paramref name="giver"/> with the loyalty rows it names, for
    /// the cases whose subject is one quest and which have no use for its id.
    /// </summary>
    internal static TarkovTask Quest(
        string giver, params (string TraderId, string TraderName, int Level)[] loyalty)
        => Quest("q", giver, loyalty);

    /// <summary>
    /// A quest given by <paramref name="giver"/> with the loyalty rows it names. No other gate,
    /// so a status other than Active can only be the loyalty check.
    /// <para>
    /// The rows are stamped with a normalized name and sorted through
    /// <see cref="QuestDbService.SortIntoBadgeOrder"/>, exactly as the loader leaves them, because
    /// the order a quest carries its requirements in is now part of its shape: the badge reads
    /// the first unmet entry rather than re-deriving the rule. Building the list by hand in
    /// arrival order would test a shape the app never produces.
    /// </para>
    /// </summary>
    internal static TarkovTask Quest(
        string id, string giver, params (string TraderId, string TraderName, int Level)[] loyalty)
    {
        var task = new TarkovTask
        {
            Ids = new List<string> { id },
            Name = id,
            NormalizedName = id,
            Trader = giver,
        };

        if (loyalty.Length > 0)
        {
            task.TraderLoyaltyRequirements = loyalty
                .Select(l => new QuestTraderRequirement
                {
                    TraderId = l.TraderId,
                    TraderName = l.TraderName,
                    NormalizedName = l.TraderName.ToLowerInvariant(),
                    Level = l.Level,
                })
                .ToList();
            QuestDbService.SortIntoBadgeOrder(task);
        }

        return task;
    }

    /// <summary>
    /// Settings holding the entered loyalty levels and whichever of the other gates a case needs.
    /// Every value left alone is passed as null, which is the shape an untouched profile has: the
    /// snapshot's <c>...OrDefault</c> getters then answer the app's own defaults, so a case that
    /// names nothing is testing the values a fresh profile really carries.
    /// </summary>
    internal static ProfileSettingsSnapshot Settings(
        int playerLevel = SettingsService.DefaultPlayerLevel,
        double? scavRep = null,
        string? faction = null,
        bool? hasEod = null,
        bool? hasUnheard = null,
        int? prestige = null,
        params (string TraderId, int Level)[] loyalty)
    {
        var levels = TraderLoyaltyLevels.Empty;
        foreach (var (traderId, level) in loyalty) levels = levels.With(traderId, level);

        return new ProfileSettingsSnapshot(
            "profile", 0,
            PlayerLevel: playerLevel,
            ScavRep: scavRep,
            ShowLevelLockedQuests: null,
            DspDecodeCount: null,
            PlayerFaction: faction,
            HasEodEdition: hasEod,
            HasUnheardEdition: hasUnheard,
            PrestigeLevel: prestige,
            TraderLoyalty: levels);
    }
}
