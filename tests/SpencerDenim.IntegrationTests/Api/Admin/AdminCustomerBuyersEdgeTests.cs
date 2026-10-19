using System.Net;
using System.Text.Json;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The tester's edge cases for the customers chart (#186b), each worked out by hand before it was run:
/// ISO weeks across a year end, months across the same data, the leap day, the 31/32 and 182/183-day
/// grouping edges against SQL Server, LIKE wildcards in the search, more roles, and the 400s matching the
/// report endpoint field for field. Every request searches for this test's marker, as in
/// <see cref="AdminCustomerBuyersTests"/>, because "first-time" reads an address's whole history.
/// </summary>
public sealed class AdminCustomerBuyersEdgeTests(SqlServerDatabaseFixture sqlServer) : ReportingTestBase(sqlServer)
{
    private const string Url = "/api/admin/reports/customers/buyers";
    private const string ReportUrl = "/api/admin/reports/customers";

    private string Buyer(string name) => $"{Marker}.{name}@example.com";

    private string Query(string url, DateOnly from, DateOnly to, string? search = null) =>
        $"{url}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&search={Uri.EscapeDataString(search ?? Marker)}";

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

    private async Task AssertSameAsReportAsync(HttpClient client, JsonElement chart, DateOnly from, DateOnly to, string? search = null)
    {
        var report = await GetJsonAsync(client, Query(ReportUrl, from, to, search));
        Assert.Equal(report.GetProperty("totalCount").GetInt32(), chart.GetProperty("buyers").GetInt32());
    }

    private static void AssertEveryPointAddsUp(JsonElement chart)
    {
        foreach (var point in chart.GetProperty("points").EnumerateArray())
        {
            var (buyers, first, returning) = Figures(point);
            Assert.True(first >= 0 && returning >= 0 && first + returning == buyers, point.ToString());
        }

        var firstSum = chart.GetProperty("points").EnumerateArray().Sum(point => point.GetProperty("firstTimeBuyers").GetInt32());
        Assert.Equal(chart.GetProperty("firstTimeBuyers").GetInt32(), firstSum);
    }

    /// <summary>
    /// 1 December 2035 – 15 February 2036 is 77 days, so weekly. ISO week 2036-W01 runs Monday 31 December 2035
    /// to Sunday 6 January 2036 and is <b>not</b> cut at the year end. Worked by hand:
    /// <list type="bullet">
    /// <item>x: 31 Dec 23:59 and 1 Jan 00:01 — both in that week, one first-time buyer there.</item>
    /// <item>y: Sun 6 Jan 23:59 (that week, first-time) and Mon 7 Jan 00:01 (next week, returning).</item>
    /// <item>old: paid in 2029, then 2 Jan — returning.</item>
    /// <item>refunded: first order 31 Dec, refunded after it was paid (still first-time); again 8 Jan (returning).</item>
    /// <item>tried: a cancelled, never-paid order in November, first paid 9 Jan — first-time.</item>
    /// </list>
    /// 31 Dec – 6 Jan = 4 buyers (3 first-time, 1 returning); 7–13 Jan = 3 (1, 2). Period: 5 buyers, 4 first-time.
    /// Monthly over 1 July 2035 – 30 June 2036 (366 days, 2036 is a leap year): December = x, refunded (2, 2, 0);
    /// January = x, y, old, refunded, tried (5, 2, 3).
    /// <para>2029 and 2035–2036 are years no other test writes orders into (the base class's one-year-per-test
    /// rule: the dashboard tests read whole-shop figures for their own year).</para>
    /// </summary>
    [Fact]
    public async Task An_ISO_week_across_the_year_end_and_the_same_orders_by_month()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();

        await InsertPaidAsync(catalogue, Buyer("x"), Colombo(2035, 12, 31, 23, 59));
        await InsertPaidAsync(catalogue, Buyer("x"), Colombo(2036, 1, 1, 0, 1));
        await InsertPaidAsync(catalogue, Buyer("y"), Colombo(2036, 1, 6, 23, 59));
        await InsertPaidAsync(catalogue, Buyer("y"), Colombo(2036, 1, 7, 0, 1));
        await InsertPaidAsync(catalogue, Buyer("old"), Colombo(2029, 5, 5));
        await InsertPaidAsync(catalogue, Buyer("old"), Colombo(2036, 1, 2));
        await InsertPaidAsync(catalogue, Buyer("refunded"), Colombo(2035, 12, 31, 10), OrderStatus.Refunded);
        await InsertPaidAsync(catalogue, Buyer("refunded"), Colombo(2036, 1, 8));
        await InsertUnpaidAsync(catalogue, Buyer("tried"), Colombo(2035, 11, 1), OrderStatus.Cancelled);
        await InsertPaidAsync(catalogue, Buyer("tried"), Colombo(2036, 1, 9));

