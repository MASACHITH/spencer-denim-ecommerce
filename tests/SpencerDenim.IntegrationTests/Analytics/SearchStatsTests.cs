using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Serilog.Core;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Analytics.Search;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Analytics;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Analytics;

/// <summary>
/// SRCH-01's popular searches end to end (issue #78; doc 08 DB25, §7.6, §8.4): the beacon, the privacy rule, the bot
/// filter, the buffered race-safe upsert under concurrency, the public list and its abuse controls, the staff hide
/// list, and the 13-month purge.
/// <para>
/// <b>Every test owns a Colombo day of its own</b>, far in the future and picked at random, by running its host on a
/// fixed clock: the table is keyed by day, so nothing another test (or an earlier run against this shared database)
/// counted can appear in a list this test reads. Terms that need no catalogue match are unique per test too.
/// </para>
/// </summary>
public sealed class SearchStatsTests(SqlServerDatabaseFixture sqlServer)
{
    private const string Chrome =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed record Host(ApiFactory Factory, CapturingSink Logs, DateOnly Today) : IAsyncDisposable
    {
        public SearchQueryWriterService Writer => Factory.Services.GetRequiredService<SearchQueryWriterService>();

        public SearchQueryBuffer Buffer => Factory.Services.GetRequiredService<SearchQueryBuffer>();

        public HttpClient Client(string? userAgent = Chrome)
        {
            var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            if (userAgent is not null)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
            }

            return client;
        }

