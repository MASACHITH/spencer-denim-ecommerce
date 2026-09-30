using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Deactivates or reactivates a staff user (ADM-02, doc 08 §3.1 DB4). Roles are unchanged by this operation,
/// so the subset check compares the target's current permissions against themselves both before and after —
/// which still correctly refuses a non-GrantsAll actor touching a GrantsAll target, or a target whose current
/// permissions already exceed the actor's own. Deactivating additionally runs the last-Super-Admin guard
/// (rule 4); reactivating never reduces the active GrantsAll count, so it never needs that guard.
/// </summary>
public sealed class SetStaffUserActiveCommandHandler(
    IStaffUserRoleManagementStore store,
    IEffectivePermissionsProvider permissionsProvider,
    IAdminUserLookup actorLookup,
    IAuditLogWriter auditLogWriter,
    IClock clock) : ICommandHandler<SetStaffUserActiveCommand, SetStaffUserActiveResult>
{
    public async Task<SetStaffUserActiveResult> HandleAsync(SetStaffUserActiveCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // See SetStaffUserActiveResult.SelfEditNotAllowed's remarks: not one of the four numbered rules by
        // name, but the same self-lockout rationale rule 3 gives for role edits.
        if (command.ActorUserId == command.TargetUserId)
        {
            return new SetStaffUserActiveResult.SelfEditNotAllowed();
        }

        // Loaded before the target lookup (tester-round-1 fix, defect 1): see the target-null branch below.
        var actor = await permissionsProvider.GetEffectivePermissionsAsync(command.ActorUserId, cancellationToken);
        var actorHasUsersManage = actor.Has(Permissions.Users.Manage);
        var actorPermissions = new HashSet<string>(actor.Permissions);

        var target = await store.GetStaffUserSnapshotAsync(command.TargetUserId, cancellationToken);
        if (target is null)
        {
            if (actor.GrantsAllPermissions)
            {
                return new SetStaffUserActiveResult.TargetNotFound();
            }

            // A non-GrantsAll actor must not be able to distinguish "doesn't exist" from "exists but is
            // protected" by the response shape.
            return new SetStaffUserActiveResult.Forbidden(RbacDenialReason.NotSubsetOfActorPermissions, RbacDenialClassifier.Message(RbacDenialReason.NotSubsetOfActorPermissions));
        }

        var allowed = RoleAssignmentPolicy.CanManageStaffUser(
            actorHasGrantsAll: actor.GrantsAllPermissions,
            actorHasUsersManage: actorHasUsersManage,
            actorEffectivePermissions: actorPermissions,
            targetHasGrantsAllBefore: target.GrantsAllPermissions,
            targetPermissionsBefore: target.EffectivePermissions,
            targetHasGrantsAllAfter: target.GrantsAllPermissions,
            targetPermissionsAfter: target.EffectivePermissions);

        if (!allowed)
        {
            var reason = RbacDenialClassifier.Classify(actor.GrantsAllPermissions, actorHasUsersManage, target.GrantsAllPermissions, target.GrantsAllPermissions);
            return new SetStaffUserActiveResult.Forbidden(reason, RbacDenialClassifier.Message(reason));
        }

        if (target.IsActive == command.Active)
        {
            // Already in the requested state: idempotent no-op, no audit row (nothing changed).
            return new SetStaffUserActiveResult.Success(command.TargetUserId, command.Active);
        }

        var writeResult = await store.SetActiveAsync(
            command.TargetUserId,
            command.Active,
            new RbacActorContext(actor.GrantsAllPermissions, actorHasUsersManage, actorPermissions),
            cancellationToken);

        switch (writeResult)
        {
            case SetActiveStoreResult.Success:
                var actorSummary = await actorLookup.FindByIdAsync(command.ActorUserId, cancellationToken);
                await WriteAuditAsync(command, actorSummary?.Email, cancellationToken);
                return new SetStaffUserActiveResult.Success(command.TargetUserId, command.Active);

            case SetActiveStoreResult.TargetNotFound:
                return new SetStaffUserActiveResult.TargetNotFound();

            case SetActiveStoreResult.LastSuperAdminGuardTriggered:
                return new SetStaffUserActiveResult.LastSuperAdminGuardTriggered();

            case SetActiveStoreResult.AuthorizationStale:
                // Tester-round-1 fix (defect 2): see AssignUserRolesCommandHandler's matching remark.
                return new SetStaffUserActiveResult.Forbidden(RbacDenialReason.NotSubsetOfActorPermissions, RbacDenialClassifier.Message(RbacDenialReason.NotSubsetOfActorPermissions));

            default:
                throw new InvalidOperationException($"Unhandled {nameof(SetActiveStoreResult)}: {writeResult.GetType().Name}");
        }
    }

    private Task WriteAuditAsync(SetStaffUserActiveCommand command, string? actorEmail, CancellationToken cancellationToken)
    {
        var entry = new AuditLog
        {
            OccurredAtUtc = clock.UtcNow.UtcDateTime,
            UserId = command.ActorUserId,
            UserEmail = actorEmail,
            Action = command.Active ? AuditActions.StaffReactivated : AuditActions.StaffDeactivated,
            EntityType = "User",
            EntityId = command.TargetUserId.ToString(CultureInfo.InvariantCulture),
            BeforeJson = JsonSerializer.Serialize(new { isActive = !command.Active }),
            AfterJson = JsonSerializer.Serialize(new { isActive = command.Active }),
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
        };

        return auditLogWriter.WriteAsync(entry, cancellationToken);
    }
}
