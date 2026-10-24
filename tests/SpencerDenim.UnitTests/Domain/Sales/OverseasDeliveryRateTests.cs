using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Domain.Sales;

/// <summary>
/// The overseas fee rule (owner decision 2026-10-21): the country's override wins, otherwise the region's fee —
/// and only when International delivery, the country and the region are all switched on and a fee exists.
/// Every branch is pinned, because checkout, the bag estimate, the admin screen and the order all read it.
///
/// Express tier tests (owner decision 2026-10-23) are in the nested <see cref="ResolveExpress"/> class.
/// </summary>
public sealed class OverseasDeliveryRateTests
{
    /// <summary>Everything on: a region at Rs. 9,500 over 7–14 days and a country with no overrides.</summary>
    private static OverseasRateInputs Open => new(
        MethodEnabled: true,
        CountryEnabled: true,
        CountryFee: null,
        CountryDaysMin: null,
        CountryDaysMax: null,
        RegionEnabled: true,
        RegionFee: 9_500m,
        RegionDaysMin: 7,
        RegionDaysMax: 14);

    [Fact]
    public void A_country_with_no_override_is_charged_its_regions_fee_and_estimate()
    {
        var quote = OverseasDeliveryRate.Resolve(Open);

        Assert.True(quote.IsAvailable);
        Assert.Equal(OverseasRefusal.None, quote.Refusal);
        Assert.Equal(9_500m, quote.Fee);
        Assert.Equal(7, quote.EstimatedDaysMin);
        Assert.Equal(14, quote.EstimatedDaysMax);
    }

    [Fact]
    public void A_country_fee_override_replaces_the_regions_fee_and_keeps_the_regions_days()
    {
        var quote = OverseasDeliveryRate.Resolve(Open with { CountryFee = 11_250.50m });

        Assert.Equal(11_250.50m, quote.Fee);
        Assert.Equal(7, quote.EstimatedDaysMin);
        Assert.Equal(14, quote.EstimatedDaysMax);
    }

    [Fact]
    public void A_country_days_override_replaces_both_days_and_keeps_the_regions_fee()
    {
        var quote = OverseasDeliveryRate.Resolve(Open with { CountryDaysMin = 3, CountryDaysMax = 5 });

        Assert.Equal(9_500m, quote.Fee);
        Assert.Equal(3, quote.EstimatedDaysMin);
        Assert.Equal(5, quote.EstimatedDaysMax);
    }

    [Theory]
    [InlineData((byte)3, null)]
    [InlineData(null, (byte)5)]
    public void Half_a_days_override_is_never_mixed_with_the_region_the_regions_pair_is_used(byte? min, byte? max)
    {
        var quote = OverseasDeliveryRate.Resolve(Open with { CountryDaysMin = min, CountryDaysMax = max });

        Assert.Equal(7, quote.EstimatedDaysMin);
        Assert.Equal(14, quote.EstimatedDaysMax);
    }

    [Fact]
    public void A_zero_fee_is_a_real_fee_free_delivery_to_that_country()
    {
        var quote = OverseasDeliveryRate.Resolve(Open with { CountryFee = 0m });

        Assert.True(quote.IsAvailable);
        Assert.Equal(0m, quote.Fee);
    }

    [Fact]
    public void A_country_override_prices_a_country_whose_region_is_not_priced()
    {
        var quote = OverseasDeliveryRate.Resolve(Open with { RegionFee = null, CountryFee = 8_000m });

        Assert.True(quote.IsAvailable);
        Assert.Equal(8_000m, quote.Fee);
    }

    [Fact]
    public void No_fee_anywhere_is_not_priced_and_not_offered()
    {
        var quote = OverseasDeliveryRate.Resolve(Open with { RegionFee = null });

        Assert.False(quote.IsAvailable);
        Assert.Equal(OverseasRefusal.NotPriced, quote.Refusal);
        Assert.Equal(0m, quote.Fee);
    }

    [Fact]
    public void A_negative_fee_is_treated_as_not_priced_never_as_a_credit()
    {
        Assert.Equal(OverseasRefusal.NotPriced, OverseasDeliveryRate.Resolve(Open with { CountryFee = -1m }).Refusal);
        Assert.Equal(OverseasRefusal.NotPriced, OverseasDeliveryRate.Resolve(Open with { RegionFee = -0.01m }).Refusal);
    }

    [Fact]
    public void International_delivery_switched_off_refuses_every_country_first()
    {
        var quote = OverseasDeliveryRate.Resolve(Open with
        {
            MethodEnabled = false,
            CountryEnabled = false,
            RegionEnabled = false,
            RegionFee = null,
        });

        Assert.False(quote.IsAvailable);
        Assert.Equal(OverseasRefusal.MethodDisabled, quote.Refusal);
    }

    [Fact]
    public void A_switched_off_country_is_refused_even_with_its_own_fee()
    {
        var quote = OverseasDeliveryRate.Resolve(Open with { CountryEnabled = false, CountryFee = 5_000m });

        Assert.False(quote.IsAvailable);
        Assert.Equal(OverseasRefusal.CountryDisabled, quote.Refusal);
    }

    [Fact]
    public void A_switched_off_region_refuses_its_countries_even_with_their_own_fee()
    {
        var quote = OverseasDeliveryRate.Resolve(Open with { RegionEnabled = false, CountryFee = 5_000m });

        Assert.False(quote.IsAvailable);
        Assert.Equal(OverseasRefusal.RegionDisabled, quote.Refusal);
    }

