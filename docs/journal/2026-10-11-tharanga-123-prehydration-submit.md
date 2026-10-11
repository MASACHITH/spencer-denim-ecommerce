# 2026-10-11 — #123: a form submitted before hydration

Branch `fix/123-checkout-pre-hydration-submit`, worktree `worktrees/sd-prehyd`, cut from `origin/main` at
`31e6aa6`. Storefront only: **no migration, no API change, no new dependency, no server change.**
**Not pushed, not a PR — the tester goes first.**

## What was wrong

Every storefront page is server rendered, so the checkout form is on the screen, focusable and typeable
while the bundle is still downloading. Before Angular hydrates there is no `(submit)` handler, so nothing
calls `preventDefault` and the browser performs its **own** submit: a `GET` to the page's own address with
every field in the query string. Measured on `main` (Chromium, scripts held back, four fields typed, Enter):

```
/checkout?firstName=Nimali&lastName=Perera&email=nimali%40example.com&phone=0771234567&districtId=&paymentMethodCode=BankTransfer&customerNote=
```

That is a name, an email address and a phone number in browser history, in the `Referer` of the next
request and in every access log between the shopper and the server (doc 11 P17), and the shopper is back at
an empty form with nothing said. The issue named three more forms; the grep for `<form` under
`projects/storefront` found a **fourth** that it did not: the contact form. All of them had the same
shape, and the contact form is the worst of the five — its one free-text field is where people type
something private. Measured on `main`:

```
/contact?name=Nimali+Perera&email=nimali%40example.com&phone=&subject=&orderNumber=&message=A+private+question+about+an+order.&website=
```

## How it was reproduced

