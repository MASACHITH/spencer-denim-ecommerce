# 2026-10-17 — Tharanga: #185, `READCOMMITTEDLOCK` on the four `sales.Payments` guards

Branch `fix/185-payments-readcommittedlock` (worktree `worktrees/sd-rclock`), cut from `main` at `154b5e4`. Not
pushed, no PR. Backend and tests only: **no migration, no dependency, no setting, no frontend.** Follow-up to #183
(RCSI), same instrument it used for `BankTransferProofStore.OpenPaymentAsync` and the campaign recompute.

**Environment note.** `TEST_SQL_CONNECTION` in this session pointed at the machine's **default SQL Server
instance**, which holds office databases (`daycare_db`, `ReportServer*`, `Fixed_Asset`, …) — what docs/06 §4
forbids. Nothing was run against it. Every run below used `Server=(localdb)\MSSQLLocalDB` (SQL Server 2025
Express), with the variable overridden for the command only. No CI exists, so every figure is from this machine.

## What changed

| Guard | Where | Hint | Backed up by anything else? |
|---|---|---|---|
| Cancellation claim, `NOT EXISTS` a settlement | `OrderCancellationStore.cs` (claim, was line 193) | **added** | **No.** The one predicate. |
| Settlement claim, `NOT EXISTS` a settlement | `OrderSettlementEffects.ClaimPendingOrderPaidAsync` (was line 112); shared by the bank Approve and the provider settlement | **added** | `UX_Payments_OneSettlement`, which turns a stale read into a **thrown** 2627 after the order was claimed |
| Bank late acceptance, `NOT EXISTS` a settlement | `BankTransferReviewStore.AcceptLateAsync` (was line 306) | **added**: the guard exists and has the settlement claim's shape | `UX_Payments_OneSettlement`, same thrown 2627 where the screen is owed a refusal naming the settlement |
| PayPal declined, `EXISTS` a Pending payment | `PayPalPaymentStore.RecordDeclinedAsync` (was line 294) | **added**: the guard exists, inside the `CASE` that cuts the hold back (R2) | **No.** A stale "not Pending" leaves the 48-hour hold in place after a decline; no money moves |

### Other `sales.Payments` existence reads found (grep of `src/backend`: every `EXISTS`, `IN (SELECT`, `FROM`/`JOIN sales.Payments`, `Payments.Any` and `ExecuteUpdate`)

None of these is the same shape (an existence guard inside the `UPDATE` that claims the order), so none was changed.
Listed so the owner can decide:

- `PayPalPaymentStore.OpenAttemptInTransactionAsync`, `AnyAsync(... CapturedRequiresRefund ...)`: a separate read
  before the insert. **This is #181**; left alone.
- `OrderCancellationStore.ReadOrderAsync`: `Payments.Any(IsOrderSettlement)` and `Payments.Any(PayPal && Pending)`.
  Plain reads before the claim; the first only picks the refusal's wording (the claim decides), the second is a
  read-then-check the file's own remarks document as outside the claim. With PayPal built since, that remark
  ("nothing creates one") is no longer true, and the `PayPalPending` branch is a read that does not wait. Worth its
  own issue; not widened here.
