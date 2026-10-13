using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.UnitTests.Domain.Marketing;

/// <summary>
/// Doc 08 §6 steps 7 and 8 (ADM-13): is this code usable on this bag, and what is it worth.
/// <para>
/// <b>The figures are doc 08 §6's own worked example wherever it has one</b>, so a test here either
/// reproduces a number the document states or asserts a rule it states in words. Steps 9, 10 and 11 are
/// <c>OrderPricingTests</c>' and are not re-asserted; what <see cref="PromoPricingPipelineTests"/> adds is
/// the two together.
/// </para>
/// </summary>
public sealed class PromoEvaluatorTests
{
    /// <summary>Doc 08 §6's worked example, line 1: Relaxed Linen Blend Shirt, ×2 at Rs. 4,392.00.</summary>
    private static OrderPricingLine LinenShirt => new(101, 2, 6_990m, 5_490m, 4_392m);

    /// <summary>Doc 08 §6's worked example, line 2: High-Rise Slim Fit Jeans, ×1 at Rs. 7,490.00.</summary>
    private static OrderPricingLine Jeans => new(202, 1, 7_490m, 7_490m, 7_490m);

    /// <summary>The instant every test judges a window against, so none of them reads a real clock.</summary>
    private static DateTime Now => new(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Doc 08 §6's <c>WELCOME10</c>: 10%, capped at Rs. 2,000.00, minimum spend Rs. 5,000.00 — and the
    /// discount the document computes for it, Rs. 1,627.40 on a subtotal of Rs. 16,274.00.
    /// </summary>
    [Fact]
    public void Doc_08_s_WELCOME10_is_worth_what_the_document_says()
    {
        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.Percent, 10m, maxDiscountAmount: 2_000m, minSpendAmount: 5_000m),
            [LinenShirt, Jeans],
            Now);

        Assert.True(evaluation.IsUsable);
        Assert.Equal(1_627.40m, evaluation.Charge!.Amount);
        Assert.False(evaluation.Charge.FreeDelivery);

