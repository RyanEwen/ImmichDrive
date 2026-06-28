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
    private const int CurrentLayoutVersion = 3;

    private ImmichClient? _client;
    private CloudProviderService? _provider;
    private AssetIndex? _index;
    private CancellationTokenSource? _populateCts;

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
            await SyncRootService.RegisterAsync(syncRoot, s.ServerUrl, icon);

            // One-time clean rebuild when the on-disk layout/naming revision changes.
            if (s.LayoutVersion != CurrentLayoutVersion)
            {
                Set(DriveStatus.Connecting, "Updating drive layout…");
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
            var pop = new PlaceholderPopulator(_client, _index, SettingsManager.Current.EffectiveSyncRootPath);
            if (newestOnly)
            {
                await pop.PopulateNewestAsync(ct);
            }
            else
            {
                var progress = new Progress<(int, int)>(p => { Progress = p; StatusChanged?.Invoke(); });
                await pop.PopulateAsync(progress, ct);
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
    private static string ResolveStableIcon()
    {
        try
        {
            string dir = @"C:\ProgramData\ImmichDrive";
            Directory.CreateDirectory(dir);
            string dst = Path.Combine(dir, "ImmichDrive.ico");
            string src = Path.Combine(AppContext.BaseDirectory, "Resources", "ImmichDrive.ico");
            if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
            if (File.Exists(dst)) return $"{dst},0";
        }
        catch (Exception ex) { Logger.Warn(ex, "Stable icon copy failed; using exe path"); }
        return $"{Environment.ProcessPath},0";
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
