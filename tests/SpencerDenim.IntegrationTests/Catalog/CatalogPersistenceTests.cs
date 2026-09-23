using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>
/// Schema and persistence rules that only a real SQL Server can prove: the seeded row counts, the
/// idempotent seeder (doc 08 §9, N13), the soft-delete rules (doc 08 §10.2, DB5) and the check
/// constraints of doc 08 §3.
/// </summary>
public sealed class CatalogPersistenceTests(SqlServerDatabaseFixture sqlServer)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_seeder_inserted_the_expected_catalogue()
    {
        sqlServer.SkipIfUnavailable();

        var counts = sqlServer.SeedCounts;

        Assert.Equal(3, counts.Departments);
        Assert.Equal(4, counts.AgeGroups);
        Assert.Equal(19, counts.AttributeOptions);
        Assert.Equal(14, counts.ColourFamilies);
        Assert.Equal(52, counts.Sizes);
        Assert.Equal(3, counts.SizeGuides);
        Assert.Equal(29, counts.Categories);
        Assert.Equal(45, counts.Products);
        Assert.Equal(480, counts.Variants);
    }

    [Fact]
    public async Task Every_product_has_stats_search_text_stock_and_a_category_link()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var products = await dbContext.Products.CountAsync(Token);

        Assert.Equal(products, await dbContext.ProductStats.CountAsync(Token));
        Assert.Equal(products, await dbContext.ProductSearchIndexes.CountAsync(Token));
        Assert.Equal(products, await dbContext.ProductCategories.CountAsync(Token));
        Assert.Equal(
            await dbContext.ProductVariants.CountAsync(Token),
            await dbContext.StockLevels.CountAsync(Token));
        Assert.True(await dbContext.MediaUsages.AnyAsync(Token));
    }

    [Fact]
    public async Task The_computed_available_column_is_on_hand_minus_reserved()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var mismatches = await dbContext.StockLevels
            .Where(stock => stock.Available != stock.OnHand - stock.Reserved)
            .CountAsync(Token);

        Assert.Equal(0, mismatches);
    }

    [Fact]
    public async Task Running_the_seeder_again_inserts_nothing()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<CatalogSeedRunner>();

        var counts = await runner.RunAsync(includeDemoCatalogue: true, Token);

        Assert.Equal(0, counts.Departments);
        Assert.Equal(0, counts.Categories);
        Assert.Equal(0, counts.Sizes);
        Assert.Equal(0, counts.Products);
        Assert.Equal(0, counts.Variants);
    }

    [Fact]
    public async Task Removing_a_soft_deletable_entity_throws_instead_of_cascading()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var product = await dbContext.Products.FirstAsync(candidate => candidate.Slug == "denim-wrap-mini-skirt", Token);
        dbContext.Products.Remove(product);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync(Token));

        Assert.Contains("SoftDelete", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_soft_deleted_product_disappears_from_the_storefront_but_keeps_its_children()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var product = await dbContext.Products.FirstAsync(candidate => candidate.Slug == "baby-muslin-bib-set", Token);
        var productId = product.Id;
        var variantsBefore = await dbContext.ProductVariants.CountAsync(variant => variant.ProductId == productId, Token);

        product.SoftDelete(DateTime.UtcNow, userId: null);
        await dbContext.SaveChangesAsync(Token);

        try
        {
            Assert.False(await dbContext.Products.AnyAsync(candidate => candidate.Id == productId, Token));
            Assert.True(await dbContext.Products.IgnoreQueryFilters().AnyAsync(candidate => candidate.Id == productId, Token));

            // Children are untouched: only the parent row is flagged.
            Assert.Equal(variantsBefore, await dbContext.ProductVariants.CountAsync(variant => variant.ProductId == productId, Token));
            Assert.True(await dbContext.ProductColours.AnyAsync(colour => colour.ProductId == productId, Token));
            Assert.True(await dbContext.ProductMedia.AnyAsync(media => media.ProductId == productId, Token));
            Assert.True(await dbContext.ProductCategories.AnyAsync(link => link.ProductId == productId, Token));
        }
        finally
        {
            // Restore, so the other tests still see the full catalogue whatever order they run in.
            product.Restore();
            await dbContext.SaveChangesAsync(Token);
        }
    }

    [Fact]
    public async Task Audit_columns_are_stamped_on_insert_and_update()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var product = await dbContext.Products.FirstAsync(candidate => candidate.Slug == "chino-shorts", Token);
        var createdAt = product.CreatedAtUtc;

        Assert.NotEqual(default, createdAt);
        Assert.Equal(DateTimeKind.Utc, createdAt.Kind);
        Assert.Null(product.UpdatedAtUtc);

        product.RecommendedRank = 99;
        await dbContext.SaveChangesAsync(Token);

        Assert.Equal(createdAt, product.CreatedAtUtc);
        Assert.NotNull(product.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, product.UpdatedAtUtc!.Value.Kind);

        product.RecommendedRank = null;
        await dbContext.SaveChangesAsync(Token);
    }

    [Theory]
    // Doc 08 §3: the constraints the database enforces whatever the application does.
    [InlineData("UPDATE catalog.Products SET BasePrice = 0 WHERE ProductCode = 'SD-W-0002';", "CK_Products_BasePrice")]
    [InlineData("UPDATE catalog.Products SET CompareAtPrice = 1 WHERE ProductCode = 'SD-W-0002';", "CK_Products_CompareAtGtBase")]
    [InlineData("UPDATE catalog.Products SET BadgeOverride = 9 WHERE ProductCode = 'SD-W-0002';", "CK_Products_BadgeOverride")]
    [InlineData("UPDATE inventory.StockLevels SET Reserved = OnHand + 1 WHERE VariantId = (SELECT MIN(Id) FROM catalog.ProductVariants);", "CK_StockLevels_ReservedLeOnHand")]
    [InlineData("UPDATE catalog.Categories SET Depth = 5 WHERE Slug = 'jeans';", "CK_Categories_Depth")]
    [InlineData("UPDATE catalog.ProductStats SET MaxDiscountPercent = 101 WHERE ProductId = (SELECT MIN(Id) FROM catalog.Products);", "CK_ProductStats_Discount")]
    [InlineData("UPDATE catalog.SizeGuides SET TableJson = 'not json' WHERE Id = (SELECT MIN(Id) FROM catalog.SizeGuides);", "CK_SizeGuides_TableJson")]
    public async Task A_check_constraint_rejects_invalid_data(string sql, string constraint)
    {
        sqlServer.SkipIfUnavailable();

        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed SQL from the theory data in this test class.
        command.CommandText = sql;
#pragma warning restore CA2100

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(Token));

        Assert.Contains(constraint, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_variant_cannot_point_at_another_products_colourway()
    {
        sqlServer.SkipIfUnavailable();

        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        // A new row, not an update: updating an existing variant would hit
        // UX_ProductVariants_Colour_Size first and prove nothing about the composite FK.
        command.CommandText = """
            DECLARE @productId int = (SELECT MIN(ProductId) FROM catalog.ProductVariants);
            DECLARE @foreignColourId int = (SELECT MIN(Id) FROM catalog.ProductColours WHERE ProductId <> @productId);
            DECLARE @freeSizeId int = (
                SELECT MIN(s.Id) FROM catalog.Sizes s
                WHERE NOT EXISTS (
                    SELECT 1 FROM catalog.ProductVariants v
                    WHERE v.ProductColourId = @foreignColourId AND v.SizeId = s.Id));

            INSERT INTO catalog.ProductVariants
                (ProductId, ProductColourId, SizeId, Sku, IsActive, SortOrder, CreatedAtUtc)
            VALUES
                (@productId, @foreignColourId, @freeSizeId, 'FK-GUARD-TEST', 1, 9999, SYSUTCDATETIME());
            """;

        var exception = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync(Token));

        Assert.Contains("FK_ProductVariants_ProductColours", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Category_paths_are_materialised_root_to_leaf()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var categories = await dbContext.Categories
            .Select(category => new { category.Id, category.ParentCategoryId, category.Path, category.Depth })
            .ToListAsync(Token);

        var byId = categories.ToDictionary(category => category.Id);

        Assert.All(categories, category =>
        {
            Assert.StartsWith("/", category.Path, StringComparison.Ordinal);
            Assert.EndsWith($"/{category.Id}/", category.Path, StringComparison.Ordinal);
            Assert.True(category.Depth is >= 1 and <= Category.MaxDepth);

            if (category.ParentCategoryId is { } parentId)
            {
                Assert.StartsWith(byId[parentId].Path, category.Path, StringComparison.Ordinal);
                Assert.Equal(byId[parentId].Depth + 1, category.Depth);
            }
        });
    }
}
