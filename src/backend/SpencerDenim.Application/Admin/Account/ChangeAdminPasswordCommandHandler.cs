using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Admin.Account;

/// <summary>
/// Orchestrates a staff user's own password change (issue #17): delegates the Identity work to
/// <see cref="IAdminPasswordChanger"/> and writes the audit trail — <c>PasswordChanged</c> on success,
/// <c>PasswordChangeFailed</c> for a wrong current password, plus <c>Lockout</c> on the attempt that trips the
/// limit, mirroring what <c>AdminLoginCommandHandler</c> writes for sign-in (doc 11 P14 "every sign-in attempt
/// is audited"). A new password that only fails the policy is not audited: the caller has just proved they know
/// the current one, so it is a typing problem, not a security event.
/// </summary>
public sealed class ChangeAdminPasswordCommandHandler(IAdminPasswordChanger passwordChanger, IAuditLogWriter auditLogWriter, IClock clock)
    : ICommandHandler<ChangeAdminPasswordCommand, ChangeAdminPasswordResult>
{
    private const string EntityType = "User";

    public async Task<ChangeAdminPasswordResult> HandleAsync(ChangeAdminPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var outcome = await passwordChanger.ChangePasswordAsync(
            command.UserId,
            command.CurrentPassword,
            command.NewPassword,
            cancellationToken);

        switch (outcome.Status)
        {
            case AdminPasswordChangeStatus.Succeeded:
                await WriteSuccessAuditAsync(outcome, command, cancellationToken);
                return new ChangeAdminPasswordResult.Success(
                    outcome.UserId,
                    outcome.Email!,
                    outcome.FirstName!,
                    outcome.LastName,
                    outcome.SecurityStamp!);

            case AdminPasswordChangeStatus.InvalidCurrentPassword:
                await WriteAuditAsync(AuditActions.PasswordChangeFailed, outcome, command, null, ReasonJson(outcome), cancellationToken);
                return new ChangeAdminPasswordResult.InvalidCurrentPassword();

            case AdminPasswordChangeStatus.LockedOut:
                await WriteAuditAsync(AuditActions.PasswordChangeFailed, outcome, command, null, ReasonJson(outcome), cancellationToken);
                if (outcome.WasNewlyLockedOut)
                {
                    await WriteAuditAsync(AuditActions.Lockout, outcome, command, null, null, cancellationToken);
                }

                return new ChangeAdminPasswordResult.LockedOut(outcome.LockoutEndUtc!.Value);

            case AdminPasswordChangeStatus.NewPasswordRejected:
                return new ChangeAdminPasswordResult.NewPasswordRejected(outcome.PolicyErrors);

            case AdminPasswordChangeStatus.AccountUnavailable:
            default:
                return new ChangeAdminPasswordResult.AccountUnavailable();
        }
    }

    /// <summary>Only the flag is diffed; the hash and stamps are never logged (doc 08 §3.8 redaction list).</summary>
    private Task WriteSuccessAuditAsync(AdminPasswordChangeOutcome outcome, ChangeAdminPasswordCommand command, CancellationToken cancellationToken)
    {
        if (!outcome.ClearedMustChangePassword)
        {
            return WriteAuditAsync(AuditActions.PasswordChanged, outcome, command, null, null, cancellationToken);
        }

        var beforeJson = JsonSerializer.Serialize(new { mustChangePassword = true });
        var afterJson = JsonSerializer.Serialize(new { mustChangePassword = false });
        return WriteAuditAsync(AuditActions.PasswordChanged, outcome, command, beforeJson, afterJson, cancellationToken);
    }

    private static string ReasonJson(AdminPasswordChangeOutcome outcome) =>
        JsonSerializer.Serialize(new { reason = outcome.Status.ToString() });

    private Task WriteAuditAsync(
        string action,
        AdminPasswordChangeOutcome outcome,
        ChangeAdminPasswordCommand command,
        string? beforeJson,
        string? afterJson,
        CancellationToken cancellationToken)
    {
        var entry = new AuditLog
        {
            OccurredAtUtc = clock.UtcNow.UtcDateTime,
            UserId = outcome.UserId,
            UserEmail = outcome.Email,
            Action = action,
            EntityType = EntityType,
            EntityId = outcome.UserId.ToString(CultureInfo.InvariantCulture),
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
        };

        return auditLogWriter.WriteAsync(entry, cancellationToken);
    }
}
