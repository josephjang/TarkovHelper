using System.Text.RegularExpressions;

namespace TarkovHelper.Tests;

/// <summary>
/// The Collector page's four settings subscriptions and the coalesced rebuild they feed
/// (feature-kappa-collector-1-1.spec.md, Design 3 and TD4), asserted structurally against the
/// source.
/// <para>
/// Structurally, because the page cannot be constructed in this suite: its markup resolves
/// App.xaml's brushes through StaticResource and its constructor reaches six singletons, each of
/// which opens a database. The subscribe/unsubscribe mirror, the lifecycle routing and the
/// factory rule are the shared page guards in <see cref="RefreshCoalescerSchedulingTests"/>,
/// which this page joins; what is specific to this page is here: WHICH events, that each one
/// books the same coalesced rebuild and nothing else, and that every status the page reads comes
/// from one captured pass. The collapsing of a burst into one run is <c>RefreshCoalescerTests</c>.
/// </para>
/// </summary>
public sealed class CollectorPageSubscriptionTests
{
    private static readonly string Source =
        SourceGuards.Read("TarkovHelper", "Pages", "CollectorPage.xaml.cs");

    private static string Body(string signature) => SourceGuards.MemberBody(Source, signature);

    /// <summary>
    /// The four, and why: the player level, the Scav karma and the trader loyalty are the three
    /// value gates Collector carries in the data; the reload closes a published burst that may
    /// carry no loyalty event at all (a profile with no stored entry).
    /// </summary>
    public static TheoryData<string, string> SettingsEvents() => new()
    {
        { "PlayerLevelChanged", "OnPlayerLevelChanged" },
        { "ScavRepChanged", "OnScavRepChanged" },
        { "TraderLoyaltyChanged", "OnTraderLoyaltyChanged" },
        { "ProfileSettingsReloaded", "OnProfileSettingsReloaded" },
    };

    [Theory]
    [MemberData(nameof(SettingsEvents))]
    public void The_page_subscribes_to_each_of_the_four_settings_events(string eventName, string handler)
    {
        var subscribe = Body("private void SubscribeServiceEvents()");

        Assert.Contains(
            $"SettingsService.Instance.{eventName} += {handler};", subscribe, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(SettingsEvents))]
    public void Each_settings_handler_books_the_coalesced_rebuild_and_does_nothing_else(
        string eventName, string handler)
    {
        // An expression-bodied one-liner over the coalescer. A handler that rebuilt inline would
        // repaint once per event of a burst: for a seven-trader profile switch that is the level,
        // the rep, seven loyalty events and the reload, so 1 + 1 + 7 + 1 = ten repaints. And one
        // that reloaded the items would re-aggregate 44 rows for a value that cannot change the
        // list's scope.
        var declaration = Regex.Match(
            Source, $@"private void {handler}\(object\? sender, \w+ e\)\s*=>\s*(?<body>[^;]+);");

        Assert.True(declaration.Success, $"{handler} ({eventName}) is not an expression-bodied handler");
        Assert.Equal("_unlockRefresh.Request()", declaration.Groups["body"].Value.Trim());
    }

    [Fact]
    public void The_events_Collector_cannot_change_on_are_not_subscribed()
    {
        // TD4: no quest in Collector's prerequisite closure carries an edition, prestige, DSP or
        // faction gate, so none of these five events can change anything the page shows. Not
        // because the page ignores those values: the item list reads them transitively, dropping
        // a quest whose status is Unavailable, which is what an unmet edition, prestige or
        // faction gate produces (QuestStatusLoyaltyTests.An_edition_gate_still_wins_over_loyalty).
        // It is the published data that puts no such gate in that closure, and
        // PublishedDataContentTests
        //   .The_quests_Collector_depends_on_carry_no_edition_prestige_or_faction_gate
        // is what keeps that true. Five handlers that can never fire is what "subscribe to all
        // eight for symmetry" would have added; a publish that changes the data arrives through
        // DataRefreshed instead.
        var subscribe = Body("private void SubscribeServiceEvents()");

        foreach (var never in new[]
                 {
                     "HasEodEditionChanged", "HasUnheardEditionChanged", "PrestigeLevelChanged",
                     "DspDecodeCountChanged", "PlayerFactionChanged",
                 })
        {
            Assert.DoesNotContain(never, subscribe, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_coalescer_is_built_in_the_constructor_body_not_in_a_field_initializer()
    {
        // A field initializer runs before the base constructor, where this.Dispatcher is not yet
        // available (CS0236), so the factory has to be called from the body.
        Assert.Contains("private readonly RefreshCoalescer _unlockRefresh;", Source, StringComparison.Ordinal);
        Assert.Contains(
            "_unlockRefresh = RefreshCoalescer.OnDispatcher(this, RefreshUnlockPanelForSettingsChange);",
            Body("public CollectorPage()"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_rebuild_repaints_the_panel_only_and_only_while_loaded()
    {
        var refresh = Body("private void RefreshUnlockPanelForSettingsChange()");

        // Scheduled, so it can land after Unloaded or before the first load: both are skipped.
        Assert.Contains("if (_isUnloaded || !_isDataLoaded) return;", refresh, StringComparison.Ordinal);
        // The panel, against a fresh pass; not the item list, whose scope none of the four
        // events can change.
        Assert.Contains(
            "RebuildUnlockPanel(RenderPass.Capture(_questProgressService, _questGraphService));",
            refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("LoadItems", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("ReloadItems", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void The_item_load_hands_its_one_pass_to_the_aggregation_and_the_panel()
    {
        // The list and the panel above it describe one profile: one capture per load, threaded to
        // both, never a second live reading for the panel. The aggregation is handed the scope
        // captured FROM that pass (see ListScope), so it cannot read a status from another.
        var load = Body("private void LoadItems()");

        Assert.Single(Regex.Matches(load, @"RenderPass\.Capture\("));
        Assert.Contains("CaptureListScope(pass)", load, StringComparison.Ordinal);
        Assert.Contains("GetCollectorItemRequirements(scope)", load, StringComparison.Ordinal);
        Assert.Contains("RebuildUnlockPanel(pass);", load, StringComparison.Ordinal);
    }
}
