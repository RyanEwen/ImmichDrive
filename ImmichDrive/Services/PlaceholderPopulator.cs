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

        foreach (var bucket in buckets) // newest first
        {
            ct.ThrowIfCancellationRequested();
            await ProcessBucketAsync(bucket, ct, () => progress?.Report((++done, total)));
        }

        Logger.Info("Populate complete: {0} processed, {1} indexed, across {2} buckets", done, _index.Count(), buckets.Count);
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

    /// <summary>Processes one month bucket: self-heals known placeholders, creates brand-new ones.</summary>
    private async Task ProcessBucketAsync(ImmichClient.BucketRef bucket, CancellationToken ct, Action? onAsset)
    {
        List<ImmichAsset> assets;
        try { assets = await _client.GetBucketAssetsAsync(bucket.Raw, ct); }
        catch (Exception ex) { Logger.Warn(ex, "Bucket {0} failed", bucket.Raw); return; }

        var localMonth = bucket.Date.ToLocalTime();
        string monthActual = MonthFolderName(localMonth);
        string monthAbs = EnsureFolder(monthActual);
        var monthUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Partition into already-known (self-heal only) and brand-new (needs enrich + create).
        var newAssets = new List<ImmichAsset>();
        foreach (var asset in assets)
        {
            ct.ThrowIfCancellationRequested();
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

        if (newAssets.Count == 0) return;

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
