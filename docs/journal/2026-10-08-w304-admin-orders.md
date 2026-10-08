# 2026-10-08 — W3-04 / ADM-09: the admin orders screen

Branch `feature/W3-04-admin-orders`, worktree `worktrees/sd-orders`, cut from `origin/main` at `31f3b27`.
Scope: doc 02 ADM-09 line 114 — "Orders: list/search/filter, detail, status workflow, approve bank
transfer, courier + tracking number, cancel/refund status, print invoice" — and doc 11 P9's fulfilment
process. **Not pushed, not a PR — the tester goes first.**

Continues `2026-10-07-w303-bank-transfer.md`, whose owed table item 12 is issue #124, and which built the
approve/reject/accept-late transaction this task gives screens to.

## What it does, end to end

A staff member opens **Orders**, types the number a customer has just read down the telephone, and gets
that order. They open it and see what was bought with SKU, colour, size and line money; the customer's
name, email address and telephone number; the delivery address or the pickup point; the money broken down;
every payment attempt with its slip and the colleague who reviewed it; and the order's own timeline with
who did what and when.

They press **Start processing**, print the **invoice / packing copy**, pick and pack, choose the courier,
type the tracking number and press **Mark as shipped**. A store-pickup order takes **Ready for
collection** and then **Mark as collected** instead, with no courier and no tracking number anywhere near
it. Seven days after a dispatch nobody has confirmed, a daily job closes it — **never a pickup order**.

And an order that **expired and then paid** is now reachable: searching the number the customer quotes
finds it, the detail offers **Accept late payment**, and a reload cannot destroy the route. That is issue
#124, which is why this task was next.

**Nothing claims an email, a notification or an acknowledgement was sent**, anywhere, because nothing
sends one.

## P9, step by step, and what is actually built

| P9 | Built | Where |
|---|---|---|
| Preconditions: active couriers with tracking templates exist | ⚠️ **the screen to add them, and nothing invented** | `/orders/couriers`, `SaveCourierCommand`, `CourierAdminStore` |
| 1. Orders, filter **Paid** (oldest first) | ✅ | `OrdersListPage`, and the rule is on the server — `GetAdminOrderListQueryHandler.DefaultSortFor` |
| 2. Start processing → Paid → Processing | ✅ | `OrderFulfilmentStore.StartProcessingAsync` |
| 3. Print the invoice / packing copy | ✅ | `OrderInvoicePage`, `GetOrderInvoiceQuery` |
| 4. Select the courier, enter the tracking number | ✅ | the detail's dispatch form |
| 5. Processing → Shipped, `ShippedAtUtc`, **queue `OrderShipped`** | ⚠️ **transition yes, email no** | `MarkShippedAsync`. `ops.EmailOutbox` is W3-06; nothing promises one |
| 6. Customer follows the link / their order page | ❌ **neither route exists** | the shopper's order page shows **one sentence** beside `Payment` — not a timeline, which an earlier draft of this journal and of `IOrderFulfilmentStore`'s remarks both claimed — and **no courier and no tracking number**, because the public order contract has no such field. ACC-02's, and item 5a below |
| 7. Courier delivers | — | outside the system |
| 8. Mark as delivered, or **auto after 7 days** | ✅ | `MarkDeliveredAsync`, `AutoDeliverShippedService` + `AutoDeliverStore` |
| 9. Delivered enables a Verified buyer review | — | P12's, not this task's |
| Pickup variant 2: Ready for collection, no courier | ✅ | `MarkShippedAsync` with no dispatch |
| Pickup variant 4: Mark as collected; **auto-delivery never applies** | ✅ | the exclusion is in the finder **and** the claim |
| Business rule: one step at a time, **never backwards** | ✅ | `IOrderFulfilmentStore` has no member that could express it |
| Business rule: only the courier, the tracking number and the internal note are editable | ✅ | `UpdateDispatchAsync`, `SetInternalNoteAsync`, and nothing else |
| Business rule: one dispatch per order, no split shipment | ✅ | one courier column, one tracking column, said on the screen |
| Exception: wrong tracking number → correct it, **telephone the customer** | ✅ | the correction form says so in words |
| Exception: paid order cannot be cancelled, it is refunded | ⚠️ **neither is built** | see "What is owed" |

## Decisions, and why

### Doc 11 P9 step 1's sort lives on the server

"Opens **Orders**, filter **Paid** (oldest first)" is a business rule, not a screen preference: the oldest
paid order is the one a customer has been waiting longest for. So `GetAdminOrderListQueryHandler` chooses
`Oldest` when the status filter is `Paid` and the caller expressed no preference, and the answer **echoes
the sort it used** so the screen labels the list from the answer rather than from its own assumption. A
screen that held the rule would mean every future client — a report, a second view — had to remember it,
and one of them would not.

### One search box, classified on the server

Doc 11 §6.6 is a whole section about a customer telephoning: what they have is the order number, their
email address, or their telephone number. Which of those they are about to say is not a question anybody
can answer, so `AdminOrderSearch.Classify` decides and the reader writes **exactly one** predicate. A
single "match six columns" `WHERE` would be a guaranteed table scan with six `LIKE '%…%'` in it, and would
also match a name against an order number — a customer called "2026" would return half the year.

Which index each classification can use is written on the reader:

- a document-number **prefix** seeks `UX_Orders_OrderNumber` / `UX_Orders_InvoiceNumber`;
- the **digits-only** form ("123" → `SD-2026-000123`) adds an `EndsWith`, which cannot seek —
  `IX_Orders_Placed` includes `OrderNumber`, so it is a scan of that index rather than of the table. A
  deliberate trade: failing to find the order the customer is holding is the outcome this screen exists to
  prevent;
- an email **prefix** seeks `IX_Orders_EmailNormalized`; a telephone **suffix** scans `IX_Orders_Phone`.

