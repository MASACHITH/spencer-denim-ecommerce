using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Catalog.Products;

/// <summary>Query behind <c>GET /api/catalog/products/{slug}</c> (PDP-01..06).</summary>
/// <param name="Slug">The product slug from the URL.</param>
public sealed record GetProductBySlugQuery(string Slug)
{
    /// <summary><c>catalog.Products.Slug</c> is <c>varchar(220)</c>.</summary>
    public const int MaxSlugLength = 220;
}

/// <summary>
/// Reads the product page payload. A slug that is too long, empty or malformed is a 400; a slug that
/// is simply unknown (or belongs to an unpublished or deleted product) yields <c>null</c> → 404.
/// </summary>
public sealed class GetProductBySlugQueryHandler(IProductCatalogReader reader)
    : IQueryHandler<GetProductBySlugQuery, Result<ProductDetailDto?>>
{
    public async Task<Result<ProductDetailDto?>> HandleAsync(
        GetProductBySlugQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var slug = query.Slug?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(slug))
        {
            return Result.Failure<ProductDetailDto?>("slug", "slug is required.");
        }

        if (slug.Length > GetProductBySlugQuery.MaxSlugLength)
        {
            return Result.Failure<ProductDetailDto?>(
                "slug",
                $"slug must be {GetProductBySlugQuery.MaxSlugLength} characters or fewer.");
        }

        var product = await reader.GetBySlugAsync(slug, cancellationToken);
        return Result.Success<ProductDetailDto?>(product);
    }
}