        using var client = await host.SignInAsync(Permissions.Reports.View);

        var from = new DateOnly(2035, 12, 1);
        var to = new DateOnly(2036, 2, 15);
        var weekly = await GetJsonAsync(client, Query(Url, from, to));

        Assert.Equal("Week", weekly.GetProperty("interval").GetString());
        // Sat 1 – Sun 2 Dec, then Mondays 3, 10, 17, 24, 31 Dec, 7, 14, 21, 28 Jan, 4, 11 Feb.
        Assert.Equal(12, weekly.GetProperty("points").GetArrayLength());
        var acrossYear = Point(weekly, "2035-12-31");
        Assert.Equal("2036-01-06", acrossYear.GetProperty("end").GetString());
        Assert.Equal((4, 3, 1), Figures(acrossYear));
        Assert.Equal((3, 1, 2), Figures(Point(weekly, "2036-01-07")));
        Assert.Equal((0, 0, 0), Figures(Point(weekly, "2035-12-24")));
        Assert.Equal((5, 4, 1), Figures(weekly));
        AssertEveryPointAddsUp(weekly);
        await AssertSameAsReportAsync(client, weekly, from, to);

        var yearFrom = new DateOnly(2035, 7, 1);
        var yearTo = new DateOnly(2036, 6, 30);
        var monthly = await GetJsonAsync(client, Query(Url, yearFrom, yearTo));

        Assert.Equal("Month", monthly.GetProperty("interval").GetString());
        Assert.Equal(366, monthly.GetProperty("period").GetProperty("days").GetInt32());
        Assert.Equal(12, monthly.GetProperty("points").GetArrayLength());
        Assert.Equal((2, 2, 0), Figures(Point(monthly, "2035-12-01")));
        Assert.Equal((5, 2, 3), Figures(Point(monthly, "2036-01-01")));
        Assert.Equal((5, 4, 1), Figures(monthly));
        AssertEveryPointAddsUp(monthly);
        await AssertSameAsReportAsync(client, monthly, yearFrom, yearTo);

