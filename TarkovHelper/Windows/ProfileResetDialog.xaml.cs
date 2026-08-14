using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using TarkovHelper.Models;
using TarkovHelper.Services;
using TarkovHelper.Services.Logging;

namespace TarkovHelper.Windows;

/// <summary>
/// The complete-profile-reset confirmation and result dialog
/// (feature-complete-profile-reset.md). One window, two states: the confirm state names the
/// CAPTURED target profile (the one selected when the dialog opened; an automatic switch while
/// it is open does not move the reset, PRD R1), enumerates every category the reset removes,
/// and warns when a raid appears to be running; the result state reports success, or failure
/// with the guarantee that nothing was removed (PRD R5). Replaces the old fixed
/// Korean-plus-English MessageBox pair, which could carry none of this structure and which the
/// e2e harness cannot drive.
/// </summary>
public partial class ProfileResetDialog : Window
{
    private static readonly ILogger _log = Log.For<ProfileResetDialog>();

    /// <summary>
    /// How long the dialog refuses to close while a reset runs, before it hands the window back
    /// to the player. Sized off the service's own bound plus a margin, so the refusal always
    /// outlives a reset that reports an outcome by itself; a run that outlives THIS has wedged
    /// somewhere no timeout reached, and a destructive modal must not be unclosable forever.
    /// ShowDialog disables the main window behind it and a cancelled close defeats the Close
    /// button, Alt+F4 and Application.Current.Shutdown alike, which would leave Task Manager as
    /// the only way out.
    /// </summary>
    internal static readonly TimeSpan CloseRefusalLimit =
        ProfileResetService.MaxDuration + TimeSpan.FromSeconds(15);

    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly AppProfile _target;
    private readonly Func<Task<ProfileResetOutcome>> _runReset;

    /// <summary>Monotonic age of the current run; drives <see cref="CloseRefusalLimit"/>.</summary>
    private readonly Stopwatch _runElapsed = new();
    private bool _isRunning;
    private bool _isClosed;

    /// <summary>True once a reset ran and succeeded; the caller refreshes its pages on it.</summary>
    public bool ResetSucceeded { get; private set; }

    /// <summary>
    /// <paramref name="target"/> is the profile captured when the dialog opened;
    /// <paramref name="runReset"/> performs the actual reset (the caller passes
    /// <c>ProfileResetService.Instance.ResetAsync</c> bound to the same captured target, so
    /// this dialog never consults the ambient selection).
    /// </summary>
    public ProfileResetDialog(AppProfile target, Func<Task<ProfileResetOutcome>> runReset)
    {
        InitializeComponent();
        _target = target;
        _runReset = runReset;

        Title = _loc.ProfileResetDialogTitle;
        TxtResetTitle.Text = _loc.ProfileResetDialogTitle;
        SetTargetLine(_loc.ProfileResetTargetFormat, _loc.ProfileName(target));
        TxtResetCategories.Text = _loc.ProfileResetCategories;
        TxtResetSurvivors.Text = _loc.ProfileResetSurvivorsNote;
        TxtRaidWarning.Text = _loc.ProfileResetRaidWarning;
        TxtResetWorking.Text = _loc.ProfileResetWorking;
        BtnConfirmReset.Content =
            string.Format(_loc.ProfileResetConfirmButtonFormat, _loc.ProfileName(target));
        BtnCancelReset.Content = _loc.Cancel;
        BtnCloseReset.Content = _loc.Close;

        // Warn, never block (PRD R8): raid detection can be stale or wrong, and the player
        // decides when their season ends, not the raid detector.
        var raidService = EftRaidEventService.Instance;
        RaidWarningBorder.Visibility =
            ShouldWarnAboutRaid(raidService.IsMonitoring, raidService.CurrentRaid?.State)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    /// Whether the confirmation shows its raid warning. A raid state is only meaningful while the
    /// watcher is running: StopMonitoring leaves the last CurrentRaid standing, so a session whose
    /// watcher stopped after a raid (a failed StartMonitoring restart, monitoring turned off)
    /// still reports InRaid forever. This is the rule HeaderSyncStatus.GetState applies to the
    /// title-bar chip, and a warning that cries wolf is worse than none: it is the PRD's only
    /// mitigation for the real mid-raid risk.
    /// </summary>
    internal static bool ShouldWarnAboutRaid(bool monitoring, RaidState? raidState)
        => monitoring && raidState is RaidState.Matching or RaidState.Connecting or RaidState.InRaid;

    /// <summary>
    /// Renders the target line with the profile name as its own emphasized run (bold, in the
    /// danger color): the name is the load-bearing word of the whole dialog (PRD R1), so it
    /// must not blend into the sentence around it. Plain runs keep TextBlock.Text, and with
    /// it the UIA Name the e2e test asserts, equal to the flat formatted string.
    /// </summary>
    private void SetTargetLine(string format, string profileName)
    {
        var slot = format.IndexOf("{0}", StringComparison.Ordinal);
        if (slot < 0)
        {
            // A translation that lost its slot degrades to the unstyled sentence rather
            // than crashing; the localization tests guard the slot's presence.
            TxtResetTarget.Text = format;
            return;
        }

        TxtResetTarget.Inlines.Clear();
        if (slot > 0)
            TxtResetTarget.Inlines.Add(new Run(format[..slot]));
        TxtResetTarget.Inlines.Add(new Run(profileName)
        {
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("ErrorBrush")
        });
        var suffix = format[(slot + "{0}".Length)..];
        if (suffix.Length > 0)
            TxtResetTarget.Inlines.Add(new Run(suffix));
    }

    private async void BtnConfirmReset_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        _isRunning = true;
        _runElapsed.Restart();
        BtnConfirmReset.IsEnabled = false;
        BtnCancelReset.IsEnabled = false;
        TxtResetWorking.Visibility = Visibility.Visible;

        ProfileResetOutcome outcome;
        try
        {
            outcome = await _runReset();
        }
        catch (Exception ex)
        {
            // ProfileResetService reports failure as an outcome, but the delegate is
            // caller-supplied; an escaping exception from an async void handler would take the
            // process down, so it is folded into the same failure rendering.
            outcome = ProfileResetOutcome.Failed(ex);
        }
        finally
        {
            _isRunning = false;
            _runElapsed.Stop();
        }

        ResetSucceeded = outcome.Success;

        if (_isClosed)
        {
            // The close backstop already handed the window back, so ShowDialog has returned and
            // the caller has read ResetSucceeded as false. There is no result state left to
            // render into; the log is where this outcome survives.
            _log.Warning(
                $"The profile reset finished after its dialog was force-closed (status={outcome.Status}): " +
                (outcome.Error ?? "no further detail"));
            return;
        }

        ShowResult(outcome);
    }

