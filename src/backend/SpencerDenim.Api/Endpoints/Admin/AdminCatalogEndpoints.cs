using System.Globalization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Admin;
using SpencerDenim.Application.Catalog.Admin.Contracts;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The admin catalogue structure: departments and the category tree (ADM-11, W1-12, doc 11 P1 part A).
/// <para>
/// <b>Permissions</b> follow doc 11 P1's own table — <c>Categories.View</c> to read the tree,
/// <c>Categories.Create</c> to add one, <c>Categories.Edit</c> to edit, reorder, move or switch one on and
/// off, <c>Categories.Delete</c> to remove one. Departments sit under <c>Categories.Edit</c> by doc 11 §7
/// G-02's stated default, because doc 08 §3.1 names no module for them. The API is the security boundary
/// (doc 03 §3a): the admin routes themselves only need a session, and a user without the permission is
/// shown the 403 state rather than a missing menu entry.
/// </para>
/// <para>
/// <b>Antiforgery</b> is validated by hand on every mutating route, as <c>AdminAuthEndpoints.Logout</c> and
/// <c>AdminMediaEndpoints</c> do, so the refusal is a problem document a client can read.
/// </para>
/// <para>
/// <b>Why the read routes are here and not on <c>/api/catalog</c>.</b> The storefront's catalogue reader
/// drops every row an admin has deactivated — that is what doc 08 §7.1's switch means — so these screens
/// need a read model that does not, or they could display only the rows that are already live and never
/// switch one back on. See <c>IAdminCatalogReader</c>.
/// </para>
/// </summary>
internal static class AdminCatalogEndpoints
{
    public static IEndpointRouteBuilder MapAdminCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/admin/catalog").WithTags("Admin Catalogue");

