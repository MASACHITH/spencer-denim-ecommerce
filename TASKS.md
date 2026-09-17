# TASKS — Internal Task Board

Last updated: 2026-09-15

Status: `TODO` · `IN PROGRESS` · `IN TEST` · `FAILED TEST` · `DONE` · `BLOCKED`
Feature IDs → [docs/02-scope-baseline.md](docs/02-scope-baseline.md). Process → [docs/06-engineering-process.md](docs/06-engineering-process.md). Plan → [docs/05-delivery-plan.md](docs/05-delivery-plan.md).

## Week 0 — Setup (Tue 15 – Fri 18 Sep 2026)

| ID | Task | Owner | Status | Notes |
|---|---|---|---|---|
| W0-01 | Read all client documents, demo, reference site | Architect | DONE | 15 Sep |
| W0-02 | Planning docs 01–06, TASKS, MEMORY | Architect | DONE | 15 Sep |
| W0-03 | Kickoff questions Q1–Q16 answered, scope baselined v1.0 | Team | DONE | 15 Sep |
| W0-04 | Replace outdated root `CLAUDE.md` | Architect | DONE | 15 Sep |
| W0-05 | Install Docker Desktop on dev machine | TU2SL team | BLOCKED (paused by user 2026-09-17) | Docker Desktop 4.91 installed per-user; WSL 2 kernel missing, so `wsl --update` as admin is still needed (DEV-GUIDE-02). Docker work paused for now. |
| W0-13 | Publish client progress page to Hostinger (task sheet only) | Developer | DONE | 2026-09-17: https://colombowardrobecompany-build-tracker.kt-wedding.online via `tools/publish-client-progress.ps1` (FTPS, pinned key, 4 files). Verified live at 390 px and 1280 px. Doc 10. |
| W0-06 | Client progress page (`../client-progress/`) | Developer → Tester | DONE | 3 QA rounds. r1: D1–D6; r2: N1–N3; r3: PASS, all fixed, no regressions. Guide wording fix G1 applied. Update guide: `docs/07-client-progress-update-guide.md`. |
| W0-07 | Style tile (Banana Republic–inspired tokens, type, buttons, card, header, mobile) | Developer → Tester | DONE | `design/style-tile/`. QA r2 PASS (S1–S12 fixed, 2 cosmetic carried into doc 04 §4 for Angular build). 9 review decisions accepted as defaults 2026-09-16 (doc 04); client supplies logo. QA r1 FAIL: 5 Major (S1–S3 missing global `[hidden]` rule → bag/filter states wrong; S4 focus lost in drawers under reduced motion; S5 no focus ring on desktop card actions), 4 Minor, 3 Cosmetic. Back with developer. |
| W0-08 | GitHub repo + branch rules + GitHub Actions CI | Developer | IN PROGRESS | Repo created (private, personal account): https://github.com/MASACHITH/spencer-denim-ecommerce. First commit pushed 2026-09-16. CI + branch rules come with the scaffold (W0-09). |
| W0-09 | Scaffold: backend layers (.NET 10), Angular 21 workspace (storefront SSR, admin, shared), docker-compose, Caddy | Developer → Tester | TODO | Needs W0-05 for compose test |
| W0-10 | DB schema design: identity + RBAC, catalogue/variants, media, content, orders/payments, promos, reviews, traffic, audit | Architect | DONE | `docs/08-database-design.md` **baselined** (rev 4, 65 tables) after 3 reviews + spot-check. Build-time notes in doc 08 Appendix B. Review r1: 4 Critical, 9 Major, 17 Minor → rev 2 (63 tables). Review r2: 23/30 fixed, derived reservations sound; new N2 Critical (SMTP settings editable by Store Manager → reset-email interception), N1/N3/N4/N5 Major (stuck Capturing recovery, staff-account takeover via Users.Manage, district delivery zones, PDPA scrub vs CHECK). Decision: light delivery-zone model. Rev 3 → review r3: R1 Critical (late PayPal COMPLETED lost on Failed/Cancelled payment), R2/R3 Major (DENIED keeps 5-day hold; anonymisation leaks PII into audit log). Rev 4 done (only doc 08 changed, verified) → reviewer spot-check for baseline. |
| W0-11 | Buy & harden Hostinger KVM 2 + domain | Developer | TODO | After client pays hosting/domain |
| W0-12 | Client: verify PayPal business account can receive payments | Client / TU2SL | TODO | Needed by week 4 |

## Week 1 — Foundation & admin catalogue (19–25 Sep)
| ID | Task | Features | Status |
|---|---|---|---|
| W1-01 | Admin auth + Users & Roles (RBAC) + audit log | ADM-01–03 | TODO |
| W1-02 | Departments & categories (create/edit/reorder) | ADM-11 | TODO |
| W1-03 | Products + variants + badges + SEO fields | ADM-10 | TODO |
| W1-04 | Media library: images + video upload pipeline | ADM-18 | TODO |
| W1-05 | Inventory per variant | ADM-12 | TODO |
| W1-06 | Storefront shell: header, announcement bar, mega menu, mobile nav, footer, WhatsApp, district selector | GLB-02–09 | TODO |
| W1-07 | Staging environment live | OPS-03 | TODO |

## Week 2 — Storefront (26 Sep–2 Oct)
| ID | Task | Features | Status |
|---|---|---|---|
| W2-01 | Homepage blocks + Content admin | HOME-01–10, ADM-17 | TODO |
| W2-02 | Department, New Arrivals, Sale pages | DEPT-01–03 | TODO |
| W2-03 | Listing: filters, chips, sort, count, empty, load more | PLP-01–07 | TODO |
| W2-04 | Product card + Quick View | CARD-01, QV-01 | TODO |
| W2-05 | Product page: gallery/zoom/video, variants, size guide, details, related, recently viewed | PDP-01–07 | TODO |
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
| W3-03 | Payment provider abstraction + Bank transfer + stock reservation/expiry | PAY-01, PAY-03 | TODO |
| W3-04 | Admin orders: workflow, approve transfer, courier tracking, invoice | ADM-09 | TODO |
| W3-05 | Promo codes + sale campaigns | ADM-13–14 | TODO |
| W3-06 | Transactional emails | ACC-03 | TODO |
| W3-07 | Customers, newsletter subscribers, settings | ADM-15, ADM-21, ADM-22 | TODO |

## Week 4 — PayPal, insight & launch (10–15 Oct)
| ID | Task | Features | Status |
|---|---|---|---|
| W4-01 | **PayPal** (server capture + webhook) | PAY-02 | TODO |
| W4-02 | Dashboard widgets | ADM-04–08 | TODO |
| W4-03 | Reports + traffic | ADM-19–20 | TODO |
| W4-04 | Full regression: journeys × 8 breakpoints, axe, Lighthouse, ZAP | — | TODO |
| W4-05 | Production deploy, backups + restore test, monitoring | OPS-01–02 | TODO |
| W4-06 | Client UAT + fixes | — | TODO |
| W4-07 | Admin training + handover guide | OPS-04 | TODO |
