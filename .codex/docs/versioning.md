# Versioning

- `<Version>` in `Directory.Build.props` is the **single source of truth** (currently
  `0.1.44`). App assembly version, the MSIX manifest (`VERSION_PLACEHOLDER`, stamped by
  `build-msix.ps1`), and the CI release tag all derive from it.
- The MSIX manifest needs a 4-part version, so `build-msix.ps1` reforms the 3-part `<Version>`
  as `X.Y.Z.0` (appends `.0`). The Store requires the 4th part (revision) to be `0`, so that
  slot is always `0` and never bumped manually.
- Bump `<Version>` for **every** MSIX build — MSIX refuses to reinstall the same version with
  different content.
- Release: bump → follow `.codex/commands/update-docs.md` → commit `Bump version to vX.Y.Z` (no Co-Authored-By trailer)
  → `git tag -a vX.Y.Z` → push. The `build-msix` Action builds x64 + ARM64 and creates a
  GitHub release from the tag.
- **That release carries notes and the tag only, never a package.** CI attaches no MSIX and
  uploads no Actions artifact, because this is a paid app in a public repo and because an
  unpackaged build cannot register the sync root or the thumbnail handler anyway. The MSIX is
  still built on every run as the packaging check, and shipping is a separate manual step:
  `build-msix.ps1 -NoSign` locally, then upload to Partner Center. See `installer.md`.

## In-app update check

`Services/UpdateService.cs` powers the About page's **Check for Updates** card. It's
**manual only** (no background/automatic network activity): on click it GETs
`api.github.com/repos/RyanEwen/ImmichDrive/releases/latest`, strips a leading `v` from the
tag, and compares it to the running assembly version. A 404 (no releases yet) is treated as
up-to-date. When newer, the button flips to **View Release** and opens the release page, which
now holds notes rather than a download, so it reads as "a newer version exists, get it from the
Store" rather than as a fetch.

Which channel gets asked is decided by `UpdateService.IsPackaged` (`Package.Current` throws when
unpackaged), so **any** MSIX copy, Store-installed or sideloaded, takes the `StoreContext` path;
the GitHub path only runs for unpackaged dev builds.

## Layout version (separate from `<Version>`)

`DriveManager.CurrentLayoutVersion` (currently `4`) is independent of the app `<Version>`. Bump
it **only** when the on-disk folder/file naming scheme changes; doing so forces a one-time clean
rebuild of the placeholder tree (old index metadata is carried forward first to avoid
re-enriching the whole library over the network).
