# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project
Spencer Denim fashion e-commerce platform (client: Spencer Denim Industries Pvt Ltd; vendor: TU2SL). Deadline **15 Oct 2026**.

Read first, in this order: `MEMORY.md` → `TASKS.md` → `docs/02-scope-baseline.md`. Read other docs in `docs/` as needed.

## ⛔ Git: personal account only
- Push **only** to `https://github.com/MASACHITH/spencer-denim-ecommerce` (private) with GitHub account **MASACHITH**.
- `destinity-sachithm` is the user's **office** account. **Never** push this project with it, and **never** run `gh auth switch --user destinity-sachithm` unless the user explicitly asks.
- Before any push: show `gh auth status` (MASACHITH must be active) and `git remote -v`. After the push, report which account and repo it went to.
- Repo-local identity only (MASACHITH / sachithishara1@gmail.com). Never change global git config.
- The pre-push hook `tools/git-hooks/pre-push` enforces this (`core.hooksPath`). Never use `--no-verify`.
- Full rules: `docs/09-git-and-repository-rules.md`.

## Non-negotiable rules
- Discuss gaps with the user before building anything outside the scope baseline.
- Every task: developer agent → separate tester agent. If a test fails, it goes back to the developer until it passes.
- SOLID + clean architecture; config via environment variables (Azure-portable); Docker.
- Responsive at 320 / 375 / 390 / 768 / 1024 / 1280 / 1440 / 1920 px; no horizontal scroll.
- Theme: Banana Republic–inspired (`docs/04-design-system.md`); never copy its assets or name it on client-facing pages.
- Payments: bank transfer, then PayPal built last, behind `IPaymentProvider`.
- `reference/` is a read-only archive; do not edit it.
- Update `MEMORY.md` and `TASKS.md` at the end of every session, and `../client-progress/progress-data.js` when a feature status changes (Done only after testing).
