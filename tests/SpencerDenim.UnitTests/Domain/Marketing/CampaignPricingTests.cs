using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Marketing;

namespace SpencerDenim.UnitTests.Domain.Marketing;

/// <summary>
/// Doc 08 §6 step 3 and doc 11 line 461 (ADM-14): "campaigns never stack; the lowest resulting price
/// wins; percent campaigns ≤ 90%", with the Rs. 1.00 floor.
/// <para>
/// <b>The crossover is the point of this file.</b> "Lowest wins" is a rule about the <i>result</i>, so a
/// percentage campaign and a fixed-amount campaign change places as the list price moves, and the same
/// pair of campaigns can be won by different campaigns on two products. Asserting one winner at one price
/// would pass against an implementation that always preferred the percentage, always preferred the fixed
/// amount, or always preferred the more specific target — which is the rule doc 08 and doc 11 both
/// decline to give.
/// </para>
/// </summary>
public sealed class CampaignPricingTests
{
    /// <summary>Doc 08 §6's worked example: "Linen Week", 20% off Women › Shirts &amp; Blouses.</summary>
    private static CampaignOffer LinenWeek => new(1, SaleDiscountType.Percent, 20m);

    /// <summary>
    /// Doc 08 §6's worked example, line 1: the shirt lists at Rs. 5,490.00 and "Linen Week" prices it at
    /// Rs. 4,392.00. The figure the pricing pipeline has carried as a constant since W3-03 is now
    /// computed from the campaign's two columns.
    /// </summary>
    [Fact]
    public void Linen_week_prices_the_worked_example_s_shirt_at_4_392()
    {
        Assert.Equal(4_392m, CampaignPricing.ResultingPrice(LinenWeek, listPrice: 5_490m));

        var best = CampaignPricing.Best([LinenWeek], listPrice: 5_490m);

        Assert.Equal(new CampaignPrice(1, 4_392m), best);
    }

    /// <summary>
    /// And the badge the document prints beside it: <c>floor((6,990 − 4,392) / 6,990 × 100)</c> = 37.
    /// </summary>
    /// <remarks>
    /// Asserted here as well as in <c>CatalogPricingTests</c> because the document writes "4,392.00
    /// (−37%)" as one fact about one line, and the two halves come from two files: if the campaign price
    /// were a rupee out the percentage would still floor to 37 and the discrepancy would be invisible on
    /// the card.
    /// </remarks>
    [Fact]
    public void The_worked_example_s_shirt_shows_minus_37_percent()
    {
        var sale = CatalogPricing.SalePrice(
            5_490m,
            CampaignPricing.Best([LinenWeek], 5_490m)!.Value.Price);

        Assert.Equal(4_392m, sale);
        Assert.Equal(37m, CatalogPricing.DiscountPercent(originalPrice: 6_990m, salePrice: sale));
    }

    /// <summary>
    /// Doc 08 §6 step 3's two formulas: <c>round(list × (1 − v/100))</c> and <c>list − v</c>.
    /// </summary>
    [Theory]
    [InlineData(SaleDiscountType.Percent, 20, 5_490, 4_392)]
    [InlineData(SaleDiscountType.Percent, 90, 10_000, 1_000)]
    [InlineData(SaleDiscountType.FixedAmount, 1_500, 7_490, 5_990)]
    public void Each_discount_type_follows_its_own_formula(
        SaleDiscountType type,
        decimal value,
        decimal listPrice,
        decimal expected) =>
        Assert.Equal(expected, CampaignPricing.ResultingPrice(new CampaignOffer(7, type, value), listPrice));

    /// <summary>
    /// Doc 08 §6: "rounded half away from zero to 2 dp". Rs. 1,000.10 less 35% is 650.065 exactly, which
    /// rounds <b>up</b> to 650.07 and not down to 650.06.
    /// </summary>
    /// <remarks>
    /// The case is chosen because <see cref="Math.Round(decimal, int)"/>'s default is banker's rounding,
    /// which would answer 650.06 — so this is the one assertion that distinguishes the rounding mode the
    /// document names from the one the framework defaults to. Every other figure in this file is exact at
    /// two decimal places and would pass under either.
    /// </remarks>
    [Fact]
    public void A_half_rupee_cent_rounds_away_from_zero()
    {
        var offer = new CampaignOffer(3, SaleDiscountType.Percent, 35m);

        Assert.Equal(650.07m, CampaignPricing.ResultingPrice(offer, listPrice: 1_000.10m));
    }

