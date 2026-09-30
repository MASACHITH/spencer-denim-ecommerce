using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Admin.Account;

/// <summary>
/// Writes the security audit rows of a staff user's own 2FA changes (W1-10), in the shape
/// <see cref="ChangeAdminPasswordCommandHandler"/> writes them: the user is both actor and entity. Keys, codes and
/// passwords never reach this class (doc 08 §3.8).
/// </summary>
internal sealed class AdminAccountAudit(IAuditLogWriter auditLogWriter, IClock clock)
{
    private const string EntityType = "User";

    public Task WriteAsync(
        string action,
        AdminTwoFactorOutcome outcome,
        string? ipAddress,
        string? userAgent,
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
            IpAddress = ipAddress,
            UserAgent = userAgent,
        };

        return auditLogWriter.WriteAsync(entry, cancellationToken);
    }

    /// <summary>
    /// The shared trail of a refused current-password check on a 2FA change: <c>TwoFactorFailed</c> with the step,
    /// plus <c>Lockout</c> on the attempt that trips the limit (the same rows sign-in writes).
    /// </summary>
    public async Task<AdminTwoFactorResult> WritePasswordFailureAsync(
        AdminTwoFactorOutcome outcome,
        string step,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var reason = JsonSerializer.Serialize(new { reason = outcome.Status.ToString(), step });
        await WriteAsync(AuditActions.TwoFactorFailed, outcome, ipAddress, userAgent, null, reason, cancellationToken);

        if (outcome.Status != AdminTwoFactorStatus.LockedOut)
        {
            return new AdminTwoFactorResult.InvalidCurrentPassword();
        }

        if (outcome.WasNewlyLockedOut)
        {
            await WriteAsync(AuditActions.Lockout, outcome, ipAddress, userAgent, null, null, cancellationToken);
        }

        return new AdminTwoFactorResult.LockedOut(outcome.LockoutEndUtc!.Value);
    }

    /// <summary>The re-issued session after a successful change that rotated the stamp.</summary>
    public static ReissuedAdminSession SessionFrom(AdminTwoFactorOutcome outcome) =>
        new(outcome.UserId, outcome.Email!, outcome.FirstName!, outcome.LastName, outcome.SecurityStamp!);

    public static string TwoFactorEnabledJson(bool enabled) => JsonSerializer.Serialize(new { twoFactorEnabled = enabled });
}
