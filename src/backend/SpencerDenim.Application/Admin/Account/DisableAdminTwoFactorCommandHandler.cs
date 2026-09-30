using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Application.Admin.Account;

/// <summary>
/// Turns the signed-in user's 2FA off after re-checking their password (W1-10). Refused outright for GrantsAll
/// holders, for whom it is mandatory (doc 08 §3.1 N14) — read from their roles in the database, never the cookie.
/// Audited as <c>TwoFactorDisabled</c>; a wrong password as <c>TwoFactorFailed</c> (+ <c>Lockout</c>).
/// </summary>
public sealed class DisableAdminTwoFactorCommandHandler(
    IAdminTwoFactorManager twoFactorManager,
    IEffectivePermissionsProvider permissionsProvider,
    IAuditLogWriter auditLogWriter,
    IClock clock)
    : ICommandHandler<DisableAdminTwoFactorCommand, AdminTwoFactorResult>
{
    private const string Step = "DisableTwoFactor";

    private readonly AdminAccountAudit _audit = new(auditLogWriter, clock);

    public async Task<AdminTwoFactorResult> HandleAsync(DisableAdminTwoFactorCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(command.UserId, cancellationToken);
        if (!TwoFactorPolicy.CanDisable(permissions.GrantsAllPermissions))
        {
            return new AdminTwoFactorResult.RequiredForRole();
        }

        var outcome = await twoFactorManager.DisableAsync(command.UserId, command.CurrentPassword, cancellationToken);

        switch (outcome.Status)
        {
            case AdminTwoFactorStatus.Succeeded:
                await _audit.WriteAsync(
                    AuditActions.TwoFactorDisabled,
                    outcome,
                    command.IpAddress,
                    command.UserAgent,
                    AdminAccountAudit.TwoFactorEnabledJson(true),
                    AdminAccountAudit.TwoFactorEnabledJson(false),
                    cancellationToken);
                return new AdminTwoFactorResult.Disabled(AdminAccountAudit.SessionFrom(outcome));

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
