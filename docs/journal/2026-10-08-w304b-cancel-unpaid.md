# 2026-10-08 — W3-04b / P10 flow A: cancelling an unpaid order

Branch `feature/W3-04b-cancel-unpaid`, worktree `worktrees/sd-cancel`, cut from `origin/main` at `e884d22`.
Scope: **doc 11 P10 flow A only** — the "Who can do what" table's first row, for the two callers it names.
**Not pushed, not a PR — the tester goes first.**

Continues `2026-10-08-w304-admin-orders.md`, whose "What is owed" item 2 is this task. Item 1 — the refund
screen — is deliberately **not** built here.

## What it does, end to end

A customer telephones to say they have changed their mind. A staff member searches the order number they
quote, opens the order, types why, and presses **Cancel the order**. The items go back on sale in the same
transaction, the open bank payment closes, and the order's timeline records that the shop closed it and the
reason. Nothing else about the order can be touched afterwards except the internal note.

Or the customer never telephones at all: they open the link in their own order page, press **Cancel this
order**, are asked once whether they are sure, and press **Yes, cancel it**. Same transaction, same
release, and the timeline records that *they* closed it.

A **paid** order offers neither, on either screen, and refuses one if asked — because doc 11 P10's table
says a paid order is refunded and never cancelled, and there is no refund in this build.

**Nothing claims an email was sent**, because nothing sends one — and doc 11 P10's notification list has no
email for a cancellation in any case (§7 G-14).

## P10 flow A, step by step, and what is actually built

| P10 flow A | Built | Where |
|---|---|---|
| 1. **[Customer]** opens the order (account or order link) and clicks **Cancel order** | ✅ **order link**; no account, because there is none | `order-page.ts`, `POST /api/orders/{token}/cancel` |
| 1. **[Staff]** clicks **Cancel** and enters a reason | ✅ reason **required** | `order-detail-page.ts`, `POST /api/admin/orders/{token}/cancel` |
| 2. Checks: order PendingPayment, stock Reserved, no active capture lock, no settling payment | ✅ all four, inside one conditional `UPDATE` | `OrderCancellationStore.ApplyAsync` |
| 2. If a PayPal payment is PENDING, only `Orders.Approve` may continue after confirming the warning | ⚠️ **the guard yes, the Approve branch no** — W4-01's, and documented as such | `CancelOrderRefusalCodes.PayPalPending` |
| 3. Order **→ Cancelled** with reason and closed time | ✅ | the claim |
| 3. Open payment → *Cancelled* | ✅ `Initiated`/`Pending` → `Cancelled`, set-based | the payment statement |
| 3. Stock **released** | ✅ **the expiry job's own loop**, extracted | `OrderStockRelease.ReleaseAsync` |
| 3. Promo use returned | ⚠️ **a no-op with nothing to write to**, and the call site exists | `OrderStockRelease.ReturnPromoUseAsync` |
| 3. History (source Customer or Staff) | ✅ and the source genuinely differs by caller | `OrderCancellationStore.WriteHistory` |
| Business rule: cancel and expiry skip an order with an active capture lock | ✅ shared with expiry, verbatim | the claim |
| Business rule: a paid order is never Cancelled; it is Refunded | ✅ structurally — `Status = 0` in the claim | the claim |
| Business rule: **Expired** = nobody paid (system); **Cancelled** = a person decided | ✅ `OrderCancelledBy` has no System member | doc 08 §4.1 G-09 |
| Notification: no email on cancellation (§7 G-14) | ✅ nothing queued, nothing claimed | both screens |

## `marketing.PromoCodes` does not exist, checked rather than taken

The brief said to check before writing anything that returns a promo use. It holds. `git grep` over
`Migrations/` finds exactly **one** table in the `marketing` schema — `NewsletterSubscribers`, from
`20260924095858_HomeContentAndNewsletter` — and `OrderConfiguration`'s own remarks say `Orders.PromoCodeId`
is a column with **no** foreign key for that reason. So `PromoCodeId` is null on every order ever placed
and returning a use that was never taken is nothing to do.

