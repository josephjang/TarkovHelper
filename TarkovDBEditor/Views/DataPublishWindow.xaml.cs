using System.Windows;
using TarkovDBEditor.Services;

// Type disambiguation for WPF + WindowsForms
using Brush = System.Windows.Media.Brush;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Color = System.Windows.Media.Color;

namespace TarkovDBEditor.Views;

public partial class DataPublishWindow : Window
{
    private DataPublishService? _service;
    private DataPublishService.ComparisonResult? _comparisonResult;

    public DataPublishWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _service = new DataPublishService();

        TxtSourcePath.Text = _service.SourceBasePath;
        TxtTargetPath.Text = _service.TargetBasePath;

        await RefreshComparison();
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshComparison();
    }

    private async Task RefreshComparison()
    {
        if (_service == null) return;

        IsEnabled = false;
        TxtStatus.Text = "Comparing files...";
        BtnPublish.IsEnabled = false;

        try
        {
            _comparisonResult = await _service.CompareAsync(
                progress => Dispatcher.Invoke(() => TxtStatus.Text = progress));

            if (_comparisonResult.Success)
            {
                UpdateUI();
            }
            else
            {
                TxtStatus.Text = $"Error: {_comparisonResult.ErrorMessage}";
                MessageBox.Show(
                    _comparisonResult.ErrorMessage,
                    "Comparison Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
            MessageBox.Show(
                ex.Message,
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void UpdateUI()
    {
        if (_comparisonResult == null) return;

        // Version info. The keep-current-version choice is offered only when there is a
        // token to keep and a bump to avoid, and a fresh comparison always starts from the
        // suggested bump rather than carrying the previous comparison's choice forward.
        TxtCurrentVersion.Text = _comparisonResult.CurrentVersion ?? "-";
        ChkKeepVersion.IsEnabled = _comparisonResult.CanKeepPublishedVersion;
        ChkKeepVersion.IsChecked = false;
        ApplyVersionChoice();

        // Database section. The publish target is the live data-channel endpoint, plus
        // the Assets mirror while format 1 is live; both are named here so the operator
        // can see exactly which endpoints a publish will write.
        UpdateSectionStatus(
            DbStatusIcon, DbStatusText,
            _comparisonResult.DbWillPublish,
            DescribeDbChange(_comparisonResult),
            "No changes");

        if (_comparisonResult.DbExists)
        {
            TxtDbSourceInfo.Text = $"{FormatSize(_comparisonResult.SourceDbSize)} - Hash: {_comparisonResult.SourceDbHash?[..8]}...";

            var target = _comparisonResult.TargetDbHash != null
                ? $"{FormatSize(_comparisonResult.TargetDbSize)} - Hash: {_comparisonResult.TargetDbHash[..8]}..."
                : "Not found (will be created)";
            var mirror = _comparisonResult.MirrorsToAssets
                ? $" + Assets mirror ({DescribeMirror(_comparisonResult.Mirror)})"
                : "";
            TxtDbTargetInfo.Text = $"data/v{_comparisonResult.LiveDataFormatVersion}: {target}{mirror}";
        }
        else
        {
            TxtDbSourceInfo.Text = "Database not found in source";
            TxtDbTargetInfo.Text = $"data/v{_comparisonResult.LiveDataFormatVersion}: source database missing, endpoint left as it is";
        }

        // Map configs section
        UpdateSectionStatus(
            ConfigStatusIcon, ConfigStatusText,
            _comparisonResult.MapConfigsChanged,
            "Changed",
            "No changes");

        // Map SVGs section
        var mapSvg = _comparisonResult.MapSvg;
        UpdateGroupStatus(MapSvgStatusIcon, MapSvgStatusText, mapSvg);
        MapSvgList.ItemsSource = DisplayList(mapSvg);

        // Marker icons section
        var markerIcon = _comparisonResult.MarkerIcon;
        UpdateGroupStatus(MarkerIconStatusIcon, MarkerIconStatusText, markerIcon);
        MarkerIconList.ItemsSource = DisplayList(markerIcon);

        // Item icons section. Summarised in a sentence rather than listed: this group runs
        // to thousands of files, and a list that long tells the operator nothing.
        var itemIcon = _comparisonResult.ItemIcon;
        UpdateGroupStatus(ItemIconStatusIcon, ItemIconStatusText, itemIcon);

        ItemIconSummary.Text = itemIcon.HasChanges
            ? $"Total {itemIcon.Total} icon files. " +
              $"{itemIcon.Added} new, {itemIcon.Updated} updated will be copied."
            : $"Total {itemIcon.Unchanged} icon files. All files are up to date.";

        // Hideout icons section
        var hideoutIcon = _comparisonResult.HideoutIcon;
        UpdateGroupStatus(HideoutIconStatusIcon, HideoutIconStatusText, hideoutIcon);
        HideoutIconList.ItemsSource = DisplayList(hideoutIcon);

        // Summary
        if (_comparisonResult.HasAnyChanges)
        {
            TxtSummary.Text = $"Total {_comparisonResult.TotalChanges} changes to publish";
            TxtStatus.Text = "Review changes and click Publish to update TarkovHelper";
            BtnPublish.IsEnabled = true;
        }
        else
        {
            TxtSummary.Text = "All files are up to date";
            TxtStatus.Text = "No changes to publish";
            BtnPublish.IsEnabled = false;
        }
    }

    /// <summary>
    /// What a publish will do to the database endpoints, and why. A mirror repair, a
    /// manifest repair and an index repair are each publishable on their own, so none can
    /// be described as "no changes" and none may be swallowed by another.
    /// </summary>
    private static string DescribeDbChange(DataPublishService.ComparisonResult comparison)
    {
        // Only a source database can be changed data; without one the endpoint keeps what
        // it has, and the source line below says so.
        if (comparison.DbChanged) return "Changed";

        var reasons = new List<string>();
        if (comparison.MirrorNeedsRepair) reasons.Add("Assets mirror out of sync - will be repaired");
        if (comparison.ManifestNeedsRepair) reasons.Add($"{comparison.ManifestDriftReason} - will be rewritten");
        if (comparison.IndexNeedsRepair) reasons.Add($"{comparison.IndexDriftReason} - will be rewritten");

        return reasons.Count > 0 ? string.Join("; ", reasons) : "No changes";
    }

    private static string DescribeMirror(MirrorSyncState state) => state switch
    {
        MirrorSyncState.InSync => "in sync",
        MirrorSyncState.Drifted => "OUT OF SYNC",
        // Unreachable from here, since this line is only rendered while the live format
        // mirrors to Assets. Present so the switch stays exhaustive.
        _ => "no Assets mirror",
    };

    /// <summary>
    /// The header line for one asset group, worded the same way for all four: what a
    /// publish would copy, or how many files it looked at and left alone.
    /// </summary>
    private void UpdateGroupStatus(
        System.Windows.Controls.TextBlock icon,
        System.Windows.Controls.TextBlock text,
        DataPublishService.FileGroupComparison group)
    {
        UpdateSectionStatus(
            icon, text,
            group.HasChanges,
            $"{group.Added} added, {group.Updated} updated",
            $"{group.Unchanged} files (no changes)");
    }

    /// <summary>
    /// One group's changed files as the list rows the window renders. Unchanged files are
    /// not in <see cref="DataPublishService.FileGroupComparison.Changes"/> at all, so the
    /// list only ever shows what a publish would actually copy.
    /// </summary>
    private static List<FileChangeDisplay> DisplayList(DataPublishService.FileGroupComparison group) =>
        group.Changes.Select(change => new FileChangeDisplay(change)).ToList();

    /// <summary>
    /// Points the version box at the token a publish will write: the one the channel
    /// already serves while "keep current version" is ticked, and the suggested bump
    /// otherwise. The box holds that token rather than merely implying it, so the operator
    /// reads what will be published instead of inferring it from a tick box, and the
    /// publish path stays the single one that reads the box.
    /// </summary>
    private void ApplyVersionChoice()
    {
        if (_comparisonResult == null) return;

        // Guarded by CanKeepPublishedVersion, not by the tick alone: the tick can only be
        // stale, and a kept token has to be one the endpoint actually publishes.
        var keepPublished = ChkKeepVersion.IsChecked == true && _comparisonResult.CanKeepPublishedVersion;

        TxtNewVersion.Text = keepPublished
            ? _comparisonResult.PublishedVersion!
            : _comparisonResult.NewVersion;
        TxtNewVersion.IsEnabled = !keepPublished;
    }

    private void ChkKeepVersion_Changed(object sender, RoutedEventArgs e) => ApplyVersionChoice();

    private void UpdateSectionStatus(
        System.Windows.Controls.TextBlock icon,
        System.Windows.Controls.TextBlock text,
        bool hasChanges,
        string changedText,
        string unchangedText)
    {
        if (hasChanges)
        {
            icon.Text = "●";
            icon.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)); // Yellow
            text.Text = $"- {changedText}";
            text.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
        }
        else
        {
            icon.Text = "✓";
            icon.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // Green
            text.Text = $"- {unchangedText}";
            text.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
        }
    }

    private async void BtnPublish_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _comparisonResult == null) return;

        var newVersion = TxtNewVersion.Text.Trim();
        if (string.IsNullOrEmpty(newVersion))
        {
            MessageBox.Show("Please enter a new version number.", "Version Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dbTargets = _comparisonResult.MirrorsToAssets
            ? $"data/v{_comparisonResult.LiveDataFormatVersion}/ + Assets mirror"
            : $"data/v{_comparisonResult.LiveDataFormatVersion}/";

        // The version token describes the database, so it is only bumped when the database
        // is being replaced, and the operator can keep it even then. Show what will
        // actually be written, not what was typed.
        var publishedVersion = _comparisonResult.ResolvePublishVersion(newVersion);
        var versionLine =
            publishedVersion != newVersion
                ? $"Version: {publishedVersion} (kept, because the database itself is not changing)"
            : publishedVersion == _comparisonResult.PublishedVersion
                ? $"Version: {publishedVersion} (kept, so installs do not download data that did not change)"
                : $"New version: {publishedVersion}";

        var confirm = MessageBox.Show(
            $"This will publish the following changes to TarkovHelper:\n\n" +
            $"• Database ({dbTargets}): " +
            $"{(_comparisonResult.DbWillPublish ? "Will be updated" : "No changes")}\n" +
            $"• Map Configs: {(_comparisonResult.MapConfigsChanged ? "Will be updated" : "No changes")}\n" +
            $"• Map SVGs: {_comparisonResult.MapSvg.Added} added, {_comparisonResult.MapSvg.Updated} updated\n" +
            $"• Marker Icons: {_comparisonResult.MarkerIcon.Added} added, {_comparisonResult.MarkerIcon.Updated} updated\n" +
            $"• Item Icons: {_comparisonResult.ItemIcon.Added} added, {_comparisonResult.ItemIcon.Updated} updated\n" +
            $"• Hideout Icons: {_comparisonResult.HideoutIcon.Added} added, {_comparisonResult.HideoutIcon.Updated} updated\n\n" +
            $"{versionLine}\n\n" +
            $"Continue?",
            "Confirm Publish",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        IsEnabled = false;
        BtnPublish.IsEnabled = false;

        try
        {
            var result = await _service.PublishAsync(
                _comparisonResult,
                newVersion,
                progress => Dispatcher.Invoke(() => TxtStatus.Text = progress));

            if (result.Success)
            {
                TxtStatus.Text = $"Published successfully: {result.FilesCopied} files, {result.IconsCopied} icons";
                TxtCurrentVersion.Text = result.NewVersion ?? newVersion;
                TxtSummary.Text = "Published!";
                TxtSummary.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));

                MessageBox.Show(
                    $"Publish completed successfully!\n\n" +
                    $"Files copied: {result.FilesCopied}\n" +
                    $"Icons copied: {result.IconsCopied}\n" +
                    $"New version: {result.NewVersion}\n\n" +
                    $"Data channel: {_comparisonResult.ChannelDirPath}\n" +
                    $"Assets: {_service.TargetBasePath}\n\n" +
                    "Commit every copied endpoint file together: raw main must never serve " +
                    "a half-published mirror.",
                    "Publish Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // Refresh to show updated state
                await RefreshComparison();
            }
            else
            {
                TxtStatus.Text = $"Publish failed: {result.ErrorMessage}";
                MessageBox.Show(
                    $"Publish failed:\n{result.ErrorMessage}\n\n" +
                    string.Join("\n", result.Errors),
                    "Publish Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Publish failed: {ex.Message}";
            MessageBox.Show(
                $"Publish failed:\n{ex.Message}",
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _service?.Dispose();
        Close();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }

    protected override void OnClosed(EventArgs e)
    {
        _service?.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>
/// Display model for file changes in the UI
/// </summary>
public class FileChangeDisplay
{
    public string FileName { get; set; }
    public string TypeText { get; set; }
    public Brush TypeColor { get; set; }

    public FileChangeDisplay(DataPublishService.FileChangeInfo info)
    {
        FileName = info.FileName;

        switch (info.Type)
        {
            case DataPublishService.ChangeType.Added:
                TypeText = "New";
                TypeColor = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); // Green
                break;
            case DataPublishService.ChangeType.Updated:
                TypeText = "Updated";
                TypeColor = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)); // Yellow
                break;
            default:
                TypeText = "No changes";
                TypeColor = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)); // Gray
                break;
        }
    }
}
