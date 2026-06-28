using ImmichDrive.Models;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ImmichDrive.Services;

/// <summary>
/// Thin Immich REST client: <c>x-api-key</c> auth, timeline enumeration, per-asset metadata,
/// thumbnails, and ranged original downloads. Deliberately WinUI-free and reflection-light
/// (uses <see cref="JsonDocument"/>, not typed deserialization) so it can be linked into the
/// thumbnail shell extension and stay trim-safe. See <c>.claude/docs/immich-api.md</c>.
/// </summary>
public sealed class ImmichClient : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>The normalized API base, e.g. <c>https://photos.example.com/api</c>.</summary>
    public string ApiBase { get; }

    public ImmichClient(string serverUrl, string apiKey, TimeSpan? timeout = null)
    {
        ApiBase = NormalizeApiBase(serverUrl);
        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Strips a trailing slash and ensures exactly one <c>/api</c> suffix.</summary>
    public static string NormalizeApiBase(string serverUrl)
    {
        string s = (serverUrl ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return s;
        if (!s.EndsWith("/api", StringComparison.OrdinalIgnoreCase)) s += "/api";
        return s;
    }

    /// <summary>Verifies the URL + key by calling <c>/users/me</c>. Returns the display name, or null.</summary>
    public async Task<string?> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{ApiBase}/users/me", ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;
            if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) return n.GetString();
            if (root.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String) return e.GetString();
            return "(connected)";
        }
        catch { return null; }
    }

    /// <summary>One month bucket: the raw API key (passed back verbatim), its parsed date, and count.</summary>
    public readonly record struct BucketRef(string Raw, DateTimeOffset Date, int Count);

    /// <summary>Month buckets, newest first.</summary>
    public async Task<List<BucketRef>> GetBucketsAsync(CancellationToken ct = default)
    {
        var result = new List<BucketRef>();
        using var resp = await _http.GetAsync($"{ApiBase}/timeline/buckets?size=MONTH&isTrashed=false&isArchived=false", ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string? tb = el.TryGetProperty("timeBucket", out var t) ? t.GetString() : null;
            int count = el.TryGetProperty("count", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
            if (tb != null && DateTimeOffset.TryParse(tb, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dto))
                result.Add(new BucketRef(tb, dto, count));
        }
        // Newest first (the API usually returns this order already).
        result.Sort((a, b) => b.Date.CompareTo(a.Date));
        return result;
    }

    /// <summary>
    /// Assets in a month bucket, normalized from either the columnar (struct-of-arrays) or
    /// the legacy array response shape. <paramref name="rawTimeBucket"/> MUST be the exact key
    /// returned by <see cref="GetBucketsAsync"/> — reformatting it (e.g. shifting to UTC) makes
    /// the server return an empty bucket.
    /// </summary>
    public async Task<List<ImmichAsset>> GetBucketAssetsAsync(string rawTimeBucket, CancellationToken ct = default)
    {
        string url = $"{ApiBase}/timeline/bucket?size=MONTH&timeBucket={Uri.EscapeDataString(rawTimeBucket)}&isTrashed=false&isArchived=false";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        return root.ValueKind == JsonValueKind.Array
            ? ParseLegacyArray(root)
            : ParseColumnar(root);
    }

    private static List<ImmichAsset> ParseLegacyArray(JsonElement arr)
    {
        var list = new List<ImmichAsset>();
        foreach (var el in arr.EnumerateArray())
        {
            var a = new ImmichAsset
            {
                Id = GetStr(el, "id") ?? "",
                Type = GetStr(el, "type") ?? "IMAGE",
                OriginalFileName = GetStr(el, "originalFileName") ?? "",
            };
            if (DateTimeOffset.TryParse(GetStr(el, "fileCreatedAt"), out var dto)) a.FileCreatedAt = dto;
            if (el.TryGetProperty("exifInfo", out var exif) && exif.ValueKind == JsonValueKind.Object &&
                exif.TryGetProperty("fileSizeInByte", out var sz) && sz.TryGetInt64(out var szv))
                a.FileSizeBytes = szv;
            if (!string.IsNullOrEmpty(a.Id)) list.Add(a);
        }
        return list;
    }

    private static List<ImmichAsset> ParseColumnar(JsonElement obj)
    {
        var list = new List<ImmichAsset>();
        if (!obj.TryGetProperty("id", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return list;

        var idArr = ids.EnumerateArray().ToArray();
        string[]? created = GetArray(obj, "fileCreatedAt");
        JsonElement[]? isImage = GetRawArray(obj, "isImage");
        string?[]? names = GetNullableArray(obj, "originalFileName"); // present in some versions
        JsonElement[]? durations = GetRawArray(obj, "duration");

        for (int i = 0; i < idArr.Length; i++)
        {
            var a = new ImmichAsset { Id = idArr[i].GetString() ?? "" };
            if (a.Id.Length == 0) continue;

            if (created != null && i < created.Length && DateTimeOffset.TryParse(created[i], out var dto))
                a.FileCreatedAt = dto;

            bool image = true;
            if (isImage != null && i < isImage.Length && isImage[i].ValueKind == JsonValueKind.False) image = false;
            else if (durations != null && i < durations.Length && durations[i].ValueKind == JsonValueKind.String &&
                     durations[i].GetString() is { Length: > 0 } d && d != "0:00:00.00000") image = false;
            a.Type = image ? "IMAGE" : "VIDEO";

            if (names != null && i < names.Length && !string.IsNullOrEmpty(names[i])) a.OriginalFileName = names[i]!;
            list.Add(a);
        }
        return list;
    }

    /// <summary>Fills <see cref="ImmichAsset.OriginalFileName"/> / size from <c>/assets/{id}</c>.</summary>
    public async Task EnrichAsync(ImmichAsset a, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{ApiBase}/assets/{a.Id}", ct);
            if (!resp.IsSuccessStatusCode) return;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var el = doc.RootElement;
            if (string.IsNullOrEmpty(a.OriginalFileName)) a.OriginalFileName = GetStr(el, "originalFileName") ?? a.OriginalFileName;
            if (a.FileSizeBytes == 0 && el.TryGetProperty("exifInfo", out var exif) && exif.ValueKind == JsonValueKind.Object &&
                exif.TryGetProperty("fileSizeInByte", out var sz) && sz.TryGetInt64(out var szv))
                a.FileSizeBytes = szv;
        }
        catch { /* best effort */ }
    }

    /// <summary>Small thumbnail bytes for an asset. <paramref name="size"/> is "thumbnail" or "preview".</summary>
    public async Task<byte[]?> GetThumbnailBytesAsync(string assetId, string size = "preview", CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{ApiBase}/assets/{assetId}/thumbnail?size={size}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch { return null; }
    }

    /// <summary>
    /// Opens the original asset stream for hydration. When <paramref name="offset"/>/<paramref name="length"/>
    /// are given, sends an HTTP Range request so we can satisfy partial cfapi fetches.
    /// Caller disposes the returned response.
    /// </summary>
    public async Task<HttpResponseMessage> GetOriginalAsync(string assetId, long? offset = null, long? length = null, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/assets/{assetId}/original");
        if (offset is { } o)
        {
            long end = length is { } l && l > 0 ? o + l - 1 : long.MaxValue;
            req.Headers.Range = new RangeHeaderValue(o, end == long.MaxValue ? null : end);
        }
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    // ── JSON helpers ────────────────────────────────────────────────
    private static string? GetStr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string[]? GetArray(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(e => e.GetString() ?? "").ToArray() : null;

    private static string?[]? GetNullableArray(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).ToArray() : null;

    private static JsonElement[]? GetRawArray(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().ToArray() : null;

    public void Dispose() => _http.Dispose();
}
