using System.Net;
using System.Text.Json;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The customers report's chart (#186b), end to end against SQL Server: buyers per day, week or month, split
/// into first-time and returning, on the report's own population.
/// <para>
/// <b>Isolated by address, not only by year.</b> Every email here starts with this test's marker and every
/// request searches for it, so the counts are this test's own whatever other suites write — which matters
/// because "first-time" reads an address's whole history, not only the period's.
/// </para>
/// </summary>
public sealed class AdminCustomerBuyersTests(SqlServerDatabaseFixture sqlServer) : ReportingTestBase(sqlServer)
{
    private const string Url = "/api/admin/reports/customers/buyers";
    private const string ReportUrl = "/api/admin/reports/customers";

    private string Buyer(string name) => $"{Marker}.{name}@example.com";

    private string Query(DateOnly from, DateOnly to) =>
        $"{Url}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&search={Marker}";

    private static (int Buyers, int FirstTime, int Returning) Figures(JsonElement element) => (
        element.GetProperty("buyers").GetInt32(),
        element.GetProperty("firstTimeBuyers").GetInt32(),
        element.GetProperty("returningBuyers").GetInt32());

    private static JsonElement Point(JsonElement chart, string start) =>
        chart.GetProperty("points").EnumerateArray().Single(point => point.GetProperty("start").GetString() == start);

    private Task<InsertedOrder> InsertPaidAsync(CatalogueRows catalogue, string email, DateTime paidUtc, OrderStatus status = OrderStatus.Paid) =>
        InsertOrderAsync(catalogue, new OrderSpec(email, paidUtc.AddHours(-1), paidUtc, status, [new LineSpec(catalogue.Picks[0], 1, 1000m)]));

    private Task<InsertedOrder> InsertUnpaidAsync(CatalogueRows catalogue, string email, DateTime placedUtc, OrderStatus status) =>
        InsertOrderAsync(catalogue, new OrderSpec(email, placedUtc, null, status, [new LineSpec(catalogue.Picks[0], 1, 1000m)]));

    // ---- permissions and filters ---------------------------------------------------------------------

