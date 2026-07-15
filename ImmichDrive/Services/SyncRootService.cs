using System.IO;
using System.Security.Principal;
using Windows.Storage;
using Windows.Storage.Provider;

namespace ImmichDrive.Services;

/// <summary>
/// Registers/unregisters the Cloud Files sync root via WinRT
/// <see cref="StorageProviderSyncRootManager"/> — the folder that shows up in Explorer's
/// navigation pane (like OneDrive). Placeholder creation + hydration callbacks are handled
/// separately by <see cref="CloudProviderService"/>. See <c>.claude/docs/cloud-files.md</c>.
/// </summary>
public static class SyncRootService
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    // Stable provider identity.
    private static readonly Guid ProviderId = new("7C4F0E2A-9B3D-4E1A-8C2F-1D5A6B7C8E90");

    /// <summary>
    /// Sync-root id. The shell requires the form <c>&lt;providerId&gt;!&lt;securityId&gt;</c> where
    /// the suffix is the current user's SID — registering without a valid SID suffix fails with
    /// 0x80070490 (ERROR_NOT_FOUND). The server hash keeps multiple servers distinct.
    /// MUST be <b>stable across processes</b> — <c>string.GetHashCode()</c> is randomized per run
    /// in .NET, which would mint a new id every launch and orphan the previous sync root, so we
    /// use a deterministic FNV-1a hash instead.
    /// </summary>
    public static string MakeSyncRootId(string serverUrl)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "S-1-5-21-0-0-0-1000";
        return $"ImmichDrive.{StableHash(serverUrl):x8}!{sid}";
    }

    private static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    private const string IdPrefix = "ImmichDrive.";

    /// <summary>
    /// Registers the sync root at <paramref name="syncRootPath"/>. Returns the filesystem path of a
    /// <b>retired old location</b> when this call <i>relocates</i> an existing registration (the user
    /// changed the drive folder), so the caller can clean up the abandoned tree; otherwise returns
    /// <c>null</c>. The sync-root id is server-derived (path-independent), so a relocation reuses the
    /// same id — without this check the old folder would silently strand as an un-deletable,
    /// read-only placeholder tree.
    /// </summary>
    public static async Task<string?> RegisterAsync(string syncRootPath, string serverUrl, string iconResource, bool forceRefresh = false)
    {
        Directory.CreateDirectory(syncRootPath);

        string? registeredPath = GetRegisteredPath(serverUrl);
        bool relocating = registeredPath != null && !SamePath(registeredPath, syncRootPath);

        // Normally: already registered with our (stable) id at the SAME path → leave it intact.
        // Re-registering means unregistering first, which tears down placeholders. This is the key to
        // not wiping the drive on every launch. Two exceptions force a re-register: forceRefresh (a new
        // app version, so the versioned IconResource path is refreshed and the self-healing populate
        // recreates any torn-down placeholders), and a relocation (the path changed — fall through so we
        // register at the new folder and hand the old one back to the caller to retire).
        if (!forceRefresh && !relocating && registeredPath != null) { Logger.Info("Sync root already registered; leaving intact"); return null; }

        if (relocating) Logger.Info("Sync root relocating from {0} to {1}", registeredPath, syncRootPath);

        // Clear any current/orphaned roots (old randomized-hash ids, the prior version's, or the
        // pre-relocation registration), then register fresh.
        UnregisterOurs();

        var folder = await StorageFolder.GetFolderFromPathAsync(syncRootPath);

        var info = new StorageProviderSyncRootInfo
        {
            Id = MakeSyncRootId(serverUrl),
            Path = folder,
            // Explorer nav-pane label. Deliberately "ImmichDrive", NOT the Store app name
            // "Drive for Immich": existing installs already show this, and it reads naturally as a
            // drive. The Store rejection (10.1.1.1) was about the *listing* product name, not this
            // runtime label. Only applied on a FRESH registration (see the forceRefresh guard above).
            DisplayNameResource = "ImmichDrive",
            IconResource = iconResource,                 // e.g. "C:\\...\\ImmichDrive.exe,0"
            Version = "1.0",
            ProviderId = ProviderId,
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime | StorageProviderInSyncPolicy.DirectoryLastWriteTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            ShowSiblingsAsGroup = false,
            ProtectionMode = StorageProviderProtectionMode.Unknown,
        };

        // Registration can fail transiently right after launch (0x80070490) — the shell's
        // provider catalog isn't ready yet, or an orphaned sync root from a previous package
        // version still lingers. Clean up our own stale roots and retry a few times so the user
        // never has to click Connect manually.
        const int attempts = 4;
        for (int i = 1; i <= attempts; i++)
        {
            try
            {
                StorageProviderSyncRootManager.Register(info);
                Logger.Info("Registered sync root {0} at {1} (attempt {2})", info.Id, syncRootPath, i);
                return relocating ? registeredPath : null;
            }
            catch (Exception ex) when (i < attempts)
            {
                Logger.Warn("Register attempt {0} failed (0x{1:X8}); retrying", i, ex.HResult);
                UnregisterOurs();
                await Task.Delay(2500);
            }
        }
        return null;
    }

    /// <summary>Unregisters any sync root registered by this app (matched by id prefix), incl. orphans.</summary>
    public static void UnregisterOurs()
    {
        try
        {
            foreach (var root in StorageProviderSyncRootManager.GetCurrentSyncRoots())
            {
                try
                {
                    if (root.Id is { } id && id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase))
                        StorageProviderSyncRootManager.Unregister(id);
                }
                catch { /* ignore individual */ }
            }
        }
        catch { /* API unavailable */ }
    }

    public static void Unregister(string serverUrl)
    {
        try { StorageProviderSyncRootManager.Unregister(MakeSyncRootId(serverUrl)); }
        catch { /* not registered */ }
    }

    public static bool IsRegistered(string serverUrl) => GetRegisteredPath(serverUrl) != null;

    /// <summary>
    /// Returns the filesystem path our sync root is currently registered at for this server, or
    /// <c>null</c> if not registered. Used to detect a folder relocation (same server-derived id, but
    /// the shell reports a different path).
    /// </summary>
    public static string? GetRegisteredPath(string serverUrl)
    {
        try
        {
            string id = MakeSyncRootId(serverUrl);
            foreach (var root in StorageProviderSyncRootManager.GetCurrentSyncRoots())
                if (root.Id == id) return root.Path?.Path;
        }
        catch { /* API unavailable */ }
        return null;
    }

    /// <summary>Case-insensitive, separator-normalized path equality (best-effort).</summary>
    private static bool SamePath(string a, string b)
    {
        try
        {
            static string Norm(string p) =>
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
        }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }
}
