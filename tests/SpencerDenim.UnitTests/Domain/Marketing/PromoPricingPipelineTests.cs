using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Domain.Marketing;

/// <summary>
/// <see cref="PromoEvaluator"/> into <see cref="OrderPricing"/>: doc 08 §6 steps 7 and 8 feeding steps 9,
/// 10 and 11 (ADM-13).
/// <para>
/// <b>Why a file of its own.</b> <c>PromoEvaluatorTests</c> asserts what a code is worth and
/// <c>OrderPricingTests</c> asserts what the calculator does with a given amount; neither can say that the
/// composition is right, and the composition is where W3-05 could break something already built. These
/// tests give the calculator the evaluator's real output and assert the identity the bag and checkout both
/// print — <c>Subtotal − Savings − Promo + Delivery − Free delivery = Total</c> — plus the two steps the
/// brief singles out: the Rs. 1.00 clamp and the free-delivery discount landing on a different line.
/// </para>
/// </summary>
public sealed class PromoPricingPipelineTests
{
    private static DateTime Now => new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Doc 08 §6's worked example, line 1.</summary>
    private static OrderPricingLine LinenShirt => new(101, 2, 6_990m, 5_490m, 4_392m);

    /// <summary>Doc 08 §6's worked example, line 2.</summary>
    private static OrderPricingLine Jeans => new(202, 1, 7_490m, 7_490m, 7_490m);

