using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Marketing;

/// <summary>
/// Doc 08 §7.4's recompute of <c>catalog.ProductStats</c>' pricing columns, as one
/// <c>UPDATE … FROM</c> per batch of ≤ 1,000 products, computing straight from the source tables.
/// </summary>
/// <remarks>
/// <b>Why one statement and not a loop in C#.</b> Doc 08 §7.4 specifies it: "Each batch is one
/// <c>UPDATE … FROM</c> statement that computes straight from the source tables, with
/// <c>WITH (READCOMMITTEDLOCK)</c> on them. A concurrent product save therefore either commits first
/// (and is read) or runs after and writes its own correct values (DB20, N11)." Reading a thousand
/// products into memory, pricing them and writing them back would open a window between the read and the
/// write in which a product save could be overwritten with a figure computed before it.
/// <para>
/// <b>The lock hint is the part that is easy to drop and hard to notice.</b> Without it, a database with
/// <c>READ_COMMITTED_SNAPSHOT ON</c> would serve the computing reads from the version store, so the
/// statement could price from a snapshot taken before a product save that commits during it — and then
/// write that stale answer over the save's. <c>READCOMMITTEDLOCK</c> forces the shared locks that make
/// the two serialise. <b>Every</b> source table carries it, the fallback's <c>NOT EXISTS</c> included:
/// that one was unhinted until issue #163, which would let it judge "has an active variant" from the
/// statement's snapshot while the hinted branch beside it waits for and reads a save that deactivates the
/// last one — neither branch then emits a row and the summary is not written. That outcome was not
/// reproduced (a test that held the deactivation open still saw the summary written, perhaps because the
/// plan runs the hinted branch first); the hint is there so the answer does not depend on plan order.
/// <c>CampaignPricingRefreshLockTests</c> shows the hint makes the statement wait; every database runs with
/// <c>READ_COMMITTED_SNAPSHOT ON</c> since #163, so it is no longer a no-op.
/// </para>
/// <para>
/// <b>It is a second implementation of <see cref="CampaignPricing"/>, in T-SQL, and that is a real
/// risk.</b> It cannot be avoided — §7.4 requires the arithmetic to happen inside one statement — so it
/// is managed instead: <c>CampaignPricingRefreshTests</c> drives a matrix of campaigns and products
/// through <b>both</b> paths and asserts the stored summary equals what the live
/// <see cref="ICampaignOfferReader"/> plus <see cref="CampaignPricing.Best"/> produce, line for line. A
/// divergence in rounding, in the floor, in the tie-break or in the targeting is a failure there rather
/// than a wrong badge on a card.
/// </para>
/// <para>
/// <b>What happens when it fails halfway, overlaps itself, or races an edit.</b>
/// <list type="bullet">
/// <item><description>
/// <b>Halfway:</b> each batch is its own statement and its own implicit transaction. A failure on the
/// fourth batch leaves the first three correct and the rest stale — never half-written, because a single
/// <c>UPDATE</c> is atomic. Nothing needs repairing: every statement recomputes the whole answer, so the
/// next pass or the next save converges. The job deliberately does not advance its cursor on a failure,
/// so the same window is retried.
/// </description></item>
/// <item><description>
/// <b>Overlapping:</b> two instances, or the job and a save, computing the same product write the
/// <i>same</i> values, because both compute from the same source rows. Overlap costs wasted work and can
/// never produce a wrong figure, which is why there is no lease here — the same argument
/// <c>ExpireUnpaidOrdersService</c> and <c>AutoDeliverShippedService</c> make, and
/// <c>ops.JobLeases</c> still does not exist.
/// </description></item>
/// <item><description>
/// <b>Racing an edit:</b> the statement reads the campaign rows inside itself, so it sees the campaign
/// either before or after the edit, never halfway. If it reads the old state, the edit's own save path
/// recomputes the same products immediately afterwards and wins. If it reads the new state, the save's
/// recompute writes the same values again. The order does not matter because neither is an increment.
/// The worst outcome is a listing that lags one interval, which §7.4 already permits — and the money path
/// never reads these columns at all.
/// </description></item>
/// </list>
/// </para>
/// </remarks>
internal sealed class CampaignPricingRefresher(AppDbContext dbContext, IClock clock) : ICampaignPricingRefresher
{
    /// <summary>Doc 08 §7.4: "recompute affected products in batches of ≤ 1,000".</summary>
    internal const int BatchSize = 1_000;

