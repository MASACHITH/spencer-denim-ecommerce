namespace SpencerDenim.Application.Catalog.Contracts;

/// <summary>One node of the category tree (GLB-05 mega menu, PLP-01 breadcrumb).</summary>
/// <param name="Id">Category ID.</param>
/// <param name="Name">Display name.</param>
/// <param name="Slug">URL segment, unique per department and parent.</param>
/// <param name="Depth">1 for a top-level category, up to 3.</param>
/// <param name="Description">Listing-page intro copy (PLP-01).</param>
/// <param name="ImageUrl">Category tile image: the <c>card</c> rendition, or the original when there is none.</param>
/// <param name="ImageSrcSet">Candidate widths for <paramref name="ImageUrl"/>; null when the asset has no renditions.</param>
/// <param name="ShowInMegaMenu">Whether the desktop mega menu lists it.</param>
/// <param name="MegaMenuColumn">Mega-menu column 1–4, when it is listed.</param>
/// <param name="AgeGroupSlug">Age group for Kids categories (DEPT-01).</param>
/// <param name="Children">Child categories, in sibling order.</param>
public sealed record CategoryNodeDto(
    int Id,
    string Name,
    string Slug,
    byte Depth,
    string? Description,
    string? ImageUrl,
    string? ImageSrcSet,
    bool ShowInMegaMenu,
    byte? MegaMenuColumn,
    string? AgeGroupSlug,
    IReadOnlyList<CategoryNodeDto> Children);

/// <summary>
/// One mega-menu card the storefront may draw for a department (GLB-05, ADM-17; #215). Only cards that are
/// switched on, inside their schedule and carry a ready, public picture are sent, best first; the storefront
/// draws the first. All text is plain and is rendered by interpolation.
/// </summary>
/// <param name="Title">The card's heading.</param>
/// <param name="Subtitle">The line under it, or null.</param>
/// <param name="ButtonLabel">The button's words.</param>
/// <param name="LinkUrl">A path on this site or a full http(s) address, as ADM-17's link rules allow.</param>
/// <param name="ImageUrl">The <c>card</c> rendition, or the original when there is none.</param>
/// <param name="ImageSrcSet">Candidate widths for <paramref name="ImageUrl"/>; null without renditions.</param>
/// <param name="ImageAlt">The picture's alt text from the media library.</param>
/// <param name="StartsAtUtc">The window's start (UTC), or null. The storefront re-checks the window when the menu
/// opens, because it keeps the tree for the life of the page (#215 tester).</param>
/// <param name="EndsAtUtc">The window's end (UTC, exclusive), or null.</param>
public sealed record MegaMenuPromoCardDto(
    int Id,
    string Title,
    string? Subtitle,
    string ButtonLabel,
    string LinkUrl,
    string? ImageUrl,
    string? ImageSrcSet,
    string? ImageAlt,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc);

/// <summary>A department with its whole category tree (GLB-05, DEPT-01).</summary>
/// <param name="HeroImageUrl">
/// The department banner: the <c>large</c> rendition, since it is drawn full-bleed.
/// </param>
/// <param name="HeroImageSrcSet">
/// Candidate widths for <paramref name="HeroImageUrl"/>, up to <c>zoom</c> — a full-bleed banner on a
/// wide retina screen is the one storefront slot that can genuinely use the largest rendition.
/// </param>
/// <param name="PromoCards">
/// The mega-menu cards that may be drawn right now, best first (#215). Empty when there are none, and the
/// storefront then draws the card it always has, from the department's own picture and description.
/// </param>
public sealed record DepartmentDto(
    int Id,
    string Name,
    string Slug,
    string? Description,
    string? HeroImageUrl,
    string? HeroImageSrcSet,
    string? SeoTitle,
    string? SeoDescription,
    IReadOnlyList<CategoryNodeDto> Categories,
    IReadOnlyList<MegaMenuPromoCardDto> PromoCards);
