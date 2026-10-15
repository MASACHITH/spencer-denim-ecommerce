using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Analytics;
using SpencerDenim.Infrastructure.Analytics;

namespace SpencerDenim.Api.Endpoints.Analytics;

/// <summary>
/// The page-view recorder's one public route (doc 08 §3.8; ADM-06, ADM-20).
/// <para>
/// <b>Who calls it.</b> Exactly one of two callers per page view, never both (see <c>docs/journal</c>
/// 2026-10-15-w403 for why):
/// </para>
/// <list type="bullet">
/// <item><description>the storefront's <b>server</b>, after it has rendered a page with a 200 — the first page of
/// every visit, and every page a crawler or a visitor without JavaScript sees;</description></item>
/// <item><description>the storefront in the <b>browser</b>, for each page reached by clicking (a client-side
/// navigation that never reaches the server), and for a first page the server did not render.</description></item>
/// </list>
/// <para>
/// <b>It cannot slow or break a page.</b> It classifies in memory and hands the row to a bounded queue with a
/// call that never waits; the database is written later, in batches, by another thread. It answers
/// <c>204</c> whether the view was queued, ignored (not a storefront path) or dropped (queue full, recording
/// off), so nothing about the answer is worth retrying and nothing in it tells a caller what was kept.
/// </para>
/// <para>
/// <b>No IP address is read here</b> — not <c>RemoteIpAddress</c>, not <c>X-Forwarded-For</c> — so none can
/// reach the table, a log line or an exception by accident. The request log line for this route is
/// <c>Debug</c> (see <c>SerilogSetup</c>), and carries the route, never the page address in the body.
/// </para>
/// </summary>
internal static class TrafficEndpoints
{
    public const string PageViewsRoute = "/api/traffic/page-views";

    public static IEndpointRouteBuilder MapTrafficEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(PageViewsRoute, Record)
            .AllowAnonymous()
            .WithTags("Traffic")
            .WithName("RecordPageView")
            .WithSummary("Record one storefront page view (fire and forget).")
            .WithDescription(
                "Body: { path, referrer?, automated?, userAgent? }. `path` is the address the shopper was on; "
                + "its query string is dropped except the listing's filter keys, and secret-bearing segments "
                + "(the order page's token) are never stored. `userAgent` is set only by the storefront's "
                + "server, which records server-rendered pages on the visitor's behalf; a browser's own user "
                + "agent header is used otherwise. Always 204. No IP address is read or stored.")
            .Produces(StatusCodes.Status204NoContent);

        return endpoints;
    }

    private static Microsoft.AspNetCore.Http.HttpResults.NoContent Record(
        HttpContext httpContext,
        PageViewBeacon beacon,
        IPageViewRecorder recorder,
        TrafficOptions options,
        IClock clock)
    {
        httpContext.Response.Headers.CacheControl = "no-store";

        var session = VisitSessionCookie.ReadOrIssue(httpContext);

        // The host this beacon was sent to is the shop's own, so a referrer on it is a click inside the shop.
        var internalHosts = new HashSet<string>(options.InternalHostSet(), StringComparer.Ordinal);
        if (httpContext.Request.Host.HasValue)
        {
            internalHosts.Add(httpContext.Request.Host.Host.ToLowerInvariant());
        }

        var userAgent = beacon.UserAgent ?? httpContext.Request.Headers.UserAgent.ToString();

        var view = PageViewClassifier.Classify(
            new PageViewSubmission(
                beacon.Path,
                beacon.Referrer,
                userAgent,
                beacon.Automated ?? false,
                session,
                clock.UtcNow.UtcDateTime),
            internalHosts);

        if (view is not null)
        {
            recorder.TryRecord(view);
        }

        return TypedResults.NoContent();
    }
}

/// <summary>The recording beacon's body.</summary>
/// <param name="Path">The address the shopper was on: path, query and fragment.</param>
/// <param name="Referrer">The referring URL, or null. Only its host is kept.</param>
/// <param name="Automated">The browser reported <c>navigator.webdriver</c>.</param>
/// <param name="UserAgent">
/// Set only by the storefront's server, which records server-rendered pages on the visitor's behalf and so
/// cannot send the visitor's user agent as its own header. An empty string means the visitor sent none.
/// </param>
internal sealed record PageViewBeacon(string? Path, string? Referrer, bool? Automated, string? UserAgent);
