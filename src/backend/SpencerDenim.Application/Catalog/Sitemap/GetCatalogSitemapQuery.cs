using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Catalog.Sitemap;

/// <summary>Query behind <c>GET /api/catalog/sitemap</c>, which <c>sitemap.xml</c> is rendered from (GLB-11).</summary>
public sealed record GetCatalogSitemapQuery;

/// <summary>
/// Reads every address a crawler may be told about. The whole of the work is in the reader; this
/// handler exists so the endpoint depends on a use case rather than on infrastructure, as every other
/// catalogue endpoint does.
/// </summary>
public sealed class GetCatalogSitemapQueryHandler(ISitemapCatalogReader reader)
    : IQueryHandler<GetCatalogSitemapQuery, CatalogSitemapDto>
{
    public Task<CatalogSitemapDto> HandleAsync(GetCatalogSitemapQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.GetSitemapAsync(cancellationToken);
    }
}