        api.MapGet("/departments", GetDepartmentsAsync)
            .RequirePermission(Permissions.Categories.View)
            .WithName("AdminListDepartments")
            .WithSummary("Every department, active or not, in nav order.")
            .WithDescription(
                "ADM-11. Unlike GET /api/catalog/departments this does not hide a deactivated department: " +
                "IsActive is the admin's own switch (doc 08 §7.1), so the screen that flips it has to be " +
                "able to see both positions. Each row carries its rowVersion for an optimistic-concurrency " +
                "check on the way back.")
            .Produces<IReadOnlyList<AdminDepartmentDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        api.MapGet("/departments/{departmentId:int}/categories", GetCategoryTreeAsync)
            .RequirePermission(Permissions.Categories.View)
            .WithName("AdminGetCategoryTree")
            .WithSummary("One department's whole category tree, active or not.")
            .WithDescription(
                "ADM-11. Soft-deleted categories are not in it. Each node carries its materialised path, " +
                "its rowVersion, the number of products linked to it (which is what the remove guard " +
                "counts) and the number whose primary category it is (which is what deactivating it hides).")
            .Produces<AdminCategoryTreeDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/categories/{categoryId:int}/deactivation-impact", GetDeactivationImpactAsync)
            .RequirePermission(Permissions.Categories.View)
            .WithName("AdminGetCategoryDeactivationImpact")
            .WithSummary("What switching this category off would do to the shop.")
            .WithDescription(
                "ADM-11, doc 08 §7.1. Three different numbers: the published products that leave the " +
                "storefront altogether (their primary category is this one), the categories underneath it, " +
                "and the published products that stay in the listing but lose every menu route to them " +
                "(their primary category is an active descendant). The third is the one an admin would not " +
                "predict, because IsActive is per row and the listing checks only a product's own primary " +
                "category.")
            .Produces<CategoryDeactivationImpactDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPut("/departments/order", ReorderDepartmentsAsync)
            .RequirePermission(Permissions.Categories.Edit)
            .WithName("AdminReorderDepartments")
            .WithSummary("Put the departments in a new order.")
            .WithDescription(
                "Send every department id, in the order they should appear. A list that is not exactly the " +
                "set that exists is refused with 409 'stale_order' rather than partly applied.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/departments/{departmentId:int}", UpdateDepartmentAsync)
            .RequirePermission(Permissions.Categories.Edit)
            .WithName("AdminUpdateDepartment")
            .WithSummary("Edit a department's text, hero image and SEO fields.")
            .WithDescription(
                "There is no create and no delete: the nav is fixed (Q5, doc 08 §3.2). The slug is checked " +
                "against the catalogue slug rule here, not only by the entity's setter, so a typo is a 400 " +
                "naming the field rather than a 500 (issue #24).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/departments/{departmentId:int}/active", SetDepartmentActiveAsync)
            .RequirePermission(Permissions.Categories.Edit)
            .WithName("AdminSetDepartmentActive")
            .WithSummary("Switch a department on or off (doc 08 §7.1).")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/categories", CreateCategoryAsync)
            .RequirePermission(Permissions.Categories.Create)
            .WithName("AdminCreateCategory")
            .WithSummary("Create a category, optionally under a parent.")
            .WithDescription(
                "It joins the end of its sibling list. Depth is bounded at three levels " +
                "(CK_Categories_Depth) and the slug must be free among its siblings " +
                "(UX_Categories_Dept_Parent_Slug).")
            .Produces<CreatedCategoryResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/categories/order", ReorderCategoriesAsync)
            .RequirePermission(Permissions.Categories.Edit)
            .WithName("AdminReorderCategories")
            .WithSummary("Put one parent's children in a new order.")
            .WithDescription(
                "Send every sibling id under that parent, in order. A reorder is a decision about a whole " +
                "list, so a list that leaves one out or names one that has moved away is refused with 409 " +
                "'stale_order': two admins reordering at once cannot interleave into an order neither of " +
                "them chose.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/categories/{categoryId:int}", UpdateCategoryAsync)
            .RequirePermission(Permissions.Categories.Edit)
            .WithName("AdminUpdateCategory")
            .WithSummary("Edit a category, and move it when its parent changes.")
            .WithDescription(
                "Changing parentCategoryId moves the category: its whole subtree is re-pathed and re-levelled " +
                "in the same transaction (doc 08 §10.1). A move to another department, under one of its own " +
                "descendants, or one that would make the tree more than three levels deep is refused with " +
                "the reason named.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPut("/categories/{categoryId:int}/active", SetCategoryActiveAsync)
            .RequirePermission(Permissions.Categories.Edit)
            .WithName("AdminSetCategoryActive")
            .WithSummary("Switch a category on or off (doc 08 §7.1).")
            .WithDescription(
                "Its own route rather than a field on the edit form, so that taking a section off the " +
                "storefront is a deliberate act with its own StatusChange audit row — and never a side " +
                "effect of saving a form whose checkbox was stale. Read " +
                "GET /categories/{categoryId}/deactivation-impact first.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapDelete("/categories/{categoryId:int}", RemoveCategoryAsync)
            .RequirePermission(Permissions.Categories.Delete)
            .WithName("AdminRemoveCategory")
            .WithSummary("Remove a category that nothing points at.")
            .WithDescription(
                "A soft delete (doc 08 §3.2). Refused with 409 'category_in_use', and the counts, while it " +
                "has child categories or linked products — a guard, never a cascade: a product whose primary " +
                "category had been deleted under it would vanish from the shop with nothing on any screen to " +
                "say why. Send the row version in the If-Match header.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> GetDepartmentsAsync(
        IQueryHandler<GetAdminDepartmentsQuery, IReadOnlyList<AdminDepartmentDto>> handler,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.HandleAsync(new GetAdminDepartmentsQuery(), cancellationToken));

    private static async Task<IResult> GetCategoryTreeAsync(
        int departmentId,
        IQueryHandler<GetAdminCategoryTreeQuery, AdminCategoryTreeDto?> handler,
        CancellationToken cancellationToken)
    {
        var tree = await handler.HandleAsync(new GetAdminCategoryTreeQuery(departmentId), cancellationToken);

        return tree is null
            ? NotFound(string.Create(CultureInfo.InvariantCulture, $"No department has the id {departmentId}."))
            : TypedResults.Ok(tree);
    }

    private static async Task<IResult> GetDeactivationImpactAsync(
        int categoryId,
        IQueryHandler<GetCategoryDeactivationImpactQuery, CategoryDeactivationImpactDto?> handler,
        CancellationToken cancellationToken)
    {
        var impact = await handler.HandleAsync(
            new GetCategoryDeactivationImpactQuery(categoryId), cancellationToken);

        return impact is null
            ? NotFound(string.Create(CultureInfo.InvariantCulture, $"No category has the id {categoryId}."))
            : TypedResults.Ok(impact);
    }

    private static async Task<IResult> UpdateDepartmentAsync(
        int departmentId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateDepartmentCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<UpdateDepartmentRequest>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var result = await handler.HandleAsync(
            new UpdateDepartmentCommand(
                departmentId,
                body!.Name,
                body.Slug,
                body.Description,
                body.HeroImageAssetId,
                body.SeoTitle,
                body.SeoDescription,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static async Task<IResult> SetDepartmentActiveAsync(
        int departmentId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetDepartmentActiveCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<SetActiveRequest>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (body!.IsActive is null)
        {
            return ValidationProblem([new ValidationError("isActive", "Say whether it should be on or off.")]);
        }

        var result = await handler.HandleAsync(
            new SetDepartmentActiveCommand(
                departmentId,
                body.IsActive.Value,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static async Task<IResult> ReorderDepartmentsAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ReorderDepartmentsCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<ReorderDepartmentsRequest>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        var result = await handler.HandleAsync(
            new ReorderDepartmentsCommand(body!.OrderedDepartmentIds, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static async Task<IResult> CreateCategoryAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<CreateCategoryCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<CreateCategoryRequest>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (body!.DepartmentId is null)
        {
            return ValidationProblem([new ValidationError("departmentId", "Choose a department.")]);
        }

        if (ReadMegaMenuColumn(body.MegaMenuColumn, out var megaMenuColumn) is { } columnError)
        {
            return columnError;
        }

        var result = await handler.HandleAsync(
            new CreateCategoryCommand(
                body.DepartmentId.Value,
                body.ParentCategoryId,
                body.Name,
                body.Slug,
                body.Description,
                body.ImageAssetId,
                body.BannerAssetId,
                body.BannerLinkUrl,
                body.AgeGroupId,
                body.SizeGuideId,

                // The column's default is 1 (doc 08 §3.2), so a body that says nothing means "yes".
                body.ShowInMegaMenu ?? true,
                megaMenuColumn,
                body.SeoTitle,
                body.SeoDescription,
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(
            result,
            id => TypedResults.Created(
                string.Create(CultureInfo.InvariantCulture, $"/api/admin/catalog/categories/{id}"),
                new CreatedCategoryResponse(id)));
    }

    private static async Task<IResult> UpdateCategoryAsync(
        int categoryId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateCategoryCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<UpdateCategoryRequest>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (ReadMegaMenuColumn(body!.MegaMenuColumn, out var megaMenuColumn) is { } columnError)
        {
            return columnError;
        }

        var result = await handler.HandleAsync(
            new UpdateCategoryCommand(
                categoryId,
                body.ParentCategoryId,
                body.Name,
                body.Slug,
                body.Description,
                body.ImageAssetId,
                body.BannerAssetId,
                body.BannerLinkUrl,
                body.AgeGroupId,
                body.SizeGuideId,
                body.ShowInMegaMenu ?? true,
                megaMenuColumn,
                body.SeoTitle,
                body.SeoDescription,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static async Task<IResult> ReorderCategoriesAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ReorderCategoriesCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<ReorderCategoriesRequest>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (body!.DepartmentId is null)
        {
            return ValidationProblem([new ValidationError("departmentId", "Say which department's list this is.")]);
        }

        var result = await handler.HandleAsync(
            new ReorderCategoriesCommand(
                body.DepartmentId.Value,
                body.ParentCategoryId,
                body.OrderedCategoryIds,
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static async Task<IResult> SetCategoryActiveAsync(
        int categoryId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<SetCategoryActiveCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<SetActiveRequest>(
            httpContext.Request, cancellationToken);
        if (problem is not null)
        {
            return problem;
        }

        if (body!.IsActive is null)
        {
            return ValidationProblem([new ValidationError("isActive", "Say whether it should be on or off.")]);
        }

        var result = await handler.HandleAsync(
            new SetCategoryActiveCommand(
                categoryId,
                body.IsActive.Value,
                AdminWriteRequest.RowVersion(httpContext.Request, body.RowVersion),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    private static async Task<IResult> RemoveCategoryAsync(
        int categoryId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<RemoveCategoryCommand, CatalogWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        // No body: a DELETE's precondition travels in If-Match, which is what that header is for and what
        // Category.RowVersion's own documentation calls the token.
        var result = await handler.HandleAsync(
            new RemoveCategoryCommand(
                categoryId,
                AdminWriteRequest.RowVersion(httpContext.Request, null),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    /// <summary>
    /// Reads the mega-menu column out of an <c>int?</c> so a value outside <c>byte</c> range is a named
    /// field error rather than a "malformed JSON" 400 about a body that parsed perfectly well.
    /// </summary>
    private static ValidationProblem? ReadMegaMenuColumn(int? supplied, out byte? column)
    {
        column = null;

        if (supplied is null)
        {
            return null;
        }

        if (supplied is < CatalogFieldRules.MinMegaMenuColumn or > CatalogFieldRules.MaxMegaMenuColumn)
        {
            return ValidationProblem(
            [
                new ValidationError(
                    "megaMenuColumn",
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The mega-menu column must be between {CatalogFieldRules.MinMegaMenuColumn} and " +
                        $"{CatalogFieldRules.MaxMegaMenuColumn}.")),
            ]);
        }

        column = (byte)supplied.Value;
        return null;
    }

    /// <summary>
    /// The one mapping from a use case's answer to HTTP, shared by all eight write routes so a new one
    /// cannot invent a different status for the same situation.
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

/// <summary>The id of a category that has just been created, so the screen can select it without a reload.</summary>
public sealed record CreatedCategoryResponse(int Id);
