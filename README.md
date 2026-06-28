<p align="center">
  <img src="ImmichDrive/Resources/ImmichDrive.png" alt="ImmichDrive" width="128">
</p>

<h1 align="center">ImmichDrive</h1>

<p align="center">
  Your <a href="https://immich.app">Immich</a> photo library as a native cloud drive in File Explorer — on demand, without storing the photos on your PC.
</p>

<p align="center">
  <img src="docs/explorer-thumbnails.png" alt="Real thumbnails on demand inside a month folder" width="760">
</p>

<p align="center">
  <img src="docs/explorer-library.png" alt="The library in File Explorer — by date, plus Albums, Favorites, Partners and Upload" width="760">
</p>

<p align="center">
  <img src="docs/home.png" alt="ImmichDrive — connected and online" width="380">
  &nbsp;&nbsp;
  <img src="docs/tray-flyout.png" alt="System-tray flyout" width="380">
</p>

Take a photo on your phone, let it auto-sync to Immich, and then grab it straight from your
computer's file picker — no need to open the Immich web UI, download anything by hand, or
fill up your disk. When you actually open or attach a photo, ImmichDrive fetches just that
file on demand and hands it to whatever app asked for it (great for attaching a recent photo
to a Craigslist listing, an email, or a form).

## How it works

ImmichDrive uses the same Windows mechanism as OneDrive and Dropbox — the **Cloud Files API**
— to create *placeholder* files. They look and behave like normal files in Explorer (correct
names, dates, and **thumbnails**) but take up **0 bytes** until you open one. Opening a file
("hydrating" it) streams the original down from your Immich server in the background; closing
and freeing space dehydrates it back to a placeholder.

- **Organized the way you think about it** — `2026-06 June` date folders (newest first), plus
  **Albums**, **Favorites**, and **Partners** folders that mirror Immich and stay in sync.
- **Real thumbnails without downloading** — a lightweight shell extension fetches Immich's
  small thumbnails so you can *see* your photos before opening them, with nothing on disk.
- **Lives in the tray** — a single tray icon shows status (online / syncing) and lets you
  open settings, refresh, or pause. No heavyweight background service.

## Setup (minimal)

1. Install the signed MSIX (or from the Store).
2. Open **ImmichDrive** and enter:
   - your **Immich server URL** (e.g. `https://photos.example.com`)
   - an **API key** (Immich → *Account Settings → API Keys → New API Key*).
3. Click **Test connection**, then **Connect**. Your drive appears in Explorer under
   *ImmichDrive* (a sync-root entry in the navigation pane, like OneDrive).

That's it — browse by date and double-click (or attach) any photo.

## Building

**Settings app (dev):**
```powershell
dotnet build ImmichDrive/ImmichDrive.csproj -c Debug
```

**MSIX package (installable / Store):**
```powershell
powershell -File ImmichDriveMSIX/generate-msix-images.ps1   # one-time: visual assets
powershell -File ImmichDriveMSIX/build-msix.ps1             # sideload (dev-signed)
powershell -File ImmichDriveMSIX/build-msix.ps1 -NoSign     # Store upload
```

The script publishes the app **and** the thumbnail shell extension into the package. Bump
`<Version>` in `Directory.Build.props` for each build (MSIX blocks same-version re-installs).
Before Store submission, set `<Identity>` `Name`/`Publisher` in
`ImmichDriveMSIX/Package.appxmanifest` to your Partner Center values.

## Requirements

- Windows 11 (build 22621+) — the Cloud Files API and modern shell thumbnail handling.
- A reachable Immich server (any reasonably recent version) and an API key.
- .NET 10 SDK; Windows SDK packaging tools to build the MSIX.

## Status

This is an early build. The Immich client, settings UI, tray host, date-organized
placeholder population, on-demand hydration, and the thumbnail extension are implemented;
the Cloud Files provider and the COM thumbnail registration are best validated on a real
Windows 11 machine with a live Immich server (see `ARCHITECTURE.md` and `.claude/docs/`).

## License

Licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE.md): free for any
**personal and other noncommercial use**, including modifying and redistributing it.
**Commercial use is not permitted.** Copyright © 2026 Ryan Ewen.