    public async Task<CampaignPricingRefreshPass> RefreshProductsAsync(
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        // Distinct and ordered: the caller passes a union of two target sets, so duplicates are ordinary,
        // and a stable order makes the batch boundaries reproducible in a failure report.
        var ids = productIds.Distinct().Order().ToList();

        if (ids.Count == 0)
        {
            return new CampaignPricingRefreshPass(0, 0, 0);
        }

        var utcNow = clock.UtcNow.UtcDateTime;
        var batches = 0;
        var rows = 0;

        for (var offset = 0; offset < ids.Count; offset += BatchSize)
        {
            var batch = ids.GetRange(offset, Math.Min(BatchSize, ids.Count - offset));

            // A comma-joined list in **one parameter**, split by `STRING_SPLIT` inside the statement.
            // The alternative — a generated `IN (@p0, @p1, …)` — is a different query plan for every
            // batch size and a thousand-parameter cache entry, and building it by concatenation is what
            // EF1003 exists to stop.
            var csv = string.Join(',', batch.Select(id => id.ToString(CultureInfo.InvariantCulture)));

            rows += await dbContext.Database.ExecuteSqlAsync(
                $"""
                WITH ids AS (
                    SELECT CAST(value AS int) AS ProductId FROM STRING_SPLIT({csv}, ',')
                ),
                lines AS (
                    -- One row per active variant: doc 08 §6 steps 1 and 2 for that variant.
                    SELECT p.Id AS ProductId,
                           p.DepartmentId,
                           ISNULL(v.PriceOverride, p.BasePrice) AS ListPrice,
                           CASE WHEN COALESCE(v.CompareAtPriceOverride, p.CompareAtPrice, ISNULL(v.PriceOverride, p.BasePrice))
                                     > ISNULL(v.PriceOverride, p.BasePrice)
                                THEN COALESCE(v.CompareAtPriceOverride, p.CompareAtPrice, ISNULL(v.PriceOverride, p.BasePrice))
                                ELSE ISNULL(v.PriceOverride, p.BasePrice)
                           END AS OriginalPrice
                      FROM catalog.Products p WITH (READCOMMITTEDLOCK)
                      JOIN ids ON ids.ProductId = p.Id
                      JOIN catalog.ProductVariants v WITH (READCOMMITTEDLOCK)
                        ON v.ProductId = p.Id AND v.IsActive = 1
                     WHERE p.IsDeleted = 0

                    UNION ALL

                    -- A product with no active variant falls back to its own base price, which is what
                    -- `ProductDerivedData.RefreshStats` does and why: an empty aggregate has no min and
                    -- `CK_ProductStats_PriceRange` would refuse a row of zeroes beside a positive
                    -- MaxOriginalPrice. Such a product is also one G-01 will not let anybody publish.
                    SELECT p.Id,
                           p.DepartmentId,
                           p.BasePrice,
                           CASE WHEN ISNULL(p.CompareAtPrice, p.BasePrice) > p.BasePrice
                                THEN p.CompareAtPrice ELSE p.BasePrice END
                      FROM catalog.Products p WITH (READCOMMITTEDLOCK)
                      JOIN ids ON ids.ProductId = p.Id
                     WHERE p.IsDeleted = 0
                       AND NOT EXISTS (
                           SELECT 1 FROM catalog.ProductVariants v WITH (READCOMMITTEDLOCK)
                            WHERE v.ProductId = p.Id AND v.IsActive = 1)
                ),
                priced AS (
                    -- Doc 08 §6 step 3, per variant: the lowest resulting price among the live campaigns
                    -- that target the product, floored at Rs. 1.00, and only when it beats the list
                    -- price. `MIN` over a CASE that yields NULL for a non-winner is how "no campaign
                    -- wins" comes back as NULL and falls through to the list price.
                    SELECT l.ProductId,
                           l.OriginalPrice,
                           ISNULL((
                               -- The "only a campaign that beats the list price wins" test is in this
                               -- subquery's WHERE and not inside the MIN, because SQL Server refuses an
                               -- aggregate whose expression mixes an outer reference with an inner
                               -- column ("Multiple columns are specified in an aggregated expression
                               -- containing an outer reference"). The two are the same rule either way:
                               -- MIN over the candidates that beat the list price, and NULL when none do.
                               SELECT MIN(candidate.Price)
                                 FROM (
                                     SELECT CASE WHEN c.DiscountType = 1
                                                 THEN CASE WHEN ROUND(l.ListPrice * (1 - c.DiscountValue / 100.0), 2) < 1.00
                                                           THEN 1.00
                                                           ELSE ROUND(l.ListPrice * (1 - c.DiscountValue / 100.0), 2) END
                                                 ELSE CASE WHEN l.ListPrice - c.DiscountValue < 1.00
                                                           THEN 1.00
                                                           ELSE l.ListPrice - c.DiscountValue END
                                            END AS Price
                                       FROM marketing.SaleCampaigns c WITH (READCOMMITTEDLOCK)
                                      WHERE c.IsActive = 1
                                        AND c.IsDeleted = 0
                                        AND c.StartsAtUtc <= {utcNow}
                                        AND c.EndsAtUtc > {utcNow}
                                        AND EXISTS (
                                            SELECT 1
                                              FROM marketing.SaleCampaignTargets t WITH (READCOMMITTEDLOCK)
                                              LEFT JOIN catalog.Categories tc WITH (READCOMMITTEDLOCK)
                                                ON tc.Id = t.CategoryId
                                             WHERE t.SaleCampaignId = c.Id
                                               AND (
                                                   (t.TargetType = 1 AND t.DepartmentId = l.DepartmentId)
                                                   OR (t.TargetType = 3 AND t.ProductId = l.ProductId)
                                                   OR (t.TargetType = 2 AND EXISTS (
                                                       SELECT 1
                                                         FROM catalog.ProductCategories pc WITH (READCOMMITTEDLOCK)
                                                         JOIN catalog.Categories cat WITH (READCOMMITTEDLOCK)
                                                           ON cat.Id = pc.CategoryId AND cat.IsDeleted = 0
                                                        WHERE pc.ProductId = l.ProductId
                                                          AND (pc.CategoryId = t.CategoryId
                                                               OR (t.IncludeSubcategories = 1
                                                                   AND tc.IsDeleted = 0
                                                                   AND cat.[Path] LIKE tc.[Path] + '%'))))
                                               )
                                        )
                                 ) AS candidate
                                WHERE candidate.Price < l.ListPrice
                           ), l.ListPrice) AS SalePrice
                      FROM lines l
                )
                UPDATE s
                   SET s.MinSalePrice = agg.MinSale,
                       s.MaxSalePrice = agg.MaxSale,
                       s.MaxDiscountPercent = agg.MaxDiscount,
                       s.HasDiscount = CASE WHEN agg.MaxDiscount > 0 THEN 1 ELSE 0 END,
                       s.PricingRefreshedAtUtc = {utcNow}
                  FROM catalog.ProductStats s
                  JOIN (
                      SELECT ProductId,
                             MIN(SalePrice) AS MinSale,
                             MAX(SalePrice) AS MaxSale,

                             -- Doc 08 §6 step 4, per variant and then the largest: a product's badge is
                             -- the best discount any of its variants carries.
                             MAX(CASE WHEN OriginalPrice > 0 AND SalePrice < OriginalPrice
                                      THEN FLOOR((OriginalPrice - SalePrice) / OriginalPrice * 100)
                                      ELSE 0 END) AS MaxDiscount
                        FROM priced
                       GROUP BY ProductId
                  ) agg ON agg.ProductId = s.ProductId;
                """,
                cancellationToken);

            batches++;
        }

        return new CampaignPricingRefreshPass(ids.Count, batches, rows);
    }

