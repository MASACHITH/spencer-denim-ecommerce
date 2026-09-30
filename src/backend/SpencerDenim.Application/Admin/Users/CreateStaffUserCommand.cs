namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Creates one staff user with an initial role set (ADM-02). <see cref="ActorUserId"/> is resolved by the
/// endpoint from the authenticated cookie principal, never client input (same convention as
/// <c>ChangeAdminPasswordCommand</c>). The new account is created with <c>MustChangePassword = true</c>,
/// exactly like the seeded bootstrap Super Admin (issue #17) — the admin who creates the account chooses a
/// temporary password and hands it over out of band; there is no email-delivery infrastructure in this
/// codebase yet to send it automatically.
/// </summary>
public sealed record CreateStaffUserCommand(
    int ActorUserId,
    string Email,
    string FirstName,
    string? LastName,
    string InitialPassword,
    IReadOnlyList<int> RoleIds,
    string? IpAddress,
    string? UserAgent);
