namespace SpencerDenim.Application.Admin.Account;

/// <summary>
/// Command for <c>POST /api/admin/auth/change-password</c>: the signed-in staff user changes their own password
/// (issue #17; doc 11 P14 step 3 — the forced change on first sign-in, and any voluntary change).
/// </summary>
/// <param name="UserId">Resolved by the endpoint from the authenticated cookie principal, never client input.</param>
public sealed record ChangeAdminPasswordCommand(
    int UserId,
    string CurrentPassword,
    string NewPassword,
    string? IpAddress,
    string? UserAgent);
