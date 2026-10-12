using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api;

/// <summary>
/// W0-15's launch blocker (carried from W1-01): <c>X-Forwarded-For</c> is honoured from the web gateway's network
/// and from nowhere else.
///
/// <para>
/// These run the API's real pipeline with the socket peer set explicitly, which a normal HTTP client cannot do,
/// and read what the rest of the pipeline sees through a probe appended after it (only reached by a path no
/// endpoint claims). That is the same <c>HttpContext.Connection.RemoteIpAddress</c> the audit log (ADM-03), the
/// newsletter consent record and the newsletter rate limit read. No SQL Server is needed.
/// </para>
/// </summary>
public sealed class ForwardedHeadersTests
{
    private const string ProbePath = "/__test/forwarded-headers-probe";

    /// <summary>The production compose file's gateway network, for readability. Any private /28 would do.</summary>
    private const string GatewayNetwork = "10.251.0.16/28";

    private static readonly IPAddress Gateway = IPAddress.Parse("10.251.0.18");
    private static readonly IPAddress OutsideTheGatewayNetwork = IPAddress.Parse("10.251.0.40");
    private const string Visitor = "203.0.113.9";
    private const string Forged = "198.51.100.66";

    [Fact]
    public async Task Configured_the_gateway_networks_forwarded_client_address_and_scheme_are_honoured()
    {
        await using var factory = CreateFactory(new() { ["ForwardedHeaders:KnownNetworks"] = GatewayNetwork });

        var seen = await ProbeAsync(factory, Gateway, forwardedFor: Visitor, forwardedProto: "https");

        Assert.Equal(Visitor, seen.RemoteIp);
        Assert.Equal("https", seen.Scheme);
    }

    [Fact]
    public async Task Configured_a_forged_header_from_a_peer_outside_the_gateway_network_is_ignored()
    {
        await using var factory = CreateFactory(new() { ["ForwardedHeaders:KnownNetworks"] = GatewayNetwork });

        var seen = await ProbeAsync(factory, OutsideTheGatewayNetwork, forwardedFor: Forged, forwardedProto: "https");

        Assert.Equal(OutsideTheGatewayNetwork.ToString(), seen.RemoteIp);
        Assert.Equal("http", seen.Scheme);
    }

    /// <summary>
    /// Configured, loopback is no longer trusted at all: inside the API container nothing legitimate calls it over
    /// loopback with forwarded headers, and "trust loopback" is how W1-01's forged audit-log address got in.
    /// </summary>
    [Fact]
    public async Task Configured_loopback_is_not_trusted_either()
    {
        await using var factory = CreateFactory(new() { ["ForwardedHeaders:KnownNetworks"] = GatewayNetwork });

        var seen = await ProbeAsync(factory, IPAddress.Loopback, forwardedFor: Forged, forwardedProto: "https");

        Assert.Equal(IPAddress.Loopback.ToString(), seen.RemoteIp);
        Assert.Equal("http", seen.Scheme);
    }

    /// <summary>
    /// Only the right-most entry — the one the trusted gateway wrote — counts. Anything a client put in front of
    /// it (its own forged value, which a proxy that appends would keep) is not the address.
    /// </summary>
    [Fact]
    public async Task Configured_only_the_entry_the_gateway_wrote_is_used()
    {
        await using var factory = CreateFactory(new() { ["ForwardedHeaders:KnownNetworks"] = GatewayNetwork });

        var seen = await ProbeAsync(factory, Gateway, forwardedFor: $"{Forged}, {Visitor}", forwardedProto: "https");

        Assert.Equal(Visitor, seen.RemoteIp);
    }

    /// <summary>Kestrel listens dual-stack in the container, so the gateway's IPv4 address may arrive mapped.</summary>
    [Fact]
    public async Task Configured_the_gateway_is_recognised_when_its_address_arrives_ipv4_mapped()
    {
        await using var factory = CreateFactory(new() { ["ForwardedHeaders:KnownNetworks"] = GatewayNetwork });

        var seen = await ProbeAsync(factory, Gateway.MapToIPv6(), forwardedFor: Visitor, forwardedProto: "https");

        Assert.Equal(Visitor, seen.RemoteIp);
    }

