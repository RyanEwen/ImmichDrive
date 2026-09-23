Cut a release.

1. Bump `<Version>` in `Directory.Build.props` (single source of truth) — semver.
2. Update the fallback `MaxVersionTested` / any hardcoded version notes if needed.
3. Follow `.codex/commands/update-docs.md` to make sure docs match the code.
4. Commit: `git commit -am "Bump version to vX.Y.Z"` (no Co-Authored-By trailer).
5. Tag: `git tag -a vX.Y.Z -m "vX.Y.Z: <summary>"`.
6. Push: `git push origin main vX.Y.Z`.

The `build-msix` GitHub Action builds x64 + ARM64 MSIX packages on the tag and creates a GitHub
release from it. **The release carries notes and the tag only**: no MSIX is attached, and no
Actions artifact is uploaded, because this is a paid app in a public repo and an unpackaged build
could not register the sync root or the thumbnail handler regardless. The CI build is the
packaging check, not a source of downloads, so do not add assets back.

The separate `store-publish.yml` submits unsigned Store packages on the same tag using
`Tier1012` for the US $0.99 base price. Verify both workflow results and inspect the
ingested regional prices. Manual Store runs default to draft review. Keep the local
`build-msix.ps1 -NoSign` path available for manual uploads. See `.codex/docs/installer.md`.

MSIX blocks same-version re-installs, so never reuse a version.
