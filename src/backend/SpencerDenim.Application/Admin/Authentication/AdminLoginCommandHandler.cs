using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Admin.Authentication;

/// <summary>
/// Orchestrates an admin login attempt: checks credentials through <see cref="IAdminAuthenticator"/> and writes
/// the doc 08 §3.8 audit trail (<c>Login</c> / <c>LoginFailed</c> / <c>Lockout</c>). Building the sign-in cookie
/// itself is left to the Api endpoint (see <see cref="AdminLoginResult.Success"/>).
/// </summary>
public sealed class AdminLoginCommandHandler(IAdminAuthenticator authenticator, IAuditLogWriter auditLogWriter, IClock clock)
    : ICommandHandler<AdminLoginCommand, AdminLoginResult>
{
    private const string EntityType = "User";

    public async Task<AdminLoginResult> HandleAsync(AdminLoginCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var outcome = await authenticator.AuthenticateAsync(command.Email, command.Password, cancellationToken);

        switch (outcome.Status)
        {
            case AdminAuthenticationStatus.Succeeded:
                await WriteAuditAsync(AuditActions.Login, outcome, command, beforeJson: null, cancellationToken);
                return new AdminLoginResult.Success(
                    outcome.UserId!.Value,
                    outcome.Email!,
                    outcome.FirstName!,
                    outcome.LastName,
                    outcome.GrantsAllPermissions,
                    outcome.Permissions,
                    outcome.SecurityStamp!,
                    outcome.MustChangePassword);

            // Nothing is audited yet: the attempt is only complete (Login, or TwoFactorFailed) once the code arrives.
            case AdminAuthenticationStatus.TwoFactorRequired:
                return new AdminLoginResult.TwoFactorRequired(outcome.UserId!.Value, outcome.SecurityStamp!);

            case AdminAuthenticationStatus.LockedOut:
                await WriteAuditAsync(AuditActions.LoginFailed, outcome, command, beforeJson: null, cancellationToken);
                if (outcome.WasNewlyLockedOut)
                {
                    await WriteAuditAsync(AuditActions.Lockout, outcome, command, beforeJson: null, cancellationToken);
                }

                return new AdminLoginResult.LockedOut(outcome.LockoutEndUtc!.Value);

            case AdminAuthenticationStatus.InvalidCredentials:
            default:
                await WriteAuditAsync(AuditActions.LoginFailed, outcome, command, beforeJson: null, cancellationToken);
                return new AdminLoginResult.InvalidCredentials();
        }
    }

    private Task WriteAuditAsync(
        string action,
        AdminAuthenticationOutcome outcome,
        AdminLoginCommand command,
        string? beforeJson,
        CancellationToken cancellationToken)
    {
        var afterJson = action == AuditActions.LoginFailed
            ? JsonSerializer.Serialize(new { reason = outcome.Status.ToString() })
            : null;

        var entry = new AuditLog
        {
            OccurredAtUtc = clock.UtcNow.UtcDateTime,
            UserId = outcome.UserId,
            UserEmail = outcome.Email ?? command.Email,
            Action = action,
            EntityType = EntityType,
            EntityId = outcome.UserId?.ToString(CultureInfo.InvariantCulture),
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
        };

        return auditLogWriter.WriteAsync(entry, cancellationToken);
    }
}
