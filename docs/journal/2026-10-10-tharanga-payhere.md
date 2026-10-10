# 2026-10-10 — Tharanga: PayHere checkout (#141, W4-01b)

Branch `feature/W4-01b-payhere`, worktree `worktrees/sd-payhere`, on top of `feature/W4-01-payment-groundwork`
(the shared seams) and the docs commit for P8b. **A change request outside the signed scope** (doc 11 G-43):
the `PayHere` provider row ships **disabled** and nothing in this build switches it on. **No migration**: the
provider row and the settings are rows, `Payments.ProviderReference` holds PayHere's `payment_id`, and the
conversion columns stay NULL because PayHere charges in LKR. **Not pushed, not a PR.**

## What a customer and a Super Admin get

- **Checkout** lists PayHere only when the provider row is switched on **and** the merchant ID, the secret and
  a valid mode are set **and** the server knows the shop's public address (`Email__StorefrontBaseUrl`). A
  request that names it while it is hidden is refused with the existing "not available" message.
- **Placing an order** with PayHere writes an `Initiated` LKR payment due after
  `Payments.PayHere.ExpiryMinutes` (60). Checkout still asks the provider two questions and names no gateway.
- **The order page** is handed a signed form (`redirectForm`, POST to `sandbox.payhere.lk` or `www.payhere.lk`
  by mode) and renders "Pay now". The form carries only values from the server's own order and a *hash* of the
  merchant secret, never the secret. While PayHere holds a payment as pending the page shows a notice and no
  button. Coming back from PayHere (`?payment=return`) shows **"Checking your payment…"** and polls for about a
  minute and a half; `?payment=cancelled` welcomes the shopper back and keeps the form.
- **PayHere's notification** posts to `POST /api/payments/payhere/notify` and is the only thing that pays the
  order.
- **The settings screen** has a PayHere panel for `Settings.Payments` holders: mode, merchant ID, a
  write-only merchant secret, the pending hold, the payment window; beside it whether checkout offers PayHere,
  what is missing, the **notify URL to register in the PayHere portal** and the reminder that the domain must
  be approved there.

## The notify endpoint

`POST /api/payments/payhere/notify` — anonymous, `application/x-www-form-urlencoded` only, 16 KB ceiling, no
antiforgery (no session to forge). Answers are bare status codes with empty bodies:

| Answer | When |
|---|---|
| **200** | verified and stored (first delivery), a redelivery, or a verified notification that names no payment of ours |
| **400** | not shaped like a notification (missing/oddly-shaped signed field, a signed field posted twice), merchant ID not ours, or `md5sig` does not verify — **nothing stored** |
| **415** | not form-encoded |
| **503** | PayHere is not configured here (no secret / merchant / mode): nothing can be verified, so PayHere should try again |

Order of operations (`PayHereNotifyCommandHandler`): shape → configured → merchant ID (constant time) →
`md5sig` (constant time, over the strings exactly as posted) → `IPaymentEventStore.RecordAsync` (key
`PayHere:{payment_id}:{status_code}`, source `VerifiedWebhook`) → apply (`PayHereNotificationProcessor`) → mark
the event. A redelivery (`IsNew = false`) answers 200 and stops. A failure to apply is caught, the event is
marked Failed with a retry time, and PayHere still gets 200.

The stored payload is an **allow-list** (`merchant_id`, `order_id`, `payment_id`, `payhere_amount`,
`payhere_currency`, `status_code`, `status_message`, `method`): never `card_holder_name`, `card_no`,
`card_expiry`, custom fields or `md5sig`. `status_message` and `method` are outside the signature, so they are
kept as bounded, control-stripped text and nothing is decided from them.

