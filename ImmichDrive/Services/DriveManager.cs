using ImmichDrive.Classes.Settings;
using System.IO;
using System.Threading;

namespace ImmichDrive.Services;

public enum DriveStatus { Disconnected, Connecting, Online, Error }

/// <summary>
/// Orchestrates the live drive: holds the Immich client + cfapi provider + index, registers
/// the sync root, and kicks off timeline population. One instance for the resident process,
/// reachable as <see cref="Current"/>. Raises <see cref="StatusChanged"/> so the tray and
/// settings UI can reflect state.
/// </summary>
public sealed class DriveManager
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public static DriveManager Current { get; } = new();

    /// <summary>Bump when the on-disk folder/file naming scheme changes (forces a clean rebuild).</summary>
    private const int CurrentLayoutVersion = 4;

    private ImmichClient? _client;
    private CloudProviderService? _provider;
    private AssetIndex? _index;
    private CancellationTokenSource? _populateCts;

    /// <summary>Metadata carried over from the pre-rebuild index so a layout migration re-creates
    /// placeholders without re-enriching every asset. Set during the migration, used by the first
    /// populate, then null for the rest of the session.</summary>
    private IReadOnlyDictionary<string, (long Size, bool IsVideo, string Name)>? _migrationCache;

    // Auto-refresh: poll the newest month often (catch new phone photos fast) and the whole
    // timeline occasionally. A single lock serializes all populate work so polls never overlap
    // each other or the initial/manual populate.
    private static readonly TimeSpan FastInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SlowInterval = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim _populateLock = new(1, 1);
    private Timer? _fastTimer;
    private Timer? _slowTimer;
    private UploadService? _upload;

    public DriveStatus Status { get; private set; } = DriveStatus.Disconnected;
    public string? StatusDetail { get; private set; }
    public (int Done, int Total) Progress { get; private set; }

    /// <summary>Raised (on a thread-pool thread) whenever Status/Progress changes.</summary>
    public event Action? StatusChanged;

    private void Set(DriveStatus status, string? detail = null)
    {
        Status = status;
        StatusDetail = detail;
        StatusChanged?.Invoke();
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SettingsManager.Current.ServerUrl) &&
        !string.IsNullOrWhiteSpace(SettingsManager.Current.ApiKey);

    /// <summary>Connects (or reconnects) the drive using the current settings.</summary>
    public async Task ConnectAsync()
    {
        var s = SettingsManager.Current;
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || string.IsNullOrWhiteSpace(s.ApiKey))
        {
            Set(DriveStatus.Disconnected, "Not configured");
            return;
        }

        try
        {
            Set(DriveStatus.Connecting, "Verifying server…");
            SharedPaths.WriteBreadcrumb();

            // Tear down any previous provider first. On a reconnect where the user changed the folder
            // without disconnecting, its cfapi connection still holds the old root — releasing it here
            // lets us re-register and retire the old folder without racing a live connection.
            _provider?.Disconnect();
            _provider?.Dispose();
            _provider = null;
            _upload?.Dispose();
            _upload = null;

            _client?.Dispose();
            _client = new ImmichClient(s.ServerUrl, s.ApiKey);
            string? who = await _client.TestConnectionAsync();
            if (who == null) { Set(DriveStatus.Error, "Could not reach Immich (check URL/API key)"); return; }

            string syncRoot = s.EffectiveSyncRootPath;
            string icon = ResolveStableIcon();
            // Non-destructive: register only if not already registered. (We do NOT re-register on
            // every version — unregister-then-register churn was failing with 0x80070005 and
            // de-placeholdering files. The stable icon path is picked up on the next fresh
            // registration; it no longer depends on the versioned package dir.)
            // Returns the old folder when the user relocated the drive, so we can retire it (the
            // sync-root id is server-derived, so a relocation would otherwise silently strand the old
            // read-only placeholder tree — the user can't delete it, see the deny ACE in DriveSecurity).
            string? retiredPath = await SyncRootService.RegisterAsync(syncRoot, s.ServerUrl, icon);
            if (retiredPath != null) RetireOldSyncRoot(retiredPath, syncRoot);

            // One-time clean rebuild when the on-disk layout/naming revision changes. Carry the old
            // index's metadata forward first so the rebuild re-creates placeholders from known
            // size/name instead of re-enriching the entire library over the network.
            if (s.LayoutVersion != CurrentLayoutVersion)
            {
                Set(DriveStatus.Connecting, "Updating drive layout…");
                try
                {
                    var old = new AssetIndex();
                    old.EnsureCreated();
                    var cache = old.BuildMetadataCache();
                    if (cache.Count > 0) _migrationCache = cache;
                    Logger.Info("Layout migration: carried {0} assets' metadata forward", cache.Count);
                }
                catch (Exception ex) { Logger.Warn(ex, "Building migration metadata cache failed (will re-enrich)"); }

                AssetIndex.DeleteDatabaseFile();
                WipeSyncRootSubfolders(syncRoot);
                s.LayoutVersion = CurrentLayoutVersion;
                SettingsManager.SaveSettings();
            }

            _index = new AssetIndex();
            _index.EnsureCreated();

            _provider = new CloudProviderService(_client);
            _provider.Connect(syncRoot);

            s.Connected = true;
            SettingsManager.SaveSettings();
            Set(DriveStatus.Online, $"Connected as {who}");

            // Populate in the background; the drive is usable as buckets land (newest first).
            _populateCts?.Cancel();
            _populateCts = new CancellationTokenSource();
            _ = RunPopulate(newestOnly: false, skipIfBusy: false, _populateCts.Token);

            StartAutoRefresh();
            SetUpSecurityAndUpload(syncRoot);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Connect failed");
            Set(DriveStatus.Error, ex.Message);
        }
    }

    /// <summary>
    /// Applies the read-only deny ACE (per the setting), creates a writable <c>Upload\</c> folder, and
    /// starts watching it for uploads. ACL work runs off the connect path (icacls over the tree takes a
    /// few seconds).
    /// </summary>
    private void SetUpSecurityAndUpload(string syncRoot)
    {
        string uploadDir = Path.Combine(syncRoot, UploadService.UploadFolderName);

        _ = Task.Run(() =>
        {
            try
            {
                // Give the root folder our icon in Explorer (writes desktop.ini) while it's still
                // writable — the deny ACE persists from a prior session, so lift it first.
                DriveSecurity.RemoveReadOnly(syncRoot);
                DriveSecurity.SetFolderIcon(syncRoot, Path.Combine(StableIconDir, "ImmichDrive.ico"));

                DriveSecurity.ApplyReadOnly(syncRoot);          // the drive is always read-only
                DriveSecurity.EnsureUploadWritable(uploadDir);  // …except the Upload folder
            }
            catch (Exception ex) { Logger.Warn(ex, "Security setup failed"); }
        });

        _upload?.Dispose();
        _upload = new UploadService(_client!, uploadDir);
        _upload.Start();
    }

    private void StartAutoRefresh()
    {
        _fastTimer?.Dispose();
        _slowTimer?.Dispose();
        _fastTimer = new Timer(_ => { _ = RunPopulate(newestOnly: true, skipIfBusy: true, CancellationToken.None); },
            null, FastInterval, FastInterval);
        _slowTimer = new Timer(_ => { _ = RunPopulate(newestOnly: false, skipIfBusy: true, CancellationToken.None); },
            null, SlowInterval, SlowInterval);
    }

    /// <summary>
    /// Runs a populate. <paramref name="newestOnly"/> refreshes just the newest month bucket;
    /// otherwise the whole timeline. <paramref name="skipIfBusy"/> (timers) bails if another
    /// populate is already running, so polls never stack up.
    /// </summary>
    private async Task RunPopulate(bool newestOnly, bool skipIfBusy, CancellationToken ct)
    {
        if (_client == null || _index == null) return;

        if (skipIfBusy) { if (!await _populateLock.WaitAsync(0, ct)) return; }
        else { await _populateLock.WaitAsync(ct); }

        try
        {
            var pop = new PlaceholderPopulator(_client, _index, SettingsManager.Current.EffectiveSyncRootPath, _migrationCache);
            if (newestOnly)
            {
                await pop.PopulateNewestAsync(ct);
            }
            else
            {
                var progress = new Progress<(int, int)>(p => { Progress = p; StatusChanged?.Invoke(); });
                await pop.PopulateAsync(progress, ct);
                _migrationCache = null; // consumed: the rebuilt index is now authoritative
                SettingsManager.Current.LastSyncUtc = DateTimeOffset.UtcNow;
                SettingsManager.SaveSettings();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logger.Warn(ex, "Populate failed (newestOnly={0})", newestOnly); }
        finally { _populateLock.Release(); }
    }

    /// <summary>Re-runs full population against the server (e.g. tray "Refresh").</summary>
    public void Refresh()
    {
        if (Status != DriveStatus.Online) return;
        _populateCts?.Cancel();
        _populateCts = new CancellationTokenSource();
        _ = RunPopulate(newestOnly: false, skipIfBusy: false, _populateCts.Token);
    }

    /// <summary>
    /// Returns an icon resource for the sync root at a <b>stable</b> path. The app exe lives under a
    /// versioned package dir (<c>…\WindowsApps\ImmichDrive_X.Y.Z…\</c>) that changes every update, so
    /// pointing the registration there leaves a broken/generic icon after an update. We copy the icon
    /// to a fixed location instead.
    /// </summary>
    /// <summary>Fixed (version-independent) folder holding the sync-root icon, referenced by both the
    /// sync-root registration and the root folder's desktop.ini.</summary>
    private const string StableIconDir = @"C:\ProgramData\ImmichDrive";

    private static string ResolveStableIcon()
    {
        try
        {
            string dir = StableIconDir;
            Directory.CreateDirectory(dir);
            string dst = Path.Combine(dir, "ImmichDrive.ico");
            string src = Path.Combine(AppContext.BaseDirectory, "Resources", "ImmichDrive.ico");
            if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
            if (File.Exists(dst)) return $"{dst},0";
        }
        catch (Exception ex) { Logger.Warn(ex, "Stable icon copy failed; using exe path"); }
        return $"{Environment.ProcessPath},0";
    }

    /// <summary>
    /// Retires the previous drive folder after the user relocates the drive to <paramref name="newRoot"/>.
    /// Lifts the read-only deny ACE, rescues any not-yet-uploaded files from the old <c>Upload\</c> into the
    /// new one, then deletes the old placeholder tree. Placeholders are local-only, so nothing leaves the
    /// Immich server. Runs off the connect path — deleting the tree touches every file's attributes.
    /// </summary>
    private static void RetireOldSyncRoot(string oldRoot, string newRoot)
    {
        // Guard against pathological overlaps (new folder nested under old, or vice versa) — deleting
        // the old tree would take the new one with it. Leave it for the user in that case.
        if (IsSameOrNested(oldRoot, newRoot))
        {
            Logger.Warn("Skipping retire of {0}: overlaps new root {1}", oldRoot, newRoot);
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(oldRoot)) return;

                // Drop the read-only deny and grant ourselves full control so the whole tree is deletable.
                DriveSecurity.RemoveReadOnly(oldRoot);
                DriveSecurity.AllowDeleteTree(oldRoot);

                RescuePendingUploads(oldRoot, newRoot);

                ClearAttributesRecursive(oldRoot);
                Directory.Delete(oldRoot, recursive: true);
                Logger.Info("Retired old drive folder {0}", oldRoot);
            }
            catch (Exception ex) { Logger.Warn(ex, "Retiring old drive folder {0} failed", oldRoot); }
        });
    }

    /// <summary>Moves any files the user dropped in the old <c>Upload\</c> into the new one so a relocation
    /// mid-upload doesn't lose them.</summary>
    private static void RescuePendingUploads(string oldRoot, string newRoot)
    {
        try
        {
            string oldUpload = Path.Combine(oldRoot, UploadService.UploadFolderName);
            if (!Directory.Exists(oldUpload)) return;
            string newUpload = Path.Combine(newRoot, UploadService.UploadFolderName);
            Directory.CreateDirectory(newUpload);
            foreach (var file in Directory.GetFiles(oldUpload, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string dest = Path.Combine(newUpload, Path.GetFileName(file));
                    for (int n = 2; File.Exists(dest); n++)
                        dest = Path.Combine(newUpload, $"{Path.GetFileNameWithoutExtension(file)} ({n}){Path.GetExtension(file)}");
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Move(file, dest);
                    Logger.Info("Rescued pending upload {0} -> {1}", file, dest);
                }
                catch (Exception ex) { Logger.Warn(ex, "Rescuing pending upload {0} failed", file); }
            }
        }
        catch (Exception ex) { Logger.Warn(ex, "Rescuing pending uploads from {0} failed", oldRoot); }
    }

    /// <summary>Clears ReadOnly/Hidden/System on every file and folder so <see cref="Directory.Delete(string,bool)"/>
    /// can remove the tree (placeholders carry ReadOnly; desktop.ini is Hidden+System).</summary>
    private static void ClearAttributesRecursive(string root)
    {
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            try { new DirectoryInfo(d).Attributes = FileAttributes.Directory; } catch { }
        try { new DirectoryInfo(root).Attributes = FileAttributes.Directory; } catch { }
    }

    /// <summary>True if the two paths are equal or one contains the other.</summary>
    private static bool IsSameOrNested(string a, string b)
    {
        try
        {
            static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))
                + Path.DirectorySeparatorChar;
            string na = Norm(a), nb = Norm(b);
            return na.StartsWith(nb, StringComparison.OrdinalIgnoreCase)
                || nb.StartsWith(na, StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    /// <summary>Deletes the placeholder folders under the sync root (for a clean layout rebuild).</summary>
    private static void WipeSyncRootSubfolders(string syncRoot)
    {
        DriveSecurity.RemoveReadOnly(syncRoot); // lift the read-only deny so the wipe can delete
        try
        {
            foreach (var dir in Directory.GetDirectories(syncRoot))
            {
                try
                {
                    // Our folders are ReadOnly and hold Hidden/System desktop.ini files — clear
                    // attributes before deleting.
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                        try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                    foreach (var d in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories))
                        try { new DirectoryInfo(d).Attributes = FileAttributes.Directory; } catch { }
                    new DirectoryInfo(dir).Attributes = FileAttributes.Directory;
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) { Logger.Warn(ex, "Wipe failed for {0}", dir); }
            }
        }
        catch (Exception ex) { Logger.Warn(ex, "Wipe sync root failed"); }
    }

    public void Disconnect()
    {
        _fastTimer?.Dispose(); _fastTimer = null;
        _slowTimer?.Dispose(); _slowTimer = null;
        _upload?.Dispose(); _upload = null;
        if (!string.IsNullOrWhiteSpace(SettingsManager.Current.ServerUrl))
            try { DriveSecurity.RemoveReadOnly(SettingsManager.Current.EffectiveSyncRootPath); } catch { }
        _populateCts?.Cancel();
        _provider?.Disconnect();
        _provider = null;
        if (!string.IsNullOrWhiteSpace(SettingsManager.Current.ServerUrl))
            SyncRootService.Unregister(SettingsManager.Current.ServerUrl);
        SettingsManager.Current.Connected = false;
        SettingsManager.SaveSettings();
        Set(DriveStatus.Disconnected, "Disconnected");
    }
}