    /// <summary>
    /// Doc 08 §6 step 3's floor: a fixed amount larger than the price leaves Rs. 1.00, never nothing and
    /// never a negative price.
    /// </summary>
    [Theory]
    [InlineData(500, 200)]
    [InlineData(500, 501)]
    [InlineData(199, 200)]
    public void A_fixed_amount_never_prices_below_one_rupee(decimal discount, decimal listPrice)
    {
        var price = CampaignPricing.ResultingPrice(
            new CampaignOffer(4, SaleDiscountType.FixedAmount, discount),
            listPrice);

        Assert.True(price >= CatalogPricing.MinimumSalePrice, $"priced at {price}");
        Assert.Equal(Math.Max(listPrice - discount, 1m), price);
    }

    /// <summary>
    /// The boundary exactly: Rs. 500.00 off a Rs. 501.00 item is Rs. 1.00 and the floor has not bitten;
    /// Rs. 500.00 off a Rs. 500.50 item is Rs. 0.50 before the floor and Rs. 1.00 after it.
    /// </summary>
    [Fact]
    public void The_floor_bites_only_below_one_rupee()
    {
        var offer = new CampaignOffer(5, SaleDiscountType.FixedAmount, 500m);

        Assert.Equal(1m, CampaignPricing.ResultingPrice(offer, listPrice: 501m));
        Assert.Equal(1m, CampaignPricing.ResultingPrice(offer, listPrice: 500.50m));
        Assert.Equal(1.01m, CampaignPricing.ResultingPrice(offer, listPrice: 501.01m));
    }

    /// <summary>
    /// <c>CK_SaleCampaigns_DiscountValue</c>'s ceiling, applied: 90% off is Rs. 999.00 off Rs. 9,990.00's
    /// list price, and a campaign can ask for no more.
    /// </summary>
    [Fact]
    public void Ninety_percent_is_the_most_a_percent_campaign_can_take()
    {
        Assert.Equal(90m, SaleCampaign.MaxPercentDiscount);

        var atTheCeiling = new CampaignOffer(6, SaleDiscountType.Percent, SaleCampaign.MaxPercentDiscount);

        Assert.Equal(999m, CampaignPricing.ResultingPrice(atTheCeiling, listPrice: 9_990m));

        // A tenth of the list price, which is what "90% off" means and what the constraint's ceiling is
        // there to stop going further: the arithmetic itself would happily compute 95%.
        Assert.Equal(9_990m / 10m, CampaignPricing.ResultingPrice(atTheCeiling, 9_990m));
    }

    /// <summary>
    /// <b>The crossover.</b> One pair of campaigns — 30% off and Rs. 500.00 off — and two products. Below
    /// Rs. 1,666.67 the fixed amount takes more; above it the percentage does. Doc 11 line 461's rule
    /// picks a different winner for each product, and neither campaign is "better" in general.
    /// </summary>
    /// <remarks>
    /// The algebra: the fixed amount wins while <c>L − 500 &lt; 0.7L</c>, i.e. <c>L &lt; 1,666.67</c>.
    /// Rs. 1,600.00 and Rs. 1,700.00 sit either side of that with room for the rounding, and the two
    /// assertions differ only in the list price.
    /// </remarks>
    [Fact]
    public void The_same_two_campaigns_are_won_by_different_campaigns_on_two_products()
    {
        var percent = new CampaignOffer(10, SaleDiscountType.Percent, 30m);
        var fixedAmount = new CampaignOffer(20, SaleDiscountType.FixedAmount, 500m);
        CampaignOffer[] both = [percent, fixedAmount];

        // Rs. 1,600.00: 30% off is 1,120.00, Rs. 500.00 off is 1,100.00 — the fixed amount wins.
        Assert.Equal(new CampaignPrice(20, 1_100m), CampaignPricing.Best(both, 1_600m));

        // Rs. 1,700.00: 30% off is 1,190.00, Rs. 500.00 off is 1,200.00 — the percentage wins.
        Assert.Equal(new CampaignPrice(10, 1_190m), CampaignPricing.Best(both, 1_700m));
    }

