using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Content;

/// <summary>
/// Reads the homepage's admin-managed content (HOME-01..10) and the announcement strip (GLB-03).
/// <para>
/// Two queries for the homepage — the sections, then every live item of those sections — and the
/// tree is joined in memory. The whole homepage is a few dozen rows, so a query per section would
/// be ten round trips for a page that fits in two.
/// </para>
/// <para>
/// The schedule is applied in SQL, with the same comparisons <see cref="ContentSchedule"/> makes, so
/// a row that is off or out of its window never crosses the wire. A unit test pins the rule; an
/// integration test pins that the SQL agrees with it.
/// </para>
/// </summary>
internal sealed class HomeContentReader(AppDbContext dbContext, IOptions<CatalogOptions> options)
    : IHomeContentReader
{
    private readonly CatalogOptions _options = options.Value;

    public async Task<IReadOnlyList<HomeSectionContent>> GetLiveSectionsAsync(
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var sections = await dbContext.HomeSections
            .AsNoTracking()
            .Where(section => section.IsEnabled
                && (section.StartsAtUtc == null || section.StartsAtUtc <= utcNow)
                && (section.EndsAtUtc == null || section.EndsAtUtc > utcNow))
            .OrderBy(section => section.SortOrder)
            .ThenBy(section => section.Id)
            .Select(section => new SectionRow(
                section.Id,
                section.SectionType,
                section.Title,
                section.Subtitle,
                section.SortOrder,
                section.SettingsJson))
            .ToListAsync(cancellationToken);

        if (sections.Count == 0)
        {
            return [];
        }

        var sectionIds = sections.Select(section => section.Id).ToList();

        var items = await dbContext.HomeSectionItems
            .AsNoTracking()
            .Where(item => sectionIds.Contains(item.HomeSectionId)
                && item.IsEnabled
                && (item.StartsAtUtc == null || item.StartsAtUtc <= utcNow)
                && (item.EndsAtUtc == null || item.EndsAtUtc > utcNow))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new ItemRow(
                item.Id,
                item.HomeSectionId,
                item.Title,
                item.Subtitle,
                item.Body,
                item.ButtonLabel,
                item.LinkUrl,
                // A soft-deleted or still-processing asset has no file to serve, so it is read as
                // "no image" rather than as a URL that 404s on the shopper's screen. The global
                // query filter already hides soft-deleted assets; the status check is this reader's.
                item.ImageAsset != null && item.ImageAsset.Status == MediaAssetStatus.Ready
                    ? new MediaRef(
                        item.ImageAsset.StorageKey,
                        item.ImageAsset.PublicId,
                        item.ImageAsset.RenditionSetVersion,
                        item.ImageAsset.Width)
                    : null,
                item.ImageAsset != null ? item.ImageAsset.AltText : null,
                item.MobileImageAsset != null && item.MobileImageAsset.Status == MediaAssetStatus.Ready
                    ? new MediaRef(
                        item.MobileImageAsset.StorageKey,
                        item.MobileImageAsset.PublicId,
                        item.MobileImageAsset.RenditionSetVersion,
                        item.MobileImageAsset.Width)
                    : null,
                item.MobileImageAsset != null ? item.MobileImageAsset.AltText : null,
                item.VideoAsset != null && item.VideoAsset.Status == MediaAssetStatus.Ready
                    ? item.VideoAsset.StorageKey
                    : null,
                item.VideoAsset != null && item.VideoAsset.PosterAsset != null
                    ? item.VideoAsset.PosterAsset.StorageKey
                    : null,
                item.IconKey,
                item.Department != null && item.Department.IsActive ? item.Department.Slug : null,
                item.Department != null && item.Department.IsActive ? item.Department.Name : null,
                item.Product != null && item.Product.IsPublished ? item.Product.Slug : null,
                item.SortOrder))
            .ToListAsync(cancellationToken);

        // Which rendition an item's image wants depends on how its section draws it, and the item row
        // does not carry that — a hero is full-bleed, a department or social tile is a grid cell. The
        // items were filtered to these section ids, so the lookup cannot miss; if it ever did, a tile's
        // sizes are the safe answer, because guessing full-bleed would hand a phone the 2000 px file.
        var fullBleedSections = sections
            .Where(section => IsFullBleed(section.SectionType))
            .Select(section => section.Id)
            .ToHashSet();

        var itemsBySection = items
            .GroupBy(item => item.HomeSectionId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => ToDto(item, fullBleedSections.Contains(group.Key)))
                    .ToList());

        return sections
            .Select(section => new HomeSectionContent(
                section.Id,
                section.SectionType,
                section.Title,
                section.Subtitle,
                section.SortOrder,
                section.SettingsJson,
                itemsBySection.TryGetValue(section.Id, out var sectionItems)
                    ? sectionItems
                    : []))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Four small aggregates rather than one query over a union: each reads a handful of rows through
    /// <c>IX (IsEnabled, SortOrder)</c> and the item ones join their section, so an item inside a section
    /// that is switched off — which cannot change the page — is not a boundary.
    /// </summary>
    public async Task<DateTime?> GetNextScheduleBoundaryAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var sections = dbContext.HomeSections.AsNoTracking().Where(section => section.IsEnabled);

        var items = dbContext.HomeSectionItems
            .AsNoTracking()
            .Where(item => item.IsEnabled && item.HomeSection!.IsEnabled);

        DateTime?[] candidates =
        [
            await sections
                .Where(section => section.StartsAtUtc > utcNow)
                .MinAsync(section => section.StartsAtUtc, cancellationToken),
            await sections
                .Where(section => section.EndsAtUtc > utcNow)
                .MinAsync(section => section.EndsAtUtc, cancellationToken),
            await items
                .Where(item => item.StartsAtUtc > utcNow)
                .MinAsync(item => item.StartsAtUtc, cancellationToken),
            await items
                .Where(item => item.EndsAtUtc > utcNow)
                .MinAsync(item => item.EndsAtUtc, cancellationToken),
        ];

        return candidates.Where(candidate => candidate is not null).Min();
    }

    public async Task<IReadOnlyList<AnnouncementDto>> GetLiveAnnouncementsAsync(
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var messages = await dbContext.AnnouncementMessages
            .AsNoTracking()
            .Where(message => message.IsActive
                && message.Message != string.Empty
                && (message.StartsAtUtc == null || message.StartsAtUtc <= utcNow)
                && (message.EndsAtUtc == null || message.EndsAtUtc > utcNow))
            .OrderBy(message => message.SortOrder)
            .ThenBy(message => message.Id)
            .Select(message => new AnnouncementDto(message.Id, message.Message, message.LinkUrl))
            .ToListAsync(cancellationToken);

        // A message of nothing but spaces is not caught by the SQL comparison above, and it would
        // render as a blank strip across every page of the site (lesson L1).
        return messages
            .Where(message => !string.IsNullOrWhiteSpace(message.Message))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    /// Whether a section draws its image edge to edge. Those are the only places on the home page where
    /// a rendition above <c>large</c> can be the right file; everything else is a tile in a grid and
    /// takes a product card's sizes.
    /// </summary>
    private static bool IsFullBleed(HomeSectionType sectionType) => sectionType
        is HomeSectionType.HeroVideo
        or HomeSectionType.CampaignBanner
        or HomeSectionType.ActivewearFeature;

    private HomeSectionItemDto ToDto(ItemRow item, bool isFullBleed)
    {
        // The mobile image is by definition the narrow-viewport one, so it never needs more than
        // `large` however the section draws the desktop image.
        var image = MediaUrlBuilder.ToImageSources(
            _options.MediaBaseUrl,
            item.Image,
            isFullBleed ? MediaRenditions.Large : MediaRenditions.Card,
            isFullBleed ? MediaRenditions.Zoom : MediaRenditions.Large);

        var mobileImage = MediaUrlBuilder.ToImageSources(
            _options.MediaBaseUrl,
            item.MobileImage,
            MediaRenditions.Card,
            MediaRenditions.Large);

        return new HomeSectionItemDto(
            item.Id,
            item.Title,
            item.Subtitle,
            item.Body,
            item.ButtonLabel,
            item.LinkUrl,
            image.Url,
            image.SrcSet,
            item.ImageAltText,
            mobileImage.Url,
            mobileImage.SrcSet,
            item.MobileImageAltText,
            MediaUrlBuilder.ToPublicUrl(_options.MediaBaseUrl, item.VideoKey),
            MediaUrlBuilder.ToPublicUrl(_options.MediaBaseUrl, item.VideoPosterKey),
            item.IconKey,
            item.DepartmentSlug,
            item.DepartmentName,
            item.ProductSlug,
            item.SortOrder);
    }

    /// <summary>One <c>content.HomeSections</c> row, as the projection returns it.</summary>
    private sealed record SectionRow(
        int Id,
        HomeSectionType SectionType,
        string? Title,
        string? Subtitle,
        short SortOrder,
        string? SettingsJson);

    /// <summary>One <c>content.HomeSectionItems</c> row, with storage keys not yet turned into URLs.</summary>
    private sealed record ItemRow(
        int Id,
        int HomeSectionId,
        string? Title,
        string? Subtitle,
        string? Body,
        string? ButtonLabel,
        string? LinkUrl,
        MediaRef? Image,
        string? ImageAltText,
        MediaRef? MobileImage,
        string? MobileImageAltText,
        string? VideoKey,
        string? VideoPosterKey,
        string? IconKey,
        string? DepartmentSlug,
        string? DepartmentName,
        string? ProductSlug,
        short SortOrder);
}
