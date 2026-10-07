# 2026-10-07 — W3-03 steps 2 and 3 (#53): `IPaymentProvider` and the bank-transfer flow (PAY-01, PAY-03, ADM-09)

Branch `feature/W3-03-bank-transfer`, worktree `worktrees/sd-pay`, cut from `origin/main` at `006a0c9`.
Scope: PAY-03's provider seam, PAY-01's bank-transfer flow end to end, and doc 11 P7 steps 1, 3, 4, 6 and 8
plus P10 flow B's expiry. **Not pushed, not a PR — the tester goes first.**

Continues `2026-10-04-tharanga-w303.md` (the schema, step 1) and `2026-10-05-w302-checkout.md` (the order,
which this task's money hangs off).

## What it does, end to end

A shopper places a bank-transfer order and lands on the order page. The page now shows the shop's bank
details, the exact amount, **the order number to quote as the reference**, the deadline (placed + 48 h) and
an optional box for their bank's reference and a photo or PDF of their slip. Staff open
**Orders → Bank transfers awaiting approval**, see every waiting transfer oldest first with the time left,
check the bank statement, and press Approve, Reject or Accept late payment. Approve pays the order, turns
the reserved stock into sold stock and issues a gapless invoice number, all in one transaction. A job
sweeps every five minutes and releases the stock of anything nobody paid for.

**Nothing claims an email was sent**, anywhere, because nothing sends one.

## P7, step by step, and what is actually built

| P7 | Built | Where |
|---|---|---|
| Preconditions: provider enabled, bank details set by a Super Admin | ✅ | `Bank.*` in `SettingCatalog`, `Settings.Payments`, seeded null |
| 1. Instructions emailed at placement | ⚠️ **page, not email** | `OrderPage`'s pay panel. `ops.EmailOutbox` is W3-06 and nothing promises an email |
| 2. Customer transfers | — | outside the system |
| 3. Reference and slip, a new upload replacing the previous | ✅ | `PUT/POST /api/orders/{token}/payment-reference|payment-slip` |
| 4. Staff queue, oldest first, with time left | ✅ | `GET /api/admin/orders/bank-transfers`, `/orders/bank-transfers` screen |
| 5. Staff check the statement | — | outside the system |
| 6a. Approve | ✅ | `IBankTransferReviewStore.ApproveAsync`, one transaction |
| 6b. Reject | ✅ | `RejectAsync`, order stays `PendingPayment` |
| 6c. Wait | ✅ | no action; the row stays in the queue |
| 7. Expiry after the due time | ✅ | `ExpireUnpaidOrdersService` + `IOrderExpiryStore`, every 5 min |
| 8. Accept late payment with a stock re-check | ✅ | `AcceptLateAsync`, refuses with every short line |
| G-13 refund of money received on a never-paid order | ❌ **not mine** | `Orders.Refund` and `sales.Refunds`; W3-04 / ADM-09 |

## The decisions, and why

### PAY-03 — the seam, and the test that makes the claim worth something

`IPaymentProvider` has exactly two members beyond its code, because every extra one is a thing the next
gateway must answer and a thing checkout could come to depend on:

- `InitiateAsync` — "what does the payment row for this order look like, and when is it due?" Doc 08 §4.1's
  placement side effects differ per provider *only* here: bank transfer opens `Pending` for 48 h, PayPal
  will open `Initiated` for `PayPalExpiryMinutes` with a USD `ProviderAmount` at a frozen rate.
- `DescribeAsync` — "what do I tell the customer to do next?" Bank transfer answers with account details and
  the reference; PayPal would answer with an approval URL. Both are one `PaymentInstructions`, which is why
  it has a `RedirectUrl` a bank transfer never sets and `Fields` PayPal never would.

**Approve and reject are deliberately not on it.** No gateway has them, they are a staff decision about a
bank statement, and putting them there would force `PayPalProvider` to implement two methods that can only
throw — the Liskov point doc 03 §3c makes about this port. What the two genuinely share is the *Paid
effects*, and that is a transaction (`IBankTransferReviewStore.ApproveAsync`) for W4-01 to call rather than
copy.

**Checkout no longer names a gateway.** `PlaceOrderCommandHandler` asks `IPaymentProviderRegistry` for the
provider the shopper chose and writes its `PaymentInitiation` verbatim; `OrderPlacementStore.BuildPayment`
has no provider-specific line left. `Amount` and `Currency` are deliberately **not** the provider's to
decide — doc 08 §3.5 defines `Amount` as the order's `GrandTotal` in LKR outright, and a provider that
could change it could take a different sum from the one `CK_Orders_Totals` checked and the shopper agreed
to (G-10). What a provider may add is a *second* figure in its own currency.

**`CheckoutNamesNoProviderTests` is what keeps the claim true**, and it earned its place immediately: it
found two real leaks in my own first version.

1. `CheckoutOptions.BankTransferExpiryHours` — a class called "checkout options" whose only property is a
   bank-transfer fact, in the namespace that is supposed not to know what a bank transfer is. Now
   `Application.Sales.Payments.BankTransferOptions`, read by `BankTransferProvider` and nothing else.
   The config section moved with it from `Checkout` to **`Orders`**, which is doc 08 §3.8's own group name
   for the key, so the variable is `Orders__BankTransferExpiryHours`. Nothing in the repository, the deploy
   files or the docs set the old name — only W3-02's journal mentioned it, as owed work — so nothing had to
   be migrated.
2. `GetOrderQueryHandler` took `IBankTransferReader` directly, putting a gateway's name in the signature of
   the order read. Now `IOrderPaymentProofReader`, a neutral port `BankTransferReader` also implements.
   The question the order page asks is "has the customer sent us anything about paying", which is a question
   about an order; that bank transfer is the only provider that answers it is an implementation fact.

The test also asserts that **nothing in the checkout namespace declares a `PaymentStatus`**, which is the
subtler half: a handler could name nothing after a gateway and still decide that a new payment opens
`Pending`, which is a bank-transfer fact and not a general one.

`PaymentProviderContractTests` runs doc 03 §3c's contract over **every** `IPaymentProvider` found in the
assembly, discovered by reflection rather than listed — so PayPal is covered the day it is added rather than
the day somebody remembers to copy a test. It asserts the code is a seeded `sales.PaymentProviders.Code`
(doc 08 §9's list, not the constants), that the opening status is inside `UX_Payments_OneOpen`'s filter and
outside `CK_Payments_IsOrderSettlement`'s allowed set, that the due time is **strictly after** the placement
time, and that a provider currency implies an amount and a rate. Providers are built with stubs rather than
through DI, so the contract is provable without a database.

### Bank details as settings — the client fills them in, nothing is invented

Five keys in a new `Bank` group (`BankName`, `Branch`, `AccountName`, `AccountNumber`, `Instructions`),
`Settings.Payments` on every row, **seeded null**. The client has not given us their bank details and an
invented account number would be worse than none: a shopper would transfer real money to a number nobody
owns.

`PUT /api/admin/settings/bank` is ADM-22's pattern, and this is the group its round-1 fix was written for.
ADM-22's own note said the absent-≠-null fix "bites the moment #53 adds a role that cannot read every
group" — this is that moment. No default role holds `Settings.Payments` (doc 08 §9 gives it to nobody), so a
Store Manager's settings screen genuinely does not receive these five keys, and if absent meant null their
first save of anything would wipe the shop's bank account number. `AdminSettingsEndpointTests` and
`settings-page.spec.ts` both assert the body, with **every field holding a real value** — a fixture with
nulls in it lets a dropped field round-trip null to null, which is how W1-12's blocker survived 1,571 tests.

Three of the five are indispensable (bank, account name, account number) and two are optional.
`BankTransferDetails.TryCreate` is the only way to make one and returns null unless all three are there, so
**half-filled details show nothing at all**. That is the decision rather than the document's: a shopper who
saw a bank name and an empty account number might transfer to whatever they found, and the money would then
have to be traced by hand.

`Bank.Instructions` is `String`, **not** `TextHtml`. Doc 08 §3.8 states no type for it and "instructions" is
the obvious reading — but `TextHtml` is sanitised on save with the content-pages allow-list sanitiser (N10),
which belongs to ADM-17 and does not exist, so an HTML value would reach a public order page unsanitised.
The order page interpolates it as text and keeps its line breaks in CSS.

**Account-number validation is deliberately shallow**: digits, spaces and dashes, at least one digit, 40
characters. Sri Lankan account numbers have no single national format — length and grouping differ by bank —
so a pattern rule would eventually refuse a correct number typed by the only person who knows it, with no
way round it from the screen. The grouping the client types is **kept**, because a shopper copies it into
online banking and the shop does not know the format better than the client does.

### The three decisions, each one transaction

Doc 08 §4.1's conditional-UPDATE design, statement for statement. **The claim is the lock**: every path that
changes an order starts with one conditional `UPDATE` on `sales.Orders` whose `WHERE` carries the states the
transition is allowed from. Doc 08 §5.2's argument applies exactly as it does to the stock reservation — SQL
Server takes an update lock on the qualifying row, a second writer waits, then **re-evaluates the predicate
against the row as it now is**. So two staff approving at once do not both see `PendingPayment`.

| | Order | Payment | Stock | Invoice |
|---|---|---|---|---|
| Approve | `0 → 1`, `StockState 0 → 1`, `PaidAtUtc`, lock cleared | open `Pending → Succeeded`, settlement, approver + time + note | `OnHand − q`, `Reserved − q` | gapless, inside the transaction |
| Reject | **unchanged** (stamp only) | `Pending → Failed`, `REJECTED`, reviewer + time + note | unchanged | none |
| Accept late | `5|6 → 1`, `StockState 2 → 1`, `PaidAtUtc = now`, `ClosedAtUtc = NULL` | latest `Pending|Expired → Succeeded`, settlement | `OnHand − q` only | a **new** gapless number |

**The payment row is resolved inside the transaction, never by the caller.** An admin request carries an
order token and nothing else. A payment id on the wire would be stale by the time it arrived: a rejection
closes a row and a resubmission opens a new one, so "approve payment 41" can arrive after 41 was rejected
and 42 created. Resolving it under the order's own lock makes that class of mistake unrepresentable.

**Invoice numbers are the opposite trade to order numbers, and `AllocateAsync` gives it for free.** It
enlists in the ambient transaction, so the series row stays locked until the decision commits and a decision
that rolls back gives its number back. Checkout's `AllocateOrderNumberAsync` throws if a transaction is
already open and is deliberately not used. The number is allocated **after** the order claim, so a decision
that lost the race never touches the series at all — either order is gapless, but holding that row for a
transaction we already know will fail is pointless contention. The year is the **Colombo** year, the same
rule as the order series.

**No `sp_getapplock` and no `Serializable`, and that is a correction to the brief** — see below.

### The expiry job

`ExpireUnpaidOrdersService`, a `BackgroundService` on a five-minute `PeriodicTimer`; `IOrderExpiryStore` is
doc 08 §5.6 statement for statement: `TOP (50)` from `IX_Orders_Pending_Due`, then **one transaction per
order**, claim → release per line in ascending `VariantId` → open payments `(0,1) → Expired` → history with
source `SystemJob`.

**No lease and no leader election.** `ops.JobLeases` is in doc 08 §3.8 and in no migration — and none is
needed: the conditional claim means that of any number of instances racing for one order exactly one expires
it and the rest are told to skip. A lease would make the *job* run once; the claim makes each *order* expire
once, which is the invariant that matters. `Two_instances_of_the_job_expire_one_order_once` proves it over 6
rounds of 4 contenders, including that only one history row is written.

The service follows `CatalogSeedHostedService`'s three hard-won lessons: it never throws out of
`ExecuteAsync`, it swallows the `OperationCanceledException` a `PeriodicTimer` raises on a healthy shutdown,
and a repeated failure drops from `Error` to `Warning` after the first so the log stays a signal. A failure
on one order does not stop the pass — the one thing that can fail is a drifted stock row for a single
variant, and letting that block every other order's release would turn a bookkeeping fault into the
stock-squatting problem the job exists to end.

Five minutes and 50 are constants rather than settings because doc 08 §3.8's list has neither. The
*deadline* is configurable, which is the part a shop has an opinion about; how often a job sweeps is an
implementation detail, and a shorter interval cannot expire anything sooner than its own deadline allows.

**This closes half of #107.** The other half — a placement throttle — is still an owner decision and is
untouched: doc 08 §3.3 N10 throttles slip uploads and G-41 covers only the order link, so adding one would
be building outside the scope baseline.

### The queue, and DB28

`GET /api/admin/orders/bank-transfers`, `Orders.View`, oldest first, with everything P7 step 4 lists.
`secondsUntilExpiry` is computed **on the server** and is **negative** once the deadline has passed: a
rendered "4 h 12 m" is stale the moment it is serialised, and a figure computed in the browser would be
wrong on a shop computer with a wrong clock with nothing on the page saying so. Negative rather than clamped
because "overdue by eleven minutes" and "due now" are different facts to somebody deciding whether to
approve.

**`IX_Payments_BankQueue`'s literal filter: #103's review asked for this and it was worth asking.** The
index is filtered `WHERE ProviderCode = 'BankTransfer' AND Status = 1`, and SQL Server matches a filtered
index only when the predicate *provably implies* the filter. `BankTransferReader` compares against
compile-time constants (a `const string` and an enum member), so EF emits literals. `BankQueuePlanTests`
reads the plan and proves all of it:

- the literal predicate **seeks** `IX_Payments_BankQueue`;
- the parameterised one does **not** — and SHOWPLAN says so itself, listing the index under
  `<UnmatchedIndexes>` by name while reading `PK_Payments` instead. That is DB28 in the optimiser's own
  words, which is better evidence than inferring it from an absent seek;
- the query EF actually generates for the reader seeks it, asserted on `ToQueryString()` first so a failure
  says *why* the plan changed;
- the expiry job's batch read seeks `IX_Orders_Pending_Due`, which is filtered `WHERE Status = 0` for the
  same reason.

The first version of that test asserted the index name was **absent** from the parameterised plan and
failed. "Mentioned" and "used" are different things; the helper now separates them, which turned the mistake
into the better test.

### The slip

`IMediaService.UploadPaymentSlipAsync`. An **image** goes through the library's own hardened image pipeline
with `Private` forced — which matters more here than for a product photo, because that path strips EXIF and
a phone photograph of a deposit slip carries the GPS coordinates of where it was taken. A **PDF** is stored
byte for byte: a PDF is a programmable container and every library that opens one is a parser running over a
stranger's bytes on the server. Visibility is **not a parameter** on the request — a caller that could ask
for a public slip could put a customer's bank document on the volume the reverse proxy serves.

Three rules hold on every slip route: it is never reachable by its own identity (every route names an
*order*), never cached (`no-store`, against `/media`'s year-long immutable cache), and always served as
`attachment` with `nosniff`.

The upload endpoint sets its **own** 5 MB body ceiling rather than reusing `MediaOptions.MaxRequestBytes`
(~65 MB): that figure is sized for a video and its poster on an authenticated admin route, and this route is
reachable with nothing but an order token.

**One schema fact worth knowing.** `UX_MediaAssets_Checksum_Ready` is unique on
`(Checksum, MediaType, Visibility)` for live `Ready` rows, so two *identical* private files cannot both
exist — the second upload is answered from the first's asset. If two orders ever shared one slip asset that
way, soft-deleting it when one of them replaced their slip would make the other order's slip vanish from the
staff queue: a customer's evidence destroyed by somebody else's upload. Identical bytes across two real
slips is vanishingly unlikely and the deduplication is forced by the index, so the fix is one read rather
than a migration: `ReplaceProofAsync` soft-deletes the previous asset **only when no other payment points at
it**.

### `UX_Payments_OneOpen` — #103's open question, settled

Doc 08 §3.5 claims the index "can never block capture recovery or a customer retry". Part of that is true and
part is not.

- **Bank transfer: true.** A rejection closes the row as `Failed (4)`, outside the filter, so a
  resubmission's new `Pending (1)` row cannot collide with it (§3.5 N7). What the index *does* constrain is
  two resubmissions at once — both find no open row, both insert, one gets 2627 — and that is the index
  enforcing its own rule correctly. `OpenPaymentAsync` treats the violation as an expected outcome and
  re-reads, so the loser uses the winner's row; the alternative is a 500 to a customer who tapped Upload
  twice. 8 rounds × 4 contenders.
- **Capture recovery: true.** Recovery moves `Capturing (2)` to 3, 4 or 7, none of them inside the filter.
- **A customer retry on PayPal: false.** §4.2 step 4's PENDING result moves the row
  `Capturing (2) → Pending (1)`, *into* the filter. A retry is allowed "only when no claim is active", and a
  claim lapses after `CaptureLockHours` (3 h) while an eCheck capture may stay in flight for
  `PendingCaptureMaxDays` (5 days). So: capture claimed, lock lapses at 3 h, customer retries and gets a new
  `Initiated (0)` row, the original capture then resolves PENDING — and the `UPDATE` that violates the index
  is **the one applying a real capture result**. The failing transaction is the one holding money.

**And there is a second, easier sequence, which review caught and this note had missed — it is the worse
one.** Once the PENDING result is applied the row sits at `Pending (1)` *inside* the filter and §4.2 step 4
says the capture lock is **cleared**, so "a retry is allowed only when no claim is active" is satisfied
immediately: no three-hour wait is needed. The retry's own `INSERT` of an `Initiated (0)` row collides at
once, and step 5 only cancels an *abandoned* `Initiated` row, which this is not. What the index refuses here
is **the buyer's retry**, for up to five days, with nothing on screen able to explain it.

That changes the recommendation. "Cancel the open retry row inside the apply transaction" fixes the first
sequence and does **nothing** for this one, where the retry arrives after the apply. What covers both: make
the **retry path** close or reuse an open `Pending` row rather than insert beside it — the retry is the
transaction that knows it is a retry — and hold the capture lock for the whole pending window rather than
3 h, which also stops the first sequence arising at all because the retry is never offered while a capture
is in flight.

Both sequences are unreachable today (nothing captures; doc 08 §9 seeds the PayPal row disabled), and both
are written into `PaymentConfiguration`'s remarks where W4-01 will be standing, with the instruction **not**
to solve it by catching 2627 — doc 08 §4.2 is explicit that the design never relies on catching a unique
violation on the settlement path, and a swallowed one would discard a capture.

**Architect: doc 08 §3.5's sentence should be narrowed.** It is right about recovery and about the bank
flow; it is wrong about a PayPal retry, and the wrongness is on the money path.

## Two real defects my own tests found

Both in code I had just written, both found by integration tests rather than by review, and both the exact
shape the brief warned about.

### 1. A `record struct` projection that could not be null — and a PayPal order was paid by the bank flow

`BankTransferReviewStore.ReadOrderAsync` projected to `private readonly record struct OrderSnapshot` and
returned `OrderSnapshot?`. **`FirstOrDefaultAsync` over a projection to a value type cannot return null** —
it returns `default` — so `OrderSnapshot?` came back with `HasValue = true` and every field zeroed whenever
the query matched nothing. `Status` and `StockState` zeroed are `PendingPayment` (0) and `Reserved` (0),
which are **the exact two values the claim's own predicate was about to look for**.

The read filtered on `PaymentProviderCode = 'BankTransfer'`; the claim did not. So a PayPal order aimed at
Approve passed the `is not { }` guard with a blank snapshot, the claim matched on status alone, and the order
was **paid, stock-deducted and invoiced by the bank-transfer flow**. `A_PayPal_order_cannot_be_approved_through_the_bank_flow`
answered `Applied` where it expected `NotFound`.

Two fixes, because one would have left the other half fragile:

- `OrderSnapshot` and `PaymentRow` are `sealed record`s, so "no such row" is representable. `PaymentRow` had
  the same latent bug: as a struct, "no reviewable payment" came back as `Id = 0, Status = Initiated`, which
  skipped the refusal branch and ran a settle statement against payment 0 — still a refusal, but the *wrong*
  one.
- `AND PaymentProviderCode = 'BankTransfer'` moved **into** all three claim statements. A guard that lives in
  the atomic statement cannot be defeated by a bug in a read.

The test also now asserts its own arrangement (`ExecuteUpdate` affected 1 row, and the column really reads
`PayPal`), because a fixture that silently did nothing would have made the three assertions below it test the
bank path against a bank order — a test passing for the wrong reason.

### 2. Reject took no order-row lock, against doc 08 §5.3 — and **this section was overstated**

**Corrected after review, and the correction matters more than the original claim.** What I wrote here, and
in the store's own comment, was that without an order-row claim a losing rejection would set the row that
now settles the order to `Failed` and so violate `CK_Payments_IsOrderSettlement`, turning a race into a 500.
**That cannot happen.** The constraint is `IsOrderSettlement = 0 OR Status IN (3, 8, 9)`; the rejection's
`UPDATE` never touches the flag and matches only a row still at `Pending`; and a row at `Pending` already
has the flag clear, or it would be violating the constraint where it sits. The statement's own
`AND Status = Pending` is the protection, and for correctness it is sufficient — the loser updates nothing.

What the claim actually buys is smaller and still worth having:

- **Lock ordering.** Doc 08 §5.3 says "paths that start from an existing order claim the order row first",
  and a rejection starts from one. Without it, approve and reject take the order row and the payment row in
  opposite orders, which is a 1205 waiting for two staff on one order — avoided by design rather than
  retried.
- **A refusal diagnosed from settled state.** The loser now *waits* for the winner's commit instead of
  racing it, so what the screen says is "this order has already been paid" rather than a sentence derived
  from a read that was already stale. A 0-row update with a stale diagnosis is not a wrong write, but it is
  a wrong sentence about somebody's money.

**And the test story was wrong too.** This section said
`Approving_and_rejecting_at_once_applies_exactly_one_of_them` was "the one no sequential test could
replace". A tester reverted the claim and ran it 48 rounds without a red — mechanically expected, because
approve executes six statements before committing, so reject almost always reaches its `UPDATE` first. That
test establishes that **exactly one decision applies** under real contention and that the rows agree with
the winner; it does not cover the claim. The claim has **no red-before**, which is now recorded in the
mutation table instead of being implied here. The test's own comment, which still said "reject deliberately
does not claim the order row", is corrected in place.

## Four more defects, found only in a browser

The jsdom specs asserted the DOM and the outgoing bodies and passed throughout. None of these four is
visible there, which is the argument for the browser tests in one paragraph.

### 1. The queue's slip link rendered 135x20 px

`class="link"` is the **storefront's** vocabulary. The admin app has no such rule, so the link fell back to
an anchor's defaults and failed `undersized()` on height at every width. Fixed with a shared `.adm-link`
rule (`inline-flex`, `align-items: center`, `min-height: var(--tap)`) rather than a one-off, because the
next admin page to want a link-shaped control will want the same thing. jsdom cannot see this: it has no
layout, so a 20 px tall link and a 44 px one are the same DOM.

### 2. Focus landed on `<body>` after an approval

My own comment said the row survives a decision so focus survives with it. **The comment was wrong**: the
action bar is replaced by the outcome message, and the button that had focus is gone with it. A keyboard
user who approves an order is dropped to the top of the document.

The outcome paragraph is now focusable (`tabindex="-1"`) and takes focus in `afterNextRender`. A
`queueMicrotask` attempt still lost it, which is worth writing down: the microtask queue is where change
detection is *scheduled*, not where it has finished. The `:focus` ring is explicit because Chrome does not
grant `:focus-visible` to programmatic focus.

### 3. The order page overflowed 74 px at 320 px

Grid items default to `min-width: auto`, which is min-content, and **a file input's min-content does not
shrink**. The pay panel therefore forced a 362 px minimum on a 320 px viewport — one of the project's
non-negotiables, broken on the page a shopper reads in order to pay.

`minmax(min(100%, 16rem), 1fr)` and a `max-width` were not enough. The fix is `min-width: 0` at every level
(`.pay`, `.pay__details`, `.pay__proof`, `.pay__field`, `.pay__detail dd`) plus explicit width bounds on
`.pay__input`. What made it findable was teaching the assertion to name the widest offenders instead of
reporting "74": the message pointed at `h2.order__panel-title` at 362 px, which is what proved the minimum
came from the panel's own grid rather than from the heading.

### 4. The Copy button measured 40x44 px

The shared `button.link` rule sets `min-height: var(--tap)` and says nothing about width — right for a
control that is a word inside a sentence, wrong for one standing on its own next to a value. "Copy" is
simply narrower than a finger. Fixed locally on `.pay__copy` (`min-width: var(--tap)`,
`justify-content: center`); **widening every link-button in the shop is a design-system decision and not
mine to take**, but any short-labelled `button.link` elsewhere in the storefront has the same defect today,
and that is worth someone's time.

### And one shopper-visible flaw the flake pointed at

The proof controls were inside a `<form>` with `type="submit"` and `(ngSubmit)`. Before Angular hydrates
there is no `ngSubmit` handler, so a click on Save is a **native** submit to the page's own URL: the browser
navigates to `?payerReference=...&slip=`, the reference the shopper just typed goes into the query string
instead of onto the order, and nothing tells them. Silent loss of something a shopper typed about their own
money, on a slow connection — which is exactly the connection a 320 px device tends to be on.

**I claimed there was no other `<form>` in the storefront, and that was false.** `checkout-page.ts` has
one, with its submit button attached from outside by the `form=` attribute, which is why a grep for
`(ngSubmit)` found only mine. So the storefront had the same trap in a worse place: before hydration a
native submit puts a shopper's name, phone, address and city into the query string, loses the checkout form,
and leaves personal data in browser history and access logs. That one is W3-02's and pre-existing; the
coordinator is filing it. The lesson for me is narrower and sharper: I asserted a negative about a codebase
from one grep, it was relayed onwards as fact, and the worse instance went unexamined because of it. It is now a `<div>` with
`(click)="saveReference()"` and an explicit `(keydown.enter)`, which keeps the keyboard shortcut, cannot
escape to the browser, and takes `FormsModule` out of the page. `[value]` + `(input)` replaces `ngModel`,
which is how every other input in this shop is bound.

### Two lessons about the test harness itself

- **`test.describe.configure({ mode: 'serial' })` serialises one describe, not the file.** The stub's
  bank-details switch was module-level — the bank details really are a property of the shop, so that looked
  like the honest model — and one test turning it off made a test running beside it see a shop with no bank
  details. It is now keyed on the requesting bag and read once at placement, which is
  `checkoutStockFaults`' shape and `checkoutStockFaults`' reason. A fixture switch keyed on the bag has to
  be armed **after** the first add-to-bag, which is why `placeAnOrder` grew an `afterBag` hook.
- **A server-rendered control is actionable before it is alive.** The reference test's second half (after a
  `page.reload()`) typed into the DOM and clicked a button nobody was listening to. `ng-version` on the root
  element is the gate, the same one `home-page.e2e.ts` uses: nothing on this page looks different when it
  hydrates, so there is no rendered state to wait for.

## Round 2: what the tester's review changed

Four Majors, and every one of them is something a client or a shopper would have met.

### MAJOR 1 — a payable order could not be approved from the queue

`canAcceptLate` was keyed on the clock: any row past its deadline offered **only** Accept late payment,
which the server refuses with `order_not_closed` because the order is still open. The clock never goes back
under zero, so Refresh could not get staff out of the dead end — and the row's stock stayed reserved to an
order nobody could approve, sellable to the next shopper, after which Accept late payment would fail its
stock re-check and the money would have to be refunded by hand (G-13, which has no system path here).

Every row in this queue has a `Pending` payment, so every one is still `PendingPayment` with stock
`Reserved`: approvable, whatever the deadline says. The expiry job sweeps `TOP (50)` every five minutes —
normally minutes of overlap, twenty with a 200-order backlog, and **indefinitely if the sweep is failing**.
P7's exception says Accept late payment is for "if expiry won", and the only honest evidence of that is the
server's `order_expired` refusal, which is now the trigger. `order_not_closed` is named in
`BANK_TRANSFER_REFUSALS` so the way back out is a behaviour with a test rather than an accident.

**A unit spec and a browser test asserted the defect, each with a comment claiming the opposite** ("offers
the action that will actually work"). Both are rewritten with the reasoning in them, and two tests added.
That is the second time in this task a confident comment on the money path was wrong; the pattern is a
comment written from intent rather than from the behaviour in front of it.

### MAJOR 2 — a `Media.Delete` holder destroyed a customer's payment slip

A slip is referenced only by `sales.Payments.ProofAssetId`, and attaching one writes no `MediaUsages` row,
so the library's "no usages" guard found nothing: `DELETE` answered 204, the queue's link went null, both
read-back routes 404'd, and `MediaRetention` would purge the bytes 30 days later with nothing on the order
to say the evidence an approval is made on had been removed. **Reachable by a default role** — Store Manager
holds `Media.Delete` alongside `Orders.View`.

**This branch is what made it reachable**: before it, no `Visibility = Private` asset existed. My journal
flagged the *read* half of this hole and not the delete half, which is the more expensive one.

The guard now reads `ProofAssetId` directly, which is the argument the `PosterAssetId` precedent already
makes — a real foreign key beats trusting that a usage row was written — and it also covers slips attached
before the check existed. The 409 names the order, so the "used in…" panel can tell an operator where to
look.

### MAJOR 3 — after a rejection the shopper was told the order could no longer be paid

The reference route required an already-open payment row. A rejection closes the only one there was, so Save
answered 404 and the page rendered *"This order can no longer be paid"* about an order doc 11 P7 step 6b
keeps `PendingPayment` — while the slip upload, two controls lower on the same panel, opened a row and
worked. The shopper saw the pay panel, their "We have slip.jpg" line gone, an empty reference box, and that
sentence; with no email in this build, nothing else would have told them.

Both halves now go through `OpenPaymentAsync`. It is still §3.5 N7's rule and still one open row — asserted
by counting rows, because a fix that bought this with a second open payment would put the same order in the
queue twice.

### MAJOR 4 — invented bank details in the production admin bundle

The placeholders read `Commercial Bank of Ceylon PLC`, `Kollupitiya`, `Spencer Denim Industries (Pvt) Ltd`
and `8001 2345 6789`: a real Sri Lankan bank, the client's own legal entity and a plausibly-formatted
account number, rendered in `dist/admin` on the one screen where a Super Admin types the real values, with
real values in the sections above and below. The criterion I was given was explicit — not in a seeder, a
default, **or a placeholder that could render** — and I had read it as being about stored data.

They now follow the convention the same file's social fields have always used (`yourshop`), the catalogue's
help text no longer names a real bank, and every fixture that asserted the fabricated values uses obviously
fake ones. Checked against the built bundle: no occurrence remains.

### The Minors, and what each one actually was

- **m-1, the red suite.** `The_batch_read_finds_due_orders…` filtered a `TOP (50)` batch down to its own
  three orders — which are the newest in a database nothing deletes from, so they have the *latest*
  deadlines and fall off the batch. Measured 1085/1 in a full run, 9/9 alone. The capped half was vacuous
  the same way: "two rows came back" was satisfied by anybody's orders. Both halves now control the set
  (2020 deadlines, a cutoff nothing else can fall inside) rather than filtering it. This is #114's family
  and the third time this suite has made the mistake.
- **m-2, `Cancelled → Paid`.** Specified in P7's exceptions and P10, and unreachable: the late acceptance
  looked for `[Pending, Expired]` payments while a cancelled order's payment is `Cancelled`, so the claim
  succeeded, no payment was found, and the whole thing rolled back saying there was nothing to review.
  Latent until W3-04 lands cancel, then silently wrong. Fixed in the status list, and
  `A_late_payment_is_accepted_on_a_cancelled_order` now covers it end to end — the state is arranged by
  hand because nothing writes a cancelled order yet, which is exactly why it is worth a test before the
  cancel path lands on top of it.
- **m-3, the lock order.** Approve took `NumberSeries` before `StockLevels`; the late acceptance the
  reverse. Two decisions on a shared variant could each hold what the other wanted — a 1205 that
  `EnableRetryOnFailure` would absorb without anybody seeing it. Approve now deducts first and allocates
  last, which is one order for both paths and the end of the transaction where a throw costs no invoice
  number.
- **m-4, "variant 470 (0 left)".** An internal id, to a staff member deciding whether to refund somebody,
  on a screen with no order-detail page to resolve it in. The refusal now names the product, colour, size
  and SKU with both figures ("0 left, 1 needed"), from `sales.OrderLines`' placement-time snapshot — no
  join, and the words the customer bought under. Deliberately **not** checkout's id-keyed shape: its reader
  is the shopper's own bag, which can resolve an id; this reader cannot.
- **m-5, a provider enabled without settings.** Doc 08 §3.5 says enabling one requires valid settings and
  nothing enforces it, so a fresh deployment takes bank orders nobody can pay. There is no
  `ops.AdminAlerts` in this build, so the provider logs a Warning naming the three keys, on the one path
  that proves a real customer has hit it.
- **m-6, "we will be in touch".** A promise nothing in this build can keep, in the branch reached precisely
  when the shop is not finished setting itself up — and that branch never mentioned the 48-hour deadline
  either. Both fixed. The reviewer's wider point stands: a three-phrase blocklist in the tests is a canary,
  not a property, and "this page only says what the system can do" has to be held line by line.
- **m-7, the customer's slip read-back**, is a scope deviation from P7 ("shown only to staff with
  `Orders.View`") that I believe is right — a shopper who uploaded a file should be able to see which file
  the shop has — but it needs an architect's line in doc 02 or doc 11 rather than only a comment in
  `PaymentSlipEndpoints`. **Owed, and not mine to grant.**

Nits taken: the dead `for` attributes on `<span>`s inside wrapping labels are gone (the wrapper is the
association; `for` on a span is invalid and did nothing — I found three of the four), and
`OrderStatusHistoryEntry` now states the convention a reviewer rightly queried: equal `FromStatus` and
`ToStatus` mean "an event on this order that did not move it", with the note saying which. The alternatives
were worse — `sales.PaymentEvents` is the *provider* log, with an idempotency key and a verbatim payload,
and no row at all would lose the one place an order's timeline can be read.

**One number from the review worth keeping**: the tester got a clean **860/860** Playwright run on an
uncontended machine, against the 856/1/3 measured here. The four non-green tests recorded below are load
artefacts on this machine, and that is now evidence rather than an inference.

## Round 3: two fixes that shipped the defect they were fixing

Both of round 2's UI-side fixes went wrong in the same way, and the shape is worth naming: **I fixed the
state the report described and did not walk the states either side of it.**

### MINOR 1 — the new wrong button, one state along

MAJOR 1 keyed Accept late payment on the server's `order_expired` refusal instead of the clock. But
`refusals` holds one slot per row and `decide()` clears it before each attempt, so pressing Accept late
payment and getting `short_stock` **replaced** the `order_expired` fact — and `short_stock` was the one 409
in the flow carrying no `orderStatus`. The screen therefore went back to offering **Approve** on an order
that had expired and can never be approved: Approve → `order_expired` → Accept late → `short_stock` →
Approve. An oscillation pointing staff at an action that cannot succeed, on a screen about money.

Two changes, because either alone would leave the other half of the hole: the refusal now **carries the
order's status** (it is the only thing the screen is told, so the state has to travel with it), and
`canAcceptLate` reads the **status** rather than only the code — `order_expired` means yes,
`order_not_closed` means no, and everything else is answered by whether the order is `Expired` or
`Cancelled`. That last clause is the one that matters: the next refusal code nobody has thought of will not
bounce the screen back to Approve.

**It also retires a stale claim.** `BANK_TRANSFER_REFUSALS.shortStock` and `.orderNotClosed` were declared
and never read, while their doc comments said they moved a button. Both are read now — `orderNotClosed` in
`canAcceptLate`, `shortStock` in `shortLinesFor`, which renders the stock figures only for the refusal that
has them so a `shortLines` field on some other refusal cannot put somebody else's numbers under this
message.

**And a lesson about my own browser tests.** Removing `orderStatus` from the real endpoint left the
Playwright suite green: the browser tests run against the stub, and the stub sends whatever this repo says
it should. A screen test cannot hold an API to a payload shape. `A_short_stock_refusal_says_the_order_is_still_closed`
is the test that can, and it goes red for exactly that mutation.

### MINOR 2 — the phantom payment, and a description I gave that the code did not match

I reported that "clearing a reference with no open row answers success rather than creating a row to hold a
null". **It did not ship that way**: the handler called `OpenPaymentAsync` unconditionally, so a shopper
pressing Save on the empty box after a rejection — by reflex, or to take back a typo — opened a `Pending`
payment with no reference and no slip, which **put the order back in the staff queue** reading "None given /
None uploaded". Staff check the statement, find nothing, reject it again. `UX_Payments_OneOpen` capped it at
one phantom per cycle, so there was no flood and no money effect, which is exactly what would have kept it
unnoticed.

Worse than the defect: **I described an intention as a fact in a hand-off**, and it was relayed onward and
believed. Three of this task's five wrong claims have been of that family — a comment or a report written
from what I meant rather than from what is in front of me.

Clearing is now its own branch: it reads the order, 404s when it cannot be paid at all (saying "cleared"
about a paid order is a lie whichever value it carries), clears the open row when there is one, and answers
success when there is nothing to clear. The route's throttle remark is corrected too — it argued that a
reference only ever rewrites "a row that already exists", which stopped being true when MAJOR 3 made the
route able to open one.

### MINOR 3 — a Warning that fired on every page view

`DescribeAsync`'s warning sat on `GET /api/orders/{token}`: unauthenticated, unthrottled (G-41 is owed) and
re-read on every refresh, so one shopper refreshing produced one Warning per refresh. A shop live without
bank details would have buried its own log — the failure `ExpireUnpaidOrdersService` guards against one file
away. Now once per process, through `BankDetailsWarningOnce`: an injected object rather than a static field
**so the throttle can be tested** (a static one could not be reset, so it would be either untested or
order-dependent), with three unit tests including 64 simultaneous callers. Once per *order* is the better
signal and belongs in `ops.AdminAlerts`, which is not this task's table; the log message says it is
process-wide so nobody reads one line as "one affected order".

### The four comments

1. **The `<form>` claim was still in `order-page.ts`** — I corrected the journal and left the comment a
   future reader actually hits, which is the worse half of the mistake. It now says both halves were false,
   names issue #123 and the data a pre-hydration checkout submit leaks, and says plainly that a comment
   written from intent rather than behaviour is worse than no comment.
2. The reference route's throttle justification (above).
3. The two unread refusal constants (above).
4. `MediaUsageOwnerTypes.Payment` now says it is **synthesised for the 409 and never persisted**, and is
   not one of doc 08 §3.3's owner types — the first version invited a reader to go looking for rows that do
   not exist.

### And two corrections to round 2's own claims

- "Every fixture uses obviously-fake values" was **slightly overstated**: `order-page.spec.ts` still used
  `Kollupitiya` as the *branch*. Fixed. `Kollupitiya` as a **city** in an address fixture is legitimate and
  stays — that is an address, not the shop's bank details.
- `Only_orders_past_their_own_deadline_are_in_the_batch` was m-1's shape with a 500 ceiling instead of 50,
  and m-1's fix had made it worse by leaving three permanently-2020-dated orders at the head of every later
  batch. Both tests now put their deadlines back in a `finally`, and the sibling asserts an exact set rather
  than containment. **Controlling your own state includes putting it back** — a test that fixes its own
  flake by handing the next one a biased batch has moved the problem, not retired it.

## Measured

| | | W3-02's baseline |
|---|---|---|
| `dotnet build SpencerDenim.sln -c Release --no-incremental` | **0 warnings, 0 errors** | 0 / 0 |
| `npm run build` (production, budgets enforced) | **0 warnings** | 0 |
| `dotnet test tests/SpencerDenim.UnitTests` | **1459 passed**, 0 failed | 1306 (+153) |
| `dotnet test tests/SpencerDenim.IntegrationTests` | **1093 passed**, 0 failed, 0 skipped (13 m 16 s) — on this machine. The tester measured **1089/1** on theirs, failing in `AuditLogEndpointTests`, which is #114's family and provably not this branch's: 8/8 alone, 64/64 beside every W3-03 suite, the file untouched in all three rounds, and its 2026-09-01→09-04 window cannot contain rows this branch stamps today. | 956 |
| `ng test shared / storefront / admin` | 24 / **1295** / **374** passed, 0 failed | 21 / 1206 / 273 |
| `ng lint` | all three projects pass | pass |
| `playwright test --project=chromium` (full suite) | **860 of 860, 0 failed** — but with **3 flaky** (green on retry): `quick-view`, `search`'s `/about` redirect and `storefront-shell`'s drawer focus. *Different tests again*, none in the W3-03 files. The tester measured 857/3 on a clean machine and an earlier run here was clean; across five full runs the only stable facts are that the set changes every time, every one passes alone, and neither W3-03 file has ever been among them. **#115 is open and none of these runs closes it.** | — |
| `playwright test bank-transfer.e2e.ts` | **10 passed**, and 30/30 under `--repeat-each=3 --workers=1` | — |
| `playwright test admin-bank-transfers.e2e.ts` | **14 passed** | — |
| `playwright test checkout.e2e.ts` | **50 passed** (two assertions removed, below) | 52 |
| `dotnet ef migrations has-pending-model-changes` | **no new migration in this branch** | — |

`TEST_SQL_CONNECTION` was LocalDB throughout (doc 06; #74).

### What the new tests are

| Suite | Tests | What they are about |
|---|---|---|
| `BankTransferReviewTests` | 16 | approve / reject / accept-late, every assertion read off the rows; approve-vs-expire and approve-vs-reject races, 8 and 8 rounds; two staff approving at once, 6 × 4; invoice numbers gapless across refusals; a cancelled order accepted late; a payment row from another provider refused |
| `OrderExpiryTests` | 9 | the claim's full guard including the capture lock, not-yet-due, already-paid, twice, the batch read's order and ceiling, two instances racing 6 × 4 |
| `AdminBankTransferEndpointTests` | 27 | permissions (`Orders.View` reads and cannot approve), the queue's shape and order, the three decisions' statuses and 409 codes, #47's malformed bodies in two environments, the bank settings' absent-≠-null, the slip end to end |
| `OpenPaymentIndexTests` | 4 | #103's open question, 8 × 4 contenders, plus the filter text read from `sys.indexes` |
| `BankQueuePlanTests` | 4 | DB28, read off SHOWPLAN |
| `PaymentProviderContractTests` | 10 | doc 03 §3c's contract over every registered provider |
| `CheckoutNamesNoProviderTests` | 3 | PAY-03's claim, as a test |
| `BankDetailRulesTests` | 18 | what the bank fields accept, which is the interesting half |
| `bank-transfers-page.spec.ts` | 13 | the queue screen: rows, time left, decisions, 409 → Accept late, the way back out of `order_not_closed`, named short lines, `aria-disabled`, announcements |
| `settings-page.spec.ts` (bank) | 7 | the W1-12 body assertions on the group where it costs money |
| `order-page.spec.ts` (pay panel) | 8 | the details, the reference, the slip, the not-set state, a paid order |
| `bank-transfer.e2e.ts` | 10 | the pay panel in a real browser: the details, the amount against the summary's own string, the reference, the slip round trip, the details-not-set state, no email claimed, overflow / tap size / reachability / occlusion at all eight widths, axe |
| `admin-bank-transfers.e2e.ts` | 14 | the queue in a real browser: oldest first, time left, approve, reject with and without a note, a 409 turning into Accept late, short lines, focus after a decision, the same eight widths, axe |

### Mutation proofs

Each applied to `BankTransferReviewStore`, the suite run, the file restored and `diff`-ed byte-identical.

| Mutation | Result |
|---|---|
| the stock deduct updates only the timestamp | **4 red** |
| a rejection also issues an invoice number | **3 red** |
| the late-acceptance deduct drops `AND OnHand − Reserved >= q` | **1 red** |
| a refused decision commits instead of rolling back | **1 red** |
| `OrderSnapshot` back to a `record struct` | **1 red** — this is the red-before of defect 1 |
| the approve claim drops `AND Status = 0` | **green — masked** |
| the reject payment statement drops `AND Status = Pending` | **green — masked** |
| the three claims drop `AND PaymentProviderCode` | **green — masked**, found by the tester |
| reject's order claim removed entirely | **green — no red-before**, found by the tester |
| creating no payment row when none is open | **1 red** — the red-before of the rejection-then-reference defect |
| the slip guard's `ProofAssetId` check never matches | **1 red** — the red-before of the slip-deletion defect |
| the payment-row `ProviderCode` filter dropped | **1 red** |
| the provider guard dropped from the snapshot read **and** all three claims | **1 red** |
| the late acceptance's payment statuses back to `[Pending, Expired]` | **1 red** — the red-before of m-2 |
| the expiry batch ordered by newest deadline first | **1 red** — the red-before of m-1's rewrite |
| `canAcceptLate` back to the clock (`secondsUntilExpiry <= 0`) | **4 red** — the red-before of MAJOR 1 |

**The masked ones are reported rather than papered over**, because "my test cannot see this" is more useful
than silence:

- The approve claim's `Status = 0` is **redundant given the other two guards**. `StockState = 0` and the
  settlement `NOT EXISTS` each catch every reachable state the status guard would: `Paid` implies
  `StockState = 1`, `Expired`/`Cancelled` imply `2`, and there is no reachable `(Status ≠ 0, StockState = 0)`
  combination. It stays because doc 08 §4.1 specifies it and because the redundancy depends on invariants
  elsewhere.
- Of reject's pair, **the statement guard is the protection and the order claim is the belt** — the reverse
  of what this file said before review. `AND Status = Pending` on the payment statement is what makes a
  losing rejection a no-op; the claim adds lock ordering and a fresh diagnosis.
- **`AND PaymentProviderCode` in the three claims is belt behind the snapshot read.** A tester removed it
  from all three and the 52 payment tests stayed green; removing it from the read *and* the claims turns
  `A_PayPal_order_cannot_be_approved_through_the_bank_flow` red. So the single-line removal is masked by
  design — the read refuses first — and the claims are there because a guard on the statement that writes is
  worth having even when a read in front of it already refused. What has changed is that the PayPal test now
  asserts **rows, stock and `NumberSeries`** rather than a result type, and a second test covers the shape
  the read cannot catch: a payment row whose `ProviderCode` belongs to another provider on an order whose
  does not.

## Things in the brief, or in the documents, that are wrong

1. **The brief says "use `sp_getapplock` and `Serializable` where §4.2 says to". §4.2 says neither.** Doc 08
   names an application lock in exactly two places — the promo per-customer limit (§3.6) and the
   reference-data seeder — and neither is on this path. §4.1 and §5.5 specify conditional `UPDATE`s and row
   counts, and §5.2 explains at length why that is *stronger* here than an isolation level. An applock on the
   order would add a second, weaker gate beside the one the database already enforces, and `Serializable`
   would turn a lost race from "0 rows, here is why" into a deadlock to retry. **Every transaction in this
   branch is `ReadCommitted`, the same as checkout's**, and the claim is the lock.
2. **The brief says the client "has not given me their bank details, so these must be settings … exactly as
   ADM-22 did for the logo".** Right, and one thing follows that the brief did not say: ADM-22's route guard
   is a *single* permission, so a caller holding only `Settings.Payments` could not reach the settings screen
   at all — not even to read the group that is theirs. `AdminSettingsEndpoints`' own remarks flagged this as
   "the widening its successor owes". `GET /api/admin/settings` now takes any `Settings.*` permission
   (`AnyPermissionRequirement`); every *write* stays on the one permission its group needs.
3. **The brief says "#103's review flagged `IX_Payments_BankQueue`'s literal filter for a second look".** The
   index and the constant were already right; what was missing was a test that the *query* matches it. There
   now is one, and it found that a plan test has to tell "used" from "mentioned".
4. **Doc 08 §3.5's `UX_Payments_OneOpen` note is wrong about a PayPal retry** — see above. The bank half and
   the recovery half are right.
5. **Doc 08 §4.1 wants `InvoiceSellerSnapshotJson` and this branch writes null.** The seller snapshot is the
   store's legal name, address and BR/VAT number, which are the `Store.LegalName`, `Store.Address`,
   `Store.BRNumber` and `Store.VatNumber` settings — and **this build seeds none of them**. Inventing a
   registration number would be worse than leaving the column null, and nothing renders an invoice document
   yet (G-34), so the snapshot has no reader to disappoint. The invoice *number*, which is what doc 02
   PAY-01 and doc 11 P7 name, is issued and gapless. Owed to ADM-22 with the four keys.
6. **Doc 08 §3.3 N10's "5 per hour per IP" cannot be implemented as written in this deployment.** Doc 03 §4
   puts Caddy in front of the API and `X-Forwarded-For` processing is deliberately off (W1-01 round 3), so
   *every* customer shares one address — and five an hour would mean the sixth genuine shopper of the hour
   cannot send their slip at all, on a shop whose only payment method is bank transfer. The per-**order**
   limit is doc 08's figure exactly (an order token is never shared). The per-address limit is five when the
   address looks like a real visitor's and **300** when it does not, with a one-time warning naming W0-15 —
   the same shape and reasoning as `NewsletterSignupThrottle`, which met this first. It tightens the day the
   forwarded-headers work lands.
7. **W3-02 left a test that W3-03 makes wrong**, and that is the system working rather than a problem.
   `order-page.spec.ts`'s "promises no bank details and claims no email has been sent" asserted that the words
   "account number", "upload" and "reference to quote" were **absent**. The first half is now exactly what the
   page must do; the second half is kept as its own test. Replaced, with the reason in the file.
8. **A wrong comment, found and corrected**: `settings-page.spec.ts`'s `toForm` header read "a key the server
   did not send stays null, which is correct" while the test immediately under it was called "omits a key the
   server did not send, rather than sending null for it". Left over from before ADM-22's round-1 fix. This is
   the second instance this week of the defect family the brief names — a wrong comment stops the next reader
   checking.

## What `reserved → sold` does without `inventory.InventoryMovements`

The table does not exist (#108, W1-05/ADM-12), so doc 08 §5.1's `Sale` movement cannot be written on an
approval any more than checkout could write its `Reserve` one. What **is** exact:

- the figures. `OnHand − q, Reserved − q` under the order's claim, row count checked per line, in ascending
  `VariantId`; a count other than 1 throws, which is §5.5's own instruction ("it indicates drift") rather
  than this code's choice;
- doc 08 §5.9's reconciliation, which compares `StockLevels.Reserved` with `Σ OrderLines.Quantity` over
  orders in `StockState = 0` — it reads the order lines, not the ledger, and an approved order leaves that
  set.

What is **lost** is the per-variant timeline. After an approval there is no row anywhere saying "two of
variant 470 moved from reserved to sold at 14:03". The *order's* side is reconstructible — its status
history, its `PaidAtUtc` and its line quantities — so the hole is in the variant's history, not the order's.
`VariantReferences` already records that the table must be `BlocksTheDelete` when ADM-12 adds it; nothing
here creates it.

There is also **no `ops.AdminAlerts` row** on a drift throw, because that table does not exist either. The
exception names the variant and reaches the log through the endpoint's 500 or the job's `Error`, and §5.9's
nightly reconciliation is the other half of the signal.

## What is owed, and by whom

| # | Owed | By |
|---|---|---|
| 1 | **`Orders.Refund` and `sales.Refunds`** — nothing in this branch writes a refund row, so doc 11 §7 G-13 ("money received on an order that was never paid") has no system path. It is the documented answer when a late acceptance fails its stock re-check, and the queue says so in words while offering no button. | W3-04 / ADM-09 |
| 2 | **Doc 08 §3.8 DB2's sensitive-change alert.** Any `Bank.*` change must insert an `ops.AdminAlerts` `SensitiveSettingsChanged` row and email every GrantsAll holder, in the same transaction. Neither table exists. The change **is** audited (actor, time, IP, before and after), so it is not invisible — but no Super Admin is told, and doc 11 P7's last exception is not honoured. | ADM-22 / W3-06 |
| 3 | **`InvoiceSellerSnapshotJson`** and the four `Store.*` legal keys. | ADM-22 |
| 4 | **`ops.EmailOutbox`.** P7 steps 1, 6a and 7 all queue emails (`BankTransferInstructions`, `PaymentConfirmed`, `OrderExpired`). None is sent and nothing claims one is. | W3-06 |
| 5 | **`inventory.InventoryMovements`** — see above. | W1-05 / ADM-12 |
| 6 | **The promo use on expiry and late acceptance.** Doc 08 §4.1/§5.6 decrement and restore `UsageCount`. `marketing.PromoCodes` does not exist, so `Orders.PromoCodeId` is null on every order ever placed and there is no branch that could run. | ADM-13 / W3-05 |
| 7 | **`Orders.BankTransferExpiryHours` as a setting.** Still `Orders__BankTransferExpiryHours` in configuration. One options class deleted, one settings read added. | ADM-22 |
| 8 | **Rate limiting the order link** (G-41), which would cover the reference route too. Nothing limits `GET /api/orders/{token}`. | Architect, then whoever |
| 9 | **The placement throttle**, the other half of #107. Not a documented requirement; an owner decision. | Owner |
| 10 | **`GET /api/admin/media/{publicId}` does not filter by visibility**, so a `Media.View` holder can read a slip's *metadata* (not its bytes — the file route is `Orders.View` and keyed on the order). Pre-existing, found while reading; worth a deliberate decision, and ADM-18's listing must exclude `Visibility = Private` when it lands. | ADM-18 |
| 11 | ~~**Playwright coverage of the two new screens.**~~ Written and run: 24 tests, four defects found. | — |
| 12 | **A way to reach an order that has already expired — filed as #124, and sharper than I first put it.** The queue lists `Pending` payments and expiry closes the payment in the same transaction as the order, so an expired order is **not in the queue at all**: Accept late payment is reachable only when expiry wins *during* a review. The tester added the part that changes its urgency — **pressing Refresh destroys that one route permanently**, because `read()` clears the refusals *and* re-reads, and the row is gone from the queue, so a staff member who gets the `order_expired` refusal and then reloads cannot reach the transaction again from any screen. A one-keystroke trap in front of a money path. And until a view exists, **the 48-hour expiry is a liability rather than a safeguard**: P7 step 8's real case has no entry point, G-13's refund fallback has no system path either, so an order that expires and then pays can only be settled by direct database work. Not a regression (the button was offered and refused before), and the recommendation is not to put the expiry job in front of a live shop without one of the two. | ADM-09 / W3-04, #124 |
| 13 | **An architect's line on the customer's slip read-back.** P7 says the slip is "shown only to staff with `Orders.View`"; this build also serves it back to the holder of the order's token, which I believe is right — a shopper who uploaded a file should be able to see which file the shop has — but it is a scope deviation and needs ratifying in doc 02 or doc 11 rather than living in a code comment (the tester's m-7). | Architect |
| 14 | **`checkout-page.ts`' `<form>` with an outside submit button** (`form=`), which natively submits name, phone, address and city into the query string before hydration, losing the form and leaving personal data in history and access logs. Pre-existing, W3-02's, found only because my own version of the same mistake was reviewed, and now **filed as #123**. The false claim about it is corrected in both the journal and the comment in `order-page.ts`. | W3-02, #123 |
| 15 | No `progress-data.js` change, no `MEMORY.md`, no `TASKS.md` (doc 14's rule). | owner's session |

## What I deliberately did not build

- **PayPal, in any form.** No `PayPalProvider`, no stub, no settings keys. It is W4-01 and built last.
- **Any refund path**, for the reason above.
- **`ops.JobLeases`, `ops.AdminAlerts`, `ops.EmailOutbox`, `inventory.InventoryMovements`** — four tables
  this flow would use and none of them mine.
- **A migration.** Nothing in this branch changes the schema: the `Bank.*` keys are `ops.Settings` rows from
  the idempotent seeder, and `media.MediaAssets` already supports `Visibility = Private`, `MediaType =
  Document` and PDF. `has-pending-model-changes` is clean because the model did not move. Main's latest is
  still `20261006135402_SalesWishlistItems`.
- **A cancel path.** Doc 11 P10 flow A (`Orders.Edit`, customer cancel) is W3-04's; G-36's "don't cancel if
  you've already paid" warning belongs with the button.

## What I could not verify

- ~~**Playwright.**~~ **Closed.** This was written up as the biggest gap in the task and it is now covered:
  24 browser tests over the two screens, both measuring horizontal overflow, `undersized`, `unreachable`,
  occlusion and axe (serious + critical) at all eight project widths. They found four layout and focus
  defects and pointed at a fifth that loses a shopper's typed reference — see the section above. Every call
  site asserts a non-zero `locator.count()` first, because #80 means `undersized()` returns `[]` for
  "nothing matched" too.
- **A second browser engine.** Everything above is Chromium. `--project=chromium` is what the project runs
  and Firefox/WebKit are not installed here, so neither screen has been checked in them.
- **The private volume in a real deployment.** The slip tests use the fixture's temp volumes, which are
  siblings as `MediaStorageRoots` insists. Nothing has checked that Caddy does not mount the private volume,
  because there is no Caddy here (W0-15).
- ~~**The expiry job on a real timer.**~~ **Checked by the tester in round 1, and the defect is absent** —
  recorded here so nobody re-does the work: `ExpireUnpaidOrdersService` owns no `CancellationTokenSource`
  and overrides neither `StopAsync` nor `Dispose`, and all four of the W3-02-shaped cases pass. The
  five-minute loop itself is still only reasoned from `CatalogSeedHostedService`'s recorded lessons, which
  is a smaller gap than the one this entry used to claim.
- **Query plans at production cardinality.** The plan tests run against tens of rows. That is sound for
  *matching* — which is about implication, not cardinality, and the measured plan is a clustered scan of a
  table with `TableCardinality="0"` where a seek would be cheaper if it were legal — but it says nothing
  about speed.

## The full browser run's leftovers, none of them this branch's — and now resolved

Three full runs here: two at 856 and a final one at 860 of 860. The tester then measured **857/3 on a clean
machine** — three *different* tests again, all green alone, none in the W3-03 files, one of them already on
this branch's round-1 list. So the honest reading is not "settled": **this bucket is #115 and it is open.**
What every run does agree on is that the failures are different each time, they pass in isolation, and
neither W3-03 file has ever been among them. A clean run is a sample, not a proof — and an earlier version
of this section read as though one closed the issue.

| Test | First run | Second run | In isolation |
|---|---|---|---|
| `search.e2e.ts` — one Escape closes the suggestions | **failed** | passed | passes |
| `search.e2e.ts` — a submitted search is offered again | passed | flaky | — |
| `wishlist.e2e.ts` — a surviving opener is still what focus returns to | flaky | passed | — |
| `quick-view.e2e.ts` — "View full details" carries the choice | passed | **failed** | passes |
| `admin-catalog.e2e.ts` — reorders with the keyboard alone | passed | flaky | — |
| `checkout.e2e.ts` — no horizontal scroll at 1280 px | passed | flaky | passes, 44/44 twice |

The search one is the most interesting, because its failure is specific: a **category** suggestion
(`JeansWomen`) ranked first where the test expects `Test Product 1`. That reads like an ordering race
between a suggestions read and whatever another worker is doing to the shared stub dataset.
`git diff origin/main` over `catalog-api-stub.mjs` touches nothing to do with suggestions, search or
categories — every line this branch adds there is the bank-details switch and the placed order's payment
block — so it is pre-existing.

**Reported rather than chased.** These are SRCH-01's, WISH-01's, QV-01's, the admin catalogue's and
CHK-01's tests; rewriting another feature's assertion to make a number green is how a real defect gets
hidden. Worth someone's half day, with `--repeat-each` **and `--workers=1`** as the first tool, since the
one thing now known about all six is that they do not reproduce alone.

## Shared-resource notes

`Get-Process` checked for a running `testhost` before each integration run; none was present and nothing was
killed. The leftover API on **5080** and `ng serve` on **4683** were left running, as instructed. Ports 4502,
4515, 4767 and 4432/4433 untouched. No database was dropped: every `SpencerDenim_Test_*` the fixture created
it also dropped, and `SpencerDenim_Dev`, the `SpencerDenim_W113_*` and `SpencerDenim_W103_Tester` were never
opened. No `git stash` was used at any point — the shared stack is shared.

Every test that writes a `Bank.*` setting restores all five to null in a `finally`: the assembly shares one
database, classes run sequentially, and a left-behind account number is a neighbour's fixture.
