using Microsoft.Extensions.Logging;

namespace SpencerDenim.Infrastructure.Identity.Seeding;

/// <summary>
/// Source-generated log messages for <see cref="DefaultRolePermissionSync"/> (CA1848). Role and permission names
/// only — nothing here is a secret, a user or a request, and every one is written at most once per start.
/// </summary>
internal static partial class DefaultRolePermissionSyncLog
{
    [LoggerMessage(
        EventId = 1501,
        Level = LogLevel.Information,
        Message = "Default role '{Role}' was granted {Count} permission(s) added after default-role seed version "
            + "{FromVersion} (now at {ToVersion}): {Permissions}.")]
    public static partial void PermissionsGranted(
        this ILogger logger,
        string role,
        int count,
        int fromVersion,
        int toVersion,
        string permissions);

    [LoggerMessage(
        EventId = 1502,
        Level = LogLevel.Warning,
        Message = "Default role '{Role}' does not hold {Count} permission(s) its shipped definition includes: "
            + "{Permissions}. They were not granted again because the default-role seed version that introduced "
            + "them has already been applied, so they were removed after the fact (or never held). If staff in this "
            + "role are being refused something, grant the permission to the role by hand.")]
    public static partial void PermissionsMissing(this ILogger logger, string role, int count, string permissions);

    [LoggerMessage(
        EventId = 1503,
        Level = LogLevel.Information,
        Message = "Default role '{Role}' holds {Count} permission(s) its shipped definition does not include "
            + "(added by a Super Admin after seeding, and left alone): {Permissions}.")]
    public static partial void PermissionsExtra(this ILogger logger, string role, int count, string permissions);

    [LoggerMessage(
        EventId = 1504,
        Level = LogLevel.Information,
        Message = "Default roles are in step with the shipped definitions: {Checked} of {Total} default role(s) "
            + "checked at default-role seed version {Version}, {Granted} permission(s) granted this start.")]
    public static partial void InStep(this ILogger logger, int @checked, int total, int version, int granted);

    [LoggerMessage(
        EventId = 1505,
        Level = LogLevel.Information,
        Message = "Default role '{Role}' does not exist, so no permissions were checked or granted for it. "
            + "Roles are created by the admin seed (Seed:Admin:Enabled), never by this step.")]
    public static partial void RoleAbsent(this ILogger logger, string role);

    [LoggerMessage(
        EventId = 1506,
        Level = LogLevel.Warning,
        Message = "A role named '{Role}' exists but is not a seeded system role, so it is treated as a custom role "
            + "and left alone.")]
    public static partial void RoleNotManaged(this ILogger logger, string role);

    [LoggerMessage(
        EventId = 1507,
        Level = LogLevel.Information,
        Message = "Another start is reconciling the default roles' permissions; leaving it to that one.")]
    public static partial void SyncSkippedBusy(this ILogger logger);

    [LoggerMessage(
        EventId = 1508,
        Level = LogLevel.Warning,
        Message = "The recorded default-role seed version ({Recorded}) is newer than this build's ({Current}), "
            + "so nothing was granted. This build is older than the database it is running against.")]
    public static partial void RecordedVersionNewer(this ILogger logger, int recorded, int current);

    [LoggerMessage(
        EventId = 1509,
        Level = LogLevel.Warning,
        Message = "The recorded default-role seed version '{Value}' is not a whole number; treating the database "
            + "as being at version {Baseline} and recording the current one.")]
    public static partial void RecordedVersionUnreadable(this ILogger logger, string value, int baseline);

    [LoggerMessage(
        EventId = 1510,
        Level = LogLevel.Error,
        Message = "Could not reconcile the default roles' permissions with this build. Staff in a default role may "
            + "lack permissions added since their database was created until the next start. The shop is not "
            + "affected.")]
    public static partial void SyncFailed(this ILogger logger, Exception exception);
}
