using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Auditing;
using SpencerDenim.Domain.Catalog;
using SpencerDenim.Domain.Common;
using SpencerDenim.Domain.Content;
using SpencerDenim.Domain.Inventory;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Domain.Settings;
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

            // W2-01, doc 08 §3.7 and §1.4 (DB15): per-type section settings are JSON.
            $"{nameof(HomeSection)}.{nameof(HomeSection.SettingsJson)}",

            // W3-03, doc 08 §3.5 and §1.4 (DB15): the seller details frozen onto an invoice, and the verbatim
            // provider payload. Both nvarchar(max) with an ISJSON check, never the native json type.
            $"{nameof(Order)}.{nameof(Order.InvoiceSellerSnapshotJson)}",
            $"{nameof(PaymentEvent)}.{nameof(PaymentEvent.PayloadJson)}",

            // ADM-22, doc 08 §3.8: ops.Settings.Value is nvarchar(max) because one column holds every value
            // type — a Json options blob, a Text/HTML snippet sanitised on save (PdpReturnsInfoHtml), and a
            // Data Protection ciphertext whose length depends on the key ring rather than on the plaintext.
            // A sized column would have to be wide enough for the largest of those, which is nvarchar(max)
            // by another name. The *inputs* are bounded instead, where a person can be told why:
            // SettingFieldRules caps a profile URL at 2,048 characters and a WhatsApp number at 15 digits,
            // so no endpoint in this build can write an unbounded value into it.
            $"{nameof(Setting)}.{nameof(Setting.Value)}",

            // GLB-10 (W2-11), doc 08 §3.7: content.ContentPages.BodyHtml is listed there as
            // nvarchar(max) and "Sanitised". A policy page is the one piece of admin-written copy with no
            // natural ceiling — a terms-and-conditions page runs to tens of kilobytes — and sizing the
            // column would mean choosing a number that one client's lawyer eventually exceeds.
            //
            // The *input* is bounded instead, where a person can be told why:
            // ContentPage.BodyHtmlMaxLength caps it at 200,000 characters and
            // ContentPageWriteHandlers.CheckBody refuses more with the figure in the message. The check is
            // deliberately applied to the *cleaned* markup rather than the paste, because the cleaned
            // markup is what is stored.
            $"{nameof(SpencerDenim.Domain.Content.ContentPage)}.{nameof(SpencerDenim.Domain.Content.ContentPage.BodyHtml)}",

            // ACC-03 (W3-06), doc 08 §3.8: `ops.EmailOutbox.BodyHtml` / `BodyText` are listed there as
            // nvarchar(max), and the *input* is not bounded the way a content page's is — because there is
            // no person to tell. These bodies are rendered by the server from an order, so their length is
            // the order's: a bag of forty lines produces a longer message than a bag of one, and a cap
            // would mean an order so large that the shop could not tell the customer about it. Both
            // columns are emptied rather than sized: doc 08 §8.4 NULLs them after thirty days, and §3.8's
            // G-03 rule NULLs them the moment a token-bearing message is sent.
            //
            // `TransportOverrideProtected` is a Data Protection ciphertext whose length depends on the key
            // ring rather than on the plaintext — the same argument `ops.Settings.Value` makes above.
            $"{nameof(SpencerDenim.Domain.Ops.EmailOutboxMessage)}.{nameof(SpencerDenim.Domain.Ops.EmailOutboxMessage.BodyHtml)}",
            $"{nameof(SpencerDenim.Domain.Ops.EmailOutboxMessage)}.{nameof(SpencerDenim.Domain.Ops.EmailOutboxMessage.BodyText)}",
            $"{nameof(SpencerDenim.Domain.Ops.EmailOutboxMessage)}.{nameof(SpencerDenim.Domain.Ops.EmailOutboxMessage.TransportOverrideProtected)}",

            // W4-02a, doc 08 §3.8: `ops.AdminAlerts.DetailsJson` is listed there as nvarchar(max) with a
            // `CK ISJSON` and nothing else, and it is unbounded for doc 08 §1.4's reason rather than for
            // want of a number: a JSON document's length is the shape of whichever alert type wrote it,
            // and six of the seven types are later tasks' — a `ReconciliationDrift` row naming every
            // drifting variant has no ceiling an author today could choose honestly.
            //
            // There is also no person to tell: every writer is a job or a background path, so a refusal
            // would reach a log and not a form. What bounds it instead is that nothing user-supplied goes
            // in: doc 08 §3.8 limits it to "keys, entity IDs and amounts", and this build's only writer
            // (`DeadEmailAlert`) emits four fixed fields.
            $"{nameof(SpencerDenim.Domain.Ops.AdminAlert)}.{nameof(SpencerDenim.Domain.Ops.AdminAlert.DetailsJson)}",
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

    /// <summary>
    /// Doc 08 §1.4: UTC times are datetime2(3), with a UTC-kind converter — with the one exception doc 08 itself
    /// makes: §3.8 gives <c>analytics.PageViews.OccurredAtUtc</c> <c>datetime2(0)</c> (W4-03), a high-volume
    /// append-only table where the second is all the reports use. The exception is named, so a second one cannot
    /// slip in unnoticed.
    /// </summary>
    [Fact]
    public void Every_date_time_column_is_datetime2_3_in_utc()
    {
        var exceptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{nameof(SpencerDenim.Domain.Analytics.PageView)}.{nameof(SpencerDenim.Domain.Analytics.PageView.OccurredAtUtc)}"] = "datetime2(0)",
        };

        var dateTimes = Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties())
            .Where(property => property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?))
            .ToList();

        Assert.NotEmpty(dateTimes);
        Assert.All(dateTimes, property =>
        {
            var name = $"{property.DeclaringType.ClrType.Name}.{property.Name}";
            Assert.Equal(exceptions.GetValueOrDefault(name, "datetime2(3)"), property.GetColumnType());
            Assert.NotNull(property.GetValueConverter());
        });

        Assert.All(exceptions.Keys, name => Assert.Contains(dateTimes, property => $"{property.DeclaringType.ClrType.Name}.{property.Name}" == name));
    }

    /// <summary>Doc 08 §1.5: the [SD] tables carry the soft-delete filter, and nothing else does.</summary>
    [Fact]
    public void Only_the_soft_deletable_entities_have_the_soft_delete_filter()
    {
        var filtered = Model.GetEntityTypes()
            .Where(entity => entity.GetDeclaredQueryFilters().Count > 0)
            .Select(entity => entity.ClrType)
            .ToList();

        Assert.Equal(7, filtered.Count);
        Assert.All(filtered, type => Assert.True(typeof(ISoftDeletable).IsAssignableFrom(type)));
        Assert.Contains(typeof(Product), filtered);
        Assert.Contains(typeof(Category), filtered);
        Assert.Contains(typeof(SizeGuide), filtered);
        Assert.Contains(typeof(SpencerDenim.Domain.Media.MediaAsset), filtered);

        // GLB-10 (W2-11): doc 08 §3.7 marks content.ContentPages [SD], and the filter is what makes a
        // removed custom page gone rather than hidden — the storefront reader never mentions IsDeleted,
        // so the filter is the only thing keeping a deleted page off the shop.
        Assert.Contains(typeof(SpencerDenim.Domain.Content.ContentPage), filtered);

        // ADM-13 (W3-05): doc 08 §3.6 marks marketing.PromoCodes [SD], and `ApplySoftDelete` gives it the
        // filter like every other such table. It is load-bearing in the same way: a removed code has to be
        // "not recognised" rather than found-and-refused, or the box a shopper types into would tell them
        // which strings the shop has ever issued.
        Assert.Contains(typeof(SpencerDenim.Domain.Marketing.PromoCode), filtered);

        // ADM-14 (W3-05b): doc 08 §3.6 marks marketing.SaleCampaigns [SD], and `ApplySoftDelete` gives
        // it the filter — which is a thing that extension *does*, whatever a comment elsewhere in this
        // repository may once have claimed. It is load-bearing here in the most direct way in the
        // feature: a removed campaign must stop pricing the catalogue, and the pricing reader's own
        // predicate never mentions IsDeleted, so this filter is the only thing that ends the sale.
        Assert.Contains(typeof(SpencerDenim.Domain.Marketing.SaleCampaign), filtered);

        // And SaleCampaignTargets does **not** carry one, deliberately: it is a child row, doc 08 §1.5
        // does not mark it [SD], and the pricing reader only reaches a target through a campaign that
        // the filter above has already vouched for.
        Assert.DoesNotContain(typeof(SpencerDenim.Domain.Marketing.SaleCampaignTarget), filtered);
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

    /// <summary>
    /// Doc 08 §1.6: nothing cascades except pure child rows. Asserted both ways, so the model can
    /// neither add a cascade the section does not permit nor quietly drop one it lists.
    /// </summary>
    [Fact]
    public void Only_pure_child_rows_cascade_on_delete()
    {
        var cascading = Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys()
                .Where(foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.Cascade)
                .Select(foreignKey => $"{entity.ShortName()} -> {foreignKey.PrincipalEntityType.ShortName()}"))
            .ToHashSet(StringComparer.Ordinal);

        // Doc 08 §1.6: "CASCADE only for pure child rows: CartItems, UserRoles, RoleClaims,
        // UserClaims/Logins/Tokens, ProductCategories, ProductColours, ProductStats,
        // ProductSearchIndex, RelatedProducts, ProductMedia, ...". The tables of later milestones are
        // not mapped yet, so only this milestone's rows can appear.
        string[] allowed =
        [
            "ProductCategory -> Product",
            "ProductColour -> Product",
            "ProductMedia -> Product",
            "RelatedProduct -> Product",
            "ProductStats -> Product",
            "ProductSearchIndex -> Product",

            // W2-01's content model. Doc 08 §1.6 already names HomeSectionItems in the permitted
            // list; no cascade was added to that list for this task.
            "HomeSectionItem -> HomeSection",

            // W2-09's bag. Doc 08 §1.6 names CartItems first in the permitted list — a cart item has no
            // meaning apart from its cart.
            "CartItem -> Cart",

            // W3-03: a zone rate has no meaning apart from its delivery method. Nothing else in the
            // twelve tables W3-03 added cascades — Orders and OrderLines are on §1.6's "never deleted"
            // list instead.
            "DeliveryMethodZoneRate -> DeliveryMethod",

            // W3-05b (ADM-14): doc 08 §1.6 names SaleCampaignTargets in the permitted list. A target has
            // no meaning apart from its campaign, and the cascade only ever runs on a purge, because a
            // campaign is removed through SoftDelete() and Remove() on it throws (DB5). The target's
            // three catalogue references are NO ACTION, which is the half worth having asserted: a
            // cascade from Products would mean purging one product deleted a whole campaign's targeting.
            "SaleCampaignTarget -> SaleCampaign",

            // W1-01's Identity model, the same doc 08 §1.6 list.
            "IdentityUserRole -> AppUser",
            "IdentityUserRole -> AppRole",
            "IdentityRoleClaim -> AppRole",
            "IdentityUserClaim -> AppUser",
            "IdentityUserLogin -> AppUser",
            "IdentityUserToken -> AppUser",
        ];

        Assert.Equal(allowed.Order(StringComparer.Ordinal), cascading.Order(StringComparer.Ordinal));
    }
}
