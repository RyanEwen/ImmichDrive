using System.IO;
using System.Text.Json;

namespace ImmichDrive.Services;

/// <summary>
/// Minimal read-only view of <c>settings.json</c> for the out-of-process thumbnail extension,
/// which only needs the server URL + API key (and must not pull in WinUI / CommunityToolkit /
/// the full <c>UserSettings</c> type). The resident app uses the full <c>SettingsManager</c>.
/// WinUI-free and trim-safe — linked into the thumbnail extension.
/// </summary>
public static class SettingsFile
{
    public static (string ServerUrl, string ApiKey)? ReadConnection()
    {
        try
        {
            string path = SharedPaths.SettingsFilePath;
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string url = root.TryGetProperty("ServerUrl", out var u) ? u.GetString() ?? "" : "";
            string key = root.TryGetProperty("ApiKey", out var k) ? k.GetString() ?? "" : "";
            if (url.Length == 0 || key.Length == 0) return null;
            return (url, key);
        }
        catch { return null; }
    }

    /// <summary>The drive folder (sync root), falling back to the default if unset.</summary>
    public static string ReadSyncRoot()
    {
        try
        {
            string path = SharedPaths.SettingsFilePath;
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("SyncRootPath", out var p) &&
                    p.GetString() is { Length: > 0 } s)
                    return s;
            }
        }
        catch { /* fall through */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ImmichDrive");
    }
}
