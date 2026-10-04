using System.Globalization;
using System.Text.Json;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Application.Admin.Users.Contracts;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Application.Admin.Users;

/// <summary>
/// Creates a staff user (ADM-02). This is the first real caller of <see cref="RoleAssignmentPolicy"/>
/// (doc 08 §3.1 DB4, issue #8): the requested role set is only ever accepted when every permission it
/// aggregates to is already inside the actor's own effective permission set —
/// <see cref="RoleAssignmentPolicy.CanManageStaffUser"/> with an empty "before" state models "may create
/// staff users only with roles that pass the same subset check" exactly, because a brand new user's
/// effective permissions before creation are the empty set.
/// </summary>
public sealed class CreateStaffUserCommandHandler(
    IStaffUserRoleManagementStore store,
    IEffectivePermissionsProvider permissionsProvider,
    IAdminUserLookup actorLookup,
    IClock clock) : ICommandHandler<CreateStaffUserCommand, CreateStaffUserResult>
{
    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>();

    public async Task<CreateStaffUserResult> HandleAsync(CreateStaffUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = ValidateInput(command);
        if (errors.Count > 0)
        {
            return new CreateStaffUserResult.ValidationFailed(errors);
        }

        var actor = await permissionsProvider.GetEffectivePermissionsAsync(command.ActorUserId, cancellationToken);
        var actorHasUsersManage = actor.Has(Permissions.Users.Manage);
        var actorPermissions = new HashSet<string>(actor.Permissions);

        var distinctRoleIds = command.RoleIds.Distinct().ToList();
        var roles = await store.GetRolesAsync(distinctRoleIds, cancellationToken);
        var unknownRoleIds = distinctRoleIds.Where(id => !roles.ContainsKey(id)).ToList();
        if (unknownRoleIds.Count > 0)
        {
            return new CreateStaffUserResult.RoleNotFound(unknownRoleIds);
        }

        var afterGrantsAll = roles.Values.Any(role => role.GrantsAllPermissions);
        var afterPermissions = new HashSet<string>(roles.Values.SelectMany(role => role.Permissions));

        var allowed = RoleAssignmentPolicy.CanManageStaffUser(
            actorHasGrantsAll: actor.GrantsAllPermissions,
            actorHasUsersManage: actorHasUsersManage,
            actorEffectivePermissions: actorPermissions,
            targetHasGrantsAllBefore: false,
            targetPermissionsBefore: NoPermissions,
            targetHasGrantsAllAfter: afterGrantsAll,
            targetPermissionsAfter: afterPermissions);

        if (!allowed)
        {
            var reason = RbacDenialClassifier.Classify(actor.GrantsAllPermissions, actorHasUsersManage, targetHasGrantsAllBefore: false, afterGrantsAll);
            return new CreateStaffUserResult.Forbidden(reason, RbacDenialClassifier.Message(reason));
        }

        if (await store.IsEmailInUseAsync(command.Email, excludingUserId: null, cancellationToken))
        {
            return new CreateStaffUserResult.EmailAlreadyInUse();
        }

        // The row names the new user's id, which only exists once the store has inserted them, so the store calls
        // this inside its transaction and enlists what it returns (issue #59).
        var roleRefs = roles.Values.Select(role => new RoleRefDto(role.Id, role.Name, role.GrantsAllPermissions)).ToList();
        var actorSummary = await actorLookup.FindByIdAsync(command.ActorUserId, cancellationToken);
        var createResult = await store.CreateStaffUserAsync(
            new NewStaffUserData(command.Email, command.FirstName, command.LastName, command.InitialPassword, distinctRoleIds),
            newUserId => BuildAuditRow(command, actorSummary?.Email, newUserId, roleRefs, afterPermissions),
            cancellationToken);

        switch (createResult)
        {
            case CreateStaffUserStoreResult.Success success:
                return new CreateStaffUserResult.Success(success.UserId, command.Email, command.FirstName, command.LastName, afterGrantsAll, roleRefs);

            case CreateStaffUserStoreResult.IdentityErrors identityErrors:
                return new CreateStaffUserResult.PasswordRejected(identityErrors.Errors);

            default:
                throw new InvalidOperationException($"Unhandled {nameof(CreateStaffUserStoreResult)}: {createResult.GetType().Name}");
        }
    }

    private static List<ValidationError> ValidateInput(CreateStaffUserCommand command)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(command.Email))
        {
            errors.Add(new ValidationError("email", "Email is required."));
        }

        if (string.IsNullOrWhiteSpace(command.FirstName))
        {
            errors.Add(new ValidationError("firstName", "First name is required."));
        }

        if (string.IsNullOrWhiteSpace(command.InitialPassword))
        {
            errors.Add(new ValidationError("initialPassword", "An initial password is required."));
        }

        if (command.RoleIds.Count == 0)
        {
            errors.Add(new ValidationError("roleIds", "At least one role is required."));
        }

        return errors;
    }

    private AuditLog BuildAuditRow(
        CreateStaffUserCommand command,
        string? actorEmail,
        int newUserId,
        IReadOnlyList<RoleRefDto> roles,
        IReadOnlySet<string> permissions)
    {
        var afterJson = JsonSerializer.Serialize(new
        {
            email = command.Email,
            firstName = command.FirstName,
            lastName = command.LastName,
            roles = roles.Select(role => role.Name),
            permissionsBefore = Array.Empty<string>(),
            permissionsAfter = permissions.Order(StringComparer.Ordinal),
        });

        return new AuditLog
        {
            OccurredAtUtc = clock.UtcNow.UtcDateTime,
            UserId = command.ActorUserId,
            UserEmail = actorEmail,
            Action = AuditActions.Create,
            EntityType = "User",
            EntityId = newUserId.ToString(CultureInfo.InvariantCulture),
            BeforeJson = null,
            AfterJson = afterJson,
            IpAddress = command.IpAddress,
            UserAgent = command.UserAgent,
        };
    }
}
