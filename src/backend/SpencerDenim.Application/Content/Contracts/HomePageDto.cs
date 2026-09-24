using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Contracts;

/// <summary>
/// The whole homepage in one payload (HOME-01..10): the sections that are live right now, in the
/// order the client put them in, each already carrying everything it needs to render.
/// <para>
/// One call, because the alternative is a homepage that cannot be server-rendered without a
/// round trip per block. Sections that are disabled or outside their schedule are not in the list
/// at all — the storefront never has to decide whether something should be on screen.
/// </para>
/// </summary>
/// <param name="Sections">Live sections, ascending by sort order.</param>
public sealed record HomePageDto(IReadOnlyList<HomeSectionDto> Sections);

/// <summary>
/// One live block of the homepage.
/// </summary>
/// <param name="Id"><c>content.HomeSections.Id</c>, so admin can be pointed at the row that drew it.</param>
/// <param name="Type">Which block this is (HOME-01..10).</param>
/// <param name="Title">Heading, or null when the client left it blank.</param>
/// <param name="Subtitle">Standfirst under the heading.</param>
/// <param name="SortOrder">Position on the page. The list is already in this order.</param>
/// <param name="LinkUrl">
/// Where the section's own "view all" goes, and — for a product row — the listing its products were
/// drawn from, so the row and the link can never disagree.
/// </param>
/// <param name="LinkLabel">Label for that link.</param>
/// <param name="Overlay">How text sits over artwork: <c>dark</c>, <c>light</c> or <c>none</c>.</param>
/// <param name="Items">The section's own tiles, benefits, gallery images or hero, already filtered and ordered.</param>
/// <param name="Products">
/// Product cards for a product-backed row (HOME-03, HOME-04 in auto mode, HOME-07). Empty for every
/// other type, and empty when the row could not be resolved — which the storefront renders as
/// nothing rather than as an empty shelf.
/// </param>
public sealed record HomeSectionDto(
    int Id,
    HomeSectionType Type,
    string? Title,
    string? Subtitle,
    short SortOrder,
    string? LinkUrl,
    string? LinkLabel,
    string? Overlay,
    IReadOnlyList<HomeSectionItemDto> Items,
    IReadOnlyList<ProductCardDto> Products);

/// <summary>
/// One tile inside a section: a department tile, a trending tile, a benefit, a gallery image, a
/// banner or the hero.
/// </summary>
/// <param name="Id"><c>content.HomeSectionItems.Id</c>.</param>
/// <param name="Title">Tile heading.</param>
/// <param name="Subtitle">Tile standfirst.</param>
/// <param name="Body">Longer copy — a benefit's sentence (HOME-09).</param>
/// <param name="ButtonLabel">Call to action, when the client set one.</param>
/// <param name="LinkUrl">Where the tile goes. A trending tile carries a filtered listing URL (HOME-04).</param>
/// <param name="ImageUrl">Artwork for tablet and desktop.</param>
/// <param name="ImageAltText">Alt text for <paramref name="ImageUrl"/>, from the media library.</param>
/// <param name="MobileImageUrl">Artwork for phones, and the hero's still under reduced motion (HOME-01).</param>
/// <param name="MobileImageAltText">Alt text for <paramref name="MobileImageUrl"/>.</param>
/// <param name="VideoUrl">The hero video (HOME-01). Null everywhere else.</param>
/// <param name="VideoPosterUrl">Its poster frame. Doc 08 §3.3 makes a poster mandatory on a video asset.</param>
/// <param name="IconKey">Names an icon in the storefront's own set (HOME-09), never a file.</param>
/// <param name="DepartmentSlug">The department a HOME-02 tile opens.</param>
/// <param name="DepartmentName">Its name, so a tile with no title of its own still has one.</param>
/// <param name="ProductSlug">The product a tile points at, when the client picked one.</param>
/// <param name="SortOrder">Position within the section. The list is already in this order.</param>
public sealed record HomeSectionItemDto(
    int Id,
    string? Title,
    string? Subtitle,
    string? Body,
    string? ButtonLabel,
    string? LinkUrl,
    string? ImageUrl,
    string? ImageAltText,
    string? MobileImageUrl,
    string? MobileImageAltText,
    string? VideoUrl,
    string? VideoPosterUrl,
    string? IconKey,
    string? DepartmentSlug,
    string? DepartmentName,
    string? ProductSlug,
    short SortOrder);