| `status_code` | What happens |
|---|---|
| **2** | `IOrderSettlementStore.SettleAsync` with the amount parsed from `payhere_amount` and its currency; the store checks both against the payment's LKR amount, pays the order or keeps the money as `CapturedRequiresRefund` + alert. PayHere's `payment_id` is written to `ProviderReference` first and to `ProviderTransactionId` by the settlement. |
| **0** | `Initiated → Pending`, order due = `max(due, placed + PendingMaxDays)`, one transaction, order row first. |
| **−1 / −2** | open payment → `Failed` (`CANCELLED` / `FAILED`, PayHere's message kept); a payment that was Pending cuts the hold back to `min(due, now + ExpiryMinutes)`; **a fresh Initiated attempt is opened** if the order is still payable. |
| **−3** | `PaymentException` alert ("Chargeback reported…", reason `chargeback`); nothing else changes. |
| other | stored, Ignored. |

A success on an already settled payment is a duplicate (Ignored) **unless its PayHere `payment_id` differs from
the one that settled it**: that is a second charge, and it raises a `PaymentException` alert
(`order_already_paid`) rather than disappearing.

The **retry sweep** (`RetryPayHereEventsService` → `PayHereRetrySweep`, every minute) applies Received/Failed
PayHere events whose retry time has come, claiming each with a conditional `UPDATE`; the ladder is 1, 5, 15, 60
and 240 minutes, six attempts in all, then a `PaymentException` alert ("could not be applied after 6 attempts").
The groundwork did not build a sweep and none existed after the rebase, so this one is PayHere-only; if a
provider-neutral one lands, delete the service and keep `PayHereRetrySweep`'s work.

## Settings

All `Settings.Payments` (no default role holds it), one save unit, group `Payments.PayHere`, read at the moment
of use (no cache, no environment variable, no rebuild to move between sandbox and live):

| Key | Type | Default |
|---|---|---|
| `Payments.PayHere.Mode` | String `sandbox`/`live` | `sandbox` |
| `Payments.PayHere.MerchantId` | String | null |
| `Payments.PayHere.MerchantSecret` | **Secret** (Data Protection, `SpencerDenim.Settings.v1`) | null |
| `Payments.PayHere.PendingMaxDays` | Int, 1–14 | 5 |
| `Payments.PayHere.ExpiryMinutes` | Int, 15–1440 | 60 |

`PUT /api/admin/settings/payhere` (fields `mode`, `merchantId`, `merchantSecret`, `pendingMaxDays`,
`expiryMinutes`, `rowVersion`; absent = untouched; blank/absent secret = keep the stored one) and
`GET /api/admin/settings/payhere/status` (notify URL, whether checkout offers PayHere, what is missing — never
the secret). Each save writes one audit row per changed key (the secret as `[secret changed]`) and **one
`SensitiveSettingsChanged` banner** (Critical, keys and actor id, never a value).

## Decisions and deviations (please read)

1. **The payment window is `Payments.PayHere.ExpiryMinutes`, not `Orders.PayHereExpiryMinutes`.** The catalogue
   requires a key to begin with its group (DB2's permission is read off the prefix) and forbids a save unit
   spanning two permissions; `Orders.` + `Settings.Payments` breaks both. Doc 11 §1.6 is corrected. For the
   owner's doc 08 edit: use the new name. One constant to change if you disagree.
2. **A failed attempt is followed by a fresh one, in the same transaction** (P8b step 6 described a customer
   "retry" that nothing in the groundwork builds). Without it a declined card would leave an order nobody can
   pay. An Initiated attempt stays payable too — the same form can be posted again. Doc 11 P8b is amended.
3. **Checkout availability is a default interface member** `IPaymentProvider.IsAvailableAsync` (true), filtered
   in `CheckoutReader.EnabledPaymentMethodsAsync`. The PayPal task needs the same for "rate older than 7 days";
   whichever lands second keeps one of the two (they are textually adjacent).
4. **The public address is `Email:StorefrontBaseUrl`, and it must be an origin and nothing else** —
   `scheme://host[:port]`, no path, query, fragment or credentials; `https` unless the mode is `sandbox` (or the
   host is loopback), where plain `http` is accepted for a local test. Anything else (including a trailing
   path such as `https://shop.example/store`) makes PayHere **unavailable**, fail closed, with one throttled
   warning in the API log (EventId 3520, at most one every half hour per process, never the value) and the admin status
   panel saying why. PayHere is hidden without it. The reverse proxy sends `/api` to the API on the same
   origin (doc 03 §4), so notify = `{origin}/api/payments/payhere/notify`. A trailing slash or newline from a
   `.env` file is trimmed, not refused.
5. **The `SensitiveSettingsChanged` banner is raised for PayHere; the bank panel still does not raise one**
   (it predates `ops.AdminAlerts`). The email half of doc 08 N2 is not built (no template exists) — the banner
   is the whole notification. Owed, for both panels.
6. **No provider switch on the screen.** The bank transfer has none either. Turn it on with
   `UPDATE sales.PaymentProviders SET IsEnabled = 1 WHERE Code = 'PayHere'` once the owner has confirmed the
   change with the client; the status panel says so.
7. **Cancelling an order that has a gateway payment Pending is refused** (changed after the first tester pass; the
   first draft allowed it and left the PayPal-only guard alone). R1's reasoning is the same for any gateway: a
   Pending payment means the money may already be on its way, so the customer and the admin screens both
   refuse, and the cancel statement itself carries the same predicate so a notification landing between the
   check and the write cannot be beaten. The rule is *"a payment that is Pending and is not a bank
   transfer"* — a bank transfer is Pending for the whole normal unpaid state (waiting for the customer's
   slip), so it must not block. PayPal keeps its own refusal code (`paypal_pending`); every other gateway
   answers `gateway_payment_pending`. An **Initiated** PayHere attempt stays cancellable (nothing was sent
   yet that could be paid). The late-success safety net is unchanged and still holds if the guard is ever
   bypassed: `CapturedRequiresRefund` plus an alert.
8. **The "second charge" alert and the dead-letter alert are additions** to P8b's table. A success the settled
   payment has no record of is real money; ignoring it was the one outcome I could not defend.
9. **Fallbacks on the form** for what PayHere requires non-empty and an order may lack: last name `-`, phone
   `0000000000`, address `Not provided`, city `N/A` (a pickup order sends the store as the address and the
   district as the city). Each value is clipped (50/50/100/20/50).
10. Seeded display name is the one proposed in #141 ("PayHere (card, eZ Cash, internet banking)").
11. **The deadline is the expiry job's, not the claim's** (accepted behaviour; the first draft of this note and of
    P8b step 7 said the opposite). `SettleAsync`'s claim has no `PaymentDueAtUtc` predicate (groundwork
    behaviour, shared with the bank flow), so a success notified after the due time but before the next 5-minute
    expiry sweep **still pays the order** — its stock is still reserved, so paying it is better than a refund.
    Only a success notified **after the sweep has expired the order** becomes `CapturedRequiresRefund` + alert.
    P8b step 7 now says exactly that.