    /// <summary>
    /// Two variants of <b>one</b> product, priced by the same two campaigns, resolve independently — which
    /// is why <see cref="CampaignPricing.Best"/> takes a list price and not a product.
    /// </summary>
    /// <remarks>
    /// A variant may carry a <c>PriceOverride</c> (doc 08 §6 step 1), so "the best campaign for this
    /// product" is not a well-defined thing to cache. This is the assertion that fails if anybody ever
    /// resolves the winner once per product and reuses it across the size run.
    /// </remarks>
    [Fact]
    public void Two_variants_of_one_product_can_be_won_by_different_campaigns()
    {
        CampaignOffer[] both =
        [
            new(10, SaleDiscountType.Percent, 30m),
            new(20, SaleDiscountType.FixedAmount, 500m),
        ];

        Assert.Equal(20, CampaignPricing.Best(both, 1_600m)!.Value.CampaignId);
        Assert.Equal(10, CampaignPricing.Best(both, 1_700m)!.Value.CampaignId);
    }

    /// <summary>
    /// <b>Specificity does not win; price does.</b> A product-specific campaign and a department-wide one
    /// on the same product are resolved by the resulting price, in both directions. Nothing in doc 08
    /// §3.6, doc 08 §6 step 3 or doc 11 P3 ranks the target types, so neither does the code.
    /// </summary>
    /// <remarks>
    /// The two offers are distinguished only by what targeted them, which <see cref="CampaignOffer"/>
    /// deliberately does not carry — the resolver cannot prefer a target type because it is never told
    /// one. This test is the statement of that design, and it is written as two directions so that an
    /// implementation that happened to take the first or the last offer in the list fails one of them.
    /// </remarks>
    [Fact]
    public void A_department_campaign_can_beat_a_product_campaign_and_the_other_way_round()
    {
        // The department campaign (id 30) takes 40%; the product campaign (id 31) takes Rs. 500.00.
        var departmentWide = new CampaignOffer(30, SaleDiscountType.Percent, 40m);
        var productOnly = new CampaignOffer(31, SaleDiscountType.FixedAmount, 500m);

        // At Rs. 2,000.00 the department campaign resolves lower (1,200.00 against 1,500.00) and wins,
        // although the other campaign names this one product.
        Assert.Equal(new CampaignPrice(30, 1_200m), CampaignPricing.Best([departmentWide, productOnly], 2_000m));

        // At Rs. 1,000.00 the product campaign resolves lower (500.00 against 600.00) and wins.
        Assert.Equal(new CampaignPrice(31, 500m), CampaignPricing.Best([departmentWide, productOnly], 1_000m));
    }

    /// <summary>
    /// Three campaigns on one product still produce one price — doc 11 line 461's "never stack". Stacking
    /// 20%, 10% and Rs. 200.00 would reach Rs. 1,240.00; the rule reaches Rs. 1,600.00.
    /// </summary>
    [Fact]
    public void Three_campaigns_on_one_product_do_not_stack()
    {
        CampaignOffer[] three =
        [
            new(41, SaleDiscountType.Percent, 20m),
            new(42, SaleDiscountType.Percent, 10m),
            new(43, SaleDiscountType.FixedAmount, 200m),
        ];

        Assert.Equal(new CampaignPrice(41, 1_600m), CampaignPricing.Best(three, 2_000m));
    }

    /// <summary>
    /// Two campaigns that resolve to the same price are broken by the lower id, and the answer does not
    /// depend on the order the reader happened to return them in.
    /// </summary>
    /// <remarks>
    /// It matters because <c>sales.OrderLines.SaleCampaignId</c> is stored: the bag, the checkout quote
    /// and the placement each resolve the price again, and a tie broken by enumeration order would let one
    /// order record a campaign the quote attributed to the other. The prices are identical either way, so
    /// nothing a shopper sees changes — which is exactly why it would never be noticed.
    /// </remarks>
    [Fact]
    public void A_tie_is_broken_by_the_lower_id_whatever_the_order()
    {
        // At Rs. 2,000.00, 25% off and Rs. 500.00 off both resolve to Rs. 1,500.00.
        var percent = new CampaignOffer(51, SaleDiscountType.Percent, 25m);
        var fixedAmount = new CampaignOffer(50, SaleDiscountType.FixedAmount, 500m);

        Assert.Equal(new CampaignPrice(50, 1_500m), CampaignPricing.Best([percent, fixedAmount], 2_000m));
        Assert.Equal(new CampaignPrice(50, 1_500m), CampaignPricing.Best([fixedAmount, percent], 2_000m));
    }

