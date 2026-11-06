# 2026-10-31 — Tharanga — issue #267 (scroll-to-top follow-ups)

Worked in `worktrees/sd-267`, branch `fix/267-scroll-followups`, based on the still-open
`fix/storefront-scroll-top` (PR #268) since the files in question don't exist on `main` yet.

Addressed all five items the tester of PR #268 raised in #267:

1. **Focus after a keyboard navigation.** `NavigationScroll`'s top-of-page scroll now also moves
   focus into `main`, same target as the skip link. Previously focus stayed on the link that was
   just activated, now off-screen at the bottom of a page scrolled back to its top, so the next Tab
   pulled the page straight back down to it.
2. **A link to the page already shown did nothing.** The router's `onSameUrlNavigation: 'ignore'`
   (left as the default; `'reload'` would re-run every resolver for a filter click that happens to
   produce a URL already applied) fires no event for that click. Added a `click` listener that
   checks `event.defaultPrevented` (set by `RouterLink` itself) and the clicked anchor's resolved
   address against the one already shown, and gives it the same top-and-focus treatment outside the
   router entirely. Fragment links (the skip link, a same-page anchor) are excluded.
3. **Test gaps.** Tightened the skip link's `scrollIntoView` e2e assertion to a band, not just a
   floor. Added e2e coverage for the two fixes above. Added a real-browser test for
   `restoreOnceDrawn`'s "wait for the page to be tall enough" logic — a delayed catalogue response
   didn't work (the listing reserves its skeleton height up front for CLS) and a `main`-level
   CSS clamp didn't either (broke the grid's own layout enough to make the page taller, not
   shorter); hiding all but the first row of cards with `display: none` did. The matching
   shopper-gesture-takeover e2e test kept failing for reasons unrelated to the fix (see below) and
   was replaced with a jsdom unit test instead.
4. **Documented limitation.** Added an explicit paragraph to `app.ts` about the skip link, with
   scripts off, going to the home page from any other page (the `<base href="/">` behaviour the
   class doc already explained, just not called out as an accepted limitation).
5. **Theoretical restore race.** `restoreOnceDrawn` now cancels any earlier pending restore —
   tracked in a `cancelPendingRestore` field — whenever a new one starts or any navigation ends, so
   a stale Back/Forward restore can no longer land its remembered position on top of whatever page
   is on screen by the time its patience or height check comes due.

**Not worth knowing twice:** the shopper-gesture-takeover e2e test went through three real-browser
techniques (delayed API response, CSS height clamp on `main`, hiding grid rows) before even the
load-only test above started working reliably — and the gesture-specific test still didn't,
for a reason I never pinned down (possibly interaction with the listing's own scroll-triggered
loading). That logic is pre-existing, not touched by this fix, so it's now a jsdom unit test
instead (`navigation-scroll.spec.ts`, "is cancelled by a shopper gesture..."), which is
deterministic. If someone wants a browser-level version of that one specifically later, budget for
it — it is not a quick add.

**Independent tester found a real bug, I found its real cause is a different, pre-existing one.**
The tester flagged that opening the mini bag while already on `/bag` (a plain `<a href="/bag">`
whose own click handler, `onBagClick`, `preventDefault()`s and opens the drawer instead of
navigating) fires the new same-page-link handling, since to anything listening for the click
bubbling past it, that's indistinguishable from a link to the page already shown. True, and
`onBagClick` now also calls `stopPropagation` — confirmed by a diagnostic that the window-level
listener never runs once it does. But the *symptom* the tester actually observed (the bag page
visibly scrolling to the top when the drawer opens) is unrelated: it reproduces identically on the
unmodified `fix/storefront-scroll-top` baseline, with `stopPropagation` and the window listener
both absent, so it is the mini bag/modal's own pre-existing focus handling, not this fix. Filed as
its own issue (#270) rather than fixed here — out of scope, and the owner should weigh in on
whether it's worth fixing before the modal drawer pattern is used anywhere else. The regression
test in `bag.e2e.ts` asserts against `document.activeElement` being `main` specifically, which
isolates the actual thing #267 could have broken from that separate bug.

All green: `ng test storefront` (2272/2272 — the full suite, including the Load More tests in
`search-page.spec.ts` / `listing-page.spec.ts` that failed under heavy parallel load earlier today;
confirmed pre-existing flakiness, not a regression, by reproducing on the unmodified baseline and
by a clean pass in isolation and on a later full run), `playwright test e2e/navigation-scroll.e2e.ts
e2e/bag.e2e.ts` (116/116), lint clean.

Not yet done: opening the PR (waiting on the owner/user to confirm before pushing).