It is a **documented no-op method that both callers invoke**, rather than a comment, and the shape is
chosen so W3-05 can fill it in one place:

```sql
UPDATE marketing.PromoCodes SET UsageCount = UsageCount - 1
 WHERE Id = (SELECT PromoCodeId FROM sales.Orders WHERE Id = @o) AND UsageCount > 0;
```

Written against the **order id** rather than a promo id on purpose: it is then one statement and no extra
read, and the call sits where doc 08 §5.3's lock order puts it — after the stock rows, last.

## Decisions, and why

### The release is the expiry job's loop, extracted — not a second one

Doc 08 §4.1 gives `PendingPayment → Expired` and `PendingPayment → Cancelled` the same side effects, in as
many words ("As Expired, reason Cancelled"). So `OrderExpiryStore.ReleaseStockAsync` moved to
`OrderStockRelease.ReleaseAsync` and both paths call it. The three things a second copy would have been
free to get differently are the lock order (`VariantId` ascending, §5.3), the duplicate-variant merge, and
the row-count check — and each of those is an oversell or a deadlock that only one of the two paths could
produce. `OrderExpiryTests` (unchanged) and `OrderCancellationTests` now both prove the one loop.

### Its own store, not a member of `IOrderFulfilmentStore`

That interface's remarks say it touches no stock and therefore that §5.3's lock order "has nothing to
order" there. Cancel releases stock, so putting it beside the fulfilment transitions would have made a true
sentence false for one of five methods — the kind of comment that quietly stops being correct. It is also a
different permission question (P10's table, not P9's) and has a second, non-staff caller.

**And cancel did not become a way out of a closing state.** `IOrderCancellationStore` has one member.
Doc 08 §4.1 allows exactly one edge out of `Cancelled` — `Cancelled → Paid`, accept-late, which
`IBankTransferReviewStore.AcceptLateAsync` already is — and there is an integration test proving a
cancelled order can still take a late payment through it.

### The staff path reuses the transition endpoint; the customer path does not

Cancel is `OrderTransitionKind.Cancel` on the existing `TransitionOrderCommand`, so the `Orders.Edit`
policy, the antiforgery check, the `JsonObject` body reader, the 409-with-`code`-and-`orderStatus` mapping
and the screen's refusal classifier are all the ones W3-04 already built and tested. The handler answers it
**before** the fulfilment switch, because `IOrderCancellationStore` has its own result type: cancel can be
refused for reasons no fulfilment transition has — a capture in flight, a settlement already on the order —
and collapsing the two outcome types would have meant inventing a code for a state the other store cannot
be in.

The customer path is its own command, because it carries no staff actor, no reason, no permission, and
answers **one opaque refusal**.

### The customer is not asked for a reason, and `CancelReason` stays null

P10 step 1 asks the *staff* member for a reason and the customer only to press the button. So a customer
cancel leaves `Orders.CancelReason` null and the history row's source `Customer` is the record — which is
what the admin timeline already renders as "by the customer". Inventing a sentence of our own to put in the
column the admin screen prints under "Why it was cancelled" would be putting words in a shopper's mouth,
and offering a free-text box would open a public write into a staff screen that this task has no reason to
open.

### Every refusal on the customer route is a bare 404, and the cost is stated

`GET /api/orders/{token}` and the payment-reference route already answer one indistinguishable 404 for "no
such order" and "that order can no longer be paid", because the token never expires (§7 G-41) and is a
guest's only credential. Cancel follows it: a stranger holding a guessed GUID must not learn that it names
a real order, nor what state it is in.

**The cost is real.** A shopper whose press lost the race to the expiry sweep is told "this order can no
longer be cancelled" rather than "it expired a minute ago". The page's sentence covers every reason at once
and it then re-reads the order, which is where somebody holding the link is entitled to the detail. Doc 11
P10's edge case gives the PayPal case a distinguishable sentence ("payment is being processed") — saying
that would be a deliberate oracle, and it is W4-01's decision to make when there is a capture that can be
in progress.

