using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Admin;

/// <summary>
/// Whether reviews wait for a moderator (doc 08 §3.8 group <c>Reviews</c>, doc 11 P12; issue #189).
/// </summary>
/// <param name="RequireApproval">
/// The new value, or <see langword="null"/> when the request did not send the field (not part of this save).
/// </param>
/// <param name="RowVersion">The <c>Reviews</c> save unit's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit row and the per-row permission check.</param>
public sealed record UpdateReviewSettingsCommand(bool? RequireApproval, string? RowVersion, AdminActor Actor);

/// <summary>
/// Saves <c>Reviews.RequireApproval</c>, writing one audit row when it changes (doc 08 §3.8 DB2).
/// </summary>
/// <remarks>
/// <b>The order of the checks is <c>UpdateTrafficSettingsCommandHandler</c>'s</b>: seeded, then concurrency, then
/// the row's own permission, then the diff, then save. <c>Settings.Edit</c> per doc 08 §3.8, and no
/// sensitive-change alert: the key is not on N2's list. Nothing caches the value — every submission reads the
/// row — so a save is in force for the next review written.
/// </remarks>
public sealed class UpdateReviewSettingsCommandHandler(
    ISettingsStore store,
    IAuditLogWriter auditLog,
    IEffectivePermissionsProvider permissionsProvider,
    IClock clock)
    : ICommandHandler<UpdateReviewSettingsCommand, SettingsWriteResult>
{
    /// <summary>The body field for <c>Reviews.RequireApproval</c>.</summary>
    public const string RequireApprovalField = "requireApproval";

    public async Task<SettingsWriteResult> HandleAsync(
        UpdateReviewSettingsCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var rows = await store.ListAsync(
            SettingCatalog.KeysInSaveUnitFor(SettingKeys.Reviews.Group),
            cancellationToken);

        var row = rows.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, SettingKeys.Reviews.RequireApproval, StringComparison.OrdinalIgnoreCase));

        if (row is null)
        {
            return new SettingsWriteResult.NotSeeded(
                $"This setting does not exist in this database: {SettingKeys.Reviews.RequireApproval}. "
                + "Run the settings seeder before using this screen.");
        }

        if (!SettingsGroupVersion.Matches(rows, command.RowVersion))
        {
            return new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                SettingFieldRules.ReadRowVersion(command.RowVersion) is null
                    ? "This request did not carry the settings' current version, so it cannot be applied "
                      + "safely. Reload the page and try again."
                    : "Somebody else changed the review settings while you were editing them. Reload the page "
                      + "to see their change, then re-apply yours.");
        }

        if (command.RequireApproval is not { } requireApproval)
        {
            return new SettingsWriteResult.Unchanged();
        }

        if (command.Actor.UserId is not { } userId)
        {
            return new SettingsWriteResult.Forbidden([row.Key]);
        }

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(userId, cancellationToken);

        if (!permissions.Has(row.RequiredPermission))
        {
            return new SettingsWriteResult.Forbidden([row.Key]);
        }

        var after = requireApproval ? "true" : "false";
        var now = clock.UtcNow.UtcDateTime;

        if (SettingsAuditTrail.ForValueChange(row, after, command.Actor, now) is not { } audit)
        {
            return new SettingsWriteResult.Unchanged();
        }

        row.Value = after;
        row.UpdatedAtUtc = now;
        row.UpdatedByUserId = command.Actor.UserId;
        auditLog.Enlist(audit);

        var outcome = await store.SaveAsync(command.Actor.UserId, cancellationToken);

        return outcome == SettingsSaveOutcome.ConcurrencyConflict
            ? new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                "Somebody else changed the review settings while your change was being saved, so nothing was "
                + "written. Reload the page and try again.")
            : new SettingsWriteResult.Succeeded([row.Key]);
    }
}
