# ImmichDrive — Architecture

```
App.xaml ──► MainWindow (invisible host)
               ├── Tray icon (Shell_NotifyIcon)  — status, context menu
               ├── DriveManager                  — orchestrates client + provider + index
               │     ├── CloudProviderService    — cfapi connection + hydration callbacks
               │     ├── SyncRootService          — register/unregister the sync root
               │     ├── PlaceholderPopulator     — build month + Albums/Favorites/Partners tree
               │     ├── DriveSecurity            — read-only deny ACE + folder icon
               │     ├── UploadService            — watch Upload\, POST drops to Immich
               │     └── AssetIndex (SQLite)      — relativePath ⇄ Immich assetId
               └── SettingsWindow (WinUI 3 + NavigationView, on demand)
                     ├── HomePage         — status, drive folder, last-updated
                     ├── ConnectionPage   — server URL + API key + Test/Connect/Disconnect
                     ├── SettingsPage      — app theme + start-at-sign-in
                     └── AboutPage

ImmichDrive.ThumbnailProvider.exe (separate process: out-of-process COM server)
   └── ImmichThumbnailProvider : IThumbnailProvider, IInitializeWithItem
         ├── AssetIndex reader (read-only SQLite)   [linked from ImmichDrive]
         └── ImmichClient (thumbnail GET)           [linked from ImmichDrive]
```

## 1. The two processes

**ImmichDrive.exe** is resident (tray). It is the cloud sync provider: it must be running
for the drive to be "online" and for hydration callbacks to fire. It is also the settings
UI, shown on demand.

**ImmichDrive.ThumbnailProvider.exe** is an **out-of-process COM server** (a real exe,
`OutputType=WinExe`) the *shell* launches on demand — never ImmichDrive.exe. The in-proc
comhost DLL could not host the .NET runtime inside the shell's thumbnail surrogate
(activation failed with `0x80008093`), so the provider is its own exe: it registers a class
factory for the CLSID (`CoRegisterClassObject`), pumps a message loop while the shell uses
it, and exits after a short idle. It runs with package identity, has no UI, and stays lean
and trim/AOT-safe. It shares source with the app via MSBuild `<Compile Include="..\ImmichDrive\…" Link="…">`:
`Models/ImmichAsset.cs`, `Services/ImmichClient.cs`, the read path of `Services/AssetIndex.cs`,
and the `SharedPaths`/`SettingsFile` helpers. Those files therefore stay WinUI-free and
NLog-free.

## 2. Cloud Files API (cfapi) flow

Registration uses the WinRT `StorageProviderSyncRootManager.Register` (clean, declarative)
with a provider id, display name, icon, and the sync-root folder path
(`%UserProfile%\ImmichDrive` by default, configurable). Registration is non-destructive: if
our (stable, FNV-1a-hashed) sync-root id is already registered we leave it intact, since
re-registering tears down placeholders. Then `CfConnectSyncRoot` wires the callback table.
We register `FETCH_DATA` (hydration) and `CANCEL_FETCH_DATA`. See `.claude/docs/cloud-files.md`.

