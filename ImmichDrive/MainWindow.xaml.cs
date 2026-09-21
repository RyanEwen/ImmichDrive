using ImmichDrive.Classes.Settings;
using ImmichDrive.Services;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WinRT.Interop;
using static ImmichDrive.Classes.NativeMethods;

namespace ImmichDrive;

/// <summary>
/// The invisible host window. Owns the tray icon and hosts the sync provider for the life of
/// the process; the visible UI is the on-demand <see cref="SettingsWindow"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    public const string HostWindowTitle = "ImmichDrive Host";

    /// <summary>Posted by a second launch to ask the running instance to show settings.</summary>
    public static readonly uint WmShowSettings = RegisterWindowMessage("ImmichDrive_ShowSettings");
    private static readonly uint WmTrayCallback = RegisterWindowMessage("ImmichDrive_TrayCallback");

    /// <summary>Broadcast by the shell when Explorer (re)starts; we must re-add the tray icon.</summary>
    private static readonly uint WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    // Tray menu command ids.
    private const uint CmdOpen = 1, CmdFolder = 2, CmdRefresh = 3, CmdToggle = 4, CmdExit = 5, CmdRetry = 6;
    private const uint TrayIconId = 1;

    private IntPtr _hwnd;
    private SUBCLASSPROC? _subclass;     // kept rooted
    private IntPtr _hIcon;
    private bool _mutedIconShown;        // which .ico variant _hIcon currently holds
    private bool _trayAdded;
    private Microsoft.UI.Windowing.AppWindow? _appWindow;

    public MainWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Make it a tool window (no taskbar button, no Alt+Tab) and keep it out of switchers.
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));

        var wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(wndId);
        _appWindow.IsShownInSwitchers = false;
        HideHost();

        _subclass = WndProc;
        SetWindowSubclass(_hwnd, _subclass, IntPtr.Zero, IntPtr.Zero);

        AddTrayIcon();
        DriveManager.Current.StatusChanged += OnDriveStatusChanged;

        // First run with no config → show settings; otherwise connect the drive in the background.
        if (DriveManager.Current.IsConfigured)
            _ = DriveManager.Current.ConnectAsync();
        else
            ShowSettings();
    }

    /// <summary>
    /// Keeps the host window off-screen/invisible. Called from the constructor and again
    /// after <c>Activate()</c> (which would otherwise re-show it). Uses AppWindow.Hide()
    /// rather than just SW_HIDE so WinUI's own show-on-activate is overridden.
    /// </summary>
    public void HideHost()
    {
        _appWindow?.Hide();
        ShowWindow(_hwnd, SW_HIDE);
    }

    // ── Tray icon ───────────────────────────────────────────────────
    /// <summary>
    /// True when the drive isn't usable, so the tray should show the muted icon. Connecting keeps the
    /// normal one — a brief startup blink between the two variants would be worse than no signal.
    /// </summary>
    private static bool WantsMutedIcon =>
        DriveManager.Current.Status is DriveStatus.Offline or DriveStatus.Error or DriveStatus.Disconnected;

    private void AddTrayIcon()
    {
        LoadTrayIcon(WantsMutedIcon);
        var data = NewIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WmTrayCallback;
        data.hIcon = _hIcon;
        data.szTip = "Drive for Immich";
        _trayAdded = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void LoadTrayIcon(bool muted)
    {
        if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
        // Load a 32px frame (not 16): the shell downscales it to the DPI-scaled tray slot, which
        // stays crisp. Loading 16 forces an upscale on high-DPI displays -> blurry.
        string path = muted ? App.OfflineIconPath : App.IconPath;
        _hIcon = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
        if (_hIcon == IntPtr.Zero && muted)   // muted variant missing — better the normal icon than none
            _hIcon = LoadImage(IntPtr.Zero, App.IconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
        _mutedIconShown = muted;
    }

    /// <summary>
    /// Reflects the drive state in the tray: the icon goes muted when the drive isn't live and the
    /// tooltip says why. Deliberately silent — no balloon (NIF_INFO), so nothing pops or chimes.
    /// </summary>
    private void UpdateTray()
    {
        if (!_trayAdded) return;
        var dm = DriveManager.Current;

        bool muted = WantsMutedIcon;
        if (muted != _mutedIconShown)
        {
            LoadTrayIcon(muted);
            var iconData = NewIconData();
            iconData.uFlags = NIF_ICON;
            iconData.hIcon = _hIcon;
            Shell_NotifyIcon(NIM_MODIFY, ref iconData);
        }

        string tip = dm.Status switch
        {
            DriveStatus.Online when dm.IsPopulating && dm.Progress.Total > dm.Progress.Done
                => $"Drive for Immich: syncing {dm.Progress.Done}/{dm.Progress.Total}",
            DriveStatus.Online when dm.IsPopulating => "Drive for Immich: finishing sync",
            DriveStatus.Online when dm.SyncIssue != null => "Drive for Immich: sync needs attention",
            DriveStatus.Online => $"Drive for Immich — online",
            DriveStatus.Connecting => "Drive for Immich — connecting…",
            DriveStatus.Offline => "Drive for Immich — can't reach Immich, retrying…",
            DriveStatus.Error => $"Drive for Immich — {dm.StatusDetail}",
            _ => "Drive for Immich — disconnected",
        };
        var data = NewIconData();
        data.uFlags = NIF_TIP;
        data.szTip = tip.Length > 127 ? tip[..127] : tip;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void RemoveTrayIcon()
    {
        if (!_trayAdded) return;
        var data = NewIconData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _trayAdded = false;
        if (_hIcon != IntPtr.Zero) { DestroyIcon(_hIcon); _hIcon = IntPtr.Zero; }
    }

    private NOTIFYICONDATA NewIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = TrayIconId,
    };

    // Throttle tray updates: during a sync, StatusChanged fires once per asset (thousands of
    // times). Rewriting the tray tooltip that fast makes the icon flicker. Coalesce to ~1/sec with
    // a trailing update so the final "Up to date" still lands.
    private long _lastTipTicks;
    private bool _tipUpdatePending;

    private void OnDriveStatusChanged()
    {
        if (_tipUpdatePending) return;
        long now = Environment.TickCount64;
        long since = now - _lastTipTicks;
        if (since >= 750)
        {
            _lastTipTicks = now;
            App.MainDispatcherQueue.TryEnqueue(UpdateTray);
        }
        else
        {
            _tipUpdatePending = true;
            _ = Task.Delay((int)(750 - since)).ContinueWith(_ =>
            {
                _tipUpdatePending = false;
                _lastTipTicks = Environment.TickCount64;
                App.MainDispatcherQueue.TryEnqueue(UpdateTray);
            });
        }
    }

    // ── Window procedure ────────────────────────────────────────────
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (msg == WmShowSettings)
        {
            ShowSettings();
            return IntPtr.Zero;
        }
        if (msg == WmTaskbarCreated)
        {
            // Explorer restarted — our tray icon was lost; re-add it.
            _trayAdded = false;
            AddTrayIcon();
            UpdateTray();
            return IntPtr.Zero;
        }
        if (msg == WmTrayCallback)
        {
            int evt = (int)(lParam.ToInt64() & 0xFFFF); // classic model: lParam = mouse message
            switch (evt)
            {
                case WM_LBUTTONUP: ImmichDrive.Windows.StatusFlyout.Toggle(); break;   // live status
                case WM_LBUTTONDBLCLK: ShowSettings(); break;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU: ShowTrayMenu(); break;
            }
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    // ── Context menu ────────────────────────────────────────────────
    private void ShowTrayMenu()
    {
        var dm = DriveManager.Current;
        IntPtr menu = CreatePopupMenu();

        // Status line (non-clickable).
        var (done, total) = dm.Progress;
        string status = dm.Status switch
        {
            DriveStatus.Online when dm.IsPopulating && total > done => $"Syncing {done:N0} of about {total:N0}…",
            DriveStatus.Online when dm.IsPopulating => "Finishing sync…",
            DriveStatus.Online when dm.SyncIssue != null => "Some photos could not be synced",
            DriveStatus.Online => "Up to date",
            DriveStatus.Connecting => "Connecting…",
            DriveStatus.Offline => "Can't reach Immich",
            DriveStatus.Error => "Problem connecting",
            _ => "Disconnected",
        };
        AppendMenu(menu, MF_STRING | MF_GRAYED, 0, status);
        AppendMenu(menu, MF_SEPARATOR, 0, null);

        if (dm.CanRetry)
        {
            AppendMenu(menu, dm.IsCheckingConnection ? MF_STRING | MF_GRAYED : MF_STRING, CmdRetry,
                dm.IsCheckingConnection ? "Checking…" : "Try again");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
        }

        AppendMenu(menu, MF_STRING, CmdOpen, "Settings");
        AppendMenu(menu, MF_STRING, CmdFolder, "Open drive folder");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, dm.Status == DriveStatus.Online ? MF_STRING : MF_STRING | MF_GRAYED, CmdRefresh, "Refresh");
        // Offline still counts as "on" — Disconnect is how the user stops the drive (and its retries).
        bool driveOn = dm.Status is DriveStatus.Online or DriveStatus.Offline;
        AppendMenu(menu, MF_STRING, CmdToggle, driveOn ? "Disconnect" : "Connect");
        AppendMenu(menu, MF_SEPARATOR, 0, null);
        AppendMenu(menu, MF_STRING, CmdExit, "Exit");

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd); // required so the menu dismisses on outside click
        uint cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);

        switch (cmd)
        {
            case CmdOpen: ShowSettings(); break;
            case CmdFolder: OpenDriveFolder(); break;
            case CmdRefresh: dm.Refresh(); break;
            case CmdRetry: _ = dm.RetryConnectionAsync(); break;
            case CmdToggle:
                if (driveOn) dm.Disconnect();
                else _ = dm.ConnectAsync();
                break;
            case CmdExit: ExitApp(); break;
        }
    }

    private void ShowSettings()
    {
        var w = SettingsWindow.GetCurrent() ?? new SettingsWindow();
        w.Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(w));
    }

    private static void OpenDriveFolder()
    {
        string path = SettingsManager.Current.EffectiveSyncRootPath;
        try { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private void ExitApp()
    {
        DriveManager.Current.StatusChanged -= OnDriveStatusChanged;
        RemoveTrayIcon();
        if (_subclass != null) RemoveWindowSubclass(_hwnd, _subclass, IntPtr.Zero);
        Application.Current.Exit();
    }
}
