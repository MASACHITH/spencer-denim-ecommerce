# 2026-10-14 — W3-07, customers (ADM-15) and newsletter subscribers (ADM-21)

Branch `feature/W3-07-customers-newsletter`, from `origin/main` at `ea4156a`. Not pushed, no PR. The
settings third of W3-07 (ADM-22) merged earlier in #104; this is the other two thirds.

**There is no CI.** Every figure below is from a run on this machine, in this worktree.

---

## What was built

| Route | Permission | What |
|---|---|---|
| `GET /api/admin/customers` | `Customers.View` | Registered customers, newest first; one search box; `?districtId=`; paged server-side (25, max 100) |
| `GET /api/admin/customers/{id}` | `Customers.View` | Profile, live statistics, saved addresses (default first) |
| `GET /api/admin/customers/{id}/orders` | `Customers.View` | The account's orders, newest first, paged |
| `GET /api/admin/newsletter/subscribers` | `Newsletter.View` | Filter by status, consent date (Colombo days) and part of the address; paged |
| `POST /api/admin/newsletter/subscribers/export` | `Newsletter.Export` + antiforgery | CSV of **subscribed** addresses only; audited |

Admin screens: `/customers`, `/customers/:customerId`, `/marketing/newsletter`, linked from the dashboard.

Read only on the customer side: no route writes to `identity`, and nothing touches `identity.Roles`,
`RoleClaims` or `UserRoles` (Tharanga, #105). No migration.

**Not built, deliberately:** anonymise/delete (doc 11 P17, `Customers.Delete` — the detail screen would host
it; it has no such button, and a Vitest spec asserts there is none), editing a customer, a customer CSV
export (doc 11 P11's table names `Customers.Export / Reports.Export`, but doc 02's ADM-15 does not include
an export and `Customers.Export` does not exist in `Permissions.cs` — see open questions).

## The three questions, answered from the documents

1. **Who is a customer?** A registered account: `identity.Users` with `UserType = Customer`. Doc 08 §3.1
   tags `identity.Users` ADM-15 (line 283) and says the statistics are computed "from `sales.Orders` for the
   rows on screen (`IX_Orders_Customer_Placed`)" (line 309) — an index keyed on `CustomerUserId`, i.e. per
   account. Doc 11 P11 step 16 (line 1281) puts "addresses" on the detail, which only an account has. A guest
   is reached through the order search by email; anonymising a guest by email is P17's own action (lines
   1720, 1769 — on the Customers screen, behind `Customers.Delete`), not built here.
2. **What is "district"?** `identity.Users.DefaultDistrictId` — the customer's default delivery district.
   Doc 11 P11 step 16 lists "district" as a list column (line 1281); step 10 (line 1271) is where a
   customer sets "default delivery district"; doc 08 §3.1 (line 298) is the column. It is a list column and
   a filter. A saved address's district is shown on the detail. Doc 11 P16's "Customers by district"
   (delivery district of paid orders) is ADM-08, the dashboard — a different figure.
3. **Export contents and permission?** Doc 11 P15 step 4 (line 1581): "list, filter by status and date,
   export CSV for use in an external email tool. The export is audited." P15's table (lines 1606–1607):
   `Newsletter.View` for the list, `Newsletter.Export` for the export. Doc 11 P16 (line 1675): "Every CSV
   export is audited (`Export`)". The file is Email, Source, Consent given (Colombo time); no IP, no status.

## Decisions, and why

- **Lifetime spend is the sum of `GrandTotal` over paid orders** (`PaidAtUtc` set): doc 08 §8.1's
  gross-revenue rule applied to one customer. A refunded order still counts, because refunds are manual in
  v1 and there is no refund row to subtract. **Order count is every order on the account**, paid or not
  (§8.1 counts orders by placement). The detail screen says spend is paid orders.
- **The order history matches `CustomerUserId` only, never email.** A guest order under the same address
  joins the account only when D5 links it after confirmation. An unconfirmed customer is flagged on the list
  and the detail, because that is exactly why their guest orders are not shown.
