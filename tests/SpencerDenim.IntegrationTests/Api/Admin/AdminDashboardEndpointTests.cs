using System.Net;
using System.Text.Json;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The dashboard's routes end to end against SQL Server (ADM-04, ADM-05, ADM-07, ADM-08; doc 11 P16).
/// Every route is called over HTTP through the real permission check; no test reads the reader directly.
/// </summary>
public sealed class AdminDashboardEndpointTests(SqlServerDatabaseFixture sqlServer) : ReportingTestBase(sqlServer)
{
    private const string DashboardUrl = "/api/admin/dashboard";
    private const string TrendUrl = "/api/admin/dashboard/trend";
    private const string LowStockUrl = "/api/admin/dashboard/low-stock";

    // ---- permissions ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(DashboardUrl)]
    [InlineData(TrendUrl)]
    [InlineData(LowStockUrl)]
    public async Task Every_dashboard_route_needs_a_session_and_Dashboard_View(string url)
    {
        await using var host = await StartAsync();

        using var anonymous = host.CreateAnonymousClient();
        using (var refused = await anonymous.GetAsync(new Uri(url, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // Reports.View is not Dashboard.View: the two screens are separate permissions (doc 11 P16).
        using var reporter = await host.SignInAsync(Permissions.Reports.View, Permissions.Reports.Export, Permissions.Orders.View);
        using (var refused = await reporter.GetAsync(new Uri(url, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using var viewer = await host.SignInAsync(Permissions.Dashboard.View);
        using var allowed = await viewer.GetAsync(new Uri(url, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Contains("no-store", allowed.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- the revenue rule ----------------------------------------------------------------------------

    /// <summary>
    /// <b>Doc 08 §8.1, decided by doc 11 P16's KPI table:</b> gross revenue is Σ <c>GrandTotal</c> of orders
    /// by <b>paid date</b>, "Includes orders later refunded"; orders are counted by <b>placed date</b>, "paid
    /// or not". So a refunded order is revenue, an order that is pending, cancelled or expired is not, and a
    /// bank transfer approved after expiry counts on the day it was approved (P16's first edge case).
    /// </summary>
    [Fact]
    public async Task Gross_revenue_is_orders_by_paid_date_refunded_included_and_unpaid_never()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];

        LineSpec[] Lines(decimal price) => [new LineSpec(pick, 1, price)];

        var delivered = await InsertOrderAsync(catalogue, new OrderSpec(Email("a"), Colombo(2007, 5, 3), Colombo(2007, 5, 3), OrderStatus.Delivered, Lines(1000m)));
        var refunded = await InsertOrderAsync(catalogue, new OrderSpec(Email("b"), Colombo(2007, 5, 4), Colombo(2007, 5, 4), OrderStatus.Refunded, Lines(2000m)));

        // Placed in April, expired, then accepted on 2 May: revenue in May, an order in April.
        var lateApproval = await InsertOrderAsync(catalogue, new OrderSpec(Email("c"), Colombo(2007, 4, 28), Colombo(2007, 5, 2), OrderStatus.Paid, Lines(4000m), Provider: PaymentProviderCodes.PayPal));

        await InsertOrderAsync(catalogue, new OrderSpec(Email("d"), Colombo(2007, 5, 5), null, OrderStatus.PendingPayment, Lines(8000m)));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("e"), Colombo(2007, 5, 6), null, OrderStatus.Cancelled, Lines(16000m)));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("f"), Colombo(2007, 5, 7), null, OrderStatus.Expired, Lines(32000m)));

        using var client = await host.SignInAsync(Permissions.Dashboard.View);
        var body = await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2007, 5, 1)}&to={Day(2007, 5, 31)}");
        var kpis = body.GetProperty("kpis");

        var gross = delivered.GrandTotal + refunded.GrandTotal + lateApproval.GrandTotal;
        Assert.Equal(gross, Current(kpis, "grossRevenue"));
        Assert.Equal(3m, Current(kpis, "paidOrders"));

        // Five placed in May (the late approval was placed in April).
        Assert.Equal(5m, Current(kpis, "orders"));
        Assert.Equal(Math.Round(gross / 3m, 2, MidpointRounding.AwayFromZero), Current(kpis, "averageOrderValue"));

        // No refund row was recorded, so net equals gross.
        Assert.Equal(gross, Current(kpis, "netRevenue"));

        // The status distribution is the five placed in May, by status now; it sums to the Orders KPI.
        var statuses = body.GetProperty("orderStatuses").EnumerateArray()
            .ToDictionary(row => row.GetProperty("status").GetString()!, row => row.GetProperty("count").GetInt32());
        Assert.Equal(8, statuses.Count);
        Assert.Equal(1, statuses["Delivered"]);
        Assert.Equal(1, statuses["Refunded"]);
        Assert.Equal(1, statuses["PendingPayment"]);
        Assert.Equal(1, statuses["Cancelled"]);
        Assert.Equal(1, statuses["Expired"]);
        Assert.Equal(0, statuses["Paid"]);
        Assert.Equal(5, statuses.Values.Sum());

        // Payment methods are the three paid in May, by provider.
        var methods = body.GetProperty("paymentMethods").EnumerateArray()
            .ToDictionary(row => row.GetProperty("code").GetString()!, row => row);
        Assert.Equal(2, methods[PaymentProviderCodes.BankTransfer].GetProperty("paidOrders").GetInt32());
        Assert.Equal(delivered.GrandTotal + refunded.GrandTotal, Money(methods[PaymentProviderCodes.BankTransfer].GetProperty("revenue")));
        Assert.Equal(1, methods[PaymentProviderCodes.PayPal].GetProperty("paidOrders").GetInt32());
        Assert.Equal(lateApproval.GrandTotal, Money(methods[PaymentProviderCodes.PayPal].GetProperty("revenue")));

        // April: one order placed, nothing paid.
        var april = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2007, 4, 1)}&to={Day(2007, 4, 30)}")).GetProperty("kpis");
        Assert.Equal(1m, Current(april, "orders"));
        Assert.Equal(0m, Current(april, "grossRevenue"));
        Assert.Equal(JsonValueKind.Null, april.GetProperty("averageOrderValue").GetProperty("current").ValueKind);
    }