A `%` typed into the box finds nothing rather than everything, and **that is EF's doing, not this
code's** — which an earlier version of this paragraph had backwards. It said the Application layer escaped
the term because "EF does not escape the parameter". EF 10.0.12 does, at run time, with an explicit
`ESCAPE` clause (measured with `ToQueryString()`: `LIKE @text_startswith ESCAPE '\'`), so the two helpers
that did the escaping were dead code justified by a falsehood — and wiring them up as the comment implied
would have **double-escaped** every term. They are gone, with the two unit tests over seven cases that
asserted a method production never called. The guarantee is pinned by
`AdminOrderEndpointTests.A_wildcard_in_the_search_box_is_not_a_wildcard`, on the wire, which is the only
place it can be seen.

**The lesson is about the sweep, not the escaping.** My end-of-task grep checked every *schema* claim I had
made against the configuration and caught five. It did not check *framework* claims, and this is the one it
missed.

**The name match is case-insensitive because the collation is, and the first version forced it with
`UPPER(FirstName + ' ' + LastName)`.** That is strictly worse: non-sargable against any future index on
the name, one more expression per row, and it would not even protect against a column-level collation
change, because the *term* would still be compared under the column's collation. Every database this
project creates is `Latin1_General_100_CI_AS_SC` and `TestDatabaseMigrator` asserts it; that assertion is
what catches a change here.

### The status wording is rendered on the server

Doc 11 P9's pickup variant says `Shipped` "is shown to staff and customer as 'Ready for collection'", and
`Delivered` on an order the customer walked in and took is the wrong verb. Two screens show an order's
state, so two copies of that conditional would be two chances for a pickup order to be described as
shipped on one of them. `OrderStatusLabels.For(status, isPickup)` is the one function, and the list and
the detail both read what it produced.

The list's **tabs** deliberately do *not* use it: a tab filters orders of both kinds, so one of the two
labels would always be wrong. The tab says `Shipped`; each row says what that order actually is.

### Transitions: the claim is the lock, and there is nothing else

Every method on `IOrderFulfilmentStore` starts with one conditional `UPDATE` on `sales.Orders` whose
`WHERE` carries the status that transition is allowed from, and the row count is the concurrency control
(doc 08 §4.1, §5.2). **No `sp_getapplock` and no `Serializable`** — doc 08 §4.2 names neither on this path,
and §4.1's own note says the transitions deliberately do not rely on the admin ETag either: a stale admin
edit is rejected by a token, a lost race between two transitions is rejected by a row count, and those are
different guarantees. Every transaction is `ReadCommitted`.

**The dispatch is in the same statement as the status.** A second `UPDATE` for the courier and the
tracking number would leave an instant at which an order is `Shipped` with no tracking number on it —
visible to this screen and to the shopper's own order page, which is exactly where a customer would be
told to follow a parcel that has no number. The integration test asserts the winner's courier, its
snapshotted name and its number all come from the *same* writer.

**`DiagnoseAsync` re-reads the row after a 0-row claim**, which is `BankTransferReviewStore`'s lesson and
checkout's `RefuseUnlessAlreadyPlacedAsync`'s before that: the claim's 0 rows says only "not in the state I
needed", and a staff member told that learns nothing. The refusal names where the order actually is, and
the message is phrased from there — "somebody has already started processing this order", not "the order
was not Paid".

### Orders never move backwards, and the interface cannot express it

There is no `MarkProcessingAgain`, no "undo shipped", no status argument. Doc 08 §4.1 and doc 11 P9 both
say a mistake is corrected with an internal note and a history note, so the only writers of a correction
are `UpdateDispatchAsync` and `SetInternalNoteAsync` — both of which write a history row whose from and to
statuses are **equal**, the convention `OrderStatusHistoryEntry` documents for "an event on this order
that did not move it".

**A pickup order's dispatch cannot be corrected either, and that was the easy half to miss.** A pickup
order genuinely *is* in `Shipped`, so the correction's own status guard would be satisfied and a courier
would be written on to an order nobody is couriering — and the detail would then draw a tracking link for a
parcel sitting on a shelf. Refused in the use case and again in the store, with its own test.

### Issue #124: the route to an expired order

The bank queue lists `Pending` payments and expiry closes the payment in the same transaction as the
order, so an expired order is **not in that queue at all**; and the tester's addition was the part that
mattered — pressing Refresh after an `order_expired` refusal destroyed the only route to the transaction
permanently.

The fix is not a new transaction. `AcceptLateAsync` is built, tested and verified; what was missing was a
screen that could reach it. The order detail is reached by **searching the order number the customer
quotes** — which is exactly what a staff member has when somebody rings about a transfer that arrived late
— and `AdminOrderActions.CanAcceptLatePayment` is true when the order is `Expired` or `Cancelled` **and**
still holds a bank payment in `Expired` or `Pending`. That second condition matters: an order whose
transfer was never submitted has no money to accept, and offering the button would send a staff member
into a refusal.

The screen posts to the existing `/api/admin/orders/bank-transfers/{token}/accept-late`, and renders the
short lines W3-03 already returns — product, colour, size and SKU — when the stock re-check refuses. There
is a browser test that opens such an order, **reloads**, and finds the action still there.

### Couriers: the client's information, and nothing invented

`sales.Couriers` is seeded empty on purpose, and **no courier name and no tracking URL appears anywhere in
this branch** — not in a seeder, not as a default, and **not as a form placeholder**, because a placeholder
renders. The only example shown is `{trackingNumber}`: a shape, with no scheme, no host and no path, which
nobody could mistake for a carrier. There is a unit test asserting the example contains no `http`, no `.`
and no `/`, and a browser test asserting no dot-domain appears on the rendered page at all.

The consequence is that a new installation **cannot mark a delivery order as shipped**, and that is
correct. What the shop is owed is the sentence, so `AdminOrderActions.BlockedReason` says "add one under
Settings → Couriers, with the tracking address its website uses" and the dispatch form is **absent**
rather than present and unusable. The stub computes that condition itself, because a stub that always sent
null would make the assertion unreachable.

**There is no delete.** An order keeps the courier's *name* as a snapshot, so deleting the row would not
corrupt a past dispatch note — but the tracking *link* is rendered from the courier's current template
(doc 08 §3.5), so deleting one turns every live parcel's link into plain text with no explanation.
Deactivating removes it from the picker and leaves the links working.

