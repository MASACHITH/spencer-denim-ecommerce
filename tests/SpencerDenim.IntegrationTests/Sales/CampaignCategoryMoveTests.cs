using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Abstractions.Auditing;
using SpencerDenim.Application.Abstractions.Results;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Catalog.Admin;
using SpencerDenim.Application.Catalog.Admin.Abstractions;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Marketing;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// W3-05c's third item: <b>a category move changes what a live campaign reaches, and the stored price
/// summary has to be put right in the same request.</b>
/// <para>
/// A campaign targeting a category with <c>IncludeSubcategories</c> reaches products through
/// <c>Categories.Path</c>, and <c>UpdateCategoryCommandHandler</c> rebases <c>Path</c> for the whole
/// subtree it moves. So the <i>live</i> price — the bag, the checkout quote, the placement — changes the
/// instant the move commits, while <c>catalog.ProductStats</c> keeps the old figure. Before W3-05c
/// nothing corrected it: <c>RefreshCampaignPricing</c> looks only at campaigns whose <b>boundary</b> has
/// passed, and a category move moves no boundary, so the wrong price would have stood on every card,
/// listing and Sale page until the campaign ended.
/// </para>
/// <para>
/// <b>Both directions, and moving out is the worse one.</b> Moving a category <i>in</i> leaves a card
/// quoting the list price while the bag charges less — the shop loses nothing and a shopper gets a
/// surprise in their favour. Moving a category <i>out</i> leaves the card advertising a discount the bag
/// refuses, which is the disagreement that costs a sale.
/// </para>
/// </summary>
/// <remarks>
/// <b>The arrangement touches exactly two rows it does not own, and both are restored unconditionally.</b>
/// The scratch parent category is this class's own and is soft-deleted at the end; <c>shirts-blouses</c> is
/// seeded, and its four moved columns are read before anything happens and written back in a
/// <c>finally</c> with one <c>ExecuteUpdate</c> that cannot fail a constraint. <c>SortOrder</c> is part of
/// that restore on purpose: a move joins the end of its new sibling list, so the handler would otherwise
/// leave Shirts &amp; Blouses last in the Women menu for every suite that runs afterwards — the shape of
/// cross-suite damage this file's base class already paid for once.
/// </remarks>
public sealed class CampaignCategoryMoveTests(SqlServerDatabaseFixture sqlServer)
    : CheckoutCampaignTestBase(sqlServer)
{
    /// <summary>
    /// The seeded shirt's price without any campaign: doc 08 §6's own worked example — Rs. 6,990.00
    /// original, marked down to Rs. 5,490.00, which is <c>floor((6990 − 5490) / 6990 × 100) = 21</c>.
    /// </summary>
    private const decimal MarkdownPrice = 5_490m;

    private const decimal MarkdownPercent = 21m;

    /// <summary>20% off Rs. 5,490.00, which doc 08 §6 step 3 prints as Rs. 4,392.00 (−37%).</summary>
    private const decimal CampaignPrice = 4_392m;

    private const decimal CampaignPercent = 37m;

    [Fact]
    public async Task Moving_a_category_into_and_out_of_a_campaigns_subtree_keeps_the_stored_price_right()
    {
        SqlServer.SkipIfUnavailable();

        var shirt = await ProductIdAsync("relaxed-linen-blend-shirt");
        var shirts = await CategoryIdAsync("women", "shirts-blouses");
        var original = await CategoryPositionAsync(shirts);

        Assert.Null(original.ParentCategoryId);

        var scratchId = await NewScratchCategoryAsync();

        try
        {
            // A live campaign on the scratch category and everything under it. It reaches nothing yet,
            // which is what makes the first assertion below a measurement rather than a coincidence.
            await NewCampaignAsync(
                SaleDiscountType.Percent,
                20m,
                [new SaleCampaignTarget
                {
                    TargetType = SaleCampaignTargetType.Category,
                    CategoryId = scratchId,
                    IncludeSubcategories = true,
                }]);

            var before = await StoredSummaryAsync(shirt);
            Assert.Equal(MarkdownPrice, before.MinSalePrice);
            Assert.Equal(MarkdownPercent, before.MaxDiscountPercent);

            // ---- in ---------------------------------------------------------------------------------
            // Through the production handler, with nothing in this test touching the refresher: the
            // whole point is that the handler owes the recompute.
            Assert.IsType<CatalogWriteResult.Succeeded>(await MoveAsync(shirts, scratchId));

            var moved = await StoredSummaryAsync(shirt);
            Assert.Equal(CampaignPrice, moved.MinSalePrice);
            Assert.Equal(CampaignPercent, moved.MaxDiscountPercent);
            Assert.True(moved.HasDiscount);

            // ---- and out ----------------------------------------------------------------------------
            Assert.IsType<CatalogWriteResult.Succeeded>(await MoveAsync(shirts, null));

            var back = await StoredSummaryAsync(shirt);
            Assert.Equal(MarkdownPrice, back.MinSalePrice);
            Assert.Equal(MarkdownPercent, back.MaxDiscountPercent);
        }
        finally
        {
            // Ordered by how much damage skipping it does, each step in its own try/finally, which is the
            // rule this file's base class states at length. The category's position first: a category left
            // nested changes its storefront URL and its place in the Women menu for every suite that
            // follows, while a stale summary misprices one product's card — and the base class's own
            // teardown puts that summary back after this, with the campaign already gone.
            try
            {
                await RestoreCategoryPositionAsync(original);
            }
            finally
            {
                await SoftDeleteCategoryAsync(scratchId);
            }
        }
    }

    /// <summary>
    /// The new reader behind the fix, against the real database: which products a set of categories holds.
    /// </summary>
    /// <remarks>
    /// Separate from the test above because it proves a different thing: that the query translates and
    /// answers correctly, which no fake store can show. The negative half is the load-bearing one — a
    /// predicate that ignored its argument would pass the positive half and return the whole catalogue.
    /// </remarks>
    [Fact]
    public async Task The_products_under_a_category_are_exactly_the_ones_linked_to_it()
    {
        SqlServer.SkipIfUnavailable();

        var shirts = await CategoryIdAsync("women", "shirts-blouses");
        var shirt = await ProductIdAsync("relaxed-linen-blend-shirt");
        var jeans = await ProductIdAsync("high-rise-slim-fit-jeans");

        using var scope = CreateScope();
        var refresher = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();

        var products = await refresher.ProductsLinkedToCategoriesAsync([shirts], Token);

        Assert.Contains(shirt, products);
        Assert.DoesNotContain(jeans, products);

        // And an empty argument is an empty answer rather than every product in the shop, which is what a
        // missing guard would make of it.
        Assert.Empty(await refresher.ProductsLinkedToCategoriesAsync([], Token));
    }

    /// <summary>
    /// <b>A move re-paths the whole subtree, so the products under the moved category's children are
    /// repriced too</b>, not only those filed directly under it. Issue #191's nested fixture, because the
    /// seeded catalogue is flat and could not tell "the subtree" from "the category alone" (#158's first
    /// surviving mutant).
    /// </summary>
    /// <remarks>
    /// Three scratch categories: <c>target</c>, which the campaign covers with its subtree; <c>parent</c>;
    /// and <c>child</c> under <c>parent</c>. The shirt is linked to <c>child</c> <b>only</b>, as an extra
    /// category beside its own. Moving <c>parent</c> under <c>target</c> puts the shirt inside the
    /// campaign's reach two levels down, and nothing links the shirt to <c>parent</c> itself, so a handler
    /// that handed the refresher the moved category's own id rather than the subtree's would leave the
    /// shirt's stored price at Rs. 5,490.00 while the bag charged Rs. 4,392.00. Then back out again.
    /// </remarks>
    [Fact]
    public async Task Moving_a_parent_reprices_the_products_in_its_child_categories()
    {
        SqlServer.SkipIfUnavailable();

        var shirt = await ProductIdAsync("relaxed-linen-blend-shirt");

        var targetId = await NewScratchCategoryAsync();
        var parentId = await NewScratchCategoryAsync();
        var childId = await NewScratchCategoryAsync(parentId);

        try
        {
            await LinkAsync(shirt, childId);

            await NewCampaignAsync(
                SaleDiscountType.Percent,
                20m,
                [new SaleCampaignTarget
                {
                    TargetType = SaleCampaignTargetType.Category,
                    CategoryId = targetId,
                    IncludeSubcategories = true,
                }]);

            Assert.Equal(MarkdownPrice, (await StoredSummaryAsync(shirt)).MinSalePrice);

            // ---- the parent in: the shirt is two levels below the target now ------------------------
            Assert.IsType<CatalogWriteResult.Succeeded>(await MoveAsync(parentId, targetId));

            var moved = await StoredSummaryAsync(shirt);
            Assert.Equal(CampaignPrice, moved.MinSalePrice);
            Assert.Equal(CampaignPercent, moved.MaxDiscountPercent);

            // ---- and out ----------------------------------------------------------------------------
            Assert.IsType<CatalogWriteResult.Succeeded>(await MoveAsync(parentId, null));

            var back = await StoredSummaryAsync(shirt);
            Assert.Equal(MarkdownPrice, back.MinSalePrice);
            Assert.Equal(MarkdownPercent, back.MaxDiscountPercent);
        }
        finally
        {
            try
            {
                await UnlinkAsync(shirt, childId);
            }
            finally
            {
                await SoftDeleteCategoryAsync(childId);
                await SoftDeleteCategoryAsync(parentId);
                await SoftDeleteCategoryAsync(targetId);

                // Recomputed from committed rows whatever happened above, so a failure mid-test cannot
                // leave the shirt mispriced for the suites that run afterwards.
                await RefreshAsync(shirt);
            }
        }
    }

    /// <summary>
    /// <b>No categories, no round trip.</b> #158's second surviving mutant: deleting the empty-set guard
    /// left the answer right, because <c>Contains</c> over an empty list matches no row, and ran a
    /// statement for nothing. Counted rather than asserted empty, the way <c>AdminAlertsBehaviourTests</c>
    /// pins its own empty-audience guard.
    /// </summary>
    [Fact]
    public async Task Asking_for_the_products_under_no_categories_runs_no_statement()
    {
        SqlServer.SkipIfUnavailable();

        var counter = new CommandCountingInterceptor();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder => builder
            .UseSqlServer(SqlServer.ConnectionString)
            .AddInterceptors(counter));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var refresher = new CampaignPricingRefresher(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            new SystemClockForTests());

        counter.Reset();

        Assert.Empty(await refresher.ProductsLinkedToCategoriesAsync([], Token));
        Assert.Equal(0, counter.Count);

        // And the counter does see this method's statement when there is something to ask, so the zero
        // above is a measurement and not a counter that was never attached.
        await refresher.ProductsLinkedToCategoriesAsync([int.MaxValue], Token);
        Assert.Equal(1, counter.Count);
    }

    /// <summary>Links a product to one more category, the way a staff member files it in two places.</summary>
    private async Task LinkAsync(int productId, int categoryId)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        dbContext.ProductCategories.Add(new ProductCategory { ProductId = productId, CategoryId = categoryId });
        await dbContext.SaveChangesAsync(Token);
    }

    /// <summary>Removes that link: a pure child row, so a set-based hard delete (doc 08 §1.6, N12).</summary>
    private async Task UnlinkAsync(int productId, int categoryId)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.ProductCategories
            .Where(link => link.ProductId == productId && link.CategoryId == categoryId)
            .ExecuteDeleteAsync(CancellationToken.None);
    }

    private async Task RefreshAsync(int productId)
    {
        using var scope = SqlServer.CreateScope();

        await scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>()
            .RefreshProductsAsync([productId], CancellationToken.None);
    }

    private sealed class SystemClockForTests : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>The id of the seeded category with this slug, qualified by department.</summary>
    /// <remarks>
    /// Qualified because <c>UX_Categories_Slug</c> is scoped to the department (doc 08 §3.2) and the
    /// seeded catalogue has <c>jeans</c> under both Women and Men — the same reason
    /// <see cref="CheckoutCampaignTestBase.OnCategoryAsync"/> gives.
    /// </remarks>
    private async Task<int> CategoryIdAsync(string departmentSlug, string slug)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.Categories
            .AsNoTracking()
            .Where(category => category.Slug == slug && category.Department!.Slug == departmentSlug)
            .Select(category => category.Id)
            .SingleAsync(Token);
    }

    /// <summary>
    /// A category under Women with nothing in it, to be the campaign's target.
    /// </summary>
    /// <remarks>
    /// Inactive and off the mega menu, which is what <c>AdminSaleCampaignEndpointTests</c>'
    /// equivalent does and for the same reason: it exists for two assertions, and no shopper should be
    /// offered an empty category even for the seconds this test runs. Neither flag affects the campaign —
    /// no campaign predicate reads a category's <c>IsActive</c>.
    /// </remarks>
    private async Task<int> NewScratchCategoryAsync(int? parentCategoryId = null)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var departmentId = await dbContext.Departments
            .Where(department => department.Slug == "women")
            .Select(department => department.Id)
            .SingleAsync(Token);

        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A child of another scratch category when asked for one (#191's nested fixture), so the move
        // below re-paths a real subtree rather than a single row.
        var parentPath = parentCategoryId is { } parentId
            ? await dbContext.Categories
                .Where(candidate => candidate.Id == parentId)
                .Select(candidate => candidate.Path)
                .SingleAsync(Token)
            : null;

        var category = new Category
        {
            DepartmentId = departmentId,
            ParentCategoryId = parentCategoryId,
            Name = "Suite move " + suffix,
            Slug = "suite-move-" + suffix,
            Depth = (byte)(parentPath is null ? 1 : 2),
            SortOrder = 9_100,
            ShowInMegaMenu = false,
            IsActive = false,
        };

        dbContext.Categories.Add(category);
        await dbContext.SaveChangesAsync(Token);

        category.Path = CategoryPath.Build(parentPath, category.Id);
        await dbContext.SaveChangesAsync(Token);

        return category.Id;
    }

    /// <summary>
    /// Reparents a category through the production handler, leaving every other field as it is.
    /// </summary>
    /// <remarks>
    /// <b>Constructed from resolved dependencies rather than resolved itself</b>, because the fixture's
    /// container holds the Infrastructure registrations and not the Application ones — <c>CreateScope</c>
    /// is built by <c>AddInfrastructure()</c> alone. Nothing is lost: the handler's own DI registration is
    /// exercised by <c>AdminCatalogEndpointTests</c>, which edits a category through the real endpoint, so
    /// a dependency the container could not supply reddens there.
    /// </remarks>
    private async Task<CatalogWriteResult> MoveAsync(int categoryId, int? newParentCategoryId)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var handler = new UpdateCategoryCommandHandler(
            scope.ServiceProvider.GetRequiredService<ICatalogStructureStore>(),
            scope.ServiceProvider.GetRequiredService<IAuditLogWriter>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>());

        var category = await dbContext.Categories
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == categoryId, Token);

        return await handler.HandleAsync(
            new UpdateCategoryCommand(
                category.Id,
                newParentCategoryId,
                category.Name,
                category.Slug,
                category.Description,
                category.ImageAssetId,
                category.BannerAssetId,
                category.BannerLinkUrl,
                category.AgeGroupId,
                category.SizeGuideId,
                category.ShowInMegaMenu,
                category.MegaMenuColumn,
                category.SeoTitle,
                category.SeoDescription,
                Convert.ToBase64String(category.RowVersion!),
                AdminActor.Unknown),
            Token);
    }

    private async Task<CategoryPosition> CategoryPositionAsync(int categoryId)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var row = await dbContext.Categories
            .AsNoTracking()
            .Where(category => category.Id == categoryId)
            .Select(category => new CategoryPosition(
                category.Id,
                category.ParentCategoryId,
                category.Path,
                category.Depth,
                category.SortOrder))
            .SingleAsync(Token);

        return row;
    }

    /// <summary>
    /// Puts the seeded category back exactly where it was, with one statement that cannot fail a
    /// constraint.
    /// </summary>
    /// <remarks>
    /// <b>Not through the handler.</b> A second move would reach the end of the sibling list again and
    /// could itself be refused — a stale row version, a slug race — and a teardown that can be refused is
    /// not a teardown. An <c>ExecuteUpdate</c> of the four columns the move rewrote writes exactly the
    /// seeded values back; the category has no children in the seeded catalogue, so there is no subtree
    /// to re-path with it, and that is asserted rather than assumed.
    /// </remarks>
    private async Task RestoreCategoryPositionAsync(CategoryPosition original)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var children = await dbContext.Categories
            .IgnoreQueryFilters()
            .CountAsync(category => category.ParentCategoryId == original.Id, CancellationToken.None);

        Assert.Equal(0, children);

        await dbContext.Categories
            .Where(category => category.Id == original.Id)
            .ExecuteUpdateAsync(
                category => category
                    .SetProperty(row => row.ParentCategoryId, original.ParentCategoryId)
                    .SetProperty(row => row.Path, original.Path)
                    .SetProperty(row => row.Depth, original.Depth)
                    .SetProperty(row => row.SortOrder, original.SortOrder),
                CancellationToken.None);
    }

    /// <summary>Soft-deletes the scratch category, as doc 08 §10.2 DB5 requires.</summary>
    private async Task SoftDeleteCategoryAsync(int categoryId)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var category = await dbContext.Categories.SingleAsync(
            candidate => candidate.Id == categoryId,
            CancellationToken.None);

        category.SoftDelete(DateTime.UtcNow, userId: null);

        await dbContext.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The four columns a move rewrites.</summary>
    private readonly record struct CategoryPosition(
        int Id,
        int? ParentCategoryId,
        string Path,
        byte Depth,
        int SortOrder);
}
