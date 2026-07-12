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

   Both `/buckets` and `/bucket` are sent with a shared filter (`BucketQuery`):
   `size=MONTH&isTrashed=false&isArchived=false`, plus optional `userId=<partner id>`
   (target a partner's library) and `isFavorite=true` (favorites only).

   The response shape changed across versions:
   - **Newer (≈ v1.133+)**: a **columnar / struct-of-arrays** object with parallel arrays.
     Confirmed fields on a live v1.x server: `id`, `fileCreatedAt`, `ownerId`, `isImage`,
     `isFavorite`, `isTrashed`, `duration` (null for images), `thumbhash`, `ratio`,
     `livePhotoVideoId`, `city`, `country`, `visibility`, `status`, `projectionType`,
     `localOffsetHours`. `ParseColumnar` reads `id`, `fileCreatedAt`, `originalFileName`
     (present in some versions; optional), and derives IMAGE/VIDEO from `isImage` (falling
     back to a non-zero `duration` string). File **size** is never in this payload, and the
     name often isn't either → enrich via `/assets/{id}`. Index `i` across the arrays
     describes asset `i`.
   - **Older**: a plain JSON **array of asset objects** (`{ id, type, fileCreatedAt,
     originalFileName, exifInfo.fileSizeInByte, … }`); parsed by `ParseLegacyArray`.
   `ImmichClient.GetBucketAssetsAsync` detects which shape it got (JSON array → legacy, else
   columnar) and normalizes both to `List<ImmichAsset>`.

We do not need full per-asset metadata for placeholders — id + createdAt + a name + type are
enough. If `originalFileName` isn't in the bucket payload, fall back to `GET /api/assets/{id}`.

## Per-asset

- **Metadata (enrich)** — `GET /api/assets/{id}` → `{ id, originalFileName, exifInfo
  { fileSizeInByte, … }, … }`. `EnrichAsync` reads only `originalFileName` and
  `exifInfo.fileSizeInByte`, and only fills them if still empty/zero — used when the columnar
  bucket payload omits the name and size. Best-effort: failures are swallowed.
- **Thumbnail** — `GET /api/assets/{id}/thumbnail?size=<size>` where `size` is `thumbnail`
  (small) or `preview` (larger; the client default). Returns a JPEG/WebP image. Used by the
  thumbnail shell extension — **no original download**.
- **Original** — `GET /api/assets/{id}/original` → the full original bytes. `GetOriginalAsync`
  sends an HTTP `Range` header when given an offset/length, to satisfy partial cfapi
  `FETCH_DATA` ranges. Used only when the user opens/attaches a file (hydration).
- **Upload** — `POST /api/assets`, `multipart/form-data` with parts: `assetData` (the file
  bytes, `application/octet-stream`), `deviceAssetId` (a synthesized
  `ImmichDrive-<name>-<len>-<ticks>` key), `deviceId` (`ImmichDrive`), `fileCreatedAt`,
  `fileModifiedAt` (both ISO‑8601 UTC). `UploadAssetAsync` returns true on any success status
  (created or duplicate).

## Albums

- **List** — `GET /api/albums` → array of `{ id, albumName, assetCount, … }`.
- **Album contents** — **`GET /api/timeline/bucket?size=MONTH&albumId={id}`** (+ its `/timeline/buckets?…&albumId={id}`
  to enumerate the months). ⚠️ **Immich v3 removed the embedded `assets` array from
  `GET /api/albums/{id}`** — it now returns only album metadata (identical keys to the list entry), even
  with `?withoutAssets=false`. Older versions returned `{ …, assets: [ full asset objects ] }`; relying on
  that shape silently yielded **empty album folders** on v3 (the call is a 200, so nothing logs).
  `GetAlbumAssetsAsync` now walks the album-scoped timeline instead. Scope the query by `albumId` **alone**
  (no `isArchived`/`isTrashed` filter — those would drop assets the album legitimately contains, so the
  bucket count wouldn't match `assetCount`).
- The album timeline is **columnar** (same struct-of-arrays as the main timeline: no size, usually no name).
  Album assets are duplicates of the timeline copy, so `PopulateAlbumsAsync` resolves name/size/type from the
  **index** (`TryResolveFromIndex`, the asset's timeline row) and only network-enriches the few not indexed
  (e.g. an archived asset absent from the main timeline). It mirrors them into `Albums\<album name>\`;
  placeholders reuse the asset id, so thumbnails + hydration work the same as the timeline copy.

## Partners

- **List** — `GET /api/partners?direction=shared-with` → array of partners who share their
  library **with** the current user. `GetPartnersAsync` reads `id` and `name` (falling back to
  `email`, then `"Partner"`). The partner `id` is fed back as the `userId` filter on the
  timeline queries above to enumerate that partner's assets. Non-200 → empty list.

## Notes

- Times are UTC ISO‑8601. Convert `fileCreatedAt` to local for the `Year\Month` foldering and
  the `yyyy-MM-dd_HHmmss_` filename prefix.
- Videos are included; the layout treats them the same (placeholder + thumbnail + hydrate).
- Be tolerant of extra/missing JSON fields — Immich evolves quickly. Deserialize loosely.
