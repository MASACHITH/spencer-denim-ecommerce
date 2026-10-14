namespace SpencerDenim.Application.Accounts.Contracts;

/// <summary>
/// What a visitor typed into the registration form (ACC-01, doc 11 P11 step A.1).
/// </summary>
/// <param name="Phone">
/// Optional — doc 11 P11 step A.1 says "and optionally phone". Checkout asks for one per order anyway, so
/// a shopper is never blocked here for want of it.
/// </param>
public sealed record CustomerRegistration(
    string FirstName,
    string? LastName,
    string Email,
    string Password,
    string? Phone);

/// <summary>
/// What registration did, <b>for the server's own log and tests only</b>.
/// </summary>
/// <remarks>
/// <b>This value never reaches a response body, and that is the whole point of doc 08 §3.1 DB10.</b>
/// "Register with an existing email (either type) gives the same generic response." So the endpoint
/// answers one sentence for all four cases below, and this enum exists so that the <i>tests</i> can tell
/// them apart — a test that could only see the response could not prove the non-enumeration property
/// holds, because it could not tell a suppressed branch from a broken one.
/// </remarks>
public enum CustomerRegistrationOutcome
{
    /// <summary>A new customer account was created; a confirmation and a welcome message were queued.</summary>
    Created = 1,

    /// <summary>
    /// The address already has a <b>customer</b> account. Nothing was created and an
    /// <c>AccountExists</c> message was queued to it (doc 11 P11 step A.2).
    /// </summary>
    AlreadyACustomer = 2,

    /// <summary>
    /// The address belongs to a <b>staff</b> account. Nothing was created and <b>nothing was sent</b>
    /// (doc 08 §3.1 DB10: "a staff match sends nothing"). A staff member who shops uses another address
    /// (doc 08 D7).
    /// </summary>
    BelongsToStaff = 3,
}

/// <summary>The customer a signed-in session is built from.</summary>
/// <param name="EmailConfirmed">
/// Doc 11 §7 G-24: an unconfirmed customer may sign in, shop and check out, so this is never a gate. It
/// is what the storefront shows the "confirm your email" banner from, and what tells a shopper why their
/// guest orders are not in their history yet.
/// </param>
public sealed record CustomerSession(
    int UserId,
    string Email,
    string FirstName,
    string? LastName,
    bool EmailConfirmed,
    string SecurityStamp);

/// <summary>
/// The outcome of a customer sign-in attempt (doc 11 P11 step B.5).
/// </summary>
/// <remarks>
/// <b>Three cases and not four.</b> There is no "unknown email" and no "this is a staff account": both
/// are <see cref="InvalidCredentials"/>, and the authenticator spends the same password-hash cost on them
/// as on a real wrong password, so the reply does not differ in its wording or in its timing (doc 08 §3.1
/// DB10; #101's finding on the staff routes).
/// </remarks>
public abstract record CustomerSignInResult
{
    private CustomerSignInResult()
    {
    }

    /// <summary>Signed in. The endpoint issues the cookie.</summary>
    public sealed record Success(CustomerSession Session) : CustomerSignInResult;

    /// <summary>
    /// Wrong password, unknown address, a staff address, or a deactivated account. One case, one reply.
    /// </summary>
    public sealed record InvalidCredentials : CustomerSignInResult;

    /// <summary>
    /// Doc 11 P11's exception list: "Too many attempts, try again in 15 minutes"; the lock clears itself.
    /// </summary>
    /// <param name="WasNewlyLockedOut">
    /// True for the one attempt whose own failure tripped the lock, so exactly one <c>Lockout</c> audit
    /// row is written however many attempts are in flight.
    /// </param>
    public sealed record LockedOut(DateTime LockoutEndUtc, bool WasNewlyLockedOut) : CustomerSignInResult;
}

/// <summary>
/// The outcome of redeeming an email-confirmation link (doc 11 P11 step A.4).
/// </summary>
public abstract record CustomerEmailConfirmationResult
{
    private CustomerEmailConfirmationResult()
    {
    }

    /// <summary>
    /// Confirmed, and doc 08 §3.1 D5's guest-order linking has run inside the same transaction.
    /// </summary>
    /// <param name="LinkedOrderNumbers">
    /// The orders that became this customer's, newest first. Shown to the shopper — doc 11 P11's
    /// exception "registers with the email used for 3 guest orders → after confirming, all 3 appear" is
    /// the thing to tell them about, and a silent confirmation leaves them wondering whether it worked.
    /// </param>
    public sealed record Confirmed(CustomerSession Session, IReadOnlyList<string> LinkedOrderNumbers)
        : CustomerEmailConfirmationResult;

