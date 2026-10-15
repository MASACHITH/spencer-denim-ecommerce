# 2026-10-14 — Tharanga: #109 (slice), the blocked "Proceed to checkout" button

Branch `fix/109-bag-checkout-aria-disabled` (worktree `worktrees/sd-bagaria`), cut from `main` at `f968ecb`.
Storefront only: **no backend, no migration, no dependency.** Claimed on the issue before starting.

## What changed

The bag's blocked checkout button was `<button disabled>`. A `disabled` button is out of the tab order, so a
keyboard or screen-reader user could never land on it to hear the reason `aria-describedby` points at. It is
now `aria-disabled="true"`, as ADM-22, Quick View and checkout's Place order already are. The button has no
click handler and no `routerLink`, so pressing it still does nothing. The shared `.btn[aria-disabled='true']`
rule in `base.css` already gives the unavailable look and guards hover (checkout uses it), so the only CSS
change is the bag's own `opacity` rule gaining the attribute selector.

## It is a slice, on purpose

#109's own title is this button. The owner's 2026-10-06 comment adds **Remove, Move to wishlist and the
quantity buttons**, which `[disabled]` *during* a request. Those are the worse instances (a keyboard user is
waiting and has nowhere to be returned to), and fixing them means rewriting the bag's busy-state tests. That
is a bigger change to the page every shopper uses, made the night before launch, so it is not here. The PR is
`Refs #109`, not `Closes`, and the issue stays open for those.

## Tests, and what was checked about them

- Bag spec: the two assertions that pinned `disabled` now read `aria-disabled`; one new test that the button
  is not `disabled` and takes focus. Playwright (`checkout.e2e.ts`, "getting there"): the blocked button takes
  focus, is described by the reason, and Enter / Space leave the page on `/bag`.
- **Proven able to fail, on committed work:** putting `disabled` back makes 3 of 41 bag unit tests fail, and
  makes the new Playwright test fail (`aria-disabled` expected `"true"`, received `""`).
- **A claim checked rather than assumed.** In both mutation runs the test failed at the *attribute* assertion,
  before reaching the focus line, so the focus assertion was never seen failing. Checked directly instead:
  `focus()` on a `<button disabled>` leaves focus off it in **jsdom** (`activeElement` none) and in
  **Chromium** (`body`), and takes it on `<button aria-disabled>` in both. So the focus assertions do
  discriminate, and the test comments are true.
- `toBeDisabled()` in the two existing Playwright assertions still passes on the `aria-disabled` button, which
  is the independent confirmation the issue asked for that Playwright 1.62 counts `aria-disabled` as disabled.
- **The Playwright suite serves the prebuilt `dist/`.** Every browser result above is from a bundle rebuilt
  from the source being tested (see the #123 journal, same day).

## Run on this branch

`ng lint` clean; storefront unit **1881 / 1881**; `ng build storefront` succeeds; `bag.e2e.ts` **51 / 51** and
`checkout.e2e.ts` "getting there" **4 / 4** on a fresh build. Not run: the rest of Playwright; no tester pass.

## Not done

The per-line controls above. And the issue's suggestion of a lint rule or written convention against
`disabled` on a button whose state changes under the user (it counts this defect at six now); that is a
decision for the owner, not a change to make at midnight.
