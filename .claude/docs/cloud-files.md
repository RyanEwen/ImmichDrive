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

For each asset (and each directory), fill a `CF_PLACEHOLDER_CREATE_INFO`:

- `RelativeFileName` — e.g. `2026\06-June\2026-06-27_142530_IMG_1234.jpg`.
- `FsMetadata.FileSize` — original size (from `/assets/{id}` exif `fileSizeInByte`, or 0 if
  unknown; a non-zero size is needed for correct Explorer display + range hydration).
- `FsMetadata.BasicInfo` — `FILE_BASIC_INFO` timestamps from `fileCreatedAt`; attributes
  `FILE_ATTRIBUTE_NORMAL` (files) / `FILE_ATTRIBUTE_DIRECTORY` (folders).
- `FileIdentity` — pointer to UTF‑8 **Immich asset id** bytes; `FileIdentityLength` set.
  This is how the hydration callback knows which asset to fetch.
- `Flags` — `CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC` (so they start "in sync, dehydrated").

Call `CfCreatePlaceholders(baseDir, infos, count, CF_CREATE_FLAG_NONE, out entriesProcessed)`.
Create parent directory placeholders first (or ensure the directory exists). We also write a
row to the SQLite `AssetIndex` (relativePath ⇄ assetId) for the thumbnail extension.

## 4. Hydration (FETCH_DATA)

The callback gets `CF_CALLBACK_INFO` (has `FileIdentity`, `ConnectionKey`, `TransferKey`,
`FileSize`) and `CF_CALLBACK_PARAMETERS.FetchData` (`RequiredFileOffset`, `RequiredLength`,
`OptionalFileOffset`, `OptionalLength`). Steps:

1. Decode `FileIdentity` → asset id.
2. `GET /api/assets/{id}/original` with `Range: bytes=offset-...` (or full).
3. Loop reading the HTTP stream; for each chunk build a `CF_OPERATION_INFO`
   (`Type = CF_OPERATION_TYPE_TRANSFER_DATA`, `ConnectionKey`, `TransferKey`) and a
   `CF_OPERATION_PARAMETERS.TransferData` (`Buffer`, `Offset`, `Length`, `CompletionStatus =
   STATUS_SUCCESS`) and call `CfExecute(opInfo, ref opParams)`.
4. On error, transfer with `CompletionStatus = STATUS_UNSUCCESSFUL` so the open fails cleanly.

Honor cancellation: if a `CANCEL_FETCH_DATA` arrives for the same `TransferKey`, stop the loop.

## 5. Dehydration / cleanup

`HydrationPolicyModifier = AutoDehydrationAllowed` lets Windows reclaim space automatically.
To force-free a file: `CfDehydratePlaceholder`. On disconnect-account we unregister the sync
root and may delete the local placeholder tree (no originals are stored, so nothing is lost).

## Gotchas

- All cfapi structs must match `cfapi.h` packing exactly — verify on-device; a wrong offset
  silently corrupts hydration.
- `CfCreatePlaceholders` `RelativeFileName` is relative to the `baseDirectoryPath` argument,
  not the sync root — pass the sync root for top-level, or the parent dir for nesting.
- Re-running populate must be idempotent: skip assets already present (check the index), and
  use `CfUpdatePlaceholders` / `CfSetInSyncState` rather than recreating.
