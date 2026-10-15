using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Analytics;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Api.Admin;
using SpencerDenim.IntegrationTests.Infrastructure;
using static SpencerDenim.IntegrationTests.Analytics.TrafficRows;

namespace SpencerDenim.IntegrationTests.Analytics;

/// <summary>
/// <c>GET /api/admin/traffic</c> (ADM-20) and <c>GET /api/admin/dashboard/traffic-sources</c> (ADM-06) against
/// SQL Server, through the real API: the permission, the validation, the figures from the stored days, the live
/// window, the conversion rate, and an empty period that says it is empty.
/// <para>
/// Past periods are in 2003 and 2004, which the rollup tests also use but for other months; the live tests own
/// their rows by a marker unique to the test.
/// </para>
/// </summary>
public sealed class AdminTrafficEndpointTests(SqlServerDatabaseFixture sqlServer) : ReportingTestBase(sqlServer)
{
    private const string TrafficUrl = "/api/admin/traffic";
    private const string SourcesUrl = "/api/admin/dashboard/traffic-sources";

    [Theory]
    [InlineData(TrafficUrl)]
    [InlineData(SourcesUrl)]
    public async Task Both_routes_need_Traffic_View_and_the_dashboard_permission_is_not_enough(string url)
    {
        await using var host = await StartAsync();

        using var anonymous = host.CreateAnonymousClient();
        using (var refused = await anonymous.GetAsync(new Uri(url, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        using var dashboardOnly = await host.SignInAsync(Permissions.Dashboard.View, Permissions.Reports.View);
        using (var refused = await dashboardOnly.GetAsync(new Uri(url, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using var traffic = await host.SignInAsync(Permissions.Traffic.View);
        using var allowed = await traffic.GetAsync(new Uri(url, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Contains("no-store", allowed.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("?from=2003-13-01", "from")]
    [InlineData("?to=yesterday", "to")]
    [InlineData("?from=0001-01-01&to=0001-01-31", "from")]
    [InlineData("?from=2003-03-31&to=2003-03-01", "to")]
    [InlineData("?from=2003-01-01&to=2004-12-31", "from")]
    public async Task A_bad_period_is_a_400_naming_the_field(string query, string field)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Traffic.View);

        foreach (var url in new[] { TrafficUrl, SourcesUrl })
        {
            using var response = await client.GetAsync(new Uri(url + query, UriKind.Relative), Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
            Assert.True(body.GetProperty("errors").TryGetProperty(field, out _), $"{url}{query}: {body}");
        }
    }

    [Fact]
    public async Task A_past_period_reports_visits_channels_devices_sources_and_top_pages_from_the_stored_days()
    {
        await using var host = await StartAsync();
        await ClearYearAsync(SqlServer, 2003, Token);

        var product = await FirstProductAsync();
        var google = Guid.NewGuid();
        var facebook = Guid.NewGuid();
        var direct = Guid.NewGuid();
        var february = Guid.NewGuid();

        await InsertAsync(
            SqlServer,
            [
                // 3 March: a phone from Google looks at a product and a listing; a desktop types the address in.
                View(Colombo(2003, 3, 3, 9), google, "/", TrafficChannel.Organic, DeviceType.Mobile, referrerHost: "www.google.com"),
                View(Colombo(2003, 3, 3, 9, 5), google, $"/product/{product.Slug}", TrafficChannel.Direct, DeviceType.Mobile, pageType: PageType.Product, productId: product.Id),
                View(Colombo(2003, 3, 3, 9, 9), google, "/women", TrafficChannel.Direct, DeviceType.Mobile, pageType: PageType.Department),
                View(Colombo(2003, 3, 3, 20), direct, $"/product/{product.Slug}", TrafficChannel.Direct, DeviceType.Desktop, pageType: PageType.Product, productId: product.Id),

                // 20 March: a tablet from a Facebook campaign; and a crawler.
                View(Colombo(2003, 3, 20, 14), facebook, "/sale", TrafficChannel.Social, DeviceType.Tablet, utmSource: "facebook", referrerHost: "l.facebook.com", pageType: PageType.Sale),
                View(Colombo(2003, 3, 20, 15), Guid.NewGuid(), "/sale", TrafficChannel.Direct, DeviceType.Other, bot: true, pageType: PageType.Sale),

                // The previous period (the 31 days before 1 March): one visit of two page views.
                View(Colombo(2003, 2, 10, 10), february, "/", TrafficChannel.Direct, DeviceType.Desktop),
                View(Colombo(2003, 2, 10, 10, 1), february, "/sale", TrafficChannel.Direct, DeviceType.Desktop, pageType: PageType.Sale),
            ],
            Token);

        using (var scope = SqlServer.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TrafficRollup>().RebuildAsync(new DateOnly(2003, 1, 1), new DateOnly(2003, 3, 31), Token);
        }

        using var client = await host.SignInAsync(Permissions.Traffic.View);
        var body = await GetJsonAsync(client, $"{TrafficUrl}?from=2003-03-01&to=2003-03-31");

        Assert.Equal(3m, body.GetProperty("visits").GetProperty("current").GetDecimal());
        Assert.Equal(1m, body.GetProperty("visits").GetProperty("previous").GetDecimal());
        Assert.Equal(200.0m, body.GetProperty("visits").GetProperty("percentChange").GetDecimal());
        Assert.Equal(5m, body.GetProperty("pageViews").GetProperty("current").GetDecimal());
        Assert.Equal(2m, body.GetProperty("pageViews").GetProperty("previous").GetDecimal());
        Assert.Equal(1, body.GetProperty("botPageViews").GetInt32());
        Assert.Equal("2003-02-28", body.GetProperty("previousPeriod").GetProperty("to").GetString());

        var days = body.GetProperty("days").EnumerateArray().ToList();
        Assert.Equal(31, days.Count);
        Assert.Equal(("2003-03-03", 2, 4), Day(days[2]));
        Assert.Equal(("2003-03-20", 1, 1), Day(days[19]));
        Assert.Equal(3, days.Sum(day => day.GetProperty("visits").GetInt32()));
        Assert.Equal(1, days[19].GetProperty("botPageViews").GetInt32());

        Assert.Equal(
            [("Direct", 1, 1), ("Organic", 1, 3), ("Social", 1, 1), ("Referral", 0, 0), ("Email", 0, 0), ("Paid", 0, 0), ("Other", 0, 0)],
            body.GetProperty("channels").EnumerateArray().Select(row => (row.GetProperty("channel").GetString(), row.GetProperty("visits").GetInt32(), row.GetProperty("pageViews").GetInt32())));
        Assert.Equal(33.3m, body.GetProperty("channels")[0].GetProperty("sharePercent").GetDecimal());

        Assert.Equal(
            [("Desktop", 1), ("Mobile", 1), ("Tablet", 1), ("Other", 0)],
            body.GetProperty("devices").EnumerateArray().Select(row => (row.GetProperty("device").GetString(), row.GetProperty("visits").GetInt32())));

        Assert.Equal(
            [("(direct)", 1, 1), ("facebook", 1, 1), ("www.google.com", 1, 3)],
            body.GetProperty("sources").EnumerateArray()
                .Select(row => (row.GetProperty("source").GetString(), row.GetProperty("visits").GetInt32(), row.GetProperty("pageViews").GetInt32()))
                .OrderBy(row => row.Item1, StringComparer.Ordinal));

        var top = body.GetProperty("topPages").EnumerateArray().ToList();
        Assert.Equal($"/product/{product.Slug}", top[0].GetProperty("path").GetString());
        Assert.Equal("Product", top[0].GetProperty("pageType").GetString());
        Assert.Equal(product.Name, top[0].GetProperty("productName").GetString());
        Assert.Equal((2, 2), (top[0].GetProperty("pageViews").GetInt32(), top[0].GetProperty("visits").GetInt32()));
        Assert.Equal(4, top.Count);
        Assert.Equal(5, top.Sum(page => page.GetProperty("pageViews").GetInt32()));
    }

    [Fact]
    public async Task An_empty_period_says_it_is_empty_rather_than_inventing_figures()
    {
        await using var host = await StartAsync();
        await ClearYearAsync(SqlServer, 2004, Token);

        using var client = await host.SignInAsync(Permissions.Traffic.View);
        var body = await GetJsonAsync(client, $"{TrafficUrl}?from=2004-11-01&to=2004-11-30");

        Assert.Equal(0m, body.GetProperty("visits").GetProperty("current").GetDecimal());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("visits").GetProperty("percentChange").ValueKind);
        Assert.Equal(30, body.GetProperty("days").GetArrayLength());
        Assert.All(body.GetProperty("days").EnumerateArray(), day => Assert.Equal(0, day.GetProperty("pageViews").GetInt32()));
        Assert.All(body.GetProperty("channels").EnumerateArray(), row => Assert.Equal(0m, row.GetProperty("sharePercent").GetDecimal()));
        Assert.Equal(0, body.GetProperty("sources").GetArrayLength());
        Assert.Equal(0, body.GetProperty("topPages").GetArrayLength());

        var sources = await GetJsonAsync(client, $"{SourcesUrl}?from=2004-11-01&to=2004-11-30");
        Assert.Equal(0, sources.GetProperty("visits").GetInt32());
        Assert.Equal(JsonValueKind.Null, sources.GetProperty("conversionPercent").ValueKind);
    }

    [Fact]
    public async Task Today_is_counted_live_before_any_rollup_and_the_answer_says_from_which_day()
    {
        await using var host = await StartAsync();

        // Rows of this test's own, recognisable by their source and path, written "now".
        var marker = "w403live" + Guid.NewGuid().ToString("N")[..8];
        var now = DateTime.UtcNow.AddMinutes(-1);
        var views = new List<PageView>();
        for (var i = 0; i < 30; i++)
        {
            var session = Guid.NewGuid();
            views.Add(View(now, session, $"/product/{marker}", TrafficChannel.Referral, DeviceType.Mobile, utmSource: marker, pageType: PageType.Product));
            views.Add(View(now, session, $"/product/{marker}", TrafficChannel.Direct, DeviceType.Mobile, pageType: PageType.Product));
        }

        await InsertAsync(SqlServer, views, Token);

        using var client = await host.SignInAsync(Permissions.Traffic.View);
        var body = await GetJsonAsync(client, TrafficUrl);

        var today = Domain.Common.ColomboTime.ToLocal(DateTimeOffset.UtcNow).Date;
        Assert.Equal(DateOnly.FromDateTime(today).AddDays(-3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), body.GetProperty("liveFrom").GetString());

        var source = body.GetProperty("sources").EnumerateArray().Single(row => row.GetProperty("source").GetString() == marker);
        Assert.Equal((30, 60), (source.GetProperty("visits").GetInt32(), source.GetProperty("pageViews").GetInt32()));

        var page = body.GetProperty("topPages").EnumerateArray().Single(row => row.GetProperty("path").GetString() == $"/product/{marker}");
        Assert.Equal((60, 30), (page.GetProperty("pageViews").GetInt32(), page.GetProperty("visits").GetInt32()));
        var nowDay = DateOnly.FromDateTime(Domain.Common.ColomboTime.ToLocal(new DateTimeOffset(now, TimeSpan.Zero)).DateTime).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(body.GetProperty("days").EnumerateArray().Single(day => day.GetProperty("date").GetString() == nowDay).GetProperty("visits").GetInt32() >= 30);
    }

    [Fact]
    public async Task The_dashboard_widget_gives_visits_by_channel_and_paid_orders_per_visit()
    {
        await using var host = await StartAsync();
        await ClearYearAsync(SqlServer, 2003, Token);

        await InsertAsync(
            SqlServer,
            [
                View(Colombo(2003, 8, 4, 9), Guid.NewGuid(), "/", TrafficChannel.Email, utmSource: "newsletter"),
                View(Colombo(2003, 8, 5, 9), Guid.NewGuid(), "/", TrafficChannel.Paid, utmSource: "google"),
                View(Colombo(2003, 8, 6, 9), Guid.NewGuid(), "/"),
                View(Colombo(2003, 8, 7, 9), Guid.NewGuid(), "/"),

                // Crawlers and link previews, each in a "session" of its own: doc 08 §8.1 divides by human
                // sessions, so these must not dilute the rate (W4-03 tester: nothing else held that).
                View(Colombo(2003, 8, 4, 10), Guid.NewGuid(), "/", bot: true),
                View(Colombo(2003, 8, 5, 10), Guid.NewGuid(), "/", TrafficChannel.Referral, bot: true, referrerHost: "l.facebook.com"),
                View(Colombo(2003, 8, 6, 10), Guid.NewGuid(), "/", bot: true),
                View(Colombo(2003, 8, 7, 10), Guid.NewGuid(), "/", bot: true),
            ],
            Token);

        using (var scope = SqlServer.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<TrafficRollup>().RebuildAsync(new DateOnly(2003, 8, 1), new DateOnly(2003, 8, 31), Token);
        }

        var catalogue = await CatalogueAsync();
        var pick = catalogue.Picks[0];
        await InsertOrderAsync(catalogue, new OrderSpec(Email("paid"), Colombo(2003, 8, 5, 10), Colombo(2003, 8, 5, 11), OrderStatus.Paid, [new LineSpec(pick, 1, 1500m)]));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("unpaid"), Colombo(2003, 8, 6, 10), null, OrderStatus.Cancelled, [new LineSpec(pick, 1, 1500m)]));

        using var client = await host.SignInAsync(Permissions.Traffic.View);
        var body = await GetJsonAsync(client, $"{SourcesUrl}?from=2003-08-01&to=2003-08-31");

        Assert.Equal(4, body.GetProperty("visits").GetInt32());
        Assert.Equal(1, body.GetProperty("paidOrders").GetInt32());
        Assert.Equal(25.0m, body.GetProperty("conversionPercent").GetDecimal());
        Assert.Equal(
            [("Direct", 2), ("Organic", 0), ("Social", 0), ("Referral", 0), ("Email", 1), ("Paid", 1), ("Other", 0)],
            body.GetProperty("channels").EnumerateArray().Select(row => (row.GetProperty("channel").GetString(), row.GetProperty("visits").GetInt32())));
    }

    private static (string?, int, int) Day(JsonElement day) =>
        (day.GetProperty("date").GetString(), day.GetProperty("visits").GetInt32(), day.GetProperty("pageViews").GetInt32());

    private async Task<(int Id, string Slug, string Name)> FirstProductAsync()
    {
        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var product = await db.Products.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Slug, p.Name }).FirstAsync(Token);
        return (product.Id, product.Slug, product.Name);
    }
}