An integration test compares the two problem documents field by field with the per-request `traceId`
removed, which is the only form of that assertion that says anything (see "What the tests corrected").

### The staff action requires the reason; the customer's confirms

Two different answers to "a confirmation step is the obvious place for a mistake".

**Staff**: the reason is **required**, refused client-side without a round trip and refused again on the
wire. Typing a sentence *is* the deliberate act, so there is no second "are you sure" — a dialog on top of
it would be a reflex to dismiss. It has its own box, labelled, separate from the internal note and from the
accept-late note: all three are staff writing and all three go somewhere different, and W3-04 shipped the
accept-late form sending the internal note's text as its audited reason.

**Customer**: two presses. The first reveals the question (which names the order, so a shopper with two
links open knows which one they are closing) and the second fires. It is an inline confirmation and not
`window.confirm` — that does not exist under server-side rendering, cannot be styled or announced, and is
the sort of thing people dismiss by reflex. "Keep my order" is the button they reach first, and the question
resets on every re-read so a page that has moved on never comes back with it half-asked.

### `canCancel` is sent by the server, on both contracts

P10 step 2 has four guards and **two of them are invisible to a screen**: a capture in flight, and a
payment that has already settled the order. A screen that offered Cancel for every `PendingPayment` order
would be offering an action the server will refuse, which is the dead-control shape this project has
already paid for.

It is a convenience and never the boundary — the claim re-decides all four under the order row's lock — and
the mitigation for having the predicate twice is the one #126 prescribes rather than a shared expression:
`CheckoutEndpointTests.The_order_payload_says_whether_the_shopper_may_cancel` asserts the flag on real rows
for **all six states**, through the real reader, beside the integration tests that assert the *transaction*
refuses the same ones.

### No migration, and it was checked rather than assumed

`Orders.CancelReason` is `nvarchar(500)`, `ClosedAtUtc` exists, and `OrderStatus.Cancelled = 5` exists.
`git diff origin/main -- …/Migrations/` is **empty** — not merely additive, empty — and
`has-pending-model-changes` is clean after a rebuild. Main's latest is still
`20261007055218_ContentPagesAndContactSubmissions`.

## Concurrency, measured

| Race | Outcome |
|---|---|
| Customer **Cancel** against the **expiry sweep**, one order, 6 rounds | Exactly one won (asserted as `^`, not "at least one"); stock released **once**; **one** closing history row; the payment closed in the winner's own terms (`Cancelled` or `Expired`); and the loser left no `CancelReason` behind on an order the sweep expired |
| Four staff pressing **Cancel** together | One applied, three refused with the current state; **one** history row and **one** audit row |
| Cancelling twice in sequence | The second finds `StockState = Released`, affects 0 rows, and the first caller's reason is still on the order |

Which of cancel and expiry wins is deliberately **not** asserted — either is correct, and an assertion that
picked one would be an assertion about scheduling.

## What the tests corrected in the code

