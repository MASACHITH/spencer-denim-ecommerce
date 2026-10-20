using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Catalog.Reviews;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Catalog.Reviews;

/// <summary>
/// The review reads (PDP-08, ADM-16; issue #189). Every ordering is applied before the projection (#126: a
/// positional record projected first and ordered afterwards answered 500).
/// </summary>
internal sealed class ProductReviewReader(AppDbContext dbContext) : IProductReviewReader
{
    public async Task<ProductReviewPageDto?> ListPublishedAsync(
        string slug,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var productId = await dbContext.Products
            .AsNoTracking()
            .Where(CatalogVisibility.VisibleProduct)
            .Where(product => product.Slug == slug)
            .Select(product => (int?)product.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (productId is not { } id)
        {
            return null;
        }

        var approved = dbContext.ProductReviews
            .AsNoTracking()
            .Where(review => review.ProductId == id && review.Status == ReviewStatus.Approved);

        var buckets = await approved
            .GroupBy(review => review.Rating)
            .Select(group => new { Stars = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var total = buckets.Sum(bucket => bucket.Count);
        var sum = buckets.Sum(bucket => (long)bucket.Stars * bucket.Count);

        var distribution = Enumerable.Range(ReviewRules.MinRating, ReviewRules.MaxRating)
            .Reverse()
            .Select(stars => new RatingBucketDto(stars, buckets.FirstOrDefault(bucket => bucket.Stars == stars)?.Count ?? 0))
            .ToList();

        var rows = await approved
            .OrderByDescending(review => review.CreatedAtUtc)
            .ThenByDescending(review => review.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(review => new
            {
                review.Id,
                review.Rating,
                review.Title,
                review.Body,
                review.DisplayName,
                review.IsVerifiedBuyer,
                review.CreatedAtUtc,
                review.UpdatedAtUtc,
            })
            .ToListAsync(cancellationToken);

        return new ProductReviewPageDto(
            new ReviewSummaryDto(ReviewRules.AverageOf(sum, total), total, distribution),
            rows.Select(row => new PublicReviewDto(
                    row.Id,
                    row.Rating,
                    row.Title,
                    row.Body,
                    row.DisplayName,
                    row.IsVerifiedBuyer,
                    row.CreatedAtUtc,
                    row.UpdatedAtUtc))
                .ToList(),
            page,
            pageSize,
            total);
    }

    public async Task<MyProductReviewDto?> MineAsync(int customerUserId, int productId, CancellationToken cancellationToken)
    {
        var visible = await dbContext.Products
            .AsNoTracking()
            .Where(CatalogVisibility.VisibleProduct)
            .AnyAsync(product => product.Id == productId, cancellationToken);

        if (!visible)
        {
            return null;
        }

        var review = await ReadMineAsync(dbContext, customerUserId, productId, cancellationToken);
        var wouldBeVerified = review?.IsVerifiedBuyer == true
            || await QualifyingOrderAsync(dbContext, customerUserId, productId, cancellationToken) is not null;

        return new MyProductReviewDto(review, wouldBeVerified);
    }

    public async Task<AdminReviewListPage> ListForAdminAsync(AdminReviewCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        // The product join (its soft-delete filter) is part of every count and list: a removed product's reviews
        // are kept (doc 11 P12) but are not on this screen, and moderating one answers 404.
        var all = dbContext.ProductReviews.AsNoTracking().Where(review => review.Product != null);

        var counts = await all
            .GroupBy(review => review.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var filtered = all;

        filtered = criteria.Status switch
        {
            AdminReviewStatusFilter.Pending => filtered.Where(review => review.Status == ReviewStatus.Pending),
            AdminReviewStatusFilter.Approved => filtered.Where(review => review.Status == ReviewStatus.Approved),
            AdminReviewStatusFilter.Hidden => filtered.Where(review => review.Status == ReviewStatus.Hidden),
            _ => filtered,
        };

        if (criteria.Verified is { } verified)
        {
            filtered = filtered.Where(review => review.IsVerifiedBuyer == verified);
        }

        if (criteria.Search is { } search)
        {
            filtered = filtered.Where(review =>
                review.Product!.Name.Contains(search)
                || review.Product!.ProductCode.Contains(search)
                || review.DisplayName.Contains(search)
                || (review.Title != null && review.Title.Contains(search))
                || (review.Body != null && review.Body.Contains(search)));
        }

        var total = await filtered.CountAsync(cancellationToken);

        // The queue reads oldest first (doc 11 P12 step 4); every other view newest first.
        var ordered = criteria.Status == AdminReviewStatusFilter.Pending
            ? filtered.OrderBy(review => review.CreatedAtUtc).ThenBy(review => review.Id)
            : filtered.OrderByDescending(review => review.CreatedAtUtc).ThenByDescending(review => review.Id);

        var rows = await Project(dbContext, ordered.Skip((criteria.Page - 1) * criteria.PageSize).Take(criteria.PageSize))
            .ToListAsync(cancellationToken);

        int CountOf(ReviewStatus status) => counts.FirstOrDefault(count => count.Status == status)?.Count ?? 0;

        return new AdminReviewListPage(
            rows.Select(ToDto).ToList(),
            criteria.Page,
            criteria.PageSize,
            total,
            new ReviewStatusCountsDto(
                CountOf(ReviewStatus.Pending),
                CountOf(ReviewStatus.Approved),
                CountOf(ReviewStatus.Hidden)));
    }

    /// <summary>
    /// The newest <b>Delivered</b> order of this <b>account</b> containing the product, or <see langword="null"/>
    /// (doc 08 D4, doc 11 P12 step 2).
    /// </summary>
    /// <remarks>
    /// <b>By <c>Orders.CustomerUserId</c> and nothing else.</b> A guest order placed with the same email address
    /// does not count until it is linked to the account, and linking requires the address to be confirmed
    /// (doc 08 §3.1 D5, #162's takeover fix) — so somebody who registers a stranger's address cannot borrow that
    /// stranger's purchases for a badge. Only <c>Delivered</c> counts: a Paid, Processing or Shipped order has not
    /// reached the customer (P9 step 9), and a Refunded or Cancelled one is not a purchase they kept.
    /// </remarks>
    internal static Task<int?> QualifyingOrderAsync(
        AppDbContext dbContext,
        int customerUserId,
        int productId,
        CancellationToken cancellationToken) =>
        dbContext.Orders
            .AsNoTracking()
            .Where(order => order.CustomerUserId == customerUserId
                && order.Status == OrderStatus.Delivered
                && dbContext.OrderLines.Any(line => line.OrderId == order.Id && line.ProductId == productId))
            .OrderByDescending(order => order.DeliveredAtUtc)
            .ThenByDescending(order => order.Id)
            .Select(order => (int?)order.Id)
            .FirstOrDefaultAsync(cancellationToken);

    internal static async Task<MyReviewDto?> ReadMineAsync(
        AppDbContext dbContext,
        int customerUserId,
        int productId,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.ProductReviews
            .AsNoTracking()
            .Where(review => review.ProductId == productId && review.CustomerUserId == customerUserId)
            .Select(review => new
            {
                review.Id,
                review.ProductId,
                review.Rating,
                review.Title,
                review.Body,
                review.DisplayName,
                review.IsVerifiedBuyer,
                review.Status,
                review.CreatedAtUtc,
                review.UpdatedAtUtc,
            })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new MyReviewDto(
                row.Id,
                row.ProductId,
                row.Rating,
                row.Title,
                row.Body,
                row.DisplayName,
                row.IsVerifiedBuyer,
                row.Status,
                row.CreatedAtUtc,
                row.UpdatedAtUtc);
    }

    /// <summary>One review for the admin screen, or <see langword="null"/> (also for a removed product's review).</summary>
    internal static async Task<AdminReviewDto?> ReadAdminAsync(
        AppDbContext dbContext,
        int reviewId,
        CancellationToken cancellationToken)
    {
        var row = await Project(
                dbContext,
                dbContext.ProductReviews.AsNoTracking().Where(review => review.Id == reviewId && review.Product != null))
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : ToDto(row);
    }

    private static IQueryable<AdminRow> Project(AppDbContext dbContext, IQueryable<ProductReview> reviews) =>
        reviews.Select(review => new AdminRow
        {
            Id = review.Id,
            ProductId = review.ProductId,
            ProductName = review.Product!.Name,
            ProductCode = review.Product!.ProductCode,
            ProductSlug = review.Product!.Slug,
            Rating = review.Rating,
            Title = review.Title,
            Body = review.Body,
            DisplayName = review.DisplayName,
            IsVerifiedBuyer = review.IsVerifiedBuyer,
            Status = review.Status,
            CreatedAtUtc = review.CreatedAtUtc,
            UpdatedAtUtc = review.UpdatedAtUtc,
            ModeratedAtUtc = review.ModeratedAtUtc,
            ModeratedByName = dbContext.Users
                .Where(user => user.Id == review.ModeratedByUserId)
                .Select(user => user.LastName == null ? user.FirstName : user.FirstName + " " + user.LastName)
                .FirstOrDefault(),
            ModerationNote = review.ModerationNote,
            RowVersion = review.RowVersion,
        });

    private static AdminReviewDto ToDto(AdminRow row) => new(
        row.Id,
        row.ProductId,
        row.ProductName,
        row.ProductCode,
        row.ProductSlug,
        row.Rating,
        row.Title,
        row.Body,
        row.DisplayName,
        row.IsVerifiedBuyer,
        row.Status,
        row.CreatedAtUtc,
        row.UpdatedAtUtc,
        row.ModeratedAtUtc,
        row.ModeratedByName,
        row.ModerationNote,
        row.RowVersion is null ? string.Empty : Convert.ToBase64String(row.RowVersion));

    /// <summary>A nominal type rather than an anonymous one, so the projection can be shared.</summary>
    private sealed class AdminRow
    {
        public int Id { get; init; }

        public int ProductId { get; init; }

        public string ProductName { get; init; } = string.Empty;

        public string ProductCode { get; init; } = string.Empty;

        public string ProductSlug { get; init; } = string.Empty;

        public byte Rating { get; init; }

        public string? Title { get; init; }

        public string? Body { get; init; }

        public string DisplayName { get; init; } = string.Empty;

        public bool IsVerifiedBuyer { get; init; }

        public ReviewStatus Status { get; init; }

        public DateTime CreatedAtUtc { get; init; }

        public DateTime? UpdatedAtUtc { get; init; }

        public DateTime? ModeratedAtUtc { get; init; }

        public string? ModeratedByName { get; init; }

        public string? ModerationNote { get; init; }

        public byte[]? RowVersion { get; init; }
    }
}
