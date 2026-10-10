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
   `PaymentProvider` (no user), `PaymentConfirmed` email in the same transaction. Any other open
   attempt is closed (Appendix B1) — on every settlement, unconditionally, by a statement hinted to
   `IX_Payments_OrderId` (see the deadlock finding and the second round below).
5. **Exception path**: payment → `CapturedRequiresRefund`, `IsOrderSettlement = 0`, provider transaction id
   kept, `ops.AdminAlerts` `PaymentException` (Warning, related entity = the order's `PublicId`, details =
   ids and amounts only) in the same transaction. **Once per payment**, by construction: the `UPDATE` only
   matches a payment that has not settled or been routed.

`RouteToExceptionAsync` is step 5 on its own for a caller that already knows (`Reason` is one of
`PaymentExceptionReasons`: `order_expired`, `order_cancelled`, `order_already_paid`, `order_not_payable`,
`amount_mismatch`, `provider_data_invalid`). `SettleAsync` / `RouteToExceptionAsync` **refuse a bank-transfer
payment with an `ArgumentException`**: a bank transfer is settled by staff, with an approver and an audit row.
The refusal is decided on **the stored row's** provider code, not on the caller's spelling (see the first
tester round below); a caller's code that is not ordinally equal to the row's answers `NotFound`.

**What the provider sends has to fit its columns, and an anomaly never loses the payment.**
`ProviderTransactionId` is `varchar(100)` in a unique index with the provider code, so it must be printable ASCII
and at most 100 characters; `PayerReference` is `nvarchar(100)`, informational and unindexed. A transaction id
that is not usable is **dropped, not repaired** (a truncated id can collide, a folded one already does): on the
exception path the payment is recorded without it and `FailureMessage` quotes the start of it; on an order that
was **otherwise payable** (checked after the order claim, rolled back to the `BeforeSettle` savepoint) the
payment is kept as an exception with reason `provider_data_invalid`, whose message and alert say to verify it
with the provider and refund it or record the reference by hand — not to accept it. An order that is already
paid, expired or cancelled is reported as that, whatever the id looked like. **A payer reference that is too
long never blocks anything**: it is cut and the payment settles. Column widths are constants on `Payment` now
(`ProviderTransactionIdMaxLength` etc.), used by the EF configuration too (same values, no migration).

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
  array** (a form post is converted to an object first — PayHere's notify is form-encoded); a property whose
  normalised name (lower-case, `_ - .` and spaces removed) **contains** `secret`, `password`, `passwd`, `pwd`,
  `passphrase`, `token`, `credential`, `apikey`, `authkey`, `privatekey`, `bearer`, `cvv`, `cvc` or
  `securitycode`, or **is exactly** `auth`, `authorization`, `proxyauthorization` or `authheader`, has its value
  overwritten with `[redacted]` at any depth (`PaymentEventPayload.LooksSecret`); otherwise the text is stored
  **verbatim**; over 64 K characters it is replaced by `{"truncated":true,"originalLength":N}` (a prefix of JSON
  is not JSON) and the receipt says so. Invalid input is an `ArgumentException` before the database is asked,
  not CK 547.
  - `authorization` is matched whole, not as a fragment, because PayPal's capture carries `authorization_id` and
    an order's `authorizations` array, neither of which is a credential. Nothing PayPal's payment webhooks or
    PayHere's notification carries is redacted (`payment_id`, `order_id`, `merchant_id`, `payer_id`,
    `status_code`, `captured_amount`, `card_no`… are pinned in a test, as well as one realistic payload of each).
  - **`token` is redacted whole** — except the words that merely contain it (`tokenization`, `tokenized`). An
    earlier version named only `access_token` and friends and left `payment_token` readable "for disputes",
    which did not do what its own remarks said; the log being safe wins, and neither provider's payment events
    have a field with "token" in its name. **`cvv` / `cvc` (and `cvv2`, `cvc2`) are redacted when the name ends
    with one** (`cvv`, `card_cvv`) and not when it starts with one: `processor_response.cvv_code` and
    `cvc_check` are result letters a dispute reads, not the digits on the back of a card.
  - **A signature (`md5sig`) is not redacted by the store.** The provider verifies it before recording, and the
    store cannot know a provider's field names. The recommendation to the PayHere task is to drop it before
    `RecordAsync`: a hash derived from the merchant secret is of no use once the event is accepted, and leaves
    nothing to brute-force from a leaked table.
- **Keys must be storable.** `ProviderCode`, `EventType`, `ProviderEventId` and `IdempotencyKey` are `varchar`,
  but a parameter arrives as `nvarchar`, so a character outside the code page is folded to `?`: a Sinhala key
  recorded twice threw a unique violation instead of answering "redelivery", and two Cyrillic keys collided. All
  four are now **printable ASCII with no space** or an `ArgumentException` (`PaymentEventKey.IsStorable`; spaces
  too because SQL Server ignores trailing spaces when comparing). The statement converts its parameters to the
  column types explicitly, so the check and the insert compare like with like.
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

