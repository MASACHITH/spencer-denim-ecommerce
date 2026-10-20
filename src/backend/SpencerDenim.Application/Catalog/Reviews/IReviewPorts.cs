using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Reviews;

/// <summary>The review reads: the product page, the customer's own review, and the moderation list.</summary>
public interface IProductReviewReader
{
    /// <summary>
    /// A visible product's approved reviews, newest first, and its summary; <see langword="null"/> when no
    /// visible product has this slug.
    /// </summary>
    Task<ProductReviewPageDto?> ListPublishedAsync(string slug, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// This customer's review of a visible product and whether a review now would be verified;
    /// <see langword="null"/> when the product is not visible.
    /// </summary>
    Task<MyProductReviewDto?> MineAsync(int customerUserId, int productId, CancellationToken cancellationToken);

    /// <summary>The moderation list (<c>Reviews.View</c>).</summary>
    Task<AdminReviewListPage> ListForAdminAsync(AdminReviewCriteria criteria, CancellationToken cancellationToken);
}

/// <summary>A customer's create or edit, already validated.</summary>
public sealed record ReviewWrite(
    int CustomerUserId,
    int ProductId,
    CleanReview Review,
    bool RequireApproval,
    DateTime NowUtc);

/// <summary>What a create or edit came to.</summary>
public abstract record ReviewWriteOutcome
{
    private ReviewWriteOutcome()
    {
    }

    public sealed record Saved(MyReviewDto Review) : ReviewWriteOutcome;

    /// <summary>No visible product has this id.</summary>
    public sealed record ProductNotFound : ReviewWriteOutcome;

    /// <summary>A create, but this customer has already reviewed the product (doc 08 §3.2's unique index).</summary>
    public sealed record AlreadyReviewed(MyReviewDto Existing) : ReviewWriteOutcome;

    /// <summary>An edit, but this customer has no review of the product.</summary>
    public sealed record NotReviewed : ReviewWriteOutcome;

    /// <summary>The session names no active customer account.</summary>
    public sealed record NotACustomer : ReviewWriteOutcome;
}

/// <summary>A moderator's approve or hide, already validated.</summary>
public sealed record ReviewModerationRequest(
    int ReviewId,
    ReviewModeration Action,
    byte[] ExpectedRowVersion,
    string? Note,
    AdminActor Actor,
    DateTime NowUtc);

/// <summary>What a moderation came to.</summary>
public abstract record ReviewModerationOutcome
{
    private ReviewModerationOutcome()
    {
    }

    /// <summary>The status moved; one audit row was written and the rating recomputed.</summary>
    public sealed record Applied(AdminReviewDto Review) : ReviewModerationOutcome;

    /// <summary>It was already in the target status; nothing written, nothing audited.</summary>
    public sealed record Unchanged(AdminReviewDto Review) : ReviewModerationOutcome;

    /// <summary>Somebody (staff or the author) changed it since the moderator loaded it.</summary>
    public sealed record Stale(AdminReviewDto Current) : ReviewModerationOutcome;

    public sealed record NotFound : ReviewModerationOutcome;
}

/// <summary>
/// The review writes. <b>Every one recomputes <c>ProductStats.RatingAverage/RatingCount</c> in its own
/// transaction</b> (doc 08 §7.4: "same transaction as moderation or edit").
/// </summary>
public interface IProductReviewStore
{
    Task<ReviewWriteOutcome> CreateAsync(ReviewWrite write, CancellationToken cancellationToken);

    Task<ReviewWriteOutcome> EditAsync(ReviewWrite write, CancellationToken cancellationToken);

    Task<ReviewModerationOutcome> ModerateAsync(ReviewModerationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Doc 08 §3.1 step 2's review half of PDPA anonymisation: delete every review this account wrote and recompute
/// the rating of every product they touched.
/// </summary>
/// <remarks>
/// <b>Nothing calls this yet, because the anonymisation routine (doc 11 P17, <c>Customers.Delete</c>) is not
/// built.</b> It is the hook that routine must call, inside its own transaction when it has one (the method joins
/// an ambient EF transaction on the same context, and opens its own otherwise). Recorded in the #189 journal as
/// owed to P17.
/// </remarks>
public interface IProductReviewErasure
{
    /// <summary>Deletes the account's reviews and recomputes the affected ratings; answers how many were deleted.</summary>
    Task<int> EraseForCustomerAsync(int customerUserId, CancellationToken cancellationToken);
}

/// <summary><c>Reviews.RequireApproval</c>, read per submission.</summary>
public interface IReviewSettingsReader
{
    /// <summary>The switch; <see langword="true"/> when the row is missing or does not hold true or false.</summary>
    Task<bool> RequireApprovalAsync(CancellationToken cancellationToken);
}
