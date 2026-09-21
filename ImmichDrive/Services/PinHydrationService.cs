using ImmichDrive.Classes.CloudFilter;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace ImmichDrive.Services;

/// <summary>
/// Turns Explorer's pin attribute changes into downloads. Pin state is user intent, not a fetch:
/// Cloud Files only asks the provider for bytes after a file is opened or explicitly hydrated.
/// </summary>
public sealed class PinHydrationService : IDisposable
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private const FileAttributes Pinned = (FileAttributes)0x00080000; // FILE_ATTRIBUTE_PINNED
    private const FileAttributes Unpinned = (FileAttributes)0x00100000; // FILE_ATTRIBUTE_UNPINNED
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private readonly string _root;
    private readonly Func<bool> _canHydrate;
    private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly Timer _progressTimer;
    private readonly object _progressLock = new();
    private readonly HashSet<string> _trackedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completedTrackedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _foldersToRefresh = new(StringComparer.OrdinalIgnoreCase);
    private (int Done, int Total, bool Active) _lastReportedProgress;
    private int _busy;
    private int _scanning;
    private int _scanRequested;
    private long _changeNumber;

    public bool HasFailures => !_failed.IsEmpty;
    public event Action? FailureStateChanged;
    public event Action? ProgressChanged;

    /// <summary>Files hydrated during the current pin operation. Total grows as folders are scanned.</summary>
    public (int Done, int Total, bool Active) Progress
    {
        get
        {
            lock (_progressLock)
                return (_completedTrackedFiles.Count, _trackedFiles.Count,
                    _trackedFiles.Count > _completedTrackedFiles.Count ||
                    Volatile.Read(ref _scanning) != 0 || Volatile.Read(ref _busy) != 0);
        }
    }

    public PinHydrationService(string root, Func<bool> canHydrate)
    {
        _root = Path.GetFullPath(root);
        _canHydrate = canHydrate;
        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.Attributes | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += (_, e) => QueueIfRelevant(e.FullPath);
        _watcher.Error += (_, e) =>
        {
            Logger.Warn(e.GetException(), "Pin change watcher lost events; rescanning pinned files");
            RequestScan();
        };
        _watcher.EnableRaisingEvents = true;

        // A restart or an event lost while disconnected must not strand pinned placeholders.
        RequestScan();
        _timer = new Timer(_ => _ = Task.Run(ProcessPending), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(15));
        _progressTimer = new Timer(_ => ReportProgress(), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => QueueIfRelevant(e.FullPath);

    /// <summary>Ignore ordinary placeholder creation and hydration events in large libraries.</summary>
    private void QueueIfRelevant(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & Pinned) != 0) Queue(path);
            }
            else
            {
                bool needsDownload = NeedsHydration(attributes) && IsPinned(path, attributes);
                if ((attributes & Unpinned) != 0 || needsDownload)
                    Queue(path, trackHydration: needsDownload);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Queue an attribute change, excluding the writable Upload area and root metadata.</summary>
    private void Queue(string path, bool trackHydration = false)
    {
        string relative = Path.GetRelativePath(_root, path);
        if (relative == "desktop.ini" || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relative.Equals(UploadService.UploadFolderName, StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(UploadService.UploadFolderName + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) return;
        _pending[path] = Interlocked.Increment(ref _changeNumber);
        if (!trackHydration) return;

        lock (_progressLock)
        {
            if (_trackedFiles.Count == _completedTrackedFiles.Count && _pending.Count <= 1)
            {
                _trackedFiles.Clear();
                _completedTrackedFiles.Clear();
            }
            _trackedFiles.Add(path);
            for (string? folder = Path.GetDirectoryName(path);
                 folder != null && !folder.Equals(_root, StringComparison.OrdinalIgnoreCase) &&
                 folder.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                 folder = Path.GetDirectoryName(folder))
                _foldersToRefresh.Add(folder);
        }
    }

    /// <summary>Coalesce overflow and startup scans without losing a request during a scan.</summary>
    private void RequestScan()
    {
        Interlocked.Exchange(ref _scanRequested, 1);
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                while (!_stop.IsCancellationRequested && Interlocked.Exchange(ref _scanRequested, 0) != 0)
                    ScanPinnedFiles();
            }
            finally
            {
                Interlocked.Exchange(ref _scanning, 0);
                if (!_stop.IsCancellationRequested && Volatile.Read(ref _scanRequested) != 0)
                    RequestScan();
            }
        });
    }

    /// <summary>Reconcile pins already on disk, including those set while the app was closed.</summary>
    private void ScanPinnedFiles()
    {
        try
        {
            if ((File.GetAttributes(_root) & Pinned) != 0) Queue(_root);
            var dirs = new Stack<string>();
            dirs.Push(_root);
            while (dirs.Count > 0 && !_stop.IsCancellationRequested)
            {
                string dir = dirs.Pop();
                try
                {
                    foreach (string child in Directory.EnumerateFileSystemEntries(dir))
                    {
                        if (_stop.IsCancellationRequested) return;
                        if (child.Equals(Path.Combine(_root, UploadService.UploadFolderName),
                                StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(child);
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if ((attributes & Pinned) != 0) Queue(child);
                                if (CanTraverseFolder(child)) dirs.Push(child);
                            }
                            else if ((attributes & Pinned) != 0 && NeedsHydration(attributes))
                            {
                                Queue(child, trackHydration: true);
                            }
                        }
                        catch (IOException) { } // A concurrent prune can remove a placeholder.
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Logger.Debug(ex, "Skipping vanished or inaccessible folder {0}", dir); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn(ex, "Scanning pinned placeholders failed");
        }
    }

    /// <summary>Bound concurrent downloads so folder pins make progress without flooding Immich.</summary>
    private void ProcessPending()
    {
        if (!_canHydrate() || Interlocked.Exchange(ref _busy, 1) != 0) return;
        try
        {
            Parallel.ForEach(_pending,
                new ParallelOptions { MaxDegreeOfParallelism = 4 },
                item => ProcessPendingItem(item.Key, item.Value));
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
            RefreshCompletedFolders();
        }
    }

    /// <summary>Recheck current pin and disk state before each request; events may arrive meanwhile.</summary>
    private void ProcessPendingItem(string path, long version)
    {
        if (_stop.IsCancellationRequested || !_canHydrate()) return;
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & Pinned) != 0) QueuePinnedFolder(path);
                RemovePending(path, version);
                return;
            }
            if (!IsPinned(path, attributes) || !NeedsHydration(attributes))
            {
                RemovePending(path, version);
                ClearFailure(path);
                DiscardTrackedFile(path);
                return;
            }

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            int result = CfApi.CfHydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero);
            if (result < 0)
            {
                MarkFailure(path);
                Logger.Warn("Hydration of pinned file {0} failed: 0x{1:X8}", path, result);
            }
            else
            {
                RemovePending(path, version);
                ClearFailure(path);
                CompleteTrackedFile(path);
            }
        }
        catch (FileNotFoundException) { RemovePending(path, version); ClearFailure(path); DiscardTrackedFile(path); }
        catch (DirectoryNotFoundException) { RemovePending(path, version); ClearFailure(path); DiscardTrackedFile(path); }
        catch (Exception ex)
        {
            MarkFailure(path);
            Logger.Warn(ex, "Hydration of pinned file {0} failed", path);
        }
    }

    /// <summary>Both cloud-file flags indicate content that is not yet fully on disk.</summary>
    private static bool NeedsHydration(FileAttributes attributes) =>
        (attributes & (FileAttributes.Offline | RecallOnDataAccess)) != 0;

    /// <summary>Keep a newer pin change queued if it arrived during this download.</summary>
    private void RemovePending(string path, long version) =>
        ((ICollection<KeyValuePair<string, long>>)_pending).Remove(new(path, version));

    /// <summary>Mark a file complete once, even if several watcher events queued it.</summary>
    private void CompleteTrackedFile(string path)
    {
        lock (_progressLock)
        {
            if (_trackedFiles.Contains(path)) _completedTrackedFiles.Add(path);
        }
    }

    private void DiscardTrackedFile(string path)
    {
        lock (_progressLock)
        {
            _trackedFiles.Remove(path);
            _completedTrackedFiles.Remove(path);
        }
    }

    /// <summary>Publish at most once per second while a pin operation is changing.</summary>
    private void ReportProgress()
    {
        var current = Progress;
        if (!current.Active && current.Total > 0)
        {
            lock (_progressLock)
            {
                if (_trackedFiles.Count == _completedTrackedFiles.Count &&
                    _pending.IsEmpty && Volatile.Read(ref _busy) == 0 &&
                    Volatile.Read(ref _scanning) == 0)
                {
                    _trackedFiles.Clear();
                    _completedTrackedFiles.Clear();
                    current = default;
                }
            }
        }
        if (current == _lastReportedProgress) return;
        _lastReportedProgress = current;
        ProgressChanged?.Invoke();
    }

    /// <summary>Clear a completed folder's stale pending badge after every requested download finishes.</summary>
    private void RefreshCompletedFolders()
    {
        if (!_pending.IsEmpty || Volatile.Read(ref _scanning) != 0 || _stop.IsCancellationRequested)
            return;

        string[] folders;
        lock (_progressLock)
        {
            if (!_pending.IsEmpty) return;
            folders = _foldersToRefresh.OrderByDescending(path => path.Length).ToArray();
            _foldersToRefresh.Clear();
        }

        foreach (string folder in folders)
            CloudFolderState.RefreshExisting(folder);
    }

    /// <summary>Cloud folder placeholders are reparse points; only links must be skipped.</summary>
    private static bool CanTraverseFolder(string path) =>
        new DirectoryInfo(path).LinkTarget == null;

    private void MarkFailure(string path)
    {
        bool hadFailures = HasFailures;
        _failed[path] = 0;
        if (!hadFailures) FailureStateChanged?.Invoke();
    }

    private void ClearFailure(string path)
    {
        if (_failed.TryRemove(path, out _) && !HasFailures) FailureStateChanged?.Invoke();
    }

    /// <summary>A pinned parent folder also requests its descendants be kept locally.</summary>
    private bool IsPinned(string path, FileAttributes attributes)
    {
        if ((attributes & Unpinned) != 0) return false;
        if ((attributes & Pinned) != 0) return true;
        for (string? parent = Path.GetDirectoryName(path);
             parent != null && parent.StartsWith(_root, StringComparison.OrdinalIgnoreCase);
             parent = Path.GetDirectoryName(parent))
        {
            FileAttributes parentAttributes = File.GetAttributes(parent);
            if ((parentAttributes & Unpinned) != 0) return false;
            if ((parentAttributes & Pinned) != 0) return true;
            if (parent.Equals(_root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }

    /// <summary>Include descendants when Explorer pins a folder rather than individual photos.</summary>
    private void QueuePinnedFolder(string folder)
    {
        if (!folder.Equals(_root, StringComparison.OrdinalIgnoreCase))
        {
            lock (_progressLock) _foldersToRefresh.Add(folder);
        }
        try
        {
            var dirs = new Stack<string>();
            dirs.Push(folder);
            while (dirs.Count > 0 && !_stop.IsCancellationRequested)
            {
                string dir = dirs.Pop();
                try
                {
                    foreach (string path in Directory.EnumerateFileSystemEntries(dir))
                    {
                        if (_stop.IsCancellationRequested) return;
                        if (path.Equals(Path.Combine(_root, UploadService.UploadFolderName),
                                StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (CanTraverseFolder(path)) dirs.Push(path);
                            }
                            else if (NeedsHydration(attributes) && IsPinned(path, attributes))
                            {
                                Queue(path, trackHydration: true);
                            }
                        }
                        catch (IOException) { } // A concurrent prune can remove one file.
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { Logger.Debug(ex, "Skipping vanished or inaccessible pinned folder {0}", dir); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn(ex, "Enumerating pinned folder {0} failed", folder);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _watcher.Dispose();
        _timer.Dispose();
        _progressTimer.Dispose();
        _stop.Dispose();
    }
}
