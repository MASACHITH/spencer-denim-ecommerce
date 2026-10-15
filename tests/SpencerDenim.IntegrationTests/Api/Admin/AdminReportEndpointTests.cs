using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The sales and customers reports and their exports, end to end against SQL Server (ADM-19; doc 11 P16
/// step 3). The inventory report is in <see cref="AdminInventoryReportTests"/>, because it is live stock and
/// cannot be moved into a year of its own.
/// </summary>
public sealed class AdminReportEndpointTests(SqlServerDatabaseFixture sqlServer) : ReportingTestBase(sqlServer)
{
    private const string SalesUrl = "/api/admin/reports/sales";
    private const string CustomersUrl = "/api/admin/reports/customers";
    private const string InventoryUrl = "/api/admin/reports/inventory";

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    // ---- permissions ---------------------------------------------------------------------------------

    /// <summary>
    /// Reports need <c>Reports.View</c>; Dashboard.View is not enough — Order Staff see the dashboard and not
    /// the reports (doc 11 P16's last edge case).
    /// </summary>
    [Theory]
    [InlineData(SalesUrl)]
    [InlineData(CustomersUrl)]
    [InlineData(InventoryUrl)]
    public async Task Every_report_needs_Reports_View(string url)
    {
        await using var host = await StartAsync();

        using var anonymous = host.CreateAnonymousClient();
        using (var refused = await anonymous.GetAsync(new Uri(url, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using var orderStaff = await host.SignInAsync(
            Permissions.Dashboard.View,
            Permissions.Orders.View,
            Permissions.Orders.Edit,
            Permissions.Orders.Approve,
            Permissions.Customers.View,
            Permissions.Inventory.View);
        using (var refused = await orderStaff.GetAsync(new Uri(url, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using var viewer = await host.SignInAsync(Permissions.Reports.View);
        using var allowed = await viewer.GetAsync(new Uri(url, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Contains("no-store", allowed.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Viewing and exporting are separate permissions</b> (doc 11 P16's table), asserted from both sides:
    /// <c>Reports.View</c> cannot export and nothing is audited; <c>Reports.Export</c> alone exports.
    /// </summary>
    [Theory]
    [InlineData(SalesUrl)]
    [InlineData(CustomersUrl)]
    [InlineData(InventoryUrl)]
    public async Task Exporting_needs_Reports_Export_and_viewing_alone_is_refused_unaudited(string url)
    {
        await using var host = await StartAsync();
        var before = await LastAuditIdAsync();

        using var viewer = await host.SignInAsync(Permissions.Reports.View);
        using (var refused = await viewer.PostAsync(new Uri(url + "/export", UriKind.Relative), null, Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        Assert.DoesNotContain(await AuditRowsAfterAsync(before), row => row.Action == AuditActions.Export);

        using var exporter = await host.SignInAsync(Permissions.Reports.Export);
        using var allowed = await exporter.PostAsync(new Uri(url + "/export", UriKind.Relative), null, Token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("text/csv", allowed.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData(SalesUrl)]
    [InlineData(CustomersUrl)]
    [InlineData(InventoryUrl)]
    public async Task An_export_without_the_antiforgery_token_is_refused_and_not_audited(string url)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Reports.Export);
        client.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

        var before = await LastAuditIdAsync();

        using var response = await client.PostAsync(new Uri(url + "/export", UriKind.Relative), null, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await AuditRowsAfterAsync(before));
    }

    [Theory]
    [InlineData(SalesUrl + "/export?from=yesterday")]
    [InlineData(SalesUrl + "/export?groupBy=2")]
    [InlineData(CustomersUrl + "/export?to=2026-02-30")]
    [InlineData(InventoryUrl + "/export?status=1")]
    public async Task A_bad_filter_on_an_export_is_a_400_and_not_audited(string url)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Reports.Export);
        var before = await LastAuditIdAsync();

        using var response = await client.PostAsync(new Uri(url, UriKind.Relative), null, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await AuditRowsAfterAsync(before));
    }

    [Theory]
    [InlineData(SalesUrl + "?groupBy=Year", "groupBy")]
    [InlineData(SalesUrl + "?from=2026-10-02&to=2026-10-01", "to")]
    [InlineData(CustomersUrl + "?from=2020-01-01&to=2021-06-01", "from")]
    [InlineData(InventoryUrl + "?status=Low", "status")]
    public async Task A_bad_filter_on_a_report_is_a_400_naming_the_field(string url, string field)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Reports.View);

        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
    }

    // ---- the sales report ----------------------------------------------------------------------------

    /// <summary>
    /// One row per day, ISO week or month, <b>cut to the period</b> at both ends; every row's items plus
    /// delivery is its gross revenue; the total row is the sum of the rows.
    /// </summary>
    [Fact]
    public async Task The_sales_report_groups_by_day_week_or_month_cut_to_the_period()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];

        // Friday 1 April 2016 to Tuesday 3 May 2016.
        var first = await InsertOrderAsync(catalogue, new OrderSpec(Email("a"), Colombo(2016, 4, 1, 0, 5), Colombo(2016, 4, 1, 0, 5), OrderStatus.Paid, [new LineSpec(pick, 2, 1000m, Promo: 150m)], DeliveryFee: 450m, DeliveryDiscount: 50m));
        var weekTwo = await InsertOrderAsync(catalogue, new OrderSpec(Email("b"), Colombo(2016, 4, 4), Colombo(2016, 4, 5), OrderStatus.Delivered, [new LineSpec(pick, 1, 3000m)]));
        var last = await InsertOrderAsync(catalogue, new OrderSpec(Email("c"), Colombo(2016, 5, 3, 23, 50), Colombo(2016, 5, 3, 23, 50), OrderStatus.Refunded, [new LineSpec(pick, 1, 500m)], DeliveryFee: 0m));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("d"), Colombo(2016, 4, 20), null, OrderStatus.Expired, [new LineSpec(pick, 1, 9000m)]));

        // Just outside: 31 March 23:59 and 4 May 00:00, Colombo.
        await InsertOrderAsync(catalogue, new OrderSpec(Email("e"), Colombo(2016, 3, 31, 23, 59), Colombo(2016, 3, 31, 23, 59), OrderStatus.Paid, [new LineSpec(pick, 1, 77000m)]));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("f"), Colombo(2016, 5, 4, 0, 0), Colombo(2016, 5, 4, 0, 0), OrderStatus.Paid, [new LineSpec(pick, 1, 88000m)]));

        await InsertRefundAsync(last.OrderId, last.GrandTotal, Colombo(2016, 5, 3, 23, 55));

        using var client = await host.SignInAsync(Permissions.Reports.View);
        var range = $"from={Day(2016, 4, 1)}&to={Day(2016, 5, 3)}";

        var byDay = await GetJsonAsync(client, $"{SalesUrl}?{range}");
        Assert.Equal("Day", byDay.GetProperty("groupBy").GetString());
        Assert.Equal(33, byDay.GetProperty("rows").GetArrayLength());

        var weeks = (await GetJsonAsync(client, $"{SalesUrl}?{range}&groupBy=week")).GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(6, weeks.Count);
        Assert.Equal(("2016-04-01", "2016-04-03"), (weeks[0].GetProperty("start").GetString(), weeks[0].GetProperty("end").GetString()));
        Assert.Equal(("2016-04-04", "2016-04-10"), (weeks[1].GetProperty("start").GetString(), weeks[1].GetProperty("end").GetString()));
        Assert.Equal(("2016-05-02", "2016-05-03"), (weeks[^1].GetProperty("start").GetString(), weeks[^1].GetProperty("end").GetString()));

        var months = await GetJsonAsync(client, $"{SalesUrl}?{range}&groupBy=Month");
        var monthRows = months.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, monthRows.Count);
        Assert.Equal("2016-05-01", monthRows[1].GetProperty("start").GetString());
        Assert.Equal("2016-05-03", monthRows[1].GetProperty("end").GetString());

        var april = monthRows[0];
        Assert.Equal(first.GrandTotal + weekTwo.GrandTotal, Money(april.GetProperty("grossRevenue")));
        Assert.Equal(first.Items + weekTwo.Items, Money(april.GetProperty("itemsRevenue")));
        Assert.Equal(first.Delivery + weekTwo.Delivery, Money(april.GetProperty("deliveryRevenue")));
        Assert.Equal(3, april.GetProperty("orders").GetInt32());
        Assert.Equal(2, april.GetProperty("paidOrders").GetInt32());

        var may = monthRows[1];
        Assert.Equal(last.GrandTotal, Money(may.GetProperty("grossRevenue")));
        Assert.Equal(last.GrandTotal, Money(may.GetProperty("refundsCompleted")));
        Assert.Equal(0m, Money(may.GetProperty("netRevenue")));

        foreach (var row in monthRows.Concat(weeks))
        {
            Assert.Equal(
                Money(row.GetProperty("grossRevenue")),
                Money(row.GetProperty("itemsRevenue")) + Money(row.GetProperty("deliveryRevenue")));
        }

        var totals = months.GetProperty("totals");
        Assert.Equal(first.GrandTotal + weekTwo.GrandTotal + last.GrandTotal, Money(totals.GetProperty("grossRevenue")));
        Assert.Equal(4, totals.GetProperty("orders").GetInt32());
        Assert.Equal(3, totals.GetProperty("paidOrders").GetInt32());
        Assert.Equal("2016-04-01", totals.GetProperty("start").GetString());
        Assert.Equal("2016-05-03", totals.GetProperty("end").GetString());

        // Every grouping has the same totals.
        Assert.Equal(totals.GetRawText(), byDay.GetProperty("totals").GetRawText());
    }

    /// <summary>
    /// The CSV has a BOM, CRLF rows, the header, one row per period row and a Total row; the audit row
    /// records its shape. Money is plain digits with two places, so a spreadsheet can sum it.
    /// </summary>
    [Fact]
    public async Task The_sales_export_is_the_report_as_csv_and_is_audited()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var order = await InsertOrderAsync(catalogue, new OrderSpec(Email("x"), Colombo(2016, 8, 2), Colombo(2016, 8, 2), OrderStatus.Paid, [new LineSpec(catalogue.Picks[0], 1, 12345.5m)]));

        using var client = await host.SignInAsync(Permissions.Reports.Export);
        var before = await LastAuditIdAsync();

        using var response = await client.PostAsync(
            new Uri($"{SalesUrl}/export?from={Day(2016, 8, 1)}&to={Day(2016, 8, 31)}&groupBy=Week", UriKind.Relative),
            null,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sales-report-2016-08-01-to-2016-08-31.csv", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var bytes = await response.Content.ReadAsByteArrayAsync(Token);
        Assert.Equal(Bom, bytes[..3]);

        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Header, five weeks (1–7, 8–14, 15–21, 22–28, 29–31 August 2016 — the 1st was a Monday), Total.
        Assert.Equal(7, lines.Length);
        Assert.StartsWith("\"From\",\"To\",\"Orders placed\"", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("\"2016-08-01\",\"2016-08-07\",\"1\",\"1\",\"" + (order.GrandTotal).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "\"", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("\"Total\",\"2016-08-31\"", lines[^1], StringComparison.Ordinal);

        var audit = Assert.Single(await AuditRowsAfterAsync(before));
        Assert.Equal(AuditActions.Export, audit.Action);
        Assert.Equal("SalesReport", audit.EntityType);

        var shape = JsonDocument.Parse(audit.AfterJson!).RootElement;
        Assert.Equal("sales", shape.GetProperty("report").GetString());
        Assert.Equal(5, shape.GetProperty("rows").GetInt32());
        Assert.Equal("2016-08-01", shape.GetProperty("from").GetString());
        Assert.Equal("2016-08-31", shape.GetProperty("to").GetString());
        Assert.Equal("Week", shape.GetProperty("groupBy").GetString());
    }

    // ---- the customers report ------------------------------------------------------------------------

    /// <summary>
    /// Everyone who paid in the period, <b>by email address</b>, guests included; highest spend first; the
    /// name, address as typed and district from their latest paid order in the period; whether any of the
    /// orders is on an account. An order not paid in the period makes nobody a buyer.
    /// </summary>
    [Fact]
    public async Task The_customers_report_is_buyers_by_email_with_their_latest_order_s_details()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];
        var accountId = await CreateCustomerAsync(host, Email("registered"));

        await InsertOrderAsync(catalogue, new OrderSpec(Email("Registered"), Colombo(2016, 10, 2), Colombo(2016, 10, 2), OrderStatus.Delivered, [new LineSpec(pick, 1, 1000m)], District: "Kandy", FirstName: "Old", LastName: "Name", CustomerUserId: accountId));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("registered"), Colombo(2016, 10, 20), Colombo(2016, 10, 20), OrderStatus.Paid, [new LineSpec(pick, 1, 2000m)], District: "Galle", FirstName: "Dilani", LastName: "Silva"));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("guest"), Colombo(2016, 10, 21), Colombo(2016, 10, 21), OrderStatus.Paid, [new LineSpec(pick, 1, 500m)], District: "Jaffna", FirstName: "Kavitha", LastName: null));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("unpaid"), Colombo(2016, 10, 22), null, OrderStatus.Expired, [new LineSpec(pick, 1, 99000m)]));

        using var client = await host.SignInAsync(Permissions.Reports.View);
        var report = await GetJsonAsync(client, $"{CustomersUrl}?from={Day(2016, 10, 1)}&to={Day(2016, 10, 31)}");

        Assert.Equal(2, report.GetProperty("totalCount").GetInt32());
        Assert.Equal(1400m + 2400m + 900m, Money(report.GetProperty("totalRevenue")));

        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        var registered = rows[0];
        Assert.Equal(Email("registered"), registered.GetProperty("email").GetString());
        Assert.Equal("Dilani Silva", registered.GetProperty("name").GetString());
        Assert.Equal("Galle", registered.GetProperty("district").GetString());
        Assert.True(registered.GetProperty("hasAccount").GetBoolean());
        Assert.Equal(2, registered.GetProperty("paidOrders").GetInt32());
        Assert.Equal(3800m, Money(registered.GetProperty("grossRevenue")));
        Assert.Equal("2016-10-02", registered.GetProperty("firstPaidOn").GetString());
        Assert.Equal("2016-10-20", registered.GetProperty("lastPaidOn").GetString());

        var guest = rows[1];
        Assert.Equal("Kavitha", guest.GetProperty("name").GetString());
        Assert.False(guest.GetProperty("hasAccount").GetBoolean());

        // The search is the start of an address, case-insensitive; paging is clamped, never refused.
        var searched = await GetJsonAsync(client, $"{CustomersUrl}?from={Day(2016, 10, 1)}&to={Day(2016, 10, 31)}&search=GUEST.{Marker}");
        Assert.Equal(1, searched.GetProperty("totalCount").GetInt32());
        Assert.Equal(900m, Money(searched.GetProperty("totalRevenue")));

        var paged = await GetJsonAsync(client, $"{CustomersUrl}?from={Day(2016, 10, 1)}&to={Day(2016, 10, 31)}&page=2&pageSize=1");
        Assert.Equal(Email("guest"), Assert.Single(paged.GetProperty("rows").EnumerateArray()).GetProperty("email").GetString());

        var clamped = await GetJsonAsync(client, $"{CustomersUrl}?from={Day(2016, 10, 1)}&to={Day(2016, 10, 31)}&page=0&pageSize=100000");
        Assert.Equal(1, clamped.GetProperty("page").GetInt32());
        Assert.Equal(100, clamped.GetProperty("pageSize").GetInt32());
    }

    /// <summary>
    /// <b>The export neutralises a cell a spreadsheet would run as a formula</b>, carries every buyer rather
    /// than one page, and <b>its audit row never holds the search text</b> — which here is the start of an
    /// email address.
    /// </summary>
    [Fact]
    public async Task The_customers_export_neutralises_formulas_and_never_audits_the_search()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];

        var hostile = $"=HYPERLINK(\"http://evil.example\";\"x\")+{Marker}@example.com";
        await InsertOrderAsync(catalogue, new OrderSpec(hostile, Colombo(2016, 12, 5), Colombo(2016, 12, 5), OrderStatus.Paid, [new LineSpec(pick, 1, 700m)], FirstName: "-2+3", LastName: null));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("plain"), Colombo(2016, 12, 6), Colombo(2016, 12, 6), OrderStatus.Paid, [new LineSpec(pick, 1, 600m)], FirstName: "නිමාලි", LastName: "கவிதா"));

        using var client = await host.SignInAsync(Permissions.Reports.Export);
        var before = await LastAuditIdAsync();

        using var response = await client.PostAsync(
            new Uri($"{CustomersUrl}/export?from={Day(2016, 12, 1)}&to={Day(2016, 12, 31)}", UriKind.Relative),
            null,
            Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync(Token);
        Assert.Equal(Bom, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil.example\"\";\"\"x\"\")+", text, StringComparison.Ordinal);
        Assert.Contains("\"'-2+3\"", text, StringComparison.Ordinal);
        Assert.Contains("\"නිමාලි கவிதா\"", text, StringComparison.Ordinal);

        // Searched: the audit row says a search narrowed it, and nothing of what was typed.
        using var searched = await client.PostAsync(
            new Uri($"{CustomersUrl}/export?from={Day(2016, 12, 1)}&to={Day(2016, 12, 31)}&search=plain.{Marker}", UriKind.Relative),
            null,
            Token);
        Assert.Equal(HttpStatusCode.OK, searched.StatusCode);

        var audits = await AuditRowsAfterAsync(before);
        Assert.Equal(2, audits.Count);
        Assert.All(audits, row => Assert.Equal("CustomerReport", row.EntityType));

        var unsearched = JsonDocument.Parse(audits[0].AfterJson!).RootElement;
        Assert.Equal(2, unsearched.GetProperty("rows").GetInt32());
        Assert.False(unsearched.GetProperty("searchApplied").GetBoolean());

        var narrowed = JsonDocument.Parse(audits[1].AfterJson!).RootElement;
        Assert.Equal(1, narrowed.GetProperty("rows").GetInt32());
        Assert.True(narrowed.GetProperty("searchApplied").GetBoolean());

        foreach (var column in typeof(AuditLog).GetProperties().Where(property => property.PropertyType == typeof(string)))
        {
            Assert.DoesNotContain(Marker, (string?)column.GetValue(audits[1]) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("plain", (string?)column.GetValue(audits[1]) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<int> CreateCustomerAsync(MediaTestHost host, string email)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Customer,
            FirstName = "Dilani",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await users.CreateAsync(user);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        return user.Id;
    }
}
