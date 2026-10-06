using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.UnitTests.Infrastructure;

/// <summary>
/// What W2-09's migration must and must not contain, asserted against the model so it cannot drift
/// (doc 08 §3.5).
/// <para>
/// These are the claims this task made to #53 on the coordination note, so they are worth a test rather
/// than a promise: <b>two tables only</b>, <b>no rowversion</b>, the token under a unique index and not the
/// key, and one cart per customer enforced by a filtered unique index.
/// </para>
/// </summary>
public sealed class CartModelTests
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

        // The design-time model, not `context.Model`: the read-optimised runtime model drops check
        // constraints, and `CK_CartItems_Quantity` is one of the things this class exists to pin.
        return scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .GetService<IDesignTimeModel>()
            .Model;
    }

    /// <summary>
    /// <b>The sales schema holds all fifteen tables of doc 08 §3.5, and no more.</b> W2-09 mapped the two
    /// cart tables, W3-03 (#53) the twelve for orders, payments and delivery, and W2-08 the last one,
    /// <c>WishlistItems</c>.
    /// <para>
    /// W2-09 asserted "exactly two tables" because that was the claim it made to #53 on the coordination note,
    /// and it then asserted that <c>WishlistItems</c> was absent because mapping it would have meant two
    /// branches creating the same table. Both numbers have moved as the branches landed — but the test exists
    /// for the same reason throughout: the sales schema is built by more than one branch, and what each one
    /// owns has to be written down somewhere a build can check. With §3.5 complete, the list is now the
    /// document's own and the next entry would be a table doc 08 does not have.
    /// </para>
    /// </summary>
    [Fact]
    public void The_sales_schema_holds_every_table_doc_08_gives_it()
    {
        var tables = Model.GetEntityTypes()
            .Where(entity => entity.GetSchema() == AppDbContext.SalesSchema)
            .Select(entity => entity.GetTableName())
            .ToList();

        Assert.Equal(
            [
                "CartItems",
                "Carts",
                "Couriers",
                "DeliveryMethodZoneRates",
                "DeliveryMethods",
                "NumberSeries",
                "OrderLines",
                "OrderStatusHistory",
                "Orders",
                "PaymentEvents",
                "PaymentProviders",
                "Payments",
                "PickupLocations",
                "Refunds",
                "WishlistItems",
            ],
            tables.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Doc 08 §1.5: rowversion is "not used on hot rows (StockLevels, Carts)", and §3.5 (DB22) puts
    /// item-level upserts in its place. A rowversion appearing here would mean someone had reintroduced the
    /// read-then-write the upsert exists to avoid.
    /// </summary>
    [Fact]
    public void Neither_cart_table_has_a_concurrency_token()
    {
        foreach (var entity in CartEntities())
        {
            Assert.DoesNotContain(entity.GetProperties(), property => property.IsConcurrencyToken);
        }
    }

    /// <summary>Doc 08 §1.3: the token is a unique alternate key, never the clustered one.</summary>
    [Fact]
    public void The_cart_token_is_a_unique_index_and_not_the_key()
    {
        var cart = Model.FindEntityType(typeof(Cart))!;

        Assert.Equal(nameof(Cart.Id), Assert.Single(cart.FindPrimaryKey()!.Properties).Name);

        var tokenIndex = Assert.Single(
            cart.GetIndexes(),
            index => index.Properties.Count == 1 && index.Properties[0].Name == nameof(Cart.Token));

        Assert.True(tokenIndex.IsUnique);
        Assert.Equal("UX_Carts_Token", tokenIndex.GetDatabaseName());
        Assert.Null(tokenIndex.GetFilter());
    }

    /// <summary>
    /// Doc 08 §3.5's <c>UX_Carts_Customer</c>. The filter is the whole point: without
    /// <c>WHERE CustomerUserId IS NOT NULL</c> a unique index would permit exactly one guest cart in the
    /// entire table, and with it, one customer can never hold two carts.
    /// </summary>
    [Fact]
    public void One_customer_can_hold_only_one_cart()
    {
        var index = Assert.Single(
            Model.FindEntityType(typeof(Cart))!.GetIndexes(),
            candidate => candidate.Properties.Count == 1
                && candidate.Properties[0].Name == nameof(Cart.CustomerUserId));

        Assert.True(index.IsUnique);
        Assert.Equal("UX_Carts_Customer", index.GetDatabaseName());
        Assert.Equal("[CustomerUserId] IS NOT NULL", index.GetFilter());
    }

    /// <summary>
    /// Doc 08 §3.5's <c>UX_CartItems_Cart_Variant</c>, which is also what makes the DB22 upsert safe: the
    /// loser of an insert race gets a unique violation instead of writing a second row for the same variant.
    /// </summary>
    [Fact]
    public void One_cart_holds_each_variant_at_most_once()
    {
        var index = Assert.Single(
            Model.FindEntityType(typeof(CartItem))!.GetIndexes(),
            candidate => candidate.IsUnique);

        Assert.Equal(
            [nameof(CartItem.CartId), nameof(CartItem.VariantId)],
            index.Properties.Select(property => property.Name));
        Assert.Equal("UX_CartItems_Cart_Variant", index.GetDatabaseName());
    }

    /// <summary>
    /// Doc 08 §1.6's cascade allow-list, over the whole <c>sales</c> schema. Two entries, and only two:
    /// CartItems from Carts, and (W3-03) DeliveryMethodZoneRates from DeliveryMethods. A cascade from
    /// ProductVariants would mean deleting a variant silently emptied part of somebody's bag, and a cascade
    /// from Orders to OrderLines would let a purge take the accounting records — EF's default for a required
    /// relationship, so it is worth asserting rather than assuming.
    /// </summary>
    [Fact]
    public void Only_the_cart_to_item_relationship_cascades()
    {
        var cascading = SalesEntities()
            .SelectMany(entity => entity.GetForeignKeys()
                .Where(foreignKey => foreignKey.DeleteBehavior == DeleteBehavior.Cascade)
                .Select(foreignKey =>
                    $"{entity.ShortName()} -> {foreignKey.PrincipalEntityType.ShortName()}"))
            .ToList();

        Assert.Equal(
            ["CartItem -> Cart", "DeliveryMethodZoneRate -> DeliveryMethod"],
            cascading.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The doc 08 cart columns that are still deliberately left out, named so that adding one is a decision
    /// rather than an accident.
    /// <para>
    /// <c>PromoCodeText</c> needs <c>marketing.PromoCodes</c> (ADM-13 / W3-05) before §3.5's "revalidated on
    /// every read" can mean anything, and <c>DistrictId</c> needs <c>ref.Districts</c> (GLB-06), which no
    /// migration has created — so it still could not carry its foreign key.
    /// </para>
    /// <para>
    /// <b><c>DeliveryMethodId</c> has moved from this list to the one below.</b> W2-09 deferred it because
    /// <c>sales.DeliveryMethods</c> did not exist; W3-03 created that table, so the column and its constraint
    /// were added together, which is what W2-09 said the owning task should do.
    /// </para>
    /// </summary>
    [Fact]
    public void The_columns_that_belong_to_unbuilt_features_are_not_on_the_cart()
    {
        var columns = Model.FindEntityType(typeof(Cart))!
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("PromoCodeText", columns);
        Assert.DoesNotContain("DistrictId", columns);

        // And what is there is only what the bag uses, plus W3-03's delivery method.
        Assert.Equal(
            ["CreatedAtUtc", "CustomerUserId", "DeliveryMethodId", "Id", "LastActivityAtUtc", "Token"],
            columns.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The new column is only worth having with its constraint: doc 08 §3.5 gives it an FK to
    /// <c>sales.DeliveryMethods</c>, and <c>NO ACTION</c>, because a method an admin retires must not be
    /// deletable out from under a live bag.
    /// </summary>
    [Fact]
    public void The_cart_delivery_method_carries_its_foreign_key()
    {
        var cart = Model.FindEntityType(typeof(Cart))!;

        var foreignKey = Assert.Single(
            cart.GetForeignKeys(),
            candidate => candidate.Properties.Count == 1
                && candidate.Properties[0].Name == nameof(Cart.DeliveryMethodId));

        Assert.Equal(nameof(DeliveryMethod), foreignKey.PrincipalEntityType.ShortName());
        Assert.Equal(DeleteBehavior.NoAction, foreignKey.DeleteBehavior);

        // Doc 08 §1.6 (DB7): and it leads an index.
        Assert.Contains(
            cart.GetIndexes(),
            index => index.Properties[0].Name == nameof(Cart.DeliveryMethodId));
    }

    /// <summary>
    /// Doc 08 §3.5: <c>Quantity smallint CK 1–10</c>, and <c>UnitPriceAtAdd decimal(18,2)</c> like every other
    /// money column (§1.4).
    /// </summary>
    [Fact]
    public void A_cart_item_has_the_types_doc_08_gives_it()
    {
        var item = Model.FindEntityType(typeof(CartItem))!;

        Assert.Equal("smallint", item.GetProperty(nameof(CartItem.Quantity)).GetColumnType());
        Assert.Equal("decimal(18,2)", item.GetProperty(nameof(CartItem.UnitPriceAtAdd)).GetColumnType());

        var check = Assert.Single(item.GetCheckConstraints());
        Assert.Equal("CK_CartItems_Quantity", check.Name);
        Assert.Equal("[Quantity] BETWEEN 1 AND 10", check.Sql);
    }

    /// <summary>
    /// Doc 08 §3.8 lists <c>Carts</c> and <c>CartItems</c> among the tables that are never audited, so neither
    /// carries the <c>[A]</c> columns. A bag is not an administrative change.
    /// </summary>
    [Fact]
    public void Neither_cart_table_carries_the_audit_columns()
    {
        foreach (var entity in CartEntities())
        {
            var columns = entity.GetProperties().Select(property => property.Name).ToList();
            Assert.DoesNotContain("CreatedByUserId", columns);
            Assert.DoesNotContain("UpdatedByUserId", columns);
            Assert.DoesNotContain("IsDeleted", columns);
        }
    }

    private static IEnumerable<IEntityType> SalesEntities() => Model.GetEntityTypes()
        .Where(entity => entity.GetSchema() == AppDbContext.SalesSchema);

    /// <summary>
    /// The two bag tables only. The cart assertions above are about rowversion and audit columns, and W3-03
    /// added sales tables that legitimately have both — so a test about carts has to say carts rather than
    /// sweep the schema.
    /// </summary>
    private static IEnumerable<IEntityType> CartEntities() =>
        [Model.FindEntityType(typeof(Cart))!, Model.FindEntityType(typeof(CartItem))!];
}
