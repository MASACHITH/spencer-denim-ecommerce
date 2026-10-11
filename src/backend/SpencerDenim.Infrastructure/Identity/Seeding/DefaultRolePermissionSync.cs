using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.Infrastructure.Identity.Seeding;

/// <summary>
/// The versioned, additive upgrade step for the default roles that doc 08 §9 / N13 promise (issue #122), and the
/// startup report of how the roles differ from what the build ships.
/// <para>
/// <b>What it does, on every start.</b> Inside one transaction, serialised on an application lock: read the
/// default-role seed version this database has been brought up to; for each default role that <i>exists</i>, add
/// only the permissions <see cref="DefaultRoleSeedManifest"/> says were introduced after that version; record the
/// current version. Then compare every default role with its shipped definition and log the difference by name.
/// A second start finds the version current and changes nothing.
/// </para>
/// <para>
/// <b>What it will never do.</b> Remove a permission. Touch a role's other permissions. Re-add a permission a
/// Super Admin took away after the version that granted it (the version gate is what guarantees that — see the
/// manifest). Create a missing role (<c>AdminSeedHostedService</c> does that, behind <c>Seed:Admin:Enabled</c>).
/// Touch Super Admin (<c>GrantsAllPermissions</c> is evaluated, not enumerated) or any role that is not a seeded
/// system role of the same name.
/// </para>
/// <para>
/// <b>Where it runs, and why not in <c>AdminSeedHostedService</c>.</b> That service is gated by
/// <c>Seed:Admin:Enabled</c>, which the deployment guidance says to turn back off once the real Super Admins
/// exist — so a step living there would never run on the client's live database, which is the one database this
/// exists for. It is called from <c>CatalogSeedRunner.RunReferenceDataAsync</c> instead, which runs on every start
/// of every environment, already holds the reference-data seed lock and already retries a database that arrives
/// late. Doc 08 §9 lists "default roles and their RoleClaims" under that same idempotent seeding step.
/// </para>
/// <para>
/// <b>A fresh database</b> needs no special case. <c>AdminSeedHostedService</c> creates each role with its full
/// current definition, so the grant finds nothing to add and records the current version; and if this step ran
/// first, there is no role to touch and it records the current version anyway, because a role created afterwards
/// is created complete.
/// </para>
/// <para>
/// <b>Concurrency.</b> Two overlapping starts serialise on <c>sp_getapplock</c> (transaction-owned, so it is released
/// by the commit). The second waits, re-reads the version the first committed and finds nothing to do; a wait that
/// runs out is not an error — the holder is doing this same job — and is reported at Information.
/// <c>UX_RoleClaims_Role_Type_Value</c> stays underneath as the floor, but no unique violation is expected.
/// </para>
/// <para>
/// <b>Signed-in users.</b> Granting a permission to a role changes what its holders may do, so — as doc 08 §3.1
/// DB4 rule 5 asks of any role or permission change — every holder of a role that gained a permission has their
/// <c>SecurityStamp</c> regenerated in the same transaction. That changes the key
/// <see cref="EffectivePermissionsProvider"/> caches under, so the next request on <i>any</i> instance reads the new
/// permission rather than waiting out the cache's five-minute lifetime, and it asks a signed-in session to
/// revalidate within the stamp interval (one minute). At a single-instance start nobody is signed in and the
/// cache is empty, so this costs nothing; it matters for a rolling deployment, where an older instance is still
/// serving.
/// </para>
/// <para>
/// <b>Audit.</b> One <c>PermissionChange</c> row per role that gained anything, with no acting user (the system),
/// holding the role's permission list before and after — doc 08 §3.1 DB4: "every change is written to AuditLogs as
/// <c>PermissionChange</c> with before/after lists".
/// </para>
/// </summary>
public sealed class DefaultRolePermissionSync(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<DefaultRolePermissionSync> logger)
{
    /// <summary>
    /// The <c>ops.Settings</c> key that records the version (doc 08 §9: "recorded in the setting
    /// <c>Seed.Version</c>"). <b>Deliberately not in <see cref="SettingCatalog"/></b>: the catalogue is the list of
    /// keys a screen offers and a write path can reach, and this is system-owned — a Super Admin who could edit it
    /// could make the next deployment re-grant, or skip, a version. A row outside the catalogue is invisible to
    /// every settings read model and write handler by construction (they work from the catalogue's keys), and
    /// <c>SettingsSeeder</c> leaves rows it does not know alone.
    /// </summary>
    public const string VersionKey = "Seed.Version";