12. **Refusals carry the host's generic status-code document** (`UseStatusCodePages`), not an empty body; it
    names nothing of the request. 200 is empty. The integration test pins "nothing of the request in the body".
13. **A second charge on a payment already parked as an exception** raises `order_not_payable`, not
    `order_already_paid`: the order was not paid, and the banner must not send staff looking for a payment.

## Second pass: the independent tester's findings (FAIL, conditional)

Crypto, the notify endpoint's verification and the replay handling were confirmed independently. What follows is
what was found and what changed; each is a new commit on top of `405d009`.

**1. Payment-exception notice for the shopper: not built here.** The PayPal branch owns the notice. What it
can rely on from PayHere is pinned by `Every_notify_outcome_that_leaves_money_unapplied_is_a_status_7_payment_staff_can_see`:
a late success (after the expiry sweep, or after a cancel) and an amount or currency mismatch all end as one
payment in `CapturedRequiresRefund` (status 7), not the settling payment, carrying PayHere's `payment_id` as
`ProviderTransactionId`, and visible on the admin order screen. A notice keyed on that status needs nothing
PayHere-specific. **One case the notice cannot see:** a second charge on a payment that is already `Succeeded`
raises only the `order_already_paid` alert and creates no status-7 row (the payment that settled the order is
correct; the extra money is a portal matter), so a shopper notice built from status 7 will not mention it.
Flagged for whoever owns the notice.

