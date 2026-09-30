namespace SpencerDenim.Application.Admin.Account;

// W1-10: the signed-in staff user's own 2FA. In every command UserId is resolved by the endpoint from the
// authenticated cookie principal, never client input; codes and passwords are never logged or audited.

/// <summary>Query for <c>GET /api/admin/auth/two-factor</c>.</summary>
public sealed record GetAdminTwoFactorStatusQuery(int UserId);

/// <summary>Command for <c>POST /api/admin/auth/two-factor/setup</c>: start or resume enrolment.</summary>
public sealed record BeginAdminTwoFactorSetupCommand(int UserId);

/// <summary>Command for <c>POST /api/admin/auth/two-factor/enable</c>: confirm enrolment with a code from the app.</summary>
public sealed record EnableAdminTwoFactorCommand(int UserId, string Code, string? IpAddress, string? UserAgent);

/// <summary>Command for <c>POST /api/admin/auth/two-factor/recovery-codes</c>: replace the recovery codes.</summary>
public sealed record RegenerateAdminRecoveryCodesCommand(int UserId, string CurrentPassword, string? IpAddress, string? UserAgent);

/// <summary>Command for <c>POST /api/admin/auth/two-factor/disable</c>: turn 2FA off (never for GrantsAll holders).</summary>
public sealed record DisableAdminTwoFactorCommand(int UserId, string CurrentPassword, string? IpAddress, string? UserAgent);

/// <summary>
/// Result of <see cref="GetAdminTwoFactorStatusQuery"/>; null when the principal no longer resolves to an active
/// staff user.
/// </summary>
/// <param name="Required">2FA is mandatory for this account (a GrantsAll holder, doc 08 §3.1 N14).</param>
/// <param name="CanDisable">The user may turn it off themselves (it is on, and not mandatory).</param>
public sealed record AdminTwoFactorStatusResponse(bool Enabled, bool Required, bool CanDisable, int RecoveryCodesLeft);
