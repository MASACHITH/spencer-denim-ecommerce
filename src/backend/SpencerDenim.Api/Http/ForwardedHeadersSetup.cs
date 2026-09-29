using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SpencerDenim.Api.Http;

/// <summary>
/// The web gateway (W0-15, docs/15) terminates nothing itself: TLS ends at the host's nginx, which proxies to the
/// gateway container, which proxies to this API over plain HTTP on a private Docker network. Without this,
/// <c>HttpContext.Request.IsHttps</c> would read <c>false</c> for every request even when the real client connected
/// over HTTPS — which would make <c>CookieSecurePolicy.Always</c> (the admin auth and antiforgery cookies, doc 03
/// §3a) silently never set the auth cookie — and <c>HttpContext.Connection.RemoteIpAddress</c> would be the
/// gateway's address for every visitor, which is what the audit log (ADM-03), the newsletter consent record and
/// the newsletter rate limit (HOME-10) would otherwise all see.
/// </summary>
/// <remarks>
/// <para>
/// <b>Who is trusted</b> comes from configuration (<see cref="TrustedProxySettings"/>):
/// <c>ForwardedHeaders__KnownNetworks</c> / <c>ForwardedHeaders__KnownProxies</c>. The compose files set
/// <c>KnownNetworks</c> to the gateway's network, a fixed subnet that only the gateway and the API are attached to.
/// </para>
/// <para>
/// <b>Unconfigured is the safe state W1-01 round 3 left:</b> <c>X-Forwarded-For</c> is not processed at all, so a
/// forged header can never reach <c>ops.AuditLogs</c>, and <c>X-Forwarded-Proto</c> is accepted from loopback only.
/// Loopback is deliberately <i>not</i> trusted for <c>X-Forwarded-For</c> even then: "trust loopback" is ASP.NET
/// Core's own default and is exactly how a same-host caller forged an audit-log address in W1-01.
/// </para>
/// <para>
/// <b>Configured</b>, the listed networks/proxies are the only trusted peers (loopback is dropped), and both headers
/// are processed with <c>ForwardLimit = 1</c>: only the right-most <c>X-Forwarded-For</c> entry — the one the
/// trusted gateway itself wrote — is used, so anything a client prepends is ignored. The gateway overwrites the
/// header anyway rather than appending to it (deploy/gateway).
/// </para>
/// </remarks>
internal static class ForwardedHeadersSetup
{
    public static IServiceCollection AddApiForwardedHeaders(this IServiceCollection services)
    {
        // Bound lazily from IConfiguration (so environment variables and test factories are honoured) and
        // validated at start-up, so a malformed network stops the API rather than surfacing on the first request.
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var settings = new TrustedProxySettings();
                configuration.GetSection(TrustedProxySettings.SectionName).Bind(settings);
                Apply(options, settings);
            })
            .ValidateOnStart();

        return services;
    }

    /// <summary>Must run before anything that reads the request scheme or remote address.</summary>
    public static IApplicationBuilder UseApiForwardedHeaders(this IApplicationBuilder app) => app.UseForwardedHeaders();

    internal static void Apply(ForwardedHeadersOptions options, TrustedProxySettings settings)
    {
        var networks = TrustedProxyParser.ParseNetworks(settings.KnownNetworks);
        var proxies = TrustedProxyParser.ParseAddresses(settings.KnownProxies);

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        // No X-Forwarded-Host: the gateway passes the client's Host header through unchanged, and trusting the
        // header form would let any trusted hop rewrite it.
        if (networks.Count == 0 && proxies.Count == 0)
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Loopback, 32));
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.IPv6Loopback, 128));
            return;
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;

        foreach (var network in networks)
        {
            options.KnownIPNetworks.Add(network);
        }

        foreach (var proxy in proxies)
        {
            options.KnownProxies.Add(proxy);
        }
    }
}
