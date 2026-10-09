# 2026-10-09 — W4-02a: `ops.AdminAlerts`, the `EmailDeliveryFailed` alert and the banner

Branch `feature/W3-07-admin-alerts`, worktree `worktrees/sd-alerts`, cut from `origin/main` at `924ebfd`.
Scope: **doc 08 §3.8's `ops.AdminAlerts`, doc 11 §4's `EmailDeliveryFailed` alert (§7 G-37), R10's
visibility rule, and the banner with its acknowledge**.
**Not pushed, not a PR.**

## The task ID: this is W4-02a, and the branch name is wrong on purpose

**This work was briefed as "W3-07" and that ID belongs to somebody else's live task.** `TASKS.md:77` has
**W3-07 = "Customers, newsletter subscribers, settings (ADM-15, ADM-21, ADM-22) — IN PROGRESS"**, and
`TASKS.md:85` has **W4-02 = "Dashboard widgets (ADM-04–08) — TODO"**. `ops.AdminAlerts` is doc 08 §3.8's
"ADM-04 dashboard banner", so ADM-04 — and therefore this — is **W4-02's**. It is **W4-02 work pulled
forward**: the alert exists because doc 11 §7 G-37 is a live hole in W3-06's shipped order emails, not
because the dashboard is being started.

The label is **`W4-02a`** everywhere in the source, the tests and this file (30 occurrences across 20
files). Three things are deliberately *not* renamed:

- **The git branch stays `feature/W3-07-admin-alerts`.** The owner is squash-merging it, so the branch
  name does not survive; renaming it would only break the worktree every tester and reviewer already has.
- **The early commit-message prefixes stay `W3-07`.** The first fifteen commits and all six of the
  tester's are already written and a squash-merge replaces the subject line anyway. Commits from the
  rename onwards say `W4-02a`, so `git log` shows the correction rather than hiding it.
- **Four files that mention the real W3-07 are untouched**: `TASKS.md:77`,
  `docs/journal/2026-10-05-w302-checkout.md`, `CatalogOptions.cs` and `AdminSeedHostedService.cs`. None of
  them is in this branch's diff and all four mean the customers/newsletter/settings task.

**Doc 02's ADM-04 row wants amending.** It reads "Dashboard: KPIs vs previous period · Revenue & Orders
trend (weekly/monthly, 12 months)" (line 109) and says nothing about a banner, while doc 08 §3.8 calls
`ops.AdminAlerts` "ADM-04 dashboard banner" and doc 11 §4.3 and §6.1 step 1 are written round opening
them. Nothing here is outside the baseline — the table and the alert are doc 08's and doc 11's — but the
feature row does not name the surface, and W4-02 will inherit a banner its own scope line does not
mention.

Closes the hole W3-06's own report filed against this task (item 2): a message that reached `Dead` was one
`Error` log line and nothing else, so a customer who never received their bank transfer instructions
simply waited and nobody was told. Doc 11 §7 G-37 has been marked **✅ Resolved** since doc 08 revision 5
against an alert that did not exist.

## What it does, end to end

`SendEmails` hands a message to the mail server for the sixth time and the server refuses it again. The
row goes to `Dead` — terminal, outside `IX_EmailOutbox_Due`'s filter, no sweep will see it again — and in
the same pass an `ops.AdminAlerts` row is written:

> **Email not delivered: BankTransferInstructions for order SD-2026-000123 (outbox row 4821)**

Every Super Admin sees it as a banner on **every admin screen** until one of them presses **I have seen
this**, which stamps the row with their name and the time, writes an `AlertAcknowledged` audit row in the
same transaction, and says so in a sentence that does not pretend anything has been fixed. Beside the
title the banner says what they can actually do about it today, and links to the order.

The second way a row reaches `Dead` — a sender that died holding its lease on a message's **final**
attempt — raises the same alert. It did not before: that transition was a `CASE` inside a statement that
could only report how many rows it touched.

## The decisions that matter, and why

### The banner is in the admin shell, not on the dashboard

Doc 08 §3.8 calls `ops.AdminAlerts` "the dashboard banner", and **ADM-04's dashboard is not built**: the
route at `/` exists and is W0-09's scaffold placeholder — a heading, an API-status panel and a list of
links, with its own docblock saying "Replaced by the full dashboard in a later milestone" — and
`client-progress/progress-data.js` line 109 lists Dashboard as `status: "planned", progress: 0`.

Putting the banner there would mean a dead email raises a banner a Super Admin sees only if they happen to
go back to the home screen. Doc 11 §6.1 step 1's whole morning routine *starts* from these banners
("open every banner"), and §6.1's escalation rule reads them for age ("an alert nobody has acknowledged
for more than 24 hours is escalated"). A banner on one screen cannot carry that.

So it is in the shell, above the router outlet, on every admin screen. That is **more** than doc 08 asks
for rather than less, and it needs no new route, no new permission and no sidebar entry.

**ADM-04 adopts this component rather than writing a second one.** `AlertBanner` takes no inputs: it
fetches, renders and acknowledges on its own, so the dashboard can render `<sda-alert-banner />` inside a
widget and the shell can drop it, or both can keep it, with no change to the component. What ADM-04 must
not do is build its own — two banners would be two wordings of "this does not fix anything", and that
sentence is the only thing standing between an acknowledgement and a silencing.

`app.spec.ts` asserts the placement (the banner is in the shell and above `#main`), and
`admin-alerts.e2e.ts` opens four different admin routes and finds it on each. Moving it back into one page
is exactly the change that would make it invisible on every other one, so it fails a test rather than
review.

### What the alert actually tells a Super Admin they can do

**There is no resend action anywhere in this build.** Doc 11 §6.1 step 6 says to "open the order it names
… and **resend from the order**", and that action is doc 11 §7 **G-42** — unspecified, owner Architect +
Client. I checked before writing the sentence: `grep -ri resend` over `src/` finds three hits and not one
of them is a route, a command or a control. The admin order screen has no resend button and the API has no
route for one.

So the banner does not mention resending. It says:

> Nothing will try to send this email again. Open the order, then telephone or WhatsApp the customer —
> this build has no way to resend an email, and a wrong address on a placed order cannot be corrected.

Every clause of that is something the shop can really do. The order detail screen carries the customer's
name, email and telephone number (`AdminOrderDetailDto.Phone`, rendered as a `tel:` link at
`order-detail-page.ts:510`), which is what "telephone them" needs. The last clause is doc 11 §4's own
exception: only the courier, the tracking number and the internal note are editable after placement, so a
resend would reach the same dead address anyway.

**When the order cannot be found, nothing is offered.** `DeadEmailAlerts.ReadOrderAsync` can resolve a
public id to no order row — `ops.EmailOutbox` has no foreign key to `sales.Orders` by design (doc 08 §3.8)
— and the title says so: "for an order that can no longer be found". **The first version then rendered
`Open order 4444…-…` directly beneath it, with advice to open the order and telephone the customer.** The
link 404s and there is nobody to telephone, because the customer's name and number live on the order. The
link now needs *both* the id and the number, and the advice sends the technical owner the two facts that
are true — the template code and the outbox row — because a message queued against an order that is not
there is a fault in the shop and not in the mail server. Orders are never deleted (doc 08 §1.6), so the
pair "id set, number null" is permanent and cannot resolve itself later.

