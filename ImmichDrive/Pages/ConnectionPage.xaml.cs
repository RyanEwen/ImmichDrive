using ImmichDrive.Classes.Settings;
using ImmichDrive.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        s.SyncRootPath = FolderBox.Text.Trim();
        SettingsManager.SaveSettings();
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
