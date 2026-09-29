namespace SpencerDenim.Application.Admin.Account;

/// <summary>
/// Port that manages a staff user's own two-factor authentication through ASP.NET Core Identity (W1-10; doc 08
/// §3.1 N14): the authenticator key and recovery codes live in <c>identity.UserTokens</c> and are never returned
/// except where noted, never logged and never audited (doc 08 §3.8). The implementation owns every Identity detail
/// (key generation, code checks, stamp rotation, the lockout bookkeeping of the current-password checks); who may
/// do what is decided by the command handlers. It never touches the cookie — re-issuing the caller's session after
/// a stamp rotation is the Api endpoint's job, as it is for sign-in.
/// </summary>
public interface IAdminTwoFactorManager
{
    /// <summary>Whether 2FA is on and how many recovery codes are left; null when the account is unavailable.</summary>
    Task<AdminTwoFactorState?> GetStateAsync(int userId, CancellationToken cancellationToken);

    /// <summary>
    /// Starts (or resumes) enrolment while 2FA is off: returns the authenticator key to scan, creating one first if
    /// the account has none. Creating a key rotates the security stamp (Identity's own behaviour).
    /// </summary>
    Task<AdminTwoFactorOutcome> BeginSetupAsync(int userId, CancellationToken cancellationToken);

    /// <summary>Confirms enrolment with a code from the app: turns 2FA on and issues fresh recovery codes.</summary>
    Task<AdminTwoFactorOutcome> EnableAsync(int userId, string code, CancellationToken cancellationToken);

    /// <summary>Replaces the recovery codes after checking the current password (same lockout as sign-in).</summary>
    Task<AdminTwoFactorOutcome> RegenerateRecoveryCodesAsync(int userId, string currentPassword, CancellationToken cancellationToken);

    /// <summary>
    /// Turns 2FA off after checking the current password, and discards the key and recovery codes so turning it on
    /// again needs a new scan. The caller must already have refused GrantsAll holders.
    /// </summary>
    Task<AdminTwoFactorOutcome> DisableAsync(int userId, string currentPassword, CancellationToken cancellationToken);
}

/// <summary>A staff user's 2FA state (W1-10).</summary>
public sealed record AdminTwoFactorState(int UserId, string? Email, bool Enabled, int RecoveryCodesLeft);
