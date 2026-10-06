# 04 — Design System

Last updated: 2026-09-15 · Status: **Banana Republic confirmed as full replacement (Q2).** Tokens are final unless the style tile review changes them.

## 1. Reference

**https://bananarepublic.gap.com/** is the agreed theme reference for the whole project. It replaces "Classic Boutique × Denim & Co. × Natural Linen".

We take the **look and feel**: restraint, whitespace, editorial photography, typographic hierarchy. We do **not** copy its logo, fonts (proprietary), images, copy, product names or code. The Max Fashion reference from the prototype is retired.

## 2. Characteristics to carry over

| Aspect | Banana Republic pattern | Our rule |
|---|---|---|
| Palette | White / warm ivory ground, near-black text, almost no colour. Photography provides the colour. | Neutral base, black as the action colour, no brand accent colour. Sale red used sparingly. |
| Typography | Clean sans-serif, sentence case, small uppercase letter-spaced nav labels | One sans-serif family (*Inter*), small uppercase tracked labels for nav and buttons |
| Imagery | Full-bleed hero, large editorial category tiles, minimal text overlay | Every section is image-led. Consistent aspect ratios (hero 16:9 desktop / 4:5 mobile; product 3:4). |
| Buttons | Square corners, outline or solid black, generous padding | `border-radius: 0`. Primary = solid dark, secondary = outline. |
| Cards | No shadows, thin or no borders, product photo dominant | Flat cards. Hover shows the second image. |
| Layout | Generous whitespace, static (non-rotating) content blocks | Avoid auto-carousels. Scroll-snap rows on mobile. |
| Header | Promo bar → logo + departments + search/account/bag | Same structure. Sticky. Mobile hamburger drawer. |
| Tone | Sophisticated, calm, "timeless" | Short, confident copy |

