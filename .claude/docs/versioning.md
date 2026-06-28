# Versioning

- `<Version>` in `Directory.Build.props` is the **single source of truth** (currently
  `0.1.32`). App assembly version, the MSIX manifest (`VERSION_PLACEHOLDER`, stamped by
  `build-msix.ps1`), and the CI release tag all derive from it.
- The MSIX manifest needs a 4-part version, so `build-msix.ps1` reforms the 3-part `<Version>`
  as `X.Y.Z.0` (appends `.0`). The Store requires the 4th part (revision) to be `0`, so that
  slot is always `0` and never bumped manually.
- Bump `<Version>` for **every** MSIX build — MSIX refuses to reinstall the same version with
  different content.
- Release: bump → `update-docs` → commit `Bump version to vX.Y.Z` (no Co-Authored-By trailer)
  → `git tag -a vX.Y.Z` → push. The `build-msix` Action builds x64 + ARM64 and creates a
  GitHub release from the tag.

## Layout version (separate from `<Version>`)

`DriveManager.CurrentLayoutVersion` (currently `4`) is independent of the app `<Version>`. Bump
it **only** when the on-disk folder/file naming scheme changes; doing so forces a one-time clean
rebuild of the placeholder tree (old index metadata is carried forward first to avoid
re-enriching the whole library over the network).
