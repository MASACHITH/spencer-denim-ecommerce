using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Search;

namespace SpencerDenim.Application.Catalog.Abstractions;

/// <summary>
/// Read port for the navigation tree (GLB-05, DEPT-01). Kept separate from
/// <see cref="IProductCatalogReader"/> so a caller that only needs the menu does not depend on the
/// listing contract (ISP, doc 03 §3).
/// </summary>
public interface IDepartmentCatalogReader
{
    /// <summary>Active departments in sort order, each with its active category tree.</summary>
    Task<IReadOnlyList<DepartmentDto>> GetDepartmentsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Read port for the storefront catalogue. Implementations project straight to DTOs with
/// <c>AsNoTracking</c>, so no EF entity ever leaves the infrastructure layer (doc 08 §10.3).
/// </summary>
public interface IProductCatalogReader
{
    /// <summary>
    /// One page of the listing (PLP-01..07), already filtered, sorted and counted, or the reasons the
    /// filters cannot be answered — an ambiguous category slug, for instance (doc 08 §3.2).
    /// </summary>
    Task<Result<PagedResult<ProductCardDto>>> ListAsync(ProductListQuery query, CancellationToken cancellationToken);

    /// <summary>The product page payload (PDP-01..06), or <c>null</c> when the slug is unknown.</summary>
    Task<ProductDetailDto?> GetBySlugAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Search results (SRCH-02), ordered by relevance unless another sort was asked for.</summary>
    Task<Result<PagedResult<ProductCardDto>>> SearchAsync(ProductSearchQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// Read port for the sitemap (GLB-11). Its own port rather than a method on
/// <see cref="IProductCatalogReader"/>: the listing contract is about one page of cards, this is about
/// every address in the shop, and a caller that wants one never wants the other (ISP, doc 03 §3).
/// </summary>
public interface ISitemapCatalogReader
{
    /// <summary>
    /// Every address a crawler may be told about, in one bounded read.
    /// <para>
    /// "Bounded" is part of the contract, not an implementation detail: this is a public endpoint that
    /// enumerates the catalogue, so the product list is capped by <c>Catalog:SitemapMaxProducts</c> and
    /// the answer says when the cap was reached. Nothing here fetches media, variants or stock.
    /// </para>
    /// </summary>
    Task<CatalogSitemapDto> GetSitemapAsync(CancellationToken cancellationToken);
}
