using System.IO;
using System.Text.RegularExpressions;

namespace TarkovHelper.Tests;

/// <summary>
/// Source-level guards on the MainWindow handlers that post their UI work to the
/// dispatcher. The behavioural versions need a real window and a pumped dispatcher, so
/// the shape is asserted structurally, the same way ProfileAttributionSourceTests pins
/// OnQuestEventDetected.
/// </summary>
public class MainWindowDataUpdateHandlerTests
{
    private static readonly string MainWindowSource =
        File.ReadAllText(Path.Combine(TestRepo.Root(), "TarkovHelper", "MainWindow.xaml.cs"));

    /// <summary>
    /// The defect this pins: DatabaseUpdateService.RaiseCompleted contains a throwing
    /// UpdateCheckCompleted subscriber precisely so one cannot escape the service's
    /// async void timer callback. Posting the UI work steps back out of that containment
    /// (the body runs later, on the dispatcher), where an escaping exception reaches
    /// DispatcherUnhandledException, which App.xaml.cs leaves unhandled, ending the
    /// process. So the posted body has to contain itself.
    /// </summary>
    [Fact]
    public void The_posted_update_check_ui_refresh_contains_its_own_failures()
    {
        AssertThePostedBodyContainsItsOwnFailures(
            MemberSource("private void OnDatabaseCheckCompleted("), "UpdateVersionChipUI()");
    }

    /// <summary>
    /// The same defect one layer over: the three monitoring handlers below are raised off
    /// the UI thread, so they hand the chip repaint to the dispatcher, and UiDispatch.Post
    /// posts it with Dispatcher.BeginInvoke. BeginInvoke's legacy semantics rethrow an
    /// action exception through DispatcherUnhandledException, which App.xaml.cs leaves
    /// unhandled, so an unguarded posted body ends the process. Simplifying the helper
    /// back to a bare `UiDispatch.Post(Dispatcher, () => UpdateSyncStatusChip())` fails
    /// this test.
    /// </summary>
    [Fact]
    public void The_posted_sync_status_chip_refresh_contains_its_own_failures()
    {
        AssertThePostedBodyContainsItsOwnFailures(
            MemberSource("private void PostSyncStatusChipRefresh("), "UpdateSyncStatusChip()");
    }

    /// <summary>
    /// All three background-raised monitoring handlers must route through that one
    /// contained post. A handler that hops on its own (Dispatcher.InvokeAsync) or repaints
    /// inline reintroduces exactly the crash path the helper exists to close, and does it
    /// where the test above cannot see it.
    /// </summary>
    [Theory]
    [InlineData("private void OnLogMonitoringStatusChanged(", "private void OnRaidMonitoringStateChanged(")]
    [InlineData("private void OnRaidMonitoringStateChanged(", "private void OnRaidEvent(")]
    [InlineData("private void OnRaidEvent(", null)]
    public void Every_background_raised_monitoring_handler_delegates_to_the_contained_post(
        string signature, string? until)
    {
        var body = MemberSource(signature, until);

        Assert.Contains("PostSyncStatusChipRefresh()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Dispatcher.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateSyncStatusChip(", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one rule both posting handlers owe: the UI work runs inside the posted lambda,
    /// a statement-position try opens before it, and a catch logs whatever it raises.
    /// </summary>
    private static void AssertThePostedBodyContainsItsOwnFailures(string body, string uiWork)
    {
        var post = body.IndexOf("UiDispatch.Post(", StringComparison.Ordinal);
        Assert.True(post >= 0, "The handler must hand its UI work to the dispatcher without blocking.");

        var work = body.IndexOf(uiWork, StringComparison.Ordinal);
        // A statement-position try, not the word inside some later comment.
        var tryKeyword = Regex.Match(body, @"^\s*try\s*$", RegexOptions.Multiline);
        var guard = tryKeyword.Success ? tryKeyword.Index : -1;
        var handler = body.IndexOf("catch (Exception", StringComparison.Ordinal);

        Assert.True(work > post, $"{uiWork} must run inside the posted lambda.");
        Assert.True(guard > post && guard < work,
            "The posted body must open its try INSIDE the lambda and before the UI work, "
            + "or a throw escapes to DispatcherUnhandledException and ends the process.");
        Assert.True(handler > work, "The posted body must catch every exception the UI work raises.");
        Assert.Contains("_log.Error(", body[handler..], StringComparison.Ordinal);
    }

    /// <summary>
    /// Everything from the named declaration up to the doc comment of the next member, or
    /// to <paramref name="until"/> when the next member carries no doc comment.
    /// </summary>
    private static string MemberSource(string signature, string? until = null)
    {
        var start = MainWindowSource.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' no longer exists; update this test with it.");

        var next = MainWindowSource.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
        if (until is not null)
        {
            var stop = MainWindowSource.IndexOf(until, start + signature.Length, StringComparison.Ordinal);
            Assert.True(stop >= 0, $"'{until}' no longer follows '{signature}'; update this test with it.");
            next = next < 0 ? stop : Math.Min(next, stop);
        }

        return next < 0 ? MainWindowSource[start..] : MainWindowSource[start..next];
    }
}
