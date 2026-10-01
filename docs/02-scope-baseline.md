# 02 — Scope Baseline (v1.0)

Last updated: 2026-09-15 · Status: **Baselined.** Answers to Q1–Q16 are applied (see [01](01-project-understanding.md) §3).

**Rule:** we build every feature the client saw in the demo pages, plus the signed proposal's additions. The **only visual change is the theme** (Banana Republic). The **only payment methods** are Bank Transfer and PayPal, with PayPal built last. Any gap found mid-build is discussed before it is built.

Feature IDs are used in `TASKS.md` and on the client progress page.

Legend: ✅ In scope · ⏭ Future phase · ❌ Removed

## A. Theme & global

| ID | Feature | Scope |
|---|---|---|
| GLB-01 | Banana Republic–inspired theme, site-wide (full replacement, no Denim × Linen) | ✅ |
| GLB-02 | Spencer Denim branding (name, logo) | ✅ |
| GLB-03 | Rotating announcement bar (admin-editable messages) | ✅ |
| GLB-04 | Sticky header: logo, search, sign in/account, wishlist count, bag count | ✅ |
| GLB-05 | Navigation **Women · Men · Kids · New Arrivals · Sale**. Desktop mega menu (categories from admin + promo image card), mobile slide-out accordion. | ✅ |
| GLB-06 | Delivery district selector (drives delivery estimate & fee) | ✅ |
| GLB-07 | Multi-column footer: links, policies, social, newsletter | ✅ |
| GLB-08 | Floating WhatsApp button, site-wide | ✅ |
| GLB-09 | Fully responsive 320 → 1920 px | ✅ |
| GLB-10 | Content pages: About, Contact (form, hours, map, social), Returns, Shipping, Privacy, Terms | ✅ |
| GLB-11 | SEO: server-side rendering, meta/OG tags, sitemap.xml, robots.txt, friendly URLs | ✅ |
| GLB-12 | Accessibility (WCAG 2.2 AA), reduced-motion support | ✅ |

## B. Homepage (proposal §2.2, in this order)

| ID | Block | Scope |
|---|---|---|
| HOME-01 | Hero video banner (admin-uploaded video + poster; image on mobile / reduced motion) | ✅ |
| HOME-02 | Shop by Department: Women / Men / Kids | ✅ |
| HOME-03 | New Arrivals | ✅ |
| HOME-04 | Trending Items: each card opens the matching filtered listing (replaces demo "Campaign Split") | ✅ |
| HOME-05 | Full-width campaign banner | ✅ |
| HOME-06 | Full-width campaign feature below HOME-05. **Relabelled "Linen and denim" on 2026-10-01** — see *Decisions after baselining* below | ✅ |
| HOME-07 | Sale section | ✅ |
| HOME-08 | Social media gallery (admin-uploaded images linking to social profiles) | ✅ |
| HOME-09 | Customer benefits (delivery / returns / quality) | ✅ |
| HOME-10 | Newsletter signup (collects subscribers) | ✅ |

Every homepage block's content (images, video, text, links, which products show) is managed in admin.

## C. Department & listing pages

| ID | Feature | Scope |
|---|---|---|
| DEPT-01 | Department landing pages: Women, Men, Kids (Kids covers Girls, Boys, Baby) | ✅ |
| DEPT-02 | New Arrivals page | ✅ |
| DEPT-03 | Sale page | ✅ |
| PLP-01 | Category listing with breadcrumb, heading, description, promo banner | ✅ |
| PLP-02 | Filters: department, category, age group, price, size, colour, material, fit, occasion, rating, availability, discount. Desktop sidebar + mobile drawer. | ✅ |
| PLP-03 | Active filter chips + Clear filters | ✅ |
| PLP-04 | Sort: Recommended, Newest, Best Selling, Price ↑, Price ↓, Highest Rated, Biggest Discount | ✅ |
| PLP-05 | Responsive grid (2 → 3 → 4 → 5 columns) | ✅ |
| PLP-06 | Live product count + "No products found" state | ✅ |
| PLP-07 | Load More pagination | ✅ |
| CARD-01 | Product card: main + hover image, wishlist icon, badge, name, category, price / original / discount %, rating, colour dots, Quick View, Add to Bag | ✅ |
| QV-01 | Quick View modal | ✅ |

## D. Product detail page

