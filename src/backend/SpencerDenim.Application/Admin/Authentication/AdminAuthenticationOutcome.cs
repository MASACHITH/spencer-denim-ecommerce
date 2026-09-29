namespace SpencerDenim.Application.Admin.Authentication;

public enum AdminAuthenticationStatus
{
    Succeeded,
    InvalidCredentials,
    LockedOut,
}

/// <summary>
/// Result of checking staff credentials (doc 08 §3.1 DB10 scoping rules; N14 lockout). Produced by
/// <see cref="IAdminAuthenticator"/>, which owns every reason credentials can fail (unknown email, wrong
/// password, non-staff account, inactive account, already/newly locked out) so the command handler never has
/// to special-case Identity itself. <paramref name="UserId"/> and <paramref name="Email"/> are populated
/// whenever a user row was found — including on failure — so the caller can still write a precise audit row.
/// <paramref name="MustChangePassword"/> is the account's <c>MustChangePassword</c> flag at sign-in (doc 08 §9,
/// doc 11 P14 step 3): the sign-in itself still succeeds, but the API refuses every other admin endpoint until
/// the password is changed (see <c>PasswordChangeCompletedRequirement</c>).
/// </summary>
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
    bool MustChangePassword)
{
    public static AdminAuthenticationOutcome InvalidCredentials(int? userId, string? email) =>
        new(AdminAuthenticationStatus.InvalidCredentials, userId, email, null, null, false, [], null, false, null, false);

    public static AdminAuthenticationOutcome LockedOut(int userId, string email, DateTimeOffset lockoutEndUtc, bool wasNewlyLockedOut) =>
        new(AdminAuthenticationStatus.LockedOut, userId, email, null, null, false, [], lockoutEndUtc, wasNewlyLockedOut, null, false);

    public static AdminAuthenticationOutcome Succeeded(
        int userId,
        string email,
        string firstName,
        string? lastName,
        bool grantsAllPermissions,
        IReadOnlyCollection<string> permissions,
        string securityStamp,
        bool mustChangePassword) =>
        new(AdminAuthenticationStatus.Succeeded, userId, email, firstName, lastName, grantsAllPermissions, permissions, null, false, securityStamp, mustChangePassword);
}
