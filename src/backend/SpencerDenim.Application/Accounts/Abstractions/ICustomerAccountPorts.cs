using SpencerDenim.Application.Accounts.Contracts;

namespace SpencerDenim.Application.Accounts.Abstractions;

/// <summary>
/// Doc 11 P11 step A: create a customer account, or do the non-enumerating nothing (ACC-01).
/// </summary>
/// <remarks>
/// <b>Four ports rather than one <c>ICustomerAccounts</c>, because the admin side's four</b>
/// (<c>IAdminAuthenticator</c>, <c>IAdminPasswordChanger</c>, <c>IAdminTwoFactorManager</c>,
/// <c>IAdminUserLookup</c>) <b>are the precedent and the ISP argument is the same</b>: the sign-in
/// endpoint has no business being able to register an account, and a fake in a unit test should not have
/// to implement eight methods to exercise one.
/// </remarks>
public interface ICustomerRegistration
{
    /// <summary>
    /// Doc 11 P11 steps A.1–A.3, in one transaction: the user row, the confirmation token, and the
    /// messages that go with whichever of doc 08 §3.1 DB10's three cases this address is.
    /// </summary>
    /// <returns>
    /// Which case it was — <b>for the log and for tests</b>. The endpoint must answer the same sentence
    /// whatever comes back; see <see cref="CustomerRegistrationOutcome"/>.
    /// </returns>
    Task<CustomerRegistrationOutcome> RegisterAsync(
        CustomerRegistration registration,
        CancellationToken cancellationToken);

    /// <summary>
    /// Mints a fresh confirmation token for a signed-in customer and queues the message (doc 11 P11's
    /// "send me a new link", which is an ordinary thing to do).
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when nothing was queued: the address is already confirmed, or there is no
    /// configured storefront origin to build a link from. The endpoint answers the same either way.
    /// </returns>
    Task<bool> ResendConfirmationAsync(int userId, CancellationToken cancellationToken);
}

/// <summary>
/// Doc 11 P11 step B: check a customer's credentials. It never signs a cookie in — that is the endpoint's
/// job, exactly as it is on the admin side.
/// </summary>
public interface ICustomerAuthenticator
{
    /// <summary>
    /// Doc 11 P11 step B.5. Filtered <c>UserType = Customer</c> before <c>UserManager</c> is consulted
    /// (doc 08 §3.1 DB10), with the unknown-address path spending the same password-hash cost as a real
    /// wrong password (#101).
    /// </summary>
    Task<CustomerSignInResult> SignInAsync(string email, string password, CancellationToken cancellationToken);

    /// <summary>
    /// The session fields for a customer who is already signed in, read fresh, or <see langword="null"/>
    /// when the account has gone away or is no longer an active customer.
    /// </summary>
    Task<CustomerSession?> FindSessionAsync(int userId, CancellationToken cancellationToken);
}

/// <summary>Doc 11 P11 step C and step 10's password half.</summary>
public interface ICustomerPasswords
{
    /// <summary>
    /// Doc 11 P11 step C.8: mint a reset token and queue the message — <b>only</b> for a customer match.
    /// </summary>
    /// <remarks>
    /// <b>It returns nothing, and that is the enumeration rule expressed in the type.</b> Doc 08 §3.1
    /// DB10: the same generic message whatever the address is, and only a customer match triggers an
    /// email. A method that reported which case it took would be one careless endpoint away from leaking
    /// it; a method that cannot report it cannot be misused that way. The outcome is logged, and the
    /// integration suite asserts the three cases by looking at <c>ops.EmailOutbox</c> rather than at the
    /// response.
    /// </remarks>
    Task RequestResetAsync(string email, CancellationToken cancellationToken);

    /// <summary>
    /// Doc 11 P11 step C.9: redeem the link, set the password, spend the token and bump the security
    /// stamp.
    /// </summary>
    /// <remarks>
    /// <b>The stamp bump is not cosmetic.</b> <c>IEffectivePermissionsProvider</c> caches on
    /// <c>(userId, SecurityStamp)</c> for five minutes and both cookie schemes revalidate against it, so
    /// the bump is what ends every other session — doc 11 P11 step C.9's "invalidates existing sessions".
    /// </remarks>
    Task<CustomerPasswordResetResult> ResetAsync(
        string token,
        string newPassword,
        CancellationToken cancellationToken);

    /// <summary>Doc 11 P11 step 10: a signed-in customer changes their own password.</summary>
    Task<CustomerPasswordChangeResult> ChangeAsync(
        int userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken);
}

/// <summary>Doc 11 P11 step A.4: redeem a confirmation link, and link the guest orders behind it.</summary>
public interface ICustomerEmailConfirmation
{
    /// <summary>
    /// Confirms the address the token was minted for and runs doc 08 §3.1 D5's linking in the same
    /// transaction, so a confirmed address and its linked orders are true together or not at all.
    /// </summary>
    /// <param name="signedInUserId">
    /// The customer the confirming browser is signed in as. The token must name <b>this</b> account, or it
    /// is refused without being spent — so a stranger cannot burn somebody else's link, and the account's
    /// credentials as well as its mailbox are required before anything is linked.
    /// </param>
    Task<CustomerEmailConfirmationResult> ConfirmAsync(
        string token,
        int signedInUserId,
        CancellationToken cancellationToken);
}

/// <summary>Doc 11 P11 step 10's profile half (ACC-02).</summary>
public interface ICustomerProfileStore
{
    Task<CustomerProfile?> FindAsync(int userId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the three fields and the default district. <see langword="null"/> when the account has gone
    /// away.
    /// </summary>
    /// <remarks>
    /// <b>A district that does not exist, or is not active, is refused rather than stored.</b> W3-01 gave
    /// <c>identity.Users.DefaultDistrictId</c> its foreign key, so an unknown id would now fail at the
    /// database with a constraint error a shopper cannot read; and an <i>inactive</i> district satisfies
    /// the constraint but has no delivery rate, so GLB-06's estimate would silently have nothing to show.
    /// </remarks>
    Task<CustomerProfile?> SaveAsync(
        int userId,
        CustomerProfileEdit edit,
        CancellationToken cancellationToken);
}