        // Null and not a set of both variant ids: every line is eligible, and `PromoCharge` documents
        // null as "all of them".
        Assert.Null(evaluation.Charge.EligibleVariantIds);
    }

    /// <summary>
    /// The cap binds, and it binds <b>after</b> the rounding: 20% of Rs. 16,274.00 is Rs. 3,254.80, and a
    /// cap of Rs. 2,000.00 makes the answer exactly Rs. 2,000.00.
    /// </summary>
    [Fact]
    public void A_percentage_above_its_cap_is_the_cap_exactly()
    {
        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.Percent, 20m, maxDiscountAmount: 2_000m),
            [LinenShirt, Jeans],
            Now);

        Assert.Equal(2_000m, evaluation.Charge!.Amount);
    }

    /// <summary>
    /// Doc 08 §6 step 8 rounds "half away from zero to 2 dp", which is the one rounding mode that turns
    /// 1.005 into 1.01 rather than into 1.00.
    /// </summary>
    /// <remarks>
    /// The figure is chosen so banker's rounding would give a different answer: 7% of Rs. 143.50 is
    /// Rs. 10.045, which rounds away from zero to Rs. 10.05 and to even to Rs. 10.04. A test using a
    /// figure both modes agree on would pass over <c>MidpointRounding.ToEven</c>, which is
    /// <c>Math.Round</c>'s default and therefore the mistake this guards.
    /// </remarks>
    [Fact]
    public void A_percentage_rounds_half_away_from_zero()
    {
        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.Percent, 7m),
            [new OrderPricingLine(301, 1, 143.50m, 143.50m, 143.50m)],
            Now);

        Assert.Equal(10.05m, evaluation.Charge!.Amount);
    }

    /// <summary>
    /// Doc 11 P3's own edge case: "Fixed code larger than eligible items (Rs. 1,000 code on Rs. 800 of
    /// eligible items) → discount Rs. 800.00."
    /// </summary>
    [Fact]
    public void A_fixed_code_never_exceeds_the_eligible_items()
    {
        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.FixedAmount, 1_000m),
            [new OrderPricingLine(301, 1, 800m, 800m, 800m)],
            Now);

        Assert.Equal(800m, evaluation.Charge!.Amount);
    }

    /// <summary>
    /// Doc 08 §6 step 8: "FreeDelivery → 0 here." The whole effect is step 10's, so the items discount is
    /// zero and the flag is what travels.
    /// </summary>
    [Fact]
    public void A_free_delivery_code_discounts_no_items()
    {
        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.FreeDelivery, discountValue: null),
            [LinenShirt, Jeans],
            Now);

        Assert.Equal(0m, evaluation.Charge!.Amount);
        Assert.True(evaluation.Charge.FreeDelivery);
    }

    /// <summary>
    /// Doc 08 §6 step 7 and doc 11 P3: the minimum spend is compared with the <b>items subtotal after
    /// sale prices</b>, not with the eligible base — so a bag that clears the threshold only because of
    /// its <i>excluded</i> lines still clears it.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that tells the two bases apart</b>, and they are only distinguishable when
    /// <c>ExcludeSaleItems</c> is on. The jeans (Rs. 7,490.00, no campaign) are eligible and the linen
    /// shirt (Rs. 8,784.00, campaign-priced) is not, so the eligible base is Rs. 7,490.00 while the items
    /// subtotal is Rs. 16,274.00. A threshold of Rs. 10,000.00 is met by the second and not by the first:
    /// comparing against the eligible base would refuse this code, and the document says it applies.
    /// </remarks>
    [Fact]
    public void A_minimum_spend_is_measured_against_the_items_subtotal_and_not_the_eligible_base()
    {
        var evaluation = PromoEvaluator.Evaluate(
            Terms(
                PromoDiscountType.Percent,
                10m,
                minSpendAmount: 10_000m,
                excludeSaleItems: true),
            [LinenShirt, Jeans],
            Now);

        Assert.True(evaluation.IsUsable);

        // 10% of the eligible base alone, which is the jeans.
        Assert.Equal(749m, evaluation.Charge!.Amount);
    }

    /// <summary>
    /// Doc 08 §6 step 7's <c>ExcludeSaleItems</c>: a line a campaign priced is out of the eligible base,
    /// and a line that only carries a markdown stays in (doc 11 §7 G-07).
    /// </summary>
    /// <remarks>
    /// <b>The markdown-only line is the half that matters.</b> The jeans here are marked down —
    /// original Rs. 9,990.00 against a list and sale price of Rs. 7,490.00 — and are still eligible,
    /// because step 2's markdown moves <c>UnitOriginalPrice</c> and only step 3's campaign moves
    /// <c>UnitPrice</c> below <c>UnitListPrice</c>. An implementation that excluded anything discounted
    /// would pass the test above and fail this one.
    /// </remarks>
    [Fact]
    public void Exclude_sale_items_drops_a_campaign_priced_line_and_keeps_a_marked_down_one()
    {
        var markedDown = new OrderPricingLine(202, 1, 9_990m, 7_490m, 7_490m);

        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.Percent, 10m, excludeSaleItems: true),
            [LinenShirt, markedDown],
            Now);

        Assert.True(evaluation.IsUsable);
        Assert.Equal(749m, evaluation.Charge!.Amount);

        // And the eligible set says which line it may be spread over, so step 9 cannot put a share on the
        // excluded one.
        Assert.Equal([202], evaluation.Charge.EligibleVariantIds!);
    }

    /// <summary>
    /// With nothing excluded, the eligible set is <see langword="null"/> rather than a set of every
    /// variant — so step 9's eligibility test is a null check and not a hash lookup.
    /// </summary>
    [Fact]
    public void Exclude_sale_items_with_nothing_to_exclude_leaves_every_line_eligible()
    {
        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.Percent, 10m, excludeSaleItems: true),
            [Jeans],
            Now);

        Assert.True(evaluation.IsUsable);
        Assert.Null(evaluation.Charge!.EligibleVariantIds);
    }

    /// <summary>
    /// Every line excluded is <see cref="PromoRefusal.NoEligibleItems"/> and not a discount of zero,
    /// because a row reading "− Rs. 0.00" tells a shopper their code worked when it did nothing.
    /// </summary>
    [Fact]
    public void A_bag_of_only_campaign_priced_lines_has_no_eligible_items()
    {
        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.Percent, 10m, excludeSaleItems: true),
            [LinenShirt],
            Now);

        Assert.Equal(PromoRefusal.NoEligibleItems, evaluation.Refusal);
        Assert.Null(evaluation.Charge);
    }

    /// <summary>
    /// <b>The same under <c>FreeDelivery</c>, which is the claim W3-05c's "no validation rule is needed"
    /// decision rests on.</b>
    /// </summary>
    /// <remarks>
    /// W3-05c put "exclude sale items" on the promo-code form and deliberately added no rule refusing it
    /// for a discount type, on the grounds that it is meaningful under <i>all three</i> — including
    /// <c>FreeDelivery</c>, where it cannot change the delivery discount but can still empty the eligible
    /// base and refuse the code outright. That is true because <see cref="PromoEvaluator"/> tests
    /// <c>eligibleBase &lt;= 0m</c> <b>before</b> it switches on the discount type, and <b>nothing pinned
    /// it</b>: the only <c>NoEligibleItems</c> test used <c>Percent</c>. A refactor that moved the check
    /// inside the percent and fixed arms would make a free-delivery code with the box ticked apply
    /// through a site-wide sale — the opposite of what the staff member asked for — and every test would
    /// still pass.
    /// <para>
    /// Added in the W3-05c tester pass, because the flag only became settable in W3-05c and this edge is
    /// therefore reachable in the shop for the first time.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_free_delivery_code_that_excludes_sale_items_is_refused_when_every_line_is_on_campaign()
    {
        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.FreeDelivery, discountValue: null, excludeSaleItems: true),
            [LinenShirt],
            Now);

        Assert.Equal(PromoRefusal.NoEligibleItems, evaluation.Refusal);
        Assert.Null(evaluation.Charge);
        Assert.False(evaluation.IsUsable);
    }

    /// <summary>
    /// And it still gives free delivery when a line survives the exclusion, so the test above is a
    /// refusal and not the flag quietly disabling the type.
    /// </summary>
    /// <remarks>
    /// The jeans are at list price with no campaign on them, so <c>IsEligible</c>'s
    /// <c>UnitPrice &gt;= UnitListPrice</c> keeps them in. The pair is the point: the flag refuses a bag
    /// it has emptied and changes nothing about a bag it has not.
    /// </remarks>
    [Fact]
    public void A_free_delivery_code_that_excludes_sale_items_still_applies_when_a_line_survives()
    {
        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.FreeDelivery, discountValue: null, excludeSaleItems: true),
            [Jeans],
            Now);

        Assert.True(evaluation.IsUsable);
        Assert.True(evaluation.Charge!.FreeDelivery);
        Assert.Equal(0m, evaluation.Charge.Amount);
    }

    /// <summary>
    /// The minimum spend refusal carries the threshold, because it is the one refusal a shopper can act
    /// on.
    /// </summary>
    [Fact]
    public void A_bag_under_the_minimum_spend_is_refused_with_the_threshold()
    {
        var evaluation = PromoEvaluator.Evaluate(
            Terms(PromoDiscountType.Percent, 10m, minSpendAmount: 20_000m),
            [LinenShirt, Jeans],
            Now);

        Assert.Equal(PromoRefusal.MinimumSpend, evaluation.Refusal);
        Assert.Equal(20_000m, evaluation.MinimumSpend);
    }

    /// <summary>
    /// Doc 08 §3.6's window: <b>the start is inclusive</b>, the same rule
    /// <c>ContentSchedule.IsLive</c> applies to every other scheduled row in this database.
    /// </summary>
    [Fact]
    public void A_code_starts_working_at_its_start_instant_exactly()
    {
        var terms = Terms(PromoDiscountType.FixedAmount, 100m) with { StartsAtUtc = Now };

        Assert.True(PromoEvaluator.Evaluate(terms, [Jeans], Now).IsUsable);

        Assert.Equal(
            PromoRefusal.NotRecognised,
            PromoEvaluator.Evaluate(terms, [Jeans], Now.AddMilliseconds(-1)).Refusal);
    }

    /// <summary>
    /// And <b>the end is exclusive</b>: a code that ends at 09:00 is over at 09:00. Two consecutive
    /// windows therefore join with no second of overlap.
    /// </summary>
    [Fact]
    public void A_code_stops_working_at_its_end_instant_exactly()
    {
        var terms = Terms(PromoDiscountType.FixedAmount, 100m) with { EndsAtUtc = Now };

        Assert.True(PromoEvaluator.Evaluate(terms, [Jeans], Now.AddMilliseconds(-1)).IsUsable);
        Assert.Equal(PromoRefusal.NotRecognised, PromoEvaluator.Evaluate(terms, [Jeans], Now).Refusal);
    }

    /// <summary>
    /// A code with no end has no end. Null is "no bound on that side" and not "ends now".
    /// </summary>
    [Fact]
    public void A_code_with_no_end_keeps_working()
    {
        var terms = Terms(PromoDiscountType.FixedAmount, 100m) with { EndsAtUtc = null };

        Assert.True(PromoEvaluator.Evaluate(terms, [Jeans], Now.AddYears(5)).IsUsable);
    }

    /// <summary>
    /// A code staff have switched off is <see cref="PromoRefusal.NotRecognised"/> — the same sentence as
    /// a code that never existed, so the box cannot be used to learn which strings the shop has issued.
    /// </summary>
    [Fact]
    public void An_inactive_code_is_not_recognised()
    {
        var terms = Terms(PromoDiscountType.FixedAmount, 100m) with { IsActive = false };

        Assert.Equal(PromoRefusal.NotRecognised, PromoEvaluator.Evaluate(terms, [Jeans], Now).Refusal);
    }

    /// <summary>
    /// Doc 08 §3.6's total limit, as the quote reads it: at the limit the code is
    /// <see cref="PromoRefusal.FullyUsed"/>, which doc 11 P3 asks to be a sentence of its own.
    /// </summary>
    /// <remarks>
    /// <b>This is the <i>cheap</i> check and not the guarantee.</b> The count it reads can be stale by the
    /// time the order is written, so <c>OrderPromoUse.TakeAsync</c> re-applies the limit in a conditional
    /// UPDATE inside the transaction; <c>CheckoutPromoRaceTests</c> is what proves that half.
    /// </remarks>
    [Theory]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, true)]
    [InlineData(2, 3, true)]
    public void A_code_at_its_total_limit_is_fully_used(int totalLimit, int usageCount, bool refused)
    {
        var terms = Terms(PromoDiscountType.FixedAmount, 100m) with
        {
            TotalUsageLimit = totalLimit,
            UsageCount = usageCount,
        };

        var evaluation = PromoEvaluator.Evaluate(terms, [Jeans], Now);

        Assert.Equal(refused ? PromoRefusal.FullyUsed : PromoRefusal.None, evaluation.Refusal);
    }

    /// <summary>
    /// A code with no total limit is never fully used, however many times it has been used.
    /// </summary>
    [Fact]
    public void A_code_with_no_total_limit_is_never_fully_used()
    {
        var terms = Terms(PromoDiscountType.FixedAmount, 100m) with
        {
            TotalUsageLimit = null,
            UsageCount = 10_000,
        };

        Assert.True(PromoEvaluator.Evaluate(terms, [Jeans], Now).IsUsable);
    }

    /// <summary>
    /// Doc 11 P3 step 10's order of checks, which is what decides the sentence a shopper sees when two
    /// things are wrong at once: the window first, then the total limit, then the minimum spend.
    /// </summary>
    /// <remarks>
    /// <b>Asserted because the order is a decision and not an accident.</b> A code that is over, full and
    /// below its minimum spend is "not recognised" — the least informative of the three, deliberately,
    /// because the other two would confirm that the string is a code the shop issued.
    /// </remarks>
    [Fact]
    public void An_ended_code_that_is_also_full_says_it_is_not_recognised()
    {
        var terms = Terms(PromoDiscountType.FixedAmount, 100m, minSpendAmount: 1_000_000m) with
        {
            EndsAtUtc = Now.AddDays(-1),
            TotalUsageLimit = 1,
            UsageCount = 1,
        };

        Assert.Equal(PromoRefusal.NotRecognised, PromoEvaluator.Evaluate(terms, [Jeans], Now).Refusal);
    }

    /// <summary>
    /// A full code that is also below its minimum spend says it is fully used, because the total limit is
    /// checked before the spend (doc 11 P3 step 10) and "fully used" is the fact that will not change if
    /// the shopper adds to their bag.
    /// </summary>
    [Fact]
    public void A_full_code_that_is_also_under_its_minimum_spend_says_it_is_fully_used()
    {
        var terms = Terms(PromoDiscountType.FixedAmount, 100m, minSpendAmount: 1_000_000m) with
        {
            TotalUsageLimit = 1,
            UsageCount = 1,
        };

        Assert.Equal(PromoRefusal.FullyUsed, PromoEvaluator.Evaluate(terms, [Jeans], Now).Refusal);
    }

    /// <summary>
    /// A discount type outside <c>CK_PromoCodes_DiscountType</c> throws rather than discounting nothing.
    /// </summary>
    /// <remarks>
    /// Unreachable through the schema, which is why it is an exception and not a comment claiming so: a
    /// code that discounted nothing while the screen said it applied is the defect, not the exception.
    /// </remarks>
    [Fact]
    public void A_discount_type_the_constraint_forbids_throws()
    {
        var terms = Terms(PromoDiscountType.FixedAmount, 100m) with
        {
            DiscountType = (PromoDiscountType)99,
        };

        Assert.Throws<InvalidOperationException>(() => PromoEvaluator.Evaluate(terms, [Jeans], Now));
    }

    /// <summary>
    /// A live, unlimited code of the given type and value. Every test overrides exactly what it is about,
    /// so a figure that matters is always visible at the call site.
    /// </summary>
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