**2. The sandbox how-to snippet** (above) was broken: it defined a function called `H`, which PowerShell resolves
to the `Get-History` alias before any function, and its `https://<host>/...` URL was unquoted. Rewritten
(`Md5Hex`, a quoted `$base`) and **run end to end** against a local `HttpListener` with the known-answer inputs
(`299858CC8ADFDBE274AFABDD689EA94B`, checked independently with Python's `hashlib`): it posts the seven fields,
form-encoded, to `/api/payments/payhere/notify`, with the signature the server computes. The only other code in
this note is SQL.

**3. Same-order deadlock (the tester counted 96 victims over 20 orders).** Two causes, found from the
`system_health` deadlock graphs rather than guessed, plus one consistency bug on the way:

- *Lock order.* A settlement takes the order row, then the payment (doc 08 section 5.3, N6). The failure path
  took the payment first and then needed the order (the replacement attempt's foreign key). PayHere's three
  writers (`MarkPending`, `MarkFailed`, `RecordReference`) now take the order row first, with
  `SELECT ... WITH (UPDLOCK, ROWLOCK)`, and `RecordReference` runs in its own order-first transaction.
- *Scans.* The replacement-attempt `INSERT ... WHERE NOT EXISTS (... Status IN (@a, @b))` could not use the
  filtered index `UX_Payments_OneOpen` because the statuses were parameters (DB28), so it scanned `PK_Payments`
  and took locks on **other orders'** rows: rings of three and four transactions across different orders. The
  statuses are literals now (`IN (0, 1)`), with a comment saying why. The cancel guard's new probe uses the
  same literal.
- *Reference overwrite.* A failure or pending notice that raced the success could overwrite the settling
  payment's `ProviderReference` with its own PayHere id (found by the three-notice test below: the reference
  must be the success's). `MarkFailed` and `MarkPending` now write `COALESCE(ProviderReference, @id)`; only
  `RecordReference` (the success path, before the settlement) replaces one. The reference follows the money.

*How it is tested.* EF's `SqlServerRetryingExecutionStrategy` retries error 1205 silently, so a deadlock leaves
every request answering 200 and the row assertions mostly green. `DeadlockVictimRecorder` (a
`DbCommandInterceptor`; a new file, **not** a `DeadlockCount` helper from another branch: rename or merge when
the branches are stacked) records every 1205 with its statement text. `PayHereConcurrencyTests`:

- `A_success_and_a_failure_for_the_same_order_arriving_together_...`: 20 orders, each pair released together,
  **zero** victims, one invoice each.
- `Pending_failed_and_success_together_for_the_same_order_...`: three notices, zero victims.
- `The_first_statement_that_touches_the_order_or_the_payment_is_the_order_lock` (3 cases: pending, failure,
  and a success's reference write): deterministic. Driven one request at a time against a statement log
  (`SqlStatementLog`), the order-row lock must come before the first write to the order or the payment. It is
  true or false every time, unlike the two race tests. It is coupled to the statement text on purpose.
- `Every_order_at_once_ends_consistent_after_the_sweep_and_no_payhere_statement_is_a_victim`: 40 notices at 20
  orders.

Red on the original code (both race tests failed with victims); green now. **Honest limit:** with **only** the
explicit order lock removed (the scan fix kept), the same-order pair test still passed - the failure path's
first statement (`UPDATE o ...`) already touches the order row, and the inversion needs a particular
interleaving - so the race tests cannot be relied on to catch a lost lock. That is why the statement-order
test exists: with the lock removed it fails in all three cases (run and checked), with it restored it passes.

**The cross-order test cannot assert zero total victims yet.** The groundwork's
`OrderSettlementEffects.CloseOtherOpenAttemptsAsync` scans payment rows of other orders and still deadlocks
against a concurrent settlement, until the groundwork fix (index hint, and only for Failed/Cancelled/Expired) is
under this branch. The test asserts that every order ends consistent after the retry sweep and that no
statement PayHere owns is a victim, and reports the total as a diagnostic. **After stacking, tighten it to
`AssertNoDeadlockVictims()`.**

**4. The 16 KB body cap on a real Kestrel** (`PayHereTransportTests`, using `KestrelApiHost.StartPayHereNotifyAsync`:
port 0, no database, the handler stubbed to count calls). `ApiFactory`'s `TestServer` has no transport and
ignores every size limit, which is how the cap shipped untested. Pinned: exactly 16,384 bytes with a
`Content-Length` is accepted and intact; 16,385 is a 413 and the handler is never reached; a chunked body is
refused at the same line. **Finding:** Kestrel counts chunked framing (chunk-size lines and CRLFs) against
`MaxRequestBodySize`, so a chunked payload is accepted up to 16,384 minus the framing (16,323 payload bytes
with 2 KB chunks), not 16,384; that test is stated in bytes on the wire. A 413 names nothing of the request,
and the limit does not outlive the request.

**5. Docs.** P8b step 7 said an over-due success "never settles". That was wrong: the claim has no due-time
predicate, so a success before the next 5-minute expiry sweep still pays the order (decision 11 above; P8b
step 7 and its edge cases say so now). Also corrected: the real `order_id` shape is `SD-2026-000123-4567`
(order number, payment id), the alert reason is `chargeback` (lowercase), and P8b now says that a **disabled
provider still applies notifications for in-flight payments** (verification needs only the saved merchant ID
and secret; the switch decides only whether new orders may choose PayHere; pinned by
`A_notification_for_a_payment_already_in_flight_is_still_applied_after_PayHere_is_switched_off`).

**6. Cancel while a gateway payment is Pending**: decision 7 above. The cancel guard is no longer PayPal-only;
`OrderCancellationStore` (statement and diagnosis), `CheckoutReader` and `AdminOrderReader` use one rule.
PayPal keeps `paypal_pending`. Tests: `PayHereOrderHandlingTests` (customer and staff refused while Pending, an
Initiated attempt still cancellable, the block lifts when PayHere fails the payment), plus unit tests.
**These three files are the PayPal task's too**: expect a textual conflict on the guard when the branches are
stacked, resolved by keeping this generalised rule and PayPal's code string.

**7. Provider ids on the admin order screen.** `AdminOrderPaymentDto` gains `ProviderReference`,
`ProviderTransactionId` and `ProviderOrderId` (derived, never stored: `ProviderOrderIds.For`, so PayHere's
`{OrderNumber}-{PaymentId}` is recomputable and the reader names no gateway). The page shows whichever exist,
labelled "Order ID sent to the provider", "Provider's payment ID" and "Provider's reference", so staff can find a
payment in the PayHere portal, which is how every refund is made. Alert details were not changed.

**8. Origin validation and the pending notice.** Decision 4 above (strict, fail closed, throttled log line). On
the storefront, after `?payment=return` a server-stated pending notice now wins over both "Checking your
payment..." and "We have not had confirmation"; polling carries on underneath it.

**9. Not PayHere, not fixed:** `AdminWriteRequest.ReadJsonAsync` answers **500** on an admin JSON body that is
not valid UTF-8, where it should be 400. Found in passing; left alone and reported to the coordinator for an
issue.

## Not done / open

- **Staff emails** for payment exceptions and for sensitive-settings changes (no template exists).
- **No rate limit** on the notify endpoint. It is cheap (one settings read, one MD5) and unauthenticated by
  nature; an edge limit is the right place.
- **A PayHere order whose account is cleared while it is open** shows the bank page's "still confirming the
  payment details" copy (the provider answers nothing). Honest enough for a rare admin mistake; a
  provider-neutral "payment unavailable" state would be better.
- **No recovery job** (PayHere's status API needs an OAuth business-app credential) — as P8b says.
- **Refunds are manual** in the PayHere portal (owner decision 2026-10-08).
- **CSP/`form-action`**: the storefront posts to another origin; check deployment config.
- Doc 02 / doc 08 wording is in the #141 comment, **not applied** (owner-only).

## Testing against the real PayHere sandbox (once there is a merchant account)

1. **Sandbox merchant.** Sign up at `sandbox.payhere.lk`, open *Integrations → Domains & Credentials*, add the
   domain the shop will run on, and note the **Merchant ID** and **Merchant Secret** PayHere shows for it (they
   differ per domain and between sandbox and live).
2. **Make the shop reachable.** PayHere's servers must reach the notify URL over HTTPS: run the stack behind its
   reverse proxy and expose it with a tunnel (for example `cloudflared tunnel --url http://localhost:80` or
   `ngrok http 80`). Register **that hostname** as the domain in step 1 and wait for approval.
3. **Tell the server its public address.** `Email__StorefrontBaseUrl=https://<the tunnel hostname>` (no trailing
   slash) on the API, then restart it. Nothing else is read from the environment.
4. **Fill the panel.** Admin → Settings → PayHere: mode `sandbox`, the merchant ID, the secret → Save. The
   status shows the notify URL (`https://<host>/api/payments/payhere/notify`) — enter it in the PayHere portal.
5. **Switch the provider on** (development database only):
   `UPDATE sales.PaymentProviders SET IsEnabled = 1 WHERE Code = 'PayHere';` — the status should now read
   "PayHere is offered to customers at checkout".
6. **Pay.** Add something to the bag, choose PayHere at checkout, place the order, press **Pay now**, and pay
   with one of the sandbox test cards in PayHere's documentation. You are returned to
   `/order/{token}?payment=return`, see "Checking your payment…", then the paid order.
7. **Check the rows.** `sales.PaymentEvents` has one `VerifiedWebhook` row (`payment.success`, Processed, no
   card fields in `PayloadJson`); `sales.Payments` is `Succeeded` with `ProviderTransactionId` = PayHere's
   `payment_id`; `ops.EmailOutbox` has a `PaymentConfirmed` row.
8. **Other outcomes.** Press Cancel on PayHere's page → `?payment=cancelled`; fail a card → the payment is
   Failed and the page offers a form with a new `order_id`. Pending and chargeback cannot easily be provoked
   in the sandbox: post a signed notification yourself —
   ```powershell
   $base   = 'https://<host>'             # the shop's public address, no trailing slash
   $mid    = '<merchant id>'
   $secret = '<merchant secret>'
   $oid    = 'SD-2026-000123-4567'        # the "Order ID sent to the provider" on the admin order screen
   $amt    = '1500.50'                    # the order's total, always two decimals
   $status = '-3'                         # 2 success, 0 pending, -1 cancelled, -2 failed, -3 chargeback

   $md5 = [System.Security.Cryptography.MD5]::Create()
   function Md5Hex([string] $text) {
       -join ($md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { $_.ToString('X2') })
   }
   $sig = Md5Hex ($mid + $oid + $amt + 'LKR' + $status + (Md5Hex $secret))

   Invoke-WebRequest "$base/api/payments/payhere/notify" -Method Post -Body @{
       merchant_id = $mid; order_id = $oid; payment_id = '999000111'; payhere_amount = $amt
       payhere_currency = 'LKR'; status_code = $status; md5sig = $sig
   }
   ```
9. **Going live.** New merchant ID and secret for the live domain, mode `live`, save; the next order's form
   posts to `www.payhere.lk`. No restart, no rebuild.

## Expectations of the groundwork fix that is being rebased under this branch

Written against the coordinator's note (2026-10-10): idempotency keys are printable ASCII (the key is
`PayHere:{payment_id}:{status_code}` and both parts are shape-checked to ASCII before anything is stored, so no
posted text can make one that is not; pinned by `The_idempotency_key_is_printable_ascii…` and
`A_payment_id_written_in_non_ascii_digits_is_refused…`); the form action is `https://sandbox.payhere.lk/...` or
`https://www.payhere.lk/...`, public host names (so the stricter `IsSafeAbsoluteUrl` accepts it); the provider
code passed to `SettleAsync`/`RouteToExceptionAsync` is `PayHere` and matches the payment row; `ProviderTransactionId`
and `ProviderReference` are at most 64 characters (column 100) and `FailureMessage` is clipped to 500. The late
success on a Failed/Cancelled payment still closes the replacement attempt (it runs for exactly those statuses).
**Re-run `PayHere*` after the rebase**; nothing here depends on the parts that change.

## Verification

All against the throwaway SQL Server container (127.0.0.1:38998), VSTest filters, no network.

**First pass (commits up to `405d009`)**

- `dotnet build SpencerDenim.sln`: 0 warnings, 0 errors (warnings are errors). CA5351 is suppressed in exactly one
  place (`PayHereHash.Md5Upper`), with the reason.
- Unit: 2,357 passed. Integration `FullyQualifiedName~PayHere`: 71 passed; the wider run over the suites this
  task touches: 382 passed. Frontend: storefront `order-page.spec.ts` + `payment-redirect.spec.ts` 117, admin
  `settings/*.spec.ts` 58; `ng lint` and production builds clean.

**Second pass (after the tester's FAIL, conditional)**

- `dotnet build SpencerDenim.sln`: 0 warnings, 0 errors.
- **Unit: the whole project, 2,403 passed, 0 failed.** The PayHere subset (`FullyQualifiedName~PayHere`) is 357.
- **Integration: `FullyQualifiedName~PayHere` is 94 tests** (`PayHereNotifyTests` 42, `PayHereCheckoutTests` 15,
  `PayHereSettingsEndpointTests` 13, and the new `PayHereConcurrencyTests` 6, `PayHereOrderHandlingTests` 9,
  `PayHereTransportTests` 8). One run over those and every neighbour (`SalesReferenceData*`, `AdminSettings*`,
  `CheckoutEndpoint*`, `OrderCancellation*`, `AdminOrder*`, `CheckoutPlacement*`, `FreshDeployment*`,
  `SettingsSchemaAndSeed*`, `OrderPagePaymentAttempt*`, `OrderSettlementStore*`, `PaymentEventStore*`,
  `BankTransferReview*`, `OrderExpiry*`, `AdminAlert*`): **428 passed, 0 failed**. The whole integration suite
  was not run.
- Along the way: the three-notice test failed once on a wrong `ProviderReference` (a pending notice overwrote the
  success's id) and led to the `COALESCE` in `MarkPending`; the no-lock experiments above were run and reverted.
- **Frontend (Node 24)**: admin `pages/orders` + `pages/settings` specs 110 passed (order detail +2 for the
  provider ids); storefront `pages/order` + `payment-redirect` specs 120 passed (order page +3 for the pending
  priority). `ng lint admin` and `ng lint storefront`: all files pass. The whole frontend suites were not run.