        public ValueTask DisposeAsync() => Factory.DisposeAsync();
    }

    /// <summary>A host on a fixed clock at 10:00 Colombo on a random far-future day nobody else uses.</summary>
    private async Task<Host> StartAsync(IReadOnlyDictionary<string, string?>? configure = null)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        var today = new DateOnly(2090, 1, 1).AddDays(Random.Shared.Next(0, 40_000));
        var now = new DateTimeOffset(today.ToDateTime(new TimeOnly(4, 30)), TimeSpan.Zero);
        await ExecuteAsync(
            "DELETE FROM [analytics].[SearchQueriesDaily] WHERE [Date] BETWEEN @from AND @to;",
            ("@from", today.AddMonths(-14)),
            ("@to", today.AddDays(1)));

        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Serilog:MinimumLevel:Default"] = "Verbose",
            ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Verbose",
            ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Verbose",
        };
        foreach (var (key, value) in configure ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }

        var logs = new CapturingSink();
        var factory = new ApiFactory(
            sqlServer.ConnectionString,
            additionalConfiguration: configuration,
            configureServices: services =>
            {
                services.AddSingleton<ILogEventSink>(logs);
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(now));
            });

        return new Host(factory, logs, today);
    }

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient client, object body) =>
        client.PostAsJsonAsync(new Uri("/api/search/submissions", UriKind.Relative), body, Token);

    private async Task<List<SearchQueryDaily>> RowsAsync(DateOnly day)
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.SearchQueriesDaily.AsNoTracking().Where(row => row.Date == day).OrderBy(row => row.QueryNormalized).ToListAsync(Token);
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

    private Task SeedAsync(DateOnly day, string term, int searches, int zero = 0) =>
        ExecuteAsync(
            "INSERT INTO [analytics].[SearchQueriesDaily] ([Date], [QueryNormalized], [Searches], [ZeroResultSearches]) VALUES (@d, @t, @n, @z);",
            ("@d", day),
            ("@t", term),
            ("@n", searches),
            ("@z", zero));

    /// <summary>
    /// A term no other test (or run) uses, in letters only: a hex suffix would carry digits, and seven digits make
    /// any term personal by the rule under test.
    /// </summary>
    private static string Unique(string label)
    {
        var letters = string.Concat(Guid.NewGuid().ToByteArray().Take(6).Select(b => (char)('a' + (b % 26))));
        return $"{label} q{letters}";
    }

    // ================================================================== recording

    [Fact]
    public async Task A_submitted_search_is_counted_normalised_once_per_submission_and_nothing_else_is_stored()
    {
        await using var host = await StartAsync();
        using var client = host.Client();

        foreach (var spelling in new[] { "  Linen  ", "linen", "LINEN" })
        {
            using var response = await SubmitAsync(client, new { term = spelling });
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

            // No session cookie, no visitor id: a search count carries nothing that could tie it to anyone.
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        Assert.Equal(3, await host.Writer.FlushAsync(Token));

        var row = Assert.Single(await RowsAsync(host.Today));
        Assert.Equal("linen", row.QueryNormalized);
        Assert.Equal(3, row.Searches);

        // "linen" matches the demo catalogue by material, so none of the three found nothing.
        Assert.Equal(0, row.ZeroResultSearches);
    }

    [Fact]
    public async Task Which_searches_found_nothing_is_decided_by_the_server_running_the_real_search()
    {
        await using var host = await StartAsync();
        using var client = host.Client();
        var nothing = "zzqx " + Guid.NewGuid().ToString("N")[..8];

        (await SubmitAsync(client, new { term = nothing, resultCount = 99 })).Dispose();
        (await SubmitAsync(client, new { term = nothing })).Dispose();

        await host.Writer.FlushAsync(Token);

        var row = Assert.Single(await RowsAsync(host.Today));
        Assert.Equal(2, row.Searches);
        Assert.Equal(2, row.ZeroResultSearches);
    }

    [Theory]
    [InlineData("someone@example.com")]
    [InlineData("my email is jane.doe@example.com please")]
    [InlineData("jane＠example.com")]
    [InlineData("077 123 4567")]
    [InlineData("+94-77-123-4567")]
    [InlineData("٠٧٧١٢٣٤٥٦٧")]
    [InlineData("SD-2026-000123")]
    [InlineData("sd 2026 12")]
    [InlineData("levis 501 32x34")]
    public async Task A_term_that_looks_personal_is_never_counted_stored_or_logged(string term)
    {
        await using var host = await StartAsync();
        using var client = host.Client();

        using var response = await SubmitAsync(client, new { term });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(0, host.Buffer.Pending);
        await host.Writer.FlushAsync(Token);
        Assert.Empty(await RowsAsync(host.Today));

        var lowered = term.ToLowerInvariant();
        Assert.DoesNotContain(
            await TrafficTestHost.EveryAnalyticsStringAsync(sqlServer.ConnectionString, Token),
            value => value.Contains(lowered, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(host.Logs.Events, line => line.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)", false)]
    [InlineData("curl/8.4.0", false)]
    [InlineData(null, false)]
    [InlineData(Chrome, true)]
    public async Task A_bot_or_an_automated_browser_is_not_counted(string? userAgent, bool automated)
    {
        await using var host = await StartAsync();
        using var client = host.Client(userAgent);

        using var response = await SubmitAsync(client, new { term = "linen", automated });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await host.Writer.FlushAsync(Token);
        Assert.Empty(await RowsAsync(host.Today));
    }

    [Fact]
    public async Task Rubbish_bodies_and_unusable_terms_answer_204_and_count_nothing()
    {
        await using var host = await StartAsync();
        using var client = host.Client();

        foreach (var body in new object[] { new { term = "" }, new { term = "a" }, new { term = "a b" }, new { }, new { term = new string('x', 401) } })
        {
            using var response = await SubmitAsync(client, body);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        await host.Writer.FlushAsync(Token);
        Assert.Empty(await RowsAsync(host.Today));
    }

    [Fact]
    public async Task Recording_switched_off_still_answers_204_and_counts_nothing()
    {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["SearchStats:RecordingEnabled"] = "false" });
        using var client = host.Client();

        using var response = await SubmitAsync(client, new { term = "linen" });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await host.Writer.FlushAsync(Token);
        Assert.Empty(await RowsAsync(host.Today));
    }

    [Fact]
    public async Task A_second_flush_adds_to_the_row_the_first_one_wrote()
    {
        await using var host = await StartAsync();
        var term = Unique("second flush");

        host.Buffer.TryRecord(term, new DateTimeOffset(host.Today.ToDateTime(new TimeOnly(5, 0)), TimeSpan.Zero));
        await host.Writer.FlushAsync(Token);

        host.Buffer.TryRecord(term, new DateTimeOffset(host.Today.ToDateTime(new TimeOnly(6, 0)), TimeSpan.Zero));
        host.Buffer.TryRecord(term, new DateTimeOffset(host.Today.ToDateTime(new TimeOnly(6, 1)), TimeSpan.Zero));
        await host.Writer.FlushAsync(Token);

        var row = Assert.Single(await RowsAsync(host.Today));
        Assert.Equal(3, row.Searches);
        Assert.Equal(3, row.ZeroResultSearches);
    }

    /// <summary>
    /// DB25's race, made as wide as a test can make it: many writers, each with its own connection, all upserting the
    /// same handful of (day, term) keys that do not exist yet — so most of them find no row, race to insert, and every
    /// loser must fall back to the update. Under RCSI a check-then-act upsert loses counts or throws 2627 here; this
    /// one must do neither.
    /// </summary>
    [Fact]
    public async Task Concurrent_flushes_of_the_same_new_terms_lose_no_count_and_throw_nothing()
    {
        await using var host = await StartAsync();
        var terms = Enumerable.Range(0, 4).Select(i => Unique($"race {i}")).ToArray();
        const int Writers = 12;
        const int RoundsPerWriter = 15;

        using var start = new SemaphoreSlim(0, Writers);

        var writers = Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
        {
            await start.WaitAsync(Token);

            for (var round = 0; round < RoundsPerWriter; round++)
            {
                await using var scope = host.Factory.Services.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<SearchQueryStore>();

                var counts = terms.ToDictionary(term => new SearchQueryKey(host.Today, term), _ => writer + 1);
                await store.UpsertAsync(counts, Token);
            }
        }, Token)).ToArray();

        start.Release(Writers);
        await Task.WhenAll(writers);

        var expected = Enumerable.Range(1, Writers).Sum() * RoundsPerWriter;
        var rows = await RowsAsync(host.Today);

        Assert.Equal(terms.Length, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(expected, row.Searches);
            Assert.Equal(expected, row.ZeroResultSearches);
        });
    }

    /// <summary>
    /// DB25's statement on its own, raced the hard way: every round a key that does not exist yet, sixteen writers on
    /// sixteen connections released together by a barrier, so most rounds have several writers finding no row and
    /// racing the <c>INSERT</c>. Every loser must take the 2627 path and fall back to the <c>UPDATE</c>: a version
    /// without that retry loses counts here, and one that rethrows the duplicate key fails here. (The store-level test
    /// above runs the catalogue probe first, which staggers its writers enough that the insert race is rare — the
    /// mutation run showed both of those mutants surviving it.)
    /// </summary>
    [Fact]
    public async Task The_upsert_statement_settles_the_insert_race_every_time_under_rcsi()
    {
        await using var host = await StartAsync();
        const int Writers = 16;
        const int Rounds = 40;
        var keys = Enumerable.Range(0, Rounds).Select(round => Unique($"insert race {round:D2}")).ToArray();
        using var barrier = new Barrier(Writers);

        var writers = Enumerable.Range(0, Writers).Select(_ => Task.Run(async () =>
        {
            await using var connection = new SqlConnection(sqlServer.ConnectionString);
            await connection.OpenAsync(Token);

            try
            {
                foreach (var key in keys)
                {
                    // Bounded, so a writer that has failed can never leave the others waiting for ever.
                    if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30), Token))
                    {
                        throw new TimeoutException("A writer never reached the barrier.");
                    }

                    await using var command = connection.CreateCommand();
                    command.CommandText = SearchQueryStore.UpsertSql;
                    command.Parameters.Add(new SqlParameter("@date", System.Data.SqlDbType.Date) { Value = host.Today.ToDateTime(TimeOnly.MinValue) });
                    command.Parameters.Add(new SqlParameter("@term", System.Data.SqlDbType.NVarChar, 100) { Value = key });
                    command.Parameters.Add(new SqlParameter("@n", System.Data.SqlDbType.Int) { Value = 1 });
                    command.Parameters.Add(new SqlParameter("@zero", System.Data.SqlDbType.Int) { Value = 0 });
                    await command.ExecuteNonQueryAsync(Token);
                }
            }
            catch
            {
                // Out of the barrier, so the writers still running are not left one short.
                barrier.RemoveParticipant();
                throw;
            }
        }, Token)).ToArray();

        await Task.WhenAll(writers);

        var rows = (await RowsAsync(host.Today)).ToDictionary(row => row.QueryNormalized, row => row.Searches, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(Rounds, rows.Count);
        Assert.All(keys, key => Assert.Equal(Writers, rows[key]));
    }

    [Fact]
    public async Task Two_spellings_the_collation_calls_equal_land_on_one_row()
    {
        await using var host = await StartAsync();
        var stem = Unique("strasse");

        await using var scope = host.Factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<SearchQueryStore>();

        // Ordinal-distinct in .NET, one key to Latin1_General_100_CI_AS_SC.
        await store.UpsertAsync(
            new Dictionary<SearchQueryKey, int>
            {
                [new SearchQueryKey(host.Today, stem.Replace("ss", "ß", StringComparison.Ordinal))] = 2,
                [new SearchQueryKey(host.Today, stem)] = 3,
            },
            Token);

        var row = Assert.Single(await RowsAsync(host.Today));
        Assert.Equal(5, row.Searches);
    }

    [Fact]
    public async Task A_flush_against_a_database_that_is_down_drops_its_counts_logs_once_and_does_not_throw()
    {
        await using var factory = new ApiFactory(
            ApiFactory.UnreachableConnectionString,
            additionalConfiguration: new Dictionary<string, string?> { ["SearchStats:FlushSeconds"] = "600" });
        var buffer = factory.Services.GetRequiredService<SearchQueryBuffer>();
        var writer = factory.Services.GetRequiredService<SearchQueryWriterService>();

        buffer.TryRecord("linen", DateTimeOffset.UtcNow);
        buffer.TryRecord("linen", DateTimeOffset.UtcNow);

        Assert.Equal(0, await writer.FlushAsync(Token));
        Assert.Equal(2, buffer.Dropped);
        Assert.Equal(0, buffer.Pending);
    }

    // ================================================================== the public list

    [Fact]
    public async Task The_public_list_shows_the_top_terms_with_results_in_the_window_and_applies_every_control()
    {
        await using var host = await StartAsync();
        var d = host.Today;
        var top = Unique("top");
        var second = Unique("second");
        var spread = Unique("spread");
        var tooFew = Unique("toofew");
        var mostlyEmpty = Unique("mostly empty");
        var tooOld = Unique("tooold");
        var hidden = Unique("hidden");
        var longTerm = Unique("long") + new string('x', 30);

        await SeedAsync(d, top, 50);
        await SeedAsync(d.AddDays(-3), second, 20);
        await SeedAsync(d.AddDays(-29), spread, 3);
        await SeedAsync(d, spread, 3);
        await SeedAsync(d, tooFew, 4);
        await SeedAsync(d, mostlyEmpty, 40, zero: 37);
        await SeedAsync(d.AddDays(-30), tooOld, 90);
        await SeedAsync(d, hidden, 70);
        await SeedAsync(d, longTerm, 80);
        await ExecuteAsync(
            "INSERT INTO [analytics].[HiddenSearchTerms] ([QueryNormalized], [HiddenAtUtc]) VALUES (@t, SYSUTCDATETIME());",
            ("@t", hidden));

        try
        {
            using var client = host.Client();
            using var response = await client.GetAsync(new Uri("/api/search/popular", UriKind.Relative), Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("public, max-age=300", response.Headers.CacheControl?.ToString());

            var dto = await response.Content.ReadFromJsonAsync<PopularSearchesDto>(Json, Token);

            // 50, 20, 6 (3 + 3 across the window's two ends). Not: 4 (below five), 3 with results of 40, outside the
            // window, hidden, longer than 40 characters.
            Assert.Equal([top, second, spread], dto!.Terms);
        }
        finally
        {
            await ExecuteAsync("DELETE FROM [analytics].[HiddenSearchTerms] WHERE [QueryNormalized] = @t;", ("@t", hidden));
        }
    }

    [Fact]
    public async Task The_public_list_is_at_most_eight_and_an_empty_shop_has_an_empty_list()
    {
        await using var host = await StartAsync();
        using var client = host.Client();

        var empty = await client.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token);
        Assert.Empty(empty!.Terms);

        // A second host on the same day: the first host cached its empty answer for five minutes, which is the point.
        await using var fresh = new ApiFactory(
            sqlServer.ConnectionString,
            configureServices: services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(new FixedClock(new DateTimeOffset(host.Today.ToDateTime(new TimeOnly(4, 30)), TimeSpan.Zero)));
            });

        for (var i = 0; i < 10; i++)
        {
            await SeedAsync(host.Today, Unique($"t{i:D2}"), 100 - i);
        }

        using var freshClient = fresh.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        var full = await freshClient.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token);

        Assert.Equal(PopularSearchRules.Take, full!.Terms.Count);
        Assert.StartsWith("t00", full.Terms[0], StringComparison.Ordinal);

        var cached = await client.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token);
        Assert.Empty(cached!.Terms);
    }

    [Fact]
    public async Task The_minimum_is_configurable()
    {
        await using var host = await StartAsync(new Dictionary<string, string?> { ["SearchStats:PopularMinimumSearches"] = "2" });
        var two = Unique("two");
        await SeedAsync(host.Today, two, 2);

        using var client = host.Client();
        var dto = await client.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token);

        Assert.Equal([two], dto!.Terms);
    }

    // ================================================================== the hide list

    [Fact]
    public async Task Staff_can_hide_a_term_which_leaves_the_public_list_at_once_and_show_it_again_both_audited()
    {
        await using var host = await StartAsync();
        var term = Unique("hide me");
        await SeedAsync(host.Today, term, 30);

        var editor = await AdminCatalogApiClient.SignInAsync(host.Factory, "srch-edit", [Permissions.Content.View, Permissions.Content.Edit], Token);
        using var shopper = host.Client();

        // Warm the public cache first, so the hide has to evict it.
        Assert.Contains(term, (await shopper.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token))!.Terms);

        var (status, body) = await editor.GetAsync("/api/admin/content/popular-searches", Token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(body.GetProperty("shown").EnumerateArray(), row =>
            row.GetProperty("term").GetString() == term && row.GetProperty("searches").GetInt32() == 30);
        Assert.Equal(SearchStatsOptions.DefaultPopularMinimumSearches, body.GetProperty("minimumSearches").GetInt32());

        try
        {
            // Typed as a person types it: the handler normalises it to the stored row.
            var (hide, hidden) = await editor.PostAsync("/api/admin/content/popular-searches/hide", new { term = "  " + term.ToUpperInvariant() + " " }, Token);
            Assert.Equal(HttpStatusCode.OK, hide);
            Assert.True(hidden.GetProperty("changed").GetBoolean());

            Assert.DoesNotContain(term, (await shopper.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token))!.Terms);

            var (_, again) = await editor.PostAsync("/api/admin/content/popular-searches/hide", new { term }, Token);
            Assert.False(again.GetProperty("changed").GetBoolean());

            var (_, list) = await editor.GetAsync("/api/admin/content/popular-searches", Token);
            Assert.Contains(list.GetProperty("hidden").EnumerateArray(), row => row.GetProperty("term").GetString() == term);
            Assert.DoesNotContain(list.GetProperty("shown").EnumerateArray(), row => row.GetProperty("term").GetString() == term);

            var (show, shown) = await editor.PostAsync("/api/admin/content/popular-searches/show", new { term }, Token);
            Assert.Equal(HttpStatusCode.OK, show);
            Assert.True(shown.GetProperty("changed").GetBoolean());
            Assert.Contains(term, (await shopper.GetFromJsonAsync<PopularSearchesDto>(new Uri("/api/search/popular", UriKind.Relative), Json, Token))!.Terms);

            var (_, showAgain) = await editor.PostAsync("/api/admin/content/popular-searches/show", new { term }, Token);
            Assert.False(showAgain.GetProperty("changed").GetBoolean());

            using var scope = sqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var audit = await db.AuditLogs.AsNoTracking()
                .Where(row => row.EntityType == HiddenSearchTermHandlers.EntityType && row.EntityId == term)
                .OrderBy(row => row.Id)
                .ToListAsync(Token);

            // One row per change and none for the two no-ops.
            Assert.Equal(["Create", "Delete"], audit.Select(row => row.Action));
            Assert.All(audit, row => Assert.Equal(editor.UserId, row.UserId));
        }
        finally
        {
            await ExecuteAsync("DELETE FROM [analytics].[HiddenSearchTerms] WHERE [QueryNormalized] = @t;", ("@t", term));
        }
    }

    [Fact]
    public async Task Two_staff_hiding_the_same_term_at_once_store_it_once_and_audit_it_once()
    {
        await using var host = await StartAsync();
        var term = Unique("both hide");

        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                await using var scope = host.Factory.Services.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<Application.Abstractions.Messaging.ICommandHandler<HideSearchTermCommand, Application.Abstractions.Results.Result<bool>>>();
                return (await handler.HandleAsync(new HideSearchTermCommand(term, Application.Abstractions.Auditing.AdminActor.Unknown), Token)).Value;
            }, Token)));

            Assert.Equal(1, results.Count(changed => changed));

            using var scope = sqlServer.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.HiddenSearchTerms.CountAsync(row => row.QueryNormalized == term, Token));
            Assert.Equal(1, await db.AuditLogs.CountAsync(row => row.EntityType == HiddenSearchTermHandlers.EntityType && row.EntityId == term, Token));
        }
        finally
        {
            await ExecuteAsync("DELETE FROM [analytics].[HiddenSearchTerms] WHERE [QueryNormalized] = @t;", ("@t", term));
        }
    }

    [Fact]
    public async Task The_hide_list_is_guarded_by_content_permissions_and_antiforgery_and_refuses_personal_terms()
    {
        await using var host = await StartAsync();

        var viewer = await AdminCatalogApiClient.SignInAsync(host.Factory, "srch-view", [Permissions.Content.View], Token);
        var outsider = await AdminCatalogApiClient.SignInAsync(host.Factory, "srch-none", [Permissions.Orders.View], Token);
        var editor = await AdminCatalogApiClient.SignInAsync(host.Factory, "srch-edit2", [Permissions.Content.View, Permissions.Content.Edit], Token);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/admin/content/popular-searches", Token)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/admin/content/popular-searches/hide", new { term = "jeans" }, Token)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/admin/content/popular-searches", Token)).Status);

        using var anonymous = AdminCatalogApiClient.Anonymous(host.Factory);
        using var anonymousResponse = await anonymous.GetAsync(new Uri("/api/admin/content/popular-searches", UriKind.Relative), Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            await editor.SendWithoutAntiforgeryAsync(HttpMethod.Post, "/api/admin/content/popular-searches/hide", new { term = "jeans" }, Token));

        foreach (var personal in new[] { "someone@example.com", "077 123 4567", "SD-2026-000123", "a", "" })
        {
            var (status, problem) = await editor.PostAsync("/api/admin/content/popular-searches/hide", new { term = personal }, Token);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.True(problem.GetProperty("errors").TryGetProperty("term", out _));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await editor.PostRawAsync("/api/admin/content/popular-searches/hide", "{bad", Token)).Status);
    }

    // ================================================================== retention and schema

    [Fact]
    public async Task The_nightly_purge_deletes_search_counts_older_than_thirteen_months_and_keeps_the_rest()
    {
        await using var host = await StartAsync();
        var firstKept = SearchQueryRetention.FirstDayKept(host.Today);
        var old = Unique("old");
        var kept = Unique("kept");

        await SeedAsync(firstKept.AddDays(-1), old, 3);
        await SeedAsync(firstKept.AddDays(-40), old, 3);
        await SeedAsync(firstKept, kept, 3);
        await SeedAsync(host.Today, kept, 3);

        var service = host.Factory.Services.GetRequiredService<TrafficRetentionService>();
        // 04:00 UTC is 09:30 the same day in Colombo.
        var purged = await service.RunSearchPurgeOnceAsync(new DateTimeOffset(host.Today.ToDateTime(new TimeOnly(4, 0)), TimeSpan.Zero), Token);

        Assert.True(purged >= 2);
        Assert.Empty(await RowsAsync(firstKept.AddDays(-1)));
        Assert.Empty(await RowsAsync(firstKept.AddDays(-40)));
        Assert.Single(await RowsAsync(firstKept));
        Assert.Single(await RowsAsync(host.Today));
    }

    [Fact]
    public async Task The_tables_carry_the_collation_keys_and_check_doc_08_specifies()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(Token);

        var columns = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t.name + '.' + c.name + ' ' + ty.name + '(' + CAST(c.max_length / 2 AS varchar(5)) + ') ' + c.collation_name
                FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                WHERE SCHEMA_NAME(t.schema_id) = 'analytics' AND c.name = 'QueryNormalized'
                ORDER BY t.name
                """;
            await using var reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            [
                "HiddenSearchTerms.QueryNormalized nvarchar(100) Latin1_General_100_CI_AS_SC",
                "SearchQueriesDaily.QueryNormalized nvarchar(100) Latin1_General_100_CI_AS_SC",
            ],
            columns);

        var keys = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.name + ':' + c.name + ':' + CAST(ic.key_ordinal AS varchar(3))
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id IN (OBJECT_ID('analytics.SearchQueriesDaily'), OBJECT_ID('analytics.HiddenSearchTerms'))
                ORDER BY i.name, ic.key_ordinal
                """;
            await using var reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                keys.Add(reader.GetString(0));
            }
        }

        Assert.Equal(
            ["PK_HiddenSearchTerms:QueryNormalized:1", "PK_SearchQueriesDaily:Date:1", "PK_SearchQueriesDaily:QueryNormalized:2"],
            keys);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_SearchQueriesDaily_Counts' AND parent_object_id = OBJECT_ID('analytics.SearchQueriesDaily')";
            Assert.Equal(1, (int)(await command.ExecuteScalarAsync(Token))!);
        }
    }
}
