# 11 — Multi-Session Coordination

This project is worked on by multiple Claude Code sessions over time, sometimes on different
physical machines, all committing under the single shared git identity **MASACHITH**
(see [docs/09](09-git-and-repository-rules.md) for the account rules — this doc is about
*handoff between sessions*, not about git identity/push safety itself).

## The rule in one line

`MEMORY.md` → "Current state" and `TASKS.md` → status columns are the single source of truth.
**Pull before you read them. Commit and push them before you stop.**

## Start of every session

1. `git pull` (or at least compare `git log -1` to origin) before reading anything — a stale
   local copy defeats the whole point of this doc.
2. Read `MEMORY.md` → `TASKS.md` → `docs/02-scope-baseline.md`, per the root `CLAUDE.md`.
3. Run the **machine readiness check** below if this machine hasn't been confirmed today.
4. Before starting a `TODO` task, set it to `IN PROGRESS` in `TASKS.md` with a short
   `(started <date>)` note, and say so in this session's `MEMORY.md` entry. That's the claim —
   it stops a later session from re-starting the same task from scratch.

## Machine readiness check

Run this on any machine before the first push of a session, or if anything below feels off:

| Check | Command | Required state |
|---|---|---|
| Repo-local git identity | `git config user.name` / `user.email` | `MASACHITH` / `sachithishara1@gmail.com` — **not** the machine's own global identity |
| Push-safety hook active | `git config core.hooksPath` | `tools/git-hooks` |
| GitHub CLI, correct account | `gh auth status` | Installed, MASACHITH active. **If `gh` isn't installed, don't push from this machine** until it is and MASACHITH is confirmed — the pre-push hook's account check depends on it. |
| Machine-specific local state | see **Known machines** below | e.g. the client-progress source folder, the DPAPI FTP credential — neither travels between machines |

Fix identity/hook if wrong — **repo-local only, never touch global config**:
```powershell
git config core.hooksPath tools/git-hooks
git config user.name "MASACHITH"
git config user.email "sachithishara1@gmail.com"
```

## Known machines

| Machine | First seen | Docker/WSL | `gh` CLI | Notes |
|---|---|---|---|---|
| `SCIENTER\sachithi` (domain-joined, scienter.lk) | 2026-09-15 | Docker Desktop 4.91 installed per-user; WSL 2 kernel missing, `wsl --update` still pending | Present, MASACHITH confirmed | Has `D:\Projects\e com\client-progress` (source for `tools/publish-client-progress.ps1`) and the DPAPI-encrypted FTP credential at `%USERPROFILE%\.spencer-denim\`. Both are per-user/per-machine and will **not** exist elsewhere — the client progress page can only be published from here (or via hPanel File Manager) until that's set up on another machine. |
| This machine (global git identity `nuwank`/scienter.lk) | 2026-09-21 | Docker/WSL not checked. LocalDB 150 and 170, `sqlcmd`, and `dotnet-ef` 10.0.12 are present | Not installed | Repo-local identity and the push hook were **both missing** on first use here — fixed 2026-09-21 (see MEMORY.md session log). No `client-progress` source folder, no FTP credential. **Do not push from here until `gh` is installed and MASACHITH is confirmed active.** Dev DB: `ConnectionStrings:Database` is set via `dotnet user-secrets` (in `src/backend/SpencerDenim.Api`) to a SQL Server 2022 instance at `127.0.0.1,11433` (sa auth) instead of LocalDB — the `SpencerDenim_Dev` database was provisioned there with `tools/db/provision-database.sql` (collation `Latin1_General_100_CI_AS_SC`, confirmed). `TEST_SQL_CONNECTION` is also set (persisted for this Windows user account) pointing at the same server, so integration tests run for real here instead of skipping. Credentials live only in user secrets and the user's Windows environment variables — never in a repo file. Neither is a global setting; a session on another machine must set these itself. |

Add a row here the first time a session runs on a machine not yet listed.

## Ending every session

1. Update `MEMORY.md`: the "Current state" block (phase, next step, blockers) and a new dated
   entry at the top of the session log — what was done, decided, found, and what's left.
2. Update `TASKS.md` statuses for anything that changed, including partial progress. Don't leave
   a task at `TODO` when it's actually underway, or mark it `DONE` when only part of it landed.
3. Commit and push `MEMORY.md`/`TASKS.md` (and any finished code) from a machine that passed the
   readiness check. If this machine can't push yet (e.g. no `gh`), say so plainly in the
   `MEMORY.md` entry and leave the commit local rather than pushing unverified — the next session
   that can push picks it up.
4. Don't let `MEMORY.md`/`TASKS.md` drift from what's actually in the repo — that drift is what
   breaks handoff. (It happened once: W0-09's scaffold was committed and CI went green on
   2026-09-17, but `TASKS.md` still read `TODO` four days later. Fixed 2026-09-21.)

## Out of scope here

Giving a second **human** their own GitHub push access to this repo is a different, bigger
decision — branch protection, PR review, who's allowed to push — and isn't covered by this doc.
Ask the user before setting that up; see the "⛔ Git" section in the root `CLAUDE.md`.
