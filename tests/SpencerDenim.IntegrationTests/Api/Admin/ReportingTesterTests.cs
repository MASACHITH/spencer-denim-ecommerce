using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The W4-02 tester's own cases: the ones the developer's suite proved with inserted rows or did not reach.
/// Each owns a year nobody else writes orders into (2001–2006), except 2004, which the ISO-week and leap-year
/// cases share: the ISO-week case asks only about 22 December 2003 – 11 January 2004, the leap-year case only
/// about 29 January 2004 onwards (and about 2003 through the sales report, never a fixed sum).
/// </summary>
public sealed class ReportingTesterTests(SqlServerDatabaseFixture sqlServer) : ReportingTestBase(sqlServer)
{
    private const string DashboardUrl = "/api/admin/dashboard";
    private const string TrendUrl = "/api/admin/dashboard/trend";

    /// <summary>Distinct from the order (24), bank-transfer (12), customer (36) and reconciliation (48) bands.</summary>
    private const int VariantBandOffset = 60;

    /// <summary>
    /// <b>Accept-late through the real route, not a row with a paid date written in.</b> An order placed on one
    /// day, expired by the real expiry store, then accepted late through
    /// <c>POST /api/admin/orders/bank-transfers/{id}/accept-late</c>: doc 11 P7 step 8 says "revenue dated
    /// today". So it is revenue once, on the approval day, and an order (not revenue) on the day it was placed.
    /// The real route stamps "now", so after it has run both dates are shifted by the same amount into 2005,
    /// keeping the gap the route created.
    /// </summary>
    [Fact]
    public async Task An_order_accepted_late_through_the_real_route_is_revenue_once_on_the_approval_day()
    {
        await using var host = await StartAsync();

        var placed = await PlaceOrderAsync(host, Email("late"));

        // Placed three days ago, so the approval is on a different Colombo day from the placement.
        var placedUtc = DateTime.UtcNow.AddDays(-3);
        await SetDatesAsync(placed.PublicId, placedUtc, paidUtc: null, dueUtc: placedUtc.AddHours(48));

        await ExpireAsync(placed.OrderId);
        Assert.Equal((OrderStatus.Expired, (DateTime?)null), await StatusAndPaidAsync(placed.PublicId));

        var before = DateTime.UtcNow;
        using (var approver = await host.SignInAsync(Permissions.Orders.Approve))
        using (var accepted = await approver.PostAsJsonAsync(
                   new Uri($"/api/admin/orders/bank-transfers/{placed.PublicId:D}/accept-late", UriKind.Relative),
                   new { note = "Money arrived after expiry." },
                   JsonOptions,
                   Token))
        {
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        var after = DateTime.UtcNow;
        var (status, paidAtUtc) = await StatusAndPaidAsync(placed.PublicId);
        Assert.Equal(OrderStatus.Paid, status);
        Assert.NotNull(paidAtUtc);

        // The route stamped the approval moment, not the placement and not the due date.
        Assert.InRange(paidAtUtc!.Value, before.AddSeconds(-1), after.AddSeconds(1));

        // Move the whole order into 2005, keeping the gap: approval at noon on 20 June 2005 Colombo.
        var shift = Colombo(2005, 6, 20, 12, 0) - paidAtUtc.Value;
        var shiftedPlaced = placedUtc + shift;
        await SetDatesAsync(placed.PublicId, shiftedPlaced, paidAtUtc.Value + shift, placedUtc.AddHours(48) + shift);

        var placedDay = DateOnly.FromDateTime(shiftedPlaced.AddMinutes(330));
        Assert.Equal(new DateOnly(2005, 6, 17), placedDay);

        using var client = await host.SignInAsync(Permissions.Dashboard.View, Permissions.Reports.View);

        // The approval day: revenue, a paid order, no order placed.
        var approvalDay = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2005, 6, 20)}&to={Day(2005, 6, 20)}")).GetProperty("kpis");
        Assert.Equal(placed.GrandTotal, Current(approvalDay, "grossRevenue"));
        Assert.Equal(1m, Current(approvalDay, "paidOrders"));
        Assert.Equal(0m, Current(approvalDay, "orders"));

