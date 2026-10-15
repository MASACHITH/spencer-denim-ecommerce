namespace SpencerDenim.Api.Http;

/// <summary>
/// The visit-session cookie behind <c>analytics.PageViews.SessionId</c> (doc 08 §3.8: "Session cookie. No
/// persistent visitor ID").
/// <para>
/// <b>Thirty minutes, sliding, and that is what makes it a session rather than a visitor ID.</b> Every page
/// view re-issues it with a fresh thirty minutes, so a visit is "page views with no gap longer than half an
/// hour" — the usual definition — and a visitor who comes back tomorrow, or after lunch, is a new session
/// with a new random value that nothing links to the old one. A cookie with no expiry at all ("session
/// cookie" read literally) would be <i>worse</i> on that score: browsers that restore tabs keep such cookies
/// for days or weeks, which would turn it into exactly the long-lived identifier doc 08 rules out.
/// </para>
/// <para>
/// It holds a random GUID and nothing else: no user, no IP, no fingerprint. HttpOnly, because no script needs
/// it (the beacon is same-origin and the browser attaches it); Secure and SameSite=Lax like
/// <c>sd_cart</c>. The storefront's server issues the same cookie, by the same name and rules, when it
/// records a server-rendered page (<c>src/server/page-view.ts</c>).
/// </para>
/// </summary>
internal static class VisitSessionCookie
{
    /// <summary>Named like the application's other cookies (<c>sd_cart</c>, <c>sd_admin_auth</c>).</summary>
    public const string Name = "sd_vs";

    /// <summary>Thirty idle minutes ends a visit.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The request's session, or a new one; either way the cookie is (re)issued with a fresh thirty minutes.
    /// A value that is not a canonical, non-empty GUID reads as no session rather than as a bad request: a
    /// visitor cannot fix their own cookie, and a recorder must never refuse a page.
    /// </summary>
    public static Guid ReadOrIssue(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var session = Read(context.Request) ?? Guid.NewGuid();

        context.Response.Cookies.Append(Name, session.ToString("D"), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = IdleLifetime,
        });

        return session;
    }

    /// <summary>The session the request carried, or <see langword="null"/>.</summary>
    public static Guid? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Cookies.TryGetValue(Name, out var raw)
            && !string.IsNullOrWhiteSpace(raw)
            && Guid.TryParseExact(raw, "D", out var session)
            && session != Guid.Empty
                ? session
                : null;
    }
}