    private void ShowResult(ProfileResetOutcome outcome)
    {
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;

        // Focus follows the state swap, so Enter/Space dismiss the result without a Tab
        // first; Escape already reaches Close through its own IsCancel flag.
        BtnCloseReset.Focus();

        TxtResetResult.Text = ResultHeadline(_loc, outcome, _target);

        // Only a failure carries a library-level detail ("database is locked"), and the outcome
        // factories guarantee that one is never blank, so there is nothing to defend against here
        // beyond the two statuses that legitimately have no detail to show.
        if (outcome.Error != null)
        {
            TxtResetError.Text = outcome.Error;
            TxtResetError.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// The headline for <paramref name="outcome"/>. Each status gets its own sentence because they
    /// promise different things: only <see cref="ProfileResetStatus.Failed"/> may state PRD R5's
    /// "nothing was removed", while an abandoned store wait does not know whether the transaction
    /// committed and has to say so rather than borrow a guarantee it cannot make.
    /// </summary>
    internal static string ResultHeadline(
        LocalizationService loc, ProfileResetOutcome outcome, AppProfile target)
        => outcome.Status switch
        {
            ProfileResetStatus.Succeeded =>
                string.Format(loc.ProfileResetSuccessFormat, loc.ProfileName(target)),
            ProfileResetStatus.Abandoned => loc.ProfileResetAbandonedText,
            _ => loc.ProfileResetFailedText,
        };

    /// <summary>
    /// Declining changes nothing (PRD R2): the dialog closes without invoking the reset. Shared
    /// with Escape, which reaches this button through its IsCancel flag.
    /// </summary>
    private void BtnCancelReset_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Dismisses the result state; Escape reaches it through IsCancel too.</summary>
    private void BtnCloseReset_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// While the reset transaction runs the dialog resists dismissal: closing mid-run would leave
    /// the player without the outcome the result state exists to deliver. The resistance is
    /// bounded by <see cref="CloseRefusalLimit"/>, because refusing forever is the worse failure:
    /// losing one outcome message beats a modal that nothing on the machine can dismiss.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isRunning && _runElapsed.Elapsed < CloseRefusalLimit)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);

        // Only after the base call, which is where a subscriber could still cancel the close.
        if (!e.Cancel)
        {
            _isClosed = true;
        }
    }
}
