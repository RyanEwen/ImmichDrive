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

        (StatusBar.Severity, StatusBar.Title, StatusBar.Message) = dm.Status switch
        {
            DriveStatus.Online => (InfoBarSeverity.Success, "Online", dm.StatusDetail ?? "Your drive is connected."),
            DriveStatus.Connecting => (InfoBarSeverity.Informational, "Connecting…", dm.StatusDetail ?? ""),
            DriveStatus.Error => (InfoBarSeverity.Error, "Problem", dm.StatusDetail ?? "Something went wrong."),
            _ => (InfoBarSeverity.Warning, "Not connected",
                  dm.IsConfigured ? "The drive is disconnected." : "Add your Immich server to get started."),
        };

        var (done, total) = dm.Progress;
        bool syncing = dm.Status == DriveStatus.Online && total > 0 && done < total;
        SyncProgress.Visibility = syncing ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        if (syncing) { SyncProgress.Maximum = total; SyncProgress.Value = done; }
        DetailText.Text = syncing ? $"Syncing {done:N0} of {total:N0} photos…"
            : SettingsManager.Current.LastSyncUtc > DateTimeOffset.MinValue
                ? $"Last updated {SettingsManager.Current.LastSyncUtc.ToLocalTime():g}" : "";
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string path = SettingsManager.Current.EffectiveSyncRootPath;
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => DriveManager.Current.Refresh();

    private void ConfigureButton_Click(object sender, RoutedEventArgs e) =>
        SettingsWindow.GetCurrent()?.NavigateTo(typeof(ConnectionPage));
}
