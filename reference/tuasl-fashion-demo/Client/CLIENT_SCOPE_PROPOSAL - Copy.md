# TU@SL Fashion — Template 1 Scope Proposal (Draft v1)

Internal draft for your review only. Once you confirm/correct this, it gets turned into the client-facing PDF. Items marked **[OPEN]** need your input before this is final.

---

## 1. Base Template & Theme

- **Base layout:** Template 1 — "Classic Boutique" (`index.html`). Client approved this structure.
- **Theme change:** Recolor away from the current maroon (`#8b1e3f`) toward a blend of two existing demo themes:
  - **Denim & Co.** (`index-v7.html`) — denim blue accent (`#2e6fad`), navy (`#192d47`)
  - **Natural Linen** (`index-v6.html`) — warm linen/beige base (`#f4f1ec` bg, `#c4a882` accent, `#7c5533` deep accent)
- **Approach:** Keep Classic Boutique's layout/blocks and fonts as-is — this is a **recolor only**, not a rebuild. Base neutrals from Natural Linen, denim blue used as the accent/CTA color instead of maroon.
- **[OPEN]** Do you want me to propose an exact hex palette for approval, or does the client have specific brand swatches to match?
- Noted as a **billable customization** (custom palette blend, not one of the 9 stock templates) — flagging so it's costed correctly, not for the client PDF wording.

## 2. Homepage Blocks (in display order)

| # | Block | Status |
|---|-------|--------|
| 1 | Hero (video banner) | Keep |
| 2 | Shop by Department | Keep — see change below |
| 3 | New Arrivals | Keep |
| 4 | ~~Campaign Split~~ → **Trending Items** block | Replaced — clicking a trending item card navigates to the matching listing page, which loads that filtered product listing |
| 5 | Trending Categories | **Removed from process** — superseded by the new Trending Items block at #4. Client's current plan only covers managing Women/Men/Kids; re-adding this later is a **chargeable** addition |
| 6 | Full-Width Campaign banner | Keep |
| 7 | Best Sellers | **Removed from process** — not part of this build. Re-adding this later is a **chargeable** addition |
| 8 | Activewear Campaign | Keep |
| 9 | Sale Section | Keep |
| 10 | Social Gallery | Keep |
| 11 | Customer Benefits (delivery/returns/quality) | Keep |
| 12 | Newsletter signup | Keep |

## 3. Shop by Department

- Change tile labels from **Women / Men / "Kids & Baby"** → **Women / Men / Kids** (3 clean tiles, drop the "& Baby" wording). Kids tile still links to the combined kids/baby catalogue underneath.

## 4. Listing / Category Pages — what's included

- Breadcrumb navigation
- Filter sidebar (desktop) + slide-in filter drawer (mobile), with active-filter chips
- Sort dropdown: Recommended, Newest, Best Selling, Price Low→High, Price High→Low, Highest Rated, Biggest Discount
- Responsive product grid (2 cols mobile → 5 cols large desktop)
- Live product count
- "No products found" empty state with a Clear Filters action
- Load More pagination

## 5. Customer Login / Account

- **[OPEN]** Not yet specified in our discussion — need to confirm what's required: guest checkout only, email/password registration, order history, saved addresses, social login, etc.

## 6. Payment Methods (this phase)

- **PayPal** — online card/PayPal payment, automatic confirmation
- **Bank / Online Transfer** — customer selects at checkout; **admin manually verifies** the transfer and marks the order as paid in the admin panel (no automatic confirmation)
- **Koko installment (BNPL)** — explicitly **excluded from this phase** per client ask. Deferred to Phase 2, billed separately when the client requests it.

## 7. Admin Panel — scope

- **Dashboard** — overview screen shown on login: sales summary (today/this month), order counts by status (pending / paid / processing), low-stock alerts, recent orders list
- **Reports** — Sales report, Inventory report, Customer report (no best-sellers report — that feature isn't part of this build)
- **Inventory management** — stock levels per product/variant
- **Listings management** — add/edit/remove products
- **Category management**
- **Sale / promotion management** — discounts, sale campaigns
- **Order management** — including manual approval of bank-transfer payments
- Admin login/auth (demo access button already exists on current build)

## 8. Next Steps

1. You review/correct this draft (especially the **[OPEN]** items above)
2. I update it based on your corrections
3. Once confirmed, I format it as a client-facing PDF

---
*Draft only — not yet sent to client.*
