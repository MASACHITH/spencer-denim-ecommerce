namespace SpencerDenim.Infrastructure.Identity.Customers;

/// <summary>
/// Customer authentication settings, bound from environment variables (<c>CustomerSecurity__*</c>).
/// </summary>
/// <remarks>
/// <b>Doc 08 §3.8 keeps the two lockout figures as <c>ops.Settings</c> rows</b>
/// (<c>Security.CustomerLockoutMaxFailures</c> = 10, <c>Security.CustomerLockoutMinutes</c> = 15) under
/// the <c>Settings.Security</c> permission, which is GrantsAll-only and needs N2's sensitive-change
/// alert. That machinery does not exist, so the keys stay in <c>DeferredSettingKeys</c> and the defaults
/// below match doc 08's seeded values exactly — the same arrangement <see cref="AdminSecurityOptions"/>
/// has for the staff pair, and <c>SettingKeySpecificationTests</c> pins all four against the document's
/// own cells rather than against a second copy of the numbers.
/// <para>
/// <b>A separate options class from <see cref="AdminSecurityOptions"/>, because the numbers are different
/// and so is the threat.</b> Doc 08 §3.1 DB10 gives staff 5 failures and customers 10: a staff account is
/// a higher-value target with a smaller user population, and a shopper who mistypes their password four
/// times on a telephone keyboard should not be locked out of a shop. One options class with one pair of
/// numbers would have forced one answer.
/// </para>
/// <para>
/// <b>Doc 08 §3.1 DB10's last bullet says "Identity's global lockout options are the customer
/// baseline", and in this repository they are not.</b> <c>IdentityRegistration</c> binds
/// <c>IdentityOptions.Lockout</c> from <see cref="AdminSecurityOptions"/> — 5 and 15 — because the admin
/// login was built first and nothing else read them. Rather than change a shared startup path that
/// Tharanga's open lockout work (#146) is in the middle of, the customer side does not use
/// <c>IdentityOptions.Lockout</c> at all: <c>CustomerAccountLockout</c> reads these options and does its
/// own atomic counting. The consequence is that the document's sentence is still unimplemented in the
/// direction it describes, and it is in the task report.
/// </para>
/// </remarks>
public sealed class CustomerSecurityOptions
{
    public const string SectionName = "CustomerSecurity";

    /// <summary>Doc 08 §3.8 <c>Security.CustomerLockoutMaxFailures</c> default (doc 11 P11 step B.5: 10).</summary>
    public int LockoutMaxFailedAttempts { get; set; } = 10;

    /// <summary>Doc 08 §3.8 <c>Security.CustomerLockoutMinutes</c> default (doc 11 P11 step B.5: 15).</summary>
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>
    /// Customer session cookie lifetime in minutes, sliding.
    /// </summary>
    /// <remarks>
    /// <b>Thirty days, where an admin session is an hour, and the difference is deliberate.</b> An admin
    /// session is a privileged one on a shared office machine; a shopper's is their own browser, and
    /// doc 08 §3.5 already gives a customer's bag a 30-day sliding window and their wishlist a durable
    /// one across devices. A session that expired faster than the bag would hand a shopper an account bag
    /// they could no longer see. It slides, so a customer who visits weekly is never signed out.
    /// </remarks>
    public int CookieLifetimeMinutes { get; set; } = 60 * 24 * 30;

    /// <summary>
    /// How often an already-signed-in request re-checks the customer's <c>IsActive</c> flag and
    /// <c>SecurityStamp</c> against the database.
    /// </summary>
    /// <remarks>
    /// <b>Five minutes, where the admin scheme uses one.</b> Doc 08 §3.1 DB10 states the one-minute
    /// interval for the admin scheme only ("interval 1 min for admin"), and doc 11 P14's "sessions end
    /// within 1 minute" is about staff. The thing a customer bump has to end is the other browser after a
    /// password reset, and five minutes of that is the ordinary cost of a cookie; checking a shopper's
    /// row against the database twelve times a minute per visitor is a cost the storefront pays on every
    /// page of every session. Configurable so a test can shrink it rather than wait out a real interval.
    /// </remarks>
    public int SecurityStampValidationIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// How long a password-reset link is good for, in hours.
    /// </summary>
    /// <remarks>
    /// <b>Two hours.</b> Doc 08 and doc 11 give no figure — doc 11 P11 step C.9 says only that the link
    /// sets a new password — so this is a decision. Identity's own data-protector default is a day, which
    /// is a long time for a credential sitting in an inbox; two hours is enough for a customer to find
    /// the message and short enough that a forwarded or breached mailbox is a narrow window. The body
    /// says the figure, from this value, so the copy cannot drift from the token.
    /// </remarks>
    public int PasswordResetTokenHours { get; set; } = 2;

    /// <summary>
    /// How long an email-confirmation link is good for, in hours.
    /// </summary>
    /// <remarks>
    /// <b>Seven days, far longer than the reset.</b> The two links are not the same kind of credential.
    /// A reset token can set a password; a confirmation token can only mark an address confirmed and link
    /// guest orders that were placed <i>with that same address</i> — so the worst an old one does is
    /// confirm an address its own owner already controls. The cost of a short window is the opposite of
    /// the reset's: doc 11 P11's exception "customer never confirms → can sign in but guest orders are
    /// not linked" is the state a two-hour link would leave people in, and a customer who opens the
    /// message the next evening should not have to ask for another.
    /// </remarks>
    public int EmailConfirmationTokenHours { get; set; } = 24 * 7;

