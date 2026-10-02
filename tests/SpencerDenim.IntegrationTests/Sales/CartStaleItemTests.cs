using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Sales.Cart.Contracts;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// What the bag does when the catalogue moves underneath it (CART-01).
/// <para>
/// A cart row holds a variant, nothing more. Between the add and the next look at the bag the price can
/// change, the product can be unpublished, its category can be deactivated — W1-12 shipped and the client
/// can do that today — and the stock can run out. Each of those has a right answer and a tempting wrong
/// one, and the wrong one is always the same shape: carry on using the figure that was stored at add time.
/// </para>
/// <para>
/// Every test here arranges the state with a direct write to the seeded catalogue and <b>undoes it in a
/// finally</b>, because the fixture's database is shared by the whole assembly.
/// </para>
/// </summary>
public sealed class CartStaleItemTests(SqlServerDatabaseFixture sqlServer) : CartTestBase(sqlServer)
{
    /// <summary>
    /// <b>A price that moved after the add.</b> The bag shows today's price, the line total is today's
    /// price × quantity, the subtotal is built from today's price — and the old figure travels only so the
    /// storefront can say it changed.
    /// </summary>
    [Fact]
    public async Task A_price_change_after_the_add_reprices_the_line_and_the_subtotal()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        var (_, before) = await api.AddAsync(variantId, 2, Token);
        Assert.NotNull(before);
        var priceAtAdd = Assert.Single(before.Lines).Price!.Current;
        Assert.False(Assert.Single(before.Lines).PriceChanged);

