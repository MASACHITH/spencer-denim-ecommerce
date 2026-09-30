namespace SpencerDenim.Application.Admin.Account;

public enum AdminPasswordChangeStatus
{
    Succeeded,

    /// <summary>The account no longer exists, is not a staff account, or has been deactivated.</summary>
    AccountUnavailable,

    InvalidCurrentPassword,

    LockedOut,

    /// <summary>The current password was right, but the new one fails the password policy.</summary>
    NewPasswordRejected,
}

/// <summary>
/// Result of <see cref="IAdminPasswordChanger"/>. Like <c>AdminAuthenticationOutcome</c>, <see cref="Email"/> is
/// populated whenever the account was found, so the handler can write a precise audit row on failure too.
/// </summary>
/// <param name="SecurityStamp">
/// The rotated stamp after a successful change. The endpoint re-issues the caller's cookie with it, so this
/// session survives while every other session for the account fails its next revalidation.
/// </param>
/// <param name="ClearedMustChangePassword">True when this change cleared a pending forced change (doc 08 §9).</param>
/// <param name="PolicyErrors">The password-policy messages, for <see cref="AdminPasswordChangeStatus.NewPasswordRejected"/>.</param>
public sealed record AdminPasswordChangeOutcome(
    AdminPasswordChangeStatus Status,
    int UserId,
    string? Email,
    string? FirstName,
    string? LastName,
    string? SecurityStamp,
    bool ClearedMustChangePassword,
    DateTimeOffset? LockoutEndUtc,
    bool WasNewlyLockedOut,
    IReadOnlyList<string> PolicyErrors)
{
    public static AdminPasswordChangeOutcome AccountUnavailable(int userId) =>
        new(AdminPasswordChangeStatus.AccountUnavailable, userId, null, null, null, null, false, null, false, []);

    public static AdminPasswordChangeOutcome InvalidCurrentPassword(int userId, string? email) =>
        new(AdminPasswordChangeStatus.InvalidCurrentPassword, userId, email, null, null, null, false, null, false, []);

    public static AdminPasswordChangeOutcome LockedOut(int userId, string? email, DateTimeOffset lockoutEndUtc, bool wasNewlyLockedOut) =>
        new(AdminPasswordChangeStatus.LockedOut, userId, email, null, null, null, false, lockoutEndUtc, wasNewlyLockedOut, []);

    public static AdminPasswordChangeOutcome NewPasswordRejected(int userId, string? email, IReadOnlyList<string> policyErrors) =>
        new(AdminPasswordChangeStatus.NewPasswordRejected, userId, email, null, null, null, false, null, false, policyErrors);

    public static AdminPasswordChangeOutcome Succeeded(
        int userId,
        string email,
        string firstName,
        string? lastName,
        string securityStamp,
        bool clearedMustChangePassword) =>
        new(AdminPasswordChangeStatus.Succeeded, userId, email, firstName, lastName, securityStamp, clearedMustChangePassword, null, false, []);
}
