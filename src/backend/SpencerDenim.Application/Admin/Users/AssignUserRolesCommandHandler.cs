using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Assigns/unassigns a staff user's roles by replacing the whole set (ADM-02, doc 08 §3.1 DB4). The real
/// caller <see cref="RoleAssignmentPolicy.CanManageStaffUser"/> was written for: every one of rules 1
/// (GrantsAll gating), 2 (the subset rule, checked before *and* after), 3 (self-edit block, checked first,
/// ahead of any database read) and 4 (the last-Super-Admin guard, enforced transactionally by
/// <see cref="IStaffUserRoleManagementStore.ReplaceRolesAsync"/>) apply here.
/// </summary>
public sealed class AssignUserRolesCommandHandler(
    IStaffUserRoleManagementStore store,
    IEffectivePermissionsProvider permissionsProvider,
    IAdminUserLookup actorLookup,
    IAuditLogWriter auditLogWriter,
    IClock clock) : ICommandHandler<AssignUserRolesCommand, AssignUserRolesResult>
{
    public async Task<AssignUserRolesResult> HandleAsync(AssignUserRolesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // DB4 rule 3: checked before any other read, so a self-edit is refused the same way regardless of
        // what the actor's or target's current permissions happen to be.
        if (RoleAssignmentPolicy.IsSelfRoleEditBlocked(command.ActorUserId, command.TargetUserId))
        {
            return new AssignUserRolesResult.SelfEditNotAllowed();
        }

        // Loaded before the target lookup (tester-round-1 fix, defect 1): a non-GrantsAll actor must get the
        // exact same denial shape whether the target doesn't exist, holds GrantsAll, or merely has one extra
        // permission — see the target-null branch below.
        var actor = await permissionsProvider.GetEffectivePermissionsAsync(command.ActorUserId, cancellationToken);
        var actorHasUsersManage = actor.Has(Permissions.Users.Manage);
        var actorPermissions = new HashSet<string>(actor.Permissions);

        var target = await store.GetStaffUserSnapshotAsync(command.TargetUserId, cancellationToken);
        if (target is null)
        {
            if (actor.GrantsAllPermissions)
            {
                return new AssignUserRolesResult.TargetNotFound();
            }

            // A Super Admin is allowed to know a target genuinely doesn't exist; anyone else must not be able
            // to distinguish "doesn't exist" from "exists but is protected" by the response shape.
            return new AssignUserRolesResult.Forbidden(RbacDenialReason.NotSubsetOfActorPermissions, RbacDenialClassifier.Message(RbacDenialReason.NotSubsetOfActorPermissions));
        }

        var distinctRoleIds = command.RoleIds.Distinct().ToList();
        var roles = await store.GetRolesAsync(distinctRoleIds, cancellationToken);
        var unknownRoleIds = distinctRoleIds.Where(id => !roles.ContainsKey(id)).ToList();
        if (unknownRoleIds.Count > 0)
        {
            return new AssignUserRolesResult.RoleNotFound(unknownRoleIds);
        }

        var afterGrantsAll = roles.Values.Any(role => role.GrantsAllPermissions);
        var afterPermissions = new HashSet<string>(roles.Values.SelectMany(role => role.Permissions));

        var allowed = RoleAssignmentPolicy.CanManageStaffUser(
            actorHasGrantsAll: actor.GrantsAllPermissions,
            actorHasUsersManage: actorHasUsersManage,
            actorEffectivePermissions: actorPermissions,
            targetHasGrantsAllBefore: target.GrantsAllPermissions,
            targetPermissionsBefore: target.EffectivePermissions,
            targetHasGrantsAllAfter: afterGrantsAll,
            targetPermissionsAfter: afterPermissions);

        if (!allowed)
        {
            var reason = RbacDenialClassifier.Classify(actor.GrantsAllPermissions, actorHasUsersManage, target.GrantsAllPermissions, afterGrantsAll);
            return new AssignUserRolesResult.Forbidden(reason, RbacDenialClassifier.Message(reason));
        }

        var writeResult = await store.ReplaceRolesAsync(
            command.TargetUserId,
            distinctRoleIds,
            new RbacActorContext(actor.GrantsAllPermissions, actorHasUsersManage, actorPermissions),
            cancellationToken);

        switch (writeResult)
        {
            case ReplaceRolesStoreResult.Success:
                var roleRefs = roles.Values.Select(role => new RoleRefDto(role.Id, role.Name, role.GrantsAllPermissions)).ToList();
                var actorSummary = await actorLookup.FindByIdAsync(command.ActorUserId, cancellationToken);
                await WriteAuditAsync(command, actorSummary?.Email, target, roleRefs, afterPermissions, cancellationToken);
                return new AssignUserRolesResult.Success(command.TargetUserId, afterGrantsAll, roleRefs);

            case ReplaceRolesStoreResult.TargetNotFound:
                return new AssignUserRolesResult.TargetNotFound();

            case ReplaceRolesStoreResult.LastSuperAdminGuardTriggered:
                return new AssignUserRolesResult.LastSuperAdminGuardTriggered();

            case ReplaceRolesStoreResult.AuthorizationStale:
                // Tester-round-1 fix (defect 2): a concurrent change to the target invalidated the
                // authorization decision above between the read and this write. Same generic shape as an
                // ordinary subset-rule refusal (defect 1) — this is not a new, separately-observable reason.
                return new AssignUserRolesResult.Forbidden(RbacDenialReason.NotSubsetOfActorPermissions, RbacDenialClassifier.Message(RbacDenialReason.NotSubsetOfActorPermissions));

            default:
                throw new InvalidOperationException($"Unhandled {nameof(ReplaceRolesStoreResult)}: {writeResult.GetType().Name}");
        }
    }

    private Task WriteAuditAsync(
        AssignUserRolesCommand command,
        string? actorEmail,
        StaffUserSnapshot targetBefore,
        IReadOnlyList<RoleRefDto> rolesAfter,
        IReadOnlySet<string> permissionsAfter,
        CancellationToken cancellationToken)
    {
        var beforeJson = JsonSerializer.Serialize(new
        {
            roles = targetBefore.Roles.Select(role => role.Name),
            permissions = targetBefore.EffectivePermissions.Order(StringComparer.Ordinal),
        });

        var afterJson = JsonSerializer.Serialize(new
        {
            roles = rolesAfter.Select(role => role.Name),
            permissions = permissionsAfter.Order(StringComparer.Ordinal),
        });

        var entry = new AuditLog
        {
            OccurredAtUtc = clock.UtcNow.UtcDateTime,
            UserId = command.ActorUserId,
            UserEmail = actorEmail,
            Action = AuditActions.PermissionChange,
            EntityType = "User",
            EntityId = command.TargetUserId.ToString(CultureInfo.InvariantCulture),
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
        };

        return auditLogWriter.WriteAsync(entry, cancellationToken);
    }
}