- **The newsletter date filter is the consent date, in Colombo days.** Re-subscribing replaces
  `ConsentAtUtc` (P15 step 2), so it is the date that matters for a mailing list. Doc 08 §3.6's index is
  `(Status, CreatedAtUtc)`, which this list does not seek on the date. No index was added, because the table
  is small and narrow.
- **The export ignores status and exports subscribed rows only.** The reader enforces that in its own
  `WHERE` rather than trusting the caller. It is a POST with the antiforgery token because it writes an
  audit row.
- **The audit row** (`Export`, `EntityType = NewsletterSubscriber`) records the row count, the date range and
  *whether* a search was applied. **It never records the search text**, which may be an email address that
  doc 08 §3.1 step 6's anonymisation (scrubs by entity and user id) could never find. The row is committed
  **before** the file is returned, and if it cannot be written the export fails.
- **No IP address on the list or in the file.** It is consent evidence, purged at 90 days (P17).
- **Both lists cap pages at 100 rows**, so neither list can become an unaudited bulk export.

## CSV safety

`CsvDocument` (Application/Admin/Exports) quotes every cell and doubles quotes inside it (RFC 4180). A cell is
prefixed with `'` (OWASP) when its first character after any leading spaces is `=`, `+`, `-` or `@`, or when
it starts with a tab or CR. The file is UTF-8 **with a BOM**, with CRLF rows.

- Hostile test values, as real rows through the real endpoint: `=HYPERLINK("http://evil.example";"x")+…@example.com`,
  `+…@example.com`, `-…@example.com`, `=1+2…@example.com`. Each comes back as `"'=…"`, `"'+…"` or `"'-…"`.
- The cost is that an address that genuinely starts with `-` or `+` reaches the mail tool with a leading
  `'`. That is the standard trade, and it fails in the safe direction.
- **There is no subscriber name.** `marketing.NewsletterSubscribers` has no name column (doc 08 §3.6), so the
  only Sinhala or Tamil a subscriber supplies is an internationalised address. `නිමාලි…@example.lk` and
  `கவிதா…@example.lk` round-trip byte for byte, and the BOM is asserted as bytes. A leading `@` cannot reach
  the table, because `NewsletterEmail` refuses an address whose `@` comes first. A unit test shows the
  neutraliser handles one anyway.

## How "only that customer's orders" is proved

`AdminCustomerEndpointTests.The_order_history_holds_only_that_customer_s_orders` runs against SQL Server.
Every order in it is placed through checkout, and the paid ones are paid through the real approval route:

- the customer's own two orders (linked with D5's own `UPDATE … SET CustomerUserId`, one of them paid);
- decoy 1: **another customer's** paid order;
- decoy 2: a paid **guest order under the customer's own email**, left unlinked (the unconfirmed case);
- decoy 3: a paid guest order under `…@example.com.lk`, which **starts with** the customer's address.

The history, the detail's count and spend, and the list row's count and spend must match the customer's own
two orders and the one paid total. Mutation m01 (match by email as well) is killed by this test.

## Mutations

Every mutation was applied to a committed tree, run, then reverted with `git checkout` on that one file.

