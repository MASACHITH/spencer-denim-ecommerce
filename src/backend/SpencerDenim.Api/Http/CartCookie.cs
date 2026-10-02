using SpencerDenim.Application.Sales.Cart;

namespace SpencerDenim.Api.Http;

/// <summary>
/// The guest bag cookie (doc 08 §3.5: "guests get an HttpOnly, Secure, SameSite=Lax cookie holding
/// <c>Carts.Token</c> (30-day sliding)").
/// <para>
/// It holds nothing but the token. The token is a capability — whoever has it has that bag — so the
/// cookie carries the three flags that keep it out of reach of a script, an unencrypted hop and a
/// cross-site request, and the cart lookup refuses to let a token reach an account cart at all
/// (<c>CartStore.FindAsync</c>). It is never echoed into a response body.
/// </para>
/// </summary>
internal static class CartCookie
{
    /// <summary>
    /// Named like the other cookies this application sets (<c>sd_admin_auth</c>, <c>sd_admin_xsrf</c>).
    /// Doc 02 §I2 keeps the <c>sd_</c> prefix deliberately: a cookie name is part of a live session, not
    /// branding.
    /// </summary>
    public const string Name = "sd_cart";

    /// <summary>Doc 08 §3.5 and §8.4: 30 days, and sliding — every answer that resolves the cart reissues it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>
    /// The token the request carried, or <see langword="null"/> when it carried nothing usable.
    /// <para>
    /// Anything that is not the canonical GUID form this application writes — an empty value, a
    /// truncated one, a word, an all-zero GUID — reads as <b>no token</b> rather than as a bad request.
    /// A shopper cannot edit their own cookie to fix it, so a 400 would wedge that browser out of the
    /// shop until they cleared site data; "no token" lets the next Add to Bag issue a fresh one. It is
    /// also what a malicious value gets: there is no path from a cookie value to a SQL parameter that is
    /// not a <see cref="Guid"/>.
    /// </para>
    /// </summary>
    public static Guid? Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Cookies.TryGetValue(Name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Exact "D" form only: the one shape this application ever writes. TryParse would also accept
        // braces, parentheses and the 32-digit form, which would mean four different cookie values
        // naming the same bag and nothing gained.
        return Guid.TryParseExact(raw, "D", out var token) && token != Guid.Empty ? token : null;
    }

    /// <summary>Writes, rewrites or deletes the cookie, as the use case decided.</summary>
    public static void Apply(HttpResponse response, CartCookieAction action, Guid? token, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(clock);

        switch (action)
        {
            case CartCookieAction.Issue when token is { } value && value != Guid.Empty:
                response.Cookies.Append(Name, value.ToString("D"), Options(clock.GetUtcNow() + Lifetime));
                break;

            case CartCookieAction.Clear:
                // Deleted with the same attributes it was written with. A Delete that disagrees about
                // Path or SameSite leaves the original cookie in place and the browser keeps sending a
                // token the server has already disowned.
                response.Cookies.Delete(Name, Options(expires: null));
                break;

            case CartCookieAction.Issue:
            case CartCookieAction.Leave:
            default:
                break;
        }
    }

    private static CookieOptions Options(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,

        // Unconditional, not CookieSecurePolicy.SameAsRequest. A cart token is a capability and the
        // deployment is HTTPS-only behind Caddy (doc 03 §4), so there is no environment where sending it
        // in clear is wanted. Browsers treat http://localhost as a trustworthy origin and accept Secure
        // cookies there, so development and the browser tests are unaffected; the .NET integration tests
        // address the host as https://localhost for the same reason.
        Secure = true,

        // Doc 08 §3.5 says Lax, and Lax rather than Strict is the right trade here: a shopper who
        // arrives from an email, an ad or a search result makes a cross-site *top-level navigation*, and
        // Strict would withhold the cookie on exactly that request — so the server-rendered header would
        // show an empty bag to someone whose bag is not empty, and the page would correct itself a moment
        // later. Lax still withholds it from cross-site POSTs, which is the case that matters: a
        // cross-site form post cannot reach anyone's bag.
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires,

        // A shopping bag is strictly necessary for the shop to work, so it is not gated behind a consent
        // banner. It holds an opaque token and no personal data.
        IsEssential = true,
    };
}
