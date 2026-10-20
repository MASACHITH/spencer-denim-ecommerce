# 2026-10-19: #186b, a chart for the Customers report (developer)

Branch `feature/186b-customers-chart` in worktree `worktrees/sd-custchart`, based on `feature/186-admin-charts` at
`d3a4a62` (tested, not merged). Not pushed, no PR. **No migration, no new index.**

## What it plots, and why

#186 left the Customers report without a chart because the API sent nothing to plot. The data really holds
paid orders (email, paid instant, delivery district) and accounts. Two candidates came from the demo:

- **Buyers per day, week or month, split into first-time and returning.** Chosen. It is about customers over time,
  which is what the report is for, and both halves can be worked out honestly from paid orders alone.
- Buyers by delivery district. Not chosen: the dashboard's Customer overview already shows exactly this
  (`BuyersByDistrict`), so a copy on the report would add nothing new.

Drawn as **stacked bars**: first-time buyers at the bottom (`--chart-1` blue) and returning buyers on top
(`--chart-2` amber). That pair has the largest colour difference in the palette and stays apart under colour
blindness (see the #186 journal's all-pairs check). The text keys sit beside the canvas, as on the line chart.

## Label definitions (on the screen, in the DTO docs, and in the API description)

- **Buyer**: a distinct email address, compared as `EmailNormalized` so case does not matter, with at least one
  order **paid** in the days counted (an order with a `PaidAtUtc` there). This is the report's own population with
  the same period and the same email search, so the chart's period total equals the report's `totalCount`.
- **First-time**: the buyer's first paid order **in this shop, ever** falls in those days.
- **Returning**: the buyer had a paid order here **before** those days began. On one bar that means before *that
  bar's* day, week or month began, which can be earlier in the same period.
- Every buyer is exactly one of the two, so on each bar first-time + returning = buyers.
- **The period's buyer count is not the sum of the bars.** Someone who paid in two weeks shows on both bars and
  counts once in the totals. The screen says so, and the figures table ends with "Whole period (each buyer once)".
  The first-time figures do add up, because a first order happens only once.
- Refunds: an order refunded **after** it was paid keeps its paid date and still counts. This follows the rule the
  reports use everywhere (doc 08 §8.1; the report's own spend is gross). The brief asked for refunded orders to be
  excluded, but then the chart would disagree with the buyer count printed just above it. An order that was
  **never** paid has no paid date and is never a buyer, nor history for anyone: awaiting payment, cancelled,
  expired, or refunded money that was never a settlement. Cancelled orders can only come from PendingPayment,
  so a cancelled order is never paid.
- "Returning" is only as deep as the shop's history. In its first weeks nearly everyone is first-time. That is the
  truth, not a gap, and the definition on the screen says "in this shop, ever".
- Grouping is chosen by the server from the period's length: **Day** up to 31 days, **ISO Week** up to 182 days,
  **Month** beyond that, so there are never more than 31 bars. Buckets are cut to the period exactly as
  `ReportBuckets.Split` cuts the sales report's rows. Days are Colombo days.

## API change

A sibling endpoint, not a new field. The report endpoint is paged, so a field on it would have recomputed the chart
on every Next/Previous.

`GET /api/admin/reports/customers/buyers?from=&to=&search=` takes `Reports.View` (the same permission as the
report), parses the period with the same `ReportPeriod.Parse`, sends `no-store`, and returns:

```
CustomerBuyersDto(Period, Interval, Buyers, FirstTimeBuyers, ReturningBuyers, Points[])
CustomerBuyersPoint(Start, End, Buyers, FirstTimeBuyers, ReturningBuyers)
```

`GetCustomerBuyersQuery` and its handler (Application) choose the interval, place the reader's counts on the
buckets, and derive returning as buyers − first-time. `IReportingReader.ReadBuyerCountsAsync` (Infrastructure)
makes three SQL aggregates and loads no rows:

1. distinct `EmailNormalized` per bucket over paid orders in the period;
2. per email, `MIN(PaidAtUtc)` over all paid orders **up to the period's end**, `HAVING` that minimum ≥ the period
   start, then counted per bucket;
3. distinct buyers over the period.

The bucket is computed in SQL as `DATEDIFF(day|month, @anchor, DATEADD(minute, 330, PaidAtUtc))`, with weeks
counted from the Monday on or before the period start and divided by 7. All three queries are filtered on
`PaidAtUtc IS NOT NULL` and the UTC range, and `IX_Orders_Paid` (which INCLUDEs `EmailNormalized`) covers them.
`ReportingQueryPlanTests` now forces that filtered index on the new queries in day, week, month and searched
forms (8 new cases), and SQL Server compiles every one (it would refuse with error 8622 if a predicate could not
match the filter). Query 2 reads the paid history up to the period's end, because "first" needs history. That is
a range scan of a narrow covering index plus one aggregate.

**CSV export: unchanged, on purpose.** The export is one row per buyer. The chart's series is one row per day,
week or month, which is a different shape and does not belong in that file. Every figure in the chart is already
on the screen in its figures table. The "first-time" judgement also depends on history outside the exported
period, so it would be wrong to present it as a property of the exported rows. If the owner wants it in a file,
that would be a second export with its own audit shape, which is a scope question.

## Frontend

- `chart-config.ts` has a fourth shape, `StackedBarSpec` (`kind: 'stacked'`, Chart.js type `bar`, both axes
  stacked, whole-number ticks, a 1px surface-coloured gap between the parts). The `sda-chart` wrapper shows its
  series keys as text. There is still one chart setup: no new registration and no new library.
- `customer-report.ts` adds a card above the table with a title, a totals line, the chart (canvas `role="img"`
  with a one-sentence summary), "Show the figures as a table" (the bars plus a "Whole period" row), and the
  definitions in a paragraph. The chart loads on start and on Show, never on a page turn. If only the chart fails,
  the table stays and the card says the chart could not be loaded. With nobody in the period, the existing
  sentence is shown and no chart.
- `min-width: 0`: the global `.adm-card` / `.adm-card > *` rules and `sda-chart`'s `.chart__table` from #186
  round 2 already cover this card. A component-level copy that I added first was proved redundant (removing it
  changed nothing), so I deleted it. With the global rules and the `.chart__table` rule removed, the new e2e
  fails "scrolls sideways at 320px with every table open". With them restored, it passes.
- Node stub: `buyersBody` / `buyersPoint` in `admin-reporting-stub.mjs`, field for field with the DTOs. That is
  pinned by `AdminReportingShapeTests`, which now also checks `CustomerBuyers` / `CustomerBuyersPoint` in
  `reporting.model.ts`.

## Numbers (my own runs)

- .NET Release build: **0 warnings, 0 errors**.
- Unit tests, full suite: **3002 passed, 0 failed** (2987 on #186; new `CustomerBuyersTests` 11 plus 4 shape-test rows).
  Note for whoever repeats the mutations: restoring a file by renaming its backup keeps the old timestamp, so
  MSBuild's incremental build keeps the mutated binary. Rebuild with `--no-incremental` afterwards.
- Integration tests, **filtered only**, `TEST_SQL_CONNECTION=Server=(localdb)\MSSQLLocalDB;…`:
  `AdminCustomerBuyersTests` + `ReportingQueryPlanTests` + `AdminReportEndpointTests` gave **46 passed, 0 failed**.
  They cover an empty period, a single buyer, first-time vs returning at both Colombo midnights (23:59 the day
  before and 00:00 the first day, 23:59 the last day and 00:00 the day after), a buyer paying twice in the period,
  unpaid/cancelled/expired/refunded-never-paid excluded (and not history), refunded-after-paid counted, email
  casing within a day and across history, week and month edges matching `Split`, search, 401/403/400.
  "District casing" from the brief does not apply: districts are not plotted.
- Admin Vitest: **54 files, 698 passed** (691 before; 7 new). `ng lint admin`: clean.
- `npm run build`: storefront initial **446.10 kB** (unchanged). `npm run build:admin`: admin initial 312.42 kB
  (unchanged); reports-page chunk 31.18 kB.
- Playwright on 67xx ports, `admin-customers-chart` (new, 5) + `admin-dashboard` + `admin-charts-edge`:
  **40 passed, 0 failed**. The customers report's width sweep now waits for the chart and measures its summary.

## Mutations (each applied alone, tests run, then reverted)

| Mutation | Result |
|---|---|
| B1 first-time reads only the period, not the history | killed: boundary + casing tests |
| B2 bucket on the UTC day, not the Colombo day | killed: boundary test |
| B3 weeks counted from the period start, not its Monday | killed: week/month edges test |
| B4 buyers per bucket counts orders, not distinct emails | killed: casing + week tests |
| B5 first-time ignores the search | killed: 6 tests |
| B6 a first order exactly at the period's first instant is dropped (`>` for `>=`) | killed: boundary test |
| B7 period total = sum of bars, not distinct | killed: unit "distinct buyers and the sum of first-time" |
| B8 31-day periods go weekly | killed: unit grouping theory (31 → Day) |
| B9 a bar's returning = buyers | killed: 2 unit tests |
| F1 first-time/returning colours swapped | killed (Vitest) |
| F2 Show does not reload the chart | killed (Vitest) |
| F3 whole-period row adds up the bars | killed (Vitest) |
| F4 bars not stacked | killed (Vitest) |
| F5 a stacked chart of zeros is drawn | killed (Vitest) |
| F6 chart reloaded on every page turn | killed (Vitest) |
| E2 no series keys for a stacked chart | killed (e2e "shows exactly the figures…") |
| E3 the `min-width: 0` guards removed (global card + `.chart__table`) | killed (e2e sideways scroll at 320 px) |
| E1 component-level `min-width: 0` removed | survived: redundant with the global rule, so it was deleted |

## Screenshots (this folder)

`2026-10-19-186b-customers-chart/ours-reports-customers-1440.png`, `…-1440-table-open.png` and `…-390.png` were
taken against the Node stub (`set=some`) at exact CSS widths. Horizontal overflow measured at 0 in all three.

## Tester (independent), 2026-10-19

**Verdict: PASS.** No High or Medium defects. Tests added in `d31b15d`.

- Hand-worked SQL Server cases all matched the first time: an ISO week across the year end (Mon 31 Dec 2035 to
  Sun 6 Jan 2036) with the same orders by month and by day, the leap day ending an ISO week (2032), 31/32/182/183-day
  edges, a mid-month monthly period, refunded-after-paid first orders, an old unpaid order that is not history,
  23:59/00:01 across bar boundaries, and the search (LIKE `_ % [` and a quote are literal; the middle of an address
  does not match). The chart's `buyers` equals the report's `totalCount` in every case.
- 401/403 match doc 08 §9: Content Editor, Order Staff and Reports.Export alone are refused; Store Manager is allowed.
  Eight bad periods give a 400 body identical to the customers report's.
- Plans: the forced-index (8622) technique is used correctly, with a control. Added: no key lookup under the forced
  index for the buyers and first-time queries, with a control that does need one.
- Mutations: 13 backend mutations (10 killed; the developer's tests would have missed T6, search `Contains` rather
  than `StartsWith`; 3 are equivalent). 12 frontend mutations (10 killed; FM1–FM7 and FM11 needed the new tests;
  FM8 equivalent; FM9 is a compile error that the developer's E2 also covers).
- Low: the three aggregates run as separate statements under RCSI. An order paid between statements 1 and 2 on
  "today" could make first-time > buyers on that bar, which shows returning as −1. A snapshot transaction around the
  three reads, or clamping returning at zero, would close it.
- Low: the third aggregate (distinct buyers over the period) has no plan test of its own. It uses the same
  `PaidIn` predicate.
- Not this branch: `admin-sale-campaigns.e2e.ts:192` hard-codes a campaign start of 2026-10-19T00:00. From today
  that campaign is "Live", not "Starts later", so the test fails on any branch.

## Fix round (developer, 2026-10-19): one consistent read, plan test for the total

**1. Negative returning buyers (tester Low).** The first version ran three statements. Under
READ_COMMITTED_SNAPSHOT each statement reads its own committed snapshot, so an order committed between them could
show up in the first-time count and be missing from the buyers count.

- **Why not a SNAPSHOT transaction.** `tools/db/provision-database.sql` enables only READ_COMMITTED_SNAPSHOT, not
  ALLOW_SNAPSHOT_ISOLATION. Turning that on is a database change this chart does not justify, so it is not used.
- **Why not clamping.** Clamping returning at zero would hide the symptom, but the period total could still
  disagree with the bars.
- **What it does instead: one statement.** `BuyerCountsQuery` reads the period's paid orders as (email, bucket)
  and inner-joins each email's first paid instant (its whole history up to the period end). It groups by
  `GROUPING SETS ((Bucket), ())`, so the bars and the period's distinct total come out of the same statement. On
  the total row, the first-time test still compares each row's own bucket with the first order's bucket, which is
  true for exactly the buyers whose first order is in the period.
- **Why one statement is enough.** Under RCSI a single statement sees a single snapshot across both references to
  the orders table, so every count comes from the same data.
- **Nothing to restore on the pooled connection.** There is no transaction and no isolation change, so nothing can
  leak into the pool (#172). `PooledIsolationLeakTests` still passes.
- **Why raw SQL.** LINQ cannot express GROUPING SETS. Every value is a typed `SqlParameter`: `datetime2(3)` like
  the column, and `nvarchar(256)` like `EmailNormalized`. The search's `%`, `_`, `[` and `\` are escaped, the same
  as EF's `StartsWith` does it. The only text spliced into the SQL is one of three fixed bucket expressions and an
  optional fixed `LIKE` clause. The tester's LIKE and quote cases pass unchanged.
- **New test: `AdminCustomerBuyersConsistencyTests`.** It makes the race deterministic. A command interceptor
  commits a new first-time buyer's paid order from another connection as soon as the reader's first command starts.
  The test asserts:
  - exactly one command was sent;
  - no bar has returning below zero;
  - on every bar, first-time + returning = buyers;
  - the bars' first-time figures add up to the period's first-time total;
  - the result is the state before the new order: (1, 1, 0).
- **Proof that the test catches the defect.** With the first-time counts taken from a second statement (and the
  one-command check switched off), it fails with exactly the reported defect: `Buyers = 1, FirstTimeBuyers = 2,
  ReturningBuyers = -1` on 10 Jul 2013. With the total taken from a second statement, the one-command check fails.

**2. Plan test for the third aggregate.** The distinct total is now the grand-total row of the same statement. For
day, week, month and the search, `ReportingQueryPlanTests` forces `IX_Orders_Paid` on **both** references to the
orders table (it asserts there are 2, and that GROUPING SETS is present). The statement compiles, which would fail
with error 8622 if a predicate could not use the filtered index. It also shows no key lookup, so the index covers
the whole statement, distinct total included.

**3. Test-data fix.** My email-casing test put a "history" order in **2012**, a year that belongs to
`AdminDashboardEndpointTests`. The combined filtered run caught it: "Department, category and product sales…"
failed, and passed when run alone. That order is now on 10 Jan 2013.

**Numbers:**

| Check | Result |
|---|---|
| Release build | 0 warnings, 0 errors (`--no-incremental`) |
| Unit tests (full suite) | 3002 passed |
| Integration, filtered (`~Report`, `~AdminCustomerBuyers`, `~Dashboard`, `~PooledIsolationLeak`) on LocalDB | 136 passed, 0 failed, 1 skipped |

The skip is `ReportingTesterTests.The_live_dashboard_at_a_year_of_orders`, which was already skipped before this branch.

`admin-sale-campaigns.e2e.ts:192` is untouched; it is being fixed on another branch.
