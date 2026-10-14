namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// Doc 03 §3a / doc 08 §3.1 DB10: "the customer scheme rejects Staff principals and the admin scheme
/// rejects Customers, both at sign-in and on security-stamp revalidation".
/// </summary>
/// <remarks>
/// <b>A separate scheme from the admin one, and not a shared cookie with a type claim.</b> Doc 08 §3.1's
/// DB10 asks for two schemes; the reason is that one cookie carrying a role flag is one bug away from a
/// customer session satisfying an admin policy, whereas two schemes cannot be confused by an endpoint
/// that names the wrong one — a customer principal simply is not present on the admin scheme.
/// <para>
/// <b>The cookies also have different names, paths and lifetimes</b>, so a browser signed in as both (a
/// staff member who shops with a second address, doc 08 D7) keeps two independent sessions, and signing
/// out of one does not touch the other.
/// </para>
/// </remarks>
public static class CustomerAuthenticationDefaults
{
    public const string Scheme = "CustomerCookie";

    public const string CookieName = "sd_customer_auth";

    /// <summary>
    /// Custom claim carrying <see cref="Domain.Identity.UserType"/>, so the scheme can reject a staff
    /// principal on every revalidation and not only at sign-in.
    /// </summary>
    /// <remarks>
    /// Deliberately the <i>same</i> claim type string as the admin scheme's
    /// (<see cref="AdminAuthenticationDefaults.UserTypeClaimType"/>). Two names for one fact would let a
    /// principal satisfy one scheme's check while carrying the other scheme's claim; one name means a
    /// cookie from either scheme states its type in the place both validators look. The schemes are kept
    /// apart by the cookie's own data-protection purpose, which is the scheme name, so a customer cookie
    /// cannot be decrypted as an admin cookie whatever its claims say.
    /// </remarks>
    public const string UserTypeClaimType = AdminAuthenticationDefaults.UserTypeClaimType;

    /// <summary>
    /// Custom claim carrying the <c>AppUser.SecurityStamp</c> at sign-in, compared against the current
    /// database value on each periodic revalidation, so a password reset ends every other session
    /// promptly (doc 11 P11 step C.9, "invalidates existing sessions").
    /// </summary>
    public const string SecurityStampClaimType = AdminAuthenticationDefaults.SecurityStampClaimType;

    /// <summary>
    /// Claim carrying whether the address is confirmed, so the storefront shell can show doc 11 §7
    /// G-24's "confirm your email" banner without a round trip on every page.
    /// </summary>
    /// <remarks>
    /// <b>It is a hint for the banner and never a gate.</b> G-24 lets an unconfirmed customer sign in,
    /// shop and check out, and the one thing confirmation does gate — guest-order linking — happens
    /// inside the confirmation transaction itself, not behind a claim check. So a stale value here costs
    /// a banner that is one navigation out of date and nothing else. It is refreshed whenever the cookie
    /// is revalidated against the database.
    /// </remarks>
    public const string EmailConfirmedClaimType = "sd_email_confirmed";
}
