namespace SpencerDenim.Application.Admin.CurrentUser;

/// <summary>
/// Basic profile fields a staff user's principal is built from and displayed with, plus the account-state flags
/// the admin API gates on (<paramref name="MustChangePassword"/>: doc 08 §9, doc 11 P14 step 3).
/// </summary>
public sealed record AdminUserSummary(
    int UserId,
    string Email,
    string FirstName,
    string? LastName,
    bool IsActive,
    bool MustChangePassword);