## The independent tester's first round (HEAD 049b188): FAIL, six findings, all fixed

No regression in the extraction (564 integration tests passed). What it found, and what changed:

1. **Deadlock (major).** `CloseOtherOpenAttemptsAsync` ran on **every** settlement:
   `UPDATE sales.Payments … WHERE OrderId = @o AND Id <> @p AND Status IN (0,1)`. On a small table the optimiser
   answers that with a range seek on the clustered key (`Id < @p OR Id > @p`) — a scan — and each settlement
   already held an exclusive lock on the payment row it had just updated, so two settlements of *different*
   orders scanned into each other's rows (twelve in parallel: 16 to 31 deadlocks per run, one call out of its
   three retries). The execution strategy retries a victim silently, so my own tests never saw it: they asserted
   results, and the result was right. **Fixed both ways:** the statement runs only when the payment's status
   before settling is outside `UX_Payments_OneOpen`'s filter — `Capturing`, `Failed`, `Cancelled`, `Expired`
   (a gate that the second round removed) — and it carries `WITH (INDEX(IX_Payments_OrderId))`.
   *(Superseded: the status gate was itself wrong — see the second round below. The hint alone is what removes
   the deadlock, and the statement now runs unconditionally.)* The other new statements were checked for the same shape: every other one is a primary-key seek (`Id = @id`), a seek on
   `UX_PaymentEvents_IdempotencyKey`, or the existing claim whose `NOT EXISTS` seeks `UX_Payments_OneSettlement`
   on a literal; none ranges over rows it does not own.
   - Tests: `Settling_twelve_orders_in_parallel_never_deadlocks` (Initiated / Capturing / Failed) asserts all
     settle, twelve distinct invoice numbers, and **zero deadlock victims on this host's own commands** — a new
     `CommandCountingInterceptor.DeadlockCount` counting SQL error 1205, not the server's
     `Number of Deadlocks/sec`, which is shared with other runs on the test server and would make the test fail
     for someone else's contention. And the deterministic version,
     `A_settlement_is_not_blocked_by_a_lock_on_another_orders_payment`: a second connection holds an exclusive
     lock on another order's payment row and the settlement must still finish. **Both were run against the
     unfixed statement and fail** (11 and 20 deadlock victims; a `TimeoutException`; a call that ran out of
     retries), and pass fixed.
2. **A full-width spelling of the bank code paid a bank order with no approver.** The payment was found with
   `ProviderCode = @code`, i.e. the *database* compared the caller's text, case- and width-insensitively; the
   guard compared the same text in C# and found it different. Now the row is read by id and **the stored row's
   code decides**: a bank row throws `ArgumentException` whatever was passed, and a caller code that is not
   ordinally equal to the row's is `NotFound`. Every later statement uses the row's own code.
3. **Non-ASCII keys** — see "Keys must be storable" above.
4. **Unchecked field lengths** — see "What the provider sends has to fit" above. A `ConfirmedCurrency` is also
   clipped to 8 characters in the alert details.
