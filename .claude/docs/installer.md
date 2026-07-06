# MSIX packaging

`ImmichDriveMSIX/` packages the app **and** the thumbnail extension into one MSIX. MSIX is
required so the COM thumbnail handler and the sync-provider shell integration register
declaratively (no manual HKCR writes).

## Pieces

- `Package.appxmanifest` — one `<Application Id="App">` (the WinUI tray app,
  `Windows.FullTrustApplication`). Its `Extensions` declare:
  - `com:Extension` (`windows.comServer`) — an **out-of-process** `com:ExeServer`
    (`ImmichDrive.ThumbnailProvider.exe`) registering the thumbnail provider CLSID. The shell
    launches it on demand to render placeholder thumbnails.
  - `desktop3:CloudFiles` (`windows.cloudFiles`) — scopes the thumbnail/state/property handlers
    to our sync-root placeholders only (the same CLSID).
  - `uap5:StartupTask` — auto-start at sign-in so the provider is running to hydrate files.

  `VERSION_PLACEHOLDER` / `ARCH_PLACEHOLDER` are stamped by the build script. Capabilities:
  `internetClient` (reach the Immich server) + `runFullTrust`.
- `build-msix.ps1` — reads the version, publishes `ImmichDrive` (self-contained,
  `WindowsPackageType=MSIX`) **and** `ImmichDrive.ThumbnailProvider` (self-contained, so the
  out-of-proc comhost resolves the .NET runtime from its own folder when the shell activates it),
  assembles the layout (exe + extension EXE/DLLs + compiled XAML `.xbf` + stamped manifest +
  `Images`), runs `makepri`, packages with `makeappx`, signs with `signtool`. It auto-acquires
  the SDK BuildTools (`makeappx`/`makepri`/`signtool`) via NuGet if they aren't already on the
  machine, and auto-generates a self-signed dev cert if `ImmichDrive.pfx` is missing.
  **ASCII only.** `-NoSign` for Store uploads.
- `generate-msix-images.ps1` — renders **one** master icon (the "Photo Panes" mark: a dark
  slate rounded tile with a 2x2 grid — three tiny photo scenes plus a white cloud pane; an
  original design, deliberately NOT Immich's iris logo, which got the app rejected under
  Store policy 10.1.1.11) and downscales it (4x supersampling per size) into every Store
  tile/logo/splash PNG under `Images\`, **plus** the multi-size app `.ico`
  (`ImmichDrive/Resources/ImmichDrive.ico`) and a 256px in-app PNG
  (`ImmichDrive/Resources/ImmichDrive.png`). The same detailed art is used at every size
  (no simplified small-size variants — Ryan's explicit choice). **ASCII only.**

## Identity stamping (dev vs Store)

The manifest's `<Identity>` holds the **real Partner Center** values — `Name`
`27766TechnicallyReal.ImmichDrive`, `Publisher` `CN=C21E6CEF-D0D1-4497-93F9-3718D054DA0E`,
`PublisherDisplayName` `TechnicallyReal`.

- **Signed (sideload) builds** — `build-msix.ps1` swaps `Name` → `ImmichDrive` and `Publisher`
  → the signing cert's subject so sideload installs form a stable local package family that
  updates in place.
- **`-NoSign` (Store) builds** — keep the real identity untouched; the Store re-signs during
  ingestion.

## Notes

- Bump `<Version>` in `Directory.Build.props` per build — MSIX blocks reinstalling the same
  `<Version>` with different content.
- The thumbnail handler + cloud-files shell integration register via the manifest
  (`com:ExeServer` + `desktop3:CloudFiles`), not HKCR pokes.
- Packaged AppData is redirected per-package; the app writes a breadcrumb to
  `%LocalAppData%\ImmichDrive` so the out-of-process thumbnail extension can find
  `settings.json` + `index.db`.
- The Cloud Files sync root is registered at **runtime** (`StorageProviderSyncRootManager`),
  not by the installer.
