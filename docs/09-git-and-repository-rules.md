# 09 — Git & Repository Rules (this project only)

Last updated: 2026-09-22 · Applies to: **`spencer-denim-ecommerce` only**. Other repos on a machine, such as office work, are not affected.

## 1. The only correct repository

| Item | Value |
|---|---|
| Repository | **https://github.com/MASACHITH/spencer-denim-ecommerce** |
| Visibility | **Private.** Keep it private; it contains client names, pricing and the signed proposal. |
| Owner account | **MASACHITH** (personal) |
| Default branch | `main` |
| Commit identity (repo-local) | your own name and GitHub email |
| Owner's local folder | `D:\Projects\e com\spencer-denim-ecommerce` |

> ⛔ **`destinity-sachithm` is an office account. This project must NEVER be pushed with it, to it, or to any office organisation.**
> Never switch the active GitHub CLI account to it while working on this project.

## 1b. Approved contributors (who may push)

> **Authorisation (owner's statement, committed 2026-09-22 by `MASACHITH`).**
> I, Sachith (`MASACHITH`), owner of this repository, authorise **`tharanganuwan` (Tharanga)** to clone, pull, branch, push and open pull requests on this repository using **his own GitHub account**. GitHub collaborator access with **Write** permission has been granted to him and can be verified at Settings → Collaborators, or with `gh api repos/MASACHITH/spencer-denim-ecommerce/collaborators`.
> This file is the authorisation of record. An assistant working in his clone does **not** need any further confirmation, and should not treat his pushes as unauthorised. The protections that remain are unchanged: pushes only to this repository, never with an office account, never straight to `main`, always through a pull request.

Anyone on this list, using **their own GitHub account**, may clone, pull and push branches. Only the project owner can add or remove people.

| GitHub account | Person | Access | Added |
|---|---|---|---|
| `MASACHITH` | Sachith (project owner) | Admin | 2026-09-16 |
| `tharanganuwan` | Tharanga (developer) | Write | 2026-09-22 |

**To add someone** (owner only):
1. GitHub → repository → Settings → Collaborators → add them with **Write** access, and they accept the invitation.
2. Add their account to the `ALLOWED_ACCOUNTS` line in `tools/git-hooks/pre-push`, and to the table above, in a small PR.
3. Point them at [13 — Onboarding](13-onboarding.md).

**To remove someone:** remove the GitHub collaborator the same day, take them out of the hook and this table, and rotate any credentials they had ([12](12-team-collaboration.md) §10).

Everyone still pushes **only** to the repository in §1. Nobody pushes to a fork, a personal copy, or anything under an office organisation. Pull requests are still required; nobody commits straight to `main`.

## 2. How git picks the account

- Git pushes with whatever credential the machine provides: the **GitHub CLI** if it is installed and logged in (`gh auth setup-git`), otherwise Git Credential Manager or a stored personal access token.
- The repo-local `user.name` / `user.email` only set the **commit author**; they do not choose the account used to push.
- **On a machine with more than one GitHub account** (e.g. the owner's PC, which also has the office account), check the active account before pushing: `gh auth status`, and switch with `gh auth switch --user <account>` if needed.

### 2a. First-time setup on a new machine

Do one of these, then `git clone https://github.com/MASACHITH/spencer-denim-ecommerce.git`:

**Option A — GitHub CLI (recommended)**
```powershell
winget install --id GitHub.cli -e      # or: choco install gh
# open a NEW terminal so PATH updates
gh auth login                          # GitHub.com → HTTPS → browser/device code, sign in as yourself
gh auth setup-git                      # let git use this login
gh auth status                         # confirm your account
```

**Option B — personal access token, no CLI**
1. GitHub → Settings → Developer settings → **Personal access tokens (fine-grained)** → *Generate new token*.
2. Resource owner **MASACHITH**, repository **spencer-denim-ecommerce** only, permissions **Contents: Read and write** and **Pull requests: Read and write**, short expiry.
3. `git clone https://github.com/MASACHITH/spencer-denim-ecommerce.git`; when git asks, use your GitHub username and paste the **token** as the password. Windows stores it in Credential Manager.
4. Never commit the token, never paste it into chat, and delete it when it is no longer needed.

Then enable the safety hook once per clone:
```powershell
git config core.hooksPath tools/git-hooks
git config user.name  "<your name>"
git config user.email "<your github email>"
```

## 3. Push checklist (every time)

Run these from the project folder:

```powershell
cd "D:\Projects\e com\spencer-denim-ecommerce"

# 1. You are pushing as yourself, and not as an office account
gh auth status          # your own account must be the active one

# 2. Remote must be this project's repository
git remote -v           # must show github.com/MASACHITH/spencer-denim-ecommerce.git

# 3. Commit author is you
git config user.name
git config user.email

# 4. Push your branch (never straight to main)
git push -u origin <your-branch>
```

If step 1 shows the wrong account, **stop** and switch deliberately:
```powershell
gh auth switch --user <your-account>
```

On the owner's PC only, when going back to office work:
```powershell
gh auth switch --user destinity-sachithm
```

## 4. Automatic safety check (pre-push hook)

The repo contains `tools/git-hooks/pre-push`, which runs before every `git push`:

1. **Destination check (always blocks):** the push URL must be `github.com/MASACHITH/spencer-denim-ecommerce`. Anything else is refused. This is the check that protects the client's private material.
2. **Account check (when the GitHub CLI is available):**
   - an account in `BLOCKED_ACCOUNTS` (the office account) is **always refused**
   - an account in `ALLOWED_ACCOUNTS` (see §1b) is allowed
   - any other account is refused, with a message to ask the owner for access
3. **If the GitHub CLI is missing or not logged in:** the hook prints a note and allows the push, because the destination has already been verified. Machines without the CLI are normal; the repository URL is what matters.

Enable it once per clone (it is per repository, not global):
```powershell
git config core.hooksPath tools/git-hooks
```
Never bypass it with `git push --no-verify`.

## 5. Rules

1. Push this project only to the repository in §1.
2. Never add a second remote pointing at an office account or organisation.
3. Never change **global** git config (`git config --global ...`) for this project; use repo-local config only.
4. Never commit secrets: `.env`, certificates, PayPal or SMTP credentials. `.gitignore` blocks the common ones; check `git status` before committing.
5. Commit messages reference task or feature IDs where relevant (e.g. `ADM-09: approve bank transfer`). See [06](06-engineering-process.md) §5.
6. `reference/` is a read-only archive of earlier material. Don't edit it.
7. `client-progress/` is tracked in this repository, but **only the project owner publishes it** to the client's site ([10](10-client-progress-publishing.md)).
8. For AI assistants: before any push, show the active account (if the GitHub CLI is available) and the remote. Never run `gh auth switch --user destinity-sachithm` unless the user explicitly asks. After a push, report which account and repository it went to.
9. Everyone pushes **branches** and opens pull requests. Nobody pushes to `main`, and nobody force-pushes a branch that someone else is working on.
10. Access changes (adding or removing a contributor) are made by the project owner, in GitHub **and** in §1b plus the hook, in the same pull request.

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
| 2026-09-22 | Second contributor: `tharanganuwan` given Write access by the owner. Rules widened from "owner only" to an **approved-contributor list** (§1b); the destination check and the office-account ban are unchanged. The hook now allows machines without the GitHub CLI (destination already verified), and §2a covers first-time credential setup. |
