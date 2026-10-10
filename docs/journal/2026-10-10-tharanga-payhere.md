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
4. **The public address is `Email:StorefrontBaseUrl`.** PayHere is hidden without it. The reverse proxy sends
   `/api` to the API on the same origin (doc 03 §4), so notify = `{origin}/api/payments/payhere/notify`.
5. **The `SensitiveSettingsChanged` banner is raised for PayHere; the bank panel still does not raise one**
   (it predates `ops.AdminAlerts`). The email half of doc 08 N2 is not built (no template exists) — the banner
   is the whole notification. Owed, for both panels.
6. **No provider switch on the screen.** The bank transfer has none either. Turn it on with
   `UPDATE sales.PaymentProviders SET IsEnabled = 1 WHERE Code = 'PayHere'` once the owner has confirmed the
   change with the client; the status panel says so.
7. **Cancelling an order whose PayHere payment is Pending is allowed** (the PayPal-pending guard in
   `OrderCancellationStore`/`CheckoutReader`/`AdminOrderReader` is PayPal-only and I left it alone). The safety
   net holds — a later success becomes `CapturedRequiresRefund` + alert — but a customer can cancel an order
   whose money is on its way. Extending R1 to PayHere is a one-line change in each of three places; flagged
   rather than done because those files are the PayPal task's.
8. **The "second charge" alert and the dead-letter alert are additions** to P8b's table. A success the settled
   payment has no record of is real money; ignoring it was the one outcome I could not defend.
9. **Fallbacks on the form** for what PayHere requires non-empty and an order may lack: last name `-`, phone
   `0000000000`, address `Not provided`, city `N/A` (a pickup order sends the store as the address and the
   district as the city). Each value is clipped (50/50/100/20/50).
10. Seeded display name is the one proposed in #141 ("PayHere (card, eZ Cash, internet banking)").

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
   $secret = '<merchant secret>'; $mid = '<merchant id>'; $oid = 'SD-2026-000123-4567'; $amt = '1500.50'
   $md5 = [System.Security.Cryptography.MD5]::Create()
   function H($s) { -join ($md5.ComputeHash([Text.Encoding]::UTF8.GetBytes($s)) | % { $_.ToString('X2') }) }
   $status = '-3'  # 0 pending, -1 cancelled, -2 failed, -3 chargeback, 2 success
   $sig = H ($mid + $oid + $amt + 'LKR' + $status + (H $secret))
   Invoke-WebRequest https://<host>/api/payments/payhere/notify -Method Post -Body @{
     merchant_id = $mid; order_id = $oid; payment_id = '999000111'; payhere_amount = $amt
     payhere_currency = 'LKR'; status_code = $status; md5sig = $sig }
   ```
9. **Going live.** New merchant ID and secret for the live domain, mode `live`, save; the next order's form
   posts to `www.payhere.lk`. No restart, no rebuild.

## Verification

(See the end of this file — filled in after the runs.)
