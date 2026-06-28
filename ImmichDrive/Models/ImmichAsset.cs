namespace ImmichDrive.Models;

/// <summary>
/// A single Immich asset, normalized from either the columnar (struct-of-arrays) or the
/// legacy array timeline-bucket response. Plain POCO — kept WinUI-free so it can be linked
/// into the thumbnail shell extension.
/// </summary>
public sealed class ImmichAsset
{
    /// <summary>Immich asset id (GUID string). Stored as the placeholder FileIdentity.</summary>
    public string Id { get; set; } = "";

    /// <summary>"IMAGE" or "VIDEO".</summary>
    public string Type { get; set; } = "IMAGE";

    /// <summary>Original capture/creation time (UTC). Drives the date foldering + filename.</summary>
    public DateTimeOffset FileCreatedAt { get; set; }

    /// <summary>Original file name as stored in Immich (may be empty in thin bucket payloads).</summary>
    public string OriginalFileName { get; set; } = "";

    /// <summary>Original size in bytes, if known (0 = unknown until /assets/{id} is fetched).</summary>
    public long FileSizeBytes { get; set; }

    public bool IsVideo => string.Equals(Type, "VIDEO", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The clean display file name — the original Immich file name (sanitized), or an id-based
    /// fallback. Files carry their real capture timestamp (set on the placeholder), so sorting a
    /// folder by Date shows newest-first; the name itself stays clean for attaching. Collisions
    /// within a folder are disambiguated by the populator.
    /// </summary>
    public string BuildFileName()
    {
        string name = OriginalFileName;
        if (string.IsNullOrWhiteSpace(name))
            name = (Id.Length >= 8 ? Id[..8] : Id) + (IsVideo ? ".mp4" : ".jpg");

        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