- **Placeholders** are created with `CfCreatePlaceholders`, one `CF_PLACEHOLDER_CREATE_INFO`
  per asset, with `FileIdentity` = UTF-8 Immich asset id, `FsMetadata` = size + timestamps
  (the asset's capture time is set on all four `FILE_BASIC_INFO` fields), and a clean
  `RelativeFileName`. Folders are created as plain directories.
- **Hydration**: on `FETCH_DATA` we read the file's `FileIdentity` (asset id), the requested
  byte range, and a transfer key, then offload the transfer to a task. We stream
  `/api/assets/{id}/original` and push 1 MiB chunks with
  `CfExecute(CF_OPERATION_TYPE_TRANSFER_DATA)`; on completion the range is satisfied and
  Windows marks the region in-sync. On error we report `STATUS_UNSUCCESSFUL` so the open
  fails instead of hanging.
- **State**: newly created placeholders are "in sync, dehydrated." Opening hydrates the
  needed range. We never proactively download (no local copies) except files the user opens.

## 3. Folder & file layout (newest first)

Top level under the sync root: month folders named `yyyy-MM MMMM` (e.g. `2026-06 June`),
plus `Albums`, `Favorites`, `Partners`, and a writable `Upload` folder.

`PlaceholderPopulator` walks the Immich timeline newest-month-first and, per month bucket:
- derives the month-folder name from the **bucket's own year/month** — *not* a local-time
  conversion. Parsing the bucket key as UTC and calling `ToLocalTime()` rolled the month
  boundary backwards for negative UTC offsets (June's assets landed in a "May" folder), so
  the date is used as-is.
- names each file with the **clean original Immich filename** (sanitized; an id-based stem
  as a last-resort fallback). There is no timestamp prefix. Because each placeholder carries
  the asset's real capture time, sorting a folder by Date (descending) in Explorer shows
  newest-first while the name stays clean for attaching.
- disambiguates name collisions within a folder by appending ` (2)`, ` (3)`, ….

`Albums\<name>\`, `Favorites\`, and `Partners\<name>\<month>\` mirror the corresponding
Immich views; their placeholders share the same asset id as the timeline copy, so thumbnails
and hydration work identically. Populate is **self-healing and incremental** (already-indexed
assets are skipped, missing placeholders are recreated from the stored size with no network)
and **prunes** assets/albums/partners removed from Immich — but only when the relevant fetch
succeeded, so a transient network error can never wipe the library. A `LayoutVersion` bump
triggers a one-time clean rebuild (carrying the old index's metadata forward to avoid
re-enriching the whole library).

**Auto-refresh** (`DriveManager`): the newest month bucket is re-polled every ~1 min (catch
new phone photos fast) and the whole library every ~15 min. A single lock serializes all
populate work so polls never overlap each other or the initial/manual populate.

## 4. Read-only drive + upload

The drive is **always read-only to the user** (`DriveSecurity`), via an inherited deny ACE
for the current account (`icacls /deny`) blocking create/edit/delete/rename. The cldflt
filter's placeholder + hydration operations bypass the deny (as OneDrive's read-only folders
do), and the provider lifts the deny per-item when it needs to prune. The sync-root folder
also gets a custom Explorer icon via a `desktop.ini` written at connect (the folder is
flagged `ReadOnly` so Explorer honors it).

The only writable spot is the `Upload` folder (inheritance broken, full control granted).
`UploadService` watches it; a file dropped there is POSTed to Immich (`/api/assets`) and the
local copy deleted. Sync is therefore one-way (Immich → PC) for everything the user sees,
with `Upload` as the single PC → Immich path.

## 5. Thumbnails without hydration

The shell resolves the thumbnail handler for placeholder files via the manifest's
`desktop3:CloudFiles` extension (scoped to the sync root — it does **not** override the
system thumbnailer for other files). `ImmichThumbnailProvider`:
1. `IInitializeWithItem::Initialize` gives us the `IShellItem`; we read its path via
   `SIGDN_FILESYSPATH` **without opening the stream** (so no hydration).
2. We confirm the path is inside the sync root (else return `E_FAIL` so the shell's default
   handler runs), then map the relative path → assetId through the SQLite index.
3. We GET `/api/assets/{id}/thumbnail?size=preview`, decode to an HBITMAP, and return it.

## 6. Settings & index storage

`SettingsManager` (static) serializes `UserSettings` to `%AppData%\ImmichDrive\settings.json`.
`AssetIndex` is a SQLite DB at `%AppData%\ImmichDrive\index.db`. The thumbnail extension —
running out of process inside the shell — must read the **same** physical files; when the app
is packaged (MSIX) AppData is redirected per-package, so we resolve a stable shared location
and store the resolved path where the extension can find it (a breadcrumb under
`%LocalAppData%\ImmichDrive`). The sync-root icon is copied to a fixed, version-independent
path (`C:\ProgramData\ImmichDrive`) so an app update doesn't leave a broken icon.

## 7. Theme / window / tray

Same as Repilot/LittleLauncher: MicaBackdrop settings window, custom title bar,
`NavigationView` + `Frame` with `Tag`→page switch, native window icon via `WM_SETICON`,
`ThemeManager` for light/dark/system, tray icon owned by the invisible `MainWindow` via
`Shell_NotifyIcon` with a registered callback message.

## Status

Implemented and shipping — validated on-device. The cfapi connect/hydration loop, the
sync-root registration + custom nav-pane icon, the out-of-process COM thumbnail handler
scoped to the sync root, and the read-only/upload behavior all run against a live Immich
server. `ImmichClient` still handles both timeline response shapes (columnar SoA vs array)
defensively across Immich versions.
