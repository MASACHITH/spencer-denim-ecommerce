# 2026-10-10 — Tharanga: payment groundwork for PayPal (#54, W4-01) and PayHere (#141, W4-01b)

Branch `feature/W4-01-payment-groundwork`, worktree `worktrees/sd-paygr`, cut from `origin/main` at `31e6aa6`.
Scope: **the shared seams a provider that is confirmed by the provider itself (a webhook, a notify call)
needs, so that `PayPalProvider` and `PayHereProvider` are each a class, a registration line and settings
rows, with no change to checkout (PAY-03).** A behaviour-preserving extraction plus four small additions.
**No provider is built, no external service is called, no migration** (the schema already had everything).
**Not pushed, not a PR.**

## What a provider can now rely on

### 1. `IOrderSettlementStore` — "this payment is complete" (`Application/Sales/Payments/Abstractions`)

```csharp
Task<PaymentSettlementResult> SettleAsync(PaymentSettlement settlement, CancellationToken ct);
Task<PaymentSettlementResult> RouteToExceptionAsync(PaymentExceptionRouting routing, CancellationToken ct);

record PaymentSettlement(int PaymentId, string ProviderCode, decimal ConfirmedAmount, string ConfirmedCurrency,
                         DateTime NowUtc, string? ProviderTransactionId = null, string? PayerReference = null,
                         string? Note = null);
record PaymentExceptionRouting(int PaymentId, string ProviderCode, string Reason, DateTime NowUtc,
                               decimal? ConfirmedAmount = null, string? ConfirmedCurrency = null,
                               string? ProviderTransactionId = null, string? PayerReference = null);

abstract record PaymentSettlementResult
    Settled(string OrderNumber, string InvoiceNumber)          // committed: order Paid
    CapturedRequiresRefund(string Reason, string OrderNumber)  // committed: money on record + alert, order untouched
    AlreadyHandled(PaymentStatus PaymentStatus)                // nothing written: a duplicate
    NotFound                                                   // nothing written: not this provider's payment
```

`SettleAsync` is doc 08 §4.2 step 3 and 3.4 in one transaction, in the §5.3 / N6 lock order (order row, payment
row, other payments, stock rows by `VariantId`, number series):

1. The payment must be this provider's and in a status a COMPLETED payment is accepted from (R1: 0, 1, 2, 4,
   5, 6). 3 / 7 / 8 / 9 answer `AlreadyHandled` with nothing written, so a redelivery costs no lock.
2. **Amount and currency are checked first** against `ProviderAmount`/`ProviderCurrency`, or — for a payment
   that was never converted (PayHere, in LKR) — against `Amount`/`Currency`. A mismatch goes to the exception
   path with `FailureCode = 'AMOUNT_MISMATCH'` (R9), clears `CaptureLockedUntilUtc`, deducts nothing.
3. `SAVE TRANSACTION BeforeSettle` (EF `CreateSavepoint`, B4), then the order claim — the **same statement**
   the bank Approve uses, pinned to the payment's provider. 0 rows or a payment update that then affects 0
   rows roll back to the savepoint only and go to the exception path.
4. Paid effects (shared with the bank flow, see below): stock, gapless invoice number, history row with source
   `PaymentProvider` (no user), `PaymentConfirmed` email in the same transaction. A late success on a Failed /
   Cancelled / Expired payment closes any other open attempt (Appendix B1).
5. **Exception path**: payment → `CapturedRequiresRefund`, `IsOrderSettlement = 0`, provider transaction id
   kept, `ops.AdminAlerts` `PaymentException` (Warning, related entity = the order's `PublicId`, details =
   ids and amounts only) in the same transaction. **Once per payment**, by construction: the `UPDATE` only
   matches a payment that has not settled or been routed.

`RouteToExceptionAsync` is step 5 on its own for a caller that already knows (`Reason` is one of
`PaymentExceptionReasons`: `order_expired`, `order_cancelled`, `order_already_paid`, `order_not_payable`,
`amount_mismatch`). `SettleAsync` / `RouteToExceptionAsync` **refuse the bank-transfer code with an
`ArgumentException`**: a bank transfer is settled by staff, with an approver and an audit row.

