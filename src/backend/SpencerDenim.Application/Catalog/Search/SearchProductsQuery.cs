using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Catalog.Search;

/// <summary>Runs the <c>LIKE</c> search over <c>catalog.ProductSearchIndex</c> (SRCH-02, doc 08 §7.6).</summary>
public sealed class SearchProductsQueryHandler(IProductCatalogReader reader)
    : IQueryHandler<ProductSearchQuery, Result<PagedResult<ProductCardDto>>>
{
    public Task<Result<PagedResult<ProductCardDto>>> HandleAsync(ProductSearchQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.SearchAsync(query, cancellationToken);
    }
}
