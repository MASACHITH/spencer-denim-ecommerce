# 2026-10-15 — W4-02, the dashboard (ADM-04, -05, -07, -08) and the reports (ADM-19)

Branch `feature/W4-02-dashboard-reports`, worktree `worktrees/sd-dashboard`, cut from `origin/main` at
`f644ade`. Not pushed, no PR. `TASKS.md` has ADM-19 under W4-03; folding the non-traffic half of it in here
is the owner's choice.

**There is no CI.** Every figure below is from a run on this machine, in this worktree.

**Not built, deliberately: traffic.** ADM-06 (Traffic Sources), ADM-20 (the Traffic page) and the
Conversion rate KPI all need doc 08 §3.8's `analytics` tables and a page-view pipeline, which do not exist.
Nothing stands in for them — no empty chart, no zero, no "coming soon" card. A Vitest spec and a Playwright
test assert the words *traffic*, *conversion* and *visits* appear nowhere on the dashboard.

---

## What was built

| Route | Permission | What |
|---|---|---|
| `GET /api/admin/dashboard?from=&to=` | `Dashboard.View` | KPIs vs the previous period, sales by department, top 10 categories, top 10 products (each vs the previous period), order status, payment methods, buyers by delivery district |
| `GET /api/admin/dashboard/trend?interval=Week\|Month&to=` | `Dashboard.View` | Revenue & orders: the 12 months, or the 52 ISO weeks, ending on `to` |
| `GET /api/admin/dashboard/low-stock` | `Dashboard.View` | Live: `0 < available ≤ threshold`, lowest first, ten rows and the total |
| `GET /api/admin/reports/sales?from=&to=&groupBy=Day\|Week\|Month` | `Reports.View` | One row per day/ISO week/month cut to the period, and a total |
| `GET /api/admin/reports/inventory?status=&search=&page=&pageSize=` | `Reports.View` | Every active variant's stock now, paged |
| `GET /api/admin/reports/customers?from=&to=&search=&page=&pageSize=` | `Reports.View` | Everyone who paid in the period, by email, guests included, highest spend first |
| `POST /api/admin/reports/{sales,inventory,customers}/export` | `Reports.Export` + antiforgery | The same as CSV through `CsvDocument`, audited before the file is sent |

Screens: `/` (the dashboard replaces W0-09's placeholder; its section links and Sign out are kept, Sign out
now `aria-disabled` rather than `[disabled]`) and `/reports?report=sales|inventory|customers`.

**Recent orders are not a new route.** The widget reads the existing admin order list
(`GET /api/admin/orders?sort=Newest&pageSize=10&search=`) and links to `/orders/{token}`. It is shown only to a
role holding `Orders.View`, the permission that list and the order screen both need.

**The alert banner is not rendered twice.** `AlertBanner` is in the shell, above every page including this
one, which is exactly what W4-02a built it for. The dashboard does not render a second copy.

No migration, no new index, no new permission (`Dashboard.View`, `Reports.View`, `Reports.Export` already
existed and are seeded: Store Manager has all three, Order Staff has `Dashboard.View` only). No isolation
level is set anywhere; every read is `AsNoTracking` at the connection's default.

## Revenue, and the document line that decided it

**Doc 08 §8.1 (line 1742): "Gross revenue: Σ `GrandTotal` of orders by `PaidAtUtc` (Colombo date)."** Doc 11
P16's KPI table (line 1644) adds "Includes orders later refunded". So, in `ReportingReader.PaidIn`:

- an order counts **for its whole `GrandTotal`, delivery included, on the Colombo day of its `PaidAtUtc`,
  whatever its status is now** — there is no status test at all;
- **refunded** orders keep their `PaidAtUtc` and count on the day they were paid;
- **cancelled, expired and pending** orders have no `PaidAtUtc` and never count — unless an expired or
  cancelled bank transfer is later accepted, which sets `PaidAtUtc` to the approval moment, so it counts on
  the approval day (doc 11 P16's first edge case, tested);
- **Orders** are counted by `PlacedAtUtc`, paid or not (§8.1 line 1743).

This is the same rule W3-07's customer list uses for lifetime spend, and the reconciliation test asserts the
two screens agree for one account.

**Net revenue = gross − completed settlement refunds by `CompletedAtUtc`** (§8.1 line 1742). A refund of a
payment exception (`IsOrderSettlement = 0`, the G-13 case) is not subtracted: it returns money that was never
revenue. **Today nothing in the application writes a `sales.Refunds` row** (`grep` finds no writer and no
transition to Refunded), so net equals gross in practice; the rule is built and tested with rows written
directly.

**Department, category and product figures are Σ `OrderLines.LineTotal`** (after promo, **without
delivery**) on paid orders, by the line's own department and category snapshot (§8.1 line 1744; P16 line
1650). They therefore **do not add up to gross revenue**, which P16 line 1672 says outright and the screen
says under the widget. Orders refunded later are still in them (P16's G-29 "state clearly on screen" —
done).

