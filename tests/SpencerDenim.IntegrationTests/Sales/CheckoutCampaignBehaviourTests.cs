using System.Net;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Sale campaigns from the shopper's side, against a real SQL Server (ADM-14; doc 08 §3.6 and §6 step 3,
/// doc 11 P3 section A).
/// <para>
/// <b>Every assertion is on the stored order, not on the response alone.</b> The response says what the
/// server believes it charged; <c>sales.Orders.CampaignDiscountTotal</c> and
/// <c>sales.OrderLines.SaleCampaignId</c> are what it charged and what it recorded. The whole reason
/// these are integration tests is that the two things that can go wrong — a campaign that does not reach
/// the product through its real targeting rows, and a column that is never written — are both invisible
/// to a unit test.
/// </para>
/// <para>
/// <b>The seeded catalogue happens to be doc 08 §6's worked example.</b> <c>DemoCatalogueData</c> carries
/// "Relaxed Linen Blend Shirt" at Rs. 5,490.00 with a Rs. 6,990.00 compare-at price in Women ›
/// Shirts &amp; Blouses, and "High-Rise Slim Fit Jeans" at Rs. 7,490.00 with none, in Women › Jeans; the
/// seeded Express rate for the Colombo zone is Rs. 950.00 with no free threshold. So the document's
/// example can be placed as a real order rather than approximated, and
/// <see cref="Doc_08_s_worked_example_places_as_a_real_order"/> does exactly that.
/// </para>
/// </summary>
public sealed class CheckoutCampaignBehaviourTests(SqlServerDatabaseFixture sqlServer)
    : CheckoutCampaignTestBase(sqlServer)
{
    private const string ShirtSlug = "relaxed-linen-blend-shirt";
    private const string JeansSlug = "high-rise-slim-fit-jeans";

    /// <summary>
    /// <b>Doc 11 line 450 / doc 08 §6's worked example, as a placed order.</b> "Linen Week", 20% off
    /// Women › Shirts &amp; Blouses; two shirts and one pair of jeans; <c>WELCOME10</c> at 10% capped at
    /// Rs. 2,000.00 with a Rs. 5,000.00 minimum; Express to Colombo. Grand total Rs. 15,596.60, saved
    /// Rs. 6,823.40.
    /// </summary>
    /// <remarks>
    /// <b>Every one of the document's nine order columns is asserted, not only the total.</b> A campaign
    /// price that was a rupee out would move <c>CampaignDiscountTotal</c> and <c>ItemsSubtotal</c>
    /// together, and the grand total with them, so a single assertion on the total cannot tell a wrong
    /// campaign price from a wrong promo discount. The markdown/campaign split is the figure that
    /// distinguishes them, and it is doc 08 §6 step 6's own.
    /// <para>
    /// The two line-level <c>SaleCampaignId</c> values are asserted too: the shirt line records the
    /// campaign, the jeans line records nothing. A build that stamped the id on every line would produce
    /// identical totals and a wrong answer to "what was on sale".
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Doc_08_s_worked_example_places_as_a_real_order()
    {
        var shirt = await BaseVariantIdAsync(ShirtSlug);
        var jeans = await BaseVariantIdAsync(JeansSlug);
        var colombo = await DistrictIdAsync("Colombo");
        var express = await DeliveryMethodIdAsync(DeliveryMethod.ExpressCode);

        await SetStockAsync(shirt, 20);
        await SetStockAsync(jeans, 20);

        var campaign = await NewCampaignAsync(
            SaleDiscountType.Percent,
            20m,
            [await OnCategoryAsync("women", "shirts-blouses")]);

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.Percent,
            discountValue: 10m,
            minSpendAmount: 5_000m,
            maxDiscountAmount: 2_000m);

        var (_, api) = await NewMultiLineBagAsync((shirt, 2), (jeans, 1));
        var (_, session) = await api.GetAsync(colombo, express, Token, code.Code);

        var (response, confirmation) = await api.PlaceAsync(
            PromoBody(session, express, colombo, UniqueEmail("worked-example"), code.Code),
            Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(confirmation);

        var order = await OrderByNumberAsync(confirmation.OrderNumber);

        // Doc 08 §6's worked example, step 5: the order row, column by column.
        Assert.Equal(21_470m, order.ItemsOriginalTotal);
        Assert.Equal(18_470m, order.ItemsListTotal);
        Assert.Equal(16_274m, order.ItemsSubtotal);
        Assert.Equal(3_000m, order.MarkdownDiscountTotal);
        Assert.Equal(2_196m, order.CampaignDiscountTotal);
        Assert.Equal(1_627.40m, order.PromoDiscountAmount);
        Assert.Equal(950m, order.DeliveryFeeAmount);
        Assert.Equal(0m, order.DeliveryDiscountAmount);
        Assert.Equal(15_596.60m, order.GrandTotal);

        // "You saved" = markdown + campaign + promo (doc 11 line 458).
        Assert.Equal(
            6_823.40m,
            order.MarkdownDiscountTotal + order.CampaignDiscountTotal + order.PromoDiscountAmount);

        var lineCampaigns = await LineCampaignsAsync(confirmation.OrderNumber);

        Assert.Equal(campaign.Id, lineCampaigns[shirt]);
        Assert.Null(lineCampaigns[jeans]);
    }

    /// <summary>
    /// <b>The crossover, on real rows.</b> One pair of campaigns — 30% off the whole Women department and
    /// Rs. 2,000.00 off, also department-wide — and two products at different list prices. The
    /// percentage wins on the jeans (Rs. 7,490.00 → 5,243.00 against 5,490.00) and the fixed amount wins
    /// on the shirt (Rs. 5,490.00 → 3,490.00 against 3,843.00).
    /// </summary>
    /// <remarks>
    /// <b>Both campaigns target the same thing, so nothing but the resulting price can decide.</b> A build
    /// that preferred the percentage, the fixed amount, the newer campaign or the older one would get one
    /// of these two lines wrong, and the two lines are in one bag so one placement answers both.
    /// <para>
    /// Asserted through <c>CampaignDiscountTotal</c> as well as the line prices, because that column is
    /// the sum of <c>(list − sale) × qty</c> and is what the "You saved" line prints: 2,000.00 + 2,247.00.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_lowest_resulting_price_wins_per_product_not_per_campaign()
    {
        var shirt = await BaseVariantIdAsync(ShirtSlug);
        var jeans = await BaseVariantIdAsync(JeansSlug);
        var colombo = await DistrictIdAsync("Colombo");
        var express = await DeliveryMethodIdAsync(DeliveryMethod.ExpressCode);

        await SetStockAsync(shirt, 20);
        await SetStockAsync(jeans, 20);

        var percent = await NewCampaignAsync(
            SaleDiscountType.Percent,
            30m,
            [await OnDepartmentAsync("women")]);

        var fixedAmount = await NewCampaignAsync(
            SaleDiscountType.FixedAmount,
            2_000m,
            [await OnDepartmentAsync("women")]);

        var (_, api) = await NewMultiLineBagAsync((shirt, 1), (jeans, 1));
        var (_, session) = await api.GetAsync(colombo, express, Token, null);

        var (response, confirmation) = await api.PlaceAsync(
            PromoBody(session, express, colombo, UniqueEmail("crossover"), null),
            Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(confirmation);

        var lineCampaigns = await LineCampaignsAsync(confirmation.OrderNumber);

        // Rs. 5,490.00: 30% off is 3,843.00, Rs. 2,000.00 off is 3,490.00 — the fixed amount wins.
        Assert.Equal(fixedAmount.Id, lineCampaigns[shirt]);

        // Rs. 7,490.00: 30% off is 5,243.00, Rs. 2,000.00 off is 5,490.00 — the percentage wins.
        Assert.Equal(percent.Id, lineCampaigns[jeans]);

        var order = await OrderByNumberAsync(confirmation.OrderNumber);

        Assert.Equal(3_490m + 5_243m, order.ItemsSubtotal);
        Assert.Equal(2_000m + 2_247m, order.CampaignDiscountTotal);
    }

    /// <summary>
    /// <b>Specificity does not win; price does.</b> A product-specific campaign and a department-wide one
    /// on the same product, and the department-wide one is charged because it resolves lower.
    /// </summary>
    /// <remarks>
    /// This is the assertion that fails if anybody ever adds the precedence rule that looks obvious and
    /// that neither doc 08 §3.6, doc 08 §6 step 3 nor doc 11 P3 contains. 40% off Rs. 5,490.00 is
    /// Rs. 3,294.00; Rs. 1,000.00 off the same shirt is Rs. 4,490.00.
    /// </remarks>
    [Fact]
    public async Task A_department_campaign_beats_a_product_campaign_when_it_prices_lower()
    {
        var shirt = await BaseVariantIdAsync(ShirtSlug);
        var colombo = await DistrictIdAsync("Colombo");
        var express = await DeliveryMethodIdAsync(DeliveryMethod.ExpressCode);

        await SetStockAsync(shirt, 20);

        var departmentWide = await NewCampaignAsync(
            SaleDiscountType.Percent,
            40m,
            [await OnDepartmentAsync("women")]);

        await NewCampaignAsync(
            SaleDiscountType.FixedAmount,
            1_000m,
            [await OnProductAsync(ShirtSlug)]);

        var (_, api) = await NewMultiLineBagAsync((shirt, 1));
        var (_, session) = await api.GetAsync(colombo, express, Token, null);

        var (response, confirmation) = await api.PlaceAsync(
            PromoBody(session, express, colombo, UniqueEmail("specificity"), null),
            Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(confirmation);

        var order = await OrderByNumberAsync(confirmation.OrderNumber);

        Assert.Equal(3_294m, order.ItemsSubtotal);
        Assert.Equal(departmentWide.Id, (await LineCampaignsAsync(confirmation.OrderNumber))[shirt]);
    }

    /// <summary>
    /// Doc 08 §6 step 3's floor: a fixed amount larger than the price charges Rs. 1.00, and the order is
    /// still payable because doc 08 §6 step 11's own minimum is the same figure.
    /// </summary>
    /// <remarks>
    /// <b>The floor is the one rule in step 3 that a constraint also guards</b>
    /// (<c>CK_OrderLines_UnitPrice</c> &gt; 0), so a build without it would not merely charge the wrong
    /// amount — it would fail the placement inside its transaction, which the shopper would meet as a
    /// failed checkout with no explanation. This asserts the charge, which is the stronger claim.
    /// </remarks>
    [Fact]
    public async Task A_fixed_campaign_larger_than_the_price_charges_one_rupee()
    {
        var shirt = await BaseVariantIdAsync(ShirtSlug);
        var colombo = await DistrictIdAsync("Colombo");
        var express = await DeliveryMethodIdAsync(DeliveryMethod.ExpressCode);

        await SetStockAsync(shirt, 20);

        await NewCampaignAsync(
            SaleDiscountType.FixedAmount,
            99_000m,
            [await OnProductAsync(ShirtSlug)]);

        var (_, api) = await NewMultiLineBagAsync((shirt, 2));
        var (_, session) = await api.GetAsync(colombo, express, Token, null);

        var (response, confirmation) = await api.PlaceAsync(
            PromoBody(session, express, colombo, UniqueEmail("floor"), null),
            Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(confirmation);

        var order = await OrderByNumberAsync(confirmation.OrderNumber);

        // Two units at Rs. 1.00, plus the Rs. 950.00 Express fee.
        Assert.Equal(2m, order.ItemsSubtotal);
        Assert.Equal(952m, order.GrandTotal);
        Assert.Equal(10_980m - 2m, order.CampaignDiscountTotal);
    }

    /// <summary>
    /// Doc 08 §6 step 7 and doc 11 §7 G-07: <b>"Exclude sale items" removes the lines a campaign
    /// priced</b>, and leaves a line that only carries a markdown eligible.
    /// </summary>
    /// <remarks>
    /// <b>This is the first test in the repository that can actually exercise that flag.</b> W3-05 built
    /// and mutation-tested it against a synthesised campaign-priced line and recorded in
    /// <c>PromoEvaluator</c>'s own remarks that no real line could satisfy the condition until ADM-14
    /// existed. The arrangement here is the pair the rule distinguishes, in one bag: the <b>jeans</b> are
    /// priced by a campaign and must be excluded, and the <b>shirt</b> carries a Rs. 6,990.00 → 5,490.00
    /// markdown and nothing else, so it must stay in.
    /// <para>
    /// The promo is a flat 10%, so the eligible base is the whole arithmetic: 10% of the shirt alone is
    /// Rs. 549.00, where 10% of both lines would be Rs. 1,098.00. The jeans line's
    /// <c>PromoDiscountAllocated</c> is asserted to be zero as well, because a build that computed the
    /// right discount and then spread it over both lines would match the order total and still put a
    /// discount on an excluded line.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Exclude_sale_items_drops_the_campaign_line_and_keeps_the_markdown_line()
    {
        var shirt = await BaseVariantIdAsync(ShirtSlug);
        var jeans = await BaseVariantIdAsync(JeansSlug);
        var colombo = await DistrictIdAsync("Colombo");
        var express = await DeliveryMethodIdAsync(DeliveryMethod.ExpressCode);

        await SetStockAsync(shirt, 20);
        await SetStockAsync(jeans, 20);

        // Only the jeans. The shirt's markdown is the seeded compare-at price, which is not a campaign.
        var campaign = await NewCampaignAsync(
            SaleDiscountType.Percent,
            20m,
            [await OnProductAsync(JeansSlug)]);

        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.Percent,
            discountValue: 10m,
            excludeSaleItems: true);

        var (_, api) = await NewMultiLineBagAsync((shirt, 1), (jeans, 1));
        var (_, session) = await api.GetAsync(colombo, express, Token, code.Code);

        var (response, confirmation) = await api.PlaceAsync(
            PromoBody(session, express, colombo, UniqueEmail("exclude-sale"), code.Code),
            Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(confirmation);

        var order = await OrderByNumberAsync(confirmation.OrderNumber);

        // The jeans at 20% off: 7,490.00 → 5,992.00. Subtotal 5,490.00 + 5,992.00.
        Assert.Equal(11_482m, order.ItemsSubtotal);
        Assert.Equal(1_498m, order.CampaignDiscountTotal);

        // 10% of the shirt alone — the markdown-only line — and not of the bag.
        Assert.Equal(549m, order.PromoDiscountAmount);

        var lines = await LinesByVariantAsync(confirmation.OrderNumber);

        Assert.Equal(549m, lines[shirt].PromoDiscountAllocated);
        Assert.Equal(0m, lines[jeans].PromoDiscountAllocated);
        Assert.Null(lines[shirt].SaleCampaignId);
        Assert.Equal(campaign.Id, lines[jeans].SaleCampaignId);
    }

    /// <summary>
    /// <b>Doc 08 §7.4's two halves, pinned against each other: the bag prices live while the stored
    /// summary still says the old price.</b> This is the test that stops a later change quietly making
    /// checkout read the five-minute-stale summary.
    /// </summary>
    /// <remarks>
    /// The campaign is created and <b>nothing refreshes <c>catalog.ProductStats</c></b> — no save path, no
    /// <c>RefreshCampaignPricing</c> pass. Doc 08 §7.4 allows that summary to lag up to five minutes at a
    /// campaign boundary and says in the same row that "checkout always prices live", so the correct
    /// state of the world here is precisely a disagreement: <c>MinSalePrice</c> still Rs. 5,490.00 and the
    /// bag already Rs. 4,392.00.
    /// <para>
    /// <b>Both halves are asserted, deliberately.</b> Asserting only the bag would pass against an
    /// implementation that had eagerly refreshed the summary too, and would then go on passing if someone
    /// later pointed the bag at the summary. Asserting the disagreement means neither change can hide.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_bag_prices_live_while_the_stored_summary_still_lags()
    {
        var shirt = await BaseVariantIdAsync(ShirtSlug);
        var productId = await ProductIdAsync(ShirtSlug);

        await SetStockAsync(shirt, 20);

        var before = await StoredSummaryAsync(productId);
        Assert.Equal(5_490m, before.MinSalePrice);

        await NewCampaignAsync(
            SaleDiscountType.Percent,
            20m,
            [await OnCategoryAsync("women", "shirts-blouses")]);

        var (_, api) = await NewMultiLineBagAsync((shirt, 1));
        var (_, session) = await api.GetAsync(null, null, Token, null);

        Assert.NotNull(session);
        Assert.Equal(4_392m, session.Lines.Single(line => line.VariantId == shirt).UnitPrice);

        // Unchanged, and that is doc 08 §7.4 working rather than failing.
        var after = await StoredSummaryAsync(productId);
        Assert.Equal(5_490m, after.MinSalePrice);
    }

    /// <summary>
    /// Doc 11 P3 step 5's three ways to end a sale, from the shopper's side: a campaign whose end has
    /// passed, one staff deactivated, and one staff removed all stop pricing the bag at once.
    /// </summary>
    /// <remarks>
    /// <b>The soft-delete case is the one worth having here rather than only in a model test.</b>
    /// <c>CampaignOfferReader</c>'s predicate never mentions <c>IsDeleted</c>: the model's global query
    /// filter is the only thing that ends the sale, and this is the test behind that claim. The
    /// deactivation case covers the other half of <c>IX_SaleCampaigns_Window</c>'s filter.
    /// </remarks>
    [Theory]
    [InlineData("ended")]
    [InlineData("inactive")]
    [InlineData("removed")]
    public async Task A_campaign_that_is_over_deactivated_or_removed_prices_nothing(string how)
    {
        var shirt = await BaseVariantIdAsync(ShirtSlug);
        await SetStockAsync(shirt, 20);

        var now = DateTime.UtcNow;

        var campaign = await NewCampaignAsync(
            SaleDiscountType.Percent,
            20m,
            [await OnProductAsync(ShirtSlug)],
            startsAtUtc: how == "ended" ? now.AddHours(-2) : null,
            endsAtUtc: how == "ended" ? now.AddHours(-1) : null,
            isActive: how != "inactive");

        if (how == "removed")
        {
            await RemoveCampaignAsync(campaign.Id);
        }

        var (_, api) = await NewMultiLineBagAsync((shirt, 1));
        var (_, session) = await api.GetAsync(null, null, Token, null);

        Assert.NotNull(session);
        Assert.Equal(5_490m, session.Lines.Single(line => line.VariantId == shirt).UnitPrice);
    }

    /// <summary>
    /// Doc 11 P3 step 5's first way: moving the end date ends the sale on the next read, with no job and
    /// no cache to wait for on the money path.
    /// </summary>
    [Fact]
    public async Task Moving_a_campaign_s_end_into_the_past_ends_it_on_the_next_bag_read()
    {
        var shirt = await BaseVariantIdAsync(ShirtSlug);
        await SetStockAsync(shirt, 20);

        var campaign = await NewCampaignAsync(
            SaleDiscountType.Percent,
            20m,
            [await OnProductAsync(ShirtSlug)]);

        var (_, api) = await NewMultiLineBagAsync((shirt, 1));

        var (_, during) = await api.GetAsync(null, null, Token, null);
        Assert.Equal(4_392m, during!.Lines.Single(line => line.VariantId == shirt).UnitPrice);

        await EndCampaignAsync(campaign.Id, DateTime.UtcNow.AddMinutes(-1));

        var (_, after) = await api.GetAsync(null, null, Token, null);
        Assert.Equal(5_490m, after!.Lines.Single(line => line.VariantId == shirt).UnitPrice);
    }

    /// <summary>
    /// A category target without its subtree reaches only that category, and the department target that
    /// would have reached the subtree is not implied (doc 08 §3.6's <c>IncludeSubcategories</c>).
    /// </summary>
    /// <remarks>
    /// Both products are in Women, and the campaign names Women › Jeans with
    /// <c>IncludeSubcategories = false</c>. The jeans are discounted and the shirt is not — which is the
    /// pair that fails if the subtree predicate is ever widened to "same department" or the flag is
    /// ignored.
    /// </remarks>
    [Fact]
    public async Task A_category_target_reaches_that_category_and_not_its_siblings()
    {
        var shirt = await BaseVariantIdAsync(ShirtSlug);
        var jeans = await BaseVariantIdAsync(JeansSlug);

        await SetStockAsync(shirt, 20);
        await SetStockAsync(jeans, 20);

        await NewCampaignAsync(
            SaleDiscountType.Percent,
            20m,
            [await OnCategoryAsync("women", "jeans", includeSubcategories: false)]);

        var (_, api) = await NewMultiLineBagAsync((shirt, 1), (jeans, 1));
        var (_, session) = await api.GetAsync(null, null, Token, null);

        Assert.NotNull(session);
        Assert.Equal(5_992m, session.Lines.Single(line => line.VariantId == jeans).UnitPrice);
        Assert.Equal(5_490m, session.Lines.Single(line => line.VariantId == shirt).UnitPrice);
    }
}