Additional UX references (screenshots in `../tuasl-fashion/Bug/`, Levi's): PDP with vertical thumbnail rail, slide-out bag drawer, clean 2-column mobile grid with colour swatches. Confirmed as UX references (Q12).

**Feature rule:** every feature and page from the demo is kept. The theme is the only visual change ([02](02-scope-baseline.md)).

## 3. Draft tokens (for approval)

```css
:root {
  /* Neutrals */
  --color-bg:        #ffffff;
  --color-bg-alt:    #f7f5f1;   /* warm ivory section background */
  --color-surface:   #ffffff;
  --color-text:      #1a1a1a;
  --color-text-muted:#6b6b6b;
  --color-border:    #e3e0da;
  /* Action */
  --color-primary:   #1a1a1a;   /* buttons, links */
  --color-on-primary:#ffffff;
  --color-primary-hover: #3d3d3d;
  /* Status */
  --color-sale:      #b3261e;
  --color-success:   #2e7d4f;
  --color-warning:   #9a6700;
  /* Shape & rhythm */
  --radius: 0;
  --space-unit: 8px;
  --container-max: 1440px;
  --font-sans: "Inter", system-ui, -apple-system, "Segoe UI", sans-serif;
}
```

Contrast target: **WCAG 2.2 AA** for all text/background pairs (checked in CI with axe).

## 4. Responsive rules

Breakpoints: **320 · 375 · 390 · 768 · 1024 · 1280 · 1440 · 1920 px.** Mobile-first CSS.

| Element | Mobile (<768) | Tablet (768–1023) | Desktop (1024–1439) | Large (≥1440) |
|---|---|---|---|---|
| Product grid | 2 col | 3 col | 4 col | 5 col |
| Filters | Slide-in drawer | Drawer | Sidebar | Sidebar |
| Nav | Hamburger drawer | Hamburger drawer | Horizontal + dropdown | Horizontal + dropdown |
| Hero | Image (video optional, respects data-saver) | Video | Video | Video |
| Admin sidebar | Off-canvas | Collapsible | Fixed | Fixed |
| Admin tables | Card list | Scrollable table | Table | Table |

Every page must have: no horizontal scroll, touch targets ≥ 44 px, no clipped or overlapping text. `prefers-reduced-motion` disables animation and video autoplay.

### Rules confirmed by the style tile (2026-09-15)
- **Hero:** 4:5 on phones, 16:9 from 768 px, height capped at 880 px on very wide screens (≈ >1580 px). The image is cropped, not stretched. Pending client confirmation.
- **Focus ring:** 2.5 px token (`--focus-width`, draws 2 px after device-pixel snapping). On image-overlay buttons the ring may use a two-colour inset line so it stays visible on light and dark photos. This is the only permitted "shadow".
- **Global reset** must include `[hidden]{display:none !important}` (lesson L2).
- **Light colour swatches** (ecru, stone, sky, rose) get a `--color-border-strong` edge.
- **Carry into Angular components** (cosmetic findings from style tile QA r2):
  - Badges: `white-space: nowrap`, and shorten the label if needed. "LOW STOCK" wrapped at 320 px.
  - Quantity inputs: clamp to the maximum and recalculate totals live while typing, not only on blur.
  - Toasts: don't cover drawer action buttons on mobile, and announce them inside an open dialog for screen readers.

Reference implementation: [`design/style-tile/`](../design/style-tile/index.html).

### Style tile review decisions (accepted 2026-09-16, TU2SL defaults)
These answer the style tile's "For your review" list. They are **build defaults**: we build with them now, and the client can still request changes during the staging preview.

| # | Question | Decision |
|---|---|---|
| 1 | Logo | Text wordmark "Colombo Wardrobe" until the client sends usable logo artwork (SVG or transparent PNG preferred — what was delivered in W1-13 is opaque JPEGs on a mockup background, so the wordmark still draws as text). The logo is an admin setting (GLB-02), so swapping it needs no code change. **Client to supply.** |
| 2 | Typeface | **Inter throughout.** No separate headline typeface. |
| 3 | Round elements | **Keep** round colour swatches and radio buttons; everything else square. |
| 4 | Labels | **Uppercase**, letter-spaced small labels for buttons and navigation. |
| 5 | Phone product cards | **Small icon buttons on the photo** (Quick View, Add to Bag), as shown. Tapping the card opens the product page. |
| 6 | WhatsApp button | **WhatsApp green**, for instant recognition. It is the only brand colour exception, and needs a contrast-checked white icon. |
| 7 | Hero text | **Over the photo with a soft dark fade**, as shown. |
| 8 | Sample content | Placeholders: free delivery above Rs. 10,000, zone fees and announcement messages are all admin-editable. **Real values from the client before go-live** (D8). |
| 9 | Hero on very wide screens | **Keep the 880 px height cap** (16:9 up to ~1580 px, cropped not stretched). |

> **Decision (owner, 2026-10-06): the floating WhatsApp button owns the bottom-right corner, and a
> phone card's action buttons sit bottom-LEFT.** This reconciles decision 5 above with GLB-08, and
> it is written down because the two were never reconciled anywhere — which is how they came to
> collide in W2-04.
>
> **What went wrong.** Decision 5 puts Quick View and Add to Bag on the photograph; GLB-08 asks for
> a floating WhatsApp button, site-wide. Decision 6 above settles that button's *colour* and says
> nothing about where it goes, and `docs/02`'s GLB-08 line says only "site-wide" — so its corner
> was never a stated decision at all, only an implementation choice (`layout/whatsapp-button`:
> fixed, bottom-right, 56 px, `z-index: 70`). The card actions were then built into the same
> corner. Measured on the built storefront: at 390 × 844 a card's Add to Bag was **fully covered**
> at 4 of 41 sampled scroll offsets, and at 320 × 720 it happened at `scrollY 0` — so the first
> card a shopper saw had a dead button that opened WhatsApp instead. An undocumented decision is
> worse than a conflicting one: there was nothing for the second decision to be checked against.
>
> **The rule now.** The float keeps bottom-right, stated here rather than left to the stylesheet.
> A phone card's action pair takes the opposite corner, bottom-left, stacked. Neither decision is
> weakened and nothing is removed: the two owners of that corner become one, and the other corner
> was empty (the badge is top-left, the wishlist heart top-right — the heart does **not** move, it
> never collided).
>
> **Where the side changes.** At the existing 1024 px breakpoint, which is already where the
> stacked icon pair becomes a labelled bar: below it the pair is a 44 px column and has a side to
> choose, at and above it the bar spans the whole photo and has none. No new breakpoint was
> introduced for this.
>
> **The alternatives, and why not.** Raising the float clear of the cards moves a site-wide element
> to suit one page and only shifts the overlap up the card. Dropping the heart from phone cards
> removes a feature CARD-01 asks for. Hiding the float on listing pages contradicts GLB-08's
> "site-wide" in the one place a shopper is most likely to want to ask a question.
>
> **How to keep it true.** A stylesheet assertion cannot see this: the collision is about what
> receives a tap. `e2e/quick-view.e2e.ts` samples `document.elementFromPoint` at the centre of every
> card's Add to Bag across every 40 px scroll offset at 320 × 720 and 390 × 844, and requires the
> element hit to *be* the button. Anything else added to a viewport corner should be checked the
> same way before it lands.

## 5. Admin panel

Same neutral tokens, denser layout, a readable data font size (14 px), and charts in a restrained palette. Function over flourish.

## 6. Deliverable before build

A one-page **style tile** (colours, type scale, buttons, product card, header, mobile) for client approval. Built as a static page, reviewed, then turned into Angular shared UI components.
