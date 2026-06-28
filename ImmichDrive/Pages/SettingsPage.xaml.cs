using ImmichDrive.Classes;
using ImmichDrive.Classes.Settings;
using Microsoft.UI.Xaml.Controls;

namespace ImmichDrive.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        var s = SettingsManager.Current;
        ThemeCombo.SelectedIndex = Math.Clamp(s.AppTheme, 0, 2);
        StartupToggle.IsOn = s.Startup;
        _loading = false;
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ThemeManager.ApplyAndSaveTheme(ThemeCombo.SelectedIndex);
    }

    private void StartupToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsManager.Current.Startup = StartupToggle.IsOn; // side-effect sets the Run key
        SettingsManager.SaveSettings();
    }
}
