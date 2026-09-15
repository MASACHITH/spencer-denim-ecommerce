# MEMORY — Project Log

Running record of what was done, what was decided, and what's next. **Newest entry on top.** Read this first every session.

---

## Current state (keep this section up to date)

- **Phase:** Week 0 — Setup. Scope baselined v1.0. **No application code yet.**
- **Deadline:** Thu **15 Oct 2026** (one month from 15 Sep).
- **Scope:** [docs/02-scope-baseline.md](docs/02-scope-baseline.md). Every demo feature is kept, only the theme changes. Nav: Women · Men · Kids · New Arrivals · Sale. Admin can manage everything, with per-user role permissions.
- **Theme:** Banana Republic–inspired, full replacement → [docs/04](docs/04-design-system.md)
- **Stack (approved):** .NET 10 API + Angular 21 (SSR storefront + admin app) + SQL Server Express, Docker, Caddy, GitHub Actions. Hostinger KVM 2, Azure-portable.
- **Payments:** Bank transfer in week 3. **PayPal last** (week 4). A second gateway comes later (TBD) through `IPaymentProvider`.
- **Repo:** https://github.com/MASACHITH/spencer-denim-ecommerce (user's **personal** account MASACHITH, branch `main`). Local git identity is set per repo (MASACHITH / sachithishara1@gmail.com). The machine's active `gh` account is the work account `destinity-sachithm`. **Never push this project with it.** For every push: `gh auth switch --user MASACHITH` → push → `gh auth switch --user destinity-sachithm`.
- **Blockers:** Docker Desktop not installed on dev machine (W0-05).
- **Next:** W0-06 client progress page → W0-07 style tile → W0-08/09 repo + scaffold → W0-10 schema.
- **Open assumptions:** A1 (Trending Categories / Best Sellers homepage blocks stay removed per signed proposal), A2 (video MP4 ≤ 50 MB, no server conversion).

---

## Key decisions log

| Date | Decision | Why | By |
|---|---|---|---|
| 2026-09-15 | Timeline one month: 15 Sep → 15 Oct 2026 (internal = client tracker) | Q1 | User |
| 2026-09-15 | Banana Republic theme, full replacement of Denim × Linen | Q2 | User |
| 2026-09-15 | PayPal implemented last; another gateway later (TBD); bank transfer kept | Q3 | User |
| 2026-09-15 | Keep **all** demo home/page features; only theme changes | Q4 | User |
| 2026-09-15 | Nav: Women · Men · Kids · New Arrivals · Sale | Q5 | User |
| 2026-09-15 | Admin manages everything (products, video, categories, content, settings) with role-based access per user | User request | User |
| 2026-09-15 | Q6–Q16 go with architect recommendations (see docs/01 §3) | User delegated | User |
| 2026-09-15 | Gaps found mid-build are discussed before building | Process | User |
| 2026-09-15 | Signed PDF proposal overrides earlier drafts | Latest agreed version | Team |
| 2026-09-15 | Client progress page is separate from codebase; shows features + progress only | Client asked for task sheet | User |
| 2026-09-15 | Every task: developer agent → tester agent → loop on failure; SOLID; Docker; 8 breakpoints | Standards | User |

---

## Session log

### 2026-09-15 (5) — Database design baselined
**Done**
- W0-10 **DONE**: `docs/08-database-design.md` rev 4 baselined (65 tables, 10 schemas). Path: author → review r1 (4 Critical, 9 Major) → rev 2 (63 tables) → r2 (1 Critical SMTP settings, 4 Major) → rev 3 (65) → r3 (1 Critical late PayPal COMPLETED lost, 2 Major) → rev 4 → spot-check BASELINE.
- Key decisions: derived stock reservations (no StockReservations table); `Settings.Security` GrantsAll-only (SMTP, security, legal retention); `Settings.Payments` for bank/PayPal; `Orders.Refund` separate; subset rule for Users.Manage; 2FA for Super Admins; PayPal charges USD (pending client); capture claim + recovery table + `CapturedRequiresRefund`; PENDING capture hold 5 days, cut back on DENIED; light delivery-zone model (`Districts.DeliveryZone` + `DeliveryMethodZoneRates`); PDPA anonymisation via set-based updates + audit scrub; collation `Latin1_General_100_CI_AS_SC`; SQL Express buffer pool 1,410 MB → ~2 GB budget.
- Docs 02/03 aligned with doc 08 (roles/security rules, backups every 15 min off-server, private uploads volume, background jobs, stock rules).
- Verified the author agent only touched doc 08 when its safety classifier was unavailable.

