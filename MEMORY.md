# MEMORY — Project Log

Running record of what was done, what was decided, and what's next. **Newest entry on top.** Read this first every session.

---

## Current state (keep this section up to date)

- **Phase:** Week 1 — Foundation & admin catalogue. Week 0 setup complete (scaffold + CI committed 2026-09-17). **First feature slice (admin login/RBAC foundation, W1-01) DONE 2026-09-21** after 3 developer↔tester fix rounds (all Major, all found by the tester, all fixed and re-verified). First real application code and passing tests now exist in the repo.
- **Deadline:** Thu **15 Oct 2026** (one month from 15 Sep). Today: 21 Sep — Week 1 runs 19–25 Sep, day 3, one of seven Week 1 tasks landed.
- **Scope:** [docs/02-scope-baseline.md](docs/02-scope-baseline.md). Every demo feature is kept, only the theme changes. Nav: Women · Men · Kids · New Arrivals · Sale. Admin can manage everything, with per-user role permissions.
- **Theme:** Banana Republic–inspired, full replacement → [docs/04](docs/04-design-system.md)
- **Stack (approved):** .NET 10 API + Angular 21 (SSR storefront + admin app) + SQL Server Express, Docker, Caddy, GitHub Actions. Hostinger KVM 2, Azure-portable. Scaffold is committed (`src/backend`, `src/frontend`); Docker packaging split out to **W0-15**, still deferred.
- **Payments:** Bank transfer in week 3. **PayPal last** (week 4). A second gateway comes later (TBD) through `IPaymentProvider`.
- **Repo:** https://github.com/MASACHITH/spencer-denim-ecommerce (user's **personal** account MASACHITH, branch `main`). Local git identity is set **per repo, per machine** (MASACHITH / sachithishara1@gmail.com) — see [docs/14](docs/14-multi-session-coordination.md). `destinity-sachithm` is the user's **office** account. **Never push this project with it, and never switch the active `gh` account to it without asking the user.** Before every push, confirm `gh auth status` shows MASACHITH as active. If it doesn't, ask the user before switching.
- **Multiple sessions/machines:** More than one Claude Code session now works on this repo, sometimes on different machines. Handoff protocol, machine readiness check and the known-machines log: [docs/14-multi-session-coordination.md](docs/14-multi-session-coordination.md). Referenced from the root `CLAUDE.md`.
- **Reference archive:** `reference/` holds read-only copies (taken 2026-09-16) of the old demo (`tuasl-fashion-demo/`), the client documents (signed proposal + theme reference) and the original prototype prompt. See `reference/README.md`. The originals remain outside the repo.
- **Blockers:** W0-05 Docker/WSL still paused (blocks W0-15). This machine (new, 2026-09-21) has no `gh` CLI, so it can't push yet — see doc 11.
- **Next:** **W2-01 (homepage, HOME-01..10) is with the tester** on `feature/W2-01-homepage` (worktree `worktrees/sd-home`): content schema, seeder, read-only content API, newsletter signup endpoint and the page itself. ADM-17's admin screens are **not** in it.
- **Next:** **W1-02 (catalogue foundation — schema + demo seeder + the read-only storefront catalogue API) is in the developer↔tester loop** on `feature/W1-02-catalogue-foundation` (worktree `worktrees/sd-catalog`): tester round 1 failed it on 3 major + 5 minor, round 2 on 2 major + 4 minor, all fixed; not pushed, not merged, not Done. Its ADM-11 admin screens are a separate task, **W1-12 (TODO)** — W1-02 no longer means "categories (create/edit/reorder)". W1-08 (Users & Roles CRUD), W1-09 (audit log viewer), W1-10 (2FA enrollment), W1-11 (route guard, SecurityStamp revalidation, forwarded-headers regression test) are the W1-01 follow-ups, tracked but not started; the user/architect should decide priority between those and W1-03 once W1-02 passes.
- **Pending (can't do from this machine):** CLAUDE.md requires `../client-progress/progress-data.js` to be updated whenever a feature status changes to Done. W1-01 slice 1 just did, but the `client-progress` source folder only exists on the `SCIENTER\sachithi` PC (see docs/11) — not here. Whichever session next runs on that machine should update the admin-auth/RBAC progress line and republish.
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
| 2026-09-21 | This repo is worked on by multiple Claude Code sessions/machines under one shared git identity; adopt a handoff protocol | A second session/machine started working on the project | User |

---

## Session log

### 2026-09-24 (4) — W2-01 homepage, developer pass

The placeholder home page is gone. `/` is a real, server-rendered page on
`feature/W2-01-homepage` (worktree `worktrees/sd-home`), built on the product-page branch. Not
pushed, not merged, not Done: it goes to the tester next.

**The page has no opinion about what is on it.** Ten section types, every one of them a row in
`content.HomeSections` that the client will edit in ADM-17: which blocks appear, in what order,
with what heading, for how long. The page renders the list the API sends, and a section that
arrives with nothing in it renders **nothing at all** — no frame, no heading over a gap, no
"coming soon". `hasContent` is one pure function with one test over all ten types, rather than ten
`@if`s that have to agree.

**The decision worth recording: a product row is resolved server-side, from its own listing URL.**
`SettingsJson` carries a storefront listing URL (`/new-arrivals`, `/sale`, `/?sort=best-selling`),
`ListingUrl.ToCriteria` turns it into the criteria the catalogue read store already takes, and the
same URL is the section's "view all". So the row *is* the head of the listing it links to, by
construction — they cannot drift apart, and an integration test asserts it against the catalogue
endpoint itself. Verified live: the homepage's New Arrivals row and `/new-arrivals` both show the
same two products. The alternative — the page fetching each row — would have put three HTTP round
trips inside the render of the page a crawler reads, and made the row and its link two definitions
of one listing. Cost: three sequential SQL queries in one request (sequential because they share a
DbContext).

**Four sections are seeded switched off**, per doc 08 §9's "media-dependent sections disabled until
content exists": the hero video, the campaign banner, the activewear feature and the social
gallery are nothing but artwork and W1-04 has not landed. All four are built against the real
shape and exercised in the browser through a stub that is deliberately **richer** than the seed —
mirroring the seed would have left them untested in the one place their behaviour can be seen.

**HOME-04 is the one place the brief and doc 08 disagree, and doc 08 wins.** The brief listed
Trending among the product-backed rows; doc 08 §3.7 says "trending items store a filtered listing
URL" and scope HOME-04 says "each card opens the matching filtered listing", which is a row of
tiles, not of products. Both modes are built: `mode: manual` renders the client's tiles, each
opening its listing, and that is what HOME-04 means. It is **seeded `auto`** anyway, because §9
pins the seed at six items and a tile needs artwork — so the row works on day one and becomes the
tile row the moment the client uploads pictures.

**HOME-10 really subscribes.** `marketing.NewsletterSubscribers` is specified in doc 08 §3.6, so
the minimal endpoint was built rather than the form being disabled with an explanation. Single
opt-in, which is the shape that table has (two statuses, no "pending"); a double opt-in needs the
§3.8 outbox. The answer is the same sentence whether or not the address was already on the list,
because a form that said otherwise is a way to test whether someone shops here. Rate-limited per
caller. Verified live end to end: a row with status Subscribed, source `homepage`, the real socket
peer as the consent IP, and an unsubscribe token.

**Three defects the browser found and jsdom could not:**
- **`routerLink` given a whole URL as one command percent-encodes the `?`.** A trending tile
  pointing at `/women/jeans?fit=wide-leg` landed on a path literally called "jeans?fit=wide-leg" —
  a 404 dressed up as a filter, on the one feature whose entire point is that the card opens the
  matching filtered listing. `toLink` now splits the query off and hands it over as `queryParams`,
  repeatable filters included. The announcement bar had the same hole.
- **The gallery's inset focus ring was no ring at all** from outside the photograph. `edgesWithoutRing`
  reported all four edges, correctly: the cell was clipping its overflow, so the first version went
  inset — which is what lesson L4 is about. `object-fit: cover` already crops the square, so the
  clip was never needed and the ring is an ordinary outward one now.
- **A container section flush against the banner below it lost the focus ring on its last row.**
  The full-bleed blocks are `position: relative`, so they paint above the static content before
  them. Measured on a trending tile at 1280, bottom edge. Fixed with real bottom spacing, which the
  layout wanted anyway.

**Two shared tests were pointed somewhere they could no longer mean what they said**, and were
corrected rather than worked around: the shell's touch-target sweep took every `a[href]` on `/`,
which was fine while `/` was a placeholder and is not now that a product card's stretched link is
on it — it measures the shell's own controls, and `home-page.e2e.ts` proves the stretched link's
target really is the card; and the listing's "not the listing" assertion read `.product-grid`,
which the homepage now uses too, so it asks about the component.

**What the API and the data could not serve, and was therefore not invented:**
- **No media at all (W1-04).** Department tiles render without photographs — a named panel that
  opens the department is usable, unlike an empty banner frame — and the four artwork sections stay
  off the page.
- **New Arrivals is two products on the seeded catalogue**, because `Catalog.NewArrivalDays` is 30
  and only two of the 45 demo products are still inside their window. Thin, and honest: it is
  exactly what `/new-arrivals` shows.
- **There is no "everything" listing route**, so the trending row has no "view all" to offer and
  renders none rather than a link to the home page.
- The benefits copy and the two announcement lines are **placeholders** the client replaces before
  go-live (docs/04 §4 decision 8, D8). None of them states a figure the system has not been
  configured with.

**Numbers:** backend 348 unit (was 232) + 204 integration (146), 0 skipped; storefront unit 431
(361), shared 19, admin 9; Playwright 183 (128), three consecutive clean runs. Lint clean, all
builds clean, no new runtime dependency.

**Next:** the tester.

### 2026-09-24 (3) — W2-05 fix round 2: the same blindness, one layer down

The tester re-implemented round 1's `edgesWithoutRing` himself, proved it falsifiable and
deterministic over 10 controls at all eight breakpoints, and then found that **the fix I made
alongside it had exactly the fault the round was about.**

**Major: the sold-out slash was invisible on 12 of the 16 seeded colourways.** It painted in
`--color-text-muted` whatever the swatch under it was — the dot has no text, so that `color` only
fed the gradient. Measured against WCAG 2.2 AA 1.4.11's 3:1: Olive Green **1.08:1**, Deep Teal
1.16, Indigo Rinse 1.30, Plum 1.31, Terracotta 1.46, Charcoal 1.59, Mid Wash Blue 1.68, Deep
Maroon 1.74, Chocolate 1.80, Mustard 2.46, Blush Pink 2.59, Deep Navy 2.68. Only Sandstone, True
Black, Coconut Cream and Optic White passed. So the size buttons got a real third state and the
swatches got a mark that mostly was not there.

**And my test made the same mistake the ring test used to**: it asserted `::after { content }` was
not `"none"` — that the mark was *specified* — on a single stub colourway that was itself one of
the invisible cases. **Specified is not visible. That is twice now in one task**, so it is worth
stating as a rule: *an assertion that reads back a declaration is testing the stylesheet. If the
finding is "a shopper cannot see this", the test has to measure what reaches the screen.*

The new test reads the colour Chromium resolved for the slash and for the dot beneath it and
computes the real ratio, over all sixteen seeded colourways (the e2e stub carries the full palette
now, all sold out on one product). Proven red first, naming the same twelve with the same ratios.

**The fix is a measurement, not a threshold.** `core/platform/contrast.ts` picks between
`--color-text` and `--color-on-primary` by measuring both against the swatch. Choosing the better
of two inks at opposite ends of the scale has a floor — the crossover where both score alike —
which for these two is **4.17:1** at luminance 0.2017. I first wrote 4.58, which is the figure for
*pure* black and white; the dark ink is `#1a1a1a`. The spec walks the whole colour cube and pins
the real number, so an admin's next colour cannot fall through. Worst across the seeded palette is
now 4.75:1 (Terracotta). `isLight()` is deliberately not reused — it answers "does this need a
border against white?", and its 0.8 threshold calls Mustard and Blush Pink dark, where white scores
2.17:1 and 2.06:1.

**Minor, and the same shape again: the ring test was aimed at the one control that could not
fail.** The quantity group is three controls edge to edge, so each one's right-hand ring lands
under the next sibling; `FOCUSABLE` listed `.qty__step:not(:disabled)` and `.first()` resolved to
the **plus** — the last child, the only one whose ring cannot be covered. The box and the minus
both lost `right` at all eight breakpoints. All three are listed now, with the page put into the
state where the minus is live.

**Minors** — `scroll-padding-top` on `html`, because the browser scrolls a focused element to the
top of the viewport and the header is sticky (118 px of the zoom button sat behind it); the size
guide's dead CSS moved to a shared `button.link` rather than being deleted, since the markup stays
for issue #32 and deleting the styling would have left it unstyled the day the API serves content;
one more pre-hydration flake (reading `document.body` the instant a replayed click resolves).

**The 4-vs-8 sub-44 px link count is settled**: 4 on the stub's profile, 7 on the real catalogue, 9
with a recently-viewed row on screen. The invariants the test asserts hold in all three.

**Numbers:** storefront unit 361 (round 1: 333), Playwright 121 (117), shared 19, admin 9; backend
232 + 146, 0 skipped. Lint clean, builds clean, four consecutive clean Playwright runs.

**Next:** back to the tester.

### 2026-09-24 (2) — W2-05 fix round 1: a test that could not fail

The tester verified every number and every claim from the developer pass and failed the branch on
two majors and five minors. All fixed on `feature/W2-05-product-page`.

**Major, and the lesson of the round: the thumbnail focus ring was clipped, and the test was
structurally incapable of noticing.** The rail scrolls, so its padding box is the clip edge, and
the thumbnails sat flush against it; a ring is drawn *outside* the border box, so three of its four
sides were cropped and a 2 px sliver along the top survived. Lesson L4 again. The assertion read
`getComputedStyle(element).outlineWidth` — the outline the element *would* paint — which no
ancestor's clipping can ever change, so it passed on a ring three quarters invisible. It now
screenshots the strip outside each edge with the control focused and again blurred and fails on any
edge whose pixels come back identical. **Proven red on the old CSS before the fix** (all four edges
at 1280 and at 375), green after, green for the other six controls throughout.

Two of its first reds were the helper *refusing to measure* rather than passing quietly — a control
flush against the viewport edge, and the zoom button's top strip covered by the sticky header at
y=118 — which is the behaviour worth having. Both were answered by the viewport the test sets, not
by loosening the assertion. **Rule: an accessibility assertion that reads a computed style is
testing the stylesheet, not the screen. Measure the pixels, and make the unmeasurable case throw.**

**Major: a sold-out size or colour looked exactly like one in stock.** The CSS distinguished only
`:disabled`, bound to "not made in this colourway"; a variant that is made with nothing left got no
treatment at all. Measured on the live API: sizes 26 and 28 of `wide-leg-rinse-jeans` were
byte-identical to 30/32/34, and both swatches of `sleeveless-column-dress` drew the in-stock
border. The screen reader was told and the sighted shopper was not. Three states now, the sold-out
one marked with a diagonal slash rather than a colour change (WCAG 1.4.1), inset inside the border
box so it can never reach the focus ring.

**Minors** — the quantity box kept showing `0`, an empty string or `1.7` whenever the typed value
clamped to the number the binding already held (`[value]` writes nothing when the value is
unchanged), so the clamped number is written back to the element; with no size chosen the page
quoted "3 × Rs. 5,990 = Rs. 17,970" under a price line reading "From", and offered a ceiling of 138
— the whole colourway's stock — so the ceiling is 1 and the note asks for a size until a variant is
settled; the touch-target test claimed "every control" while excluding every link, and now says
what it measures, with the links checked separately against the exemption each one actually stands
on (a stretched card link whose target is the card, or a breadcrumb crumb at 44 px tall and over
the AA 24 px minimum); `role="tabpanel"` no longer renders without a tab list.

**The size-guide trigger is gone until issue #32.** `SizeGuideRefDto` is `(id, name)` and the API
projects none of the content in `catalog.SizeGuides`, so the button opened a dialog to say there
was nothing to see. `SizeGuideContentDto` now writes down the shape the API has to send,
`hasSizeGuideContent` is the single condition the trigger and the modal hang on, and a test proves
both come back the moment content arrives. `SizeGuideModal` stays in the tree.

**The stub was still shaped more politely than the API** — everything in stock, everything
discounted, every product with a size guide — so those branches never reached a browser. Stock and
discount are derived from the variant grid now, every eleventh product is wholly out of stock and
every fourth has no size guide. **Same lesson as W2-03 round 3 and as the description bug: a stub
that is tidier than the real API tests nothing.**

`Modal` also got its own spec (14 cases), because the only cover it had was the size-guide tests
that just went away; and the rating row joined the price block in the shared stylesheet rather than
being copied into two components.

**Numbers:** storefront unit 333 (round 1: 312), Playwright 117 (112), shared 19, admin 9; backend
232 + 146, 0 skipped. Lint clean, builds clean with no budget warnings.

**Next:** back to the tester.

### 2026-09-24 (1) — W2-05 product detail page, developer pass

`/product/:slug` is a real, server-rendered page on `feature/W2-05-product-page` (worktree
`worktrees/sd-pdp`), built on the listing branch. Not pushed, not merged, not Done: it goes to the
tester next.

**The decision worth recording: the colourway and the size live in the query string.** The rule
was "shareable and server-renderable if it changes what is shown", and both do. A colour changes
the *photographs* — `catalog.ProductMedia.ProductColourId` ties each gallery shot to one colourway
and the demo seeder gives every colour its own, so `?colour=47` and `?colour=48` are different
pictures. A size changes the *money* — `ProductVariants.PriceOverride` is real and seeded, and
Washed Linen Camp Shirt is Rs. 5,990 in XL and Rs. 6,290 in XXL. The **canonical** address carries
neither, so the colourways stay one page in an index rather than one URL per colour.

**Three failures, three answers.** A slug the catalogue does not know (404) and a slug the API will
not accept (400) are the same thing to a shopper, so both render the site's own not-found page at
a real HTTP 404. A store that answers badly is different — the product probably exists — so that
is a "try again" page and **503 with `Retry-After`**, which a crawler retries where it drops a 404.
Same reasoning as the fallback-tree note filed against ADM-11 last session.

**What the API cannot serve, and was therefore not invented:**
- **PDP-04 has no size-guide content.** `catalog.SizeGuides` holds `IntroHtml`, `TableJson`,
  `MeasuringTipsHtml` and an image, and `CatalogReferenceDataSeeder` fills all four — but
  `SizeGuideRefDto` is `(int Id, string Name)` and no endpoint serves the rest. The modal is built
  against the real shape and says plainly that the chart is not published yet, pointing at the
  WhatsApp button. **The fix is an API change, not a component change**: project the guide's
  content onto `SizeGuideRefDto` or add `GET /api/catalog/size-guides/{id}`. Filed for W1-02/ADM-17.
- **PDP-05's delivery estimate, delivery methods and returns terms have no source at all.**
  Delivery methods and fees are ADM-22/CHK-02 settings and the returns copy is a GLB-10 content
  page, both week 3. Nothing was written: "2–3 working days" on a storefront is a promise no part
  of the system has made yet. The description, material, fit, occasion, age group and care are all
  from the API and all render.
- No seeded product has a **video**. The gallery handles one (role `Video`, poster, its own slide);
  the e2e stub is what exercises it.
- **Media is still not served (W1-04)**, so every `<img>` on the page 404s today. Every frame is a
  fixed 3:4 box and a failed image swaps for a placeholder inside it, so the layout is identical
  either way and nothing moves.

**Two defects that only clicking through the real API found, after the suites were green:**
- `Products.Description` is **sanitised HTML** (doc 08 §3.2) and the seeder writes `<p>…</p>` into
  it. Interpolating it printed the tags at the shopper. It goes through `[innerHTML]` now (Angular
  sanitises it again), with the markup stripped out of anything copied into a `<meta>`. The e2e
  stub served a plain sentence in that field, which is exactly why no test saw it — **a stub that
  is shaped more politely than the real API tests nothing**, the same lesson as round 3 of W2-03 in
  a different coat. The stub serves HTML there now.
- A not-found page re-asked the API after hydration and collected a second, guaranteed 404 in the
  console. A missing product is handed over as missing; a *fault* still is not, because a 500
  during the render may be over by the time the page hydrates.

**Two the browser tests found, both in the new modal:** the full-screen panel was a grid item at
`height: 100%` and a grid track sizes to its content, so a tall photograph pushed the zoom controls
off the bottom of the screen; and the focus trap never armed, because the `@if` and the view query
settle in the same render and the effect read only `open()`, so it ran once while the panel was
still undefined and gave up. Both fixed. A third was in a test, not the code: a helper looked for
`[role="dialog"]` and found the shell's permanently mounted, hidden mobile nav drawer.

**Deliberately absent:** Add to Bag and Buy Now (bag API is W2-09), the real wishlist (W2-08 — the
heart is the same per-browser `LocalWishlist` the card uses), reviews (PDP-08, W2-06) and Quick
View (QV-01, W2-04). Nothing dead or disabled stands in for any of them.

**Numbers:** storefront unit 312 (was 220), Playwright 112 (was 65), shared 19, admin 9; backend
232 + 146, 0 skipped. Lint and all builds clean, no new runtime dependency.

**Next:** the tester.

### 2026-09-23 (5) — W2-03 fix round 3: a blocker the tests were built to miss

Round-2 work verified on the wire. The tester then found a blocker that had been on the branch
since round 1 and that he had missed twice himself, because the tests could not see it.

**Blocker — a shared, bookmarked or reloaded deep listing rendered duplicates.**
`toRestoreRequests` shrank its last request to the rows still wanted, but the API's offset is
`(Page - 1) * PageSize` against *that request's own* page size (`ProductListQuery.Skip`), so a
smaller last page moved its window backwards instead of trimming it. `?pages=5` asked for rows
25–48 where it meant 97–120. Measured on a 399-product listing: `?pages=5` rendered 120 cards of
which 96 were distinct, `?pages=17` said "Showing 408 of 399". Only the restore path — which is the
entire reason `pages` is in the URL — and only when the page count was not a multiple of four.

One page size for the whole restore now, `min(wanted, MaxPageSize)` rather than always the cap: a
fresh listing wants 24 rows, and asking for 96 would fetch four times what it shows on the most
common page view in the storefront. Verified by hand at 1, 2, 4, 5, 6, 7, 17 and 20 pages: every
one distinct, every count line honest.

**The test lesson is the real finding.** `flushAll` answered every request with the rows the
component *expected* — 96 at a time, numbered from the request's index — regardless of the `page`
and `pageSize` actually sent, so it could only ever confirm the component's arithmetic against
itself. Worse, a unit test asserted the page sizes summed to exactly the rows wanted, which is not
a property of a correct restore: it is what *forced* the defect. The stub now answers from the
request, and the test asserts the honest property — the windows tile the listing with no gap and no
repeat. Both changes fail on the old code (5 tests red). **Lesson: a stub that fabricates what the
caller expected tests nothing; answer from the request, and assert a property the implementation
could not have chosen for itself.**

**Minors** — `usable()` now requires a department with a non-empty string `slug`, so
`[{"nope":1}]` falls back too; "Wishlist, 1 items" pluralises; the flaky ceiling test was
root-caused rather than guessed at a third time (see below).

**The ceiling test flake, measured.** The guess was that the `activated()` helper's polling loop
ran up to fifty change-detection passes. It does not: instrumenting it showed activation completes
on the first attempt every time, across three full runs. The real cost is the test itself —
~30 ms to navigate and **~650 ms to render 480 product cards** in jsdom, which is work the
assertion genuinely needs, against vitest's 5 s default. A machine running the backend suite
alongside ate that seven-fold margin twice in thirty-two runs. The polling loop is gone anyway
(it was the wrong shape and a latent hazard), and the three tests that render hundreds of cards
carry a stated 20 s budget with the measurement written next to it.

**Numbers:** 220 frontend unit tests (round 2: 212), 65 Playwright tests (60), backend 232 + 146,
0 skipped.

**Operational note — the `SeoTitle` fix does not reach already-seeded databases.** The seeder skips
a department whose slug exists, which is the right policy for an admin-editable field, so any
database seeded before this branch still reads `Kids's clothing`. That is every developer's
`SpencerDenim_Dev` (all three worktrees) and any seeded staging. Integration tests build a fresh
database per run and are unaffected. The one-off correction:

```sql
UPDATE catalog.Departments
SET    SeoTitle = N'Kids'' clothing | Spencer Denim'
WHERE  Slug = 'kids' AND SeoTitle = N'Kids''s clothing | Spencer Denim';
```

**Filed against ADM-11, not built:** when the departments feed is unhealthy the storefront falls
back to the seeded tree, so a category that exists only in the live catalogue 404s with `noindex`.
Nothing can diverge today — the fallback is an exact copy of the seed and ADM-11 is not built — but
once admin can add categories, `tree$` should say whether the tree is live or fallback and the
missing-page route should answer **503 with `Retry-After`** rather than 404 + noindex while it is
the fallback: a crawler retries a 503 and keeps the URL, it drops a 404.

**Next:** back to the tester.

### 2026-09-23 (4) — W2-03 fix round 2: one regression the guards introduced

The tester verified every round-1 fix as genuinely fixed and **backed the 404 reasoning** — he read
the Angular source and the wire himself and confirmed `ServerRoute.status` matches by path pattern,
so it would have 404'd every category page. He failed the branch on one regression.

**Major, a regression from round 1.** `usable()` guarded only the `departments` signal; the route
guards read `tree$`, the raw pipeline, and `catchError` does not catch a *successful* response with
a useless body. `200 []` — what the API legitimately answers for a freshly migrated database
before anyone runs the seeder, and the README says seeding is off by default — sent every listing
URL to the home page while the mega menu still listed all three departments and all 29 categories.
The first boot of a new staging or production environment would have looked like that. `200
{"oops":true}` threw inside a guard and served Express's own 144-byte error page on every listing
URL. `map(usable)` now sits at the end of `tree$`, so the menu and the routes cannot disagree by
construction; the redundant guard on the signal is gone, and `trailTo` no longer trusts that a
department carries a `categories` array. Verified against both shapes: 206 kB pages with real
headings instead of a redirect and an error page. **Lesson: the defensive read has to sit where
every reader passes, not on the one reader you were looking at.**

**Minors** — the test guarding the Load More blocker was flaky (1 run in 14 on a loaded machine)
because `canMatch` puts an async hop before activation; it now waits for the router to put a
component on screen, and *not* by polling `HttpTestingController.match`, which takes the requests it
matches off the queue. 16 consecutive clean runs with the backend suite running alongside. The
canonical tag leaked across client-side navigations (home inherited the listing's) — it moved to a
`CanonicalLink` service the page clears on destroy. `/sale` and `/new-arrivals` showed waist sizes
28–36 twice, under "Women waist" and "Men waist", bound to the same value: two checkboxes with one
accessible name that ticked each other; with no department they are one "Waist" group now.
`CatalogReferenceDataSeeder` built `SeoTitle` as `$"{Name}'s clothing"`, which read
**"Kids's clothing"** on every Kids listing — the title is seed data now, not a string operation.
That last one touches the catalogue branch's data and is flagged for the PR.

**Left as decided, not built:** a mistyped department redirecting home while an unknown category
404s (`/bag` and the footer paths share that `**` route until GLB-10 lands); the 1.77 MB document at
the Load More ceiling; `/wishlist` redirecting home until W2-08.

**Numbers:** 212 frontend unit tests (round 1: 203), 60 Playwright tests (57), backend 232 + 146,
0 skipped. Lint and all builds clean.

**Next:** back to the tester.

### 2026-09-23 (3) — W2-03 product listing: tester FAILED it, developer fix round 1

The tester rejected the branch on 2 Blockers, 4 Majors and a set of Minors. All fixed on
`feature/W2-03-product-listing`.

**Blockers**
- **Load More past the ceiling collapsed the listing.** `readLoadedPages` dropped an out-of-range
  value back to 1, and the button was still offered at the cap, so one more click took a listing of
  480 products down to 24. The count is now clamped at both ends and the button is hidden at the
  ceiling, with a line saying why.
- **`/:department` swallowed every single-segment path.** Eleven live header and footer links —
  `/bag`, `/account`, `/wishlist`, `/contact`, `/privacy` and the rest — rendered the listing's
  not-found state at HTTP 200, telling the shopper a *department* was missing when they had clicked
  "Bag". A `canMatch` guard now accepts only a slug the department tree knows, so everything else
  falls through to `**` and the home page as before.

**Majors**
- `/new-arrivals` and `/sale` are real listings now (DEPT-02/DEPT-03 happy path), each with its own
  heading, title and meta description, so no permanent navigation entry points at a missing page.
- An unknown category (`/women/shirts`, a Men's category) rendered as the department page with
  "0 products". It is now a genuine **404** — status set from the component through `RESPONSE_INIT`,
  because a server route's `status` is matched by path and no pattern can express "a category this
  department does not have". `noindex` with it, and listings now carry a relative `<link rel="canonical">`.
- Angular's HTTP transfer cache was still on and serialising every SSR response into the page under
  a key the browser cannot use — 23.8 kB of dead JSON on a department listing, each entry carrying
  `API_INTERNAL_URL`. Off (`withNoHttpTransferCache`); `ServerHandover` already hands the two
  catalogue calls over explicitly.
- The department feed had `catchError` but no timeout, so a 12 s API gave every page a 12 s TTFB.
  2 s, then the seeded fallback.
- The wishlist heart saved the product and left the header reading "Wishlist, 0 items". Wired, and
  taken up only after the first render so hydration is not asked to grow the header.

**Minors:** a reversed price range drops both bounds (no chip that contradicts the URL); no products
request at all on a 404; literal font sizes replaced with the type-scale tokens; the filter panel is
rendered once instead of once per container (202 inputs in the DOM, doubled in the HTML); sizes are
grouped into real `fieldset`/`legend` scales and only the scales a department stocks; a card's
colour summary names the family ("Ecru (Cream)"); README §4 documents that the built SSR server does
not proxy `/api`.

**Numbers:** 203 frontend unit tests (was 174), 57 Playwright tests (was 42), backend unchanged at
232 + 146, 0 skipped. Lint and all four builds clean.

**Still open, filed rather than built:** the facets endpoint (the filter option lists are still a
copy of the seed data, and the API cannot filter a size by scale), and the
interceptor/transfer-cache ordering in the shared SSR setup.

**Next:** back to the tester.

### 2026-09-23 (2) — W1-02 fix round 2: tester verified round 1, failed it on two more Majors

The tester confirmed all nine round-1 fixes as real and rejected the branch again.

**Fixed**
- **Major, a regression from round 1's relevance fix.** Moving `SoldQty30d` out of the additive score into its own `ThenByDescending` is what stops popularity crossing a band — keeping the cap of 9 as well made the key inert: every product selling 9+ clamped to the same value, so the real tie-break became `ThenBy(Id)` and `?q=denim` served 45 sales above 51. Cap deleted, the key is the raw column. Lesson: two changes that each solve the same problem, made in one step, and only one of them was needed.
- **Major, pre-existing and missed in round 1.** `IsActive` was honoured in the menu and in filter resolution but not in the product query, so deactivating a category left its products in the listing and in search with a breadcrumb into a category that answered nothing. A product is now on the storefront only while its **department and primary category** are active; secondary category links do not keep it visible. Written into doc 08 §7.1.
- **Minor** — the product slug rule moved to `Domain.Catalog.CatalogSlug` and guards `Product.Slug` on write, so a slug the PDP refuses cannot be persisted through the app (the matching `CK_Products_Slug` is a **W1-03** migration, on the board); the category-ambiguity guard now fires on the match count alone, since two categories under different parents of one department can share a slug and naming the department does not tell them apart; `MEMORY.md`'s Current state corrected; the seeder determinism test draws the real variant count instead of 15.

**Numbers:** build clean, 232 unit + 146 integration green, 0 skipped (round 1: 215 + 141).

**Next:** back to the tester.

### 2026-09-23 (1) — W1-02 catalogue foundation: tester FAILED it, developer fix round 1

**Context:** `feature/W1-02-catalogue-foundation` (worktree `worktrees/sd-catalog`) delivers the catalogue schema + demo seeder + the read-only storefront catalogue API. An independent tester rejected it with 3 major and 5 minor findings plus a board correction. All fixed on the branch; not pushed, no PR.

**Fixed**
- **Major — 500 on an unparseable query parameter.** `ProductListRequest` bound `int?`/`decimal?`/`bool?` straight from the query string, so `?page=abc`, `?minPrice=abc`, `?inStock=yes` and friends threw in model binding: a 500 with an ERROR log on a public endpoint. Those parameters bind as `string?` now and are parsed in `ProductListQuery.Create` with `InvariantCulture`, so an unreadable value is the same 400 naming the parameter that an out-of-range value already got. Flags accept `true`/`false` and `1`/`0`, and nothing else — a typo is named, not silently read as false.
- **Major — the "deterministic" seeder was not.** `DemoCatalogueSeeder` seeded its generator from `string.GetHashCode`, which .NET randomises per process, so seeded stock differed run to run (≈3% of CI runs failed on the partly-sold-out product) and `Math.Abs(int.MinValue)` could throw. The seed is now the first four bytes of the product code's SHA-256 digest, read unsigned — the same hash `DeterministicGuid` already used.
- **Major — an ambiguous `category` slug served one department silently.** Slugs are unique per `(DepartmentId, ParentCategoryId)` (doc 08 §3.2), so `?category=jeans` with no `department` matched Women's and Men's jeans and returned only the first. It is a 400 now, naming the departments to choose from. One match still works with no `department`.
- **Minor** — the product slug is pattern-validated (a malformed slug is a 400, and only the validated slug is echoed into the 404); all four catalogue endpoints answer HEAD as well as GET; `ProductListQuery.ToCacheKey` deleted with its self-comparing test (caching and facets belong to the listing-page task); search relevance rewritten to doc 08 §7.6's actual bands with the `SoldQty30d` term bounded to 9 so it can only break ties.
- **Minor** — doc 08 §1.6's permitted-cascade list now names `ProductStats`, `ProductSearchIndex` and `ProductColours` (all three were already in the model and the migration; only the first two were in the tester's report). No new migration. The model test now asserts the list and the model match **both ways**.
- **Board** — W1-02 was titled "Departments & categories (create/edit/reorder) — ADM-11", an admin write feature this branch never touched. Retitled to what it delivers; ADM-11's admin screens are now **W1-12 (TODO)**.

**Numbers:** build clean, 215 unit + 141 integration tests green, 0 skipped (baseline before the fixes: 180 + 111).

**Next:** back to the tester agent. Facet counts and the 60 s response cache of doc 08 §7.5 are deliberately **not** in this slice.

### 2026-09-21 (1) — Multi-session coordination, status review, W1-01 started
**Context:** first session on a **new machine** (global git identity `nuwank`/scienter.lk — different from the `SCIENTER\sachithi` PC used since 2026-09-15). Confirms this project is now being worked from more than one machine/session.

**Found**
- `MEMORY.md`/`TASKS.md` had drifted from the repo since 2026-09-17: W0-09 (scaffold) was committed and CI went green on 2026-09-17 but `TASKS.md` still read `TODO`; W0-08 was still `IN PROGRESS` although CI now exists. README defers Docker to a task "W0-15" that didn't exist on the board.
- This machine had **no repo-local git identity** (resolved to the machine's global `nuwank` identity) and **no push-safety hook active** (`core.hooksPath` unset) — both required by `docs/09` and would have let a push go out under the wrong identity/unchecked.
- No `gh` CLI on this machine, so `gh auth status` can't be verified here — per the non-negotiable git rule, **no push from this machine** until that's fixed.
- The `client-progress` source folder and the DPAPI FTP credential (both machine-local) aren't present here either — the client progress page can't be republished from this machine.

**Done**
- Fixed repo-local identity (`MASACHITH` / `sachithishara1@gmail.com`) and `core.hooksPath` (`tools/git-hooks`) on this machine — repo-local only, global config untouched and verified unchanged.
- Added [docs/14-multi-session-coordination.md](docs/14-multi-session-coordination.md): start/end-of-session steps, a machine-readiness check, and a known-machines log (both machines listed). Linked from the root `CLAUDE.md` and from this file.
- Corrected `TASKS.md`: W0-08 and W0-09 → `DONE` (with the branch-protection caveat noted), added **W0-15** (Docker packaging, still blocked on W0-05) and **W0-14** (this coordination fix). Claimed **W1-01** as `IN PROGRESS` (this session).
- Reviewed overall status for the user: Week 0 setup essentially done, no feature code yet, Week 1 (19–25 Sep) three days in with nothing started before today.
- Kicked off **W1-01 vertical slice 1** (admin login + RBAC foundation + audit log on auth events) via a developer agent, per doc 03 §3a's approved design: ASP.NET Core Identity, HttpOnly admin cookie scheme (no JWT), permission-based `RequirePermission` policies. Scope explicitly **excludes** from this slice: full Users/Roles management UI, 2FA enrollment (baselined for Super Admins, doc 08), and an audit log viewer — flagged as follow-up. A separate tester agent reviews it next, per the developer → tester rule; loop back to developer on any failure before this is marked done in `TASKS.md`.

**Next:** tester agent on the W1-01 slice → fix loop if needed → mark W1-01 (partially) DONE and open the follow-up tasks (Users/Roles CRUD, 2FA, audit log viewer) → W1-02 categories. Get `gh` installed on this machine (or continue pushing only from the original PC) before this machine pushes anything.

### 2026-09-21 (2) — Dev environment wired to a real SQL Server instance
The user gave a running SQL Server's connection details in chat (host/port/sa credentials) to set up as this machine's dev database, in place of LocalDB.

**Found:** no database-provisioning script existed anywhere in the repo. `AppDbContext`'s remarks and doc 08 §1.4/§10.1 (N12) require the collation to be set at `CREATE DATABASE` time and never altered by EF Core — `SqlServerDatabaseFixture` already does this itself for ephemeral integration-test databases, but nothing did it for a persistent named database.

**Done**
- Verified the server (SQL Server 2022) is reachable and the credentials authenticate, before storing anything.
- Added [tools/db/provision-database.sql](tools/db/provision-database.sql): idempotent `CREATE DATABASE ... COLLATE Latin1_General_100_CI_AS_SC`, with a hard failure (not silent) if a database of that name already exists with the wrong collation. Ran it to create `SpencerDenim_Dev` on the provided server; confirmed online with the correct collation via `sys.databases`.
- Set `ConnectionStrings:Database` via `dotnet user-secrets` (already scaffolded, `UserSecretsId` in `SpencerDenim.Api.csproj`) to point at that database — **not** written to any `appsettings*.json`, per the explicit warning in `DatabaseOptions.cs`. Never committed, never printed into a repo file.
- Set `TEST_SQL_CONNECTION` (server-only, no database — `SqlServerDatabaseFixture` picks a fresh database name per test run) as a Windows user-level environment variable on this machine, so the integration test suite runs for real here instead of skipping.
- Documented this as machine-specific state in [docs/14](docs/14-multi-session-coordination.md)'s known-machines table (host/port only, no password — a session on another machine sets its own).

**Not done:** did not run the full `dotnet build`/`dotnet test` suite against it, since the W1-01 developer agent was actively editing backend code at the same time — that verification happens once that slice lands (its migration should target this database next). LocalDB (150 and 170), `sqlcmd`, and `dotnet-ef` 10.0.12 all turned out to already be present on this machine, corrected from "not checked" in doc 11.

### 2026-09-21 (3) — W1-01 slice 1: developer done, tester FAILED it, back for a fix
**Developer agent finished** the login/RBAC vertical slice: ASP.NET Core Identity, admin cookie scheme, permission-based authorization (`RequirePermission` + cached-by-SecurityStamp handler), RBAC schema matching doc 08 exactly, audit log on login attempts, gated/idempotent Super Admin seeder, login/logout/whoami endpoints, Angular login page, one EF migration (`20260921160620_InitialIdentityAndAudit`). Its own numbers: build clean, 51/51 unit tests, 16/16 integration tests (LocalDB), 18/18 frontend tests, lint clean, prod build OK. It found `tools/db/provision-database.sql` (from session (2) above) already on disk, correctly left it alone per doc 11, and used it to fix its own LocalDB database's collation.

**Tester agent (separate, per the non-negotiable process) independently reproduced all of that** — including re-running the integration suite against **both** LocalDB and the real SQL Server at `127.0.0.1,11433` via `TEST_SQL_CONNECTION` — and additionally verified the migration schema against doc 08 line-by-line, the antiforgery/cookie/lockout/seeder logic by reading and exercising the code, and the layer-dependency test's real teeth. Verdict: **FAIL.**

**Defect (Major, confirmed by measurement, not just code reading):** `AdminAuthenticator` returns early for an unknown/non-staff/inactive account **before** running the password hash check, so response time leaks whether an email belongs to an admin account — measured ~10–30 ms for unknown emails vs ~150–220 ms for a real account with a wrong password. A username-enumeration timing oracle on the admin login endpoint.

Two Minor items also found: the antiforgery cookie doesn't force `Secure` (inconsistent with the auth cookie, masked behind Caddy/HTTPS in prod but worth fixing for symmetry), and no Angular route guard yet on the dashboard (explicitly a next-slice item, not a security issue since every real endpoint is server-side cookie-gated).

**Done (this session, orchestrator):**
- Fixed the `tools/db/provision-database.sql` `-v DbName` bug myself (unrelated to the developer's slice — a `:setvar` in the script always wins over a command-line `-v` override, so the hardcoded default was removed and the variable is now required). Verified with a throwaway database, then dropped it.
- Sent the Major defect (and the cheap-to-fold-in Minor antiforgery-cookie fix) back to the **same developer agent**, per the non-negotiable fix-loop rule. Told it to re-verify with an actual timing measurement, not just a functional test, and left the route-guard item for a later slice.
- Marked `TASKS.md` W1-01 as `FAILED TEST` rather than `IN PROGRESS`, so a later session sees the real state.

**Round 2:** developer fixed both — re-measured timing itself (now within noise) and added `CookieSecurePolicy.Always` to the antiforgery cookie to match the auth cookie. Sent to the tester for a focused re-check (not a full re-review).

**Tester round 2: FAIL again, but only on the second item.** Timing fix confirmed independently (own re-measurement, own methodology, varied usernames, all three early-return branches in `AdminAuthenticator.cs` checked) — genuinely fixed, doesn't need touching again. But the antiforgery `SecurePolicy = Always` change **regressed**: `GET /api/admin/auth/antiforgery-token` now throws (`AntiforgeryOptions.Cookie.SecurePolicy = Always` on a non-SSL request) whenever the API is reached over plain HTTP — which is this project's **own default dev launch profile** (`launchSettings.json` has no HTTPS profile) and, per doc 03 §4's deployment topology (Caddy terminates TLS, proxies to the API over plain HTTP internally, no `UseForwardedHeaders` configured in `Program.cs`), likely how it runs in production too. Since the Angular admin app fetches this token before calling `logout`, **logout breaks** under the project's own normal running conditions, not just in some edge case. The integration test suite didn't catch it because `AdminAuthEndpointTests.CreateClient()` hardcodes `BaseAddress = "https://localhost"`, which fakes `IsHttps = true` regardless of the real fix — the test harness itself was masking the exact bug class it needed to catch.

**Round 2 fix:** developer went further than a patch — added real `UseForwardedHeaders` handling (new `Api/Http/ForwardedHeadersSetup.cs`) plus an `AdminSecurityOptions.RequireSecureCookies` toggle (default `true`, only `appsettings.Development.json` relaxes it) so both cookies' secure policy is environment-correct, and added a genuine plain-HTTP end-to-end test closing the harness gap that hid the original bug.

**Tester round 3: FAIL again — a new, concretely-exploitable defect found by scrutinizing the trade-off, not just re-checking the fix.** The plain-HTTP round-trip is confirmed fixed live (real `curl`, real dev profile). But `ForwardedHeadersSetup.cs` clears `KnownIPNetworks`/`KnownProxies` entirely, trusting `X-Forwarded-For`/`X-Forwarded-Proto` from *any* direct caller, on the justification that "only Caddy can reach the API container" — a topology that **doesn't exist yet** (W0-15 Docker/Caddy is still TODO). The tester proved this is exploitable today, not hypothetical: sent a login request with a forged `X-Forwarded-For` header and confirmed the spoofed IP was written straight into `ops.AuditLogs.IpAddress` — defeating the forensic purpose of the very audit-log feature (ADM-03) this slice implements, with zero authentication required.

**Round 3 fix, and the developer improved on the suggested fix:** pinning `KnownIPNetworks` to loopback alone turned out not to close the hole — loopback is exactly how `curl`/`dotnet run`/CI/tests connect, so that "restriction" wasn't restrictive at all against the tester's own repro (the developer proved this empirically before deciding). Instead it **disabled `X-Forwarded-For` processing entirely** (kept `X-Forwarded-Proto`, which is the one the round-2 fix actually needs and never touches the audit trail), so `RemoteIpAddress` always reflects the real socket peer regardless of any header. Verified with the same before/after repro the tester used: forged header before → forged IP in `ops.AuditLogs`; after → real peer IP (`::1`) recorded, forged header ignored. Full plain-HTTP round trip re-confirmed still working. A regression-guard unit test was attempted but hit an unrelated machine-specific toolchain issue (`Microsoft.AspNetCore.HttpOverrides` types not resolving in the plain-SDK test project on this machine); reverted cleanly rather than land something fragile, relying on the manual repro instead.

**Tester round 4: PASS.** Independently re-ran the exact forged-header repro live (audit row now shows the real peer, not the forged one), re-ran the full plain-HTTP round trip by hand (200/200/200/204/401), rebuilt and re-ran the suite (51/51 unit, 17/17 integration, clean), and confirmed `git diff --stat` shows only `ForwardedHeadersSetup.cs` changed this round with no stray artifacts from the reverted test attempt. **W1-01 slice 1 is DONE** — no further rounds needed on this defect line.

**Closed out (this session, orchestrator):** marked W1-01 `DONE` in `TASKS.md` as slice 1 of the full original scope, with the 3-round fix history recorded inline so a later session doesn't need to re-read this whole log to know why it took 3 rounds. Opened four follow-up tasks for what slice 1 deliberately left out or carried forward: **W1-08** (Users/Roles management CRUD, wiring `RoleAssignmentPolicy` to a real caller, seeding the remaining 3 default roles), **W1-09** (audit log viewer), **W1-10** (2FA enrollment for Super Admins), **W1-11** (Angular route guard, cookie `SecurityStamp` revalidation loop, the forwarded-headers regression test blocked by the toolchain issue). Added a note to **W0-15** (Docker/Caddy, still TODO) that it must re-enable `X-Forwarded-For` and widen `KnownIPNetworks` to the real Docker bridge subnet once that topology exists — currently deliberately disabled/loopback-only.

**Overall shape of this task:** three fix-loop rounds, each catching a real, independently-confirmed Major defect (login timing side-channel → antiforgery regression breaking logout over plain HTTP → forgeable audit-log IP address) — the non-negotiable developer→tester process did exactly what it's for here. This took far longer than a typical first slice; the user/architect should weigh that against the Week 1 schedule when deciding whether to pick up W1-08–W1-11 now or move to **W1-02** (categories) first.

### 2026-09-18 — End of day: where to resume
**State**
- `main` = `W0-09 scaffold` + `fix(publish)` pin rotation (PR #1, #2 both merged, CI green).
- **Open branch `feature/W1-02-catalogue-foundation`** (pushed, no PR): 4 commits — catalogue domain, EF mapping + migration + seeders + read-only catalogue API, unit/integration tests, an SQL-command-count test. **Not yet tested by a tester agent** (the developer agent's final report never arrived; its work is committed and the tree is clean).
- **Open branch `docs/W0-14-business-processes`** (pushed, no PR): `docs/11-business-processes.md` (17 processes, WooCommerce-informed, 36 gaps), docs/06 §4 + docs/09 §5b updates, TASKS/MEMORY. **Independent review was still running when the day ended**; its findings are not yet applied.
- Client progress page published 2026-09-18: 6% overall, 2 done, 3 in progress, 49 planned.

**Resume tomorrow, in this order**
1. `git switch feature/W1-02-catalogue-foundation`; re-run the developer's verification (build, `dotnet test` with `TEST_SQL_CONNECTION`, migrate+seed `SpencerDenim_Dev`, exercise the catalogue endpoints), then hand it to a **tester agent**; fix → PR → squash merge.
2. Re-run the **doc 11 review** (the earlier run's findings were lost when the day ended), apply the fixes, then PR the docs branch and merge.
3. Then the next build task: **storefront pages** (home, listing with filters, product page) on the real catalogue API, or **admin auth + catalogue screens** — the user still has to choose the order.
4. Small tasks queued: **W0-16** update GitHub Actions to current majors (Node 20 deprecation warning on every run), **W0-15** Docker packaging (blocked by the office firewall).

**Notes**
- Publishing the task sheet broke because Hostinger rotated its FTPS certificate to a Let's Encrypt `*.hstgr.io` one. `$PinnedKey` now accepts a list; refresh with `-ShowPin` (docs/10 §4).
- `SpencerDenim_Dev` on LocalDB currently holds the scaffold-era schema; the catalogue migration may still need applying.

### 2026-09-17 (2) — Scaffold merged, branching workflow adopted, no Docker
**Decisions (user)**
- **No Docker for now** (company firewall). Development runs directly on Windows; Docker packaging = W0-15.
- **Git best practice:** branch per task, Conventional Commits, PR + squash merge after tester pass. Documented in docs/09 §5b; worktrees for parallel agents.
- **WooCommerce** is the reference for standard e-commerce operations; our baselined design wins where they differ.

**Done**
- Docker Desktop fixed (`wsl --update --web-download` → WSL 2.7.14) but **not used**; a stuck Docker instance was blocking the UI and was closed.
- **W0-09 scaffold merged to `main` (`4efe767`, PR #1)**: .NET 10 clean-architecture backend (`/api/ping`, `/health`, `/health/ready`, Swagger in Dev only, EF `AppDbContext` with configurable compatibility level), Angular 21 workspace (storefront SSR, admin, shared tokens library), 20 unit + 8 integration + architecture tests, GitHub Actions CI. **First CI run green.**
- Local dev database: **LocalDB** `(localdb)\MSSQLLocalDB`, databases `SpencerDenim_*` only. `SpencerDenim_Dev` exists (empty, collation `Latin1_General_100_CI_AS_SC`). The default office SQL instance is never touched (the dev account cannot even log in to it).
- Integration tests use `TEST_SQL_CONNECTION` (skip with a reason when unset); CI uses a pinned SQL Server service container with a login-based readiness gate. Testcontainers dropped until Docker returns.
- QA found and the developer fixed: stale README status/paths, broken npm scripts, EF warning noise, missing shared-library and SSR coverage in CI, repeated CI password, and 5 CI first-run risks.

**In progress**
- `docs/W0-14-business-processes` branch pushed: `docs/11-business-processes.md` (17 processes, WooCommerce-informed, 36 gaps) under independent review; docs/06 §4 and docs/09 §5b updated.

**Open question for the user:** storefront first with sample data, or admin catalogue first (current plan)?

### 2026-09-17 (1) — Docker guides, hosting, task sheet publishing
**Done**
- PDF guides in `docs/guides/`: DEV-GUIDE-01 Docker Desktop on Windows (v1.1, 7 pp) and DEV-GUIDE-02 Fix "WSL needs updating" (3 pp). HTML sources are kept; built with headless Chrome + a pypdf footer stamp.
- Dev PC: Docker Desktop 4.91 installed **per-user** (CLI 29.8.0). WSL features on, but the **WSL 2 kernel is missing**. The user must run `wsl --update` as admin (DEV-GUIDE-02). Account `SCIENTER\sachithi` is not a local admin; the PC is domain-joined (scienter.lk).
- Hostinger shared hosting bought for the task sheet: https://colombowardrobecompany-build-tracker.kt-wedding.online. FTP host 147.93.101.68:21, user `u765465138.colombowardrobecompany-build-tracker.kt-wedding.online`, opens in `/public_html` (contained only `default.php`).
- FTP password stored DPAPI-encrypted at `%USERPROFILE%\.spencer-denim\tracker-ftp-credential.xml` (never in repo or docs). The user shared it in chat, so recommend changing it and running `-SetPassword`.
- `tools/publish-client-progress.ps1` (allow-list of 4 files, node validation, FTPS with pinned Hostinger key `sha256//rRBXAmuSvqI5e99CZPWQDXLPWIlTIvu77x/aZc349JQ=`, cert expires 2027-01-17, size + live verification, never deletes) + `docs/10-client-progress-publishing.md`. Dry run OK; wrong pin correctly rejected (curl 90).
- `client-progress/progress-data.js`: lastUpdated 2026-09-17; "Hosting, domain & security setup" in progress 25%; log entries for 16 and 17 Sep; overall 6%.

**Publishing**
- First attempts failed: FTP data connections reset or timed out (control on port 21 worked). Recorded in doc 10 §5b.
- **Published successfully on a later single retry** (same PC): all 4 files uploaded, sizes verified, live `lastUpdated` 2026-09-17 at https://colombowardrobecompany-build-tracker.kt-wedding.online. Hostinger serves `index.html` ahead of `default.php`. Live check in Playwright at 390 px: no horizontal scroll, no overflow. Hostinger shows a short "Checking your browser" bot check on first visit.
- The data-connection problem is **intermittent**. If publishing fails, wait and retry once later, or use hPanel File Manager.

**Decision (user, 2026-09-17):** Docker work is **paused for now**. Focus is on the client progress page publishing only. The WSL 2 kernel update (`wsl --update`) is still pending whenever Docker resumes.

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
