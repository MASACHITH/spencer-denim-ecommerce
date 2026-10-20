using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using SpencerDenim.Api.Authorization;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Reviews;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Identity;

namespace SpencerDenim.Api.Endpoints.Admin;

/// <summary>What approve and hide answer: the review as it now is, and whether this request changed it.</summary>
internal sealed record AdminModerationResponse(AdminReviewDto Review, bool Changed);

/// <summary>
/// Review moderation (ADM-16, doc 11 P12; issue #189).
/// <para>
/// <b>Permissions are doc 11 P12's own table</b>: the list needs <c>Reviews.View</c>; approving (including
/// approving a hidden review again) needs <c>Reviews.Approve</c>; hiding needs <c>Reviews.Edit</c>. Content Editor,
/// Store Manager and Super Admin hold all three by default (doc 02 §G, doc 08 §9); Order Staff hold none.
/// <c>Reviews.Delete</c> exists but no route uses it: ADM-16 is "approve / hide", and a hidden review is already off
/// the site (recorded in the #189 journal).
/// </para>
/// <para>
/// <b>Every applied action writes one audit row and recomputes the product's rating in the same transaction</b>,
/// and is conditional on the rowversion the moderator loaded (<c>ProductReviewStore</c>).
/// </para>
/// </summary>
internal static class AdminReviewEndpoints
{
    public static IEndpointRouteBuilder MapAdminReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var reviews = endpoints.MapGroup("/api/admin/reviews").WithTags("Admin Reviews");

        reviews.MapGet("/", ListAsync)
            .RequirePermission(Permissions.Reviews.View)
            .WithName("AdminListReviews")
            .WithSummary("Reviews to moderate, with counts per status (ADM-16, doc 11 P12 step 4).")
            .WithDescription(
                "?status=Pending (default; oldest first), Approved, Hidden or All (newest first); ?verified=true "
                + "or false; ?search= matches part of the product name or code, the reviewer's display name, the "
                + "title or the text. Paged, 25 by default, at most 100. counts ignore the filters. rowVersion on "
                + "each row is what approve and hide must send back.")
            .Produces<AdminReviewListPage>()
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        reviews.MapPost("/{reviewId:int}/approve", ApproveAsync)
            .RequirePermission(Permissions.Reviews.Approve)
            .WithName("AdminApproveReview")
            .WithSummary("Show a pending or hidden review on the product page (Reviews.Approve).")
            .WithDescription(ModerationDescription)
            .Produces<AdminModerationResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status403Forbidden);

        reviews.MapPost("/{reviewId:int}/hide", HideAsync)
            .RequirePermission(Permissions.Reviews.Edit)
            .WithName("AdminHideReview")
            .WithSummary("Take a pending or approved review off the site (Reviews.Edit).")
            .WithDescription(ModerationDescription)
            .Produces<AdminModerationResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private const string ModerationDescription =
        "Body: { rowVersion, note }. rowVersion is required (the list's value); note is optional, up to 500 "
        + "characters, kept on the review and never copied into the audit log. Applies only while the review is "
        + "still the version you loaded: otherwise 409 stale_review with the review as it now is. A review already "
        + "in the requested status answers 200 with changed false and writes nothing. An applied action writes "
        + "one audit row and recomputes the product's rating from its approved reviews.";

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        IQueryHandler<GetAdminReviewsQuery, AdminReviewListPage> handler,
        string? status,
        string? verified,
        string? search,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        var errors = new List<ValidationError>();
        var chosenStatus = AdminReviewStatusFilter.Pending;

        if (!string.IsNullOrWhiteSpace(status)
            && !AdminInventoryEndpoints.TryExactName(status, out chosenStatus))
        {
            errors.Add(new ValidationError("status", "Use status=Pending, Approved, Hidden or All."));
        }

        bool? chosenVerified = null;

        if (!string.IsNullOrWhiteSpace(verified))
        {
            if (bool.TryParse(verified.Trim(), out var flag))
            {
                chosenVerified = flag;
            }
            else
            {
                errors.Add(new ValidationError("verified", "Use verified=true or verified=false."));
            }
        }

        if (errors.Count > 0)
        {
            return AdminJsonBody.Problem(errors);
        }

        return TypedResults.Ok(await handler.HandleAsync(
            new GetAdminReviewsQuery(chosenStatus, chosenVerified, search, page, pageSize),
            cancellationToken));
    }

    private static Task<IResult> ApproveAsync(
        int reviewId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ModerateReviewCommand, ModerationResult> handler,
        CancellationToken cancellationToken) =>
        ModerateAsync(reviewId, ReviewModeration.Approve, httpContext, antiforgery, handler, cancellationToken);

    private static Task<IResult> HideAsync(
        int reviewId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ModerateReviewCommand, ModerationResult> handler,
        CancellationToken cancellationToken) =>
        ModerateAsync(reviewId, ReviewModeration.Hide, httpContext, antiforgery, handler, cancellationToken);

    private static async Task<IResult> ModerateAsync(
        int reviewId,
        ReviewModeration action,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        ICommandHandler<ModerateReviewCommand, ModerationResult> handler,
        CancellationToken cancellationToken)
    {
        NoStore(httpContext.Response);

        if (await AdminWriteRequest.ValidateAntiforgeryAsync(httpContext, antiforgery) is { } forged)
        {
            return forged;
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(httpContext.Request, cancellationToken);

        if (problem is not null)
        {
            return problem;
        }

        if (AdminJsonBody.ReadString(body!, "rowVersion", out var rowVersion) is { } badVersion)
        {
            return badVersion;
        }

        // The note may be left out entirely; present and null means the same.
        string? note = null;

        if (body!.ContainsKey("note") && AdminJsonBody.ReadString(body, "note", out note) is { } badNote)
        {
            return badNote;
        }

        var result = await handler.HandleAsync(
            new ModerateReviewCommand(
                reviewId,
                action,
                AdminWriteRequest.RowVersion(httpContext.Request, rowVersion),
                note,
                AdminWriteRequest.Actor(httpContext)),
            cancellationToken);

        return result switch
        {
            ModerationResult.Applied applied => TypedResults.Ok(new AdminModerationResponse(applied.Review, Changed: true)),
            ModerationResult.Unchanged unchanged => TypedResults.Ok(new AdminModerationResponse(unchanged.Review, Changed: false)),
            ModerationResult.Invalid invalid => AdminJsonBody.Problem(invalid.Errors),
            ModerationResult.Stale stale => TypedResults.Problem(
                title: "The review has changed",
                detail: "Somebody changed this review since you loaded it — another moderator, or its author editing "
                    + "it. Nothing was changed. Check it again and decide.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "stale_review",
                    ["review"] = stale.Current,
                }),
            ModerationResult.NotFound => AdminWriteRequest.Problem(
                StatusCodes.Status404NotFound,
                "Not found",
                "There is no such review, or its product has been removed.",
                "not_found"),
            _ => throw new InvalidOperationException($"Unexpected moderation result '{result}'."),
        };
    }

    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, private";
        response.Headers.Vary = "Cookie";
    }
}