    /// <summary>The row's <c>GroupKey</c>; a key starts with its group, as every catalogued key does.</summary>
    public const string VersionGroup = "Seed";

    private const string LockResource = "SpencerDenim:DefaultRoleSeed";

    /// <summary>
    /// Shorter than the ADO command timeout below, so SQL Server's own "busy" answer (-1) is what comes back
    /// rather than the client giving up first — the mistake <c>CatalogSeedHostedService</c> documents.
    /// </summary>
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan LockCommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Brings the default roles up to the current seed version and reports how they differ from the shipped
    /// definitions. Throws on a database failure; <see cref="TryRunAsync"/> is the startup entry point.
    /// </summary>
    public async Task<DefaultRoleSyncResult> RunAsync(CancellationToken cancellationToken)
    {
        // A scope of its own, so this has a change tracker nobody else wrote to and a connection whose
        // transaction nobody else is inside — the caller (the seed runner) holds a different connection with
        // the reference-data lock on it.
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var strategy = dbContext.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(() => ApplyAsync(dbContext, cancellationToken));

        Report(result);
        return result;
    }

    /// <summary>
    /// <see cref="RunAsync"/>, except that a failure is logged and swallowed. This is the startup path: the
    /// reference data it follows is what lets the shop take an order, and a permissions reconciliation that
    /// cannot finish must not turn a shop that works into one that reports itself unready. The cost is that a
    /// failure is only retried on the next start, which is why it is logged at Error with the consequence.
    /// </summary>
    public async Task<DefaultRoleSyncResult?> TryRunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.SyncFailed(exception);
            return null;
        }
    }

    private async Task<DefaultRoleSyncResult> ApplyAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        // The strategy may run this whole block again after a transient failure; whatever the failed attempt
        // left tracked (new claim rows, a changed stamp) must not be sent a second time.
        dbContext.ChangeTracker.Clear();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var currentVersion = DefaultRoleSeedManifest.CurrentVersion;

        if (!await TryTakeLockAsync(dbContext, cancellationToken))
        {
            return DefaultRoleSyncResult.Busy(currentVersion);
        }

        var versionRow = await dbContext.Settings.SingleOrDefaultAsync(
            setting => setting.Key == VersionKey,
            cancellationToken);

        // Taken now: recording the new version overwrites this very row, and the report must name what was found.
        var recordedText = versionRow?.Value;
        var recorded = ReadVersion(recordedText, out var unreadable);
        var appliedVersion = recorded ?? DefaultRoleSeedManifest.BaselineVersion;
        var versionIsNewer = recorded > currentVersion;

        var definitions = DefaultRoleDefinitions.All;
        var definitionNames = definitions.Select(definition => definition.Name).ToList();

        var roles = await dbContext.Roles.AsNoTracking()
            .Where(role => definitionNames.Contains(role.Name!))
            .ToListAsync(cancellationToken);

        var roleIds = roles.ConvertAll(role => role.Id);

        var claims = await dbContext.RoleClaims.AsNoTracking()
            .Where(claim => roleIds.Contains(claim.RoleId) && claim.ClaimType == RoleClaimConfiguration.PermissionClaimType)
            .Select(claim => new { claim.RoleId, claim.ClaimValue })
            .ToListAsync(cancellationToken);

        var grants = new List<DefaultRoleGrant>();
        var differences = new List<DefaultRoleDifference>();
        var absentRoles = new List<string>();
        var unmanagedRoles = new List<string>();
        var rolesChecked = 0;
        var changedRoleIds = new List<int>();

        foreach (var definition in definitions)
        {
            var role = roles.Find(candidate => string.Equals(candidate.Name, definition.Name, StringComparison.OrdinalIgnoreCase));

            if (role is null)
            {
                absentRoles.Add(definition.Name);
                continue;
            }

            // A role of this name that the seeder did not make, or that grants everything, is not the role this
            // step manages. Only a seeded system role is.
            if (!role.IsSystem || role.GrantsAllPermissions)
            {
                unmanagedRoles.Add(definition.Name);
                continue;
            }

            rolesChecked++;

            var held = claims
                .Where(claim => claim.RoleId == role.Id && claim.ClaimValue is not null)
                .Select(claim => claim.ClaimValue!)
                .ToHashSet(StringComparer.Ordinal);

            var before = held.Order(StringComparer.Ordinal).ToList();
            var granted = new List<string>();

            if (!versionIsNewer && appliedVersion < currentVersion)
            {
                foreach (var introduction in DefaultRoleSeedManifest.IntroducedAfter(definition.Name, appliedVersion))
                {
                    // The build must still ship the permission for this role, and it must be one a role may hold
                    // (CK_RoleClaims_NoSecurity); a permission already held is simply not added twice.
                    if (definition.Permissions.Contains(introduction.Permission)
                        && PermissionCatalog.IsGrantableToRoles(introduction.Permission)
                        && held.Add(introduction.Permission))
                    {
                        granted.Add(introduction.Permission);

                        dbContext.RoleClaims.Add(new IdentityRoleClaim<int>
                        {
                            RoleId = role.Id,
                            ClaimType = RoleClaimConfiguration.PermissionClaimType,
                            ClaimValue = introduction.Permission,
                        });
                    }
                }
            }

            if (granted.Count > 0)
            {
                changedRoleIds.Add(role.Id);
                grants.Add(new DefaultRoleGrant(definition.Name, granted));
                dbContext.AuditLogs.Add(BuildAuditRow(role.Id, definition.Name, before, held));
            }

            AddDifferences(differences, definition, held);
        }

        await RefreshSecurityStampsAsync(dbContext, changedRoleIds, cancellationToken);

        // Monotonic: a database never goes back to an older version, so rolling the application back does not
        // make the next roll-forward re-grant what a Super Admin removed in between.
        if (recorded is null || recorded < currentVersion)
        {
            RecordVersion(dbContext, versionRow, currentVersion);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new DefaultRoleSyncResult(
            DefaultRoleSyncOutcome.Completed,
            recorded,
            currentVersion,
            grants,
            differences,
            absentRoles,
            unmanagedRoles,
            rolesChecked,
            definitions.Count,
            unreadable ? recordedText : null);
    }

    /// <summary>
    /// What the shipped definition and the role disagree about, after the grant. Whatever is still missing here
    /// was <i>not</i> re-granted — the version that introduced it was already applied — so it is a deliberate
    /// removal (or a permission the role never had at the baseline), and worth a line somebody reads.
    /// </summary>
    private static void AddDifferences(List<DefaultRoleDifference> differences, RoleDefinition definition, HashSet<string> held)
    {
        var missing = definition.Permissions.Where(permission => !held.Contains(permission)).Order(StringComparer.Ordinal).ToList();
        var extra = held.Where(permission => !definition.Permissions.Contains(permission)).Order(StringComparer.Ordinal).ToList();

        if (missing.Count > 0)
        {
            differences.Add(new DefaultRoleDifference(definition.Name, DefaultRoleDifferenceKind.Missing, missing));
        }

        if (extra.Count > 0)
        {
            differences.Add(new DefaultRoleDifference(definition.Name, DefaultRoleDifferenceKind.Extra, extra));
        }
    }

    private AuditLog BuildAuditRow(int roleId, string roleName, List<string> before, HashSet<string> after) =>
        new()
        {
            OccurredAtUtc = clock.UtcNow.UtcDateTime,

            // No acting user: this is the system bringing a default role up to the build's definition. The
            // before/after lists follow the shape AssignUserRolesCommandHandler writes for a user.
            UserId = null,
            UserEmail = null,
            Action = AuditActions.PermissionChange,
            EntityType = "Role",
            EntityId = roleId.ToString(CultureInfo.InvariantCulture),
            BeforeJson = JsonSerializer.Serialize(new { role = roleName, permissions = before }),
            AfterJson = JsonSerializer.Serialize(new
            {
                role = roleName,
                permissions = after.Order(StringComparer.Ordinal).ToList(),
                source = "default-role seed",
                seedVersion = DefaultRoleSeedManifest.CurrentVersion,
            }),
        };

    /// <summary>
    /// Doc 08 §3.1 DB4 rule 5: a role or permission change regenerates the <c>SecurityStamp</c> of the affected
    /// users. See the class remarks for what that buys at startup.
    /// </summary>
    private static async Task RefreshSecurityStampsAsync(
        AppDbContext dbContext,
        List<int> changedRoleIds,
        CancellationToken cancellationToken)
    {
        if (changedRoleIds.Count == 0)
        {
            return;
        }

        var holderIds = await dbContext.UserRoles.AsNoTracking()
            .Where(userRole => changedRoleIds.Contains(userRole.RoleId))
            .Select(userRole => userRole.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (holderIds.Count == 0)
        {
            return;
        }

        var holders = await dbContext.Users
            .Where(user => holderIds.Contains(user.Id))
            .ToListAsync(cancellationToken);

        foreach (var holder in holders)
        {
            holder.SecurityStamp = Guid.NewGuid().ToString();
            holder.ConcurrencyStamp = Guid.NewGuid().ToString();
        }
    }

    private void RecordVersion(AppDbContext dbContext, Setting? row, int version)
    {
        var value = version.ToString(CultureInfo.InvariantCulture);

        if (row is null)
        {
            dbContext.Settings.Add(new Setting
            {
                Key = VersionKey,
                GroupKey = VersionGroup,
                ValueType = SettingValueType.Int,
                Value = value,
                IsSecret = false,

                // The strictest permission there is: Settings.Security cannot be granted to any role, so even a
                // generic reader that listed every row could show this one only to a Super Admin.
                RequiredPermission = Permissions.Settings.Security,
                Description = "System-owned. The default-role seed version this database has been brought up to "
                    + "(doc 08 §9, N13). Written at startup, never by a screen. Do not edit.",
                UpdatedAtUtc = clock.UtcNow.UtcDateTime,
            });

            return;
        }

        row.Value = value;
        row.UpdatedAtUtc = clock.UtcNow.UtcDateTime;
    }

    /// <summary>
    /// The recorded version, or null when none is recorded or the stored text is not a positive whole number —
    /// the latter treated as "none" (the baseline) and reported, because granting what is missing since the
    /// baseline is additive and so the safe reading of a value that cannot be trusted.
    /// </summary>
    private static int? ReadVersion(string? value, out bool unreadable)
    {
        unreadable = false;

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= 1)
        {
            return version;
        }

        unreadable = true;
        return null;
    }

    /// <summary>
    /// Takes the transaction-scoped application lock. False when it could not be had in
    /// <see cref="LockTimeout"/>, which the caller treats as "another start is doing this" and not as a fault.
    /// </summary>
    private static async Task<bool> TryTakeLockAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();

        await using var command = connection.CreateCommand();
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "sp_getapplock";
        command.CommandType = CommandType.StoredProcedure;
        command.CommandTimeout = (int)LockCommandTimeout.TotalSeconds;

        AddParameter(command, "@Resource", LockResource);
        AddParameter(command, "@LockMode", "Exclusive");
        AddParameter(command, "@LockOwner", "Transaction");
        AddParameter(command, "@LockTimeout", (int)LockTimeout.TotalMilliseconds);

        var result = command.CreateParameter();
        result.ParameterName = "@Result";
        result.DbType = DbType.Int32;
        result.Direction = ParameterDirection.ReturnValue;
        command.Parameters.Add(result);

        await command.ExecuteNonQueryAsync(cancellationToken);

        // 0 granted, 1 granted after waiting; -1 timeout, -2 cancelled, -3 deadlock victim, -999 bad parameter.
        return result.Value is int outcome && outcome >= 0;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>
    /// The startup report. By name, once per start and never per request. Nothing in it is a secret: role and
    /// permission names only.
    /// </summary>
    private void Report(DefaultRoleSyncResult result)
    {
        if (result.Outcome == DefaultRoleSyncOutcome.SkippedLockBusy)
        {
            logger.SyncSkippedBusy();
            return;
        }

        if (result.UnreadableRecordedValue is { } unreadable)
        {
            logger.RecordedVersionUnreadable(unreadable, DefaultRoleSeedManifest.BaselineVersion);
        }

        if (result.PreviousVersion is { } previous && previous > result.CurrentVersion)
        {
            logger.RecordedVersionNewer(previous, result.CurrentVersion);
        }

        foreach (var grant in result.Grants)
        {
            var permissions = string.Join(", ", grant.Permissions);
            logger.PermissionsGranted(grant.RoleName, grant.Permissions.Count, result.PreviousVersion ?? DefaultRoleSeedManifest.BaselineVersion, result.CurrentVersion, permissions);
        }

        foreach (var role in result.AbsentRoles)
        {
            logger.RoleAbsent(role);
        }

        foreach (var role in result.UnmanagedRoles)
        {
            logger.RoleNotManaged(role);
        }

        foreach (var difference in result.Differences)
        {
            var permissions = string.Join(", ", difference.Permissions);

            if (difference.Kind == DefaultRoleDifferenceKind.Missing)
            {
                logger.PermissionsMissing(difference.RoleName, difference.Permissions.Count, permissions);
            }
            else
            {
                logger.PermissionsExtra(difference.RoleName, difference.Permissions.Count, permissions);
            }
        }

        if (result.Differences.Count == 0)
        {
            var grantedCount = result.Grants.Sum(grant => grant.Permissions.Count);
            logger.InStep(result.RolesChecked, result.RolesTotal, result.CurrentVersion, grantedCount);
        }
    }
}

