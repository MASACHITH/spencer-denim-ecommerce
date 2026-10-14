using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Doc 08 §7.4's batched recompute of <c>catalog.ProductStats</c>' pricing columns (ADM-14, DEPT-03,
/// HOME-07), and the three operational questions it has to have an answer to.
/// <para>
/// <b>The first test is the one that matters most, because the statement is a second implementation of
/// <see cref="CampaignPricing"/>.</b> Doc 08 §7.4 requires each batch to be one <c>UPDATE … FROM</c> that
/// computes in the database, so the percentage, the floor, the tie-break and the targeting all exist
/// twice — once in C# for the bag and the checkout, once in T-SQL for the listing. Two implementations of
/// a price is how this project already shipped a summary where subtotal minus discount did not equal the
/// total. <see cref="The_stored_summary_agrees_with_the_live_price_for_every_product"/> drives a matrix
/// through both and compares them, so a divergence is a failure here rather than a wrong badge on a card.
/// </para>
/// </summary>
public sealed class CampaignPricingRefreshTests(SqlServerDatabaseFixture sqlServer)
    : CheckoutCampaignTestBase(sqlServer)
{
    /// <summary>
    /// <b>The equivalence test.</b> A matrix of campaigns — a percentage, a fixed amount that crosses
    /// over with it, and one large enough to hit the Rs. 1.00 floor — over the whole Women department,
    /// then every affected product's stored summary compared with what the live path computes from the
    /// same rows.
    /// </summary>
    /// <remarks>
    /// <b>The expectation is computed, not written down.</b> For each product it reads the product's
    /// active variants and the live <see cref="ICampaignOfferReader"/>, applies
    /// <see cref="CampaignPricing.Best"/> and <see cref="CatalogPricing"/> exactly as the bag does, and
    /// aggregates. So the test cannot pass by agreeing with a figure a developer typed: the two paths have
    /// to agree with each other, and every one of the four columns is compared.
    /// <para>
    /// <b>The crossover is in the matrix on purpose.</b> 30% off and Rs. 2,000.00 off change places at
    /// Rs. 6,666.67, and the Women department spans prices either side of that — so the T-SQL's
    /// <c>MIN</c> over the candidate prices has to pick different campaigns for different products, which
    /// is the thing a <c>TOP 1 ORDER BY DiscountValue</c> would get wrong while looking right.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_stored_summary_agrees_with_the_live_price_for_every_product()
    {
        var campaignIds = new List<int>
        {
            (await NewCampaignAsync(SaleDiscountType.Percent, 30m, [await OnDepartmentAsync("women")])).Id,
            (await NewCampaignAsync(SaleDiscountType.FixedAmount, 2_000m, [await OnDepartmentAsync("women")])).Id,
            (await NewCampaignAsync(
                SaleDiscountType.FixedAmount,
                99_000m,
                [await OnProductAsync("relaxed-linen-blend-shirt")])).Id,

            // A category target with its subtree, so the T-SQL's `LIKE Path + '%'` branch is in the
            // matrix too and not only the department and product branches. (That the targeting *rule*
            // is right is asserted independently on the live path, in
            // CheckoutCampaignBehaviourTests.A_category_target_reaches_that_category_and_not_its_siblings;
            // what this adds is that the two implementations of it agree.)
            (await NewCampaignAsync(
                SaleDiscountType.Percent,
                15m,
                [await OnCategoryAsync("men", "jeans")])).Id,

            // **Three campaigns that must price nothing**, so the T-SQL's own three exclusions are in
            // the matrix as well as its arithmetic: the active flag and the two window bounds. Each is
            // 50% off the whole catalogue, so dropping any one of the three conditions from the statement
            // moves nearly every product and the comparison fails loudly. Measured: without them,
            // removing `c.IsActive = 1` from the statement left every test green.
            (await NewCampaignAsync(
                SaleDiscountType.Percent,
                50m,
                [await OnDepartmentAsync("women"), await OnDepartmentAsync("men")],
                isActive: false)).Id,

            (await NewCampaignAsync(
                SaleDiscountType.Percent,
                50m,
                [await OnDepartmentAsync("women"), await OnDepartmentAsync("men")],
                startsAtUtc: DateTime.UtcNow.AddHours(2),
                endsAtUtc: DateTime.UtcNow.AddHours(3))).Id,

            (await NewCampaignAsync(
                SaleDiscountType.Percent,
                50m,
                [await OnDepartmentAsync("women"), await OnDepartmentAsync("men")],
                startsAtUtc: DateTime.UtcNow.AddHours(-3),
                endsAtUtc: DateTime.UtcNow.AddHours(-2))).Id,
        };

        using var scope = CreateScope();
        var refresher = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();

        var targeted = await refresher.ProductsTargetedByAsync(campaignIds, Token);
        Assert.NotEmpty(targeted);

        // **Every product in the catalogue, not only the targeted ones — and that is the difference
        // between this test catching a divergence and missing one.** An `UPDATE` only writes the ids it
        // is given, so if the affected set came from the C# targeting, a T-SQL rule that reached *wider*
        // than the C# one could never show: the only products compared would be ones both rules agree
        // about. Measured: with the affected set, widening the T-SQL's subtree test to "same department"
        // left this test green. Over the whole catalogue it fails, because an over-reaching rule writes a
        // discount to a product the live path says has none.
        var all = await AllProductIdsAsync();
        Assert.True(all.Count > targeted.Count, "every product is targeted, so over-reach cannot show");

        var pass = await refresher.RefreshProductsAsync(all, Token);

        Assert.Equal(all.Count, pass.Products);
        Assert.Equal(1, pass.Batches);
        Assert.True(pass.RowsWritten > 0, "the pass wrote no ProductStats row");

        var expected = await ExpectedSummariesAsync(all);
        var actual = await StoredSummariesAsync(all);

        Assert.Equal(expected.Count, actual.Count);

        foreach (var (productId, want) in expected)
        {
            var got = actual[productId];

            Assert.Equal(want.MinSalePrice, got.MinSalePrice);
            Assert.Equal(want.MaxSalePrice, got.MaxSalePrice);
            Assert.Equal(want.MaxDiscountPercent, got.MaxDiscountPercent);
            Assert.Equal(want.HasDiscount, got.HasDiscount);
        }

        // And the floored product specifically, so the matrix cannot pass by both paths being wrong in
        // the same way about the one case with a hard boundary in it.
        var shirt = await ProductIdAsync("relaxed-linen-blend-shirt");
        Assert.Equal(CatalogPricing.MinimumSalePrice, actual[shirt].MinSalePrice);
    }

    /// <summary>
    /// Doc 11 P3 step 4, through the stored summary: a live campaign puts the Sale badge and the discount
    /// percentage on the product, and ending the campaign takes them off again.
    /// </summary>
    /// <remarks>
    /// Both directions, because the recompute that puts a product <i>back</i> is the one with no campaign
    /// to find and is therefore the one an implementation can forget: a statement that only ever wrote a
    /// discount would pass the first half and leave the shop advertising a sale that ended.
    /// </remarks>
    [Fact]
    public async Task A_campaign_raises_the_sale_badge_and_ending_it_lowers_it()
    {
        var shirt = await ProductIdAsync("relaxed-linen-blend-shirt");

        var before = await StoredSummaryAsync(shirt);
        Assert.Equal(5_490m, before.MinSalePrice);

        // The seeded shirt already carries a markdown (6,990.00 → 5,490.00), so it has a discount
        // percentage before any campaign — 21% — and the campaign has to raise it rather than create it.
        Assert.True(before.HasDiscount);
        Assert.Equal(21m, before.MaxDiscountPercent);

        var campaign = await NewCampaignAsync(
            SaleDiscountType.Percent,
            20m,
            [await OnProductAsync("relaxed-linen-blend-shirt")]);

        using var scope = CreateScope();
        var refresher = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();

        await refresher.RefreshProductsAsync([shirt], Token);

        var during = await StoredSummaryAsync(shirt);

        // 20% off 5,490.00 is 4,392.00, and floor((6,990 − 4,392) / 6,990 × 100) = 37 (doc 08 §6's own
        // "(shows −37%)").
        Assert.Equal(4_392m, during.MinSalePrice);
        Assert.Equal(37m, during.MaxDiscountPercent);
        Assert.True(during.HasDiscount);

        await EndCampaignAsync(campaign.Id, DateTime.UtcNow.AddMinutes(-1));
        await refresher.RefreshProductsAsync([shirt], Token);

        var after = await StoredSummaryAsync(shirt);

        Assert.Equal(5_490m, after.MinSalePrice);
        Assert.Equal(21m, after.MaxDiscountPercent);
    }

    /// <summary>
    /// Doc 08 §7.4's job window: a campaign that ended since the last pass is found by its <b>end</b>, and
    /// one that starts after the window is not.
    /// </summary>
    [Fact]
    public async Task The_job_pass_finds_the_boundaries_that_fell_inside_its_window()
    {
        var shirt = await ProductIdAsync("relaxed-linen-blend-shirt");
        var now = DateTime.UtcNow;

        // Ended two minutes ago, so its end is inside a ten-minute window.
        var ended = await NewCampaignAsync(
            SaleDiscountType.Percent,
            20m,
            [await OnProductAsync("relaxed-linen-blend-shirt")],
            startsAtUtc: now.AddHours(-3),
            endsAtUtc: now.AddMinutes(-2));

        // Starts in an hour, so neither of its bounds is inside it.
        await NewCampaignAsync(
            SaleDiscountType.Percent,
            50m,
            [await OnProductAsync("relaxed-linen-blend-shirt")],
            startsAtUtc: now.AddHours(1),
            endsAtUtc: now.AddHours(2));

        using var scope = CreateScope();
        var refresher = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();

        var pass = await refresher.RefreshBoundariesAsync(now.AddMinutes(-10), now, Token);

        // The ended campaign's product, and not the scheduled one's extra pass.
        Assert.Contains(shirt, (await refresher.ProductsTargetedByAsync([ended.Id], Token)));
        Assert.True(pass.Products > 0, "the window found no campaign");

        // Neither campaign is live, so the summary is back at the list price.
        Assert.Equal(5_490m, (await StoredSummaryAsync(shirt)).MinSalePrice);

        // A window with no boundary in it does nothing at all — not even one batch.
        var quiet = await refresher.RefreshBoundariesAsync(now.AddMinutes(5), now.AddMinutes(10), Token);

        Assert.Equal(new CampaignPricingRefreshPass(0, 0, 0), quiet);
    }

    /// <summary>
    /// Doc 08 §7.4: "batches of ≤ 1,000". Two thousand five hundred ids become three statements, and the
    /// batch size is the document's figure rather than whatever the list happened to be.
    /// </summary>
    /// <remarks>
    /// <b>The ids are mostly fictitious, and that is the cheap way to test the arithmetic.</b> A product
    /// id that does not exist joins to nothing, so the statement is a no-op for it — which means the batch
    /// count can be measured without seeding 2,500 products, and it also pins that an unknown id is
    /// harmless rather than an error. The real ids are in the set so the pass still writes something.
    /// </remarks>
    [Fact]
    public async Task A_set_larger_than_the_batch_size_is_split_into_batches_of_a_thousand()
    {
        var shirt = await ProductIdAsync("relaxed-linen-blend-shirt");

        // 2,500 ids: the shirt plus 2,499 that are not in the catalogue.
        var ids = new List<int> { shirt };
        ids.AddRange(Enumerable.Range(900_000, 2_499));

        using var scope = CreateScope();
        var refresher = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();

        var pass = await refresher.RefreshProductsAsync(ids, Token);

        Assert.Equal(2_500, pass.Products);
        Assert.Equal(3, pass.Batches);

        // One real product, so exactly one ProductStats row was written however many batches ran.
        Assert.Equal(1, pass.RowsWritten);
    }

    /// <summary>
    /// <b>What a pass that fails halfway leaves behind.</b> Refreshing the first half of an affected set
    /// and not the second is exactly the state a failure on a later batch produces, and it is a correct
    /// partial state rather than a corrupt one: the refreshed products are right, the rest are stale, and
    /// a second pass over the whole set converges with no repair step.
    /// </summary>
    /// <remarks>
    /// <b>The failure is simulated by splitting the call rather than by breaking the database.</b> Each
    /// batch is one atomic <c>UPDATE</c>, so "failed on batch two" and "was only asked for batch one" put
    /// the table in the same state — and the second is reachable without a fault injection point in
    /// production code, which is a thing this codebase does not have and should not grow for a test.
    /// <para>
    /// The second pass is also the idempotence assertion: it runs over products that are already correct
    /// as well as products that are stale, and writes the same values to the former.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_half_finished_pass_leaves_correct_rows_and_stale_rows_and_the_next_pass_converges()
    {
        await NewCampaignAsync(SaleDiscountType.Percent, 20m, [await OnDepartmentAsync("women")]);

        using var scope = CreateScope();
        var refresher = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();

        var affected = (await refresher.ProductsTargetedByAsync(
            [.. (await LiveCampaignIdsAsync())],
            Token)).Order().ToList();

        Assert.True(affected.Count >= 4, $"the Women department has only {affected.Count} products");

        var half = affected.Count / 2;
        var first = affected.Take(half).ToList();
        var rest = affected.Skip(half).ToList();

        await refresher.RefreshProductsAsync(first, Token);

        var expected = await ExpectedSummariesAsync(affected);
        var partial = await StoredSummariesAsync(affected);

        // The half that ran is right.
        foreach (var productId in first)
        {
            Assert.Equal(expected[productId].MinSalePrice, partial[productId].MinSalePrice);
        }

        // And at least one of the half that did not is stale — not corrupt, just the pre-campaign figure.
        Assert.Contains(rest, productId => partial[productId].MinSalePrice != expected[productId].MinSalePrice);

        // The next pass, over everything including the rows that are already correct.
        await refresher.RefreshProductsAsync(affected, Token);

        var converged = await StoredSummariesAsync(affected);

        foreach (var productId in affected)
        {
            Assert.Equal(expected[productId].MinSalePrice, converged[productId].MinSalePrice);
            Assert.Equal(expected[productId].MaxDiscountPercent, converged[productId].MaxDiscountPercent);
        }
    }

    /// <summary>
    /// <b>Two passes at once write the same values.</b> This is the whole argument for the job having no
    /// lease, and <c>ops.JobLeases</c> does not exist.
    /// </summary>
    /// <remarks>
    /// Both passes compute from the same source rows, so the second cannot write anything the first did
    /// not. What this can catch is the failure mode that would make a lease necessary — a statement that
    /// read a stored value and adjusted it, where two writers would double or cancel the adjustment — and
    /// a deadlock between two identical set-based statements, which would surface as a thrown exception
    /// rather than an assertion.
    /// </remarks>
    [Fact]
    public async Task Two_overlapping_passes_write_the_same_values()
    {
        await NewCampaignAsync(SaleDiscountType.Percent, 25m, [await OnDepartmentAsync("women")]);

        using var scopeA = CreateScope();
        using var scopeB = CreateScope();
        var a = scopeA.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();
        var b = scopeB.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();

        var affected = await a.ProductsTargetedByAsync([.. (await LiveCampaignIdsAsync())], Token);

        await Task.WhenAll(
            a.RefreshProductsAsync(affected, Token),
            b.RefreshProductsAsync(affected, Token));

        var expected = await ExpectedSummariesAsync(affected);
        var actual = await StoredSummariesAsync(affected);

        foreach (var (productId, want) in expected)
        {
            Assert.Equal(want.MinSalePrice, actual[productId].MinSalePrice);
            Assert.Equal(want.MaxDiscountPercent, actual[productId].MaxDiscountPercent);
        }
    }

    /// <summary>
    /// <b>A pass that races an edit converges on the edit.</b> A refresh computed while the campaign said
    /// 20%, then the campaign changed to 40%, then the refresh the edit's own save path owes — and the
    /// stored summary is the 40% one.
    /// </summary>
    /// <remarks>
    /// <b>This is the race stated as the property that matters rather than as a timing test.</b> The two
    /// orders a real race can produce are "job reads old, save writes new" and "job reads new, save
    /// writes new"; neither statement is an increment, so both end at the campaign's current state. The
    /// test performs the first — the only one that could go wrong — and asserts the end state. A test
    /// that tried to interleave the two statements would be measuring the scheduler.
    /// </remarks>
    [Fact]
    public async Task A_pass_that_races_an_edit_converges_on_the_edited_campaign()
    {
        var shirt = await ProductIdAsync("relaxed-linen-blend-shirt");

        var campaign = await NewCampaignAsync(
            SaleDiscountType.Percent,
            20m,
            [await OnProductAsync("relaxed-linen-blend-shirt")]);

        using var scope = CreateScope();
        var refresher = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();

        await refresher.RefreshProductsAsync([shirt], Token);
        Assert.Equal(4_392m, (await StoredSummaryAsync(shirt)).MinSalePrice);

        // The edit a staff member makes while that pass is in flight.
        using (var editScope = CreateScope())
        {
            var dbContext = editScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var edited = await dbContext.SaleCampaigns.SingleAsync(c => c.Id == campaign.Id, Token);
            edited.DiscountValue = 40m;
            await dbContext.SaveChangesAsync(Token);
        }

        // The save path's own recompute (doc 11 P3 step 3), which is what the admin use case owes.
        await refresher.RefreshProductsAsync([shirt], Token);

        // 40% off 5,490.00 is 3,294.00.
        Assert.Equal(3_294m, (await StoredSummaryAsync(shirt)).MinSalePrice);
    }

    /// <summary>
    /// The summary this test expects, computed the way the <b>bag</b> computes a price: the live campaign
    /// reader, <see cref="CampaignPricing.Best"/> per variant, then doc 08 §6 step 4's aggregate.
    /// </summary>
    private async Task<Dictionary<int, Summary>> ExpectedSummariesAsync(IReadOnlyCollection<int> productIds)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var offers = await scope.ServiceProvider
            .GetRequiredService<ICampaignOfferReader>()
            .ReadAsync(productIds, Token);

        var products = await dbContext.Products
            .AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .Select(product => new
            {
                product.Id,
                product.BasePrice,
                product.CompareAtPrice,
                Variants = product.Variants
                    .Where(variant => variant.IsActive)
                    .Select(variant => new VariantPrices(
                        variant.PriceOverride, variant.CompareAtPriceOverride))
                    .ToList(),
            })
            .ToListAsync(Token);

        var expected = new Dictionary<int, Summary>();

        foreach (var product in products)
        {
            var sales = new List<decimal>();
            var discounts = new List<decimal>();

            // The same fallback `ProductDerivedData.RefreshStats` and the T-SQL both apply: a product with
            // no active variant is priced from its own base price.
            List<VariantPrices> variants = product.Variants.Count > 0
                ? product.Variants
                : [new VariantPrices(null, null)];

            foreach (var variant in variants)
            {
                var list = CatalogPricing.ListPrice(product.BasePrice, variant.PriceOverride);
                var original = CatalogPricing.OriginalPrice(
                    list, product.CompareAtPrice, variant.CompareAtPriceOverride);
                var sale = CatalogPricing.SalePrice(list, offers.Best(product.Id, list)?.Price);

                sales.Add(sale);
                discounts.Add(CatalogPricing.DiscountPercent(original, sale));
            }

            var maxDiscount = discounts.Max();

            expected[product.Id] = new Summary(sales.Min(), sales.Max(), maxDiscount, maxDiscount > 0m);
        }

        return expected;
    }

    private async Task<Dictionary<int, Summary>> StoredSummariesAsync(IReadOnlyCollection<int> productIds)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.ProductStats
            .AsNoTracking()
            .Where(stats => productIds.Contains(stats.ProductId))
            .ToDictionaryAsync(
                stats => stats.ProductId,
                stats => new Summary(
                    stats.MinSalePrice,
                    stats.MaxSalePrice,
                    stats.MaxDiscountPercent,
                    stats.HasDiscount),
                Token);
    }

    /// <summary>Every undeleted product in the catalogue.</summary>
    private async Task<List<int>> AllProductIdsAsync()
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Products
            .AsNoTracking()
            .Select(product => product.Id)
            .ToListAsync(Token);
    }

    /// <summary>The campaigns this test arranged that are live now, by their own rows.</summary>
    private async Task<List<int>> LiveCampaignIdsAsync()
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTime.UtcNow;

        return await dbContext.SaleCampaigns
            .AsNoTracking()
            .Where(campaign =>
                campaign.IsActive && campaign.StartsAtUtc <= now && campaign.EndsAtUtc > now)
            .Select(campaign => campaign.Id)
            .ToListAsync(Token);
    }

    private readonly record struct VariantPrices(
        decimal? PriceOverride,
        decimal? CompareAtPriceOverride);

    private readonly record struct Summary(
        decimal MinSalePrice,
        decimal MaxSalePrice,
        decimal MaxDiscountPercent,
        bool HasDiscount);
}
