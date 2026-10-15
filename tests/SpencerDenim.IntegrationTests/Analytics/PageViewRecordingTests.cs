using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Analytics;

/// <summary>
/// The recorder end to end on the API side (doc 08 §3.8; ADM-06, ADM-20): one beacon is one row, nothing
/// secret and no IP is stored or logged, bots are marked, and neither a full queue nor a database that is down
/// can fail the request.
/// </summary>
public sealed class PageViewRecordingTests(SqlServerDatabaseFixture sqlServer)
{
    private const string ChromeWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";

    private const string SafariIphone =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<TrafficTestHost> StartAsync(IReadOnlyDictionary<string, string?>? configure = null)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return TrafficTestHost.Create(sqlServer.ConnectionString, configure);
    }

    [Fact]
    public async Task One_beacon_is_exactly_one_row_with_a_fresh_thirty_minute_session_cookie()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(SafariIphone);

        using var response = await TrafficTestHost.PostAsync(
            client,
            new { path = "/new-arrivals?utm_source=Instagram&utm_medium=social", referrer = "https://l.instagram.com/" },
            Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("sd_vs=", StringComparison.Ordinal));
        Assert.Contains("max-age=1800", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);

        var session = TrafficTestHost.SessionFrom(response);

        Assert.Equal(1, await host.FlushAsync(Token));

        var row = Assert.Single(await TrafficTestHost.RowsAsync(sqlServer, session, Token));
        Assert.Equal("/new-arrivals", row.Path);
        Assert.Equal(PageType.NewArrivals, row.PageType);
        Assert.Equal("l.instagram.com", row.ReferrerHost);
        Assert.Equal("instagram", row.UtmSource);
        Assert.Equal("social", row.UtmMedium);
        Assert.Equal(TrafficChannel.Social, row.Channel);
        Assert.Equal(DeviceType.Mobile, row.DeviceType);
        Assert.False(row.IsBot);
        Assert.True(Math.Abs((DateTime.UtcNow - row.OccurredAtUtc).TotalMinutes) < 5);
        Assert.Equal(0, row.OccurredAtUtc.Millisecond);
    }

    [Fact]
    public async Task The_session_cookie_is_reused_on_the_next_view_and_its_thirty_minutes_restart()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeWindows);

        using var first = await TrafficTestHost.PostAsync(client, new { path = "/" }, Token);
        var session = TrafficTestHost.SessionFrom(first);

        using var second = await TrafficTestHost.PostAsync(client, new { path = "/women", referrer = "https://localhost/" }, Token);
        Assert.Equal(session, TrafficTestHost.SessionFrom(second));
        Assert.Contains("max-age=1800", second.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);

        await host.FlushAsync(Token);

        var rows = await TrafficTestHost.RowsAsync(sqlServer, session, Token);
        Assert.Equal(["/", "/women"], rows.Select(row => row.Path));

        // The second view's referrer is the shop itself (the host the beacon was sent to): a click inside the
        // shop, not a source.
        Assert.Null(rows[1].ReferrerHost);
        Assert.Equal(TrafficChannel.Direct, rows[1].Channel);
    }

    [Fact]
    public async Task A_cookie_that_is_not_a_session_is_replaced_rather_than_refused()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "sd_vs=00000000-0000-0000-0000-000000000000");

        using var response = await TrafficTestHost.PostAsync(client, new { path = "/" }, Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotEqual(Guid.Empty, TrafficTestHost.SessionFrom(response));
    }

    /// <summary>
    /// The privacy rule, swept rather than spot-checked: every secret a storefront address can carry, posted
    /// with an IP in every forwarding header, and then <b>every string column of every analytics row</b> and
    /// <b>every log event at every level</b> searched for each of them.
    /// </summary>
    [Fact]
    public async Task No_secret_from_an_address_and_no_ip_address_is_stored_or_logged()
    {
        const string orderToken = "5b1e7c2a-44d0-4c3e-9f1a-8e2d6b7c9a01";
        const string resetToken = "CfDJ8ResetTokenW403xyz";
        const string email = "jane.w403secret@example.com";
        const string phone = "0779403123";
        const string ip = "203.0.113.77";
        const string paypalToken = "EC-W403PAYPAL12345";

        await using var host = await StartAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeWindows);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);
        client.DefaultRequestHeaders.Add("X-Real-IP", ip);
        client.DefaultRequestHeaders.Add("Forwarded", $"for={ip}");

        string[] paths =
        [
            $"/order/{orderToken}",
            $"/order/{orderToken}?paypal=return&token={paypalToken}&PayerID=W403PAYER",
            $"/account/reset-password?token={resetToken}&email={Uri.EscapeDataString(email)}",
            $"/account/confirm-email?token={resetToken}",
            $"/account/sign-in?returnTo=%2Forder%2F{orderToken}",
            $"/search?q={Uri.EscapeDataString(email)}",
            $"/search?q={phone}",
            $"/women?colours={Uri.EscapeDataString(email)}&sizes={phone}",
            $"/product/{Uri.EscapeDataString(email)}",
            $"/{phone}",
            $"/checkout?email={Uri.EscapeDataString(email)}#{orderToken}",
            $"/?utm_campaign={Uri.EscapeDataString(email)}&utm_source={phone}&gclid={orderToken}",
        ];

        Guid? session = null;

        foreach (var path in paths)
        {
            using var response = await TrafficTestHost.PostAsync(
                client,
                new { path, referrer = $"https://www.google.com/search?q={Uri.EscapeDataString(email)}&ip={ip}" },
                Token);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            session ??= TrafficTestHost.SessionFrom(response);
        }

        Assert.Equal(paths.Length, await host.FlushAsync(Token));

        var rows = await TrafficTestHost.RowsAsync(sqlServer, session!.Value, Token);
        Assert.Equal(paths.Length, rows.Count);
        Assert.Equal(2, rows.Count(row => row.Path == "/order/*"));

        var everything = (await TrafficTestHost.EveryAnalyticsStringAsync(sqlServer.ConnectionString, Token))
            .Concat(host.Logs.Events)
            .ToList();

        string[] secrets = [orderToken, "5b1e7c2a", resetToken, email, "w403secret", phone, ip, paypalToken, "W403PAYER"];

        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(everything, value => value.Contains(secret, StringComparison.OrdinalIgnoreCase));
        }

        // The sweep looked at something: the host logged this test's requests, and the table holds its rows.
        Assert.Contains(host.Logs.Events, value => value.Contains("/api/traffic/page-views", StringComparison.Ordinal));
        Assert.Contains(everything, value => value == "/order/*");
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", false)]
    [InlineData("curl/8.4.0", false)]
    [InlineData(ChromeWindows, true)]
    public async Task A_bot_is_recorded_and_marked_as_one(string userAgent, bool automated)
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

        using var response = await TrafficTestHost.PostAsync(client, new { path = "/sale", automated }, Token);
        var session = TrafficTestHost.SessionFrom(response);
        await host.FlushAsync(Token);

        Assert.True(Assert.Single(await TrafficTestHost.RowsAsync(sqlServer, session, Token)).IsBot);
    }

    [Fact]
    public async Task The_storefront_servers_user_agent_field_is_the_visitors_and_an_empty_one_is_a_bot()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();

        // The storefront's server posts with its own (Node) user agent and the visitor's in the body.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("node");

        using var person = await TrafficTestHost.PostAsync(client, new { path = "/", userAgent = SafariIphone }, Token);
        using var nobody = await TrafficTestHost.PostAsync(client, new { path = "/bag", userAgent = "" }, Token);
        var session = TrafficTestHost.SessionFrom(person);
        await host.FlushAsync(Token);

        var rows = await TrafficTestHost.RowsAsync(sqlServer, session, Token);
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsBot);
        Assert.Equal(DeviceType.Mobile, rows[0].DeviceType);
        Assert.True(rows[1].IsBot);
    }

    [Fact]
    public async Task Pages_are_resolved_against_the_catalogue_in_the_batch()
    {
        string productSlug, departmentSlug, categoryPath, contentSlug;
        int productId, categoryId;

        await using var host = await StartAsync();

        using (var scope = sqlServer.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var product = await db.Products.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Slug }).FirstAsync(Token);
            (productId, productSlug) = (product.Id, product.Slug);

            var category = await db.Categories
                .AsNoTracking()
                .Where(c => c.ParentCategoryId == null)
                .OrderBy(c => c.Id)
                .Select(c => new { c.Id, c.Slug, Department = c.Department!.Slug })
                .FirstAsync(Token);
            (categoryId, departmentSlug, categoryPath) = (category.Id, category.Department, $"/{category.Department}/{category.Slug}");

            // A published page of this test's own: other suites publish and unpublish the seeded ones, so "the first
            // published page" is an order-dependent assumption (the whole-assembly run found none left published).
            contentSlug = "w403-page-" + Guid.NewGuid().ToString("N")[..8];
            db.ContentPages.Add(new Domain.Content.ContentPage
            {
                Slug = contentSlug,
                Title = "W4-03 traffic test page",
                BodyHtml = "<p>Test</p>",
                IsPublished = true,
                PublishedAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(Token);
        }

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeWindows);

        string[] paths =
        [
            $"/product/{productSlug}", "/product/no-such-product-w403", $"/{departmentSlug}", $"/{contentSlug}",
            categoryPath, $"/{departmentSlug}/no-such-category-w403", "/no-such-page-w403",
        ];

        Guid? session = null;
        foreach (var path in paths)
        {
            using var response = await TrafficTestHost.PostAsync(client, new { path }, Token);
            session ??= TrafficTestHost.SessionFrom(response);
        }

        await host.FlushAsync(Token);
        var rows = await TrafficTestHost.RowsAsync(sqlServer, session!.Value, Token);

        // The page is removed before anything is asserted, so a failure here cannot leave an extra published page
        // for a later suite that counts them.
        using (var cleanup = sqlServer.CreateScope())
        {
            await cleanup.ServiceProvider.GetRequiredService<AppDbContext>().ContentPages
                .IgnoreQueryFilters()
                .Where(page => page.Slug == contentSlug)
                .ExecuteDeleteAsync(Token);
        }

        Assert.Equal(
            [
                (PageType.Product, (int?)productId, (int?)null),
                (PageType.Product, null, null),
                (PageType.Department, null, null),
                (PageType.Content, null, null),
                (PageType.Category, null, (int?)categoryId),
                (PageType.Other, null, null),
                (PageType.Other, null, null),
            ],
            rows.Select(row => (row.PageType, row.ProductId, row.CategoryId)));
    }

    [Fact]
    public async Task An_address_that_is_not_a_storefront_path_answers_204_and_records_nothing()
    {
        await using var host = await StartAsync();
        using var client = host.CreateClient();

        using var response = await TrafficTestHost.PostAsync(client, new { path = "https://evil.example/" }, Token);
        var session = TrafficTestHost.SessionFrom(response);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await host.FlushAsync(Token));
        Assert.Empty(await TrafficTestHost.RowsAsync(sqlServer, session, Token));
    }

    [Fact]
    public async Task A_full_queue_drops_the_extra_views_and_every_request_still_answers_204()
    {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["Traffic:QueueCapacity"] = "2" });
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeWindows);

        Guid? session = null;
        for (var i = 0; i < 5; i++)
        {
            using var response = await TrafficTestHost.PostAsync(client, new { path = "/women" }, Token);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            session ??= TrafficTestHost.SessionFrom(response);
        }

        Assert.Equal(3, host.Queue.Dropped);
        Assert.Equal(2, await host.FlushAsync(Token));
        Assert.Equal(2, (await TrafficTestHost.RowsAsync(sqlServer, session!.Value, Token)).Count);
        Assert.Contains(host.Logs.Events, value => value.Contains("page-view queue is full", StringComparison.Ordinal));
    }

    /// <summary>
    /// The database is a socket that accepts the connection and never answers — the hung server, not the refused
    /// one. A refused connection fails in milliseconds, so a request that waited on it would still look fast; this
    /// one makes anything that touched the database on the request path take the full three-second connect
    /// timeout. (The first version of this test used the refusing address and did not notice an endpoint that
    /// called <c>Database.CanConnect()</c> on every beacon — mutation m09.)
    /// </summary>
    [Fact]
    public async Task With_the_database_down_the_beacon_still_answers_at_once_and_the_view_is_dropped()
    {
        sqlServer.SkipIfUnavailable();

        using var hung = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        hung.Start();
        var accepted = new List<System.Net.Sockets.Socket>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    accepted.Add(await hung.AcceptSocketAsync(Token));
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or System.Net.Sockets.SocketException or ObjectDisposedException)
            {
            }
        }, Token);

        var port = ((IPEndPoint)hung.LocalEndpoint).Port;
        var connectionString = $"Server=127.0.0.1,{port};Database=SpencerDenim_Hung;User Id=unused;Password=unused;Connect Timeout=3;TrustServerCertificate=True";

        await using var host = TrafficTestHost.Create(
            connectionString,
            new Dictionary<string, string?> { ["Database:MaxRetryCount"] = "0" });
        using var client = host.CreateClient();

        // Warm the host up first, so the measured request is the beacon and not the start-up.
        using (await TrafficTestHost.PostAsync(client, new { path = "/warm" }, Token))
        {
        }

        var timer = Stopwatch.StartNew();
        using var response = await TrafficTestHost.PostAsync(client, new { path = "/" }, Token);
        timer.Stop();

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        // Well under the hung server's 3 s connect timeout: the request never waited on the database.
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1.5), $"took {timer.Elapsed}");

        Assert.Equal(0, await host.FlushAsync(Token));
        Assert.Equal(2, host.Queue.Dropped);
        Assert.Contains(host.Logs.Events, value => value.Contains("they were dropped", StringComparison.Ordinal));

        // And the next view is still accepted: a failed write does not stop recording.
        using var next = await TrafficTestHost.PostAsync(client, new { path = "/" }, Token);
        Assert.Equal(HttpStatusCode.NoContent, next.StatusCode);
        Assert.Equal(1, host.Queue.Reader.Count);

        hung.Stop();
        foreach (var socket in accepted.ToArray())
        {
            socket.Dispose();
        }
    }

    [Fact]
    public async Task With_recording_off_the_beacon_answers_204_and_nothing_is_queued()
    {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["Traffic:RecordingEnabled"] = "false" });
        using var client = host.CreateClient();

        using var response = await TrafficTestHost.PostAsync(client, new { path = "/" }, Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, host.Queue.Reader.Count);
        Assert.Equal(0, await host.FlushAsync(Token));
    }

    [Fact]
    public async Task The_writer_writes_a_full_batch_without_waiting_for_its_interval()
    {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["Traffic:BatchSize"] = "5" });
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeWindows);

        Guid? session = null;
        for (var i = 0; i < 5; i++)
        {
            using var response = await TrafficTestHost.PostAsync(client, new { path = "/" }, Token);
            session ??= TrafficTestHost.SessionFrom(response);
        }

        // The interval is five minutes, so only the batch threshold can have written these.
        Assert.Equal(5, await WaitForRowsAsync(session!.Value, 5, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task The_writer_writes_a_part_batch_when_its_interval_is_up()
    {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["Traffic:FlushSeconds"] = "1" });
        using var client = host.CreateClient();

        using var response = await TrafficTestHost.PostAsync(client, new { path = "/" }, Token);
        var session = TrafficTestHost.SessionFrom(response);

        Assert.Equal(1, await WaitForRowsAsync(session, 1, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task Stopping_the_host_writes_what_was_still_queued()
    {
        Guid session;

        await using (var host = await StartAsync())
        {
            using var client = host.CreateClient();
            using var response = await TrafficTestHost.PostAsync(client, new { path = "/" }, Token);
            session = TrafficTestHost.SessionFrom(response);

            Assert.Empty(await TrafficTestHost.RowsAsync(sqlServer, session, Token));
        }

        Assert.Single(await TrafficTestHost.RowsAsync(sqlServer, session, Token));
    }

    private async Task<int> WaitForRowsAsync(Guid session, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var count = 0;

        while (DateTime.UtcNow < deadline)
        {
            count = (await TrafficTestHost.RowsAsync(sqlServer, session, Token)).Count;
            if (count >= expected)
            {
                return count;
            }

            await Task.Delay(100, Token);
        }

        return count;
    }
}
