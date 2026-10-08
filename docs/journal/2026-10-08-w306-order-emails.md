# 2026-10-08 — W3-06 / ACC-03: `ops.EmailOutbox`, the order emails and `SendEmails`

Branch `feature/W3-06-order-emails`, worktree `worktrees/sd-email`, cut from `origin/main` at `6f65d3e`.
Scope: **doc 08 §3.8's `ops.EmailOutbox`, doc 11 §4.1's customer emails, and the `SendEmails` job**.
**Not pushed, not a PR — the tester goes first.**

Closes the hole every task since W3-02 has written into its own report: the shop took money and told
nobody. `OrderPlacementStore`, `OrderExpiryStore`, `BankTransferReviewStore`, `OrderFulfilmentStore` and
`OrderCancellationStore` each carried a paragraph saying an email belonged there and that nothing sent
one. All five now queue one, and none of them claims anything it cannot do.

## What it does, end to end

A customer checks out. In the **same transaction** that reserves their stock and writes their order, two
rows go into `ops.EmailOutbox`: "We have your order SD-2026-000123", and — if the shop's bank details are
set — "How to pay for order SD-2026-000123", carrying the account to pay into, the exact amount, the order
number to quote as the reference, the deadline in Colombo time, and the link to send the slip.

Staff find the credit on the statement and press Approve. In that transaction, with the invoice number
that was issued three statements earlier, "Payment received for order SD-2026-000123" is queued. They pack
it, choose a courier, enter the tracking number: "Order SD-2026-000123 is on its way", with the link built
from the courier's own template. Or they put it on the shelf for collection and the same template says
"ready to collect", with the store's address and hours and no mention of a courier.

Nobody pays: the expiry job closes the order and "Order SD-2026-000123 has closed" tells the customer the
items are back on sale, that nothing has been charged, and what to do if the money has already gone.
Somebody cancels: the same, worded by who did it.

Thirty seconds later `SendEmails` wakes up, finds the messages, and **sends none of them**, because we do
not have the client's SMTP credentials. It says so, with a count, at `Warning`, once every thirty
minutes. Nothing is marked Sent, nothing is marked Failed, and nothing burns a rung of the retry ladder
against a mail server that does not exist. The day the credentials arrive, the queue goes out.

## The two decisions that matter, and why

### An unconfigured SMTP queues and does not send

`SendEmails` asks the transport whether it could send **before it claims anything**. If it could not, it
reads how many messages are due and leaves the queue untouched.

An operator can therefore tell the two quiet states apart. **Silence means there is nothing to send.** A
repeating `Warning` naming a growing number means there is plenty to send and no way to send it:

> `3 email(s) are queued and cannot be sent, because this host has no SMTP configuration. Nothing has been
> lost: the messages are untouched and will go out as soon as `Email__Smtp__Host` and
> `Email__Smtp__FromAddress` are set (doc 08 §3.8's Email.Smtp group). This is repeated periodically for as
> long as the queue is not empty.`

Thirty minutes, not every sweep, for `ExpireUnpaidOrders`' reason: a job that logs its failure once a cycle
for a day is a job whose log nobody reads. The alternative — treating an unconfigured host as a failing one
— would spend all six attempts in the first five hours and kill every order email the shop had queued.

### The bank-transfer email is not queued when the bank details are unset

`IPaymentProvider.DescribeAsync` already answers `null` when the shop's `Bank.*` settings are empty; W3-03
built that so the order page could say the details were being confirmed rather than print an empty account
number. The placement transaction reads the same answer and queues **no instructions email at all** when
it is null.

An email headed "How to pay" with a blank account number is not a degraded email, it is a wrong one: a
customer could transfer money nowhere. It is W3-04's invoice rule — print nothing for unset legal details
rather than something plausible — applied to the one message where the cost of a placeholder is somebody's
money. The order confirmation still goes, and it deliberately **does not announce** the second message,
because the second message is not always there.

