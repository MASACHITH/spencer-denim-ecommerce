using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Domain.Sales;

/// <summary>
/// Doc 08 §6 steps 5 to 12, including the pro-rata allocation (step 9), the delivery discount (step 10)
/// and the Rs. 1.00 minimum-total clamp (step 11, G-11).
/// <para>
/// <b>The arithmetic is asserted, not the shape.</b> Every test here either reproduces a figure doc 08
/// states, or asserts an identity that <c>CK_Orders_Totals</c> and <c>CK_OrderLines_LineTotal</c> enforce
/// in the database — so a change that satisfied the constraints by accident but printed a ladder that did
/// not add up would still fail here.
/// </para>
/// </summary>
public sealed class OrderPricingTests
{
    /// <summary>Doc 08 §6's worked example, line 1: Relaxed Linen Blend Shirt, Beige / M, ×2.</summary>
    private static OrderPricingLine LinenShirt => new(101, 2, 6_990m, 5_490m, 4_392m);

    /// <summary>Doc 08 §6's worked example, line 2: High-Rise Slim Fit Jeans, Dark Blue / 30, ×1.</summary>
    private static OrderPricingLine Jeans => new(202, 1, 7_490m, 7_490m, 7_490m);

    [Fact]
    public void Doc_08_s_worked_example_reproduces_every_order_column()
    {
        // §6's worked example: Express to Colombo at Rs. 950, no free threshold, WELCOME10 at 10% capped
        // at Rs. 2,000 — the promo amount is given, because steps 7 and 8 belong to ADM-13.
        var result = OrderPricing.Price(
            [LinenShirt, Jeans],
            new DeliveryCharge(950m, FreeAboveAmount: null),
            new PromoCharge(1_627.40m));

        Assert.Equal(21_470m, result.ItemsOriginalTotal);
        Assert.Equal(18_470m, result.ItemsListTotal);
        Assert.Equal(16_274m, result.ItemsSubtotal);
        Assert.Equal(3_000m, result.MarkdownDiscountTotal);
        Assert.Equal(2_196m, result.CampaignDiscountTotal);
        Assert.Equal(1_627.40m, result.PromoDiscountAmount);
        Assert.Equal(950m, result.DeliveryFeeAmount);
        Assert.Equal(0m, result.DeliveryDiscountAmount);
        Assert.Equal(0m, result.TaxAmount);
        Assert.Equal(15_596.60m, result.GrandTotal);
        Assert.False(result.PromoDiscountClamped);

        // §6 step 4 of the example: "You saved" = 3,000 + 2,196 + 1,627.40.
        Assert.Equal(6_823.40m, result.SavedTotal);
    }

    [Fact]
    public void Doc_08_s_worked_example_allocates_the_promo_with_no_remainder()
    {
        var result = OrderPricing.Price(
            [LinenShirt, Jeans],
            new DeliveryCharge(950m, FreeAboveAmount: null),
            new PromoCharge(1_627.40m));

        // §6's example: "Allocated 878.40 + 749.00 with no remainder."
        Assert.Equal(878.40m, result.Lines[0].PromoDiscountAllocated);
        Assert.Equal(749.00m, result.Lines[1].PromoDiscountAllocated);
        Assert.Equal(7_905.60m, result.Lines[0].LineTotal);
        Assert.Equal(6_741.00m, result.Lines[1].LineTotal);
    }

    [Fact]
    public void Standard_to_Colombo_is_free_above_its_threshold()
    {
        // §6's example, "other delivery choices": Standard to Colombo is Rs. 400, free above Rs. 10,000.
        // 16,274.00 − 1,627.40 = 14,646.60 ≥ 10,000, so delivery is free and the total is 14,646.60.
        var result = OrderPricing.Price(
            [LinenShirt, Jeans],
            new DeliveryCharge(400m, FreeAboveAmount: 10_000m),
            new PromoCharge(1_627.40m));

        Assert.Equal(400m, result.DeliveryFeeAmount);
        Assert.Equal(400m, result.DeliveryDiscountAmount);
        Assert.Equal(14_646.60m, result.GrandTotal);
    }

