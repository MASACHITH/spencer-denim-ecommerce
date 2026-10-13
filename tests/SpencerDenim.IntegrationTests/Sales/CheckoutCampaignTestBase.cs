using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Arranging sale campaigns for the checkout suites (ADM-14).
/// <para>
/// <b>Every campaign is created by the test that uses it and none is seeded.</b> A seeded campaign would
/// be a discount the client never agreed to, which is why <c>DemoCatalogueSeeder</c> creates none — so a
/// suite relying on one would be relying on a row the shop's own migration deliberately does not write.
/// </para>
/// </summary>
/// <remarks>
/// <b>It extends <see cref="CheckoutPromoTestBase"/></b> because the one place campaigns and promo codes
/// meet is doc 08 §6 step 7's <c>ExcludeSaleItems</c>, and testing that needs both arranged at once —
/// against the same bag, through the same placement.
/// </remarks>
public abstract class CheckoutCampaignTestBase(SqlServerDatabaseFixture sqlServer)
    : CheckoutPromoTestBase(sqlServer)
{
    /// <summary>
    /// Every campaign this test created, so <see cref="DisposeAsync"/> can end it.
    /// </summary>
    /// <remarks>
    /// <b>A campaign is global state in a way a promo code is not, and the first version of this suite
    /// proved it the hard way.</b> A code only discounts the order that types it; a campaign silently
    /// reprices every product it targets for every test that runs afterwards against this shared
    /// database. The Rs. 99,000-off campaign that <c>A_fixed_campaign_larger_than_the_price_charges_one_
    /// rupee</c> arranges on the shirt left seven of eleven tests in this class asserting Rs. 1.00 — and
    /// it would have reached <c>CheckoutPromoBehaviourTests</c> and the catalogue suites too, where the
    /// failures would have looked like nothing to do with campaigns at all.
    /// <para>
    /// Removed rather than deactivated: a soft delete goes through the domain method doc 08 §10.2 DB5
    /// requires, takes the row out of the global query filter's reach for good, and leaves the targets in
    /// place so a cascade is never exercised by a test teardown.
    /// </para>
    /// </remarks>
    private readonly List<int> _campaignIds = [];

    /// <summary>
    /// Every variant whose stock this test arranged, so <see cref="DisposeAsync"/> can give back the
    /// reservations its orders are holding.
    /// </summary>
    /// <remarks>
    /// <b>This exists because the first version of this suite reddened four of
    /// <c>CartStaleItemTests</c>' twelve tests in a whole-assembly run, and the mechanism is the one
    /// <c>CheckoutTestBase</c>'s own remarks describe.</b> Those tests set <c>OnHand</c> to 0 or 1 on
    /// variants taken from the <i>low</i> end of the catalogue, and this suite has to use the <i>linen
    /// shirt</i> and the <i>jeans</i> — doc 08 §6's worked example is about those two products
    /// specifically — which are <c>SD-W-0001</c> and <c>SD-W-0009</c>, at the low end. A placed order
    /// holds its reservation for ever, so <c>Reserved</c> stayed above zero and
    /// <c>CK_StockLevels_ReservedLeOnHand</c> refused their <c>UPDATE</c>: "The UPDATE statement
    /// conflicted with the CHECK constraint". Twelve of twelve on its own, four red together, reproduced
    /// with the two classes alone in sixteen seconds.
    /// <para>
    /// <b>The fix is to give the stock back, not to restore a number.</b> <c>CheckoutTestBase</c>'s
    /// restore deliberately puts <c>OnHand</c> back as <c>original + held</c> so that <c>Available</c> is
    /// right, which is correct for a suite whose orders stand — but it leaves <c>Reserved</c> above zero,
    /// and that is what the other suite trips on. Cancelling each order releases the reservation through
    /// the real <c>IOrderCancellationStore</c>, so <c>Reserved</c> returns to what it was and the base's
    /// restore then puts <c>OnHand</c> back to exactly the seeded figure.
    /// </para>
    /// <para>
    /// The promo suites do not need this because <c>CheckoutVariantIdsAsync</c> hands out the
    /// <i>highest</i> ids for exactly this reason. A campaign suite cannot use it: it needs named
    /// products.
    /// </para>
    /// </remarks>
    private readonly HashSet<int> _arrangedVariantIds = [];

    /// <summary>
    /// A new <c>marketing.SaleCampaigns</c> row with its targets, live by default.
    /// </summary>
    /// <param name="discountType">Percent (≤ 90) or FixedAmount.</param>
    /// <param name="discountValue">The percentage or the LKR amount.</param>
    /// <param name="targets">
    /// What it applies to, built with <see cref="OnDepartmentAsync"/>, <see cref="OnCategoryAsync"/> or
    /// <see cref="OnProductAsync"/>. At least one, because a campaign with no target prices nothing and
    /// a test that arranged one by accident would be asserting the absence of a discount for the wrong
    /// reason.
    /// </param>
    /// <param name="startsAtUtc">Defaults to an hour ago, so the campaign is live.</param>
    /// <param name="endsAtUtc">Defaults to an hour hence.</param>
    /// <param name="isActive">False to arrange a campaign staff have switched off.</param>
    protected async Task<SaleCampaign> NewCampaignAsync(
        SaleDiscountType discountType,
        decimal discountValue,
        IReadOnlyCollection<SaleCampaignTarget> targets,
        DateTime? startsAtUtc = null,
        DateTime? endsAtUtc = null,
        bool isActive = true)
    {
        ArgumentNullException.ThrowIfNull(targets);
        Assert.NotEmpty(targets);

        SqlServer.SkipIfUnavailable();

        var now = DateTime.UtcNow;

        var campaign = new SaleCampaign
        {
            // 38 characters, inside doc 08 §3.6's nvarchar(100), and unique so two tests' campaigns are
            // never confused in a failure message.
            Name = $"Suite {Guid.NewGuid():N}",
            Description = "Arranged by the integration suite.",
            DiscountType = discountType,
            DiscountValue = discountValue,
            StartsAtUtc = startsAtUtc ?? now.AddHours(-1),
            EndsAtUtc = endsAtUtc ?? now.AddHours(1),
            IsActive = isActive,
        };

        foreach (var target in targets)
        {
            campaign.Targets.Add(target);
        }

        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        dbContext.SaleCampaigns.Add(campaign);
        await dbContext.SaveChangesAsync(Token);

        _campaignIds.Add(campaign.Id);

        return campaign;
    }

    /// <summary>Targets a whole department by slug (<c>women</c>, <c>men</c>, …).</summary>
    protected async Task<SaleCampaignTarget> OnDepartmentAsync(string slug) =>
        new()
        {
            TargetType = SaleCampaignTargetType.Department,
            DepartmentId = await IdOfAsync(dbContext => dbContext.Departments
                .Where(department => department.Slug == slug)
                .Select(department => department.Id)),
        };

    /// <summary>
    /// Targets a category, with or without its subtree (doc 08 §3.6).
    /// </summary>
    /// <remarks>
    /// <b>Qualified by department, because a category slug is not unique.</b> Doc 08 §3.2 scopes
    /// <c>UX_Categories_Slug</c> to the department, and the seeded catalogue has <c>jeans</c> under both
    /// Women and Men — so a lookup by slug alone would throw on some categories and silently pick a
    /// department's worth of the wrong products on others.
    /// </remarks>
    protected async Task<SaleCampaignTarget> OnCategoryAsync(
        string departmentSlug,
        string slug,
        bool includeSubcategories = true) =>
        new()
        {
            TargetType = SaleCampaignTargetType.Category,
            CategoryId = await IdOfAsync(dbContext => dbContext.Categories
                .Where(category => category.Slug == slug && category.Department!.Slug == departmentSlug)
                .Select(category => category.Id)),
            IncludeSubcategories = includeSubcategories,
        };

    /// <summary>Targets one product by slug.</summary>
    protected async Task<SaleCampaignTarget> OnProductAsync(string slug) =>
        new()
        {
            TargetType = SaleCampaignTargetType.Product,
            ProductId = await ProductIdAsync(slug),
        };

    /// <summary>The id of the seeded product with this slug.</summary>
    protected async Task<int> ProductIdAsync(string slug) =>
        await IdOfAsync(dbContext => dbContext.Products
            .Where(product => product.Slug == slug)
            .Select(product => product.Id));

    /// <summary>
    /// A variant of the product with this slug whose price is the product's own base price.
    /// </summary>
    /// <remarks>
    /// <b>The <c>PriceOverride is null</c> filter is the point.</b> <c>DemoCatalogueSeeder</c> gives XXL
    /// and XXXL an override of base + Rs. 300, so a test that took "a variant" would sometimes be
    /// computing a campaign against 5,790.00 and asserting a figure derived from 5,490.00 — and would
    /// pass or fail by which size the id order happened to reach first.
    /// </remarks>
    protected async Task<int> BaseVariantIdAsync(string productSlug)
    {
        SqlServer.SkipIfUnavailable();

        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant =>
                variant.Product!.Slug == productSlug
                && variant.IsActive
                && variant.PriceOverride == null
                && variant.CompareAtPriceOverride == null)
            .OrderBy(variant => variant.Id)
            .Select(variant => variant.Id)
            .FirstAsync(Token);
    }

    /// <summary>
    /// A fresh guest bag holding several lines, with the cart id and a checkout client sharing its
    /// cookie jar.
    /// </summary>
    /// <remarks>
    /// <see cref="CheckoutTestBase.NewBagAsync"/> adds one line, which is all every suite before this one
    /// needed. Doc 08 §6's worked example is a two-line bag whose lines are priced by different rules —
    /// one by a campaign and one not — so the campaign suite needs more than one.
    /// </remarks>
    protected async Task<(int CartId, CheckoutApiClient Api)> NewMultiLineBagAsync(
        params (int VariantId, int Quantity)[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Assert.NotEmpty(items);

        var cart = NewVisitor();
        Guid? token = null;

        foreach (var (variantId, quantity) in items)
        {
            var (response, _) = await cart.AddAsync(variantId, quantity, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Only the first Add sets the cookie; the rest travel on the jar it created.
            if (token is null)
            {
                var setCookie = response.Headers.GetValues("Set-Cookie")
                    .Single(value => value.StartsWith("sd_cart=", StringComparison.Ordinal));

                token = Guid.ParseExact(setCookie["sd_cart=".Length..].Split(';')[0], "D");
            }
        }

        using var scope = CreateScope();
        var cartId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Carts
            .AsNoTracking()
            .Where(candidate => candidate.Token == token!.Value)
            .Select(candidate => candidate.Id)
            .SingleAsync(Token);

        return (cartId, new CheckoutApiClient(cart.Http));
    }

    /// <summary>
    /// Sets a variant's stock <b>and remembers it</b>, so the reservations this test's orders take are
    /// given back in <see cref="DisposeAsync"/>. Use this rather than
    /// <see cref="CheckoutTestBase.SetStockAsync"/> anywhere in a campaign suite — see
    /// <see cref="_arrangedVariantIds"/> for what goes wrong otherwise.
    /// </summary>
    protected async Task ArrangeStockAsync(int variantId, int onHand)
    {
        _arrangedVariantIds.Add(variantId);

        await SetStockAsync(variantId, onHand);
    }

    /// <summary>
    /// Puts <c>inventory.StockLevels</c> back to the seeded figures for every variant this test arranged:
    /// <c>OnHand = OnHand − Reserved</c> and <c>Reserved = 0</c>, in one statement per variant.
    /// </summary>
    /// <remarks>
    /// <b>Run after <c>CheckoutTestBase.DisposeAsync</c>, whose restore this completes.</b> That restore
    /// sets <c>OnHand = original + held</c> and <c>Reserved = held</c>, which is right for a suite whose
    /// orders stand — <c>Available</c> comes back to the seeded value. It still leaves <c>Reserved</c>
    /// above zero, and that is what <c>CartStaleItemTests</c> trips on: it sets <c>OnHand</c> to 0 or 1 on
    /// variants from the low end of the catalogue, which is where the linen shirt and the jeans are, and
    /// <c>CK_StockLevels_ReservedLeOnHand</c> then refuses its <c>UPDATE</c>. Subtracting <c>Reserved</c>
    /// from <c>OnHand</c> and zeroing it yields exactly <c>(original, 0)</c>, and the constraint holds at
    /// every step.
    /// <para>
    /// <b>Not by cancelling the orders, which is what the first version of this did and why this remark is
    /// long.</b> Cancelling through <c>IOrderCancellationStore</c> looked more honest — a real release with
    /// its status change and history row — but the query could not tell this test's orders from any other
    /// order holding the same variant, so a test that placed nothing at all tried to cancel an order a
    /// previous test had placed and whose stock the base had already rebalanced.
    /// <c>OrderStockRelease</c> refused it, correctly ("the reservation made at placement is no longer
    /// there"), the exception came out of <c>DisposeAsync</c> <b>before the campaigns were removed</b>, and
    /// a Rs. 99,000-off campaign stayed live for the rest of the assembly: fifteen tests in four classes
    /// went red, several of them reading Rs. 1.00 where they expected a real price. Hence both changes
    /// here — this statement cannot throw, and the campaign removal below no longer sits behind anything
    /// that can.
    /// </para>
    /// <para>
    /// The orders keep a reservation the stock row no longer reflects. In a throwaway test database, for
    /// orders this suite placed itself, that is the right trade: the alternative cost four classes.
    /// </para>
    /// </remarks>
    private async Task RestoreArrangedStockAsync()
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var variantId in _arrangedVariantIds)
        {
            await dbContext.StockLevels
                .Where(level => level.VariantId == variantId)
                .ExecuteUpdateAsync(
                    level => level
                        // Both in one SET, so CK_StockLevels_ReservedLeOnHand sees them together.
                        .SetProperty(row => row.OnHand, row => row.OnHand - row.Reserved)
                        .SetProperty(row => row.Reserved, 0)
                        .SetProperty(row => row.UpdatedAtUtc, DateTime.UtcNow),
                    CancellationToken.None);
        }
    }

    /// <summary>Soft-deletes a campaign through the domain method, as doc 08 §10.2 DB5 requires.</summary>
    protected async Task RemoveCampaignAsync(int campaignId)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var campaign = await dbContext.SaleCampaigns.SingleAsync(c => c.Id == campaignId, Token);
        campaign.SoftDelete(DateTime.UtcNow, userId: null);

        await dbContext.SaveChangesAsync(Token);
    }

    /// <summary>Moves a campaign's end, which is doc 11 P3 step 5's first way to finish a sale early.</summary>
    protected async Task EndCampaignAsync(int campaignId, DateTime endsAtUtc)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var campaign = await dbContext.SaleCampaigns.SingleAsync(c => c.Id == campaignId, Token);
        campaign.EndsAtUtc = endsAtUtc;

        await dbContext.SaveChangesAsync(Token);
    }

    /// <summary>The stored price summary doc 08 §7.4 lets lag — read, never relied on for a charge.</summary>
    protected async Task<(decimal MinSalePrice, decimal MaxDiscountPercent, bool HasDiscount)> StoredSummaryAsync(
        int productId)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await dbContext.ProductStats
            .AsNoTracking()
            .Where(stats => stats.ProductId == productId)
            .Select(stats => new { stats.MinSalePrice, stats.MaxDiscountPercent, stats.HasDiscount })
            .SingleAsync(Token);

        return (row.MinSalePrice, row.MaxDiscountPercent, row.HasDiscount);
    }

    /// <summary>The <c>SaleCampaignId</c> each line of an order recorded, keyed by variant.</summary>
    protected async Task<IReadOnlyDictionary<int, int?>> LineCampaignsAsync(string orderNumber)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.OrderLines
            .AsNoTracking()
            .Where(line => line.Order!.OrderNumber == orderNumber)
            .ToDictionaryAsync(line => line.VariantId, line => line.SaleCampaignId, Token);
    }

    /// <summary>The order's lines, keyed by variant, for the columns a campaign writes.</summary>
    protected async Task<IReadOnlyDictionary<int, OrderLine>> LinesByVariantAsync(string orderNumber)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.OrderLines
            .AsNoTracking()
            .Where(line => line.Order!.OrderNumber == orderNumber)
            .ToDictionaryAsync(line => line.VariantId, Token);
    }

    /// <summary>
    /// Removes every campaign this test arranged, puts the affected products' stored summaries back, then
    /// hands over to the stock restore.
    /// </summary>
    /// <remarks>
    /// <b>The summary has to be put back as well as the campaign removed.</b> <c>catalog.ProductStats</c>
    /// is what the listing, Sale page and homepage suites assert prices from, so a test that recomputed a
    /// product's summary while a campaign was live and then only soft-deleted the campaign would leave a
    /// Rs. 4,392.00 and a Sale badge behind for <c>CatalogListingTests</c> to trip over — a failure with
    /// no visible connection to campaigns at all. The restore runs the production refresher with every
    /// campaign already gone, so it writes exactly what the seeder wrote.
    /// </remarks>
    public override async ValueTask DisposeAsync()
    {
        // **Every step in its own try/finally, and the campaigns first.** A campaign left live reprices
        // the catalogue for every class that runs afterwards, so it is the most dangerous thing here and
        // nothing that can throw may be allowed to come before it — which is exactly the mistake the
        // first version made, and it cost fifteen red tests in four classes.
        try
        {
            if (_campaignIds.Count > 0 && SqlServer.IsAvailable)
            {
                await RemoveArrangedCampaignsAsync();
            }
        }
        finally
        {
            _campaignIds.Clear();

            try
            {
                // The base disposes the host and restores `OnHand = original + held`.
                await base.DisposeAsync();
            }
            finally
            {
                if (_arrangedVariantIds.Count > 0 && SqlServer.IsAvailable)
                {
                    await RestoreArrangedStockAsync();
                }

                _arrangedVariantIds.Clear();
                GC.SuppressFinalize(this);
            }
        }
    }

    /// <summary>
    /// Soft-deletes the campaigns this test arranged and puts the affected products' stored summaries
    /// back.
    /// </summary>
    /// <remarks>
    /// <b>The summary has to go back as well as the campaign.</b> <c>catalog.ProductStats</c> is what the
    /// listing, Sale-page and homepage suites assert prices from, so a test that recomputed a product's
    /// summary while a campaign was live and then only soft-deleted the campaign would leave a
    /// Rs. 4,392.00 and a Sale badge behind for <c>CatalogListingTests</c> to trip over — a failure with
    /// no visible connection to campaigns at all. The restore runs the production refresher with every
    /// campaign already gone, so it writes exactly what the seeder wrote.
    /// <para>
    /// <b>The delete goes first, and the previous arrangement did not — which left the hole it was
    /// written to close.</b> This method used to read the affected set <i>before</i> the soft delete, on
    /// the stated grounds that "after it, the campaign's targets are still there but asking which products
    /// they reach is a question about a row the filter now hides". That is not true of the method it
    /// calls: <c>ProductsTargetedByAsync</c> calls <c>IgnoreQueryFilters()</c> precisely so a
    /// just-soft-deleted campaign's affected set is still answerable, and its own remarks say so. So the
    /// old order bought nothing and cost the one guarantee that matters: a failure in that first query —
    /// a timeout on a loaded box during a twenty-five-minute run is enough — came out of
    /// <c>DisposeAsync</c> with the campaign still live, which is exactly the Rs. 99,000-off failure this
    /// class already paid for once. <c>DisposeAsync</c> attempting the removal first is not sufficient on
    /// its own; the removal has to be the first thing <i>inside</i> it too.
    /// </para>
    /// <para>
    /// Each step therefore gets its own <c>try</c>/<c>finally</c>, in order of how much damage skipping it
    /// does: a campaign left live reprices the catalogue for every class that follows, while a stale
    /// summary misprices one product's card. Both are reported, because a teardown that swallows its own
    /// failure silently is how a suite comes to depend on one.
    /// </para>
    /// </remarks>
    private async Task RemoveArrangedCampaignsAsync()
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var refresher = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();

        try
        {
            var campaigns = await dbContext.SaleCampaigns
                .Where(campaign => _campaignIds.Contains(campaign.Id))
                .ToListAsync(CancellationToken.None);

            foreach (var campaign in campaigns)
            {
                campaign.SoftDelete(DateTime.UtcNow, userId: null);
            }

            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        finally
        {
            // Read after the delete, which `ProductsTargetedByAsync` supports by design. In its own
            // `finally` so that a failed delete is still followed by a best-effort restore rather than
            // leaving both halves undone.
            var affected = await refresher.ProductsTargetedByAsync(_campaignIds, CancellationToken.None);

            await refresher.RefreshProductsAsync(affected, CancellationToken.None);
        }
    }

    private async Task<int> IdOfAsync(Func<AppDbContext, IQueryable<int>> query)
    {
        SqlServer.SkipIfUnavailable();

        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await query(dbContext).SingleAsync(Token);
    }
}