Not in the port, on purpose: pending / failed / cancelled notifications (they only change a payment's status
and are each provider's rule), cutting back or extending `PaymentDueAtUtc` (R2 / N16), the capture claim, and
staff acceptance of an exception payment (§4.2 step 7 — a later use case that will reuse the same effects;
`PaymentFailureCodes.AmountMismatch` is the constant it must refuse on).

### 2. `IPaymentEventStore` — the provider event log (`sales.PaymentEvents`)

```csharp
Task<PaymentEventReceipt> RecordAsync(PaymentEventRecord record, ct);   // IsNew false => redelivery: answer 200, stop
Task<bool> MarkProcessedAsync(long eventId, DateTime nowUtc, ct);
Task<bool> MarkIgnoredAsync(long eventId, DateTime nowUtc, ct);
Task<bool> MarkFailedAsync(long eventId, string failure, DateTime? retryAtUtc, ct);  // null => no more retries
Task<bool> LinkAsync(long eventId, int? paymentId, int? orderId, ct);

record PaymentEventRecord(string ProviderCode, PaymentEventSource Source, string EventType, string? ProviderEventId,
                          string IdempotencyKey, string PayloadJson, DateTime ReceivedAtUtc,
                          int? PaymentId = null, int? OrderId = null);
record PaymentEventReceipt(long EventId, bool IsNew, PaymentEventProcessingStatus ProcessingStatus,
                           bool PayloadTruncated, bool PayloadRedacted);
PaymentEventKey.ForEvent(provider, eventId) / .ForResource(provider, type, resourceId)   // doc 08 key shapes
```

- **Recording is one guarded `INSERT … WHERE NOT EXISTS (… WITH (UPDLOCK, HOLDLOCK))`**; the affected-row count
  is the answer. This repo had no guarded-insert precedent (it catches 2627 via `SqlUniqueViolation` in
  `CartStore`, the number series and placement); doc 08 §4.2 says the *settlement* path must never rely on
  catching a unique violation and DB13 describes the event insert as "succeeds or hits the unique index". I
  chose the guarded insert and **deliberately did not add a catch for 2627**, so the concurrency test fails if
  the lock hint is ever lost instead of passing on the backstop.
- **Payload rules** (`PaymentEventPayload.Prepare`, applied inside the store): must be a JSON **object or
  array** (a form post is converted to an object first — PayHere's notify is form-encoded); secret-named
  properties (`*secret*`, `*password*`, `authorization`, `apikey`, `accesstoken`, `refreshtoken`, `privatekey`,
  `bearer`, `cvv`…) at any depth have their value overwritten with `[redacted]`; otherwise the text is stored
  **verbatim**; over 64 K characters it is replaced by `{"truncated":true,"originalLength":N}` (a prefix of JSON
  is not JSON) and the receipt says so. Invalid input is an `ArgumentException` before the database is asked,
  not CK 547.
- A `Received` row is stored with `NextAttemptAtUtc = received + 2 min` so `IX_PaymentEvents_Retry` finds an
  event whose processor died; `MarkProcessed` / `MarkIgnored` clear it. All `Mark*` count the attempt and only
  move a row from Received/Failed, so a late worker cannot rewrite a finished outcome.
- **The order the docs require is the caller's** (stated on the port): verify the signature first and store
  nothing before it; `RecordAsync`; apply; `Mark*`.

### 3. `PaymentInstructionsRequest` knows the attempt; `IPaymentAttemptReader`

`PaymentInstructionsRequest(…, int? PaymentId = null, string? ProviderReference = null)` — additive, so
`BankTransferProvider` and every existing call compile unchanged. `GetOrderQueryHandler` reads the order's
live attempt through the new `IPaymentAttemptReader.FindInFlightAsync(orderPublicId)` (newest payment in
Initiated, Pending or Capturing) and fills both. **`PlaceOrderCommandHandler` passes `null`/`null` explicitly,
and that is not an omission:** it asks `DescribeAsync` *before* the placement transaction that inserts the
payment row, so there is no id to give. The convention for a redirect provider is therefore two-stage: answer
`null` at placement (which also means **no bank-transfer instructions email is queued**), and answer with the
redirect on the order page, where the attempt exists.

### 4. The redirect contract

`PaymentInstructions` gained `PaymentRedirectForm? RedirectForm` (`Method` = `"POST"`, absolute `Action`,
ordered `Fields[Name,Value]`; `PaymentRedirectForm.Post(...)` refuses a non-https action — `http` only for a
loopback host — and nameless fields). It is mirrored in `OrderPaymentInstructionsDto.RedirectForm` and in the TS
model (`redirectForm?: OrderPaymentRedirectFormDto | null`). If both `RedirectForm` and `RedirectUrl` are set, the
form wins. **A redirect provider must set `AcceptsProof = false`.** Nothing secret goes in the form: a *hash*
computed from a secret, never the secret.

The storefront "How to pay" panel is provider-neutral: with a safe `redirectForm` it renders a real
`<form method="post">` with hidden fields and a "Pay now" submit button; with a safe `redirectUrl` a real
`<a href>` "Pay now"; with neither, the bank panel is rendered exactly as before. An unsafe address (not
absolute https, `javascript:`, userinfo, …) renders no control at all. See "Frontend" below.

### 5. `PaymentProviderContractTests`

`StubFor` now knows `IOrderSettlementStore` / `IPaymentEventStore` (stubs that **refuse** — answering the two
PAY-03 questions must not settle an order or write an event), `IPaymentAttemptReader` (answers "no attempt") and
`IClock`. New contract cases: `DescribeAsync` for an order with an existing attempt, and a shared consistency
checker (redirect URL / form must be safe, a redirecting provider must not accept proof) that is itself
exercised with synthetic good and bad instructions, so it is not untested code waiting for its first provider.

## The extraction (item 1) — what moved, what did not

`BankTransferReviewStore` is now 702 lines (was 1,061). **Moved unchanged, with their comments, into
`OrderSettlementEffects`** (internal, scoped, no transaction of its own): `ClaimPendingOrderPaidAsync` (the
Approve claim, with the provider code and the acting user as parameters), `DeductStockAsync`,
`ReacquireStockAsync`, `IssueInvoiceAsync`, `RecordStatusChange` (was `WriteHistoryAsync`),
`EnlistPaymentConfirmed` (was `QueuePaymentConfirmed`), `ReadLinesAsync`/`StockLine`. `OrderSnapshot` (and its
struct-vs-class lesson) moved to its own file, with the projection as an expression both readers share. The
transaction wrapper moved to `SettlementTransaction.RunAsync`, generalised over the result type; the bank
store's `InTransactionAsync` is now one call to it. New (not moved): `CloseOtherOpenAttemptsAsync` (B1).

**Stayed in the bank store** because they are the bank flow's own: the actor, `FindReviewablePaymentAsync`,
the bank `SettleAsync` (approver columns, status-exact guard), `DiagnoseAsync` and its refusal wording, the
AcceptLate claim, `EnlistAudit`, `Note`. Same SQL, same guards, same history and audit rows.

### Where preserving behaviour forced an awkward seam

- **The extraction target is a concrete internal class, not an Application port.** The brief said the bank
  store should "call" the port. It calls `OrderSettlementEffects`; `IOrderSettlementStore` is the
  *provider-facing* transaction around the same effects. The effects must run inside their caller's
  transaction and lock order; a port invites a caller to wrap each in a transaction of its own, which is the
  atomicity the callers exist to provide, and nothing in the Application layer can use them. If the owner wants
  an interface for substitution it is a one-line change.
- `ReacquireStockAsync` now returns the short lines and the bank store wraps them in `ShortStock` with the
  order's status (it used to return the `ShortStock` result itself, which would have tied the effects to a
  bank type).
- `WriteHistoryAsync` was `async` with a bare `await Task.CompletedTask`; `RecordStatusChange` is synchronous.
  The bank store keeps a one-line `RecordStaffStatusChange` so its call sites still read as before and still
  say why the source is `Staff`.
- The bank store's constructor lost `INumberSeriesAllocator` and `IOrderEmailOutbox` and gained
  `OrderSettlementEffects`; all construction is through DI, so no test changed.

## Decisions and uncertainties

1. **Exception-path lock order.** Doc 08 §4.2 lists the payment update before the mismatch lock-clear on the
   order. I do the order first (N6: order row before payment row), because the rule is the lock order and the
   listing is a description. The lock-clear is also pinned to the payment's provider and only touches an order
   that holds a lock.
2. **`FailureCode` is written only for `AMOUNT_MISMATCH`**, the one code §4.2 step 7 makes load-bearing; every
   reason goes in `FailureMessage` and in the alert details. A late success on a Failed payment **clears**
   `FailureCode`/`FailureMessage` (a settling payment must not read as "declined").
3. **No audit row for a System payment**; the history row (source `PaymentProvider`) and the event log are its
   record. No *staff email* on the exception path: doc 08 §4.2 asks for one to Super Admins and
   `Orders.Approve` / `Orders.Refund` holders, but no staff email exists in this build (doc 11 §4.2,
   `IOrderEmailOutbox` is customer-only). The banner is the whole notification today — **owed**.
4. **`OrderPlacementStore` change (the one outside the payments folder).** It queued the bank-transfer
   instructions email whenever instructions were non-null; its own remarks said W4-01 must write the rule for
   a second provider. It now skips instructions that carry a `RedirectUrl` or `RedirectForm`. It reads the
   shape of the answer and names no gateway. Tested both ways.
5. **Not built, and the PayPal/PayHere tasks need a decision about it:** the step that *creates* the
   provider-side order after the payment row exists (PayPal "create order → approval URL"; PayHere's signed form
   needs the payment id). `DescribeAsync` on a GET page view should not create things, so this probably wants
   a small post-commit hook or a "begin payment" endpoint. I did not invent one; the request now carries
   `PaymentId`/`ProviderReference` so a provider that already holds its reference can describe it.
6. **Not built:** the `RetryPaymentEvents` sweep (the store gives it `Received`/`Failed` + `NextAttemptAtUtc`
   semantics and the Mark* methods, but not a "claim due events" query), a payment lookup by
   `(ProviderCode, ProviderReference)`, and staff acceptance of an exception payment.
7. The `PayHere` provider code is not in `PaymentProviderCodes` or the seed; that is #141's (and the scope
   note in doc 11 P8b / doc 02 is the owner's — not touched).
8. **CSP:** the storefront posts a form to another origin. Any `Content-Security-Policy` with
   `form-action 'self'` in front of the site would block it; none exists in the repo, but deployment config
   should be checked. The order-page URL is a bearer token; the default `strict-origin-when-cross-origin`
   referrer policy does not leak it across the hand-over.

## Frontend (written by a sub-agent, reviewed here)

`core/checkout/payment-redirect.ts` (new: `safeRedirectUrl`, `paymentRedirect`) and `pages/order/order-page.ts`
(a `payRedirect` computed and one `@if` branch before the bank section). No new CSS (reuses `.order__panel`,
`.pay`, `.pay__actions`, `.btn`). Hidden fields use `[attr.name]`/`[attr.value]` so they are in server-rendered
HTML and a press before hydration still posts. One extra `<!--container-->` comment node appears in the bank
rendering (any new `@if` adds one); elements, text, attributes and classes are identical.

## Verification

All on the throwaway SQL Server container (127.0.0.1:38998); VSTest filters; the whole integration suite
was **not** run (the box is shared and loaded).

- `dotnet build SpencerDenim.sln`: 0 warnings, 0 errors (warnings are errors).
- **Unit: the whole project, 2,041 passed, 0 failed.** New: `PaymentEventPayloadTests` 30,
  `PaymentExceptionAlertTests` 12, `PaymentRedirectFormTests` 15, plus 4 new cases in
  `PaymentProviderContractTests` (10 now) and 1 in `PlaceOrderCommandHandlerTests` (7 now).
  `CheckoutNamesNoProviderTests`, `BrandNameConventionTests` and the existing contract cases pass unchanged.
- **Integration, the existing suites run against the refactor, unchanged: 317 passed, 0 failed** —
  `BankTransferReviewTests`, `OrderExpiryTests`, `OpenPaymentIndexTests`, `OrderCancellationTests`,
  `SalesConstraintTests`, `OrderEmailTransactionTests`, `OrderFulfilmentTests`, `AdminBankTransferEndpointTests`,
  `CheckoutEndpointTests`, `BankQueuePlanTests`, `SalesSchemaTests`, `OrderPlacementEmailTests`,
  `AdminAlertsBehaviourTests`, `AutoDeliverShippedTests`. (A pre-refactor baseline run was started and
  abandoned: two copies of the script ran at once on the loaded box. Nothing failed after the refactor, so
  there is no regression for a baseline to be compared against.)
- **Integration, the new and touched classes: 257 passed, 0 failed** (same run also re-ran the existing
  suites above that overlap it). New: `OrderSettlementStoreTests` 18, `PaymentEventStoreTests` 19,
  `OrderPagePaymentAttemptTests` 16, and 3 added to `CheckoutPlacementStoreTests`.
- Frontend (Node 24, `src/frontend`): `ng lint storefront` clean; `ng build storefront` (production, SSR)
  clean; order-page + payment-redirect specs 100 passed (`order-page.spec.ts` 31 -> 59, new
  `payment-redirect.spec.ts` 41). The whole storefront suite was not run (flaky under load, per the brief).

One defect the new tests found in the new tests, not in the product: the theory over wrong amounts reused one
provider transaction id, and `UX_Payments_Provider_Txn` refused the second row — the index doing its job.

