using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Admin.Users;

/// <summary>The three ways a Super Admin can get a staff member back into their account (#254, ADM-01..03).</summary>
public enum StaffAccountRecovery
{
    /// <summary>Set a temporary password the member must replace at their next sign-in; their sessions end.</summary>
    ResetPassword = 1,

    /// <summary>Clear a lockout (five wrong passwords) and the failed-attempt count.</summary>
    Unlock = 2,

    /// <summary>Remove a lost authenticator and its recovery codes; the member sets it up again; their sessions end.</summary>
    ResetTwoFactor = 3,
}

/// <param name="TemporaryPassword">
/// <see cref="StaffAccountRecovery.ResetPassword"/> only: typed by the Super Admin and handed over in person or by
/// phone, exactly as a new staff user's first password is. <b>Never emailed</b>: an email link is a takeover channel
/// for whoever controls the address (doc 08 N3 is why only a Super Admin can change a staff email).
/// </param>
public sealed record RecoverStaffAccountCommand(
    int ActorUserId,
    int TargetUserId,
    StaffAccountRecovery Operation,
    string? TemporaryPassword,
    string? IpAddress,
    string? UserAgent);

public abstract record RecoverStaffAccountResult
{
    private RecoverStaffAccountResult()
    {
    }

    /// <summary>Done; <paramref name="Changed"/> is false when there was nothing to do (not locked, no authenticator).</summary>
    public sealed record Success(int UserId, StaffAccountRecovery Operation, bool Changed) : RecoverStaffAccountResult;

    public sealed record TargetNotFound : RecoverStaffAccountResult;

    /// <summary>On oneself: the self-service pages exist for that, and a Super Admin resetting their own 2FA is how the
    /// last one locks themselves out.</summary>
    public sealed record SelfNotAllowed : RecoverStaffAccountResult;

    /// <summary>The actor is not a Super Admin. Decided before the target is read, so it says nothing about the target.</summary>
    public sealed record Forbidden(string Message) : RecoverStaffAccountResult;

    /// <summary>A protected account (#255): recovered only by its owner or with the break-glass CLI.</summary>
    public sealed record ProtectedAccount : RecoverStaffAccountResult;

    /// <summary>A guarded account (#255): only a protected account may recover it.</summary>
    public sealed record GuardedAccount : RecoverStaffAccountResult;

    public sealed record PasswordRejected(IReadOnlyList<string> Errors) : RecoverStaffAccountResult;
}

