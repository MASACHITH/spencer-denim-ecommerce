namespace SpencerDenim.Application.Admin.Users.Contracts;

/// <summary>
/// One row of the staff user list (ADM-02). <paramref name="IsProtected"/> is true for an account named in
/// <c>AdminSecurity__ProtectedStaffEmails</c>: the screen offers no Deactivate button for it.
/// <paramref name="LockedForViewer"/> is true for a guarded account (<c>AdminSecurity__GuardedStaffEmails</c>) when
/// the person looking is not a protected account: they cannot deactivate, reactivate or rename it, so the screen
/// offers no button for it. Set by the query handler, which knows who is looking; the reader leaves it false.
/// </summary>
public sealed record StaffUserSummaryDto(
    int Id,
    string Email,
    string FirstName,
    string? LastName,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LastLoginAtUtc,
    IReadOnlyList<RoleRefDto> Roles,
    bool IsProtected,
    bool LockedForViewer = false);
