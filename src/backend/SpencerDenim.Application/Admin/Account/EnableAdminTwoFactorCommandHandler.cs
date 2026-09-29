using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Admin.Account;

/// <summary>
/// Confirms enrolment with a code from the authenticator app (W1-10) and writes <c>TwoFactorEnabled</c>. A wrong
/// code here is neither audited nor counted towards the lockout: the caller already holds a full session and has
/// just been shown the key, so it is a typing problem, not a guessing attack.
/// </summary>
public sealed class EnableAdminTwoFactorCommandHandler(IAdminTwoFactorManager twoFactorManager, IAuditLogWriter auditLogWriter, IClock clock)
    : ICommandHandler<EnableAdminTwoFactorCommand, AdminTwoFactorResult>
{
    private readonly AdminAccountAudit _audit = new(auditLogWriter, clock);

    public async Task<AdminTwoFactorResult> HandleAsync(EnableAdminTwoFactorCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var outcome = await twoFactorManager.EnableAsync(command.UserId, command.Code, cancellationToken);

        switch (outcome.Status)
        {
            case AdminTwoFactorStatus.Succeeded:
                // Only the flag is diffed: the key and the recovery codes are never logged (doc 08 §3.8).
                await _audit.WriteAsync(
                    AuditActions.TwoFactorEnabled,
                    outcome,
                    command.IpAddress,
                    command.UserAgent,
                    AdminAccountAudit.TwoFactorEnabledJson(false),
                    AdminAccountAudit.TwoFactorEnabledJson(true),
                    cancellationToken);
                return new AdminTwoFactorResult.Enabled(outcome.RecoveryCodes, AdminAccountAudit.SessionFrom(outcome));

            case AdminTwoFactorStatus.InvalidCode:
                return new AdminTwoFactorResult.InvalidCode();

            case AdminTwoFactorStatus.AlreadyEnabled:
                return new AdminTwoFactorResult.AlreadyEnabled();

            case AdminTwoFactorStatus.NotSetUp:
                return new AdminTwoFactorResult.NotSetUp();

            default:
                return new AdminTwoFactorResult.AccountUnavailable();
        }
    }
}
