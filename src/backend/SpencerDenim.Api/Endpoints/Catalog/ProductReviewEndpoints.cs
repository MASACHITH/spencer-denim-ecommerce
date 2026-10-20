using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using SpencerDenim.Api.Endpoints.Accounts;
using SpencerDenim.Api.Http;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Catalog.Products;
using SpencerDenim.Application.Catalog.Reviews;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Infrastructure.Identity.Customers;

namespace SpencerDenim.Api.Endpoints.Catalog;

/// <summary>
/// Reviews and ratings on the storefront (PDP-08; doc 11 P12; issue #189): the product page's approved reviews,
/// and a signed-in customer's own review.
/// </summary>
/// <remarks>
/// <b>Two kinds of route, and they are cached differently on purpose.</b> The product's reviews are the same for
/// every visitor — approved reviews only, no session read — so that route sets no cache headers, like the rest of
/// <c>/api/catalog</c>, and the server-rendered product page can carry them. Everything under
/// <c>/api/account/reviews</c> is about one person, needs the customer session and answers <c>no-store</c> with
/// <c>Vary: Cookie</c> (<c>CustomerSessionCookie.DoNotStore</c>), the account routes' rule.
/// <para>
/// <b>No antiforgery token, for the account routes' reason</b> (<c>CustomerAuthEndpoints</c>' remarks): the
/// customer cookie is <c>SameSite=Lax</c>, so a cross-site <c>POST</c>/<c>PUT</c> carries no session, and every
/// write reads a JSON body a cross-site form cannot send without a preflight the API does not allow.
/// </para>
/// <para>
/// <b>The customer is always the session's.</b> No route takes a customer id; the product id is in the path.
/// </para>
/// </remarks>
internal static class ProductReviewEndpoints
{
    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head];

    public static IEndpointRouteBuilder MapProductReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapMethods("/api/catalog/products/{slug}/reviews", ReadMethods, ListAsync)
            .WithTags("Catalog")
            .WithName("GetProductReviews")
            .WithSummary("A product's approved reviews, newest first, and its rating summary (PDP-08).")
            .WithDescription(
                "Only approved reviews are listed or counted (doc 08 §7.4). The summary's average and count are "
                + "the approved reviews' and match ProductStats; distribution is the count per star, 5 down to 1. "
                + "?page= from 1 and ?pageSize= 1 to 50 (default 10), clamped. Title and body are plain text: "
                + "render them as text, never as HTML. 404 when no published product has the slug.")
            .Produces<ProductReviewPageDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesValidationProblem();

        var account = endpoints
            .MapGroup("/api/account/reviews")
            .WithTags("Customer Account")
            .RequireAuthorization(CustomerAuthorization.PolicyName);

        account.MapGet("/{productId:int}", GetMineAsync)
            .WithName("CustomerGetProductReview")
            .WithSummary("The signed-in customer's own review of a product, and whether a review would be verified.")
            .WithDescription(
                "review is null when they have not reviewed it; its status may be Pending, Approved or Hidden. "
                + "wouldBeVerified is true when a Delivered order of this account contains the product (doc 08 "
                + "D4) — never by email address. 404 when the product is not published.")
            .Produces<MyProductReviewDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        account.MapPost("/{productId:int}", CreateAsync)
            .WithName("CustomerCreateProductReview")
            .WithSummary("Write a review of a product (doc 11 P12 steps 1–3).")
            .WithDescription(
                "Body: { rating, title, body }. rating is a whole number 1 to 5 (required); title up to 120 "
                + "characters on one line and body up to 4,000, both optional, plain text, no control characters. "
                + "One review per customer per product: a second answers 409 already_reviewed with the existing "
                + "review, to be edited with PUT. Verified buyer is set when a Delivered order of this account "
                + "contains the product. outcome is Pending (Reviews.RequireApproval on, the default) or "
                + "Published. At most 10 writes an hour per customer (429 with Retry-After); a refused (400) body "
                + "does not count.")
            .Produces<ReviewSubmissionDto>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        account.MapPut("/{productId:int}", EditAsync)
            .WithName("CustomerEditProductReview")
            .WithSummary("Edit the customer's own review of a product (doc 11 §7 G-32).")
            .WithDescription(
                "Same body and rules as POST. An edit returns the review to Pending while approval is required; a "
                + "review staff have hidden stays hidden (outcome StillHidden). The display name keeps its "
                + "first-submission snapshot; the Verified buyer badge can be gained, never lost. 404 not_reviewed "
                + "when there is nothing to edit.")
            .Produces<ReviewSubmissionDto>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        string slug,
        int? page,
        int? pageSize,
        IQueryHandler<GetProductReviewsQuery, ProductReviewPageDto?> handler,
        CancellationToken cancellationToken)
    {
        var query = GetProductBySlugQuery.Create(slug);

        if (!query.IsSuccess)
        {
            return ValidationProblem(query.Errors);
        }

        var result = await handler.HandleAsync(
            new GetProductReviewsQuery(query.Value.Slug, page, pageSize),
            cancellationToken);

        return result is null
            ? TypedResults.Problem(
                title: "Product not found",
                detail: $"No published product has the slug '{query.Value.Slug}'.",
                statusCode: StatusCodes.Status404NotFound)
            : TypedResults.Ok(result);
    }

    private static async Task<IResult> GetMineAsync(
        int productId,
        HttpContext httpContext,
        IQueryHandler<GetMyProductReviewQuery, MyProductReviewDto?> handler,
        CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var mine = await handler.HandleAsync(new GetMyProductReviewQuery(userId, productId), cancellationToken);

        return mine is null ? ProductNotFound(productId) : TypedResults.Ok(mine);
    }

    private static Task<IResult> CreateAsync(
        int productId,
        HttpContext httpContext,
        ReviewWriteThrottle throttle,
        ICommandHandler<CreateReviewCommand, ReviewCommandResult> handler,
        CancellationToken cancellationToken) =>
        WriteAsync(
            productId,
            httpContext,
            throttle,
            (userId, rating, title, body) => handler.HandleAsync(
                new CreateReviewCommand(userId, productId, rating, title, body),
                cancellationToken),
            created: true,
            cancellationToken);

    private static Task<IResult> EditAsync(
        int productId,
        HttpContext httpContext,
        ReviewWriteThrottle throttle,
        ICommandHandler<EditReviewCommand, ReviewCommandResult> handler,
        CancellationToken cancellationToken) =>
        WriteAsync(
            productId,
            httpContext,
            throttle,
            (userId, rating, title, body) => handler.HandleAsync(
                new EditReviewCommand(userId, productId, rating, title, body),
                cancellationToken),
            created: false,
            cancellationToken);

    private static async Task<IResult> WriteAsync(
        int productId,
        HttpContext httpContext,
        ReviewWriteThrottle throttle,
        Func<int, int?, string?, string?, Task<ReviewCommandResult>> write,
        bool created,
        CancellationToken cancellationToken)
    {
        CustomerSessionCookie.DoNotStore(httpContext.Response);

        if (!CustomerSessionCookie.TryGetUserId(httpContext.User, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var (body, problem) = await AdminWriteRequest.ReadJsonAsync<JsonObject>(httpContext.Request, cancellationToken);

        if (problem is not null)
        {
            return problem;
        }

        var errors = new List<ValidationError>();
        var rating = ReadRating(body!, errors);
        var title = ReadText(body!, "title", errors);
        var text = ReadText(body!, "body", errors);

        if (errors.Count > 0)
        {
            return ValidationProblem(errors);
        }

        // The use case validates again; this copy exists only so that the permit below is taken for a review that
        // could be saved. A refused (400) body costs the customer nothing; anything that reaches the database —
        // a save, a 409 already-reviewed, a 404 — spends a permit, because each of those is a transaction.
        var (_, problems) = ReviewRules.Clean(rating, title, text);

        if (problems.Count > 0)
        {
            return ValidationProblem(problems.Select(item => new ValidationError(item.Field, item.Message)).ToList());
        }

        if (!throttle.TryAcquire(userId))
        {
            httpContext.Response.Headers.RetryAfter =
                ((int)ReviewWriteThrottle.Window.TotalSeconds).ToString(CultureInfo.InvariantCulture);

            return TypedResults.Problem(
                title: "Too many reviews",
                detail: "You have written or edited a lot of reviews in the last hour. Please try again later.",
                statusCode: StatusCodes.Status429TooManyRequests,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "rate_limited" });
        }

        var result = await write(userId, rating, title, text);

        return result switch
        {
            ReviewCommandResult.Saved saved => created
                ? TypedResults.Created($"/api/account/reviews/{productId.ToString(CultureInfo.InvariantCulture)}", saved.Submission)
                : TypedResults.Ok(saved.Submission),
            ReviewCommandResult.Invalid invalid => ValidationProblem(invalid.Errors),
            ReviewCommandResult.ProductNotFound => ProductNotFound(productId),
            ReviewCommandResult.AlreadyReviewed already => TypedResults.Problem(
                title: "Already reviewed",
                detail: "You have already reviewed this product. You can edit your review instead.",
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["code"] = "already_reviewed",
                    ["review"] = already.Existing,
                }),
            ReviewCommandResult.NotReviewed => TypedResults.Problem(
                title: "No review to edit",
                detail: "You have not reviewed this product yet.",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "not_reviewed" }),
            ReviewCommandResult.SignedOut => TypedResults.Unauthorized(),
            _ => throw new InvalidOperationException($"Unexpected review result '{result}'."),
        };
    }

    /// <summary>
    /// <c>rating</c>: absent or null is "not chosen" (the use case says so), a JSON whole number is the value, and
    /// anything else — <c>4.5</c>, <c>"4"</c>, <c>true</c> — is refused here rather than coerced.
    /// </summary>
    private static int? ReadRating(JsonObject body, List<ValidationError> errors)
    {
        if (!body.TryGetPropertyValue("rating", out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue json && json.TryGetValue<int>(out var rating))
        {
            return rating;
        }

        errors.Add(new ValidationError("rating", "The rating must be a whole number from 1 to 5."));
        return null;
    }

    /// <summary>A text field: absent or null is "none"; a non-string is refused.</summary>
    private static string? ReadText(JsonObject body, string field, List<ValidationError> errors)
    {
        if (!body.TryGetPropertyValue(field, out var node) || node is null)
        {
            return null;
        }

        if (node is JsonValue json && json.TryGetValue<string>(out var text))
        {
            return text;
        }

        errors.Add(new ValidationError(field, $"Send {field} as text."));
        return null;
    }

    private static ProblemHttpResult ProductNotFound(int productId) =>
        TypedResults.Problem(
            title: "Product not found",
            detail: string.Create(CultureInfo.InvariantCulture, $"No published product has the id {productId}."),
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "product_not_found" });

    private static ValidationProblem ValidationProblem(IReadOnlyList<ValidationError> errors) =>
        TypedResults.ValidationProblem(
            errors
                .GroupBy(error => error.Field, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(error => error.Message).ToArray(),
                    StringComparer.Ordinal));
}
