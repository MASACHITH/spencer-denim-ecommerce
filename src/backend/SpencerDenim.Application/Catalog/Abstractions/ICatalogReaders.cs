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
    /// <summary>One page of the listing (PLP-01..07), already filtered, sorted and counted.</summary>
    Task<PagedResult<ProductCardDto>> ListAsync(ProductListQuery query, CancellationToken cancellationToken);

    /// <summary>The product page payload (PDP-01..06), or <c>null</c> when the slug is unknown.</summary>
    Task<ProductDetailDto?> GetBySlugAsync(string slug, CancellationToken cancellationToken);

    /// <summary>Search results (SRCH-02), ordered by relevance unless another sort was asked for.</summary>
    Task<PagedResult<ProductCardDto>> SearchAsync(ProductSearchQuery query, CancellationToken cancellationToken);
}