## The four-way (in fact six-way) reconciliation

`ReportingReconciliationTests.Revenue_for_one_period_is_the_same_on_every_screen`, Q1 2015: eight inserted
orders across Jan–Mar (two departments, a promo, a delivery discount, PayPal, a refunded order, an expired
one, a late approval placed in December, one at 23:59 Colombo on 31 March and one at 00:00 on 1 April) plus
**two orders through the real checkout, one approved through the real bank-transfer route**, then moved
into the quarter. It asserts, for the same period:

1. the KPI gross revenue = the arithmetic of the orders as written;
2. = Σ of the monthly trend's last three points;
3. = the sales report's total **grouped by day, by week and by month** (and Σ of each grouping's rows);
4. = Σ departments **+** the sales report's delivery column (and Σ departments = the report's items column);
5. = the customers report's total = Σ payment methods = Σ delivery districts;
6. and the W3-07 customer list's lifetime spend for one account = the customers report's figure for it.

Paid orders and orders placed are reconciled the same way (KPI = trend = report = Σ statuses = Σ methods).

Honest caveat: the trend and the sales report share one reader method (`ReadDailyAsync`) and one bucketing
function, so (2) and (3) agree partly by construction; what they test is the bucketing. The KPI is a separate
`SUM`, and departments come from the lines table.

## "vs previous period"

- **Same number of days, immediately before** (§8.1 line 1745): `Previous = From − Days … From − 1`. Never a
  calendar month: 1–31 March compares with 29 January – 28 February (31 days) — integration-tested with
  orders on 28 Jan 23:59 (out), 29 Jan 00:00 and 28 Feb 23:59 (in). The last 7 days compare with the 7
  before; one day with the day before; 366 days with the 366 before (2012 → 31 Dec 2010, because 2011 has
  365).
- **A previous value of zero has no percentage**: `PercentChange` is `null`, `Change` is the rise; the
  screen says "Up from none in the previous period". Both zero: change 0, "No change". **A current value of
  zero is −100 %**, not an error. An average with no paid orders is `null` ("—"), never 0.
- Rounded to one decimal place, half away from zero, on the server.

## Colombo days

A day is `[Colombo midnight, next Colombo midnight)` in UTC — 18:30 UTC the day before. The range is compared
on the raw UTC column (so `IX_Orders_Paid` can seek it); grouping uses doc 08 §1.4's
`CONVERT(date, DATEADD(minute, 330, x))`. `An_order_at_23_59_and_one_at_00_01_Colombo_fall_on_different_days`
puts orders at 18:29 and 18:31 UTC on **the same UTC day** and asserts they land on 31 January and
1 February respectively in the KPIs, in the placed-date count, in the one-day previous period and in the
monthly trend. The browser works out "today" as UTC + 330 minutes too (Vitest: 18:29 vs 18:31 UTC, and New
Year at 18:30 UTC on 31 December).

Dates are accepted from 2000-01-01 to 2099-12-31 only: `0001-01-01` answered 500 before that guard (the
previous period underflows), now a 400 naming the field.

## Low stock

**The threshold is the variant's own `StockLevels.LowStockThreshold`, or `Inventory.DefaultLowStockThreshold`
when it has none**, read as `CatalogOptions.DefaultLowStockThreshold` (`Catalog__DefaultLowStockThreshold`),
**default 5** — the deferred-setting fallback #113 left, matching doc 08 §9. No new value was invented; the
response carries the default it used, and the tests read it from there. Low is `0 < Available ≤ threshold`
(P16 line 1655) on active variants of products that are not deleted; `Available = OnHand − Reserved`, so
stock held by unpaid orders is not available (tested). Out of stock is not "low" and is on the inventory
report.

## Customers by district

