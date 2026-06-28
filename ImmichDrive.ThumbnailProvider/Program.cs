using System.Runtime.InteropServices;

namespace ImmichDrive.ThumbnailProvider;

/// <summary>
/// Out-of-process COM server host. Registers a class factory for the thumbnail provider CLSID,
/// pumps messages while the shell uses it, and exits after a short idle. The shell re-launches it
/// on demand. See <c>.claude/docs/thumbnails.md</c>.
/// </summary>
internal static class Program
{
    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint REGCLS_MULTIPLEUSE = 1;
    private const uint REGCLS_SUSPENDED = 4;
    private const uint WM_QUIT = 0x0012;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    private static long _lastActivityTicks;
    private static uint _mainThreadId;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(ref Guid rclsid, [MarshalAs(UnmanagedType.IUnknown)] object pUnk, uint dwClsContext, uint flags, out uint lpdwRegister);
    [DllImport("ole32.dll")] private static extern int CoRevokeClassObject(uint dwRegister);
    [DllImport("ole32.dll")] private static extern int CoResumeClassObjects();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptx; public int pty; }

    /// <summary>Records activity so the idle watchdog keeps the server alive while in use.</summary>
    internal static void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    [STAThread]
    private static int Main()
    {
        _mainThreadId = GetCurrentThreadId();
        Touch();

        var clsid = new Guid(ComGuids.ThumbnailProviderClsid);
        int hr = CoRegisterClassObject(ref clsid, new ClassFactory(),
            CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE | REGCLS_SUSPENDED, out uint cookie);
        if (hr < 0) return hr;
        CoResumeClassObjects();

        // Idle watchdog: quit the message loop once the shell stops using us for IdleTimeout.
        var watchdog = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(5000);
                long last = Interlocked.Read(ref _lastActivityTicks);
                if (DateTime.UtcNow.Ticks - last > IdleTimeout.Ticks)
                {
                    PostThreadMessage(_mainThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                    break;
                }
            }
        }) { IsBackground = true };
        watchdog.Start();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        CoRevokeClassObject(cookie);
        return 0;
    }
}

/// <summary>Class factory that hands out <see cref="ImmichThumbnailProvider"/> instances to the shell.</summary>
[ComVisible(true)]
internal sealed class ClassFactory : IClassFactory
{
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        Program.Touch();
        if (pUnkOuter != IntPtr.Zero) return CLASS_E_NOAGGREGATION;

        IntPtr unk = Marshal.GetIUnknownForObject(new ImmichThumbnailProvider());
        try { return Marshal.QueryInterface(unk, ref riid, out ppvObject); }
        finally { Marshal.Release(unk); }
    }

    public int LockServer(bool fLock) { Program.Touch(); return 0; }
}
