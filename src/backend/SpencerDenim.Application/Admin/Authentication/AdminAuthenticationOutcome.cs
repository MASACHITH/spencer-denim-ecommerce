namespace SpencerDenim.Application.Admin.Authentication;

public enum AdminAuthenticationStatus
{
    Succeeded,
    InvalidCredentials,
    LockedOut,

    /// <summary>The password was right, but the account has 2FA on: a code is needed before any session is issued (W1-10).</summary>
    TwoFactorRequired,

    /// <summary>Second step only: the authenticator or recovery code was wrong. Counts towards the lockout.</summary>
    InvalidTwoFactorCode,

    /// <summary>
    /// Second step only: the pending sign-in no longer matches the account (the password or 2FA changed in
    /// between, or the account was deactivated), so the user has to start again from the password.
    /// </summary>
    TwoFactorSessionExpired,
}

/// <summary>
/// Result of checking staff credentials (doc 08 §3.1 DB10 scoping rules; N14 lockout) and, for an account with
/// 2FA on, of the second sign-in step (W1-10). Produced by <see cref="IAdminAuthenticator"/>, which owns every
/// reason credentials can fail (unknown email, wrong password, non-staff account, inactive account, already/newly
/// locked out, wrong code) so the command handlers never have to special-case Identity themselves.
/// <paramref name="UserId"/> and <paramref name="Email"/> are populated whenever a user row was found — including
/// on failure — so the caller can still write a precise audit row.
/// <paramref name="MustChangePassword"/> is the account's <c>MustChangePassword</c> flag at sign-in (doc 08 §9,
/// doc 11 P14 step 3): the sign-in itself still succeeds, but the API refuses every other admin endpoint until
/// the password is changed (see <c>PasswordChangeCompletedRequirement</c>).
/// </summary>
/// <param name="SecurityStamp">
/// On <see cref="AdminAuthenticationStatus.Succeeded"/>, the stamp the session cookie is issued with. On
/// <see cref="AdminAuthenticationStatus.TwoFactorRequired"/>, the stamp the pending second step is tied to: if it
/// changes before the code arrives, the second step is refused.
/// </param>
/// <param name="TwoFactorEnabled">Whether the account has 2FA on (true on every second-step success).</param>
/// <param name="UsedRecoveryCode">Second step only: the code was a recovery code, now spent.</param>
/// <param name="RecoveryCodesLeft">Second step only: recovery codes still unused after this sign-in.</param>
public sealed record AdminAuthenticationOutcome(
    AdminAuthenticationStatus Status,
    int? UserId,
    string? Email,
    string? FirstName,
    string? LastName,
    bool GrantsAllPermissions,
    IReadOnlyCollection<string> Permissions,
    DateTimeOffset? LockoutEndUtc,
    bool WasNewlyLockedOut,
    string? SecurityStamp,
    bool MustChangePassword,
    bool TwoFactorEnabled = false,
    bool UsedRecoveryCode = false,
    int RecoveryCodesLeft = 0)
{
    public static AdminAuthenticationOutcome InvalidCredentials(int? userId, string? email) =>
        new(AdminAuthenticationStatus.InvalidCredentials, userId, email, null, null, false, [], null, false, null, false);

    public static AdminAuthenticationOutcome LockedOut(
        int userId,
        string email,
        DateTimeOffset lockoutEndUtc,
        bool wasNewlyLockedOut,
        bool usedRecoveryCode = false) =>
        new(AdminAuthenticationStatus.LockedOut, userId, email, null, null, false, [], lockoutEndUtc, wasNewlyLockedOut, null, false, UsedRecoveryCode: usedRecoveryCode);

    public static AdminAuthenticationOutcome TwoFactorRequired(int userId, string email, string securityStamp) =>
        new(AdminAuthenticationStatus.TwoFactorRequired, userId, email, null, null, false, [], null, false, securityStamp, false, TwoFactorEnabled: true);

    public static AdminAuthenticationOutcome InvalidTwoFactorCode(int userId, string? email, bool usedRecoveryCode) =>
        new(AdminAuthenticationStatus.InvalidTwoFactorCode, userId, email, null, null, false, [], null, false, null, false, TwoFactorEnabled: true, UsedRecoveryCode: usedRecoveryCode);

    public static AdminAuthenticationOutcome TwoFactorSessionExpired(int userId, string? email) =>
        new(AdminAuthenticationStatus.TwoFactorSessionExpired, userId, email, null, null, false, [], null, false, null, false);

    public static AdminAuthenticationOutcome Succeeded(
        int userId,
        string email,
        string firstName,
        string? lastName,
        bool grantsAllPermissions,
        IReadOnlyCollection<string> permissions,
        string securityStamp,
        bool mustChangePassword,
        bool twoFactorEnabled = false,
        bool usedRecoveryCode = false,
        int recoveryCodesLeft = 0) =>
        new(
            AdminAuthenticationStatus.Succeeded,
            userId,
            email,
            firstName,
            lastName,
            grantsAllPermissions,
            permissions,
            null,
            false,
            securityStamp,
            mustChangePassword,
            twoFactorEnabled,
            usedRecoveryCode,
            recoveryCodesLeft);
}
