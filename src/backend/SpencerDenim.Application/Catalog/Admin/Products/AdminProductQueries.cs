using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Products.Contracts;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Catalog.Admin.Products;

/// <summary>One page of the admin product list (ADM-10).</summary>
public sealed record GetAdminProductsQuery(AdminProductListCriteria Criteria);

/// <summary>One product with its whole aggregate, for the add/edit form (ADM-10).</summary>
public sealed record GetAdminProductQuery(int ProductId);

/// <summary>Everything the product form has to choose from (ADM-10, doc 11 P1 steps 6–12).</summary>
public sealed record GetProductFormOptionsQuery;

/// <summary>Products matching a typed term, for the related-products picker (PDP-06).</summary>
public sealed record SearchProductsForRelatedQuery(string? Term, int? ExcludeProductId);

/// <summary>
/// Hands the admin product list to the read port.
/// <para>
/// A handler with no logic is still the right shape: it keeps the endpoint depending on
/// <see cref="IQueryHandler{TQuery,TResult}"/> like every other route rather than on an infrastructure
/// adapter, and it is where the page-size bound lives.
/// </para>
/// </summary>
public sealed class GetAdminProductsQueryHandler(IAdminProductReader reader, CatalogOptions options)
    : IQueryHandler<GetAdminProductsQuery, PagedResult<AdminProductListItemDto>>
{
    public Task<PagedResult<AdminProductListItemDto>> HandleAsync(
        GetAdminProductsQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var criteria = query.Criteria;

        // The same bounds the storefront listing uses (doc 08 §7.2), so one admin request cannot ask for the
        // whole catalogue, and a page number below one is read as the first page rather than becoming a
        // negative OFFSET.
        var pageSize = criteria.PageSize <= 0
            ? options.DefaultPageSize
            : Math.Min(criteria.PageSize, options.MaxPageSize);

        return reader.ListAsync(
            criteria with { Page = Math.Max(criteria.Page, 1), PageSize = pageSize },
            cancellationToken);
    }
}

/// <inheritdoc cref="GetAdminProductsQueryHandler"/>
public sealed class GetAdminProductQueryHandler(IAdminProductReader reader)
    : IQueryHandler<GetAdminProductQuery, AdminProductDetailDto?>
{
    public Task<AdminProductDetailDto?> HandleAsync(
        GetAdminProductQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.GetAsync(query.ProductId, cancellationToken);
    }
}

/// <inheritdoc cref="GetAdminProductsQueryHandler"/>
public sealed class GetProductFormOptionsQueryHandler(IAdminProductReader reader)
    : IQueryHandler<GetProductFormOptionsQuery, ProductFormOptionsDto>
{
    public Task<ProductFormOptionsDto> HandleAsync(
        GetProductFormOptionsQuery query,
        CancellationToken cancellationToken) =>
        reader.GetFormOptionsAsync(cancellationToken);
}

/// <inheritdoc cref="GetAdminProductsQueryHandler"/>
public sealed class SearchProductsForRelatedQueryHandler(IAdminProductReader reader)
    : IQueryHandler<SearchProductsForRelatedQuery, IReadOnlyList<AdminRelatedProductDto>>
{
    /// <summary>
    /// How many matches the picker shows. Small on purpose: a picker is for recognising a product, and a
    /// list long enough to need its own scrollbar is a list an admin reads by typing more of the name.
    /// </summary>
    public const int Take = 20;

    public Task<IReadOnlyList<AdminRelatedProductDto>> HandleAsync(
        SearchProductsForRelatedQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.SearchForRelatedAsync(query.Term, query.ExcludeProductId, Take, cancellationToken);
    }
}
