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
  (`ImmichDrive/Resources/ImmichDrive.ico`), its muted tray variant
  (`ImmichDrive-Offline.ico` — the same mark run through a luminance `ColorMatrix` with an amber
  pip, shown while the server is unreachable), and a 256px in-app PNG
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

## Store update checks (`Services/UpdateService.cs`)

Packaged copies check the Store instead of GitHub Releases. One trap governs that path:

**`StorePackageUpdate.Package` describes the package *as installed*, so `Package.Id.Version` is
the version already on the machine — never the version being offered.** There is no WinRT API
that reports a pending update's version.

Measured on the sibling Little Launcher package with a live update pending (installed 1.27.1.0,
published 1.28.0.0): `GetAppAndOptionalStorePackageUpdatesAsync` returned exactly one entry — the
app's own family — reporting **1.27.1.0**.

So: **presence of the app's own family in that list is the update signal**, and requiring the
listed version to be strictly newer (an earlier attempt to stop the UI offering an update to the
running version) can never match — it reports "up to date" forever, and does so *silently*
because the check succeeds. `TryGetPublishedVersionAsync` supplies the number instead, from the
Store's public display-catalog endpoint, and a published version that is not newer is what
suppresses a stale offer. Every check is now logged so a repeat is visible rather than invisible.

Verify either half without a Store submission:

```bash
curl -s "https://displaycatalog.mp.microsoft.com/v7.0/products/9MWC6165N7DH?market=US&languages=en-us&fieldsTemplate=Details"
```

```powershell
Invoke-CommandInDesktopPackage -PackageFamilyName '27766TechnicallyReal.ImmichDrive_gfb69tsnc4jnp' -AppId 'App' -Command 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe' -Args '-NoProfile -ExecutionPolicy Bypass -File <script>'
```

The human-readable version is inside each `PackageFullName` (`…_0.1.40.0_arm64__hash`), not the
numeric `Version` field beside it (a packed 64-bit value). `StoreContext` and `Package.Current`
need package identity, which is what `Invoke-CommandInDesktopPackage` supplies; use Windows
PowerShell 5.1, not `pwsh`, and have the script write output outside the package's redirected
AppData.

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
