# 2026-10-14 — Tharanga: #166, junk POSTs and the PayPal webhook's throttle

Branch `fix/166-paypal-webhook-throttle` (worktree `worktrees/sd-ppthrottle`), cut from `main` at `f968ecb`, the
PayPal merge. Backend only: **no migration, no dependency, no setting, no frontend.** Found by the owner's review
of #155 and filed as #166 (`priority:high`, "must be fixed before PayPal is enabled").

## The defect

The webhook's endpoint-wide bucket (120 a minute) is the budget PayPal's real deliveries draw on, and a pending
capture settles only by webhook. `LooksLikePayPal` ran inside the Infrastructure gateway, after
`WebhookAsync` had already taken a permit, so a POST that could not possibly be PayPal's still cost one. About
two a second emptied the bucket and the real deliveries got 429.

## What changed

- `PayPalWebhookHeaders.LooksLikePayPal()` (Application) is the single definition; the gateway calls it instead
  of its own copy. The endpoint asks it first and answers 400 without taking a permit and without reading the
  body. Same status the gateway would have given.
- What does look like PayPal is rationed per address (30 a minute) and then from the shared bucket, address
  first. The order is the point: an address that has spent its own budget is refused without touching the
  shared one.
- The issue offered "check first, or limit per IP". Both are done, because either alone leaves a hole: the check
  does nothing against well-formed junk, and a per-IP limit alone still lets junk spend the shared bucket up to
  each address's share.

## Tests (the throttle had none)

- Unit: `PayPalEndpointThrottleTests` pins the ordering **without restating the constants** (the shared capacity
  is measured on a reference throttle; an attacker's refused attempts must have taken nothing from it).
  `PayPalWebhookHeadersTests` pins the header check, including hostile certificate addresses.
- Integration (`PayPalWebhookTests`): 400 junk POSTs, half with no headers and half dressed up with a
  non-PayPal certificate host, then a genuine delivery: 200 and the order paid. And a 400-request flood that
  does look like PayPal: a bounded number reach PayPal, the rest are 429 with `Retry-After`.
- **Proven able to fail, one change at a time, on committed work:** the original ordering (permit before the
  check) fails the junk-flood test with `429` where `400` was expected, which is the production symptom;
  address-after-shared fails the unit test with `Expected: 90, Actual: 0`; no per-address bucket fails it with
  "the attacker must not be able to take the whole bucket by itself". Restored: green.

## Run on this branch

Release build 0 warnings; unit **2673 / 2673**; `PayPalWebhookTests` **40 / 40** (LocalDB).

## A finding that is not this change: six settlement tests fail on a fresh database, on `main` too

A wider integration run (`Sales.PayPal`, `OrderSettlement`, `PaymentEvent`: 219 tests) gave 213 passed and 6
failed, all in `OrderSettlementRobustnessTests` (`A_settlement_is_not_blocked_by_a_lock_on_another_orders_payment`
and `Settling_twelve_orders_in_parallel_never_deadlocks`, three `openingStatus` values each), each timing out
at about 30 s. Run alone, same 6 of 32. **Run alone on a clean `origin/main` (`f968ecb`), no part of this change,
the same 6 fail**, so it is not caused here. LocalDB was clean (no leftover test databases, no blocked
sessions).

They passed in my 646-test subset earlier the same day, and the owner's whole-assembly run passed them
(1819 / 1819). So they depend on the run around them, not on the code. **I have not found why.** A guess worth
checking, and only a guess: on a near-empty database the optimiser may scan instead of seek, so the update
touches the row the test deliberately locks; that would explain "passes in a big run, fails in a small one".
Whoever next runs this class alone should check the plan. Not filed as an issue yet.

## Judgement calls, and what is not covered

- **30 a minute per address is a judgement, not a measurement.** No live webhook volume exists. Behind a
  gateway whose forwarded headers are not trusted every visitor is one address (W0-15) and this becomes the limit
  for the whole endpoint, which is why it is not lower. Compose sets `KnownProxies` to the gateway, so a
  deployed environment should see real addresses; I did not verify that on a running stack.
- **Many addresses together are not stopped by either change.** The shared bucket remains the last cap.
- Not run: Playwright and the full integration assembly (about an hour); no tester pass.
