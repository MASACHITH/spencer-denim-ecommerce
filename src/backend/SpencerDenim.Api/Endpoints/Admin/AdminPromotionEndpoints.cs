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
/// The admin promo-code screen (ADM-13; doc 11 P3 part B, doc 08 §3.6).
/// <para>
/// <b>Four permissions, as doc 11 P3's own table names them:</b> <c>Promotions.View</c> to list,
/// <c>Promotions.Create</c> to add, <c>Promotions.Edit</c> to change, <c>Promotions.Delete</c> to remove.
/// The last two did not exist in <c>Permissions</c> before W3-05 and were added with a versioned seeder
/// step, so they reach a database whose Store Manager role already exists (issue #122, doc 08 §9 N13).
/// </para>
/// <para>
/// <b>Sale campaigns (ADM-14) are not here; they are in <see cref="AdminSaleCampaignEndpoints"/>.</b> Doc
/// 02 line 119 is a separate feature with its own tables and its own <c>RefreshCampaignPricing</c> job,
/// and nothing in this file touches campaign pricing — which is why the route group is named for the
/// promo codes it serves rather than for the module. The two screens share the four
/// <c>Promotions.*</c> permissions and, since W3-05b, the body readers in <see cref="AdminJsonBody"/>.
/// </para>
/// <para>
/// <b>Every mutating route requires every field to be present in the body</b>, and that is not pedantry.
/// "A field this body does not carry is left as it is" and "a field it carries as null is cleared" are
/// different instructions; collapsing them is how W1-12 cleared every category image and how W1-03's
/// mapper cleared four of five collections. Here the stakes are money: a save that silently dropped
/// <c>totalUsageLimit</c> would turn a code with fifty uses into an unlimited one. So a missing key is a
/// 400 naming it, and the screen sends the whole form every time.
/// </para>
/// <para>
/// <b>There is no one-bit oracle.</b> Each route carries a single permission, enforced before the handler
/// runs and without reference to anything in the body — so no answer here depends on what the submitted
/// value would have changed, which is the defect ADM-22's settings save had to be fixed for.
/// </para>
/// </summary>
internal static class AdminPromotionEndpoints
{
    public static IEndpointRouteBuilder MapAdminPromotionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var codes = endpoints.MapGroup("/api/admin/marketing/promo-codes").WithTags("Admin Promo Codes");

        codes.MapGet("/", GetPromoCodesAsync)
            .RequirePermission(Permissions.Promotions.View)
            .WithName("AdminListPromoCodes")
            .WithSummary("Every promo code that has not been removed, newest first.")
            .WithDescription(
                "ADM-13. Inactive, scheduled and ended codes are all included: this list is where a staff "
                + "member ends an expired code or turns one back on. `status` is derived from the dates "
                + "and the active flag rather than stored (doc 11 P3), so it cannot disagree with what a "
                + "shopper's code actually does. `startsAt` and `endsAt` are **Colombo** wall-clock times, "
                + "not UTC — the column is UTC and the conversion happens at this edge, because a staff "
                + "member who types 23:59 means 23:59 in the shop. `usageCount` is held uses (doc 08 "
                + "§3.6) and is not editable. Each row carries its `rowVersion` for the way back.")
            .Produces<IReadOnlyList<AdminPromoCodeDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        codes.MapPost("/", CreatePromoCodeAsync)
            .RequirePermission(Permissions.Promotions.Create)
            .WithName("AdminCreatePromoCode")
            .WithSummary("Create a promo code.")
            .WithDescription(
                "ADM-13, doc 11 P3 steps 6 to 8. The code is stored upper-case and must be unique among "
                + "codes that have not been removed. `discountValue` is a percentage above 0 and up to "
                + "100 for `Percent`, an amount above 0 for `FixedAmount`, and must be absent for "
                + "`FreeDelivery` — sending one with a free-delivery code is refused rather than ignored, "
                + "so nobody believes they have made a 20%-off-and-free-delivery code. `endsAt` must be "
                + "after `startsAt`, and `perCustomerLimit` may not exceed `totalUsageLimit` when both "
                + "are set. **Send every field**, including the empty ones.")
            .Accepts<AdminPromoCodeRequest>("application/json")
            .Produces<AdminPromoCodeSavedDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        codes.MapPut("/{promoCodeId:int}", UpdatePromoCodeAsync)
            .RequirePermission(Permissions.Promotions.Edit)
            .WithName("AdminUpdatePromoCode")
            .WithSummary("Edit a promo code.")
            .WithDescription(
                "ADM-13. A full replacement: **send every field**, including the empty ones, because a "
                + "missing key is refused rather than treated as \"leave it alone\". `usageCount` is not "
                + "settable — only a placement and the return of a use move it (doc 08 §3.6). Lowering "
                + "`totalUsageLimit` below the uses already held is allowed and answers with a warning "
                + "(DB19): it blocks new uses, and orders already placed keep theirs. Send the row "
                + "version in `If-Match` or in the body.")
            .Accepts<AdminPromoCodeRequest>("application/json")
            .Produces<AdminPromoCodeSavedDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        codes.MapDelete("/{promoCodeId:int}", DeletePromoCodeAsync)
            .RequirePermission(Permissions.Promotions.Delete)
            .WithName("AdminDeletePromoCode")
            .WithSummary("Remove a promo code.")
            .WithDescription(
                "ADM-13, doc 11 P3's \"Remove campaign or code\". A soft delete, which releases the code "
                + "string for reuse. Orders already placed are untouched and still print the code, "
                + "because an order keeps its own `promoCodeText` (doc 08 §3.6 DB19) — and a pending "
                + "order still gives its use back when it expires or is cancelled. Send the row version "
                + "in `If-Match`.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return endpoints;
    }

