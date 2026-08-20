Cut a release.

1. Bump `<Version>` in `Directory.Build.props` (single source of truth) — semver.
2. Update the fallback `MaxVersionTested` / any hardcoded version notes if needed.
3. Run `update-docs` to make sure docs match the code.
4. Commit: `git commit -am "Bump version to vX.Y.Z"` (no Co-Authored-By trailer).
5. Tag: `git tag -a vX.Y.Z -m "vX.Y.Z: <summary>"`.
6. Push: `git push origin main vX.Y.Z`.

The `build-msix` GitHub Action builds x64 + ARM64 MSIX packages on the tag and creates a GitHub
release from it. **The release carries notes and the tag only**: no MSIX is attached, and no
Actions artifact is uploaded, because this is a paid app in a public repo and an unpackaged build
could not register the sync root or the thumbnail handler regardless. The CI build is the
packaging check, not a source of downloads, so do not add assets back.

Shipping the release is a separate, manual step: the Microsoft Store is the install route, and
its package is built locally with `.\ImmichDriveMSIX\build-msix.ps1 -NoSign` (real Partner Center
identity, unsigned, the Store re-signs at ingestion) and uploaded to Partner Center.

MSIX blocks same-version re-installs, so never reuse a version.
