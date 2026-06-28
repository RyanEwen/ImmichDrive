using System.IO;

namespace ImmichDrive.Services;

/// <summary>
/// Resolves the data directory shared between the resident app and the out-of-process
/// thumbnail shell extension. When the app is packaged (MSIX) its <c>%AppData%</c> is
/// redirected per-package, so the app drops a breadcrumb under (non-redirected)
/// <c>%LocalAppData%\ImmichDrive</c> pointing at the real data dir; the extension reads it.
/// WinUI-free and trim-safe — linked into the thumbnail extension.
/// </summary>
public static class SharedPaths
{
    public const string AppFolderName = "ImmichDrive";

    private static string DefaultAppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    private static string BreadcrumbDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

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