The suite could not see it because every test waits for the page to be interactive before touching a form.
`e2e/support/hold-scripts.ts` puts the browser in the shopper's position instead: `page.route('**/*.js')`
**holds** every script request, so the document, its styles and its forms arrive and no JavaScript runs;
the test types, presses Enter and clicks, then **releases** the scripts and waits for the page's own handler
to answer (a positive condition, no sleeps) before asking where the shopper is. Held rather than blocked
so a fix that only worked by breaking the form for good would still fail. `javaScriptEnabled: false` was
rejected for the same reason, and because it also removes the inline event-replay script that is part of the
real situation.

"Nothing happened" cannot be waited for, so it is proved after the fact from three independent signals:
the log of every request address the page made (names the leak even if the page moved on), the final URL,
and a marker on `window` that a replaced document does not have.

On `main`, with the final tests and no fix, all of: checkout, header search, footer newsletter, homepage
newsletter and the six structural checks fail; the contact test fails with the URL above.

## The shape chosen: `method="dialog"` on the form

A `<form method="dialog">` that is not inside a `<dialog>` has a native submit that **does nothing** (HTML
Standard, form submission algorithm, the `dialog` branch: with no ancestor `dialog` the algorithm returns).
The `submit` event still fires, so `placeOrder` is unchanged after hydration and Enter-to-submit keeps
working with no new code. Before hydration the press is a harmless no-op and the typed values stay in the
fields. It is one attribute, needs no script (so it also holds if the bundle never loads) and no CSP
allowance. `<button type="submit" form="checkout-form">` and the `form=` association are unchanged.

### The alternatives, and why not

| Option | Verdict |
|---|---|
| **Order page's pattern**: drop the `<form>`, `<div>` + `(click)` + `(keydown.enter)` | Removes the native path entirely, but re-implements implicit submission by hand for a 12-field form: Enter on text inputs only (not the `<textarea>`, which must insert a newline), the radio and select behaviour, and **IME composition** — Sinhala and Tamil input editors confirm a candidate with Enter, and a bare `keydown.enter` would place an order mid-word unless it checks `isComposing`. It also deletes the `form=` association this page was built around, and rewrites two existing tests (`form=` in the spec, `dispatchEvent('submit')` in `accessibility.e2e.ts`). Largest diff, most ways to regress a page that works. The order page could afford it: one text box. |
| **`method="post"` + an `action`** | Measured: a `POST /checkout` to the SSR server answers **200 and renders the whole page** (it does not 500, and nothing logs the body), at the full cost of a page render and its API calls for a request that is not a page view; the shopper sees an empty form and a "confirm form resubmission" prompt on reload. Making it tidy needs a new Express route (`303` back), which is new server surface to drain and test. The personal data also still travels, as a body, through every proxy. The typed data is lost either way. More moving parts, strictly worse outcome than a no-op. |
| **Inline `onsubmit="return false"`** | Works only if inline handlers are allowed. There is no CSP in the repository today, but a `script-src` without `unsafe-inline` — the first thing a hardening pass adds — would turn this into a silent regression. It also does nothing when the bundle never loads **and** script is off. `method="dialog"` has neither failure. |
| **`method="dialog"`** | Chosen. |

### What it does not cover

- **Engines.** I could only run Chromium (the project's only Playwright project), so Firefox and Safari
  rest on the specification and on my recollection that `<dialog>` and `method="dialog"` shipped in Firefox
  98 and Safari 15.4 (March 2022) — **not verified here**. A browser older than that treats the value as
  unknown and falls back to `GET`, i.e. the original defect; that is outside any browser Angular 21
  supports, so I have not added a second mechanism for it. Worth one manual check on a real iPhone and
  Firefox before launch.
- **Typed text is still lost when the page hydrates.** Not caused by this fix and not fixed by it: the
  inputs are `[value]`-bound to signals that start empty, so when Angular applies the binding it overwrites
  what the shopper typed (measured: fields hold their text until the scripts arrive and are empty after).
  The same is true of browser autofill that lands before hydration. The harm is a shopper retyping, not a
  leak, and the issue accepts it as the lesser harm — but the issue's first sentence says "loses the whole
  form", and with this fix that is now true only of hydration. A proper fix reads the DOM values into the
  signals when the component is created in the browser. I did not do it: it is a behaviour change to the
  checkout state, with autofill and bfcache edge cases, and it belongs in its own issue.
- **A press before hydration is not replayed.** Angular's event replay does not deliver a pre-hydration
  `submit` to `placeOrder` (measured: no refusal summary appears after hydration), so nothing can place an
  order on the shopper's behalf. Good, and worth knowing: the press is simply ignored.

## The other forms

| Form | Verdict |
|---|---|
| Footer newsletter (`site-footer.ts`) | An email address in `?email=` on whatever page the shopper is on. **Fixed**: `method="dialog"`. |
| Homepage newsletter (`newsletter-signup.ts`) | Same. **Fixed**: `method="dialog"`. |
| Contact form (`contact-panel.ts`) — not in the issue | Name, email, phone and the message in the query string. **Fixed**: `method="dialog"`. |
| Header search (`header-search.ts`, rendered on every page, three instances including the search page's own) | A term in the address is what a search **is**, so it may navigate — but it had no `action`, so the native submit went to *the page the shopper was on* (measured: `/women?q=linen+shirt`, the listing reloaded with a parameter it ignores), not the results route. **Fixed the other way**: `action="/search" method="get"` lands exactly where `onSubmit` sends them (`/search?q=linen+shirt`, results server rendered). |
| Order page's payment-reference controls | Not a `<form>` (W3-03). Untouched. |
| Admin forms (about two dozen) | Not affected: the admin app is a client-rendered SPA with no server render, so no `<form>` exists in the document before the bundle runs. |

Other native default actions that a handler cancels (`site-header.ts` bag link, `product-gallery.ts` keys,
`modal.ts`, header-search keys) are all progressive enhancement: before hydration they do the browser's
default, which is a normal navigation or scroll. None is lossy or leaks anything.

## Tests

- `e2e/pre-hydration-submit.e2e.ts` (new, main server pair, no database): checkout (Enter in a field **and**
  Enter on the Place order button, then, once alive, Enter in a field raises the refusal summary — the
  complete order round trip stays in `checkout.e2e.ts`'s keyboard test), header search, footer newsletter,
  homepage newsletter, and a structural check that **every `<form>` the server renders on six pages is
  either `method="dialog"` or the search form**, so a seventh form that forgets fails there naming the page.
- `e2e/content-pages.e2e.ts`: the contact form's pre-hydration test, on that file's own server pair and in
  its serial describe, for the reason its header gives.
- `e2e/support/hold-scripts.ts`: `holdScripts`, `recordRequests`, `markDocument`/`isSameDocument`,
  `carriesAny`.
- Unit (`checkout-page`, `site-footer`, `home-page`, `header-search` specs): the attributes, with a comment
  saying why jsdom cannot see the behaviour.
- The checkout template carries the full reasoning, with a warning not to give the form an `action`/`method`
  without reading the e2e test. The other three `method="dialog"` forms point at it.

## Next

- After this merges, the comment in `order-page.ts` ("It is W3-02's and filed as issue #123 — not fixed
  here") is out of date; the W3-03 branch owns that file, so it is left for them.
- Open question for the owner: the hydration-overwrites-typed-text finding above, as its own issue?