        var raised = priceAtAdd + 500m;
        await WithVariantPriceAsync(variantId, raised, async () =>
        {
            var (_, after) = await api.GetAsync(Token);
            Assert.NotNull(after);
            var line = Assert.Single(after.Lines);

            Assert.Equal(raised, line.Price!.Current);
            Assert.Equal(raised * 2, line.LineSubtotal);
            Assert.Equal(raised * 2, after.Totals.ItemsSubtotal);

            // The stored figure is reported, and is reported as history: it is what the storefront says
            // "the price has changed" from, and it is nowhere in any total.
            Assert.Equal(priceAtAdd, line.UnitPriceAtAdd);
            Assert.True(line.PriceChanged);
            Assert.NotEqual(line.UnitPriceAtAdd * 2, after.Totals.ItemsSubtotal);
        });
    }

    /// <summary>
    /// <b>A product that was unpublished.</b> The line stays visible — a bag that silently drops an item is
    /// worse than one that explains itself — but it is <see cref="CartLineStatus.Unavailable"/>, it carries
    /// no price at all, and it is out of the subtotal.
    /// </summary>
    [Fact]
    public async Task An_unpublished_product_stays_in_the_bag_unpriced_and_out_of_the_subtotal()
    {
        var api = NewVisitor();
        var ids = await InStockVariantIdsAsync(2);
        var (doomedVariantId, keptVariantId) = await TwoVariantsOfDifferentProductsAsync(ids);

        await api.AddAsync(doomedVariantId, 2, Token);
        var (_, before) = await api.AddAsync(keptVariantId, 1, Token);
        Assert.NotNull(before);
        var keptSubtotal = before.Lines.Single(line => line.VariantId == keptVariantId).LineSubtotal!.Value;

        await WithProductPublishedAsync(doomedVariantId, published: false, async () =>
        {
            var (response, cart) = await api.GetAsync(Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(cart);

            Assert.Equal(2, cart.Lines.Count);
            var doomed = cart.Lines.Single(line => line.VariantId == doomedVariantId);

            Assert.Equal(CartLineStatus.Unavailable, doomed.Status);
            Assert.Null(doomed.Price);
            Assert.Null(doomed.LineSubtotal);

            // It can still be identified and taken out: the name, the colour and the size are all there.
            Assert.NotEmpty(doomed.ProductName);
            Assert.NotEmpty(doomed.SizeLabel);
            Assert.NotEmpty(doomed.ColourName);

            // The subtotal is only what can be bought — and the header still counts what is in the bag.
            Assert.Equal(keptSubtotal, cart.Totals.ItemsSubtotal);
            Assert.Equal(3, cart.Totals.ItemCount);
            Assert.Equal(1, cart.Totals.BuyableItemCount);
        });
    }

    /// <summary>
    /// <b>A soft-deleted product.</b> The same answer as unpublished, and the one that is easiest to get
    /// catastrophically wrong: <c>Product</c> carries the soft-delete query filter, so a reader that left
    /// the filter on would drop the line out of the join and the item would vanish from the bag with no
    /// message at all.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_product_is_still_shown_in_the_bag_rather_than_vanishing()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        await api.AddAsync(variantId, 1, Token);

        await WithProductSoftDeletedAsync(variantId, async () =>
        {
            var (response, cart) = await api.GetAsync(Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(cart);

            var line = Assert.Single(cart.Lines);
            Assert.Equal(CartLineStatus.Unavailable, line.Status);
            Assert.Null(line.Price);
            Assert.Equal(0m, cart.Totals.ItemsSubtotal);
        });
    }

    /// <summary>
    /// <b>A category the client deactivated (ADM-11, W1-12).</b> The storefront stops listing the product,
    /// so the bag must stop selling it: the alternative is a line that can be bought from a section the
    /// client believes they have taken down.
    /// </summary>
    [Fact]
    public async Task A_deactivated_primary_category_takes_the_line_out_of_the_subtotal()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        await api.AddAsync(variantId, 1, Token);

        await WithPrimaryCategoryActiveAsync(variantId, active: false, async () =>
        {
            var (_, cart) = await api.GetAsync(Token);
            Assert.NotNull(cart);
            Assert.Equal(CartLineStatus.Unavailable, Assert.Single(cart.Lines).Status);
            Assert.Equal(0m, cart.Totals.ItemsSubtotal);
        });
    }

    /// <summary>And the same product can no longer be added at all, not merely shown as unavailable.</summary>
    [Fact]
    public async Task A_deactivated_primary_category_cannot_be_added_to_the_bag()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        await WithPrimaryCategoryActiveAsync(variantId, active: false, async () =>
        {
            var (response, _) = await api.AddAsync(variantId, 1, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        });
    }

    /// <summary>
    /// <b>Sold out after the add.</b> <see cref="CartLineStatus.OutOfStock"/>, out of the subtotal, still
    /// listed and still removable.
    /// </summary>
    [Fact]
    public async Task A_line_that_sold_out_is_out_of_stock_and_out_of_the_subtotal()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        await api.AddAsync(variantId, 1, Token);

        await WithStockAsync(variantId, onHand: 0, async () =>
        {
            var (_, cart) = await api.GetAsync(Token);
            Assert.NotNull(cart);

            var line = Assert.Single(cart.Lines);
            Assert.Equal(CartLineStatus.OutOfStock, line.Status);
            Assert.Equal(0, line.AvailableQuantity);
            Assert.Equal(0m, cart.Totals.ItemsSubtotal);
            Assert.Equal(0, cart.Totals.BuyableItemCount);

            // Still priced: the shop still sells it, there is simply none left, and the price is the one
            // thing the shopper needs in order to decide whether to wait for it.
            Assert.NotNull(line.Price);
        });
    }

    /// <summary>
    /// <b>Fewer left than the bag asks for.</b> The quantity the shopper chose is kept — it is theirs — the
    /// line says how many there really are, and it stays in the subtotal at the price that is really being
    /// offered. The quantity selector's ceiling drops to the stock, but never below the number the line is
    /// already showing, or the control could not re-select its own value.
    /// </summary>
    [Fact]
    public async Task A_line_with_less_stock_than_quantity_says_so_and_keeps_its_quantity()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        var (_, before) = await api.AddAsync(variantId, 3, Token);
        Assert.NotNull(before);
        var unitPrice = Assert.Single(before.Lines).Price!.Current;

        await WithStockAsync(variantId, onHand: 1, async () =>
        {
            var (_, cart) = await api.GetAsync(Token);
            Assert.NotNull(cart);

            var line = Assert.Single(cart.Lines);
            Assert.Equal(CartLineStatus.NotEnoughStock, line.Status);
            Assert.Equal<short>(3, line.Quantity);
            Assert.Equal(1, line.AvailableQuantity);
            Assert.Equal<short>(3, line.MaxQuantity);
            Assert.Equal(unitPrice * 3, line.LineSubtotal);
            Assert.Equal(unitPrice * 3, cart.Totals.ItemsSubtotal);
        });
    }

    /// <summary>A variant with stock caps the selector at the stock, not at the per-line ceiling of 10.</summary>
    [Fact]
    public async Task The_quantity_ceiling_is_the_lower_of_ten_and_the_stock()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        await api.AddAsync(variantId, 1, Token);

        await WithStockAsync(variantId, onHand: 4, async () =>
        {
            var (_, cart) = await api.GetAsync(Token);
            Assert.NotNull(cart);
            Assert.Equal<short>(4, Assert.Single(cart.Lines).MaxQuantity);
        });

        await WithStockAsync(variantId, onHand: 50, async () =>
        {
            var (_, cart) = await api.GetAsync(Token);
            Assert.NotNull(cart);
            Assert.Equal<short>(10, Assert.Single(cart.Lines).MaxQuantity);
        });
    }

    /// <summary>
    /// A sold-out variant cannot be added in the first place, and the answer is a <b>409</b> rather than a
    /// 400: the request was fine, the shop's state refused it, and the same request works after a restock.
    /// </summary>
    [Fact]
    public async Task Adding_a_sold_out_variant_is_a_conflict_not_a_bad_request()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        await WithStockAsync(variantId, onHand: 0, async () =>
        {
            var (response, _) = await api.AddAsync(variantId, 1, Token);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        });
    }

    /// <summary>An unavailable line can still be taken out, which is the only thing a shopper can do with it.</summary>
    [Fact]
    public async Task An_unavailable_line_can_still_be_removed()
    {
        var api = NewVisitor();
        var variantId = (await InStockVariantIdsAsync(1))[0];

        await api.AddAsync(variantId, 1, Token);

        await WithProductPublishedAsync(variantId, published: false, async () =>
        {
            var (response, cart) = await api.RemoveAsync(variantId, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(cart);
            Assert.Empty(cart.Lines);
        });
    }

    private async Task<(int Doomed, int Kept)> TwoVariantsOfDifferentProductsAsync(IReadOnlyList<int> candidates)
    {
        using var scope = CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var byProduct = await db.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && variant.Product!.IsPublished)
            .Where(variant => variant.Product!.Department!.IsActive && variant.Product!.PrimaryCategory!.IsActive)
            .Where(variant => variant.StockLevel != null && variant.StockLevel.Available > 0)
            .OrderBy(variant => variant.Id)
            .Select(variant => new { variant.Id, variant.ProductId })
            .Take(200)
            .ToListAsync(Token);

        var first = byProduct[0];
        var second = byProduct.First(variant => variant.ProductId != first.ProductId);

        // Different products, so unpublishing one cannot take the other with it.
        Assert.NotEqual(first.ProductId, second.ProductId);
        return (first.Id, second.Id);
    }

    private async Task WithVariantPriceAsync(int variantId, decimal price, Func<Task> body)
    {
        var original = await ReadAsync(db => db.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.Id == variantId)
            .Select(variant => variant.PriceOverride)
            .SingleAsync(Token));

        await ExecuteAsync(db => db.ProductVariants
            .Where(variant => variant.Id == variantId)
            .ExecuteUpdateAsync(variant => variant.SetProperty(row => row.PriceOverride, price), Token));

        try
        {
            await body();
        }
        finally
        {
            await ExecuteAsync(db => db.ProductVariants
                .Where(variant => variant.Id == variantId)
                .ExecuteUpdateAsync(variant => variant.SetProperty(row => row.PriceOverride, original), Token));
        }
    }

    private async Task WithProductPublishedAsync(int variantId, bool published, Func<Task> body)
    {
        var productId = await ProductIdAsync(variantId);

        await ExecuteAsync(db => db.Products
            .Where(product => product.Id == productId)
            .ExecuteUpdateAsync(product => product.SetProperty(row => row.IsPublished, published), Token));

        try
        {
            await body();
        }
        finally
        {
            await ExecuteAsync(db => db.Products
                .Where(product => product.Id == productId)
                .ExecuteUpdateAsync(product => product.SetProperty(row => row.IsPublished, !published), Token));
        }
    }

    /// <summary>
    /// Soft-deletes through raw SQL rather than through <c>SoftDelete()</c>: the domain method is the only
    /// supported write path (DB5), and it is not what is under test here — the state of the row is. The
    /// restore puts the three columns back exactly.
    /// </summary>
    private async Task WithProductSoftDeletedAsync(int variantId, Func<Task> body)
    {
        var productId = await ProductIdAsync(variantId);

        await ExecuteAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE catalog.Products SET IsDeleted = 1, DeletedAtUtc = sysutcdatetime() WHERE Id = {productId};",
            Token));

        try
        {
            await body();
        }
        finally
        {
            await ExecuteAsync(db => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE catalog.Products SET IsDeleted = 0, DeletedAtUtc = NULL, DeletedByUserId = NULL WHERE Id = {productId};",
                Token));
        }
    }

    private async Task WithPrimaryCategoryActiveAsync(int variantId, bool active, Func<Task> body)
    {
        var categoryId = await ReadAsync(db => db.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.Id == variantId)
            .Select(variant => variant.Product!.PrimaryCategoryId)
            .SingleAsync(Token));

        await ExecuteAsync(db => db.Categories
            .Where(category => category.Id == categoryId)
            .ExecuteUpdateAsync(category => category.SetProperty(row => row.IsActive, active), Token));

        try
        {
            await body();
        }
        finally
        {
            await ExecuteAsync(db => db.Categories
                .Where(category => category.Id == categoryId)
                .ExecuteUpdateAsync(category => category.SetProperty(row => row.IsActive, !active), Token));
        }
    }

    private async Task WithStockAsync(int variantId, int onHand, Func<Task> body)
    {
        var original = await ReadAsync(db => db.StockLevels
            .AsNoTracking()
            .Where(stock => stock.VariantId == variantId)
            .Select(stock => stock.OnHand)
            .SingleAsync(Token));

        await ExecuteAsync(db => db.StockLevels
            .Where(stock => stock.VariantId == variantId)
            .ExecuteUpdateAsync(stock => stock.SetProperty(row => row.OnHand, onHand), Token));

        try
        {
            await body();
        }
        finally
        {
            await ExecuteAsync(db => db.StockLevels
                .Where(stock => stock.VariantId == variantId)
                .ExecuteUpdateAsync(stock => stock.SetProperty(row => row.OnHand, original), Token));
        }
    }

    private Task<int> ProductIdAsync(int variantId) => ReadAsync(db => db.ProductVariants
        .AsNoTracking()
        .Where(variant => variant.Id == variantId)
        .Select(variant => variant.ProductId)
        .SingleAsync(Token));

    private async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task ExecuteAsync(Func<AppDbContext, Task<int>> write)
    {
        using var scope = CreateScope();
        await write(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