### The invoice prints nothing it was not given

Four `Store.*` keys were added to the catalogue — `LegalName`, `Address`, `BRNumber`, `VatNumber` — all
seeded **null**. An unset key prints **nothing at all**: no label, no blank line, no placeholder. This
project has twice shipped invented client data as a Major finding, and an invoice is a legal document, so
an invented registered address on one is worse than a gap somebody can see and ask about. The *admin*
screen warns before printing (`seller.isComplete`), which is the one place saying so helps; the document
itself still renders, because a staff member may want a packing copy today and the legal details next week.

**`Store.LegalName` is not the shop's name under another name.** The shop a customer bought from is
`StoreBrand.Name` (docs/02 §I2) and is a build-time constant; the party that issues an invoice is the
registered company that commissioned it. The printout carries both, in different places, and the
convention test refused the first draft of this journal's own wording for writing the shop's name out —
which is the test working.

`Orders.AutoDeliverAfterDays` was added too, seeded **7**, which is doc 08 §3.8's own default. It is the
**only** key in the catalogue with a seed value, and `SettingDefinition.SeedValue` says why in full: a
figure one of this project's documents states may be seeded; a fact only the client has may not. There is a
unit test over the whole catalogue asserting exactly one key carries one.

### The auto-deliver job, and what it refuses to do

