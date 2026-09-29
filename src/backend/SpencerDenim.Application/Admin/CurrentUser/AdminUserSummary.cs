namespace SpencerDenim.Application.Admin.CurrentUser;

/// <summary>
/// Basic profile fields a staff user's principal is built from and displayed with, plus the account-state flags
/// the admin API gates on (<paramref name="MustChangePassword"/>: doc 08 §9, doc 11 P14 step 3).
/// <paramref name="SecurityStamp"/> is the current database value, compared with the stamp the session was
/// issued with so a session opened before a password change can never pass the admin gates. It is never
/// returned to a client.
/// </summary>
public sealed record AdminUserSummary(
    int UserId,
    string Email,
    string FirstName,
    string? LastName,
    bool IsActive,
    bool MustChangePassword,
    string? SecurityStamp);
