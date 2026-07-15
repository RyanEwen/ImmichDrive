using ImmichDrive.Classes.Settings;
using ImmichDrive.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.IO;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace ImmichDrive.Pages;

public sealed partial class ConnectionPage : Page
{
    public ConnectionPage()
    {
        InitializeComponent();
        var s = SettingsManager.Current;
        ServerBox.Text = s.ServerUrl;
        ApiKeyBox.Password = s.ApiKey;
        FolderBox.Text = s.EffectiveSyncRootPath;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool online = DriveManager.Current.Status == DriveStatus.Online;
        DisconnectButton.Visibility = online ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    private void SaveFields()
    {
        var s = SettingsManager.Current;
        s.ServerUrl = ServerBox.Text.Trim();
        s.ApiKey = ApiKeyBox.Password;
        // Note: the drive folder is NOT written from here — it's read-only in the UI and only ever
        // changed via ChangeFolder_Click (which also moves the drive when it's live).
        SettingsManager.SaveSettings();
    }

    // ── Drive folder: read-only, changed via a preset/Browse dialog ──────────────────────

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var s = SettingsManager.Current;
        string current = s.EffectiveSyncRootPath;
        bool online = DriveManager.Current.Status == DriveStatus.Online;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        string home = Path.Combine(profile, "ImmichDrive");
        string pics = Path.Combine(pictures, "Immich");

        var paths = new List<string>();
        var radios = new RadioButtons();

        void AddOption(string label, string path, bool select = false)
        {
            int idx = paths.Count;
            paths.Add(path);
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = label });
            panel.Children.Add(new TextBlock { Text = path, Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            radios.Items.Add(new RadioButton { Content = panel });
            if (select) radios.SelectedIndex = idx;
        }

        // Surface the current location first (pre-selected) when it isn't one of the presets.
        if (!PathsEqual(current, home) && !PathsEqual(current, pics))
            AddOption("Current location", current, select: true);
        AddOption("Home folder", home, select: PathsEqual(current, home));
        AddOption("Pictures", pics, select: PathsEqual(current, pics));

        var browseButton = new Button { Content = "Browse for another folder…", Margin = new Thickness(0, 8, 0, 0) };
        browseButton.Click += async (object _, RoutedEventArgs __) =>
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            var window = SettingsWindow.GetCurrent();
            if (window == null) return;
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) AddOption("Chosen folder", folder.Path, select: true);
        };

        var content = new StackPanel { Spacing = 4 };
        if (online)
            content.Children.Add(new TextBlock
            {
                Text = "Your photos re-appear at the new location and the old folder is removed. " +
                       "Nothing is deleted from Immich.",
                Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });
        content.Children.Add(radios);
        content.Children.Add(browseButton);

        var dialog = new ContentDialog
        {
            Title = online ? "Move drive folder" : "Choose drive folder",
            Content = content,
            PrimaryButtonText = online ? "Move drive here" : "Use this folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        int sel = radios.SelectedIndex;
        if (sel < 0 || sel >= paths.Count) return;
        string chosen = paths[sel];
        if (PathsEqual(chosen, current)) return; // no change

        s.SyncRootPath = chosen;
        SettingsManager.SaveSettings();
        FolderBox.Text = s.EffectiveSyncRootPath;

        // If the drive is live, reconnecting relocates it: SyncRootService re-registers at the new
        // path and DriveManager retires (and deletes) the old folder. Otherwise the new path just
        // takes effect on the next Connect.
        if (online) await MoveDriveAsync();
    }

    private async Task MoveDriveAsync()
    {
        Busy.IsActive = true;
        ChangeFolderButton.IsEnabled = false;
        try
        {
            await DriveManager.Current.ConnectAsync();
            var dm = DriveManager.Current;
            ResultBar.IsOpen = true;
            bool ok = dm.Status == DriveStatus.Online;
            ResultBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ResultBar.Title = ok ? "Drive moved" : "Move failed";
            ResultBar.Message = ok
                ? $"The drive is now at {SettingsManager.Current.EffectiveSyncRootPath}."
                : (dm.StatusDetail ?? "");
        }
        finally
        {
            Busy.IsActive = false;
            ChangeFolderButton.IsEnabled = true;
            UpdateButtons();
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        SaveFields();
        Busy.IsActive = true;
        TestButton.IsEnabled = false;
        try
        {
            using var client = new ImmichClient(SettingsManager.Current.ServerUrl, SettingsManager.Current.ApiKey,
                TimeSpan.FromSeconds(15));
            string? who = await client.TestConnectionAsync();
            ResultBar.IsOpen = true;
            if (who != null)
            {
                ResultBar.Severity = InfoBarSeverity.Success;
                ResultBar.Title = "Connected";
                ResultBar.Message = $"Reached Immich as {who}.";
            }
            else
            {
                ResultBar.Severity = InfoBarSeverity.Error;
                ResultBar.Title = "Could not connect";
                ResultBar.Message = "Check the server URL and API key.";
            }
        }
        finally
        {
            Busy.IsActive = false;
            TestButton.IsEnabled = true;
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        SaveFields();
        Busy.IsActive = true;
        ConnectButton.IsEnabled = false;
        try
        {
            await DriveManager.Current.ConnectAsync();
            var dm = DriveManager.Current;
            ResultBar.IsOpen = true;
            ResultBar.Severity = dm.Status == DriveStatus.Online ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            ResultBar.Title = dm.Status == DriveStatus.Online ? "Drive connected" : "Connection failed";
            ResultBar.Message = dm.StatusDetail ?? "";
        }
        finally
        {
            Busy.IsActive = false;
            ConnectButton.IsEnabled = true;
            UpdateButtons();
        }
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        DriveManager.Current.Disconnect();
        ResultBar.IsOpen = true;
        ResultBar.Severity = InfoBarSeverity.Informational;
        ResultBar.Title = "Disconnected";
        ResultBar.Message = "The drive has been disconnected.";
        UpdateButtons();
    }
}
