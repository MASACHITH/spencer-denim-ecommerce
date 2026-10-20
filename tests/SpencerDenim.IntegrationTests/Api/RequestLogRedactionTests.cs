using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api;

/// <summary>
/// Issue <b>#201</b> (follow-up to #179): the API's own log must not hold a credential.
/// <para>
/// <b>What leaked.</b> <c>UseSerilogRequestLogging</c> writes <c>RequestPath</c> on every request, and a guest's
/// order token is a <i>path segment</i> (<c>/api/orders/{token}</c>, the payment-slip and PayPal routes under it,
/// <c>/api/admin/orders/…</c>). Worse, ASP.NET Core's hosting layer opens a logging scope that carries the same
/// raw <c>RequestPath</c>, and the Serilog bridge copies a scope's properties onto <b>every</b> event written
/// while the request runs — so an EF Core warning, an unhandled-exception error and a throttle warning each
/// carried the token too, and the JSON console formatter (the deployed format) writes all of them.
/// </para>
/// <para>
/// <b>How these tests look.</b> They host the real API with the real <c>appsettings.json</c> levels
/// (<c>everyLevel: false</c>, what the deployed console would print) and again with every logger at Verbose
/// (<c>everyLevel: true</c>, what it would print if a deployment lowered a level in its environment). They drive
/// <i>every route that takes a GUID segment</i>, read from the endpoint table rather than from a list somebody
/// had to remember to extend, with a fake token in the path <b>and</b> in the query string, on the success, 404,
/// 400, 401/403 and 500 paths; then they search <b>everything</b> the host logged — rendered message, every
/// property (the hosting scope included) and the exception text — for the token, in the spellings a request can
/// carry it in, and for the visitor's address.
/// </para>
/// </summary>
public sealed class RequestLogRedactionTests(SqlServerDatabaseFixture sqlServer)
{
    /// <summary>A fake order token: the shape of a real one, belonging to no order.</summary>
    private const string OrderToken = "7d1e5c0a-4b3f-4a8e-9c2d-1f6b8a3e5d90";

    private const string QuerySecret = "qs-Zk93TmXp41";
    private const string PayerSecret = "PAYERID8H2KQ";
    private const string EmailSecret = "jane.secret@example.com";
    private const string VisitorAddress = "203.0.113.9";
    private const string ForwardedAddress = "198.51.100.77";

    /// <summary>The query string every probing request carries: a credential per way the shop passes one.</summary>
    private static readonly string Query =
        $"?token={QuerySecret}&PayerID={PayerSecret}&email={Uri.EscapeDataString(EmailSecret)}&returnTo=%2Forder%2F{OrderToken}";

    /// <summary>
    /// Everything that must never appear in any log event. The token in the spellings it can arrive in: with and
    /// without hyphens, and (compared case-insensitively) in either case. The percent-encoded form of the query
    /// values is covered by the decoded one, because the pipeline decodes before it logs.
    /// </summary>
    private static readonly string[] Secrets =
    [
        OrderToken,
        OrderToken.Replace("-", string.Empty, StringComparison.Ordinal),
        OrderToken[..8],
        QuerySecret,
        PayerSecret,
        EmailSecret,
        Uri.EscapeDataString(EmailSecret),
        VisitorAddress,
        ForwardedAddress,
    ];

