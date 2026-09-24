using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Catalog.Products;

/// <summary>Reads one page of the listing (PLP-01..07).</summary>
public sealed class GetProductsQueryHandler(IProductCatalogReader reader)
    : IQueryHandler<ProductListQuery, Result<PagedResult<ProductCardDto>>>
{
    public Task<Result<PagedResult<ProductCardDto>>> HandleAsync(ProductListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.ListAsync(query, cancellationToken);
    }
}
