using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The newsletter subscriber list and its export, end to end against a real SQL Server (ADM-21, doc 11 P15
/// step 4).
/// <para>
/// <b>Subscriber rows are inserted directly</b>, and that is deliberate for two of them. The unsubscribe
/// link (doc 11 P15 step 3, §7 G-25) is not built yet, so an <c>Unsubscribed</c> row can only be written
/// by hand; and the hostile addresses are written by hand because the export's defence must hold whatever
/// the signup validator lets through today or tomorrow. Every row carries this test's marker and is
/// removed at the end.
/// </para>
/// </summary>
public sealed class AdminNewsletterEndpointTests(SqlServerDatabaseFixture sqlServer)
    : AdminCustomerTestBase(sqlServer), IAsyncDisposable
{
    private const string ListUrl = "/api/admin/newsletter/subscribers";
    private const string ExportUrl = "/api/admin/newsletter/subscribers/export";

    private readonly List<int> _subscribers = [];

    // ---- authentication and authorization -------------------------------------------------------

    [Fact]
    public async Task Both_routes_refuse_an_unauthenticated_caller()
    {
        await using var host = await StartAsync();
        using var client = host.CreateAnonymousClient();

        using var list = await client.GetAsync(new Uri(ListUrl, UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);

        using var export = await client.PostAsync(new Uri(ExportUrl, UriKind.Relative), null, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, export.StatusCode);
    }

    /// <summary>
    /// <b>Doc 11 P15's two permissions are two gates.</b> <c>Newsletter.View</c> reads the list and cannot
    /// export; <c>Newsletter.Export</c> exports and cannot read the list. Asserted from both sides, so an
    /// endpoint that required nothing, or the wrong one, fails.
    /// </summary>
    [Fact]
    public async Task View_and_Export_are_separate_permissions()
    {
        await using var host = await StartAsync();

        using var viewer = await host.SignInAsync(Permissions.Newsletter.View);
        using (var list = await viewer.GetAsync(new Uri(ListUrl, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        }

        using (var refused = await viewer.PostAsync(new Uri(ExportUrl, UriKind.Relative), null, Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using var exporter = await host.SignInAsync(Permissions.Newsletter.Export);
        using (var export = await exporter.PostAsync(new Uri(ExportUrl, UriKind.Relative), null, Token))
        {
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        }

        using (var refused = await exporter.GetAsync(new Uri(ListUrl, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        // Customers.View is not the newsletter's permission.
        using var customers = await host.SignInAsync(Permissions.Customers.View, Permissions.Reports.Export);
        using (var refused = await customers.GetAsync(new Uri(ListUrl, UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }

        using (var refused = await customers.PostAsync(new Uri(ExportUrl, UriKind.Relative), null, Token))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
    }

    // ---- the list -------------------------------------------------------------------------------

    [Fact]
    public async Task A_list_row_carries_every_field_the_screen_reads_and_no_ip_address()
    {
        await using var host = await StartAsync();
        var consent = new DateTime(2026, 9, 30, 4, 15, 0, DateTimeKind.Utc);
        var id = await AddSubscriberAsync($"{Marker}@example.com", NewsletterSubscriberStatus.Subscribed, consent);

        using var client = await host.SignInAsync(Permissions.Newsletter.View);
        var page = await GetJsonAsync(client, $"{ListUrl}?search={Marker}");

        var row = Assert.Single(page.GetProperty("rows").EnumerateArray());
        Assert.Equal(id, row.GetProperty("subscriberId").GetInt32());
        Assert.Equal($"{Marker}@example.com", row.GetProperty("email").GetString());
        Assert.Equal("Subscribed", row.GetProperty("status").GetString());
        Assert.Equal(NewsletterSource.Footer, row.GetProperty("source").GetString());
        Assert.Equal(consent, row.GetProperty("consentAtUtc").GetDateTime());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("unsubscribedAtUtc").ValueKind);

        // The consent IP is proof of consent, not something a list shows.
        Assert.Equal(
            ["consentAtUtc", "email", "source", "status", "subscriberId", "unsubscribedAtUtc"],
            row.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(25, page.GetProperty("pageSize").GetInt32());

        // The two counts are the whole table's, whatever the filter.
        var (subscribed, unsubscribed) = await CountsAsync();
        Assert.Equal(subscribed, page.GetProperty("subscribedCount").GetInt32());
        Assert.Equal(unsubscribed, page.GetProperty("unsubscribedCount").GetInt32());
    }

    [Fact]
    public async Task The_status_filter_and_the_unsubscribed_time()
    {
        await using var host = await StartAsync();
        var consent = DateTime.UtcNow.AddDays(-10);
        var gone = DateTime.UtcNow.AddDays(-1);

        var subscribed = await AddSubscriberAsync($"{Marker}-in@example.com", NewsletterSubscriberStatus.Subscribed, consent);
        var unsubscribed = await AddSubscriberAsync(
            $"{Marker}-out@example.com", NewsletterSubscriberStatus.Unsubscribed, consent, unsubscribedAtUtc: gone);

        using var client = await host.SignInAsync(Permissions.Newsletter.View);

        Assert.Equal([subscribed], Ids(await GetJsonAsync(client, $"{ListUrl}?search={Marker}&status=Subscribed")));
        Assert.Equal([unsubscribed], Ids(await GetJsonAsync(client, $"{ListUrl}?search={Marker}&status=unsubscribed")));
        Assert.Equal(2, Ids(await GetJsonAsync(client, $"{ListUrl}?search={Marker}")).Count);

        var row = Assert.Single(
            (await GetJsonAsync(client, $"{ListUrl}?search={Marker}&status=Unsubscribed")).GetProperty("rows").EnumerateArray());
        Assert.Equal("Unsubscribed", row.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("unsubscribedAtUtc").ValueKind);
    }

    /// <summary>
    /// <b>The date filter is Colombo calendar days.</b> 18:29 UTC on 30 September is 23:59 on the 30th in
    /// Colombo, and 18:31 UTC is 00:01 on 1 October — so "from 1 October" includes the second and not the
    /// first, which a UTC-day filter would get the wrong way round.
    /// </summary>
    [Fact]
    public async Task The_date_filter_is_by_Colombo_calendar_day_inclusive()
    {
        await using var host = await StartAsync();

        var lateOn30th = await AddSubscriberAsync(
            $"{Marker}-a@example.com", NewsletterSubscriberStatus.Subscribed, new DateTime(2026, 9, 30, 18, 29, 0, DateTimeKind.Utc));
        var earlyOn1st = await AddSubscriberAsync(
            $"{Marker}-b@example.com", NewsletterSubscriberStatus.Subscribed, new DateTime(2026, 9, 30, 18, 31, 0, DateTimeKind.Utc));
        var lateOn1st = await AddSubscriberAsync(
            $"{Marker}-c@example.com", NewsletterSubscriberStatus.Subscribed, new DateTime(2026, 10, 1, 18, 29, 0, DateTimeKind.Utc));
        var on2nd = await AddSubscriberAsync(
            $"{Marker}-d@example.com", NewsletterSubscriberStatus.Subscribed, new DateTime(2026, 10, 1, 18, 31, 0, DateTimeKind.Utc));

        using var client = await host.SignInAsync(Permissions.Newsletter.View);

        Assert.Equal(
            [lateOn1st, earlyOn1st],
            Ids(await GetJsonAsync(client, $"{ListUrl}?search={Marker}&from=2026-10-01&to=2026-10-01")));

        Assert.Equal(
            [on2nd, lateOn1st, earlyOn1st],
            Ids(await GetJsonAsync(client, $"{ListUrl}?search={Marker}&from=2026-10-01")));

        Assert.Equal(
            [lateOn30th],
            Ids(await GetJsonAsync(client, $"{ListUrl}?search={Marker}&to=2026-09-30")));
    }

    [Theory]
    [InlineData("?status=Pending", "status")]
    [InlineData("?status=1", "status")]
    [InlineData("?from=01-10-2026", "from")]
    [InlineData("?to=2026-13-01", "to")]
    [InlineData("?from=2026-10-02&to=2026-10-01", "to")]
    public async Task A_bad_filter_is_a_400_naming_the_field(string query, string field)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Newsletter.View, Permissions.Newsletter.Export);

        using var list = await client.GetAsync(new Uri($"{ListUrl}{query}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.BadRequest, list.StatusCode);

        var problem = await list.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), field);
    }

    [Fact]
    public async Task Pages_are_most_recent_consent_first_and_neither_repeat_nor_skip()
    {
        await using var host = await StartAsync();
        var start = DateTime.UtcNow.AddDays(-5);

        var oldest = await AddSubscriberAsync($"{Marker}-1@example.com", NewsletterSubscriberStatus.Subscribed, start);
        var middle = await AddSubscriberAsync($"{Marker}-2@example.com", NewsletterSubscriberStatus.Subscribed, start.AddHours(1));
        var newest = await AddSubscriberAsync($"{Marker}-3@example.com", NewsletterSubscriberStatus.Subscribed, start.AddHours(2));

        using var client = await host.SignInAsync(Permissions.Newsletter.View);

        var first = await GetJsonAsync(client, $"{ListUrl}?search={Marker}&pageSize=2&page=1");
        var second = await GetJsonAsync(client, $"{ListUrl}?search={Marker}&pageSize=2&page=2");

        Assert.Equal(3, first.GetProperty("totalCount").GetInt32());
        Assert.Equal([newest, middle], Ids(first));
        Assert.Equal([oldest], Ids(second));

        var clamped = await GetJsonAsync(client, $"{ListUrl}?pageSize=5000&page=-3");
        Assert.Equal(100, clamped.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, clamped.GetProperty("page").GetInt32());
    }

    // ---- the export -----------------------------------------------------------------------------

    /// <summary>
    /// <b>An unsubscribed address is never exported</b> — not by default, and not when the caller asks
    /// for <c>?status=Unsubscribed</c>, which the export does not read.
    /// </summary>
    [Fact]
    public async Task The_export_holds_subscribed_addresses_only_whatever_the_status_asked_for()
    {
        await using var host = await StartAsync();
        var consent = DateTime.UtcNow.AddDays(-3);

        await AddSubscriberAsync($"{Marker}-stays@example.com", NewsletterSubscriberStatus.Subscribed, consent);
        await AddSubscriberAsync(
            $"{Marker}-left@example.com", NewsletterSubscriberStatus.Unsubscribed, consent, unsubscribedAtUtc: DateTime.UtcNow);

        using var client = await host.SignInAsync(Permissions.Newsletter.Export);

        foreach (var query in new[] { $"?search={Marker}", $"?search={Marker}&status=Unsubscribed" })
        {
            var text = (await ExportAsync(client, query)).Text;

            Assert.Contains($"{Marker}-stays@example.com", text, StringComparison.Ordinal);
            Assert.DoesNotContain($"{Marker}-left@example.com", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>The CSV injection test, with hostile values that are real addresses.</b> Each starts with one of
    /// the characters a spreadsheet reads as the start of a formula; in the file each must begin with a
    /// single quote inside its quotes, so Excel shows text rather than running it.
    /// </summary>
    [Fact]
    public async Task A_cell_that_would_be_a_formula_is_neutralised()
    {
        await using var host = await StartAsync();
        var consent = DateTime.UtcNow.AddDays(-2);

        var hostile = new[]
        {
            $"=HYPERLINK(\"http://evil.example\";\"x\")+{Marker}@example.com",
            $"+{Marker}1@example.com",
            $"-{Marker}2@example.com",
            $"=1+2{Marker}@example.com",
        };

        foreach (var email in hostile)
        {
            await AddSubscriberAsync(email, NewsletterSubscriberStatus.Subscribed, consent);
        }

        using var client = await host.SignInAsync(Permissions.Newsletter.Export);
        var text = (await ExportAsync(client, $"?search={Marker}")).Text;

        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + hostile.Length, lines.Length);

        foreach (var email in hostile)
        {
            var escaped = email.Replace("\"", "\"\"", StringComparison.Ordinal);
            Assert.Contains(lines, line => line.StartsWith($"\"'{escaped}\",", StringComparison.Ordinal));
        }

        // No line starts a cell with a bare formula character.
        foreach (var line in lines)
        {
            Assert.DoesNotMatch("(^|,)\"?[=+\\-@]", line);
        }
    }

    /// <summary>
    /// <b>Encoding.</b> The file starts with the UTF-8 byte-order mark, which is what makes Excel read it
    /// as UTF-8 rather than in the Windows code page; a Sinhala and a Tamil address come back byte for byte.
    /// There is no name column — doc 08 §3.6's table holds no name — so the address is the only text a
    /// subscriber supplies, and an internationalised address is where Sinhala and Tamil arrive.
    /// </summary>
    [Fact]
    public async Task The_file_is_UTF8_with_a_byte_order_mark_and_keeps_Sinhala_and_Tamil()
    {
        await using var host = await StartAsync();
        var consent = new DateTime(2026, 10, 1, 4, 30, 0, DateTimeKind.Utc);

        var sinhala = $"නිමාලි{Marker}@example.lk";
        var tamil = $"கவிதா{Marker}@example.lk";
        await AddSubscriberAsync(sinhala, NewsletterSubscriberStatus.Subscribed, consent);
        await AddSubscriberAsync(tamil, NewsletterSubscriberStatus.Subscribed, consent.AddMinutes(1));

        using var client = await host.SignInAsync(Permissions.Newsletter.Export);
        var file = await ExportAsync(client, $"?search={Marker}");

        Assert.Equal("text/csv", file.MediaType);
        Assert.Equal("utf-8", file.CharSet);
        Assert.StartsWith("newsletter-subscribers-", file.FileName, StringComparison.Ordinal);
        Assert.EndsWith(".csv", file.FileName, StringComparison.Ordinal);

        var lines = file.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("\"Email\",\"Source\",\"Consent given (Colombo time)\"", lines[0]);

        // Oldest consent first; 04:30 UTC is 10:00 in Colombo.
        Assert.Equal($"\"{sinhala}\",\"{NewsletterSource.Footer}\",\"2026-10-01 10:00\"", lines[1]);
        Assert.Equal($"\"{tamil}\",\"{NewsletterSource.Footer}\",\"2026-10-01 10:01\"", lines[2]);
    }

    /// <summary>
    /// <b>Every export is audited</b> (doc 11 P15 step 4, P16): one <c>Export</c> row naming the staff member
    /// and the row count — and <b>not</b> the search text, which may be an email address that doc 08 §3.1
    /// step 6's anonymisation could never find in an audit row.
    /// </summary>
    [Fact]
    public async Task An_export_writes_one_audit_row_with_its_shape_and_no_search_text()
    {
        await using var host = await StartAsync();
        var consent = DateTime.UtcNow.AddDays(-2);
        await AddSubscriberAsync($"{Marker}-1@example.com", NewsletterSubscriberStatus.Subscribed, consent);
        await AddSubscriberAsync($"{Marker}-2@example.com", NewsletterSubscriberStatus.Subscribed, consent);

        using var client = await host.SignInAsync(Permissions.Newsletter.Export);
        var me = await GetJsonAsync(client, "/api/admin/auth/whoami");
        var before = await LastAuditIdAsync();

        await ExportAsync(client, $"?search={Marker}&from=2020-01-01&to=2030-12-31");

        var rows = await AuditRowsAfterAsync(before);
        var row = Assert.Single(rows);

        Assert.Equal(AuditActions.Export, row.Action);
        Assert.Equal("NewsletterSubscriber", row.EntityType);
        Assert.Equal(me.GetProperty("userId").GetInt32(), row.UserId);
        Assert.Equal(me.GetProperty("email").GetString(), row.UserEmail);

        var after = JsonDocument.Parse(row.AfterJson!).RootElement;
        Assert.Equal(2, after.GetProperty("rows").GetInt32());
        Assert.Equal("Subscribed", after.GetProperty("status").GetString());
        Assert.Equal("2020-01-01", after.GetProperty("consentFrom").GetString());
        Assert.Equal("2030-12-31", after.GetProperty("consentTo").GetString());
        Assert.True(after.GetProperty("searchApplied").GetBoolean());

        Assert.DoesNotContain(Marker, row.AfterJson!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", row.AfterJson!, StringComparison.Ordinal);

        // Nor in any other column of the committed row (W3-07 tester: EntityId was unchecked).
        Assert.Null(row.EntityId);
        Assert.Null(row.BeforeJson);
        foreach (var column in typeof(AuditLog).GetProperties().Where(property => property.PropertyType == typeof(string)))
        {
            Assert.DoesNotContain(Marker, (string?)column.GetValue(row) ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// <b>Without the antiforgery token the export is refused and nothing is audited or sent.</b> The route
    /// writes an audit row, so it must not be triggerable by a page the staff member happens to visit.
    /// </summary>
    [Fact]
    public async Task An_export_without_the_antiforgery_token_is_refused_and_not_audited()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Newsletter.Export);
        client.DefaultRequestHeaders.Remove(MediaTestHost.AntiforgeryHeader);

        var before = await LastAuditIdAsync();

        using var response = await client.PostAsync(new Uri(ExportUrl, UriKind.Relative), null, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await AuditRowsAfterAsync(before));
    }

    [Fact]
    public async Task A_bad_date_on_the_export_is_a_400_and_not_audited()
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Newsletter.Export);
        var before = await LastAuditIdAsync();

        using var response = await client.PostAsync(new Uri($"{ExportUrl}?from=yesterday", UriKind.Relative), null, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await AuditRowsAfterAsync(before));
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscribers.Count > 0 && SqlServer.IsAvailable)
        {
            using var scope = SqlServer.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().NewsletterSubscribers
                .Where(subscriber => _subscribers.Contains(subscriber.Id))
                .ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    private async Task<int> AddSubscriberAsync(
        string email,
        NewsletterSubscriberStatus status,
        DateTime consentAtUtc,
        DateTime? unsubscribedAtUtc = null)
    {
        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var subscriber = new NewsletterSubscriber
        {
            Email = email,
            EmailNormalized = email.ToUpperInvariant(),
            Status = status,
            Source = NewsletterSource.Footer,
            ConsentAtUtc = consentAtUtc,
            ConsentIpAddress = "203.0.113.7",
            UnsubscribeToken = Guid.NewGuid(),
            UnsubscribedAtUtc = unsubscribedAtUtc,
            CreatedAtUtc = consentAtUtc,
        };

        db.NewsletterSubscribers.Add(subscriber);
        await db.SaveChangesAsync(Token);

        _subscribers.Add(subscriber.Id);

        return subscriber.Id;
    }

    private async Task<(int Subscribed, int Unsubscribed)> CountsAsync()
    {
        using var scope = SqlServer.CreateScope();
        var subscribers = scope.ServiceProvider.GetRequiredService<AppDbContext>().NewsletterSubscribers.AsNoTracking();

        return (
            await subscribers.CountAsync(row => row.Status == NewsletterSubscriberStatus.Subscribed, Token),
            await subscribers.CountAsync(row => row.Status == NewsletterSubscriberStatus.Unsubscribed, Token));
    }

    private async Task<long> LastAuditIdAsync()
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .MaxAsync(row => (long?)row.Id, Token) ?? 0;
    }

    private async Task<List<AuditLog>> AuditRowsAfterAsync(long id)
    {
        using var scope = SqlServer.CreateScope();

        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().AuditLogs
            .AsNoTracking()
            .Where(row => row.Id > id && row.EntityType == "NewsletterSubscriber")
            .ToListAsync(Token);
    }

    private static async Task<ExportedFile> ExportAsync(HttpClient client, string query)
    {
        using var response = await client.PostAsync(new Uri($"{ExportUrl}{query}", UriKind.Relative), null, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync(Token);

        // The BOM, asserted as bytes: a decoder would swallow it silently.
        Assert.True(bytes.Length >= 3, "The file is empty.");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);

        return new ExportedFile(
            response.Content.Headers.ContentType?.MediaType,
            response.Content.Headers.ContentType?.CharSet,
            response.Content.Headers.ContentDisposition?.FileName?.Trim('"'),
            new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3));
    }

    private sealed record ExportedFile(string? MediaType, string? CharSet, string? FileName, string Text);

    private static List<int> Ids(JsonElement page) =>
        page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("subscriberId").GetInt32()).ToList();
}
