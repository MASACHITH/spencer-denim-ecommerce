namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// The name of the one authorization policy a customer endpoint requires (ACC-01, ACC-02).
/// </summary>
/// <remarks>
/// <b>A constant rather than a literal per endpoint, because a mistyped policy name is a 500 and not a
/// compile error.</b> ASP.NET Core throws <c>InvalidOperationException</c> at request time for a policy
/// it cannot find, so eleven routes each spelling the name out is eleven chances to spell it differently
/// and find out in production.
/// <para>
/// <b>Why the customer surface needs its own policy at all</b> — rather than a bare
/// <c>RequireAuthorization()</c> — is on the registration that builds it: the default policy carries the
/// admin account gates, which a shopper satisfies none of, and the default scheme is the admin cookie.
/// </para>
/// </remarks>
public static class CustomerAuthorization
{
    public const string PolicyName = "CustomerSession";
}