**For a dead account email the advice is per template, because doc 11 §4 carves them out one by one.** The
first version said "ask the customer to request a new link themselves" for all of them, and that is wrong
twice:

| Template | What doc 11 §4 says to do |
|---|---|
| `StaffInvite` | **A Super Admin resets that user's password**, which sends a fresh set-password link (P14 step 7, doc 08 §3.1 `AdminPasswordReset`). The invited staff member cannot self-serve at all: `PasswordReset` is **customer-match only** (§4.1), so asking for one gets them nothing. |
| `PasswordReset`, `EmailConfirmation` | Ask the customer to request a fresh link. These are the two whose link carries a single-use token, and doc 11's "has usually expired by the time anyone notices" is about these two. |
| `Welcome`, `AccountExists` | **No link to re-issue at all**, so there is nothing to ask for. The first version's "has usually expired anyway" was over-general and applied doc 11's clause to messages it is not about. |

None of those five templates exists yet — all are W3-01's — and every template the six built order emails
use takes an order branch, so none of this is reachable today. It was committed wrong advice that would
have gone live the day accounts land, which is why it was worth fixing before the branch merged rather
than after.

**The template code is carried as a DTO field** (`templateCode`) rather than only inside the title,
because the advice has to branch on it and a sentence cannot be branched on. It is G-37's third fact, and
it was already in `DetailsJson` — the banner now gets it the same way it gets the order link.

**The sentence lives in the screen and not in the row.** `alertAdvice` in `admin-alert.model.ts` is one
function; the day G-42 lands, it changes once and every alert already in the table starts giving the new
advice. An alert row that said "resend it from the order" would still be saying it in a year.
`admin-alert.model.spec.ts` asserts the absence of the word as well as the presence of the advice, because
a later edit that helpfully added "or resend it from the order" would otherwise be silent.

### The payload is three facts, and the reason is PDPA

Doc 11 §7 G-37: the alert carries "the template code, the order number and the outbox row ID **only**, so
the anonymisation acceptance test in 08 §3.1 still passes". Doc 08 §3.1 step 8 searches **every table** —
`ops.AdminAlerts` named in it — for a customer's original email, telephone number and name, and doc 11 §4
records that nothing anonymises these rows. An email address copied into `Title` would be a PDPA defect
that no later routine could clean up, and it would stay invisible until somebody ran that acceptance test.

`DeadEmailFacts` has no field that could carry one, which is the real guard. On top of that:

- `DeadEmailAlertTests.Nothing_in_the_row_carries_an_email_address_a_name_or_a_telephone_number` joins
  every column of the row and searches it for the three values, including a bare `@`;
- `AdminAlertsBehaviourTests.No_column_of_the_alert_holds_the_customers_email_name_or_telephone_number`
  does the same against a real database, with a real order and a real outbox row, and first asserts that
  the outbox row beside it *does* hold them — so the search is not vacuous.

The acknowledgement note is the one free-text field, and it is **not** copied into the audit row: the
audit log is not scrubbed either, the note is already on the row the audit entry points at, and a second
copy is just another place for a telephone number somebody pasted to survive. `AfterJson` records
`hasNote` instead.

### The alert is about the message, not about the order

`RelatedEntityType` / `RelatedEntityId` are `EmailOutbox` / the row id, and the order is in `DetailsJson`
as `orderPublicId` + `orderNumber`.

Two messages go out at placement — "we have your order" and "how to pay" — and an SMTP outage kills both.
A Super Admin has to know that the bank details did not arrive **as well as** the confirmation, because
those are two different things to tell the customer. Keying the alert on the order would have collapsed
them into one. It is also what makes "has this message already been alerted about?" an exact question, and
`Two_dead_messages_for_one_order_raise_two_alerts` pins it.

The banner's order link is lifted out of the details by the read handler, so any later alert type whose
details name an order gets the link for free — `PaymentException` will.

### Visibility is R10, built as written and not widened

Doc 08 §3.8 R10 is one sentence and it is the security rule of this whole feature: GrantsAll holders see
and acknowledge every alert; `Orders.Approve` and `Orders.Refund` holders also see and acknowledge
`PaymentException` and `CaptureRecoveryFailed`. **For `EmailDeliveryFailed` that is Super Admins only**, and
doc 11 §7 G-37 says so in as many words — "the visibility rule in 08 §3.8 R10 gives no other role a path to
it" — with "whether the Store Manager should see it too" recorded as **still open with the Architect**.

It is not widened here. `AdminAlertVisibility` is a pure Domain function and
`AdminAlertVisibilityTests` pins it **type by type rather than by sampling**, because a rule one type too
wide would show a Store Manager the `SensitiveSettingsChanged` and `BreakGlassUsed` banners that the two
Super Admin accounts exist to keep separate. An unknown alert type — the column has no check constraint —
defaults **closed** for everybody but a GrantsAll holder.

**A real Store Manager is already one of R10's audiences, and the first version of that test file did not
know it.** Doc 08 §9's default-role grid gives a Store Manager every `Module.Action` except `Users.*` and
the two sensitive settings permissions, and it says so explicitly: "**Includes `Orders.Refund`**". So they
see `PaymentException` and `CaptureRecoveryFailed` today, and the question G-37 leaves open is only about
`EmailDeliveryFailed`. My fixture had left `Orders.Refund` out and was named *StoreManagerish*, which would
have proved "a Store Manager sees nothing" — both wrong and the opposite of the rule.
`A_store_manager_sees_the_payment_alerts_and_not_the_dead_email` now writes the real grid out and asserts
both halves, so G-37's decision cannot be taken by accident.

Three layers enforce it and each is tested where it lives:

| Layer | What it does | Test |
|---|---|---|
| Route policy | admits anyone R10 could name (`Orders.Approve`, `Orders.Refund`, GrantsAll through the bypass) | `A_staff_member_with_no_alert_audience_is_forbidden` |
| Read model | asks the reader for exactly the types this caller may see | `Order_staff_are_read_only_the_two_payment_types`, `A_reader_told_two_types_sees_only_those_two` |
| Acknowledge | the visible types go into the `UPDATE`'s `WHERE` | `An_alert_of_a_type_the_caller_may_not_see_cannot_be_acknowledged` |

The endpoint test is written round a **negative**, which is the only way a visibility rule is proved: an
`Orders.Approve` holder — who genuinely does have a path to the payment alerts — is shown the payment
alert and **not** the dead email, and cannot acknowledge it either. A wrong id and an id they may not see
answer the same bare 404, so the route cannot be walked to count the shop's security alerts.

There is no GrantsAll case at the HTTP layer, and that is deliberate: a GrantsAll holder cannot reach any
admin endpoint before enrolling in 2FA (W1-10), so signing one in means driving the whole enrolment flow,
which is `AdminTwoFactorEndpointTests`' subject. What a Super Admin sees is proved where the rule lives,
where it is applied, and against the real database.

