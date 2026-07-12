using ImmichDrive.Models;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using static ImmichDrive.Classes.CloudFilter.CfApi;

namespace ImmichDrive.Services;

/// <summary>
/// Walks the Immich timeline (newest month first) and lays down cfapi placeholders under the
/// sync root. Single level of month folders that sort <b>newest-first</b> (the folder name is an
/// inverted sort key; a desktop.ini gives the friendly "YYYY - Month" display name), plus a
/// flat <c>Recent</c> folder pinned to the top. Populate is <b>self-healing and incremental</b>:
/// already-indexed assets are skipped (only re-created if their placeholder went missing, using
/// the stored size — no network), so restarts are fast and a torn-down folder repairs itself.
/// See <c>.claude/docs/cloud-files.md</c>.
/// </summary>
public sealed class PlaceholderPopulator
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const int EnrichConcurrency = 10;

    private const string LegacyRecentFolderName = "Recent";
    private const string AlbumsFolderName = "Albums";
    private const string FavoritesFolderName = "Favorites";
    private const string PartnersFolderName = "Partners";

    // Top-level folders that are NOT part of the main timeline (so the timeline prune ignores them).
    private static bool IsUnderReserved(string rel) =>
        rel.StartsWith(AlbumsFolderName + "\\", StringComparison.OrdinalIgnoreCase) ||
        rel.StartsWith(FavoritesFolderName + "\\", StringComparison.OrdinalIgnoreCase) ||
        rel.StartsWith(PartnersFolderName + "\\", StringComparison.OrdinalIgnoreCase);

    private readonly ImmichClient _client;
    private readonly AssetIndex _index;
    private readonly string _syncRootPath;

    /// <summary>
    /// Optional <c>asset_id → (size, isVideo, name)</c> carried over from the previous index before a
    /// layout rebuild. When present, a "new" asset whose metadata is cached is created from the cache
    /// instead of re-enriching it over the network — so a folder-naming migration doesn't re-fetch the
    /// whole library. Null on a normal first sync.
    /// </summary>
    private readonly IReadOnlyDictionary<string, (long Size, bool IsVideo, string Name)>? _metaCache;

    public PlaceholderPopulator(ImmichClient client, AssetIndex index, string syncRootPath,
        IReadOnlyDictionary<string, (long Size, bool IsVideo, string Name)>? metaCache = null)
    {
        _client = client;
        _index = index;
        _syncRootPath = syncRootPath;
        _metaCache = metaCache;
    }

    /// <summary>Full populate over the whole timeline. Reports (processed, totalApprox) as it goes.</summary>
    public async Task PopulateAsync(IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        _index.EnsureCreated();
        Directory.CreateDirectory(_syncRootPath);
        RemoveLegacyRecentFolder();

        var buckets = await _client.GetBucketsAsync(ct: ct);
        int total = buckets.Sum(b => b.Count);
        int done = 0;

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool allOk = true;
        foreach (var bucket in buckets) // newest first
        {
            ct.ThrowIfCancellationRequested();
            allOk &= await ProcessBucketAsync(bucket, "", null, ct, () => progress?.Report((++done, total)), seenIds);
        }

        // Only prune when we have the complete picture (no bucket fetch failed), so a transient
        // network error can never delete the whole library.
        if (allOk) PruneTimeline(seenIds);

        await PopulateAlbumsAsync(ct);
        await PopulateFavoritesAsync(ct);
        await PopulatePartnersAsync(ct);

        Logger.Info("Populate complete: {0} processed, {1} indexed, across {2} buckets", done, _index.Count(), buckets.Count);
    }

    /// <summary>Removes main-timeline placeholders whose asset no longer exists in Immich's timeline.</summary>
    private void PruneTimeline(HashSet<string> seenIds)
    {
        int removed = 0;
        foreach (var (rel, assetId) in _index.AllRows())
        {
            if (IsUnderReserved(rel)) continue;          // Albums / Favorites / Partners handled separately
            if (seenIds.Contains(assetId)) continue;
            DeletePlaceholder(rel);
            removed++;
        }
        if (removed > 0) Logger.Info("Pruned {0} timeline placeholders removed from Immich", removed);
    }

    /// <summary>Deletes a placeholder file (clearing attributes first) and its index row.</summary>
    private void DeletePlaceholder(string rel)
    {
        try
        {
            string abs = Path.Combine(_syncRootPath, rel);
            if (File.Exists(abs))
            {
                try { File.SetAttributes(abs, FileAttributes.Normal); } catch { }
                DriveSecurity.AllowDeleteFile(abs);   // override the read-only deny so the provider can prune
                File.Delete(abs);
            }
        }
        catch (Exception ex) { Logger.Warn(ex, "Delete placeholder failed for {0}", rel); }
        _index.DeleteByRelPath(rel);
    }

    /// <summary>
    /// Mirrors Immich albums into an <c>Albums\&lt;album name&gt;\</c> tree, and keeps them in sync:
    /// assets removed from an album are pruned, and folders for deleted/renamed albums are removed.
    /// Album assets carry full metadata, so no per-asset enrich is needed; placeholders share the
    /// asset id with the timeline copy, so thumbnails + hydration work the same.
    /// </summary>
    private async Task PopulateAlbumsAsync(CancellationToken ct)
    {
        List<ImmichClient.AlbumRef> albums;
        try { albums = await _client.GetAlbumsAsync(ct); }
        catch (Exception ex) { Logger.Warn(ex, "Listing albums failed"); return; }
        if (albums.Count == 0) return; // empty (or a transient failure) → don't prune anything

        EnsureFolder(AlbumsFolderName);
        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentAlbumFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var album in albums)
        {
            ct.ThrowIfCancellationRequested();
            string folderName = Disambiguate(SanitizeFolderName(album.Name), usedFolders);
            string albumRel = Path.Combine(AlbumsFolderName, folderName);
            string albumAbs = EnsureFolder(albumRel);
            string relPrefix = albumRel + Path.DirectorySeparatorChar;
            currentAlbumFolders.Add(albumRel); // reserve the folder before fetching so a transient
                                               // asset-fetch failure can't orphan it

            List<ImmichAsset> assets;
            try { assets = await _client.GetAlbumAssetsAsync(album.Id, ct); }
            catch (Exception ex) { Logger.Warn(ex, "Album {0} fetch failed", album.Name); continue; }

            var currentIds = new HashSet<string>(assets.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexRows = new List<(string, string, bool, long)>();

            // The album timeline is columnar (no name/size), so partition into already-here (self-heal)
            // and new; new assets get their name/size/type resolved from the index — their timeline copy
            // — or, failing that, enriched over the network before the placeholder is created.
            var newAssets = new List<ImmichAsset>();
            foreach (var asset in assets)
            {
                ct.ThrowIfCancellationRequested();
                var inAlbum = _index.RowsForAsset(asset.Id)
                    .Where(r => r.RelPath.StartsWith(relPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (inAlbum.Count > 0)
                {
                    // Already in this album — reserve names, recreate any missing placeholder.
                    foreach (var (rel, size, isVideo) in inAlbum)
                    {
                        used.Add(Path.GetFileName(rel));
                        if (!File.Exists(Path.Combine(_syncRootPath, rel)))
                            CreatePlaceholder(albumAbs, Path.GetFileName(rel), asset.Id, size, isVideo, asset.FileCreatedAt);
                    }
                    continue;
                }
                newAssets.Add(asset);
            }

            var toEnrich = newAssets.Where(a => !TryResolveFromIndex(a)).ToList();
            if (toEnrich.Count > 0)
                using (var sem = new SemaphoreSlim(EnrichConcurrency))
                    await Task.WhenAll(toEnrich.Select(async a =>
                    {
                        await sem.WaitAsync(ct);
                        try { await _client.EnrichAsync(a, ct); } finally { sem.Release(); }
                    }));

            foreach (var asset in newAssets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    string fileName = Disambiguate(asset.BuildFileName(), used);
                    string rel2 = Path.Combine(albumRel, fileName);
                    CreatePlaceholder(albumAbs, fileName, asset.Id, asset.FileSizeBytes, asset.IsVideo, asset.FileCreatedAt);
                    indexRows.Add((rel2, asset.Id, asset.IsVideo, asset.FileSizeBytes));
                }
                catch (Exception ex) { Logger.Warn(ex, "Album placeholder failed for asset {0}", asset.Id); }
            }
            if (indexRows.Count > 0) _index.UpsertMany(indexRows);

            // Prune assets that were removed from this album (fetch succeeded → set is authoritative).
            int pruned = 0;
            foreach (var (rel, assetId) in _index.RowsUnderPrefix(relPrefix))
                if (!currentIds.Contains(assetId)) { DeletePlaceholder(rel); pruned++; }
            if (pruned > 0) Logger.Info("Album '{0}': pruned {1} removed assets", album.Name, pruned);
        }

        PruneOrphanFolders(AlbumsFolderName, currentAlbumFolders);
    }

    /// <summary>Removes immediate subfolders of <paramref name="rootFolderName"/> (and their index rows)
    /// that aren't in <paramref name="currentFolders"/> — i.e. albums/partners deleted or renamed in Immich.</summary>
    private void PruneOrphanFolders(string rootFolderName, HashSet<string> currentFolders)
    {
        string rootAbs = Path.Combine(_syncRootPath, rootFolderName);
        if (!Directory.Exists(rootAbs)) return;
        foreach (var dir in Directory.GetDirectories(rootAbs))
        {
            string rel = Path.Combine(rootFolderName, Path.GetFileName(dir));
            if (currentFolders.Contains(rel)) continue;
            try
            {
                DriveSecurity.AllowDeleteTree(dir);   // override the read-only deny on the subtree
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, recursive: true);
                Logger.Info("Removed orphaned folder {0}", rel);
            }
            catch (Exception ex) { Logger.Warn(ex, "Removing folder {0} failed", rel); }
            _index.DeleteByPathPrefix(rel + "\\");
        }
    }

    private static string SanitizeFolderName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        return name.Length == 0 ? "Unnamed" : name;
    }

    /// <summary>
    /// Fills an album asset's name/size/type from an existing index row (the same asset's timeline
    /// copy), so the columnar album payload needn't be re-fetched over the network. Prefers a
    /// non-reserved (timeline) row for the cleanest name and a row with a known size. Returns false
    /// when the asset isn't indexed yet (e.g. archived assets absent from the main timeline) — the
    /// caller then enriches it.
    /// </summary>
    private bool TryResolveFromIndex(ImmichAsset asset)
    {
        var rows = _index.RowsForAsset(asset.Id);
        if (rows.Count == 0) return false;
        var best = rows
            .OrderByDescending(r => !IsUnderReserved(r.RelPath)) // timeline copy → clean original name
            .ThenByDescending(r => r.Size > 0)                   // and a real size when available
            .First();
        asset.OriginalFileName = Path.GetFileName(best.RelPath);
        asset.FileSizeBytes = best.Size;
        asset.Type = best.IsVideo ? "VIDEO" : "IMAGE";
        return true;
    }

    /// <summary>Mirrors favorited assets into a flat <c>Favorites\</c> folder, and prunes un-favorited ones.</summary>
    private async Task PopulateFavoritesAsync(CancellationToken ct)
    {
        List<ImmichClient.BucketRef> buckets;
        try { buckets = await _client.GetBucketsAsync(isFavorite: true, ct: ct); }
        catch (Exception ex) { Logger.Warn(ex, "Listing favorite buckets failed"); return; }

        string favAbs = EnsureFolder(FavoritesFolderName);
        string favPrefix = FavoritesFolderName + Path.DirectorySeparatorChar;
        var seenFav = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newAssets = new List<ImmichAsset>();
        bool allOk = true;

        foreach (var bucket in buckets)
        {
            ct.ThrowIfCancellationRequested();
            List<ImmichAsset> assets;
            try { assets = await _client.GetBucketAssetsAsync(bucket.Raw, isFavorite: true, ct: ct); }
            catch (Exception ex) { Logger.Warn(ex, "Favorite bucket {0} failed", bucket.Raw); allOk = false; continue; }

            foreach (var asset in assets)
            {
                seenFav.Add(asset.Id);
                var rows = _index.RowsForAsset(asset.Id)
                    .Where(r => r.RelPath.StartsWith(favPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (rows.Count == 0) { newAssets.Add(asset); continue; }
                foreach (var (rel, size, isVideo) in rows)
                {
                    used.Add(Path.GetFileName(rel));
                    if (!File.Exists(Path.Combine(_syncRootPath, rel)))
                        CreatePlaceholder(favAbs, Path.GetFileName(rel), asset.Id, size, isVideo, asset.FileCreatedAt);
                }
            }
        }

        if (newAssets.Count > 0)
        {
            using (var sem = new SemaphoreSlim(EnrichConcurrency))
                await Task.WhenAll(newAssets.Select(async a => { await sem.WaitAsync(ct); try { await _client.EnrichAsync(a, ct); } finally { sem.Release(); } }));

            var indexRows = new List<(string, string, bool, long)>();
            foreach (var asset in newAssets)
            {
                try
                {
                    string fileName = Disambiguate(asset.BuildFileName(), used);
                    CreatePlaceholder(favAbs, fileName, asset.Id, asset.FileSizeBytes, asset.IsVideo, asset.FileCreatedAt);
                    indexRows.Add((Path.Combine(FavoritesFolderName, fileName), asset.Id, asset.IsVideo, asset.FileSizeBytes));
                }
                catch (Exception ex) { Logger.Warn(ex, "Favorite placeholder failed for asset {0}", asset.Id); }
            }
            if (indexRows.Count > 0) _index.UpsertMany(indexRows);
        }

        if (allOk)
        {
            int pruned = 0;
            foreach (var (rel, assetId) in _index.RowsUnderPrefix(favPrefix))
                if (!seenFav.Contains(assetId)) { DeletePlaceholder(rel); pruned++; }
            if (pruned > 0) Logger.Info("Pruned {0} un-favorited placeholders", pruned);
        }
    }

    /// <summary>
    /// Mirrors each partner's shared library into <c>Partners\&lt;name&gt;\&lt;month&gt;\</c> (same month
    /// layout as the main timeline), and keeps it in sync (removed assets + removed partners are pruned).
    /// </summary>
    private async Task PopulatePartnersAsync(CancellationToken ct)
    {
        List<ImmichClient.PartnerRef> partners;
        try { partners = await _client.GetPartnersAsync(ct); }
        catch (Exception ex) { Logger.Warn(ex, "Listing partners failed"); return; }
        if (partners.Count == 0) return; // none (or transient) → don't prune

        EnsureFolder(PartnersFolderName);
        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var partner in partners)
        {
            ct.ThrowIfCancellationRequested();
            string folderName = Disambiguate(SanitizeFolderName(partner.Name), usedFolders);
            string partnerRoot = Path.Combine(PartnersFolderName, folderName);
            currentFolders.Add(partnerRoot);
            EnsureFolder(partnerRoot);

            List<ImmichClient.BucketRef> buckets;
            try { buckets = await _client.GetBucketsAsync(userId: partner.Id, ct: ct); }
            catch (Exception ex) { Logger.Warn(ex, "Partner {0} buckets failed", partner.Name); continue; }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool allOk = true;
            foreach (var bucket in buckets)
            {
                ct.ThrowIfCancellationRequested();
                allOk &= await ProcessBucketAsync(bucket, partnerRoot, partner.Id, ct, null, seen);
            }

            if (allOk)
            {
                int pruned = 0;
                foreach (var (rel, assetId) in _index.RowsUnderPrefix(partnerRoot + Path.DirectorySeparatorChar))
                    if (!seen.Contains(assetId)) { DeletePlaceholder(rel); pruned++; }
                if (pruned > 0) Logger.Info("Partner '{0}': pruned {1} removed assets", partner.Name, pruned);
            }
        }

        PruneOrphanFolders(PartnersFolderName, currentFolders);
    }

    /// <summary>
    /// Lightweight refresh of just the newest month bucket — picks up freshly-added photos quickly
    /// without re-walking the whole timeline. Incremental (skips already-indexed assets).
    /// </summary>
    public async Task PopulateNewestAsync(CancellationToken ct = default)
    {
        _index.EnsureCreated();
        Directory.CreateDirectory(_syncRootPath);
        var buckets = await _client.GetBucketsAsync(ct: ct);
        if (buckets.Count > 0) await ProcessBucketAsync(buckets[0], "", null, ct, null);
    }

    /// <summary>
    /// Processes one month bucket: self-heals known placeholders, creates brand-new ones, and adds
    /// every asset id it sees to <paramref name="seenIds"/> (for prune reconciliation). Returns false
    /// if the bucket couldn't be fetched (so the caller knows the picture is incomplete and skips pruning).
    /// </summary>
    private async Task<bool> ProcessBucketAsync(ImmichClient.BucketRef bucket, string relRoot, string? userId, CancellationToken ct, Action? onAsset, HashSet<string>? seenIds = null)
    {
        List<ImmichAsset> assets;
        try { assets = await _client.GetBucketAssetsAsync(bucket.Raw, userId, ct: ct); }
        catch (Exception ex) { Logger.Warn(ex, "Bucket {0} failed", bucket.Raw); return false; }

        // Name the folder from the bucket's OWN month — NOT a local-time conversion. The bucket key
        // ("2026-06-01") already identifies the month; parsing it as UTC and calling ToLocalTime()
        // shifted it back a day for users west of UTC (e.g. -04:00 → 2026-05-31), so June's assets
        // landed in a "May" folder and the current month had no folder at all.
        string monthName = MonthFolderName(bucket.Date);
        string monthRel = string.IsNullOrEmpty(relRoot) ? monthName : Path.Combine(relRoot, monthName);
        string monthAbs = EnsureFolder(monthRel);
        string monthPrefix = monthRel + Path.DirectorySeparatorChar;
        var monthUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Partition into already-known (self-heal only) and brand-new (needs enrich + create).
        // Self-heal is scoped to THIS folder so processing one scope (timeline / partner) never
        // touches another scope's placeholders.
        var newAssets = new List<ImmichAsset>();
        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();
            seenIds?.Add(asset.Id);
            var rows = _index.RowsForAsset(asset.Id)
                .Where(r => r.RelPath.StartsWith(monthPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (rows.Count == 0) { newAssets.Add(asset); continue; }

            foreach (var (rel, size, isVideo) in rows)
            {
                string fn = Path.GetFileName(rel);
                monthUsed.Add(fn);
                if (!File.Exists(Path.Combine(_syncRootPath, rel)))
                    CreatePlaceholder(monthAbs, fn, asset.Id, size, isVideo, asset.FileCreatedAt);
            }
            onAsset?.Invoke();
        }

        if (newAssets.Count == 0) return true;

        // Resolve metadata from the carried-over cache first (a layout migration already knows every
        // asset's size + name), then enrich only what's left — the timeline payload has neither.
        var toEnrich = newAssets;
        if (_metaCache != null)
        {
            toEnrich = new List<ImmichAsset>();
            foreach (var a in newAssets)
            {
                if (_metaCache.TryGetValue(a.Id, out var m) && m.Size > 0)
                {
                    a.FileSizeBytes = m.Size;
                    a.Type = m.IsVideo ? "VIDEO" : "IMAGE";
                    if (string.IsNullOrEmpty(a.OriginalFileName)) a.OriginalFileName = m.Name;
                }
                else toEnrich.Add(a);
            }
        }

        if (toEnrich.Count > 0)
            using (var sem = new SemaphoreSlim(EnrichConcurrency))
            {
                await Task.WhenAll(toEnrich.Select(async a =>
                {
                    await sem.WaitAsync(ct);
                    try { await _client.EnrichAsync(a, ct); } finally { sem.Release(); }
                }));
            }

        var indexRows = new List<(string, string, bool, long)>();
        foreach (var asset in newAssets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                string fileName = Disambiguate(asset.BuildFileName(), monthUsed);
                CreatePlaceholder(monthAbs, fileName, asset.Id, asset.FileSizeBytes, asset.IsVideo, asset.FileCreatedAt);
                indexRows.Add((Path.Combine(monthRel, fileName), asset.Id, asset.IsVideo, asset.FileSizeBytes));
            }
            catch (Exception ex) { Logger.Warn(ex, "Placeholder failed for asset {0}", asset.Id); }
            onAsset?.Invoke();
        }

        if (indexRows.Count > 0) _index.UpsertMany(indexRows);
        return true;
    }

    /// <summary>Readable month folder name, e.g. "2026-06 June" (the yyyy-MM prefix keeps it sortable).
    /// Takes the bucket's own date (its year+month identify the month) — do NOT pass a local-time
    /// conversion, which can roll the month boundary backwards for negative UTC offsets.</summary>
    private static string MonthFolderName(DateTimeOffset bucketMonth) =>
        bucketMonth.ToString("yyyy-MM MMMM", CultureInfo.InvariantCulture);

    /// <summary>One-time cleanup: the flat "Recent" folder was removed from the layout.</summary>
    private void RemoveLegacyRecentFolder()
    {
        try
        {
            string dir = Path.Combine(_syncRootPath, LegacyRecentFolderName);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                _index.DeleteByPathPrefix(LegacyRecentFolderName + "\\");
                Logger.Info("Removed legacy Recent folder");
            }
        }
        catch (Exception ex) { Logger.Warn(ex, "Removing legacy Recent folder failed"); }
    }

    /// <summary>Ensures a plain folder exists under the sync root and returns its absolute path.</summary>
    private string EnsureFolder(string name)
    {
        string abs = Path.Combine(_syncRootPath, name);
        Directory.CreateDirectory(abs);
        return abs;
    }

    /// <summary>Returns a name unique within <paramref name="used"/>, adding " (2)", " (3)", … on collision.</summary>
    private static string Disambiguate(string baseName, HashSet<string> used)
    {
        if (used.Add(baseName)) return baseName;
        string stem = Path.GetFileNameWithoutExtension(baseName);
        string ext = Path.GetExtension(baseName);
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>Creates a single dehydrated, in-sync file placeholder with the asset id as identity.</summary>
    private static void CreatePlaceholder(string baseDir, string fileName, string assetId, long size, bool isVideo, DateTimeOffset createdAt)
    {
        IntPtr identity = IntPtr.Zero;
        try
        {
            byte[] idBytes = Encoding.UTF8.GetBytes(assetId);
            identity = Marshal.AllocHGlobal(idBytes.Length);
            Marshal.Copy(idBytes, 0, identity, idBytes.Length);

            long ft = createdAt.UtcDateTime.ToFileTimeUtc();
            var infos = new[]
            {
                new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = fileName,
                    FsMetadata = new CF_FS_METADATA
                    {
                        BasicInfo = new FILE_BASIC_INFO
                        {
                            CreationTime = ft,
                            LastAccessTime = ft,
                            LastWriteTime = ft,
                            ChangeTime = ft,
                            FileAttributes = FILE_ATTRIBUTE_NORMAL,
                        },
                        FileSize = Math.Max(size, 0),
                    },
                    FileIdentity = identity,
                    FileIdentityLength = (uint)idBytes.Length,
                    Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                },
            };

            int hr = CfCreatePlaceholders(baseDir, infos, 1, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out _);
            if (hr < 0 && hr != unchecked((int)0x800700B7)) // ignore "already exists"
                Logger.Warn("CfCreatePlaceholders 0x{0:X8} for {1}", hr, fileName);
        }
        finally
        {
            if (identity != IntPtr.Zero) Marshal.FreeHGlobal(identity);
        }
    }
}
