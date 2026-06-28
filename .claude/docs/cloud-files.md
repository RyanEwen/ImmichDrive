# Cloud Files API (cfapi) — placeholders & hydration

ImmichDrive is a cloud sync provider built on the Windows **Cloud Files API**
(`cldapi.dll`, headers `cfapi.h`). This is the same placeholder mechanism OneDrive uses.
P/Invoke declarations live in `ImmichDrive/Classes/CloudFilter/CfApi.cs`.

## 1. Register the sync root

Use the WinRT `Windows.Storage.Provider.StorageProviderSyncRootManager.Register(info)` —
cleaner than the registry. Set on `StorageProviderSyncRootInfo`:

- `Id` — **MUST be `<providerId>!<userSID>`**; the suffix after `!` has to be the current
  user's SID (`WindowsIdentity.GetCurrent().User.Value`). Any other suffix → `Register` fails
  with **0x80070490 (ERROR_NOT_FOUND)** (no cloud icon / no nav-pane entry). We use
  `ImmichDrive.<serverHash>!<SID>` and unregister any stale root on the path first. Needs
  **package identity** (MSIX) to work.
- `Path` — a `StorageFolder` for the sync-root directory (`%UserProfile%\ImmichDrive` default).
- `DisplayNameResource` — "ImmichDrive" (shows in the Explorer navigation pane).
- `IconResource` — `"<exe>,0"` (the app icon).
- `HydrationPolicy = Full`, `HydrationPolicyModifier = AutoDehydrationAllowed`.
- `PopulationPolicy = AlwaysFull` (we enumerate the whole timeline ourselves).
- `InSyncPolicy` — track file attrs/timestamps.
- `Version`, `ProviderId` (a stable GUID), `ShowSiblingsAsGroup = false`.
- `ProtectionMode = Unknown`.

Unregister with `StorageProviderSyncRootManager.Unregister(Id)` when the user disconnects.

## 2. Connect the callback table

`CfConnectSyncRoot(syncRootPath, callbackRegistration[], callbackContext, flags, out key)`.
Flags: `CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH`.
Register at least:

- `CF_CALLBACK_TYPE_FETCH_DATA` → our hydration handler.
- `CF_CALLBACK_TYPE_CANCEL_FETCH_DATA` → cooperatively cancel an in-flight download.
- (`CF_CALLBACK_TYPE_NONE` terminates the array.)

Keep the connection key; call `CfDisconnectSyncRoot(key)` on shutdown. **The process must stay
alive while connected** — this is why the tray app is resident.

## 3. Create placeholders

`PlaceholderPopulator` walks the timeline (newest bucket first) and lays down placeholders.

**Folder layout (under the sync root):**

- **Month folders** `yyyy-MM MMMM` (e.g. `2026-06 June`) — one per timeline bucket. The name
  derives from the bucket's **own** year/month; do **not** convert the bucket time to local
  time (that rolled the month boundary backwards for negative UTC offsets, landing June's
  assets in a "May" folder — `MonthFolderName(bucket.Date)`).
