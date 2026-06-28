using ImmichDrive.Services;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ImmichDrive.ThumbnailProvider;

/// <summary>
/// In-process COM thumbnail handler for ImmichDrive placeholder files. Resolves the file path
/// → Immich asset id via the shared SQLite index and fetches Immich's small thumbnail over HTTP
/// — without ever opening (hydrating) the file. Runs inside the shell, so it stays lean and
/// fails fast/quiet. See <c>.claude/docs/thumbnails.md</c>.
/// </summary>
[ComVisible(true)]
[Guid(ComGuids.ThumbnailProviderClsid)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ImmichThumbnailProvider : IThumbnailProvider, IInitializeWithItem
{
    private const int S_OK = 0;
    private const int E_FAIL = unchecked((int)0x80004005);
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(5);

    private string? _path;

    public int Initialize(IShellItem psi, uint grfMode)
    {
        try
        {
            // SIGDN_FILESYSPATH gives the path without opening a stream (no hydration).
            int hr = psi.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out IntPtr p);
            if (hr < 0 || p == IntPtr.Zero) { Log($"Initialize: GetDisplayName hr=0x{hr:X8}"); return E_FAIL; }
            try { _path = Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
            Log($"Initialize: path={_path}");
            return string.IsNullOrEmpty(_path) ? E_FAIL : S_OK;
        }
        catch (Exception ex) { Log($"Initialize EX: {ex.Message}"); return E_FAIL; }
    }

    public int GetThumbnail(uint cx, out IntPtr phbmp, out WTS_ALPHATYPE pdwAlpha)
    {
        phbmp = IntPtr.Zero;
        pdwAlpha = WTS_ALPHATYPE.WTSAT_RGB;

        try
        {
            if (string.IsNullOrEmpty(_path)) { Log("GetThumbnail: no path"); return E_FAIL; }

            // Only handle files inside the sync root; otherwise let the shell's default handler run.
            string syncRoot = SettingsFile.ReadSyncRoot();
            string full = Path.GetFullPath(_path);
            if (!full.StartsWith(EnsureSep(syncRoot), StringComparison.OrdinalIgnoreCase))
            { Log($"GetThumbnail: outside syncRoot ({syncRoot}) path={full}"); return E_FAIL; }

            string rel = full[EnsureSep(syncRoot).Length..];
            string? assetId = new AssetIndex().TryGetAssetId(rel);
            if (string.IsNullOrEmpty(assetId)) { Log($"GetThumbnail: no asset for rel={rel} (db={SharedPaths.IndexDbPath})"); return E_FAIL; }

            var conn = SettingsFile.ReadConnection();
            if (conn is not { } c) { Log("GetThumbnail: no connection in settings"); return E_FAIL; }

            byte[]? bytes = FetchThumbnail(c.ServerUrl, c.ApiKey, assetId);
            if (bytes is null || bytes.Length == 0) { Log($"GetThumbnail: empty thumb for {assetId}"); return E_FAIL; }

            phbmp = ToHBitmap(bytes, (int)cx);
            Log($"GetThumbnail: OK {assetId} cx={cx} bytes={bytes.Length} hbmp={(phbmp != IntPtr.Zero)}");
            return phbmp == IntPtr.Zero ? E_FAIL : S_OK;
        }
        catch (Exception ex) { Log($"GetThumbnail EX: {ex}"); return E_FAIL; }
    }

    /// <summary>Best-effort diagnostic log to a fixed, non-redirected path the shell surrogate can write.</summary>
    private static void Log(string msg)
    {
        try
        {
            string dir = @"C:\ProgramData\ImmichDrive";
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "thumb.log"), $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
        }
        catch { }
    }

    private static byte[]? FetchThumbnail(string url, string key, string assetId)
    {
        using var client = new ImmichClient(url, key, HttpTimeout);
        // Block briefly; the shell calls us on a worker thread. preview = JPEG (decodable by GDI+).
        return Task.Run(() => client.GetThumbnailBytesAsync(assetId, "preview")).GetAwaiter().GetResult();
    }

    private static IntPtr ToHBitmap(byte[] bytes, int cx)
    {
        using var src = (Bitmap)Image.FromStream(new MemoryStream(bytes));
        double ratio = Math.Min((double)cx / src.Width, (double)cx / src.Height);
        if (ratio <= 0 || double.IsInfinity(ratio)) ratio = 1;
        int w = Math.Max(1, (int)Math.Round(src.Width * ratio));
        int h = Math.Max(1, (int)Math.Round(src.Height * ratio));

        using var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, 0, 0, w, h);
        }
        // The shell takes ownership of the returned HBITMAP and deletes it.
        return dst.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
    }

    private static string EnsureSep(string dir) =>
        dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
}
