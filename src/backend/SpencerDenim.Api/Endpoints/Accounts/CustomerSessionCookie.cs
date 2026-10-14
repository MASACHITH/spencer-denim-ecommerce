using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using SpencerDenim.Application.Accounts.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Identity.Customers;

namespace SpencerDenim.Api.Endpoints.Accounts;

/// <summary>
/// Issues and reads the customer session cookie (doc 03 §3a, doc 08 §3.1 DB10).
/// </summary>
/// <remarks>
/// <b>One place that builds the principal, because three endpoints issue one</b> — sign-in, the
/// password change that rotates the security stamp, and the confirmation that changes the confirmed
/// claim. Three copies of the claim list is three chances for one of them to leave out the stamp, and a
/// cookie with no stamp claim is a session the revalidation cannot end.
/// </remarks>
internal static class CustomerSessionCookie
{
    /// <summary>
    /// Signs the customer in, replacing any session already in this browser.
    /// </summary>
    /// <remarks>
    /// <b>The claims are the minimum the storefront needs and no more.</b> The id and the stamp are what
    /// the revalidation compares; the email and the first name are what the header greets them by, so
    /// that showing "Hello Nimal" costs no request. There is <b>no permission claim and no role claim</b>
    /// — doc 08 §3.1 R4 says permissions come from roles read from the database and never from a
    /// principal, and a customer has neither in any case (doc 08 §3.1: "only <c>UserType = Staff</c> users
    /// receive roles").
    /// </remarks>
    public static Task SignInAsync(HttpContext httpContext, CustomerSession session)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(session);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.UserId.ToString(CultureInfo.InvariantCulture)),
            new(ClaimTypes.Email, session.Email),
            new(ClaimTypes.GivenName, session.FirstName),
            new(
                CustomerAuthenticationDefaults.UserTypeClaimType,
                ((byte)UserType.Customer).ToString(CultureInfo.InvariantCulture)),
            new(CustomerAuthenticationDefaults.SecurityStampClaimType, session.SecurityStamp),
            new(CustomerAuthenticationDefaults.EmailConfirmedClaimType, session.EmailConfirmed ? "1" : "0"),
        };

        if (!string.IsNullOrWhiteSpace(session.LastName))
        {
            claims.Add(new Claim(ClaimTypes.Surname, session.LastName));
        }

        var identity = new ClaimsIdentity(claims, CustomerAuthenticationDefaults.Scheme);

        return httpContext.SignInAsync(
            CustomerAuthenticationDefaults.Scheme,
            new ClaimsPrincipal(identity));
    }

    public static Task SignOutAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return httpContext.SignOutAsync(CustomerAuthenticationDefaults.Scheme);
    }

    /// <summary>
    /// The signed-in customer's user id, from the session principal.
    /// </summary>
    /// <remarks>
    /// <b>Never from client input, and never from a route or a body.</b> Every account endpoint reads the
    /// owner from here, which is why none of the ports has an overload that takes an owner the caller
    /// chose.
    /// </remarks>
    public static bool TryGetUserId(ClaimsPrincipal? user, out int userId)
    {
        userId = 0;

        return user is not null
            && int.TryParse(
                user.FindFirstValue(ClaimTypes.NameIdentifier),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out userId);
    }

    /// <summary>
    /// A freshly issued session, a profile and an address book must not be kept by a browser or a shared
    /// proxy cache.
    /// </summary>
    /// <remarks>
    /// <c>Vary: Cookie</c> as well as <c>no-store</c>, for <c>CheckoutEndpoints.NoStore</c>'s reason: the
    /// two say different things and a cache honouring only one of them has handed one shopper another
    /// shopper's page.
    /// </remarks>
    public static void DoNotStore(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        response.Headers.CacheControl = "no-store, no-cache, private";
        response.Headers.Pragma = "no-cache";
        response.Headers.Vary = "Cookie";
    }
}
