# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project
Spencer Denim fashion e-commerce platform (client: Spencer Denim Industries Pvt Ltd; vendor: TU2SL). Deadline **15 Oct 2026**.

Read first, in this order: `MEMORY.md` → `TASKS.md` → `docs/02-scope-baseline.md`. Read other docs in `docs/` as needed.

## ⛔ Git: this repository only, approved accounts only
- Push **only** to `https://github.com/MASACHITH/spencer-denim-ecommerce` (private). Never to a fork, a personal copy, or any office organisation.
- **Approved contributors** (their own accounts): `MASACHITH` (owner), `tharanganuwan`. List and how to add someone: `docs/09` §1b.
- `destinity-sachithm` is an **office** account. **Never** push this project with it, and **never** run `gh auth switch --user destinity-sachithm` unless the user explicitly asks.
- Before any push: show `gh auth status` (if the CLI is installed) and `git remote -v`. After the push, report which account and repo it went to.
- Repo-local identity only (each person's own name and GitHub email). Never change global git config.
- The pre-push hook `tools/git-hooks/pre-push` enforces this (`core.hooksPath`). Never use `--no-verify`.
- Full rules: `docs/09-git-and-repository-rules.md`.

## Client progress page (task sheet) publishing
- The Hostinger shared hosting at https://colombowardrobecompany-build-tracker.kt-wedding.online is **only** for the client progress page. Upload **only** `index.html`, `styles.css`, `app.js` and `progress-data.js` from `../client-progress/`. **Never** upload the main project.
- Publish with `pwsh tools/publish-client-progress.ps1` (FTPS, pinned certificate, never deletes remote files). Details: `docs/10-client-progress-publishing.md`.
- The FTP password lives only DPAPI-encrypted at `%USERPROFILE%\.spencer-denim\tracker-ftp-credential.xml`. Never print, log, store or commit it.
- The office network blocks FTP data connections. Use another network or hPanel File Manager (doc 10 §5b); don't retry repeatedly.

## Team: two developers working in parallel
- People: **Sachith (`MASACHITH`)** project owner, architect, client contact · **Tharanga (`tharanganuwan`)** developer. Each runs their own Claude Code session.
- **Live board = GitHub Issues.** Claim work by assigning the issue to yourself before starting. One task = one issue = one branch = one PR.
- **Never commit to `main`**; branch, PR, tester pass, green CI, squash merge (`docs/09` §5b).
- **Don't edit `TASKS.md` or `MEMORY.md` during the day** (they conflict between sessions). Write `docs/journal/YYYY-MM-DD-<name>.md` instead; the owner folds them in at the end of the day.
- **One database migration in flight at a time** across the whole team; announce it in the issue.
- Stay inside your issue's area. Shared library, design tokens, `angular.json`, CI and `Directory.Packages.props` are coordinated changes.
- Only the **project owner's session** publishes the client progress page, changes scope (`docs/02`) or the database design (`docs/08`), and talks to the client.
- Never touch, rebase or push another person's branch.
- Full process: `docs/12-team-collaboration.md`. New joiner: `docs/13-onboarding.md`. Short version: `CONTRIBUTING.md`.

## Non-negotiable rules
- Discuss gaps with the user before building anything outside the scope baseline.
- Every task: developer agent → separate tester agent. If a test fails, it goes back to the developer until it passes.
- SOLID + clean architecture; config via environment variables (Azure-portable); Docker.
- Responsive at 320 / 375 / 390 / 768 / 1024 / 1280 / 1440 / 1920 px; no horizontal scroll.
- Theme: Banana Republic–inspired (`docs/04-design-system.md`); never copy its assets or name it on client-facing pages.
- Payments: bank transfer, then PayPal built last, behind `IPaymentProvider`.
- `reference/` is a read-only archive; do not edit it.
- Update `MEMORY.md` and `TASKS.md` at the end of every session, and `../client-progress/progress-data.js` when a feature status changes (Done only after testing).