### No acknowledgement note — owner's decision, 2026-10-09

**I shipped the first and only writer of `AcknowledgementNote`: a 500-character free-text box.** The owner
asked for it to come out and I agree. The column stays — doc 08 §3.8 defines it, the migration creates it,
and `AdminAlertsSchemaTests` still proves its width and its half of `CK_AdminAlerts_Acknowledged` — and the
*write* is gone.

The reasoning is one my own code had already written down and then not followed. `AdminAlertAcknowledger`'s
audit-trail remark argued against copying the note into `ops.AuditLogs` because that "adds a place for a
telephone number somebody pasted to survive" — and that applies with more force to the alert row itself:

- **doc 08 §3.1 step 2's anonymisation deletion list names `content.ContactSubmissions` and
  `ops.EmailOutbox` and not `ops.AdminAlerts`**, and doc 11 §4 says these rows are "**not** scrubbed by the
  anonymisation routine";
- **doc 08 §3.1 step 8's acceptance test searches this table** for the customer's email, telephone number
  and name — so a pasted number here is a PDPA failure with no routine that can clear it;
- **nothing read it back.** The journal already listed that as owed (item 8): the only screen that would
  show an acknowledged alert is ADM-04's or an alert history, and neither exists;
- my own test asserted the round trip with `"Rang the customer on 0771234567"`, which is exactly the
  sentence a Super Admin following doc 11 §6.1 step 6 would type.

So the whole of what it bought was a field nobody reads, in the one place in this feature where a staff
member could put a customer's telephone number somewhere permanent, six days before launch.

**The route refuses a note rather than accepting one and dropping it**, which is slightly more than
"remove the write" and is the point: a field a route takes and silently discards is the shape of promise
this project has shipped twice — the caller believes their sentence was kept. A `null` note is still
accepted, because that is what a client sending "no note" looks like on the wire. The Playwright stub
refuses it too, so a screen that started sending one fails in the browser suite rather than in production.

Two smaller consequences, both taken: `AcknowledgementTrail` lost its `HasNote` flag rather than reporting
`false` for ever, and `NoteTooLong` became `NoteNotAccepted`.

### Acknowledging is audited, and resolves nothing

One transaction: the conditional `UPDATE` and the `AlertAcknowledged` audit row commit together or neither
does. A banner that disappeared with no audit row behind it would be exactly the silencing doc 11 §6.1's
escalation rules name ("Nothing is ever resolved by silencing it").

Of two Super Admins pressing the button in the same second, exactly one acknowledgement and exactly one
audit row are recorded — and the second is told **when** the first cleared it rather than being refused,
because doc 08 §3.8 shows the banner "until *one of them* acknowledges it" and the second person's
intention is already satisfied. The screen says so; the API answers 200 with a flag.

`Acknowledging_leaves_the_dead_message_exactly_as_it_was` is the assertion that matters most: the outbox
row is untouched afterwards — not requeued, not reset, not deleted — so the message is still dead and the
customer still has to be telephoned.

## The schema

`20261009035138_OpsAdminAlerts` — doc 08 §3.8, column for column and width for width. Snapshot diff
against `origin/main` is **76 insertions, no deletions**; `has-pending-model-changes` clean after a
rebuild.

| Index | Source |
|---|---|
| `IX_AdminAlerts_Open (CreatedAtUtc DESC) WHERE AcknowledgedAtUtc IS NULL` | doc 08 §3.8, verbatim |
| `IX_AdminAlerts_AcknowledgedBy (AcknowledgedByUserId)` | **EF's foreign-key convention, named explicitly** |

The second is not in doc 08 and is not a design decision: EF creates an index for every foreign key, and
naming it is what stops the convention quietly collapsing it into a hand-written index on the same column
later (the brief's warning — it ignores filters when it decides whether the index it needs already
exists). These two are on different columns and cannot collide; both are named anyway so that is true by
inspection. `AcknowledgedByUserId` has a foreign key to `identity.Users` and `AuditLogId` has **none**,
both by doc 08, and `Only_the_acknowledging_user_is_a_foreign_key` asserts both halves — a key on
`AuditLogId` would eventually block the audit log's own three-year purge against an alert kept for one.

Three check constraints, each proved by violating it:

- `CK_AdminAlerts_Severity` (1–3), doc 08's;
- `CK_AdminAlerts_DetailsJson` (`ISJSON`), doc 08's;
- `CK_AdminAlerts_Acknowledged` — **not in doc 08**, and it pins the invariant the whole read path rests
  on. `IX_AdminAlerts_Open` is filtered on `AcknowledgedAtUtc IS NULL`, so "open" means exactly that one
  column; a row with an acknowledging user and no time would be invisible to the banner and look
  acknowledged in a report, with nothing to say which reader is right.

This is the opposite judgement from W3-06's rejected `AttemptCount <= MaxAttempts`, and for a stated
reason: that constraint had no reader, and this one has two that would otherwise disagree. Nothing writes
these rows inside a payment transaction either.

## Two measurements, and both changed the code

### The filtered index, and the count that is not there

The brief asked for a plan test, and the first version of it **could not fail**: on an empty table the
optimiser scans the clustered index because a scan of nothing is cheapest, so every assertion about
`IX_AdminAlerts_Open` was reading `PK_AdminAlerts` and passing. The suite now seeds 20,000 rows of the
width doc 08 allows — a 200-character title and a 600-character details document, because the choice
between a seek with twelve key lookups and a scan of the whole clustered index turns on how many pages
that scan is — in the shape §8.4 produces: acknowledged alerts kept a year, a handful open.

Measured on that table (`SET STATISTICS IO`, 20,000 rows, 12 of them open):

| Statement | Logical reads |
|---|---|
| the banner's list (`TOP (n) … ORDER BY CreatedAtUtc DESC, Id`) | **38** |
| the same count forced onto the index with `WITH (INDEX(IX_AdminAlerts_Open))` | **38** |
| a `COUNT(*)` over the same predicate, as the optimiser chooses it | **5,020** |

So **the banner no longer carries an exact total.** SQL Server costs that count as a clustered scan however
it is written — with the alert types in the predicate or without them, as `COUNT(*)` or `COUNT(Id)`, with
or without a key predicate on `CreatedAtUtc` — because the filtered index gives it no key to seek on and
the predicate is already implied by the filter. An index hint would have bought the number back and was
not taken: it forbids the optimiser from ever choosing better, and nothing else in this codebase does it.
Instead the reader asks for **one row more than the cap** and the extra row answers "are there more"; the
payload carries `moreThanShown` and the banner says "· more are waiting after these". What a staff member
loses is a precise figure they do not act on — doc 11 §6.1 step 6 tells them to open every alert, and this
says whether they have finished.

`A_count_over_the_same_predicate_does_not_seek_the_open_index` asserts the **scan**, which is the opposite
of every other test in that file and is the point: adding the exact total back is the obvious readable
change and it has to fail loudly rather than quietly cost five thousand reads on every admin page load.

