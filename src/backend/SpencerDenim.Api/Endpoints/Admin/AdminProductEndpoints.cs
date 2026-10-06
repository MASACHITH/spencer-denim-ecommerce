using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Admin;
using SpencerDenim.Application.Catalog.Admin.Products;
using SpencerDenim.Application.Catalog.Admin.Products.Abstractions;
using SpencerDenim.Application.Catalog.Admin.Products.Contracts;
using SpencerDenim.Application.Catalog.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The admin product screens' API (ADM-10, W1-03, doc 11 P1 part B): list, add, edit, publish, unpublish
/// and remove a product, with its colourways, variant matrix, media, badges, SEO fields and related picks.
/// <para>
/// <b>Permissions</b> follow doc 11 P1's own table: <c>Products.View</c> to read, <c>Products.Create</c> to
/// add, <c>Products.Edit</c> to edit and to publish or unpublish, <c>Products.Delete</c> to remove. The API
/// is the security boundary (doc 03 §3a): the admin routes themselves need only a session, and a user
/// without the permission is shown the 403 state rather than a missing menu entry.
/// </para>
/// <para>
/// <b>Media is uploaded through the media library, not here.</b> ADM-10 says "images + video upload", and
/// the upload path already exists and is already used for category tiles and the client's logo:
/// <c>POST /api/admin/media/images</c> and <c>POST /api/admin/media/videos</c> (W1-04). They check the file
/// by signature rather than by name, cap an image at <c>Media:MaxImageBytes</c> and a video at 50 MB
/// (<c>MediaLimits.MaxVideoBytes</c>, doc 08 §3.3's <c>CK_MediaAssets_SizeBytes</c>), insist on a poster for
/// a video, and refuse an MP4 with no H.264 track. A second upload path here would be a second place for
/// those rules to be got wrong. These routes take the resulting <c>publicId</c>.
/// </para>
/// <para>
/// <b>Antiforgery</b> is validated by hand on every mutating route, as <c>AdminCatalogEndpoints</c> and
/// <c>AdminMediaEndpoints</c> do, so the refusal is a problem document a client can read rather than an
/// empty 400.
/// </para>
/// </summary>
internal static class AdminProductEndpoints
{
    public static IEndpointRouteBuilder MapAdminProductEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/products").WithTags("Admin Products");

        api.MapGet("/", ListAsync)
            .RequirePermission(Permissions.Products.View)
            .WithName("AdminListProducts")
            .WithSummary("One page of the product list, published and unpublished.")
            .WithDescription(
                "ADM-10. Unlike GET /api/catalog/products this does not hide an unpublished product, a "
                + "product in a deactivated category or one whose department is switched off: those are "
                + "exactly the products this screen exists to finish and publish. Soft-deleted products are "
                + "not in it — removed is not hidden, and ADM-10 has no restore. `term` matches the name, "
                + "the product code, the slug and any variant SKU.")
            .Produces<PagedResult<AdminProductListItemDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        api.MapGet("/form-options", GetFormOptionsAsync)
            .RequirePermission(Permissions.Products.View)
            .WithName("AdminGetProductFormOptions")
            .WithSummary("Everything the product form has to choose from.")
            .WithDescription(
                "ADM-10, doc 11 P1 steps 6–12: departments, the whole category tree flattened with indented "
                + "display names, age groups, materials, fits, occasions, colour families, size scales with "
                + "their sizes, and size guides. One payload rather than eight requests, because the form "
                + "cannot render a single row of the variant matrix until it has all of it. Deactivated "
                + "categories are included and flagged, so a product already filed under one still shows "
                + "which.")
            .Produces<ProductFormOptionsDto>();

        api.MapGet("/related-search", SearchForRelatedAsync)
            .RequirePermission(Permissions.Products.View)
            .WithName("AdminSearchProductsForRelated")
            .WithSummary("Products matching a typed term, for the related-products picker.")
            .WithDescription(
                "PDP-06, doc 11 P1 step 12. A search rather than a list: the catalogue is the one admin list "
                + "that is not a small seeded table, so sending every product to render a picker is a "
                + "payload that grows without limit.")
            .Produces<IReadOnlyList<AdminRelatedProductDto>>();

