using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;
using CapturedLogEvent = SpencerDenim.IntegrationTests.Api.RequestLogRedactionTests.CapturedLogEvent;

namespace SpencerDenim.IntegrationTests.Api;

/// <summary>
/// <b>#207 review:</b> a database error's text never reaches the log, because SQL Server's text can quote the row.
/// <para>
/// <b>What leaked.</b> EF Core writes <c>SaveChangesFailed</c> and <c>CommandError</c> at Error with the exception,
/// and SQL Server's duplicate-key message ends <c>The duplicate key value is (…)</c>. A checkout double-submit that
/// reached <c>UX_Orders_CheckoutIdempotencyKey</c> wrote the checkout key, which <c>POST /api/checkout/orders</c>
/// answers with the whole order (link, e-mail, phone, address): in effect the order's token. A registration race
/// wrote the shopper's e-mail address the same way, and any unhandled database error was written in full by
/// <c>UseExceptionHandler</c>.
/// </para>
/// <para>
/// <b>How these look.</b> The real API host, the production Serilog pipeline (the levels of
/// <c>appsettings.json</c>, and again with every level at Verbose, as if a deployment lowered one), with a sink
/// registered in the container, which the pipeline wraps exactly as it wraps the console. The races are made
/// certain rather than likely: an EF command interceptor holds the first INSERT for a moment so the other
/// submission passes the pre-check and then hits the index (the same technique as the address-cap race), and it
/// records each SQL failure, so every test first proves the database really said the value, then that no event
/// repeats it.
/// </para>
/// </summary>
public sealed class DatabaseErrorLogRedactionTests(SqlServerDatabaseFixture sqlServer) : CheckoutTestBase(sqlServer)
{
    private const string OrdersInsert = "INSERT INTO [sales].[Orders]";
    private const string UsersInsert = "INSERT INTO [identity].[Users]";
    private const string CheckoutKeyIndex = "UX_Orders_CheckoutIdempotencyKey";

    /// <summary>How long the first INSERT is held: long enough for the other submissions to pass the pre-check.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(1500);

    private readonly CaptureSink _sink = new();
    private readonly SqlFailureInterceptor _interceptor = new();
    private bool _everyLevel;