    /// <summary>
    /// Sign-in attempts one caller may make per ten minutes (<c>AccountRequestThrottle</c>).
    /// </summary>
    /// <remarks>
    /// <b>Thirty.</b> A person signs in once and mistypes twice; the margin above that is for
    /// carrier-grade NAT — Sri Lankan mobile networks put many subscribers behind one public address,
    /// which is <c>NewsletterSignupThrottle</c>'s own reason for its margin — and for a household or an
    /// office sharing a connection.
    /// <para>
    /// <b>It is a setting and not a constant for two reasons.</b> A deployment whose reverse-proxy
    /// headers are not trusted has every visitor in one bucket (the throttle logs a warning about
    /// exactly that), and raising this is the stopgap while the configuration is fixed. And a test
    /// cannot raise a compile-time constant: the sign-in race tests fire thirty attempts at one account
    /// and would otherwise be measuring the rate limiter rather than the lockout.
    /// </para>
    /// <para>
    /// <b>This is not the lockout and does not replace it.</b> The lockout protects one account from
    /// being guessed at; this protects the shop from one source trying many accounts, which a
    /// per-account lockout cannot see at all.
    /// </para>
    /// </remarks>
    public int SignInAttemptsPerWindow { get; set; } = 30;

    /// <summary>
    /// Registrations one caller may make per ten minutes. <b>Five</b>: a person registers once, a family
    /// behind one connection a handful of times, and each one writes a user row and queues two messages.
    /// </summary>
    public int RegistrationsPerWindow { get; set; } = 5;

    /// <summary>
    /// Reset or confirmation links one caller may ask for per ten minutes.
    /// </summary>
    /// <remarks>
    /// <b>Five, and this is the tightest of the four</b>, because the cost lands in <i>somebody else's</i>
    /// inbox: an unlimited forgot-password form is a way to send a stranger as many messages as you
    /// like, with the shop's name on them.
    /// </remarks>
    public int LinkRequestsPerWindow { get; set; } = 5;

    /// <summary>
    /// One-time links one caller may try to redeem per ten minutes.
    /// </summary>
    /// <remarks>
    /// <b>Twenty.</b> A genuine caller redeems once, and perhaps three more times after mistyping a
    /// password. The number does not have to be small to be useful: the verifier is 256 bits of
    /// <c>RandomNumberGenerator</c> output, so guessing is infeasible at any rate — what this stops is
    /// somebody walking a list of leaked links or hammering the endpoint for its hash and database work.
    /// Doc 11 §7 G-41 makes the same argument about the guest order link's 122-bit token.
    /// </remarks>
    public int LinkRedemptionsPerWindow { get; set; } = 20;

    /// <summary>
    /// The least time, in milliseconds, that sign-in, registration and forgot-password take to answer —
    /// whatever the answer is.
    /// </summary>
    /// <remarks>
    /// <b>Why a floor as well as the dummy password hash.</b> The independent tester measured
    /// forgot-password at 4.6 ms for a customer and 1.2 ms for an unknown or staff address — 3.8x, and it
    /// passed my own 10x test. That difference is not a hash at all: it is the token insert and the outbox
    /// row a customer match writes, which an unknown address cannot write. Equalising the <i>work</i>
    /// would mean doing database writes for addresses that have no account; equalising the <i>time</i>
    /// costs nothing but latency. So each of the three endpoints waits until this much time has passed
    /// before answering, and every path — free, taken, staff, unknown, locked — answers at the floor.
    /// <para>
    /// <b>250 ms</b>: about three times the slowest genuine path measured (registering a free address,
    /// ~77 ms, which is a PBKDF2 hash and four inserts), so ordinary load does not push a real path past
    /// it; short enough that a shopper does not notice it on a form they submit once. A path that ever
    /// exceeds the floor leaks its own time again, which is why the dummy hash stays — it keeps the
    /// expensive term equal even then. The delay is a timer, not work, so it costs no CPU.
    /// </para>
    /// <para>
    /// A setting so the rest of the integration suite can set it to zero rather than spend a quarter of a
    /// second on every sign-in it makes; the timing test runs at the real default.
    /// </para>
    /// </remarks>
    public int AnonymousResponseFloorMilliseconds { get; set; } = 250;

    /// <summary>
    /// Whether the customer auth cookie requires HTTPS (<c>Always</c> versus <c>SameAsRequest</c>).
    /// </summary>
    /// <remarks>
    /// True everywhere, for the reasons <see cref="AdminSecurityOptions.RequireSecureCookies"/> states at
    /// length: TLS terminates at Caddy and <c>UseForwardedHeaders</c> restores
    /// <c>Request.IsHttps</c>. Set <c>CustomerSecurity__RequireSecureCookies=false</c> only where there is
    /// no TLS anywhere in the chain — <c>appsettings.Development.json</c> already does, as it does for
    /// the admin cookie, because <c>CookieSecurePolicy.Always</c> otherwise means the cookie is never set
    /// at all and the sign-in silently does nothing.
    /// </remarks>
    public bool RequireSecureCookies { get; set; } = true;
}