        // Daily, 22 December – 11 January (21 days).
        var daily = await GetJsonAsync(client, Query(Url, new DateOnly(2035, 12, 22), new DateOnly(2036, 1, 11)));
        Assert.Equal("Day", daily.GetProperty("interval").GetString());
        Assert.Equal((2, 2, 0), Figures(Point(daily, "2035-12-31")));
        Assert.Equal((1, 0, 1), Figures(Point(daily, "2036-01-01")));
        Assert.Equal((1, 1, 0), Figures(Point(daily, "2036-01-06")));
        Assert.Equal((1, 0, 1), Figures(Point(daily, "2036-01-07")));
        Assert.Equal((5, 4, 1), Figures(daily));
        AssertEveryPointAddsUp(daily);
    }

    /// <summary>
    /// The leap day and the grouping edges, against the real query, in 2032 (a leap year no other test uses).
    /// 1 Feb – 2 Mar is 31 days (29 in February), so daily; to 3 March is 32, so weekly; 1 Jan – 30 Jun is 182,
    /// weekly; to 1 July is 183, monthly. "leap" pays at 23:59 on Sunday 29 February — the last day of an ISO
    /// week — and 00:01 on Monday 1 March.
    /// </summary>
    [Fact]
    public async Task The_leap_day_and_the_31_32_182_183_day_edges()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();

        await InsertPaidAsync(catalogue, Buyer("leap"), Colombo(2032, 2, 29, 23, 59));
        await InsertPaidAsync(catalogue, Buyer("leap"), Colombo(2032, 3, 1, 0, 1));
        await InsertPaidAsync(catalogue, Buyer("jan1"), Colombo(2032, 1, 1, 0, 0));

        using var client = await host.SignInAsync(Permissions.Reports.View);

        var d31 = await GetJsonAsync(client, Query(Url, new DateOnly(2032, 2, 1), new DateOnly(2032, 3, 2)));
        Assert.Equal("Day", d31.GetProperty("interval").GetString());
        Assert.Equal(31, d31.GetProperty("points").GetArrayLength());
        Assert.Equal((1, 1, 0), Figures(Point(d31, "2032-02-29")));
        Assert.Equal((1, 0, 1), Figures(Point(d31, "2032-03-01")));
        Assert.Equal((1, 1, 0), Figures(d31));

        var d32 = await GetJsonAsync(client, Query(Url, new DateOnly(2032, 2, 1), new DateOnly(2032, 3, 3)));
        Assert.Equal("Week", d32.GetProperty("interval").GetString());
        // Sun 1 Feb alone, then Mondays 2, 9, 16, 23 Feb and 1 Mar (cut at Wed 3 Mar).
        Assert.Equal(6, d32.GetProperty("points").GetArrayLength());
        var leapWeek = Point(d32, "2032-02-23");
        Assert.Equal("2032-02-29", leapWeek.GetProperty("end").GetString());
        Assert.Equal((1, 1, 0), Figures(leapWeek));
        Assert.Equal((1, 0, 1), Figures(Point(d32, "2032-03-01")));

        var d182 = await GetJsonAsync(client, Query(Url, new DateOnly(2032, 1, 1), new DateOnly(2032, 6, 30)));
        Assert.Equal("Week", d182.GetProperty("interval").GetString());
        Assert.Equal(182, d182.GetProperty("period").GetProperty("days").GetInt32());
        // Thursday 1 – Sunday 4 January is the first, partial week (its Monday is 29 December 2031).
        var first = d182.GetProperty("points")[0];
        Assert.Equal("2032-01-04", first.GetProperty("end").GetString());
        Assert.Equal((1, 1, 0), Figures(first));
        Assert.Equal((1, 1, 0), Figures(Point(d182, "2032-02-23")));
        Assert.Equal((1, 0, 1), Figures(Point(d182, "2032-03-01")));
        Assert.Equal(27, d182.GetProperty("points").GetArrayLength());
        Assert.Equal((2, 2, 0), Figures(d182));
        AssertEveryPointAddsUp(d182);

        var d183 = await GetJsonAsync(client, Query(Url, new DateOnly(2032, 1, 1), new DateOnly(2032, 7, 1)));
        Assert.Equal("Month", d183.GetProperty("interval").GetString());
        Assert.Equal(7, d183.GetProperty("points").GetArrayLength());
        Assert.Equal((1, 1, 0), Figures(Point(d183, "2032-01-01")));
        Assert.Equal((1, 1, 0), Figures(Point(d183, "2032-02-01")));
        Assert.Equal((1, 0, 1), Figures(Point(d183, "2032-03-01")));
        Assert.Equal("2032-07-01", d183.GetProperty("points")[6].GetProperty("end").GetString());
        Assert.Equal((2, 2, 0), Figures(d183));
        await AssertSameAsReportAsync(client, d183, new DateOnly(2032, 1, 1), new DateOnly(2032, 7, 1));
    }

    /// <summary>
    /// A month bucket on a period that starts mid-month: 15 January – 14 August 2023 (a year no other test uses) (212 days, monthly).
    /// The first bar is 15–31 January; an order on 1 February at 00:00 Colombo (31 January 18:30 UTC) is
    /// February's, not January's.
    /// </summary>
    [Fact]
    public async Task A_mid_month_period_cuts_its_first_month_and_turns_at_the_Colombo_first()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();

        await InsertPaidAsync(catalogue, Buyer("jan"), Colombo(2023, 1, 31, 23, 59));
        await InsertPaidAsync(catalogue, Buyer("feb"), Colombo(2023, 2, 1, 0, 0));
        await InsertPaidAsync(catalogue, Buyer("aug"), Colombo(2023, 8, 14, 23, 59));
        await InsertPaidAsync(catalogue, Buyer("early"), Colombo(2023, 1, 14, 23, 59));

        using var client = await host.SignInAsync(Permissions.Reports.View);
        var chart = await GetJsonAsync(client, Query(Url, new DateOnly(2023, 1, 15), new DateOnly(2023, 8, 14)));

        Assert.Equal("Month", chart.GetProperty("interval").GetString());
        Assert.Equal(8, chart.GetProperty("points").GetArrayLength());
        Assert.Equal((1, 1, 0), Figures(Point(chart, "2023-01-15")));
        Assert.Equal((1, 1, 0), Figures(Point(chart, "2023-02-01")));
        Assert.Equal((1, 1, 0), Figures(Point(chart, "2023-08-01")));
        Assert.Equal((3, 3, 0), Figures(chart));
    }

    /// <summary>
    /// The search is a parameter, and LIKE's own characters in it are literal: <c>_</c> matches only an
    /// underscore, <c>%</c> and <c>[</c> match nothing here, and a quote is harmless. The chart and the report
    /// agree on every one.
    /// </summary>
    [Theory]
    [InlineData(".a_", 1)]
    [InlineData(".a", 2)]
    [InlineData(".a%", 0)]
    [InlineData(".[a]", 0)]
    [InlineData(".a'; DROP TABLE sales.Orders; --", 0)]
    [InlineData("", 3)]
    public async Task The_search_is_a_literal_email_prefix(string suffix, int expected)
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();

        await InsertPaidAsync(catalogue, Buyer("a_b"), Colombo(2018, 4, 10));
        await InsertPaidAsync(catalogue, Buyer("axb"), Colombo(2018, 4, 11));
        await InsertPaidAsync(catalogue, Buyer("zed"), Colombo(2018, 4, 12));

        using var client = await host.SignInAsync(Permissions.Reports.View);
        var from = new DateOnly(2018, 4, 1);
        var to = new DateOnly(2018, 4, 30);
        var search = Marker + suffix;

        var chart = await GetJsonAsync(client, Query(Url, from, to, search));

        Assert.Equal(expected, chart.GetProperty("buyers").GetInt32());
        Assert.Equal(expected, chart.GetProperty("firstTimeBuyers").GetInt32());
        await AssertSameAsReportAsync(client, chart, from, to, search);
    }

    /// <summary>
    /// The search is the <b>start</b> of an address, as the report's is: the middle of this test's marker
    /// matches nobody, in the chart and in the report alike.
    /// </summary>
    [Fact]
    public async Task The_middle_of_an_address_is_not_a_match()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        await InsertPaidAsync(catalogue, Buyer("mid"), Colombo(2018, 7, 10));

        using var client = await host.SignInAsync(Permissions.Reports.View);
        var from = new DateOnly(2018, 7, 1);
        var to = new DateOnly(2018, 7, 31);
        var middle = Marker[2..];

        var chart = await GetJsonAsync(client, Query(Url, from, to, middle));

        Assert.Equal((0, 0, 0), Figures(chart));
        Assert.All(chart.GetProperty("points").EnumerateArray(), point => Assert.Equal((0, 0, 0), Figures(point)));
        await AssertSameAsReportAsync(client, chart, from, to, middle);
    }

    /// <summary>A bare <c>%</c> is not "everyone": with the LIKE escaped it matches no address at all.</summary>
    [Fact]
    public async Task A_bare_percent_search_matches_nobody()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        await InsertPaidAsync(catalogue, Buyer("p"), Colombo(2018, 6, 10));

        using var client = await host.SignInAsync(Permissions.Reports.View);
        var chart = await GetJsonAsync(client, Query(Url, new DateOnly(2018, 6, 1), new DateOnly(2018, 6, 30), "%"));

        Assert.Equal((0, 0, 0), Figures(chart));
    }

    /// <summary>Doc 08 §9: Content Editor and Order Staff have no Reports.View; Export alone is not View.</summary>
    [Fact]
    public async Task Roles_without_Reports_View_are_refused_and_Store_Manager_is_not()
    {
        await using var host = await StartAsync();

        foreach (var role in new[] { DefaultRoleDefinitions.ContentEditor, DefaultRoleDefinitions.OrderStaff })
        {
            using var client = await host.SignInAsync([.. role.Permissions]);
            using var refused = await client.GetAsync(new Uri(Url, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using (var exportOnly = await host.SignInAsync(Permissions.Reports.Export))
        using (var refused = await exportOnly.GetAsync(new Uri(Url, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using var manager = await host.SignInAsync([.. DefaultRoleDefinitions.StoreManager.Permissions]);
        using var allowed = await manager.GetAsync(new Uri(Url, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>Every bad period answers exactly as the customers report does: the same status and body.</summary>
    [Theory]
    [InlineData("?from=abc")]
    [InlineData("?to=2013-13-01")]
    [InlineData("?from=1999-12-31&to=2000-01-10")]
    [InlineData("?to=2100-01-01")]
    [InlineData("?from=2013-03-02&to=2013-03-01")]
    [InlineData("?from=2013-01-01&to=2014-01-02")]
    [InlineData("?from=2013/01/01&to=2013-02-01")]
    [InlineData("?from=2013-01-01T00:00&to=2013-02-01")]
    public async Task A_bad_period_is_the_same_400_as_the_report_s(string query)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Reports.View);

        using var chart = await client.GetAsync(new Uri(Url + query, UriKind.Relative), Token);
        using var report = await client.GetAsync(new Uri(ReportUrl + query, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.BadRequest, chart.StatusCode);
        Assert.Equal(report.StatusCode, chart.StatusCode);

        var chartErrors = (await GetErrorsAsync(chart)).ToString();
        var reportErrors = (await GetErrorsAsync(report)).ToString();
        Assert.Equal(reportErrors, chartErrors);
    }

    /// <summary>366 days is the longest period, and it is twelve or thirteen monthly bars.</summary>
    [Fact]
    public async Task The_longest_period_is_accepted_and_monthly()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Reports.View);

        var chart = await GetJsonAsync(client, Query(Url, new DateOnly(2013, 1, 15), new DateOnly(2014, 1, 15)));

        Assert.Equal("Month", chart.GetProperty("interval").GetString());
        Assert.Equal(13, chart.GetProperty("points").GetArrayLength());
    }

    private static async Task<JsonElement> GetErrorsAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        return document.RootElement.GetProperty("errors").Clone();
    }
}
