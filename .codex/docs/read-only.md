# Read-only drive + Upload folder

The drive mirrors Immich one-way (Immich → PC). To stop the user from corrupting that mirror, it's
made **read-only** via a deny ACE; the one writable spot is the **Upload** folder, which pushes
files to Immich.

## Read-only (deny ACE)

`DriveSecurity.EnsureReadOnly(syncRoot)` installs an inheritable deny ACE equivalent to
`(OI)(CI)(DE,DC,WD,WEA)` for the current user. That denies Delete, DeleteChild,
WriteData/AddFile, WriteExtendedAttributes → **blocks edit, new files, delete, rename**.
WriteAttributes stays allowed because Explorer uses it to set cloud-file pin attributes. This also
means a user can alter ordinary file attributes; contents and deletions remain protected.

**Why the provider still works:** cfapi placeholder ops (`CfCreatePlaceholders`) and hydration
(`CfExecute`) are performed by the `cldflt` filter and **bypass the user's deny ACE** (verified on
device — a deleted placeholder in a deny-folder heals on the next sync). This is the same way
OneDrive's read-only folders behave.

Cloud folder badge updates convert ordinary populated directories to in-sync placeholders, then
call `CfSetInSyncState` on later passes. These operations use `WRITE_DAC`, which is not denied by
the content-write ACE. They do not grant permission to add or edit files.

**Two deliberate gaps, handled:**
- We do NOT deny `AD` (AddSubdirectory) — the provider creates month/album/partner folders with a
  normal `CreateDirectory` (cfapi only bypasses for *file* placeholders, not folders). The only leak
  is the user can make an empty folder, but never put a file in it (WD denied).
- The provider's **prune** uses `File.Delete` (user context, NOT cfapi) → blocked by the deny. So
  `DeletePlaceholder` calls `DriveSecurity.AllowDeleteFile` (adds an explicit allow-Delete ACE, which
  beats the inherited deny) before deleting; `PruneOrphanFolders`/wipe call `AllowDeleteTree`
  (`icacls /grant … /T`).

Checked on connect on a background thread. The correct existing ACE is left alone, avoiding a
slow remove-and-add operation over large libraries. If an older ACE still denies WriteAttributes,
`EnsureReadOnly` replaces just the app's old ACE in one security-descriptor update, preserving
unrelated rules. An `icacls` timeout in cleanup terminates the process before returning, so it
cannot continue changing permissions after the next step. The drive is always read-only (no
setting). `RemoveReadOnly` remains for disconnect and layout-migration cleanup.

**Connect ordering:** `SetFolderIcon` checks the root `desktop.ini` content and avoids rewriting
it if the stable icon path is already set. Then `EnsureReadOnly` validates or repairs the deny.
`desktop.ini` remains Hidden and System; excluding it from the ACL is unnecessary. The root
ReadOnly *attribute* makes Explorer read the icon file and does not change the ACL.

## Upload folder

`DriveSecurity.EnsureUploadWritable` creates `Upload\` and runs `icacls /inheritance:r` + `/grant …F`
so it escapes the inherited deny. `UploadService` (a `FileSystemWatcher`) watches it: when a dropped
file becomes readable-exclusive (finished copying), it `POST /api/assets` (multipart: `assetData`,
`deviceAssetId`, `deviceId`, `fileCreatedAt`, `fileModifiedAt`) and **deletes the local file** on
success. The asset then reappears in its date/album folders on the next sync — its "final destination".
Failed uploads are left in Upload and retried on next app start.

`UploadService` also owns the folder's Explorer sync badge through `CloudFolderState`. It
converts an ordinary Upload directory to a cloud folder in place, preserving its writable ACL.
Startup and upload passes check the actual files recursively: an empty folder is in sync;
unfinished copies, failed uploads, and files whose local deletion failed remain pending.
File and directory changes (including deletions) invalidate the cached badge, and failed
Cloud Files state updates are retried on later ticks. Empty subfolders do not count as uploads.
