Stage and commit the current changes.

1. Run `git status` and `git diff` to review what changed.
2. Check the **Documentation maintenance** table in `CLAUDE.md` and the topic docs under
   `.claude/docs/`: if the change touches an area a doc covers (cfapi, Immich API, thumbnails,
   settings, packaging, versioning), update that doc in the same commit.
3. Stage the relevant files and commit with a clear subject + body describing *why*.
4. Do **not** add a `Co-Authored-By` trailer (per the user's global git preference).
5. Do not push unless asked.
