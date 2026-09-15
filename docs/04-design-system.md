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

## 5. Admin panel

Same neutral tokens, denser layout, a readable data font size (14 px), and charts in a restrained palette. Function over flourish.

## 6. Deliverable before build

A one-page **style tile** (colours, type scale, buttons, product card, header, mobile) for client approval. Built as a static page, reviewed, then turned into Angular shared UI components.
