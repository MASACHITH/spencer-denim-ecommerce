using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Configurations.Identity;

namespace SpencerDenim.Infrastructure.Identity.Seeding;

/// <summary>
/// Doc 08 §9's versioned seeder step: the permissions added to a default role's shipped definition since a
/// database was created are granted to that role, and nothing else about the role is touched (issue #122,
/// N13).
/// <para>
/// <b>What it does and does not do.</b> It adds <c>identity.RoleClaims</c> rows named by
/// <see cref="DefaultRoleSeedVersions.All"/> for versions above the database's recorded
/// <c>Seed.Version</c>, skipping any the role already holds; it records the new version; and it reports
/// what the default roles are missing against <see cref="DefaultRoleDefinitions"/> so that a silent gap
/// becomes a log line. It never deletes a claim, never creates a role, never touches
/// <see cref="AppRole.GrantsAllPermissions"/> or <see cref="AppRole.IsSystem"/>, and never compares a role
/// against its whole shipped definition in order to "fix" it — that would undo the client's own edits,
/// which is exactly what N13 exists to prevent.
/// </para>
/// <para>
/// <b>Why it is safe although "removed by the client" and "never had it" look identical.</b> It only ever
/// applies increments above the recorded version, and a permission introduced in a version above the one a
/// database holds cannot have been removed by that database's owner — it did not exist for them to remove.
/// See <see cref="DefaultRoleSeedVersions.Baseline"/> for the one case where a marker is absent and why it
/// is sound here.
/// </para>
/// <para>
/// <b>Where it runs, and why not in <see cref="AdminSeedHostedService"/>.</b> It is a step of
/// <c>CatalogSeedRunner.RunReferenceDataAsync</c> — doc 08 §9 puts "default roles and their RoleClaims" in
/// the same idempotent step as the rest of the reference data — so it inherits that phase's
/// <c>sp_getapplock</c> on <c>SpencerDenim:ReferenceDataSeed</c>, its unique-violation retry, its
/// readiness reporting and its retry timer, rather than growing a second copy of all of it. It is also
/// deliberately <b>not</b> behind <c>Seed:Admin:Enabled</c>: that flag is turned off after go-live, which
/// is precisely when an upgrade step has to keep working.
/// </para>
/// <para>
/// <b>Ordering against role creation is not a hazard, and the safety does not come from the order.</b>
/// What makes every interleaving safe is that role creation is always complete-at-current —
/// <see cref="AdminSeedHostedService"/> creates a role from the <i>whole current</i> definition, which
/// already includes every increment — and that the grant below is idempotent per row, with
/// <c>UX_RoleClaims_Role_Type_Value</c> underneath it, so a role created while this is running costs at
/// worst a unique violation the reference-data phase already retries. The version marker only ever
/// governs top-ups of roles that already exist.
/// <para>
/// What the order does decide is what a fresh database <i>observes</i>: nothing sets
/// <c>HostOptions.ServicesStartConcurrently</c>, so hosted services start one at a time in registration
/// order, and <c>AddHostedService&lt;CatalogSeedHostedService&gt;()</c> comes before
/// <c>AddAdminIdentity()</c> in <c>DependencyInjection.AddInfrastructure</c> — so on a fresh database this
/// step runs first, finds no roles and grants nothing, which is the <c>RolesNotPresent</c> line in the
/// log.
/// </para>
/// </para>
/// </summary>
public sealed partial class DefaultRoleSeedUpgrader(
    AppDbContext dbContext,
    ILogger<DefaultRoleSeedUpgrader> logger)
{
    /// <summary>
    /// Applies the pending increments, records the version and reports the drift. One
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>, so the claims and the version marker
    /// commit together or not at all.
    /// </summary>
    public async Task<DefaultRoleSeedUpgradeResult> RunAsync(CancellationToken cancellationToken)
    {
        var versionRow = await dbContext.Settings
            .FirstOrDefaultAsync(setting => setting.Key == SettingKeys.Seed.Version, cancellationToken);

        var recorded = ReadRecordedVersion(versionRow);

        var names = DefaultRoleDefinitions.All.Select(definition => definition.Name).ToList();

        var roles = await dbContext.Roles
            .Where(role => role.Name != null && names.Contains(role.Name))
            .ToListAsync(cancellationToken);

        var byName = roles.ToDictionary(role => role.Name!, StringComparer.OrdinalIgnoreCase);
        var roleIds = roles.ConvertAll(role => role.Id);

        var held = new Dictionary<int, HashSet<string>>();

        if (roleIds.Count > 0)
        {
            var claims = await dbContext.RoleClaims
                .Where(claim =>
                    roleIds.Contains(claim.RoleId)
                    && claim.ClaimType == RoleClaimConfiguration.PermissionClaimType)
                .Select(claim => new { claim.RoleId, claim.ClaimValue })
                .ToListAsync(cancellationToken);

            foreach (var claim in claims.Where(candidate => candidate.ClaimValue is not null))
            {
                if (!held.TryGetValue(claim.RoleId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    held[claim.RoleId] = set;
                }

                set.Add(claim.ClaimValue!);
            }
        }

        var granted = new List<DefaultRoleSeedGrant>();
        var absentRoles = new List<string>();

        foreach (var increment in DefaultRoleSeedVersions.Since(recorded))
        {
            if (!byName.TryGetValue(increment.RoleName, out var role))
            {
                // Not an error and not a gap: the role has not been created yet, and whoever creates it
                // will create it from the whole current definition. Reported so the log says which.
                if (!absentRoles.Contains(increment.RoleName, StringComparer.Ordinal))
                {
                    absentRoles.Add(increment.RoleName);
                }

                continue;
            }

            if (!held.TryGetValue(role.Id, out var current))
            {
                current = new HashSet<string>(StringComparer.Ordinal);
                held[role.Id] = current;
            }

            foreach (var permission in increment.AddedPermissions.OrderBy(value => value, StringComparer.Ordinal))
            {
                // Idempotent against the row, not against the version: a database that already holds the
                // claim — because it was created at this version, or because a previous run was
                // interrupted after the insert and before the marker — gets no second row, and
                // UX_RoleClaims_Role_Type_Value is the backstop if two instances ever reach here at once.
                if (!current.Add(permission))
                {
                    continue;
                }

                dbContext.RoleClaims.Add(new IdentityRoleClaim<int>
                {
                    RoleId = role.Id,
                    ClaimType = RoleClaimConfiguration.PermissionClaimType,
                    ClaimValue = permission,
                });

                granted.Add(new DefaultRoleSeedGrant(increment.Version, increment.RoleName, permission));
            }
        }

        var versionWritten = RecordVersion(versionRow, recorded);

        // The claims and the marker in one transaction. If this throws, nothing moved and the next startup
        // reads the same recorded version and does the same work: the step is restartable at any point.
        if (granted.Count > 0 || versionWritten)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var missing = ScanForDrift(byName, held);

        var result = new DefaultRoleSeedUpgradeResult(
            recorded,
            DefaultRoleSeedVersions.Current,
            granted,
            absentRoles,
            missing);

        Report(result);
        return result;
    }

    /// <summary>
    /// The recorded <c>Seed.Version</c>, or <see cref="DefaultRoleSeedVersions.Baseline"/> when the row is
    /// absent or does not hold an integer.
    /// </summary>
    /// <remarks>
    /// An unparseable value is treated as the baseline rather than as a reason to stop: re-offering every
    /// increment is idempotent, and refusing to run would leave the roles short for as long as the bad
    /// value sat there. It is logged, because somebody wrote it.
    /// </remarks>
    private int ReadRecordedVersion(Setting? versionRow)
    {
        if (versionRow is null)
        {
            return DefaultRoleSeedVersions.Baseline;
        }

        if (int.TryParse(versionRow.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            return version;
        }

        LogUnreadableVersion(logger, SettingKeys.Seed.Version);
        return DefaultRoleSeedVersions.Baseline;
    }

    /// <summary>
    /// Brings <c>Seed.Version</c> up to <see cref="DefaultRoleSeedVersions.Current"/>, answering whether
    /// anything changed.
    /// </summary>
    /// <remarks>
    /// <b>It never moves the recorded version down.</b> A database marked above this build's version was
    /// last seeded by a newer build, so its roles may hold permissions this build has never heard of;
    /// writing a lower number would make a later upgrade re-offer increments the client may have since
    /// removed, which is the one thing this whole mechanism is for. Rolling back is therefore reported and
    /// left alone.
    /// </remarks>
    private bool RecordVersion(Setting? versionRow, int recorded)
    {
        if (recorded > DefaultRoleSeedVersions.Current)
        {
            LogNewerDatabase(logger, recorded, DefaultRoleSeedVersions.Current);
            return false;
        }

        var value = DefaultRoleSeedVersions.Current.ToString(CultureInfo.InvariantCulture);

        if (versionRow is null)
        {
            dbContext.Settings.Add(new Setting
            {
                Key = SettingKeys.Seed.Version,
                GroupKey = SettingKeys.Seed.Group,
                ValueType = SettingValueType.Int,
                Value = value,

                // Settings.Security is never grantable to any role (CK_RoleClaims_NoSecurity, doc 08
                // §3.1), so only a GrantsAll holder could ever hold it — and no screen lists this key
                // anyway, because SettingCatalog does not contain it and every read model starts there.
                // CK_Settings_RequiredPermission refuses an empty string, so the column has to say
                // something; this says "nobody edits this".
                RequiredPermission = Permissions.Settings.Security,
                Description =
                    "The highest default-role permission seed version applied to this database "
                    + "(doc 08 §9). Written by the seeder; not editable.",
            });

            return true;
        }

        if (string.Equals(versionRow.Value, value, StringComparison.Ordinal))
        {
            // Nothing to write. Skipped rather than assigned so an already-correct row produces no UPDATE
            // and does not move its rowversion, which is the reason SettingsSeeder.Realign is a diff.
            return false;
        }

        versionRow.Value = value;
        return true;
    }

    /// <summary>
    /// What each existing default role is missing against <see cref="DefaultRoleDefinitions"/>, by name.
    /// </summary>
    /// <remarks>
    /// <b>This is a report, never a repair.</b> A missing permission has two possible causes that the
    /// database cannot tell apart — the client removed it, or it never arrived — and only one of them is a
    /// defect. Granting it here would resolve the ambiguity the wrong way for a client who had decided
    /// their Store Manager should not refund. So the step above fixes what it can prove is new and this
    /// names the rest.
    /// </remarks>
    private static List<DefaultRoleMissingPermissions> ScanForDrift(
        Dictionary<string, AppRole> byName,
        Dictionary<int, HashSet<string>> held)
    {
        var missing = new List<DefaultRoleMissingPermissions>();

        foreach (var definition in DefaultRoleDefinitions.All)
        {
            if (!byName.TryGetValue(definition.Name, out var role))
            {
                continue;
            }

            var current = held.TryGetValue(role.Id, out var set)
                ? set
                : new HashSet<string>(StringComparer.Ordinal);

            var absent = definition.Permissions
                .Where(permission => !current.Contains(permission))
                .OrderBy(permission => permission, StringComparer.Ordinal)
                .ToList();

            if (absent.Count > 0)
            {
                missing.Add(new DefaultRoleMissingPermissions(definition.Name, absent));
            }
        }

        return missing;
    }

    /// <summary>
    /// Writes the step's one or two lines.
    /// </summary>
    /// <remarks>
    /// <b>The drift line is <see cref="LogLevel.Warning"/>, and the level is the decision.</b> Error is
    /// wrong because a difference is the <i>expected</i> state of a correctly configured shop whose owner
    /// removed a permission on purpose — N13 exists to let them — and this codebase has already paid for
    /// an Error on a healthy startup once (<c>CatalogSeedHostedService</c>'s lock timeout, which printed a
    /// stack trace and the wrong remedy against a database that was fine). Information is wrong because it
    /// is where the startup noise lives: the host logs its counts, its ports and its migrations there, and
    /// this is the one line that distinguishes "the client chose this" from "#122 happened again". Warning
    /// is what an operator greps for after a deploy and what the configured minimum level
    /// (<c>Serilog:MinimumLevel:Default</c> is Information in <c>appsettings.json</c>, so Warning is
    /// emitted) will not swallow. It names the permissions, because "3 missing" tells nobody which
    /// button will refuse.
    /// <para>
    /// A grant is news and gets Information. A run with nothing to grant and nothing missing gets
    /// <see cref="LogLevel.Debug"/>, so the quiet case is quiet.
    /// </para>
    /// </remarks>
    private void Report(DefaultRoleSeedUpgradeResult result)
    {
        // The joins are built inside an IsEnabled guard and passed as locals, not as expressions at the
        // call site (CA1873): a sink that is off should not pay for a string nobody reads.
        if (result.Granted.Count > 0 && logger.IsEnabled(LogLevel.Information))
        {
            var grants = string.Join(
                ", ",
                result.Granted.Select(grant => $"{grant.RoleName}: {grant.Permission}"));

            LogGranted(logger, result.RecordedVersion, result.CurrentVersion, grants);
        }

        foreach (var role in result.RolesNotPresent)
        {
            LogRoleNotPresent(logger, role);
        }

        if (result.MissingPermissions.Count > 0 && logger.IsEnabled(LogLevel.Warning))
        {
            foreach (var role in result.MissingPermissions)
            {
                var permissions = string.Join(", ", role.Permissions);

                LogDrift(logger, role.RoleName, role.Permissions.Count, permissions);
            }
        }

        if (result.Granted.Count == 0 && result.MissingPermissions.Count == 0)
        {
            LogUpToDate(logger, result.CurrentVersion);
        }
    }

    [LoggerMessage(
        EventId = 1310,
        Level = LogLevel.Information,
        Message = "Default role permission seed moved from version {RecordedVersion} to {CurrentVersion} "
            + "and granted {Grants}.")]
    private static partial void LogGranted(
        ILogger logger,
        int recordedVersion,
        int currentVersion,
        string grants);

    [LoggerMessage(
        EventId = 1311,
        Level = LogLevel.Warning,
        Message = "Default role '{RoleName}' is missing {Count} permission(s) this build's defaults grant: "
            + "{Permissions}. Either a staff user removed them deliberately (doc 08 §9 N13 keeps such "
            + "edits, so nothing here will put them back) or they never reached this database. See "
            + "docs/journal/2026-10-09-seed-existing-databases.md for the launch-day check.")]
    private static partial void LogDrift(ILogger logger, string roleName, int count, string permissions);

    [LoggerMessage(
        EventId = 1312,
        Level = LogLevel.Information,
        Message = "Default role '{RoleName}' does not exist yet, so the permission seed had nothing to add "
            + "to it; it will be created with the current defaults.")]
    private static partial void LogRoleNotPresent(ILogger logger, string roleName);

    [LoggerMessage(
        EventId = 1313,
        Level = LogLevel.Debug,
        Message = "Default role permissions are at seed version {CurrentVersion} with no drift.")]
    private static partial void LogUpToDate(ILogger logger, int currentVersion);

    [LoggerMessage(
        EventId = 1314,
        Level = LogLevel.Warning,
        Message = "The setting '{Key}' does not hold an integer, so every default role permission "
            + "increment has been re-offered and the value rewritten.")]
    private static partial void LogUnreadableVersion(ILogger logger, string key);

    [LoggerMessage(
        EventId = 1315,
        Level = LogLevel.Warning,
        Message = "This database records default role permission seed version {RecordedVersion}, which is "
            + "above this build's {CurrentVersion}: it was last seeded by a newer build. The marker has "
            + "been left alone and no permission was granted.")]
    private static partial void LogNewerDatabase(ILogger logger, int recordedVersion, int currentVersion);
}

/// <summary>One permission granted to one default role by one version's increment.</summary>
public sealed record DefaultRoleSeedGrant(int Version, string RoleName, string Permission);

/// <summary>What one existing default role lacks against this build's shipped definition.</summary>
public sealed record DefaultRoleMissingPermissions(string RoleName, IReadOnlyList<string> Permissions);

/// <summary>What <see cref="DefaultRoleSeedUpgrader"/> did and found.</summary>
/// <param name="RecordedVersion">The version the database held before this run.</param>
/// <param name="CurrentVersion">The version this build ships.</param>
/// <param name="Granted">Every claim inserted, in the order it was inserted.</param>
/// <param name="RolesNotPresent">
/// Default roles an increment named that do not exist yet, which is the ordinary state of a fresh database
/// at this point in startup.
/// </param>
/// <param name="MissingPermissions">
/// What each existing default role still lacks against <see cref="DefaultRoleDefinitions"/> after the
/// grants — the drift report, which this step reads out and never acts on.
/// </param>
public sealed record DefaultRoleSeedUpgradeResult(
    int RecordedVersion,
    int CurrentVersion,
    IReadOnlyList<DefaultRoleSeedGrant> Granted,
    IReadOnlyList<string> RolesNotPresent,
    IReadOnlyList<DefaultRoleMissingPermissions> MissingPermissions);
