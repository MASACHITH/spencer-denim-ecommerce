# Journal

One file per developer per working day: `YYYY-MM-DD-<firstname>.md` (e.g. `2026-09-22-sachith.md`).

**Why:** two people editing `MEMORY.md` and `TASKS.md` all day would conflict on every merge. A new file each day never conflicts. The project owner folds these into `MEMORY.md` and `TASKS.md` at the end of the day.

**Write yours at the end of each session.** Five lines is enough.

```markdown
# 2026-09-22 — <name>

**Done:** issue #12 product listing filters; PR #15 merged.
**In progress:** issue #13 product page, branch `feature/PDP-01-product-page` (pushed, tests failing on variant stock).
**Blocked:** waiting on the client logo for the header.
**Decisions:** used the shared `api-status` component instead of a new one; discount % rounds down.
**Next:** finish PDP-01, then the size guide.
```

Also write an entry when you:
- find something surprising (a library quirk, a data trap) that would cost the other person time
- change something other people depend on (shared library, tokens, a migration, CI)
- decide anything that the documents don't already cover
