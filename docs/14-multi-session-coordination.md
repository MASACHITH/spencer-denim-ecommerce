# 14 — Multi-Session Coordination (machines and handoff)

Last updated: 2026-09-22

Several Claude Code sessions work on this project, on **different machines and for different people**. This document covers **machine setup and handoff between sessions**.

- **Who does what, claiming work, branches, reviews:** [12 — Team collaboration](12-team-collaboration.md)
- **Accounts, push safety, approved contributors:** [09 — Git rules](09-git-and-repository-rules.md)
- **A new person's first day:** [13 — Onboarding](13-onboarding.md)

## The rule in one line

Before you read anything: **pull**. Before you stop: **push**. Claim work on the **GitHub issue**, not by editing shared files.

## Start of every session

1. `git fetch origin && git switch main && git pull --ff-only`. A stale copy defeats the point.
2. Read `CLAUDE.md` → `MEMORY.md` → `TASKS.md` → your issue → `docs/02-scope-baseline.md`.
3. Run the **machine readiness check** below if this machine hasn't been confirmed today.
4. **Claim the work in GitHub Issues** (`gh issue edit <n> --add-assignee "@me"`), then branch from `main`. If no issue exists for it, create one first. This is the claim that stops two sessions doing the same task; `TASKS.md` is only the owner's end-of-day summary ([12](12-team-collaboration.md) §3).
5. Check what else is in flight before you start: `gh issue list --assignee "*"`, `git branch -r`, and the latest files in `docs/journal/`.

## Machine readiness check

| Check | Command | Required state |
|---|---|---|
| Repo-local git identity | `git config user.name` / `user.email` | **Your own** name and GitHub email, set repo-locally, so history shows who did what. Never the machine's unrelated global identity, and never someone else's. |
| Push-safety hook active | `git config core.hooksPath` | `tools/git-hooks` |
| GitHub account | `gh auth status` | Your own account, and it must be on the approved list in [09](09-git-and-repository-rules.md) §1b. Without the CLI the hook still verifies the destination repository and allows the push; install it when you can, so the account is checked too. No CLI at all? See [09](09-git-and-repository-rules.md) §2a for the token route. |
| Machine-specific local state | see **Known machines** | Client-progress publishing credentials, database instances and user-secrets do not travel between machines |

Fix identity and hook, **repo-local only, never global**:
```powershell
git config core.hooksPath tools/git-hooks
git config user.name  "<your name>"
git config user.email "<your github email>"
```

## Known machines

