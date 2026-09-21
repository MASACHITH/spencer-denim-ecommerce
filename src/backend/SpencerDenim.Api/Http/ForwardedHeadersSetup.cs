using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace SpencerDenim.Api.Http;

/// <summary>
/// Doc 03 §4: Caddy is meant to terminate TLS at the edge and proxy to this API container over plain HTTP.
/// Without this, <c>HttpContext.Request.IsHttps</c> would read <c>false</c> for every request even when the
/// real client connected over HTTPS — which would make <c>CookieSecurePolicy.Always</c> (the admin auth cookie
/// and the antiforgery cookie, doc 03 §3a) silently never set the auth cookie, and make the antiforgery
/// middleware throw outright.
/// </summary>
internal static class ForwardedHeadersSetup
{
    public static IServiceCollection AddApiForwardedHeaders(this IServiceCollection services)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            // FOLLOW-UP (needs a TASKS.md entry, W0-15): the Docker/Caddy topology doc 03 §4 describes does not
            // exist yet — no compose file, no Dockerfile, no Caddyfile — so the API's port is NOT currently
            // shielded behind a firewall that only lets Caddy reach it.
            //
            // X-Forwarded-For is deliberately NOT in the enabled set below. Nothing in this slice needs the
            // client IP corrected today (there is no real proxy anywhere in front of this API yet), and trusting
            // that header — even only from a "known" peer — was proven concretely exploitable: a request whose
            // TCP connection genuinely is loopback (exactly how `dotnet run`, CI and every test in this repo
            // reach the API) can still forge X-Forwarded-For, because "trust loopback" is precisely ASP.NET
            // Core's own framework default for this middleware, not a restriction against a same-host caller.
            // That let an unauthenticated caller plant an arbitrary IpAddress in ops.AuditLogs (ADM-03) via the
            // login endpoint. Leaving XForwardedFor out means the middleware ignores that header outright, no
            // matter who sends it, so HttpContext.Connection.RemoteIpAddress always reflects the real socket
            // peer. Re-enable it (and only then) once W0-15 stands up real Caddy networking and there is an
            // actual, non-loopback proxy subnet to pin KnownIPNetworks to — don't guess that subnet now.
            //
            // X-Forwarded-Proto stays enabled so HttpContext.Request.IsHttps is corrected once Caddy exists;
            // it only affects which scheme a request is treated as, never anything written to the audit trail.
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;

            options.KnownIPNetworks.Clear();
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Loopback, 32));
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.IPv6Loopback, 128));
            options.KnownProxies.Clear();
        });

        return services;
    }

    /// <summary>Must run before anything that reads the request scheme (cookie auth, antiforgery, HTTPS redirection).</summary>
    public static IApplicationBuilder UseApiForwardedHeaders(this IApplicationBuilder app) => app.UseForwardedHeaders();
}
