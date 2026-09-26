using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace ImmichDrive.Services;

/// <summary>
/// Watches the writable <c>Upload</c> folder. Any file dropped there is uploaded to Immich and then
/// deleted locally — it reappears in its date/album folders on the next sync (its "final
/// destination"). The folder is the one place the user can add to the otherwise read-only drive.
/// </summary>
public sealed class UploadService : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    public const string UploadFolderName = "Upload";

    private readonly ImmichClient _client;
    private readonly string _uploadDir;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private int _busy;
    private int _folderChangeVersion;
    private int _reportedFolderVersion = -1;
    private bool? _reportedInSync;

    public UploadService(ImmichClient client, string uploadDir)
    {
        _client = client;
        _uploadDir = uploadDir;
    }

    public void Start()
    {
        Directory.CreateDirectory(_uploadDir);

        _watcher = new FileSystemWatcher(_uploadDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                NotifyFilters.Size | NotifyFilters.LastWrite,
        };
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += (s, e) => Enqueue(e.FullPath);
        _watcher.EnableRaisingEvents = true;

        // Watch first so a drop during the initial scan cannot be missed.
        foreach (var f in SafeEnumerate()) _pending[f] = 0;
        RefreshFolderSyncState();

        _timer = new Timer(_ => _ = ProcessPendingAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Enqueue(e.FullPath);

    private void Enqueue(string path)
    {
        Interlocked.Increment(ref _folderChangeVersion);
        if (File.Exists(path)) _pending[path] = 0;
    }

    private async Task ProcessPendingAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            // Report pending before a potentially slow upload starts.
            RefreshFolderSyncState();
            foreach (var path in _pending.Keys.ToList())
            {
                if (!File.Exists(path)) { _pending.TryRemove(path, out _); continue; }
                if (!IsStable(path)) continue;                 // still being written/copied
                _pending.TryRemove(path, out _);

                bool ok = await _client.UploadAssetAsync(path);
                if (ok)
                {
                    try { File.Delete(path); } catch (Exception ex) { Logger.Warn(ex, "Delete after upload failed: {0}", path); }
                    Logger.Info("Uploaded {0}", Path.GetFileName(path));
                }
                else
                {
                    Logger.Warn("Upload failed (left in Upload for retry): {0}", Path.GetFileName(path));
                }
            }
            CleanEmptySubfolders();
        }
        catch (Exception ex) { Logger.Warn(ex, "Upload processing failed"); }
        finally
        {
            // Failed uploads leave files behind even though they are no longer in _pending.
            RefreshFolderSyncState();
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>
    /// Reports completion from the actual folder contents, not the processing queue. A failed
    /// upload, unfinished copy, or failed local deletion must keep the folder pending. Successful
    /// reports are cached until contents change; failed state updates are retried on the next tick.
    /// </summary>
    private void RefreshFolderSyncState()
    {
        int version = Volatile.Read(ref _folderChangeVersion);
        bool inSync;
        try
        {
            inSync = !Directory.EnumerateFiles(_uploadDir, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn(ex, "Could not inspect Upload folder sync state");
            inSync = false;
        }

        // A concurrent drop invalidates an empty snapshot. The next tick will inspect it again.
        if (version != Volatile.Read(ref _folderChangeVersion)) inSync = false;
        if (_reportedInSync == inSync && _reportedFolderVersion == version) return;

        if (CloudFolderState.SetInSync(_uploadDir, inSync))
        {
            _reportedInSync = inSync;
            _reportedFolderVersion = version;
        }
    }

    /// <summary>A file is "stable" once it can be opened exclusively (nothing else is still writing it).</summary>
    private static bool IsStable(string path)
    {
        try
        {
            using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return s.Length > 0;
        }
        catch { return false; }
    }

    private IEnumerable<string> SafeEnumerate()
    {
        // Materialize inside the try: lazy enumeration can throw while traversing subfolders.
        try { return Directory.GetFiles(_uploadDir, "*", SearchOption.AllDirectories); }
        catch { return []; }
    }

    private void CleanEmptySubfolders()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_uploadDir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
        catch { }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); }
    }
}
