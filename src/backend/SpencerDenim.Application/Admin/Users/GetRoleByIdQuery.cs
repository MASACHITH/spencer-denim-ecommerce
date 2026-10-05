namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// One role's permission set (ADM-02). <see cref="RoleId"/> comes straight from the route;
/// <see cref="ActorUserId"/> is the signed-in viewer, whose visibility of the GrantsAll role the handler decides.
/// </summary>
public sealed record GetRoleByIdQuery(int RoleId, int ActorUserId);
