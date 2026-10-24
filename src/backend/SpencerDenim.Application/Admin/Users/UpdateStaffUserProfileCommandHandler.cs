using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Edits a staff user's profile (ADM-02, doc 08 §3.1 DB4). Roles are unchanged, so the subset check compares
/// the target's current permissions against themselves, exactly like <c>SetStaffUserActiveCommandHandler</c>.
/// A requested email change is gated separately by <see cref="RoleAssignmentPolicy.CanChangeStaffEmail"/>,
/// because rule 2 requires GrantsAll for that specific field regardless of whether the target's permissions
/// would otherwise pass the subset check.
/// </summary>
public sealed class UpdateStaffUserProfileCommandHandler(
    IStaffUserRoleManagementStore store,
    IEffectivePermissionsProvider permissionsProvider,
    IAdminUserLookup actorLookup,
    IProtectedStaffAccounts protectedAccounts,
    IClock clock) : ICommandHandler<UpdateStaffUserProfileCommand, UpdateStaffUserProfileResult>
{
    public async Task<UpdateStaffUserProfileResult> HandleAsync(UpdateStaffUserProfileCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.FirstName))
        {
            return new UpdateStaffUserProfileResult.ValidationFailed([new ValidationError("firstName", "First name is required.")]);
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
                return new UpdateStaffUserProfileResult.TargetNotFound();
            }

            // A non-GrantsAll actor must not be able to distinguish "doesn't exist" from "exists but is
            // protected" by the response shape.
            // The body is identical to the subset-rule refusal below, but the time taken is not: this path does
            // less database work than one that loads a real target, so response time still answers "does this id
            // exist" (issue #101). Reachable only by a holder of Users.Manage who is not a Super Admin, which no
            // role can be until role CRUD ships — see #101 for the measurements and the fix.
            return new UpdateStaffUserProfileResult.Forbidden(RbacDenialReason.NotSubsetOfActorPermissions, RbacDenialClassifier.Message(RbacDenialReason.NotSubsetOfActorPermissions));
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
            return new UpdateStaffUserProfileResult.Forbidden(reason, RbacDenialClassifier.Message(reason));
        }

        var isEmailChanging = !string.IsNullOrWhiteSpace(command.NewEmail)
            && !string.Equals(command.NewEmail, target.Email, StringComparison.OrdinalIgnoreCase);

        if (isEmailChanging && !RoleAssignmentPolicy.CanChangeStaffEmail(actor.GrantsAllPermissions))
        {
            return new UpdateStaffUserProfileResult.Forbidden(
                RbacDenialReason.EmailChangeRequiresGrantsAll,
                RbacDenialClassifier.Message(RbacDenialReason.EmailChangeRequiresGrantsAll));
        }

        // The protection is keyed on the address: a Super Admin who could rename a protected account could then
        // deactivate it under its new name. Refused before the in-use check so the answer does not depend on it.
        if (isEmailChanging && protectedAccounts.IsProtected(target.Email))
        {
            return new UpdateStaffUserProfileResult.ProtectedEmail();
        }

        // A guarded account's address is changed only by a protected account, for the same reason: it is how the
        // account is recognised.
        var actorSummary = await actorLookup.FindByIdAsync(command.ActorUserId, cancellationToken);
        if (isEmailChanging && protectedAccounts.IsGuarded(target.Email) && !protectedAccounts.IsProtected(actorSummary?.Email))
        {
            return new UpdateStaffUserProfileResult.GuardedEmail();
        }

        var newEmail = isEmailChanging ? command.NewEmail : null;
        if (newEmail is not null && await store.IsEmailInUseAsync(newEmail, command.TargetUserId, cancellationToken))
        {
            return new UpdateStaffUserProfileResult.EmailAlreadyInUse();
        }

        // Enlisted by the store inside its transaction (issue #59): the profile change and its trail commit
        // together, and a refused write leaves no row.
        // A factory, not a row: EnableRetryOnFailure re-runs the store's transaction on a transient failure and
        // each attempt needs its own AuditLog instance (tester round 3, Major 1).
        var finalEmail = newEmail ?? target.Email;
        AuditLog Audit() => BuildAuditRow(command, actorSummary?.Email, target, finalEmail, isEmailChanging);

        var writeResult = await store.UpdateProfileAsync(
            command.TargetUserId,
            command.FirstName,
            command.LastName,
            newEmail,
            new RbacActorContext(actor.GrantsAllPermissions, actorHasUsersManage, actorPermissions),
            Audit,
            cancellationToken);

        switch (writeResult)
        {
            case UpdateProfileStoreResult.Success:
                return new UpdateStaffUserProfileResult.Success(command.TargetUserId, finalEmail, command.FirstName, command.LastName);

            case UpdateProfileStoreResult.TargetNotFound:
                return new UpdateStaffUserProfileResult.TargetNotFound();

            case UpdateProfileStoreResult.EmailAlreadyInUse:
                return new UpdateStaffUserProfileResult.EmailAlreadyInUse();

            case UpdateProfileStoreResult.IdentityErrors identityErrors:
                return new UpdateStaffUserProfileResult.ValidationFailed(
                    [.. identityErrors.Errors.Select(error => new ValidationError("email", error))]);

            case UpdateProfileStoreResult.AuthorizationStale:
                // Tester-round-1 fix (defect 2): see AssignUserRolesCommandHandler's matching remark.
                return new UpdateStaffUserProfileResult.Forbidden(RbacDenialReason.NotSubsetOfActorPermissions, RbacDenialClassifier.Message(RbacDenialReason.NotSubsetOfActorPermissions));

            default:
                throw new InvalidOperationException($"Unhandled {nameof(UpdateProfileStoreResult)}: {writeResult.GetType().Name}");
        }
    }

    private AuditLog BuildAuditRow(
        UpdateStaffUserProfileCommand command,
        string? actorEmail,
        StaffUserSnapshot targetBefore,
        string finalEmail,
        bool emailChanged)
    {
        var beforeJson = JsonSerializer.Serialize(new
        {
            email = targetBefore.Email,
            firstName = targetBefore.FirstName,
            lastName = targetBefore.LastName,
        });

        var afterJson = JsonSerializer.Serialize(new
        {
            email = finalEmail,
            firstName = command.FirstName,
            lastName = command.LastName,
        });

        return new AuditLog
        {
            OccurredAtUtc = clock.UtcNow.UtcDateTime,
            UserId = command.ActorUserId,
            UserEmail = actorEmail,
            Action = emailChanged ? AuditActions.EmailChanged : AuditActions.Update,
            EntityType = "User",
            EntityId = command.TargetUserId.ToString(CultureInfo.InvariantCulture),
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
        };
    }
}
