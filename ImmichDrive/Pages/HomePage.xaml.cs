using ImmichDrive.Classes.Settings;
using ImmichDrive.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.IO;

namespace ImmichDrive.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            DriveManager.Current.StatusChanged += OnStatusChanged;
            UpdateUi();
        };
        Unloaded += (s, e) => DriveManager.Current.StatusChanged -= OnStatusChanged;
    }

    private void OnStatusChanged() => DispatcherQueue.TryEnqueue(UpdateUi);

    private void UpdateUi()
    {
        var dm = DriveManager.Current;
        FolderText.Text = SettingsManager.Current.EffectiveSyncRootPath;
        ConfigureButton.Visibility = dm.IsConfigured
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
        RefreshButton.IsEnabled = dm.Status == DriveStatus.Online;
        RetryButton.Visibility = dm.CanRetry || dm.IsCheckingConnection
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;
        RetryButton.IsEnabled = !dm.IsCheckingConnection;
        RetryText.Text = dm.IsCheckingConnection ? "Checking…" : "Try again";

        (StatusBar.Severity, StatusBar.Title, StatusBar.Message) = dm.Status switch
        {
            DriveStatus.Online when dm.SyncIssue != null =>
                (InfoBarSeverity.Warning, "Some photos could not be synced", dm.SyncIssue),
            DriveStatus.Online => (InfoBarSeverity.Success, "Online", dm.StatusDetail ?? "Your drive is connected."),
            DriveStatus.Connecting => (InfoBarSeverity.Informational, "Connecting…", dm.StatusDetail ?? ""),
            DriveStatus.Offline => (InfoBarSeverity.Warning, "Can't reach Immich",
                  "Your photos are still listed, but they can't be opened until the server answers. "
                  + "Retrying in the background."),
            DriveStatus.Error => (InfoBarSeverity.Error, "Problem", dm.StatusDetail ?? "Something went wrong."),
            _ => (InfoBarSeverity.Warning, "Not connected",
                  dm.IsConfigured ? "The drive is disconnected." : "Add your Immich server to get started."),
        };

        var (done, total) = dm.Progress;
        bool syncing = dm.Status == DriveStatus.Online && dm.IsPopulating;
        SyncProgress.Visibility = syncing ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        if (syncing) { SyncProgress.Maximum = Math.Max(total, 1); SyncProgress.Value = Math.Min(done, total); }
        DetailText.Text = syncing ? total > done ? $"Syncing {done:N0} of about {total:N0} photos…" : "Finishing sync…"
            : SettingsManager.Current.LastSyncUtc > DateTimeOffset.MinValue
                ? $"Last updated {SettingsManager.Current.LastSyncUtc.ToLocalTime():g}" : "";

        var (pinDone, pinTotal, pinActive) = dm.PinProgress;
        bool downloadingPins = dm.Status == DriveStatus.Online && pinActive && pinTotal > 0;
        PinProgressText.Visibility = downloadingPins ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        PinProgress.Visibility = downloadingPins ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        if (downloadingPins)
        {
            PinProgressText.Text = $"Downloading {pinDone:N0} of {pinTotal:N0} pinned photos…";
            PinProgress.Maximum = pinTotal;
            PinProgress.Value = pinDone;
        }
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string path = SettingsManager.Current.EffectiveSyncRootPath;
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => DriveManager.Current.Refresh();

    private void RetryButton_Click(object sender, RoutedEventArgs e) =>
        _ = DriveManager.Current.RetryConnectionAsync();

    private void ConfigureButton_Click(object sender, RoutedEventArgs e) =>
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(ConnectionPage));
}
