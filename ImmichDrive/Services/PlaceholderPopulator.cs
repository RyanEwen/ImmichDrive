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

    private readonly ImmichClient _client;
    private readonly AssetIndex _index;
    private readonly string _syncRootPath;

    public PlaceholderPopulator(ImmichClient client, AssetIndex index, string syncRootPath)
    {
        _client = client;
        _index = index;
        _syncRootPath = syncRootPath;
    }

    /// <summary>Full populate over the whole timeline. Reports (processed, totalApprox) as it goes.</summary>
    public async Task PopulateAsync(IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        _index.EnsureCreated();
        Directory.CreateDirectory(_syncRootPath);
        RemoveLegacyRecentFolder();

        var buckets = await _client.GetBucketsAsync(ct);
        int total = buckets.Sum(b => b.Count);
        int done = 0;

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool allOk = true;
        foreach (var bucket in buckets) // newest first
        {
            ct.ThrowIfCancellationRequested();
            allOk &= await ProcessBucketAsync(bucket, ct, () => progress?.Report((++done, total)), seenIds);
        }

        // Only prune when we have the complete picture (no bucket fetch failed), so a transient
        // network error can never delete the whole library.
        if (allOk) PruneTimeline(seenIds);

        await PopulateAlbumsAsync(ct);

        Logger.Info("Populate complete: {0} processed, {1} indexed, across {2} buckets", done, _index.Count(), buckets.Count);
    }

    /// <summary>Removes timeline placeholders whose asset no longer exists in Immich.</summary>
    private void PruneTimeline(HashSet<string> seenIds)
    {
        int removed = 0;
        foreach (var (rel, assetId) in _index.RowsNotUnderPrefix(AlbumsFolderName + "\\"))
        {
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
            foreach (var asset in assets)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
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

        PruneOrphanAlbumFolders(currentAlbumFolders);
    }

    /// <summary>Removes album folders (and index rows) that no longer match a current Immich album.</summary>
    private void PruneOrphanAlbumFolders(HashSet<string> currentAlbumFolders)
    {
        string albumsAbs = Path.Combine(_syncRootPath, AlbumsFolderName);
        if (!Directory.Exists(albumsAbs)) return;
        foreach (var dir in Directory.GetDirectories(albumsAbs))
        {
            string rel = Path.Combine(AlbumsFolderName, Path.GetFileName(dir));
            if (currentAlbumFolders.Contains(rel)) continue;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, recursive: true);
                Logger.Info("Removed deleted album folder {0}", rel);
            }
            catch (Exception ex) { Logger.Warn(ex, "Removing album folder {0} failed", rel); }
            _index.DeleteByPathPrefix(rel + "\\");
        }
    }

    private static string SanitizeFolderName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.', ' ');
        return name.Length == 0 ? "Album" : name;
    }

    /// <summary>
    /// Lightweight refresh of just the newest month bucket — picks up freshly-added photos quickly
    /// without re-walking the whole timeline. Incremental (skips already-indexed assets).
    /// </summary>
    public async Task PopulateNewestAsync(CancellationToken ct = default)
    {
        _index.EnsureCreated();
        Directory.CreateDirectory(_syncRootPath);
        var buckets = await _client.GetBucketsAsync(ct);
        if (buckets.Count > 0) await ProcessBucketAsync(buckets[0], ct, null);
    }

    /// <summary>
    /// Processes one month bucket: self-heals known placeholders, creates brand-new ones, and adds
    /// every asset id it sees to <paramref name="seenIds"/> (for prune reconciliation). Returns false
    /// if the bucket couldn't be fetched (so the caller knows the picture is incomplete and skips pruning).
    /// </summary>
    private async Task<bool> ProcessBucketAsync(ImmichClient.BucketRef bucket, CancellationToken ct, Action? onAsset, HashSet<string>? seenIds = null)
    {
        List<ImmichAsset> assets;
        try { assets = await _client.GetBucketAssetsAsync(bucket.Raw, ct); }
        catch (Exception ex) { Logger.Warn(ex, "Bucket {0} failed", bucket.Raw); return false; }

        var localMonth = bucket.Date.ToLocalTime();
        string monthActual = MonthFolderName(localMonth);
        string monthAbs = EnsureFolder(monthActual);
        var monthUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Partition into already-known (self-heal only) and brand-new (needs enrich + create).
        var newAssets = new List<ImmichAsset>();
        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();
            seenIds?.Add(asset.Id);
            var rows = _index.RowsForAsset(asset.Id);
            if (rows.Count == 0) { newAssets.Add(asset); continue; }

            // Known — recreate any placeholder that went missing (e.g. after a re-register),
            // and reserve its name so new assets don't collide with it.
            foreach (var (rel, size, isVideo) in rows)
            {
                string fn = Path.GetFileName(rel);
                monthUsed.Add(fn);

                string abs = Path.Combine(_syncRootPath, rel);
                if (File.Exists(abs)) continue;
                try
                {
                    string dir = Path.GetDirectoryName(abs)!;
                    Directory.CreateDirectory(dir);
                    CreatePlaceholder(dir, fn, asset.Id, size, isVideo, asset.FileCreatedAt);
                }
                catch (Exception ex) { Logger.Warn(ex, "Re-create failed for {0}", rel); }
            }
            onAsset?.Invoke();
        }

        if (newAssets.Count == 0) return true;

        // Enrich new assets (real size + name — the timeline payload has neither) concurrently.
        using (var sem = new SemaphoreSlim(EnrichConcurrency))
        {
            await Task.WhenAll(newAssets.Select(async a =>
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
                string baseName = asset.BuildFileName();
                string fileName = Disambiguate(baseName, monthUsed);
                string monthRel = Path.Combine(monthActual, fileName);
                CreatePlaceholder(monthAbs, fileName, asset.Id, asset.FileSizeBytes, asset.IsVideo, asset.FileCreatedAt);
                indexRows.Add((monthRel, asset.Id, asset.IsVideo, asset.FileSizeBytes));
            }
            catch (Exception ex) { Logger.Warn(ex, "Placeholder failed for asset {0}", asset.Id); }
            onAsset?.Invoke();
        }

        if (indexRows.Count > 0) _index.UpsertMany(indexRows);
        return true;
    }

    /// <summary>Readable month folder name, e.g. "2026-06 June" (the yyyy-MM prefix keeps it sortable).</summary>
    private static string MonthFolderName(DateTimeOffset localMonth) =>
        localMonth.ToString("yyyy-MM MMMM", CultureInfo.InvariantCulture);

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