/// <summary>Whether <see cref="DefaultRolePermissionSync"/> did its work or left it to the start that holds the lock.</summary>
public enum DefaultRoleSyncOutcome
{
    /// <summary>The transaction committed (it may have changed nothing).</summary>
    Completed,

    /// <summary>The application lock was not free in time; another start is doing the same job.</summary>
    SkippedLockBusy,
}

/// <summary>The direction of a difference between a default role and its shipped definition.</summary>
public enum DefaultRoleDifferenceKind
{
    /// <summary>The definition includes it and the role does not. Not re-granted: its version was already applied.</summary>
    Missing,

    /// <summary>The role holds it and the definition does not — added by a Super Admin after seeding.</summary>
    Extra,
}

/// <summary>Permissions newly granted to one default role.</summary>
public sealed record DefaultRoleGrant(string RoleName, IReadOnlyList<string> Permissions);

/// <summary>Permissions on which one default role and its shipped definition disagree, in one direction.</summary>
public sealed record DefaultRoleDifference(string RoleName, DefaultRoleDifferenceKind Kind, IReadOnlyList<string> Permissions);

/// <summary>What <see cref="DefaultRolePermissionSync.RunAsync"/> did and found.</summary>
/// <param name="Outcome">Whether the work was done.</param>
/// <param name="PreviousVersion">The version recorded before this run; null when none was (or it was unreadable).</param>
/// <param name="CurrentVersion">The build's <see cref="DefaultRoleSeedManifest.CurrentVersion"/>.</param>
/// <param name="Grants">Permissions added this run, per role.</param>
/// <param name="Differences">What still differs from the shipped definitions after the grant.</param>
/// <param name="AbsentRoles">Default roles that do not exist (never created by this step).</param>
/// <param name="UnmanagedRoles">Roles of a default role's name that are not seeded system roles, left alone.</param>
/// <param name="RolesChecked">How many default roles were found and compared.</param>
/// <param name="RolesTotal">How many default roles the build ships.</param>
/// <param name="UnreadableRecordedValue">The stored text when it was not a version number; otherwise null.</param>
public sealed record DefaultRoleSyncResult(
    DefaultRoleSyncOutcome Outcome,
    int? PreviousVersion,
    int CurrentVersion,
    IReadOnlyList<DefaultRoleGrant> Grants,
    IReadOnlyList<DefaultRoleDifference> Differences,
    IReadOnlyList<string> AbsentRoles,
    IReadOnlyList<string> UnmanagedRoles,
    int RolesChecked,
    int RolesTotal,
    string? UnreadableRecordedValue)
{
    internal static DefaultRoleSyncResult Busy(int currentVersion) =>
        new(DefaultRoleSyncOutcome.SkippedLockBusy, null, currentVersion, [], [], [], [], 0, 0, null);
}
