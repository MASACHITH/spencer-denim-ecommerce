# 2026-10-09 — W3-07: `ops.AdminAlerts`, the `EmailDeliveryFailed` alert and the banner

Branch `feature/W3-07-admin-alerts`, worktree `worktrees/sd-alerts`, cut from `origin/main` at `924ebfd`.
Scope: **doc 08 §3.8's `ops.AdminAlerts`, doc 11 §4's `EmailDeliveryFailed` alert (§7 G-37), R10's
visibility rule, and the banner with its acknowledge**.
**Not pushed, not a PR — the tester goes first.**

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

Doc 08 §3.8 calls `ops.AdminAlerts` "the dashboard banner", and **ADM-04's dashboard does not exist**: the
route at `/` is W0-09's scaffold placeholder — a heading, an API-status panel and a list of links — and
the client's progress page lists the dashboard as `planned`, 0 %.

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

For a dead **account** email — `PasswordReset`, `EmailConfirmation`, `Welcome` — there is no order, and
doc 11 §4 says so: those alerts "give staff the outbox row and nothing to resend from". The banner says
there is nothing to open and to ask the customer to request a new link, because the token in a message
that has been in the queue for five hours has usually expired.

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

## Four accessibility rules, each one a defect this project has shipped

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
| Unit | **1,925** | 1,818 | **1,980** | 55 |
| Integration | **1,292** | 1,292 | **1,345** | 53 |
| Vitest (shared / storefront / admin) | **24 / 1,417 / 465** | same | **24 / 1,417 / 490** | 25 |
| Playwright | **935 / 0** | same | **957 / 0** | 22 |

Both frontends were rebuilt before the browser run (`npm run build:storefront` **and**
`npm run build:admin` — `npm run build` is storefront only, and the admin e2e server serves `dist/admin`).
The browser suite ran with all seventeen `E2E_*_PORT` variables overridden into **4940–4956**, scanned free
first; nothing on 4432/4433, 4502, 4515, 4767 or 4880–4916 was touched, and no process this task did not
start was stopped.

One database was created and dropped: `SpencerDenim_W307_Plan`, for the `SET STATISTICS IO` measurements
above. Nothing else was dropped.

## Mutation testing

Every new test was proved to fail against a mutation of the code it covers. **49 mutations, 44 killed, 3
non-viable, and 2 survivors that were real holes and are now closed.**

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

### Three non-viable mutations, said plainly

- **M9b — `Guid.TryParse` over a null related id.** Unreachable: the `RelatedEntityType != "Order"` test
  short-circuits in front of it for every row that has no order.
- **M23c — `IsNullOrWhiteSpace` relaxed to `is null`.** Behaviourally identical *given the catch*:
  `JsonDocument.Parse("")` throws, the catch answers the same nulls. It is belt-and-braces over the catch,
  which is now the tested one.
- **M14 in the sweep.** Removing the requeue's `AttemptCount < MaxAttempts` is only viable against a caller
  that runs the requeue alone, because in the sweep the kill takes those rows first. That is exactly why
  `The_requeue_alone_does_not_put_an_exhausted_row_back_on_the_ladder` exists — the mutation survived
  until it did.

## The neighbour's code that had to change, and the one latent bug in it

`EmailOutboxStore.ReleaseExpiredLeasesAsync` was one statement with a `CASE`: requeue a stranded row, or
kill it if it had no attempt left. The behaviour was right and the shape was wrong — one statement can
only answer *how many* rows it touched, and doc 11 §4 requires every row that reaches `Dead` to raise an
alert naming *which* message it was. So a sender that died on a message's final attempt produced a dead row
nobody was told about, by the one path the failure handler cannot see.

It is now two statements, each with one job, both with their statuses as literals (DB28) and both proved
to seek `IX_EmailOutbox_Stuck`:

- `KillStrandedExhaustedAsync` → `Dead`, `OUTPUT inserted.Id`, `AND AttemptCount >= MaxAttempts`;
- `ReleaseExpiredLeasesAsync` → `Failed`, requeued immediately, `AND AttemptCount < MaxAttempts`.