    /// <summary>No campaign targets the product: doc 08 §6 step 3's "with no campaign, sale = list".</summary>
    [Fact]
    public void No_campaign_is_no_discount()
    {
        Assert.Null(CampaignPricing.Best([], 5_490m));
        Assert.Equal(5_490m, CatalogPricing.SalePrice(5_490m, campaignPrice: null));
    }

    /// <summary>
    /// A campaign that cannot beat the list price is not a winner, so no line is stamped with a campaign
    /// id it was never discounted by.
    /// </summary>
    /// <remarks>
    /// Reachable only at a list price of Rs. 1.00, where the floor makes every campaign resolve to
    /// Rs. 1.00. It is not a hypothetical: <c>CK_ProductVariants</c> and <c>CK_Products_BasePrice</c>
    /// allow a price of Rs. 1.00, and a campaign over a whole department sweeps up whatever is in it.
    /// Were a <see cref="CampaignPrice"/> returned here, the card would show "−0%", the Sale page would
    /// list the product and a promo code with <c>ExcludeSaleItems</c> would throw the line out of its
    /// eligible base for a discount of nothing.
    /// </remarks>
    [Fact]
    public void A_campaign_that_cannot_beat_the_list_price_does_not_win()
    {
        CampaignOffer[] offers =
        [
            new(60, SaleDiscountType.Percent, 90m),
            new(61, SaleDiscountType.FixedAmount, 10_000m),
        ];

        Assert.Equal(1m, CampaignPricing.ResultingPrice(offers[0], listPrice: 1m));
        Assert.Null(CampaignPricing.Best(offers, listPrice: 1m));
    }

