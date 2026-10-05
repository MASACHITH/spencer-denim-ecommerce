namespace SpencerDenim.Application.Admin.Users;

/// <summary>Deactivates or reactivates a staff user (ADM-02). <see cref="Active"/> is the desired new state.</summary>
public sealed record SetStaffUserActiveCommand(
    int ActorUserId,
    int TargetUserId,
    bool Active,
    string? IpAddress,
    string? UserAgent);
