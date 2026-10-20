using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Admin.Home;

/// <summary>
/// The whole homepage as the ADM-17 editor loads it (doc 11 P13 part B): every block — switched on or
/// off, inside its schedule or not — with every item, the rule that says what the block lets a staff
/// member edit, and the two lookups the item form needs.
/// </summary>
/// <param name="Sections">All ten blocks, ascending by sort order then id.</param>
/// <param name="Departments">Every department, for the HOME-02 tile picker.</param>
/// <param name="BenefitIcons">The icon keys the storefront's benefits row draws (HOME-09).</param>
public sealed record AdminHomeContentDto(
    IReadOnlyList<AdminHomeSectionDto> Sections,
    IReadOnlyList<AdminHomeDepartmentDto> Departments,
    IReadOnlyList<string> BenefitIcons);

/// <summary>One department, as the tile picker lists it.</summary>
public sealed record AdminHomeDepartmentDto(int Id, string Name, string Slug, bool IsActive);

/// <summary>
/// One homepage block as the editor shows it.
/// </summary>
/// <param name="Id"><c>content.HomeSections.Id</c>.</param>
/// <param name="Type">Which block (HOME-01..10).</param>
/// <param name="Title">Heading.</param>
/// <param name="Subtitle">Standfirst.</param>
/// <param name="IsEnabled">The on/off switch.</param>
/// <param name="SortOrder">Position on the page.</param>
/// <param name="StartsAt">Colombo wall-clock, <c>yyyy-MM-ddTHH:mm</c>, or null for "no start".</param>
/// <param name="EndsAt">Colombo wall-clock, or null for "no end".</param>
/// <param name="Mode">Trending only: drawn from a listing or hand-arranged.</param>
/// <param name="Count">Product rows: how many products.</param>
/// <param name="ListingUrl">Product rows: the listing the row is drawn from and links to.</param>
/// <param name="LinkLabel">Product rows: the label on that link.</param>
/// <param name="Overlay">Hero and banners: <c>dark</c>, <c>light</c> or <c>none</c>.</param>
/// <param name="IsLiveNow">Switched on and inside its schedule right now.</param>
/// <param name="Rules">What this block lets a staff member edit.</param>
/// <param name="Items">Every item, on or off, in sort order.</param>
/// <param name="RowVersion">
/// The block's concurrency token. <b>Item writes send it too</b>: the block is the aggregate, so editing
/// a tile while somebody else rearranges the block is the conflict optimistic concurrency exists to catch
/// (doc 11 P13: "the second is told it changed and must reload").
/// </param>
public sealed record AdminHomeSectionDto(
    int Id,
    HomeSectionType Type,
    string? Title,
    string? Subtitle,
    bool IsEnabled,
    short SortOrder,
    string? StartsAt,
    string? EndsAt,
    HomeSectionMode? Mode,
    int? Count,
    string? ListingUrl,
    string? LinkLabel,
    string? Overlay,
    bool IsLiveNow,
    AdminHomeSectionRulesDto Rules,
    IReadOnlyList<AdminHomeSectionItemDto> Items,
    string RowVersion);

/// <summary><see cref="HomeBlockRule"/>, as the screen reads it.</summary>
public sealed record AdminHomeSectionRulesDto(
    IReadOnlyList<string> SectionFields,
    IReadOnlyList<string> ItemFields,
    IReadOnlyList<string> RequiredItemFields,
    int MaxItems,
    bool LinkMayLeaveSite,
    bool NeedsArtwork)
{
    public static AdminHomeSectionRulesDto From(HomeBlockRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new(rule.SectionFields, rule.ItemFields, rule.RequiredItemFields, rule.MaxItems, rule.LinkMayLeaveSite, rule.NeedsArtwork);
    }
}

/// <summary>One tile, banner, benefit, gallery image or hero, as the editor shows it.</summary>
public sealed record AdminHomeSectionItemDto(
    int Id,
    string? Title,
    string? Subtitle,
    string? Body,
    string? ButtonLabel,
    string? LinkUrl,
    AdminMediaRefDto? Image,
    AdminMediaRefDto? MobileImage,
    AdminVideoRefDto? Video,
    int? DepartmentId,
    string? ProductSlug,
    string? IconKey,
    short SortOrder,
    bool IsEnabled,
    string? StartsAt,
    string? EndsAt,
    bool IsLiveNow);

/// <summary>An image an admin row points at: what to send back, what to show, and its alt text.</summary>
public sealed record AdminMediaRefDto(Guid PublicId, string? Url, string? AltText);

/// <summary>A video an admin row points at, with its poster.</summary>
public sealed record AdminVideoRefDto(Guid PublicId, string? Url, string? PosterUrl);

/// <summary>What a homepage write answers with.</summary>
/// <param name="Id">The section's or the item's id.</param>
/// <param name="RowVersion">The <b>section's</b> new concurrency token, so the screen can keep editing.</param>
public sealed record HomeContentSavedDto(int Id, string? RowVersion);
