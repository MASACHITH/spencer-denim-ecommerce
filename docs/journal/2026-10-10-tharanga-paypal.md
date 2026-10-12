# 2026-10-10 — Tharanga: PayPal checkout (#54, W4-01, PAY-02)

Branch `feature/W4-01-paypal`, worktree `worktrees/sd-paypal`, cut from the **unmerged** payment groundwork
(`feature/W4-01-payment-groundwork`, journal `2026-10-10-tharanga-payment-groundwork.md`). That groundwork is
still being corrected by the lead (provider-code pinning in `SettleAsync`, length limits, printable-ASCII
idempotency keys, a stricter `IsSafeAbsoluteUrl`); this branch is written against those expectations and will
be rebased onto the corrected groundwork. **Not pushed, no PR.** **No database migration.**

## What was built

A customer who picks PayPal at checkout is sent to PayPal to approve a USD payment, and the order becomes Paid
when the shop has **captured** the money (never because the buyer's browser came back). The webhook and a
recovery job make that true even when the browser never returns, the capture times out, or PayPal is slow.

### Backend (Clean Architecture; the layering tests hold)

| Layer | Where | Key names |
|---|---|---|
| Domain | `Domain/Settings/SettingKeys.PayPal.cs`, `PayPalSettingDefinitions.cs`, `SettingCatalog.cs` | 12 setting definitions, group `Payments.PayPal` |
| Application | `Application/Sales/Payments/PayPal/` | `PayPalSettings`, `PayPalUsdAmount`, `PayPalRequestIds`, `PayPalInitiationPolicy`, `IPayPalGateway` (+ result records), `PayPalApplyDecision`, `PayPalRecoverySchedule`, `IPayPalPaymentStore`, `PayPalCaptureApplier`, `PayPalWebhookEvent`, `IPayPalCheckoutFlow`, `IPayPalWebhookHandler`, `PayPalLinks`, `PayPalPageHints`, `CaptureRecoveryFailedAlert` |
| Application (shared, additive) | `Sales/Payments/Abstractions/IPaymentEventRetry.cs`, `IPaymentProvider.IsAvailableAsync` (default `true`), `Settings/SensitiveSettingsChangedAlert.cs`; the save command and its rules are `Payments/PayPal/Admin/UpdatePayPalSettingsCommand.cs` and `PayPalSettingRules.cs` | provider-neutral retry port; availability gate |
| Infrastructure | `Infrastructure/Sales/Payments/PayPal/` | `PayPalProvider`, `PayPalHttpGateway`, `PayPalTokenCache`, `PayPalPaymentStore`, `PayPalCaptureProcessor`, `PayPalCheckoutFlow`, `PayPalWebhookProcessor`, `PayPalCaptureRecovery`, `PayPalEventLog`, `PayPalSettingsReader`, `AddPayPalPayments()` |
| Infrastructure (shared, additive) | `Sales/Payments/DuePaymentEvents.cs`, `PaymentEventRetryRunner.cs`, `PaymentSweepServices.cs`, `Sales/CheckoutReader.cs` | the two background sweeps; checkout only offers a provider that says it is available |
| Api | `Endpoints/Sales/PayPalEndpoints.cs`, `Http/PayPalEndpointThrottle.cs`, `Endpoints/Admin/AdminSettingsEndpoints.PayPal.cs` | endpoints below |

### Endpoints

| Method and path | Auth | What it does |
|---|---|---|
| `GET /api/payments/paypal/start/{publicId}` | none (the order's unguessable public id is the capability, throttled) | Creates the PayPal order (idempotent per payment attempt, `PayPal-Request-Id`) and 302s the buyer to PayPal; on any refusal 302s to the order page with a `?pay=` hint |
| `GET /api/payments/paypal/return/{publicId}` | none | The buyer came back. **Never proof of payment.** Runs the claim-capture-apply path and 302s to `/order/{token}?pay=paid|pending|processing|declined|received|notapproved|closed|unavailable` |
| `GET /api/payments/paypal/cancel/{publicId}` | none | The buyer left PayPal. Closes the unapproved attempt and 302s with `?pay=cancelled` |
| `POST /api/payments/paypal/webhook` | PayPal's signature, verified by PayPal (`verify-webhook-signature`) | 200 handled or duplicate; 400 not verified or malformed; 503 when verification itself could not run (PayPal retries); 429 throttled; body capped at 256 KB; `Cache-Control: no-store` |
| `PUT /api/admin/settings/paypal` | `Settings.Payments` | The admin panel's save (see below) |

### Settings keys (group `Payments.PayPal`, plus one in `Orders`)

| Key | Default | Meaning |
|---|---|---|
| `Payments.PayPal.Mode` | `sandbox` | `sandbox` or `live`. Chooses the PayPal API host and checkout host |
| `Payments.PayPal.ClientId` | empty | REST app client id |
| `Payments.PayPal.ClientSecret` | empty | **Secret.** Stored with Data Protection, never returned by any endpoint, blank on save keeps the stored one |
| `Payments.PayPal.WebhookId` | empty | The id PayPal gives the registered webhook; required to verify signatures |
| `Payments.PayPal.LkrPerUsd` | empty | The rate. PayPal is **not offered** until it is set |
| `Payments.PayPal.FxBufferPercent` | `3.00` | Added on top of the rate: `USD = round(total / rate x (1 + buffer/100), 2)`, half away from zero, minimum 0.01 |
| `Payments.PayPal.RateMaxAgeDays` | `7` | PayPal is not offered when the rate was stamped longer ago than this |
| `Payments.PayPal.RateUpdatedAtUtc` | empty | **Server-set only**: stamped when the rate changes or when staff tick "Confirm the rate is current" |
| `Payments.PayPal.CaptureLockHours` | `3` | How long a claimed capture blocks the order from expiring |
| `Payments.PayPal.CaptureRecoveryMaxHours` | `24` | After this long in `Capturing`, recovery gives up (`RECOVERY_TIMEOUT` plus an alert) |
| `Payments.PayPal.PendingCaptureMaxDays` | `5` | A PENDING capture holds the order until `max(due, placed + this)` |
| `Orders.PayPalExpiryMinutes` | `60` | How long an order paying by PayPal is held; a DENIED capture cuts the hold back to `min(due, now + this)` |

PayPal is offered at checkout only when **all** of these hold: the `sales.PaymentProviders` row for `PayPal`
is enabled, the credentials and webhook id are present, the rate is set and fresh, and `Email:StorefrontBaseUrl`
is configured (PayPal has to be given return addresses).

### The capture protocol (doc 08 section 4.2, followed exactly)

1. **Claim**: one transaction, order row first. `Orders.CaptureLockedUntilUtc = now + CaptureLockHours` and the
   payment `Initiated -> Capturing`, both as conditional `UPDATE`s whose row counts decide who owns the capture.
   A second tab, the webhook and the recovery job all lose that race and read the outcome instead.
2. **Capture**, outside any transaction, with `PayPal-Request-Id: capture-{PaymentId}`. **There is no retry of
   a capture inside a request.** A timeout leaves the payment `Capturing`.
3. **Apply**, through the shared `IOrderSettlementStore.SettleAsync` / `RouteToExceptionAsync`, by the decision
   table in `PayPalApplyDecision`: COMPLETED with the right amount and currency settles; a mismatch, an expired
   or cancelled order, a second payment for a paid order or a currency other than USD is routed to a payment
   exception with a `CapturedRequiresRefund` alert; PENDING holds the order; DENIED cuts the hold back and lets
   the buyer try again.
4. **Recovery** (`RecoverPayPalCaptures`, every 2 minutes): finds payments stuck in `Capturing`, asks PayPal for
   the order (`GET`), and applies what it says. Backoff is derived from `CaptureStartedAtUtc` / `UpdatedAtUtc`
   (no new column): first look 2 min after the claim, then gaps of 2, 5, 15, 30 and 60 minutes (cumulative
   2, 4, 9, 24, 54 and 114 minutes), then every 60 minutes; past `CaptureRecoveryMaxHours` it gives up
   with `RECOVERY_TIMEOUT` and raises the `CaptureRecoveryFailed` alert in the same transaction.
5. **Webhook**: verified first (headers pre-checked: certificate URL must be https on `*.paypal.com`; then
   PayPal's own verification call with the **raw** body bytes). The event is recorded with idempotency key
   `PayPal:{eventId}` before it is acted on; a duplicate is a 200 no-op. It goes through the **same** apply path
   as the return, so a webhook and a return racing each other settle once. Out-of-order events (PENDING after
   COMPLETED) and replays change nothing. A late COMPLETED for an order that already expired is applied under
   rule R1 (the money is real).
6. **Event retry** (`RetryPaymentEvents`, every 2 minutes, provider-neutral): re-processes webhook events that
   were recorded but not finished (a crash, a transient SQL failure), claimed with `UPDATE TOP ... OUTPUT` plus a
   lease on `NextAttemptAtUtc`, 10 attempts at most. There is no `ops.JobLeases`; the claim SQL is the guard.

`UX_Payments_OneOpen` allows one `Initiated`/`Pending` payment per order, and `Capturing` is not one of those,
so a retry while a capture is in flight would open a second attempt next to a live one. **A "pay again" is
refused while the previous attempt is `Capturing` or `Pending`**, and the buyer sees "being processed".

### Admin panel (Settings screen, "PayPal")

Mode, client id, a **write-only** secret box (the server never sends the secret back; the panel shows only
"a secret is saved"), webhook id, rate, buffer, rate max age, the capture timings, the PayPal order hold, a
"Confirm the rate is current" button and a readiness line listing what is still missing. Saves go through
`UpdatePayPalSettingsCommandHandler`: validate, optimistic concurrency, permission over the keys actually asked
for, diff, audit (secret values never in the audit), and a **Critical `SensitiveSettingsChanged` alert** when
the mode, client id, secret, webhook id, rate or buffer changes.

### Storefront

The order page is provider-neutral. For an online provider it prints the provider's own fields ("Amount charged
by PayPal", "Exchange rate", "Currency buffer") and notes, shows a **Pay now** button that goes to the start
endpoint, and shows a fixed sentence for the `?pay=` hint (picked from a whitelist; nothing from the address is
ever displayed, and no hint ever says "paid"). After a `processing` return it re-reads the order every 5 s,
12 times at most, in the browser only.

### Money that landed but could not settle the order (shared, provider-neutral)

Added after the PayHere tester's finding, and it is **not PayPal's**: any provider's payment that ends in
`CapturedRequiresRefund` (status 7) is covered, because it reads the payment's status and nothing else.
Doc 11 P8 step 6 / P8b step 4: "We received your payment, our team will contact you". Before this the page said
the order had expired (or "we are still confirming the payment details... nothing to transfer yet" for an amount
mismatch) with no word about the money, and the `?pay=received` hint only covered the browser-return path, not a
notification that landed while the browser was closed.

- **DTO**: `OrderConfirmationDto.PaymentNotice` (`string?`, last positional parameter, default `null`), with the
  value constants in `OrderPaymentNotices` (`Received = "received"`), both in
  `Application/Sales/Checkout/Contracts/OrderConfirmationDto.cs`. TS: `OrderConfirmationDto.paymentNotice?: string | null`
  in `core/checkout/checkout.model.ts`.
- **Derived from the database on every read**, in `CheckoutReader.FindOrderWhereAsync` (one extra `EXISTS` in the
  existing projection, no extra round trip): any payment of the order with status 7. So a reload, a later visit and a
  webhook that arrived with the browser closed all show it, and it disappears when staff refund the payment (the
  status leaves 7).
- **Carries nothing else**: no provider, amount, payment id or capture id. The order link is the credential, and
  the integration tests assert the raw JSON contains none of those.
- **No way to pay beside it**: `GetOrderQueryHandler` returns the order as read, without asking a provider, when
  the notice is set, so `Payment` and `Proof` are null (no bank account, no Pay now). The page also refuses to draw
  a redirect panel if instructions ever arrive with a notice, and the "Payment" line stops saying "Awaiting
  payment". PayPal's start endpoint refuses a new attempt while a payment is in status 7
  (`PayPalBlock.CapturedAwaitingStaff`, decided inside the attempt's own transaction after the order row is held),
  redirecting with `?pay=received`; `PayPalOrderView.HasUnsettledCapture` is the read-side copy.
- **Wording** (`core/checkout/payment-notice.ts`, a whitelist; an unknown code shows nothing): an order that is
  waiting, expired or cancelled reads "We received your payment, our team will contact you. You do not need to pay
  again."; an order that is already paid (a duplicate capture) reads "We received a second payment for this order.
  Our team will contact you about it, and you do not need to pay again." The `?pay=received` hint is suppressed when
  the order carries the notice, so the page does not say it twice.
- **Pending and declined copy** was already derived from the database (the provider's `Describe` reads the open or
  latest payment), not from `?pay=`; only status 7 had no database-derived message. Unchanged.
- **Not changed, deliberately**: `CanCancel` (the Cancel panel still shows for an order that is still
  `PendingPayment` with a status-7 payment). Cancelling is harmless there (staff refund a payment on a cancelled
  order, doc 11 P8 step 11) and hiding it would have meant a second copy of the cancel predicate in the read model
  that the cancellation store does not share.
- Bank-transfer orders and orders with no payment in status 7 are byte-for-byte what they were
  (`GetOrderQueryHandlerTests`, the order-page spec "keeps the bank rendering exactly as it was", and the existing
  `CheckoutEndpointTests`).

## How to test against the real PayPal sandbox (once credentials exist)

You need a PayPal developer account (developer.paypal.com), which the client will provide, and an HTTPS public
address PayPal can reach for the webhook (a tunnel such as `cloudflared` or `ngrok` is fine for the sandbox).

1. **Create a sandbox REST app** (Apps and Credentials, Sandbox, Create App, merchant type). Copy the
   **Client ID** and **Secret**. Note the sandbox **business** account the app uses and create or reuse a
   sandbox **personal (buyer)** account (Sandbox accounts).
2. **Run the API with a public https origin**: set `Email__StorefrontBaseUrl=https://<your-tunnel-host>` and
   point the tunnel at whatever serves the storefront and `/api` on one host (as Caddy does). PayPal return
   addresses and the order page's Pay link are built from this value. It must be https and a real host name:
   `localhost`, loopback, private ranges and IP literals are refused by the redirect safety check, so on plain
   `localhost` the order page shows the details but no Pay now link.
3. **Register the webhook**: in the app, Webhooks, Add Webhook, URL `https://<your-tunnel-host>/api/payments/paypal/webhook`,
   events `PAYMENT.CAPTURE.COMPLETED`, `PAYMENT.CAPTURE.PENDING`, `PAYMENT.CAPTURE.DENIED`,
   `PAYMENT.CAPTURE.DECLINED`, `PAYMENT.CAPTURE.REFUNDED`, `PAYMENT.CAPTURE.REVERSED`. Copy the **Webhook ID**.
4. **Fill the admin panel** (Admin, Settings, PayPal): Mode `sandbox`, Client ID, Client secret, Webhook ID,
   the LKR per USD rate, and tick **Confirm the rate is current**. Save. The readiness line should read ready.
5. **Enable the provider row** (no admin toggle exists yet, see open items):
   `UPDATE sales.PaymentProviders SET IsEnabled = 1 WHERE Code = 'PayPal';`
6. **Happy path**: add a product to the cart, check out choosing PayPal. The order page shows the USD amount, the
   rate and the buffer; click **Pay now**; log in to the sandbox with the buyer account and approve. You land on
   the order page with "paid" (or "processing" for a moment, then paid). Check, in the database:
   `sales.Orders.Status = Paid` (Paid), `sales.Payments.Status = Succeeded` with `ProviderTransactionId` (the
   capture id) and `ProviderAmount` in USD, and `sales.PaymentEvents` rows: `api.create-order`,
   `api.capture` and a `PAYMENT.CAPTURE.COMPLETED` webhook (source 1) marked processed.
7. **Webhook alone**: place another order, start, approve, but **close the browser on PayPal's page before
   returning**. The webhook should still mark the order Paid within seconds. If it does not, look at
   `sales.PaymentEvents` for the event and its `LastError`; the retry job picks up unfinished webhook events
   every 2 minutes.
8. **Verification failure**: temporarily set a wrong Webhook ID; deliver an event from the PayPal dashboard
   (Webhooks simulator). The endpoint answers 400 and nothing is recorded as paid. Restore the id.
9. **Pending**: in the sandbox, set the **buyer account's** or the business account's payment review
   (Sandbox accounts, account settings, "Payment Review"; or use the negative-testing header documented at
   developer.paypal.com/tools/sandbox/negative-testing if the account allows it). A PENDING capture should leave
   the order PendingPayment with "pending" on the order page and extend the hold to
   `max(due, placed + PendingCaptureMaxDays)`.
10. **Declined**: use a sandbox buyer funding source that PayPal declines (the negative-testing instrument for
    `INSTRUMENT_DECLINED`). The order page should offer "try again" and the hold should be cut to
    `min(due, now + PayPalExpiryMinutes)`.
11. **Lost answer**: stop the API between the capture request and its answer is hard to do by hand; the
    integration tests (`PayPalRecoveryTests`) cover it with a scripted gateway. If you want to see it live,
    block outbound traffic to `api-m.sandbox.paypal.com` for a few seconds right after pressing approve.
12. **Stale rate**: set the rate stamp older than `RateMaxAgeDays` (set it directly:
    `UPDATE ops.Settings SET [Value] = '2026-01-01T00:00:00.0000000Z' WHERE [Key] = 'Payments.PayPal.RateUpdatedAtUtc';`).
    PayPal disappears from the checkout's payment methods.

Live mode is the same with Mode `live`, the live app's credentials and webhook id, and the live webhook
registered; do it once, with a small real purchase, after the client signs off.

## Decisions

- **Typed gateway behind a port (`IPayPalGateway`).** All of PayPal's HTTP lives in one class; everything else
  is tested with a scripted `FakePayPalGateway`. The real client is unit tested over a stubbed
  `HttpMessageHandler` (request shapes, token cache and its one 401 resend, error mapping, header checks).
- **Token cache** keyed by a SHA-256 of mode + client id + secret, so changing credentials in the admin panel
  takes effect on the next call without a restart and no secret is ever a dictionary key or a log value.
- **One apply path.** The return, the webhook, the recovery job and the retry job all end in
  `PayPalCaptureApplier` -> `IOrderSettlementStore`. The concurrency control is the conditional `UPDATE` row
  count, not a lock or a job lease.
- **No new column and no migration**: the recovery backoff is computed from timestamps that already exist.
- **PayPal types live in `Application.Sales.Payments.PayPal`**, not under `Checkout`, because
  `CheckoutNamesNoProviderTests` forbids naming a provider there.
- **Public origin reuses `Email:StorefrontBaseUrl`**, the only configured public origin. It is never taken from
  a request's `Host` header.
- **The retry job is provider-neutral** (`IPaymentEventProcessor` per provider, resolved by event provider
  code), so PayHere only registers its own processor. `AddPayPalPayments()` uses `TryAdd` for the neutral
  pieces so the two providers' registrations do not collide.
- **Availability** is a default interface member on `IPaymentProvider` (`IsAvailableAsync`, `true`), so
  `BankTransferProvider` is untouched; `CheckoutReader` filters through `IPaymentProviderRegistry`.
- **Customer copy** follows doc 11 P8; nothing says a payment succeeded except the order's own status.
- **Logging ids** 3501 to 3591 are used by the PayPal classes.

## Open items and places I was unsure or deviated

1. **`Orders.PayPalExpiryMinutes` requires `Settings.Edit`, not `Settings.Payments`.** The settings catalogue
   test `No_save_unit_spans_two_permissions` forbids a save unit that mixes permissions, and this key lives in
   the `Orders` group beside the bank hold. A person with only `Settings.Payments` can change everything else
   in the PayPal panel but not this one value (the panel leaves that field out for them, and the server rejects
   it if it is sent).
2. **No admin toggle to enable the PayPal provider row**; it is the SQL in step 5 above. The groundwork did not
   add a `PaymentProviders` admin screen and it is outside this issue.
3. **Admin order detail was not extended** (PayPal order and capture ids, the event log). The existing payment
   block already shows `ProviderAmount`/`ProviderCurrency` and `PayerReference`; the ids and event log would
   have touched the shared order DTO, reader and screen, which is not cheap and would conflict with PayHere.
4. **Refund and reversal webhooks are recorded and logged only.** `PAYMENT.CAPTURE.REFUNDED` and `.REVERSED`
   are linked to the payment when it can be found, marked Ignored, and change nothing. Doc 08 section 4.3's
   handling (refund row, dashboard refund alert) is not built; it belongs with the refunds task.
5. **No staff e-mail** for a payment exception or `CaptureRecoveryFailed`; they raise the admin alert banner
   (W4-02a) only.
6. **A residual 500**: if the rate goes stale (or the settings are changed) in the instant between the
   availability check and `InitiateAsync` during placement, `InitiateAsync` throws and
   `PlaceOrderCommandHandler` does not catch it. It needs a provider-neutral "provider became unavailable"
   result in checkout; I did not touch the checkout handler.
7. **Every routine rate update raises a Critical `SensitiveSettingsChanged` banner**, as doc 08 DB2 specifies
   for the rate. The shop may find it noisy; the owner decides. The bank panel does not raise this alert
   (follow-up, not part of this issue).
8. **The approval URL** is taken from PayPal's `payer-action` link, falling back to
   `{checkoutBase}/checkoutnow?token={orderId}` when the link is missing. **Verify in the sandbox** that the
   `payer-action` link is returned for our request shape.
9. **`application_context` vs `payment_source.paypal.experience_context`**: I used the request shape I am most
   sure the Orders v2 API accepts for a redirect flow (`payment_source.paypal.experience_context` with
   `return_url`, `cancel_url`, `user_action = PAY_NOW`). PayPal's 422 `issue` strings I map
   (`INSTRUMENT_DECLINED`, `ORDER_NOT_APPROVED`, `ORDER_ALREADY_CAPTURED`, `RESOURCE_NOT_FOUND`) are from the
   documentation, not from a live call. **Both should be confirmed in the sandbox**; the gateway is the only
   place that would change.
10. **Plain `localhost` development has no Pay link** on the order page, because the stricter
    `IsSafeAbsoluteUrl` (lead's groundwork fix) refuses loopback and non-https. Use the tunnel in step 2.
11. **End-to-end browser tests** (Playwright) for the PayPal path were not written; PayPal's own pages cannot be
    driven without sandbox credentials. The storefront and admin specs, and the HTTP-level integration tests,
    cover everything on our side of the redirect.

## Shared files touched (expect small merge conflicts with PayHere, all additive)

`Infrastructure/DependencyInjection.cs` (two lines: the provider and `AddPayPalPayments()`),
`Api/Program.cs` (throttle, two `Map...` calls), `Domain/Settings/SettingKeys.cs` (now `partial`),
`SettingCatalog.cs` (definitions and one save unit), `AdminSettingsEndpoints.cs` (now `partial`),
`Application/DependencyInjection.cs` (one handler), `CheckoutReader.cs` (registry filter),
`IPaymentProvider.cs` (default member), `PaymentProviderContractTests.cs` (stub), the settings catalogue tests,
and on the storefront `order-page.ts` and `payment-redirect.ts` (notice kind, fields and notes, hint banner).

## Test hygiene found on the way: shared database, fixed capture ids

The whole integration run shares one database (a fresh one per run, shared by every class), and a webhook finds its
payment by the **capture id first**. The settlement store's tests (`OrderSettlementStoreTests`) leave a PayPal
payment with `ProviderTransactionId = "CAP-DUP"` behind, and `PayPalWebhookTests` used the same literal, so when
both classes ran in one pass the webhook was matched to that other order's payment and
"The_same_capture_under_a_new_event_id_is_a_duplicate_and_is_ignored" found no event on the payment under test
("Sequence contains no matching element"). Reproduced by running `OrderSettlementStoreTests` with that one test.
It was a test-data collision, **not** an incompatibility with the groundwork fixes and not load. Fix: every
literal capture id in the PayPal tests now goes through `Cap("...")` in `PayPalTestBase`, which appends a per-run
tag, so none of them can equal a row another class left behind. (The ids the fake gateway makes were already
unique per instance.) Worth knowing for PayHere: any test that posts a fixed provider transaction id into the
shared database can be matched by an earlier class's leftovers.

## Tests

See the commit messages and the lead's report for the counts and the last run. Unit: `PayPalUsdAmountTests`,
`PayPalSettingsTests`, `PayPalApplyDecisionTests`, `PayPalRecoveryScheduleTests`, `PayPalWebhookEventParserTests`,
`PayPalSettingRulesTests`, `PayPalAlertTests`, `PayPalHttpGatewayTests`. Integration (`Sales/PayPal`):
`PayPalCheckoutTests`, `PayPalCaptureFlowTests`, `PayPalWebhookTests`, `PayPalRecoveryTests`,
`PayPalSettingsEndpointTests`. Frontend: `settings-page.spec.ts` (22 new), `payment-redirect.spec.ts`,
`payment-hint.spec.ts`, `order-page.spec.ts` (10 new).

## Verified against the real PayPal sandbox — 2026-10-12

A sandbox REST app and a sandbox buyer exist now (credentials were supplied in chat and are **not** in the repository, a
commit or a log; the secret should be regenerated now that it has been in a conversation). What was run, in order, all
against `api-m.sandbox.paypal.com`, with the request body `PayPalHttpGateway.CreateOrderBody` writes:

| Step | Result |
|---|---|
| `POST /v1/oauth2/token` (client credentials) | 200, a 9 h bearer token |
| `POST /v2/checkout/orders`, USD 10.00, `PayPal-Request-Id` set | **200** (not 201), `PAYER_ACTION_REQUIRED`, links `self` and `payer-action` = `https://www.sandbox.paypal.com/checkoutnow?token=…` |
| Buyer opens that link, signs in, **Complete Purchase** | PayPal shows the shop name (`brand_name`) and USD 10.00; redirects to our `return_url` with `?token=…&PayerID=…` |
| `POST …/capture`, body `{}`, `PayPal-Request-Id: capture-…` | **201**, order `COMPLETED`, capture `COMPLETED`, USD 10.00, **`custom_id` echoed on the capture**, `final_capture: true` |
| The same capture again, same request id | **200** with the identical capture — no second charge |
| `GET …/orders/{id}` | 200, `COMPLETED`, agrees with the capture answer on every field the gateway reads |

**Open items 8 and 9 are closed.** The `payment_source.paypal.experience_context` shape is accepted as written; PayPal
returns `payer-action`, on `www.sandbox.paypal.com`, which passes the gateway's "https on `*.paypal.com`" check. Two
corrections to what the hand-written tests assumed: creation answers **200**, and a repeated capture answers **200** — both
are 2xx and the gateway reads any 2xx as success, so no code changed, but a gateway that had insisted on 201 would have
failed on the very first real call.

**What is now pinned.** `PayPalHttpGatewayTests`, section "recorded from the real sandbox": the create-order body PayPal
accepted, compared whole; PayPal's real create, capture (as 201 and as the repeat's 200) and read-back answers (only the
buyer's name and email masked) read as the right ids, statuses, amount and currency; and the buyer's personal fields
reaching neither the result nor a log. **Proven by mutation:** changing `landing_page` fails the pinned-body test;
adding 1 to the parsed amount fails both real-capture tests with `Expected 10.00, Actual 11.00`.

**Not verified, and not claimed:**
- **Webhooks** (steps 3, 7, 8 above): they need a public HTTPS address PayPal can reach, and no tunnel was available. The
  signature verification call, the `verify-webhook-signature` request body and the event shapes are still written from
  documentation.
- **The application end to end.** A sandbox payment was driven through the gateway's own request and PayPal's own pages,
  not through checkout, the order page and the admin panel — on `localhost` the order page shows no Pay link by design
  (the groundwork's `IsSafeAbsoluteUrl` refuses loopback and non-https).
- **PENDING, DENIED and declined captures** — only the happy path was run.
- **The sandbox buyer's currency conversion** — the buyer paid from a USD balance; nothing was learned about LKR.