        // The day it was placed: an order, no revenue.
        var placementDay = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2005, 6, 17)}&to={Day(2005, 6, 17)}")).GetProperty("kpis");
        Assert.Equal(0m, Current(placementDay, "grossRevenue"));
        Assert.Equal(1m, Current(placementDay, "orders"));

        // Over the whole month: counted exactly once.
        var june = await GetJsonAsync(client, $"/api/admin/reports/sales?from={Day(2005, 6, 1)}&to={Day(2005, 6, 30)}&groupBy=Day");
        Assert.Equal(placed.GrandTotal, Money(june.GetProperty("totals").GetProperty("grossRevenue")));
        Assert.Equal(1, june.GetProperty("totals").GetProperty("paidOrders").GetInt32());
        var paidRows = june.GetProperty("rows").EnumerateArray().Where(row => Money(row.GetProperty("grossRevenue")) != 0m).ToList();
        Assert.Equal("2005-06-20", Assert.Single(paidRows).GetProperty("start").GetString());
    }

    /// <summary>
    /// <b>ISO weeks across a Colombo midnight and a year boundary.</b> 1 January 2004 is a Thursday, so ISO week 1
    /// of 2004 starts on Monday 29 December 2003. 23:59 on Sunday 28 December and 00:01 on Monday 29 December
    /// (Colombo) are on the same UTC day and in different weeks; 23:59 on Sunday 4 January is still in the week
    /// that began in 2003; 00:00 on Monday 5 January starts the next.
    /// </summary>
    [Fact]
    public async Task Iso_weeks_split_on_a_colombo_midnight_across_a_year_boundary()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];

        async Task<InsertedOrder> Paid(DateTime when, decimal price) =>
            await InsertOrderAsync(catalogue, new OrderSpec(Email(when.Ticks.ToString(CultureInfo.InvariantCulture)), when, when, OrderStatus.Paid, [new LineSpec(pick, 1, price)]));

        var sundayLast = await Paid(Colombo(2003, 12, 28, 23, 59), 1000m);
        var mondayFirst = await Paid(Colombo(2003, 12, 29, 0, 1), 2000m);
        var sundayNewYear = await Paid(Colombo(2004, 1, 4, 23, 59), 4000m);
        var mondayNext = await Paid(Colombo(2004, 1, 5, 0, 0), 8000m);

        Assert.Equal(Colombo(2003, 12, 28, 23, 59).Date, Colombo(2003, 12, 29, 0, 1).Date);

        using var client = await host.SignInAsync(Permissions.Dashboard.View, Permissions.Reports.View);

        var weeks = (await GetJsonAsync(client, $"{TrendUrl}?interval=Week&to={Day(2004, 1, 11)}"))
            .GetProperty("points").EnumerateArray().TakeLast(3).ToList();
        Assert.Equal(["2003-12-22", "2003-12-29", "2004-01-05"], weeks.Select(week => week.GetProperty("start").GetString()!));
        Assert.Equal("2004-01-04", weeks[1].GetProperty("end").GetString());
        Assert.Equal(sundayLast.GrandTotal, Money(weeks[0].GetProperty("grossRevenue")));
        Assert.Equal(mondayFirst.GrandTotal + sundayNewYear.GrandTotal, Money(weeks[1].GetProperty("grossRevenue")));
        Assert.Equal(mondayNext.GrandTotal, Money(weeks[2].GetProperty("grossRevenue")));
        Assert.Equal(2, weeks[1].GetProperty("orders").GetInt32());

        // The sales report by week over the same days, cut at the period's ends.
        var report = await GetJsonAsync(client, $"/api/admin/reports/sales?from={Day(2003, 12, 24)}&to={Day(2004, 1, 6)}&groupBy=Week");
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(["2003-12-24", "2003-12-29", "2004-01-05"], rows.Select(row => row.GetProperty("start").GetString()!));
        Assert.Equal(sundayLast.GrandTotal, Money(rows[0].GetProperty("grossRevenue")));
        Assert.Equal(mondayFirst.GrandTotal + sundayNewYear.GrandTotal, Money(rows[1].GetProperty("grossRevenue")));
        Assert.Equal(mondayNext.GrandTotal, Money(rows[2].GetProperty("grossRevenue")));

        // And by month: the two December orders, the two January ones. The trend ends on 10 January, not the
        // 31st: 2004 is shared with the leap-year test below, whose 29 and 30 January orders are (rightly) in
        // January's bucket. Ending the window on the 10th cuts January's bucket to 1–10 January, the same days
        // the KPI below asks about, so the sums stay exact whichever of the two tests runs first.
        var months = (await GetJsonAsync(client, $"{TrendUrl}?interval=Month&to={Day(2004, 1, 10)}"))
            .GetProperty("points").EnumerateArray().TakeLast(2).ToList();
        Assert.Equal(["2003-12-01", "2004-01-01"], months.Select(month => month.GetProperty("start").GetString()!));
        Assert.Equal(["2003-12-31", "2004-01-10"], months.Select(month => month.GetProperty("end").GetString()!));
        Assert.Equal(sundayLast.GrandTotal + mondayFirst.GrandTotal, Money(months[0].GetProperty("grossRevenue")));
        Assert.Equal(sundayNewYear.GrandTotal + mondayNext.GrandTotal, Money(months[1].GetProperty("grossRevenue")));

        // The first ten days of 2004 compare with 22–31 December 2003: the two December orders, both in it.
        var kpis = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2004, 1, 1)}&to={Day(2004, 1, 10)}")).GetProperty("kpis");
        Assert.Equal(sundayNewYear.GrandTotal + mondayNext.GrandTotal, Current(kpis, "grossRevenue"));
        Assert.Equal(sundayLast.GrandTotal + mondayFirst.GrandTotal, Previous(kpis, "grossRevenue"));
    }

    /// <summary>
    /// <b>The previous period in a leap year, for one day and for 366.</b> March 2006 is not a leap year, so
    /// use 2004: 1–31 March 2004 compares with 30 January – 29 February 2004 (31 days, Feb has 29). One day,
    /// 1 March 2004, compares with 29 February. The whole of 2004 (366 days) compares with 31 December 2002 –
    /// 31 December 2003, because 2003 has 365.
    /// </summary>
    [Fact]
    public async Task The_previous_period_in_a_leap_year_for_one_day_and_for_366()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];

        async Task<InsertedOrder> Paid(DateTime when, decimal price) =>
            await InsertOrderAsync(catalogue, new OrderSpec(Email(when.Ticks.ToString(CultureInfo.InvariantCulture)), when, when, OrderStatus.Paid, [new LineSpec(pick, 1, price)]));

        await Paid(Colombo(2004, 1, 29, 23, 59), 100_000m);              // just before March's previous period
        var leapStart = await Paid(Colombo(2004, 1, 30, 0, 0), 1_000m);    // its first minute
        var leapDay = await Paid(Colombo(2004, 2, 29, 23, 59), 2_000m);    // its last minute: the leap day
        var march = await Paid(Colombo(2004, 3, 1, 0, 0), 4_000m);

        var outside2002 = await Paid(Colombo(2002, 12, 30, 23, 59), 300_000m); // a day before 2004's previous period
        var inside2002 = await Paid(Colombo(2002, 12, 31, 0, 0), 30_000m);     // its first minute

        using var client = await host.SignInAsync(Permissions.Dashboard.View);

        var marchBody = await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2004, 3, 1)}&to={Day(2004, 3, 31)}");
        Assert.Equal("2004-01-30", marchBody.GetProperty("previousPeriod").GetProperty("from").GetString());
        Assert.Equal("2004-02-29", marchBody.GetProperty("previousPeriod").GetProperty("to").GetString());
        Assert.Equal(march.GrandTotal, Current(marchBody.GetProperty("kpis"), "grossRevenue"));
        Assert.Equal(leapStart.GrandTotal + leapDay.GrandTotal, Previous(marchBody.GetProperty("kpis"), "grossRevenue"));

        var oneDay = await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2004, 3, 1)}&to={Day(2004, 3, 1)}");
        Assert.Equal("2004-02-29", oneDay.GetProperty("previousPeriod").GetProperty("from").GetString());
        Assert.Equal(leapDay.GrandTotal, Previous(oneDay.GetProperty("kpis"), "grossRevenue"));

        var year = await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2004, 1, 1)}&to={Day(2004, 12, 31)}");
        Assert.Equal(366, year.GetProperty("period").GetProperty("days").GetInt32());
        Assert.Equal("2002-12-31", year.GetProperty("previousPeriod").GetProperty("from").GetString());
        Assert.Equal("2003-12-31", year.GetProperty("previousPeriod").GetProperty("to").GetString());

        // 2003 is shared with the ISO-week test, so compare with the sales report over the exact window rather
        // than a fixed sum: 31 Dec 2002 is in the previous period, 30 Dec 2002 is not.
        var previous = Previous(year.GetProperty("kpis"), "grossRevenue");
        using var reporter = await host.SignInAsync(Permissions.Reports.View);
        var exact = await GetJsonAsync(reporter, $"/api/admin/reports/sales?from={Day(2002, 12, 31)}&to={Day(2003, 12, 31)}&groupBy=Month");
        var edge = (await GetJsonAsync(reporter, $"/api/admin/reports/sales?from={Day(2002, 12, 30)}&to={Day(2002, 12, 31)}&groupBy=Day"))
            .GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(Money(exact.GetProperty("totals").GetProperty("grossRevenue")), previous);
        Assert.Equal(outside2002.GrandTotal, Money(edge[0].GetProperty("grossRevenue")));
        Assert.Equal(inside2002.GrandTotal, Money(edge[1].GetProperty("grossRevenue")));
        Assert.Equal(inside2002.GrandTotal, Money(exact.GetProperty("rows")[0].GetProperty("grossRevenue")));
    }

    /// <summary>
    /// <b>Paid orders are orders, not buyers, on every screen.</b> One buyer paying twice on one Colombo day is two
    /// paid orders in the KPI, the trend and the sales report. Without this case, counting distinct buyers per
    /// day in the shared daily query survived the whole suite (tester mutation T02): no fixture had one email
    /// paying twice on the same day, so orders and buyers coincided everywhere.
    /// </summary>
    [Fact]
    public async Task One_buyer_paying_twice_in_a_day_is_two_paid_orders_everywhere()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];
        var email = Email("twice");

        var first = await InsertOrderAsync(catalogue, new OrderSpec(email, Colombo(2002, 6, 10, 9, 0), Colombo(2002, 6, 10, 9, 5), OrderStatus.Paid, [new LineSpec(pick, 1, 1000m)]));
        var second = await InsertOrderAsync(catalogue, new OrderSpec(email.ToUpperInvariant(), Colombo(2002, 6, 10, 15, 0), Colombo(2002, 6, 10, 15, 5), OrderStatus.Paid, [new LineSpec(pick, 1, 2000m)]));

        using var client = await host.SignInAsync(Permissions.Dashboard.View, Permissions.Reports.View);

        var kpis = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2002, 6, 1)}&to={Day(2002, 6, 30)}")).GetProperty("kpis");
        Assert.Equal(2m, Current(kpis, "paidOrders"));
        Assert.Equal(first.GrandTotal + second.GrandTotal, Current(kpis, "grossRevenue"));

        var month = (await GetJsonAsync(client, $"{TrendUrl}?interval=Month&to={Day(2002, 6, 30)}")).GetProperty("points").EnumerateArray().Last();
        Assert.Equal(2, month.GetProperty("paidOrders").GetInt32());
        Assert.Equal(2, month.GetProperty("orders").GetInt32());

        foreach (var groupBy in new[] { "Day", "Week", "Month" })
        {
            var totals = (await GetJsonAsync(client, $"/api/admin/reports/sales?from={Day(2002, 6, 1)}&to={Day(2002, 6, 30)}&groupBy={groupBy}")).GetProperty("totals");
            Assert.Equal(2, totals.GetProperty("paidOrders").GetInt32());
        }

        // One buyer, though: the district and customers figures count people.
        var dashboard = await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2002, 6, 1)}&to={Day(2002, 6, 30)}");
        Assert.Equal(1, dashboard.GetProperty("buyersByDistrict").GetProperty("distinctBuyers").GetInt32());
    }

    /// <summary>
    /// <b>The configured default threshold is the one the low-stock filter uses</b>, not only the one the response
    /// reports. Every other low-stock test runs at the default of 5, so hard-coding 5 in the SQL survived the
    /// suite (tester mutation T18) — and a shop that set <c>Catalog__DefaultLowStockThreshold=10</c> would have
    /// been told "threshold 10" while being alerted at 5.
    /// </summary>
    [Fact]
    public async Task The_low_stock_filter_uses_the_configured_default_threshold()
    {
        SqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(SqlServer.ConnectionString, Token);

        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var reader = new SpencerDenim.Infrastructure.Admin.Reporting.ReportingReader(
            db,
            Microsoft.Extensions.Options.Options.Create(new SpencerDenim.Application.Catalog.CatalogOptions { DefaultLowStockThreshold = 37 }));

        var sql = reader.LowStockQuery().ToQueryString();
        TestContext.Current.TestOutputHelper?.WriteLine(sql);
        // Alone this renders "DECLARE @threshold int = 37;"; in one whole-assembly run the same build rendered it
        // with no DECLARE at all, so the value was inlined. Accept either form: 37 is there, a literal 5 is not.
        Assert.True(Regex.IsMatch(sql, @"(int = 37;)|(, 37\))"), sql);
        Assert.False(Regex.IsMatch(sql, @", 5\)"), sql);

        var answer = await reader.ReadLowStockAsync(10, Token);
        Assert.Equal(37, answer.DefaultThreshold);
        Assert.All(answer.Rows, row => Assert.InRange(row.Available, 1, row.Threshold));
    }

    /// <summary>
    /// <b>How long the live dashboard takes at a year's volume.</b> Opt-in: set <c>REPORTING_PERF_ORDERS</c>
    /// (e.g. 36500). Seeds that many orders across 2001, four in five paid, two or three lines each, about
    /// 8,000 distinct buyers over every district, then times each route. Writes the timings to
    /// <c>%TEMP%/w402-perf.txt</c>. It asserts only that each answer is a 200: the numbers are for a person.
    /// </summary>
    [Fact]
    public async Task The_live_dashboard_at_a_year_of_orders()
    {
        var configured = Environment.GetEnvironmentVariable("REPORTING_PERF_ORDERS");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(configured), "Set REPORTING_PERF_ORDERS to run the timing.");
        var count = int.Parse(configured!, CultureInfo.InvariantCulture);

        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();

        // One template order per pick, two or three lines each, all on catalogue rows.
        var templates = new List<int>();
        for (var index = 0; index < catalogue.Picks.Count; index++)
        {
            var lines = new List<LineSpec>
            {
                new(catalogue.Picks[index], 1 + (index % 3), 1990m + (index * 250m), Promo: index % 4 == 0 ? 200m : 0m),
                new(catalogue.Picks[(index + 1) % catalogue.Picks.Count], 1, 3490m),
            };

            if (index % 2 == 0)
            {
                lines.Add(new LineSpec(catalogue.Picks[(index + 2) % catalogue.Picks.Count], 2, 990m));
            }

            var template = await InsertOrderAsync(catalogue, new OrderSpec(Email($"tpl{index}"), Colombo(2001, 1, 1), Colombo(2001, 1, 1), OrderStatus.Delivered, lines));
            templates.Add(template.OrderId);
        }

        var seed = Stopwatch.StartNew();
        await SeedAsync(templates, count);
        seed.Stop();

        using var client = await host.SignInAsync(
            Permissions.Dashboard.View,
            Permissions.Reports.View,
            Permissions.Reports.Export,
            Permissions.Orders.View);

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Seeded {count:N0} orders (+{templates.Count} templates) in {seed.Elapsed.TotalSeconds:F1} s");

        async Task Time(string label, string url)
        {
            // One warm-up (plan compile), then five timed.
            using (var warm = await client.GetAsync(new Uri(url, UriKind.Relative), Token))
            {
                Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
            }

            var samples = new List<double>();
            for (var run = 0; run < 5; run++)
            {
                var watch = Stopwatch.StartNew();
                using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Token);
                _ = await response.Content.ReadAsByteArrayAsync(Token);
                watch.Stop();
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                samples.Add(watch.Elapsed.TotalMilliseconds);
            }

            samples.Sort();
            report.AppendLine(CultureInfo.InvariantCulture, $"{label,-46} median {samples[2],7:F0} ms   max {samples[4],7:F0} ms");
        }

        await Time("dashboard, 30 days", $"{DashboardUrl}?from={Day(2001, 6, 1)}&to={Day(2001, 6, 30)}");
        await Time("dashboard, 366 days (whole seeded year)", $"{DashboardUrl}?from={Day(2001, 1, 1)}&to={Day(2002, 1, 1)}");
        await Time("trend, monthly, 12 months", $"{TrendUrl}?interval=Month&to={Day(2001, 12, 31)}");
        await Time("trend, weekly, 52 weeks", $"{TrendUrl}?interval=Week&to={Day(2001, 12, 31)}");
        await Time("low stock", "/api/admin/dashboard/low-stock");
        await Time("sales report, 366 days by day", $"/api/admin/reports/sales?from={Day(2001, 1, 1)}&to={Day(2002, 1, 1)}&groupBy=Day");
        await Time("customers report, 366 days, page 1", $"/api/admin/reports/customers?from={Day(2001, 1, 1)}&to={Day(2002, 1, 1)}");
        await Time("customers report, 366 days, page 100", $"/api/admin/reports/customers?from={Day(2001, 1, 1)}&to={Day(2002, 1, 1)}&page=100");

        // The export: every buyer in the year, through the real antiforgery route.
        var exportWatch = Stopwatch.StartNew();
        using (var exported = await client.PostAsync(new Uri($"/api/admin/reports/customers/export?from={Day(2001, 1, 1)}&to={Day(2002, 1, 1)}", UriKind.Relative), null, Token))
        {
            var bytes = await exported.Content.ReadAsByteArrayAsync(Token);
            exportWatch.Stop();
            Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
            report.AppendLine(CultureInfo.InvariantCulture, $"{"customers export, 366 days",-46} once   {exportWatch.Elapsed.TotalMilliseconds,7:F0} ms   {bytes.Length:N0} bytes");
        }

        // What the year's figures were, so the timing is about real work.
        var body = await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2001, 1, 1)}&to={Day(2002, 1, 1)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"year: orders {Current(body.GetProperty("kpis"), "orders")}, paid {Current(body.GetProperty("kpis"), "paidOrders")}, gross {Current(body.GetProperty("kpis"), "grossRevenue")}, buyers {body.GetProperty("buyersByDistrict").GetProperty("distinctBuyers").GetInt32()}");

        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "w402-perf.txt"), report.ToString(), Token);
        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static decimal Current(JsonElement kpis, string name) => kpis.GetProperty(name).GetProperty("current").GetDecimal();

    private static decimal Previous(JsonElement kpis, string name) => kpis.GetProperty(name).GetProperty("previous").GetDecimal();

    /// <summary>Copies the templates <paramref name="count"/> times across 2001, set-based.</summary>
    private async Task SeedAsync(IReadOnlyList<int> templates, int count)
    {
        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        async Task<List<string>> ColumnsAsync(string table)
        {
            var connection = db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(Token);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT c.name FROM sys.columns c
                 WHERE c.object_id = OBJECT_ID('{table}') AND c.is_identity = 0 AND c.is_computed = 0
                   AND TYPE_NAME(c.system_type_id) NOT IN ('timestamp', 'rowversion')
                 ORDER BY c.column_id
                """;
            var names = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                names.Add(reader.GetString(0));
            }

            return names;
        }

        var orderColumns = await ColumnsAsync("sales.Orders");
        var lineColumns = await ColumnsAsync("sales.OrderLines");
        var prefix = "PF" + Marker[^6..];

        var orderOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OrderNumber"] = $"CONCAT('{prefix}', n.i)",
            ["PublicId"] = "NEWID()",
            ["CheckoutIdempotencyKey"] = "NEWID()",
            ["PlacedAtUtc"] = "n.placed",
            ["CreatedAtUtc"] = "n.placed",
            ["PaymentDueAtUtc"] = "DATEADD(hour, 48, n.placed)",
            ["PaidAtUtc"] = "CASE WHEN n.i % 5 = 0 THEN NULL ELSE DATEADD(minute, 45, n.placed) END",
            ["Status"] = $"CASE WHEN n.i % 5 = 0 THEN {(byte)OrderStatus.Expired} WHEN n.i % 7 = 0 THEN {(byte)OrderStatus.Paid} ELSE {(byte)OrderStatus.Delivered} END",
            ["StockState"] = $"CASE WHEN n.i % 5 = 0 THEN {(byte)OrderStockState.Released} ELSE {(byte)OrderStockState.Committed} END",
            ["Email"] = "CONCAT('perf', n.i % 8000, '@example.com')",
            ["EmailNormalized"] = "UPPER(CONCAT('perf', n.i % 8000, '@example.com'))",
            ["DeliveryDistrictId"] = "d.Id",
            ["DeliveryDistrictName"] = "d.Name",
            ["PaymentProviderCode"] = $"CASE WHEN n.i % 3 = 0 THEN '{PaymentProviderCodes.PayPal}' ELSE '{PaymentProviderCodes.BankTransfer}' END",
            ["InvoiceNumber"] = "NULL",
            ["CustomerUserId"] = "NULL",
        };

        var lineOverrides = new Dictionary<string, string>(StringComparer.Ordinal) { ["OrderId"] = "o.Id" };

        string Select(IEnumerable<string> columns, Dictionary<string, string> overrides, string alias) =>
            string.Join(", ", columns.Select(column => overrides.TryGetValue(column, out var expression) ? expression : $"{alias}.[{column}]"));

        var templateValues = string.Join(", ", templates.Select((id, k) => $"({k}, {id})"));
        var start = Colombo(2001, 1, 1, 0, 0).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

        var sql = $"""
            SET NOCOUNT ON;
            DECLARE @n int = {count};
            CREATE TABLE #t (k int PRIMARY KEY, id int NOT NULL);
            INSERT INTO #t VALUES {templateValues};
            DECLARE @k int = (SELECT COUNT(*) FROM #t);
            CREATE TABLE #d (r int PRIMARY KEY, Id int NOT NULL, Name nvarchar(100) NOT NULL);
            INSERT INTO #d SELECT ROW_NUMBER() OVER (ORDER BY Id) - 1, Id, Name FROM ref.Districts;
            DECLARE @dc int = (SELECT COUNT(*) FROM #d);
            SELECT TOP (@n) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i
              INTO #n0 FROM sys.all_objects a CROSS JOIN sys.all_objects b;
            SELECT i, DATEADD(second, CAST(CAST(i AS bigint) * 31535000 / @n AS int), CAST('{start}' AS datetime2)) AS placed
              INTO #n FROM #n0;

            INSERT INTO sales.Orders ({string.Join(", ", orderColumns.Select(column => $"[{column}]"))})
            SELECT {Select(orderColumns, orderOverrides, "t")}
              FROM #n n
              JOIN #t tk ON tk.k = n.i % @k
              JOIN sales.Orders t ON t.Id = tk.id
              JOIN #d d ON d.r = n.i % @dc;

            INSERT INTO sales.OrderLines ({string.Join(", ", lineColumns.Select(column => $"[{column}]"))})
            SELECT {Select(lineColumns, lineOverrides, "l")}
              FROM sales.Orders o
              JOIN #t tk ON tk.k = CAST(SUBSTRING(o.OrderNumber, {prefix.Length + 1}, 12) AS int) % @k
              JOIN sales.OrderLines l ON l.OrderId = tk.id
             WHERE o.OrderNumber LIKE '{prefix}%';
            """;

        await db.Database.ExecuteSqlRawAsync(sql, Token);
    }

    private async Task<PlacedOrder> PlaceOrderAsync(MediaTestHost host, string email)
    {
        using var shopper = host.CreateAnonymousClient();

        using (var scope = SqlServer.CreateScope())
        {
            var variantId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductVariants
                .AsNoTracking()
                .Where(variant => variant.IsActive && variant.Product!.IsPublished)
                .Where(variant => variant.Product!.Department!.IsActive && variant.Product!.PrimaryCategory!.IsActive)
                .Where(variant => variant.StockLevel != null && variant.StockLevel.Available > 2)
                .OrderByDescending(variant => variant.Id)
                .Skip(VariantBandOffset)
                .Select(variant => variant.Id)
                .FirstAsync(Token);

            using var added = await shopper.PostAsJsonAsync(new Uri("/api/cart/items", UriKind.Relative), new { variantId, quantity = 1 }, JsonOptions, Token);
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        var catalogue = await CatalogueAsync();
        var districtId = catalogue.Districts["Colombo"];

        using var quote = await shopper.GetAsync(
            new Uri($"/api/checkout?districtId={districtId}&deliveryMethodId={catalogue.StandardDeliveryId}", UriKind.Relative),
            Token);
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        var session = await quote.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        using var placed = await shopper.PostAsJsonAsync(
            new Uri("/api/checkout/orders", UriKind.Relative),
            new
            {
                checkoutKey = session.GetProperty("checkoutKey").GetGuid(),
                firstName = "Ruwan",
                lastName = "Fernando",
                email,
                phone = "0771234567",
                deliveryMethodId = catalogue.StandardDeliveryId,
                districtId,
                paymentMethodCode = PaymentProviderCodes.BankTransfer,
                expectedGrandTotal = session.GetProperty("totals").GetProperty("grandTotal").GetDecimal(),
                address = new { recipientName = "Ruwan Fernando", phone = "0771234567", line1 = "14 Galle Road", city = "Kollupitiya" },
            },
            JsonOptions,
            Token);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

        var publicId = (await placed.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token)).GetProperty("publicId").GetGuid();

        using var read = SqlServer.CreateScope();
        var order = await read.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Where(row => row.PublicId == publicId)
            .Select(row => new { row.Id, row.GrandTotal })
            .SingleAsync(Token);

        return new PlacedOrder(order.Id, publicId, order.GrandTotal);
    }

    private async Task ExpireAsync(int orderId)
    {
        using var scope = SqlServer.CreateScope();

        Assert.True(await scope.ServiceProvider.GetRequiredService<IOrderExpiryStore>().ExpireAsync(orderId, DateTime.UtcNow, Token));
    }

    private async Task SetDatesAsync(Guid publicId, DateTime placedUtc, DateTime? paidUtc, DateTime dueUtc)
    {
        using var scope = SqlServer.CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .Where(order => order.PublicId == publicId)
            .ExecuteUpdateAsync(
                order => order
                    .SetProperty(row => row.PlacedAtUtc, placedUtc)
                    .SetProperty(row => row.PaidAtUtc, paidUtc)
                    .SetProperty(row => row.PaymentDueAtUtc, dueUtc),
                Token);

        Assert.Equal(1, moved);
    }

    private async Task<(OrderStatus Status, DateTime? PaidAtUtc)> StatusAndPaidAsync(Guid publicId)
    {
        using var scope = SqlServer.CreateScope();

        var row = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Where(order => order.PublicId == publicId)
            .Select(order => new { order.Status, order.PaidAtUtc })
            .SingleAsync(Token);

        return (row.Status, row.PaidAtUtc);
    }

    private sealed record PlacedOrder(int OrderId, Guid PublicId, decimal GrandTotal);
}
