using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api;

/// <summary>
/// <c>GET /api/currency-rates</c> (issue #248), end to end against a real SQL Server: anonymous, only the currencies
/// with a usable rate, in the offered order, with a hand-written shape that cannot carry PayPal's rate or a secret,
/// and an empty answer, not an error, when nothing is set or the database cannot be read.
/// <para>
/// <b>Every test puts the five rows back to empty</b> in a <c>finally</c>, because the assembly's database is shared.
/// </para>
/// </summary>
public sealed class CurrencyRatesEndpointTests(SqlServerDatabaseFixture sqlServer)
{
    private const string Url = "/api/currency-rates";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task With_no_rate_set_the_answer_is_an_empty_list_and_still_cacheable()
    {
        await using var factory = await StartAsync();
        await RestoreAsync(factory);
        using var client = Anonymous(factory);

        using var response = await client.GetAsync(new Uri(Url, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.Equal("public, max-age=60", response.Headers.CacheControl?.ToString());
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Only_the_currencies_with_a_rate_are_listed_in_the_offered_order_with_the_four_documented_fields()
    {
        await using var factory = await StartAsync();

        try
        {
            var stamped = new DateTime(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);
            await WriteAsync(factory, SettingKeys.CurrencyDisplay.AedLkrPerUnit, "82.275", stamped);
            await WriteAsync(factory, SettingKeys.CurrencyDisplay.GbpLkrPerUnit, "399.5", stamped);
            await WriteAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit, "301.5", null);
            using var client = Anonymous(factory);

            using var response = await client.GetAsync(new Uri(Url, UriKind.Relative), Token);
            var body = await response.Content.ReadFromJsonAsyncCompat(Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["USD", "GBP", "AED"], body.Select(rate => rate.GetProperty("code").GetString()));
            Assert.Equal(["US$", "£", "AED"], body.Select(rate => rate.GetProperty("symbol").GetString()));
            Assert.Equal([301.5m, 399.5m, 82.275m], body.Select(rate => rate.GetProperty("lkrPerUnit").GetDecimal()));

            // Exactly four fields: nothing else can ride along on this payload.
            Assert.All(body, rate => Assert.Equal(
                ["code", "symbol", "lkrPerUnit", "updatedAtUtc"],
                rate.EnumerateObject().Select(property => property.Name)));

            Assert.Equal(JsonValueKind.Null, body[0].GetProperty("updatedAtUtc").ValueKind);
            Assert.Equal("2026-10-24T10:00:00Z", body[1].GetProperty("updatedAtUtc").GetString());
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    /// <summary>HEAD, because a monitor or a link checker leads with it (as <c>/api/site/settings</c> does).</summary>
    [Fact]
    public async Task The_feed_answers_head()
    {
        await using var factory = await StartAsync();
        using var client = Anonymous(factory);

        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri(Url, UriKind.Relative));
        using var response = await client.SendAsync(request, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A row can hold what the form would refuse today (a database edit, an earlier build). It is skipped, not an
    /// error and not published, and the rows beside it are unaffected.
    /// </summary>
    [Theory]
    [InlineData("garbage")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("1e3")]
    [InlineData("399.12345")]
    [InlineData("1000001")]
    [InlineData("")]
    public async Task A_stored_value_that_is_not_a_usable_rate_is_skipped_without_an_error(string stored)
    {
        await using var factory = await StartAsync();

        try
        {
            await WriteAsync(factory, SettingKeys.CurrencyDisplay.EurLkrPerUnit, stored, null);
            await WriteAsync(factory, SettingKeys.CurrencyDisplay.AudLkrPerUnit, "205", null);
            using var client = Anonymous(factory);

            using var response = await client.GetAsync(new Uri(Url, UriKind.Relative), Token);
            var body = await response.Content.ReadFromJsonAsyncCompat(Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["AUD"], body.Select(rate => rate.GetProperty("code").GetString()));
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    /// <summary>
    /// The query names five keys and excludes a secret row, so even a rate flipped to <c>Secret</c> is never
    /// selected. A guard in the shape, not a filter that one flag can get wrong.
    /// </summary>
    [Fact]
    public async Task A_row_flagged_secret_is_never_published()
    {
        await using var factory = await StartAsync();

        try
        {
            await WriteAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit, "301.5", null);
            await WriteAsync(factory, SettingKeys.CurrencyDisplay.GbpLkrPerUnit, "399.5", null);
            await SetSecretAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit, true);
            using var client = Anonymous(factory);

            using var response = await client.GetAsync(new Uri(Url, UriKind.Relative), Token);
            var body = await response.Content.ReadFromJsonAsyncCompat(Token);

            Assert.Equal(["GBP"], body.Select(rate => rate.GetProperty("code").GetString()));
        }
        finally
        {
            await SetSecretAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit, false);
            await RestoreAsync(factory);
        }
    }

    /// <summary>
    /// PayPal's rate, buffer and credentials sit in the same table and must never appear here: they are not in the
    /// list of five, and the payload has no field they could occupy.
    /// </summary>
    [Fact]
    public async Task Nothing_of_paypals_is_in_the_answer()
    {
        await using var factory = await StartAsync();
        string? paypalRate;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            paypalRate = await db.Settings.AsNoTracking()
                .Where(row => row.Key == SettingKeys.PayPal.LkrPerUsd)
                .Select(row => row.Value)
                .SingleAsync(Token);
        }

        try
        {
            await WriteAsync(factory, SettingKeys.CurrencyDisplay.UsdLkrPerUnit, "301.5", null);
            using var client = Anonymous(factory);

            using var response = await client.GetAsync(new Uri(Url, UriKind.Relative), Token);
            var text = await response.Content.ReadAsStringAsync(Token);

            Assert.DoesNotContain("paypal", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("buffer", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("permission", text, StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(paypalRate) && paypalRate != "301.5")
            {
                Assert.DoesNotContain(paypalRate, text, StringComparison.Ordinal);
            }
        }
        finally
        {
            await RestoreAsync(factory);
        }
    }

    /// <summary>
    /// The storefront asks for this while rendering a page, so a database that cannot be read is an empty 200, which
    /// is also the honest answer for a shop with no rate set, and is not given a cache lifetime: a blip must not be
    /// remembered by a browser or a proxy for a minute.
    /// </summary>
    [Fact]
    public async Task A_database_that_cannot_be_read_is_an_empty_list_with_no_cache_lifetime()
    {
        await using var factory = new ApiFactory(ApiFactory.UnreachableConnectionString);
        using var client = Anonymous(factory);

        using var response = await client.GetAsync(new Uri(Url, UriKind.Relative), Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.CacheControl);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync(Token));
    }

    // ---- helpers ----------------------------------------------------------------------------------------

    private async Task<ApiFactory> StartAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return new ApiFactory(sqlServer.ConnectionString);
    }

    private static HttpClient Anonymous(ApiFactory factory) =>
        factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

    private static async Task WriteAsync(ApiFactory factory, string key, string? value, DateTime? updatedAtUtc)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Settings
            .Where(row => row.Key == key)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.Value, value)
                    .SetProperty(row => row.UpdatedAtUtc, updatedAtUtc),
                Token);
    }

    private static async Task SetSecretAsync(ApiFactory factory, string key, bool secret)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await db.Settings
            .Where(row => row.Key == key)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.IsSecret, secret)
                    .SetProperty(row => row.ValueType, secret ? SettingValueType.Secret : SettingValueType.Decimal),
                Token);
    }

    private static async Task RestoreAsync(ApiFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var keys = DisplayCurrencies.All.Select(currency => currency.SettingKey).ToArray();

        await db.Settings
            .Where(row => keys.Contains(row.Key))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(row => row.Value, (string?)null)
                    .SetProperty(row => row.UpdatedAtUtc, (DateTime?)null)
                    .SetProperty(row => row.UpdatedByUserId, (int?)null),
                Token);
    }
}

/// <summary>Reads the feed's JSON array into elements, so an assertion can name each property it expects.</summary>
internal static class CurrencyRatesResponseExtensions
{
    public static async Task<List<JsonElement>> ReadFromJsonAsyncCompat(this HttpContent content, CancellationToken cancellationToken)
    {
        var text = await content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(text);

        return [.. document.RootElement.EnumerateArray().Select(element => element.Clone())];
    }
}
