using ImmichDrive.Classes.Settings;
using Microsoft.UI.Xaml;

namespace ImmichDrive.Classes;

/// <summary>Applies the selected app theme (WinUI 3 ElementTheme) to the settings window.</summary>
internal static class ThemeManager
{
    public static void ApplySavedTheme(Window window) => Apply(SettingsManager.Current.AppTheme, window);

    public static void ApplyAndSaveTheme(int theme)
    {
        SettingsManager.Current.AppTheme = theme;
        SettingsManager.SaveSettings();
        var w = SettingsWindow.GetCurrent();
        if (w != null) Apply(theme, w);
    }

    private static void Apply(int theme, Window window)
    {
        var requested = theme switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        if (window.Content is FrameworkElement fe) fe.RequestedTheme = requested;
    }

    private static readonly global::Windows.UI.ViewManagement.UISettings s_uiSettings = new();

    public static bool IsDarkTheme()
    {
        var fg = s_uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Foreground);
        return fg.R > 128;
    }
}
