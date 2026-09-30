namespace SpencerDenim.Application.Admin.Authentication;

/// <summary>
/// Command for <c>POST /api/admin/auth/login/two-factor</c> (W1-10): the second sign-in step for a staff account
/// with 2FA on. <paramref name="UserId"/>, <paramref name="PendingSecurityStamp"/> and
/// <paramref name="PendingSignInId"/> come from the short-lived pending cookie the password step issued, never from
/// the request body.
/// </summary>
/// <param name="IsRecoveryCode">The code is one of the account's single-use recovery codes, not an authenticator code.</param>
public sealed record CompleteAdminTwoFactorSignInCommand(
    int UserId,
    string PendingSecurityStamp,
    string PendingSignInId,
    string Code,
    bool IsRecoveryCode,
    string? IpAddress,
    string? UserAgent);