| ID | Feature | Scope |
|---|---|---|
| PDP-01 | Image gallery: thumbnails, zoom, full-screen view, optional product video | ✅ |
| PDP-02 | Name, code, rating, price / original / discount, stock status | ✅ |
| PDP-03 | Colour + size selectors, quantity, Add to Bag (disabled until selected), Buy Now, Add to Wishlist | ✅ |
| PDP-04 | Size guide modal (admin-managed per category) | ✅ |
| PDP-05 | Delivery estimate + delivery methods, description, material, care, fit, returns info | ✅ |
| PDP-06 | Related products | ✅ |
| PDP-07 | Recently viewed products | ✅ |
| PDP-08 | Reviews & ratings: list, summary, write a review (logged-in customers; "Verified buyer" badge) | ✅ |

## E. Search, wishlist, bag

| ID | Feature | Scope |
|---|---|---|
| SRCH-01 | Search overlay: debounced suggestions (products, categories), popular & recent searches, keyboard navigation | ✅ |
| SRCH-02 | Search results page with filters/sort + no-results state | ✅ |
| WISH-01 | Wishlist: add/remove, count, move to bag, clear, empty state. Guests keep it in the browser; it merges into the account on login. | ✅ |
| CART-01 | Bag page: items with size/colour, quantity, remove, move to wishlist, subtotal / discount / delivery / total, promo code | ✅ |
| CART-02 | Slide-out mini bag | ✅ |

## F. Checkout, payments, account

| ID | Feature | Scope |
|---|---|---|
| CHK-01 | Checkout: customer details, delivery address (district), order summary, validation | ✅ |
| CHK-02 | Delivery methods: Standard / Express / Store pickup (admin sets fees, can switch each on/off, free above threshold) | ✅ |
| CHK-03 | Guest checkout | ✅ |
| CHK-04 | Order success page with order number | ✅ |
| PAY-01 | **Bank / online transfer:** bank details shown, order "Awaiting payment" until admin approves | ✅ (with checkout, week 3) |
| PAY-02 | **PayPal:** server-side capture + webhook auto-confirmation (cards accepted through PayPal) | ✅ (**built last**, week 4) |
| PAY-03 | Pluggable payment-provider design so the next gateway plugs in without touching checkout | ✅ |
| ACC-01 | Customer register / login / logout / forgot password | ✅ |
| ACC-02 | My account: profile, order history, live order status + courier tracking link | ✅ |
| ACC-03 | Emails: welcome, order placed, bank-transfer instructions, payment confirmed, shipped (with tracking), password reset | ✅ |

## G. Admin panel: everything is manageable, controlled by user access roles

**Sidebar:** Dashboard · Orders · Products · Categories · Inventory · Customers · Reviews · Promotions · Content · Media · Reports · Traffic · Users & Roles · Settings

| ID | Feature | Scope |
|---|---|---|
| ADM-01 | Admin login, forgot password, lockout after failed attempts | ✅ |
| ADM-02 | **Users & Roles:** create staff users, custom roles, per-module permissions (View / Create / Edit / Delete / Approve / Export). The sidebar and actions show only what the role allows, and the API enforces the same rules. | ✅ |
| ADM-03 | Audit log: who changed what, when | ✅ |
| ADM-04 | Dashboard: KPIs vs previous period · Revenue & Orders trend (weekly/monthly, 12 months) | ✅ |
| ADM-05 | Dashboard: Sales by Department · Order Status distribution · Payment Methods breakdown | ✅ |
| ADM-06 | Dashboard: Traffic Sources | ✅ |
| ADM-07 | Dashboard: Top Categories by revenue · Top Products with sales trend | ✅ |
| ADM-08 | Dashboard: searchable Recent Orders · Low Stock alerts · Customers by district | ✅ |
| ADM-09 | Orders: list/search/filter, detail, status workflow, **approve bank transfer**, courier + tracking number, cancel/refund status, print invoice | ✅ |
| ADM-10 | Products: add / edit / remove, images + **video upload**, variants (colour × size), price & original price, badges, material/fit/care/occasion, related products, SEO fields, publish/unpublish | ✅ |
| ADM-11 | Categories: **create / edit / reorder / remove** per department, category image, mega-menu placement | ✅ |
| ADM-12 | Inventory: stock per variant, bulk update, low-stock threshold | ✅ |
| ADM-13 | Promo codes: % / fixed / free delivery, min spend, validity dates, usage limits | ✅ |
| ADM-14 | Sale campaigns: discount on categories/products for a date range | ✅ |
| ADM-15 | Customers: list, detail, order history, district | ✅ |
| ADM-16 | Reviews: approve / hide | ✅ |
| ADM-17 | Content: homepage blocks (hero video, banners, trending items, activewear, sale, social gallery, benefits), announcement bar, mega-menu promo cards, size guides, content pages | ✅ |
| ADM-18 | Media library: upload & manage images and videos | ✅ |
| ADM-19 | Reports: Sales, Inventory, Customers, with CSV export | ✅ |
| ADM-20 | Traffic: visits, sources, top pages, devices | ✅ |
| ADM-21 | Newsletter subscribers: list + export | ✅ |
| ADM-22 | Settings: store info, contact/WhatsApp/social, bank details, delivery methods & fees, couriers + tracking URL templates, PayPal credentials, email (SMTP), order payment-expiry hours | ✅ |

