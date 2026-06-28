using ImmichDrive.Classes.Settings;
using ImmichDrive.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.IO;
using WinRT.Interop;
using static ImmichDrive.Classes.NativeMethods;

namespace ImmichDrive.Windows;

/// <summary>
/// A small borderless flyout shown on a tray left-click. Unlike the right-click context menu
/// (a static snapshot), it subscribes to <see cref="DriveManager.StatusChanged"/> and updates the
/// sync progress <b>live</b>. Dismisses when it loses focus.
/// </summary>
public sealed partial class StatusFlyout : Window
{
    private static StatusFlyout? _instance;
    private AppWindow _appWindow = null!;

    public static void Toggle()
    {
        if (_instance != null) { _instance.Close(); return; }
        _instance = new StatusFlyout();
        _instance.ShowNearTray();
    }

    public StatusFlyout()
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        _appWindow.IsShownInSwitchers = false;

        // Match LittleLauncher's flyout: a context-menu presenter (borderless, top-most,
        // light-dismiss), Desktop Acrylic, and OS rounded corners — no manual border color.
        var presenter = OverlappedPresenter.CreateForContextMenu();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        _appWindow.SetPresenter(presenter);

        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();

        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        if (File.Exists(App.IconPath))
            try { Icon.Source = new BitmapImage(new Uri(App.IconPath)); } catch { }

        Classes.ThemeManager.ApplySavedTheme(this);
        DriveManager.Current.StatusChanged += OnStatusChanged;
        UpdateUi();

        Activated += (s, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) Close();
        };
        Closed += (s, e) =>
        {
            DriveManager.Current.StatusChanged -= OnStatusChanged;
            if (_instance == this) _instance = null;
        };
    }

    private void OnStatusChanged() => DispatcherQueue.TryEnqueue(UpdateUi);

    private void UpdateUi()
    {
        var dm = DriveManager.Current;
        var (done, total) = dm.Progress;
        bool syncing = dm.Status == DriveStatus.Online && total > 0 && done < total;

        StatusText.Text = dm.Status switch
        {
            DriveStatus.Online when syncing => $"Syncing {done:N0} of {total:N0}…",
            DriveStatus.Online => "Up to date",
            DriveStatus.Connecting => "Connecting…",
            DriveStatus.Error => dm.StatusDetail ?? "Problem connecting",
            _ => "Disconnected",
        };
        SyncProgress.Visibility = syncing ? Visibility.Visible : Visibility.Collapsed;
        if (syncing) { SyncProgress.Maximum = total; SyncProgress.Value = done; }

        RefreshButton.IsEnabled = dm.Status == DriveStatus.Online;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        DriveManager.Current.Refresh();   // stays open; progress shows live

    private void ShowNearTray()
    {
        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        double scale = dpi / 96.0;

        // Measure the content so the window fits it exactly (no empty space / scrollbar).
        Root.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = Root.DesiredSize;
        int w = (int)Math.Ceiling((desired.Width > 0 ? desired.Width : 300) * scale);
        int h = (int)Math.Ceiling((desired.Height > 0 ? desired.Height : 150) * scale);

        GetCursorPos(out var pt);
        var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        int x = pt.X, y = pt.Y;
        if (GetMonitorInfo(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST), ref mi))
        {
            int margin = (int)(12 * scale);
            x = mi.rcWork.Right - w - margin;
            y = mi.rcWork.Bottom - h - margin;
        }
        _appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(x, y, w, h));
        Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(this));
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string path = SettingsManager.Current.EffectiveSyncRootPath;
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { }
        Close();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var w = SettingsWindow.GetCurrent() ?? new SettingsWindow();
        w.Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(w));
        Close();
    }
}
