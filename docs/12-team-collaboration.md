# 12 — Team Collaboration (parallel development)

Last updated: 2026-09-22 · Applies to: everyone working on this repository, and every Claude Code session used on it.

Two developers now work on this project **at the same time**, each with their own Claude Code session. This document is the process that keeps that safe: no lost work, no duplicated work, no painful merges, and `main` always working.

## 1. The team

| Person | GitHub | Role | Owns |
|---|---|---|---|
| Sachith (project owner) | `MASACHITH` | Lead developer, architect, client contact | Repository owner, scope decisions, client communication, task sheet publishing, hosting and deployment |
| Tharanga | `tharanganuwan` | Developer | Feature work as assigned in GitHub Issues |

**Only the project owner** talks to the client, publishes the client progress page, and changes the scope baseline ([02](02-scope-baseline.md)) or the database design ([08](08-database-design.md)).

## 2. Golden rules

1. **Never commit to `main`.** Every change goes through a branch and a pull request ([09](09-git-and-repository-rules.md) §5b).
2. **One task = one issue = one branch = one pull request.**
3. **Claim work before starting**: assign the GitHub issue to yourself. If it isn't assigned to you, don't start it.
4. **Sync before you start and before you push**: `git fetch origin` then `git rebase origin/main`.
5. **Keep branches short-lived**: aim to merge within 1–2 days. Long branches cause the conflicts this process exists to prevent.
6. **Never touch someone else's branch.** No pushing to it, no rebasing it, no force-pushing it.
7. **Never `git push --force` to `main`.** On your own branch use `--force-with-lease` only.
8. **Definition of Done still applies** ([06](06-engineering-process.md) §3): developer → **tester agent** → CI green → PR → review → squash merge.
9. **Nothing outside the scope baseline gets built.** New ideas become issues labelled `needs-decision`, not code.

## 3. The task board: GitHub Issues

`TASKS.md` is the **plan** (weeks, scope, feature IDs). Day-to-day claiming happens in **GitHub Issues**, because two people editing `TASKS.md` all day would conflict constantly.

| Where | What it is | Who edits |
|---|---|---|
| GitHub Issues | The live board: who is doing what, right now | Everyone, all the time |
| `TASKS.md` | The weekly plan and status summary | Project owner, at the end of the day |
| `docs/journal/` | One file per developer per day: what happened, decisions, gotchas | Each developer, own file only |

**Labels:** `area:backend`, `area:frontend`, `area:admin`, `area:docs`, `area:infra`, plus `priority:high|normal`, `blocked`, `needs-decision`, `bug`.

**Starting work:**
```powershell
gh issue list --assignee "@me"          # what is mine
gh issue list --search "no:assignee"    # what is free
gh issue edit <n> --add-assignee "@me"  # claim it
```

## 4. Areas of ownership (avoid two people in the same files)

Ownership is not a wall, it's the default. If you need to change a file in someone else's area, say so in the issue or PR first.

| Area | Typical paths | Default owner |
|---|---|---|
| Backend domain, API, database, migrations | `src/backend/**`, `tests/**` | See issue assignment |
| Storefront (customer website) | `src/frontend/projects/storefront/**` | See issue assignment |
| Admin panel | `src/frontend/projects/admin/**` | See issue assignment |
| Shared UI library and design tokens | `src/frontend/projects/shared/**` | Coordinate: both apps depend on it |
| Infrastructure, CI, scripts | `.github/**`, `tools/**` | Project owner |
| Scope, database design, client docs | `docs/01`, `docs/02`, `docs/08`, `reference/**` | Project owner |
| Client progress page | `../client-progress/**` | Project owner only |

**Rule of thumb:** two people should not be in the same Angular app, or the same backend module, on the same day. Split by feature, not by layer.

## 5. Files that cause conflicts, and how to handle them

