using SpencerDenim.Domain.Inventory;

namespace SpencerDenim.UnitTests.Domain.Inventory;

/// <summary>Doc 08 §3.2 and §7.1 (N11): the read-time stock rules.</summary>
public sealed class StockAvailabilityTests
{
    [Fact]
    public void No_availability_is_out_of_stock()
    {
        Assert.Equal(StockStatus.OutOfStock, StockAvailability.Evaluate(totalAvailable: 0, totalThreshold: 15));
        Assert.False(StockAvailability.IsInStock(0));
    }

    [Fact]
    public void Negative_availability_is_still_out_of_stock()
    {
        // The Available column cannot go negative, but the rule must not invert if it ever did.
        Assert.Equal(StockStatus.OutOfStock, StockAvailability.Evaluate(totalAvailable: -3, totalThreshold: 15));
        Assert.False(StockAvailability.IsInStock(-3));
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(14, 15)]
    [InlineData(15, 15)]
    public void At_or_below_the_summed_threshold_is_low_stock(int available, int threshold)
    {
        Assert.Equal(StockStatus.LowStock, StockAvailability.Evaluate(available, threshold));
        Assert.True(StockAvailability.IsInStock(available));
    }

    [Fact]
    public void Above_the_summed_threshold_is_in_stock()
    {
        Assert.Equal(StockStatus.InStock, StockAvailability.Evaluate(totalAvailable: 16, totalThreshold: 15));
    }

    [Fact]
    public void A_zero_threshold_never_reports_low_stock()
    {
        Assert.Equal(StockStatus.InStock, StockAvailability.Evaluate(totalAvailable: 1, totalThreshold: 0));
    }

    [Fact]
    public void A_variant_threshold_wins_over_the_store_default()
    {
        Assert.Equal(3, StockAvailability.ResolveThreshold(variantThreshold: 3, defaultThreshold: 5));
        Assert.Equal(5, StockAvailability.ResolveThreshold(variantThreshold: null, defaultThreshold: 5));
    }
}