    [Fact]
    public async Task The_chart_needs_Reports_View_like_the_report_it_sits_on()
    {
        await using var host = await StartAsync();

        using var anonymous = host.CreateAnonymousClient();
        using (var refused = await anonymous.GetAsync(new Uri(Url, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // Order Staff: the dashboard, orders, customers — not the reports (doc 11 P16's last edge case).
        using var orderStaff = await host.SignInAsync(
            Permissions.Dashboard.View,
            Permissions.Orders.View,
            Permissions.Customers.View);
        using (var refused = await orderStaff.GetAsync(new Uri(Url, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using var viewer = await host.SignInAsync(Permissions.Reports.View);
        using var allowed = await viewer.GetAsync(new Uri(Url, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Contains("no-store", allowed.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?from=2013-01-01&to=2014-06-01", "from")]
    [InlineData("?from=2013-03-02&to=2013-03-01", "to")]
    [InlineData("?to=2013-02-30", "to")]
    public async Task A_bad_period_is_a_400_naming_the_field(string query, string field)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Reports.View);

        using var response = await client.GetAsync(new Uri(Url + query, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    // ---- the figures ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_period_is_every_day_at_zero()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Reports.View);

        var chart = await GetJsonAsync(client, Query(new DateOnly(2013, 3, 1), new DateOnly(2013, 3, 31)));

        Assert.Equal("Day", chart.GetProperty("interval").GetString());
        Assert.Equal((0, 0, 0), Figures(chart));
        var points = chart.GetProperty("points").EnumerateArray().ToList();
        Assert.Equal(31, points.Count);
        Assert.All(points, point => Assert.Equal((0, 0, 0), Figures(point)));
    }

    [Fact]
    public async Task A_single_buyer_is_one_first_time_buyer_on_their_day()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        await InsertPaidAsync(catalogue, Buyer("only"), Colombo(2013, 3, 10));

        using var client = await host.SignInAsync(Permissions.Reports.View);
        var chart = await GetJsonAsync(client, Query(new DateOnly(2013, 3, 1), new DateOnly(2013, 3, 31)));

        Assert.Equal((1, 1, 0), Figures(chart));
        Assert.Equal((1, 1, 0), Figures(Point(chart, "2013-03-10")));
        Assert.Equal(1, chart.GetProperty("points").EnumerateArray().Sum(point => point.GetProperty("buyers").GetInt32()));
    }

    /// <summary>
    /// <b>Colombo midnight is the boundary</b>, as on every report. A paid 23:59 on 28 February (Colombo) is
    /// history and makes the next day's buyer returning; 00:00 on 1 March is inside; 23:59 on 31 March is the
    /// last day's; 00:00 on 1 April is outside. A buyer who pays twice in the period is first-time on the
    /// first day and returning on the second — "paid before this point began".
    /// </summary>
    [Fact]
    public async Task First_time_and_returning_turn_on_the_Colombo_midnight_at_each_boundary()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();

        await InsertPaidAsync(catalogue, Buyer("before"), Colombo(2013, 2, 28, 23, 59));
        await InsertPaidAsync(catalogue, Buyer("before"), Colombo(2013, 3, 1, 0, 0));
        await InsertPaidAsync(catalogue, Buyer("new"), Colombo(2013, 3, 1, 0, 0));
        await InsertPaidAsync(catalogue, Buyer("new"), Colombo(2013, 3, 5, 9, 0));
        await InsertPaidAsync(catalogue, Buyer("last"), Colombo(2013, 3, 31, 23, 59));
        await InsertPaidAsync(catalogue, Buyer("after"), Colombo(2013, 4, 1, 0, 0));

        using var client = await host.SignInAsync(Permissions.Reports.View);
        var chart = await GetJsonAsync(client, Query(new DateOnly(2013, 3, 1), new DateOnly(2013, 3, 31)));

        // before, new and last; "after" paid on 1 April.
        Assert.Equal((3, 2, 1), Figures(chart));
        Assert.Equal((2, 1, 1), Figures(Point(chart, "2013-03-01")));
        Assert.Equal((1, 0, 1), Figures(Point(chart, "2013-03-05")));
        Assert.Equal((1, 1, 0), Figures(Point(chart, "2013-03-31")));
        Assert.Equal((0, 0, 0), Figures(Point(chart, "2013-03-02")));

        // The same population as the report: its buyer count is the chart's.
        var report = await GetJsonAsync(client, $"{ReportUrl}?from=2013-03-01&to=2013-03-31&search={Marker}");
        Assert.Equal(report.GetProperty("totalCount").GetInt32(), chart.GetProperty("buyers").GetInt32());

        // A period that starts a day earlier owns "before"'s first order, so "before" is first-time in it
        // (28 February – 30 March: 31 days, still daily; "last" on the 31st is outside it).
        var earlier = await GetJsonAsync(client, Query(new DateOnly(2013, 2, 28), new DateOnly(2013, 3, 30)));
        Assert.Equal("Day", earlier.GetProperty("interval").GetString());
        Assert.Equal((2, 2, 0), Figures(earlier));
        Assert.Equal((1, 1, 0), Figures(Point(earlier, "2013-02-28")));
        Assert.Equal((2, 1, 1), Figures(Point(earlier, "2013-03-01")));
    }

    /// <summary>
    /// <b>Only paid orders make a buyer, or a history.</b> Awaiting payment, cancelled, expired, and refunded
    /// money that was never a settlement (no paid date) are nobody; an order refunded after it was paid keeps
    /// its paid date and counts, as it does in the report's revenue. An unpaid order before the period does
    /// not make a later buyer "returning".
    /// </summary>
    [Fact]
    public async Task Unpaid_cancelled_and_expired_orders_make_nobody_a_buyer_and_no_history()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();

        await InsertUnpaidAsync(catalogue, Buyer("pending"), Colombo(2013, 3, 3), OrderStatus.PendingPayment);
        await InsertUnpaidAsync(catalogue, Buyer("cancelled"), Colombo(2013, 3, 3), OrderStatus.Cancelled);
        await InsertUnpaidAsync(catalogue, Buyer("expired"), Colombo(2013, 3, 3), OrderStatus.Expired);
        await InsertUnpaidAsync(catalogue, Buyer("refundednotpaid"), Colombo(2013, 3, 3), OrderStatus.Refunded);

        await InsertPaidAsync(catalogue, Buyer("refundedlater"), Colombo(2013, 3, 4), OrderStatus.Refunded);

        await InsertUnpaidAsync(catalogue, Buyer("triedbefore"), Colombo(2013, 2, 10), OrderStatus.Cancelled);
        await InsertUnpaidAsync(catalogue, Buyer("triedbefore"), Colombo(2013, 2, 12), OrderStatus.Expired);
        await InsertPaidAsync(catalogue, Buyer("triedbefore"), Colombo(2013, 3, 6));

        using var client = await host.SignInAsync(Permissions.Reports.View);
        var chart = await GetJsonAsync(client, Query(new DateOnly(2013, 3, 1), new DateOnly(2013, 3, 31)));

        Assert.Equal((2, 2, 0), Figures(chart));
        Assert.Equal((0, 0, 0), Figures(Point(chart, "2013-03-03")));
        Assert.Equal((1, 1, 0), Figures(Point(chart, "2013-03-04")));
        Assert.Equal((1, 1, 0), Figures(Point(chart, "2013-03-06")));
    }

    /// <summary>
    /// <b>An address is one buyer however it was typed</b> (compared as <c>EmailNormalized</c>), within a day
    /// and across history.
    /// </summary>
    [Fact]
    public async Task Email_casing_does_not_make_two_buyers_or_a_false_first_time()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();

        await InsertPaidAsync(catalogue, Buyer("Mixed"), Colombo(2013, 3, 8, 10));
        await InsertPaidAsync(catalogue, Buyer("mixed").ToUpperInvariant(), Colombo(2013, 3, 8, 15));
        await InsertPaidAsync(catalogue, Buyer("HISTORY"), Colombo(2013, 1, 10)); // 2012 belongs to AdminDashboardEndpointTests.
        await InsertPaidAsync(catalogue, Buyer("history"), Colombo(2013, 3, 9));

        using var client = await host.SignInAsync(Permissions.Reports.View);
        var chart = await GetJsonAsync(client, $"{Url}?from=2013-03-01&to=2013-03-31&search={Marker.ToUpperInvariant()}");

        Assert.Equal((2, 1, 1), Figures(chart));
        Assert.Equal((1, 1, 0), Figures(Point(chart, "2013-03-08")));
        Assert.Equal((1, 0, 1), Figures(Point(chart, "2013-03-09")));
    }

    /// <summary>
    /// <b>Weeks and months are bucketed in SQL exactly as <c>ReportBuckets.Split</c> cuts them</b>: the first,
    /// partial ISO week is Friday 1 to Sunday 3 March 2013, and Monday the 4th opens the next; the months turn
    /// at Colombo midnight on the 1st.
    /// </summary>
    [Fact]
    public async Task Weeks_and_months_match_the_report_s_own_buckets_at_their_edges()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();

        await InsertPaidAsync(catalogue, Buyer("sunday"), Colombo(2013, 3, 3, 23, 59));
        await InsertPaidAsync(catalogue, Buyer("monday"), Colombo(2013, 3, 4, 0, 0));
        await InsertPaidAsync(catalogue, Buyer("monday"), Colombo(2013, 3, 10, 23, 0));
        await InsertPaidAsync(catalogue, Buyer("january"), Colombo(2013, 1, 31, 23, 59));
        await InsertPaidAsync(catalogue, Buyer("february"), Colombo(2013, 2, 1, 0, 0));

        using var client = await host.SignInAsync(Permissions.Reports.View);

        // 1 March – 15 May: 76 days, so weekly.
        var weekly = await GetJsonAsync(client, Query(new DateOnly(2013, 3, 1), new DateOnly(2013, 5, 15)));
        Assert.Equal("Week", weekly.GetProperty("interval").GetString());
        var first = weekly.GetProperty("points")[0];
        Assert.Equal("2013-03-03", first.GetProperty("end").GetString());
        Assert.Equal((1, 1, 0), Figures(first));
        var second = weekly.GetProperty("points")[1];
        Assert.Equal("2013-03-04", second.GetProperty("start").GetString());
        Assert.Equal("2013-03-10", second.GetProperty("end").GetString());
        Assert.Equal((1, 1, 0), Figures(second)); // "monday" twice in one week is one buyer, first-time.
        Assert.Equal(12, weekly.GetProperty("points").GetArrayLength()); // ... 29 Apr – 5 May, 6–12 May, 13–15 May.

        // The whole year: monthly.
        var monthly = await GetJsonAsync(client, Query(new DateOnly(2013, 1, 1), new DateOnly(2013, 12, 31)));
        Assert.Equal("Month", monthly.GetProperty("interval").GetString());
        Assert.Equal(12, monthly.GetProperty("points").GetArrayLength());
        Assert.Equal((1, 1, 0), Figures(monthly.GetProperty("points")[0]));
        Assert.Equal((1, 1, 0), Figures(monthly.GetProperty("points")[1]));
        Assert.Equal((2, 2, 0), Figures(monthly.GetProperty("points")[2]));
        Assert.Equal((4, 4, 0), Figures(monthly));
    }

    [Fact]
    public async Task The_search_narrows_the_chart_as_it_narrows_the_report()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();

        await InsertPaidAsync(catalogue, Buyer("alpha"), Colombo(2013, 3, 12));
        await InsertPaidAsync(catalogue, Buyer("beta"), Colombo(2013, 3, 12));

        using var client = await host.SignInAsync(Permissions.Reports.View);
        var narrowed = await GetJsonAsync(client, $"{Url}?from=2013-03-01&to=2013-03-31&search={Marker}.ALPHA");
        var report = await GetJsonAsync(client, $"{ReportUrl}?from=2013-03-01&to=2013-03-31&search={Marker}.ALPHA");

        Assert.Equal((1, 1, 0), Figures(narrowed));
        Assert.Equal(1, report.GetProperty("totalCount").GetInt32());
    }
}
