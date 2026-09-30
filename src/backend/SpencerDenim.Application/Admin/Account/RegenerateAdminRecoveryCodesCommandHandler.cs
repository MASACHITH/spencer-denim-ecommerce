using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Admin.Account;

/// <summary>
/// Replaces the signed-in user's recovery codes after re-checking their password (W1-10). Audited as
/// <c>RecoveryCodesRegenerated</c>; a wrong password as <c>TwoFactorFailed</c>, plus <c>Lockout</c> on the attempt
/// that trips the limit.
/// </summary>
public sealed class RegenerateAdminRecoveryCodesCommandHandler(IAdminTwoFactorManager twoFactorManager, IAuditLogWriter auditLogWriter, IClock clock)
    : ICommandHandler<RegenerateAdminRecoveryCodesCommand, AdminTwoFactorResult>
{
    private const string Step = "RegenerateRecoveryCodes";

    private readonly AdminAccountAudit _audit = new(auditLogWriter, clock);

    public async Task<AdminTwoFactorResult> HandleAsync(RegenerateAdminRecoveryCodesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var outcome = await twoFactorManager.RegenerateRecoveryCodesAsync(command.UserId, command.CurrentPassword, cancellationToken);

        switch (outcome.Status)
        {
            case AdminTwoFactorStatus.Succeeded:
                await _audit.WriteAsync(
                    AuditActions.RecoveryCodesRegenerated, outcome, command.IpAddress, command.UserAgent, null, null, cancellationToken);
                return new AdminTwoFactorResult.RecoveryCodesRegenerated(outcome.RecoveryCodes);

            case AdminTwoFactorStatus.InvalidCurrentPassword:
            case AdminTwoFactorStatus.LockedOut:
                return await _audit.WritePasswordFailureAsync(outcome, Step, command.IpAddress, command.UserAgent, cancellationToken);

            case AdminTwoFactorStatus.NotEnabled:
                return new AdminTwoFactorResult.NotEnabled();

            default:
                return new AdminTwoFactorResult.AccountUnavailable();
        }
    }
}