The kill runs first in the sweep, which is what the `CASE` was for: a requeued exhausted row would be
reclaimed at once (its next attempt time is already past) and spend a seventh attempt on a six-rung ladder.

**The kill statement's text is a field on the store, not a literal inside the method**, and that is not
tidiness. It runs as a raw `DbCommand` — it has to, to read its own `OUTPUT` — and an EF
`DbCommandInterceptor` never sees a command EF did not create, so the trick `EmailOutboxPlanTests` uses to
capture the requeue's SQL cannot capture this one. The plan probe is built **from the field**, so the probe
and the statement cannot drift.

**One latent bug found and fixed in W3-06's code.** Neither raw `DbCommand` in `EmailOutboxStore` assigned
`command.Transaction`, so either would throw inside an ambient transaction —
`"BeginExecuteReader requires the command to have a transaction when the connection assigned to the command
is in a pending local transaction"`. Nothing in production opens one around them; the plan test that
captures SQL inside a rolled-back transaction is what found it. Both now take
`dbContext.Database.CurrentTransaction?.GetDbTransaction()`. `ClaimAsync` is W3-06's and was changed too,
in two lines, because the failure would be worse there: a raw command that silently escaped a transaction
would claim messages a rolled-back transaction had decided not to send.

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
| 8 | **The acknowledgement note has one writer and no reader.** The banner writes it and nothing displays it afterwards, because the only screen that reads acknowledged alerts would be the dashboard or an alert history, and neither exists. The column, the API field and the audit row's `hasNote` are all honest today; a reader is ADM-04's. | ADM-04 |

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
5. **The live-region and refusal defects are not W3-05's.** W3-07's brief calls them "yesterday's branch";
   `TASKS.md` has W3-05 as *Promo codes + sale campaigns*, TODO, and `docs/journal/` has no entry for a
   branch describing those two defects. My comments describe the defects without naming a branch.
6. **`EF`'s parameterised collections are not rendered through `OPENJSON`** in EF Core 10 for this query —
   one parameter per element. Measured above. This was my own wrong comment before it was anybody's
   correction, and it is recorded here because the brief asked for exactly this check.
7. **A `COUNT(*)` cannot be made to use `IX_AdminAlerts_Open`** by writing the predicate differently. The
   brief's rule — "put the statuses in the command text and prove the plan seeks" — is right for the
   outbox's `Status IN (0,3)` filter and does not transfer to an `IS NULL` filter with no key predicate.
   The response was to remove the query, not to work round it.

## One scope note the owner should see

**Doc 02's ADM-04 row does not mention a banner.** It reads "Dashboard: KPIs vs previous period · Revenue
& Orders trend (weekly/monthly, 12 months)" and nothing else (line 109). The banner's warrant is doc 08
§3.8 — which calls `ops.AdminAlerts` "ADM-04 dashboard banner" and describes it as "persistent dashboard
alerts that must be acknowledged" — together with doc 11 §4.3's table and §6.1 step 1's routine, which is
written round opening them. Nothing new is being built outside the baseline: the table and the alert are
doc 08's and doc 11's, and the banner is the surface they already specify. But ADM-04's own scope line
does not name it, and this task has now put it somewhere ADM-04 is not, so the owner may want doc 02's row
amended to say so rather than leaving the banner's home implicit.

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
- **An acknowledged alert disappears with nowhere to look it up.** The audit trail has it
  (`AlertAcknowledged` on `AdminAlert`/id, visible in ADM-03's audit log viewer), and the alert row itself
  is kept for a year, but no screen lists acknowledged alerts. A Super Admin who clears one by mistake has
  to read the audit log to find out which.
- **Ten alerts at a time.** A shop with sixty open alerts sees the ten newest and "more are waiting after
  these". They are cleared ten at a time across page loads. For the volume doc 11 anticipates — a handful
  a month — that is comfortable; it would not be if a later task raised one per order.