**Lesson**
- L5: large design docs must be written to disk section by section (a single-response doc hit the output limit and was lost). Reviews should run in rounds until no Critical/Major remain, then a targeted spot-check.

**Waiting on user / client** (unchanged): Docker Desktop, GitHub account, style-tile decisions, D1 PayPal USD, D2 VAT, D8 delivery zone rates/couriers/pickup locations, D9 retention, D13 PENDING hold length.

### 2026-09-15 (4) — Style tile done, DB design drafted
**Done**
- W0-07 style tile **DONE**: QA r1 FAIL (5 Major: missing `[hidden]` rule, focus lost under reduced motion, invisible focus ring on desktop card actions), QA r2 PASS. Lessons L2–L4 added to docs/06. Confirmed rules + carry-forward items added to docs/04.
- W0-10 DB design written: `docs/08-database-design.md` (76 tables, 10 schemas). First attempt failed (agent output limit), rewritten section by section. Independent architect review running.
- Client progress: "Design style guide" set to In progress 90% (awaiting client review); log entry added.

**Waiting on user / client**
- Docker Desktop install; GitHub account.
- Style tile decisions (logo, headline font, round swatches, uppercase labels, mobile card actions, WhatsApp colour, hero text placement + hero cap, real delivery values).
- DB open questions: PayPal charge currency / exchange rate, VAT registration, bank-slip upload, bank-transfer expiry (48 h default), verified buyer = Delivered?

### 2026-09-15 (3) — Week 0 build started
**Done**
- W0-06 client progress page built by developer agent (`client-progress/`: index.html, styles.css, app.js, progress-data.js). Data-driven, works from file://, 4% real status.
- Tester agent round 1: all 12 acceptance criteria pass. Defects D1–D6 found (invalid data entries crash sections, bad milestone dates show "Completed", 8 client-visible features missing, README with internal notes would be publicly served, impossible `?today=` dates roll over, "Banana Republic" named on client page). Sent back to developer.

- W0-06 **DONE** after 3 QA rounds (r2 found N1 runtime typo blanking page, N2 missing-progress warning, N3 empty header labels; r3 PASS). Guide G1 wording (comma when adding after last group) fixed directly in docs/07.
- Lesson: data files edited by non-developers need defensive loading + a plain-language error guide. Apply the same to any admin-imported data later.

**In progress**
- W0-07 style tile (developer agent) → `spencer-denim-ecommerce/design/style-tile/`.
- W0-10 database design (architect agent) → `docs/08-database-design.md`.

**Decisions**
- Team-only instructions for updating the progress page live in `docs/07-client-progress-update-guide.md`, not in the uploaded `client-progress/` folder.
- No third-party brand names on client-facing pages.

### 2026-09-15 (2) — Decisions applied
**Done**
- Applied answers Q1–Q5 + recommendations Q6–Q16. Scope baseline v1.0 (all demo features kept, RBAC admin, media library with video, PayPal last).
- Rewrote docs 01, 02, 05, TASKS. Updated 03 (RBAC, media, payments, toolchain) and 04 (no accent colour).
- Replaced root `CLAUDE.md` with pointer to this project.
- Verified toolchain: .NET 10.0.201, Node 24.13.1, Angular CLI 21.1.4, git, gh. **Docker missing.**

**Next**
- Install Docker Desktop. Build client progress page, style tile, scaffold.

### 2026-09-15 (1) — Kick-off / discovery
**Done**
- Read signed proposal PDF, reference.txt (Banana Republic), content checklist, draft proposals, prototype brief, demo home/admin/checkout, Bug screenshots. Fetched Banana Republic homepage.
- Created `spencer-denim-ecommerce/` with README, docs 01–06, TASKS, MEMORY.

**Found**
- PayPal receive limitation in Sri Lanka (risk). Proposal date conflict (now resolved). Out-of-scope demo items. Proposal omissions (PDP/cart/search/emails/policies/delivery fees). Root CLAUDE.md outdated. No git repo.
