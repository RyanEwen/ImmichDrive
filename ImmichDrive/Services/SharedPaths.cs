using System.IO;

namespace ImmichDrive.Services;

/// <summary>
/// Resolves the data directory shared between the resident app and the out-of-process
/// thumbnail shell extension. When the app is packaged (MSIX) <b>both</b> <c>%AppData%</c> and
/// <c>%LocalAppData%</c> are redirected per-package into <c>…\Packages\&lt;family&gt;\LocalCache\…</c>,
/// so each package keeps its own data dir. The app drops a breadcrumb at a <b>fixed, never-redirected</b>
/// machine location — <c>C:\ProgramData\ImmichDrive\location.txt</c> — pointing at its real (redirected)
/// data dir; the thumbnail process reads it from there regardless of whether the shell launched it with
/// package identity. (An earlier version used <c>%LocalAppData%</c>, which is itself redirected, so the
/// breadcrumb was invisible across identities and thumbnails silently failed.)
/// WinUI-free and trim-safe — linked into the thumbnail extension.
/// </summary>
public static class SharedPaths
{
    public const string AppFolderName = "ImmichDrive";

    private static string DefaultAppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    // CommonApplicationData = C:\ProgramData (machine-wide, NOT package-redirected), so the app and the
    // thumbnail process resolve the exact same absolute path even with different/no package identity.
    private static string BreadcrumbDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolderName);

    private static string BreadcrumbPath => Path.Combine(BreadcrumbDir, "location.txt");

    /// <summary>The resolved data directory (settings.json, index.db, logs).</summary>
    public static string DataDir
    {
        get
        {
            try
            {
                if (File.Exists(BreadcrumbPath))
                {
                    string dir = File.ReadAllText(BreadcrumbPath).Trim();
                    if (dir.Length > 0 && Directory.Exists(dir)) return dir;
                }
            }
            catch { /* fall through to default */ }
            return DefaultAppDataDir;
        }
    }

    public static string SettingsFilePath => Path.Combine(DataDir, "settings.json");
    public static string IndexDbPath => Path.Combine(DataDir, "index.db");

    /// <summary>Called once at app startup to record where this (possibly packaged) process stores data.</summary>
    public static void WriteBreadcrumb()
    {
        try
        {
            string dir = DefaultAppDataDir;
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(BreadcrumbDir);
            File.WriteAllText(BreadcrumbPath, dir);
        }
        catch { /* best effort */ }
    }
}