    protected override ApiFactory CreateFactory()
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Email:StorefrontBaseUrl"] = "https://shop.test",
            ["CustomerSecurity:AnonymousResponseFloorMilliseconds"] = "0",
        };

        if (_everyLevel)
        {
            configuration["Serilog:MinimumLevel:Default"] = "Verbose";
            configuration["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Verbose";
            configuration["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Verbose";
        }

        return new ApiFactory(
            SqlServer.ConnectionString,
            "Production",
            configuration,
            commandInterceptor: _interceptor,
            configureServices: services => services.AddSingleton<ILogEventSink>(_sink));
    }

    /// <summary>
    /// The checkout double-submit: the loser is caught by <c>UX_Orders_CheckoutIdempotencyKey</c> and answered with
    /// the order, and no event, at any level that is written, holds the checkout key.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_checkout_double_submit_that_reaches_the_index_logs_no_checkout_key(bool everyLevel)
    {
        _everyLevel = everyLevel;

        var variantId = await CheckoutVariantIdAsync();
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        var keys = new List<Guid>();

        for (var round = 1; round <= 5 && !_interceptor.SawDuplicateOn(CheckoutKeyIndex); round++)
        {
            // Spare stock, so a loser reserves and is refused by the index rather than by the stock check.
            await SetStockAsync(variantId, 10);

            var (bag, _, api) = await NewBagAsync(variantId, quantity: 2);
            var (_, session) = await api.GetAsync(colombo, standard, Token);
            keys.Add(session!.CheckoutKey);
            var body = ValidBody(session.CheckoutKey, standard, colombo, QuotedTotal(session));

            _interceptor.HoldNext(OrdersInsert, Hold);

            var clients = Enumerable.Range(0, 3).Select(_ => NewCheckoutVisitorWithoutCookieJar()).ToList();
            var results = await Task.WhenAll(clients.Select(client => client.PlaceWithCookieAsync(bag.ToString("D"), body, Token)));

            Assert.All(results, result => Assert.True(
                result.Response.IsSuccessStatusCode,
                $"Round {round}: a submission of one checkout key was answered {(int)result.Response.StatusCode}."));
        }

        // The database really did quote the key: otherwise there was nothing for the log to leak.
        Assert.True(
            _interceptor.SawDuplicateOn(CheckoutKeyIndex),
            "No submission reached UX_Orders_CheckoutIdempotencyKey, so the test proved nothing. Failures seen: "
            + _interceptor.Describe());
        Assert.Contains(_interceptor.Failures, failure => keys.Any(key => failure.Contains(key.ToString("D"), StringComparison.OrdinalIgnoreCase)));

        await _sink.SettleAsync(Token);

        var secrets = keys.SelectMany(KeySpellings).Append("nimali@example.com").ToArray();
        AssertNoEventHolds(secrets, $"checkout race, everyLevel={everyLevel}");
        AssertTheHandledRaceIsNotAnError();

        if (everyLevel)
        {
            // At Verbose the EF Core failure IS written (at Debug), with the index and the number, without the value.
            Assert.Contains(_sink.Events, logged =>
                logged.Source.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                && logged.Level == LogEventLevel.Debug
                && logged.Text.Contains(CheckoutKeyIndex, StringComparison.Ordinal)
                && logged.Text.Contains("SQL Server error 2601", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The registration race: the loser is refused by the unique index on the address and answered like any
    /// taken address, and no event holds the address.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_registration_race_that_reaches_the_index_logs_no_email_address(bool everyLevel)
    {
        _everyLevel = everyLevel;
        SqlServer.SkipIfUnavailable();

        var email = $"race-{Guid.NewGuid():N}@example.com";
        var clients = Enumerable.Range(0, 2)
            .Select(_ => Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = BaseAddress }))
            .ToList();

        _interceptor.HoldNext(UsersInsert, Hold);

        var responses = await Task.WhenAll(clients.Select(client => client.PostAsJsonAsync(
            "/api/account/register",
            new { firstName = "Nimal", lastName = "Perera", email, password = "Correct-Horse-Battery-1!", phone = (string?)null },
            Token)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
        Assert.True(
            _interceptor.Failures.Any(failure => failure.Contains(email, StringComparison.OrdinalIgnoreCase)),
            "Neither registration reached the unique index on the address, so the test proved nothing. Failures seen: "
            + _interceptor.Describe());

        await _sink.SettleAsync(Token);

        AssertNoEventHolds([email, email[..email.IndexOf('@', StringComparison.Ordinal)]], $"registration race, everyLevel={everyLevel}");
        AssertTheHandledRaceIsNotAnError();

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// A database error nobody handles reaches <c>UseExceptionHandler</c>, which logs it at Error: it is logged,
    /// with its number, and without the value the database quoted. Two shapes that quote one: a duplicate key
    /// (2627) and a truncation (2628).
    /// </summary>
    [Theory]
    [InlineData("duplicate", 2627)]
    [InlineData("truncation", 2628)]
    public async Task An_unhandled_database_error_is_logged_without_the_value_it_quotes(string shape, int number)
    {
        var secret = $"leak-{Guid.NewGuid():N}@example.com";

        // What the database will print of it: all of it for a duplicate, the first 20 characters for a truncation.
        var printed = shape == "duplicate" ? secret : secret[..20];
        var sabotage = shape == "duplicate"
            ? $"CREATE TABLE #leak (v nvarchar(200) NOT NULL PRIMARY KEY); INSERT INTO #leak (v) VALUES (N'{secret}'), (N'{secret}');\n"
            : $"DECLARE @leak TABLE (v nvarchar(20) NOT NULL); INSERT INTO @leak (v) VALUES (N'{secret}');\n";

        var variantId = await CheckoutVariantIdAsync();
        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);
        await SetStockAsync(variantId, 10);

        var (_, _, api) = await NewBagAsync(variantId, quantity: 1);
        var (_, session) = await api.GetAsync(colombo, standard, Token);

        _interceptor.SabotageNext(OrdersInsert, sabotage);

        var (response, _) = await api.PlaceAsync(ValidBody(session!.CheckoutKey, standard, colombo, QuotedTotal(session)), Token);
        var answer = await response.Content.ReadAsStringAsync(Token);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(printed, answer, StringComparison.OrdinalIgnoreCase);

        // The database said it, so there was something to leak.
        Assert.Contains(_interceptor.Failures, failure => failure.Contains(printed, StringComparison.OrdinalIgnoreCase));

        await _sink.SettleAsync(Token);

        // The exception handler wrote the failure, with the error number, so an operator still sees what happened.
        Assert.Contains(_sink.Events, logged =>
            logged.Source == "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware"
            && logged.Level >= LogEventLevel.Error
            && logged.Exception.Contains($"SQL Server error {number}", StringComparison.Ordinal));

        AssertNoEventHolds([printed], $"unhandled {shape}");
    }

    private static IEnumerable<string> KeySpellings(Guid key) => [key.ToString("D"), key.ToString("N")];

    private void AssertNoEventHolds(IReadOnlyCollection<string> secrets, string scenario)
    {
        var leaks = _sink.Events
            .Where(logged => secrets.Any(secret => logged.Text.Contains(secret, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.True(
            leaks.Count == 0,
            $"{scenario}: {leaks.Count} of {_sink.Events.Count} events hold a value the database quoted:\n"
            + string.Join("\n", leaks.Take(5).Select(leak => leak.ToString() + "\n    " + leak.Exception.Split('\n')[0])));
    }

    /// <summary>
    /// A handled race is not an error: no EF Core event at Information or above carries the failure (EF Core
    /// writes <c>SaveChangesFailed</c> / <c>CommandError</c> at Debug, <c>AppDbContext.DatabaseErrorEvents</c>).
    /// </summary>
    private void AssertTheHandledRaceIsNotAnError()
    {
        var loud = _sink.Events
            .Where(logged => logged.Source.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                && logged.Level >= LogEventLevel.Information
                && logged.Exception.Length > 0)
            .ToList();

        Assert.True(
            loud.Count == 0,
            "EF Core reported a race the store handles as a failure: " + string.Join("\n", loud.Take(3)));
    }

    /// <summary>Every event the host logged, as the console would print it.</summary>
    private sealed class CaptureSink : ILogEventSink
    {
        private readonly ConcurrentQueue<CapturedLogEvent> _events = new();

        public IReadOnlyList<CapturedLogEvent> Events => [.. _events];

        public void Emit(LogEvent logEvent) => _events.Enqueue(CapturedLogEvent.From(logEvent));

        /// <summary>The request-completion line is written after the response; give the last ones a moment.</summary>
        public async Task SettleAsync(CancellationToken cancellationToken)
        {
            var count = -1;

            for (var attempt = 0; attempt < 20 && count != _events.Count; attempt++)
            {
                count = _events.Count;
                await Task.Delay(150, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Holds, or rewrites, the next command that starts a given INSERT, and records every SQL error a command
    /// failed with (its full text: what the database said, which is what the log must not repeat).
    /// </summary>
    private sealed class SqlFailureInterceptor : DbCommandInterceptor, ISaveChangesInterceptor
    {
        private readonly ConcurrentQueue<string> _failures = new();
        private readonly ConcurrentQueue<int> _numbers = new();
        private string? _holdFor;
        private TimeSpan _hold;
        private string? _sabotageFor;
        private string? _sabotage;

        public IReadOnlyList<string> Failures => [.. _failures];

        public void HoldNext(string commandStart, TimeSpan hold)
        {
            _hold = hold;
            Volatile.Write(ref _holdFor, commandStart);
        }

        public void SabotageNext(string commandStart, string prefix)
        {
            _sabotage = prefix;
            Volatile.Write(ref _sabotageFor, commandStart);
        }

        public bool SawDuplicateOn(string index) =>
            _failures.Any(failure => failure.Contains(index, StringComparison.Ordinal)
                && failure.Contains("duplicate key", StringComparison.OrdinalIgnoreCase));

        private readonly ConcurrentQueue<string> _trace = new();

        public string Describe() =>
            (_failures.IsEmpty ? "(none)" : string.Join(" | ", _failures))
            + " trace: " + string.Join(" ; ", _trace);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await BeforeAsync(command, cancellationToken);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await BeforeAsync(command, cancellationToken);
            return result;
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Record(eventData.Exception);
            return Task.CompletedTask;
        }

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Record(eventData.Exception);

        // A batch's error can arrive while its results are read, after the command "succeeded", and EF Core
        // then reports it only as a failed save: so both are watched.
        public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Record(eventData.Exception);
            return Task.CompletedTask;
        }

        public void SaveChangesFailed(DbContextErrorEventData eventData) => Record(eventData.Exception);

        private async Task BeforeAsync(DbCommand command, CancellationToken cancellationToken)
        {
            if (command.CommandText.Contains("[Orders]", StringComparison.Ordinal) || command.CommandText.Contains("[Users]", StringComparison.Ordinal))
            {
                var first = command.CommandText.ReplaceLineEndings(" ");
                _trace.Enqueue(DateTime.UtcNow.ToString("ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + first[..Math.Min(90, first.Length)]);
            }

            if (_sabotageFor is { } sabotageFor
                && command.CommandText.Contains(sabotageFor, StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _sabotageFor, null, sabotageFor) == sabotageFor)
            {
                command.CommandText = _sabotage + command.CommandText;
            }

            if (_holdFor is { } holdFor
                && command.CommandText.Contains(holdFor, StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _holdFor, null, holdFor) == holdFor)
            {
                await Task.Delay(_hold, cancellationToken);
            }
        }

        private void Record(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is SqlException sql)
                {
                    _failures.Enqueue(sql.Message);
                    _numbers.Enqueue(sql.Number);
                }
            }
        }
    }
}
