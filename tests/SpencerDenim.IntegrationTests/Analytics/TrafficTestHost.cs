using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using SpencerDenim.Domain.Analytics;
using SpencerDenim.Infrastructure.Analytics;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Analytics;

/// <summary>
/// An API host for the traffic tests, with the page-view writer under the test's control.
/// <para>
/// <b>The writer's interval is five minutes here unless a test asks otherwise</b>, so nothing is written in
/// the background while a test is looking: every test drains it with <see cref="FlushAsync"/>, which returns
/// only once the rows are committed. That is the deterministic drain the brief asks for (the dashboard tests'
/// background write broke <c>OrderExpiryTests</c>); and every test owns its rows by <c>SessionId</c>, so a
/// view another test left queued cannot be counted here.
/// </para>
/// </summary>
internal sealed class TrafficTestHost : IAsyncDisposable
{
    private TrafficTestHost(ApiFactory factory, CapturingSink logs)
    {
        Factory = factory;
        Logs = logs;
    }

    public ApiFactory Factory { get; }

    /// <summary>Every log event the host wrote, at every level.</summary>
    public CapturingSink Logs { get; }

    public PageViewWriterService Writer => Factory.Services.GetRequiredService<PageViewWriterService>();

    public PageViewQueue Queue => Factory.Services.GetRequiredService<PageViewQueue>();

    public static TrafficTestHost Create(string connectionString, IReadOnlyDictionary<string, string?>? configure = null)
    {
        var configuration = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Traffic:FlushSeconds"] = "300",
            ["Traffic:BatchSize"] = "1000",

            // Everything, so a test can say what was never logged at any level.
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
            connectionString,
            additionalConfiguration: configuration,
            configureServices: services => services.AddSingleton<ILogEventSink>(logs));

        return new TrafficTestHost(factory, logs);
    }

    /// <summary>A client with its own cookie jar, addressed as https://localhost like every other test host.</summary>
    public HttpClient CreateClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, object body, CancellationToken cancellationToken) =>
        client.PostAsJsonAsync(new Uri("/api/traffic/page-views", UriKind.Relative), body, cancellationToken);

    /// <summary>Writes everything queued and waits for it to commit.</summary>
    public Task<int> FlushAsync(CancellationToken cancellationToken) => Writer.FlushAsync(cancellationToken);

    public ValueTask DisposeAsync() => Factory.DisposeAsync();

    /// <summary>The session the response's <c>sd_vs</c> cookie carries.</summary>
    public static Guid SessionFrom(HttpResponseMessage response)
    {
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("sd_vs=", StringComparison.Ordinal));
        var value = cookie["sd_vs=".Length..cookie.IndexOf(';', StringComparison.Ordinal)];

        return Guid.ParseExact(value, "D");
    }

    /// <summary>The rows a session wrote, oldest first.</summary>
    public static async Task<List<PageView>> RowsAsync(SqlServerDatabaseFixture sqlServer, Guid session, CancellationToken cancellationToken)
    {
        using var scope = sqlServer.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await db.PageViews
            .AsNoTracking()
            .Where(view => view.SessionId == session)
            .OrderBy(view => view.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Every string-typed column of every row of every analytics table, as text: what a sweep for a secret
    /// has to search, so a column added later is searched too without anyone remembering to add it here.
    /// </summary>
    public static async Task<List<string>> EveryAnalyticsStringAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var columns = new List<(string Table, string Column)>();

        await using (var list = connection.CreateCommand())
        {
            list.CommandText = """
                SELECT t.name, c.name
                FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                WHERE SCHEMA_NAME(t.schema_id) = 'analytics' AND ty.name IN ('nvarchar', 'varchar', 'nchar', 'char')
                """;

            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var values = new List<string>();

        foreach (var (table, column) in columns)
        {
            await using var select = connection.CreateCommand();
            select.CommandText = $"SELECT [{column}] FROM [analytics].[{table}] WHERE [{column}] IS NOT NULL";

            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                values.Add(reader.GetString(0));
            }
        }

        return values;
    }
}

/// <summary>A Serilog sink that keeps every event, rendered with all its properties, for a privacy sweep.</summary>
internal sealed class CapturingSink : ILogEventSink
{
    private readonly List<string> _events = [];

    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);

        var text = logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture)
            + " | " + string.Join(" | ", logEvent.Properties.Select(property => $"{property.Key}={property.Value}"))
            + " | " + logEvent.Exception;

        lock (_events)
        {
            _events.Add(text);
        }
    }
}
