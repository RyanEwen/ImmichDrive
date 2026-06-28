# ImmichDrive — Architecture

```
App.xaml ──► MainWindow (invisible host)
               ├── Tray icon (Shell_NotifyIcon)  — status, context menu
               ├── CloudProviderService          — cfapi connection + hydration callbacks
               │     ├── SyncRootService         — register/unregister the sync root
               │     ├── PlaceholderPopulator    — build Year\Month + Recent tree
               │     └── AssetIndex (SQLite)      — relativePath ⇄ Immich assetId
               └── SettingsWindow (WinUI 3 + NavigationView, on demand)
                     ├── HomePage         — status, drive folder, recent count
                     ├── ConnectionPage   — server URL + API key + Test/Connect
                     ├── SettingsPage      — theme, Recent window, startup, storage
                     └── AboutPage

ImmichDrive.ThumbnailProvider.dll (separate process: Explorer / dllhost)
   └── ThumbnailProvider : IThumbnailProvider, IInitializeWithItem
         ├── AssetIndex reader (read-only SQLite)   [linked from ImmichDrive]
         └── ImmichClient (thumbnail GET)           [linked from ImmichDrive]
```

## 1. The two processes

**ImmichDrive.exe** is resident (tray). It is the cloud sync provider: it must be running
for the drive to be "online" and for hydration callbacks to fire. It is also the settings
UI, shown on demand.

**ImmichDrive.ThumbnailProvider.dll** is an in-process COM server the *shell* loads (never
ImmichDrive.exe). It has no UI and must be lean and trim/AOT-safe. It shares two source
files with the app via MSBuild `<Compile Include="..\ImmichDrive\…" Link="…">`:
`Services/ImmichClient.cs` and the read path of `Services/AssetIndex.cs`. Those files
therefore stay WinUI-free and NLog-free.

## 2. Cloud Files API (cfapi) flow

Registration uses the WinRT `StorageProviderSyncRootManager.Register` (clean, declarative)
with a provider id, display name, icon, and the sync-root folder path
(`%UserProfile%\ImmichDrive` by default, configurable). Then `CfConnectSyncRoot` wires the
callback table. We register a `FETCH_DATA` callback (hydration) and optionally
`CANCEL_FETCH_DATA`. See `.claude/docs/cloud-files.md`.

- **Placeholders** are created with `CfCreatePlaceholders`, one `CF_PLACEHOLDER_CREATE_INFO`
  per asset, with `FileIdentity` = UTF-8 Immich asset id, `FsMetadata` = size + timestamps
  (from the timeline), and `RelativeFileName` = `Year\MM-Month\<date>_<name>`. Folders are
  created the same way (directory placeholders) or as plain directories.
- **Hydration**: on `FETCH_DATA` we get the file's `FileIdentity` (asset id), the requested
  byte range, and a transfer key. We stream `/api/assets/{id}/original` and push chunks with
  `CfExecute(CF_OPERATION_TYPE_TRANSFER_DATA)`, reporting progress; on completion the range
  is satisfied and Windows marks the region in-sync.
- **State**: newly created placeholders are "in sync, dehydrated." Opening hydrates the
  needed range. We never proactively download (no local copies) except files the user opens.

## 3. Date layout (newest first)

The Immich timeline returns month buckets newest-first. `PlaceholderPopulator`:
- creates `Year\MM-MonthName\` directory placeholders on demand,
- names each file `yyyy-MM-dd_HHmmss_<originalFileName>` so a plain alphabetical Explorer
  sort (descending) shows newest first, and collisions are avoided,
- additionally mirrors assets from the last `RecentDays` (default 14) into a flat top-level
  `Recent\` folder for one-click access. The mirror is a second placeholder pointing at the
  same asset id (hydrates independently; cheap because both are 0-byte until opened).

## 4. Thumbnails without hydration

The shell resolves a thumbnail handler by file extension. Our `IThumbnailProvider`:
1. `IInitializeWithItem::Initialize` gives us the `IShellItem`; we read its path **without
   opening the stream** (so no hydration).
2. We map path → assetId through the SQLite index (or, fallback, by reading the placeholder
   `FileIdentity` via `CfGetPlaceholderInfo` opened with `FILE_FLAG_OPEN_NO_RECALL`).
3. We GET `/api/assets/{id}/thumbnail?size=preview`, decode to an HBITMAP, and return it.

Registration scoping (so we only override thumbnails for files inside the sync root, not all
JPEGs system-wide) is the one piece that needs on-device validation — see
`.claude/docs/thumbnails.md`. The handler returns `WTS_NOTCACHED`/falls through for paths
outside the sync root.

## 5. Settings & index storage

`SettingsManager` (static) serializes `UserSettings` to `%AppData%\ImmichDrive\settings.json`.
`AssetIndex` is a SQLite DB at `%AppData%\ImmichDrive\index.db`. The thumbnail extension —
running out of process inside the shell — must read the **same** physical files; when the app
is packaged (MSIX) AppData is redirected per-package, so we resolve a stable shared location
and store the resolved path where the extension can find it (a breadcrumb under
`%LocalAppData%\ImmichDrive`).

## 6. Theme / window / tray

Same as Repilot/LittleLauncher: MicaBackdrop settings window, custom title bar,
`NavigationView` + `Frame` with `Tag`→page switch, native window icon via `WM_SETICON`,
`ThemeManager` for light/dark/system, tray icon owned by the invisible `MainWindow` via
`Shell_NotifyIcon` with a registered callback message.

## Open items requiring a device + live Immich server

- cfapi `CfConnectSyncRoot` callback marshalling and `CfExecute(TRANSFER_DATA)` chunk loop.
- Exact `StorageProviderSyncRootInfo` fields for the Explorer nav-pane entry + custom icon.
- COM thumbnail-handler registration scope (sync-root-only vs per-extension guard).
- Immich timeline response shape across versions (columnar SoA vs array) — `ImmichClient`
  handles both defensively.
