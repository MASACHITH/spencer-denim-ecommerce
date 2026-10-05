using System.Globalization;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Media;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Admin;

/// <summary>
/// Sets or removes the shop's logo (GLB-02, ADM-22).
/// </summary>
/// <param name="HasLogoAssetPublicId">
/// Whether the request carried the field at all.
/// <para>
/// <b>Absent is refused, not read as "remove".</b> A request model whose missing field means "clear it" is
/// W1-12's Blocker shape, and here it would mean a screen that stopped sending the field silently deleted the
/// shop's logo. This route's whole job is an explicit decision, so it insists on one.
/// </para>
/// </param>
/// <param name="LogoAssetPublicId">
/// The media asset to use, or <see langword="null"/> to remove the logo and go back to the text wordmark.
/// <para>
/// The <b>public</b> token, not <c>MediaAssets.Id</c>. The stored value is the internal id, because that is
/// what doc 08 §3.8 specifies and what a <c>MediaUsages</c> row needs — but a client never sees or sends
/// one. Accepting an internal id here would make a sequential integer the key to a write, which is the
/// enumeration hazard <see cref="MediaAsset.PublicId"/> exists to avoid.
/// </para>
/// </param>
/// <param name="RowVersion">The <c>Store</c> group's token, as the screen read it.</param>
/// <param name="Actor">Who is asking, for the audit row.</param>
public sealed record SetStoreLogoCommand(
    bool HasLogoAssetPublicId,
    Guid? LogoAssetPublicId,
    string? RowVersion,
    AdminActor Actor);

/// <summary>
/// The logo's write path, and the one place the owner's actual request is honoured: <i>"the logo will
/// change in future, so the admin upload option should let them remove the existing one and add a new
/// one."</i>
/// <para>
/// <b>So "replace" is three things in one transaction, not one.</b> The setting's value moves, the
/// previous asset's <c>MediaUsages</c> row is released, and the new asset's is claimed. Getting any one of
/// them wrong has a distinct and bad failure: without the release the old logo can never be deleted from
/// the library (409 for ever, naming a setting that no longer points at it — exactly the removal the owner
/// asked to be able to do); without the claim a staff user can delete the <i>live</i> logo and leave a
/// broken image in the header of every page; and without the value nothing changes at all. They commit
/// together, with the audit row (issue #59).
/// </para>
/// </summary>
public sealed class SetStoreLogoCommandHandler(
    ISettingsStore store,
    IAuditLogWriter auditLog,
    ISiteSettingsCache cache,
    IEffectivePermissionsProvider permissionsProvider,
    SpencerDenim.Application.Abstractions.Time.IClock clock)
    : ICommandHandler<SetStoreLogoCommand, SettingsWriteResult>
{
    public async Task<SettingsWriteResult> HandleAsync(
        SetStoreLogoCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The save unit, not the group: the token the read model published covers exactly these rows
        // (SettingCatalog.SaveUnits), and reading a different set here is the mismatch that made every
        // contact-and-social save a 409. For Store the two happen to coincide today — which is precisely why
        // it is read from the shared declaration rather than assumed.
        if (!command.HasLogoAssetPublicId)
        {
            return new SettingsWriteResult.Invalid(
                "logoAssetPublicId",
                "Say which image to use, or null to remove the logo. A request that leaves the field out is "
                + "refused rather than read as 'remove'.");
        }

        var keys = SettingCatalog.KeysInSaveUnitFor(SettingKeys.Store.Group);
        var rows = await store.ListAsync(keys, cancellationToken);

        var row = rows.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, SettingKeys.Store.LogoAssetId, StringComparison.OrdinalIgnoreCase));

        if (row is null)
        {
            return new SettingsWriteResult.NotSeeded(
                $"The setting '{SettingKeys.Store.LogoAssetId}' does not exist in this database. "
                + "Run the settings seeder before using this screen.");
        }

        // Before the asset lookup, so a stale form is told to reload rather than told its asset is fine
        // and then refused anyway. (The order also matters for what the audit row can claim.)
        if (!SettingsGroupVersion.Matches(rows, command.RowVersion))
        {
            return Stale(command.RowVersion);
        }

        int? assetId = null;

        if (command.LogoAssetPublicId is { } publicId)
        {
            var asset = await store.FindUsableImageAsync(publicId, cancellationToken);

            if (asset is null)
            {
                // One message for "no such asset", "that is a video", "that is still processing" and "that
                // is a private asset". Four different reasons, deliberately one answer: the differences
                // between them are facts about the library that an endpoint guarded by Settings.Edit has no
                // business confirming, and the remedy is the same in all four cases.
                return new SettingsWriteResult.Invalid(
                    "logoAssetPublicId",
                    "That image is not in the media library, or is not a ready public image. Upload the "
                    + "logo again.");
            }

            assetId = asset.Id;
        }

        var after = assetId?.ToString(CultureInfo.InvariantCulture);

        var audit = SettingsAuditTrail.ForValueChange(row, after, command.Actor, clock.UtcNow.UtcDateTime);
        if (audit is null)
        {
            // The same asset, or null for null. Nothing to write — and in particular no usage rewrite,
            // which would otherwise delete and re-insert the identical row on every save.
            return new SettingsWriteResult.Unchanged();
        }

        // Doc 08 §3.8 DB2 on the write path: the row carries the permission needed to change it. Read after
        // the diff, so an unchanged logo is never a refusal, and before any mutation, so a refusal leaves
        // nothing half applied. Added in round 1 — the first version never read this column.
        if (command.Actor.UserId is not { } userId)
        {
            return new SettingsWriteResult.Forbidden([row.Key]);
        }

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(userId, cancellationToken);
        if (!permissions.Has(row.RequiredPermission))
        {
            return new SettingsWriteResult.Forbidden([row.Key]);
        }

        row.Value = after;
        row.UpdatedAtUtc = clock.UtcNow.UtcDateTime;
        row.UpdatedByUserId = command.Actor.UserId;

        await store.ReplaceMediaUsageAsync(
            SettingKeys.Store.LogoAssetId,
            MediaUsageRoles.Logo,
            assetId,
            cancellationToken);

        auditLog.Enlist(audit);

        var outcome = await store.SaveAsync(command.Actor.UserId, cancellationToken);

        if (outcome == SettingsSaveOutcome.ConcurrencyConflict)
        {
            return new SettingsWriteResult.Conflict(
                SettingsConflictCodes.StaleRowVersion,
                "Somebody else changed the logo while your change was being saved, so nothing was written. "
                + "Reload the page and try again.");
        }

        // Only after a real commit. Evicting before the save would publish a change that then failed, and
        // the storefront would serve the new logo for up to a minute with no row behind it.
        cache.Evict();

        return new SettingsWriteResult.Succeeded([SettingKeys.Store.LogoAssetId]);
    }

    /// <inheritdoc cref="SettingsGroupVersion.Matches"/>
    private static SettingsWriteResult.Conflict Stale(string? supplied) => new(
        SettingsConflictCodes.StaleRowVersion,
        SettingFieldRules.ReadRowVersion(supplied) is null
            ? "This request did not carry the settings' current version, so it cannot be applied safely. "
              + "Reload the page and try again."
            : "Somebody else changed these settings while you were editing them. Reload the page to see "
              + "their change, then re-apply yours.");
}