| # | What | How it was found |
|---|---|---|
| 1 | **A comment claimed §4.2 step 3.3's savepoint fall-through could leave a settlement beside a `PendingPayment` order. It cannot** — that path ends with the payment in status **7 CapturedRequiresRefund**, which is not a settlement. The comment now says plainly that **no path in this build reaches the state**, that the guard is doc 11 P10 step 2's own, and that it is kept because it costs one `NOT EXISTS` and is the guard that would matter the moment a provider settles a payment in a transaction separate from the order's status — which is what a webhook is. | The fixture for that test failed against the database |
| 2 | `CK_Payments_IsOrderSettlement` allows the flag **only** on a payment in `Succeeded`, `PartiallyRefunded` or `Refunded`. The first fixture set it on the `Pending` row and SQL Server refused it, which is how the guard's reachability came to be checked instead of assumed. The reachable shape is a **Succeeded settlement beside a PendingPayment order**, and that is what the fixture writes. | the same failure |
| 3 | **The storefront refusal message lived inside the Cancel panel**, which is drawn on `canCancel` — and a refused cancel is followed by a re-read that almost always turns `canCancel` **false**, because the order has moved. So the panel disappeared taking the explanation with it, and **a shopper whose press lost the race to the expiry sweep was told nothing at all.** The message is now a sibling above the panel. | `order-page.spec.ts` |
| 4 | **The storefront e2e stub never sent `statusLabel`.** The page prints it rather than `status.toLowerCase()` (W3-04's decision 4), and nothing could move a storefront order off `PendingPayment` — so `undefined.toLowerCase()` was one pressable Cancel away. It also built payment instructions for **every** status, which `GetOrderQuery` does not: that method returns the order unchanged for any status but `PendingPayment`. Both fixed. | writing the e2e test |
| 5 | **A test that asserted an unreachable path.** The storefront's ADM-22 re-fire test failed, correctly: pressing Cancel, keeping the order and pressing again puts "Your order is still with us." between the two identical questions, so the second one *is* a change and the guard is never entered. Rewritten to assert what is true, naming the page's existing copy-twice spec as the guard's real coverage — the same resolution ADM-22 itself and W3-04's orders list both reached. | its own failure |
| 6 | **A backtick inside a `template:` literal, twice, on two screens** — including one in a comment I wrote *about* the 4 kB style budget, three lines under the existing warning "No backticks in this block: it is a template literal, and one would end it." 23 compile errors about class attributes. Both comments now say so again, with the fact that it happened. | the build |

## The style budget, measured both ways

`order-page.ts`'s component styles have a **4 kB** budget. The cancel panel's first four classes
(`.cancel`, `.cancel__ask`, `.cancel__choices`, `.cancel__note`, plus a second `aria-disabled` selector)
pushed the build over by **exactly 253 bytes**. Measured both ways — `origin/main`'s version of the file
builds clean; with those classes it warned — so it is this change's doing and not a pre-existing warning.

The panel now reuses `order__panel` for its grid, `pay__lead` for its 60 ch measure and `pay__actions` for
its button row, and the `aria-disabled` rule **dropped its `.pay` prefix** rather than gaining a second
selector. That is a net reduction, and it is the truer rule anyway: every `aria-disabled` control in this
component is a busy guard and every one of them should look unavailable. Angular scopes it to the
component.

## Mutation sweep: 10 of 13 killed, and the three survivors are explained rather than hidden

| Mutation | Result |
|---|---|
| Drop the no-settling-payment clause | red |
| Drop the capture-lock clause | red |
| Drop the PayPal PENDING guard | red |
| Always record the history source as `Staff` | red |
| Write an audit row for a customer cancel too | red |
| Leave the open payment open | red |
| Do not write `CancelReason` | red |
| Do not set `ClosedAtUtc` | red |
| Do not release the stock | red |
| `CanCancel` ignores the capture lock and the settlement | red |
| Drop the claim's `Status` guard **alone** | **15/15 green** |
| Drop the claim's `StockState` guard **alone** | **15/15 green** |
| Drop **both** | **4 of 15 red** |
| Storefront: one press cancels with no confirmation | red |
| Storefront: the refusal message back inside the panel | red |
| Storefront: draw the panel whatever the server says | red |
| Storefront: the busy guard does not refuse the activation | red |
| Admin: send the reason even when it is blank | red |
| Admin: send the internal note as the cancel reason | red |
| Admin: draw the cancel form whatever the server says | red |
| Admin: drop **either** busy guard alone | green |
| Admin: drop **both** busy guards | red |
| Do not return the promo use | green |

Three survivors, each for a reason now written into the code rather than left to be rediscovered:

- **`Status` and `StockState` in the claim are mutually redundant.** No reachable state has one without the
  other — every path that moves `Status` out of `PendingPayment` moves `StockState` in the same statement —
  so each is a sufficient guard by itself and neither can be mutation-proved alone. They both stay because
  doc 08 §4.1's table names both, and because the day a path writes one without the other is the day the
  surviving guard is the one that matters. The comment in `OrderCancellationStore` states the measurement
  and says in as many words that a test would **not** catch their removal one at a time.
- **The two admin busy guards** (`submitCancel` and `act`) are the same shape, and removing both turns the
  double-press spec red.
- **The promo-use call is a documented no-op** with nothing to write to.

## Test numbers

Run on this machine, `TEST_SQL_CONNECTION` pointed at `(localdb)\MSSQLLocalDB`.

| Suite | Before (`e884d22`) | **After** | New |
|---|---|---|---|
| xUnit unit | 1,812 / 0 | **1,814 / 0** | 2 |
| xUnit integration | 1,216 / 0 | **1,233 / 0** | 17 |
| Vitest — shared | 24 | **24** | — |
| Vitest — storefront | 1,407 | **1,413 / 0** | 6 |
| Vitest — admin | 459 | **465 / 0** | 6 |
| Playwright (chromium) | 928 / 0 | **935 / 0** | 7 |

The whole-assembly integration run took **16 m 40 s** and looked hung for most of it, which is what W3-04's
journal already warns about: the two `dotnet` processes sat on a few seconds of CPU while `sqlservr` did the
work. The check that settles it is `MAX(last_request_end_time)` in `sys.dm_exec_sessions` against the server
clock, and it was 60 ms behind the clock every time it was looked at. Nothing was killed.

My run's database (`SpencerDenim_Test_79c4fb18…`) was dropped by the fixture at the end. The **seven**
`SpencerDenim_Test_*`, the **eight** `SpencerDenim_W113_*`, `SpencerDenim_W103_Tester` and
`SpencerDenim_Dev` that remain are not mine and were not touched.

**The whole Playwright suite was 935/0 on its first run**, which is worth recording because W3-04's journal
says 928/0 "should be read as a best case rather than a steady state" — none of the four latent one-shot
races it names (`admin-catalog.e2e.ts`, `product-listing.e2e.ts:696`, `search.e2e.ts:647`,
`search.e2e.ts:661`) fired in this run. That is one observation, not a measurement of the rate.

**Ports:** all **seventeen** `E2E_*_PORT` variables (sixteen plus `E2E_PORT`) overridden into
**4880–4896**, the whole band scanned free first. Overriding is not optional: two of the config's own
defaults are **4432/4433** (`E2E_DEPT_FAULT_PORT` / `E2E_DEPT_FAULT_API_PORT`), which this machine's
protected list covers. `4502`, `4515`, `4767`, `5080` and `4683` were not touched and nothing was killed.

**Both bundles rebuilt** before every browser run — `npm run build` **and** `npm run build:admin`, because
the first is storefront only and the admin e2e server serves `dist/admin`.

### The worktree had no `node_modules`, and a junction to the main checkout's made Vitest lie

`src/frontend/node_modules` did not exist in this worktree. A directory junction to
`spencer-denim-ecommerce/src/frontend/node_modules` (identical `package-lock.json`, md5-checked) let the
bundles build — but the Vitest runs then failed **4, 1, 6 and 8 test files in four consecutive runs, in a
different set each time, in files this branch does not touch** (`auth-guards`, `audit-log`, `users-list`).
Every stack frame pointed through the junction with the path URL-encoded (`e%20com/spencer-denim-...`).

A real `npm ci` in the worktree did not fix it on its own; what settled it was fixing the two genuine
failures in my own spec, after which the admin suite went **35 files / 465 tests, 0 failures** and stayed
there. So the junction may be a red herring and the varying sets were cascades from a dirty `TestBed` —
either way, **a worktree should get its own `npm ci`**, and a run with failures in files you did not touch
is worth one more run before it is believed.

## What I deliberately did not build

- **Any refund.** Doc 11 P10's refund paths need `Orders.Refund` and a `sales.Refunds` writer; refunds stay
  manual for v1 (owner decision, 2026-10-08) and are recorded in the order's internal note. The admin
  screen offers no refund control and an e2e test asserts there is no button matching `/refund/i` on a paid
  order — so cancel cannot quietly become one.
- **Any PayPal.** The PENDING *guard* is built and tested; the `Orders.Approve`-with-warning branch that
  may continue past it is **not**, and is W4-01's. A warning about a refund is worth nothing while there is
  no refund.
- **Any sign-in.** P10 step 1 says "account **or** order link", the guest order page exists, and this did
  not wait for W3-01. Nothing in this branch touches `identity.*`, registration or login.
- **A migration.** Nothing in this branch changes the schema.
- **A cancel on the order *list*.** P10 step 1 puts the staff action on the order, and a list row has no
  room for a required reason. The list's focus re-homing was therefore not touched.
- **`inventory.InventoryMovements`** (#108) or `marketing.PromoCodes`.
- **An "Order cancelled" email**, or any sentence implying one.

## What I could not verify

- **A second browser engine.** Everything is Chromium, which is what `--project=chromium` gives.
- **Anything about the real API from a browser test.** Issue #126: Playwright never starts the .NET API, so
  every browser assertion here is about a screen against a Node stub. Each field the screens depend on is
  paired with an integration test that pins it on a real row — `canCancel` and `statusLabel` in
  `CheckoutEndpointTests`, the whole transaction in `OrderCancellationTests`, the admin contract in
  `AdminOrderEndpointTests` — and that pairing is the mitigation.
- **The PayPal PENDING branch end to end.** Nothing captures and the provider row is seeded disabled, so
  the state exists only as a fixture rewriting this test's own payment row.

## What is owed, and by whom

| # | Owed | By |
|---|---|---|
| 1 | **`Orders.Refund` and the refund screen.** Unchanged from W3-04's item 1, and now more visible: both cancel screens tell somebody a paid order is "refunded instead" and there is no system path for it. §7 G-13 ("money received on an order that was never paid") also still has none. | W3-05 |
| 2 | **`AdminOrderActions.CanAcceptLatePayment` does not light up on a *cancelled* order.** It requires a bank payment in `Expired` or `Pending`, and a cancel closes the payment to **`Cancelled`** — so the `Cancelled → Paid` edge doc 08 §4.1 allows, and which `AcceptLateAsync` really can apply (there is an integration test), **has no button**. That is not a regression, because nothing could cancel an order before today; it is a new gap this task created and did not close, because widening that flag is W3-03's transaction's own question (its `AcceptLateAsync` claims on `Status IN (5,6)` and then on the payment's status) and a one-line change to a neighbour's predicate is exactly the sort nobody reviews for the thing it claims. **A shop can cancel an order and then be unable to accept the transfer that arrives the next day.** | W3-05 / W3-03 |
| 3 | **The storefront order page cannot show a cancelled order's reason**, and does not try. A staff cancel writes one and the public contract has no field for it. Whether a shopper should read the shop's internal wording back is a decision, not an omission — but if the answer is yes it is a change to `OrderConfirmationDto` and belongs with ACC-02. | ACC-02 |
| 4 | **Rate limiting the order link** (§7 G-41). This route is the most destructive thing a token can do, and it has no throttle — consistent with the payment-reference route beside it, which `SubmitBankTransferReferenceCommandHandler` already records as an architect decision rather than a task's. | Architect |
| 5 | **`ops.EmailOutbox`.** Nothing here claims otherwise, and P10 owes no cancellation email in any case. | W3-06 |
| 6 | **`inventory.InventoryMovements`** (#108). The release writes exact figures and no per-movement trail, which is `OrderExpiryStore`'s existing state and unchanged. | W1-05 / ADM-12 |
| 7 | No `progress-data.js` change, no `MEMORY.md`, no `TASKS.md` (doc 14's rule). | owner's session |
