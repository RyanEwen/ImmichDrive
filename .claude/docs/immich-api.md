# Immich REST API — surface used by ImmichDrive

Base URL is the user's server with `/api` appended (e.g. `https://photos.example.com/api`).

## Authentication

All requests send the header **`x-api-key: <API_KEY>`**. The user creates the key in Immich
under *Account Settings → API Keys*. Also send `Accept: application/json` for JSON endpoints.

Validate the key/URL with `GET /api/users/me` (200 + a user object) — used by the
"Test connection" button. (`GET /api/server/ping` returns `{ "res": "pong" }` but does not
verify the key, so prefer `users/me`.)

## Timeline (the source of the date layout)

1. **Buckets** — `GET /api/timeline/buckets?size=MONTH`
   Optional: `isArchived=false`, `isFavorite`, `isTrashed=false`, `withPartners`, `withStacked`.
   Returns an array of `{ timeBucket: "2026-06-01T00:00:00.000Z", count: N }`, **newest first**.

2. **Bucket contents** — `GET /api/timeline/bucket?size=MONTH&timeBucket=<key>`
   ⚠️ **Pass the `timeBucket` key back EXACTLY as returned by `/buckets`** (e.g. `2026-06-01`
   or `2026-06-01T00:00:00.000Z`). Do **not** re-parse to a `DateTimeOffset` and reformat to
   UTC — a non-midnight-UTC value like `2026-06-01T04:00:00.000Z` returns **0 assets** (the
   server matches the bucket key literally). `GetBucketsAsync` returns a `BucketRef(Raw, Date,
   Count)`; `GetBucketAssetsAsync` takes the **raw** string. (This was the "no files" bug.)

   The response shape changed across versions:
   - **Newer (≈ v1.133+)**: a **columnar / struct-of-arrays** object with parallel arrays.
     Confirmed fields on a live v1.x server: `id`, `fileCreatedAt`, `ownerId`, `isImage`,
     `isFavorite`, `isTrashed`, `duration` (null for images), `thumbhash`, `ratio`,
     `livePhotoVideoId`, `city`, `country`, `visibility`, `status`, `projectionType`,
     `localOffsetHours`. **No `originalFileName` and no file size** here → enrich via
     `/assets/{id}`. Index `i` across the arrays describes asset `i`.
   - **Older**: a plain JSON **array of asset objects** (`{ id, type, fileCreatedAt,
     originalFileName, … }`).
   `ImmichClient.GetBucketAssetsAsync` detects which shape it got (object with arrays vs JSON
   array) and normalizes both to `List<ImmichAsset>`.

We do not need full per-asset metadata for placeholders — id + createdAt + a name + type are
enough. If `originalFileName` isn't in the bucket payload, fall back to `GET /api/assets/{id}`.

## Per-asset

- **Metadata** — `GET /api/assets/{id}` → `{ id, type ("IMAGE"|"VIDEO"), originalFileName,
  fileCreatedAt, exifInfo { fileSizeInByte, … }, checksum, … }`. Used to fill placeholder
  size/timestamps and the display name when the bucket payload is thin.
- **Thumbnail** — `GET /api/assets/{id}/thumbnail?size=thumbnail` (small) or `size=preview`
  (larger). Returns a JPEG/WebP image. Used by the thumbnail shell extension — **no original
  download**.
- **Original** — `GET /api/assets/{id}/original` → the full original bytes. Supports HTTP
  `Range` requests, which we use to satisfy partial cfapi `FETCH_DATA` ranges. Used only when
  the user opens/attaches a file (hydration).

## Notes

- Times are UTC ISO‑8601. Convert `fileCreatedAt` to local for the `Year\Month` foldering and
  the `yyyy-MM-dd_HHmmss_` filename prefix.
- Videos are included; the layout treats them the same (placeholder + thumbnail + hydrate).
- Be tolerant of extra/missing JSON fields — Immich evolves quickly. Deserialize loosely.
