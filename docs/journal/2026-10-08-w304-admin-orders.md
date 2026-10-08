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

| Suite | Before (main) | After | New |
|---|---|---|---|
| xUnit unit | 1,726 / 0 | **1,808 / 0** | 82 |
| xUnit integration | 1,124 | **1,192 / 0**, 0 skipped (18 m 5 s, whole assembly) | 68 |
| Vitest — shared | 24 | 24 | — |
| Vitest — storefront | 1,404 | 1,404 | — |
| Vitest — admin | 407 | **444 / 0** | 37 |
| Playwright (chromium) | 891 / 0 | **920 / 0** | 29 |

**The whole-assembly integration run looked hung at 16 minutes with 3 s of CPU and no SQL session, and was
not**: it finished green at 21 m 8 s. Worth knowing before somebody kills one. (An attempt to kill it was
refused by the permission system, which turned out to be the right answer.)

**The three Vitest projects are listed separately because a single "1,404 / 407 / 24" is ambiguous and I
got it wrong first.** Measured one project at a time on this machine: `shared` is **24**, `storefront` is
**1,404** and `admin` is **407** before this task — so the brief's three figures are all correct, in the
order *storefront / admin / shared*, which is neither alphabetical nor the order `npm run test:ci` runs
them in (`shared`, `storefront`, `admin`). Worth writing down per project from now on.

Ports: every `E2E_*_PORT` was overridden into **4820–4836**, scanned free first. `4502` (`charon-svc`) and
`4767` (`PanGPS`) were identified by PID and left alone; nothing was killed.

## What is owed, and by whom

| # | Owed | By |
|---|---|---|
| 1 | **`Orders.Refund` and the refund screen.** Doc 02 ADM-09 says "cancel/refund **status**", which this build shows — `sales.Refunds` is read and rendered on the detail — but nothing *writes* one. So doc 11 §7 G-13 ("money received on an order that was never paid") still has no system path, and the short-stock refusal still says "refund the money received instead" while offering no button. W3-03's journal owed this to W3-04; I have not built it, and I would rather say so than ship a half-path over money. The detail's internal note is the honest place to record what was done by hand. | W3-05 / ADM-09 |
| 2 | **Cancelling an unpaid order** (doc 11 P10 flow A, `Orders.Edit` and the customer's own). W3-03's journal owed this here too. Not built: it releases stock and a promo use, and it belongs with the refund transaction rather than bolted on to a fulfilment screen. | W3-05 / ADM-09 |
| 3 | **`InvoiceSellerSnapshotJson`.** Doc 08 §4.1 wants the seller's details frozen on to the order when the number is issued. The four keys now exist, so the blocker is gone — but the column is still null on every order ever paid, and the invoice reads the **live** settings instead. That is right for every invoice from now on and wrong for a reprint of an old one after the client changes their registration details. One statement in `BankTransferReviewStore.IssueInvoiceAsync`, owed to whoever next touches that transaction. | W3-05 / W4-01 |
| 4 | **A write screen for the four `Store.*` legal keys and `Orders.AutoDeliverAfterDays`.** They are in the catalogue, seeded, and **readable** on the settings screen — the read model is generic — but there is no write handler for them, so today they can only be set with SQL. The save unit and the token are already declared, so this is one command handler and one form. I did not build it because the invoice and the job both work without it (one prints nothing, the other uses the seeded 7) and the brief's order of value put the invoice fifth. | ADM-22 |
| 5 | **`ops.EmailOutbox`.** Doc 11 P9 step 5's `OrderShipped` is specified and unbuilt. Nothing here claims otherwise. | W3-06 |
| 5a | **The shopper cannot see their tracking number anywhere.** This is the one that matters most to a customer, and it is worth stating plainly: the public order DTO has no courier or tracking field and the storefront's order page renders neither — I grepped for it. Doc 11 P9 step 6 assumes the customer gets the tracking link from the dispatch email *or* from their order page; the email is W3-06 and the order page's link is **ACC-02** ("live order status + courier tracking link", doc 02 line 97). So from today staff can dispatch an order, and the number the courier gave them lives only in the admin panel. It is not ADM-09's to add — a change to the public order contract and the storefront page is ACC-02's — but whoever schedules next should know that shipping this screen without one of those two leaves the customer with "Shipped" and nothing to do about it. | ACC-02 / W3-06 |
| 6 | **`ops.JobLeases`.** `AutoDeliverShippedService` waits one interval before its first sweep, so **a host that restarts more often than once a day never reaches one**. That is a real limitation and it is written on the method. A persisted "last run" is the proper fix; until then the staff button is the primary path and the job is the safety net, which is the order doc 11 P9 step 8 puts them in. | W3-06 / Architect |
| 7 | **`inventory.InventoryMovements`** (#108). Unchanged by this task — none of these four transitions touches stock. | W1-05 / ADM-12 |
| 8 | **ADM-12's inventory screen** is explicitly *not* this task and is untouched. | ADM-12 |
| 9 | No `progress-data.js` change, no `MEMORY.md`, no `TASKS.md` (doc 14's rule). | owner's session |

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