    [Fact]
    public async Task Configured_a_single_known_proxy_is_trusted_and_its_neighbours_are_not()
    {
        await using var factory = CreateFactory(new() { ["ForwardedHeaders:KnownProxies"] = Gateway.ToString() });

        var trusted = await ProbeAsync(factory, Gateway, forwardedFor: Visitor, forwardedProto: "https");
        var neighbour = await ProbeAsync(factory, IPAddress.Parse("10.251.0.19"), forwardedFor: Forged, forwardedProto: "https");

        Assert.Equal(Visitor, trusted.RemoteIp);
        Assert.Equal("10.251.0.19", neighbour.RemoteIp);
    }

    /// <summary>
    /// Unconfigured is W1-01 round 3's safe state, unchanged: X-Forwarded-For is ignored from everybody —
    /// including loopback, which is how every developer machine, CI and test reaches the API — while
    /// X-Forwarded-Proto is still taken from loopback.
    /// </summary>
    [Fact]
    public async Task Unconfigured_a_forged_header_from_loopback_is_ignored_but_the_scheme_is_taken()
    {
        await using var factory = CreateFactory([]);

        var seen = await ProbeAsync(factory, IPAddress.Loopback, forwardedFor: Forged, forwardedProto: "https");

        Assert.Equal(IPAddress.Loopback.ToString(), seen.RemoteIp);
        Assert.Equal("https", seen.Scheme);
    }

    [Fact]
    public async Task Unconfigured_a_would_be_gateway_is_not_trusted_for_anything()
    {
        await using var factory = CreateFactory([]);

        var seen = await ProbeAsync(factory, Gateway, forwardedFor: Forged, forwardedProto: "https");

        Assert.Equal(Gateway.ToString(), seen.RemoteIp);
        Assert.Equal("http", seen.Scheme);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownNetworks", "10.251.0.16")]
    [InlineData("ForwardedHeaders:KnownNetworks", "0.0.0.0/0")]
    [InlineData("ForwardedHeaders:KnownProxies", "gateway")]
    public async Task A_malformed_setting_stops_the_api_from_starting(string key, string value)
    {
        await using var factory = CreateFactory(new() { [key] = value });

        // Building the host is what fails, so ask for exactly that. Going through a request instead let a
        // background service that touched the already-disposed provider during the failed start win the race
        // and surface an ObjectDisposedException in place of the validation error.
        var exception = Assert.ThrowsAny<Exception>(() => factory.Services);

        Assert.Contains(key, Flatten(exception), StringComparison.Ordinal);
    }

    /// <summary>The real API host (no database needed) with a terminal probe after its whole pipeline.</summary>
    private static ApiFactory CreateFactory(Dictionary<string, string?> configuration) =>
        new(
            ApiFactory.UnreachableConnectionString,
            additionalConfiguration: configuration,
            configureServices: services => services.AddTransient<IStartupFilter, ProbeStartupFilter>());

    private static async Task<(string? RemoteIp, string Scheme)> ProbeAsync(
        ApiFactory factory,
        IPAddress peer,
        string forwardedFor,
        string forwardedProto)
    {
        var context = await factory.Server.SendAsync(
            http =>
            {
                http.Connection.RemoteIpAddress = peer;
                http.Request.Method = HttpMethods.Get;
                http.Request.Scheme = "http";
                http.Request.Path = ProbePath;
                http.Request.Headers["X-Forwarded-For"] = forwardedFor;
                http.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        var remote = context.Response.Headers["X-Probe-Remote-Ip"].ToString();
        var scheme = context.Response.Headers["X-Probe-Scheme"].ToString();
        return (remote, scheme);
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(inner => inner.Message));
            }
        }

        return string.Join(" | ", messages);
    }

    private sealed class ProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);

            // The API's own pipeline passes a request no endpoint matched on to whatever follows it.
            app.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.Headers["X-Probe-Remote-Ip"] = context.Connection.RemoteIpAddress?.ToString();
                context.Response.Headers["X-Probe-Scheme"] = context.Request.Scheme;
                return Task.CompletedTask;
            });
        };
    }
}