P16 line 1656: "**Distinct customer emails on paid orders by delivery district**" — `Orders.DeliveryDistrictId`,
not `Users.DefaultDistrictId` (the Customers screen's column). The widget is titled "Buyers by delivery
district" and says, in so many words, that it is not the default district on a customer account and that a
buyer who used two districts is in both; the distinct total is shown separately because the rows do not sum
to it.

## CSV exports

`CsvDocument` for all three (formula neutralising, quoting, UTF-8 BOM, CRLF). Each export writes one
`Export` audit row **before** the file is returned, and fails with no file if it cannot (unit-tested with a
failing audit writer). `EntityType` is `SalesReport`, `InventoryReport` or `CustomerReport`; `AfterJson`
holds the report name, the row count, the dates or stock filter, and **whether** a search was applied —
**never the search text** (an integration test sweeps every string column of the audit row for it).
Money is `0.00` with no separator so a spreadsheet can sum it; the sales file ends with a Total row. The
Export button is **not rendered** for a role without `Reports.Export`.

## Index use

No index was added. The paid-date queries rely on `IX_Orders_Paid` (filtered `PaidAtUtc IS NOT NULL`) and
the refund query on `IX_Refunds_Completed` (filtered `Status = 1`). `ReportingQueryPlanTests` renders **the
reader's own** queries (`PaidTotalsQuery`, `PaidDailyQuery`, `BuyersByDistrictQuery`,
`DepartmentSalesQuery`, `RefundsDailyQuery`) with `ToQueryString`, forces the index on every reference to the
table and asks for the estimated plan — error 8622 if the predicate cannot match the filter. A control test
proves the technique refuses a query that does not imply the filter (a `PlacedAtUtc` predicate → 8622). The
low-stock query is not plan-tested: `IX_StockLevels_Available` is unfiltered, so forcing it always compiles
and would prove nothing.

## Mutations

Each applied to a committed tree, the five W4-02 integration classes and the reporting unit tests run, then
restored with `git checkout` on that file.

| # | Mutation | Killed by |
|---|---|---|
| m01 | Revenue by `PlacedAtUtc` instead of `PaidAtUtc` | `Gross_revenue_is_orders_by_paid_date…`, reconciliation |
| m02 | Refunded orders left out of revenue | gross-rule test, net-revenue test, sales report test, reconciliation |
| m03 | Colombo offset 0 in the SQL grouping | 23:59/00:01 test, trend test, sales report test, reconciliation |
| m04 | Period bounds at UTC midnight | 23:59/00:01, previous-period, sales report, reconciliation; unit `A_Colombo_day_runs_from_18_30_UTC…` |
| m05 | Previous period = one calendar month back | previous-period test, 366-day test; 6 unit cases |
| m06 | Percentage 100 when the previous value is 0 | zero-bases test, department/product test; 3 unit tests |
| m07 | Non-settlement refunds subtracted | net-revenue test |
| m08 | Department sales from `LineSubtotal` (before promo) | department test, reconciliation |
| m09 | Buyers counted as orders, not distinct emails | buyers-by-district test |
| m10 | Low stock `<` instead of `≤` threshold | low-stock boundary test |
| m11 | Low stock ignores the variant's own threshold | **survived the first time** — see below; killed after the fix |
| m12 | Audit row carries the search text | customers export test (integration) and its unit test |
| m13 | Customers export gated on `Reports.View` | 4 integration tests (permission, antiforgery, bad filter, export) |
| m14 | Drop the explicit `PaidAtUtc != null` from `PaidIn` | **survives — equivalent**: SQL Server matches the filtered index from the range predicate alone; the plan test passes either way, so the explicit clause is belt and braces |
| m15 | Weeks start on Sunday | trend test, sales export test, sales report test; 5 unit cases |
| m16 | Customers export skips the antiforgery check | antiforgery test |
| m17 | KPI refunds hard-coded to 0 | net-revenue test, reconciliation |
| m18 | Date range guard removed (2000–2099) | 3 integration cases (0001/9999 → 500), 4 unit cases |
| F1 | "Up from none" replaced by "+100.0%" | 2 Vitest specs |
| F2 | Sales Export shown regardless of `Reports.Export` | Vitest + Playwright "offered no Export button" |
| F3 | Trend accepts a stale answer | Vitest "keeps only the latest answer" |
| F4 | Inventory clears the page while the next loads | Vitest paging spec + Playwright focus test |
| F5 | Browser "today" in UTC | Vitest dashboard period + 2 format specs |
| F6 | Stub stops neutralising formulas | Playwright customers export test |
| F7 | Recent orders shown without `Orders.View` | Vitest + Playwright role test |
| F8 | Breakdown tables lose their scroll wrapper (40 rem min width) | Playwright no-sideways-scroll sweep |

