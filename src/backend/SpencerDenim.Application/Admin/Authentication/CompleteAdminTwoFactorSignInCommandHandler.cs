using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Admin.Authentication;

/// <summary>
/// Orchestrates the second sign-in step (W1-10) and writes its audit trail: <c>Login</c> once the code is
/// accepted (plus <c>RecoveryCodeUsed</c> when it was a recovery code), <c>TwoFactorFailed</c> for a wrong code,
/// and <c>Lockout</c> on the attempt that trips the limit — the same shape as the password step (doc 11 P14
/// "every sign-in attempt is audited"). The code itself is never written anywhere (doc 08 §3.8).
/// </summary>
public sealed class CompleteAdminTwoFactorSignInCommandHandler(IAdminAuthenticator authenticator, IAuditLogWriter auditLogWriter, IClock clock)
    : ICommandHandler<CompleteAdminTwoFactorSignInCommand, AdminLoginResult>
{
    private const string EntityType = "User";

    public async Task<AdminLoginResult> HandleAsync(CompleteAdminTwoFactorSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var outcome = await authenticator.CompleteTwoFactorSignInAsync(
            command.UserId,
            command.PendingSecurityStamp,
            command.PendingSignInId,
            command.Code,
            command.IsRecoveryCode,
            cancellationToken);

        switch (outcome.Status)
        {
            case AdminAuthenticationStatus.Succeeded:
                await WriteAuditAsync(AuditActions.Login, outcome, command, MethodJson(outcome), cancellationToken);
                if (outcome.UsedRecoveryCode)
                {
                    var remaining = JsonSerializer.Serialize(new { recoveryCodesLeft = outcome.RecoveryCodesLeft });
                    await WriteAuditAsync(AuditActions.RecoveryCodeUsed, outcome, command, remaining, cancellationToken);
                }

                return new AdminLoginResult.Success(
                    outcome.UserId!.Value,
                    outcome.Email!,
                    outcome.FirstName!,
                    outcome.LastName,
                    outcome.GrantsAllPermissions,
                    outcome.Permissions,
                    outcome.SecurityStamp!,
                    outcome.MustChangePassword,
                    TwoFactorEnabled: true);

            case AdminAuthenticationStatus.InvalidTwoFactorCode:
                await WriteAuditAsync(AuditActions.TwoFactorFailed, outcome, command, FailureJson(outcome, "InvalidCode"), cancellationToken);
                return new AdminLoginResult.InvalidTwoFactorCode();

            case AdminAuthenticationStatus.LockedOut:
                // Written for every refused attempt, including those turned away because the account was already
                // locked: the audit log is where "who is being attacked" shows (doc 11 P14).
                await WriteAuditAsync(AuditActions.TwoFactorFailed, outcome, command, FailureJson(outcome, "LockedOut"), cancellationToken);
                if (outcome.WasNewlyLockedOut)
                {
                    await WriteAuditAsync(AuditActions.Lockout, outcome, command, null, cancellationToken);
                }

                return new AdminLoginResult.LockedOut(outcome.LockoutEndUtc!.Value);

            case AdminAuthenticationStatus.TwoFactorSessionExpired:
            default:
                // No code was checked, so there is nothing to audit: the user simply starts again from the password.
                return new AdminLoginResult.TwoFactorSessionExpired();
        }
    }

    private static string MethodJson(AdminAuthenticationOutcome outcome) =>
        JsonSerializer.Serialize(new { twoFactorMethod = MethodName(outcome) });

    private static string FailureJson(AdminAuthenticationOutcome outcome, string reason) =>
        JsonSerializer.Serialize(new { reason, step = "SignIn", twoFactorMethod = MethodName(outcome) });

    private static string MethodName(AdminAuthenticationOutcome outcome) =>
        outcome.UsedRecoveryCode ? "RecoveryCode" : "Authenticator";

    private Task WriteAuditAsync(
        string action,
        AdminAuthenticationOutcome outcome,
        CompleteAdminTwoFactorSignInCommand command,
        string? afterJson,
        CancellationToken cancellationToken)
    {
        var entry = new AuditLog
        {
            OccurredAtUtc = clock.UtcNow.UtcDateTime,
            UserId = command.UserId,
            UserEmail = outcome.Email,
            Action = action,
            EntityType = EntityType,
            EntityId = command.UserId.ToString(CultureInfo.InvariantCulture),
            AfterJson = afterJson,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
        };

        return auditLogWriter.WriteAsync(entry, cancellationToken);
    }
}
