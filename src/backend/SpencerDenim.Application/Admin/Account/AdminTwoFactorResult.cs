namespace SpencerDenim.Application.Admin.Account;

/// <summary>
/// The fields the admin cookie principal is re-issued from after a change rotated the account's security stamp
/// (same claims as sign-in, new <see cref="SecurityStamp"/>).
/// </summary>
public sealed record ReissuedAdminSession(int UserId, string Email, string FirstName, string? LastName, string SecurityStamp);

/// <summary>Result of the W1-10 2FA management commands, returned to the Api endpoints.</summary>
public abstract record AdminTwoFactorResult
{
    private AdminTwoFactorResult()
    {
    }

    /// <summary>
    /// Enrolment started or resumed: the key to scan. <see cref="Session"/> is set when a new key was created,
    /// which rotated the stamp, so the endpoint must re-issue the caller's cookie.
    /// </summary>
    public sealed record SetupStarted(string SharedKey, string AuthenticatorUri, ReissuedAdminSession Session) : AdminTwoFactorResult;

    /// <summary>2FA is on. <see cref="RecoveryCodes"/> are shown to the user once and never again.</summary>
    public sealed record Enabled(IReadOnlyList<string> RecoveryCodes, ReissuedAdminSession Session) : AdminTwoFactorResult;

    /// <summary>New recovery codes; the old ones no longer work.</summary>
    public sealed record RecoveryCodesRegenerated(IReadOnlyList<string> RecoveryCodes) : AdminTwoFactorResult;

    /// <summary>2FA is off; the stamp was rotated, so the endpoint re-issues the caller's cookie.</summary>
    public sealed record Disabled(ReissuedAdminSession Session) : AdminTwoFactorResult;

    public sealed record InvalidCode : AdminTwoFactorResult;

    public sealed record InvalidCurrentPassword : AdminTwoFactorResult;

    /// <summary>Doc 08 §3.1 N14: the same time-boxed lockout as sign-in.</summary>
    public sealed record LockedOut(DateTimeOffset LockoutEndUtc) : AdminTwoFactorResult;

    public sealed record AlreadyEnabled : AdminTwoFactorResult;

    public sealed record NotEnabled : AdminTwoFactorResult;

    public sealed record NotSetUp : AdminTwoFactorResult;

    /// <summary>A GrantsAll holder tried to turn 2FA off (doc 08 §3.1 N14: mandatory for them).</summary>
    public sealed record RequiredForRole : AdminTwoFactorResult;

    /// <summary>The account behind the session is gone or deactivated: the endpoint ends the session.</summary>
    public sealed record AccountUnavailable : AdminTwoFactorResult;
}