        api.MapGet("/{productId:int}", GetAsync)
            .RequirePermission(Permissions.Products.View)
            .WithName("AdminGetProduct")
            .WithSummary("One product with its colours, variants, media and related picks.")
            .WithDescription(
                "ADM-10. Carries `rowVersion` for the optimistic-concurrency check on the way back, and "
                + "`canBePublished` with `publishBlockers` — the checklist of doc 11 §7 G-01, read from the "
                + "same function the publish route refuses with, so the button's explanation and the API's "
                + "answer cannot disagree.")
            .Produces<AdminProductDetailDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/", CreateAsync)
            .RequirePermission(Permissions.Products.Create)
            .WithName("AdminCreateProduct")
            .WithSummary("Create a product. It is always created unpublished.")
            .WithDescription(
                "Doc 11 P1 step 13: unpublished, always. Publishing is its own route with its own checklist, "
                + "and a create that could publish would have to pass a checklist about photographs the "
                + "admin has not attached yet. Media assets are named by the `publicId` the media library "
                + "returns, never by an internal id.")
            .Produces<CreatedProductResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/{productId:int}", UpdateAsync)
            .RequirePermission(Permissions.Products.Edit)
            .WithName("AdminUpdateProduct")
            .WithSummary("Edit a product, its colours, its variant matrix, its media and its related picks.")
            .WithDescription(
                "PUT replaces the product, so send every field — one the body omits is one the API is told "
                + "to clear. The **one exception is `media`**: a body with no `media` field at all leaves the "
                + "photographs and the video alone, while `\"media\": []` removes them. W1-12 conflated "
                + "those and every save silently cleared the images.\n\n"
                + "Changing the slug of a product that has been published needs `confirmSlugChange: true`; "
                + "without it the answer is 409 `slug_change_needs_confirmation`, naming both slugs, because "
                + "there are no redirects in this version (doc 08 Appendix A).\n\n"
                + "Dropping a variant an order line names is refused with 409 `variant_has_orders` and the "
                + "SKUs: doc 08 §3.2 keeps an ordered variant for ever, so the answer is to switch it off.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/{productId:int}/published", SetPublishedAsync)
            .RequirePermission(Permissions.Products.Edit)
            .WithName("AdminSetProductPublished")
            .WithSummary("Put a product on the shop, or take it off (doc 11 P1 steps 14–15).")
            .WithDescription(
                "Its own route rather than a field on the edit form, so putting a product on sale is a "
                + "deliberate act with its own StatusChange audit row and never a side effect of saving a "
                + "form whose checkbox was stale — the argument PUT /categories/{id}/active already makes.\n\n"
                + "Publishing runs doc 11 §7 G-01's checklist: at least one active variant, a gallery "
                + "photograph (which becomes the card image), and alt text on every public image. A refusal "
                + "is 409 `publish_checklist_failed` with `blockers`, one sentence per unmet rule. "
                + "Unpublishing never refuses.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapDelete("/{productId:int}", RemoveAsync)
            .RequirePermission(Permissions.Products.Delete)
            .WithName("AdminRemoveProduct")
            .WithSummary("Remove a product.")
            .WithDescription(
                "A soft delete (doc 08 §10.2). Doc 11 P1 step 15: the variants, colours, media links and "
                + "order lines stay, and the product leaves every storefront query. Nothing is refused — a "
                + "product sitting in a customer's bag is handled in the bag, which shows the line as "
                + "unavailable and blocks checkout (P5), so refusing here would leave an admin unable to "
                + "take a product off sale because somebody left it in a bag. Send the row version in the "
                + "If-Match header.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string? term,
        int? departmentId,
        int? categoryId,
        bool? published,
        string? sort,
        int? page,
        int? pageSize,
        IQueryHandler<GetAdminProductsQuery, PagedResult<AdminProductListItemDto>> handler,
        CancellationToken cancellationToken)
    {
        if (!TryReadSort(sort, out var parsedSort))
        {
            return ValidationProblem(
            [
                new ValidationError(
                    "sort",
                    "Sort by one of: recentlyUpdated, name, productCode, priceAscending, priceDescending."),
            ]);
        }

        var result = await handler.HandleAsync(
            new GetAdminProductsQuery(new AdminProductListCriteria(
                term,
                departmentId,
                categoryId,
                published,
                parsedSort,
                page ?? 1,
                pageSize ?? 0)),
            cancellationToken);

        return TypedResults.Ok(result);
    }

    private static async Task<IResult> GetFormOptionsAsync(
        IQueryHandler<GetProductFormOptionsQuery, ProductFormOptionsDto> handler,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new GetProductFormOptionsQuery(), cancellationToken));

    private static async Task<IResult> SearchForRelatedAsync(
        string? term,
        int? excludeProductId,
        IQueryHandler<SearchProductsForRelatedQuery, IReadOnlyList<AdminRelatedProductDto>> handler,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(
            new SearchProductsForRelatedQuery(term, excludeProductId), cancellationToken));