    /// <summary>
    /// A discount type outside <c>CK_SaleCampaigns_DiscountType</c> throws rather than quietly charging
    /// the list price.
    /// </summary>
    [Fact]
    public void An_impossible_discount_type_throws()
    {
        var offer = new CampaignOffer(70, (SaleDiscountType)9, 10m);

        var exception = Assert.Throws<InvalidOperationException>(
            () => CampaignPricing.ResultingPrice(offer, 1_000m));

        Assert.Contains("CK_SaleCampaigns_DiscountType", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Doc 11 line 450 / doc 08 §6's worked example, end to end, with the sale price computed from the
    /// campaign rather than given as a constant.</b> Grand total Rs. 15,596.60, "You saved" Rs. 6,823.40.
    /// </summary>
    /// <remarks>
    /// <c>PromoPricingPipelineTests</c> already asserts these two figures, and asserts them from the promo
    /// code's own columns — but with <c>UnitPrice</c> written into the test as the constant 4,392.00,
    /// because <c>marketing.SaleCampaigns</c> did not exist. This is the last link of that chain: the
    /// Rs. 4,392.00 now comes out of "Linen Week"'s <c>DiscountType</c> and <c>DiscountValue</c>, and the
    /// split doc 08 §6 step 6 prints — markdown Rs. 3,000.00, campaign Rs. 2,196.00 — is asserted with it,
    /// since that is the figure that moves if the campaign price is wrong by a rupee while the total still
    /// looks plausible.
    /// </remarks>
    [Fact]
    public void Doc_08_s_worked_example_holds_with_the_sale_price_computed_from_the_campaign()
    {
        // Line 1: the shirt. Original 6,990.00, list 5,490.00, and "Linen Week" targets Women › Shirts
        // &amp; Blouses, which this product is in.
        var shirtList = CatalogPricing.ListPrice(basePrice: 5_490m, priceOverride: null);
        var shirtOriginal = CatalogPricing.OriginalPrice(shirtList, productCompareAtPrice: 6_990m, null);
        var shirtSale = CatalogPricing.SalePrice(shirtList, CampaignPricing.Best([LinenWeek], shirtList)?.Price);

        // Line 2: the jeans, which are in Men > Jeans. "Linen Week" does not target them, so the offer
        // set for this line is empty and sale = list = original = 7,490.00. Which campaigns reach which
        // product is the reader's question, not this function's — handing it `LinenWeek` here would have
        // asserted that the jeans were 20% off, which is what the first draft of this test did.
        var jeansList = CatalogPricing.ListPrice(basePrice: 7_490m, priceOverride: null);
        var jeansOriginal = CatalogPricing.OriginalPrice(jeansList, productCompareAtPrice: null, null);
        var jeansSale = CatalogPricing.SalePrice(jeansList, CampaignPricing.Best([], jeansList)?.Price);

        Assert.Equal(4_392m, shirtSale);
        Assert.Equal(7_490m, jeansSale);

        SpencerDenim.Domain.Sales.OrderPricingLine[] lines =
        [
            new(101, 2, shirtOriginal, shirtList, shirtSale),
            new(202, 1, jeansOriginal, jeansList, jeansSale),
        ];

        // WELCOME10: 10%, cap Rs. 2,000.00, minimum spend Rs. 5,000.00 (doc 11 line 450).
        var promo = PromoEvaluator.Evaluate(
            new PromoCodeTerms(
                Id: 1,
                Code: "WELCOME10",
                DiscountType: PromoDiscountType.Percent,
                DiscountValue: 10m,
                MaxDiscountAmount: 2_000m,
                MinSpendAmount: 5_000m,
                ExcludeSaleItems: false,
                StartsAtUtc: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                EndsAtUtc: null,
                TotalUsageLimit: null,
                PerCustomerLimit: null,
                UsageCount: 0,
                IsActive: true),
            lines,
            new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc));

        // Express, Colombo: Rs. 950.00, no free threshold (doc 08 §6's worked example, step 3).
        var result = SpencerDenim.Domain.Sales.OrderPricing.Price(
            lines,
            new SpencerDenim.Domain.Sales.DeliveryCharge(950m, FreeAboveAmount: null),
            promo.Charge);

        Assert.Equal(21_470m, result.ItemsOriginalTotal);
        Assert.Equal(18_470m, result.ItemsListTotal);
        Assert.Equal(16_274m, result.ItemsSubtotal);
        Assert.Equal(3_000m, result.MarkdownDiscountTotal);
        Assert.Equal(2_196m, result.CampaignDiscountTotal);
        Assert.Equal(1_627.40m, result.PromoDiscountAmount);
        Assert.Equal(950m, result.DeliveryFeeAmount);
        Assert.Equal(0m, result.DeliveryDiscountAmount);
        Assert.Equal(15_596.60m, result.GrandTotal);
        Assert.Equal(6_823.40m, result.SavedTotal);
    }

    /// <summary>
    /// The same bag with the document's other two delivery choices (doc 08 §6's worked example, step 6):
    /// Standard in Colombo is free above Rs. 10,000.00 and the total is Rs. 14,646.60; a customer in
    /// Jaffna pays Rs. 600.00 and the total is Rs. 15,246.60.
    /// </summary>
    [Theory]
    [InlineData(400, 10_000, 14_646.60)]
    [InlineData(600, 15_000, 15_246.60)]
    public void The_worked_example_s_other_delivery_choices_hold_too(
        decimal fee,
        decimal freeAbove,
        decimal expectedGrandTotal)
    {
        var shirtSale = CatalogPricing.SalePrice(5_490m, CampaignPricing.Best([LinenWeek], 5_490m)?.Price);

        SpencerDenim.Domain.Sales.OrderPricingLine[] lines =
        [
            new(101, 2, 6_990m, 5_490m, shirtSale),
            new(202, 1, 7_490m, 7_490m, 7_490m),
        ];

        var result = SpencerDenim.Domain.Sales.OrderPricing.Price(
            lines,
            new SpencerDenim.Domain.Sales.DeliveryCharge(fee, freeAbove),
            new SpencerDenim.Domain.Sales.PromoCharge(1_627.40m));

        Assert.Equal(expectedGrandTotal, result.GrandTotal);
    }

    /// <summary>
    /// Doc 11 P3's derived statuses: the start is inclusive and the end exclusive, and an inactive
    /// campaign is not live whatever its dates say (doc 11 P3 step 5's "deactivating it").
    /// </summary>
    [Theory]
    [InlineData(true, "2026-10-13T00:00:00Z", true)]
    [InlineData(true, "2026-10-12T23:59:59Z", false)]
    [InlineData(true, "2026-10-19T23:59:59Z", true)]
    [InlineData(true, "2026-10-20T00:00:00Z", false)]
    [InlineData(false, "2026-10-15T12:00:00Z", false)]
    public void A_campaign_is_live_from_its_start_until_its_end(bool isActive, string now, bool expected)
    {
        var starts = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
        var ends = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);

        var utcNow = DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;

        Assert.Equal(expected, CampaignWindow.IsLive(isActive, starts, ends, utcNow));
    }
}
