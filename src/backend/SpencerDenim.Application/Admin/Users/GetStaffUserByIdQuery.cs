namespace SpencerDenim.Application.Admin.Users;

/// <summary>One staff user's detail (ADM-02). <see cref="UserId"/> comes straight from the route.</summary>
public sealed record GetStaffUserByIdQuery(int UserId);
