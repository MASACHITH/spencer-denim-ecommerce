using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Application.Catalog.Departments;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Search;
using SpencerDenim.Application.Catalog.Sitemap;

namespace SpencerDenim.Api.Endpoints.Catalog;

/// <summary>
/// The public, read-only catalogue API (GLB-05, PLP-01..07, CARD-01, PDP-01..06, SRCH-02).
/// No authentication: everything here is what an anonymous visitor may see. Admin write endpoints
/// arrive with ADM-10/ADM-11 behind permission policies.
/// </summary>
internal static class CatalogEndpoints
{
    /// <summary>
    /// Every catalogue endpoint answers HEAD as well as GET. Uptime monitors and link checkers lead
    /// with HEAD, and ASP.NET Core answers it from the GET handler with the body dropped, so mapping
    /// only GET turned a health probe into a 405.
    /// </summary>
    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head];

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var catalog = endpoints.MapGroup("/api/catalog").WithTags("Catalog");

        catalog.MapMethods("/departments", ReadMethods, GetDepartmentsAsync)
            .WithName("GetDepartments")
            .WithSummary("Departments with their category tree.")
            .WithDescription("Drives the mega menu (GLB-05) and the department landing pages (DEPT-01). Inactive departments and categories are omitted.")
            .Produces<IReadOnlyList<DepartmentDto>>();

        catalog.MapMethods("/products", ReadMethods, GetProductsAsync)
            .WithName("GetProducts")
            .WithSummary("Product listing with filters, sorting and paging.")
            .WithDescription(
                "PLP-01..07. Repeatable filters (ageGroup, size, colour, material, fit, occasion) accept the parameter more than once. " +
                "Paging is offset based: doc 08 §7.1 specifies OFFSET/FETCH with an Id tiebreaker, and PLP-06 needs a live total count.")
            .Produces<PagedResult<ProductCardDto>>()
            .ProducesValidationProblem();

        catalog.MapMethods("/products/{slug}", ReadMethods, GetProductBySlugAsync)
            .WithName("GetProductBySlug")
            .WithSummary("One product page payload.")
            .WithDescription("PDP-01..06: gallery, colours, sizes, variants with stock status, price and discount, attributes and related products.")
            .Produces<ProductDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();

        catalog.MapMethods("/search", ReadMethods, SearchProductsAsync)
            .WithName("SearchProducts")
            .WithSummary("Search products by name, category, colour and attributes.")
            .WithDescription("SRCH-02. All of up to five tokens must match the denormalised search text (doc 08 §7.6). The listing filters and sorts apply on top.")
            .Produces<ProductSearchResponse>()
            .ProducesValidationProblem();

        MapInternalCatalogEndpoints(endpoints);

        return endpoints;
    }

    /// <summary>
    /// Catalogue reads the storefront's own <b>server</b> makes, on a prefix the public origin does not
    /// route (GLB-11).
    /// <para>
    /// <b>Why this is not under <c>/api</c>.</b> Doc 03 §4 has Caddy routing <c>/admin</c>, <c>/api</c>
    /// and <c>/media</c> to their targets and <b>everything else to the SSR server</b> — which proxies
    /// neither, so an address under <c>/internal</c> on the public origin reaches the Angular router and
    /// answers 404. The health-check paths already rely on exactly this, so it is the codebase's own
    /// convention rather than a new one. The SSR process asks over <c>API_INTERNAL_URL</c> on the
    /// container network, where it is reachable.
    /// </para>
    /// <para>
    /// <b>It is surface reduction, not authentication, and the difference matters.</b> Nothing here
    /// checks who is asking; a reverse proxy configured to forward <c>/internal</c> would publish it. So
    /// the endpoint is also safe to publish: all three of its queries are bounded
    /// (<c>Catalog:SitemapMaxDepartments</c>, <c>SitemapMaxCategories</c>, <c>SitemapMaxProducts</c>) and
    /// a short output-cache policy means a repeated request is answered from memory rather than from
    /// three fresh projections. The prefix removes the surface; the bounds and the cache mean losing the
    /// prefix is not a defect. <b>Whoever writes the Caddyfile (W0-15) must not add a blanket rule that
    /// forwards this.</b>
    /// </para>
    /// <para>
    /// There is no rate limiting anywhere in this solution except <c>NewsletterSignupThrottle</c>, and
    /// adding a general one is an architectural decision rather than this endpoint's to make — which is
    /// the other reason the cheap local answers (do not publish it, bound it, cache it) are the ones
    /// taken here.
    /// </para>
    /// </summary>
    private static void MapInternalCatalogEndpoints(IEndpointRouteBuilder endpoints)
    {
        var internalCatalog = endpoints.MapGroup("/internal/catalog").WithTags("Catalog (internal)");

        internalCatalog.MapMethods("/sitemap", ReadMethods, GetSitemapAsync)
            .WithName("GetCatalogSitemap")
            .WithSummary("Every department, category and product a crawler may be told about. Internal.")
            .WithDescription(
                "GLB-11, read by the storefront's SSR process to render /sitemap.xml. Not under /api, so the public origin does not route it. " +
                "Slugs and last-modified timestamps only: the storefront turns them into addresses, because it owns its route table. " +
                "Deactivated departments and categories, soft-deleted categories and unpublished products are absent, by the same predicates the listing uses. " +
                "All three lists are capped and the response says which caps were reached.")
            .CacheOutput(SitemapOutputCache.PolicyName)
            .Produces<CatalogSitemapDto>();
    }

    private static async Task<Ok<IReadOnlyList<DepartmentDto>>> GetDepartmentsAsync(
        IQueryHandler<GetDepartmentsQuery, IReadOnlyList<DepartmentDto>> handler,
        CancellationToken cancellationToken)
    {
        var departments = await handler.HandleAsync(new GetDepartmentsQuery(), cancellationToken);
        return TypedResults.Ok(departments);
    }

    private static async Task<Results<Ok<PagedResult<ProductCardDto>>, ValidationProblem>> GetProductsAsync(
        [AsParameters] ProductListRequest request,
        IQueryHandler<ProductListQuery, Result<PagedResult<ProductCardDto>>> handler,
        IOptions<CatalogOptions> options,
        CancellationToken cancellationToken)
    {
        var query = ProductListQuery.Create(request.ToCriteria(), options.Value);
        if (!query.IsSuccess)
        {
            return ToValidationProblem(query.Errors);
        }

        var page = await handler.HandleAsync(query.Value, cancellationToken);
        return page.IsSuccess
            ? TypedResults.Ok(page.Value)
            : ToValidationProblem(page.Errors);
    }

    private static async Task<Results<Ok<ProductDetailDto>, ProblemHttpResult, ValidationProblem>> GetProductBySlugAsync(
        string slug,
        IQueryHandler<GetProductBySlugQuery, ProductDetailDto?> handler,
        CancellationToken cancellationToken)
    {
        var query = GetProductBySlugQuery.Create(slug);
        if (!query.IsSuccess)
        {
            return ToValidationProblem(query.Errors);
        }

        var product = await handler.HandleAsync(query.Value, cancellationToken);
        if (product is null)
        {
            // The normalised slug, never the raw route value: only a value that passed the slug
            // pattern is ever written into a response body.
            return TypedResults.Problem(
                title: "Product not found",
                detail: $"No published product has the slug '{query.Value.Slug}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(product);
    }

    /// <summary>
    /// The sitemap snapshot. No parameters: there is one answer, and a parameter would be one more way
    /// for a crawler to ask for work.
    /// </summary>
    private static async Task<Ok<CatalogSitemapDto>> GetSitemapAsync(
        IQueryHandler<GetCatalogSitemapQuery, CatalogSitemapDto> handler,
        CancellationToken cancellationToken)
    {
        var sitemap = await handler.HandleAsync(new GetCatalogSitemapQuery(), cancellationToken);
        return TypedResults.Ok(sitemap);
    }

    private static async Task<Results<Ok<ProductSearchResponse>, ValidationProblem>> SearchProductsAsync(
        [FromQuery] string? q,
        [AsParameters] ProductListRequest request,
        IQueryHandler<ProductSearchQuery, Result<PagedResult<ProductCardDto>>> handler,
        IOptions<CatalogOptions> options,
        CancellationToken cancellationToken)
    {
        var query = ProductSearchQuery.Create(q, request.ToCriteria(), options.Value);
        if (!query.IsSuccess)
        {
            return ToValidationProblem(query.Errors);
        }

        var page = await handler.HandleAsync(query.Value, cancellationToken);
        return page.IsSuccess
            ? TypedResults.Ok(new ProductSearchResponse(query.Value.Term, query.Value.Tokens, page.Value))
            : ToValidationProblem(page.Errors);
    }

    /// <summary>One RFC 9457 response carrying every rejected parameter, keyed by its query-string name.</summary>
    private static ValidationProblem ToValidationProblem(IReadOnlyList<ValidationError> errors)
    {
        var grouped = errors
            .GroupBy(error => error.Field, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Message).Distinct(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);

        return TypedResults.ValidationProblem(grouped, title: "One or more query parameters are invalid.");
    }
}

/// <summary>The search response: the page, plus the term and tokens the API actually used.</summary>
/// <param name="Term">The trimmed search term.</param>
/// <param name="Tokens">The tokens every result matched (doc 08 §7.6, at most five).</param>
/// <param name="Results">The matching page of product cards.</param>
public sealed record ProductSearchResponse(
    string Term,
    IReadOnlyList<string> Tokens,
    PagedResult<ProductCardDto> Results);
