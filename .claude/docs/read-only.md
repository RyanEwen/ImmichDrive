# Read-only drive + Upload folder

The drive mirrors Immich one-way (Immich → PC). To stop the user from corrupting that mirror, it's
made **read-only** via a deny ACE; the one writable spot is the **Upload** folder, which pushes
files to Immich.

## Read-only (deny ACE)

`DriveSecurity.ApplyReadOnly(syncRoot)` runs `icacls "<root>" /deny *<SID>:(OI)(CI)(DE,DC,WD,WA,WEA)`
for the current user, inherited by all current + future items. That denies Delete, DeleteChild,
WriteData/AddFile, WriteAttributes, WriteExtendedAttributes → **blocks edit, new files, delete, rename**.

**Why the provider still works:** cfapi placeholder ops (`CfCreatePlaceholders`) and hydration
(`CfExecute`) are performed by the `cldflt` filter and **bypass the user's deny ACE** (verified on
device — a deleted placeholder in a deny-folder heals on the next sync). This is the same way
OneDrive's read-only folders behave.

**Two deliberate gaps, handled:**
- We do NOT deny `AD` (AddSubdirectory) — the provider creates month/album/partner folders with a
  normal `CreateDirectory` (cfapi only bypasses for *file* placeholders, not folders). The only leak
  is the user can make an empty folder, but never put a file in it (WD denied).
- The provider's **prune** uses `File.Delete` (user context, NOT cfapi) → blocked by the deny. So
  `DeletePlaceholder` calls `DriveSecurity.AllowDeleteFile` (adds an explicit allow-Delete ACE, which
  beats the inherited deny) before deleting; `PruneOrphanFolders`/wipe call `AllowDeleteTree`
  (`icacls /grant … /T`).

Applied on connect on a background thread (icacls over ~47k files ≈ 4s). Toggle via the
`ReadOnlyDrive` setting (`DriveManager.RefreshSecurity`); removed on disconnect.

## Upload folder

`DriveSecurity.EnsureUploadWritable` creates `Upload\` and runs `icacls /inheritance:r` + `/grant …F`
so it escapes the inherited deny. `UploadService` (a `FileSystemWatcher`) watches it: when a dropped
file becomes readable-exclusive (finished copying), it `POST /api/assets` (multipart: `assetData`,
`deviceAssetId`, `deviceId`, `fileCreatedAt`, `fileModifiedAt`) and **deletes the local file** on
success. The asset then reappears in its date/album folders on the next sync — its "final destination".
Failed uploads are left in Upload and retried on next app start.
