using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// Independent tester probes for W3-07: the inputs the developer's suite did not try — telephone numbers
/// stored the way registration stores them, paging and date values at the edges of their types, LIKE
/// syntax the first suite did not cover, ids that are not a customer's, and every way of asking the
/// export for an unsubscribed address.
/// </summary>
public sealed class W307TesterProbeTests(SqlServerDatabaseFixture sqlServer)
    : AdminCustomerTestBase(sqlServer), IAsyncDisposable
{
    private const string CustomersUrl = "/api/admin/customers";
    private const string NewsletterUrl = "/api/admin/newsletter/subscribers";

    private readonly List<int> _subscribers = [];

    /// <summary>
    /// Registration keeps the number as typed (<c>Trim()</c> only), and <c>LooksLikePhone</c> accepts
    /// spaces, hyphens, dots, slashes and parentheses — so a number stored with separators must still be
    /// found by its digits, typed in any of the forms the search box promises.
    /// </summary>
    [Theory]
    [InlineData("077 {0} {1}", "07{0}{1}")]
    [InlineData("077-{0}-{1}", "+947{0}{1}")]
    [InlineData("(077) {0}{1}", "77{0}{1}")]
    [InlineData("+94 77 {0} {1}", "077{0}{1}")]
    [InlineData("077.{0}.{1}", "077 {0} {1}")]
    public async Task A_number_stored_as_the_customer_typed_it_is_found_by_its_digits(string stored, string typed)
    {
        await using var host = await StartAsync();
        var a = Random.Shared.Next(100, 999).ToString(CultureInfo.InvariantCulture);
        var b = Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);
        var id = await CreateCustomerAsync(
            host, $"{Marker}@example.com", "Phone", Marker, string.Format(CultureInfo.InvariantCulture, stored, a, b));

        using var client = await host.SignInAsync(Permissions.Customers.View);

        Assert.Contains(id, await IdsAsync(client, string.Format(CultureInfo.InvariantCulture, typed, a, b)));
    }

    [Theory]
    [InlineData("?page=2147483647")]
    [InlineData("?page=2147483647&pageSize=100")]
    [InlineData("?page=42949673&pageSize=100")]
    [InlineData("?page=-5&pageSize=-5")]
    [InlineData("?pageSize=2147483647")]
    public async Task Extreme_paging_never_answers_500(string query)
    {
        await using var host = await StartAsync();
        var customerId = await CreateCustomerAsync(host, $"{Marker}@example.com", "Paging", Marker);
        using var client = await host.SignInAsync(Permissions.Customers.View, Permissions.Newsletter.View);

        foreach (var url in new[]
                 {
                     $"{CustomersUrl}{query}",
                     $"{CustomersUrl}/{customerId}/orders{query}",
                     $"{NewsletterUrl}{query}",
                 })
        {
            using var response = await client.GetAsync(new Uri(url, UriKind.Relative), Token);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)response.StatusCode}");
        }
    }

    [Theory]
    [InlineData("?to=9999-12-31")]
    [InlineData("?from=0001-01-01")]
    [InlineData("?from=0001-01-01&to=9999-12-31")]
    public async Task Extreme_dates_never_answer_500(string query)
    {
        await using var host = await StartAsync();
        using var client = await host.SignInAsync(Permissions.Newsletter.View, Permissions.Newsletter.Export);

        using var list = await client.GetAsync(new Uri($"{NewsletterUrl}{query}", UriKind.Relative), Token);
        Assert.True((int)list.StatusCode < 500, $"list answered {(int)list.StatusCode}");

        using var export = await client.PostAsync(
            new Uri($"{NewsletterUrl}/export{query}", UriKind.Relative), null, Token);
        Assert.True((int)export.StatusCode < 500, $"export answered {(int)export.StatusCode}");
    }

    [Fact]
    public async Task Search_terms_that_are_LIKE_syntax_long_or_empty_are_literal_and_safe()
    {
        await using var host = await StartAsync();
        var plain = await CreateCustomerAsync(host, $"{Marker}@example.com", "Ruwan", $"Fernando{Marker}");
        var bracket = await CreateCustomerAsync(host, $"{Marker}-b@example.com", "Ruwan", $"[a]{Marker}");

        using var client = await host.SignInAsync(Permissions.Customers.View);

        // "[a]" as a LIKE class would match the single letter; as a literal it matches only the brackets.
        Assert.Equal([bracket], await IdsAsync(client, $"[a]{Marker}"));
        Assert.Empty(await IdsAsync(client, $"Fernand[o]{Marker}"));
        Assert.Empty(await IdsAsync(client, $"Fernand_{Marker}"));
        Assert.Empty(await IdsAsync(client, $"Fernand%{Marker}"));

        // An email-kind term (it has an @) is a StartsWith: a % inside it is literal too.
        Assert.Empty(await IdsAsync(client, $"{Marker[..4]}%{Marker[6..]}@"));

        // 1,000 characters: cut to 100, never refused, never a 500.
        using var longResponse = await client.GetAsync(
            new Uri($"{CustomersUrl}?search={new string('x', 1000)}", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.OK, longResponse.StatusCode);

        // Empty and whitespace-only are no search at all.
        var all = (await GetJsonAsync(client, $"{CustomersUrl}?pageSize=1")).GetProperty("totalCount").GetInt32();
        Assert.Equal(
            all,
            (await GetJsonAsync(client, $"{CustomersUrl}?pageSize=1&search=")).GetProperty("totalCount").GetInt32());
        Assert.Equal(
            all,
            (await GetJsonAsync(client, $"{CustomersUrl}?pageSize=1&search=%20%20")).GetProperty("totalCount").GetInt32());

        Assert.Contains(plain, await IdsAsync(client, Marker));
    }

    [Fact]
    public async Task No_id_reaches_a_staff_account_or_a_missing_one()
    {
        await using var host = await StartAsync();
        var staffId = await CreateStaffAsync(host, $"{Marker}-staff@example.com");
        using var client = await host.SignInAsync(Permissions.Customers.View);
        var me = (await GetJsonAsync(client, "/api/admin/auth/whoami")).GetProperty("userId").GetInt32();

        foreach (var id in new[] { staffId, me, 0, -1, int.MinValue })
        {
            foreach (var path in new[]
                     {
                         $"{CustomersUrl}/{id}",
                         $"{CustomersUrl}/{id}/orders",
                         $"{CustomersUrl}/{id}/orders?page=2",
                     })
            {
                using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Token);
                Assert.True(
                    response.StatusCode == HttpStatusCode.NotFound,
                    $"{path} answered {(int)response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task No_parameter_smuggles_an_unsubscribed_address_into_the_export()
    {
        await using var host = await StartAsync();
        await AddSubscriberAsync($"{Marker}-gone@example.com", NewsletterSubscriberStatus.Unsubscribed);

        using var client = await host.SignInAsync(Permissions.Newsletter.Export);

        var queries = new[]
        {
            $"?search={Marker}",
            $"?search={Marker}&status=Unsubscribed",
            $"?search={Marker}&Status=unsubscribed",
            $"?search={Marker}&status=2",
            $"?search={Marker}&status=All",
            $"?search={Marker}&includeUnsubscribed=true",
            $"?search={Marker}&status=Subscribed&status=Unsubscribed",
        };

        foreach (var query in queries)
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["status"] = "Unsubscribed" });
            using var response = await client.PostAsync(
                new Uri($"{NewsletterUrl}/export{query}", UriKind.Relative), form, Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(Token));
            Assert.DoesNotContain(Marker, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The vectors the developer's list did not include, through the real endpoint's bytes. Only characters
    /// an address can hold are reachable (NewsletterEmail refuses whitespace and control characters), so the
    /// probes are the ones an address can carry. Each line is asserted exactly.
    /// </summary>
    [Fact]
    public async Task The_file_the_endpoint_returns_neutralises_or_passes_each_vector_exactly()
    {
        await using var host = await StartAsync();

        var vectors = new (string Email, bool Neutralised)[]
        {
            ($"|{Marker}a@example.com", false),
            ($"%{Marker}b@example.com", false),
            ($"＝1+1{Marker}c@example.com", false),
            ($"-2+3{Marker}d@example.com", true),
            ($"+{Marker}e\"x\"@example.com", true),
            ($"=cmd|'/c calc'!A1{Marker}f@example.com", true),
            ($"නිමාලි{Marker}g@example.lk", false),
            ($"கவிதா{Marker}h@example.lk", false),
        };

        foreach (var (email, _) in vectors)
        {
            await AddSubscriberAsync(email, NewsletterSubscriberStatus.Subscribed);
        }

        using var client = await host.SignInAsync(Permissions.Newsletter.Export);
        using var response = await client.PostAsync(
            new Uri($"{NewsletterUrl}/export?search={Uri.EscapeDataString(Marker)}", UriKind.Relative), null, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync(Token);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 3, bytes.Length - 3);
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + vectors.Length, lines.Length);

        foreach (var (email, neutralised) in vectors)
        {
            var cell = "\"" + (neutralised ? "'" : string.Empty) + email.Replace("\"", "\"\"", StringComparison.Ordinal) + "\",";
            Assert.Contains(lines, line => line.StartsWith(cell, StringComparison.Ordinal));

            // Byte for byte: the UTF-8 of the stored address appears in the file unchanged.
            var stored = Encoding.UTF8.GetBytes(email.Replace("\"", "\"\"", StringComparison.Ordinal));
            Assert.True(bytes.AsSpan().IndexOf(stored) >= 0, $"bytes of {email} not found");
        }
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

    private async Task AddSubscriberAsync(string email, NewsletterSubscriberStatus status)
    {
        using var scope = SqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var consent = DateTime.UtcNow.AddDays(-1);

        var subscriber = new NewsletterSubscriber
        {
            Email = email,
            EmailNormalized = email.ToUpperInvariant(),
            Status = status,
            Source = NewsletterSource.Footer,
            ConsentAtUtc = consent,
            ConsentIpAddress = "203.0.113.7",
            UnsubscribeToken = Guid.NewGuid(),
            UnsubscribedAtUtc = status == NewsletterSubscriberStatus.Unsubscribed ? DateTime.UtcNow : null,
            CreatedAtUtc = consent,
        };

        db.NewsletterSubscribers.Add(subscriber);
        await db.SaveChangesAsync(Token);
        _subscribers.Add(subscriber.Id);
    }

    private static async Task<List<int>> IdsAsync(HttpClient client, string search) =>
        (await GetJsonAsync(client, $"{CustomersUrl}?pageSize=100&search={Uri.EscapeDataString(search)}"))
            .GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("customerId").GetInt32()).ToList();
}
