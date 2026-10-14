using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Marketing;

/// <summary>
/// One live campaign reaching one product, as the query projects it.
/// </summary>
internal readonly record struct CampaignOfferRow(
    int ProductId,
    int CampaignId,
    SaleDiscountType DiscountType,
    decimal DiscountValue);

/// <summary>
/// Doc 08 §6 step 3's "each active campaign targeting the product", read live (doc 08 §7.4: "checkout
/// always prices live").
/// </summary>
/// <remarks>
/// <b>One query for the whole bag, not one per line.</b> A bag of ten lines is one round-trip; the
/// alternative is ten, inside the checkout's repricing read, which is on the revenue path.
/// <para>
/// <b>Three target types, one predicate.</b> A department target matches the product's own
/// <c>DepartmentId</c>; a product target matches its id; a category target matches any category the
/// product is filed under — <i>every</i> link in <c>catalog.ProductCategories</c> and not only the primary
/// one, because doc 08 §3.2 says the primary category is stored there too and "one EXISTS predicate
/// answers every category filter". With <c>IncludeSubcategories</c> the match widens to the subtree via
/// the materialised <c>Categories.Path</c>, whose trailing slash is what stops <c>/1/</c> matching
/// <c>/12/</c>.
/// </para>
/// <para>
/// <b>Soft-deleted campaigns and categories are excluded by the model's global filters</b>
/// (<c>ApplySoftDelete</c>, doc 08 §10.2), not by a predicate here — which is exactly why
/// <c>CatalogModelTests</c> asserts that <c>SaleCampaign</c> carries one. <c>IsActive</c> is written as a
/// <b>constant</b> so that EF renders it as a literal and <c>IX_SaleCampaigns_Window</c>'s filter
/// (<c>WHERE IsActive = 1 AND IsDeleted = 0</c>) is provably implied (DB28); the two date bounds are
/// parameters, because they are a moving instant and are the index's key columns rather than its filter.
/// </para>
/// <para>
/// <b>There is no <c>IgnoreQueryFilters</c> here, and that is load-bearing.</b> <c>CartReader</c> turns
/// the filters off so a withdrawn product still appears in the bag as unavailable rather than vanishing;
/// this query must not, because the one filter it depends on for correctness is the campaign's own. EF
/// applies <c>IgnoreQueryFilters()</c> to the whole query tree, so turning it off for the product would
/// also turn it off for <c>SaleCampaigns</c> in the subquery and a soft-deleted campaign would go on
/// pricing the shop. The cost is that a soft-deleted product gets no offers — which is harmless, because
/// such a line is marked <c>Unavailable</c>, carries no current price and is never priced into a total.
/// </para>
/// </remarks>
internal sealed class CampaignOfferReader(AppDbContext dbContext, IClock clock) : ICampaignOfferReader
{
    public async Task<CampaignOfferSet> ReadAsync(
        IReadOnlyCollection<int> productIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        if (productIds.Count == 0)
        {
            return CampaignOfferSet.None;
        }

        // One instant for the whole read: doc 08 §6 prices a bag as one thing, and two lines evaluated
        // against two clock readings could straddle a campaign's end and produce a subtotal that is the
        // sum of two different catalogues.
        var utcNow = clock.UtcNow.UtcDateTime;

        var rows = await Query(dbContext, productIds, utcNow).ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return CampaignOfferSet.None;
        }

        var byProductId = rows
            .GroupBy(row => row.ProductId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<CampaignOffer>)group
                    .Select(row => new CampaignOffer(row.CampaignId, row.DiscountType, row.DiscountValue))
                    .ToList());

        return new CampaignOfferSet(byProductId);
    }

    /// <summary>
    /// The query itself, separated from the <c>await</c> so that a plan test can take
    /// <see cref="EntityFrameworkQueryableExtensions.ToQueryString"/> of <b>this</b> query rather than of a
    /// copy written in the test file.
    /// </summary>
    /// <remarks>
    /// <b>That distinction is the whole reason this method exists.</b> This project has a plan test that
    /// ran a query string written in the test file and stayed green with its index dropped — 20,101
    /// logical reads against 18 — and the promo code's own plan test records in its remarks that it could
    /// not observe its reader because the reader was an <c>async</c> method whose <c>IQueryable</c> was
    /// unreachable from outside. <c>IX_SaleCampaigns_Window</c> is filtered, so the same trap is open
    /// here; splitting the query out closes it, and <c>SaleCampaignPlanTests</c> calls this method.
    /// </remarks>
    internal static IQueryable<CampaignOfferRow> Query(
        AppDbContext dbContext,
        IReadOnlyCollection<int> productIds,
        DateTime utcNow) =>
        dbContext.Products
            .AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .SelectMany(product => dbContext.SaleCampaigns
                // `true` as a constant, not a captured variable: DB28 needs the predicate to imply
                // `IX_SaleCampaigns_Window`'s filter at compile time, and EF renders a captured bool as a
                // parameter. The soft-delete half of that filter arrives from the global query filter,
                // which `ApplySoftDelete` also writes as a constant.
                .Where(campaign =>
                    campaign.IsActive
                    && campaign.StartsAtUtc <= utcNow
                    && campaign.EndsAtUtc > utcNow)
                .Where(campaign => campaign.Targets.Any(target =>
                    (target.TargetType == SaleCampaignTargetType.Department
                        && target.DepartmentId == product.DepartmentId)
                    || (target.TargetType == SaleCampaignTargetType.Product
                        && target.ProductId == product.Id)
                    || (target.TargetType == SaleCampaignTargetType.Category

                        // Through the DbSet rather than `product.CategoryLinks`, so the predicate is an
                        // `Expression<Func<>>` all the way down. The navigation's `Any` would be an
                        // `Enumerable.Any` over an `ICollection`, i.e. a delegate lambda nested in an
                        // expression tree — which translates identically but makes CA1310 fire on the
                        // `StartsWith` that every other subtree predicate in this codebase uses.
                        && dbContext.ProductCategories.Any(link =>
                            link.ProductId == product.Id
                            && (link.CategoryId == target.CategoryId
                                || (target.IncludeSubcategories
                                    && link.Category!.Path.StartsWith(target.Category!.Path)))))))
                .Select(campaign => new CampaignOfferRow(
                    product.Id,
                    campaign.Id,
                    campaign.DiscountType,
                    campaign.DiscountValue)));
}
