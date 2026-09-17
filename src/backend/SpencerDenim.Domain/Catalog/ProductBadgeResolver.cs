using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.Domain.Catalog;

/// <summary>
/// Doc 08 §3.2, "Card badge (one shown)": the admin's <c>BadgeOverride</c> wins; otherwise the first
/// computed badge in the order Sale → Low Stock → Best Seller → New.
/// </summary>
public static class ProductBadgeResolver
{
    /// <param name="badgeOverride">The admin-assigned badge, if any.</param>
    /// <param name="hasDiscount"><c>ProductStats.HasDiscount</c>.</param>
    /// <param name="stockStatus">Read-time stock status of the product's active variants.</param>
    /// <param name="isBestSeller">
    /// True when the product is in the department's top <c>Catalog.BestSellerTopN</c> by
    /// <c>SoldQty30d</c>. Never stored.
    /// </param>
    /// <param name="newUntilUtc"><c>Products.NewUntilUtc</c>.</param>
    /// <param name="utcNow">Current UTC time.</param>
    /// <returns>The one badge to show, or <c>null</c> when the card shows none.</returns>
    public static ProductBadge? Resolve(
        ProductBadge? badgeOverride,
        bool hasDiscount,
        StockStatus stockStatus,
        bool isBestSeller,
        DateTime? newUntilUtc,
        DateTime utcNow)
    {
        if (badgeOverride is not null)
        {
            return badgeOverride;
        }

        if (hasDiscount)
        {
            return ProductBadge.Sale;
        }

        if (stockStatus == StockStatus.LowStock)
        {
            return ProductBadge.LowStock;
        }

        if (isBestSeller)
        {
            return ProductBadge.BestSeller;
        }

        if (newUntilUtc is not null && newUntilUtc > utcNow)
        {
            return ProductBadge.New;
        }

        return null;
    }
}
