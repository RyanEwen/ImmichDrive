using Microsoft.Data.Sqlite;
using System.IO;

namespace ImmichDrive.Services;

/// <summary>
/// SQLite map between a placeholder's sync-root-relative path and its Immich asset id (+ size,
/// so a missing placeholder can be re-created without re-fetching metadata). Written during
/// timeline population by the app; the read path is linked into the thumbnail shell extension
/// to resolve a file path → asset id without hydrating the file. WinUI-free. Stored at
/// <c>%AppData%\ImmichDrive\index.db</c>.
/// </summary>
public sealed class AssetIndex
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public AssetIndex(string? dbPath = null)
    {
        _dbPath = dbPath ?? SharedPaths.IndexDbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=2000;";
        pragma.ExecuteNonQuery();
        return c;
    }

    public void EnsureCreated()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS assets (
                rel_path  TEXT PRIMARY KEY,
                asset_id  TEXT NOT NULL,
                is_video  INTEGER NOT NULL DEFAULT 0,
                size      INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_assets_id ON assets(asset_id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Bulk insert in one transaction (used during population).</summary>
    public void UpsertMany(IEnumerable<(string RelPath, string AssetId, bool IsVideo, long Size)> rows)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO assets (rel_path, asset_id, is_video, size) VALUES ($p, $a, $v, $s)
            ON CONFLICT(rel_path) DO UPDATE SET asset_id = excluded.asset_id, is_video = excluded.is_video, size = excluded.size;
            """;
        var pp = cmd.Parameters.Add("$p", SqliteType.Text);
        var pa = cmd.Parameters.Add("$a", SqliteType.Text);
        var pv = cmd.Parameters.Add("$v", SqliteType.Integer);
        var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
        foreach (var (rel, id, video, size) in rows)
        {
            pp.Value = Normalize(rel); pa.Value = id; pv.Value = video ? 1 : 0; ps.Value = size;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>All indexed placeholder rows for an asset (0, 1, or 2 — month + Recent).</summary>
    public List<(string RelPath, long Size, bool IsVideo)> RowsForAsset(string assetId)
    {
        var list = new List<(string, long, bool)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rel_path, size, is_video FROM assets WHERE asset_id = $a;";
        cmd.Parameters.AddWithValue("$a", assetId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2) != 0));
        return list;
    }

    /// <summary>Resolves a relative path to an Immich asset id, or null. Read-only path used by the extension.</summary>
    public string? TryGetAssetId(string relPath)
    {
        try
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT asset_id FROM assets WHERE rel_path = $p LIMIT 1;";
            cmd.Parameters.AddWithValue("$p", Normalize(relPath));
            return cmd.ExecuteScalar() as string;
        }
        catch { return null; }
    }

    // Prefix queries use a range [lo, hi) rather than LIKE, so '_' / '%' in folder names (sanitized
    // album names can contain '_') aren't treated as wildcards.
    private static (string Lo, string Hi) PrefixRange(string prefix)
    {
        string lo = Normalize(prefix);
        string hi = lo[..^1] + (char)(lo[^1] + 1);
        return (lo, hi);
    }

    /// <summary>All (relPath, assetId) rows whose path starts with the prefix (e.g. an album folder).</summary>
    public List<(string Rel, string AssetId)> RowsUnderPrefix(string prefix)
    {
        var (lo, hi) = PrefixRange(prefix);
        return RowsWhere("rel_path >= $lo AND rel_path < $hi", lo, hi);
    }

    /// <summary>All (relPath, assetId) rows whose path does NOT start with the prefix (e.g. timeline = not under Albums\).</summary>
    public List<(string Rel, string AssetId)> RowsNotUnderPrefix(string prefix)
    {
        var (lo, hi) = PrefixRange(prefix);
        return RowsWhere("rel_path < $lo OR rel_path >= $hi", lo, hi);
    }

    /// <summary>Every (relPath, assetId) row.</summary>
    public List<(string Rel, string AssetId)> AllRows()
    {
        var list = new List<(string, string)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT rel_path, asset_id FROM assets;";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    private List<(string, string)> RowsWhere(string where, string lo, string hi)
    {
        var list = new List<(string, string)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT rel_path, asset_id FROM assets WHERE {where};";
        cmd.Parameters.AddWithValue("$lo", lo);
        cmd.Parameters.AddWithValue("$hi", hi);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    /// <summary>Removes a single row by relative path.</summary>
    public void DeleteByRelPath(string relPath)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM assets WHERE rel_path = $p;";
        cmd.Parameters.AddWithValue("$p", Normalize(relPath));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Removes all rows whose relative path starts with the given prefix (e.g. "Recent\").</summary>
    public void DeleteByPathPrefix(string prefix)
    {
        var (lo, hi) = PrefixRange(prefix);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM assets WHERE rel_path >= $lo AND rel_path < $hi;";
        cmd.Parameters.AddWithValue("$lo", lo);
        cmd.Parameters.AddWithValue("$hi", hi);
        cmd.ExecuteNonQuery();
    }

    public long Count()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM assets;";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Deletes the database file (and WAL/SHM) — used for a clean layout migration.</summary>
    public static void DeleteDatabaseFile(string? dbPath = null)
    {
        string path = dbPath ?? SharedPaths.IndexDbPath;
        SqliteConnection.ClearAllPools();
        foreach (var p in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }

    private static string Normalize(string relPath) =>
        relPath.Replace('/', '\\').TrimStart('\\');
}
