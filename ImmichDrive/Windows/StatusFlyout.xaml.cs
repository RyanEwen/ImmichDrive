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

        if (File.Exists(App.IconImagePath))
            try { Icon.Source = new BitmapImage(new Uri(App.IconImagePath)); } catch { }

        Classes.ThemeManager.ApplySavedTheme(this);
        DriveManager.Current.StatusChanged += OnStatusChanged;
        UpdateUi();

        Activated += (s, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) Close();
        };
        Closed += (s, e) =>
        {
            _closed = true;
            DriveManager.Current.StatusChanged -= OnStatusChanged;
            if (_instance == this) _instance = null;
        };
    }

    /// <summary>Set on Close so a status update already queued on the dispatcher doesn't touch a
    /// torn-down window (the flyout closes the moment it loses focus, so this is easy to hit).</summary>
    private bool _closed;

    private void OnStatusChanged() => DispatcherQueue.TryEnqueue(UpdateUi);

    private void UpdateUi()
    {
        if (_closed) return;
        var dm = DriveManager.Current;
        var (done, total) = dm.Progress;
        bool syncing = dm.Status == DriveStatus.Online && dm.IsPopulating;
        bool checking = dm.IsCheckingConnection;

        StatusText.Text = checking ? "Checking the connection…" : dm.Status switch
        {
            DriveStatus.Online when syncing && total > done => $"Syncing {done:N0} of about {total:N0}…",
            DriveStatus.Online when syncing => "Finishing sync…",
            DriveStatus.Online when dm.SyncIssue != null => "Some photos could not be synced",
            DriveStatus.Online => "Up to date",
            DriveStatus.Connecting => "Connecting…",
            DriveStatus.Offline => "Can't reach Immich",
            DriveStatus.Error => dm.StatusDetail ?? "Problem connecting",
            _ => "Disconnected",
        };

        StatusDot.Fill = (Microsoft.UI.Xaml.Media.Brush)Root.Resources[
            checking || dm.Status == DriveStatus.Connecting ? "DotBusy" : dm.Status switch
            {
                DriveStatus.Online => "DotOnline",
                DriveStatus.Offline => "DotWarning",
                DriveStatus.Error => "DotError",
                _ => "DotIdle",
            }];

        // While offline, say how stale the view is rather than leaving the user guessing. The retries
        // are silent by design, so this line is the only place they're mentioned. (Error needs no
        // second line — its StatusText is already the detail.)
        string? detail = dm.Status switch
        {
            DriveStatus.Offline when checking => null,
            DriveStatus.Offline => $"Your photos are still listed{LastContactSuffix(dm)}. Retrying in the background.",
            DriveStatus.Online when dm.SyncIssue != null => dm.SyncIssue,
            _ => null,
        };
        DetailText.Text = detail ?? "";
        DetailText.Visibility = detail is null ? Visibility.Collapsed : Visibility.Visible;

        SyncProgress.Visibility = syncing ? Visibility.Visible : Visibility.Collapsed;
        if (syncing) { SyncProgress.Maximum = Math.Max(total, 1); SyncProgress.Value = Math.Min(done, total); }

        RetryButton.Visibility = dm.CanRetry || checking ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.IsEnabled = !checking;
        RetryText.Text = checking ? "Checking…" : "Try again";
        RefreshButton.IsEnabled = dm.Status == DriveStatus.Online;

        if (_corner != null) Place();   // rows appear/disappear as the state changes — refit
    }

    /// <summary>", last updated 12:04" when we ever got through this session, otherwise nothing.</summary>
    private static string LastContactSuffix(DriveManager dm) =>
        dm.LastContactUtc > DateTimeOffset.MinValue
            ? $", last updated {dm.LastContactUtc.ToLocalTime():t}"
            : "";

    private void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        DriveManager.Current.Refresh();   // stays open; progress shows live

    private void RetryButton_Click(object sender, RoutedEventArgs e) =>
        _ = DriveManager.Current.RetryConnectionAsync();   // stays open; the status updates live

    private void ShowNearTray()
    {
        _corner = null;
        Place();
        Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(this));
    }

    /// <summary>The bottom-right corner the flyout is pinned to, captured on first placement so later
    /// resizes (the Try-again button appearing/disappearing) grow upward instead of drifting.</summary>
    private (int X, int Y)? _corner;

    private (int W, int H) _size;

    /// <summary>
    /// Sizes the window to its content and pins it to the tray corner. A no-op when the content
    /// hasn't changed size — <see cref="UpdateUi"/> runs once per synced asset, and moving the
    /// window that often would make it judder.
    /// </summary>
    private void Place()
    {
        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        double scale = dpi / 96.0;

        // Measure the content so the window fits it exactly (no empty space / scrollbar).
        Root.Measure(new global::Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = Root.DesiredSize;
        int w = (int)Math.Ceiling((desired.Width > 0 ? desired.Width : 300) * scale);
        int h = (int)Math.Ceiling((desired.Height > 0 ? desired.Height : 150) * scale);
        if (_corner != null && (w, h) == _size) return;
        _size = (w, h);

        if (_corner == null)
        {
            GetCursorPos(out var pt);
            var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST), ref mi))
            {
                int margin = (int)(12 * scale);
                _corner = (mi.rcWork.Right - margin, mi.rcWork.Bottom - margin);
            }
            else _corner = (pt.X + w, pt.Y + h);
        }

        _appWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(_corner.Value.X - w, _corner.Value.Y - h, w, h));
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string path = SettingsManager.Current.EffectiveSyncRootPath;
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { }
        Close();
    }

    private void OpenImmichButton_Click(object sender, RoutedEventArgs e)
    {
        string url = SettingsManager.Current.ServerUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
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
