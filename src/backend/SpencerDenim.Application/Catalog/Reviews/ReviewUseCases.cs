using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Reviews;

// ---- queries --------------------------------------------------------------------------------------------

/// <summary>A product's approved reviews (public). <paramref name="Slug"/> has already passed the slug rule.</summary>
public sealed record GetProductReviewsQuery(string Slug, int? Page, int? PageSize)
{
    public const int DefaultPageSize = 10;

    public const int MaxPageSize = 50;

    public const int MaxPage = 10_000;
}

/// <summary>The signed-in customer's own review of one product.</summary>
public sealed record GetMyProductReviewQuery(int CustomerUserId, int ProductId);

/// <summary>The moderation list (<c>Reviews.View</c>). Page and page size are clamped, never refused.</summary>
public sealed record GetAdminReviewsQuery(
    AdminReviewStatusFilter Status,
    bool? Verified,
    string? Search,
    int? Page,
    int? PageSize);

public sealed class ReviewQueryHandlers(IProductReviewReader reader)
    : IQueryHandler<GetProductReviewsQuery, ProductReviewPageDto?>,
        IQueryHandler<GetMyProductReviewQuery, MyProductReviewDto?>,
        IQueryHandler<GetAdminReviewsQuery, AdminReviewListPage>
{
    public Task<ProductReviewPageDto?> HandleAsync(GetProductReviewsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.ListPublishedAsync(
            query.Slug,
            Math.Clamp(query.Page ?? 1, 1, GetProductReviewsQuery.MaxPage),
            Math.Clamp(query.PageSize ?? GetProductReviewsQuery.DefaultPageSize, 1, GetProductReviewsQuery.MaxPageSize),
            cancellationToken);
    }

    public Task<MyProductReviewDto?> HandleAsync(GetMyProductReviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        return reader.MineAsync(query.CustomerUserId, query.ProductId, cancellationToken);
    }

    public Task<AdminReviewListPage> HandleAsync(GetAdminReviewsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var search = query.Search?.Trim();

        if (string.IsNullOrEmpty(search))
        {
            search = null;
        }
        else if (search.Length > AdminReviewCriteria.MaxSearchLength)
        {
            search = search[..AdminReviewCriteria.MaxSearchLength];
        }

        return reader.ListForAdminAsync(
            new AdminReviewCriteria(
                query.Status,
                query.Verified,
                search,
                Math.Clamp(query.Page ?? 1, 1, AdminReviewCriteria.MaxPage),
                Math.Clamp(query.PageSize ?? AdminReviewCriteria.DefaultPageSize, 1, AdminReviewCriteria.MaxPageSize)),
            cancellationToken);
    }
}

// ---- customer writes ------------------------------------------------------------------------------------

/// <summary>Write a review (doc 11 P12 steps 1–3). The customer comes from the session, never from the body.</summary>
public sealed record CreateReviewCommand(int CustomerUserId, int ProductId, int? Rating, string? Title, string? Body);

/// <summary>Edit the customer's own review (doc 11 §7 G-32).</summary>
public sealed record EditReviewCommand(int CustomerUserId, int ProductId, int? Rating, string? Title, string? Body);

/// <summary>What a customer write came to, for the endpoint to turn into a status code.</summary>
public abstract record ReviewCommandResult
{
    private ReviewCommandResult()
    {
    }

    public sealed record Saved(ReviewSubmissionDto Submission) : ReviewCommandResult;

    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : ReviewCommandResult;

    public sealed record ProductNotFound : ReviewCommandResult;

    public sealed record AlreadyReviewed(MyReviewDto Existing) : ReviewCommandResult;

    public sealed record NotReviewed : ReviewCommandResult;

    public sealed record SignedOut : ReviewCommandResult;
}

