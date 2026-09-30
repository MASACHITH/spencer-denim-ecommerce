namespace SpencerDenim.Application.Admin.Account;

public enum AdminTwoFactorStatus
{
    Succeeded,

    /// <summary>The account no longer exists, is not a staff account, or has been deactivated.</summary>
    AccountUnavailable,

    /// <summary>Enrolment was asked for, but 2FA is already on.</summary>
    AlreadyEnabled,

    /// <summary>A change that needs 2FA on (new recovery codes, turning it off) was asked for while it is off.</summary>
    NotEnabled,

    /// <summary>A code was sent to confirm enrolment before any key was issued.</summary>
    NotSetUp,

    /// <summary>The authenticator code did not match.</summary>
    InvalidCode,

    InvalidCurrentPassword,

    LockedOut,
}

/// <summary>
/// Result of an <see cref="IAdminTwoFactorManager"/> change. Like <c>AdminPasswordChangeOutcome</c>,
/// <see cref="Email"/> is populated whenever the account was found, so the handler can write a precise audit row.
/// </summary>
/// <param name="SecurityStamp">
/// The account's stamp after the change. When the change rotated it, the endpoint re-issues the caller's cookie
/// with it, so this session carries on while every other session for the account is refused (doc 08 §3.1 DB10).
/// </param>
/// <param name="SharedKey">Enrolment only: the authenticator key, formatted in groups of four for typing in.</param>
/// <param name="AuthenticatorUri">Enrolment only: the <c>otpauth://</c> URI the QR code encodes.</param>
/// <param name="RecoveryCodes">New recovery codes, returned exactly once and never stored in clear anywhere else.</param>
public sealed record AdminTwoFactorOutcome(
    AdminTwoFactorStatus Status,
    int UserId,
    string? Email,
    string? FirstName,
    string? LastName,
    string? SecurityStamp,
    string? SharedKey,
    string? AuthenticatorUri,
    IReadOnlyList<string> RecoveryCodes,
    DateTimeOffset? LockoutEndUtc,
    bool WasNewlyLockedOut)
{
    public static AdminTwoFactorOutcome Failed(AdminTwoFactorStatus status, int userId, string? email) =>
        new(status, userId, email, null, null, null, null, null, [], null, false);

    public static AdminTwoFactorOutcome LockedOut(int userId, string? email, DateTimeOffset lockoutEndUtc, bool wasNewlyLockedOut) =>
        new(AdminTwoFactorStatus.LockedOut, userId, email, null, null, null, null, null, [], lockoutEndUtc, wasNewlyLockedOut);

    public static AdminTwoFactorOutcome Succeeded(
        int userId,
        string email,
        string firstName,
        string? lastName,
        string securityStamp,
        string? sharedKey = null,
        string? authenticatorUri = null,
        IReadOnlyList<string>? recoveryCodes = null) =>
        new(AdminTwoFactorStatus.Succeeded, userId, email, firstName, lastName, securityStamp, sharedKey, authenticatorUri, recoveryCodes ?? [], null, false);
}
