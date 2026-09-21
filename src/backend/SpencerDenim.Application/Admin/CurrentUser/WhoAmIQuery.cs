namespace SpencerDenim.Application.Admin.CurrentUser;

/// <summary>Query for <c>GET /api/admin/auth/whoami</c>: the signed-in admin plus their effective permissions.</summary>
/// <param name="UserId">Resolved by the endpoint from the authenticated cookie principal, never client input.</param>
public sealed record WhoAmIQuery(int UserId);

/// <summary>Result of <see cref="WhoAmIQuery"/>. Null when the principal no longer resolves to a real user.</summary>
public sealed record WhoAmIResponse(
    int UserId,
    string Email,
    string FirstName,
    string? LastName,
    bool GrantsAllPermissions,
    IReadOnlyCollection<string> Permissions);
