using SpencerDenim.Application.Abstractions.Messaging;

namespace SpencerDenim.Application.Admin.Account;

/// <summary>
/// Starts or resumes enrolment (W1-10): hands back the authenticator key to scan. Nothing is audited here — no
/// security setting changes until the user confirms a code (<see cref="EnableAdminTwoFactorCommandHandler"/>).
/// </summary>
public sealed class BeginAdminTwoFactorSetupCommandHandler(IAdminTwoFactorManager twoFactorManager)
    : ICommandHandler<BeginAdminTwoFactorSetupCommand, AdminTwoFactorResult>
{
    public async Task<AdminTwoFactorResult> HandleAsync(BeginAdminTwoFactorSetupCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var outcome = await twoFactorManager.BeginSetupAsync(command.UserId, cancellationToken);

        return outcome.Status switch
        {
            AdminTwoFactorStatus.Succeeded => new AdminTwoFactorResult.SetupStarted(
                outcome.SharedKey!,
                outcome.AuthenticatorUri!,
                AdminAccountAudit.SessionFrom(outcome)),
            AdminTwoFactorStatus.AlreadyEnabled => new AdminTwoFactorResult.AlreadyEnabled(),
            _ => new AdminTwoFactorResult.AccountUnavailable(),
        };
    }
}
