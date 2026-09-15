# 09 — Git & Repository Rules (this project only)

Last updated: 2026-09-16 · Applies to: **`spencer-denim-ecommerce` only**. Other repos on this machine, such as office work, are not affected.

## 1. The only correct repository

| Item | Value |
|---|---|
| Repository | **https://github.com/MASACHITH/spencer-denim-ecommerce** |
| Visibility | **Private.** Keep it private; it contains client names, pricing and the signed proposal. |
| GitHub account | **MASACHITH** (personal) |
| Default branch | `main` |
| Commit identity (repo-local) | `MASACHITH` / `sachithishara1@gmail.com` |
| Local folder | `D:\Projects\e com\spencer-denim-ecommerce` |

> ⛔ **`destinity-sachithm` is the office account. This project must NEVER be pushed with it, to it, or to any office organisation.**
> Do not switch the active GitHub CLI account to it while working on this project. The user decides when to switch back for office work.

## 2. Why accounts matter on this machine

- This PC is logged in to **two** GitHub CLI accounts: `MASACHITH` (personal) and `destinity-sachithm` (office).
- Git uses the GitHub CLI as its credential helper, so **git pushes with whichever account is active in `gh`**. Putting a username in the remote URL does not change this.
- The repo-local `user.name` / `user.email` only set the commit author. They do **not** choose the account used to push.

## 3. Push checklist (every time)

Run these from the project folder:

```powershell
cd "D:\Projects\e com\spencer-denim-ecommerce"

# 1. Active account must be MASACHITH
gh auth status          # look for: account MASACHITH ... Active account: true

# 2. Remote must be the personal repo
git remote -v           # must show github.com/MASACHITH/spencer-denim-ecommerce.git

# 3. Commit author must be the personal identity
git config user.name    # MASACHITH
git config user.email   # sachithishara1@gmail.com

# 4. Push
git push
```

If step 1 shows another account, **stop** and switch deliberately:
```powershell
gh auth switch --user MASACHITH
```

When you go back to office work, switch yourself:
```powershell
gh auth switch --user destinity-sachithm
```

## 4. Automatic safety check (pre-push hook)

The repo contains `tools/git-hooks/pre-push`, which runs before every `git push` and **blocks the push** unless:
1. the push URL is `github.com/MASACHITH/spencer-denim-ecommerce`, and
2. the active GitHub CLI account is `MASACHITH`.

It is enabled per repository (not globally) with:
```powershell
git config core.hooksPath tools/git-hooks
```
This is already set on this PC. On a fresh clone, run the command once. Never bypass it with `git push --no-verify`.

## 5. Rules

1. Push this project only to the repository in §1.
2. Never add a second remote pointing at an office account or organisation.
3. Never change **global** git config (`git config --global ...`) for this project; use repo-local config only.
4. Never commit secrets: `.env`, certificates, PayPal or SMTP credentials. `.gitignore` blocks the common ones; check `git status` before committing.
5. Commit messages reference task or feature IDs where relevant (e.g. `ADM-09: approve bank transfer`). See [06](06-engineering-process.md) §5.
6. `reference/` is a read-only archive of earlier material. Don't edit it.
7. `client-progress/` (outside this repo) is managed and uploaded separately.
8. For AI assistants: before any push, show the active account and remote. Never run `gh auth switch --user destinity-sachithm` unless the user explicitly asks. After a push, report which account and repository it went to.

## 6. If something was pushed to the wrong place

1. Stop. Don't push again.
2. Tell the project owner immediately.
3. On the wrong repository: delete the pushed branch, or the repo if it was newly created, and check that no client documents remain.
4. Check whether any secrets were included. If so, rotate them (PayPal, SMTP, SQL).
5. Log what happened in `MEMORY.md`.

## 7. History

| Date | Event |
|---|---|
| 2026-09-16 | Repo created on MASACHITH (private). Initial commits: planning docs, style tile, database design, `.gitattributes`, `reference/` archive. All pushed with MASACHITH. |
| 2026-09-16 | This document and the pre-push hook added. |
