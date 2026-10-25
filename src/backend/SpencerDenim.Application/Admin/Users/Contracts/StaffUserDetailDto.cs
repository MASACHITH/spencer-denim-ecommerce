namespace SpencerDenim.Application.Admin.Users.Contracts;

/// <summary>
/// One staff user's detail view (ADM-02), including the effective permissions computed from their roles
/// (doc 08 §3.1 R4) — shown so an admin can see exactly what a user can do without cross-referencing roles by
/// hand, and so the frontend can disable actions the *viewer* (not necessarily this user) could not perform.
/// <paramref name="LockedForViewer"/> is as on <see cref="StaffUserSummaryDto"/>.
/// </summary>
public sealed record StaffUserDetailDto(
    int Id,
    string Email,
    string FirstName,
    string? LastName,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LastLoginAtUtc,
    DateTime CreatedAtUtc,
    bool GrantsAllPermissions,
    IReadOnlyList<RoleRefDto> Roles,
    IReadOnlyList<string> EffectivePermissions,
    bool IsProtected,
    bool LockedForViewer = false);