    private static async Task<IResult> GetPromoCodesAsync(
        IPromoCodeAdminReader reader,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await reader.ListAsync(cancellationToken));

    private static async Task<IResult> CreatePromoCodeAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<CreatePromoCodeCommand, PromoWriteResult> handler,
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
            new CreatePromoCodeCommand(fields, AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(
            result,
            saved => TypedResults.Created(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"/api/admin/marketing/promo-codes/{saved.PromoCode.Id}"),
                saved));
    }

    private static async Task<IResult> UpdatePromoCodeAsync(
        int promoCodeId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<UpdatePromoCodeCommand, PromoWriteResult> handler,
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
            new UpdatePromoCodeCommand(
                promoCodeId,
                fields,
                AdminWriteRequest.RowVersion(httpContext.Request, AdminJsonBody.RowVersionIn(body!)),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, TypedResults.Ok);
    }

    private static async Task<IResult> DeletePromoCodeAsync(
        int promoCodeId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<DeletePromoCodeCommand, PromoWriteResult> handler,
        CancellationToken cancellationToken)
    {
        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var result = await handler.HandleAsync(
            new DeletePromoCodeCommand(
                promoCodeId,
                AdminWriteRequest.RowVersion(httpContext.Request, null),
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return ToHttp(result, _ => TypedResults.NoContent());
    }

    /// <summary>
    /// Reads the form, refusing a body that leaves a field out.
    /// </summary>
    /// <remarks>
    /// <b>Presence, not just value</b> — see the class remarks. Each reader answers a problem document
    /// naming the field, so a client sending a number where a string belongs is told which one rather
    /// than meeting a 500 from the binder.
    /// </remarks>
    private static ValidationProblem? ReadFields(JsonObject body, out PromoCodeInput fields)
    {
        fields = default!;

        if (AdminJsonBody.ReadString(body, "code", out var code) is { } badCode)
        {
            return badCode;
        }

        if (AdminJsonBody.ReadString(body, "description", out var description) is { } badDescription)
        {
            return badDescription;
        }

        if (AdminJsonBody.ReadEnum<PromoDiscountType>(
                body, "discountType", "one of Percent, FixedAmount or FreeDelivery", out var discountType) is { } badType)
        {
            return badType;
        }

        if (AdminJsonBody.ReadDecimal(body, "discountValue", out var discountValue) is { } badValue)
        {
            return badValue;
        }

        if (AdminJsonBody.ReadDecimal(body, "maxDiscountAmount", out var maxDiscount) is { } badCap)
        {
            return badCap;
        }

        if (AdminJsonBody.ReadDecimal(body, "minSpendAmount", out var minSpend) is { } badMinimum)
        {
            return badMinimum;
        }

        if (AdminJsonBody.ReadLocalDateTime(body, "startsAt", out var startsAt) is { } badStart)
        {
            return badStart;
        }

        if (AdminJsonBody.ReadLocalDateTime(body, "endsAt", out var endsAt) is { } badEnd)
        {
            return badEnd;
        }

        if (AdminJsonBody.ReadInt(body, "totalUsageLimit", out var totalLimit) is { } badTotal)
        {
            return badTotal;
        }

        if (AdminJsonBody.ReadInt(body, "perCustomerLimit", out var perCustomerLimit) is { } badPerCustomer)
        {
            return badPerCustomer;
        }

        if (AdminJsonBody.ReadBool(body, "isActive", out var isActive) is { } badActive)
        {
            return badActive;
        }

        fields = new PromoCodeInput(
            code,
            description,
            discountType,
            discountValue,
            maxDiscount,
            minSpend,
            startsAt,
            endsAt,
            totalLimit,
            perCustomerLimit,
            isActive);

        return null;
    }

    /// <summary>The one mapping from an ADM-13 write's answer to HTTP.</summary>
    private static IResult ToHttp(PromoWriteResult result, Func<AdminPromoCodeSavedDto, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        return result switch
        {
            PromoWriteResult.Succeeded success => onSuccess(success.Saved),

            PromoWriteResult.Invalid invalid => AdminJsonBody.Problem(invalid.Errors),

            PromoWriteResult.NotFound missing => AdminWriteRequest.Problem(
                StatusCodes.Status404NotFound,
                "Not found",
                missing.Message,
                "not_found"),

            PromoWriteResult.Conflict conflict => AdminWriteRequest.Problem(
                StatusCodes.Status409Conflict,
                "Cannot be applied",
                conflict.Message,
                conflict.Code),

            _ => throw new InvalidOperationException($"Unhandled promo write result '{result}'."),
        };
    }

}

/// <summary>
/// The promo-code form (ADM-13). <b>The OpenAPI shape only; the endpoint reads the body as a
/// <c>JsonObject</c></b> so that a missing key and an explicit null can be told apart — see
/// <see cref="AdminPromotionEndpoints"/>.
/// </summary>
/// <remarks>
/// Every property is nullable because that is the truth about what JSON can send (issue #47), and every
/// one must still be <i>present</i>: a save that omitted <c>totalUsageLimit</c> would otherwise turn a
/// limited code into an unlimited one.
/// </remarks>
/// <param name="DiscountType">One of <c>Percent</c>, <c>FixedAmount</c> or <c>FreeDelivery</c>.</param>
/// <param name="DiscountValue">
/// The percentage (above 0, up to 100) or the LKR amount (above 0). Must be null for
/// <c>FreeDelivery</c>.
/// </param>
/// <param name="StartsAt">
/// <b>The shop's own time, with no offset</b> — <c>2026-10-13T00:00</c>. A trailing <c>Z</c> or a
/// <c>+05:30</c> is refused, because the server would otherwise have to guess whether the caller meant
/// Colombo and guessing wrong moves a code's window by five and a half hours.
/// </param>
/// <param name="EndsAt">The same, or null for no end.</param>
/// <param name="RowVersion">
/// On an edit: the row version from the list, unless it is sent in <c>If-Match</c>. Ignored on a create.
/// </param>
public sealed record AdminPromoCodeRequest(
    string? Code,
    string? Description,
    string? DiscountType,
    decimal? DiscountValue,
    decimal? MaxDiscountAmount,
    decimal? MinSpendAmount,
    string? StartsAt,
    string? EndsAt,
    int? TotalUsageLimit,
    int? PerCustomerLimit,
    bool? IsActive,
    string? RowVersion);
