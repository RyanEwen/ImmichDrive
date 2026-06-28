# MSIX packaging

`ImmichDriveMSIX/` packages the app **and** the thumbnail extension into one MSIX. MSIX is
required so the COM thumbnail handler and the sync-provider shell integration register
declaratively (no manual HKCR writes).

## Pieces

- `Package.appxmanifest` — one `<Application Id="App">` (the WinUI tray app,
  `Windows.FullTrustApplication`) plus the `windows.comServer` extension declaring the
  thumbnail provider DLL/CLSID, and its thumbnail-handler association. `VERSION_PLACEHOLDER` /
  `ARCH_PLACEHOLDER` are stamped by the build script. Capabilities: `internetClient` (reach the
  Immich server) + `runFullTrust`.
- `build-msix.ps1` — reads the version, publishes `ImmichDrive` (self-contained,
  `WindowsPackageType=MSIX`) and `ImmichDrive.ThumbnailProvider`, assembles the layout
  (exe + extension DLL + compiled XAML `.xbf` + stamped manifest + `Images`), runs `makepri`,
  packages with `makeappx`, signs with `signtool` (dev self-signed or a trusted PFX).
  **ASCII only.** `-NoSign` for Store uploads.
- `generate-msix-images.ps1` — generates the tile/logo/splash PNGs into `Images\`.

## Notes

- Set `<Identity>` `Name`/`Publisher` from Partner Center before Store submission.
- Packaged AppData is redirected per-package; the app writes a breadcrumb to
  `%LocalAppData%\ImmichDrive` so the out-of-process thumbnail extension can find
  `settings.json` + `index.db`.
- The Cloud Files sync root is registered at **runtime** (`StorageProviderSyncRootManager`),
  not by the installer.
