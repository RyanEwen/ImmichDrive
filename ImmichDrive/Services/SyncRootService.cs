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

    public static async Task RegisterAsync(string syncRootPath, string serverUrl, string iconResource, bool forceRefresh = false)
    {
        Directory.CreateDirectory(syncRootPath);

        // Normally: already registered with our (stable) id → leave it intact. Re-registering means
        // unregistering first, which tears down placeholders. This is the key to not wiping the
        // drive on every launch. The exception is forceRefresh (a new app version), where we must
        // re-register so the IconResource path — which contains the versioned package dir — points
        // at the new build; the self-healing populate then recreates any torn-down placeholders.
        if (!forceRefresh && IsRegistered(serverUrl)) { Logger.Info("Sync root already registered; leaving intact"); return; }

        // Clear any current/orphaned roots (old randomized-hash ids, or the prior version's), then
        // register fresh.
        UnregisterOurs();

        var folder = await StorageFolder.GetFolderFromPathAsync(syncRootPath);

        var info = new StorageProviderSyncRootInfo
        {
            Id = MakeSyncRootId(serverUrl),
            Path = folder,
            DisplayNameResource = "Drive for Immich",
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
                return;
            }
            catch (Exception ex) when (i < attempts)
            {
                Logger.Warn("Register attempt {0} failed (0x{1:X8}); retrying", i, ex.HResult);
                UnregisterOurs();
                await Task.Delay(2500);
            }
        }
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

    public static bool IsRegistered(string serverUrl)
    {
        try
        {
            string id = MakeSyncRootId(serverUrl);
            foreach (var root in StorageProviderSyncRootManager.GetCurrentSyncRoots())
                if (root.Id == id) return true;
        }
        catch { /* API unavailable */ }
        return false;
    }
}
