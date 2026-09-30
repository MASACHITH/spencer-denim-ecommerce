namespace SpencerDenim.Application.Admin.Users.Contracts;

/// <summary>One role's full permission set (ADM-02).</summary>
public sealed record RoleDetailDto(
    int Id,
    string Name,
    string? Description,
    bool IsSystem,
    bool GrantsAllPermissions,
    IReadOnlyList<string> Permissions,
    int UserCount);
