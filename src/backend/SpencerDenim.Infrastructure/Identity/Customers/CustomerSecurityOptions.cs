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