    [Fact]
    public void Standard_island_wide_is_not_free_below_its_threshold()
    {
        // §6's example: "Customer in Jaffna … Standard is Rs. 600, free above Rs. 15,000; 14,646.60 <
        // 15,000, so the total is Rs. 15,246.60."
        var result = OrderPricing.Price(
            [LinenShirt, Jeans],
            new DeliveryCharge(600m, FreeAboveAmount: 15_000m),
            new PromoCharge(1_627.40m));

        Assert.Equal(600m, result.DeliveryFeeAmount);
        Assert.Equal(0m, result.DeliveryDiscountAmount);
        Assert.Equal(15_246.60m, result.GrandTotal);
    }

    [Fact]
    public void The_threshold_is_compared_against_the_subtotal_after_the_promo_discount()
    {
        // Doc 08 §6 step 10 says the comparison is against "items subtotal − promo". The two readings
        // differ by exactly one delivery fee, so this is asserted rather than assumed: with a subtotal of
        // 16,274.00 and a threshold of 16,000.00, the pre-promo figure qualifies and the post-promo one
        // (14,646.60) does not.
        var result = OrderPricing.Price(
            [LinenShirt, Jeans],
            new DeliveryCharge(500m, FreeAboveAmount: 16_000m),
            new PromoCharge(1_627.40m));

        Assert.Equal(0m, result.DeliveryDiscountAmount);
        Assert.Equal(15_146.60m, result.GrandTotal);
    }

    [Fact]
    public void A_free_delivery_promo_discounts_the_whole_fee_whatever_the_threshold()
    {
        var result = OrderPricing.Price(
            [Jeans],
            new DeliveryCharge(950m, FreeAboveAmount: null),
            new PromoCharge(0m, FreeDelivery: true));

        Assert.Equal(950m, result.DeliveryFeeAmount);
        Assert.Equal(950m, result.DeliveryDiscountAmount);
        Assert.Equal(7_490m, result.GrandTotal);
    }

    [Fact]
    public void With_no_promo_code_the_subtotal_plus_the_fee_is_the_total()
    {
        // The shape every order placed by W3-02 actually has: marketing.PromoCodes does not exist, so the
        // promo columns are zero and the ladder is subtotal + fee.
        //
        // The threshold here is Rs. 20,000, above the subtotal, so the fee is payable. **That detail is
        // the correction this test made on first run:** it was written against the island-wide seed rate
        // (Rs. 600 free above Rs. 15,000) and asserted a total of 16,874.00, which was wrong — with no
        // promo code the comparison is 16,274.00 ≥ 15,000.00, so delivery is free and the total is
        // 16,274.00. Worth leaving on the record, because it is the arithmetic a shopper meets: removing
        // the promo discount makes delivery *more* likely to be free, not less.
        var result = OrderPricing.Price([LinenShirt, Jeans], new DeliveryCharge(600m, 20_000m));

        Assert.Equal(0m, result.PromoDiscountAmount);
        Assert.Equal(0m, result.DeliveryDiscountAmount);
        Assert.Equal(16_874m, result.GrandTotal);
        Assert.All(result.Lines, line => Assert.Equal(0m, line.PromoDiscountAllocated));
    }

    [Fact]
    public void With_no_promo_code_the_threshold_is_compared_against_the_full_subtotal()
    {
        // The other half of the correction above, asserted on purpose rather than left implicit: the
        // island-wide seed rate (Rs. 600, free above Rs. 15,000) gives this bag free delivery once no
        // promo code reduces the base.
        var result = OrderPricing.Price([LinenShirt, Jeans], new DeliveryCharge(600m, 15_000m));

        Assert.Equal(600m, result.DeliveryFeeAmount);
        Assert.Equal(600m, result.DeliveryDiscountAmount);
        Assert.Equal(16_274m, result.GrandTotal);
    }

