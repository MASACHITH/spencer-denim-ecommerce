namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// One staff user's detail (ADM-02). <see cref="UserId"/> comes straight from the route;
/// <see cref="ActorUserId"/> is the signed-in viewer, whose visibility of GrantsAll holders the handler decides.
/// </summary>
public sealed record GetStaffUserByIdQuery(int UserId, int ActorUserId);
