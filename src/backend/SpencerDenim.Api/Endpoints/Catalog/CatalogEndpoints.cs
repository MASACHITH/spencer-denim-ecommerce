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

namespace SpencerDenim.Api.Endpoints.Catalog;

/// <summary>
/// The public, read-only catalogue API (GLB-05, PLP-01..07, CARD-01, PDP-01..06, SRCH-02).
/// No authentication: everything here is what an anonymous visitor may see. Admin write endpoints
/// arrive with ADM-10/ADM-11 behind permission policies.
/// </summary>
internal static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var catalog = endpoints.MapGroup("/api/catalog").WithTags("Catalog");

        catalog.MapGet("/departments", GetDepartmentsAsync)
            .WithName("GetDepartments")
            .WithSummary("Departments with their category tree.")
            .WithDescription("Drives the mega menu (GLB-05) and the department landing pages (DEPT-01). Inactive departments and categories are omitted.")
            .Produces<IReadOnlyList<DepartmentDto>>();

        catalog.MapGet("/products", GetProductsAsync)
            .WithName("GetProducts")
            .WithSummary("Product listing with filters, sorting and paging.")
            .WithDescription(
                "PLP-01..07. Repeatable filters (ageGroup, size, colour, material, fit, occasion) accept the parameter more than once. " +
                "Paging is offset based: doc 08 §7.1 specifies OFFSET/FETCH with an Id tiebreaker, and PLP-06 needs a live total count.")
            .Produces<PagedResult<ProductCardDto>>()
            .ProducesValidationProblem();

        catalog.MapGet("/products/{slug}", GetProductBySlugAsync)
            .WithName("GetProductBySlug")
            .WithSummary("One product page payload.")
            .WithDescription("PDP-01..06: gallery, colours, sizes, variants with stock status, price and discount, attributes and related products.")
            .Produces<ProductDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();

        catalog.MapGet("/search", SearchProductsAsync)
            .WithName("SearchProducts")
            .WithSummary("Search products by name, category, colour and attributes.")
            .WithDescription("SRCH-02. All of up to five tokens must match the denormalised search text (doc 08 §7.6). The listing filters and sorts apply on top.")
            .Produces<ProductSearchResponse>()
            .ProducesValidationProblem();

        return endpoints;
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
        IQueryHandler<ProductListQuery, PagedResult<ProductCardDto>> handler,
        IOptions<CatalogOptions> options,
        CancellationToken cancellationToken)
    {
        var query = ProductListQuery.Create(request.ToCriteria(), options.Value);
        if (!query.IsSuccess)
        {
            return ToValidationProblem(query.Errors);
        }

        var page = await handler.HandleAsync(query.Value, cancellationToken);
        return TypedResults.Ok(page);
    }

    private static async Task<Results<Ok<ProductDetailDto>, ProblemHttpResult, ValidationProblem>> GetProductBySlugAsync(
        string slug,
        IQueryHandler<GetProductBySlugQuery, Result<ProductDetailDto?>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetProductBySlugQuery(slug), cancellationToken);
        if (!result.IsSuccess)
        {
            return ToValidationProblem(result.Errors);
        }

        if (result.Value is not { } product)
        {
            return TypedResults.Problem(
                title: "Product not found",
                detail: $"No published product has the slug '{slug}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        return TypedResults.Ok(product);
    }

    private static async Task<Results<Ok<ProductSearchResponse>, ValidationProblem>> SearchProductsAsync(
        [FromQuery] string? q,
        [AsParameters] ProductListRequest request,
        IQueryHandler<ProductSearchQuery, PagedResult<ProductCardDto>> handler,
        IOptions<CatalogOptions> options,
        CancellationToken cancellationToken)
    {
        var query = ProductSearchQuery.Create(q, request.ToCriteria(), options.Value);
        if (!query.IsSuccess)
        {
            return ToValidationProblem(query.Errors);
        }

        var page = await handler.HandleAsync(query.Value, cancellationToken);
        return TypedResults.Ok(new ProductSearchResponse(query.Value.Term, query.Value.Tokens, page));
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