The tie-break ascends inside the descending sort, because `(CreatedAtUtc DESC, Id ASC)` is the index's own
physical order and therefore needs no sort. **That is a shape choice and not a measured saving, and the
first version of the comment claimed otherwise.** I had turned the tie-break round in the same patch that
widened the probe rows, and then wrote that the plan test "went red that way" — but a mutation that
reverses only the tie-break leaves every plan test in that file green and is caught only by
`Alerts_raised_in_the_same_millisecond_are_ordered_by_id`. At twenty thousand rows a sort of ten is free.
The remark now says exactly that, because the reader ascending inside a descending sort reads like a
mistake and the next person needs the real reason.

### `EF.Constant`, and a comment that was wrong

The reader wraps the alert types in `EF.Constant` so they reach SQL Server as literals, and the remark
explaining it claimed that a captured collection becomes an `OPENJSON` join. **That was a guess and it was
wrong.** Measured by printing both query strings:

```
EF.Constant(types) → … AND [a].[AlertType] IN ('SensitiveSettingsChanged', 'PaymentException', …)
types.Contains(…)  → DECLARE @types1 varchar(40) = 'SensitiveSettingsChanged'; … @types7 …
```

One parameter per element, not `OPENJSON` — and the filtered index is matched either way, because the
predicate that has to imply its filter is `AcknowledgedAtUtc IS NULL` and the alert type is only a
residual. A mutation proved it: swapping `EF.Constant` out left every plan in the file identical.

It stays for the smaller, honest reason now written down: R10 produces exactly two lists, so as literals
they give two stable cached plans and as seven sniffed parameters they give one plan compiled for whichever
audience asked first. The test pins the literals as a deliberate choice and pins the seek separately,
because the two are not the same claim.

## Five accessibility rules, each one a defect this project has shipped