/// <summary>
/// Staff account recovery (#254; doc 11 P14 steps 7-8): reset a password, unlock, reset two-factor authentication.
/// </summary>
/// <remarks>
/// <para>
/// <b>Super Admin only, and decided before the target is read.</b> The issue asks for GrantsAll alone, which is
/// stricter than doc 08 N3 / doc 11 P14 (they also allow a <c>Users.Manage</c> holder under the subset rule). Stricter
/// is safe, and checking it first means a refusal costs the same and says the same whatever id was asked about (the
/// shape #101 measured on the other routes). <c>Users.Manage</c> is still required by the route, as for every staff write.
/// </para>
/// <para>
/// <b>Why protected and guarded accounts apply here although #255 left roles alone:</b> a Super Admin who sets a
/// member's password and removes their authenticator can sign in as them. Taking over the technical owner's account
/// that way would defeat the point of protecting it, so a protected account is never recovered from the web app (its
/// owner uses the break-glass CLI, doc 08 N14), and a guarded one only by a protected account — the same rule as
/// deactivating it.
/// </para>
/// <para>
/// <b>Audited by the store, in the same transaction as the change</b>: who, whom, when, which operation. Never the
/// temporary password, the authenticator key, the recovery codes or the failed-attempt count (doc 08 DB24).
/// </para>
/// </remarks>
public sealed class RecoverStaffAccountCommandHandler(
    IStaffUserRoleManagementStore store,
    IEffectivePermissionsProvider permissionsProvider,
    IAdminUserLookup actorLookup,
    IProtectedStaffAccounts protectedAccounts,
    IClock clock) : ICommandHandler<RecoverStaffAccountCommand, RecoverStaffAccountResult>
{
    /// <summary>What a member who is not a Super Admin is told.</summary>
    public const string SuperAdminOnly =
        "Only a Super Admin can reset a staff password, unlock an account or reset two-factor authentication.";

    public async Task<RecoverStaffAccountResult> HandleAsync(
        RecoverStaffAccountCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.ActorUserId == command.TargetUserId)
        {
            return new RecoverStaffAccountResult.SelfNotAllowed();
        }

        // The actor's own account first, before the target is read (#254 tester): a Super Admin deactivated in the last
        // minute still has a valid cookie until the 60-second revalidation, and these routes can take over an account,
        // so they do not wait for it. Same answer as for any non-Super Admin, so nothing about the target is said.
        var actorSummary = await actorLookup.FindByIdAsync(command.ActorUserId, cancellationToken);
        var actor = await permissionsProvider.GetEffectivePermissionsAsync(command.ActorUserId, cancellationToken);
        if (!actor.GrantsAllPermissions || actorSummary is not { IsActive: true })
        {
            return new RecoverStaffAccountResult.Forbidden(SuperAdminOnly);
        }

        if (command.Operation == StaffAccountRecovery.ResetPassword && string.IsNullOrEmpty(command.TemporaryPassword))
        {
            return new RecoverStaffAccountResult.PasswordRejected(["Type a temporary password."]);
        }

        var target = await store.GetStaffUserSnapshotAsync(command.TargetUserId, cancellationToken);
        if (target is null)
        {
            return new RecoverStaffAccountResult.TargetNotFound();
        }

        if (protectedAccounts.IsProtected(target.Email))
        {
            return new RecoverStaffAccountResult.ProtectedAccount();
        }

        if (protectedAccounts.IsGuarded(target.Email) && !protectedAccounts.IsProtected(actorSummary.Email))
        {
            return new RecoverStaffAccountResult.GuardedAccount();
        }

        // A factory, not a row: the store's transaction can be retried and each attempt needs its own instance.
        AuditLog Audit() => BuildAuditRow(command, actorSummary.Email);

        var result = await store.RecoverAccountAsync(
            command.TargetUserId,
            command.Operation,
            command.TemporaryPassword,
            clock.UtcNow,
            Audit,
            cancellationToken);

        return result switch
        {
            RecoverAccountStoreResult.Applied => new RecoverStaffAccountResult.Success(command.TargetUserId, command.Operation, true),
            RecoverAccountStoreResult.NothingToDo => new RecoverStaffAccountResult.Success(command.TargetUserId, command.Operation, false),
            RecoverAccountStoreResult.TargetNotFound => new RecoverStaffAccountResult.TargetNotFound(),
            RecoverAccountStoreResult.PasswordRejected rejected => new RecoverStaffAccountResult.PasswordRejected(rejected.Errors),
            _ => throw new InvalidOperationException($"Unhandled {nameof(RecoverAccountStoreResult)}: {result.GetType().Name}"),
        };
    }

    /// <summary>The audit action for each operation (doc 11 P14 step 8 names the first and the last).</summary>
    public static string AuditActionFor(StaffAccountRecovery operation) => operation switch
    {
        StaffAccountRecovery.ResetPassword => AuditActions.AdminPasswordReset,
        StaffAccountRecovery.Unlock => AuditActions.StaffUnlocked,
        StaffAccountRecovery.ResetTwoFactor => AuditActions.TwoFactorReset,
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };

    private AuditLog BuildAuditRow(RecoverStaffAccountCommand command, string? actorEmail) =>
        new()
        {
            OccurredAtUtc = clock.UtcNow.UtcDateTime,
            UserId = command.ActorUserId,
            UserEmail = actorEmail,
            Action = AuditActionFor(command.Operation),
            EntityType = "User",
            EntityId = command.TargetUserId.ToString(CultureInfo.InvariantCulture),

            // What changed, as flags. Never the password, the key, the codes or the failed-attempt count (DB24).
            AfterJson = command.Operation switch
            {
                StaffAccountRecovery.ResetPassword => JsonSerializer.Serialize(new { mustChangePassword = true, sessionsEnded = true }),
                StaffAccountRecovery.Unlock => JsonSerializer.Serialize(new { lockedOut = false }),
                _ => JsonSerializer.Serialize(new { twoFactorEnabled = false, authenticatorRemoved = true, sessionsEnded = true }),
            },
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
        };
}
