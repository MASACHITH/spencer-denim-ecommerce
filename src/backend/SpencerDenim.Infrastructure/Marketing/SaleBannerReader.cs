using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Marketing.SaleBanner;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Marketing;

/// <summary>
/// The EF Core side of <see cref="ISaleBannerReader"/>: the live campaign's banner for the Sale page.
/// </summary>
/// <remarks>
/// <b>The window is written out here rather than evaluated in memory</b>, and it is the same predicate as
/// <c>CampaignWindow.IsLive</c> (<c>ContentSchedule.IsLive</c>): active, start inclusive, end exclusive.
/// It has to be SQL, because the point is to read one row and not every campaign the shop has ever run,
/// and it is the shape <c>IX_SaleCampaigns_Window</c> (filtered on <c>IsActive = 1 AND IsDeleted = 0</c>)
/// exists for. The integration suite pins the boundary behaviour so the two definitions cannot drift.
/// <para>
/// <c>IsDeleted</c> is excluded both ways, by the global filter and by the predicate, for the reason
/// <see cref="SaleCampaignAdminReader"/> gives.
/// </para>
/// </remarks>
internal sealed class SaleBannerReader(AppDbContext dbContext, IOptions<CatalogOptions> catalogOptions)
    : ISaleBannerReader
{
    public async Task<SaleBannerImageDto?> GetLiveBannerAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var row = await dbContext.SaleCampaigns
            .AsNoTracking()
            .Where(campaign => campaign.IsActive
                && !campaign.IsDeleted
                && campaign.StartsAtUtc <= utcNow
                && campaign.EndsAtUtc > utcNow
                && campaign.BannerAsset != null
                && campaign.BannerAsset.Status == MediaAssetStatus.Ready
                && campaign.BannerAsset.MediaType == MediaAssetType.Image
                && campaign.BannerAsset.Visibility == MediaVisibility.Public)

            // The most recent start, then the higher id: see ISaleBannerReader for why.
            .OrderByDescending(campaign => campaign.StartsAtUtc)
            .ThenByDescending(campaign => campaign.Id)
            .Select(campaign => new
            {
                campaign.BannerAsset!.StorageKey,
                campaign.BannerAsset.PublicId,
                campaign.BannerAsset.RenditionSetVersion,
                campaign.BannerAsset.Width,
                campaign.BannerAsset.Height,
                campaign.BannerAsset.AltText,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        // Drawn across the full width of the listing container, like a department's banner, so it is the
        // other storefront slot whose candidates may run to `zoom`.
        var sources = MediaUrlBuilder.ToImageSources(
            catalogOptions.Value.MediaBaseUrl,
            new MediaRef(row.StorageKey, row.PublicId, row.RenditionSetVersion, row.Width),
            MediaRenditions.Large,
            MediaRenditions.Zoom);

        if (sources.Url is null)
        {
            return null;
        }

        return new SaleBannerImageDto(
            sources.Url,
            sources.SrcSet,
            row.Width,
            row.Height,
            row.AltText?.Trim() ?? string.Empty);
    }
}
