using ImmichDrive.Classes.Settings;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace ImmichDrive.Services;

public enum DriveStatus
{
    /// <summary>Not running — never connected this session, or the user disconnected it.</summary>
    Disconnected,
    /// <summary>Bringing the drive up.</summary>
    Connecting,
    /// <summary>Live: the sync root is mounted and the server answers.</summary>
    Online,
    /// <summary>The server can't be reached right now. Deliberately passive — the tray icon goes
    /// muted and a watchdog retries quietly; nothing pops up and nothing plays a sound.</summary>
    Offline,
    /// <summary>Something the user has to fix (rejected API key, registration failure).</summary>
    Error,
}

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
    private PinHydrationService? _pins;
    private string? _populateIssue;

    // Offline watchdog: while the server is unreachable we stop polling the timeline and instead
    // re-probe on a backing-off schedule until it answers again. Passive by design — no balloon,
    // no sound, no dialog; the tray icon and the flyout are the only signals.
    private static readonly int[] WatchdogSteps = [30, 60, 120, 300]; // seconds between probes
    private Timer? _watchdogTimer;
    private int _watchdogStep;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private string? _connectedAs;

    public DriveStatus Status { get; private set; } = DriveStatus.Disconnected;
    public string? StatusDetail { get; private set; }
    public (int Done, int Total) Progress { get; private set; }
    public bool IsPopulating { get; private set; }
    public string? SyncIssue => _populateIssue ?? (_pins?.HasFailures == true
        ? "Some pinned photos could not be downloaded. Retrying in the background."
        : null);

    /// <summary>When the server last answered us, or <see cref="DateTimeOffset.MinValue"/> if it never has
    /// this session. Shown in the flyout while offline so "how stale is this?" has an answer.</summary>
    public DateTimeOffset LastContactUtc { get; private set; }

    /// <summary>True while a connection re-test is in flight, so the UI can show a busy affordance.</summary>
    public bool IsCheckingConnection { get; private set; }

    /// <summary>True when re-testing the connection is a sensible thing to offer.</summary>
    public bool CanRetry => IsConfigured && Status is DriveStatus.Offline or DriveStatus.Error or DriveStatus.Disconnected;

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
    public Task ConnectAsync() => ConnectAsync(announce: true);

    /// <summary>
    /// <paramref name="announce"/> is false for the watchdog's automatic retries while offline: they
    /// must not flip the status to <see cref="DriveStatus.Connecting"/>, or the tray icon would blink
    /// back to "healthy" every retry for a server that is still down.
    /// </summary>
    private async Task ConnectAsync(bool announce)
    {
        var s = SettingsManager.Current;
        if (string.IsNullOrWhiteSpace(s.ServerUrl) || string.IsNullOrWhiteSpace(s.ApiKey))
        {
            StopWatchdog();
            Set(DriveStatus.Disconnected, "Not configured");
            return;
        }

        try
        {
            if (announce) Set(DriveStatus.Connecting, "Verifying server…");
            SharedPaths.WriteBreadcrumb();

            // Finish any retire that a previous session started but didn't complete (app quit mid-delete).
            _ = Task.Run(CleanUpPendingRetirements);

            // Tear down any previous provider first. On a reconnect where the user changed the folder
            // without disconnecting, its cfapi connection still holds the old root — releasing it here
            // lets us re-register and retire the old folder without racing a live connection.
            _provider?.Disconnect();
            _provider?.Dispose();
            _provider = null;
            _upload?.Dispose();
            _upload = null;
            _pins?.Dispose();
            _pins = null;

            _client?.Dispose();
            _client = new ImmichClient(s.ServerUrl, s.ApiKey);
            var (reachability, who) = await _client.ProbeAsync();
            if (reachability == ImmichReachability.Unauthorized)
            {
                // The user has to fix this, so don't burn retries on it.
                StopWatchdog();
                Set(DriveStatus.Error, "Immich rejected the API key — update it in Settings");
                return;
            }
            if (reachability != ImmichReachability.Ok)
            {
                // Server down / wrong URL / no network yet (e.g. we started before the VPN did).
                // Sit in Offline and let the watchdog bring the drive up when it answers.
                GoOffline();
                return;
            }
            _connectedAs = who;
            LastContactUtc = DateTimeOffset.UtcNow;
            StopWatchdog();

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
            // A failed hydration is often the first sign the server went away (the user opened a photo
            // and nothing came back), so let it nudge the reachability check.
            _provider.TransferFailed += NoteServerUnreachable;
            _provider.Connect(syncRoot);
            _pins = new PinHydrationService(syncRoot, () => Status == DriveStatus.Online);
            _pins.FailureStateChanged += () => StatusChanged?.Invoke();

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
            if (IsNetworkFailure(ex)) GoOffline();
            else Set(DriveStatus.Error, ex.Message);
        }
    }

    // ── Reachability ────────────────────────────────────────────────
    // The drive can be mounted and still useless because the server went away. That is a distinct,
    // recoverable state (DriveStatus.Offline) from a misconfiguration (DriveStatus.Error): we keep the
    // placeholders in place, mute the tray icon, and quietly re-probe until Immich answers again.

    /// <summary>
    /// Re-tests the connection on demand (tray menu / flyout "Try again"). Pings the server when the
    /// drive is already mounted; otherwise runs a full connect.
    /// </summary>
    public async Task RetryConnectionAsync()
    {
        if (IsCheckingConnection || !IsConfigured) return;
        IsCheckingConnection = true;
        StatusChanged?.Invoke();
        try
        {
            StopWatchdog();  // an explicit retry resets the backoff
            // Only a mounted-but-unreachable drive can be fixed by a ping. Anything else (including a
            // rejected key the user has just corrected in Settings) needs a connect from scratch, so
            // the client picks up the current settings.
            if (Status == DriveStatus.Offline && _provider != null && _client != null) await ProbeAsync();
            else await ConnectAsync();
        }
        finally
        {
            IsCheckingConnection = false;
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Pings the server once and moves the drive between Online and Offline. Never throws.</summary>
    private async Task ProbeAsync()
    {
        if (_client == null) return;
        if (!await _probeLock.WaitAsync(0)) return;   // one probe at a time
        try
        {
            var (reachability, who) = await _client.ProbeAsync();
            switch (reachability)
            {
                case ImmichReachability.Ok:
                    NoteServerReachable(who);
                    break;
                case ImmichReachability.Unauthorized:
                    StopWatchdog();   // retrying won't help; the user has to replace the key
                    Set(DriveStatus.Error, "Immich rejected the API key — update it in Settings");
                    break;
                default:
                    GoOffline();
                    break;
            }
        }
        finally { _probeLock.Release(); }
    }

    /// <summary>Records a successful round-trip and, if we were offline, brings the drive back.</summary>
    private void NoteServerReachable(string? who = null)
    {
        LastContactUtc = DateTimeOffset.UtcNow;
        if (who != null) _connectedAs = who;
        if (Status != DriveStatus.Offline) return;

        StopWatchdog();
        Logger.Info("Immich is reachable again");
        if (_provider == null)
        {
            // We never got the drive up (the server was down at launch) — do the real connect now.
            _ = ConnectAsync(announce: false);
            return;
        }
        Set(DriveStatus.Online, _connectedAs != null ? $"Connected as {_connectedAs}" : "Connected");
        _ = RunPopulate(newestOnly: false, skipIfBusy: true, CancellationToken.None);
    }

    /// <summary>
    /// A request failed in a way that looks like the server/network is down rather than one bad asset.
    /// Confirms with a probe before muting the tray — a single failed request shouldn't do that.
    /// </summary>
    private void NoteServerUnreachable(Exception? ex)
    {
        if (Status != DriveStatus.Online) return;
        if (!IsNetworkFailure(ex)) return;
        _ = ProbeAsync();
    }

    private void GoOffline()
    {
        if (Status != DriveStatus.Offline)
        {
            Logger.Info("Immich unreachable — going offline, will keep retrying");
            Set(DriveStatus.Offline, "Can't reach the Immich server");
        }
        StartWatchdog();
    }

    private void StartWatchdog()
    {
        if (_watchdogTimer != null) return;
        _watchdogStep = 0;
        _watchdogTimer = new Timer(_ => _ = OnWatchdogTick(), null,
            TimeSpan.FromSeconds(WatchdogSteps[0]), Timeout.InfiniteTimeSpan);
    }

    private void StopWatchdog()
    {
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
        _watchdogStep = 0;
    }

    private async Task OnWatchdogTick()
    {
        // The drive may never have come up (nothing to ping through), in which case retry the
        // whole connect; otherwise a cheap ping is enough.
        if (_provider != null && _client != null) await ProbeAsync();
        else await ConnectAsync(announce: false);

        if (Status != DriveStatus.Offline) return;   // recovered, or torn down
        _watchdogStep = Math.Min(_watchdogStep + 1, WatchdogSteps.Length - 1);
        int next = WatchdogSteps[_watchdogStep];
        Logger.Debug("Still offline; next reachability check in {0}s", next);
        _watchdogTimer?.Change(TimeSpan.FromSeconds(next), Timeout.InfiniteTimeSpan);
    }

    /// <summary>True for "the server/network is unavailable" as opposed to "this one asset is bad".</summary>
    private static bool IsNetworkFailure(Exception? ex) => ex switch
    {
        null => false,
        HttpRequestException => true,
        TaskCanceledException or TimeoutException => true,          // HttpClient timeout
        System.Net.Sockets.SocketException => true,
        IOException => true,                                        // socket torn down mid-stream
        _ => IsNetworkFailure(ex.InnerException),
    };

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
        if (Status == DriveStatus.Offline) return;   // the watchdog owns recovery while offline

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
                IsPopulating = true;
                _populateIssue = null;
                Progress = default;
                StatusChanged?.Invoke();
                var progress = new InlineProgress<(int Done, int Total)>(p =>
                {
                    Progress = p;
                    StatusChanged?.Invoke();
                });
                var result = await pop.PopulateAsync(progress, ct);
                ct.ThrowIfCancellationRequested();
                Progress = (result.Processed, result.Processed);
                if (result.Failures > 0)
                    _populateIssue = "Some photos could not be synced. Refresh to retry.";
                _migrationCache = null; // consumed: the rebuilt index is now authoritative
                if (result.Failures == 0)
                {
                    SettingsManager.Current.LastSyncUtc = DateTimeOffset.UtcNow;
                    SettingsManager.SaveSettings();
                }
            }
            NoteServerReachable();   // a completed populate is proof the server is answering
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Populate failed (newestOnly={0})", newestOnly);
            if (!newestOnly) _populateIssue = "Some photos could not be synced. Refresh to retry.";
            NoteServerUnreachable(ex);
        }
        finally
        {
            if (!newestOnly) { IsPopulating = false; StatusChanged?.Invoke(); }
            _populateLock.Release();
        }
    }

    /// <summary>Reports progress in order, including the final update, on the populate thread.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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
    /// Rescues any not-yet-uploaded files from the old <c>Upload\</c>, then <b>renames the old folder aside</b>
    /// (atomic, same-volume) so it disappears from Explorer immediately, and deletes the renamed tree in the
    /// background — a large placeholder tree can take a minute-plus to delete, and the user shouldn't watch the
    /// old folder linger meanwhile. The renamed path is recorded so an interrupted delete (e.g. the app is quit
    /// mid-retire) is cleaned up on the next launch. Placeholders are local-only, so nothing leaves Immich.
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

                // Lift the read-only deny at the root — it's inherited, so this clears it across the whole
                // tree (no need for a slow recursive icacls grant). Then salvage pending uploads before the
                // folder moves out from under their paths.
                DriveSecurity.RemoveReadOnly(oldRoot);
                RescuePendingUploads(oldRoot, newRoot);

                // Rename aside so Explorer stops showing the old folder right away; delete the renamed tree
                // afterwards. If the rename fails (e.g. a handle is open on it), fall back to deleting in place.
                string target = oldRoot;
                try
                {
                    try { new DirectoryInfo(oldRoot).Attributes = FileAttributes.Directory; } catch { }
                    string graveyard = oldRoot + ".retiring";
                    if (Directory.Exists(graveyard))
                        graveyard = $"{oldRoot}.retiring-{Guid.NewGuid():N}";
                    Directory.Move(oldRoot, graveyard);
                    target = graveyard;
                    // Hide the renamed-aside folder so it doesn't briefly flash in Explorer before the
                    // background delete removes it. (Cleared again by ClearAttributesRecursive below.)
                    try { new DirectoryInfo(graveyard).Attributes |= FileAttributes.Hidden; } catch { }
                }
                catch (Exception ex) { Logger.Warn(ex, "Renaming old folder {0} aside failed; deleting in place", oldRoot); }

                RecordPendingRetire(target);
                ClearAttributesRecursive(target);
                Directory.Delete(target, recursive: true);
                UnrecordPendingRetire(target);
                Logger.Info("Retired old drive folder {0}", oldRoot);
            }
            catch (Exception ex) { Logger.Warn(ex, "Retiring old drive folder {0} failed (leftover cleaned up next launch)", oldRoot); }
        });
    }

    // Crash-safe cleanup: the retire records the renamed-aside folder here before deleting it, and clears the
    // entry on success. A leftover (delete interrupted by app exit) is finished on the next launch.
    private static readonly object _retireLogLock = new();
    private static string RetireLogPath => Path.Combine(SharedPaths.DataDir, "pending-retire.txt");

    private static void RecordPendingRetire(string path)
    {
        try
        {
            lock (_retireLogLock)
            {
                Directory.CreateDirectory(SharedPaths.DataDir);
                File.AppendAllText(RetireLogPath, path + Environment.NewLine);
            }
        }
        catch (Exception ex) { Logger.Warn(ex, "Recording pending retire {0} failed", path); }
    }

    private static void UnrecordPendingRetire(string path)
    {
        try
        {
            lock (_retireLogLock)
            {
                if (!File.Exists(RetireLogPath)) return;
                var remaining = new List<string>();
                foreach (var line in File.ReadAllLines(RetireLogPath))
                {
                    string t = line.Trim();
                    if (t.Length > 0 && !string.Equals(t, path, StringComparison.OrdinalIgnoreCase))
                        remaining.Add(t);
                }
                if (remaining.Count == 0) File.Delete(RetireLogPath);
                else File.WriteAllLines(RetireLogPath, remaining);
            }
        }
        catch (Exception ex) { Logger.Warn(ex, "Unrecording pending retire {0} failed", path); }
    }

    /// <summary>Finishes deleting any folder a prior retire renamed aside but didn't get to remove (app quit
    /// mid-delete). Called at connect; runs in the background since a leftover tree can be large.</summary>
    private static void CleanUpPendingRetirements()
    {
        string[] pending;
        lock (_retireLogLock)
        {
            if (!File.Exists(RetireLogPath)) return;
            try { pending = File.ReadAllLines(RetireLogPath); } catch { return; }
        }
        foreach (var line in pending)
        {
            string path = line.Trim();
            if (path.Length == 0) continue;
            try
            {
                if (Directory.Exists(path))
                {
                    DriveSecurity.RemoveReadOnly(path);
                    ClearAttributesRecursive(path);
                    Directory.Delete(path, recursive: true);
                    Logger.Info("Cleaned up leftover retired folder {0}", path);
                }
            }
            catch (Exception ex) { Logger.Warn(ex, "Cleaning leftover retired folder {0} failed", path); continue; }
            UnrecordPendingRetire(path);
        }
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
        StopWatchdog();
        _fastTimer?.Dispose(); _fastTimer = null;
        _slowTimer?.Dispose(); _slowTimer = null;
        _upload?.Dispose(); _upload = null;
        _pins?.Dispose(); _pins = null;
        if (!string.IsNullOrWhiteSpace(SettingsManager.Current.ServerUrl))
            try { DriveSecurity.RemoveReadOnly(SettingsManager.Current.EffectiveSyncRootPath); } catch { }
        _populateCts?.Cancel();
        IsPopulating = false;
        _populateIssue = null;
        _provider?.Disconnect();
        _provider = null;
        if (!string.IsNullOrWhiteSpace(SettingsManager.Current.ServerUrl))
            SyncRootService.Unregister(SettingsManager.Current.ServerUrl);
        SettingsManager.Current.Connected = false;
        SettingsManager.SaveSettings();
        Set(DriveStatus.Disconnected, "Disconnected");
    }
}
