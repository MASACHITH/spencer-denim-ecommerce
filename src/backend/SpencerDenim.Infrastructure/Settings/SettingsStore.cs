using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Settings.Abstractions;
using SpencerDenim.Domain.Media;
using SpencerDenim.Domain.Settings;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Settings;

/// <summary>
/// The EF Core side of <see cref="ISettingsStore"/> (ADM-22).
/// <para>
/// It hands out tracked rows and keeps one unit of work, so the handlers decide what a change is and this
/// class decides only how it reaches SQL Server. The one judgement it makes is the one the database alone
/// can make: a row somebody else changed first.
/// </para>
/// </summary>
internal sealed class SettingsStore(AppDbContext dbContext, CatalogOptions catalogOptions) : ISettingsStore
{
    public async Task<IReadOnlyList<Setting>> ListAsync(
        IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            return [];
        }

        // Tracked: the handlers mutate what comes back and SaveAsync commits it.
        return await dbContext.Settings
            .Where(setting => keys.Contains(setting.Key))
            .OrderBy(setting => setting.Key)
            .ToListAsync(cancellationToken);
    }

    public Task<SettingImageAsset?> FindUsableImageAsync(Guid publicId, CancellationToken cancellationToken) =>
        FindAsync(asset => asset.PublicId == publicId, cancellationToken);

    public Task<SettingImageAsset?> FindUsableImageByIdAsync(int assetId, CancellationToken cancellationToken) =>
        FindAsync(asset => asset.Id == assetId, cancellationToken);

    public async Task ReplaceMediaUsageAsync(
        string settingKey,
        string usageRole,
        int? newAssetId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(settingKey);
        ArgumentException.ThrowIfNullOrEmpty(usageRole);

        // Every row for this owner and role, whichever asset it names. Keyed on the owner rather than on
        // "the asset I think is there": a row left behind by an earlier bug, or two rows from a race, both
        // get cleared, and the invariant afterwards is "at most one usage row for this key and role"
        // whatever the state was before.
        var existing = await dbContext.MediaUsages
            .Where(usage =>
                usage.OwnerType == MediaUsageOwnerTypes.Setting
                && usage.OwnerId == settingKey
                && usage.UsageRole == usageRole)
            .ToListAsync(cancellationToken);

        // Nothing to do when the row that is there already names the asset wanted: deleting and
        // re-inserting it would move CreatedAtUtc and make the library's "used in…" panel claim the logo
        // was attached just now.
        if (newAssetId is { } wanted && existing.Count == 1 && existing[0].MediaAssetId == wanted)
        {
            return;
        }

        dbContext.MediaUsages.RemoveRange(existing);

        if (newAssetId is { } assetId)
        {
            dbContext.MediaUsages.Add(new MediaUsage
            {
                MediaAssetId = assetId,
                OwnerType = MediaUsageOwnerTypes.Setting,

                // Doc 08 §3.3: "the owning entity's ID, or the setting key". Both columns are varchar(100),
                // which is exactly the width of ops.Settings.Key — so no key can be silently truncated into
                // a usage row that guards the wrong setting.
                OwnerId = settingKey,
                UsageRole = usageRole,
            });
        }
    }

    public async Task<SettingsSaveOutcome> SaveAsync(int? actingUserId, CancellationToken cancellationToken)
    {
        // Settings is not an [A] table — doc 08 §3.8 gives it UpdatedAtUtc/UpdatedByUserId but not the
        // Created pair — so the audit-column interceptor does not stamp it and the handlers set those two
        // columns themselves. The parameter stays because it is the port's contract and because the next
        // settings group may well own an [A] row.
        _ = actingUserId;

        if (!dbContext.ChangeTracker.HasChanges())
        {
            return SettingsSaveOutcome.NothingToSave;
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return SettingsSaveOutcome.Saved;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Nothing was written: the UPDATE's RowVersion predicate matched no row, so SQL Server applied
            // none of the batch — the audit row and the usage rewrite included. The caller reloads and
            // re-applies (doc 08 §1.5).
            return SettingsSaveOutcome.ConcurrencyConflict;
        }
    }

    /// <summary>
    /// The rendition a logo is linked at.
    /// <para>
    /// <c>card</c> (600 px) rather than <c>thumb</c> (160 px): the header draws the logo at roughly 150 px
    /// wide, and 160 px is exactly wrong for it — correct at one device pixel per CSS pixel and soft on
    /// every phone, which is most of this shop's traffic (doc 08 §3.3's <c>srcset</c> note). 600 px covers
    /// a 150 px slot at DPR 3 with nothing to spare and nothing wasted, and the encoder clamps rather than
    /// upscales, so a small flat logo stays small.
    /// </para>
    /// </summary>
    private const string LogoRendition = MediaRenditions.Card;

    /// <summary>
    /// One usable image, projected and then mapped <b>in memory</b>.
    /// <para>
    /// The URL is built after materialisation on purpose: <c>MediaUrlBuilder</c> walks the rendition set and
    /// clamps widths, none of which SQL Server can express — composing it into the LINQ tree would either
    /// fail to translate or silently drag the whole table into the client. Every other reader in this
    /// assembly does the same thing for the same reason.
    /// </para>
    /// <para>
    /// <b>Four conditions, each preventing a distinct broken header.</b> <c>Image</c>, because a
    /// <c>MediaAssetRef</c> has to be drawable and a video is not. <c>Public</c>, because a private asset
    /// lives on a volume the reverse proxy does not mount (doc 08 §3.3), so a logo pointed at one would 404
    /// on every page. <c>Ready</c>, because a <c>Processing</c> or <c>Failed</c> row has no file behind it —
    /// the rule <c>MediaRef</c> documents and <c>PublicMediaFiles</c> enforces from the serving end. And not
    /// soft-deleted, which the global query filter does; named here so the reason is on the record rather
    /// than inherited silently.
    /// </para>
    /// </summary>
    private async Task<SettingImageAsset?> FindAsync(
        System.Linq.Expressions.Expression<Func<MediaAsset, bool>> predicate,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.MediaAssets
            .AsNoTracking()
            .Where(asset =>
                asset.MediaType == MediaAssetType.Image
                && asset.Visibility == MediaVisibility.Public
                && asset.Status == MediaAssetStatus.Ready)
            .Where(predicate)
            .Select(asset => new
            {
                asset.Id,
                asset.PublicId,
                asset.StorageKey,
                asset.RenditionSetVersion,
                asset.Width,
                asset.Height,
                asset.AltText,
                asset.OriginalFileName,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var media = new MediaRef(row.StorageKey, row.PublicId, row.RenditionSetVersion, row.Width);

        return new SettingImageAsset(
            row.Id,
            row.PublicId,
            MediaUrlBuilder.ToRenditionUrl(catalogOptions.MediaBaseUrl, media, LogoRendition),
            row.Width,
            row.Height,
            row.AltText,
            row.OriginalFileName);
    }
}