**m11 survived the first run, and why is worth recording.** The low-stock test had one variant low only by
its own (higher) threshold and one in stock only by its own (lower) one. A rule that ignored the variant's
threshold counted the second instead of the first, the total came out the same, and the report's separate
`LowStock` filter (unmutated) agreed with it. A seventh variant, a second own-higher one, breaks the symmetry;
m11 is now killed.

F1's first form (`&& false`) did not compile, so it was not a mutant; the table shows the corrected one. In
the F4 Vitest run several unrelated specs (the bank-transfer page's among them; my output was cut at ten lines, so I did not count them) also failed in 1–2 ms each, which reads as a cascade inside the
mutated run; the unmutated suite passed 624/624 before and after.

## Numbers (this machine, this worktree)

| Suite | Result |
|---|---|
| `dotnet build SpencerDenim.sln -c Release --no-incremental` | 0 warnings, 0 errors |
| Unit, whole assembly | **2,780 / 2,780** (main's 2,701 per the brief + this branch's 79) |
| Integration, whole assembly, unfiltered, alone | INTEGRATION_RESULT |
| My integration classes, filtered | 55 / 55 (23 dashboard, 21 reports, 4 inventory, 1 reconciliation, 6 plan); the m11 fix changed a test rather than adding one |
| Vitest | shared 24, storefront 1,880, admin **624** — all pass |
| Playwright, whole suite, retries 0, ports 5700–5716 | **1,052 / 1,052** in 8.0 min (this branch's file: 18) |
| `npm run build:admin`, `npm run build`, eslint on the admin app | clean (no budget warning) |

## Open questions for the owner / tester

1. **The 5-minute dashboard cache** (doc 08 §8.2, P16 "Data freshness") is **not built**: every request is
   live. The answers are always current; the cost is a few grouped queries per load. Doc 08's own trigger
   for summary tables is p95 > 500 ms or > 100k orders.
2. **Doc 11 line 378** puts an "inventory report" behind `Inventory.View / Reports.Export`; P16 puts reports
   behind `Reports.View`. I followed P16. Order Staff (`Inventory.View`) therefore cannot open it.
3. **Promo code report** (P16 step 3 "and promo code usage"; P3 line 487) is not built — ADM-19 in doc 02
   lists Sales, Inventory and Customers only.
4. **Customer export.** Doc 11 P11 line 1308 says "Export customers | `Customers.Export` / `Reports.Export`";
   the customers report export answers it with `Reports.Export`. `Customers.Export` still does not exist.
5. **Doc 02's ADM-04 row** still does not mention the alert banner (W4-02a's journal raised it).
6. **The top-products "sales trend"** (ADM-07) is read as each product's revenue against the previous
   period; there is no per-product sparkline. Say if a series was meant.

## Corrections to the brief

- **"Revenue on the KPI card, the trend, the department breakdown and the sales report must reconcile."**
  Three of them do; the department breakdown **cannot** equal gross revenue — doc 11 P16 (line 1672) defines
  it without delivery. It reconciles to the report's items column, and items + delivery = gross. The test
  asserts exactly that.
- **"docs/11 §51 lists a `NightlyStats` job."** Line 51 is the system-jobs row of a table, not a section.
  `NightlyStats` is **not built**; doc 08 §8.2/§8.3 say all sales widgets are live anyway and the job only
  rebuilds traffic tables and `ProductStats`, so the dashboard computes live, as specified.
- **"Check what exists in `Permissions.cs`."** All three permissions exist and are seeded; nothing was added.
- **"The customers list … counts lifetime spend … so a refunded order still counts."** True — but there is
  no refund row in the system yet (nothing writes `sales.Refunds`), so "refunded" today only means an order
  whose status somebody set by other means. Net revenue is built to the document and tested with rows
  written directly.
- **"Order Status distribution."** Doc 11 P16 says "Live count by current status" without saying over which
  orders. I scoped it to **orders placed in the period**, by their status now, so it sums to the Orders KPI;
  the screen says so. If the owner meant the whole order book, it is a one-line change.