    public async Task<IReadOnlyList<int>> ProductsTargetedByAsync(
        IReadOnlyCollection<int> campaignIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaignIds);

        if (campaignIds.Count == 0)
        {
            return [];
        }

        // `IgnoreQueryFilters` here, and only here: the affected set of a campaign that has just been
        // **soft-deleted** is still the products it targeted, and they are the products whose summaries
        // have to go back up. The pricing statement above is the one that must never bypass the filter,
        // and it does not.
        return await dbContext.SaleCampaignTargets
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(target => campaignIds.Contains(target.SaleCampaignId))
            .SelectMany(target => dbContext.Products
                .IgnoreQueryFilters()
                .Where(product =>
                    (target.TargetType == SaleCampaignTargetType.Department
                        && target.DepartmentId == product.DepartmentId)
                    || (target.TargetType == SaleCampaignTargetType.Product
                        && target.ProductId == product.Id)
                    || (target.TargetType == SaleCampaignTargetType.Category
                        && dbContext.ProductCategories.Any(link =>
                            link.ProductId == product.Id
                            && (link.CategoryId == target.CategoryId
                                || (target.IncludeSubcategories
                                    && link.Category!.Path.StartsWith(target.Category!.Path))))))
                .Select(product => product.Id))
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>No <c>IgnoreQueryFilters</c>, and that is measured rather than assumed.</b>
    /// <c>ProductCategory</c> carries no soft-delete filter of its own — <c>ProductCategoryConfiguration</c>
    /// does not call <c>ApplySoftDelete</c> — and this query reads only <c>CategoryId</c> and
    /// <c>ProductId</c>, so it touches no filtered navigation. A soft-deleted product's link is therefore
    /// included, which is what is wanted: recomputing its summary is correct and costs one row.
    /// </remarks>
    public async Task<IReadOnlyList<int>> ProductsLinkedToCategoriesAsync(
        IReadOnlyCollection<int> categoryIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(categoryIds);

        if (categoryIds.Count == 0)
        {
            return [];
        }

        return await dbContext.ProductCategories
            .AsNoTracking()
            .Where(link => categoryIds.Contains(link.CategoryId))
            .Select(link => link.ProductId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<CampaignPricingRefreshPass> RefreshBoundariesAsync(
        DateTime sinceUtc,
        DateTime untilUtc,
        CancellationToken cancellationToken)
    {
        // Doc 08 §7.4: "for campaigns that started or ended since the last run". Half-open on the left so
        // two consecutive passes neither repeat nor skip a boundary, which is the same convention
        // `CampaignWindow` uses for the window itself.
        var campaignIds = await dbContext.SaleCampaigns
            .AsNoTracking()
            .Where(campaign =>
                (campaign.StartsAtUtc > sinceUtc && campaign.StartsAtUtc <= untilUtc)
                || (campaign.EndsAtUtc > sinceUtc && campaign.EndsAtUtc <= untilUtc))
            .Select(campaign => campaign.Id)
            .ToListAsync(cancellationToken);

        if (campaignIds.Count == 0)
        {
            return new CampaignPricingRefreshPass(0, 0, 0);
        }

        var productIds = await ProductsTargetedByAsync(campaignIds, cancellationToken);

        return await RefreshProductsAsync(productIds, cancellationToken);
    }
}
