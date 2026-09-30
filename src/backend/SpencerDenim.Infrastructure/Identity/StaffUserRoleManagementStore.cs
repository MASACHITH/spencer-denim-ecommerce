using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Admin.Users;
using SpencerDenim.Application.Admin.Users.Abstractions;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.Infrastructure.Identity;

/// <summary>
/// Write port for staff user and role-assignment mutations (ADM-02, doc 08 §3.1 DB4, issue #8). RBAC
/// authorization (the subset rule, self-edit block, GrantsAll gating) has already happened in the command
/// handler by the time any write method here runs — this class only ever enforces DB4 rule 4, the last-Super-
/// Admin guard, because that is the one rule that needs real transactional machinery (<c>sp_getapplock</c>)
/// to be correct under concurrency; everything else is a per-request decision with no analogous race.
/// <para>
/// <see cref="ReplaceRolesAsync"/> and <see cref="SetActiveAsync"/> open an explicit transaction on the
/// shared <see cref="AppDbContext"/> and then call <see cref="UserManager{TUser}"/> methods against that same
/// context — those methods each trigger their own <c>SaveChangesAsync</c>, but because the context already
/// has an ambient transaction, every one of those saves is enlisted in it and only becomes durable when this
/// class commits.
/// </para>
/// </summary>
internal sealed class StaffUserRoleManagementStore(
    AppDbContext dbContext,
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager) : IStaffUserRoleManagementStore
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

    public async Task<CreateStaffUserStoreResult> CreateStaffUserAsync(NewStaffUserData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

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
            return new CreateStaffUserStoreResult.IdentityErrors([.. createResult.Errors.Select(e => e.Description)]);
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
                return new CreateStaffUserStoreResult.IdentityErrors([.. addRolesResult.Errors.Select(e => e.Description)]);
            }
        }

        var stamp = await userManager.GetSecurityStampAsync(user);
        return new CreateStaffUserStoreResult.Success(user.Id, stamp);
    }

    public async Task<ReplaceRolesStoreResult> ReplaceRolesAsync(int targetUserId, IReadOnlyList<int> roleIds, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString(CultureInfo.InvariantCulture));
        if (user is null)
        {
            return new ReplaceRolesStoreResult.TargetNotFound();
        }

        var currentRoleNames = await userManager.GetRolesAsync(user);
        var newRoles = await roleManager.Roles.Where(r => roleIds.Contains(r.Id)).ToListAsync(cancellationToken);
        var newRoleNames = newRoles.ConvertAll(r => r.Name!);

        var currentHasGrantsAll = currentRoleNames.Count > 0
            && await roleManager.Roles.AnyAsync(r => r.Name != null && currentRoleNames.Contains(r.Name) && r.GrantsAllPermissions, cancellationToken);
        var newHasGrantsAll = newRoles.Exists(r => r.GrantsAllPermissions);
        var reducesActiveGrantsAllCount = currentHasGrantsAll && !newHasGrantsAll;

        // Database:MaxRetryCount configures EnableRetryOnFailure (Infrastructure/DependencyInjection.cs), whose
        // retrying execution strategy refuses a bare Database.BeginTransactionAsync() outside itself ("does not
        // support user-initiated transactions"). CreateExecutionStrategy().ExecuteAsync retries the whole
        // begin/work/commit block together on a transient failure, which is what EF Core requires here.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

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

            await transaction.CommitAsync(cancellationToken);

            var newStamp = await userManager.GetSecurityStampAsync(user);
            return (ReplaceRolesStoreResult)new ReplaceRolesStoreResult.Success(newStamp);
        });
    }

    public async Task<SetActiveStoreResult> SetActiveAsync(int targetUserId, bool active, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == targetUserId && u.UserType == UserType.Staff, cancellationToken);
        if (user is null)
        {
            return new SetActiveStoreResult.TargetNotFound();
        }

        // Reactivating can never reduce the active GrantsAll count, so only deactivating a current GrantsAll
        // holder needs the guard.
        var mayReduceActiveGrantsAllCount = !active
            && await dbContext.UserRoles.AnyAsync(
                ur => ur.UserId == targetUserId && dbContext.Roles.Any(r => r.Id == ur.RoleId && r.GrantsAllPermissions),
                cancellationToken);

        // See the matching remark in ReplaceRolesAsync: EnableRetryOnFailure requires the transaction to be
        // opened and committed inside CreateExecutionStrategy().ExecuteAsync, not around it.
        var strategy = dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            if (mayReduceActiveGrantsAllCount)
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
        CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(targetUserId.ToString(CultureInfo.InvariantCulture));
        if (user is null || user.UserType != UserType.Staff)
        {
            return new UpdateProfileStoreResult.TargetNotFound();
        }

        if (newEmail is not null)
        {
            var setEmailResult = await userManager.SetEmailAsync(user, newEmail);
            if (!setEmailResult.Succeeded)
            {
                return MapProfileError(setEmailResult);
            }

            // Login resolves by email (AdminAuthenticator.FindByEmailAsync), but UserName mirrors Email at
            // creation (AdminSeedHostedService, and CreateStaffUserAsync above) — kept in sync here too, so
            // NormalizedUserName never silently drifts from NormalizedEmail.
            var setUserNameResult = await userManager.SetUserNameAsync(user, newEmail);
            if (!setUserNameResult.Succeeded)
            {
                return MapProfileError(setUserNameResult);
            }
        }

        user.FirstName = firstName;
        user.LastName = lastName;

        var updateResult = await userManager.UpdateAsync(user);
        return updateResult.Succeeded ? new UpdateProfileStoreResult.Success() : MapProfileError(updateResult);
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