    [Fact]
    public void A_refusal_carries_no_fee_and_no_days()
    {
        var quote = OverseasDeliveryRate.Resolve(Open with { CountryEnabled = false });

        Assert.Equal(0m, quote.Fee);
        Assert.Equal(0, quote.EstimatedDaysMin);
        Assert.Equal(0, quote.EstimatedDaysMax);
    }

    /// <summary>
    /// <see cref="OverseasDeliveryRate.ResolveExpress"/> (owner decision 2026-10-23): mirrors Standard's
    /// override rules but uses the express columns, and returns <see cref="OverseasRefusal.ExpressNotPriced"/>
    /// rather than <see cref="OverseasRefusal.NotPriced"/> when no express fee is configured.
    /// </summary>
    public sealed class ResolveExpress
    {
        /// <summary>Everything on: region express at Rs. 14,000 over 3–5 days, country no overrides.</summary>
        private static OverseasExpressInputs Open => new(
            MethodEnabled: true,
            CountryEnabled: true,
            CountryExpressFee: null,
            CountryExpressDaysMin: null,
            CountryExpressDaysMax: null,
            RegionEnabled: true,
            RegionExpressFee: 14_000m,
            RegionExpressDaysMin: 3,
            RegionExpressDaysMax: 5);

        [Fact]
        public void Region_express_fee_is_used_when_country_has_no_override()
        {
            var quote = OverseasDeliveryRate.ResolveExpress(Open);

            Assert.True(quote.IsAvailable);
            Assert.Equal(OverseasRefusal.None, quote.Refusal);
            Assert.Equal(14_000m, quote.Fee);
            Assert.Equal(3, quote.EstimatedDaysMin);
            Assert.Equal(5, quote.EstimatedDaysMax);
        }

        [Fact]
        public void Country_express_fee_override_replaces_the_regions_fee_and_keeps_the_regions_days()
        {
            var quote = OverseasDeliveryRate.ResolveExpress(Open with { CountryExpressFee = 18_500m });

            Assert.Equal(18_500m, quote.Fee);
            Assert.Equal(3, quote.EstimatedDaysMin);
            Assert.Equal(5, quote.EstimatedDaysMax);
        }

        [Fact]
        public void Country_express_days_override_replaces_both_days_and_keeps_the_regions_fee()
        {
            var quote = OverseasDeliveryRate.ResolveExpress(Open with { CountryExpressDaysMin = 2, CountryExpressDaysMax = 3 });

            Assert.Equal(14_000m, quote.Fee);
            Assert.Equal(2, quote.EstimatedDaysMin);
            Assert.Equal(3, quote.EstimatedDaysMax);
        }

        [Theory]
        [InlineData((byte)2, null)]
        [InlineData(null, (byte)4)]
        public void Half_an_express_days_override_falls_back_to_the_region_pair(byte? min, byte? max)
        {
            var quote = OverseasDeliveryRate.ResolveExpress(Open with { CountryExpressDaysMin = min, CountryExpressDaysMax = max });

            Assert.Equal(3, quote.EstimatedDaysMin);
            Assert.Equal(5, quote.EstimatedDaysMax);
        }

        [Fact]
        public void No_express_fee_anywhere_returns_ExpressNotPriced_not_NotPriced()
        {
            var quote = OverseasDeliveryRate.ResolveExpress(Open with { RegionExpressFee = null });

            Assert.False(quote.IsAvailable);
            Assert.Equal(OverseasRefusal.ExpressNotPriced, quote.Refusal);
        }

        [Fact]
        public void Country_express_override_prices_a_country_whose_region_has_no_express_fee()
        {
            var quote = OverseasDeliveryRate.ResolveExpress(Open with { RegionExpressFee = null, CountryExpressFee = 12_000m });

            Assert.True(quote.IsAvailable);
            Assert.Equal(12_000m, quote.Fee);
        }

        [Fact]
        public void Method_disabled_is_reported_ahead_of_ExpressNotPriced()
        {
            var quote = OverseasDeliveryRate.ResolveExpress(Open with
            {
                MethodEnabled = false,
                RegionExpressFee = null,
            });

            Assert.False(quote.IsAvailable);
            Assert.Equal(OverseasRefusal.MethodDisabled, quote.Refusal);
        }

        [Fact]
        public void A_switched_off_country_returns_CountryDisabled_for_express_too()
        {
            var quote = OverseasDeliveryRate.ResolveExpress(Open with { CountryEnabled = false });

            Assert.False(quote.IsAvailable);
            Assert.Equal(OverseasRefusal.CountryDisabled, quote.Refusal);
        }

        [Fact]
        public void A_switched_off_region_returns_RegionDisabled_for_express_too()
        {
            var quote = OverseasDeliveryRate.ResolveExpress(Open with { RegionEnabled = false });

            Assert.False(quote.IsAvailable);
            Assert.Equal(OverseasRefusal.RegionDisabled, quote.Refusal);
        }

        [Fact]
        public void A_refusal_carries_no_fee_and_no_days()
        {
            var quote = OverseasDeliveryRate.ResolveExpress(Open with { CountryEnabled = false });

            Assert.Equal(0m, quote.Fee);
            Assert.Equal(0, quote.EstimatedDaysMin);
            Assert.Equal(0, quote.EstimatedDaysMax);
        }
    }
}
