using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Marketing.Promotions;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Marketing.Promotions.Contracts;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>
/// The admin sale-campaign screen (ADM-14; doc 11 P3 section A, doc 08 §3.6).
/// <para>
/// <b>The same four permissions as the promo-code screen</b>, as doc 11 P3's own table names them:
/// <c>Promotions.View</c> to list, <c>Promotions.Create</c> to add, <c>Promotions.Edit</c> to change,
/// <c>Promotions.Delete</c> to remove. All four already exist — W3-05 added the last two through the
/// versioned seeder step — so this feature needs no new permission and no seeder change.
/// </para>
/// <para>
/// <b>Every mutating route requires every field to be present in the body</b>, including <c>targets</c>.
/// "A field this body does not carry is left as it is" and "a field it carries as null is cleared" are
/// different instructions; collapsing them is how W1-12 cleared every category image. Here it would be
/// worse than a cleared image: a save that dropped <c>targets</c> and kept the old ones would leave a
/// staff member believing they had narrowed a sale they had not, and the shop discounting products they
/// thought they had taken off it.
/// </para>
/// <para>
/// <b>There is no one-bit oracle.</b> Each route carries a single permission, enforced before the handler
/// runs and without reference to anything in the body.
/// </para>
/// </summary>
internal static class AdminSaleCampaignEndpoints
{
    public static IEndpointRouteBuilder MapAdminSaleCampaignEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var campaigns = endpoints
            .MapGroup("/api/admin/marketing/sale-campaigns")
            .WithTags("Admin Sale Campaigns");

        campaigns.MapGet("/", GetCampaignsAsync)
            .RequirePermission(Permissions.Promotions.View)
            .WithName("AdminListSaleCampaigns")
            .WithSummary("Every sale campaign that has not been removed, newest first.")
            .WithDescription(
                "ADM-14. Inactive, scheduled and ended campaigns are all included: this list is where a "
                + "staff member ends a sale early or turns one back on (doc 11 P3 step 5). `status` is "
                + "derived from the dates and the active flag rather than stored, so it cannot disagree "
                + "with what the shop charges. `startsAt` and `endsAt` are **Colombo** wall-clock times, "
                + "not UTC — the columns are UTC and the conversion happens at this edge. Each target "
                + "carries the name of what it points at, and says so when the category or product has "
                + "since been removed from the catalogue. Each row carries its `rowVersion` for the way "
                + "back.")
            .Produces<IReadOnlyList<AdminSaleCampaignDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        campaigns.MapPost("/", CreateCampaignAsync)
            .RequirePermission(Permissions.Promotions.Create)
            .WithName("AdminCreateSaleCampaign")
            .WithSummary("Create a sale campaign.")
            .WithDescription(
                "ADM-14, doc 11 P3 steps 1 to 3. `discountValue` is a percentage above 0 and **up to 90** "
                + "for `Percent` (doc 08 §3.6's own ceiling) or an amount above 0 for `FixedAmount`. "
                + "`endsAt` is required and must be after `startsAt` — doc 02 line 119 is \"for a date "
                + "range\". At least one target is required; each names exactly one department, category "
                + "or product, and `includeSubcategories` is only meaningful for a category. On success "
                + "the stored price summary of every affected product is recomputed in batches and the "
                + "count comes back as `productsRepriced`; a campaign that repriced nothing answers with "
                + "a warning, because that is almost always a targeting mistake. Listing prices and the "
                + "Sale page may still lag up to five minutes at a boundary (doc 08 §7.4) — the bag and "
                + "checkout always price live. **Send every field**, including the empty ones.")
            .Accepts<AdminSaleCampaignRequest>("application/json")
            .Produces<AdminSaleCampaignSavedDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        campaigns.MapPut("/{campaignId:int}", UpdateCampaignAsync)
            .RequirePermission(Permissions.Promotions.Edit)
            .WithName("AdminUpdateSaleCampaign")
            .WithSummary("Edit a sale campaign.")
            .WithDescription(
                "ADM-14, including doc 11 P3 step 5's two ways to end a sale early: move `endsAt` into "
                + "the past, or set `isActive` to false. A full replacement — **send every field**, "
                + "including `targets`, because a missing key is refused rather than treated as \"leave "
                + "it alone\". The targets are replaced wholesale, and the recompute covers the union of "
                + "the products the campaign reached before and after, so a category you remove has its "
                + "prices put back in the same save. Send the row version in `If-Match` or in the body.")
            .Accepts<AdminSaleCampaignRequest>("application/json")
            .Produces<AdminSaleCampaignSavedDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        campaigns.MapDelete("/{campaignId:int}", DeleteCampaignAsync)
            .RequirePermission(Permissions.Promotions.Delete)
            .WithName("AdminDeleteSaleCampaign")
            .WithSummary("Remove a sale campaign.")
            .WithDescription(
                "ADM-14, doc 11 P3 step 5's third way to end a sale. A soft delete: the orders the "
                + "campaign priced keep their `saleCampaignId`, which is why the foreign key is NO "
                + "ACTION and nothing here is hard-deleted. The affected products' stored summaries are "
                + "recomputed in the same request, so the Sale page stops advertising it without waiting "
                + "for the five-minute job. Send the row version in `If-Match`.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> GetCampaignsAsync(
        ISaleCampaignAdminReader reader,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await reader.ListAsync(cancellationToken));