`With_the_bank_details_unset_the_instructions_email_is_not_queued` pins both halves: the one row that is
queued, and that it says nothing about paying.

## The emails, against doc 11 §4.1

| Template | Trigger built here | Content doc 11 lists | Built |
|---|---|---|---|
| `OrderPlaced` | placement (P6 step 9/10) | order number, items with colour/size/qty, prices, discounts, delivery method and address or pickup location, total, payment method, order link | ✅ all of it |
| `BankTransferInstructions` | placement, bank transfer only (P7 step 1) | bank name, branch, account name and number, exact amount, order number as reference, due date/time, slip-upload link | ✅ all eight, or **no email** |
| `PaymentConfirmed` | approve (P7 6a) and accept-late (P7 8) | payment received, amount, invoice number, what happens next | ✅, and the late case says the order was reopened |
| `OrderShipped` | mark shipped (P9 step 5) | courier, tracking number and link; or pickup location, hours, "ready for collection" | ✅ both variants (§7 G-35) |
| `OrderExpired` | expiry (P7 step 7, P10 flow B) | not paid in time, items released, how to order again, what to do if already paid | ✅ all four |
| `OrderCancelled` | cancel (P10 flow A) | — **not in doc 08 §3.8 or doc 11 §4** | ⚠️ **a scope increase — see below** |

And the ones deliberately **not** sent, each because a document says so:

