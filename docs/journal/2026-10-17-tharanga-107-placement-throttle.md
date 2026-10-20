# 2026-10-17 — Tharanga: #107, order placement throttle (and what the issue's other half looks like now)

Branch `fix/107-order-placement-throttle` (worktree `worktrees/sd-placethrottle`), cut from `main` at `154b5e4`.
Handed over as a pre-Live security fix (#187 §1). **No migration, no dependency, no new `ops.Settings` key.**
Three environment variables (`OrderPlacementThrottle__*`, README "Other settings").

## 1. The true state of #107 today: the expiry half is built, registered and works

The issue pre-dates W3-03. Checked, not assumed:

- `ExpireUnpaidOrdersService` exists (`Infrastructure/Sales/Payments`), is registered by `AddInfrastructure()`
  (`AddHostedService`, unconditional) and so runs in the API host. Every 5 minutes it takes up to 50 orders past
  `PaymentDueAtUtc` and expires each in its own transaction (`OrderExpiryStore`, doc 08 §5.6).
- **Hold window: 48 h for bank transfer** (`Orders__BankTransferExpiryHours`, default 48); PayPal's is
  `Orders.PayPalExpiryMinutes` (60), longer only while a capture lock is in force.
- It does release the reservation: `OrderExpiryTests` (store level, real SQL) assert `Reserved` falls by the
  order's quantity, `Available` rises, the order is `Expired`/`Released`, the open payment is `Expired` and the history
  row is `SystemJob`. What no test covered was **whether the host runs the job at all**, which is the half of #107
  that mattered ("nothing will ever release them"). `ExpireUnpaidOrdersHostingTests` now asserts the API host
  contains the hosted service and that the default hold is 48 h.
- Real run (not committed): see "Evidence" below.

**Limits of the expiry that the owner should know**, none of them a defect: it drains 50 orders per 5 minutes
(600 an hour) and only starts when an order is past its deadline, so a flood from many addresses is held for the full 48 h
and then released at that rate; and the hold itself is 48 h.

## 2. What was added: the placement throttle

`POST /api/checkout/orders`, per visitor address (`HttpContext.Connection.RemoteIpAddress`, which behind the gateway is
the real client, docs/15 §3). 429 with `Retry-After` and a problem-details body (`code: "rate_limited"`), never a 500.

- Port `IOrderPlacementThrottle` (Application) + adapter `OrderPlacementThrottle` (Api/Http), the same shape and the same
  `PartitionedRateLimiter` as `NewsletterSignupThrottle`/`PaymentSlipUploadThrottle`. IPv6 visitors are bucketed by /64
  (`ClientAddressPartition`). Loopback/private/missing addresses get the larger shared budget and a one-off warning
  (event 3311), as the slip throttle does, because refusing genuine shoppers when the gateway is misconfigured is worse than
  the squatting. I added `ClientAddressPartition.IsInfrastructure` rather than a fourth private copy of `IsLocal`; the
  three existing copies are untouched (candidate for a later clean-up, not done here).
- **Default 20 orders per address per 10 minutes**; shared budget 200; window 600 s. All three are env vars.
  Why 20: Sri Lankan mobile carriers use carrier-grade NAT, so one address can be a district of subscribers, and a campaign
  link brings a crowd; the shop's whole plausible peak is a handful of orders per ten minutes, so no real address reaches
  20, and a shopper refused at the last step is a lost sale. The newsletter's 20 is the precedent.
- **Replay exemption is structural, not a flag.** The handler asks for a permit only after the replay check (step 1), after
  validation, after the cart is priced and G-10 has agreed the total, and before the order number is allocated. A resend of
  a key that already placed an order is answered 200 with that order and never reaches the throttle, however spent the
  address is. The client cannot claim to be a replay: the check is "an order with this key exists in the database".
  If the permit is refused, the handler asks the replay question once more (the first submission may have committed in the
  meantime), the same re-check every other refusal in this handler makes.
- A request refused on its own merits (400, empty bag, unbuyable line, moved total) costs no permit and an address that has
  spent its budget still gets the specific 400 rather than a 429.
- **Accepted imprecision**: a placement that has taken its permit and then loses the stock race (409) keeps the permit spent;
  and two *overlapping* submissions of one key (a double tap, not a sequential retry) each take a permit, the loser being
  answered with the winner's order by the existing duplicate path. A fixed window cannot return a permit.
- Test hosts raise the limit (`ApiFactory`, 1,000,000): `TestServer` reports no address, so every shopper in every test is
  one bucket and the concurrency rounds alone place several hundred orders. The new tests set it back to a few permits through
  the same configuration key.
- Storefront: `checkoutFailure` returns `rateLimited` for 429; the sentence says nothing was placed or charged, the bag is
  unchanged, how long to wait (from `Retry-After`, rounded up) and to press Place order again; the page does not re-read or
  clear anything, keeps the same checkout key and moves focus to the message (it is at the top of a long page).

## 3. What the throttle does NOT do — for the owner

**It is a speed limit, not a ceiling.** One address can still place 20 orders every 10 minutes (about 2,900 a day) and a script
with many addresses is not slowed at all. And two things make the real exposure larger than the issue described:

1. **A single order can hold the whole catalogue.** Each cart line is capped at 10 units but **the number of distinct lines in a
   cart or an order is not capped anywhere** (`CartStore`, `PlaceOrderValidator`; `CartItem.MaxQuantity` is the only limit), and
   the cart endpoints are not rate-limited. One placement, one permit, can reserve 10 of every in-stock variant for 48 h.
   *Recommendation:* cap lines (and/or total units) per order. This is a business rule (does the shop sell wholesale?), so it
   is not built.
2. **A cap on open unpaid orders per contact** (the thing item 3 of the brief asked about). Feasible without a migration:
   `IX_Orders_EmailNormalized`, `IX_Orders_Phone` and a filtered `StockState = Reserved` index exist. It would have to be counted
   inside the placement transaction under an app lock (like the promo per-customer limit) or concurrent requests would slip
   past it, which is why it is not "small and clearly safe". It is also bypassed by changing the email address, so it stops the
   naive loop and not the determined one. Owner decisions: the number (3? 5?), whether email, phone or both count, whether a
   signed-in customer is exempt, the message ("pay or cancel an open order"). Not built.
3. **The 48 h hold** is the other lever (24 h would halve every squat) and is also the owner's trade-off between overselling and
   squatting that #107 itself says should be re-confirmed, not reopened.

## 4. Findings left alone

- `OrderSettlementRobustnessTests`: 6 failures on a clean `main` (see the 2026-10-14 #166 journal); not touched.
- `TEST_SQL_CONNECTION` in the Windows user environment points at the **default** SQL Server instance
  (`SW-LAP-0043`, `sa`), which holds office databases (PurchaseOrderDb, daycare_db, WooComAppDb, …) and a few stale
  `SpencerDenim_Test_*`/`SpencerDenim_W113_*` databases. docs/06 §4 and docs/13 forbid exactly that. Every run for this issue
  overrode it to LocalDB for the process only; **the variable itself was not changed** and the stale databases were not
  dropped (not mine, possibly in use). Worth fixing on this machine.
- The checkout GET and the cart endpoints are unthrottled; each cart add is a database write.

## Evidence

All on LocalDB (`(localdb)\MSSQLLocalDB`), the process-level override described in section 4. Every test below was run red
against an unprotected build first.

**Red against the unprotected code, then green**

| Layer | Red (unprotected) | Green |
|---|---|---|
| Unit: `OrderPlacementThrottleTests` + `PlaceOrderCommandHandlerTests` (always-allow adapter, handler never asks) | 33 of 45 failed | 45 of 45 (57 of 57 with `ClientAddressPartitionTests`) |
| Integration: `CheckoutPlacementThrottleTests` (handler skips the permit) | 7 of 7 failed (the 2 hosting guards pass by design: they describe what already exists) | 9 of 9 with the hosting guards |
| Storefront: `checkout-api.spec.ts` + the new `checkout-page.spec.ts` case (sources unmodified) | 6 failed (69 passed) | 75 of 75 |

**Mutation checks** (one change at a time on committed code, then restored; each caught by the test named)

| Change | Caught by |
|---|---|
| Permit asked before the replay check (handler top) | unit `A_replay_of_a_placed_key_is_answered_with_its_order_without_touching_the_limit`, `A_request_refused_on_its_own_merits_costs_no_permit`; integration `A_resubmission_of_a_placed_order_does_not_consume_the_limit`, `Requests_that_would_not_place_an_order_are_not_counted` |
| Refused permit no longer re-asks the replay question | unit `A_refused_permit_still_replays_an_order_that_committed_in_the_meantime` |
| Both of the above together (an exhausted address's replay is refused with a 429) | integration `A_resubmission_is_answered_with_its_order_when_the_address_has_no_permits_left` (3 red with the two named above; on its own, asking the permit first is masked by the re-check, which is why the pair is mutated together) |
| Limit off by one (`PermitsPerWindow + 1`) | unit: the N / N+1 theory (1, 3, 7), defaults, concurrency, partition tests (10 red) |
| One global bucket instead of per address | unit `One_address_spending_its_whole_budget_does_not_refuse_another` and 12 others; integration `Two_addresses_do_not_share_a_bucket`, `A_refused_order_can_be_placed_later_with_the_same_bag_and_the_same_key` |
| IPv6 /64 and v4-mapped normalisation removed | unit `Addresses_in_one_ipv6_slash_64_share_a_bucket_and_another_network_does_not`, `An_ipv4_mapped_ipv6_address_spends_the_ipv4_addresses_bucket` |
| Shared budget never applied / always applied | unit `A_caller_that_is_not_a_visitor_gets_the_shared_budget` (10 cases) / `A_visitors_own_address_does_not_get_the_shared_budget` |
| Window ignored | unit `The_budget_comes_back_when_the_window_turns` |

**The expiry job, run for real (not committed):** a copy of the integration build started the real API host, placed an order over
HTTP (due in 48 h, `Reserved = 2`, `Available = 3`), back-dated its deadline, and waited for the host's own timer. The order was
`Expired` by the job at +312 s (the 5-minute tick), `Reserved = 0`, `Available = 5`, history row `SystemJob`.

**Existing checkout suites, unmodified, with the test-host lift:** `CheckoutConcurrencyTests`, `CheckoutEndpointTests`,
`CheckoutPlacementStoreTests`, `CheckoutPlacedIpTests`, `CheckoutPromoRaceTests`, `CheckoutRequestValidationTests`: 89 of 90 passed
in the first run, the one failure being a SQL command timeout (30 s, `GET /api/checkout` answered 500) on a machine at 100 % CPU
with other sessions' builds running; the same test passes on the final run below.

**Final run on the committed sources** (Debug, LocalDB): unit **3025 / 3025** (whole project); integration
`CheckoutPlacementThrottleTests` (7) + `ExpireUnpaidOrdersHostingTests` (2) + the whole of `CheckoutConcurrencyTests` (6):
**15 / 15**; storefront `checkout-api.spec.ts` + `checkout-page.spec.ts`: **75 / 75**. Not run, as instructed: the full
integration suite and Playwright.

## Tester round 1 (2026-10-19): PASS, one Major fixed

The independent tester passed the code and tests (all six mutations caught: permit before replay, off-by-one, one
global bucket, storefront 429 mapping, focus call, expiry-service registration; 108 checkout/expiry integration tests,
57 unit, 208 storefront checkout specs, 0 flaky) and raised:

| # | Finding | Status |
|---|---|---|
| 1 Major | **The tuning knob the docs promise cannot be turned in the deployed stack.** The API container in `deploy/app/compose.yml` has an explicit `environment:` list and no `env_file`; `OrderPlacementThrottle__*` set in `config/app.env` reached nothing | **Fixed.** compose maps `SD_ORDER_THROTTLE_PERMITS`, `_SHARED_PERMITS`, `_WINDOW_SECONDS` onto the three settings with the code's own defaults; `.env.example`, `docs/15` §3 and the README say so. New `OrderPlacementThrottleDeploymentTests` (4): red against the original compose.yml (3 fail, "does not pass … to the API container") and red when a compose default is drifted (20 -> 25), green now. **Not run:** `docker compose config` (Docker is off); indentation was checked against the neighbouring lines and there are no tabs |
| 3 Minor | `SharedAddressPermitsPerWindow` below `PermitsPerWindow` stops the API at start-up (by name) | Documented next to the variables in `.env.example` and `docs/15`; the start-up refusal is deliberate |
| 2 Minor | A double-submit of one key at the edge of the limit (address with one permit left) answers `201` and `429`, where before this change it was `201` and `200`; the replay re-check only sees an order that has committed. Pressing again returns 200 with the order | Left; rare, and the message to the shopper is correct on a retry |
| 4 Minor | `Retry-After` is always the full window (600 s), not the time to the next replenishment | Left; it is an upper bound and never 0 or negative |

Judgement calls the tester left to the owner: the default (20 per 10 minutes) is right for shoppers and weak against a
determined squatter (no cap on distinct cart lines, no cap on open unpaid orders per contact — see section 3 above);
and a CDN in front of the VPS would put every shopper on the CDN's edge addresses (the runbook plans none).