**Default roles** (Super Admin can edit these or create new ones):

| Role | Access |
|---|---|
| Super Admin | Everything, including Users & Roles and Settings |
| Store Manager | Everything except Users & Roles, payment settings (PayPal credentials **and bank account details**) and security settings (email/SMTP, login security, legal retention) |
| Order Staff | Orders (incl. approve transfers, **not refunds**), Customers (view), Inventory (view) |
| Content Editor | Products, Categories, Content, Media, Reviews |

Security rules (from DB design review):
- Only a Super Admin can assign the Super Admin role or edit roles.
- No user can grant a permission they don't hold themselves, or manage a staff user who holds permissions they don't have.
- Refunds need a separate `Orders.Refund` permission.
- Payment settings (bank, PayPal), email/SMTP settings and legal-retention settings are Super Admin only. Every settings change is audited, and changes to those settings alert all Super Admins.
- Super Admins must use two-factor authentication. At least two Super Admin accounts are recommended.

## H. Platform & handover

| ID | Feature | Scope |
|---|---|---|
| OPS-01 | Hostinger KVM 2 setup, domain, SSL | ✅ |
| OPS-02 | Docker deployment, automated backups (database log backups every 15 min, shipped off-server), monitoring | ✅ |
| OPS-03 | Staging site (`staging.<domain>`) | ✅ |
| OPS-04 | Admin training + handover guide | ✅ |

---

## I. Removed from the demo

| Demo item | Why |
|---|---|
| Template switcher + alternative templates/themes | One theme only (Banana Republic) |
| TU@SL Fashion branding, maroon palette, tagline | Storefront is Spencer Denim |
| Max Fashion and Denim × Linen references | Replaced by Banana Republic |
| Homepage "Campaign Split" | Replaced by Trending Items (signed proposal) |
| Homepage "Trending Categories" and "Best Sellers" blocks | Removed in the signed proposal. Re-adding is chargeable. *(Assumption. Confirm if you meant to keep them.)* |
| Top-level Baby / Activewear / Footwear / Accessories nav | Nav is Women · Men · Kids · New Arrivals · Sale. These become categories. Baby sits under Kids. |
| Cash on Delivery, "Card payment demo" | Payments are Bank Transfer + PayPal (cards via PayPal) |
| Hard-coded promo codes `TUASL10` / `FREESHIP` | Admin-managed promo codes |
| "Demo: quick admin access" button | Security |
| Dummy catalogue, fake dashboard numbers, browser-only orders | Real database |

## I2. Decisions after baselining

Changes agreed with the owner after this document was baselined on 2026-09-15. Each one is recorded
here so that a feature that reads differently from the proposal is visibly a decision and not drift.

| Date | Decision | Why | By |
|---|---|---|---|
| 2026-10-01 | **HOME-06 is "Linen and denim", not "Activewear".** The heading, standfirst, button and link describe linen and denim separates, and the block opens `/women?material=linen&material=linen-blend&material=denim`. The feature ID, its position and its behaviour are unchanged; only the copy and the destination are. | The photograph the client delivered for this slot is a flat-lay of a white linen co-ord and a denim co-ord — nothing athletic. A block whose picture, words and destination disagree is worse than no block (issue #63), and the owner chose to retitle the slot rather than wait for an activewear photograph. The link is a listing that genuinely holds linen and denim rather than a department that happens to include them. | Owner |

> Activewear itself is unaffected: it remains a category under Women and under Men (DEPT-01), and it
> is reachable from the navigation. What changed is which of them this one homepage block is about.

## J. Future phases (not in this 1-month build)

- Second payment gateway (provider to be decided). The design is ready for it (PAY-03).
- Koko installments / BNPL
- Newsletter email campaigns, loyalty/rewards, mobile apps, multi-language/currency, courier API integrations
