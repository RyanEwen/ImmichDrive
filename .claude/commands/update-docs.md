Audit the docs against the code before a release or after a significant change.

Walk each doc and confirm it still matches the code:

- `CLAUDE.md` — architecture, shared/linked files, build commands.
- `ARCHITECTURE.md` — the diagram and the cfapi/thumbnail/index flows.
- `.claude/docs/cloud-files.md` — cfapi calls, callback registration, placeholder fields.
- `.claude/docs/immich-api.md` — endpoint paths, params, auth header, response shapes.
- `.claude/docs/thumbnails.md` — COM interfaces, registration, path→assetId mapping.
- `.claude/docs/user-settings.md` — the settings list.
- `.claude/docs/versioning.md` / `installer.md` — version flow + MSIX layout.

Fix anything stale. Note in your summary which docs changed and why.
