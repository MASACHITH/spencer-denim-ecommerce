using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Content.Abstractions;
using SpencerDenim.Application.Content.Contracts;
using SpencerDenim.Domain.Content;

namespace SpencerDenim.Application.Content.Home;

/// <summary>Query behind <c>GET /api/content/home</c> (HOME-01..10).</summary>
public sealed record GetHomePageQuery;

/// <summary>
/// Assembles the homepage: the live sections from the content store, then the products for the rows
/// that are drawn from a listing (HOME-03 New Arrivals, HOME-04 Trending in auto mode, HOME-07 Sale).
/// <para>
/// <b>Why the products are resolved here and not by the page.</b> The homepage is server-rendered
/// and is the page search engines and first-time shoppers read, so what matters is the time to the
/// first byte of real content. Letting the page call <c>/api/catalog/products</c> for each row would
/// add three HTTP round trips *inside* the render — the storefront's SSR process to the API and back
/// — before the page could stabilise, and would make the row and its "view all" link two separate
/// definitions of the same listing that can drift apart. Resolving them here costs three SQL queries
/// on one connection inside one request, reuses the listing query the catalogue already has
/// (filters, sorting, stock, pricing and badges included), and guarantees the row is the head of the
/// listing its link opens.
/// </para>
/// <para>
/// The three queries run one after another, not in parallel: they share a
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/>, which is not thread-safe.
/// </para>
/// </summary>
public sealed class GetHomePageQueryHandler(
    IHomeContentReader content,
    IProductCatalogReader products,
    CatalogOptions catalogOptions,
    IClock clock)
    : IQueryHandler<GetHomePageQuery, HomePageDto>
{
    /// <summary>The section types whose content is a row of products rather than a row of tiles.</summary>
    private static readonly HomeSectionType[] ProductBacked =
    [
        HomeSectionType.NewArrivals,
        HomeSectionType.TrendingItems,
        HomeSectionType.SaleSection,
    ];

    public async Task<HomePageDto> HandleAsync(GetHomePageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var utcNow = clock.UtcNow.UtcDateTime;
        var sections = await content.GetLiveSectionsAsync(utcNow, cancellationToken);

        var resolved = new List<HomeSectionDto>(sections.Count);
        foreach (var section in sections)
        {
            var settings = HomeSectionSettings.Parse(section.SettingsJson);

            resolved.Add(new HomeSectionDto(
                section.Id,
                section.Type,
                section.Title,
                section.Subtitle,
                section.SortOrder,
                settings.ListingUrl,
                settings.LinkLabel,
                settings.Overlay,
                section.Items,
                await ResolveProductsAsync(section, settings, cancellationToken)));
        }

        return new HomePageDto(resolved);
    }

    /// <summary>
    /// The head of the listing this row points at, or nothing.
    /// <para>
    /// "Nothing" covers three cases, all of which the storefront renders as no section at all rather
    /// than as an empty shelf: the section is not a product row, it is set to show its own items
    /// instead, or its <c>listingUrl</c> is missing or unusable. The last one matters because the
    /// URL is typed by a person: a filter this catalogue cannot answer is a 400 on the listing page,
    /// and it must not be a 500 on the homepage.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<ProductCardDto>> ResolveProductsAsync(
        HomeSectionContent section,
        HomeSectionSettings settings,
        CancellationToken cancellationToken)
    {
        if (!ProductBacked.Contains(section.Type) || settings.Mode != HomeSectionMode.Auto)
        {
            return [];
        }

        var criteria = ListingUrl.ToCriteria(settings.ListingUrl, settings.Count);
        if (criteria is null)
        {
            return [];
        }

        var listQuery = ProductListQuery.Create(criteria, catalogOptions);
        if (!listQuery.IsSuccess)
        {
            return [];
        }

        var page = await products.ListAsync(listQuery.Value, cancellationToken);

        return page.IsSuccess ? page.Value.Items : [];
    }
}