    // ---- Colombo days --------------------------------------------------------------------------------

    /// <summary>
    /// <b>A day is a Colombo day.</b> 23:59 on 31 January in Colombo is 18:29 UTC on the 31st; 00:01 on
    /// 1 February in Colombo is 18:31 UTC on the <b>31st</b> — a UTC day would put both on 31 January.
    /// Asserted on the KPIs, on the monthly trend and on the placed-date count.
    /// </summary>
    [Fact]
    public async Task An_order_at_23_59_and_one_at_00_01_Colombo_fall_on_different_days()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];

        var lastMinute = Colombo(2008, 1, 31, 23, 59);
        var firstMinute = Colombo(2008, 2, 1, 0, 1);

        // Both on the same UTC calendar day, which is the point.
        Assert.Equal(lastMinute.Date, firstMinute.Date);
        Assert.Equal(new DateTime(2008, 1, 31, 18, 29, 0, DateTimeKind.Utc), lastMinute);
        Assert.Equal(new DateTime(2008, 1, 31, 18, 31, 0, DateTimeKind.Utc), firstMinute);

        var january = await InsertOrderAsync(catalogue, new OrderSpec(Email("jan"), lastMinute, lastMinute, OrderStatus.Paid, [new LineSpec(pick, 1, 1000m)]));
        var february = await InsertOrderAsync(catalogue, new OrderSpec(Email("feb"), firstMinute, firstMinute, OrderStatus.Paid, [new LineSpec(pick, 1, 3000m)]));

        using var client = await host.SignInAsync(Permissions.Dashboard.View);

        var thirtyFirst = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2008, 1, 31)}&to={Day(2008, 1, 31)}")).GetProperty("kpis");
        Assert.Equal(january.GrandTotal, Current(thirtyFirst, "grossRevenue"));
        Assert.Equal(1m, Current(thirtyFirst, "orders"));

        var first = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2008, 2, 1)}&to={Day(2008, 2, 1)}")).GetProperty("kpis");
        Assert.Equal(february.GrandTotal, Current(first, "grossRevenue"));
        Assert.Equal(1m, Current(first, "orders"));

        // The one-day period's previous period is the day before: 1 February compares with 31 January.
        Assert.Equal(january.GrandTotal, Previous(first, "grossRevenue"));

        var trend = await GetJsonAsync(client, $"{TrendUrl}?interval=Month&to={Day(2008, 2, 29)}");
        var points = trend.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(12, points.Count);
        Assert.Equal(january.GrandTotal, Money(points[^2].GetProperty("grossRevenue")));
        Assert.Equal("2008-01-01", points[^2].GetProperty("start").GetString());
        Assert.Equal(february.GrandTotal, Money(points[^1].GetProperty("grossRevenue")));
        Assert.Equal(1, points[^1].GetProperty("orders").GetInt32());
    }

    // ---- the previous period -------------------------------------------------------------------------

    /// <summary>
    /// <b>The previous period is the same number of days immediately before</b> (doc 08 §8.1), counted in
    /// days and never as "last month". March 2009 is 31 days, so it compares with 29 January – 28 February
    /// (February 2009 has 28 days): 28 January is outside, 29 January is inside.
    /// </summary>
    [Fact]
    public async Task The_previous_period_is_the_same_number_of_days_immediately_before()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];

        async Task<InsertedOrder> Paid(DateTime when, decimal price) =>
            await InsertOrderAsync(catalogue, new OrderSpec(Email(when.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)), when, when, OrderStatus.Paid, [new LineSpec(pick, 1, price)]));

        await Paid(Colombo(2009, 1, 28, 23, 59), 100000m);          // the day before the previous period
        var earliest = await Paid(Colombo(2009, 1, 29, 0, 0), 1000m); // its first minute
        var latest = await Paid(Colombo(2009, 2, 28, 23, 59), 2000m); // its last minute
        var now = await Paid(Colombo(2009, 3, 1, 0, 0), 4000m);       // the current period's first minute

        using var client = await host.SignInAsync(Permissions.Dashboard.View);
        var body = await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2009, 3, 1)}&to={Day(2009, 3, 31)}");

        Assert.Equal("2009-01-29", body.GetProperty("previousPeriod").GetProperty("from").GetString());
        Assert.Equal("2009-02-28", body.GetProperty("previousPeriod").GetProperty("to").GetString());
        Assert.Equal(31, body.GetProperty("previousPeriod").GetProperty("days").GetInt32());
        Assert.Equal(31, body.GetProperty("period").GetProperty("days").GetInt32());

        var gross = body.GetProperty("kpis").GetProperty("grossRevenue");
        Assert.Equal(now.GrandTotal, gross.GetProperty("current").GetDecimal());
        Assert.Equal(earliest.GrandTotal + latest.GrandTotal, gross.GetProperty("previous").GetDecimal());
        Assert.Equal(now.GrandTotal - earliest.GrandTotal - latest.GrandTotal, gross.GetProperty("change").GetDecimal());

        var expected = Math.Round(
            (now.GrandTotal - earliest.GrandTotal - latest.GrandTotal) / (earliest.GrandTotal + latest.GrandTotal) * 100m,
            1,
            MidpointRounding.AwayFromZero);
        Assert.Equal(expected, gross.GetProperty("percentChange").GetDecimal());
    }

    /// <summary>
    /// <b>A previous period of zero has no percentage</b> — a rise from nothing is not a percentage of
    /// anything — and <b>a current period of zero is −100 %</b>, not an error. Both zero: no change, no
    /// percentage.
    /// </summary>
    [Fact]
    public async Task A_zero_previous_period_has_no_percentage_and_a_zero_current_period_is_minus_100()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];

        // June 2010 has sales; the 30 days before 1 June (2–31 May) and July 2010 have none.
        var june = await InsertOrderAsync(catalogue, new OrderSpec(Email("june"), Colombo(2010, 6, 15), Colombo(2010, 6, 15), OrderStatus.Paid, [new LineSpec(pick, 2, 1500m)]));

        using var client = await host.SignInAsync(Permissions.Dashboard.View);

        // 1–30 June (30 days) against 2–31 May: nothing then.
        var risen = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2010, 6, 1)}&to={Day(2010, 6, 30)}")).GetProperty("kpis");
        Assert.Equal(june.GrandTotal, Current(risen, "grossRevenue"));
        Assert.Equal(0m, Previous(risen, "grossRevenue"));
        Assert.Equal(june.GrandTotal, risen.GetProperty("grossRevenue").GetProperty("change").GetDecimal());
        Assert.Equal(JsonValueKind.Null, risen.GetProperty("grossRevenue").GetProperty("percentChange").ValueKind);
        Assert.Equal(JsonValueKind.Null, risen.GetProperty("averageOrderValue").GetProperty("percentChange").ValueKind);
        Assert.Equal(JsonValueKind.Null, risen.GetProperty("averageOrderValue").GetProperty("previous").ValueKind);

        // 1–30 July (30 days) against 1–30 June: everything gone.
        var fallen = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2010, 7, 1)}&to={Day(2010, 7, 30)}")).GetProperty("kpis");
        Assert.Equal(0m, Current(fallen, "grossRevenue"));
        Assert.Equal(june.GrandTotal, Previous(fallen, "grossRevenue"));
        Assert.Equal(-100m, fallen.GetProperty("grossRevenue").GetProperty("percentChange").GetDecimal());
        Assert.Equal(-100m, fallen.GetProperty("orders").GetProperty("percentChange").GetDecimal());

        // August against July: both zero.
        var flat = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2010, 8, 1)}&to={Day(2010, 8, 30)}")).GetProperty("kpis");
        Assert.Equal(0m, flat.GetProperty("grossRevenue").GetProperty("change").GetDecimal());
        Assert.Equal(JsonValueKind.Null, flat.GetProperty("grossRevenue").GetProperty("percentChange").ValueKind);
    }

    // ---- net revenue ---------------------------------------------------------------------------------

    /// <summary>
    /// <b>Net revenue = gross − completed settlement refunds by completion date</b> (doc 08 §8.1; doc 11 P16:
    /// "An order paid in September and refunded in October → gross revenue in September, refund reduces
    /// October net revenue"). A pending refund, a refund completed in another period and a refund of a
    /// payment exception (not the settlement) are not subtracted.
    /// </summary>
    [Fact]
    public async Task Net_revenue_subtracts_completed_settlement_refunds_by_completion_date()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];

        var september = await InsertOrderAsync(catalogue, new OrderSpec(Email("sep"), Colombo(2011, 9, 10), Colombo(2011, 9, 10), OrderStatus.Refunded, [new LineSpec(pick, 1, 3000m)]));
        var october = await InsertOrderAsync(catalogue, new OrderSpec(Email("oct"), Colombo(2011, 10, 5), Colombo(2011, 10, 5), OrderStatus.Delivered, [new LineSpec(pick, 1, 5000m)]));
        var expired = await InsertOrderAsync(catalogue, new OrderSpec(Email("exc"), Colombo(2011, 10, 6), null, OrderStatus.Refunded, [new LineSpec(pick, 1, 7000m)]));

        await InsertRefundAsync(september.OrderId, september.GrandTotal, Colombo(2011, 10, 2));                    // counts in October
        var octoberPayment = await InsertRefundAsync(october.OrderId, 500m, null, RefundStatus.Pending);       // not completed
        await InsertRefundAsync(october.OrderId, 700m, Colombo(2011, 11, 2), paymentId: octoberPayment);        // November
        await InsertRefundAsync(expired.OrderId, expired.GrandTotal, Colombo(2011, 10, 7), settlement: false); // a payment exception

        using var client = await host.SignInAsync(Permissions.Dashboard.View);

        var octoberKpis = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2011, 10, 1)}&to={Day(2011, 10, 31)}")).GetProperty("kpis");
        Assert.Equal(october.GrandTotal, Current(octoberKpis, "grossRevenue"));
        Assert.Equal(september.GrandTotal, Current(octoberKpis, "refundsCompleted"));
        Assert.Equal(october.GrandTotal - september.GrandTotal, Current(octoberKpis, "netRevenue"));

        // September keeps its gross revenue, refunded or not, and its net is untouched.
        var septemberKpis = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2011, 9, 1)}&to={Day(2011, 9, 30)}")).GetProperty("kpis");
        Assert.Equal(september.GrandTotal, Current(septemberKpis, "grossRevenue"));
        Assert.Equal(september.GrandTotal, Current(septemberKpis, "netRevenue"));

        var novemberKpis = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2011, 11, 1)}&to={Day(2011, 11, 30)}")).GetProperty("kpis");
        Assert.Equal(700m, Current(novemberKpis, "refundsCompleted"));
        Assert.Equal(-700m, Current(novemberKpis, "netRevenue"));
    }

    // ---- customers by district -----------------------------------------------------------------------

    /// <summary>
    /// "Customers by district" is doc 11 P16's: <b>distinct customer emails on paid orders, by delivery
    /// district</b> — not <c>Users.DefaultDistrictId</c>, which is the Customers screen's column. Two orders
    /// by one email to one district are one buyer; the same email delivering to a second district is a buyer
    /// there too, and once in the distinct total. Case is ignored, as <c>EmailNormalized</c> does.
    /// </summary>
    [Fact]
    public async Task Buyers_by_district_are_distinct_emails_by_delivery_district_on_paid_orders()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];
        LineSpec[] line = [new LineSpec(pick, 1, 1000m)];

        await InsertOrderAsync(catalogue, new OrderSpec(Email("amali"), Colombo(2012, 3, 2), Colombo(2012, 3, 2), OrderStatus.Paid, line, District: "Kandy"));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("AMALI"), Colombo(2012, 3, 9), Colombo(2012, 3, 9), OrderStatus.Delivered, line, District: "Kandy"));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("amali"), Colombo(2012, 3, 12), Colombo(2012, 3, 12), OrderStatus.Paid, line, District: "Galle"));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("bimal"), Colombo(2012, 3, 15), Colombo(2012, 3, 15), OrderStatus.Paid, line, District: "Kandy"));

        // Unpaid: not a buyer anywhere.
        await InsertOrderAsync(catalogue, new OrderSpec(Email("chathu"), Colombo(2012, 3, 16), null, OrderStatus.Expired, line, District: "Jaffna"));

        using var client = await host.SignInAsync(Permissions.Dashboard.View);
        var buyers = (await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2012, 3, 1)}&to={Day(2012, 3, 31)}")).GetProperty("buyersByDistrict");

        Assert.Equal(2, buyers.GetProperty("distinctBuyers").GetInt32());

        var rows = buyers.GetProperty("districts").EnumerateArray()
            .ToDictionary(row => row.GetProperty("name").GetString()!, row => row);
        Assert.Equal(["Galle", "Kandy"], rows.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(2, rows["Kandy"].GetProperty("buyers").GetInt32());
        Assert.Equal(3, rows["Kandy"].GetProperty("paidOrders").GetInt32());
        Assert.Equal(1, rows["Galle"].GetProperty("buyers").GetInt32());

        // Kandy first: most buyers.
        Assert.Equal("Kandy", buyers.GetProperty("districts")[0].GetProperty("name").GetString());
    }

    // ---- what sells ----------------------------------------------------------------------------------

    /// <summary>
    /// Departments, categories and products are Σ <b>line totals after promo, excluding delivery</b>, on paid
    /// orders, by the department and category recorded on the line at purchase (doc 11 P16). The top
    /// products carry their revenue against the previous period.
    /// </summary>
    [Fact]
    public async Task Department_category_and_product_sales_are_line_totals_after_promo_without_delivery()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var (first, second) = catalogue.TwoInOneDepartment();
        var other = catalogue.OnePerDepartment.First(pick => pick.DepartmentId != first.DepartmentId);

        // Previous period (1–30 November 2012): the first product sold 1,000.
        await InsertOrderAsync(catalogue, new OrderSpec(Email("prev"), Colombo(2012, 11, 20), Colombo(2012, 11, 20), OrderStatus.Paid, [new LineSpec(first, 1, 1000m)]));

        // Current period (1–30 December 2012): delivery 400 on each order, a 300 promo on one line.
        await InsertOrderAsync(catalogue, new OrderSpec(
            Email("one"),
            Colombo(2012, 12, 3),
            Colombo(2012, 12, 3),
            OrderStatus.Delivered,
            [new LineSpec(first, 2, 1500m, Promo: 300m), new LineSpec(second, 1, 800m)]));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("two"), Colombo(2012, 12, 4), Colombo(2012, 12, 4), OrderStatus.Paid, [new LineSpec(other, 3, 500m)]));

        // Unpaid: sells nothing.
        await InsertOrderAsync(catalogue, new OrderSpec(Email("three"), Colombo(2012, 12, 5), null, OrderStatus.Cancelled, [new LineSpec(other, 9, 9000m)]));

        using var client = await host.SignInAsync(Permissions.Dashboard.View);
        var body = await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2012, 12, 1)}&to={Day(2012, 12, 30)}");

        var departments = body.GetProperty("departments").EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetInt32(), row => row);
        Assert.Equal(2700m + 800m, Money(departments[first.DepartmentId].GetProperty("revenue")));
        Assert.Equal(3, departments[first.DepartmentId].GetProperty("units").GetInt32());
        Assert.Equal(1500m, Money(departments[other.DepartmentId].GetProperty("revenue")));

        // Every department is listed, zeros included, and the shares are of line revenue (5,000).
        Assert.True(departments.Count >= 3);
        Assert.Equal(70.0m, departments[first.DepartmentId].GetProperty("sharePercent").GetDecimal());
        Assert.Equal(30.0m, departments[other.DepartmentId].GetProperty("sharePercent").GetDecimal());
        Assert.Equal(5000m, departments.Values.Sum(row => Money(row.GetProperty("revenue"))));

        // Gross revenue is the same orders plus their delivery: the departments do not add up to it.
        Assert.Equal(5000m + 800m, Current(body.GetProperty("kpis"), "grossRevenue"));

        var categories = body.GetProperty("topCategories").EnumerateArray().ToList();
        Assert.Equal(first.CategoryId, categories[0].GetProperty("id").GetInt32());
        Assert.Equal(2700m, Money(categories[0].GetProperty("revenue")));
        Assert.Contains(first.Department, categories[0].GetProperty("name").GetString()!, StringComparison.Ordinal);

        var products = body.GetProperty("topProducts").EnumerateArray().ToList();
        Assert.Equal(first.ProductId, products[0].GetProperty("productId").GetInt32());
        Assert.Equal(2, products[0].GetProperty("units").GetInt32());
        Assert.Equal(2700m, products[0].GetProperty("revenue").GetProperty("current").GetDecimal());
        Assert.Equal(1000m, products[0].GetProperty("revenue").GetProperty("previous").GetDecimal());
        Assert.Equal(170.0m, products[0].GetProperty("revenue").GetProperty("percentChange").GetDecimal());

        var otherProduct = products.Single(row => row.GetProperty("productId").GetInt32() == other.ProductId);
        Assert.Equal(0m, otherProduct.GetProperty("revenue").GetProperty("previous").GetDecimal());
        Assert.Equal(JsonValueKind.Null, otherProduct.GetProperty("revenue").GetProperty("percentChange").ValueKind);
    }

    // ---- the trend -----------------------------------------------------------------------------------

    /// <summary>
    /// Twelve months monthly, or 52 ISO weeks (Monday to Sunday) weekly, ending on the day asked for; the
    /// last point is the part of its week or month so far. Revenue by paid date, orders by placed date.
    /// </summary>
    [Fact]
    public async Task The_trend_is_twelve_months_or_fifty_two_iso_weeks_ending_on_the_day_asked()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];

        // Sunday 7 and Monday 8 September 2014 are in different ISO weeks.
        var sunday = await InsertOrderAsync(catalogue, new OrderSpec(Email("sun"), Colombo(2014, 9, 7, 23, 0), Colombo(2014, 9, 7, 23, 0), OrderStatus.Paid, [new LineSpec(pick, 1, 1000m)]));
        var monday = await InsertOrderAsync(catalogue, new OrderSpec(Email("mon"), Colombo(2014, 9, 8, 1, 0), Colombo(2014, 9, 8, 1, 0), OrderStatus.Paid, [new LineSpec(pick, 1, 2000m)]));

        // Placed in the window, paid nowhere.
        await InsertOrderAsync(catalogue, new OrderSpec(Email("unpaid"), Colombo(2014, 9, 9), null, OrderStatus.Expired, [new LineSpec(pick, 1, 4000m)]));

        using var client = await host.SignInAsync(Permissions.Dashboard.View);

        // Wednesday 10 September 2014.
        var weekly = await GetJsonAsync(client, $"{TrendUrl}?interval=week&to={Day(2014, 9, 10)}");
        var weeks = weekly.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal("Week", weekly.GetProperty("interval").GetString());
        Assert.Equal(52, weeks.Count);
        Assert.Equal("2014-09-08", weeks[^1].GetProperty("start").GetString());
        Assert.Equal("2014-09-10", weeks[^1].GetProperty("end").GetString());
        Assert.Equal("2014-09-01", weeks[^2].GetProperty("start").GetString());
        Assert.Equal("2014-09-07", weeks[^2].GetProperty("end").GetString());
        Assert.Equal(sunday.GrandTotal, Money(weeks[^2].GetProperty("grossRevenue")));
        Assert.Equal(monday.GrandTotal, Money(weeks[^1].GetProperty("grossRevenue")));
        Assert.Equal(2, weeks[^1].GetProperty("orders").GetInt32());
        Assert.Equal(1, weeks[^1].GetProperty("paidOrders").GetInt32());

        // 52 consecutive Monday-starting weeks.
        Assert.All(weeks, week => Assert.Equal(DayOfWeek.Monday, DateOnly.Parse(week.GetProperty("start").GetString()!, System.Globalization.CultureInfo.InvariantCulture).DayOfWeek));
        Assert.Equal("2013-09-16", weeks[0].GetProperty("start").GetString());

        var monthly = await GetJsonAsync(client, $"{TrendUrl}?to={Day(2014, 9, 10)}");
        var months = monthly.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal("Month", monthly.GetProperty("interval").GetString());
        Assert.Equal(12, months.Count);
        Assert.Equal("2013-10-01", months[0].GetProperty("start").GetString());
        Assert.Equal("2014-09-01", months[^1].GetProperty("start").GetString());
        Assert.Equal(sunday.GrandTotal + monday.GrandTotal, Money(months[^1].GetProperty("grossRevenue")));
        Assert.Equal(3, months[^1].GetProperty("orders").GetInt32());
        Assert.Equal(0m, months.Take(11).Sum(month => Money(month.GetProperty("grossRevenue"))));
    }

    // ---- validation ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("?from=2026-10-10&to=2026-10-01", "to")]
    [InlineData("?from=2025-01-01&to=2026-01-02", "from")]       // 367 days
    [InlineData("?from=10/01/2026", "from")]
    [InlineData("?to=1999-12-31", "to")]
    [InlineData("?from=0001-01-01&to=0001-01-02", "from")]
    [InlineData("?to=9999-12-31", "to")]
    public async Task A_period_the_dashboard_cannot_answer_is_a_400_naming_the_field(string query, string field)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Dashboard.View);

        using var response = await client.GetAsync(new Uri(DashboardUrl + query, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadAsStringAsync(Token);
        Assert.Contains($"\"{field}\"", problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_366_day_period_is_answered()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Dashboard.View);

        var body = await GetJsonAsync(client, $"{DashboardUrl}?from={Day(2012, 1, 1)}&to={Day(2012, 12, 31)}");
        Assert.Equal(366, body.GetProperty("period").GetProperty("days").GetInt32());
        // 366 days before 1 January 2012 is 31 December 2010, because 2011 has 365.
        Assert.Equal("2010-12-31", body.GetProperty("previousPeriod").GetProperty("from").GetString());
    }

    [Theory]
    [InlineData("?interval=Day", "interval")]
    [InlineData("?interval=1", "interval")]
    [InlineData("?interval=fortnight", "interval")]
    [InlineData("?to=2026-13-01", "to")]
    public async Task A_trend_the_dashboard_cannot_draw_is_a_400(string query, string field)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Dashboard.View);

        using var response = await client.GetAsync(new Uri(TrendUrl + query, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    /// <summary>With no dates the period is the last 30 days, today (Colombo) included.</summary>
    [Fact]
    public async Task With_no_dates_the_period_is_the_last_30_days_including_today()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Dashboard.View);

        var body = await GetJsonAsync(client, DashboardUrl);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330));
        var to = DateOnly.Parse(body.GetProperty("period").GetProperty("to").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        // The clock can tick over Colombo midnight between the request and this line.
        Assert.InRange(to.DayNumber, today.DayNumber - 1, today.DayNumber);
        Assert.Equal(30, body.GetProperty("period").GetProperty("days").GetInt32());
    }

    private static decimal Current(JsonElement kpis, string name) => kpis.GetProperty(name).GetProperty("current").GetDecimal();

    private static decimal Previous(JsonElement kpis, string name) => kpis.GetProperty(name).GetProperty("previous").GetDecimal();
}
