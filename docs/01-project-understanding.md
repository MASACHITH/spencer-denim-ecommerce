# 01 — Project Understanding & Decisions

Last updated: 2026-09-15 · Status: **All kickoff questions answered.** New gaps found during the build are discussed before they are built.

## 1. Understanding

### Engagement
- **Client:** Manusha Weerawardene, Spencer Denim Industries Pvt Ltd. Sri Lankan family fashion retailer (Women, Men, Kids).
- **Vendor:** TU2SL, Kumarage Mawatha, Padukka.
- **Contract:** proposal TU2SL-2026-0823 (23 Aug 2026), LKR 80,000, paid 50/50. Hosting, domain and support are billed separately.
- **Demo:** the static prototype (`../tuasl-fashion/`) is the feature reference. **Every feature on the demo pages is kept**, plus the proposal's additions. Only the theme changes.
- **Theme:** Banana Republic (`bananarepublic.gap.com`) replaces Denim × Linen completely, across the whole project.
- **Brand on storefront:** Colombo Wardrobe (owner’s decision, 2026-10-02 — see `docs/02` §I2). The company is still Spencer Denim Industries Pvt Ltd; the shop a customer sees is Colombo Wardrobe at `colombowardrobecompany.com`.

### Hosting
Hostinger VPS KVM 2 (2 vCPU / 8 GB / 100 GB NVMe / 8 TB). Portable to Azure next year: Docker, config through environment variables, no host-specific code.

### Timeline
**One month from today: Tue 15 Sep → Thu 15 Oct 2026.** The internal plan and the client progress page use the same dates.

### Ways of working
.NET 10 + Angular + SQL Server in Docker · SOLID / clean architecture · developer agent → tester agent loop · responsive at 8 breakpoints · `TASKS.md` + `MEMORY.md` updated every session · separate simple client progress page.

## 2. Gaps & how they are handled

| # | Gap | Resolution |
|---|---|---|
| G1 | PayPal may not receive funds for a Sri Lankan account | PayPal is built **last** (week 4). The client verifies the account in the meantime. Payments sit behind a pluggable provider, so a second gateway (decided later) plugs in cleanly. |
| G2 | Proposal omits PDP, cart, search, wishlist, quick view, size guide, recently viewed | **All kept** (Q4). They were in the demo. |
| G3 | Emails not listed | Included (ACC-03). SMTP is configurable in admin Settings. |
| G4 | Traffic data source | Our own privacy-friendly tracking (page views, referrer, UTM, device) with bot filtering |
| G5 | Review abuse | Logged-in customers only, "Verified buyer" badge, admin can approve/hide |
| G6 | Delivery fees & methods | Standard / Express / Store pickup, fees and on/off set in admin, free-delivery threshold |
| G7 | Courier tracking | Admin-managed courier list with tracking-URL templates. Order stores courier + tracking number. |
| G8 | Social gallery | Admin-uploaded images that link to the social profiles |
| G9 | Policy/content pages | Included, editable in admin |
| G10 | Sri Lanka PDPA (Act No. 9 of 2022) | Privacy policy, cookie notice, customer data deletion on request |
| G11 | Timeline mismatch | Resolved: one month, to 15 Oct 2026 |
| G12 | Large scope for one month | Weekly plan with priorities ([05](05-delivery-plan.md)). Risks raised early. |
| G13 | Demo bugs (broken mobile images, raw template text) | Image pipeline + E2E visual checks at every breakpoint |
| G14 | Root `CLAUDE.md` outdated | Replaced with pointer to this project |
| G15 | No git repo | GitHub repository + GitHub Actions |
| G16 | "Admin can do everything" | Full admin management of catalogue, categories, media (images + video), content, settings, with **role-based access per user** (ADM-02) and an audit log |

## 3. Decisions (answers to kickoff questions)

| Q | Topic | Decision | Source |
|---|---|---|---|
| Q1 | Timeline | One month from 15 Sep → **15 Oct 2026** | User |
| Q2 | Theme | **Banana Republic**, full replacement | User |
| Q3 | Payments | **PayPal** integrated, as the last build step. Another gateway later (TBD). Bank transfer stays (signed scope). | User |
| Q4 | Demo features | **Keep everything** from the demo home and pages. Only the theme changes. | User |
| Q5 | Navigation | **Women · Men · Kids · New Arrivals · Sale** | User |
| Q6 | Delivery | Standard / Express / Store pickup, admin-configurable fees per method, free above threshold. No COD. | Recommendation |
| Q7 | Couriers | Admin-managed list + tracking URL templates (no courier APIs) | Recommendation |
| Q8 | Email | SMTP settings in admin. Suggest Brevo or Zoho Mail on the client's domain. | Recommendation |
| Q9 | Analytics | Own in-app tracking (feeds dashboard). GA4 optional later. | Recommendation |
| Q10 | Social gallery | Admin-uploaded images | Recommendation |
| Q11 | Repo & CI | GitHub + GitHub Actions | Recommendation |
| Q12 | Levi's screenshots | UX references: PDP thumbnail rail, slide-out bag, clean mobile grid | Recommendation |
| Q13 | Reviews | Logged-in customers, verified-buyer badge, admin moderation | Recommendation |
| Q14 | Admin users | Unlimited staff users with role-based permissions (4 default roles + custom) | User + recommendation |
| Q15 | Environments | Staging + production on the same VPS | Recommendation |
| Q16 | Product data | Client/staff enter products via admin. We seed departments, categories and sample products for testing. | Recommendation |

## 3b. Build defaults accepted 2026-09-16

The user accepted the recommended defaults for the open client items. We build with these; items marked *before go-live* still need the client's or accountant's real input, but they don't block development.

| Topic | Default we build with | Before go-live |
|---|---|---|
| Design review (style tile) | All 9 recommendations accepted. See [04](04-design-system.md) "Style tile review decisions". | Client supplies logo; may comment at staging preview |
| PayPal currency | Charge **USD** at an admin-set LKR→USD rate plus 3% buffer; show USD before redirect; refund captured USD; client bears FX ([08](08-database-design.md) D1) | Inform client |
| VAT | Treat as **not VAT-registered**: no VAT line on invoices, `TaxAmount = 0`. Gapless invoice numbers kept, so VAT can be switched on later ([08](08-database-design.md) D2) | Client's accountant confirms |
| Delivery | Placeholder zone rates (Colombo / Western / Island-wide), couriers and pickup locations seeded; all admin-editable ([08](08-database-design.md) D8) | Client supplies real fees, couriers, pickup locations |
| Slow PayPal payments (PENDING) | Hold the order and stock up to **5 days**; cut back if denied ([08](08-database-design.md) D13) | Inform client |

## 4. Assumptions to confirm when convenient

- **A1:** The homepage blocks "Trending Categories" and "Best Sellers" stay removed, as the signed proposal says (chargeable to re-add). The "Best Selling" sort option stays.
- **A2:** Product and hero videos are uploaded as MP4 (H.264), max 50 MB each. No server-side video conversion, to protect the VPS CPU.
