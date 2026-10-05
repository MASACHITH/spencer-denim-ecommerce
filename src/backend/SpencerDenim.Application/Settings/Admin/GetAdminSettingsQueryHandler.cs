using System.Globalization;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Admin.CurrentUser;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Application.Settings.Contracts;
using SpencerDenim.Domain.Settings;

namespace SpencerDenim.Application.Settings.Admin;

/// <summary>
/// The settings screen's read (ADM-22).
/// </summary>
/// <param name="UserId">
/// The signed-in staff user, whose <i>effective</i> permissions decide which groups come back.
/// <see langword="null"/> — no session — returns no groups at all rather than throwing: the endpoint is
/// already behind <c>Settings.Edit</c>, so this is a second floor rather than the check.
/// </param>
public sealed record GetAdminSettingsQuery(int? UserId);

/// <summary>
/// Reads the settings a caller may see, group by group (ADM-22, doc 08 §3.8 DB2).
/// <para>
/// <b>Permissions are read from the user's roles, never from the principal.</b> That is doc 08 §3.1 R4 and
/// the same rule <c>PermissionAuthorizationHandler</c> follows: a cookie carries claims that were true when
/// it was issued, and a Super Admin who was demoted five minutes ago must not still be reading bank
/// details out of their own cookie.
/// </para>
/// <para>
/// <b>A group the caller may not read is absent, not empty.</b> An empty <c>Bank</c> group would confirm
/// that bank details exist and tell a Store Manager exactly which five keys to go looking for another way.
/// There is nothing to hide today — no <c>Bank</c> row is seeded — which is precisely why the filter is
/// built and tested now, while getting it wrong costs nothing.
/// </para>
/// </summary>
public sealed class GetAdminSettingsQueryHandler(
    ISettingsStore store,
    IEffectivePermissionsProvider permissionsProvider)
    : IQueryHandler<GetAdminSettingsQuery, AdminSettingsDto>
{
    public async Task<AdminSettingsDto> HandleAsync(
        GetAdminSettingsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.UserId is not { } userId)
        {
            return new AdminSettingsDto([], null);
        }

        var permissions = await permissionsProvider.GetEffectivePermissionsAsync(userId, cancellationToken);

        var rows = await store.ListAsync(
            SettingCatalog.All.Select(definition => definition.Key).ToList(),
            cancellationToken);

        // Case-insensitively, to match the database collation (Latin1_General_100_CI_AS_SC). An Ordinal
        // dictionary here is how a row scripted in as `social.facebookurl` becomes invisible to the screen
        // while every save answers NotSeeded -> 500, for ever. See SettingCatalog.ByKey.
        var byKey = rows.ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);

        var groups = new List<AdminSettingGroupDto>();

        foreach (var groupKey in SettingCatalog.Groups)
        {
            // Catalogue order, and only the keys whose row actually exists: an unseeded database yields a
            // screen with fewer boxes rather than a 500, and the write path's NotSeeded answer is what
            // reports the real fault when somebody tries to save.
            var groupRows = SettingCatalog.KeysIn(groupKey)
                .Select(key => byKey.GetValueOrDefault(key))
                .OfType<Setting>()
                .ToList();

            if (groupRows.Count == 0)
            {
                continue;
            }

            // Every key in a group shares a permission in doc 08 §3.8's design, but the check is per row
            // rather than per group: the column is per row, and a group that ever held two permissions
            // would otherwise be all-or-nothing on whichever row was read first.
            var readable = groupRows.Where(row => permissions.Has(row.RequiredPermission)).ToList();

            if (readable.Count == 0)
            {
                continue;
            }

            groups.Add(new AdminSettingGroupDto(
                groupKey,

                // The token covers every row the matching *save* will compare — which for Contact and Social
                // is both groups, because one form saves both (SettingCatalog.SaveUnits). Publishing a
                // per-group token here and comparing a per-form one there is exactly the defect that made
                // every valid save a 409; both sides now read the same declaration.
                //
                // Rows the caller cannot read are included on purpose: a token that covered only the visible
                // ones would differ between two readers with different permissions, and neither could save.
                SettingsGroupVersion.For(
                    SettingCatalog.KeysInSaveUnitFor(groupKey)
                        .Select(key => byKey.GetValueOrDefault(key))
                        .OfType<Setting>()),
                readable.Select(AdminSettingDto.From).ToList()));
        }

        return new AdminSettingsDto(groups, await ReadLogoAsync(byKey, permissions, cancellationToken));
    }

    /// <summary>
    /// Resolves <c>Store.LogoAssetId</c> to something the screen can draw.
    /// <para>
    /// <b>Three ways this legitimately comes back null, all of them ordinary.</b> No logo has been set; the
    /// caller cannot read the <c>Store</c> group; or the value names an asset that is no longer usable
    /// because somebody soft-deleted it from the library. The third one is the interesting case, and
    /// returning null for it is deliberate: the screen then shows "no logo" and an Upload button, which is
    /// recoverable, instead of a broken preview or an error page. It is also why the header's fallback has
    /// to be a real state rather than a transitional one.
    /// </para>
    /// </summary>
    private async Task<AdminLogoDto?> ReadLogoAsync(
        Dictionary<string, Setting> byKey,
        EffectivePermissions permissions,
        CancellationToken cancellationToken)
    {
        if (!byKey.TryGetValue(SettingKeys.Store.LogoAssetId, out var row)
            || !permissions.Has(row.RequiredPermission)
            || !int.TryParse(row.Value, CultureInfo.InvariantCulture, out var assetId))
        {
            return null;
        }

        var asset = await store.FindUsableImageByIdAsync(assetId, cancellationToken);

        return asset is null
            ? null
            : new AdminLogoDto(
                asset.PublicId,
                asset.Url,
                asset.Width,
                asset.Height,
                asset.AltText,
                asset.OriginalFileName);
    }
}
