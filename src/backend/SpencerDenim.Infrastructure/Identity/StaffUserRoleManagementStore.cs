using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Write port for staff user and role-assignment mutations (ADM-02, doc 08 §3.1 DB4, issue #8). The command
/// handler makes the doc 08 §3.1 DB4 rules 1/2 (GrantsAll gating, the subset rule) authorization decision
/// first, against a snapshot it read outside any transaction — but every write method here re-validates that
/// exact decision against the target's fresh, in-transaction state before writing (tester-round-1 fix, W1-08,
/// #8, defect 2), via the <see cref="RbacActorContext"/> the handler supplies, so a concurrent change to the
/// same target between the handler's read and this write can never be silently clobbered (lost update) or
/// leave a write applied against a state that no longer satisfies the subset rule. This class also enforces
/// DB4 rule 4, the last-Super-Admin guard, which needs the same <c>sp_getapplock</c> machinery for the same
/// reason: a per-request decision is not enough under concurrency.
/// <para>
/// <see cref="ReplaceRolesAsync"/>, <see cref="SetActiveAsync"/> and <see cref="UpdateProfileAsync"/> each open
/// an explicit transaction on the shared <see cref="AppDbContext"/> and then call
/// <see cref="UserManager{TUser}"/> methods against that same context — those methods each trigger their own
/// <c>SaveChangesAsync</c>, but because the context already has an ambient transaction, every one of those
/// saves is enlisted in it and only becomes durable when this class commits.
/// </para>
/// </summary>
internal sealed class StaffUserRoleManagementStore(
    AppDbContext dbContext,
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager,
    IAuditLogWriter auditLog) : IStaffUserRoleManagementStore
{
    /// <summary>Doc 08 §3.1 DB4 rule 4: the one application-lock resource name for the whole GrantsAll headcount.</summary>
    private const string GrantsAllLockResource = "rbac:grants-all";

    public async Task<StaffUserSnapshot?> GetStaffUserSnapshotAsync(int userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.UserType == UserType.Staff)
            .Select(u => new { u.Id, Email = u.Email ?? string.Empty, u.FirstName, u.LastName, u.IsActive, u.SecurityStamp })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return null;
        }

        var roleIds = await dbContext.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId)
            .ToListAsync(cancellationToken);

        var roles = await LoadRoleSnapshotsAsync(roleIds, cancellationToken);

        return new StaffUserSnapshot(user.Id, user.Email, user.FirstName, user.LastName, user.IsActive, user.SecurityStamp ?? string.Empty, roles);
    }

    public async Task<IReadOnlyDictionary<int, RoleGrantSnapshot>> GetRolesAsync(IReadOnlyCollection<int> roleIds, CancellationToken cancellationToken)
    {
        var snapshots = await LoadRoleSnapshotsAsync(roleIds, cancellationToken);
        return snapshots.ToDictionary(role => role.Id);
    }

    public async Task<bool> IsEmailInUseAsync(string email, int? excludingUserId, CancellationToken cancellationToken)
    {
        var normalized = userManager.NormalizeEmail(email);
        var query = dbContext.Users.AsNoTracking().Where(u => u.NormalizedEmail == normalized);
        if (excludingUserId is { } id)
        {
            query = query.Where(u => u.Id != id);
        }

        return await query.AnyAsync(cancellationToken);
    }

    public async Task<CreateStaffUserStoreResult> CreateStaffUserAsync(
        NewStaffUserData data,
        Func<int, AuditLog> auditForNewUser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(auditForNewUser);

        // One transaction for the user, their roles and the audit row. Before issue #59 this method had none:
        // a failure in AddToRolesAsync left a staff user who existed but held no roles, with nothing in the trail.
        // See ReplaceRolesAsync for why the block must sit inside CreateExecutionStrategy().ExecuteAsync.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // A retry re-runs this whole block, and the previous attempt's new user would still be tracked as
            // Added, so without this the retry would insert it a second time.
            //
            // What else this discards: the handler resolves the actor's email through IAdminUserLookup, which
            // goes via UserManager and therefore *does* track that AppUser. Detaching it is safe because it is an
            // unmodified read whose only use — the email string — is already materialised into the audit row by
            // the time this runs. Nothing in this request has pending changes to lose.
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var user = new AppUser
            {
                UserName = data.Email,
                Email = data.Email,
                EmailConfirmed = true,
                UserType = UserType.Staff,
                FirstName = data.FirstName,
                LastName = data.LastName,
                IsActive = true,

                // Same convention as the bootstrap Super Admin seed (issue #17): a temporary password handed over
                // out of band must not stay valid past the first sign-in.
                MustChangePassword = true,
            };

            var createResult = await userManager.CreateAsync(user, data.InitialPassword);
            if (!createResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (CreateStaffUserStoreResult)new CreateStaffUserStoreResult.IdentityErrors([.. createResult.Errors.Select(e => e.Description)]);
            }

            if (data.RoleIds.Count > 0)
            {
                var roleNames = await roleManager.Roles
                    .Where(r => data.RoleIds.Contains(r.Id))
                    .Select(r => r.Name!)
                    .ToListAsync(cancellationToken);

                var addRolesResult = await userManager.AddToRolesAsync(user, roleNames);
                if (!addRolesResult.Succeeded)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (CreateStaffUserStoreResult)new CreateStaffUserStoreResult.IdentityErrors([.. addRolesResult.Errors.Select(e => e.Description)]);
                }
            }

            auditLog.Enlist(auditForNewUser(user.Id));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var stamp = await userManager.GetSecurityStampAsync(user);
            return (CreateStaffUserStoreResult)new CreateStaffUserStoreResult.Success(user.Id, stamp);
        });
    }

    public async Task<ReplaceRolesStoreResult> ReplaceRolesAsync(
        int targetUserId,
        IReadOnlyList<int> roleIds,
        RbacActorContext actor,
        Func<AuditLog> audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(audit);

        if (await userManager.FindByIdAsync(targetUserId.ToString(CultureInfo.InvariantCulture)) is null)
        {
            return new ReplaceRolesStoreResult.TargetNotFound();
        }

        var newRoles = await roleManager.Roles.Where(r => roleIds.Contains(r.Id)).ToListAsync(cancellationToken);
        var newRoleNames = newRoles.ConvertAll(r => r.Name!);
        var newHasGrantsAll = newRoles.Exists(r => r.GrantsAllPermissions);
        var newPermissions = await LoadRolePermissionsAsync(newRoles.ConvertAll(r => r.Id), cancellationToken);

        // Database:MaxRetryCount configures EnableRetryOnFailure (Infrastructure/DependencyInjection.cs), whose
        // retrying execution strategy refuses a bare Database.BeginTransactionAsync() outside itself ("does not
        // support user-initiated transactions"). CreateExecutionStrategy().ExecuteAsync retries the whole
        // begin/work/commit block together on a transient failure, which is what EF Core requires here.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // Tester round 3, Major 1: every attempt starts from a clean tracker and re-reads the user, because a
            // retry of a failed attempt would otherwise re-apply that attempt's tracked role changes on top of
            // state that was rolled back — which surfaced as "User already in role" rather than a retry.
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // Tester-round-1 fix (defect 2): serialise on this target before re-reading its current role
            // state, so a concurrent writer for the SAME target either finishes first (and this re-read sees
            // its result) or blocks until this transaction commits or rolls back — closing the gap between the
            // handler's necessarily non-transactional authorization read and this write.
            await AcquireUserLockAsync(targetUserId, cancellationToken);

            var user = await userManager.FindByIdAsync(targetUserId.ToString(CultureInfo.InvariantCulture));
            if (user is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (ReplaceRolesStoreResult)new ReplaceRolesStoreResult.TargetNotFound();
            }

            var currentRoleNames = await userManager.GetRolesAsync(user);
            var (currentHasGrantsAll, currentPermissions) = await LoadCurrentPermissionStateAsync(targetUserId, cancellationToken);

            if (!actor.HasGrantsAll)
            {
                var stillAllowed = actor.HasUsersManage && RoleAssignmentPolicy.CanManageStaffUser(
                    actorHasGrantsAll: false,
                    actorHasUsersManage: actor.HasUsersManage,
                    actorEffectivePermissions: actor.EffectivePermissions,
                    targetHasGrantsAllBefore: currentHasGrantsAll,
                    targetPermissionsBefore: currentPermissions,
                    targetHasGrantsAllAfter: newHasGrantsAll,
                    targetPermissionsAfter: newPermissions);

                if (!stillAllowed)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (ReplaceRolesStoreResult)new ReplaceRolesStoreResult.AuthorizationStale();
                }
            }

            var reducesActiveGrantsAllCount = currentHasGrantsAll && !newHasGrantsAll;
            if (reducesActiveGrantsAllCount)
            {
                await AcquireGrantsAllLockAsync(cancellationToken);

                var remainingHolders = await CountActiveGrantsAllHoldersExcludingAsync(targetUserId, cancellationToken);
                if (!RoleAssignmentPolicy.CanReduceActiveGrantsAllHolders(remainingHolders))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (ReplaceRolesStoreResult)new ReplaceRolesStoreResult.LastSuperAdminGuardTriggered();
                }
            }

            if (currentRoleNames.Count > 0)
            {
                var removeResult = await userManager.RemoveFromRolesAsync(user, currentRoleNames);
                if (!removeResult.Succeeded)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw new InvalidOperationException(
                        $"Could not unassign roles from user {targetUserId}: {string.Join("; ", removeResult.Errors.Select(e => e.Description))}");
                }
            }

            if (newRoleNames.Count > 0)
            {
                var addResult = await userManager.AddToRolesAsync(user, newRoleNames);
                if (!addResult.Succeeded)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw new InvalidOperationException(
                        $"Could not assign roles to user {targetUserId}: {string.Join("; ", addResult.Errors.Select(e => e.Description))}");
                }
            }

            // Doc 08 §3.1 DB4 rule 5: a role/permission change bumps SecurityStamp, so the 1-minute revalidation
            // loop (issue #16, PR #42) and the effective-permissions cache (keyed by SecurityStamp) both pick
            // this up without any explicit cache-eviction call.
            await userManager.UpdateSecurityStampAsync(user);

            // Enlisted only here, on the success path: every refusal above returned before this line, so a
            // refused write leaves no row. Saved explicitly rather than relying on the stamp update's own save.
            auditLog.Enlist(audit());
            await dbContext.SaveChangesAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var newStamp = await userManager.GetSecurityStampAsync(user);
            return (ReplaceRolesStoreResult)new ReplaceRolesStoreResult.Success(newStamp);
        });
    }

    public async Task<SetActiveStoreResult> SetActiveAsync(
        int targetUserId,
        bool active,
        RbacActorContext actor,
        Func<AuditLog> audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(audit);

        if (!await dbContext.Users.AsNoTracking().AnyAsync(u => u.Id == targetUserId && u.UserType == UserType.Staff, cancellationToken))
        {
            return new SetActiveStoreResult.TargetNotFound();
        }

        // See the matching remark in ReplaceRolesAsync: EnableRetryOnFailure requires the transaction to be
        // opened and committed inside CreateExecutionStrategy().ExecuteAsync, not around it.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // See ReplaceRolesAsync (tester round 3, Major 1): a clean tracker and a fresh read per attempt.
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // Tester-round-1 fix (defect 2): see ReplaceRolesAsync's remarks — this operation does not change
            // the target's roles itself, but a concurrent role change elsewhere to the SAME target between the
            // handler's authorization read and this write could make that earlier decision stale.
            await AcquireUserLockAsync(targetUserId, cancellationToken);

            var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == targetUserId && u.UserType == UserType.Staff, cancellationToken);
            if (user is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (SetActiveStoreResult)new SetActiveStoreResult.TargetNotFound();
            }

            var (currentHasGrantsAll, currentPermissions) = await LoadCurrentPermissionStateAsync(targetUserId, cancellationToken);

            if (!actor.HasGrantsAll)
            {
                var stillAllowed = actor.HasUsersManage && RoleAssignmentPolicy.CanManageStaffUser(
                    actorHasGrantsAll: false,
                    actorHasUsersManage: actor.HasUsersManage,
                    actorEffectivePermissions: actor.EffectivePermissions,
                    targetHasGrantsAllBefore: currentHasGrantsAll,
                    targetPermissionsBefore: currentPermissions,
                    targetHasGrantsAllAfter: currentHasGrantsAll,
                    targetPermissionsAfter: currentPermissions);

                if (!stillAllowed)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (SetActiveStoreResult)new SetActiveStoreResult.AuthorizationStale();
                }
            }

            // Reactivating can never reduce the active GrantsAll count, so only deactivating a (freshly
            // re-read) current GrantsAll holder needs the guard.
            var reducesActiveGrantsAllCount = !active && currentHasGrantsAll;
            if (reducesActiveGrantsAllCount)
            {
                await AcquireGrantsAllLockAsync(cancellationToken);

                var remainingHolders = await CountActiveGrantsAllHoldersExcludingAsync(targetUserId, cancellationToken);
                if (!RoleAssignmentPolicy.CanReduceActiveGrantsAllHolders(remainingHolders))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (SetActiveStoreResult)new SetActiveStoreResult.LastSuperAdminGuardTriggered();
                }
            }

            user.IsActive = active;
            auditLog.Enlist(audit());
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return (SetActiveStoreResult)new SetActiveStoreResult.Success();
        });
    }

    public async Task<UpdateProfileStoreResult> UpdateProfileAsync(
        int targetUserId,
        string firstName,
        string? lastName,
        string? newEmail,
        RbacActorContext actor,
        Func<AuditLog> audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(audit);

        if (await userManager.FindByIdAsync(targetUserId.ToString(CultureInfo.InvariantCulture)) is not { UserType: UserType.Staff })
        {
            return new UpdateProfileStoreResult.TargetNotFound();
        }

        // See ReplaceRolesAsync's remarks (tester-round-1 fix, defect 2). This operation does not touch roles,
        // but still needs the same fresh re-validation: a concurrent role change to this target between the
        // handler's authorization read and this write could make that earlier decision stale.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            // See ReplaceRolesAsync (tester round 3, Major 1). This method is the one the tester saw fail with
            // "Cannot insert explicit value for identity column": the enlisted audit row carried the identity
            // value EF had assigned it on the failed attempt. The row comes from a factory now, and the tracker
            // is cleared, so a retry starts from nothing.
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            await AcquireUserLockAsync(targetUserId, cancellationToken);

            var user = await userManager.FindByIdAsync(targetUserId.ToString(CultureInfo.InvariantCulture));
            if (user is null || user.UserType != UserType.Staff)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (UpdateProfileStoreResult)new UpdateProfileStoreResult.TargetNotFound();
            }

            if (!actor.HasGrantsAll)
            {
                var (currentHasGrantsAll, currentPermissions) = await LoadCurrentPermissionStateAsync(targetUserId, cancellationToken);
                var stillAllowed = actor.HasUsersManage && RoleAssignmentPolicy.CanManageStaffUser(
                    actorHasGrantsAll: false,
                    actorHasUsersManage: actor.HasUsersManage,
                    actorEffectivePermissions: actor.EffectivePermissions,
                    targetHasGrantsAllBefore: currentHasGrantsAll,
                    targetPermissionsBefore: currentPermissions,
                    targetHasGrantsAllAfter: currentHasGrantsAll,
                    targetPermissionsAfter: currentPermissions);

                if (!stillAllowed)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (UpdateProfileStoreResult)new UpdateProfileStoreResult.AuthorizationStale();
                }
            }

            if (newEmail is not null)
            {
                var setEmailResult = await userManager.SetEmailAsync(user, newEmail);
                if (!setEmailResult.Succeeded)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (UpdateProfileStoreResult)MapProfileError(setEmailResult);
                }

                // Login resolves by email (AdminAuthenticator.FindByEmailAsync), but UserName mirrors Email at
                // creation (AdminSeedHostedService, and CreateStaffUserAsync above) — kept in sync here too, so
                // NormalizedUserName never silently drifts from NormalizedEmail.
                var setUserNameResult = await userManager.SetUserNameAsync(user, newEmail);
                if (!setUserNameResult.Succeeded)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return (UpdateProfileStoreResult)MapProfileError(setUserNameResult);
                }
            }

            user.FirstName = firstName;
            user.LastName = lastName;

            var updateResult = await userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (UpdateProfileStoreResult)MapProfileError(updateResult);
            }

            auditLog.Enlist(audit());
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (UpdateProfileStoreResult)new UpdateProfileStoreResult.Success();
        });
    }

    private static UpdateProfileStoreResult MapProfileError(IdentityResult result)
    {
        if (result.Errors.Any(e => string.Equals(e.Code, "DuplicateEmail", StringComparison.Ordinal)
            || string.Equals(e.Code, "DuplicateUserName", StringComparison.Ordinal)))
        {
            return new UpdateProfileStoreResult.EmailAlreadyInUse();
        }

        return new UpdateProfileStoreResult.IdentityErrors([.. result.Errors.Select(e => e.Description)]);
    }

    /// <summary>
    /// Doc 08 §3.1 DB4 rule 4. Called with the app lock already held, inside the caller's transaction, so this
    /// count reflects every such transaction that has committed before this one acquired the lock — including
    /// one that ran concurrently and got there first, which is exactly the race this rule exists to close.
    /// </summary>
    private Task<int> CountActiveGrantsAllHoldersExcludingAsync(int excludingUserId, CancellationToken cancellationToken) =>
        dbContext.Users.AsNoTracking()
            .Where(u => u.Id != excludingUserId
                && u.IsActive
                && dbContext.UserRoles.Any(ur => ur.UserId == u.Id && dbContext.Roles.Any(r => r.Id == ur.RoleId && r.GrantsAllPermissions)))
            .CountAsync(cancellationToken);

    /// <summary>
    /// <c>sp_getapplock</c> with the default infinite <c>@LockTimeout</c>: a second caller blocks here until
    /// the first one commits or rolls back, then proceeds to its own (now up to date) count — it does not
    /// fail fast. That is deliberate: this guard's job is to serialise the two attempts, not to reject the
    /// second one outright, so "two concurrent deactivate-the-last-Super-Admin attempts" resolves to exactly
    /// one success and one <see cref="SetActiveStoreResult.LastSuperAdminGuardTriggered"/>, never both
    /// succeeding.
    /// </summary>
    private Task<int> AcquireGrantsAllLockAsync(CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"EXEC sp_getapplock @Resource = {GrantsAllLockResource}, @LockMode = N'Exclusive', @LockOwner = N'Transaction';",
            cancellationToken);

    /// <summary>
    /// Tester-round-1 fix (W1-08, #8, defect 2): one <c>sp_getapplock</c> resource per target user, so a
    /// concurrent write to the SAME target serialises here exactly like rule 4 already serialises on the
    /// global GrantsAll headcount — but scoped per-target, since rules 1/2's subset condition is per-target,
    /// not global, and locking every write against the single global resource would serialise unrelated users'
    /// writes against each other for no reason. Same infinite-timeout, block-don't-fail-fast semantics as
    /// <see cref="AcquireGrantsAllLockAsync"/>.
    /// </summary>
    private Task<int> AcquireUserLockAsync(int targetUserId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"EXEC sp_getapplock @Resource = {UserLockResource(targetUserId)}, @LockMode = N'Exclusive', @LockOwner = N'Transaction';",
            cancellationToken);

    private static string UserLockResource(int targetUserId) =>
        string.Create(CultureInfo.InvariantCulture, $"rbac:manage-user:{targetUserId}");

    /// <summary>
    /// Tester-round-1 fix (defect 2): the target's CURRENT GrantsAll/permission state, read fresh inside the
    /// caller's transaction (and after <see cref="AcquireUserLockAsync"/>, so it reflects any write from a
    /// concurrent caller for the same target that got there first) — never the possibly-stale snapshot the
    /// command handler read before this call started.
    /// </summary>
    private async Task<(bool HasGrantsAll, IReadOnlySet<string> Permissions)> LoadCurrentPermissionStateAsync(int targetUserId, CancellationToken cancellationToken)
    {
        var roleIds = await dbContext.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == targetUserId)
            .Select(ur => ur.RoleId)
            .ToListAsync(cancellationToken);

        if (roleIds.Count == 0)
        {
            return (false, new HashSet<string>());
        }

        var hasGrantsAll = await dbContext.Roles.AsNoTracking()
            .AnyAsync(r => roleIds.Contains(r.Id) && r.GrantsAllPermissions, cancellationToken);

        var permissions = await LoadRolePermissionsAsync(roleIds, cancellationToken);

        return (hasGrantsAll, permissions);
    }

    /// <summary>The union of permissions granted by the given roles, via their <see cref="RoleClaimConfiguration.PermissionClaimType"/> claims.</summary>
    private async Task<IReadOnlySet<string>> LoadRolePermissionsAsync(List<int> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
        {
            return new HashSet<string>();
        }

        var claims = await dbContext.RoleClaims.AsNoTracking()
            .Where(rc => roleIds.Contains(rc.RoleId) && rc.ClaimType == RoleClaimConfiguration.PermissionClaimType)
            .Select(rc => rc.ClaimValue!)
            .ToListAsync(cancellationToken);

        return new HashSet<string>(claims);
    }

    private async Task<List<RoleGrantSnapshot>> LoadRoleSnapshotsAsync(IReadOnlyCollection<int> roleIds, CancellationToken cancellationToken)
    {
        if (roleIds.Count == 0)
        {
            return [];
        }

        var roles = await dbContext.Roles.AsNoTracking()
            .Where(r => roleIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Name, r.GrantsAllPermissions })
            .ToListAsync(cancellationToken);

        var claims = await dbContext.RoleClaims.AsNoTracking()
            .Where(rc => roleIds.Contains(rc.RoleId) && rc.ClaimType == RoleClaimConfiguration.PermissionClaimType)
            .Select(rc => new { rc.RoleId, rc.ClaimValue })
            .ToListAsync(cancellationToken);

        var permissionsByRole = claims
            .GroupBy(c => c.RoleId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)new HashSet<string>(g.Select(c => c.ClaimValue!)));

        return roles
            .ConvertAll(r => new RoleGrantSnapshot(
                r.Id,
                r.Name ?? string.Empty,
                r.GrantsAllPermissions,
                permissionsByRole.TryGetValue(r.Id, out var permissions) ? permissions : new HashSet<string>()));
    }
}
