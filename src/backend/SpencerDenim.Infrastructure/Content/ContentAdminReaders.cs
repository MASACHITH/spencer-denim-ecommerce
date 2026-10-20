using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Content;
using SpencerDenim.Application.Content.Admin;
using SpencerDenim.Application.Content.Admin.Abstractions;
using SpencerDenim.Application.Content.Admin.Announcements;
using SpencerDenim.Application.Content.Admin.Home;
using SpencerDenim.Application.Content.Admin.MegaMenu;
using SpencerDenim.Application.Content.Admin.SizeGuides;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Media;
using SpencerDenim.Infrastructure.Catalog;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Content;

/// <summary>
/// The read side of the ADM-17 homepage, announcement and size-guide screens (#192) and the mega-menu cards (#215).
/// <para>
/// <b>Unlike <see cref="HomeContentReader"/>, nothing here filters on the switch or the schedule.</b> A
/// screen that could not see a block that is switched off could not switch it on — the same reason
/// <c>IAdminContentReader</c> does not hide a draft page. What is live right now is worked out per row as
/// <c>IsLiveNow</c>, with the one rule <see cref="ContentSchedule"/>, so the screen and the storefront
/// cannot disagree about it.
/// </para>
/// </summary>
internal sealed class ContentAdminReaders(AppDbContext dbContext, IOptions<CatalogOptions> options)
    : IHomeContentAdminReader, IAnnouncementAdminReader, ISizeGuideAdminReader, IMegaMenuCardAdminReader
{
    private readonly string? _mediaBaseUrl = options.Value.MediaBaseUrl;

    // ------------------------------------------------------------------ homepage

    public async Task<AdminHomeContentDto> GetAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var sections = await dbContext.HomeSections
            .AsNoTracking()
            .OrderBy(section => section.SortOrder)
            .ThenBy(section => section.Id)
            .ToListAsync(cancellationToken);

        var items = await dbContext.HomeSectionItems
            .AsNoTracking()
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Id)
            .Select(item => new ItemRow(
                item.HomeSectionId,
                item.Id,
                item.Title,
                item.Subtitle,
                item.Body,
                item.ButtonLabel,
                item.LinkUrl,
                item.ImageAsset != null
                    ? new AssetRow(
                        new MediaRef(item.ImageAsset.StorageKey, item.ImageAsset.PublicId, item.ImageAsset.RenditionSetVersion, item.ImageAsset.Width),
                        item.ImageAsset.AltText,
                        null)
                    : null,
                item.MobileImageAsset != null
                    ? new AssetRow(
                        new MediaRef(item.MobileImageAsset.StorageKey, item.MobileImageAsset.PublicId, item.MobileImageAsset.RenditionSetVersion, item.MobileImageAsset.Width),
                        item.MobileImageAsset.AltText,
                        null)
                    : null,
                item.VideoAsset != null
                    ? new AssetRow(
                        new MediaRef(item.VideoAsset.StorageKey, item.VideoAsset.PublicId, null, null),
                        null,
                        item.VideoAsset.PosterAsset != null ? item.VideoAsset.PosterAsset.StorageKey : null)
                    : null,
                item.DepartmentId,
                item.Product != null ? item.Product.Slug : null,
                item.IconKey,
                item.SortOrder,
                item.IsEnabled,
                item.StartsAtUtc,
                item.EndsAtUtc))
            .ToListAsync(cancellationToken);

        var departments = await dbContext.Departments
            .AsNoTracking()
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Name)
            .Select(department => new AdminHomeDepartmentDto(department.Id, department.Name, department.Slug, department.IsActive))
            .ToListAsync(cancellationToken);

        var itemsBySection = items.ToLookup(item => item.SectionId);

        var dtos = sections
            .Select(section =>
            {
                var settings = HomeSectionSettings.Parse(section.SettingsJson);
                var rule = HomeBlockRules.For(section.SectionType);

                return new AdminHomeSectionDto(
                    section.Id,
                    section.SectionType,
                    section.Title,
                    section.Subtitle,
                    section.IsEnabled,
                    section.SortOrder,
                    ContentFieldRules.ToColomboText(section.StartsAtUtc),
                    ContentFieldRules.ToColomboText(section.EndsAtUtc),
                    rule.UsesSectionField(HomeBlockRules.SectionFields.Mode) ? settings.Mode : null,
                    rule.UsesSectionField(HomeBlockRules.SectionFields.Count) ? settings.Count : null,
                    rule.UsesSectionField(HomeBlockRules.SectionFields.ListingUrl) ? settings.ListingUrl : null,
                    rule.UsesSectionField(HomeBlockRules.SectionFields.LinkLabel) ? settings.LinkLabel : null,
                    rule.UsesSectionField(HomeBlockRules.SectionFields.Overlay) ? settings.Overlay : null,
                    section.IsLiveAt(utcNow),
                    AdminHomeSectionRulesDto.From(rule),
                    itemsBySection[section.Id].Select(item => ToDto(item, utcNow)).ToList(),
                    Convert.ToBase64String(section.RowVersion ?? []));
            })
            .ToList();

        return new AdminHomeContentDto(dtos, departments, HomeBlockRules.BenefitIcons);
    }

    private AdminHomeSectionItemDto ToDto(ItemRow item, DateTime utcNow) => new(
        item.Id,
        item.Title,
        item.Subtitle,
        item.Body,
        item.ButtonLabel,
        item.LinkUrl,
        ToImage(item.Image),
        ToImage(item.MobileImage),
        item.Video is { } video
            ? new AdminVideoRefDto(
                video.Media.PublicId,
                MediaUrlBuilder.ToPublicUrl(_mediaBaseUrl, video.Media.StorageKey),
                MediaUrlBuilder.ToPublicUrl(_mediaBaseUrl, video.PosterKey))
            : null,
        item.DepartmentId,
        item.ProductSlug,
        item.IconKey,
        item.SortOrder,
        item.IsEnabled,
        ContentFieldRules.ToColomboText(item.StartsAtUtc),
        ContentFieldRules.ToColomboText(item.EndsAtUtc),
        ContentSchedule.IsLive(item.IsEnabled, item.StartsAtUtc, item.EndsAtUtc, utcNow));

    private AdminMediaRefDto? ToImage(AssetRow? asset) =>
        asset is null
            ? null
            : new AdminMediaRefDto(
                asset.Media.PublicId,
                MediaUrlBuilder.ToRenditionUrl(_mediaBaseUrl, asset.Media, MediaRenditions.Card),
                asset.AltText);

    // ------------------------------------------------------------------ announcements

    public async Task<IReadOnlyList<AdminAnnouncementDto>> ListAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var rows = await dbContext.AnnouncementMessages
            .AsNoTracking()
            .OrderBy(message => message.SortOrder)
            .ThenBy(message => message.Id)
            .ToListAsync(cancellationToken);

        return rows
            .Select(message => new AdminAnnouncementDto(
                message.Id,
                message.Message,
                message.LinkUrl,
                message.SortOrder,
                message.IsActive,
                ContentFieldRules.ToColomboText(message.StartsAtUtc),
                ContentFieldRules.ToColomboText(message.EndsAtUtc),
                // The storefront also drops a blank line (HomeContentReader), so "live" means the same here.
                message.IsLiveAt(utcNow) && !string.IsNullOrWhiteSpace(message.Message),
                AnnouncementVersion.Of(message.CreatedAtUtc, message.UpdatedAtUtc)))
            .ToList()
            .AsReadOnly();
    }

    // ------------------------------------------------------------------ size guides

    public async Task<IReadOnlyList<AdminSizeGuideListItemDto>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.SizeGuides
            .AsNoTracking()
            .OrderBy(guide => guide.Name)
            .ThenBy(guide => guide.Id)
            .Select(guide => new
            {
                guide.Id,
                guide.Name,
                Categories = dbContext.Categories.Count(category => category.SizeGuideId == guide.Id),
                Products = dbContext.Products.Count(product => product.SizeGuideId == guide.Id),
                HasImage = guide.ImageAssetId != null,
                guide.UpdatedAtUtc,
                guide.RowVersion,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new AdminSizeGuideListItemDto(
                row.Id,
                row.Name,
                row.Categories,
                row.Products,
                row.HasImage,
                row.UpdatedAtUtc,
                Convert.ToBase64String(row.RowVersion ?? [])))
            .ToList()
            .AsReadOnly();
    }

    public async Task<AdminSizeGuideDto?> GetAsync(int sizeGuideId, CancellationToken cancellationToken)
    {
        var row = await dbContext.SizeGuides
            .AsNoTracking()
            .Where(guide => guide.Id == sizeGuideId)
            .Select(guide => new
            {
                guide.Id,
                guide.Name,
                guide.IntroHtml,
                guide.TableJson,
                guide.MeasuringTipsHtml,
                Image = guide.ImageAsset != null
                    ? new AssetRow(
                        new MediaRef(guide.ImageAsset.StorageKey, guide.ImageAsset.PublicId, guide.ImageAsset.RenditionSetVersion, guide.ImageAsset.Width),
                        guide.ImageAsset.AltText,
                        null)
                    : null,
                Categories = dbContext.Categories.Count(category => category.SizeGuideId == guide.Id),
                Products = dbContext.Products.Count(product => product.SizeGuideId == guide.Id),
                guide.RowVersion,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new AdminSizeGuideDto(
                row.Id,
                row.Name,
                row.IntroHtml,
                SizeGuideTable.Parse(row.TableJson),
                row.MeasuringTipsHtml,
                ToImage(row.Image),
                row.Categories,
                row.Products,
                Convert.ToBase64String(row.RowVersion ?? []),
                SizeGuideLimitsDto.Current);
    }

    // ------------------------------------------------------------------ mega-menu cards (#215)

    async Task<AdminMegaMenuContentDto> IMegaMenuCardAdminReader.GetAsync(DateTime utcNow, CancellationToken cancellationToken)
    {
        var departments = await dbContext.Departments
            .AsNoTracking()
            .OrderBy(department => department.SortOrder)
            .ThenBy(department => department.Id)
            .Select(department => new
            {
                department.Id,
                department.Name,
                department.Slug,
                department.IsActive,
                department.Description,
                Hero = department.HeroImageAsset != null
                    ? new AssetRow(
                        new MediaRef(department.HeroImageAsset.StorageKey, department.HeroImageAsset.PublicId, department.HeroImageAsset.RenditionSetVersion, department.HeroImageAsset.Width),
                        department.HeroImageAsset.AltText,
                        null)
                    : null,
            })
            .ToListAsync(cancellationToken);

        var cards = await dbContext.MegaMenuPromoCards
            .AsNoTracking()
            .OrderBy(card => card.DepartmentId)
            .ThenBy(card => card.SortOrder)
            .ThenBy(card => card.Id)
            .Select(card => new
            {
                card.Id,
                card.DepartmentId,
                card.Title,
                card.Subtitle,
                card.ButtonLabel,
                card.LinkUrl,
                Image = card.ImageAsset != null
                    ? new AssetRow(
                        new MediaRef(card.ImageAsset.StorageKey, card.ImageAsset.PublicId, card.ImageAsset.RenditionSetVersion, card.ImageAsset.Width),
                        card.ImageAsset.AltText,
                        null)
                    : null,
                card.SortOrder,
                card.IsActive,
                card.StartsAtUtc,
                card.EndsAtUtc,
                PictureUsable = card.ImageAsset != null
                    && card.ImageAsset.Status == MediaAssetStatus.Ready
                    && card.ImageAsset.Visibility == MediaVisibility.Public
                    && card.ImageAsset.MediaType == MediaAssetType.Image,
                card.RowVersion,
            })
            .ToListAsync(cancellationToken);

        // The storefront's own rule, asked in the same query shape the departments feed asks it, so "on the
        // menu now" here is exactly the card a shopper sees.
        var shown = (await dbContext.MegaMenuPromoCards
                .AsNoTracking()
                .Where(MegaMenuCardVisibility.DrawableAt(utcNow))
                .OrderBy(card => card.SortOrder)
                .ThenBy(card => card.Id)
                .Select(card => new { card.Id, card.DepartmentId })
                .ToListAsync(cancellationToken))
            .GroupBy(card => card.DepartmentId)
            .Select(group => group.First().Id)
            .ToHashSet();

        var byDepartment = cards.ToLookup(card => card.DepartmentId);

        var dtos = departments
            .Select(department => new AdminMegaMenuDepartmentDto(
                department.Id,
                department.Name,
                department.Slug,
                department.IsActive,
                department.Description,
                ToImage(department.Hero),
                byDepartment[department.Id]
                    .Select(card => new AdminMegaMenuCardDto(
                        card.Id,
                        card.DepartmentId,
                        card.Title,
                        card.Subtitle,
                        card.ButtonLabel,
                        card.LinkUrl,
                        ToImage(card.Image),
                        card.SortOrder,
                        card.IsActive,
                        ContentFieldRules.ToColomboText(card.StartsAtUtc),
                        ContentFieldRules.ToColomboText(card.EndsAtUtc),
                        ContentSchedule.IsLive(card.IsActive, card.StartsAtUtc, card.EndsAtUtc, utcNow),
                        shown.Contains(card.Id),
                        card.IsActive && card.EndsAtUtc is { } ends && ends <= utcNow,
                        ContentSchedule.IsLive(card.IsActive, card.StartsAtUtc, card.EndsAtUtc, utcNow) && !card.PictureUsable,
                        Convert.ToBase64String(card.RowVersion ?? [])))
                    .ToList()))
            .ToList();

        return new AdminMegaMenuContentDto(dtos, MegaMenuPromoCardLimits.MaxCardsPerDepartment);
    }

    private sealed record AssetRow(MediaRef Media, string? AltText, string? PosterKey);

    private sealed record ItemRow(
        int SectionId,
        int Id,
        string? Title,
        string? Subtitle,
        string? Body,
        string? ButtonLabel,
        string? LinkUrl,
        AssetRow? Image,
        AssetRow? MobileImage,
        AssetRow? Video,
        int? DepartmentId,
        string? ProductSlug,
        string? IconKey,
        short SortOrder,
        bool IsEnabled,
        DateTime? StartsAtUtc,
        DateTime? EndsAtUtc);
}
