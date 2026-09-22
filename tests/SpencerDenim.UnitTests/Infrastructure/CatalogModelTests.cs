using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Infrastructure;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.UnitTests.Infrastructure;

/// <summary>
/// Model rules from doc 08 that must hold without a database: the FK-index policy (§1.6, DB7), the
/// standard types (§1.4), the soft-delete filters (§10.2) and the JSON mapping rule (DB15).
/// </summary>
public sealed class CatalogModelTests
{
    private static readonly IModel Model = BuildModel();

    private static IModel BuildModel()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = "Server=example;Database=SpencerDenim;",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddInfrastructure();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Model;
    }

    /// <summary>
    /// The entity types this milestone owns. One <see cref="AppDbContext"/> now also holds W1-01's Identity
    /// and audit model (doc 08 §3.1 and §3.8), so a test about the catalogue has to say so rather than
    /// assert over every entity in the context.
    /// </summary>
    private static IEnumerable<IEntityType> CatalogueEntities => Model.GetEntityTypes()
        .Where(entity => entity.GetSchema() is AppDbContext.CatalogSchema
            or AppDbContext.MediaSchema
            or AppDbContext.InventorySchema);

    [Fact]
    public void The_catalogue_media_and_inventory_tables_of_doc_08_are_all_mapped()
    {
        var tables = CatalogueEntities
            .Select(entity => $"{entity.GetSchema()}.{entity.GetTableName()}")
            .ToHashSet(StringComparer.Ordinal);

        string[] expected =
        [
            "catalog.Departments", "catalog.Categories", "catalog.AgeGroups", "catalog.AttributeOptions",
            "catalog.ColourFamilies", "catalog.Sizes", "catalog.SizeGuides", "catalog.Products",
            "catalog.ProductStats", "catalog.ProductSearchIndex", "catalog.ProductCategories",
            "catalog.ProductColours", "catalog.ProductVariants", "catalog.ProductMedia",
            "catalog.RelatedProducts", "media.MediaAssets", "media.MediaUsages", "inventory.StockLevels",
        ];

        Assert.Equal(expected.Length, tables.Count);
        foreach (var table in expected)
        {
            Assert.Contains(table, tables);
        }
    }

    /// <summary>Doc 08 §1.6 (DB7): every FK column leads at least one index or key.</summary>
    [Fact]
    public void Every_foreign_key_leads_an_index_or_a_key()
    {
        var offenders = new List<string>();

        foreach (var entity in Model.GetEntityTypes())
        {
            var leadingColumns = entity.GetIndexes()
                .Select(index => index.Properties[0].Name)
                .Concat(entity.GetKeys().Select(key => key.Properties[0].Name))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var foreignKey in entity.GetForeignKeys())
            {
                if (!leadingColumns.Contains(foreignKey.Properties[0].Name))
                {
                    offenders.Add($"{entity.ShortName()}.{foreignKey.Properties[0].Name}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>Doc 08 §1.4: every string column is sized; only HTML, JSON and payloads are nvarchar(max).</summary>
    [Fact]
    public void Every_string_column_is_sized_or_deliberately_unbounded()
    {
        string[] deliberatelyUnbounded =
        [
            $"{nameof(Product)}.{nameof(Product.Description)}",
            $"{nameof(SizeGuide)}.{nameof(SizeGuide.IntroHtml)}",
            $"{nameof(SizeGuide)}.{nameof(SizeGuide.TableJson)}",
            $"{nameof(SizeGuide)}.{nameof(SizeGuide.MeasuringTipsHtml)}",

            // W1-01, doc 08 §3.1: "PasswordHash, SecurityStamp, ConcurrencyStamp | nvarchar(max)" on Users
            // and Roles, and "Identity standard tables" for UserClaims / UserLogins / UserTokens.
            "AppUser.PasswordHash",
            "AppUser.SecurityStamp",
            "AppUser.ConcurrencyStamp",
            "AppRole.ConcurrencyStamp",
            "IdentityUserClaim.ClaimType",
            "IdentityUserClaim.ClaimValue",
            "IdentityUserLogin.ProviderDisplayName",
            "IdentityUserToken.Value",

            // W1-01, doc 08 §3.8 and §1.4 (DB15): audit diffs are JSON, so nvarchar(max) with ISJSON checks.
            $"{nameof(AuditLog)}.{nameof(AuditLog.BeforeJson)}",
            $"{nameof(AuditLog)}.{nameof(AuditLog.AfterJson)}",
        ];

        var offenders = Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Where(property => property.ClrType == typeof(string))
                .Select(property => new
                {
                    Key = $"{entity.ShortName()}.{property.Name}",
                    Type = property.GetColumnType(),
                    MaxLength = property.GetMaxLength(),
                }))
            .Where(property => property.MaxLength is null
                && (property.Type is null || property.Type.Contains("(max)", StringComparison.OrdinalIgnoreCase)))
            .Select(property => property.Key)
            .Where(key => !deliberatelyUnbounded.Contains(key, StringComparer.Ordinal))
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>Doc 08 §1.4 (DB15): JSON is nvarchar(max), never the native json type.</summary>
    [Fact]
    public void Json_columns_are_mapped_as_nvarchar_max()
    {
        var tableJson = Model.FindEntityType(typeof(SizeGuide))!.FindProperty(nameof(SizeGuide.TableJson))!;

        Assert.Equal("nvarchar(max)", tableJson.GetColumnType());
    }

    /// <summary>Doc 08 §1.4: money is decimal(18,2).</summary>
    [Theory]
    [InlineData(typeof(Product), nameof(Product.BasePrice), "decimal(18,2)")]
    [InlineData(typeof(Product), nameof(Product.CompareAtPrice), "decimal(18,2)")]
    [InlineData(typeof(ProductVariant), nameof(ProductVariant.PriceOverride), "decimal(18,2)")]
    [InlineData(typeof(ProductStats), nameof(ProductStats.MinSalePrice), "decimal(18,2)")]
    [InlineData(typeof(ProductStats), nameof(ProductStats.MaxDiscountPercent), "decimal(5,2)")]
    [InlineData(typeof(ProductStats), nameof(ProductStats.RatingAverage), "decimal(3,2)")]
    public void Decimal_columns_use_the_precision_doc_08_specifies(Type entity, string property, string expected)
    {
        Assert.Equal(expected, Model.FindEntityType(entity)!.FindProperty(property)!.GetColumnType());
    }

    /// <summary>Doc 08 §1.4: UTC times are datetime2(3), with a UTC-kind converter.</summary>
    [Fact]
    public void Every_date_time_column_is_datetime2_3_in_utc()
    {
        var dateTimes = Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
            .ToList();

        Assert.NotEmpty(dateTimes);
        Assert.All(dateTimes, property =>
        {
            Assert.Equal("datetime2(3)", property.GetColumnType());
            Assert.NotNull(property.GetValueConverter());
        });
    }

    /// <summary>Doc 08 §1.5: the four [SD] tables carry the soft-delete filter, and nothing else does.</summary>
    [Fact]
    public void Only_the_soft_deletable_entities_have_the_soft_delete_filter()
    {
        var filtered = Model.GetEntityTypes()
            .Where(entity => entity.GetDeclaredQueryFilters().Count > 0)
            .Select(entity => entity.ClrType)
            .ToList();

        Assert.Equal(4, filtered.Count);
        Assert.All(filtered, type => Assert.True(typeof(ISoftDeletable).IsAssignableFrom(type)));
        Assert.Contains(typeof(Product), filtered);
        Assert.Contains(typeof(Category), filtered);
        Assert.Contains(typeof(SizeGuide), filtered);
        Assert.Contains(typeof(SpencerDenim.Domain.Media.MediaAsset), filtered);
    }

    /// <summary>Doc 08 §3.4: Available is a persisted computed column so it can be indexed.</summary>
    [Fact]
    public void Stock_available_is_a_stored_computed_column()
    {
        var available = Model.FindEntityType(typeof(StockLevel))!.FindProperty(nameof(StockLevel.Available))!;

        Assert.Equal("[OnHand] - [Reserved]", available.GetComputedColumnSql());
        Assert.True(available.GetIsStored());
    }

    /// <summary>Doc 08 §3.2: the variant → colour relationship is a composite FK on the (Id, ProductId) key.</summary>
    [Fact]
    public void The_variant_colour_relationship_is_a_composite_foreign_key()
    {
        var variant = Model.FindEntityType(typeof(ProductVariant))!;
        var colourForeignKey = variant.GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(ProductColour));

        Assert.Equal(
            [nameof(ProductVariant.ProductColourId), nameof(ProductVariant.ProductId)],
            colourForeignKey.Properties.Select(property => property.Name));
        Assert.Equal(
            [nameof(ProductColour.Id), nameof(ProductColour.ProductId)],
            colourForeignKey.PrincipalKey.Properties.Select(property => property.Name));
    }

    /// <summary>Doc 08 §7: the indexes the listing, price, size, colour and stock predicates rely on.</summary>
    [Theory]
    [InlineData("IX_Products_Listing")]
    [InlineData("IX_Products_NewUntilUtc")]
    [InlineData("IX_ProductStats_MinSalePrice")]
    [InlineData("IX_ProductVariants_Size")]
    [InlineData("IX_ProductVariants_Product")]
    [InlineData("IX_ProductColours_ColourFamily")]
    [InlineData("IX_StockLevels_Available")]
    [InlineData("IX_Categories_Path")]
    [InlineData("IX_Categories_Dept_Parent_Sort")]
    [InlineData("UX_Products_Slug")]
    [InlineData("UX_ProductVariants_Sku")]
    [InlineData("UX_Categories_Dept_Parent_Slug")]
    public void The_indexes_doc_08_section_7_specifies_exist(string name)
    {
        var names = Model.GetEntityTypes()
            .SelectMany(entity => entity.GetIndexes())
            .Select(index => index.GetDatabaseName())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(name, names);
    }

    /// <summary>DB28: the listing index is filtered on literal predicates.</summary>
    [Fact]
    public void The_listing_index_is_filtered_on_published_and_not_deleted()
    {
        var index = Model.FindEntityType(typeof(Product))!
            .GetIndexes()
            .Single(candidate => candidate.GetDatabaseName() == "IX_Products_Listing");

        Assert.Equal("[IsPublished] = 1 AND [IsDeleted] = 0", index.GetFilter());
        Assert.Equal(
            [nameof(Product.DepartmentId), nameof(Product.PrimaryCategoryId)],
            index.Properties.Select(property => property.Name));
    }

    /// <summary>Doc 08 §1.6: nothing cascades except pure child rows.</summary>
    [Fact]
    public void Only_pure_child_rows_cascade_on_delete()
    {
        var cascading = Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys()
                .Where(foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.Cascade)
                .Select(foreignKey => $"{entity.ShortName()} -> {foreignKey.PrincipalEntityType.ShortName()}"))
            .ToList();

        // Doc 08 §1.6: "CASCADE only for pure child rows: CartItems, UserRoles, RoleClaims,
        // UserClaims/Logins/Tokens, ProductCategories, RelatedProducts, ProductMedia, ...".
        string[] allowed =
        [
            "ProductCategory -> Product",
            "ProductColour -> Product",
            "ProductMedia -> Product",
            "RelatedProduct -> Product",
            "ProductStats -> Product",
            "ProductSearchIndex -> Product",

            // W1-01's Identity model, the same doc 08 §1.6 list.
            "IdentityUserRole -> AppUser",
            "IdentityUserRole -> AppRole",
            "IdentityRoleClaim -> AppRole",
            "IdentityUserClaim -> AppUser",
            "IdentityUserLogin -> AppUser",
            "IdentityUserToken -> AppUser",
        ];

        Assert.All(cascading, relationship => Assert.Contains(relationship, allowed, StringComparer.Ordinal));
    }
}
