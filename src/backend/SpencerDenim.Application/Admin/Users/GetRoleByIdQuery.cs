namespace SpencerDenim.Application.Admin.Users;

/// <summary>One role's permission set (ADM-02). <see cref="RoleId"/> comes straight from the route.</summary>
public sealed record GetRoleByIdQuery(int RoleId);
