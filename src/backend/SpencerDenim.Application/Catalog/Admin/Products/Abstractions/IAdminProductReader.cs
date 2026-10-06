using SpencerDenim.Application.Catalog.Admin.Products.Contracts;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Catalog.Admin.Products.Abstractions;

/// <summary>
/// Read port for the ADM-10 product screens (W1-03).
/// <para>
/// Separate from <see cref="SpencerDenim.Application.Catalog.Abstractions.IProductCatalogReader"/> for the
/// same reason <see cref="Admin.Abstractions.IAdminCatalogReader"/> is separate from the storefront's
/// department reader: that reader answers "what should the shop show", which means it drops every
/// unpublished, soft-deleted or deactivated-category row. An admin products screen built on it could only
/// list the products that are already live and could never reach one to publish it.
/// </para>
/// </summary>
public interface IAdminProductReader
{
    /// <summary>One page of the product list, newest first within the requested sort.</summary>
    Task<PagedResult<AdminProductListItemDto>> ListAsync(
        AdminProductListCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>
    /// One product with its whole aggregate, or <c>null</c> when no live product has that id. Soft-deleted
    /// products are not returned: ADM-10 has no restore, so a removed product is gone rather than hidden.
    /// </summary>
    Task<AdminProductDetailDto?> GetAsync(int productId, CancellationToken cancellationToken);

    /// <summary>Everything the form has to choose from (doc 11 P1 steps 6–12).</summary>
    Task<ProductFormOptionsDto> GetFormOptionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Products matching a typed term, for the related-products picker (doc 11 P1 step 12).
    /// <para>
    /// A search rather than a full list, because the catalogue is the one admin list that is not a small
    /// seeded table: sending every product to render a picker is the payload that grows without limit.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<AdminRelatedProductDto>> SearchForRelatedAsync(
        string? term,
        int? excludeProductId,
        int take,
        CancellationToken cancellationToken);
}

/// <summary>
/// What the admin product list is asking for (ADM-10's "list/search/filter").
/// </summary>
/// <param name="Published">
/// <c>true</c> for published only, <c>false</c> for unpublished only, <c>null</c> for both. Three states
/// rather than a bool, because "both" is the default an admin wants and a bool cannot say it.
/// </param>
/// <param name="Page">One-based.</param>
public sealed record AdminProductListCriteria(
    string? Term,
    int? DepartmentId,
    int? CategoryId,
    bool? Published,
    AdminProductSort Sort,
    int Page,
    int PageSize);

/// <summary>How the admin product list is ordered.</summary>
public enum AdminProductSort
{
    /// <summary>Most recently edited first, falling back to creation — the list an admin at work wants.</summary>
    RecentlyUpdated = 0,

    Name,

    ProductCode,

    PriceAscending,

    PriceDescending,
}
