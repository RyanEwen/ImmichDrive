using ImmichDrive.Classes.Settings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.IO;
using static ImmichDrive.Classes.NativeMethods;

namespace ImmichDrive;

/// <summary>
/// Application entry point (WinUI 3). Unlike Repilot, this process is RESIDENT: it owns
/// the tray icon and hosts the Cloud Files sync provider for the lifetime of the session.
/// The visible <see cref="SettingsWindow"/> is shown on demand (tray menu / first run).
/// </summary>
public partial class App : Application
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public static DispatcherQueue MainDispatcherQueue { get; private set; } = null!;

    /// <summary>Path to the multi-size app icon (.ico) for Win32 window/tray/taskbar surfaces.</summary>
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Resources", "ImmichDrive.ico");

    /// <summary>Path to a high-res PNG of the app icon. XAML <c>Image</c> elements decode this and
    /// scale it down crisply for whatever DPI they render at — a <c>BitmapImage</c> over the .ico
    /// would grab the tiny 16px frame and upscale it (blurry).</summary>
    public static string IconImagePath => Path.Combine(AppContext.BaseDirectory, "Resources", "ImmichDrive.png");

    // Single resident instance — a second launch just surfaces settings on the first.
    private static readonly Mutex Singleton = new(true, "ImmichDrive.Instance");

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainDispatcherQueue = DispatcherQueue.GetForCurrentThread();

        if (!Singleton.WaitOne(TimeSpan.Zero, true))
        {
            // Already running — tell the existing instance to open settings, then exit.
            IntPtr existing = FindWindow(null, MainWindow.HostWindowTitle);
            if (existing != IntPtr.Zero)
                PostMessage(existing, MainWindow.WmShowSettings, IntPtr.Zero, IntPtr.Zero);
            Environment.Exit(0);
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (s, a) =>
        {
            Logger.Error(a.ExceptionObject as Exception, "Unhandled exception");
            NLog.LogManager.Flush();
        };
        UnhandledException += (s, e) =>
        {
            Logger.Error(e.Exception, "Unhandled UI exception");
            NLog.LogManager.Flush();
            e.Handled = true;
        };

        SettingsManager.RestoreSettings();

        // The invisible host owns the tray icon and the sync provider. It shows the
        // settings window itself on first run (when no server is configured yet).
        _host = new MainWindow();
        _host.Activate();
        _host.HideHost(); // Activate() shows the window; immediately re-hide the invisible host.
    }

    private MainWindow? _host;
}
