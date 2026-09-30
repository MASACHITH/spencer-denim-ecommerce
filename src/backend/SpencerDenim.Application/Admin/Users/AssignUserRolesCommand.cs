namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Replaces a staff user's entire role assignment with <see cref="RoleIds"/> (ADM-02). Sending the full
/// desired set, rather than separate assign/unassign calls, is what lets the handler evaluate the doc 08
/// §3.1 DB4 rule 2 subset check against one clean before/after pair instead of reasoning about a sequence of
/// incremental edits.
/// </summary>
public sealed record AssignUserRolesCommand(
    int ActorUserId,
    int TargetUserId,
    IReadOnlyList<int> RoleIds,
    string? IpAddress,
    string? UserAgent);
