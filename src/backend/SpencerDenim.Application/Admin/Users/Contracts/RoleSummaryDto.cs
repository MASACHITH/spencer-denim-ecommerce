namespace SpencerDenim.Application.Admin.Users.Contracts;

/// <summary>One row of the role list (ADM-02, read-only in this slice — see <c>AppRole</c>'s remarks).</summary>
public sealed record RoleSummaryDto(
    int Id,
    string Name,
    string? Description,
    bool IsSystem,
    bool GrantsAllPermissions,
    int PermissionCount,
    int UserCount);
