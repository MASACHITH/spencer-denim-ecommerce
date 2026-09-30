namespace SpencerDenim.Application.Admin.CurrentUser;

/// <summary>Query for <c>GET /api/admin/auth/whoami</c>: the signed-in admin plus their effective permissions.</summary>
/// <param name="UserId">Resolved by the endpoint from the authenticated cookie principal, never client input.</param>
public sealed record WhoAmIQuery(int UserId);

/// <summary>
/// Result of <see cref="WhoAmIQuery"/>. Null when the principal no longer resolves to a real user.
/// <see cref="MustChangePassword"/> and <see cref="TwoFactorEnrolmentRequired"/> are read from the database on every
/// call, so the admin app can send the user to the change-password page, then the 2FA enrolment page, at startup
/// as well as straight after sign-in (doc 11 P14 step 3; W1-10).
/// </summary>
/// <param name="TwoFactorEnabled">The account has an authenticator app confirmed (W1-10).</param>
/// <param name="TwoFactorEnrolmentRequired">
/// A GrantsAll holder without 2FA (doc 08 §3.1 N14): the API refuses everything but enrolment until they enrol.
/// </param>
public sealed record WhoAmIResponse(
    int UserId,
    string Email,
    string FirstName,
    string? LastName,
    bool GrantsAllPermissions,
    IReadOnlyCollection<string> Permissions,
    bool MustChangePassword,
    bool TwoFactorEnabled = false,
    bool TwoFactorEnrolmentRequired = false);