| Not sent | Why |
|---|---|
| Bank transfer rejected | doc 11 P7: "No email on rejection" (§7 G-14, client's decision) |
| Order processing, order delivered | doc 11 P9: "No email on Processing or Delivered" (§7 G-14) |
| A corrected tracking number | doc 11 P9's exception: the customer is telephoned, and "a 'tracking number corrected' email is not in v1" |
| Order refunded | §7 G-14, and there is no refund in this build (owner decision, 2026-10-08) |
| Payment due reminder, abandoned bag, low stock, newsletter campaigns | §4.4, line 626, §7 G-05, line 42 — all out of scope |
| `Welcome`, `EmailConfirmation`, `AccountExists`, `PasswordReset`, `StaffInvite` | W3-01's and P14's. The outbox is *ready* for them (below) and has none of them |
| `SensitiveSettingsChanged` and its dual-send through the previous SMTP settings (doc 08 N2) | the settings task's. The **column** is added because the schema has it; see below |

## `OrderCancelled` is a scope increase, and the client has not decided

Doc 11 **§4.4** lists "order cancelled" among the events with **no notification in v1**, and doc 08 §3.8's
`TemplateCode` list has no such code. Doc 11 **§7 G-14** recommends adding one — "Recommend at least Bank
transfer rejected, **Order cancelled**, Order refunded; client to decide (adds templates, small scope
increase)" — and its owner is the **Client**, with the row still open.

It is built on the owner's instruction for this task. The gap it closes is real: before today a shopper
could cancel their own order from their own order link and hear nothing at all. But it is one template
beyond what doc 08 and doc 11 §4 commit to, so:

- it needs **doc 11 §4.1 and §4.4 and doc 08 §3.8's code list** amending, or
- the constant, one composer method and the one call in `OrderCancellationStore` removing.

Nothing else reads it. The note is on `EmailTemplateCodes.OrderCancelled` in the source as well, so it
cannot be found only here. `TemplateCode` has **no check constraint** (doc 08 marks a CK on `Status` only),
so neither adding nor removing it is a migration.

## The transactional outbox, and the four places it is inserted

Doc 08 §3.8: "Rows are inserted in the business transaction (transactional outbox)." Doc 11 P6 step 9:
"saves the order … and the queued emails."

`IEmailOutboxWriter.Enlist` is `IAuditLogWriter.Enlist`'s shape and exists for issue #59's reason: the
caller's own `SaveChangesAsync` commits the message together with the thing the message is about. There is
**no `SendAsync` and no `WriteAsync`** on the port, so there is no way for a caller to commit an email
separately from the change it describes.

| Transaction | Where the enlist sits | What a rollback does |
|---|---|---|
| Placement (`OrderPlacementStore.PlaceAsync`) | after `Orders.Add`, before the one `SaveChangesAsync` | out-of-stock rolls back before the insert; `DetachAll()` stops a retried attempt re-adding |
| Approve / accept-late (`BankTransferReviewStore`) | after the audit row, before `SaveChangesAsync` | `InTransactionAsync` rolls back every non-`Applied` answer |
| Expiry (`OrderExpiryStore.ExpireAsync`) | after the history row, before `SaveChangesAsync` | a losing claim returns before the enlist |
| Cancel (`OrderCancellationStore.ApplyAsync`) | after the history row, before `SaveChangesAsync` | a refusal returns before the enlist, and `DiagnoseAsync` rolls back |
| Dispatch (`OrderFulfilmentStore.MarkShippedAsync`) | after the audit row, before `SaveChangesAsync` | `InTransactionAsync` rolls back every refusal |

Five rollback paths are tested directly — an out-of-stock placement, a refused approval, a refused
cancellation, a second expiry and an order that is not yet due — and each queues nothing.

### What each of the four neighbours' transactions gained, line by line

Four of these files move money or stock, so every difference is listed:

| File | Change | Lock order |
|---|---|---|
| `OrderPlacementStore` | one ctor parameter; `QueueEmails` + `AddressLines` added; one call between `Orders.Add` and `SaveChangesAsync`; class docblock's "what this does not write" corrected | unchanged — the enlist is EF change-tracking, not a statement |
| `OrderExpiryStore` | one ctor parameter; `ReadForEmailAsync` + `EmailSnapshot` added; the read placed **immediately after the claim**, on a row this transaction already holds exclusively; one enlist beside the history row; the stale "is not queued" paragraph corrected | unchanged — the read takes no new lock |
| `BankTransferReviewStore` | one ctor parameter; four columns added to `ReadOrderAsync`'s existing projection and to `OrderSnapshot`; `QueuePaymentConfirmed` added; one call in `ApproveAsync` and one in `AcceptLateAsync`; the "No email" bullet rewritten as "no email on a rejection" | unchanged — no new statement at all |
| `OrderCancellationStore` | one ctor parameter; three columns added to the existing projection and to `OrderSnapshot`; one enlist after `WriteHistory` | unchanged |
| `OrderFulfilmentStore` | one ctor parameter; three columns added to the existing projection; the courier read now also selects `TrackingUrlTemplate`; `ReadPickupAsync` + `PickupSnapshot` added (pickup branch only); one enlist; the "No email" paragraph rewritten | unchanged — `sales.Couriers` and `sales.PickupLocations` are outside §5.3's four-table order and neither is written |

`RejectAsync` was **not** touched, which is the point: doc 11 P7 says no email on rejection, and
`Rejecting_a_transfer_queues_nothing` is what keeps it that way (mutation M19 proved it).

One shared helper moved rather than being copied: `AdminOrderReader`'s private `BuildTrackingUrl` is now
`SpencerDenim.Domain.Sales.OrderTrackingLink.Build`, read by the admin screen and by the dispatch email.
A second copy would have been free to get the URL-escaping differently, which is the one part of it that
is not cosmetic.

## `SendEmails`

Doc 11 §1.4's job table lists it with **no interval**, so thirty seconds is a decision: a newly queued row
is due immediately, so the sweep interval *is* how long a shopper waits for their confirmation, and a
minute of that looks broken when the page has already said the order is placed. An empty sweep is one seek
on `IX_EmailOutbox_Due`.

It follows `ExpireUnpaidOrdersService` rather than inventing a shape: `PeriodicTimer`, first sweep after
one interval, `OperationCanceledException` swallowed on shutdown, every cycle wrapped so an exception
cannot fault the host, and a repeated failure dropping to `Warning` after the first.

**No lease and no leader election.** `ops.JobLeases` is in doc 08 §3.8 and in **no migration** — checked,
as the brief asked, and the two existing jobs say the same — and none is needed. The claim is

```sql
WITH due AS (
    SELECT TOP (20) * FROM ops.EmailOutbox WITH (ROWLOCK, READPAST, UPDLOCK)
     WHERE Status IN (0, 3) AND NextAttemptAtUtc <= @now
     ORDER BY NextAttemptAtUtc, Id
)
UPDATE due SET Status = 1, LockedUntilUtc = @leaseUntil, AttemptCount = AttemptCount + 1
OUTPUT inserted.Id, …;
```

A lease would make the *job* run once; the claim makes each *message* send once, which is the invariant
that matters when the thing being sent is somebody's bank details.
`No_message_is_ever_claimed_twice_however_many_senders_race_for_it` runs three senders against the real
engine in rounds and asserts that no id is ever handed out twice.

It is doc 08's `UPDATE TOP (20) … OUTPUT` wrapped in a CTE, for two things the bare form cannot carry: the
`ORDER BY` (two messages queued in the same millisecond share a `NextAttemptAtUtc` to `datetime2(3)`, which
is `OrderExpiryStore.FindDueOrdersAsync`'s own lesson) and `READPAST`, which makes a second sender skip
locked rows instead of queueing behind them.

**The counter is incremented by the claim, not by the failure path.** A message claimed and then lost — the
process is killed, the connection drops — has been attempted as far as anybody can tell, and a counter that
only moved on a *recorded* failure would let a crash loop retry one message for ever.

### The ladder: five intervals mean six attempts

Doc 08 §3.8 and doc 11 (§1.4's SMTP actor row and §4) both write "1/5/15/60/240 min". An interval is the
wait *between* two attempts, so five waits are the first attempt plus five retries: six in all, spread over
**5 h 21 min**. The alternative reading — five attempts, the last interval never used — would make one of
the document's five figures dead text. `EmailBackoff` holds the figures and the arithmetic in one place and
`EmailBackoffTests` writes them out as the anchor.

After the sixth failure the row is **Dead**, which is terminal because 4 is outside
`IX_EmailOutbox_Due`'s filter — asserted by claiming again and finding nothing, not by reading the column.

### Two things the sender does that nothing yet writes

- **`TransportOverrideProtected` is NULLed on every terminal status** (doc 08 §3.8 R5). The dual-send that
  fills it is the settings task's. The column is here because the schema has it, and the rule is honoured
  ahead of its first writer — proved by giving a row a ciphertext by hand.
- **The body is destroyed the moment a `PasswordReset` or `EmailConfirmation` is sent** (G-03: "the body
  must not sit in the outbox table or in a backup"). Both templates are W3-01's and neither exists; the
  rule is four lines in the sender, and building it now is what stops that task having to come and find a
  privacy rule written in another document. Tested under both codes, and tested *not* to fire for the six
  that are built.

## The schema

`20261008182826_OpsEmailOutbox` — doc 08 §3.8, column for column and width for width. Snapshot diff against
`origin/main` is **94 insertions, no deletions**; `has-pending-model-changes` clean after a rebuild.

| Index | Source |
|---|---|
| `IX_EmailOutbox_Due (NextAttemptAtUtc) WHERE Status IN (0,3)` | doc 08 §3.8, verbatim |
| `UX_EmailOutbox_Dedup (DeduplicationKey) UNIQUE WHERE DeduplicationKey IS NOT NULL` | doc 08 §3.8's "Filtered UX" |
| `IX_EmailOutbox_Stuck (LockedUntilUtc) WHERE Status = 1` | **not in doc 08 — added** |

The third one is a cost traded against a worse one. A sender that dies between the claim and the send
leaves its rows at `Sending`, which is *outside* the due index's filter, so recovering them needs a
predicate on `Status = 1` — and with no index for it that is a clustered scan of a table whose rows carry
`nvarchar(max)` bodies, run once every thirty seconds for ever. This index is almost always empty, so it
costs a few pages and turns that scan into a seek. The brief's warning about EF's FK-index convention
collapsing a filtered and an unfiltered index **does not apply here**: this table has no foreign key at all
(doc 08: "related to Orders with no FK"). Both are named explicitly anyway.

**`DeduplicationKey` is null on every row this build writes.** Each of the five call sites sits behind a
conditional claim that can succeed once, so there is no duplicate to prevent — and a unique violation
raised from *inside* an approval would roll back a payment to stop a second copy of an email, which is the
wrong trade. The index is there for the account emails, where a customer pressing "send me a new link"
twice is an ordinary thing to do, and it is tested on its own.

Two check constraints, both proved by violating them: `CK_EmailOutbox_Status` (0–5, the document's whole
list including the `Cancelled` nothing writes) and `CK_EmailOutbox_Attempts` (`AttemptCount >= 0 AND
MaxAttempts >= 1`). There is deliberately **no** `AttemptCount <= MaxAttempts` clause: it is true of every
row the sender writes, nothing reads it, and a CK with no reader on a table written inside a payment
transaction is a way to turn a counter bug into a rolled-back approval.

The table is **never audited** (doc 08 §3.8, "may contain reset links"), so the entity is not `IAuditable`
and the absence of the audit column pair is asserted against the database.

## SMTP configuration

Environment variables under doc 08 §3.8's own key names, so the move into `ops.Settings` later is a reader
swapped in behind one options class:

| Variable | Default | Notes |
|---|---|---|
| `Email__Smtp__Host` | — | unset ⇒ the shop queues and does not send |
| `Email__Smtp__Port` | `587` | the submission port |
| `Email__Smtp__Username` / `__Password` | — | optional: a relay may authenticate by IP |
| `Email__Smtp__FromAddress` | — | required with the host; unset ⇒ not configured |
| `Email__Smtp__FromName` | — | **not** defaulted to the shop's name: a From name belongs to whoever owns the mailbox |
| `Email__Smtp__UseStartTls` | `true` | on unless turned off, never off by omission |
| `Email__StorefrontBaseUrl` | — | unset ⇒ the emails carry **no links**, and nothing else is lost |
| `Email__SweepSeconds` | `30` | |
| `Email__BatchSize` | `20` | doc 08 §3.8's `TOP (20)` |
| `Email__LeaseMinutes` | `5` | |
| `Email__UnconfiguredWarningMinutes` | `30` | |

Doc 08 §3.8 files `Email.Smtp.*` under **`Settings.Security`**, which needs the sensitive-change alert,
`ops.AdminAlerts` and the GrantsAll-only permission — none of which exists. A settings group with no screen
and no alert is a worse place for a mail password than an environment variable the host already protects.
`CLAUDE.md`'s own rule ("config via environment variables, Azure-portable") points the same way.

`Email__StorefrontBaseUrl` is validated at startup to an absolute `http`/`https` URL **or nothing**, because
the two mistakes that would put a broken link into every email the shop sends — a bare host with no scheme,
and a relative path — should fail on the way up rather than in a customer's inbox. The API cannot work the
origin out for itself: taking it from a request's `Host` header would put a forgeable value into a message
we send on the shop's behalf, which is the hole W1-01 round 3 closed for IP addresses.

## The bodies

`OrderEmailComposer` is pure — nothing in it reads a database, a clock or a request — which is what lets
the unit suite assert the messages themselves. Three rules every body obeys:

1. **Nothing is invented.** No telephone number, no postal address, no support mailbox: those are
   `Contact.*` settings the client has not filled in, and a customer asked to ring a number nobody answers
   is worse off than one asked to get in touch. No invented tracking link for a courier with no template.
   No "Dear Customer" for an order with no first name. An omitted provider field leaves **no empty label**.
2. **Nothing is promised that this build cannot do.** `OrderPlaced` does not announce the instructions
   email, because the instructions email is not always queued. (A promise made *inside* a sent email is
   safe in one direction only: if the reader is holding it, the transport works — so "you will hear from us
   when it leaves us" is honest, because `OrderShipped` is built and is queued by the dispatch.)
3. **The shop is the shop.** The name is read from `StoreBrand.Name`; the client company's name appears
   nowhere. `BrandNameConventionTests` caught two of my own docblocks writing the name out and was right to.

Both parts are written, never one derived from the other: a text part stripped from HTML reads like
stripped HTML, and it is the part that reaches a text-only client and every spam filter that scores a
message with no text alternative. The HTML is a 600-pixel single column with inline styles, no images and
no web fonts — a mail client is not a browser, `<style>` is stripped by several of them, and a remote image
is blocked by most until the reader asks for it. Every interpolated value is HTML-escaped: a product name
is free text somebody typed into the catalogue.

Times are **Colombo** times with the offset named in the string ("Sunday 11 October 2026 at 2:03 pm (Sri
Lanka time)"). The deadline is the one figure in these messages a customer acts on with a clock in front of
them, and a time five and a half hours out would cost them their order. The weekday is spelled out because
"11/10/2026" means two different days depending on where the reader learned to write dates.

## No test touches a real mail server, and that is structural

Two independent reasons, so neither has to be remembered:

1. **`ApiFactory` blanks `Email:Smtp:Host` and `Email:Smtp:FromAddress`** for every test host. Blanked
   rather than left out, because the in-memory provider only wins for keys it holds — a developer with
   `Email__Smtp__Host` set in their own environment would otherwise give every test host a working
   transport and `SendEmails` something to do with the queue. Blank is also the shop's real state, so the
   suite exercises the production path.
2. **`ApiFactory` replaces `IEmailTransport`** with one that reports itself unconfigured and **throws** if
   anything calls it, naming what went wrong.

The unit suite drives the sweep against a fake transport with no socket in it; the integration suite drives
the four statements directly and has no transport at all. The only untested code is `SmtpClient`'s own
conversation with a server, which is the smallest honest surface to leave untested and the alternative is a
test suite that can send mail to a real address.

## Numbers

| Suite | Before (main) | After | New |
|---|---|---|---|
| Unit | 1,814 | **1,904** | 90 |
| Integration | 1,236 | **INTEGRATION_TOTAL** | 52 |
| Vitest | 24 / 1,417 / 465 | unchanged | — |
| Playwright | 935 / 0 | unchanged | — |

No frontend file was touched, so neither front-end suite was run and `npm ci` was not needed. Nothing in
this change is observable from a browser: there is no new screen, no new endpoint and no change to any
payload. **That is deliberate** — see "What is owed", item 1.

### Mutation testing

Every new test was proved to fail against a mutation of the code it covers. 23 mutations, 21 killed:

| Mutation | Killed by |
|---|---|
| M1 ladder's last rung 240 → 241 | `The_ladder_is_the_one_both_documents_state`, `Each_failed_attempt_waits_its_own_rung`, `The_ladder_spans_five_hours_and_twenty_one_minutes`, `A_count_past_the_ladder_stays_on_the_last_rung` |
| M2 `MaxAttempts` = 5 not 6 | `Six_attempts_in_all`, `Each_failed_attempt_waits_its_own_rung`, `The_ladder_spans_five_hours_and_twenty_one_minutes` |
| M3 a broken counter restarts the ladder | `A_broken_counter_stops` |
| M4 drop the "Reference to quote" row | `The_bank_instructions_carry_every_field_P7_step_1_names`, `The_html_part_carries_the_same_bank_details` |
| M5 stop HTML-escaping | `A_product_name_holding_markup_is_escaped` |
| M6 invent an origin when none is configured | `With_no_configured_origin_there_are_no_links_and_nothing_else_is_lost` |
| M7 print UTC instead of Colombo | `Times_are_Colombo_times_and_say_so` + both field theories |
| M8 queue rows as `Sent` | `A_queued_message_is_pending_unattempted_and_due_now` |
| M9 send with no SMTP configured | `With_no_smtp_configured_nothing_is_claimed_and_the_count_is_reported`, `With_no_smtp_configured_and_an_empty_queue_it_does_nothing` |
| M10 unconfigured transport reports success | `An_unconfigured_transport_refuses_rather_than_pretending` |
| M11 widen the claim's `Status IN (0,3)` to every status | `A_row_in_these_states_is_never_claimed`, `The_last_failure_kills_the_message_and_no_later_sweep_sees_it`, `A_message_that_never_sends_is_attempted_exactly_six_times`, `No_message_is_ever_claimed_twice_…` |
| M12 the claim does not increment the attempt | 6 tests, incl. `A_failed_attempt_waits_the_ladders_next_rung` and `A_sender_whose_lease_expired_…` |
| M13 `MarkSent` ignores the claimed attempt count | `A_sender_whose_lease_expired_cannot_record_against_the_reclaimed_row` |
| M14 always destroy the body on send | `Marking_sent_stamps_the_row_clears_the_lease_and_keeps_an_ordinary_body` |
| M15 never destroy the body on send | `A_token_bearing_message_loses_its_body_the_moment_it_is_sent` (both codes) |
| M16 lease release ignores the expiry time | `An_expired_lease_is_released_and_a_live_one_is_left_alone` |
| M17 lease release requeues an exhausted row | `A_stranded_row_with_no_attempt_left_is_killed_rather_than_requeued` |
| M18 queue the instructions email with no bank details | 15 tests, incl. `With_the_bank_details_unset_the_instructions_email_is_not_queued` |
| M19 email a rejected transfer | `Rejecting_a_transfer_queues_nothing` |
| M21 put the raw template in the dispatch email | `Dispatching_an_order_queues_the_shipped_email_with_a_real_tracking_link` |
| M23 remove every guard from the cancel claim | `A_refused_cancellation_queues_nothing` |

**Two mutations survived, and both are non-viable rather than test holes** — said plainly, because
explaining a survivor away is how yesterday's real hole got through:

- **M20: enlist the cancellation email before the guards, so a refusal queues one too.** It cannot leak:
  `OrderCancellationStore` calls `SaveChangesAsync` only on the applied path, and the `finally` clears the
  change tracker — so the enlisted row is never written. The mutant is behaviourally identical to the
  original. M23 is its viable form and it kills.
- **M22: widen the cancel claim's status to allow a Paid order.** Neutralised by a *different* clause in
  the same statement: the claim's `NOT EXISTS (… IsOrderSettlement = 1)` still matches nothing for a paid
  order, so the cancel is still refused. This is the mutual redundancy `OrderCancellationStore`'s own
  remarks already measure for `Status` and `StockState`; M23 removes the whole `WHERE` tail and dies.

Two of my own tests were **wrong first and the code was right**, which is worth recording as evidence they
are not vacuous: the bank-instructions theory expected "Saturday 11 October 2026" for a Sunday, and the
ladder walk stepped an hour per pass and reported five attempts for a six-rung ladder because the fifth
wait is four hours. A third — the first version of the field theory — asserted bare values against the
HTML part and **survived M4**, because the order number and the amount are also in the title and the
preheader; it is now a labelled-pair assertion against the text part, which has no decoration.

## What is owed, and by whom

| # | Owed | To |
|---|---|---|
| 1 | **Nothing on either screen says an email was sent, and that is correct today but will not be tomorrow.** With no SMTP configured the shop must not claim it has emailed anybody, so the success page and the order page are unchanged and the three existing "no promised email" browser sweeps stay green. The day the credentials land, the confirmation page should say so — and whoever adds that sentence must gate it on the transport being configured, or it becomes the promise this project has already been burned by twice. | ADM-22 / whoever configures SMTP |
| 2 | **`ops.AdminAlerts` and the `EmailDeliveryFailed` alert (doc 11 §4, G-37, ✅ Resolved in doc 08 rev 5).** A message that reaches `Dead` should raise a Warning banner for Super Admins carrying the template code, the order number and the row id. The table does not exist, so the whole signal is one `Error` log line that carries exactly those three things and tells the reader to telephone the customer. The daily routine in doc 11 §6.1 step 6 cannot be followed until the table is built. | the task that builds `ops.AdminAlerts` |
| 3 | **Retention (doc 08 §8.4: "EmailOutbox — Bodies 30 days, rows 1 year").** Neither purge is built; the nightly `Retention` job does not exist. Both are a single set-based statement — `UPDATE … SET BodyHtml = NULL, BodyText = NULL WHERE SentAtUtc < @cutoff` and `DELETE TOP (4000) … WHERE CreatedAtUtc < @cutoff` — and `CreatedAtUtc` is not indexed, which that task should add. Until then bodies accumulate: at a few hundred orders a month, a few hundred megabytes a year. | the `Retention` job |
| 4 | **PDPA anonymisation now has one more table to clear**, and doc 08 §3.1 step 2 already names it: "ContactSubmissions **and EmailOutbox rows for the email**". Its acceptance test (R3) searches every table for the original email, phone and name, and these bodies carry all three. The routine is not built (P17). | P17 |
| 5 | **The `TransportOverrideProtected` dual-send** (doc 08 §3.8 N2): a `SensitiveSettingsChanged` email going out through both the new and the previous SMTP settings. The column is here and is NULLed on every terminal status; the dual-send needs `ops.AdminAlerts`, the `Email.Smtp.*` settings group and `Settings.Security`. **Not built, on instruction.** | the settings task |
| 6 | **`Email.Smtp.*` as settings rather than configuration** (doc 08 §3.8 under `Settings.Security`), with the sensitive-change alert behind the write. | ADM-22 |
| 7 | **The shopper still cannot see their tracking number anywhere but the email** — W3-04's journal item 5a, unchanged. The dispatch email is now the *only* route, which raises the stakes on item 2: a dead `OrderShipped` leaves a customer with "Shipped" and nothing to do about it. | ACC-02 |
| 8 | **`ops.JobLeases`** still does not exist, and `AutoDeliverShipped` still never reaches its first sweep on a host that restarts more than once a day (W3-04's item 6). `SendEmails` does not need the table — see above — and sweeps every thirty seconds, so it is unaffected. | Architect |

## Two things found in neighbours' code, neither fixed here

1. **`OrderExpiryStore` and `OrderCancellationStore` take `sales.Payments` and `inventory.StockLevels` in
   opposite orders.** Doc 08 §5.3's order is `sales.Orders` → `sales.Payments` → `inventory.StockLevels` →
   `sales.NumberSeries`; cancel follows it, expiry releases stock *before* updating payments. It cannot
   deadlock today — both claim the same order row first, and two different orders' payment rows do not
   conflict — so it is a documentation-versus-code discrepancy rather than a live fault. **Not changed**:
   reordering two statements inside the expiry transaction is a change to a money path that this task's
   tests do not cover.
2. **`AdminOrderReader`'s `BuildTrackingUrl` had no test of its own.** It is now
   `OrderTrackingLink.Build` in the Domain, with the dispatch email as a second caller and
   `Dispatching_an_order_queues_the_shipped_email_with_a_real_tracking_link` exercising the substitution
   and the placeholder's absence. The admin screen's behaviour is unchanged.

## Corrections to the brief

- Doc 11's job table and SMTP actor row are **lines 51 and 54**, not "§51" and "§54" — §5.1 is the order
  status reference.
- **G-14 is doc 11 §7, not doc 08 §7** (doc 08 §7 is indexing and query strategy), and the retention table
  is doc 08 **§8.4**.
- The brief's "**plus cancel**" is a scope increase, not a specified email: doc 11 §4.4 lists order
  cancelled among the events with no notification in v1. Built on instruction and flagged above.
- The FK-index collapsing warning does not apply to this table: it has no foreign key.
- `ops.JobLeases` does not exist — checked, and both existing jobs already say so.
