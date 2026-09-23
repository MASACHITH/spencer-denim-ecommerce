using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Abstractions;
using SpencerDenim.Application.Catalog.Contracts;

namespace SpencerDenim.Application.Catalog.Products;

/// <summary>A validated request for one product page (PDP-01..06).</summary>
public sealed record GetProductBySlugQuery
{
    /// <summary><c>catalog.Products.Slug</c> is <c>varchar(220)</c>.</summary>
    public const int MaxSlugLength = 220;

    private GetProductBySlugQuery(string slug) => Slug = slug;

    /// <summary>
    /// The normalised slug: lower-case letters, digits and single hyphens. Because it is checked
    /// rather than merely trimmed, the API can name it in a 404 without echoing the raw URL.
    /// </summary>
    public string Slug { get; }

    /// <summary>
    /// Validates and normalises the slug from the URL, using the same rule
    /// <see cref="ProductListQuery.Create"/> applies to the listing's slugs, so a slug the listing
    /// links to is always a slug the product page accepts. Empty, too long or not a slug at all is a
    /// 400 naming <c>slug</c>; an unknown but well-formed slug is the handler's 404.
    /// </summary>
    public static Result<GetProductBySlugQuery> Create(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Result.Failure<GetProductBySlugQuery>("slug", "slug is required.");
        }

        var errors = new List<ValidationError>();
        var normalised = ProductListQuery.NormaliseSlug(slug, "slug", errors, MaxSlugLength);

        return normalised is null
            ? Result.Failure<GetProductBySlugQuery>(errors.AsReadOnly())
            : Result.Success(new GetProductBySlugQuery(normalised));
    }
}

/// <summary>
/// Reads the product page payload. A slug that is simply unknown (or belongs to an unpublished or
/// deleted product) yields <c>null</c>, which the endpoint turns into a 404.
/// </summary>
public sealed class GetProductBySlugQueryHandler(IProductCatalogReader reader)
    : IQueryHandler<GetProductBySlugQuery, ProductDetailDto?>
{
    public Task<ProductDetailDto?> HandleAsync(GetProductBySlugQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.GetBySlugAsync(query.Slug, cancellationToken);
    }
}