    /// <summary>
    /// The confirming browser is not signed in. Nothing was read, spent, confirmed or linked.
    /// </summary>
    /// <remarks>
    /// <b>Why confirming needs a session at all</b> — the independent tester's Blocker 2. A confirmation
    /// link proves the clicker controls the <i>mailbox</i>; it says nothing about who chose the account's
    /// <i>password</i>. An attacker who registered the victim's address first owns the password, and before
    /// this case existed the victim's click linked their guest orders to the attacker's account. Linking now
    /// needs both proofs at once: the token (the mailbox) and a session for the account the token names
    /// (the credentials). A victim gets the second by resetting the password through the same mailbox,
    /// which kills the attacker's password and session.
    /// </remarks>
    public sealed record SignInRequired : CustomerEmailConfirmationResult;

    /// <summary>
    /// The link was already used, has expired, names no pending confirmation, does not verify, or names an
    /// account other than the one signed in.
    /// </summary>
    /// <remarks>
    /// <b>One case for all four, deliberately.</b> Telling a holder that a token is "expired" rather than
    /// "wrong" tells them it was once real, which is the same class of leak the guest order link's single
    /// opaque refusal avoids.
    /// </remarks>
    public sealed record InvalidToken : CustomerEmailConfirmationResult;
}

/// <summary>The outcome of redeeming a password-reset link (doc 11 P11 step C.9).</summary>
public abstract record CustomerPasswordResetResult
{
    private CustomerPasswordResetResult()
    {
    }

    /// <summary>
    /// The password is set, the security stamp is bumped (so every other session ends), and the token is
    /// spent.
    /// </summary>
    /// <remarks>
    /// <b>No session is issued.</b> Doc 11 P11 step C.9 allows either ("signs the customer in or asks
    /// them to sign in") and this build asks them to sign in: issuing a cookie from a link redeemed in
    /// whatever browser opened the email is the one place a reset could hand a session to a mailbox
    /// reader rather than to the account's owner, and the cost of the other choice is one form.
    /// </remarks>
    public sealed record Success : CustomerPasswordResetResult;

    /// <summary>The link was already used, has expired, or does not verify. One opaque case.</summary>
    public sealed record InvalidToken : CustomerPasswordResetResult;

    /// <summary>The new password was refused. The messages are Identity's own plus the length rule.</summary>
    public sealed record WeakPassword(IReadOnlyList<string> Problems) : CustomerPasswordResetResult;
}

/// <summary>The outcome of a signed-in customer changing their own password (doc 11 P11 step 10).</summary>
public abstract record CustomerPasswordChangeResult
{
    private CustomerPasswordChangeResult()
    {
    }

    /// <summary>
    /// Changed. The security stamp is bumped, so the caller's own cookie has to be reissued with the new
    /// one or this browser signs itself out on the next revalidation.
    /// </summary>
    public sealed record Success(CustomerSession Session) : CustomerPasswordChangeResult;

    /// <summary>The current password was wrong. Counted towards the same lockout as a sign-in failure.</summary>
    public sealed record WrongCurrentPassword : CustomerPasswordChangeResult;

    /// <summary>Too many wrong current passwords (doc 08 §3.1 N14's time-boxed lockout).</summary>
    public sealed record LockedOut(DateTime LockoutEndUtc, bool WasNewlyLockedOut) : CustomerPasswordChangeResult;

    /// <summary>The new password was refused.</summary>
    public sealed record WeakPassword(IReadOnlyList<string> Problems) : CustomerPasswordChangeResult;
}

/// <summary>A customer's own profile, as My account shows it (ACC-02, doc 11 P11 step 10).</summary>
public sealed record CustomerProfile(
    int UserId,
    string Email,
    bool EmailConfirmed,
    string FirstName,
    string? LastName,
    string? Phone,
    int? DefaultDistrictId,
    string? DefaultDistrictName);

/// <summary>What a customer may change about their own profile (doc 11 P11 step 10).</summary>
/// <remarks>
/// <b>The email address is not here.</b> Doc 11 P11 step 10 includes "changes email (confirmation to the
/// new address required)" and that flow is <b>not built</b> — see the task report. It is a different
/// shape from the three fields below: it needs the uniqueness check across both user types, a
/// confirmation token minted against the <i>new</i> address, the old address left working until the new
/// one is confirmed, and a decision about whether the old address is notified (doc 08 §3.1 R10 gives
/// that rule for staff only). Half of it would be worse than none.
/// </remarks>
public sealed record CustomerProfileEdit(
    string FirstName,
    string? LastName,
    string? Phone,
    int? DefaultDistrictId);