    /// <summary>
    /// The credential-bearing route families and what a log may say instead, longest prefix first. The same table
    /// as the gateway's <c>$sd_log_path</c> map (deploy/gateway/nginx.conf) and
    /// <c>RequestPathRedaction.CredentialRoutes</c>; <see cref="Every_route_with_a_guid_segment_is_a_known_credential_family"/>
    /// fails when a route with a GUID segment is added that is in none of them.
    /// </summary>
    private static readonly (string Prefix, string Redacted)[] Families =
    [
        ("/api/payments/paypal/start/", "/api/payments/paypal/start/*"),
        ("/api/payments/paypal/return/", "/api/payments/paypal/return/*"),
        ("/api/payments/paypal/cancel/", "/api/payments/paypal/cancel/*"),
        ("/api/admin/orders/bank-transfers/", "/api/admin/orders/bank-transfers/*"),
        ("/api/admin/orders/", "/api/admin/orders/*"),
        ("/api/orders/", "/api/orders/*"),

        // Not a credential (a public upload's id), but a GUID: the shape rule stars it.
        ("/api/admin/media/", "/api/admin/media/*"),
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private async Task<LogProbe> StartAsync(bool everyLevel)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return new LogProbe(sqlServer.ConnectionString, everyLevel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_route_with_a_guid_segment_is_logged_without_its_token(bool everyLevel)
    {
        await using var probe = await StartAsync(everyLevel);
        var routes = GuidRoutes(probe.Services);

        // The enumeration looked at the table: the routes the issue names are in it.
        Assert.Contains(routes, route => route.Template == "/api/orders/{publicId:guid}");
        Assert.Contains(routes, route => route.Template == "/api/orders/{publicId:guid}/payment-slip");
        Assert.Contains(routes, route => route.Template == "/api/payments/paypal/start/{publicId:guid}");
        Assert.Contains(routes, route => route.Template == "/api/payments/paypal/return/{publicId:guid}");
        Assert.Contains(routes, route => route.Template == "/api/admin/orders/{orderPublicId:guid}");
        Assert.Contains(routes, route => route.Template == "/api/admin/orders/bank-transfers/{orderPublicId:guid}/approve");
        Assert.True(routes.Count >= 12, $"Only {routes.Count} (method, route) pairs have a GUID segment.");

        foreach (var route in routes)
        {
            var completion = await probe.SendAsync(route.Method, route.Url + Query, Token);

            Assert.True(
                completion is not null,
                $"{route.Method} {route.Template} wrote no request-completion event, so there is nothing to check.");
            Assert.Equal(ExpectedLogPath(route.Template), completion.Property("RequestPath"));
            Assert.Equal(route.Method.ToUpperInvariant(), completion.Property("RequestMethod"));
        }

        probe.AssertNoSecretInAnyEvent($"{routes.Count} credential routes, everyLevel={everyLevel}");
    }

    [Fact]
    public async Task Every_route_with_a_guid_segment_is_a_known_credential_family()
    {
        await using var probe = await StartAsync(everyLevel: false);

        var unknown = GuidRoutes(probe.Services)
            .Where(route => !Families.Any(family => route.Template.StartsWith(family.Prefix, StringComparison.Ordinal)))
            .Select(route => route.Method + " " + route.Template)
            .Distinct()
            .ToList();

        Assert.True(
            unknown.Count == 0,
            "A route with a GUID segment is in no credential family, so nobody has said whether its segment is "
            + "a secret. Add it to RequestPathRedaction.CredentialRoutes and to the gateway's $sd_log_path map, "
            + "and to Families in this test: " + string.Join("; ", unknown));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_failure_paths_log_no_credential(bool everyLevel)
    {
        await using var probe = await StartAsync(everyLevel);

        // 404: a route that does not exist, with the token in the position a real one has it, and the real
        // route with a segment after it that does not exist.
        await probe.SendAsync("GET", $"/api/orders/{OrderToken}/no-such-action{Query}", Token);
        await probe.SendAsync("GET", $"/api/nothing/{OrderToken}{Query}", Token);
        await probe.SendAsync("GET", $"/{OrderToken}{Query}", Token);
        await probe.SendAsync("GET", $"/order/{OrderToken}{Query}", Token);
        await probe.SendAsync("GET", $"/api/orders/{OrderToken}-not-a-guid{Query}", Token);

        // 404 from a handler that does exist and finds no such order.
        await probe.SendAsync("GET", $"/api/orders/{OrderToken}{Query}", Token);

        // 400: the body cannot be read, and a value that does not bind.
        await probe.SendAsync("PUT", $"/api/orders/{OrderToken}/payment-reference{Query}", Token, "{bad");
        await probe.SendAsync("POST", $"/api/orders/{OrderToken}/cancel{Query}", Token, "{\"email\":123}");
        await probe.SendAsync("GET", $"/api/catalog/products?minPrice={QuerySecret}&sort={PayerSecret}", Token);

        // 405 and 415.
        await probe.SendAsync("DELETE", $"/api/orders/{OrderToken}{Query}", Token);
        await probe.SendAsync("POST", $"/api/orders/{OrderToken}/payment-slip{Query}", Token, "not multipart");

        // 401 and 403: staff routes without a session, with and without the antiforgery header.
        await probe.SendAsync("GET", $"/api/admin/orders/{OrderToken}{Query}", Token);
        await probe.SendAsync("GET", $"/api/admin/orders/{OrderToken}/invoice{Query}", Token);
        await probe.SendAsync("POST", $"/api/admin/orders/bank-transfers/{OrderToken}/approve{Query}", Token, "{}");
        await probe.SendAsync("GET", $"/api/admin/orders/bank-transfers/{OrderToken}/slip{Query}", Token);

        // The credentials that travel in the query: the account tokens and the PayPal return.
        await probe.SendAsync("POST", $"/api/account/reset-password{Query}", Token, "{}");
        await probe.SendAsync("POST", $"/api/account/confirm-email{Query}", Token, "{}");
        await probe.SendAsync("GET", $"/api/payments/paypal/return/{OrderToken}{Query}", Token);

        // A beacon that carries the order address in its body, as the storefront's does.
        await probe.SendAsync(
            "POST", "/api/traffic/page-views" + Query, Token, $"{{\"path\":\"/order/{OrderToken}?token={QuerySecret}\"}}");

        probe.AssertNoSecretInAnyEvent($"failure paths, everyLevel={everyLevel}");
    }

    /// <summary>
    /// The exception path, which is the one that logs the most: the request fails inside a handler (the database
    /// is unreachable), the exception handler logs the exception at Error, and every event on the way out carries
    /// the hosting scope's properties.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_request_that_throws_logs_no_credential(bool everyLevel)
    {
        await using var probe = new LogProbe(ApiFactory.UnreachableConnectionString, everyLevel);

        foreach (var url in new[]
        {
            $"/api/orders/{OrderToken}{Query}",
            $"/api/orders/{OrderToken}/payment-slip{Query}",
            $"/api/payments/paypal/return/{OrderToken}{Query}",
            $"/api/admin/orders/{OrderToken}{Query}",
        })
        {
            await probe.SendAsync("GET", url, Token);
        }

        // The sweep looked at the failure: something was logged at Error with an exception behind it.
        Assert.Contains(probe.Events, logged => logged.Level >= LogEventLevel.Error && logged.Exception.Length > 0);

        probe.AssertNoSecretInAnyEvent($"requests that throw, everyLevel={everyLevel}");
    }

    /// <summary>
    /// Spellings. A request can carry the same token in more shapes than the route template shows, and the log
    /// must say the redacted form for every one that reaches the pipeline (and none of them the token).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Other_spellings_of_the_token_are_logged_without_it(bool everyLevel)
    {
        await using var probe = await StartAsync(everyLevel);
        var upper = OrderToken.ToUpperInvariant();
        var plain = OrderToken.Replace("-", string.Empty, StringComparison.Ordinal);

        string[] targets =
        [
            $"/api/orders/{upper}",
            $"/API/ORDERS/{upper}",
            $"/api/orders/{OrderToken}/",
            $"/api/orders//{OrderToken}",
            $"//api/orders/{OrderToken}",
            $"/api/orders/{plain}",
            $"/api/orders/{OrderToken};x=1",
            $"/api/orders;x=1/{OrderToken}",
            $"/api/orders/%37{OrderToken[1..]}",
            $"/api/orders%2F{OrderToken}",
            $"/api/orders/{OrderToken}%2Fcancel",
            $"/api/orders/{OrderToken}%2e%2e/{OrderToken}",
            $"/api/orders/{new string('a', 400)}{OrderToken}",
            $"/api/payments/paypal/start/{upper}",
            $"/api/admin/orders/{upper}/invoice",
            $"/api/admin/orders/bank-transfers/{upper}/reject",
            $"/order/{OrderToken}",
            $"/order;x=1/{OrderToken}",
            $"/something/else/{OrderToken}/more",
            $"/api/orders/{OrderToken}?{Uri.EscapeDataString(OrderToken)}=1",
        ];

        foreach (var target in targets)
        {
            await probe.SendAsync("GET", target + (target.Contains('?', StringComparison.Ordinal) ? string.Empty : Query), Token);
        }

        probe.AssertNoSecretInAnyEvent($"{targets.Length} spellings, everyLevel={everyLevel}");
    }

    /// <summary>
    /// Doc 08 / issue #201: "No visitor IP is written by either (check, not assumed)." The request is addressed
    /// from a public address (and carries a forwarded one), the host records the address it saw so the check is
    /// known to have had something to find, and neither appears in any event — the requests logged, the
    /// throttle and the exception paths included.
    /// </summary>
    [Fact]
    public async Task No_visitor_address_is_logged_by_any_event_at_any_level()
    {
        await using var probe = await StartAsync(everyLevel: true);

        foreach (var (method, url) in new[]
        {
            ("GET", "/api/catalog/departments"),
            ("GET", $"/api/orders/{OrderToken}"),
            ("POST", "/api/content/newsletter"),
            ("POST", "/api/account/login"),
            ("POST", "/api/admin/auth/login"),
            ("POST", "/api/traffic/page-views"),
            ("POST", "/api/content/contact"),
        })
        {
            await probe.SendAsync(method, url, Token, method == "GET" ? null : "{\"email\":\"a@example.com\",\"password\":\"x\",\"path\":\"/\"}");
        }

        Assert.True(probe.AddressesSeenByTheHost.All(address => address == VisitorAddress) && probe.AddressesSeenByTheHost.Count >= 7);
        probe.AssertNoSecretInAnyEvent("visitor address");
        Assert.DoesNotContain(probe.Events, logged => logged.Text.Contains("127.0.0.1", StringComparison.Ordinal));
    }

    /// <summary>
    /// The query string is not part of the logged path, and the database's parameter values are not logged:
    /// EF Core prints them only with <c>EnableSensitiveDataLogging</c>, which nothing turns on.
    /// </summary>
    [Fact]
    public async Task The_query_string_and_database_parameters_are_not_logged()
    {
        await using var probe = await StartAsync(everyLevel: true);

        await probe.SendAsync("GET", $"/api/catalog/search?q={QuerySecret}", Token);

        var completion = Assert.Single(probe.Events, logged => logged.IsRequestCompletion);
        Assert.Equal("/api/catalog/search", completion.Property("RequestPath"));
        Assert.DoesNotContain(probe.Events, logged => logged.Text.Contains(QuerySecret, StringComparison.OrdinalIgnoreCase));

        // The sweep looked at the database too: a command ran, at a level that was listening.
        Assert.Contains(
            probe.Events,
            logged => logged.Source.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    private static string ExpectedLogPath(string template)
    {
        foreach (var (prefix, redacted) in Families)
        {
            if (template.StartsWith(prefix, StringComparison.Ordinal))
            {
                return redacted;
            }
        }

        throw new InvalidOperationException($"{template} is in no credential family.");
    }

    /// <summary>
    /// Every (method, concrete address) the API answers whose route has a GUID segment, read from the routing
    /// table. The GUID segment is the order token (or a media id), so the test needs no list of its own to go
    /// stale: a route added tomorrow is in the table today.
    /// </summary>
    private static List<RouteProbe> GuidRoutes(IServiceProvider services)
    {
        var probes = new List<RouteProbe>();

        foreach (var endpoint in services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern;

            if (!pattern.Parameters.Any(parameter => parameter.ParameterPolicies.Any(policy =>
                    string.Equals(policy.Content, "guid", StringComparison.OrdinalIgnoreCase))))
            {
                continue;
            }

            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"];

            foreach (var method in methods)
            {
                probes.Add(new RouteProbe(method, pattern.RawText ?? string.Empty, ConcreteUrl(pattern)));
            }
        }

        return probes;
    }

    private static string ConcreteUrl(RoutePattern pattern)
    {
        var url = new StringBuilder();

        foreach (var segment in pattern.PathSegments)
        {
            url.Append('/');

            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        url.Append(literal.Content);
                        break;
                    case RoutePatternSeparatorPart separator:
                        url.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        url.Append(parameter.ParameterPolicies.Any(policy =>
                            string.Equals(policy.Content, "guid", StringComparison.OrdinalIgnoreCase))
                            ? OrderToken
                            : parameter.IsCatchAll ? "public/originals/x.webp" : "1");
                        break;
                }
            }
        }

        return url.ToString();
    }

    private sealed record RouteProbe(string Method, string Template, string Url);

    /// <summary>One event as the console would have written it: message, every property, the exception.</summary>
    internal sealed record CapturedLogEvent(
        LogEventLevel Level,
        string Source,
        string Message,
        IReadOnlyDictionary<string, string> Properties,
        string Exception)
    {
        public bool IsRequestCompletion =>
            Source.EndsWith("RequestLoggingMiddleware", StringComparison.Ordinal)
            && Message.StartsWith("HTTP ", StringComparison.Ordinal);

        /// <summary>Everything a log reader could read in this event.</summary>
        public string Text =>
            Message + "\n" + string.Join("\n", Properties.Select(pair => pair.Key + "=" + pair.Value)) + "\n" + Exception;

        public string? Property(string name) => Properties.GetValueOrDefault(name);

        public override string ToString()
        {
            var text = $"[{Level}] {Source}: {Message} | "
                + string.Join(", ", Properties.Where(pair => pair.Key is not "SourceContext").Select(pair => pair.Key + "=" + pair.Value));
            var firstLine = Exception.Length == 0 ? string.Empty : " | exception: " + Exception.Split('\n')[0];

            var line = text + firstLine;

            return line.Length > 600 ? line[..600] + "..." : line;
        }

        public static CapturedLogEvent From(LogEvent logEvent)
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (name, value) in logEvent.Properties)
            {
                properties[name] = value is ScalarValue { Value: string text }
                    ? text
                    : value.ToString(null, CultureInfo.InvariantCulture);
            }

            return new CapturedLogEvent(
                logEvent.Level,
                properties.GetValueOrDefault("SourceContext") ?? "(none)",
                logEvent.RenderMessage(CultureInfo.InvariantCulture),
                properties,
                logEvent.Exception?.ToString() ?? string.Empty);
        }
    }

    /// <summary>The real API in memory, a sink for everything it logs, and a visitor with a public address.</summary>
    private sealed class LogProbe : IAsyncDisposable
    {
        private readonly ApiFactory _factory;
        private readonly CaptureSink _sink = new();
        private readonly HttpClient _client;
        private readonly ConcurrentQueue<string> _addresses = new();

        public LogProbe(string connectionString, bool everyLevel)
        {
            var configuration = new Dictionary<string, string?>(StringComparer.Ordinal);

            if (everyLevel)
            {
                // What the deployed console would print if its levels were lowered by an environment variable.
                configuration["Serilog:MinimumLevel:Default"] = "Verbose";
                configuration["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Verbose";
                configuration["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Verbose";
                configuration["Serilog:MinimumLevel:Override:System.Net.Http.HttpClient"] = "Verbose";
                configuration["Serilog:MinimumLevel:Override:Microsoft.Extensions.Diagnostics.HealthChecks"] = "Verbose";
            }

            _factory = new ApiFactory(
                connectionString,
                "Production",
                configuration,
                configureServices: services =>
                {
                    services.AddSingleton<ILogEventSink>(_sink);
                    services.AddSingleton<IStartupFilter>(new VisitorAddressFilter(_addresses));
                });

            _client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false,
            });
        }

        public IServiceProvider Services => _factory.Services;

        public IReadOnlyList<CapturedLogEvent> Events => _sink.Events;

        public ConcurrentQueue<string> AddressesSeenByTheHost => _addresses;

        /// <summary>
        /// Sends one request and returns its request-completion event (null if none was written in time).
        /// The completion event is written after the response is on its way, so it is waited for.
        /// </summary>
        public async Task<CapturedLogEvent?> SendAsync(string method, string target, CancellationToken cancellationToken, string? body = null)
        {
            var before = _sink.Events.Count(logged => logged.IsRequestCompletion);

            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri("https://localhost" + target, UriKind.Absolute));
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", ForwardedAddress);

            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            try
            {
                using var response = await _client.SendAsync(request, cancellationToken);
                await response.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            catch (HttpRequestException)
            {
                // A request the client itself refuses to send is not one the server logged.
                return null;
            }
            catch (UriFormatException)
            {
                return null;
            }

            for (var attempt = 0; attempt < 100; attempt++)
            {
                var completions = _sink.Events.Where(logged => logged.IsRequestCompletion).ToList();

                if (completions.Count > before)
                {
                    return completions[before];
                }

                await Task.Delay(20, cancellationToken);
            }

            return null;
        }

        public void AssertNoSecretInAnyEvent(string what)
        {
            var events = _sink.Events;

            // The sweep looked at something.
            Assert.True(events.Any(logged => logged.IsRequestCompletion), $"{what}: no request was logged at all.");

            var leaks = events
                .Where(logged => Secrets.Any(secret => logged.Text.Contains(secret, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Assert.True(
                leaks.Count == 0,
                $"{what}: {leaks.Count} of {events.Count} log events carry a credential or an address.{Environment.NewLine}"
                + string.Join(Environment.NewLine, leaks.Take(12).Select(logged => "  " + logged)));
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
        }
    }

    private sealed class CaptureSink : ILogEventSink
    {
        private readonly ConcurrentQueue<CapturedLogEvent> _events = new();

        public IReadOnlyList<CapturedLogEvent> Events => [.. _events];

        public void Emit(LogEvent logEvent) => _events.Enqueue(CapturedLogEvent.From(logEvent));
    }

    /// <summary>Runs first in the pipeline and gives every request a public remote address.</summary>
    private sealed class VisitorAddressFilter(ConcurrentQueue<string> seen) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(VisitorAddress);
                    seen.Enqueue(context.Connection.RemoteIpAddress.ToString());
                    await nextMiddleware(context);
                });

                next(app);
            };
    }
}