1. **An acknowledged alert is not torn out from under the focus.** It keeps its place with its outcome and
   goes on the next page load — the rule `bank-transfers-page.ts` follows for a decided row. Removing the
   item would destroy the node holding the focus, which is the same harm as disabling it (issue #109).
2. **Nothing is ever `disabled`.** `aria-disabled` plus a refusal in the handler; the button stays
   focusable throughout, and a second press while the first is in flight sends nothing (proved by
   `http.verify()` — a second antiforgery fetch would be an unexpected request).
3. **The confirmation is written where it survives.** The live region is the component's first child and
   outside every `@if`, so it exists before any alert loads and after the last one is cleared. A branch
   last week put a live region inside a block that success removed and a blind customer pressed Cancel and
   heard nothing at all.
4. **An identical announcement re-fires.** `role="status"` announces a *change*, so two alerts with the
   same title cleared one after the other would be read out once. Unlike the bank queue there is no
   "…ing" sentence in between to alternate with, so the trailing space is the guarantee here rather than a
   backstop, and the spec asserts it directly.
5. **The banner's title is not a heading**, and this one was a defect in my own first version. The banner
   renders above every admin page's `<h1>`, so the `<h2>` it had put a heading in front of the page saying
   what it was — which is exactly #76 and #20's SH11 ("a screen reader met `H3 Girls, H3 Boys, H3 Baby,
   H2 Menu, H1 <the page>`"), and `accessibility.e2e.ts` says in as many words why nothing caught it:
   "axe reports nothing, because nested heading order is a best-practice rule rather than a WCAG failure,
   which is exactly why it survived." The axe gate was green over it. It is now a `<p>` that names the
   `region` through `aria-labelledby` — a named landmark is how a screen-reader user finds a banner, so
   nothing is lost — and `admin-alerts.e2e.ts` asserts that file's own rule (the first heading in DOM
   order is the `h1`) on three admin screens. An `<h2>` there fails all three; the failure message prints
   `["H2 10 alerts need attention…","H1 Orders","H2 Show"]`.

Focus moves to the outcome line on `afterNextRender` and **not** `queueMicrotask`; the e2e suite is what
proves the difference, and it did (see the mutation table).

The banner is measured at all eight breakpoints for the three questions axe cannot answer — big enough, on
screen, not buried — with each selector guarded on its own, because a compound selector's guard is
satisfied by its first clause while a later one matches nothing (#80).

## Numbers

Every figure measured in this worktree. **Main's own figures were measured first, from
`spencer-denim-ecommerce/` at `924ebfd`, because the brief's unit figure was out of date.**

| Suite | Main (`924ebfd`) | Brief said | After | New |
|---|---|---|---|---|
| Unit | **1,925** | 1,818 | **1,981** | 56 |
| Integration | **1,292** | 1,292 | **1,354** | 62 |
| Vitest (shared / storefront / admin) | **24 / 1,417 / 465** | same | **24 / 1,417 / 490** | 25 |
| Playwright | **935 / 0** | same | **960 / 0** | 25 |

All four were run to completion on the final tree, in this worktree, after the last commit of code:
unit **1,981/0** (34 s), integration **1,354/0** (19 m 27 s), Vitest **24/1,417/490** all passing,
Playwright **960/0** (8.6 m). The integration suite's earlier run reported one failure — see below — and
this is the clean re-run after it was fixed.

Both frontends were rebuilt before the browser run (`npm run build:storefront` **and**
`npm run build:admin` — `npm run build` is storefront only, and the admin e2e server serves `dist/admin`).
The browser suite ran with all seventeen `E2E_*_PORT` variables overridden into **4940–4956**, scanned free
first; nothing on 4432/4433, 4502, 4515, 4767 or 4880–4916 was touched, and no process this task did not
start was stopped.

One database was created and dropped: `SpencerDenim_W307_Plan`, for the `SET STATISTICS IO` measurements
above. One `SpencerDenim_Test_*` was dropped as well, and it was mine: I had two integration runs in
flight at once (the second was started with `--no-build`, so a build failure did not stop it), killed the
newer one, and the fixture drops its database on dispose rather than on a kill. Nothing belonging to
`SpencerDenim_Dev`, `SpencerDenim_W113_*`, `SpencerDenim_W103_Tester` or anybody else's
`SpencerDenim_Test_*` was touched.

### One test was failing and the full run is what found it

`The_banners_read_sends_its_alert_types_as_literals` asserted `DoesNotContain("DECLARE @")` — "no
parameters" — and the query has one parameter it is *supposed* to have: `DECLARE @p int = 11` for
`SELECT TOP(@p)`, the cap plus the extra row `moreThanShown` is read from. The claim I wanted was "no
parameter **for the alert types**", so the assertion is now on that and the paging parameter is asserted
*positively* instead — it pins the one-row-more read in the SQL, which is invisible in the LINQ.

**And I got the parameter's name wrong in the fix**, which the tester caught: EF names a captured
collection's elements after the variable, so the text is `@alertTypes1 … @alertTypes7` and my
`DoesNotContain("@types")` could not have failed for the shape it was written for. It asserts
`@alertTypes` now. Two guards in a row on the same line, both inert — which is why the `OPENJSON` claim
underneath them survived three corrections.

**It had been red since I rewrote it**, and I had not noticed because the only run I made of it in between
was with a mutation applied, where it was *expected* to fail. A filtered re-run of the unmutated version
is the step I skipped. `M28` still kills it.

## Mutation testing

Every new test was proved to fail against a mutation of the code it covers.

**The count: 55 distinct mutations, 53 killed, 2 non-viable, 0 surviving.**

**The first version of this line said "50 mutations, 45 killed, 3 non-viable, 2 survivors" and every one
of those four numbers was wrong.** It is worth setting out how, because the mutation campaign is the main
evidence this branch offers for its own test quality and an inflated count costs more than it saves:

- **"50" was arithmetic, not a count.** 45 + 3 + 2 happens to make 50; the table and the narrative
  together document **49** distinct ids (M1–M44 plus the five lettered variants M4b, M9b, M23b, M23c and
  M33b). The owner's own re-count of 44 missed those five.
- **"45 killed" and "2 survivors" double-counted.** M16 and M42 survived their first run, the holes they
  exposed were closed, and they were then re-run and killed — so they were counted in both columns.
- **"3 non-viable" counted two.** M9b and M23c are genuinely unreachable. The third entry was "M14 in the
  sweep", which is not a mutation at all: it is the explanation of why M14 needed a test of its own, and
  M14 itself was killed once it had one.
- **Three more (M21, M23, M41) are not in the count either**, and should not be: their first form did not
  compile, so they were rewritten and the rewritten form is what ran.

The six follow-ups added **M45–M54**. The honest final state is therefore 55 ids attempted, 53 killed, and
M9b and M23c non-viable. Nothing survives.

| Mutation | Killed by |
|---|---|
| M1 R10's order-staff list gains `EmailDeliveryFailed` | `A_dead_email_is_visible_to_grantsAll_holders_and_to_nobody_else`, `Order_staff_see_the_two_payment_types_and_no_others` (×3), `The_rule_is_written_from_doc_08_R10s_two_lists` |
| M2 `CanSee` drops the type check | the same four, plus `An_unknown_alert_type_defaults_to_super_admins_only` |
| M3 `VisibleTypes` answers every type for everybody | `Everybody_else_sees_nothing`, `Order_staff_see_…` (×3), and three handler tests |
| M4 the title stops naming the outbox row | `The_title_names_the_template_the_order_number_and_the_row` |
| M4b the title is not clipped to the column | `A_title_that_would_overflow_the_column_is_clipped_rather_than_refused` |
| M5 the details omit `orderNumber` | `The_details_are_the_three_facts_and_a_valid_json_document` |
| M6 severity Critical instead of Warning | `The_row_is_a_warning_about_the_message_with_no_source_audit_row` |
| M7 the alert is keyed on the order instead of the message | the same |
| M8 the raiser stops checking the row is `Dead` | `A_message_that_is_not_dead_raises_nothing` (×5 statuses) |
| M9 the raiser stops checking for an existing alert | `Raising_the_same_message_twice_writes_one_alert` |
| M10 the sweep raises no alert on the last failure | `A_message_that_runs_out_of_ladder_raises_the_dead_email_alert`, `An_alert_that_cannot_be_raised_does_not_stop_the_sweep` |
| M11 the sweep raises one on every failure | `A_message_that_will_be_retried_raises_no_alert` |
| M12 the sweep raises none for a stranded kill | `A_stranded_message_on_its_final_attempt_raises_the_alert_too` |
| M13 the kill runs after the requeue | `The_kill_runs_before_the_requeue`, `Expired_leases_are_released_before_the_claim` |
| M14 the requeue drops `AttemptCount < MaxAttempts` | `The_requeue_alone_does_not_put_an_exhausted_row_back_on_the_ladder`, `The_recovery_statement_writes_its_statuses_as_literals` |
| M15 the acknowledger drops the R10 check | `An_alert_of_a_type_the_caller_may_not_see_cannot_be_acknowledged`, `An_order_staff_member_cannot_acknowledge_a_dead_email_alert` |
| M16 the `UPDATE` drops `AND AcknowledgedAtUtc IS NULL` | `A_second_acknowledgement_changes_nothing_and_writes_no_second_audit_row`, `Racing_acknowledgements_produce_one_winner_and_one_audit_row` |
| M17 the audit row is never committed | those two plus `Acknowledging_stamps_the_row_and_writes_the_audit_trail` and `Acknowledging_an_alert_they_may_see_records_it_with_its_audit_row` |
| M18 the staff note is copied into the audit row | three, incl. `Acknowledging_stamps_the_row_and_writes_the_audit_trail` |
| M19 the handler stops trimming the note | `Acknowledging_passes_the_visible_types_the_actor_and_the_trimmed_note`, `The_length_is_measured_after_trimming`, `A_blank_note_is_stored_as_null…` |
| M20 the handler clips an over-long note instead of refusing | `A_note_longer_than_the_column_is_refused` |
| M21 an acknowledgement with no actor is accepted | `An_acknowledgement_with_nobody_in_it_is_refused` |
| M22 the order public id is not validated as a GUID | `A_public_id_is_normalised_…`, and two cases of `Details_it_cannot_read_cost_the_link_and_nothing_else` |
| M23 the details reader stops catching `JsonException` | the two malformed-JSON cases of that theory **(added because this survived — see below)** |
| M23b the root `ValueKind` check is removed | the `[1,2,3]` and `"a string"` cases |
| M24 the reader drops the empty-visible-list guard | `An_empty_visible_list_returns_nothing` **(after it was given a command count — see below)** |
| M25 the reader takes `take` instead of `take + 1` | `The_cap_bounds_the_list_and_the_extra_row_only_answers_whether_there_are_more` (×2) |
| M26 the reader returns the extra probe row | the same (×2) |
| M27 the reader drops `AcknowledgedAtUtc == null` | `The_banner_reads_open_alerts_newest_first` |
| M28 `EF.Constant` replaced by a captured collection | `The_banners_read_sends_its_alert_types_as_literals` **(after both the test and the query under it were fixed — see below)** |
| M29 the tie-break descends | `Alerts_raised_in_the_same_millisecond_are_ordered_by_id` |
| M30 the kill statement's status becomes a parameter | `The_kill_statements_shape_seeks_the_stuck_index`, `The_kill_statement_writes_its_statuses_as_literals_and_outputs_the_ids` |
| M31 the kill statement drops its `OUTPUT` clause | those two plus `A_stranded_row_with_no_attempt_left_is_killed_rather_than_requeued` and `A_stranded_row_is_reported_to_exactly_one_sender` |
| M32 `IX_AdminAlerts_Open` loses its filter | `The_banners_index_is_the_one_doc_08_names` |
| M33 `CK_AdminAlerts_Severity` widened to 0–255 | `A_severity_outside_doc_08s_three_is_refused` (×3) |
| M33b `CK_AdminAlerts_Acknowledged` made vacuous | `A_half_acknowledged_row_is_refused` |
| M34 the advice says "or resend it from the order" | `sends a Super Admin to the order and the telephone, and never to a resend button`, and the banner spec's first test |
| M35 the acknowledged alert is removed from the list | `keeps the acknowledged alert on screen with its outcome, and moves focus to it` + two more |
| M36 `[disabled]` instead of `aria-disabled` | `never puts 'disabled' on a button, and refuses a second press in the handler` + four more |
| M37 `announce` sets the region without the trailing space | `re-fires an identical announcement` |
| M38 the live region moves inside the `@if` | `writes the confirmation into a live region that the acknowledgement cannot destroy`, `app.spec`'s shell test, and six more |
| M39 the banner is removed from the shell | `puts the alert banner in the shell, above the outlet, on every admin screen` |
| M40 `queueMicrotask` instead of `afterNextRender` | **only** the browser test: `keeps the alert on screen, moves focus to the outcome, and announces it` (`toBeFocused` failed). The Vitest suite passed — `whenStable()` lets a microtask find the node. This is the brief's own warning, reproduced. |
| M41 `orderNumber` dropped from the TypeScript model | `The_alert_payloads_properties_are_exactly_what_the_screen_declares` |
| M42 `orderNumber` dropped from the Playwright stub | `The_playwright_stub_answers_with_every_field_the_api_sends` **(after the test was narrowed — see below)** |
| M43 the alert type the banner branches on is renamed | `The_alert_type_the_banner_branches_on_is_the_one_the_server_writes` |
| M44 the banner's title becomes an `<h2>` again | all three `still says what the page is before anything else` tests, in the browser only — axe is green over it |

### The two survivors, and what they cost

Both were real, both are closed, and both are the shape the brief warned about.

- **M16 — the acknowledging `UPDATE`'s `AND AcknowledgedAtUtc IS NULL` could be deleted with no test going
  red.** The acknowledger had an "is it already acknowledged?" branch in front of the statement, so the
  sequential tests never reached the statement with an acknowledged row; and the four-way race test did not
  reliably interleave its reads — under ReadCommitted the first writer's row lock serialised the others'
  *reads*, so each of them short-circuited on the branch instead of racing. The branch is gone. The
  conditional `UPDATE` is now the only place openness is decided (doc 08 §5.2's own argument), the ordinary
  second acknowledgement goes through it, and the clause is load-bearing in a test that does not depend on
  timing.

- **M42 — a field dropped from the Playwright stub left the shape test green**, because the stub's
  bank-transfer fixture also has an `orderNumber:` and the assertion searched the whole file. That is
  issue #80's shape — a guard satisfied by a clause other than the one being checked — in the one test
  whose entire job is issue #126. It now searches a bounded window round each payload's own literal.

Two more repairs came out of mutations that *did* kill, but only after the test under them was fixed:

- **M28 showed that the plan tests rebuilt the banner's query instead of calling it**, so they were
  measuring SQL the test file had written. `AdminAlertReader.OpenPage` is `internal` and the tests read it.
  The same run is what exposed the wrong `OPENJSON` comment above.
- **M23 showed that no test fed malformed JSON**, so the `catch (JsonException)` in the details reader was
  untested — every case in the theory was *valid* JSON of an unwanted shape, which the `ValueKind` and
  `TryGetProperty` checks handle without the catch running. Two malformed cases added. They are
  unreachable from this database (`CK_AdminAlerts_DetailsJson` forbids them) and they are still worth
  having: `ISJSON` and `System.Text.Json` are two parsers over one column and need not agree about every
  input, and a banner that 500s on one row hides every alert in the shop.

### The two non-viable mutations, said plainly

- **M9b — `Guid.TryParse` over a null related id.** Unreachable: the `RelatedEntityType != "Order"` test
  short-circuits in front of it for every row that has no order.
- **M23c — `IsNullOrWhiteSpace` relaxed to `is null`.** Behaviourally identical *given the catch*:
  `JsonDocument.Parse("")` throws, the catch answers the same nulls. It is belt-and-braces over the catch,
  which is now the tested one.

**M14 was in this list and does not belong in it.** Removing the requeue's `AttemptCount < MaxAttempts` is
only viable against a caller that runs the requeue alone, because in the sweep the kill takes those rows
first — which is why `The_requeue_alone_does_not_put_an_exhausted_row_back_on_the_ladder` exists. The
mutation survived until that test did, and then it was killed. It is a killed mutation with a story, not a
non-viable one, and counting it as non-viable inflated the campaign by one.

### The six follow-ups' mutations

| Mutation | Killed by |
|---|---|
| M45 `orderNumber` dropped from the stub's alert fixture **with six lines of prose added** — the mutation the 1,200-character window would eventually have let through | `The_playwright_stub_answers_with_every_field_the_api_sends` (the brace match has no margin to run out) |
| M46 `templateCode` dropped from the stub's alert fixture | the same |
| M47 the banner links on the order id alone, with no number | `offers no link, and no telephone call, when the order is not in the database` |
| M48 the order-gone case falls through to "open the order, then telephone" | `does not send anybody to an order that is not there`, the seven-sentence sweep, and the banner spec |
| M49 a dead `StaffInvite` gets the token-bearing advice | `sends a dead StaffInvite to a Super Admin, not to the invited user`, `sends a dead staff invitation to a Super Admin`, and the sweep |
| M50 `Welcome` / `AccountExists` get the "has usually expired" advice | `does not claim an expired link for the two that carry no link` and the sweep |
| M51 the route accepts a note again and drops it | `An_acknowledgement_note_is_refused_by_name_and_nothing_is_acknowledged` |
| M52 the command grows somewhere to put a note back | `The_acknowledge_command_has_no_note_to_carry` |
| M53 the template code is no longer lifted out of the details | three handler tests, including `An_unreadable_order_id_costs_the_order_and_not_the_template_code` |
| M54 the audit trail grows its `HasNote` flag back | `Acknowledging_stamps_the_row_and_writes_the_audit_trail` |

**M42 was re-run against the brace match and still dies**, which is what makes M45 meaningful rather than
a second copy of it: the window and the brace match both catch the plain mutation, and only the brace
match catches it once the file grows.

## The neighbour's code that had to change, and the bug I reported in it that was not one

`EmailOutboxStore.ReleaseExpiredLeasesAsync` was one statement with a `CASE`: requeue a stranded row, or
kill it if it had no attempt left. The behaviour was right and the shape was wrong — one statement can
only answer *how many* rows it touched, and doc 11 §4 requires every row that reaches `Dead` to raise an
alert naming *which* message it was. So a sender that died on a message's final attempt produced a dead row
nobody was told about, by the one path the failure handler cannot see.

It is now two statements, each with one job, both with their statuses as literals (DB28) and both proved
to seek `IX_EmailOutbox_Stuck`:

- `KillStrandedExhaustedAsync` → `Dead`, `OUTPUT inserted.Id`, `AND AttemptCount >= MaxAttempts`;
- `ReleaseExpiredLeasesAsync` → `Failed`, requeued immediately, `AND AttemptCount < MaxAttempts`.

The kill runs first in the sweep, and **the guard is the requeue's own `AND AttemptCount < MaxAttempts`,
not the ordering.** The first version of this paragraph — and the comment in `SendEmailsService.SweepAsync`
— said the ordering was what stopped an exhausted row being requeued and reclaimed for a seventh attempt.
The tester reversed the two calls and nothing changed, which is right: the clause is what holds it, and a
comment crediting the ordering is how somebody deletes the clause. The ordering is still the tidier shape —
a row the kill has taken is not a row the requeue has to consider — and it is no longer load-bearing.

**The kill statement's text is a field on the store, not a literal inside the method**, and that is not
tidiness. It runs as a raw `DbCommand` — it has to, to read its own `OUTPUT` — and an EF
`DbCommandInterceptor` never sees a command EF did not create, so the trick `EmailOutboxPlanTests` uses to
capture the requeue's SQL cannot capture this one. The plan probe is built **from the field**, so the probe
and the statement cannot drift.

**The "latent bug" I reported in W3-06's code was not one, and the diagnosis was wrong.** I added
`command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()` to both of
`EmailOutboxStore`'s raw commands — one of them `ClaimAsync`, W3-06's merged code, which decides which
emails get sent — on the reasoning that either "throws inside an ambient transaction", and wrote no test
for it. **I should not have touched `ClaimAsync` on an untested diagnosis.**

The tester wrote the case and it says the opposite. Both methods wrap their raw command in
`Database.CreateExecutionStrategy()`, and doc 08 §10.3 puts `EnableRetryOnFailure` on the context, so that
strategy is `SqlServerRetryingExecutionStrategy` — which refuses a user-initiated transaction in
`ExecuteAsync`, **before the command is built**. `CurrentTransaction` is therefore necessarily null
wherever my assignment runs: both assignments are no-ops, and the behaviour inside a caller's transaction
is unchanged. Measured by the tester: with both deleted, the 120 EmailOutbox / AdminAlert / OrderEmail
integration tests stay green.

The assignments and the two new tests stay. The refusal is the safe behaviour — enlisting would let a
claim be rolled back after the caller had been handed the rows and sent the mail, which is the duplicate
send `EmailOutboxStore` exists to prevent — and the tests pin it, so if a later change makes either
statement reachable inside a transaction they go red. What changed is the claim: this was a no-op added on
a wrong diagnosis, not a bug found.

## The alert is not in the same transaction as the statement that killed the row

Said plainly, because it is a trade and not an oversight. `IDeadEmailAlerts.RaiseAsync` runs after the
`Dead` transition has committed. The two statements run on one connection microseconds apart; the only
failure that separates them is the database becoming unreachable between them, and in that case
`SendEmails` still writes the `Error` line it wrote before this alert existed — so the signal degrades to
the previous build's behaviour rather than to silence, and a second `Error` line says the alert could not
be written and that nothing will raise it later.

The alternative is an explicit transaction wrapped round the one set-based statement W3-06's tester proved
over 900 contention runs, in both of the two paths to `Dead`, one of which would need the alert's title
built in SQL. That is a larger risk than the window it closes. The raiser is idempotent per row instead, so
the pair cannot duplicate a banner, and it reads the row's status back rather than trusting its caller, so
it cannot raise one for a message that is still being retried.

## What is owed, and by whom

| # | Owed | To |
|---|---|---|
| 1 | **Retention: acknowledged alerts are kept 1 year** (doc 08 §8.4: "Acknowledged: 1 year; unacknowledged: kept"). Not built — the nightly `Retention` job still does not exist, and it now owes this table a `DELETE` as well as the outbox's two purges. It is one set-based statement, `DELETE TOP (4000) FROM ops.AdminAlerts WHERE AcknowledgedAtUtc < @cutoff`, and `AcknowledgedAtUtc` is **not indexed** for it — whoever builds the job should measure before adding one, because the table is small and the predicate is the filtered index's complement. | the `Retention` job |
| 2 | **The six other alert types have no producer.** `SensitiveSettingsChanged` (the settings task, doc 08 §3.8 DB2 — the alert row goes in with the setting change, which is what `IAdminAlertWriter.Enlist` is for), `PaymentException` and `CaptureRecoveryFailed` and `DashboardRefund` (PayPal, doc 08 §4.2–4.3), `ReconciliationDrift` (doc 08 §5.9), `BreakGlassUsed` (the recovery CLI). The table, the read path and `Enlist` are ready for all six and `Enlist` is tested rather than promised; the banner shows any of them with its title, and `alertAdvice` gives them a generic sentence until each task writes its own. | the settings, PayPal and reconciliation tasks |
| 3 | **G-42: the per-order "Resend email" action, and the outbox screen.** Still unspecified (Architect + Client). Until it lands the banner tells a Super Admin to telephone the customer, and doc 11 §6.1 step 6's own wording ("resend from the order") describes something that does not exist. One function changes when it does. | Architect + Client |
| 4 | **G-37's open half: whether a Store Manager should see `EmailDeliveryFailed`.** Built as doc 08 §3.8 R10 is written — Super Admins only — and not widened. A decision here is a doc 08 change plus one list in `AdminAlertVisibility`. | Architect |
| 5 | **Nothing emails anybody about an alert.** Doc 11 §4.2 pairs **five** of the seven types with a staff email — `SensitiveSettingsChanged`, `BreakGlassUsed`, `PaymentException`, `CaptureRecoveryFailed`, `ReconciliationDrift` — and `EmailDeliveryFailed` and `DashboardRefund` with none. Doc 11 does not say why `EmailDeliveryFailed` has no email; **that is my inference**, and it is that §6.1's escalation rule points the same way ("phone or WhatsApp, not email — a mail problem may be the alert"). Either way, no email is built here, and the five that are specified belong to their own tasks. | the settings, PayPal and reconciliation tasks |
| 6 | **The banner does not poll.** It reloads on construction and on every navigation. An admin panel that polls every thirty seconds is a change to how often every staff browser talks to the API and doc 02 asks for no such thing; a dead email is acted on in the morning routine. If ADM-04 wants a live count, that is the place to decide it. | ADM-04 |
| 7 | **`ops.JobLeases` still does not exist** (doc 08 §3.8, in no migration). Unchanged by this task: `SendEmails` needs no lease, for the reason W3-06 recorded. | Architect |
| 8 | **The acknowledgement note has no writer at all** (owner's decision, 2026-10-09 — see above). Doc 08 §3.8's column and its check constraint are created and tested; nothing fills them. **It comes back on either of two conditions**, and the first is the one that matters: the PDPA anonymisation routine covering `ops.AdminAlerts` (doc 08 §3.1 step 2's deletion list naming it, which doc 11 §4 currently says it does not), **or** a screen that reads acknowledged alerts, which would make the note worth something to somebody. Until one of those, a note is customer data in a table nothing can clean, read by nobody. | P17 (anonymisation) + ADM-04 |

## What the tester found, and what it cost

The tester passed the schema, R10 and the PDPA payload as correct, and found six things. All six were
mine. They are listed here rather than left in the commit log because four of them are the same kind of
defect this branch's own journal was already claiming to have fixed.

1. **The PDPA test could not see a telephone number — the one value the whole payload design exists to
   keep out.** `No_column_of_the_alert_holds_the_customers_email_name_or_telephone_number` covered the
   email and the name and covered the number not at all, for two reasons that happened to cancel:
   `SqlQuery<int>($"… LIKE '%{order.Phone}%'")` parameterises *inside* the string literal, so the server
   received `LIKE '%@p0%'` — a search for the text "@p0" — and `order.Phone` was null anyway, because
   `SalesRowWriter` sets `Ship_Phone` and leaves `Orders.Phone` unset, so a correctly parameterised
   version would have read `LIKE '%%'` and matched everything. **No leak exists; the finding is that this
   test could not have told anybody.** It now plants a number in a title and proves the search finds it
   before concluding that it found none.
2. **The `OPENJSON` claim survived its own correction three times.** This journal recorded fixing one
   comment; there were four, and the other three still stated it as fact — including the class summary of
   the very file whose first test exists to settle it. **And the guard I wrote to replace it never
   fired**: the comment said EF names the elements `@types1 … @types7` "(measured)", and EF names them
   after the variable, so the real text is `@alertTypes1` and `DoesNotContain("@types")` could not fail
   for the shape it was written for. My claim that two independent guards were needed "because either one
   alone would go green" was wrong — only the literals assertion was working.
3. **The sign-in page really does fetch alerts.** `load()` guarded on `router.url.startsWith('/login')`
   and the comment said "skipped rather than made and swallowed". The component is built during bootstrap,
   before the first navigation resolves, so `router.url` is `'/'` the first time — and the first time is
   the only one that matters on that page. The guard now reads the document's path too, and the e2e
   asserts the absence of the *request* rather than of the banner.
4. **The OpenAPI description still advertised the total I removed on a measurement** — in the one place a
   client reads.
5. **Four more comments that say something the code does not**, including "Four accessibility rules" above
   a list of five, and the sweep's ordering claim (see above).
6. **The heading rule was asserted on three admin routes and the banner renders above all sixteen.**

## Corrections to the brief

1. **Main's unit count is 1,925, not 1,818.** Measured from `spencer-denim-ecommerce/` at `924ebfd`
   before any of this work. Integration (1,292), Vitest (24 / 1,417 / 465) and Playwright (935 / 0) were
   all correct.
2. **"the producers of the other five alert types" lists six** — `SensitiveSettingsChanged`,
   `PaymentException`, `CaptureRecoveryFailed`, `ReconciliationDrift`, `BreakGlassUsed`,
   `DashboardRefund`. Doc 08 §3.8's `AlertType` list has seven values in all.
3. **The dashboard route does exist**, as W0-09's scaffold placeholder at `/`. The brief's point stands —
   ADM-04 is unstarted and the placeholder is not a dashboard — but "the dashboard **does not exist**" is
   not literally true of the route, and the banner had to be kept out of that component rather than merely
   not added to a missing one.
4. **Issue #109's instance count is unclear.** The brief says seven; three comments already in the
   repository say six. I have not read the issue, so my comments cite `#109` with no number and say why.
5. **The live-region and refusal defects are not W3-05's.** W4-02a's brief calls them "yesterday's branch";
   `TASKS.md` has W3-05 as *Promo codes + sale campaigns*, TODO, and `docs/journal/` has no entry for a
   branch describing those two defects. My comments describe the defects without naming a branch.
6. **`EF`'s parameterised collections are not rendered through `OPENJSON`** in EF Core 10 for this query —
   one parameter per element. Measured above. This was my own wrong comment before it was anybody's
   correction, and it is recorded here because the brief asked for exactly this check.
7. **A `COUNT(*)` cannot be made to use `IX_AdminAlerts_Open`** by writing the predicate differently. The
   brief's rule — "put the statuses in the command text and prove the plan seeks" — is right for the
   outbox's `Status IN (0,3)` filter and does not transfer to an `IS NULL` filter with no key predicate.
   The response was to remove the query, not to work round it.

## The shape test's window was a hole with 173 characters left in it

`AssertStubBlockHas` searched **1,200 characters** from its anchor, which is how M42 was killed after the
first version searched the whole file. The owner measured the margin: **173 characters** before the next
`orderNumber:` in `alertsFor`. Two lines of added prose in the stub and the window would have reached a
neighbour's field again, silently re-opening exactly the defect the narrowing was for — and the test would
have stayed green while doing it.

It is a **balanced brace match** now, from a named opener (`return {`, `json(response, 200, {`) rather than
from the first brace after the anchor, because `deadEmailAlert(alertId, overrides = {})` has an empty
object in its own parameter list. `${…}` inside the fixture's template literals balances like any other
pair. A second test, `The_playwright_stubs_braces_balance`, checks the whole file first — without it a
stray brace would make every literal run to the end of the file and every field assertion pass, which is
the brace match's own version of the failure the budget had.

**Measured**: M45 is M42 with six lines of prose added, and it dies. The character budget would not have
caught it.

## Anything a client or a shopper could still hit

- **A dead email still means a customer was not told by the shop.** The alert tells a Super Admin to
  telephone them; whether that happens is a human in Colombo at 10:00, and doc 11 §6.1's escalation rule
  is the only thing behind it. Nothing automatic reaches the customer, because nothing can: the address on
  the row is the one that failed.
- **No SMTP is configured, so nothing can die yet.** `SendEmails` leaves the queue untouched on a host with
  no mail credentials (W3-06), so no row reaches `Dead` and no alert is raised until the client's
  credentials arrive. The day they do, a bad address starts producing banners 5 h 21 min later. **That is
  the first day this feature does anything**, and it is worth saying to the client rather than discovering.
- **A Store Manager cannot see a dead email.** Built as doc 08 §3.8 R10 says, and G-37 records the question
  as open. If the shop runs with one Super Admin on leave, a dead email waits for them — doc 11 §6.1's
  escalation covers it by telephone, and the second Super Admin account exists for exactly this.
- **An order an email names that is not in the database leaves nobody to contact.** The banner now says so
  instead of offering a link that 404s, and tells the technical owner — but the shopper in that case has no
  record in the shop at all, so there is nobody for a Super Admin to ring. It should be unreachable
  (`ops.EmailOutbox` has no foreign key to `sales.Orders` by design, and orders are never deleted), and
  the handling exists because "should be unreachable" is not the same as "is".
- **An acknowledged alert disappears with nowhere to look it up.** The audit trail has it
  (`AlertAcknowledged` on `AdminAlert`/id, visible in ADM-03's audit log viewer), and the alert row itself
  is kept for a year, but no screen lists acknowledged alerts. A Super Admin who clears one by mistake has
  to read the audit log to find out which.
- **Ten alerts at a time.** A shop with sixty open alerts sees the ten newest and "more are waiting after
  these". They are cleared ten at a time across page loads. For the volume doc 11 anticipates — a handful
  a month — that is comfortable; it would not be if a later task raised one per order.
