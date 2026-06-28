# Versioning

- `<Version>` in `Directory.Build.props` is the **single source of truth**. App assembly
  version, the MSIX manifest (`VERSION_PLACEHOLDER`, stamped by `build-msix.ps1`), and the CI
  release tag all derive from it.
- Bump it for **every** MSIX build — MSIX refuses to reinstall the same version with different
  content.
- Release: bump → `update-docs` → commit `Bump version to vX.Y.Z` (no Co-Authored-By trailer)
  → `git tag -a vX.Y.Z` → push. The `build-msix` Action builds x64 + ARM64 and creates a
  GitHub release from the tag.
