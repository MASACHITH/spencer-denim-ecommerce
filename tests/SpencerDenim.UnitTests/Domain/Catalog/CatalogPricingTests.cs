using SpencerDenim.Domain.Catalog;

namespace SpencerDenim.UnitTests.Domain.Catalog;

/// <summary>Doc 08 §6 steps 1, 2, 3 and 4.</summary>
public sealed class CatalogPricingTests
{
    [Fact]
    public void List_price_is_the_base_price_when_the_variant_has_no_override()
    {
        Assert.Equal(5490m, CatalogPricing.ListPrice(5490m, priceOverride: null));
    }

    [Fact]
    public void List_price_is_the_variant_override_when_there_is_one()
    {
        Assert.Equal(5790m, CatalogPricing.ListPrice(5490m, priceOverride: 5790m));
    }

    [Fact]
    public void Original_price_prefers_the_variant_override_over_the_product_compare_at_price()
    {
        Assert.Equal(7490m, CatalogPricing.OriginalPrice(5490m, productCompareAtPrice: 6990m, variantCompareAtOverride: 7490m));
    }

    [Fact]
    public void Original_price_falls_back_to_the_product_compare_at_price()
    {
        Assert.Equal(6990m, CatalogPricing.OriginalPrice(5490m, productCompareAtPrice: 6990m, variantCompareAtOverride: null));
    }

    [Fact]
    public void Original_price_falls_back_to_the_list_price_when_nothing_is_set()
    {
        Assert.Equal(5490m, CatalogPricing.OriginalPrice(5490m, productCompareAtPrice: null, variantCompareAtOverride: null));
    }

    [Fact]
    public void Original_price_is_never_below_the_list_price()
    {
        // DB17: a stale compare-at price below the list price must not produce a negative discount.
        Assert.Equal(5490m, CatalogPricing.OriginalPrice(5490m, productCompareAtPrice: 4000m, variantCompareAtOverride: null));
    }

    [Fact]
    public void Sale_price_is_the_list_price_when_no_campaign_applies()
    {
        Assert.Equal(5490m, CatalogPricing.SalePrice(5490m, campaignPrice: null));
    }

    [Fact]
    public void Sale_price_ignores_a_campaign_that_is_not_cheaper()
    {
        Assert.Equal(5490m, CatalogPricing.SalePrice(5490m, campaignPrice: 6000m));
    }

    [Fact]
    public void Sale_price_is_floored_at_one_rupee()
    {
        Assert.Equal(CatalogPricing.MinimumSalePrice, CatalogPricing.SalePrice(5490m, campaignPrice: 0m));
    }

    [Theory]
    // Doc 08 §6: floor((original - sale) / original * 100).
    [InlineData(6990, 4392, 37)]
    [InlineData(10990, 8490, 22)]
    [InlineData(100, 50, 50)]
    [InlineData(3, 2, 33)]
    public void Discount_percent_is_floored_to_a_whole_percent(decimal original, decimal sale, decimal expected)
    {
        Assert.Equal(expected, CatalogPricing.DiscountPercent(original, sale));
    }

    [Theory]
    [InlineData(5490, 5490)]
    [InlineData(5490, 6000)]
    [InlineData(0, 0)]
    public void Discount_percent_is_zero_when_there_is_nothing_to_show(decimal original, decimal sale)
    {
        Assert.Equal(0m, CatalogPricing.DiscountPercent(original, sale));
        Assert.False(CatalogPricing.HasDiscount(original, sale));
    }

    [Fact]
    public void Has_discount_is_true_only_when_the_rounded_percent_is_at_least_one()
    {
        // 0.5% floors to 0, so the card shows no discount rather than "-0%".
        Assert.False(CatalogPricing.HasDiscount(1000m, 999m));
        Assert.True(CatalogPricing.HasDiscount(1000m, 989m));
    }

    [Fact]
    public void Worked_example_from_doc_08_section_6_matches()
    {
        // "Relaxed Linen Blend Shirt, Beige / M": original 6,990.00, list 5,490.00, sale 4,392.00, -37%.
        var list = CatalogPricing.ListPrice(5490m, priceOverride: null);
        var original = CatalogPricing.OriginalPrice(list, productCompareAtPrice: 6990m, variantCompareAtOverride: null);
        var sale = CatalogPricing.SalePrice(list, campaignPrice: 4392m);

        Assert.Equal(5490m, list);
        Assert.Equal(6990m, original);
        Assert.Equal(4392m, sale);
        Assert.Equal(37m, CatalogPricing.DiscountPercent(original, sale));
    }
}