/// <summary>
/// Validates a customer's review, reads <c>Reviews.RequireApproval</c>, and hands the write to the store, which
/// decides "already reviewed" and "Verified buyer" under the product's lock.
/// </summary>
public sealed class ReviewWriteHandlers(IProductReviewStore store, IReviewSettingsReader settings, IClock clock)
    : ICommandHandler<CreateReviewCommand, ReviewCommandResult>,
        ICommandHandler<EditReviewCommand, ReviewCommandResult>
{
    public Task<ReviewCommandResult> HandleAsync(CreateReviewCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return WriteAsync(
            command.CustomerUserId,
            command.ProductId,
            command.Rating,
            command.Title,
            command.Body,
            store.CreateAsync,
            cancellationToken);
    }

    public Task<ReviewCommandResult> HandleAsync(EditReviewCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        return WriteAsync(
            command.CustomerUserId,
            command.ProductId,
            command.Rating,
            command.Title,
            command.Body,
            store.EditAsync,
            cancellationToken);
    }

    /// <summary>The submission outcome a saved status means.</summary>
    public static ReviewSubmissionOutcome OutcomeOf(ReviewStatus status) => status switch
    {
        ReviewStatus.Approved => ReviewSubmissionOutcome.Published,
        ReviewStatus.Hidden => ReviewSubmissionOutcome.StillHidden,
        _ => ReviewSubmissionOutcome.Pending,
    };

    private async Task<ReviewCommandResult> WriteAsync(
        int customerUserId,
        int productId,
        int? rating,
        string? title,
        string? body,
        Func<ReviewWrite, CancellationToken, Task<ReviewWriteOutcome>> write,
        CancellationToken cancellationToken)
    {
        var (review, problems) = ReviewRules.Clean(rating, title, body);

        if (review is null)
        {
            return new ReviewCommandResult.Invalid(
                problems.Select(problem => new ValidationError(problem.Field, problem.Message)).ToList());
        }

        var requireApproval = await settings.RequireApprovalAsync(cancellationToken);

        var outcome = await write(
            new ReviewWrite(customerUserId, productId, review, requireApproval, clock.UtcNow.UtcDateTime),
            cancellationToken);

        return outcome switch
        {
            ReviewWriteOutcome.Saved saved => new ReviewCommandResult.Saved(
                new ReviewSubmissionDto(saved.Review, OutcomeOf(saved.Review.Status))),
            ReviewWriteOutcome.ProductNotFound => new ReviewCommandResult.ProductNotFound(),
            ReviewWriteOutcome.AlreadyReviewed already => new ReviewCommandResult.AlreadyReviewed(already.Existing),
            ReviewWriteOutcome.NotReviewed => new ReviewCommandResult.NotReviewed(),
            ReviewWriteOutcome.NotACustomer => new ReviewCommandResult.SignedOut(),
            _ => throw new InvalidOperationException($"Unexpected review write outcome '{outcome}'."),
        };
    }
}

// ---- moderation -----------------------------------------------------------------------------------------

/// <summary>Approve (<c>Reviews.Approve</c>) or hide (<c>Reviews.Edit</c>) one review (doc 11 P12 step 5).</summary>
public sealed record ModerateReviewCommand(
    int ReviewId,
    ReviewModeration Action,
    string? RowVersion,
    string? Note,
    AdminActor Actor);

public abstract record ModerationResult
{
    private ModerationResult()
    {
    }

    public sealed record Applied(AdminReviewDto Review) : ModerationResult;

    public sealed record Unchanged(AdminReviewDto Review) : ModerationResult;

    public sealed record Invalid(IReadOnlyList<ValidationError> Errors) : ModerationResult;

    public sealed record Stale(AdminReviewDto Current) : ModerationResult;

    public sealed record NotFound : ModerationResult;
}

public sealed class ModerateReviewHandler(IProductReviewStore store, IClock clock)
    : ICommandHandler<ModerateReviewCommand, ModerationResult>
{
    public async Task<ModerationResult> HandleAsync(ModerateReviewCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var errors = new List<ValidationError>();
        var rowVersion = ReadRowVersion(command.RowVersion);

        if (rowVersion is null)
        {
            errors.Add(new ValidationError(
                "rowVersion",
                "Send the review's rowVersion as the list gave it, so a change made since you loaded it is not overwritten."));
        }

        var (note, noteProblem) = ReviewRules.CleanNote(command.Note);

        if (noteProblem is not null)
        {
            errors.Add(new ValidationError(noteProblem.Field, noteProblem.Message));
        }

        if (errors.Count > 0)
        {
            return new ModerationResult.Invalid(errors);
        }

        var outcome = await store.ModerateAsync(
            new ReviewModerationRequest(
                command.ReviewId,
                command.Action,
                rowVersion!,
                note,
                command.Actor,
                clock.UtcNow.UtcDateTime),
            cancellationToken);

        return outcome switch
        {
            ReviewModerationOutcome.Applied applied => new ModerationResult.Applied(applied.Review),
            ReviewModerationOutcome.Unchanged unchanged => new ModerationResult.Unchanged(unchanged.Review),
            ReviewModerationOutcome.Stale stale => new ModerationResult.Stale(stale.Current),
            ReviewModerationOutcome.NotFound => new ModerationResult.NotFound(),
            _ => throw new InvalidOperationException($"Unexpected moderation outcome '{outcome}'."),
        };
    }

    /// <summary>A rowversion is exactly eight bytes; anything else cannot match a row and is refused as malformed.</summary>
    internal static byte[]? ReadRowVersion(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[16];

        return Convert.TryFromBase64String(base64.Trim(), buffer, out var written) && written == 8
            ? buffer[..written].ToArray()
            : null;
    }
}