| # | Mutation | Killed by |
|---|---|---|
| m01 | History also matches the customer's email | `The_order_history_holds_only_that_customer_s_orders` |
| m02 | List spend counts unpaid orders | `A_list_row_carries_every_field_the_screen_reads`, the privacy test |
| m03 | Staff accounts listed as customers | `A_staff_account_is_not_on_the_list_and_its_id_answers_404` |
| m04 | Export drops the `Subscribed` filter | `The_export_holds_subscribed_addresses_only_whatever_the_status_asked_for` |
| m05 | Formula neutraliser returns false | 2 `CsvDocumentTests` (one is a 7-case theory) + 1 handler test (unit); `A_cell_that_would_be_a_formula_is_neutralised` |
| m06 | No BOM | 2 `CsvDocumentTests` + 1 handler test (unit); 4 export tests (integration) |
| m07 | Audit write removed | 2 handler tests (unit); `An_export_writes_one_audit_row_with_its_shape_and_no_search_text` |
| m08 | Audit row carries the search text | `An_export_writes_one_Export_audit_row_with_its_shape` (unit); the integration audit test |
| m09 | Export gated on `Newsletter.View` | `View_and_Export_are_separate_permissions` + 6 export tests |
| m10 | Customer list gated on `Orders.View` | 12 customer endpoint tests, including both permission tests |
| m11 | Date filter in UTC days | `The_date_range_is_Colombo_midnight_to_the_next_Colombo_midnight` (unit); `The_date_filter_is_by_Colombo_calendar_day_inclusive` |
| m12b | Statistics compare `COALESCE(CustomerUserId, 0)`: results unchanged, filtered index unusable | **only** the plan test (all 4 cases) |
| m13 | Page ceiling 1000 | `Paging_is_clamped` (unit); `Paging_inputs_are_clamped` |
| m15 | Name search ignores the last name | the search test, the Sinhala/Tamil test |
| m16 | Phone matched on all its digits | `Digits_and_separators_are_a_telephone_number_by_its_last_nine_digits` (unit); the search test |
| m17 | Export skips the antiforgery check | `An_export_without_the_antiforgery_token_is_refused_and_not_audited` |
| F1 | Client sends `status` on the export | Vitest `exports with the dates and search applied, the token, and no status` |
| F2 | Paging guard removed | Vitest `refuses a page move past the end rather than disabling the button` |
| F4 | Stub exports unsubscribed rows | Playwright `downloads a CSV of subscribed addresses that Excel can open safely` |
| F5 | Export re-entry guard removed | Vitest `refuses a second export while one is being prepared, without disabling the button` |
| F6 | Stub stops neutralising | Playwright `downloads a CSV of subscribed addresses that Excel can open safely` |

**Survivors:**

- **m12** (`(CustomerUserId ?? 0) == user.Id`) survived. The plan was unchanged; my best explanation is that
  EF simplifies the coalesce back to a plain equality, though I did not read the SQL to confirm it. m12b
  has the same intent in a form EF cannot simplify, and it was killed.
- **m18** (the export endpoint reads `?status=`) survived, and it is an equivalent mutant: the reader forces
  `Subscribed` in its own `WHERE` whatever the filter says. m04 proves that guard is load-bearing.