    private static async Task<IResult> CreateCampaignAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<CreateSaleCampaignCommand, SaleCampaignWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(
            httpContext.Request, cancellationToken);

        if (problem is not null)
        {
            return problem;
        }

        if (ReadFields(body!, out var fields) is { } badField)
        {
            return badField;
        }

        var result = await handler.HandleAsync(
            new CreateSaleCampaignCommand(fields, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(
            result,
            saved => TypedResults.Created(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"/api/admin/marketing/sale-campaigns/{saved.Campaign.Id}"),
                saved));
    }

    private static async Task<IResult> UpdateCampaignAsync(
        int campaignId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdateSaleCampaignCommand, SaleCampaignWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(
            httpContext.Request, cancellationToken);

        if (problem is not null)
        {
            return problem;
        }

        if (ReadFields(body!, out var fields) is { } badField)
        {
            return badField;
        }

        var result = await handler.HandleAsync(
            new UpdateSaleCampaignCommand(
                campaignId,
                fields,
                AdminWriteRequest.RowVersion(httpContext.Request, AdminJsonBody.RowVersionIn(body!)),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, TypedResults.Ok);
    }

    private static async Task<IResult> DeleteCampaignAsync(
        int campaignId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<DeleteSaleCampaignCommand, SaleCampaignWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var result = await handler.HandleAsync(
            new DeleteSaleCampaignCommand(
                campaignId,
                AdminWriteRequest.RowVersion(httpContext.Request, null),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    /// <summary>
    /// Reads the form, refusing a body that leaves a field out. See
    /// <see cref="AdminJsonBody"/> for why presence and not only value.
    /// </summary>
    private static ValidationProblem? ReadFields(JsonObject body, out SaleCampaignInput fields)
    {
        fields = default!;

        if (AdminJsonBody.ReadString(body, "name", out var name) is { } badName)
        {
            return badName;
        }

        if (AdminJsonBody.ReadString(body, "description", out var description) is { } badDescription)
        {
            return badDescription;
        }

        if (AdminJsonBody.ReadEnum<SaleDiscountType>(
                body, "discountType", "one of Percent or FixedAmount", out var discountType)
            is { } badType)
        {
            return badType;
        }

        if (AdminJsonBody.ReadDecimal(body, "discountValue", out var discountValue) is { } badValue)
        {
            return badValue;
        }

        if (AdminJsonBody.ReadLocalDateTime(body, "startsAt", out var startsAt) is { } badStart)
        {
            return badStart;
        }

        if (AdminJsonBody.ReadLocalDateTime(body, "endsAt", out var endsAt) is { } badEnd)
        {
            return badEnd;
        }

        if (AdminJsonBody.ReadBool(body, "isActive", out var isActive) is { } badActive)
        {
            return badActive;
        }

        if (AdminJsonBody.ReadInt(body, "bannerAssetId", out var bannerAssetId) is { } badBanner)
        {
            return badBanner;
        }

        if (AdminJsonBody.ReadObjectArray(body, "targets", out var targetNodes) is { } badTargets)
        {
            return badTargets;
        }

        var targets = new List<SaleCampaignTargetInput>(targetNodes.Count);

        for (var index = 0; index < targetNodes.Count; index++)
        {
            if (ReadTarget(targetNodes[index], index, out var target) is { } badTarget)
            {
                return badTarget;
            }

            targets.Add(target);
        }

        fields = new SaleCampaignInput(
            name,
            description,
            discountType,
            discountValue,
            startsAt,
            endsAt,
            isActive,
            bannerAssetId,
            targets);

        return null;
    }

    /// <summary>
    /// One target. <b>Every key present here too</b>, including the three ids and the flag: the one that
    /// matters is <c>includeSubcategories</c>, whose absence would otherwise quietly become doc 08 §3.6's
    /// default of <c>true</c> and widen a sale a staff member had narrowed to one category.
    /// </summary>
    private static ValidationProblem? ReadTarget(
        JsonObject node,
        int index,
        out SaleCampaignTargetInput target)
    {
        target = default!;

        var prefix = string.Create(CultureInfo.InvariantCulture, $"targets[{index}]");

        if (AdminJsonBody.ReadEnum<SaleCampaignTargetType>(
                node, "targetType", "one of Department, Category or Product", out var type)
            is not null)
        {
            return AdminJsonBody.WrongType(prefix, "a target with targetType Department, Category or Product");
        }

        if (AdminJsonBody.ReadInt(node, "departmentId", out var departmentId) is not null
            || AdminJsonBody.ReadInt(node, "categoryId", out var categoryId) is not null
            || AdminJsonBody.ReadInt(node, "productId", out var productId) is not null
            || AdminJsonBody.ReadBool(node, "includeSubcategories", out var includeSubcategories) is not null)
        {
            // Re-reported against the target rather than against the inner key, so the screen can attach
            // the message to the row the staff member is looking at.
            return AdminJsonBody.WrongType(
                prefix,
                "a target with departmentId, categoryId, productId and includeSubcategories, using null "
                + "for the ones that do not apply");
        }

        target = new SaleCampaignTargetInput(
            type,
            departmentId,
            categoryId,
            productId,
            includeSubcategories);

        return null;
    }

    /// <summary>The one mapping from an ADM-14 write's answer to HTTP.</summary>
    private static IResult ToHttp(
        SaleCampaignWriteResult result,
        Func<AdminSaleCampaignSavedDto, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        return result switch
        {
            SaleCampaignWriteResult.Succeeded success => onSuccess(success.Saved),

            SaleCampaignWriteResult.Invalid invalid => AdminJsonBody.Problem(invalid.Errors),

            SaleCampaignWriteResult.NotFound missing => AdminWriteRequest.Problem(
                StatusCodes.Status404NotFound,
                "Not found",
                missing.Message,
                "not_found"),

            SaleCampaignWriteResult.Conflict conflict => AdminWriteRequest.Problem(
                StatusCodes.Status409Conflict,
                "Cannot be applied",
                conflict.Message,
                conflict.Code),

            _ => throw new InvalidOperationException($"Unhandled campaign write result '{result}'."),
        };
    }
}

/// <summary>One target on the sale-campaign form (ADM-14). The OpenAPI shape only.</summary>
/// <param name="TargetType">One of <c>Department</c>, <c>Category</c> or <c>Product</c>.</param>
/// <param name="IncludeSubcategories">
/// Whether a <c>Category</c> target covers its subtree. Send it for every target; it is ignored for a
/// department (which already covers its categories) and a product (which has no subtree).
/// </param>
public sealed record AdminSaleCampaignTargetRequest(
    string? TargetType,
    int? DepartmentId,
    int? CategoryId,
    int? ProductId,
    bool? IncludeSubcategories);

/// <summary>
/// The sale-campaign form (ADM-14). <b>The OpenAPI shape only; the endpoint reads the body as a
/// <c>JsonObject</c></b> so that a missing key and an explicit null can be told apart — see
/// <see cref="AdminSaleCampaignEndpoints"/>.
/// </summary>
/// <param name="DiscountType">One of <c>Percent</c> or <c>FixedAmount</c>.</param>
/// <param name="DiscountValue">
/// The percentage (above 0, up to 90) or the LKR amount (above 0).
/// </param>
/// <param name="StartsAt">
/// <b>The shop's own time, with no offset</b> — <c>2026-10-13T00:00</c>. A trailing <c>Z</c> or a
/// <c>+05:30</c> is refused, because guessing wrong moves a campaign's window by five and a half hours.
/// </param>
/// <param name="EndsAt">The same, and required.</param>
/// <param name="Targets">At least one. A campaign with no target discounts nothing.</param>
/// <param name="RowVersion">
/// On an edit: the row version from the list, unless it is sent in <c>If-Match</c>. Ignored on a create.
/// </param>
public sealed record AdminSaleCampaignRequest(
    string? Name,
    string? Description,
    string? DiscountType,
    decimal? DiscountValue,
    string? StartsAt,
    string? EndsAt,
    bool? IsActive,
    int? BannerAssetId,
    IReadOnlyList<AdminSaleCampaignTargetRequest>? Targets,
    string? RowVersion);