`AutoDeliverShippedService` runs daily (doc 08 §4.1's own word), batches 50, and never faults the host. The
**pickup exclusion is in both statements** — the candidate query and the claim — and that is not belt and
braces: the candidate list is read outside the transaction, so a guard that lived only in the finder is one
a concurrent change could walk past, and a guard that lived only in the claim would still have the job
logging "9 candidates" every night about orders it can never move. Removing either one turns
`A_store_pickup_order_is_never_auto_delivered` red on its own; both mutations were run.

**An unusable setting skips the pass rather than assuming 7.** The key is seeded, so a null means somebody
deleted the row or put something that is not a number in it — and a job that silently fell back to a
hard-coded figure would be moving money's worth of orders to Delivered on a number no screen could show.
`0` is refused for its own reason: "mark it delivered the moment it ships" is exactly the false "Verified
buyer" badge G-18 is about.

### `Store` and `Orders` are one save unit

`SettingCatalog.SaveUnits` pairs them, so the logo form and the invoice-and-fulfilment form share a
concurrency token. That is deliberate: `KeysInSaveUnitFor` guarantees the token a form is handed is the one
its save compares, which is the 409-on-every-save defect that list was created to end. The cost is that
saving one form makes the other's token stale until the screen re-reads, and the screen re-reads after
every save.

## Defects this task's own tests found

| # | What | Where |
|---|---|---|
| 1 | **A 403 on the order list blew the screen up with a `TypeError`** instead of showing the refusal. `isEmpty()` answers `-1 === 0`, which is false, so the list branch rendered the page's rows over a null. The null branch is now explicit. | `orders-list-page.spec.ts` |
| 2 | **The two new routes' signal `input.required` was never populated**, because `withComponentInputBinding()` is not in this application's router configuration. The three existing form pages already read `route.snapshot.paramMap`; the new ones now do too, rather than changing every route's behaviour for one screen. | the browser suite |
| 3 | **The couriers screen's active checkbox was 20 px**, which GLB-09's floor refuses — it was using the shared `.adm-check` rule. **The shared rule is still 20 px** (`styles.css:94`); the 24 px lives in `couriers-page.ts` as `.couriers-check`, local to this screen. An earlier version of this row said the shared rule "is now a 24 px box", which it is not — the component's own comment was right and the journal was wrong, which is the fourth time in this task a comment outran what I had actually changed. The other two screens with a checkbox are fine by construction, because they wrap the input in a 44 px label. Whether the shared rule should move to 24 px is a question for whoever owns `styles.css`; changing it would touch every admin screen's checkbox and is not this task's to decide. | the breakpoint sweep |
| 4 | **The no-promised-email sweep read the customer's "Email" field label as a promise.** It matches promise-shaped phrases now, not the bare word. The field is a staff member's way of telephoning somebody and refusing the word would be refusing the panel. | the browser suite |
| 5 | Four column facts in my own first draft were wrong against the schema: `Couriers.TrackingUrlTemplate` is `nvarchar(500)` not 300, `Couriers.Phone` is `varchar(20)` not `nvarchar(30)`, the check constraint is `CK_Couriers_TrackingUrlTemplate` not `CK_Couriers_TrackingTemplate`, and the delivery snapshot's column is `DeliveryMethodCode` with **no** `Delivery_` prefix. Three of the four were in comments, which is the worse half: a wrong comment stops the next reader checking. Found by grepping my own new files for every column length, index and constraint name I had claimed and checking each against the configuration — worth doing again at the end of any task that quotes the schema. | reading the configuration |
| 6 | `varchar` columns **substitute `?` for a non-ASCII character rather than erroring**, so a pasted tracking number with a non-breaking space in it would be stored as a number that 404s at the courier with nothing saying so. Both `TrackingNumber` and `Couriers.Phone` now refuse one. | reading the configuration |
| 7 | **The dispatch was on the contract and on no screen.** The courier name and the tracking link came back from the API and the detail rendered neither — the list showed them, the detail had only the correction form. A field nothing reads is a field nobody will notice breaking. | `order-detail-page.spec.ts` |
| 8 | **The courier select came up unselected on every order that already had one.** Angular applies an element's own property bindings before refreshing its children, so the select's value was written while the option list was still empty — and a select whose value names no option resets to the empty one. The selection is now on each `<option>`. | `order-detail-page.spec.ts` |
| 9 | **A redundant expression with a comment claiming it did something.** `ReadDispatchAsync` wrote `template is null && order.CourierId is null ? null : order.CourierId`, which is `order.CourierId`, and said it handled a deleted courier — which `FK_Orders_Couriers_CourierId` (`NoAction`) makes impossible. | reading my own diff |
| 10 | **The courier was selected by matching the order's snapshotted name against the live list**, which came up empty the moment somebody renamed a carrier — on an order that plainly had a courier, inviting a staff member to pick whichever looked right. The dispatch now carries the courier's id. | thinking about a rename |

## A test I deleted rather than let pass vacuously

The order list's live region has the ADM-22 re-fire guard like every other screen here, and the test I
wrote for it **could not fail**: every action on that screen announces an in-progress sentence and then a
result sentence, so consecutive announcements always differ and the guard is never entered. That is the
same resolution ADM-22 itself reached ("gave the save two genuinely different sentences").

So the test moved to the couriers screen, where pressing Save twice with an empty name produces the same
sentence both times, and the component now **says** that the path is unreachable on the list rather than
claiming coverage it has not got.

The couriers version is watched with a `MutationObserver` rather than sampled, because Angular flushes
`afterNextRender` inside the same change-detection pass — so reading the DOM afterwards always finds the
sentence back, and a before/after comparison would pass against a component that never cleared the region.
Without the clear-and-restore there is **no DOM mutation at all**, which is precisely the silence. Removing
the guard turns it red.

## Concurrency, measured

| Race | Outcome |
|---|---|
| Two staff pressing **Start processing** | Exactly one applied, one refused with the current status; **one** history row |
| Two staff pressing **Mark as shipped** with different couriers | Exactly one applied; the courier, its snapshotted name and the tracking number all from that one writer; **one** history row |
| A staff **Mark as delivered** against the **auto-deliver job** | Exactly one (`^` asserted, not "at least one"); one history row. Which one is deliberately not asserted — either is correct |
| A courier **deactivated** between the form's read and the press | Refused with `courier_unavailable`, nothing written |

## Two latent races in other tasks' browser tests

The whole Playwright suite came back with **exactly one failure, in a different test each run**:

| Run | `styles.css` `aria-disabled` hunk | Failure |
|---|---|---|
| 1 | on | `admin-products.e2e.ts` → "moves a colour with the buttons and keeps focus on the one pressed" |
| 2 | on | the same test |
| 3 | off | `admin-catalog.e2e.ts` → "reorders with the keyboard alone, keeping focus on the row that moved" |

A different reorder test, in a different file, of the same shape — which rules out this branch's one CSS
change (the products file also passed **3/3** on its own with the hunk on) and names the real mechanism:

> A reorder is a signal write, so Angular schedules the re-render as a **task**. Both tests then read the
> DOM with `expect(await …)`, which is a **one-shot** assertion with no retry. Under the whole suite's load
> — seventeen web servers competing with the browsers — the read happens before the render. And it reads
> exactly like a product defect: the live region said the colour had moved while the inputs still showed
> the old order.

W3-04's 29 new tests are what pushed it over; the race was there before.

**The products one is fixed** (`await expect.poll(names).toEqual(…)`), which is a two-line change that
makes the assertion wait for the thing it is asserting. It has since passed 46/46 on its own and 118/118
twice across the three admin files.

**The catalog one is measured, diagnosed and deliberately left alone.** The same change there turned the
failure into a 30-second timeout on the test's final `waitForResponse`: polling after `Alt+ArrowDown`
changes the timing of the `Alt+ArrowUp` → `Enter` → Save sequence that follows, and whether `Save order` is
still offered at that point depends on whether the staged order has returned to the server's. Reverting it
put the file back to 118/118. That test needs its owner, and a one-line "fix" from a neighbouring task
would be exactly the sort of change that hides something.

## Test numbers

Run on this machine, `TEST_SQL_CONNECTION` pointed at `(localdb)\MSSQLLocalDB`.

| Suite | Before (main) | After round 1 | **After round 2** | New |
|---|---|---|---|---|
| xUnit unit | 1,726 / 0 | 1,808 / 0 | **1,812 / 0** | 86 (and 7 deleted: see item 7) |
| xUnit integration | 1,124 | 1,192 / 0 | **1,216 / 0** | 92 |
| Vitest — shared | 24 | 24 | 24 | — |
| Vitest — storefront | 1,404 | 1,404 | **1,407 / 0** | 3 |
| Vitest — admin | 407 | 444 / 0 | **459 / 0** | 52 |
| Playwright (chromium) | 891 / 0 | 920 / 0 | **928 / 0** | 37 |

**The whole-assembly integration run looked hung at 16 minutes with 3 s of CPU and no SQL session, and was
not**: it finished green at 21 m 8 s. Worth knowing before somebody kills one. (An attempt to kill it was
refused by the permission system, which turned out to be the right answer.) Round 2's two runs were
**16 m 0 s** and **15 m 54 s**, both 1,216 / 0, and both showed the same profile: the two `dotnet`
processes sitting on about 1.5 s of CPU while `sqlservr` does the work. The check that settles it is
`MAX(last_request_end_time)` in `sys.dm_exec_sessions` against the server clock, not CPU time.

**Round 2's Playwright run found two defects in the four tests it was added to run** — the exhaustive
heading list, which exists to catch exactly the new panel it caught, and my own re-use of `openSettings` to
check that a save had persisted, which reseeds the stub's state cookie and so read every field back blank.
The second is the one worth remembering: the assertion was right and the fixture was wrong, and the easy
"fix" — asserting the boxes instead of re-reading — would have deleted the only part of the test that
proved anything.

**The three Vitest projects are listed separately because a single "1,404 / 407 / 24" is ambiguous and I
got it wrong first.** Measured one project at a time on this machine: `shared` is **24**, `storefront` is
**1,404** and `admin` is **407** before this task — so the brief's three figures are all correct, in the
order *storefront / admin / shared*, which is neither alphabetical nor the order `npm run test:ci` runs
them in (`shared`, `storefront`, `admin`). Worth writing down per project from now on.

Ports: every one of the **seventeen** `E2E_*_PORT` variables was overridden — round 1 into **4820–4836**, round 2 into **4860–4876** — the whole band scanned free first each time. Overriding is not optional here: two of the config's defaults are **4432/4433**, which this machine's protected list covers.  `4502` (`charon-svc`) and
`4767` (`PanGPS`) were identified by PID and left alone; nothing was killed.

## Round 2: the tester's verdict, and what it changed

**FAIL, two blockers, four decisions and three "also fix".** The core held — every illegal transition
refused twice from every status, the row-count control verified *sufficient* rather than asserted, the
pickup exclusion really in both auto-deliver statements, #124 surviving a reload, nothing invented reaching
either bundle (the tester rebuilt and grepped them). The tester also extended the non-ASCII refusal to all
eight of NBSP, ZWSP, BOM, soft hyphen, tab, word joiner, LRM and ideographic space.

### Blocker 1 — I made the integration suite flaky and reported a number that was not reproducible

`SettingsSchemaAndSeedTests.The_seeder_creates_every_catalogued_key_with_no_invented_value` asserted a
**global** property of the shared, deliberately-mutated `ops.Settings` table — so it failed about one run in
three.

Measured, before and after, with the tester's four-class filter: **2 failures in 6 batches → 0 in 6.**

### The fix was right. My diagnosis was wrong, and it reached four files before review caught it

I wrote that the classes **interleave**, and called it measured. **They do not.** The reviewer ran four
timed probe classes — two taking `SqlServerDatabaseFixture`, one without it to rule the fixture out — each
occupying three seconds, and logged **zero overlap**: strictly one after another, and no `xunit.runner.json`
anywhere to override the assembly attribute. `[assembly: CollectionBehavior(DisableTestParallelization =
true)]` works on xunit.v3 3.2.2 in this assembly, and **the comment I replaced was correct.**

What actually happened was a **permanent leak, not a window.** Round-1 `RestoreAsync` set *every* catalogued
key to NULL, including `Orders.AutoDeliverAfterDays`, whose `SeedValue` is `"7"` — so the NULL survived for
the rest of the run. Measured four runs each way:

| `RestoreAsync` | resting value of `Orders.AutoDeliverAfterDays`, four runs |
|---|---|
| round-1 (null everything) | `7`, `7`, `7`, **`<NULL>`** |
| round-2 (null, then re-apply `SeedValue`) | `7`, `7`, `7` |

The non-determinism was **xUnit varying class order**, which is permitted and expected: the assertion fails
if and only if it is ordered *after* `AdminSettingsEndpointTests`. That is precisely the one-run-in-three
rate, and precisely why it passed twenty times alone.

Three things follow, and I had all three the wrong way round:

- **The original failure message was correct.** "A test that left one behind is a neighbour's fixture"
  described the real mechanism. My round-2 remark called it misleading and drew the lesson that *a message
  must describe the weakest explanation for the failure* — a good lesson, attached to the wrong example. The
  lesson that actually applies here is the opposite one: **I trusted my own reconstruction over a message
  written by somebody who had seen the failure.**
- **The assertion could not tell its two causes apart.** It reported the value with `holds ""`, and
  `$"{null}"` interpolates as the empty string — so a NULL row and an empty row read identically, and a leak
  that wrote NULL presented as the Theory's `("")` case. That is what sent me looking for a race.
- **A false "measured" claim is worse than an unexamined one**, because it survives review by borrowing the
  authority of a measurement. It reached `AssemblyInfo.cs`, both new test remarks and this journal, and it
  made the rewritten `SettingsSchemaAndSeedTests` safe for the *opposite* of its stated reason: its global
  metadata assertion is safe **because** classes are serialised. All four are corrected.

The fix splits the test: the **metadata** loop stays global (nothing outside that class touches metadata),
and the **seed value** became a Theory that deletes one key and re-seeds it **inside a rolled-back
transaction** — so no other connection ever sees the row missing. A plain delete-and-restore would have
traded a wide race for a narrow one, and `AutoDeliverShippedTests` is a real reader of one of those keys.

**And mutation showed my replacement was tautological in the dangerous direction.** Giving
`Store.LegalName` a seed value makes the integration test pass — it faithfully asserts the invented
address. What that test proves is that the seeder *honours* the catalogue; the guard for the catalogue
being right is the unit test `Nothing_that_holds_a_client_fact_is_seeded_with_a_value`. Both halves are now
mutation-proved separately and each test's remarks say which half it owns and names the other.

### Blocker 2 — defect class #1 was not swept

`enquiries-page.ts` had the identical shape the orders list had: `isEmpty()` answers `-1 === 0` over a
null, so a refused first read rendered the list branch over a null and threw
`TypeError: Cannot read properties of null (reading 'items')` instead of showing the server's sentence.
Pre-existing from GLB-10, with **no failed-read test on that screen at all**.

Fixed, with two specs — a refused first read (which draws neither the list nor the empty state, because
"No enquiries yet" is a different and false thing to tell somebody who was just refused) and a later read
that fails, which keeps the rows on screen. Mutation reproduces the tester's exact `TypeError`.

### The four decisions

| # | What | What I did |
|---|---|---|
| 3 | `money()` prefixed `Rs.` unconditionally while every contract supplies a `currency` | Every call site passes the currency it has, including the bank queue, which was carrying `row.currency` and ignoring it. **And the sharper form:** `Currency` is LKR by check constraint, so the field that genuinely differs is `ProviderAmount`/`ProviderCurrency` — the USD PayPal was asked for, which doc 11 P10 step 6 has staff read out, and which reached no screen. Both on the contract now and rendered **gated on presence**. |
| 4 | The storefront printed `status.toLowerCase()`, so a pickup customer read "This order is shipped." | `OrderConfirmationDto` carries `statusLabel`, rendered by the same `OrderStatusLabels.For` the admin screens use. Three storefront specs, two integration tests pinning it on a real pickup order through the real reader (#126's pairing). **And the claim is corrected in all four places it had spread to:** the shopper's page shows one sentence beside `Payment`, not a timeline. |
| 5 | The five new settings keys could be set from **no screen at all** — and "readable on the settings screen" was wrong, because that screen renders named forms | One command handler, one endpoint, one form, built on `UpdateBankDetailsCommandHandler` down to the order of its checks. Six specs including the absent-vs-null guarantee over a fixture holding real data. Found on the way: the invoice address collided with `Contact.Address` on its DOM id and its error field, so the request field is `registeredAddress`. |
| 6 | `cancelReason` and the refund's `completedAtUtc` reached no screen | Both render. `completedAtUtc` is the date §6.6 step 2 has staff read out and is **not** `createdAtUtc` — a `Pending` refund has been recorded and the money has not moved. `cancelReason` is null today, which is exactly why it goes in now rather than the day W3-05 populates it. |

### Also fixed

- **7. `LikeEscaped()` / `SequenceSuffixEscaped()` were dead code justified by a falsehood.** Nothing called
  either, and three comments plus this journal asserted that EF does *not* escape a `LIKE` parameter. It
  does, at run time, with an explicit `ESCAPE` clause — so **wiring them up as the comments implied would
  have double-escaped every term**. Deleted, with the two unit tests over seven cases that asserted a method
  production never called. The guarantee stays where it can be seen: on the wire.
- **8.** The journal said the shared `.adm-check` rule "is now a 24 px box". It is still 20 px; the 24 px is
  local to `couriers-page.ts`. Corrected.
- **9.** The oracle disclosure now names both facts a refusal leaks to an `Orders.Edit` holder without
  `Orders.View`: the status, and — one step earlier, before any state is read — the **delivery method**,
  because `Validate` reads `IsPickup` to decide what a request means.

### The nit that mattered most

**Accept late payment was sending the internal-note textarea's text as its audited reason.** That value
reaches `Payments.ApprovalNote` and the order's history, and is what staff read back to a customer asking
why their money was taken a week late — so a note somebody wrote to themselves about a telephone call
silently became a statement about money. It has its own box now, labelled, with the screen saying the
internal note is separate and is not sent. Two mutation-proved specs.

The others: the search finds `SD 2026 000123` and `2026-000123` (separators normalised; a year group tells
a document number from a telephone number), with the remaining ambiguity — a bare `2026000123`, which is
also the shape of a telephone number — asserted as a deliberate loss rather than left to be discovered.
Two ASCII messages stopped claiming a narrower rule than the check applies. And the detail's breakpoint
sweep now measures the transition buttons **before** pressing one: it clicked Start processing first, so
`start-processing`, `ready-for-collection`, `mark-delivered`, `accept-late` and `refusal-reload` were never
measured at 320 px — a sweep with the audit done and the subject missing.

### The second member of blocker 1's family, which the review did not name and I found looking for it

The blocker was one test asserting a settings value nothing in it had written. Sweeping for the *shape*
rather than the instance turned up another of mine: `AutoDeliverShippedTests.The_window_is_read_from_the_
settings_row` opened by asserting that an unwritten read of `Orders.AutoDeliverAfterDays` answers the seeded
7 — and `AdminSettingsEndpointTests.RestoreAsync` was nulling **every** catalogued key in its `finally`,
33 times per run, including this one, whose seeded value is not null. Because classes are serialised the
NULL did not race this test; it **persisted**, so the line failed in whichever runs were ordered after that
class.

It now writes two different values and asserts both. Two rather than one on purpose: a reader that returned
a constant would pass a single-value test whenever that constant was the value written, and mutation
confirms it — pinning the reader to a hard-coded 14 turns the two-value test red.

The same sweep found two more things worth recording:

- **`RestoreAsync` put every key back to null**, which was correct while every key was seeded empty. W3-04
  added the first key the seeder gives a *value*, so the restore was leaving the database in a state the
  seeder never produces, and handing the auto-deliver job's own tests a window of null. It now restores
  `SettingDefinition.SeedValue`.
- **The sequential-execution falsehood is in thirteen files**, not one. Two of them are in
  `AdminSettingsEndpointTests` — the file whose restore semantics blocker 1 turned on — and those are
  corrected — to say that classes **are** serialised, that the attribute works, and that the round-1 restore
  was therefore not a race but a value every later class read. The rule that holds is narrower than the one
  I wrote: a test restores to the **catalogue's** resting value rather than to a guessed one, and an
  assertion over a shared table should still own what it asserts so that it stays honest if parallelism is
  ever turned on. **The other eleven are other tasks' files** and I have deliberately not touched
  them; the list is in "What is owed, and by whom", because each needs a judgement about whether that
  particular fixture is actually safe, and a sweeping comment edit across catalog, content and sales tests
  from this branch would be a change nobody reviewed for the thing it claims.

### The settings form had no server-side test at all, which is exactly #126's pairing

Decision 5 shipped with six Vitest specs, and issue #126 says in as many words that Playwright never starts
this API — so what existed proved only what the screen *sends*. Ten tests now cover the endpoint against
real SQL Server: the round trip read back from the database rather than the payload (W1-12's blocker was a
field that never reached the request, which a round trip through the same read model cannot see), the
absent-versus-null guarantee over a fixture **filled first** (against empty rows that bug is invisible), the
window's range and its own bounds on the wire, the over-long refusals all named in one response, a stale
token as a 409 that writes nothing including the fields not in conflict, one audit row per changed key and
none for a key re-sent unchanged, and the form and `IOrderSettingsReader` agreeing *through the database*.

That last one is the test I would keep if I could keep one: the write path and the reader are different code
with different key lists, and the only failure that reaches a client — a form that saves and an invoice that
still prints nothing — is invisible to either half alone.

**The route was also missing from `Routes()`**, the helper the file's two authorisation tests enumerate. So
neither "refuses an anonymous caller" nor "refuses a signed-in caller without `Settings.Edit`" covered the
newest write route in the file. Added, and mutation-proved by deleting the endpoint's `RequirePermission`:
both tests go red, which is what makes the new line load-bearing rather than decorative.

### Decision 6's other half: which unrendered fields are dead, and which are owed

The review asked which of finding 6's remaining unrendered fields I judge genuinely dead rather than owed.
Eight are still unrendered after `cancelReason` and the refund's `completedAtUtc` went in.

**Genuinely dead on this screen:**

| Field | Why |
|---|---|
| `totals.itemsOriginalTotal`, `totals.itemsListTotal` | Derivable from three figures the panel already shows (`itemsSubtotal`, `markdownDiscountTotal`, `campaignDiscountTotal`). A fourth and fifth number that must add up is a number that can be made to disagree, and the reduction a staff member is asked about is already on screen as an amount. |
| `delivery.zone` | An internal pricing band. The screen shows the district, which is what staff and the courier use, and the delivery fee the zone explains. |
| `payment.slipContentType` | A MIME type. The screen links the slip and the browser decides how to open it; printing `image/jpeg` beside it is noise. |
| `lines[].productCode` | The SKU already identifies the variant uniquely and is shown with name, colour and size. The product code is what a *packing slip* wants, and a packing slip is not in ADM-09's deliverable list. |

**Owed, and I can name the task:**

| Field | To whom, and why |
|---|---|
| `lines[].promoDiscountAllocated` | **W3-05's partial refund.** It is the per-line share of a promo code's discount, and a line refunded at its `lineSubtotal` rather than its `lineTotal` refunds money the shop never took. This is the one on the list I would call a real gap rather than a tidy-up. |
| `lines[].unitOriginalPrice`, `lines[].unitListPrice` | The answer to a price dispute — "the site said Rs. X that day" — cheapest as a struck-through figure beside the unit price. It belongs with whatever next renders line-level price detail, and it is not urgent while the two discount totals are shown. |

### Three corrections the tester made to its own brief, recorded because they matter

1. **ADM-12 is ✅ in scope** at doc 02 line 117 — a different *task*, not out of scope. The check meant (no
   scope creep) passes.
2. 20 commits, not 21.
3. **The `admin-catalog` timeout is pre-existing (#88), not caused by my poll.** Measured at
   `--workers=1 --repeat-each=6`: unpolled 4 failures in 7 batches, polled 3 in 7 — ~10% either way.
   Leaving a neighbour's test alone was the right call; **my stated reason for leaving it was wrong**, and
   that is the more useful half.

## Round 3: what the review corrected, recorded because I got it wrong

The verdict was **PASS after one comment-only correction**, and the correction was my own diagnosis rather
than any code. It is written up in full under blocker 1 above. Four further corrections to what I reported,
each verified here rather than taken:

1. **The branch was 1 behind `origin/main`, not 0.** `8467023` landed while I was measuring (board only:
   `TASKS.md` and `client-progress/`, conflict-free), so both integration runs were measured without it.
2. **`928 / 0` was the *second* Playwright run; the first was `926 / 2`.** The two failures were
   `product-listing.e2e.ts:696` and `search.e2e.ts:647`, both other tasks' storefront tests, both green
   133/133 in isolation. **And `search.e2e.ts:661` carries the exact one-shot shape I diagnosed for the
   reorder family** — `expect(await box.evaluate(element => element === document.activeElement)).toBe(true)`
   read immediately after `box.press('Escape')`, with focus moving asynchronously after the key event. I
   checked the line rather than taking it. So that family has at least two more members than the two named
   in "Two latent races in other tasks' browser tests", my 37 new browser tests push the suite further over
   whatever its tipping point is, and **`928 / 0` should be read as a best case rather than a steady state.**
3. **Two things I reported had never landed in the branch.** The parse-versus-range distinction in the
   window Theory existed only in my hand-back; it is now a remark on the test, with the measurement that
   establishes it. The restore count and the file count *had* landed but spelled as words
   ("thirty-three", "seven files"), so a grep for `33` found nothing — which is the same lesson as owed item
   10, in a third costume. Both now carry the numeral. **A measurement that exists only in a report is the
   same defect as a comment that describes intent.**
4. **`bank-transfers-page.ts` was already fixed in round 2** — line 127 is
   `money(row.amountLkr, row.currency)`. I listed it as an open oddity; the residual oddity is the field
   *name*, which encodes LKR while the currency travels beside it, and that is W3-03's to rename.

And one of my suggestions was refused, correctly: `An_unusable_window_answers_null` keeps its name, because
"unusable" covers both the parse guard and the range guard and its remarks single out only `0`.

**On `promoDiscountAllocated`** (my decision-6 answer): the reviewer checked the semantics rather than
taking them and confirmed the money reading — a per-line refund computed on `lineSubtotal` hands back the
customer's promo share as well. It cannot arise today, because v1 refunds in full from the settlement
payment, so the right filing is a **constraint on W3-05's refund transaction** rather than a bug: *a line's
refundable amount is `lineTotal`, never `lineSubtotal`*. That is **#129**, and it is the one field on my
list nobody should call dead.

## What is owed, and by whom

| # | Owed | By |
|---|---|---|
| 1 | **`Orders.Refund` and the refund screen.** Doc 02 ADM-09 says "cancel/refund **status**", which this build shows — `sales.Refunds` is read and rendered on the detail — but nothing *writes* one. So doc 11 §7 G-13 ("money received on an order that was never paid") still has no system path, and the short-stock refusal still says "refund the money received instead" while offering no button. W3-03's journal owed this to W3-04; I have not built it, and I would rather say so than ship a half-path over money. The detail's internal note is the honest place to record what was done by hand. | W3-05 / ADM-09 |
| 2 | **Cancelling an unpaid order** (doc 11 P10 flow A, `Orders.Edit` and the customer's own). W3-03's journal owed this here too. Not built: it releases stock and a promo use, and it belongs with the refund transaction rather than bolted on to a fulfilment screen. | W3-05 / ADM-09 |
| 3 | **`InvoiceSellerSnapshotJson`.** Doc 08 §4.1 wants the seller's details frozen on to the order when the number is issued. The four keys now exist, so the blocker is gone — but the column is still null on every order ever paid, and the invoice reads the **live** settings instead. That is right for every invoice from now on and wrong for a reprint of an old one after the client changes their registration details. One statement in `BankTransferReviewStore.IssueInvoiceAsync`, owed to whoever next touches that transaction. | W3-05 / W4-01 |
| 4 | ~~A write screen for the four `Store.*` legal keys and `Orders.AutoDeliverAfterDays`.~~ **Built in round 2** (tester's decision 5). And the note above was wrong twice: they were not "readable on the settings screen" — that screen renders named forms, so they appeared nowhere — and an invoice that cannot be completed short of SQL is not a deliverable, so it was never ADM-22's to wait for. | — |
| 5 | **`ops.EmailOutbox`.** Doc 11 P9 step 5's `OrderShipped` is specified and unbuilt. Nothing here claims otherwise. | W3-06 |
| 5a | **The shopper cannot see their tracking number anywhere.** *(Confirmed by the tester, scheduled as its own piece of work — estimate half a day: three nullable members on `OrderConfirmationDto`, populated in `ICheckoutReader`'s projection reusing `BuildTrackingUrl`'s substitution so the template never reaches the public contract, one `<dt>/<dd>` gated on status ≥ Shipped and `!isPickup`, plus one integration test pinning the three fields on a real shipped row. **Not started, on instruction.**)* This is the one that matters most to a customer, and it is worth stating plainly: the public order DTO has no courier or tracking field and the storefront's order page renders neither — I grepped for it. Doc 11 P9 step 6 assumes the customer gets the tracking link from the dispatch email *or* from their order page; the email is W3-06 and the order page's link is **ACC-02** ("live order status + courier tracking link", doc 02 line 97). So from today staff can dispatch an order, and the number the courier gave them lives only in the admin panel. It is not ADM-09's to add — a change to the public order contract and the storefront page is ACC-02's — but whoever schedules next should know that shipping this screen without one of those two leaves the customer with "Shipped" and nothing to do about it. | ACC-02 / W3-06 |
| 6 | **`ops.JobLeases`.** `AutoDeliverShippedService` waits one interval before its first sweep, so **a host that restarts more often than once a day never reaches one**. That is a real limitation and it is written on the method. A persisted "last run" is the proper fix; until then the staff button is the primary path and the job is the safety net, which is the order doc 11 P9 step 8 puts them in. | W3-06 / Architect |
| 7 | **`inventory.InventoryMovements`** (#108). Unchanged by this task — none of these four transitions touches stock. | W1-05 / ADM-12 |
| 8 | **ADM-12's inventory screen** is explicitly *not* this task and is untouched. | ADM-12 |
| 9 | No `progress-data.js` change, no `MEMORY.md`, no `TASKS.md` (doc 14's rule). | owner's session |
| 10 | **If parallelism is ever enabled, the dangerous thing is a fixture that mutates seeded *reference* data, and my round-2 inventory looked in the wrong place.** The premise I filed this under inverts — the assembly **does** run its classes one after another, measured, so the thirteen comments saying so are **correct** and actioning my previous version of this row would have replaced true statements with false ones across three other tasks' tests. The conclusion stands for a stronger reason: the eleven stay untouched because there is nothing wrong with them. **The real item, rebuilt from the pattern rather than from a grep for the words "sequential" and "parallel":** `SalesReferenceDataTests.Running_the_seeder_again_changes_nothing_it_has_already_written` sets `DeliveryMethods.IsEnabled = false` for **STANDARD** and the Colombo zone fee to **555**, restoring in a `finally` — and `PlaceOrderAsync`, the base fixture of at least six classes, places orders through Standard/Colombo and asserts their totals. Harmless while classes are serialised; the single most dangerous fixture in the assembly the day they are not. My word-grep could not have found it, because it says nothing about parallelism — it *does* something unsafe. That is round-1 defect #8's lesson in a new costume: **a grep sweep is only as good as its grep**, and the second time I have paid for it in this task. The pattern to sweep for is a test mutating a row it did not create in `DeliveryMethods`, `DeliveryMethodZoneRates`, `Districts`, `PickupLocations`, `PaymentProviders` or `ops.Settings`; today that is `SalesReferenceDataTests` (reference data, the unsafe one), `CheckoutTestBase`, `AdminOrderTestBase`, `BankTransferTestBase`, `AdminBankTransferEndpointTests`, `AdminOrderEndpointTests`, `AdminSettingsEndpointTests`, `AdminSettingsShapeTests` and `SettingsSchemaAndSeedTests`. | #114 |

## What I deliberately did not build

- **Any refund or cancel path**, for the reason above.
- **A migration.** Nothing in this branch changes the schema: the five new settings are `ops.Settings` rows
  from the idempotent seeder, and every column, index and constraint the screens use already exists from
  #103 and W3-03. `has-pending-model-changes` is clean after a rebuild and
  `git diff origin/main -- …/Migrations/` is **empty** — not merely additive, empty. Main's latest is still
  `20261007055218_ContentPagesAndContactSubmissions`.
- **Any new index.** All five the list and the job need are already there:
  `UX_Orders_OrderNumber`, `IX_Orders_Status_Placed`, `IX_Orders_Placed` (which covers `OrderNumber`),
  `IX_Orders_EmailNormalized` / `IX_Orders_Phone`, and `IX_Orders_Shipped` — the last filtered on
  `Status = 3`, which is the auto-deliver job's own index and the reason its predicate writes
  `OrderStatus.Shipped` as a compile-time member rather than a captured variable (DB28).
- **A server-rendered invoice PDF.** `window.print()` and `@media print` rules, which produce a PDF on
  every platform the client uses with no font embedding, no rendering service and no second copy of the
  layout to keep in step. Doc 02 asks for "print invoice".
- **A courier delete.** Deactivation only; see above.
- **`withComponentInputBinding()`.** It would have been one line, and it changes every route's behaviour
  in the application for the sake of two new ones. `ActivatedRoute` is this project's established
  convention.

## What I could not verify

- **A second browser engine.** Everything is Chromium, which is what `--project=chromium` gives and what
  the project runs.
- **The job under a real 24-hour clock.** `AutoDeliverShippedService`'s timing is a constant and its
  statements are tested directly with a chosen cut-off; what is not tested is that the host actually ticks
  once a day, which needs a running container and a day.
- **The invoice as a printed sheet.** The `@media print` rules are asserted only in that the screen's own
  bars carry the class that hides them; nobody has put paper through a printer.
- **Anything about the real API from a browser test.** Issue #126: Playwright never starts the .NET API, so
  every browser assertion here is about the screen against a Node stub. Each field the screens depend on is
  paired with an integration test that pins it on a real row — that pairing is the mitigation, and it is the
  reason `AdminOrderEndpointTests` asserts every field of a list row and a detail rather than a sample.
