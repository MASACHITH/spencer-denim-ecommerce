using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Analytics.Search;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Analytics;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Analytics;

/// <summary>
/// Independent tester pass for #78 (2026-10-21): attacks on the search beacon, the public list and the hide list
/// that the developer's <see cref="SearchStatsTests"/> do not cover. Tests marked <c>Skip</c> state the expected
/// behaviour of a defect the tester found and fail today; remove the <c>Skip</c> when the defect is fixed.
/// </summary>
public sealed class SearchStatsTesterTests(SqlServerDatabaseFixture sqlServer)
{
    private const string Chrome =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private static DateTimeOffset TenInColombo(DateOnly day) => new(day.ToDateTime(new TimeOnly(4, 30)), TimeSpan.Zero);

    private ApiFactory Factory(DateOnly today, IReadOnlyDictionary<string, string?>? configuration = null) =>
        new(
            sqlServer.ConnectionString,
            additionalConfiguration: configuration,
            configureServices: services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(TenInColombo(today)));
            });

    /// <summary>
    /// The same host, but one <c>TestServer</c> can be many visitors: <see cref="HeaderAddressStartupFilter"/> sets
    /// <c>Connection.RemoteIpAddress</c> from <see cref="AddressHeader"/> ahead of the whole pipeline, which is
    /// what the rate-limit and buffer tests need to tell one address's submissions from another's — the same
    /// mechanism <c>CheckoutPlacementThrottleTests</c> uses for the order-placement throttle.
    /// </summary>
    private const string AddressHeader = "X-Test-Remote-Address";

    private ApiFactory FactoryWithAddresses(DateOnly today, IReadOnlyDictionary<string, string?>? configuration = null) =>
        new(
            sqlServer.ConnectionString,
            additionalConfiguration: configuration,
            configureServices: services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(TenInColombo(today)));
                services.AddSingleton<IStartupFilter>(new HeaderAddressStartupFilter());
            });

    private static async Task<HttpResponseMessage> SubmitFromAsync(HttpClient client, string address, string term)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/search/submissions", UriKind.Relative))
        {
            Content = JsonContent.Create(new { term }),
        };
        request.Headers.Add(AddressHeader, address);

        return await client.SendAsync(request, Token);
    }

    /// <summary>Sets <c>Connection.RemoteIpAddress</c> from a request header, ahead of the whole pipeline.</summary>
    private sealed class HeaderAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            builder =>
            {
                builder.Use(async (context, continuation) =>
                {
                    if (context.Request.Headers.TryGetValue(AddressHeader, out var value)
                        && IPAddress.TryParse(value.ToString(), out var address))
                    {
                        context.Connection.RemoteIpAddress = address;
                    }

                    await continuation(context);
                });

                next(builder);
            };
    }

    private async Task<DateOnly> FreshDayAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var today = new DateOnly(2090, 1, 1).AddDays(Random.Shared.Next(0, 40_000));
        await ExecuteAsync(
            "DELETE FROM [analytics].[SearchQueriesDaily] WHERE [Date] BETWEEN @from AND @to;",
            ("@from", today.AddDays(-31)),
            ("@to", today.AddDays(1)));
        return today;
    }

    private static HttpClient Client(ApiFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Chrome);
        return client;
    }

    private static async Task SubmitAsync(HttpClient client, string term)
    {
        using var response = await client.PostAsJsonAsync(new Uri("/api/search/submissions", UriKind.Relative), new { term }, Token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private async Task<List<SearchQueryDaily>> RowsAsync(DateOnly day)
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SearchQueriesDaily.AsNoTracking().Where(row => row.Date == day).ToListAsync(Token);
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new SqlParameter(name, value is DateOnly day ? day.ToDateTime(TimeOnly.MinValue) : value));
        }

        await command.ExecuteNonQueryAsync(Token);
    }

    private static string Letters()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        return string.Concat(bytes.Take(6).Select(b => (char)('a' + (b % 26))));
    }

    // ================================================================== multiple API instances

    [Fact]
    public async Task Two_api_instances_flushing_the_same_terms_at_the_same_moment_lose_no_count()
    {
        var today = await FreshDayAsync();
        await using var first = Factory(today);
        await using var second = Factory(today);
        var buffers = new[] { first.Services.GetRequiredService<SearchQueryBuffer>(), second.Services.GetRequiredService<SearchQueryBuffer>() };
        var writers = new[] { first.Services.GetRequiredService<SearchQueryWriterService>(), second.Services.GetRequiredService<SearchQueryWriterService>() };
        var stem = "twin " + Letters();
        var expected = 0;

        for (var round = 0; round < 15; round++)
        {
            for (var key = 0; key < 8; key++)
            {
                var term = $"{stem} r{(char)('a' + round)} k{(char)('a' + key)}";
                buffers[0].TryRecord(term, TenInColombo(today));
                buffers[1].TryRecord(term, TenInColombo(today));
                buffers[1].TryRecord(term, TenInColombo(today));
                expected += 3;
            }

            await Task.WhenAll(writers.Select(writer => writer.FlushAsync(Token)));
        }

        var rows = await RowsAsync(today);
        Assert.Equal(15 * 8, rows.Count);
        Assert.Equal(expected, rows.Sum(row => row.Searches));
        Assert.All(buffers, buffer => Assert.Equal(0, buffer.Dropped));
    }

    // ================================================================== the public list

    [Fact]
    public async Task A_term_of_exactly_forty_characters_is_shown_and_one_of_forty_one_is_not()
    {
        var today = await FreshDayAsync();
        var stem = "edge " + Letters() + " ";
        var forty = stem + new string('k', 40 - stem.Length);
        var fortyOne = stem + new string('m', 41 - stem.Length);
        Assert.Equal(40, forty.Length);

        await ExecuteAsync(
            "INSERT INTO [analytics].[SearchQueriesDaily] ([Date], [QueryNormalized], [Searches], [ZeroResultSearches]) VALUES (@d, @a, 50, 0), (@d, @b, 60, 0);",
            ("@d", today),
            ("@a", forty),
            ("@b", fortyOne));

        await using var factory = Factory(today);
        using var client = Client(factory);
        var dto = await client.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token);

        Assert.Contains(forty, dto!.Terms);
        Assert.DoesNotContain(fortyOne, dto.Terms);
    }

    [Fact]
    public async Task Terms_with_equal_counts_are_listed_in_a_stable_alphabetical_order()
    {
        var today = await FreshDayAsync();
        var stem = Letters();
        var names = new[] { $"tie {stem} c", $"tie {stem} a", $"tie {stem} b" };
        foreach (var name in names)
        {
            await ExecuteAsync(
                "INSERT INTO [analytics].[SearchQueriesDaily] ([Date], [QueryNormalized], [Searches], [ZeroResultSearches]) VALUES (@d, @t, 999999, 0);",
                ("@d", today),
                ("@t", name));
        }

        await using var factory = Factory(today);
        using var client = Client(factory);
        var dto = await client.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token);

        Assert.Equal(names.Order(StringComparer.Ordinal), dto!.Terms.Where(term => term.StartsWith($"tie {stem}", StringComparison.Ordinal)));
    }

    // ================================================================== admin

    [Fact]
    public async Task The_admin_screen_reads_fresh_counts_not_the_public_lists_five_minute_cache()
    {
        var today = await FreshDayAsync();
        var term = "fresh " + Letters();
        await ExecuteAsync(
            "INSERT INTO [analytics].[SearchQueriesDaily] ([Date], [QueryNormalized], [Searches], [ZeroResultSearches]) VALUES (@d, @t, 10, 0);",
            ("@d", today),
            ("@t", term));

        await using var factory = Factory(today);
        using var shopper = Client(factory);
        var viewer = await AdminCatalogApiClient.SignInAsync(factory, "srch-fresh", [Permissions.Content.View], Token);

        // Warm the public cache with 10.
        Assert.Contains(term, (await shopper.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token))!.Terms);

        await ExecuteAsync(
            "UPDATE [analytics].[SearchQueriesDaily] SET [Searches] = 25 WHERE [Date] = @d AND [QueryNormalized] = @t;",
            ("@d", today),
            ("@t", term));

        var (status, body) = await viewer.GetAsync("/api/admin/content/popular-searches", Token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(body.GetProperty("shown").EnumerateArray(), row =>
            row.GetProperty("term").GetString() == term && row.GetProperty("searches").GetInt32() == 25);
    }

    [Fact]
    public async Task Showing_a_term_again_reaches_the_public_list_at_once_even_when_the_list_was_cached()
    {
        var today = await FreshDayAsync();
        var term = "unhide " + Letters();
        await ExecuteAsync(
            "INSERT INTO [analytics].[SearchQueriesDaily] ([Date], [QueryNormalized], [Searches], [ZeroResultSearches]) VALUES (@d, @t, 30, 0);",
            ("@d", today),
            ("@t", term));
        await ExecuteAsync(
            "INSERT INTO [analytics].[HiddenSearchTerms] ([QueryNormalized], [HiddenAtUtc]) VALUES (@t, SYSUTCDATETIME());",
            ("@t", term));

        try
        {
            await using var factory = Factory(today);
            using var shopper = Client(factory);
            var editor = await AdminCatalogApiClient.SignInAsync(factory, "srch-unhide", [Permissions.Content.View, Permissions.Content.Edit], Token);

            // Cached without the term.
            Assert.DoesNotContain(term, (await shopper.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token))!.Terms);

            var (status, _) = await editor.PostAsync("/api/admin/content/popular-searches/show", new { term }, Token);
            Assert.Equal(HttpStatusCode.OK, status);

            Assert.Contains(term, (await shopper.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token))!.Terms);
        }
        finally
        {
            await ExecuteAsync("DELETE FROM [analytics].[HiddenSearchTerms] WHERE [QueryNormalized] = @t;", ("@t", term));
        }
    }

    [Fact]
    public async Task The_admin_list_is_never_cached_by_a_browser_or_proxy()
    {
        var today = await FreshDayAsync();
        await using var factory = Factory(today);
        var viewer = await AdminCatalogApiClient.SignInAsync(factory, "srch-cache", [Permissions.Content.View], Token);

        using var response = await viewer.GetResponseAsync("/api/admin/content/popular-searches", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    // ================================================================== the beacon

    [Fact]
    public async Task A_pasted_term_longer_than_the_column_is_counted_cut_to_one_hundred_characters()
    {
        var today = await FreshDayAsync();
        var stem = "long " + Letters() + " ";
        var raw = stem + string.Concat(Enumerable.Repeat("denim ", 40));
        Assert.InRange(raw.Length, 201, 400);

        await using var factory = Factory(today);
        using var client = Client(factory);
        await SubmitAsync(client, raw);
        await factory.Services.GetRequiredService<SearchQueryWriterService>().FlushAsync(Token);

        var row = Assert.Single(await RowsAsync(today));
        Assert.Equal(SearchQueryText.Normalise(raw), row.QueryNormalized);
        Assert.InRange(row.QueryNormalized.Length, 95, SearchQueryDaily.QueryMaxLength);
    }

    [Fact]
    public async Task The_beacons_request_log_line_is_debug_so_it_does_not_copy_every_search_into_the_log()
    {
        var today = await FreshDayAsync();
        var sink = new LevelSink();
        await using var factory = new ApiFactory(
            sqlServer.ConnectionString,
            additionalConfiguration: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Serilog:MinimumLevel:Default"] = "Verbose",
                ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Verbose",
            },
            configureServices: services =>
            {
                services.AddSingleton<Serilog.Core.ILogEventSink>(sink);
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(TenInColombo(today)));
            });
        using var client = Client(factory);

        await SubmitAsync(client, "linen");
        using (var popular = await client.GetAsync(new Uri("/api/search/popular", UriKind.Relative), Token))
        {
            Assert.Equal(HttpStatusCode.OK, popular.StatusCode);
        }

        var beacon = Assert.Single(sink.Completions, entry => entry.Path == "/api/search/submissions");
        Assert.Equal(Serilog.Events.LogEventLevel.Debug, beacon.Level);

        // The control: an ordinary request is still logged at Information.
        var list = Assert.Single(sink.Completions, entry => entry.Path == "/api/search/popular");
        Assert.Equal(Serilog.Events.LogEventLevel.Information, list.Level);
    }

    private sealed class LevelSink : Serilog.Core.ILogEventSink
    {
        private readonly List<(string Path, Serilog.Events.LogEventLevel Level)> _completions = [];

        public IReadOnlyList<(string Path, Serilog.Events.LogEventLevel Level)> Completions
        {
            get
            {
                lock (_completions)
                {
                    return [.. _completions];
                }
            }
        }

        public void Emit(Serilog.Events.LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);

            if (logEvent.Properties.TryGetValue("RequestPath", out var path)
                && logEvent.Properties.ContainsKey("StatusCode")
                && path is Serilog.Events.ScalarValue { Value: string value })
            {
                lock (_completions)
                {
                    _completions.Add((value, logEvent.Level));
                }
            }
        }
    }

    // ================================================================== the rate limit (issue #78 tester pass)

    /// <summary>
    /// Was a defect (tester, 2026-10-21): the beacon had no rate limit at all, and 2,000 submissions from one
    /// client in under a second all counted. <see cref="SearchSubmissionThrottle"/> now answers 204 for every one
    /// of them (a refused submission is dropped, not reported) but only <see cref="SearchSubmissionThrottle.PermitLimit"/>
    /// of them are counted.
    /// </summary>
    [Fact]
    public async Task Submissions_past_the_rate_limit_are_silently_dropped_and_do_not_count()
    {
        var today = await FreshDayAsync();
        const string address = "203.0.113.50";
        var term = "flood " + Letters();

        await using var factory = FactoryWithAddresses(today);
        using var client = Client(factory);

        var attempts = SearchSubmissionThrottle.PermitLimit * 4;
        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(_ => SubmitFromAsync(client, address, term)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));

        await factory.Services.GetRequiredService<SearchQueryWriterService>().FlushAsync(Token);

        var row = Assert.Single(await RowsAsync(today));
        Assert.Equal(term, row.QueryNormalized);
        Assert.Equal(SearchSubmissionThrottle.PermitLimit, row.Searches);
    }

    /// <summary>One address being throttled must not cost another address its own budget.</summary>
    [Fact]
    public async Task Two_addresses_are_throttled_independently()
    {
        var today = await FreshDayAsync();
        const string flooder = "203.0.113.60";
        const string genuine = "203.0.113.61";
        var floodTerm = "flood " + Letters();
        var genuineTerm = "genuine " + Letters();

        await using var factory = FactoryWithAddresses(today);
        using var client = Client(factory);

        await Task.WhenAll(Enumerable.Range(0, SearchSubmissionThrottle.PermitLimit * 3)
            .Select(_ => SubmitFromAsync(client, flooder, floodTerm)));
        await SubmitFromAsync(client, genuine, genuineTerm);

        await factory.Services.GetRequiredService<SearchQueryWriterService>().FlushAsync(Token);

        var rows = await RowsAsync(today);
        Assert.Equal(SearchSubmissionThrottle.PermitLimit, Assert.Single(rows, row => row.QueryNormalized == floodTerm).Searches);
        Assert.Equal(1, Assert.Single(rows, row => row.QueryNormalized == genuineTerm).Searches);
    }

    /// <summary>
    /// Re-measured after the rate limit (MEDIUM, tester pass): before it, 6,000 unique junk terms from one client
    /// filled the whole 5,000-entry buffer and a legitimate term from another visitor was lost. The limit's window
    /// (one minute) matches the buffer's flush interval (<see cref="SearchStatsOptions.DefaultFlushSeconds"/>), so
    /// one address can now put at most <see cref="SearchSubmissionThrottle.PermitLimit"/> distinct terms into the
    /// buffer before the next flush empties it again — far short of <see cref="SearchStatsOptions.MaxBufferedTerms"/>'s
    /// default of 5,000, and nowhere near enough to crowd out anyone else's search.
    /// </summary>
    [Fact]
    public async Task A_client_that_respects_the_rate_limit_cannot_fill_the_buffer_within_one_flush_window()
    {
        var today = await FreshDayAsync();
        const string flooder = "203.0.113.70";
        const string genuine = "203.0.113.71";
        var genuineTerm = "last straw " + Letters();

        await using var factory = FactoryWithAddresses(today, new Dictionary<string, string?> { ["SearchStats:MaxBufferedTerms"] = "5000" });
        using var client = Client(factory);
        var buffer = factory.Services.GetRequiredService<SearchQueryBuffer>();

        // Every distinct term this one address could possibly get through the throttle in a single window.
        var attempts = SearchSubmissionThrottle.PermitLimit * 3;
        await Task.WhenAll(Enumerable.Range(0, attempts).Select(index => SubmitFromAsync(client, flooder, $"junk {Letters()} {index}")));

        Assert.True(buffer.Pending <= SearchSubmissionThrottle.PermitLimit, $"the buffer held {buffer.Pending} entries from one throttled address");

        // A different visitor's search still has room and is still counted — the whole point of the limit.
        var genuineResponse = await SubmitFromAsync(client, genuine, genuineTerm);
        Assert.Equal(HttpStatusCode.NoContent, genuineResponse.StatusCode);

        await factory.Services.GetRequiredService<SearchQueryWriterService>().FlushAsync(Token);

        Assert.Contains(await RowsAsync(today), row => row.QueryNormalized == genuineTerm && row.Searches == 1);
    }

    // ================================================================== shutdown

    [Fact]
    public async Task Counts_still_in_the_buffer_are_written_when_the_api_stops()
    {
        var today = await FreshDayAsync();
        var term = "drain " + Letters();

        var factory = Factory(today, new Dictionary<string, string?> { ["SearchStats:FlushSeconds"] = "600" });
        await using (factory)
        {
            using var client = Client(factory);
            await SubmitAsync(client, term);
            await SubmitAsync(client, term);
            Assert.Empty(await RowsAsync(today));
        }

        var row = Assert.Single(await RowsAsync(today));
        Assert.Equal(term, row.QueryNormalized);
        Assert.Equal(2, row.Searches);
    }

    // ================================================================== defects found by the tester, now fixed

    /// <summary>
    /// Was a defect (tester, 2026-10-21): only the first 5 tokens were searched, so a phrase of five matching
    /// substrings plus a sixth, unrelated word reached the public list with five requests. Fixed by
    /// <c>SearchTerms.HasUncheckedTokens</c>, which <c>SearchQueryStore.HasResultsAsync</c> checks before it ever
    /// queries: a term with more real tokens than the search would look at is never credited with "having
    /// results".
    /// </summary>
    [Fact]
    public async Task A_phrase_whose_extra_words_match_nothing_never_reaches_the_public_list()
    {
        var today = await FreshDayAsync();
        var phrase = "li in ne en lin q" + Letters();
        await using var factory = Factory(today);
        using var client = Client(factory);

        for (var i = 0; i < 5; i++)
        {
            await SubmitAsync(client, phrase);
        }

        await factory.Services.GetRequiredService<SearchQueryWriterService>().FlushAsync(Token);

        var dto = await client.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token);
        Assert.DoesNotContain(phrase, dto!.Terms);
    }

    /// <summary>
    /// Was a defect (tester, 2026-10-21): the privacy rule ran after the 100-character cut, so an address past the
    /// cut-off was stored as its local part alone ("…aaaa john.perera"). <c>SearchQueryText.Normalise</c> now
    /// checks <c>IsPersonal</c> on the whole normalised term before <c>Truncate</c> runs.
    /// </summary>
    [Fact]
    public async Task An_email_address_cut_off_by_the_hundred_character_limit_is_not_stored()
    {
        var today = await FreshDayAsync();
        var local = "jane.q" + Letters();
        await using var factory = Factory(today);
        using var client = Client(factory);

        await SubmitAsync(client, new string('a', 88) + " " + local + "@example.com");
        await factory.Services.GetRequiredService<SearchQueryWriterService>().FlushAsync(Token);

        Assert.DoesNotContain(await RowsAsync(today), row => row.QueryNormalized.Contains(local, StringComparison.Ordinal));
    }

    [Theory(Skip = "DEFECT (tester 2026-10-21): order numbers written with en or em dashes slip past the SD-/INV- shape.")]
    [InlineData("sd–2026–12")]
    [InlineData("order sd—2026—1")]
    public async Task An_order_number_written_with_typographic_dashes_is_not_stored(string term)
    {
        var today = await FreshDayAsync();
        await using var factory = Factory(today);
        using var client = Client(factory);

        await SubmitAsync(client, term);
        await factory.Services.GetRequiredService<SearchQueryWriterService>().FlushAsync(Token);

        Assert.Empty(await RowsAsync(today));
    }
}
