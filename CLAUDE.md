# ImmichDrive — Claude Code Instructions

## Project overview

ImmichDrive is a Windows 11 **native cloud drive** that surfaces a user's
[Immich](https://immich.app) photo/video library in File Explorer **without storing the
files on the device**. It uses the Windows **Cloud Files API** (`cldapi.dll`, "cfapi") to
create on-demand *placeholder* files: they appear in Explorer like normal files, show
real thumbnails, and are only downloaded ("hydrated") from the Immich server when actually
opened. The point: take a photo on your phone → it auto-syncs to Immich → you grab it from
your PC's file picker (e.g. attaching to a Craigslist listing) without opening the WebUI.

Reuses the LittleLauncher / Repilot (CopilotRekey) patterns: WinUI 3 settings window,
`SettingsManager`/`UserSettings`, native `Shell_NotifyIcon` tray on an invisible host
window, MSIX build script, single-source-of-truth versioning.

## Architecture — TWO components

- **`ImmichDrive.exe`** (project `ImmichDrive/`) — the resident app. A WinUI 3 settings UI
  **and** the cloud sync provider. An invisible `MainWindow` owns the tray icon and hosts
  the cfapi callback connection for the lifetime of the process; `SettingsWindow` is shown
  on demand. On launch it registers the sync root, populates the placeholder tree from the
  Immich timeline, and connects the hydration callbacks. Single-instance via a named mutex.
- **`ImmichDrive.ThumbnailProvider.exe`** (project `ImmichDrive.ThumbnailProvider/`) — an
  **out-of-process** COM **`IThumbnailProvider`** server (an `exe`, not a DLL — an in-proc
  comhost failed to activate in the shell surrogate, `0x80008093`). The shell launches it on
  demand to render thumbnails for placeholder files **without hydrating** them: it maps the
  file path → Immich asset id via the shared on-disk index and fetches Immich's small
  thumbnail over HTTP. Must stay dependency-light and WinUI-free. Registered via the MSIX
  manifest (`com:ExeServer` + the `desktop3:CloudFiles` handlers, scoped to the sync root).

Both ship in one MSIX (`ImmichDriveMSIX/`). MSIX is required so the COM thumbnail handler
and the sync-provider shell integration register cleanly (declared, not poked into HKCR).

## How a photo flows

1. **Populate** — `PlaceholderPopulator` walks the Immich timeline
   (`GET /api/timeline/buckets` → `GET /api/timeline/bucket`, newest first) and calls
   `CfCreatePlaceholders` to lay down 0-byte placeholders under the sync root in month
   folders named `2026-06 June` (from the bucket's own month — **not** a local-time
   conversion), each file keeping its clean original Immich name (`" (2)"` on collision; the
   capture time is set on the placeholder so Explorer's sort-by-date shows newest first). It
   also mirrors `Albums\`, `Favorites\`, and `Partners\<name>\`, and creates a writable
   `Upload\` folder. The Immich **asset id** is stored both as the placeholder's
   `FileIdentity` blob and in the SQLite index (`AssetIndex`).
2. **Thumbnail** — the shell asks `ImmichDrive.ThumbnailProvider` for a thumbnail; it
   resolves path→assetId (index) and GETs `/api/assets/{id}/thumbnail`. No hydration.
3. **Hydrate** — when the user opens/attaches a file, Windows raises a cfapi `FETCH_DATA`
   callback; `CloudProviderService` streams `/api/assets/{id}/original` back via
   `CfExecute(TRANSFER_DATA)`. The file is now real on disk until dehydrated.

## Shared types / index (keep the thumbnail extension lean)

| File | Role |
|---|---|
| `Models/ImmichAsset.cs` | Plain POCO: asset id, type, fileCreatedAt, originalFileName, size. |
| `Services/AssetIndex.cs` | SQLite map relativePath ⇄ assetId (built during populate). Read-only reader is linked into the thumbnail extension. |
| `Services/ImmichClient.cs` | `HttpClient` wrapper: `x-api-key` auth, timeline, albums, favorites, partners, original, thumbnail, upload, connection test. WinUI-free. |

`ImmichClient` and the `AssetIndex` reader are **linked** (`<Compile Include="..\ImmichDrive\...">`)
into the thumbnail extension, so they must stay WinUI-free and trim-safe (no NLog there).

## Conventions / gotchas

- Settings at `%AppData%\ImmichDrive\settings.json`; SQLite index at
  `%AppData%\ImmichDrive\index.db`. When packaged these redirect to package-local AppData —
  both the app and the (out-of-process) shell extension must resolve the **same physical**
  path. Prefer `Environment.GetFolderPath(ApplicationData)`; if Explorer can't see the
  package-redirected path, fall back to a fixed `%LocalAppData%\ImmichDrive` location.
- **The cfapi sync root must be registered before connecting callbacks**, and the process
  must stay alive while the drive is "online" — that is why the tray app is resident.
- Use `[ObservableProperty]` (partial-property form) for every bindable setting; it
  auto-serializes. Suppress side-effects during deserialization with the `_initializing` flag.
- P/Invoke lives in `Classes/NativeMethods.cs` (tray + window) and `Classes/CloudFilter/CfApi.cs`
  (cfapi). Use `[LibraryImport]` for new declarations.
- **PowerShell build scripts must be ASCII** (Windows PowerShell 5.1 reads BOM-less `.ps1`
  as ANSI; a UTF-8 em-dash inside a string breaks parsing).
- MSIX blocks reinstalling the same version with different content — bump `<Version>` in
  `Directory.Build.props` per build.
- `global::` does not parse inside interpolated strings — assign to a local first.

## Building

- Dev UI: `dotnet build ImmichDrive/ImmichDrive.csproj -c Debug` (auto-detects x64/ARM64).
- Thumbnail extension: built as part of the solution / MSIX layout.
- MSIX: `ImmichDriveMSIX/generate-msix-images.ps1` then `build-msix.ps1` (publishes the app
  + the extension exe COM server; `-NoSign` for Store). The manifest holds the real Partner
  Center `<Identity>`; signed sideload builds swap it for a dev identity so they update in
  place, while `-NoSign` (Store) builds keep the real one.

## Topic-specific guidance

| Topic | File |
|---|---|
| Cloud Files API / placeholders / hydration | `.claude/docs/cloud-files.md` |
| Immich REST API surface used | `.claude/docs/immich-api.md` |
| Thumbnail shell extension (COM) | `.claude/docs/thumbnails.md` |
| Read-only drive + Upload folder | `.claude/docs/read-only.md` |
| Settings persistence conventions | `.claude/docs/user-settings.md` |
| WinUI 3 XAML conventions | `.claude/docs/xaml.md` |
| P/Invoke conventions | `.claude/docs/pinvoke.md` |
| Versioning / release | `.claude/docs/versioning.md` |
| MSIX packaging | `.claude/docs/installer.md` |

## Adding a setting / page

See `.claude/commands/add-setting.md` and `add-settings-page.md`. In short: add an
`[ObservableProperty]` to `UserSettings`, bind it `TwoWay` to `SettingsManager.Current`,
and (if it has a side-effect) handle it in a partial `On<Name>Changed` guarded by `_initializing`.