    /// <summary>
    /// Doc 08 §6's worked example end to end, with the promo amount computed rather than given: the
    /// grand total is Rs. 15,596.60 and "You saved" is Rs. 6,823.40.
    /// </summary>
    /// <remarks>
    /// <b><c>OrderPricingTests</c> asserts the same figures with the discount handed to it as a
    /// constant.</b> This asserts that <see cref="PromoEvaluator"/> produces that constant from the
    /// columns doc 08 §3.6 gives <c>WELCOME10</c> — which is the join the document describes and no test
    /// could make before the table existed.
    /// </remarks>
    [Fact]
    public void Doc_08_s_worked_example_holds_with_the_discount_computed_from_the_code()
    {
        var lines = new[] { LinenShirt, Jeans };

        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.Percent, 10m, maxDiscountAmount: 2_000m, minSpendAmount: 5_000m),
            lines,
            Now);

        var result = OrderPricing.Price(
            lines,
            new DeliveryCharge(950m, FreeAboveAmount: null),
            evaluation.Charge);

        Assert.Equal(1_627.40m, result.PromoDiscountAmount);
        Assert.Equal(15_596.60m, result.GrandTotal);
        Assert.Equal(6_823.40m, result.SavedTotal);
        Assert.False(result.PromoDiscountClamped);

        // Step 9's allocation, from the document's own table: 878.40 and 749.00, with no remainder.
        Assert.Equal(878.40m, result.Lines[0].PromoDiscountAllocated);
        Assert.Equal(749m, result.Lines[1].PromoDiscountAllocated);

        AssertLadderAddsUp(result);
    }

    /// <summary>
    /// <b>Doc 08 §6 step 11's minimum-total clamp (G-11).</b> A fixed-amount code worth more than the
    /// order reduces so the grand total is exactly Rs. 1.00, and <c>PromoDiscountClamped</c> says so.
    /// </summary>
    /// <remarks>
    /// <b>Delivery is free, because the clamp can only bite when the fee is not holding the total up</b> —
    /// a store-pickup rate, which is the only seeded rate with a zero fee. It is a unit test rather than
    /// an integration one for exactly that reason: <c>PICKUP</c> ships disabled (doc 08 §9 seeds no pickup
    /// location) and enabling it would mutate reference data the whole integration suite reads.
    /// </remarks>
    [Fact]
    public void A_fixed_code_larger_than_the_order_is_clamped_to_leave_one_rupee()
    {
        var lines = new[] { Jeans };
        const decimal subtotal = 7_490m;

        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.FixedAmount, 50_000m),
            lines,
            Now);

        // Step 8 has already capped it at the eligible base, so the clamp is reached by the base itself
        // exceeding the Rs. 1.00 floor rather than by an absurd figure reaching the calculator.
        Assert.Equal(subtotal, evaluation.Charge!.Amount);

        var result = OrderPricing.Price(lines, new DeliveryCharge(0m, FreeAboveAmount: null), evaluation.Charge);

        Assert.Equal(OrderPricing.MinimumGrandTotal, result.GrandTotal);
        Assert.Equal(subtotal - 1.00m, result.PromoDiscountAmount);
        Assert.True(result.PromoDiscountClamped);

        // And the allocation was re-run over the clamped amount, which is what step 11 requires: the line
        // keeps Rs. 1.00 rather than being reduced to nothing.
        Assert.Equal(subtotal - 1.00m, result.Lines[0].PromoDiscountAllocated);
        Assert.Equal(1.00m, result.Lines[0].LineTotal);

        AssertLadderAddsUp(result);
    }

    /// <summary>
    /// A percentage of 100 — the most <c>CK_PromoCodes_DiscountValue</c> allows — is clamped the same
    /// way, so the clamp does not depend on the discount type.
    /// </summary>
    [Fact]
    public void A_hundred_percent_code_is_clamped_too()
    {
        var lines = new[] { Jeans };

        var evaluation = PromoEvaluator.Evaluate(Terms(PromoDiscountType.Percent, 100m), lines, Now);

        Assert.Equal(7_490m, evaluation.Charge!.Amount);

        var result = OrderPricing.Price(lines, new DeliveryCharge(0m, null), evaluation.Charge);

        Assert.Equal(OrderPricing.MinimumGrandTotal, result.GrandTotal);
        Assert.True(result.PromoDiscountClamped);
        AssertLadderAddsUp(result);
    }

    /// <summary>
    /// <b>Free delivery is a discount on a different line.</b> The items discount stays zero, the whole
    /// fee becomes the delivery discount, and the grand total is the items subtotal.
    /// </summary>
    [Fact]
    public void A_free_delivery_code_discounts_the_fee_and_leaves_the_items_alone()
    {
        var lines = new[] { LinenShirt, Jeans };

        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.FreeDelivery, discountValue: null),
            lines,
            Now);

        var result = OrderPricing.Price(
            lines,
            new DeliveryCharge(950m, FreeAboveAmount: null),
            evaluation.Charge);

        Assert.Equal(0m, result.PromoDiscountAmount);
        Assert.Equal(950m, result.DeliveryFeeAmount);
        Assert.Equal(950m, result.DeliveryDiscountAmount);
        Assert.Equal(16_274m, result.GrandTotal);

        // No line carries a share, because there is nothing to allocate — and `LineTotal` is therefore
        // the line subtotal, which is what #129's "a line's refundable amount is its LineTotal" needs to
        // be true for a free-delivery order.
        Assert.All(result.Lines, line => Assert.Equal(0m, line.PromoDiscountAllocated));
        Assert.All(result.Lines, line => Assert.Equal(line.LineSubtotal, line.LineTotal));

        AssertLadderAddsUp(result);
    }

    /// <summary>
    /// A free-delivery code on an order that already qualified for free delivery is not free delivery
    /// twice: the discount is the fee, once.
    /// </summary>
    /// <remarks>
    /// <b>The one way step 10 could double-count.</b> `CK_Orders_Totals` would catch a delivery discount
    /// above the fee, but it would catch it inside the placement transaction as a failed checkout with no
    /// explanation — so the arithmetic is asserted here instead.
    /// </remarks>
    [Fact]
    public void A_free_delivery_code_on_an_order_that_was_already_free_discounts_the_fee_once()
    {
        var lines = new[] { LinenShirt, Jeans };

        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.FreeDelivery, discountValue: null),
            lines,
            Now);

        // A threshold of Rs. 10,000 against a subtotal of Rs. 16,274: already free.
        var result = OrderPricing.Price(
            lines,
            new DeliveryCharge(400m, FreeAboveAmount: 10_000m),
            evaluation.Charge);

        Assert.Equal(400m, result.DeliveryDiscountAmount);
        Assert.Equal(16_274m, result.GrandTotal);
        AssertLadderAddsUp(result);
    }

    /// <summary>
    /// <b>Doc 08 §6 step 10 compares the threshold with the items subtotal <i>after</i> the promo</b>, so
    /// a percentage code can take an order below a free-delivery threshold it had cleared — and the
    /// shopper is charged for delivery.
    /// </summary>
    /// <remarks>
    /// <b>This is a real shopper-facing consequence and it is asserted rather than discovered.</b> A code
    /// that saves Rs. 1,627.40 on an order of Rs. 16,274.00 leaves Rs. 14,646.60, which still clears
    /// Rs. 10,000 — so the figures here use a threshold of Rs. 15,000 to put the order on the other side
    /// of it. The alternative reading, comparing before the promo, would promise free delivery and then
    /// charge for it; doc 08 §6 step 10 says "items subtotal − promo ≥ FreeAboveAmount" and this is that
    /// sentence as a test.
    /// </remarks>
    [Fact]
    public void A_promo_can_take_an_order_below_a_free_delivery_threshold()
    {
        var lines = new[] { LinenShirt, Jeans };

        var withoutCode = OrderPricing.Price(lines, new DeliveryCharge(600m, FreeAboveAmount: 15_000m));

        // Rs. 16,274.00 clears Rs. 15,000.00, so delivery is free with no code.
        Assert.Equal(600m, withoutCode.DeliveryDiscountAmount);
        Assert.Equal(16_274m, withoutCode.GrandTotal);

        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.Percent, 10m, maxDiscountAmount: 2_000m),
            lines,
            Now);

        var withCode = OrderPricing.Price(
            lines,
            new DeliveryCharge(600m, FreeAboveAmount: 15_000m),
            evaluation.Charge);

        // Rs. 14,646.60 does not, so the fee is charged — and the order is still cheaper overall.
        Assert.Equal(0m, withCode.DeliveryDiscountAmount);
        Assert.Equal(15_246.60m, withCode.GrandTotal);
        Assert.True(withCode.GrandTotal < withoutCode.GrandTotal);

        AssertLadderAddsUp(withCode);
    }

    /// <summary>
    /// With <c>ExcludeSaleItems</c>, step 9 puts the whole discount on the eligible line and nothing on
    /// the excluded one — so <c>LineTotal = LineSubtotal</c> holds for the campaign-priced line.
    /// </summary>
    [Fact]
    public void An_excluded_line_carries_no_share_of_the_discount()
    {
        var lines = new[] { LinenShirt, Jeans };

        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.Percent, 10m, excludeSaleItems: true),
            lines,
            Now);

        var result = OrderPricing.Price(lines, new DeliveryCharge(0m, null), evaluation.Charge);

        Assert.Equal(749m, result.PromoDiscountAmount);
        Assert.Equal(0m, result.Lines[0].PromoDiscountAllocated);
        Assert.Equal(749m, result.Lines[1].PromoDiscountAllocated);

        AssertLadderAddsUp(result);
    }

    /// <summary>
    /// The identity the bag and checkout both print, and the per-line one <c>CK_OrderLines_LineTotal</c>
    /// enforces.
    /// </summary>
    /// <remarks>
    /// Asserted on every pipeline test rather than in one of its own, because the thing this project has
    /// shipped is a ladder that did not add up — and it added up in the test that was about the ladder.
    /// </remarks>
    private static void AssertLadderAddsUp(OrderPricingResult result)
    {
        var savings = result.MarkdownDiscountTotal + result.CampaignDiscountTotal;

        Assert.Equal(
            result.ItemsOriginalTotal
            - savings
            - result.PromoDiscountAmount
            + result.DeliveryFeeAmount
            - result.DeliveryDiscountAmount,
            result.GrandTotal);

        Assert.Equal(result.ItemsSubtotal, result.Lines.Sum(line => line.LineSubtotal));

        Assert.All(
            result.Lines,
            line =>
            {
                Assert.Equal(line.LineSubtotal - line.PromoDiscountAllocated, line.LineTotal);
                Assert.True(line.LineTotal >= 0m);
            });
    }

    private static PromoCodeTerms Terms(
        PromoDiscountType discountType,
        decimal? discountValue,
        decimal? maxDiscountAmount = null,
        decimal? minSpendAmount = null,
        bool excludeSaleItems = false) =>
        new(
            Id: 1,
            Code: "TESTCODE",
            discountType,
            discountValue,
            maxDiscountAmount,
            minSpendAmount,
            excludeSaleItems,
            StartsAtUtc: Now.AddDays(-1),
            EndsAtUtc: Now.AddDays(1),
            TotalUsageLimit: null,
            PerCustomerLimit: null,
            UsageCount: 0,
            IsActive: true);
}