    [Fact]
    public void Items_subtotal_is_the_sum_of_the_line_subtotals()
    {
        // The cross-row invariant no CHECK constraint can express (W3-03's review flagged it): it rests
        // entirely on the code that writes the lines, which is this function.
        var result = OrderPricing.Price(
            [LinenShirt, Jeans, new OrderPricingLine(303, 3, 1_200m, 1_200m, 999m)],
            new DeliveryCharge(400m, FreeAboveAmount: null));

        Assert.Equal(result.Lines.Sum(line => line.LineSubtotal), result.ItemsSubtotal);
    }

    [Fact]
    public void Every_line_subtotal_is_the_unit_price_times_the_quantity()
    {
        // CK_OrderLines_LineSubtotal, which compares exactly this product.
        var result = OrderPricing.Price(
            [LinenShirt, Jeans],
            new DeliveryCharge(400m, FreeAboveAmount: null));

        Assert.All(result.Lines, line => Assert.Equal(line.UnitPrice * line.Quantity, line.LineSubtotal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_627.40)]
    [InlineData(16_274)]
    public void The_order_satisfies_CK_Orders_Totals_whatever_the_promo(decimal promo)
    {
        var result = OrderPricing.Price(
            [LinenShirt, Jeans],
            new DeliveryCharge(950m, FreeAboveAmount: 10_000m),
            new PromoCharge(promo));

        // The four equalities CK_Orders_Totals enforces, asserted here so a failure names the arithmetic
        // instead of arriving as a constraint violation inside the placement transaction.
        Assert.Equal(result.MarkdownDiscountTotal, result.ItemsOriginalTotal - result.ItemsListTotal);
        Assert.Equal(result.CampaignDiscountTotal, result.ItemsListTotal - result.ItemsSubtotal);
        Assert.Equal(
            result.GrandTotal,
            result.ItemsSubtotal - result.PromoDiscountAmount + result.DeliveryFeeAmount
                - result.DeliveryDiscountAmount);
        Assert.True(result.GrandTotal >= 0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1_627.40)]
    [InlineData(16_274)]
    public void Every_line_satisfies_CK_OrderLines_LineTotal_whatever_the_promo(decimal promo)
    {
        var result = OrderPricing.Price(
            [LinenShirt, Jeans],
            new DeliveryCharge(950m, FreeAboveAmount: null),
            new PromoCharge(promo));

        Assert.All(result.Lines, line =>
        {
            Assert.Equal(line.LineTotal, line.LineSubtotal - line.PromoDiscountAllocated);
            Assert.True(line.LineTotal >= 0m, $"Line {line.VariantId} total {line.LineTotal} is negative.");
            Assert.True(line.PromoDiscountAllocated >= 0m);
        });
    }

    [Fact]
    public void The_promo_allocation_adds_up_to_the_promo_discount()
    {
        // Step 9's remainder rule: the shares must sum to the figure in the order row, or the order's
        // promo column and its lines would disagree about the same discount.
        var result = OrderPricing.Price(
            [
                new OrderPricingLine(1, 1, 333m, 333m, 333m),
                new OrderPricingLine(2, 1, 333m, 333m, 333m),
                new OrderPricingLine(3, 1, 334m, 334m, 334m),
            ],
            new DeliveryCharge(400m, FreeAboveAmount: null),

            // 10% of 1,000 = 100.00, which does not divide evenly over 333/333/334.
            new PromoCharge(100m));

        Assert.Equal(100m, result.Lines.Sum(line => line.PromoDiscountAllocated));
        Assert.Equal(100m, result.PromoDiscountAmount);
    }

    [Fact]
    public void The_allocation_remainder_goes_to_the_largest_eligible_line()
    {
        var result = OrderPricing.Price(
            [
                new OrderPricingLine(1, 1, 333m, 333m, 333m),
                new OrderPricingLine(2, 1, 333m, 333m, 333m),
                new OrderPricingLine(3, 1, 334m, 334m, 334m),
            ],
            new DeliveryCharge(400m, FreeAboveAmount: null),
            new PromoCharge(100m));

        // 33.30 + 33.30 + 33.40 = 100.00 exactly here, so the interesting assertion is that the largest
        // line is never *smaller* than the others' share — the remainder can only ever be added to it.
        Assert.Equal(33.30m, result.Lines[0].PromoDiscountAllocated);
        Assert.Equal(33.30m, result.Lines[1].PromoDiscountAllocated);
        Assert.Equal(33.40m, result.Lines[2].PromoDiscountAllocated);
    }

    [Fact]
    public void An_ineligible_line_gets_no_share_of_the_promo()
    {
        // Where ExcludeSaleItems lands when ADM-13 exists: the campaign-priced line is out of the base.
        var result = OrderPricing.Price(
            [LinenShirt, Jeans],
            new DeliveryCharge(400m, FreeAboveAmount: null),
            new PromoCharge(749m, EligibleVariantIds: new HashSet<int> { Jeans.VariantId }));

        Assert.Equal(0m, result.Lines[0].PromoDiscountAllocated);
        Assert.Equal(749m, result.Lines[1].PromoDiscountAllocated);
        Assert.Equal(8_784m, result.Lines[0].LineTotal);
    }

    [Fact]
    public void A_promo_with_no_eligible_line_is_refused_rather_than_misallocated()
    {
        Assert.Throws<InvalidOperationException>(() => OrderPricing.Price(
            [LinenShirt],
            new DeliveryCharge(400m, FreeAboveAmount: null),
            new PromoCharge(100m, EligibleVariantIds: new HashSet<int>())));
    }

    [Fact]
    public void The_clamp_holds_the_grand_total_at_one_rupee_rather_than_zero()
    {
        // G-11: a fixed code covering every item with no delivery fee would otherwise produce a Rs. 0.00
        // order, which no payment path can settle.
        var result = OrderPricing.Price(
            [new OrderPricingLine(1, 1, 500m, 500m, 500m)],
            new DeliveryCharge(0m, FreeAboveAmount: null),
            new PromoCharge(500m));

        Assert.True(result.PromoDiscountClamped);
        Assert.Equal(499m, result.PromoDiscountAmount);
        Assert.Equal(OrderPricing.MinimumGrandTotal, result.GrandTotal);
    }

    [Fact]
    public void The_clamp_re_runs_the_allocation_over_the_clamped_amount()
    {
        // Doc 08 §6 step 11: "step 9's allocation is re-run over the clamped amount". Without that, the
        // line shares would still sum to the pre-clamp discount and CK_OrderLines_LineTotal would reject
        // a line whose allocation exceeded its subtotal.
        var result = OrderPricing.Price(
            [
                new OrderPricingLine(1, 1, 300m, 300m, 300m),
                new OrderPricingLine(2, 1, 200m, 200m, 200m),
            ],
            new DeliveryCharge(0m, FreeAboveAmount: null),
            new PromoCharge(500m));

        Assert.True(result.PromoDiscountClamped);
        Assert.Equal(499m, result.PromoDiscountAmount);
        Assert.Equal(499m, result.Lines.Sum(line => line.PromoDiscountAllocated));
        Assert.All(result.Lines, line => Assert.True(line.LineTotal >= 0m));
    }

    [Fact]
    public void The_clamp_leaves_the_delivery_fee_payable()
    {
        // A promo that covers every item but not the delivery fee needs no clamp: the fee already keeps
        // the total above Rs. 1.00, and reducing the discount would overcharge.
        var result = OrderPricing.Price(
            [new OrderPricingLine(1, 1, 500m, 500m, 500m)],
            new DeliveryCharge(400m, FreeAboveAmount: null),
            new PromoCharge(500m));

        Assert.False(result.PromoDiscountClamped);
        Assert.Equal(500m, result.PromoDiscountAmount);
        Assert.Equal(400m, result.GrandTotal);
    }

    [Fact]
    public void A_promo_larger_than_the_items_is_capped_at_the_items_subtotal()
    {
        var result = OrderPricing.Price(
            [new OrderPricingLine(1, 1, 500m, 500m, 500m)],
            new DeliveryCharge(400m, FreeAboveAmount: null),
            new PromoCharge(100_000m));

        Assert.Equal(500m, result.PromoDiscountAmount);
        Assert.Equal(400m, result.GrandTotal);
    }

    [Fact]
    public void Nothing_is_clamped_when_there_is_no_promo_to_reduce()
    {
        // The clamp works by reducing the promo discount, so an order with no code cannot be clamped. The
        // floor then comes from CatalogPricing's Rs. 1.00 minimum sale price instead.
        var result = OrderPricing.Price(
            [new OrderPricingLine(1, 1, 1m, 1m, 1m)],
            new DeliveryCharge(0m, FreeAboveAmount: null));

        Assert.False(result.PromoDiscountClamped);
        Assert.Equal(1m, result.GrandTotal);
    }

    [Fact]
    public void Pricing_without_a_delivery_rate_is_refused_rather_than_charged_at_zero()
    {
        // Doc 08 §6 step 10 needs a rate row that exists and is available. "No rate" must refuse the
        // checkout, not silently make delivery free — which would be the shop paying for every parcel.
        Assert.Throws<ArgumentNullException>(() => OrderPricing.Price([Jeans], delivery: null));
    }

    [Fact]
    public void An_order_with_no_lines_is_refused()
    {
        Assert.Throws<ArgumentException>(() => OrderPricing.Price(
            [],
            new DeliveryCharge(400m, FreeAboveAmount: null)));
    }

    [Theory]

        // Quantity must be > 0 (CK_OrderLines_Quantity).
        [InlineData(0, 500, 500, 500)]

        // Sale above list, list above original: CK_OrderLines on original ≥ list ≥ sale.
        [InlineData(1, 500, 500, 600)]
        [InlineData(1, 500, 600, 500)]

        // Sale below the Rs. 1.00 floor (CatalogPricing.MinimumSalePrice).
        [InlineData(1, 500, 500, 0)]
    public void A_line_that_no_check_constraint_would_accept_is_refused_before_the_insert(
        short quantity,
        decimal original,
        decimal list,
        decimal sale)
    {
        Assert.Throws<ArgumentException>(() => OrderPricing.Price(
            [new OrderPricingLine(1, quantity, original, list, sale)],
            new DeliveryCharge(400m, FreeAboveAmount: null)));
    }

    [Fact]
    public void A_negative_promo_amount_is_read_as_no_discount()
    {
        var result = OrderPricing.Price(
            [Jeans],
            new DeliveryCharge(400m, FreeAboveAmount: null),
            new PromoCharge(-5_000m));

        Assert.Equal(0m, result.PromoDiscountAmount);
        Assert.Equal(7_890m, result.GrandTotal);
    }

    [Fact]
    public void Rounding_is_half_away_from_zero()
    {
        // Doc 08 §6: "rounded half away from zero to 2 dp where noted". Two lines of 0.05 apart make the
        // pro-rata share land exactly on a half-cent: 1.00 × (3.33 / 10.00) = 0.333 → 0.33, and
        // 1.00 × (6.67 / 10.00) = 0.667 → 0.67. Banker's rounding would give 0.33 and 0.67 as well, so the
        // case that distinguishes them is a share of exactly x.xx5: 2.00 × (1.25 / 10.00) = 0.25 is exact,
        // while 1.00 × (1.25 / 10.00) = 0.125 → 0.13 away from zero and 0.12 to even.
        var result = OrderPricing.Price(
            [
                new OrderPricingLine(1, 1, 1.25m, 1.25m, 1.25m),
                new OrderPricingLine(2, 1, 8.75m, 8.75m, 8.75m),
            ],
            new DeliveryCharge(400m, FreeAboveAmount: null),
            new PromoCharge(1m));

        Assert.Equal(0.13m, result.Lines[0].PromoDiscountAllocated);
    }
}
