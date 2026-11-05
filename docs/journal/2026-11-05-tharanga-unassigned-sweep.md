# 2026-11-05 — Tharanga: the four unassigned issues

Asked to take every open issue with no assignee: #272, #270, #239, #240.

## Done

- **#239, #240: closed as already delivered.** Both were the owner's specs from 2026-10-23 and had been built
  and merged the next day (#242; #241 + #246) without the issues being closed. Checked each spec against `main`
  before closing; the closing comments map spec → what shipped. Two differences from the written spec, both
  owner decisions at the time, noted in the comments: Express/Standard prices live on `sales.DeliveryRegions`
  with per-country overrides (no separate `OverseasDeliveryRates` table), and the blocklist is
  `ref.Countries.IsShippingEnabled` (no `BlockedCountries` table). Server-side enforcement confirmed:
  `OverseasRefusal.CountryDisabled` / `NotPriced` / `ExpressNotPriced` refuse in `PlaceOrderCommandHandler`.

- **#272 → PR #273 (dependency gate green).** `@angular/build` 21.2.27 pinned `undici` to exactly 7.29.1, so
  `npm audit fix` could not move it; 21.2.29 pins 7.30.1 and vite 7.3.7.
  - Tooling `^21.2.29`, framework set `^21.2.26` (npm's peer resolution forces the framework to its newest patch;
    a tooling-only bump is `ERESOLVE`). Needed: delete the `@angular/*` entries from the lockfile and reinstall —
    npm kept resolving against the old tree otherwise. Written up in docs/06 §4 for the next bump.
  - In range: node-gyp's undici 6.29.1, body-parser 2.4.0 (+ qs 6.17.1).
  - MCP SDK override removed (cli 21.2.29 depends on 1.32.1). **Allow-list now empty** — all three entries stale.
  - The old body-parser entry called it "extraneous"; it is express's dependency in the SSR bundle (not
    exploitable only because `server.ts` mounts no body parser). Tester caught my overcorrection ("shopper-
    reachable") — fixed before the commit.
  - Verified: gate PASS; builds + lint OK; unit shared 24/24, admin 1029/1029, storefront 2270/2272 in the full run
    — two 5 s timeouts (search-page, listing-page Load More) on a loaded machine, 90/90 when those files run alone.
    Independent tester PASS.

- **#270 → fix on `fix/270-header-scroll-into-view`.** The issue's guess (modal focus without `preventScroll`) was
  not the cause, and the bug is wider than `/bag`:
  - `base.css` sets `html { scroll-padding-top: var(--header-height) }`. Every control in the sticky header is
    always inside that band, so **any** scroll-into-view of one (keyboard focus, `focus()`, Playwright's click)
    scrolls up to clear the header — which comes along, being sticky — until the page is at the top.
  - Real-shopper impact, measured on the built storefront: scrolled 400 px down `/bag` or `/women`, open the mini
    bag with the mouse (fine), **close it → page at 0** (22 at 1280), because the drawer returns focus to the bag
    icon. Keyboard users hit it on every Shift+Tab into the header. A raw mouse click alone never did, which is
    why it looked like a Playwright-only artefact at first.
  - Fix: two rules in the storefront `styles.css`. (1) Header controls get `scroll-margin-top: calc(-1 *
    var(--header-height))`, excluding the mega menu, the search suggestions (own scroller) and both drawers.
    (2) `html:has(sd-site-header :is(input, select, textarea):focus) { scroll-padding-top: 0 }` — the tester found
    that Chromium's focus scroll and per-keystroke caret scroll for a **text field** ignore the field's
    `scroll-margin`, so the search box still jumped (inline at 1280+: 400→0-21 on Tab; panel below 1280: 470→270
    while typing — that half was already so on `main`).
  - Tests: `navigation-scroll.e2e.ts` — focus each header control, and focus + type in the search box, at 390 and
    1280; `bag.e2e.ts` — the issue's repro + close + focus back on the icon. Each was red without its rule (400→0,
    400→22, 400→21, 470→270) and is green with it. Tester: PASS, no regressions found across mega menu, suggestions,
    both drawers, skip link, the 8 breakpoints and short landscape viewports; their one should-fix is rule (2).

## Notes for whoever is next

- **Only Chromium was measured.** Negative `scroll-margin` is allowed by the spec, but WebKit/Firefox behaviour with
  the inverted snap area was not checked here (no WebKit browser installed). Worth a look in the device regression
  pass (#197).
- Left untracked, unrelated to today's work and not included in either PR: `docs/journal/2026-10-24-tharanga-gmail-smtp.md`,
  `pay-panel-320.png`. Decide whether they belong in a commit.
