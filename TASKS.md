# TASKS — Internal Task Board

Last updated: 2026-09-21

> **Two developers now work in parallel.** This file is the **plan** (weeks, scope, status summary), updated by the project owner at the end of the day.
> The **live board is GitHub Issues** — claim work there before starting. Write your daily notes in `docs/journal/`, not here.
> Process: [docs/12-team-collaboration.md](docs/12-team-collaboration.md) · New joiner: [docs/13-onboarding.md](docs/13-onboarding.md)

Status: `TODO` · `IN PROGRESS` · `IN TEST` · `FAILED TEST` · `DONE` · `BLOCKED`
Feature IDs → [docs/02-scope-baseline.md](docs/02-scope-baseline.md). Process → [docs/06-engineering-process.md](docs/06-engineering-process.md). Plan → [docs/05-delivery-plan.md](docs/05-delivery-plan.md).

## Week 0 — Setup (Tue 15 – Fri 18 Sep 2026)

| ID | Task | Owner | Status | Notes |
|---|---|---|---|---|
| W0-01 | Read all client documents, demo, reference site | Architect | DONE | 15 Sep |
| W0-02 | Planning docs 01–06, TASKS, MEMORY | Architect | DONE | 15 Sep |
| W0-03 | Kickoff questions Q1–Q16 answered, scope baselined v1.0 | Team | DONE | 15 Sep |
| W0-04 | Replace outdated root `CLAUDE.md` | Architect | DONE | 15 Sep |
| W0-05 | Install Docker Desktop on dev machine | TU2SL team | DONE | 2026-09-17: `wsl --update` failed ("Catastrophic failure"), `wsl --update --web-download` worked (WSL 2.7.14, kernel 6.18). Stuck Docker instance closed; engine running (Docker 29.8.0, Compose v5.5.1). Use a **normal** (non-admin) PowerShell for docker. |
| W0-14 | Business process document (WooCommerce-informed) + git branching workflow | Analyst → Reviewer | DONE | `docs/11-business-processes.md` (17 processes, 42 gaps) + doc 09 §5b branching workflow. **Review PASS at round 4** (r1: 3 blockers, 7 majors, 6 minors; r2: 1 blocker, 2 majors; r3: 1 major; r4: PASS). Open decisions for the architect/client are tracked as gap rows, not as unfinished text. |
| W0-13 | Publish client progress page to Hostinger (task sheet only) | Developer | DONE | 2026-09-17: https://colombowardrobecompany-build-tracker.kt-wedding.online via `tools/publish-client-progress.ps1` (FTPS, pinned key, 4 files). Verified live at 390 px and 1280 px. Doc 10. |
| W0-06 | Client progress page (`../client-progress/`) | Developer → Tester | DONE | 3 QA rounds. r1: D1–D6; r2: N1–N3; r3: PASS, all fixed, no regressions. Guide wording fix G1 applied. Update guide: `docs/07-client-progress-update-guide.md`. |
| W0-07 | Style tile (Banana Republic–inspired tokens, type, buttons, card, header, mobile) | Developer → Tester | DONE | `design/style-tile/`. QA r2 PASS (S1–S12 fixed, 2 cosmetic carried into doc 04 §4 for Angular build). 9 review decisions accepted as defaults 2026-09-16 (doc 04); client supplies logo. QA r1 FAIL: 5 Major (S1–S3 missing global `[hidden]` rule → bag/filter states wrong; S4 focus lost in drawers under reduced motion; S5 no focus ring on desktop card actions), 4 Minor, 3 Cosmetic. Back with developer. |
| W0-08 | GitHub repo + branch rules + GitHub Actions CI | Developer | DONE | Repo created (private, personal account): https://github.com/MASACHITH/spencer-denim-ecommerce. `.github/workflows/ci.yml` added with W0-09 (2026-09-17), first CI run green (backend + frontend jobs). Branch protection rules on `main` not yet verified via GitHub API — check before Week 4 launch hardening. |
| W0-09 | Scaffold: backend layers (.NET 10), Angular 21 workspace (storefront SSR, admin, shared) | Developer → Tester | DONE | Committed 2026-09-17 (`4efe767`), squashed scaffold branch, QA PASS, first CI run green. `docker-compose`/Caddy split out to **W0-15** below (README: "Docker packaging is deferred to W0-15" — office network blocks it; needs W0-05 first). |
| W0-15 | Docker packaging: docker-compose, Dockerfiles, Caddy reverse proxy | Developer → Tester | TODO | Deferred out of W0-09 (see README "Getting started"). Blocked on W0-05 (WSL 2 kernel update, paused by user 2026-09-17). No compose/Dockerfile/Caddyfile exist yet. **Carries a W1-01 follow-up:** once real Caddy/Docker networking exists, re-enable `ForwardedHeaders.XForwardedFor` in `Api/Http/ForwardedHeadersSetup.cs` and widen `KnownIPNetworks` from loopback-only to the real Docker bridge subnet (deliberately left disabled/loopback-only until this lands — see W1-01's audit-log spoofing defect in MEMORY.md 2026-09-21). **Launch blocker:** the reverse proxy must be given real forwarded-headers handling here — `UseForwardedHeaders` with `KnownIPNetworks`/`KnownProxies` pinned to the Docker bridge, and `X-Forwarded-For` re-enabled (it is deliberately off since W1-01 round 3, when a forged header was written straight into `ops.AuditLogs`). Until it is done the API sees only Caddy's address, so the newsletter signup's rate limit (HOME-10) is one shared bucket for the whole site and its ceiling is set for that; `NewsletterSignupThrottle` logs a warning naming this task when it detects it. Tighten the ceiling in the same change. |
| W0-10 | DB schema design: identity + RBAC, catalogue/variants, media, content, orders/payments, promos, reviews, traffic, audit | Architect | DONE | `docs/08-database-design.md` **baselined** (rev 4, 65 tables) after 3 reviews + spot-check. Build-time notes in doc 08 Appendix B. Review r1: 4 Critical, 9 Major, 17 Minor → rev 2 (63 tables). Review r2: 23/30 fixed, derived reservations sound; new N2 Critical (SMTP settings editable by Store Manager → reset-email interception), N1/N3/N4/N5 Major (stuck Capturing recovery, staff-account takeover via Users.Manage, district delivery zones, PDPA scrub vs CHECK). Decision: light delivery-zone model. Rev 3 → review r3: R1 Critical (late PayPal COMPLETED lost on Failed/Cancelled payment), R2/R3 Major (DENIED keeps 5-day hold; anonymisation leaks PII into audit log). Rev 4 done (only doc 08 changed, verified) → reviewer spot-check for baseline. |
| W0-11 | Buy & harden Hostinger KVM 2 + domain | Developer | TODO | After client pays hosting/domain |
| W0-12 | Client: verify PayPal business account can receive payments | Client / TU2SL | TODO | Needed by week 4 |
| W0-14 | Multi-session coordination doc + machine readiness fix | Architect | DONE | 2026-09-21: `docs/14-multi-session-coordination.md` added (second Claude Code session/machine now works on this repo). Fixed on this (new) machine: repo-local git identity was resolving to the machine's global identity (`nuwank`), and the push-safety hook (`core.hooksPath`) was unset — both now set repo-locally. `gh` CLI not installed here, so no push from this machine yet. |

## Week 1 — Foundation & admin catalogue (19–25 Sep)
| ID | Task | Features | Status |
|---|---|---|---|
| W1-01 | Admin auth + RBAC foundation + audit log on auth events (**slice 1** of the full "Admin auth + Users & Roles (RBAC) + audit log" scope) | ADM-01–03 (partial) | DONE | 2026-09-21. Developer → tester loop, 3 fix rounds, all found by the tester before PASS: (1) Major — login response timing leaked whether an email belonged to an admin account (username enumeration); fixed with an equal-cost dummy password-hash check on every reject path. (2) Major regression from fixing (1)'s companion antiforgery-cookie hardening — `SecurePolicy=Always` 500'd the antiforgery-token endpoint (and therefore logout) over plain HTTP, which is both this project's own dev default and, per doc 03 §4, likely production's internal path too (no `UseForwardedHeaders` existed); fixed with real forwarded-headers handling + an environment-driven `RequireSecureCookies` option (default `true`, only dev relaxes it). (3) Major — the forwarded-headers fix from (2) trusted `X-Forwarded-For` from any direct caller, letting an unauthenticated request forge the IP address written into the audit log (proven with a live forged-header request, not just reasoned about); fixed by not processing `X-Forwarded-For` at all until a real proxy exists (`X-Forwarded-Proto` still enabled, that's what (2) actually needed). Final tester pass: build clean, 51/51 unit, 17/17 integration (both LocalDB and the real SQL Server at 127.0.0.1,11433), 18/18 frontend, live manual repro of all three fixes. Follow-ups from this slice: see **W1-08/W1-09/W1-10/W1-11** below and the forwarded-headers note on **W0-15**. |
| W1-08 | Users & Roles management: CRUD endpoints/UI, wire `RoleAssignmentPolicy` (subset rule) to a real caller, seed the remaining 3 default roles (Store Manager, Order Staff, Content Editor) + their permission sets | ADM-01–03 remainder | TODO |
| W1-09 | Audit log viewer (admin screen) | ADM-03 remainder | TODO |
| W1-10 | 2FA enrollment/verification for Super Admins (schema already in place from W1-01; `UserTokens` supports it) | ADM-01 remainder (doc 08 baseline) | TODO |
| W1-11 | Carry-over hardening from W1-01: Angular route guard on admin routes (server-side cookie auth is the real boundary; this is UX only), cookie `SecurityStamp` revalidation loop for promptly signing out a deactivated/locked account mid-session, a forwarded-headers regression test (blocked so far by `Microsoft.AspNetCore.HttpOverrides` types not resolving in the plain-SDK unit test project on this machine — needs investigating, possibly machine-specific) | — | TODO |
| W1-02 | Catalogue foundation: the `catalog`/`media`/`inventory` schema and EF model, the demo catalogue seeder, and the **read-only** storefront catalogue API (departments + category tree, product listing with filters/sorts/paging, product page, search). No admin write endpoints and no storefront UI — those are W1-12, W1-03 and W2-*. | GLB-05, DEPT-01, PLP-01–07, CARD-01, PDP-01–06, SRCH-02 | IN PROGRESS | Branch `feature/W1-02-catalogue-foundation`. Tester round 1 FAILED it (3 major, 5 minor) and round 2 FAILED it again (2 major — one a regression from the round-1 relevance fix, one pre-existing: `IsActive` was not honoured on the product query — plus 4 minor); developer fixes applied for both rounds and back with the tester. Was listed as "Departments & categories (create/edit/reorder) — ADM-11" until 2026-09-23; that is an admin write feature this branch never touched, now **W1-12**. |
| W1-12 | Departments & categories admin screens (create/edit/reorder), on top of W1-02's schema. **Carries a W1-02 follow-up:** apply `CatalogSlug` to `Category.Slug` (and `Department.Slug`) on the write path — only `Product.Slug` is guarded today, because only it had a write path to guard. | ADM-11 | TODO |
| W1-03 | Products + variants + badges + SEO fields. **Carries a W1-02 follow-up:** its migration adds `CK_Products_Slug` for the slug pattern `CatalogSlug` already enforces in the domain, so a direct SQL write cannot create a product whose own page answers 400 (see `ProductConfiguration.cs`). | ADM-10 | TODO |
| W1-04 | Media library: images + video upload pipeline | ADM-18 | **PASSED** — tester passed it after two fix rounds (round 1: 1 blocker, 3 majors, 5 minors; round 2: 3 small items), plus a hardening pass before the PR. Pushed to `origin feature/W1-04-media-library`, awaiting a PR; **not Done until it is merged and the client progress page is updated**. In it: `IFileStorage` over two volumes, the detect-validate-hash-store-render-publish pipeline with EXIF stripped from originals and every rendition, `/media` serving with a year's immutable cache, strong `ETag`, conditional GETs and byte ranges, the admin library endpoints, the demo placeholder generator, and the WebP rendition set (thumb/card/large/zoom) **linked by every consumer** — a listing page went from 2,101 KB of originals to 383 KB. Deduplication by checksum is now a real guarantee (`UX_MediaAssets_Checksum_Ready`, its own migration), not a read-then-write. **Unblocks the five HOME sections W2-01 seeded switched off** (HOME-01/04/05/06/08) |
| W1-05 | Inventory per variant | ADM-12 | TODO |
| W1-06 | Storefront shell: header, announcement bar, mega menu, mobile nav, footer, WhatsApp, district selector | GLB-02–09 | TODO |
| W1-07 | Staging environment live | OPS-03 | TODO |

## Week 2 — Storefront (26 Sep–2 Oct)
| ID | Task | Features | Status |
|---|---|---|---|
| W2-01 | Homepage blocks + Content admin | HOME-01–10, ADM-17 | **PASSED** — tester passed it after one fix round (6 majors, 5 minors), plus three polish items before the PR. Pushed to `origin feature/W2-01-homepage`, awaiting a PR; **not Done until it is merged and the client progress page is updated**. The storefront half is built on `feature/W2-01-homepage` and going to the tester: `content.HomeSections` / `HomeSectionItems` / `AnnouncementMessages` and `marketing.NewsletterSubscribers` with their migration and seeder, the read-only content API, the newsletter signup endpoint, and the homepage itself. **ADM-17's admin screens are not in it** and remain TODO — see W1-12 for the same split on the catalogue. HOME-01/04/05/06/08 are seeded switched off until the media library lands (W1-04) — five sections, HOME-04 among them because a trending item is a picture with a filtered listing URL on it (doc 08 §3.7), not a product row |
| W2-02 | Department, New Arrivals, Sale pages | DEPT-01–03 | IN REVIEW — DEPT-01 and the New Arrivals / Sale listings landed with W2-03 (two of the five permanent nav entries could not point at a missing page). Their promo banners and editorial copy are still TODO |
| W2-03 | Listing: filters, chips, sort, count, empty, load more | PLP-01–07 | IN REVIEW — fix round 1 delivered on `feature/W2-03-product-listing`, back with the tester |
| W2-04 | Product card + Quick View | CARD-01, QV-01 | IN PROGRESS — the card landed with W2-03 minus Quick View (QV-01) and Add to Bag (needs the bag API, W2-09); the product detail page is still TODO |
| W2-05 | Product page: gallery/zoom/video, variants, size guide, details, related, recently viewed | PDP-01–07 | IN REVIEW — built on `feature/W2-05-product-page`, going to the tester. PDP-01..03 and PDP-05..07 done; PDP-04's modal is built but the API projects no size-guide content (see MEMORY). PDP-03's Add to Bag / Buy Now wait for the bag API (W2-09) and the real wishlist for W2-08, so neither button is on the page |
| W2-06 | Reviews + moderation | PDP-08, ADM-16 | TODO |
| W2-07 | Search overlay + results | SRCH-01–02 | TODO |
| W2-08 | Wishlist | WISH-01 | TODO |
| W2-09 | Bag + mini bag | CART-01–02 | TODO |
| W2-10 | Content pages, SEO, accessibility | GLB-10–12 | TODO |

## Week 3 — Commerce (3–9 Oct)
| ID | Task | Features | Status |
|---|---|---|---|
| W3-01 | Customer register/login/reset + My account | ACC-01–02 | TODO |
| W3-02 | Checkout, guest, delivery methods, success page | CHK-01–04 | TODO |
| W3-03 | Payment provider abstraction + Bank transfer + stock reservation/expiry. **Tharanga** — issue #53, which also carries the `sales` schema (orders, payments, refunds), because none of it exists yet. Read docs/11 P7 before building; the states are already decided. | PAY-01, PAY-03 | TODO |
| W3-04 | Admin orders: workflow, approve transfer, courier tracking, invoice | ADM-09 | TODO |
| W3-05 | Promo codes + sale campaigns | ADM-13–14 | TODO |
| W3-06 | Transactional emails | ACC-03 | TODO |
| W3-07 | Customers, newsletter subscribers, settings | ADM-15, ADM-21, ADM-22 | TODO |

> **Cash on Delivery is out of the signed scope** (docs/02:166, docs/11:42 and :719) and is **not** part of W3-03 or W4. It came up as work to assign; it is raised as a change request in issue #55 and nobody builds it until the client decides. See that issue for why it is not a small addition.

## Week 4 — PayPal, insight & launch (10–15 Oct)
| ID | Task | Features | Status |
|---|---|---|---|
| W4-01 | **PayPal** (server capture + webhook) | PAY-02 **Tharanga** — issue #54. Build and test on the **sandbox**; live credentials are a settings change, not a code change. Blocked by #53. | TODO |
| W4-02 | Dashboard widgets | ADM-04–08 | TODO |
| W4-03 | Reports + traffic | ADM-19–20 | TODO |
| W4-04 | Full regression: journeys × 8 breakpoints, axe, Lighthouse, ZAP | — | TODO |
| W4-05 | Production deploy, backups + restore test, monitoring | OPS-01–02 | TODO |
| W4-06 | Client UAT + fixes | — | TODO |
| W4-07 | Admin training + handover guide | OPS-04 | TODO |
