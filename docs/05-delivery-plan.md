# 05 — Delivery Plan & Client Progress Tracker

Last updated: 2026-09-15 · Status: **Approved timeline: 15 Sep → 15 Oct 2026**

## 1. Plan (one month)

Ordering principle: foundation → admin catalogue → storefront → commerce (bank transfer) → **PayPal last** → launch.

| Week | Dates | Goal | Feature IDs |
|---|---|---|---|
| **0 — Setup** | Tue 15 – Fri 18 Sep | Planning docs, client progress page, style tile, GitHub repo + CI, solution scaffold + Docker, DB schema, VPS + domain | OPS-01, GLB-01 |
| **1 — Foundation & admin catalogue** | Sat 19 – Fri 25 Sep | Admin login, **Users & Roles**, audit log, departments/categories, products + variants, media library (images + video), inventory, storefront shell (header, mega menu, mobile nav, footer, WhatsApp), staging live | ADM-01–03, ADM-10–12, ADM-18, GLB-02–09 |
| **2 — Storefront** | Sat 26 Sep – Fri 2 Oct | Homepage + content admin, department pages, listing/filters/sort, product cards + quick view, product page, search, wishlist, bag, reviews, content pages, SEO | HOME-*, DEPT-*, PLP-*, CARD-01, QV-01, PDP-*, SRCH-*, WISH-01, CART-*, ADM-16/17, GLB-10–12 |
| **3 — Commerce** | Sat 3 – Fri 9 Oct | Customer accounts, checkout + guest, delivery methods, **bank transfer**, order lifecycle, admin orders, promo codes, sale campaigns, emails, order history + tracking, customers, newsletter | CHK-*, PAY-01, PAY-03, ACC-*, ADM-09, ADM-13–15, ADM-21, ADM-22 |
| **4 — PayPal, insight & launch** | Sat 10 – Thu 15 Oct | **PayPal integration**, dashboard, reports, traffic, full regression (all journeys × 8 breakpoints), security + performance pass, production deploy, backup restore test, UAT, admin training | PAY-02, ADM-04–08, ADM-19/20, OPS-02–04 |

**First to slip if time runs short:** ADM-06 Traffic Sources widget, ADM-07 product sales-trend sparkline, ADM-03 audit-log UI (logging still records), PDP-07 recently viewed.

**Risk:** this is a full custom e-commerce build in one month. We protect the date by holding scope to [02](02-scope-baseline.md), raising blockers the day they appear, and discussing mid-build gaps immediately.

## 2. Client progress tracker (separate from the main project)

**What:** one simple static HTML page that shows the client what we are building and how far along it is.
**Where:** `../client-progress/` (outside this codebase). Can later be hosted at `progress.<domain>`.

**Client sees**
- Project name, timeline 15 Sep → 15 Oct 2026, overall % complete, last-updated date
- Features grouped by area: Design & Theme · Homepage · Shopping Pages · Product Page · Search, Wishlist & Bag · Checkout & Payments · Customer Account · Admin Panel · Hosting & Launch
- Per feature: **Planned / In progress / Done** with a progress bar
- Weekly milestones
- Daily update log ("Today we …")
- Works well on a phone

**Client does NOT see:** test cases, bugs, internal tasks, hours or effort, agent process.

**Maintenance:** see [07-client-progress-update-guide.md](07-client-progress-update-guide.md) (team only, never uploaded). All content lives in `progress-data.js`. The daily update edits statuses and adds one log line. A feature is marked Done only after it passes testing.

## 3. Milestones & payments (from proposal)

| Milestone | Target | Trigger |
|---|---|---|
| Project start | 15 Sep | 50% advance + hosting + domain |
| Staging preview for client | 2 Oct | End of week 2 |
| UAT | 13–14 Oct | |
| Handover | 15 Oct | Final 50%. Support window starts. |
