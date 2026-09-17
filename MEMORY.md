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
- **Repo:** https://github.com/MASACHITH/spencer-denim-ecommerce (user's **personal** account MASACHITH, branch `main`). Local git identity is set per repo (MASACHITH / sachithishara1@gmail.com). `destinity-sachithm` is the user's **office** account. **Never push this project with it, and never switch the active `gh` account to it without asking the user.** Before every push, confirm `gh auth status` shows MASACHITH as active. If it doesn't, ask the user before switching.
- **Reference archive:** `reference/` holds read-only copies (taken 2026-09-16) of the old demo (`tuasl-fashion-demo/`), the client documents (signed proposal + theme reference) and the original prototype prompt. See `reference/README.md`. The originals remain outside the repo.
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

### 2026-09-17 (1) — Docker guides, hosting, task sheet publishing
**Done**
- PDF guides in `docs/guides/`: DEV-GUIDE-01 Docker Desktop on Windows (v1.1, 7 pp) and DEV-GUIDE-02 Fix "WSL needs updating" (3 pp). HTML sources are kept; built with headless Chrome + a pypdf footer stamp.
- Dev PC: Docker Desktop 4.91 installed **per-user** (CLI 29.8.0). WSL features on, but the **WSL 2 kernel is missing**. The user must run `wsl --update` as admin (DEV-GUIDE-02). Account `SCIENTER\sachithi` is not a local admin; the PC is domain-joined (scienter.lk).
- Hostinger shared hosting bought for the task sheet: https://colombowardrobecompany-build-tracker.kt-wedding.online. FTP host 147.93.101.68:21, user `u765465138.colombowardrobecompany-build-tracker.kt-wedding.online`, opens in `/public_html` (contained only `default.php`).
- FTP password stored DPAPI-encrypted at `%USERPROFILE%\.spencer-denim\tracker-ftp-credential.xml` (never in repo or docs). The user shared it in chat, so recommend changing it and running `-SetPassword`.
- `tools/publish-client-progress.ps1` (allow-list of 4 files, node validation, FTPS with pinned Hostinger key `sha256//rRBXAmuSvqI5e99CZPWQDXLPWIlTIvu77x/aZc349JQ=`, cert expires 2027-01-17, size + live verification, never deletes) + `docs/10-client-progress-publishing.md`. Dry run OK; wrong pin correctly rejected (curl 90).
- `client-progress/progress-data.js`: lastUpdated 2026-09-17; "Hosting, domain & security setup" in progress 25%; log entries for 16 and 17 Sep; overall 6%.

**Blocked**
- Publishing failed: the office network blocks FTP **data** connections (control on port 21 works; data resets or times out in both PROT P and PROT C). Live site still shows Hostinger's default page. Options: another network, hPanel File Manager, or IT allowing passive FTP. Recorded in doc 10 §5b.

### 2026-09-16 (2) — Client-item defaults accepted; plan for tomorrow
**Decisions (user accepted recommended defaults)**
- Style tile: all 9 recommendations (Inter only, round swatches/radios, uppercase labels, icon buttons on phone cards, WhatsApp green, hero text over photo with fade, 880 px hero cap, placeholder delivery values). Client still supplies the logo. Recorded in docs/04.
- PayPal charges USD (+3% buffer). VAT default: not registered (accountant confirms before go-live). Delivery: placeholder zone rates, couriers and pickup locations (real values before go-live). PENDING PayPal hold: 5 days. Recorded in docs/01 §3b and docs/08 §11.

**PC readiness checked:** Windows 10 Pro 22H2, virtualization ON, 16 GB RAM, C: 60 GB / D: 105 GB free, OpenSSH client present, **WSL not installed**. User installs `wsl --install --no-distribution` → reboot → Docker Desktop (WSL 2 backend, disk image on D:).

**Tomorrow (2026-09-17)**
1. Verify Docker (`docker run --rm hello-world`) → W0-09 scaffold (.NET 10 layers, Angular 21 storefront SSR + admin + shared, docker-compose, Caddy, GitHub Actions) + spikes B6 (EF complex types, collation). Developer → tester loop.
2. User brings Hostinger KVM 2 details (IP, Ubuntu 24.04 plain OS, hPanel access) + domain/registrar DNS access. **No passwords in chat**: generate an SSH key locally, and the user adds the public key in hPanel.
3. W0-11 VPS hardening (SSH keys only, UFW 22/80/443, fail2ban, unattended-upgrades, Docker) + DNS A records (@, www, staging) + Caddy HTTPS.
4. Client progress: "Hosting, domain & security setup" → In progress.

### 2026-09-16 (1) — GitHub repo set up (personal account)
**Done**
- Private repo https://github.com/MASACHITH/spencer-denim-ecommerce created by the user; pushed planning docs, style tile, DB design, `.gitattributes`, and `reference/` archive (old demo, client documents, prototype prompt). All commits authored and pushed as MASACHITH.
- Added `docs/09-git-and-repository-rules.md`, a project `CLAUDE.md` (git rule at top), a git rule in the root `CLAUDE.md`, and a pre-push hook `tools/git-hooks/pre-push` (repo-local `core.hooksPath`). The hook blocks any push whose URL isn't MASACHITH/spencer-denim-ecommerce or whose active `gh` account isn't MASACHITH. The URL check was tested (pass and block). The account check wasn't tested, to avoid switching to the office account.

**Incident / lesson**
- L6: the user was alarmed to see the office account `destinity-sachithm` in command output (a `gh auth switch` back after the push; nothing was pushed there). Rule: never switch to the office account unless asked; always state which account a push went to.

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
