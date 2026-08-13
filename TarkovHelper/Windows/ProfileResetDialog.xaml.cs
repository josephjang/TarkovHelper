using System.ComponentModel;
using System.Windows;
using TarkovHelper.Models;
using TarkovHelper.Services;

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
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly AppProfile _target;
    private readonly Func<Task<ProfileResetOutcome>> _runReset;
    private bool _isRunning;

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
        TxtResetTarget.Text = string.Format(_loc.ProfileResetTargetFormat, _loc.ProfileName(target));
        TxtResetCategories.Text = _loc.ProfileResetCategories;
        TxtResetSurvivors.Text = _loc.ProfileResetSurvivorsNote;
        TxtRaidWarning.Text = _loc.ProfileResetRaidWarning;
        TxtResetWorking.Text = _loc.ProfileResetWorking;
        BtnConfirmReset.Content = _loc.ProfileResetConfirmButton;
        BtnCancelReset.Content = _loc.Cancel;
        BtnCloseReset.Content = _loc.Close;

        // Warn, never block (PRD R8): raid detection can be stale or wrong, and the player
        // decides when their season ends, not the raid detector.
        var raidState = EftRaidEventService.Instance.CurrentRaid?.State;
        RaidWarningBorder.Visibility =
            raidState is RaidState.Matching or RaidState.Connecting or RaidState.InRaid
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private async void BtnConfirmReset_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        _isRunning = true;
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
            outcome = new ProfileResetOutcome(false, ex.Message);
        }
        finally
        {
            _isRunning = false;
        }

        ResetSucceeded = outcome.Success;
        ShowResult(outcome);
    }

    private void ShowResult(ProfileResetOutcome outcome)
    {
        ConfirmPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;

        if (outcome.Success)
        {
            TxtResetResult.Text = string.Format(
                _loc.ProfileResetSuccessFormat, _loc.ProfileName(_target));
        }
        else
        {
            TxtResetResult.Text = _loc.ProfileResetFailedText;
            if (!string.IsNullOrEmpty(outcome.Error))
            {
                TxtResetError.Text = outcome.Error;
                TxtResetError.Visibility = Visibility.Visible;
            }
        }
    }

    /// <summary>
    /// Declining changes nothing (PRD R2): the dialog closes without invoking the reset.
    /// </summary>
    private void BtnCancelReset_Click(object sender, RoutedEventArgs e) => Close();

    private void BtnCloseReset_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// While the reset transaction runs the dialog cannot be dismissed: closing mid-run would
    /// leave the player without the outcome the result state exists to deliver.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isRunning)
        {
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