| Machine | Person | First seen | Database | `gh` CLI | Notes |
|---|---|---|---|---|---|
| `SCIENTER\sachithi` (domain-joined, scienter.lk) | Sachith | 2026-09-15 | LocalDB `(localdb)\MSSQLLocalDB`, `SpencerDenim_Dev` | Installed; `MASACHITH` and the office account both logged in, so **check the active account before pushing** | Holds `client-progress` publishing credentials (DPAPI, `%USERPROFILE%\.spencer-denim\`). The client progress page can only be published from here, or via hPanel File Manager. Docker Desktop installed but paused (W0-05/W0-15). |
| Machine with global identity `nuwank`/scienter.lk | Tharanga | 2026-09-21 | SQL Server 2022 at `127.0.0.1,11433` (sa), `SpencerDenim_Dev` provisioned with `tools/db/provision-database.sql`; `TEST_SQL_CONNECTION` set for this Windows user | Not installed yet | Repo-local identity and push hook were missing on first use, then set. **Identity was set to the owner's name and email, so commits from 2026-09-21/22 show as `MASACHITH` — set your own name and email here** ([09](09-git-and-repository-rules.md) §1b). No `client-progress` credentials. Credentials live only in user-secrets and Windows environment variables, never in a repo file. |

Add a row the first time a session runs on a new machine.

## Scratch scripts and ports are shared too

The temporary directory and the machine's ports are shared by every session on that machine, and
neither is namespaced. This has now bitten two sessions on one task (W2-01, 2026-09-24):

- A developer wrote `scratchpad/proxy.mjs`, verified the contents, and later read the same path back
  to find **a different script** — another session's, with different ports and behaviour.
- A tester started a proxy on a port another session's `node` process already held. It bound, the
  measurements looked plausible, and the **first batch of results came from someone else's
  process** before he noticed and redid them.

Neither failure announced itself. A script at a familiar path runs; a port that is already in use
usually still answers something. So:

1. **Work in a private, uniquely named directory** under the scratch area — `w201-<task>/`,
   `qa-<task>/` — never the shared root.
2. **Write every script fresh** at the start of the run, with a name nobody else would choose
   (`w201-proxy.mjs`, not `proxy.mjs`). Never reuse one you find already there.
3. **Check it is yours immediately before running it:** `sha256sum <script>`, compared with the
   hash of what you just wrote. Do this even when you wrote it a minute ago — a minute is long
   enough.
4. **Pick per-session ports**, not the conventional ones. 4500/4501 and 8080 are the ports everyone
   reaches for; take a block nobody else is using and verify it is free first
   (`netstat -ano | grep ":<port> "`), then verify the thing answering is yours (a banner line, a
   known response body).
5. **Never run a script you did not write this session**, whatever its name suggests. Read it first
   or delete it.

The same applies to databases: the integration fixture already generates a per-run name
(`SpencerDenim_Test_<guid>`), and a hand-made verification database should too.

## Ending every session

1. Write your own journal file: `docs/journal/YYYY-MM-DD-<yourname>.md` (done, in progress, blocked, decisions, next). Never edit someone else's.
2. Push your branch, even if the work is unfinished, so nothing lives only on your laptop.
3. Comment on your GitHub issue with where you got to, so the other session can see it without reading code.
4. **`MEMORY.md` and `TASKS.md` are updated by the project owner at the end of the day**, from the journals. Don't edit them mid-day: two sessions editing the same lines is exactly what caused the merge conflict committed to `main` on 2026-09-22 ([12](12-team-collaboration.md) §5).

> **Clarified 2026-10-02, because `CLAUDE.md` and this rule appeared to contradict each other.** `CLAUDE.md` says to update `MEMORY.md` and `TASKS.md` "at the end of every session", and a session reading both cannot satisfy them at once. **This rule wins, and here is how the two fit together:** the *orchestrating* session — the one the owner is talking to — does both updates **once, at the end, on a branch of its own**. A session or agent working a feature branch writes **only** its journal file and does not touch either shared file, whatever `CLAUDE.md` says. A feature branch that edits them is doing it wrong even when it is the only branch open at the time, because it will not be the only one by the time it merges.
>
> This is not theoretical. On 2026-10-02 the W1-12 branch carried three commits recording its progress in both files. When `main` moved underneath it the pull request conflicted — and **GitHub does not run CI on a conflicting pull request**, so the branch sat looking as though it were waiting on a slow build while nothing was running at all. Three of its four conflicted hunks were in `MEMORY.md` and `TASKS.md` and existed for no reason other than this rule being ignored; only the fourth, in `playwright.config.ts`, was a real one. A branch that had written just its journal would have had one conflict instead of four.
>
> The same day another session asked which rule won rather than guessing, and was right to: it wrote `docs/journal/2026-10-02-w207-search.md` and left both shared files alone. That is the pattern to copy.

## If two sessions collide anyway

- **Merge conflict:** resolve it in the branch, never commit `<<<<<<<` markers. If markers reach `main`, fix them immediately in a `fix/` branch.
- **Same task started twice:** whoever claimed the issue first keeps it; the other stops and comments on the issue.
- **Two database migrations at once:** only one may be in flight ([12](12-team-collaboration.md) §5). The later one rebases onto `main`, deletes its migration and regenerates it.
- **Something on `main` is broken:** say so in the issue and fix it first. A broken `main` blocks everyone.