| File | Risk | Rule |
|---|---|---|
| `TASKS.md`, `MEMORY.md` | High: everyone edits the same lines | Don't edit during the day. Write to your own `docs/journal/<date>-<name>.md`. The owner folds them into `TASKS.md`/`MEMORY.md` at the end of the day. |
| EF Core migrations (`src/backend/**/Migrations/**`) | High: two migrations on the same model break each other | **Only one migration in flight at a time.** Say in the issue that you are creating one. If someone else's migration merges first: rebase, delete yours, regenerate it. Never edit a migration that is already on `main`. |
| `src/frontend/package.json`, `package-lock.json` | Medium: lockfile conflicts | Add dependencies only when the issue needs it; mention it in the PR. On conflict: take `main`'s lockfile, re-run `npm install`, commit the result. |
| `Directory.Packages.props` | Medium | Same: announce version changes in the PR. |
| `src/frontend/angular.json`, shared tokens/CSS | Medium: both apps depend on them | Small, separate PR; tell the other developer. |
| `docs/08-database-design.md` | High: baselined | Project owner only; changes need an architect review round. |

## 6. Daily rhythm

1. **Start:** `git fetch origin`, `git switch main`, `git pull --ff-only`, then branch from `main`. Read your issue and `docs/journal/` from yesterday.
2. **During:** small commits, Conventional Commits, push your branch early so the other person can see it exists.
3. **Before pushing:** `git fetch origin && git rebase origin/main`, run the tests, then push.
4. **PR:** open it as soon as the work is testable, even as a draft. Small PRs get reviewed fast.
5. **End of day:** write `docs/journal/YYYY-MM-DD-<yourname>.md` (5 lines is fine: done, in progress, blocked, decisions, next). Push everything, even unfinished work, so nothing lives only on your laptop.

## 7. Pull requests and review

- **Author:** fill in the PR template, link the issue (`Closes #12`), state what the tester agent checked, and make sure CI is green.
- **Reviewer:** the other developer reviews within **4 working hours**. Check: does it match the scope baseline, does it follow the layering and standards ([03](03-architecture-and-tech-stack.md), [06](06-engineering-process.md)), are there tests, any secrets, any change outside the stated area?
- **If the reviewer is unavailable for more than 4 hours** and CI is green and the change is inside your own area and is not database design, scope, security or payments: you may self-merge with a comment saying why, and the reviewer reviews it afterwards. The deadline matters more than ceremony, but **never** self-merge changes to payments, authentication, permissions, migrations or the database design.
- **Merge:** squash merge, delete the branch, then `git switch main && git pull`.

## 8. Claude Code sessions (one each, working in parallel)

Each developer runs their own session, in their own clone or worktree. Sessions do not share memory, so the repository must carry the context.

**Every session must:**
1. Read `CLAUDE.md`, then `MEMORY.md`, then `TASKS.md`, then the issue it is working on, before making changes.
2. Work only on the branch for its own assigned issue.
3. Follow the developer → tester agent loop, and never merge on a red pipeline.
4. Verify the GitHub account before pushing (see [09](09-git-and-repository-rules.md)), and never switch to the office account.
5. Write the end-of-session journal entry in `docs/journal/`, its own file.
6. Never edit `TASKS.md` or `MEMORY.md` mid-day; that is the owner's end-of-day job.
7. Never publish the client progress page unless it is the project owner's session.

**Two sessions must never** work in the same folder at the same time. Use separate clones, or `git worktree add` for a second branch ([09](09-git-and-repository-rules.md) §5b).

## 9. Communication

| Need | Where |
|---|---|
| "I'm taking this" | Assign the GitHub issue |
| Question about code | PR comment on the line |
| Blocked, or need a decision | Issue with the `blocked` or `needs-decision` label, then message the owner |
| Something urgent | WhatsApp, then write it into the issue so it isn't lost |
| Daily check-in | 10 minutes each morning: what's done, what's next, anything blocking |

## 10. Security rules for the team

- The repository is **private** and stays private: it holds client contracts and pricing.
- Never commit secrets. Connection strings with credentials go in user-secrets or environment variables.
- The client progress page FTP password stays on the owner's machine only ([10](10-client-progress-publishing.md)).
- Production and hosting credentials are the owner's; developers never need them.
- If someone leaves the project, remove their GitHub access the same day and rotate any shared credentials.

## 11. Onboarding a new developer

See [13 — Onboarding](13-onboarding.md): environment setup, first build, first pull request.
