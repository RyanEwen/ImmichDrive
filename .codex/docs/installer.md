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
`PublisherDisplayName` `Dynamic Solutions Canada`.

The Store listing is named **Drive for Immich**, while manifest `DisplayName` values identify
the installed package, app, thumbnail server, and startup task as **ImmichDrive**. Keep that
visible-name distinction separate from `<Identity>`: changing the identity would create a
different package family and break in-place updates for existing Store installs.

- **Signed (sideload) builds** — `build-msix.ps1` swaps `Name` → `ImmichDrive` and `Publisher`
  → the signing cert's subject so sideload installs form a stable local package family that
  updates in place.
- **`-NoSign` (Store) builds** — keep the real identity untouched; the Store re-signs during
  ingestion.

## Distribution (what gets published, and what deliberately does not)

`store-publish.yml` submits to product `9MWC6165N7DH`.

The workflow pins [Microsoft Store CLI v0.4.3](https://github.com/microsoft/msstore-cli/releases/tag/v0.4.3),
builds unsigned x64 and ARM64 packages on one runner, bundles them into a `.msixupload`,
and submits directly to Partner Center on `v*` tags. It uploads no public binary artifacts.
Manual dispatch defaults `no_commit` to true for draft review; disable it to commit the submission.
Certification and the submission's publishing settings determine when it becomes available.

The published base price is US $0.99. The API may report it as `PriceId: "Base"`, which
the CLI cannot round-trip. This workflow explicitly supplies `Tier1012`, the US $0.99
tier identified by a [Microsoft maintainer](https://github.com/microsoft/msstore-cli/pull/175#issuecomment-5791491206).
Tier pricing can change converted prices in other markets; review the ingested submission
in Partner Center. The workflow checks for a pending submission before invoking the CLI,
because the CLI would otherwise delete an existing draft.

The repository secrets are `AZURE_AD_TENANT_ID`, `AZURE_AD_APPLICATION_CLIENT_ID`,
`AZURE_AD_APPLICATION_SECRET`, and `SELLER_ID`. All four were present when checked on
September 22, 2026; expiry and the Entra application's Partner Center Manager role still
need a live submission check. Inspect the first tier-based submission's packages and pricing.

For manual fallback, run `build-msix.ps1 -Platform x64 -NoSign` and
`-Platform ARM64 -NoSign`, then upload both individual `.msix` files in Partner Center.

`.github/workflows/build-msix.yml` publishes **no binary at all**: no release asset, no Actions
artifact. Two reasons, and both have to stop being true before that changes:

- ImmichDrive is a **paid app in a public repo**. An Actions artifact needs only read access to
  download, which on a public repo is everyone.
- There is **no usable unpackaged build to hand out anyway**. The sync root and the thumbnail COM
  server are registered by `Package.appxmanifest` alone (`com:ExeServer` + `desktop3:CloudFiles`),
  so a build without package identity is not the product. See `cloud-files.md`, where sync-root
  registration is documented as needing package identity.

So the install route is the Microsoft Store (`9MWC6165N7DH`), and anyone wanting to run it from
source builds the MSIX themselves.

**The workflow still builds the MSIX on every run, and that is not pointless.** It is the only
automated check that version/arch stamping, `makepri`, `makeappx` and `signtool` still work
together; drop it and the next packaging break is discovered in a Store submission.

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
