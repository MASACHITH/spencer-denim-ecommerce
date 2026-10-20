using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.Application.Catalog.Reviews;

// ---- storefront -----------------------------------------------------------------------------------------

/// <summary>How many approved reviews gave this many stars.</summary>
public sealed record RatingBucketDto(int Stars, int Count);

/// <summary>
/// The product page's rating summary (PDP-08): the average and count of <b>approved</b> reviews, and how many
/// gave each star, 5 down to 1. Computed in one read from the same rows <c>ProductStats</c> is recomputed from,
/// so the bars always add up to the count.
/// </summary>
public sealed record ReviewSummaryDto(decimal? Average, int Count, IReadOnlyList<RatingBucketDto> Distribution);

/// <summary>One approved review as a shopper sees it. Title and body are plain text; render them as text.</summary>
public sealed record PublicReviewDto(
    int Id,
    int Rating,
    string? Title,
    string? Body,
    string DisplayName,
    bool IsVerifiedBuyer,
    DateTime CreatedAtUtc,
    DateTime? EditedAtUtc);

/// <summary>A page of a product's approved reviews, newest first, with the summary.</summary>
public sealed record ProductReviewPageDto(
    ReviewSummaryDto Summary,
    IReadOnlyList<PublicReviewDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>The signed-in customer's own review of one product, whatever its status.</summary>
public sealed record MyReviewDto(
    int Id,
    int ProductId,
    int Rating,
    string? Title,
    string? Body,
    string DisplayName,
    bool IsVerifiedBuyer,
    ReviewStatus Status,
    DateTime CreatedAtUtc,
    DateTime? EditedAtUtc);

/// <summary>
/// What the write-a-review form needs: the customer's existing review (to edit) and whether a review written now
/// would carry the Verified buyer badge.
/// </summary>
public sealed record MyProductReviewDto(MyReviewDto? Review, bool WouldBeVerified);

/// <summary>What happened to a submission, in the words the storefront turns into a message.</summary>
public enum ReviewSubmissionOutcome
{
    /// <summary>Saved and waiting for a moderator (<c>Reviews.RequireApproval</c> on).</summary>
    Pending = 1,

    /// <summary>Saved and shown on the product page now.</summary>
    Published = 2,

    /// <summary>An edit of a review staff had hidden: saved, still not shown.</summary>
    StillHidden = 3,
}

/// <summary>A saved review and what the customer should be told.</summary>
public sealed record ReviewSubmissionDto(MyReviewDto Review, ReviewSubmissionOutcome Outcome);

// ---- admin ----------------------------------------------------------------------------------------------

/// <summary>Which reviews the moderation list shows (doc 11 P12's "pending, approved, hidden").</summary>
public enum AdminReviewStatusFilter
{
    Pending = 1,
    Approved = 2,
    Hidden = 3,
    All = 4,
}

/// <summary>One review as a moderator sees it. <c>RowVersion</c> is the token approve and hide must send back.</summary>
public sealed record AdminReviewDto(
    int Id,
    int ProductId,
    string ProductName,
    string ProductCode,
    string ProductSlug,
    int Rating,
    string? Title,
    string? Body,
    string DisplayName,
    bool IsVerifiedBuyer,
    ReviewStatus Status,
    DateTime CreatedAtUtc,
    DateTime? EditedAtUtc,
    DateTime? ModeratedAtUtc,
    string? ModeratedByName,
    string? ModerationNote,
    string RowVersion);

/// <summary>How many reviews are in each status, for the filter tabs. Ignores the status filter and search.</summary>
public sealed record ReviewStatusCountsDto(int Pending, int Approved, int Hidden);

public sealed record AdminReviewListPage(
    IReadOnlyList<AdminReviewDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    ReviewStatusCountsDto Counts);

/// <summary>The moderation list's filters, already clamped.</summary>
public sealed record AdminReviewCriteria(
    AdminReviewStatusFilter Status,
    bool? Verified,
    string? Search,
    int Page,
    int PageSize)
{
    public const int DefaultPageSize = 25;

    public const int MaxPageSize = 100;

    public const int MaxPage = 10_000;

    public const int MaxSearchLength = 100;
}
