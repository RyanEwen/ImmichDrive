using System;
using ImmichDrive.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Reflection;

namespace ImmichDrive.Pages;

public sealed partial class AboutPage : Page
{
    private UpdateService.UpdateCheckResult? _update;

    public AboutPage()
    {
        InitializeComponent();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v != null ? $"Version {v.Major}.{v.Minor}.{v.Build}" : "";
        if (File.Exists(App.IconImagePath))
        {
            try { AppIcon.Source = new BitmapImage(new Uri(App.IconImagePath)); } catch { }
        }
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "Checking...";
        UpdateStatusText.Text = "Checking for updates...";

        var result = await UpdateService.CheckForUpdateAsync();
        if (result == null)
        {
            UpdateStatusText.Text = "Unable to check for updates. Check your internet connection.";
        }
        else if (result.UpdateAvailable)
        {
            _update = result;
            // The Store path can know an update exists without knowing its version number.
            UpdateStatusText.Text = string.IsNullOrEmpty(result.LatestVersion)
                ? $"A new version is available (you have {result.CurrentVersion})."
                : $"Version {result.LatestVersion} is available (you have {result.CurrentVersion}).";

            // A Store copy installs in place; a GitHub copy gets a link to the release.
            CheckUpdateButton.Content = result.IsStoreManaged ? "Download & Install" : "View Release";
            CheckUpdateButton.Click -= CheckForUpdates_Click;
            if (result.IsStoreManaged)
                CheckUpdateButton.Click += InstallStoreUpdate_Click;
            else
                CheckUpdateButton.Click += ViewRelease_Click;
            CheckUpdateButton.IsEnabled = true;
            return;
        }
        else if (result.IsStoreManaged)
        {
            // "Nothing to download" and "already staged, waiting for us to exit" look identical
            // from the Store APIs, and this app never exits on its own, so offer the restart
            // rather than claiming everything is settled.
            UpdateStatusText.Text =
                $"You're up to date ({result.CurrentVersion}). If the Store downloaded an update "
                + "in the background, restart to finish installing it.";
            CheckUpdateButton.Content = "Restart Now";
            CheckUpdateButton.Click -= CheckForUpdates_Click;
            CheckUpdateButton.Click += RestartForUpdate_Click;
            CheckUpdateButton.IsEnabled = true;
            return;
        }
        else
        {
            UpdateStatusText.Text = $"You're up to date ({result.CurrentVersion}).";
        }

        CheckUpdateButton.Content = "Check for Updates";
        CheckUpdateButton.IsEnabled = true;
    }

    private async void InstallStoreUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateButton.Content = "Downloading...";

        // Owning the Store's dialogs to a real window matters on desktop; without it the
        // consent UI has no parent and the call can fail outright.
        nint hwnd = 0;
        if (SettingsWindow.GetCurrent() is { } window)
            hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

        var progress = new Progress<double>(p =>
        {
            int pct = (int)Math.Round(Math.Clamp(p, 0, 1) * 100);
            CheckUpdateButton.Content = pct < 100 ? $"Downloading ({pct}%)..." : "Installing...";
        });

        var (success, message) = await UpdateService.DownloadAndInstallStoreUpdateAsync(hwnd, progress);
        UpdateStatusText.Text = message;

        if (success)
        {
            // The install only completes once every process in the package is gone, so leaving
            // the window open would stall the very update just downloaded.
            CheckUpdateButton.Content = "Closing...";
            UpdateService.RestartToApplyUpdates();
            return;
        }

        CheckUpdateButton.Content = "Check for Updates";
        CheckUpdateButton.Click -= InstallStoreUpdate_Click;
        CheckUpdateButton.Click += CheckForUpdates_Click;
        CheckUpdateButton.IsEnabled = true;
    }

    private void RestartForUpdate_Click(object sender, RoutedEventArgs e)
    {
        UpdateStatusText.Text = "Restarting...";
        CheckUpdateButton.IsEnabled = false;
        UpdateService.RestartToApplyUpdates();
    }

    private void ViewRelease_Click(object sender, RoutedEventArgs e)
    {
        if (_update?.ReleaseUrl is { Length: > 0 } url)
            UpdateService.OpenUrl(url);
    }
}