- `Albums\<album name>\…` — mirrors Immich albums.
- `Favorites\…` — flat folder of favorited assets.
- `Partners\<name>\<month>\…` — each partner's shared library, same month layout.
- `Upload\` — the one **writable** folder (one-way Immich → PC sync otherwise; the rest of the
  tree is read-only via a deny ACE — see `read-only.md`).

There is **no `Recent` folder** (removed); `RemoveLegacyRecentFolder` cleans up any stale one.

**Filenames** are the clean original Immich filename (`asset.BuildFileName()`), sanitized, with
`" (2)"`, `" (3)"`, … appended on collision (`Disambiguate`). They are **not** timestamp-prefixed.
Instead the asset's capture time (`fileCreatedAt`) is stamped on the placeholder so Explorer's
sort-by-date (descending) surfaces newest first.

For each asset fill a `CF_PLACEHOLDER_CREATE_INFO` (see `CreatePlaceholder`):

- `RelativeFileName` — the file name only (it's relative to the `baseDir` arg, i.e. the month/
  album folder), e.g. `IMG_1234.jpg`.
- `FsMetadata.FileSize` — original size (enriched from `/assets/{id}` `exifInfo.fileSizeInByte`,
  or 0 if unknown; a non-zero size is needed for correct Explorer display + range hydration).
- `FsMetadata.BasicInfo` — `FILE_BASIC_INFO` with all four timestamps set from `fileCreatedAt`;
  attribute `FILE_ATTRIBUTE_NORMAL` (0x80).
- `FileIdentity` — pointer to UTF‑8 **Immich asset id** bytes; `FileIdentityLength` set. This is
  how the hydration callback knows which asset to fetch.
- `Flags` — `CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC` (so they start "in sync, dehydrated").

Call `CfCreatePlaceholders(baseDir, infos, 1, CF_CREATE_FLAG_NONE, out entriesProcessed)` (the
folder is created with a plain `Directory.CreateDirectory` first, not as a placeholder). The
"already exists" HRESULT `0x800700B7` is ignored. We also write a row to the SQLite `AssetIndex`
(`rel_path` ⇄ `asset_id`, plus `is_video`/`size`) for the thumbnail extension and for
self-healing re-creation without a network round-trip.

Populate is **incremental and self-healing**: already-indexed assets are skipped (only re-created
from the stored size if their placeholder went missing), and a `PruneTimeline` pass removes
placeholders whose asset no longer appears in the timeline — but only when every bucket fetch
succeeded, so a transient network error can't wipe the library.

**Auto-refresh** (`DriveManager`): the newest month bucket is re-polled every ~1 min
(`PopulateNewestAsync`) to catch freshly-synced phone photos fast, and the whole library every
~15 min (`PopulateAsync`). Runs are serialized by a lock — a timer tick bails if a populate is
already in flight.

## 4. Hydration (FETCH_DATA)

The callback (`CloudProviderService.OnFetchData`) gets `CF_CALLBACK_INFO` (has `FileIdentity`,
`ConnectionKey`, `TransferKey`, `RequestKey`, `FileSize`) and `CF_CALLBACK_PARAMETERS_FETCHDATA`
(`RequiredFileOffset`, `RequiredLength`, `OptionalFileOffset`, `OptionalLength`). The callback
itself returns promptly and offloads the work to a `Task`. Steps:

1. Decode `FileIdentity` → UTF‑8 **Immich asset id** (`ReadFileIdentity`, length-bounded).
2. `GET /api/assets/{id}/original` with `Range: bytes=offset-…` for the required slice.
3. Loop reading the HTTP stream in 1 MiB chunks; for each chunk build a `CF_OPERATION_INFO`
   (`Type = CF_OPERATION_TYPE_TRANSFER_DATA`, `ConnectionKey`, `TransferKey`, `RequestKey`) and a
   `CF_OPERATION_PARAMETERS_TRANSFERDATA` (`Buffer`, `Offset`, `Length`, `CompletionStatus =
   STATUS_SUCCESS`) and call `CfExecute(in opInfo, ref opParams)`.
4. On error (or a short read), transfer with `CompletionStatus = STATUS_UNSUCCESSFUL` so the open
   fails cleanly instead of hanging.

A `CF_CALLBACK_TYPE_CANCEL_FETCH_DATA` registration exists (`OnCancelFetchData`); it is currently
a no-op stub — a production build would signal the matching in-flight transfer (keyed by
`TransferKey`) to stop.

## 5. Dehydration / cleanup

`HydrationPolicyModifier = AutoDehydrationAllowed` lets Windows reclaim space automatically.
To force-free a file: `CfDehydratePlaceholder`. On disconnect-account we unregister the sync
root and may delete the local placeholder tree (no originals are stored, so nothing is lost).

## Gotchas

- All cfapi structs must match `cfapi.h` packing exactly — verify on-device; a wrong offset
  silently corrupts hydration.
- `CfCreatePlaceholders` `RelativeFileName` is relative to the `baseDirectoryPath` argument,
  not the sync root — we pass the parent (month/album) folder and a bare file name.
- Re-running populate is idempotent by checking the index first and skipping known assets;
  re-creating a placeholder that already exists is harmless (the `0x800700B7` "already exists"
  HRESULT is ignored), so missing placeholders self-heal without `CfUpdatePlaceholders`.