- **F3** (`queueMicrotask` instead of `afterNextRender` in the list's focus re-homing) survived the Vitest
  spec. On these screens every load passes through the loading state, which removes the focused control
  *before* the response arrives, so a microtask also finds it disconnected. That makes it equivalent here.
  The code uses `afterNextRender` anyway, per the project rule.

**One of my assertions was flaky, and the mutation runs found it.** The plan test first also asserted that
`PK_Orders` is never read. In three back-to-back runs of unchanged code it failed once (2 of its 4 cases), because the optimiser
sometimes costs one aggregate as a clustered read on test-sized tables. I removed that assertion. The one
that matters (`IX_Orders_Customer_Placed` is used, and is absent from `<UnmatchedIndexes>`) never failed. (The message of commit `e5ee7bd` says "2 runs in 5"; that count is wrong. It was 1 run in 3.)
Before the fix, the plan test showed up as a spurious extra "kill" in fourteen of the seventeen integration mutation runs (all except m01, m02 and m15). m12 was one of them: its first "kill" was this same assertion, and with the assertion removed m12 survives. The
kills listed in the table are the other tests.

## The plan test

`AdminCustomerQueryPlanTests` takes **the reader's own** `PageQuery(...)`, renders it with `ToQueryString` and
reads its `SHOWPLAN_XML`. It runs four cases: no search, a name, an email prefix and a phone number.
`IX_Orders_Customer_Placed` is filtered (`CustomerUserId IS NOT NULL`) and the statistics are correlated
subqueries, which is the shape where the filter could fail to match. This task added no index.

## Numbers (this machine, this worktree)

| Suite | Result |
|---|---|
| Unit, whole assembly | **2,408 / 2,408**: main's 2,353 plus this branch's 55 |
| Integration, whole assembly, unfiltered, alone | **Three runs.** (1) Branch at `e5ee7bd`: 1,709 / 1,723 in 26 min 54 s. My 37 all passed. The 14 failures were outside this branch's files: 13 in `EmailOutboxSendingTests` (`SqlException: You can only specify the READPAST lock in the READ COMMITTED or REPEATABLE READ isolation levels`) and `SaleCampaignPlanTests.The_window_shaped_read_seeks_the_window_index`, which W3-01's journal already records as order-dependent. Both classes pass when run together with this branch's classes (74 / 74). (2) **Base `ea4156a`** in a temporary worktree (since removed): **1,686 / 1,686**. (3) **Branch again**, with the same binaries plus only a comment change: **1,723 / 1,723** in 33 min 24 s. **The first run's failures are not attributed.** They did not recur, the base was clean once, and I have one bad run in two. My best hypothesis, which I have not proved, is a pooled connection reaching the outbox claim with a non-READ-COMMITTED isolation level (`CartStore` runs a `Serializable` transaction, and this branch's tests place carts and orders through it like several other suites do). The tester should watch for it |
| My new integration classes, filtered | 37 / 37 in the first run and again, with the plan test fixed, inside the 74 / 74 combined run. Before the fix, 1 of 3 back-to-back runs failed the flaky assertion described above |
| Vitest | shared 24, storefront 1,844, admin **566** (main's 540 + 26) — all pass |
| Playwright, whole suite, retries 0, ports 4600–4616 | **1,032 / 1,033** (main's 1,022 + this branch's 11). The one failure is `admin-catalog.e2e.ts` "reorders with the keyboard alone", a known flake (W3-03 and W3-04 journals). It passed 3 of 3 in a `--repeat-each=3` run of that file (129 / 129) |
| `ng build admin`, `ng build storefront`, `dotnet build` (analyzers as errors), eslint on new files | clean |

## Open questions for the owner / tester

1. **Customer export.** Doc 11 P11's permission table lists "Export customers | `Customers.Export` /
   `Reports.Export`" (line 1308). Doc 02's ADM-15 has no export, and `Customers.Export` is not a permission in
   `Permissions.cs`. It was not built. ADM-19's Customers report is the natural home for it.
2. **"Anonymise by email address" lives on the Customers screen** (doc 11 P17, line 1769). When P17 is
   built, that action goes on this screen, and so does the detail's Anonymise button.
3. **The unsubscribe link is not built** (P15 step 3, §7 G-25), so no row can become `Unsubscribed` through
   the application yet. The list and the export honour the status anyway, and the tests write such rows
   directly.
4. **Orphaned test database.** A `dotnet test` I stopped early (my filter syntax was wrong, so it was
   running the whole suite) left `SpencerDenim_Test_056c803f0b534d32b6af78e16f2c69fb` (created 20:02:46) on
   `(localdb)\MSSQLLocalDB`. My attempt to drop it was refused by the permission system. It is mine and safe
   to drop.

## Corrections to the brief

- **"A subscriber whose name … begins with `=`".** Subscribers have no name; doc 08 §3.6 has no such column.
  The email address is the only text a subscriber supplies.
- **"…or `@`".** An address cannot start with `@`, because `NewsletterEmail` refuses it. The neutraliser
  covers that case anyway.
- **"§9's permission grid".** Doc 08 §9 is *Seed data* (the default-role table). The permission catalogue
  is §3.1 (`RoleClaims`), and the per-screen permissions are doc 11 P11's and P15's tables. `Newsletter.View`
  and `Newsletter.Export` already existed, and Store Manager is seeded with both.
- **"Check what the table records for an unsubscribe".** It records `Status = 2` and `UnsubscribedAtUtc`.
  Nothing in the application can write either yet, because the unsubscribe link is unbuilt (G-25).
- **The `--filter-class` syntax** does not filter this project, which runs under VSTest. The working filter
  is `--filter "FullyQualifiedName~X"`, as the 2026-10-09 journal already notes.

