Cut a release.

1. Bump `<Version>` in `Directory.Build.props` (single source of truth) — semver.
2. Update the fallback `MaxVersionTested` / any hardcoded version notes if needed.
3. Run `update-docs` to make sure docs match the code.
4. Commit: `git commit -am "Bump version to vX.Y.Z"` (no Co-Authored-By trailer).
5. Tag: `git tag -a vX.Y.Z -m "vX.Y.Z: <summary>"`.
6. Push: `git push origin main vX.Y.Z`.

The `build-msix` GitHub Action builds x64 + ARM64 MSIX packages on the tag and attaches them
to a GitHub release. MSIX blocks same-version re-installs, so never reuse a version.