5. **Backend `IsSafeAbsoluteUrl` was looser than the storefront's `safeRedirectUrl`** (it accepted
   `https://good.example@evil.example/`, `http://[::1]` and `http://127.0.0.2`, and a comment said they
   matched). It now mirrors the TS rule exactly: trimmed, absolute, no user name or password, `https` on any host,
   `http` only on exactly `localhost` or `127.0.0.1`. The rule is **not tightened beyond the storefront's**
   (an `https` IP literal is allowed on both sides — the value is server-controlled and the redirect is the
   browser's). One list of safe and unsafe addresses is run on both sides, each naming the other, and a C# test
   fails if an entry is missing from the TS spec.
6. **The redaction list did not match its own doc** — see "Payload rules" above. Whitespace at the end of this
   file is fixed (`git diff --check` is clean).

## The tester's second round (HEAD 12cbe67): two narrow findings and one cosmetic, all fixed

1. **The status gate lost a race (my regression).** `CanHaveOtherOpenAttempt(payment.Status)` used the status read
   at the top of `SettleInTransactionAsync`, before the order claim and with no lock. A retry flow locks the order
   first: payment A read as Initiated, the settlement blocked on the order row, the other transaction cancelled A
   and opened a newer attempt B and committed, and the settlement paid A and left B Initiated — against doc 08
   Appendix B1. **A read taken before the lock is not a fact about the state the lock protects.** The tester showed
   with raw SQL that the `INDEX(IX_Payments_OrderId)` hint alone removes the deadlock (0 of 40 with it, 40 of 40
   without), so the gate is gone — `CanHaveOtherOpenAttempt`, its call site and its test are deleted — and
   `CloseOtherOpenAttemptsAsync` runs on every settlement. New test
   `An_attempt_opened_while_the_settlement_waited_for_the_order_is_closed_with_it` makes the race happen with a
   second connection (order locked, settlement observed blocked through `sys.dm_exec_requests`, then A cancelled,
   B inserted, commit) and asserts A `Succeeded` and B `Cancelled`; it fails with the gate put back (B stays
   Initiated). The deadlock tests stay, and now cover the close statement in all three opening statuses.
2. **The dropped-id rule over-reached.**
   - A too-long **payer reference** no longer takes the `provider_data_invalid` path: it is informational and
     unindexed, so it is cut to 100 characters and the payment settles. (The exception path still cuts it and
     says so in the failure message.)
   - The **transaction id** check now happens **after the order claim**, with a rollback to the `BeforeSettle`
     savepoint, so an already-paid or expired order reports `order_already_paid` / `order_expired`. The reason
     `provider_data_invalid` is for a payable order only, and its wording no longer leaves room to "accept": the
     payment's `FailureMessage` says "Verify the payment with the provider, then refund it or record the reference
     by hand", and the alert title ends "Check it with the provider, then refund it or record the reference by
     hand." No other reason carries a remedy. I did **not** add a `FailureCode` for it (so a later "accept an
     exception payment" use case would not refuse it on code alone) — that is a decision for that use case, and
     the wording is the only guard today.
   - Tests for each combination: payable + long txn id (four spellings, claim undone, order still payable),
     payable + long payer reference (settles, cut), already-paid + long txn id, expired + long txn id.
3. **Redaction no longer eats result flags** (`processor_response.cvv_code`, `cvc_check`, `tokenization`); real
   secrets (`cvv`, `card_cvv`, `cvc2`, `tokenization_token`) are still redacted. See "Payload rules" above.
   The two URL-parity gaps the tester noted (`https:evil.example` and `https:\\evil.example\x`, accepted by the
   WHATWG parser and refused by .NET) are left as they are — the backend is the stricter there — and are recorded
   in a comment above the shared list on both sides.

The test-fixture cleanup failure (a `SendEmails` sweep re-taking the single-user slot at assembly cleanup) is
not touched here and is being filed separately; the "Test Assembly Cleanup Failure" note in the verification
block above is that.

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

### Verification after the tester's first round

- `dotnet build SpencerDenim.sln`: 0 warnings, 0 errors. `git diff --check origin/main..HEAD`: clean.
- Unit, the whole project: **2,125 passed**, 0 failed (new this round: `PaymentEventPayloadTests` now 89,
  `PaymentRedirectFormTests` 38, `PaymentExceptionAlertTests` 14).
- Integration, `FullyQualifiedName~Sales` plus `AdminAlertsBehaviourTests`, `OrderEmailTransactionTests` and
  `AdminBankTransferEndpointTests`: **616 passed**, 0 failed, 29 m 50 s. New: `OrderSettlementRobustnessTests`
  37, `PaymentEventStoreTests` 33 (was 19). The run ended with an xUnit "Test Assembly Cleanup Failure": the
  fixture's `ALTER DATABASE … SINGLE_USER` succeeded and the `DROP` after it did not, leaving this run's database
  single-user on the test container (I dropped it by hand). None of the shorter runs, including every run of the
  new classes, did this; I could not tell whether a background job in a test host re-took the single-user slot
  or whether it is the 30-minute run on a loaded box, and I have not chased it.
- Mutation check: with the index hint removed and the predicate defeated, the two deadlock tests fail (11 and 20
  deadlock victims, a `TimeoutException`, a call that ran out of retries) and pass with the fix.
- Storefront (Node 24): `payment-redirect.spec.ts` + `order-page.spec.ts` 132 passed (was 100); `ng lint storefront`
  clean.

### Verification after the tester's second round

- `dotnet build SpencerDenim.sln`: 0 warnings, 0 errors. `git diff --check origin/main..HEAD`: clean.
- Unit, the whole project: **2,137 passed**, 0 failed.
- Integration — `OrderSettlementRobustnessTests`, `OrderSettlementStoreTests`, `PaymentEventStoreTests`,
  `BankTransferReviewTests`, `OrderExpiryTests`, `OrderPagePaymentAttemptTests`: **124 passed**, 0 failed.
- Mutation check: with the close statement gated on the pre-lock status again, the new race test fails
  (`Expected: Cancelled, Actual: Initiated`).
- Storefront: `payment-redirect.spec.ts` 73 passed; `ng lint storefront` clean (a comment changed there).
