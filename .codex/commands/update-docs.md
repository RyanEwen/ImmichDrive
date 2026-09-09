Audit the docs against the code before a release or after a significant change.

Walk each doc and confirm it still matches the code:

- `AGENTS.md` — architecture, shared/linked files, build commands.
- `ARCHITECTURE.md` — the diagram and the cfapi/thumbnail/index flows.
- `.codex/docs/cloud-files.md` — cfapi calls, callback registration, placeholder fields.
- `.codex/docs/immich-api.md` — endpoint paths, params, auth header, response shapes.
- `.codex/docs/thumbnails.md` — COM interfaces, registration, path→assetId mapping.
- `.codex/docs/user-settings.md` — the settings list.
- `.codex/docs/versioning.md` / `installer.md` — version flow + MSIX layout.

Fix anything stale. Note in your summary which docs changed and why.
