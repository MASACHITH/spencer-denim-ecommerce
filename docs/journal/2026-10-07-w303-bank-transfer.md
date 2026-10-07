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

Unreachable today (nothing captures; doc 08 §9 seeds the PayPal row disabled). The sequence and three ways
out are written into `PaymentConfiguration`'s remarks where W4-01 will be standing, with a note **not** to
solve it by catching 2627 — doc 08 §4.2 is explicit that the design never relies on catching a unique
violation on the settlement path, and a swallowed one would discard a capture. The recommended fix is to
cancel the open retry row inside the apply transaction before moving `Capturing → Pending`, which is what
§4.2 step 5 already does for the reverse order.

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

### 2. Reject took no order-row lock, against doc 08 §5.3

My first version argued that a read plus the payment statement's own `Status = Pending` guard was complete,
because every way an order leaves `PendingPayment` closes its open payment. The argument is true and the
**window** is the problem: between the read and the payment `UPDATE`, an approval can commit. Without an
order-row claim the two paths take their locks in opposite orders, and the losing rejection arrives at a row
that is now the settling payment and tries to make it `Failed` — which
`CK_Payments_IsOrderSettlement` then refuses, turning a race that should answer "already approved" into a
constraint violation and a 500.

Doc 08 §5.3 already said what to do: "paths that start from an existing order claim the order row first". A
rejection starts from one. It now claims it with a conditional `UPDATE` that writes only the stamp — the
status and stock state are deliberately untouched (P7 step 6b: "the order stays PendingPayment until its due
time") — so all three decisions take the same lock in the same order and the loser waits and is told what
happened.

`Approving_and_rejecting_at_once_applies_exactly_one_of_them` is the test that made me look, and it is the
one no sequential test could replace.

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

There was no other `<form>` in the storefront — checkout's Place Order is a button with a click handler —
so this was my deviation from the shop's own convention, not the shop's. It is now a `<div>` with
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

## Measured

| | | W3-02's baseline |
|---|---|---|
| `dotnet build SpencerDenim.sln -c Release --no-incremental` | **0 warnings, 0 errors** | 0 / 0 |
| `npm run build` (production, budgets enforced) | **0 warnings** | 0 |
| `dotnet test tests/SpencerDenim.UnitTests` | **1459 passed**, 0 failed | 1306 (+153) |
| `dotnet test tests/SpencerDenim.IntegrationTests` | **1086 passed**, 0 failed, 0 skipped (14 m 31 s) | 956 |
| `ng test shared / storefront / admin` | 24 / **1295** / **372** passed, 0 failed | 21 / 1206 / 273 |
| `ng lint` | all three projects pass | pass |
| `playwright test --project=chromium` (full suite) | 860 tests: **856 passed**, 1 failed, 3 flaky — every one of them in another feature's file and every one passing in isolation; both W3-03 files green (see below) | — |
| `playwright test bank-transfer.e2e.ts` | **10 passed**, and 30/30 under `--repeat-each=3 --workers=1` | — |
| `playwright test admin-bank-transfers.e2e.ts` | **14 passed** | — |
| `playwright test checkout.e2e.ts` | **50 passed** (two assertions removed, below) | 52 |
| `dotnet ef migrations has-pending-model-changes` | **no new migration in this branch** | — |

`TEST_SQL_CONNECTION` was LocalDB throughout (doc 06; #74).

### What the new tests are

| Suite | Tests | What they are about |
|---|---|---|
| `BankTransferReviewTests` | 14 | approve / reject / accept-late, every assertion read off the rows; approve-vs-expire and approve-vs-reject races, 8 and 8 rounds; two staff approving at once, 6 × 4; invoice numbers gapless across refusals |
| `OrderExpiryTests` | 9 | the claim's full guard including the capture lock, not-yet-due, already-paid, twice, the batch read's order and ceiling, two instances racing 6 × 4 |
| `AdminBankTransferEndpointTests` | 25 | permissions (`Orders.View` reads and cannot approve), the queue's shape and order, the three decisions' statuses and 409 codes, #47's malformed bodies in two environments, the bank settings' absent-≠-null, the slip end to end |
| `OpenPaymentIndexTests` | 4 | #103's open question, 8 × 4 contenders, plus the filter text read from `sys.indexes` |
| `BankQueuePlanTests` | 4 | DB28, read off SHOWPLAN |
| `PaymentProviderContractTests` | 10 | doc 03 §3c's contract over every registered provider |
| `CheckoutNamesNoProviderTests` | 3 | PAY-03's claim, as a test |
| `BankDetailRulesTests` | 18 | what the bank fields accept, which is the interesting half |
| `bank-transfers-page.spec.ts` | 12 | the queue screen: rows, time left, decisions, 409 → Accept late, short lines, `aria-disabled`, announcements |
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

**The two masked ones are reported rather than papered over**, because "my test cannot see this" is more
useful than silence:

- The approve claim's `Status = 0` is **redundant given the other two guards**. `StockState = 0` and the
  settlement `NOT EXISTS` each catch every reachable state the status guard would: `Paid` implies
  `StockState = 1`, `Expired`/`Cancelled` imply `2`, and there is no reachable `(Status ≠ 0, StockState = 0)`
  combination. It stays because doc 08 §4.1 specifies it and because the redundancy depends on invariants
  elsewhere.
- The reject statement's own `Status = Pending` is now masked **by the order claim in front of it** (defect
  2's fix). It was load-bearing before that claim existed, and the race test is what established the window
  rather than the statement.

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
| 11 | **Playwright coverage of the two new screens.** Not written: see below. | this task's tester, or a follow-up |
| 12 | No `progress-data.js` change, no `MEMORY.md`, no `TASKS.md` (doc 14's rule). | owner's session |

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
- **The expiry job on a real timer.** `IOrderExpiryStore` is tested directly, including the races; the
  hosted service's five-minute loop and its shutdown behaviour are reasoned from
  `CatalogSeedHostedService`'s recorded lessons and **not** tested. A lifetime test like
  `CatalogSeedHostedServiceLifetimeTests` is owed.
- **Query plans at production cardinality.** The plan tests run against tens of rows. That is sound for
  *matching* — which is about implication, not cardinality, and the measured plan is a clustered scan of a
  table with `TableCardinality="0"` where a seek would be cheaper if it were legal — but it says nothing
  about speed.

## The full browser run's leftovers, none of them this branch's

Two full-suite runs, before and after the fixes above. Both land on **856 of 860 passing**, and the four
that do not are **different tests each time** — which is the signature of load rather than of a defect.
Every one of them passes in isolation, each was re-run that way to check, and both W3-03 files are green in
both runs.

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