    private static async Task<IResult> GetAsync(
        int productId,
        IQueryHandler<GetAdminProductQuery, AdminProductDetailDto?> handler,
        CancellationToken cancellationToken)
    {
        var product = await handler.HandleAsync(new GetAdminProductQuery(productId), cancellationToken);

        return product is null
            ? NotFound(string.Create(CultureInfo.InvariantCulture, $"No product has the id {productId}."))
            : TypedResults.Ok(product);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<CreateProductCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<ProductRequestBody>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var (form, field, message) = ProductRequestMapper.Map(body!);
        if (form is null)
        {
            return ValidationProblem([new ValidationError(field!, message!)]);
        }

        var result = await handler.HandleAsync(
            new CreateProductCommand(form, AdminWriteRequest.Actor(httpContext)), cancellationToken);

        return ToHttp(
            result,
            id => TypedResults.Created(
                string.Create(CultureInfo.InvariantCulture, $"/api/admin/products/{id}"),
                new CreatedProductResponse(id)));
    }

    private static async Task<IResult> UpdateAsync(
        int productId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateProductCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<ProductRequestBody>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var (form, field, message) = ProductRequestMapper.Map(body!);
        if (form is null)
        {
            return ValidationProblem([new ValidationError(field!, message!)]);
        }

        var result = await handler.HandleAsync(
            new UpdateProductCommand(
                productId,
                form,
                body!.ConfirmSlugChange ?? false,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static async Task<IResult> SetPublishedAsync(
        int productId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetProductPublishedCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<SetProductPublishedRequest>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (body!.IsPublished is null)
        {
            return ValidationProblem(
                [new ValidationError("isPublished", "Say whether the product should be on the shop or off it.")]);
        }

        var result = await handler.HandleAsync(
            new SetProductPublishedCommand(
                productId,
                body.IsPublished.Value,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static async Task<IResult> RemoveAsync(
        int productId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<RemoveProductCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        // No body: a DELETE's precondition travels in If-Match, which is what that header is for.
        var result = await handler.HandleAsync(
            new RemoveProductCommand(
                productId,
                AdminWriteRequest.RowVersion(httpContext.Request, null),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    /// <summary>
    /// Reads the sort from the query string by name rather than by number, so a bookmarked admin URL does
    /// not change meaning the day a sort is inserted into the middle of the enum.
    /// </summary>
    private static bool TryReadSort(string? value, out AdminProductSort sort)
    {
        sort = AdminProductSort.RecentlyUpdated;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return Enum.TryParse(value.Trim(), ignoreCase: true, out sort);
    }

    /// <summary>
    /// The one mapping from a use case's answer to HTTP, shared by every write route here so a new one
    /// cannot invent a different status for the same situation. Deliberately the same shape as
    /// <c>AdminCatalogEndpoints.ToHttp</c>, over the same result type.
    /// </summary>
    private static IResult ToHttp(CatalogWriteResult result, Func<int, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        return result switch
        {
            CatalogWriteResult.Succeeded success => onSuccess(success.EntityId),

            // The caller asked for a state the row is already in. Nothing was written and no audit row
            // exists, and the answer is still success: the request's intent holds.
            CatalogWriteResult.Unchanged unchanged => onSuccess(unchanged.EntityId),

            CatalogWriteResult.Invalid invalid => ValidationProblem(invalid.Errors),

            CatalogWriteResult.NotFound notFound => NotFound(notFound.Message),

            CatalogWriteResult.Conflict conflict => TypedResults.Problem(
                title: "This change cannot be applied",
                detail: conflict.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: Extensions(conflict)),

            _ => throw new InvalidOperationException($"Unhandled write result {result.GetType().Name}."),
        };
    }

    private static Dictionary<string, object?> Extensions(CatalogWriteResult.Conflict conflict)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = conflict.Code };

        if (conflict.Details is not null)
        {
            foreach (var (key, value) in conflict.Details)
            {
                extensions[key] = value;
            }
        }

        return extensions;
    }

    /// <summary>One RFC 9457 response carrying every rejected field, keyed by its name in the request body.</summary>
    private static ValidationProblem ValidationProblem(IReadOnlyList<ValidationError> errors) =>
        TypedResults.ValidationProblem(
            errors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).Distinct(StringComparer.Ordinal).ToArray(),
                    StringComparer.Ordinal),
            title: "One or more fields are invalid.");

    private static ProblemHttpResult NotFound(string detail) =>
        AdminWriteRequest.Problem(StatusCodes.Status404NotFound, "Not found", detail, "not_found");
}
