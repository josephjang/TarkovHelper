using System.IO;
using System.Text.RegularExpressions;

namespace TarkovHelper.Tests;

/// <summary>
/// MainWindow subscribes to app-lifetime singletons (settings, localization, log sync, the raid
/// poller, the profile service, the quest database) and every one of those handlers marshals onto
/// this window's dispatcher and touches its controls and resources. A subscription the window
/// never drops outlives the window: a background raise during or after teardown then blocks on a
/// dispatcher that is shutting down and resolves resources on a closed window, and the window
/// itself stays alive for as long as the singleton does.
/// <para>
/// So the rule is symmetry, and it is checked over the source because neither the unit suite nor
/// the e2e can observe a detach: constructing MainWindow needs the whole app, and a leaked
/// handler is silent until a race fires. Reading the source is what makes the NEXT subscription
/// someone adds fail here instead of in a shutdown crash report.
/// </para>
/// </summary>
public class MainWindowTeardownTests
{
    /// <summary>
    /// The singleton receivers whose events MainWindow subscribes to. Window-owned members
    /// (the drawer timer, TitleBar, the window's own SourceInitialized/Closing) are deliberately
    /// out of scope: they die with the window, so they cannot outlive it.
    /// </summary>
    private const string SingletonTargets = @"_settingsService|_logSyncService|_loc|\w+\.Instance";

    private static string Source() =>
        File.ReadAllText(Path.Combine(TestRepo.Root(), "TarkovHelper", "MainWindow.xaml.cs"));

    /// <summary>
    /// The body of a method, from its signature to the first line that closes at method
    /// indentation (four spaces), which is how every method in this file is laid out.
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"MainWindow.xaml.cs no longer contains '{signature}'");

        var end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of '{signature}'");
        return source[start..end];
    }

    [Fact]
    public void Every_singleton_subscription_the_constructor_adds_is_detached_on_close()
    {
        var source = Source();
        var constructor = MethodBody(source, "public MainWindow()");
        var closing = MethodBody(source, "private void OnWindowClosing(");

        var subscriptions = Regex.Matches(
            constructor, $@"(?<target>{SingletonTargets})\.(?<event>\w+)\s*\+=\s*(?<handler>\w+);");

        // A guard on the guard: a regex that stopped matching would pass this test silently.
        // Eight settings events, the language change, and four other singletons.
        Assert.True(subscriptions.Count >= 13,
            $"only {subscriptions.Count} singleton subscriptions found in the constructor; " +
            "the pattern has drifted from the source");

        var missing = subscriptions
            .Select(m => $"{m.Groups["target"].Value}.{m.Groups["event"].Value} -= {m.Groups["handler"].Value};")
            .Where(detach => !closing.Contains(detach, StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "MainWindow subscribes to these singleton events and never detaches them in " +
            $"OnWindowClosing:{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }

    /// <summary>
    /// The two drawer handlers this rule exists for: both block on Dispatcher.Invoke and resolve
    /// window resources, and the loyalty one repaints controls built at runtime. Named
    /// explicitly so a rename that slipped past the symmetry check above still gets caught.
    /// </summary>
    [Theory]
    [InlineData("TraderLoyaltyChanged", "OnTraderLoyaltyChanged")]
    [InlineData("ProfileSettingsReloaded", "OnProfileSettingsReloaded")]
    [InlineData("PlayerLevelChanged", "OnPlayerLevelChanged")]
    [InlineData("PrestigeLevelChanged", "OnPrestigeLevelChanged")]
    public void Settings_drawer_handlers_are_detached_on_close(string eventName, string handler)
    {
        var closing = MethodBody(Source(), "private void OnWindowClosing(");
        Assert.Contains($"_settingsService.{eventName} -= {handler};", closing);
    }
}