- `BankTransferProofStore.ReplaceProofAsync`, `Payments.AnyAsync(ProofAssetId == previous ...)` then a soft-delete of
  the media asset: read-then-write on media, not on money or stock (#181 lists "bank slip replacement").
- `BankTransferProofStore.OpenPaymentAsync`, the `existing` Pending read before an insert: backed by
  `UX_Payments_OneOpen` and the caught violation.
- `OrderSettlementStore.ResolvePaymentAsync` / `Settleable`: "read before any statement, takes no lock" by design;
  the claim decides.

## Why the tests do not force a payments-first plan

The issue asked for a forced plan that reads `sales.Payments` before the order. **None exists for these four
statements.** Each production statement was run through an interceptor that appended one query hint, with a writer
holding **both** the order row and the payment row (so the plan's order matters), on an RCSI database:

| Hint appended | cancel | settle | late accept | declined |
|---|---|---|---|---|
| none (natural plan) | fresh (refused / cut) | fresh | fresh | fresh |
| `FORCE ORDER` | fresh | fresh | fresh | fresh |
| `LOOP JOIN, FORCE ORDER` | fresh | fresh | fresh | fresh |
| `RECOMPILE`, `RECOMPILE, FORCE ORDER` | fresh | fresh | fresh | fresh |
| `HASH JOIN` | **error 8622** | **error 8622** | **error 8622** | **error 8622** |
| `MERGE JOIN` | **error 8622** | **error 8622** | **error 8622** | **error 8622** |

8622 is "the query processor could not produce a query plan because of the hints". The subqueries are keyed on a
parameter, so there is no join predicate between the order and the payments; the only operator left is a nested
loops anti semi join with the order seek outside and the payments seek inside. The plan the tester measured as
`Clustered Index Seek (PK_Orders)` then `Index Seek (UX_Payments_OneSettlement)` is the only plan these statements
can have, so a plan-order exposure needs a subquery **correlated** with the target, and these are not.
**But an unhinted guard misses a payment write that has not committed in every plan**, because under RCSI a plain
read never waits for an uncommitted writer: with a writer holding only the payment row, the order seek does not
wait for anyone, nothing fixes the read point late, and the guard answers from the committed version. That is the
scenario the tests use, and it needs no forcing.

Writer holds only the payment row, natural plan, no hint (the pre-fix code):

| Guard | What the unhinted code does |
|---|---|
| cancellation | **cancels the order** and releases its stock; the settlement commits behind it (order `Cancelled/Released`, payment `Succeeded` + settlement) |
| settlement | claims the order, then the payment update runs into `UX_Payments_OneSettlement`: a thrown 2627 |
| late acceptance | claims the closed order, then the same thrown 2627, where staff are owed `order_already_settled` |
| declined | `PaymentDueAtUtc` stays 2,880 min out; with the hint it is cut to 30 |

No path in the code base writes a settlement without holding the order row first, so none of this is reachable
today by an in-tree writer; the guards' own remarks already call that state "a settlement in a transaction of its
own, which is exactly what a webhook does". The hint is what keeps the guard's contract if one ever appears, and
it makes the answer independent of plan order if a correlated rewrite ever makes payments-first possible.

## Tests

`tests/SpencerDenim.IntegrationTests/Sales/PaymentGuardLockTests.cs`, 8 tests, `HintStrippingInterceptor` (new, in
`Infrastructure/`). Per guard: one test that holds the write open, requires the **claim itself** to be the statement
SQL Server reports blocked, then releases the writer and requires the fresh outcome; and a **control** that strips
that one statement's `WITH (READCOMMITTEDLOCK)` at run time and asserts the wrong outcome. The interceptor counts
what it stripped, so a control cannot pass by matching nothing.

### Evidence (LocalDB, SQL Server 2025; targeted runs only, the full suite was not run)

| Run | Source | Result |
|---|---|---|
| Red | the four hints absent (`main`) | the four **guard tests fail, each for its own reason**: cancellation, the claim was not the statement that waited (the call was blocked later, in `UPDATE sales.Payments`) and, left to finish, **cancels the order**; settlement and late acceptance, a thrown `UX_Payments_OneSettlement` violation; declined, blocked in the payment update, not the guard, and the hold stays 2,880 min out. The four controls fail with "the control removed no hint", as they must when there is none to remove |
| Green | all four hints | **8 / 8** |
| One hint removed from source | only the **cancellation** hint taken out of `OrderCancellationStore.cs` | exactly two fail: the cancellation guard test and the cancellation control (nothing left to strip). The settlement, late-acceptance and declined tests and controls (6) stay green, so each hint is judged by its own test and nothing else shares its wait |

The runtime controls make the same one-hint-at-a-time check for every guard on every run without editing source.
Each control strips only its own statement's hint (`Stripped == 1` is asserted) and requires the wrong outcome.

Targeted regression with the hints in (existing suites that run these statements: `OrderCancellationTests`,
`BankTransferReviewTests`, `OrderSettlementStoreTests`, `OrderSettlementRobustnessTests`,
`PayPalCaptureFlowTests`, `OpenPaymentIndexTests`): **126 / 126 pass** (40 min on this machine), the 8 new tests among them.

## Left for the owner

- docs/08 §1.1 and §5.2 name the sites that need `READCOMMITTEDLOCK` ("the campaign recompute, §7.4;
  `BankTransferProofStore.OpenPaymentAsync`"). They should now also name the four guards here. Not edited: docs/08 is
  the owner's.
- Deadlock exposure is what the guards had before #163: the hinted read takes a short shared lock on the payment
  while the claim holds the order row, in the documented order (order, then payments, §5.3). A writer that took the
  payment first and the order second would deadlock with it, as it would have before RCSI.
- #181 (PayPal open-attempt check) is the same disease and is untouched here.
