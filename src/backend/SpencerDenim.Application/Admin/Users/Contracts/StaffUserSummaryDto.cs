namespace SpencerDenim.Application.Admin.Users.Contracts;

/// <summary>One row of the staff user list (ADM-02).</summary>
public sealed record StaffUserSummaryDto(
    int Id,
    string Email,
    string FirstName,
    string? LastName,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LastLoginAtUtc,
    IReadOnlyList<RoleRefDto> Roles);
