using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using SpencerDenim.Application.Sales.Cart;
using SpencerDenim.Infrastructure.Identity.Customers;

namespace SpencerDenim.Api.Http;

/// <summary>
/// Who the signed-in customer is on an endpoint that does not <i>require</i> one — the bag and checkout.
/// </summary>
/// <remarks>
/// <b>This is the line W3-01 first left out, and the independent tester found what that cost.</b> The bag
/// and checkout endpoints are anonymous, so ASP.NET Core authenticates them with the <i>default</i> scheme
/// — the admin cookie — and <see cref="HttpContext.User"/> never holds a customer. Signing in merged the
/// guest bag into the account cart and cleared <c>sd_cart</c>, and both endpoints then went on reading
/// <c>CartLookup.ForGuest(cookie)</c> with no cookie: <b>the bag emptied on sign-in</b>, and every order
/// placed while signed in was stored as a guest order that never reached My account.
/// <para>
/// <b>So the customer scheme is asked for by name</b>, which runs its <c>OnValidatePrincipal</c> — a
/// staff principal, a deactivated account and a stale security stamp are all rejected there, exactly as
/// on the account routes. The answer is cached on the request, because checkout asks twice (the lookup
/// and the order's owner) and each ask would otherwise be a cookie decryption.
/// </para>
/// </remarks>
internal static class SignedInCustomer
{
    private const string CacheKey = "sd.SignedInCustomerId";

    /// <summary>The signed-in customer's id, or <see langword="null"/> for a guest.</summary>
    public static async Task<int?> ReadIdAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.Items.TryGetValue(CacheKey, out var cached))
        {
            return (int?)cached;
        }

        var result = await httpContext.AuthenticateAsync(CustomerAuthenticationDefaults.Scheme);

        int? id = result.Succeeded
            && int.TryParse(
                result.Principal.FindFirstValue(ClaimTypes.NameIdentifier),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var userId)
            ? userId
            : null;

        httpContext.Items[CacheKey] = id;

        return id;
    }

    /// <summary>
    /// Which bag this request means: the account's when a customer is signed in, otherwise the guest
    /// cookie's.
    /// </summary>
    /// <remarks>
    /// The guest token is still passed for a customer, because <c>CartLookup</c>'s own contract is that
    /// the customer id alone decides (<c>CartLookup.ByCustomer</c>) — the token is ignored, and the bag
    /// handlers then answer <c>CartCookieAction.Clear</c>, so a guest token left lying in a signed-in
    /// browser is removed rather than read.
    /// </remarks>
    public static async Task<CartLookup> ReadCartLookupAsync(HttpContext httpContext)
    {
        var guestToken = CartCookie.Read(httpContext.Request);

        return await ReadIdAsync(httpContext) is { } customerUserId
            ? CartLookup.ForCustomer(customerUserId, guestToken)
            : CartLookup.ForGuest(guestToken);
    }
}
