using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.UnitTests.Domain.Catalog;

/// <summary>Doc 08 §3.2, "Card badge (one shown)".</summary>
public sealed class ProductBadgeResolverTests
{
    private static readonly DateTime Now = new(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_admin_badge_wins_over_every_computed_badge()
    {
        var badge = ProductBadgeResolver.Resolve(
            ProductBadge.OnlineExclusive,
            hasDiscount: true,
            StockStatus.LowStock,
            isBestSeller: true,
            newUntilUtc: Now.AddDays(5),
            Now);

        Assert.Equal(ProductBadge.OnlineExclusive, badge);
    }

    [Fact]
    public void Sale_wins_over_low_stock_best_seller_and_new()
    {
        var badge = ProductBadgeResolver.Resolve(
            badgeOverride: null,
            hasDiscount: true,
            StockStatus.LowStock,
            isBestSeller: true,
            newUntilUtc: Now.AddDays(5),
            Now);

        Assert.Equal(ProductBadge.Sale, badge);
    }

    [Fact]
    public void Low_stock_wins_over_best_seller_and_new()
    {
        var badge = ProductBadgeResolver.Resolve(
            badgeOverride: null,
            hasDiscount: false,
            StockStatus.LowStock,
            isBestSeller: true,
            newUntilUtc: Now.AddDays(5),
            Now);

        Assert.Equal(ProductBadge.LowStock, badge);
    }

    [Fact]
    public void Best_seller_wins_over_new()
    {
        var badge = ProductBadgeResolver.Resolve(
            badgeOverride: null,
            hasDiscount: false,
            StockStatus.InStock,
            isBestSeller: true,
            newUntilUtc: Now.AddDays(5),
            Now);

        Assert.Equal(ProductBadge.BestSeller, badge);
    }

    [Fact]
    public void New_is_shown_while_the_window_is_open()
    {
        var badge = ProductBadgeResolver.Resolve(
            badgeOverride: null,
            hasDiscount: false,
            StockStatus.InStock,
            isBestSeller: false,
            newUntilUtc: Now.AddSeconds(1),
            Now);

        Assert.Equal(ProductBadge.New, badge);
    }

    [Fact]
    public void An_expired_new_window_shows_no_badge()
    {
        var badge = ProductBadgeResolver.Resolve(
            badgeOverride: null,
            hasDiscount: false,
            StockStatus.InStock,
            isBestSeller: false,
            newUntilUtc: Now,
            Now);

        Assert.Null(badge);
    }

    [Fact]
    public void An_out_of_stock_product_does_not_get_the_low_stock_badge()
    {
        var badge = ProductBadgeResolver.Resolve(
            badgeOverride: null,
            hasDiscount: false,
            StockStatus.OutOfStock,
            isBestSeller: false,
            newUntilUtc: null,
            Now);

        Assert.Null(badge);
    }
}
