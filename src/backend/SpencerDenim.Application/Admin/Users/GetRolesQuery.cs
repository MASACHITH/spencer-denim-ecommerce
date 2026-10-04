namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// The role list (ADM-02): four default roles today, no paging needed at this scale. <see cref="ActorUserId"/>
/// is the signed-in viewer, whose visibility of the GrantsAll role the handler decides.
/// </summary>
public sealed record GetRolesQuery(int ActorUserId);
