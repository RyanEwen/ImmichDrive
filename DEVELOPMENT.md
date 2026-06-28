# ImmichDrive — Development

ImmichDrive ships as a single MSIX containing **two components**:

- **`ImmichDrive.exe`** — the resident WinUI 3 app. It is both the settings UI **and** the cloud
  sync provider: an invisible host window owns the tray icon and the Cloud Files (cfapi) connection
  for the life of the process, registers the sync root, populates placeholders from the Immich
  timeline, and services hydration callbacks. It must stay running for the drive to be online.
- **`ImmichDrive.ThumbnailProvider`** — an out-of-process `IThumbnailProvider` COM server that the
  shell launches (in its own process) to render placeholder thumbnails **without hydrating** them.
  It stays dependency-light and WinUI-free, and shares `ImmichClient` and the `AssetIndex` reader
  with the app via linked source files.

## Prerequisites

- Windows 11 (build 22621+)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows SDK packaging tools (`makeappx` / `makepri` / `signtool`) — `build-msix.ps1` auto-acquires
  them via the `Microsoft.Windows.SDK.BuildTools` NuGet package if they aren't already installed.

`Directory.Build.props` is the single source of truth for the version and auto-detects the platform
from `PROCESSOR_ARCHITECTURE` (ARM64 → ARM64, otherwise x64). Override with `-p:Platform=x64` or
`-p:Platform=ARM64`.

## Build the app (dev)

```powershell
dotnet build ImmichDrive/ImmichDrive.csproj -c Debug
```

Good for iterating on the settings UI. The Cloud Files provider and the COM thumbnail handler only
register cleanly from the packaged build, so use the MSIX for end-to-end testing.

## Build the MSIX

```powershell
powershell -File ImmichDriveMSIX/generate-msix-images.ps1   # (re)generate the icon + visual assets
powershell -File ImmichDriveMSIX/build-msix.ps1             # sideload build (dev-signed, updates in place)
powershell -File ImmichDriveMSIX/build-msix.ps1 -NoSign     # Store build (unsigned; the Store re-signs)
```

- Bump `<Version>` in `Directory.Build.props` for each build — MSIX blocks reinstalling the same
  version with different content.
- **Identity:** sideload (signed) builds swap in a local dev identity so they form a stable package
  family that updates your install in place; `-NoSign` (Store) builds keep the real Partner Center
  identity declared in `ImmichDriveMSIX/Package.appxmanifest`.
- `generate-msix-images.ps1` renders one master icon and downscales it into every Store logo/tile,
  the app `.ico`, and the in-app PNG. PowerShell build scripts must be **ASCII only** (Windows
  PowerShell 5.1 reads BOM-less `.ps1` as ANSI).

## Architecture & internals

- **[ARCHITECTURE.md](ARCHITECTURE.md)** — the two processes, the cfapi flow, the date layout,
  thumbnails-without-hydration, and storage paths.
- Topic deep-dives live under **`.claude/docs/`**:

  | Topic | Doc |
  |---|---|
  | Cloud Files API / placeholders / hydration | [`.claude/docs/cloud-files.md`](.claude/docs/cloud-files.md) |
  | Immich REST API surface used | [`.claude/docs/immich-api.md`](.claude/docs/immich-api.md) |
  | Thumbnail shell extension (COM) | [`.claude/docs/thumbnails.md`](.claude/docs/thumbnails.md) |
  | Read-only drive + Upload folder | [`.claude/docs/read-only.md`](.claude/docs/read-only.md) |
  | Settings persistence conventions | [`.claude/docs/user-settings.md`](.claude/docs/user-settings.md) |
  | WinUI 3 XAML conventions | [`.claude/docs/xaml.md`](.claude/docs/xaml.md) |
  | P/Invoke conventions | [`.claude/docs/pinvoke.md`](.claude/docs/pinvoke.md) |
  | Versioning / release | [`.claude/docs/versioning.md`](.claude/docs/versioning.md) |
  | MSIX packaging | [`.claude/docs/installer.md`](.claude/docs/installer.md) |

## Tech stack

- .NET 10, target `net10.0-windows10.0.22000.0`, platforms `x64` and `ARM64`
- WinUI 3 / Windows App SDK 1.8
- CommunityToolkit.Mvvm (`[ObservableProperty]`), `System.Text.Json` settings,
  Microsoft.Data.Sqlite index, NLog
- Native tray via `Shell_NotifyIcon`; Cloud Files API via P/Invoke (`cldapi.dll`)
