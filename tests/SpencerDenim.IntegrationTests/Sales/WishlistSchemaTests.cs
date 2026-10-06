using Microsoft.EntityFrameworkCore;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// W2-08's migration against a real SQL Server: <c>sales.WishlistItems</c> exists with the columns doc 08
/// §3.5 gives it, the indexes it names, and foreign keys with teeth.
/// <para>
/// <b>Why this table gets a suite of its own rather than rows in <c>SalesSchemaTests</c>.</b> That file is
/// W3-03's and asserts the twelve tables of its own migration, including a test that this one did
/// <i>not</i> exist yet — which W2-08 has inverted, with a pointer here. The database-wide sweeps there
/// (every FK leads an unfiltered index, only the allowed cascades exist, which tables carry a rowversion
/// and which are audited) cover this table for free, and deliberately: they enumerate nothing, so a table
/// added later is checked by them without being named.
/// </para>
/// <para>
/// <b>Nothing in the application writes this table yet</b> — <c>CustomerUserId</c> is <c>NOT NULL</c> and
/// there are no customer accounts until W3-01 / ACC-01, while a guest's wishlist is in the browser (doc 08
/// §1.7). These tests therefore write rows by hand, which is also the only way to prove a constraint has
/// teeth: an application that cannot insert cannot violate anything.
/// </para>
/// </summary>
public sealed class WishlistSchemaTests(SqlServerDatabaseFixture sqlServer) : SalesSchemaTestBase(sqlServer)
{
    /// <summary>Doc 08 §3.5's column list in full: five columns, no token, no audit, no rowversion.</summary>
    private static readonly string[] ExpectedColumns =
    [
        "AddedAtUtc|datetime2(3)|NOT NULL",
        "CustomerUserId|int|NOT NULL",
        "Id|int|NOT NULL",
        "ProductColourId|int|NULL",
        "ProductId|int|NOT NULL",
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "IX_WishlistItems_Product (ProductId)",
        "IX_WishlistItems_ProductColour (ProductColourId)",
        "PK_WishlistItems (Id) UNIQUE",
        "UX_WishlistItems_Customer_Product (CustomerUserId, ProductId) UNIQUE",
    ];

    private static readonly string[] ExpectedForeignKeys =
    [
        "FK_WishlistItems_ProductColours_ProductColourId -> catalog.ProductColours NO_ACTION",
        "FK_WishlistItems_Products_ProductId -> catalog.Products NO_ACTION",
        "FK_WishlistItems_Users_CustomerUserId -> identity.Users NO_ACTION",
    ];

    [Fact]
    public async Task The_migration_created_the_table()
    {
        SqlServer.SkipIfUnavailable();

        var exists = await DbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'sales' AND t.name = 'WishlistItems'
                """)
            .SingleAsync(Token);

        Assert.Equal(1, exists);
    }

    /// <summary>
    /// The whole column set, not a sample: an assertion per interesting column proves those columns are
    /// right and says nothing about one that was forgotten or one that was invented.
    /// <para>
    /// <b><c>CustomerUserId</c> is <c>NOT NULL</c>, and that is the load-bearing line here.</b> Doc 08 §3.5
    /// gives it that way, and it is the difference between this table and <c>Carts</c>: a guest cart needs
    /// a row with a NULL owner and a cookie token, a guest wishlist does not exist server-side at all
    /// (§1.7). <b>There is no <c>Token</c> column</b>, for the same reason.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_columns_match_doc_08()
    {
        SqlServer.SkipIfUnavailable();

        var columns = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT c.name COLLATE DATABASE_DEFAULT + '|' + ty.name COLLATE DATABASE_DEFAULT
                    + CASE WHEN ty.name = 'datetime2' THEN '(' + CAST(c.scale AS varchar(10)) + ')' ELSE '' END
                    + '|' + CASE WHEN c.is_nullable = 1 THEN 'NULL' ELSE 'NOT NULL' END AS [Value]
                FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                WHERE s.name = 'sales' AND t.name = 'WishlistItems'
                """)
            .ToListAsync(Token);

        Assert.Equal(ExpectedColumns, columns.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Doc 08 §3.5 names <c>UX_WishlistItems_Customer_Product</c>; doc 08 §1.6 (DB7) requires the other
    /// two, because <c>ProductId</c> is only the <i>second</i> key of the unique index and a second key is
    /// not a leading one.
    /// <para>
    /// <b>That none of them is filtered is asserted, not just the names.</b> <c>Carts</c> has to filter its
    /// equivalent index and therefore needs an entry in
    /// <c>SalesSchemaTests.FilteredLeadingIndexIsEnough</c> to excuse it under DB7. This table needs none,
    /// and that is only true while the index stays unfiltered — so that is what is checked here, rather
    /// than left to the other suite's sweep to discover later.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_indexes_match_doc_08_and_none_of_them_is_filtered()
    {
        SqlServer.SkipIfUnavailable();

        var indexes = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT i.name COLLATE DATABASE_DEFAULT + ' ('
                    + STUFF((
                        SELECT ', ' + c.name COLLATE DATABASE_DEFAULT
                        FROM sys.index_columns ic
                        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                        ORDER BY ic.key_ordinal
                        FOR XML PATH('')), 1, 2, '')
                    + ')'
                    + CASE WHEN i.is_unique = 1 THEN ' UNIQUE' ELSE '' END
                    + CASE WHEN i.has_filter = 1 THEN ' FILTERED' ELSE '' END AS [Value]
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID('sales.WishlistItems') AND i.name IS NOT NULL
                """)
            .ToListAsync(Token);

        Assert.Equal(ExpectedIndexes, indexes.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// All three foreign keys, with their delete rule. <c>NO_ACTION</c> on every one: doc 08 §1.6's
    /// cascade allow-list does not name <c>WishlistItems</c>, a customer is anonymised rather than deleted,
    /// and a saved product or colourway is withdrawn from sale rather than removed.
    /// </summary>
    [Fact]
    public async Task Every_foreign_key_points_where_doc_08_says_and_cascades_nothing()
    {
        SqlServer.SkipIfUnavailable();

        var keys = await DbContext.Database
            .SqlQuery<string>($"""
                -- COLLATE DATABASE_DEFAULT on each sysname: the catalogue views use the server collation and
                -- the test database uses doc 08 §1.1's, so concatenating the two without this is an error.
                SELECT f.name COLLATE DATABASE_DEFAULT + ' -> '
                    + OBJECT_SCHEMA_NAME(f.referenced_object_id) COLLATE DATABASE_DEFAULT + '.'
                    + OBJECT_NAME(f.referenced_object_id) COLLATE DATABASE_DEFAULT + ' '
                    + f.delete_referential_action_desc COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.foreign_keys f
                WHERE f.parent_object_id = OBJECT_ID('sales.WishlistItems')
                """)
            .ToListAsync(Token);

        Assert.Equal(ExpectedForeignKeys, keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Doc 11 §553: "one entry per product per customer, optional colour". The <i>colour</i> is not part of
    /// the key, which is what makes doc 08 §3.5's login merge a plain insert-where-not-exists with nothing
    /// to reconcile — so saving the same product in a second colourway has to be refused, not stored twice.
    /// </summary>
    [Fact]
    public async Task One_customer_cannot_save_the_same_product_twice()
    {
        SqlServer.SkipIfUnavailable();

        var (productId, colourId) = await AnyProductWithColourAsync();
        var userId = await NewCustomerAsync();

        await Writer.InsertAsync("sales.WishlistItems", Row(userId, productId, colourId), Token);

        // A second row for the same (customer, product) — and with a *different* colourway, to prove the
        // colour is not silently part of the key.
        await Writer.AssertRejectedAsync(
            "sales.WishlistItems",
            Row(userId, productId, null),
            "UX_WishlistItems_Customer_Product",
            Token);
    }

    [Fact]
    public async Task A_wishlist_row_cannot_name_a_customer_that_does_not_exist()
    {
        SqlServer.SkipIfUnavailable();

        var (productId, _) = await AnyProductWithColourAsync();
        var highest = await DbContext.Users.Select(user => (int?)user.Id).MaxAsync(Token) ?? 0;

        await Writer.AssertRejectedAsync(
            "sales.WishlistItems",
            Row(highest + 1000, productId, null),
            "FK_WishlistItems_Users_CustomerUserId",
            Token);
    }

    [Fact]
    public async Task A_wishlist_row_cannot_name_a_product_that_does_not_exist()
    {
        SqlServer.SkipIfUnavailable();

        var userId = await NewCustomerAsync();
        var highest = await DbContext.Products
            .IgnoreQueryFilters()
            .Select(product => (int?)product.Id)
            .MaxAsync(Token) ?? 0;

        await Writer.AssertRejectedAsync(
            "sales.WishlistItems",
            Row(userId, highest + 1000, null),
            "FK_WishlistItems_Products_ProductId",
            Token);
    }

    [Fact]
    public async Task A_wishlist_row_cannot_name_a_colourway_that_does_not_exist()
    {
        SqlServer.SkipIfUnavailable();

        var (productId, _) = await AnyProductWithColourAsync();
        var userId = await NewCustomerAsync();
        var highest = await DbContext.ProductColours.Select(colour => (int?)colour.Id).MaxAsync(Token) ?? 0;

        await Writer.AssertRejectedAsync(
            "sales.WishlistItems",
            Row(userId, productId, highest + 1000),
            "FK_WishlistItems_ProductColours_ProductColourId",
            Token);
    }

    /// <summary>
    /// The colour really is optional (doc 08 §3.5: nullable), which is what the heart on a listing card
    /// needs — <c>ProductCardDto</c> carries colour dots and no selection, so a card genuinely does not
    /// know which colourway the shopper meant.
    /// </summary>
    [Fact]
    public async Task A_product_can_be_saved_with_no_colourway()
    {
        SqlServer.SkipIfUnavailable();

        var (productId, _) = await AnyProductWithColourAsync();
        var userId = await NewCustomerAsync();

        var id = await Writer.InsertAsync("sales.WishlistItems", Row(userId, productId, null), Token);

        var saved = await DbContext.WishlistItems
            .AsNoTracking()
            .Where(item => item.Id == id)
            .SingleAsync(Token);

        Assert.Null(saved.ProductColourId);
        Assert.Equal(productId, saved.ProductId);
    }

    /// <summary>
    /// A row with no <c>AddedAtUtc</c> at all, to prove the default is in the database and not only in the
    /// model — which the login merge (W3-01) will rely on, since doc 08 §3.5 has it insert with SQL rather
    /// than through EF.
    /// </summary>
    [Fact]
    public async Task The_added_time_has_a_database_default()
    {
        SqlServer.SkipIfUnavailable();

        var (productId, _) = await AnyProductWithColourAsync();
        var userId = await NewCustomerAsync();

        var id = await Writer.InsertAsync(
            "sales.WishlistItems",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["CustomerUserId"] = userId,
                ["ProductId"] = productId,
            },
            Token);

        var stamped = await DbContext.WishlistItems
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => item.AddedAtUtc)
            .SingleAsync(Token);

        Assert.InRange(stamped, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddMinutes(5));
    }

    /// <summary>
    /// Two different customers saving the same product is the ordinary case, and the unique index must not
    /// refuse it. Without this, an index mistakenly declared on <c>ProductId</c> alone would pass every
    /// other test in this file.
    /// </summary>
    [Fact]
    public async Task Two_customers_can_save_the_same_product()
    {
        SqlServer.SkipIfUnavailable();

        var (productId, _) = await AnyProductWithColourAsync();
        var userIds = new[] { await NewCustomerAsync(), await NewCustomerAsync() };

        await Writer.InsertAsync("sales.WishlistItems", Row(userIds[0], productId, null), Token);
        await Writer.InsertAsync("sales.WishlistItems", Row(userIds[1], productId, null), Token);

        var saved = await DbContext.WishlistItems
            .AsNoTracking()
            .CountAsync(
                item => item.ProductId == productId && userIds.Contains(item.CustomerUserId),
                Token);

        Assert.Equal(2, saved);
    }

    private static Dictionary<string, object?> Row(int customerUserId, int productId, int? productColourId) =>
        new(StringComparer.Ordinal)
        {
            ["CustomerUserId"] = customerUserId,
            ["ProductId"] = productId,
            ["ProductColourId"] = productColourId,
            ["AddedAtUtc"] = DateTime.UtcNow,
        };

    /// <summary>
    /// A fresh customer row, written by hand.
    /// <para>
    /// <b>There is no other way to get one.</b> The test database has no users at all — the fixture
    /// migrates and seeds the catalogue, and <c>AdminSeedHostedService</c> deliberately does not run — and
    /// there is no customer registration anywhere in the application until W3-01 / ACC-01. So the owner of
    /// a wishlist row is inserted directly, with <c>UserType = 1</c> (Customer, doc 08 §3.1), which is the
    /// only kind of user this table is ever meant to hold.
    /// </para>
    /// <para>
    /// A <b>new</b> one per call, with a unique address: <c>UX_Users_NormalizedEmail</c> is filtered on NOT
    /// NULL, the suite shares one database and runs sequentially, and doc 08 §1.6 never deletes a user — so
    /// a fixed address would make the second test in a run fail on the first one's leftovers. It is also
    /// what makes "two customers can save the same product" a real two-customer test rather than one that
    /// passes because the database happened to hold two rows.
    /// </para>
    /// </summary>
    private async Task<int> NewCustomerAsync()
    {
        var email = $"{SalesRowWriter.Unique("wish")}@example.invalid";

        return await Writer.InsertAsync(
            // Bracketed, because `identity` is a reserved word in T-SQL and `SalesRowWriter` passes the
            // table name through verbatim — every other caller is in `sales`, where that never came up.
            "[identity].[Users]",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["UserType"] = (byte)1,
                ["UserName"] = email,
                ["NormalizedUserName"] = email.ToUpperInvariant(),
                ["Email"] = email,
                ["NormalizedEmail"] = email.ToUpperInvariant(),
                ["EmailConfirmed"] = false,
                ["PhoneNumberConfirmed"] = false,
                ["TwoFactorEnabled"] = false,
                ["LockoutEnabled"] = true,
                ["AccessFailedCount"] = 0,
                ["FirstName"] = "Wishlist",
                ["LastName"] = "Tester",
                ["IsActive"] = true,
                ["MustChangePassword"] = false,
                ["SecurityStamp"] = Guid.NewGuid().ToString("N"),
                ["ConcurrencyStamp"] = Guid.NewGuid().ToString("N"),
                ["CreatedAtUtc"] = DateTime.UtcNow,
            },
            Token);
    }

    /// <summary>
    /// A seeded product together with one of its own colourways, taken from <c>ProductColours</c> rather
    /// than from <c>Products</c> — which guarantees the pair really belongs together, so the
    /// optional-colour tests cannot pass by pointing at a colour of some other product.
    /// </summary>
    private async Task<(int ProductId, int ColourId)> AnyProductWithColourAsync()
    {
        var pair = await DbContext.ProductColours
            .AsNoTracking()
            .OrderBy(colour => colour.Id)
            .Select(colour => new { colour.ProductId, colour.Id })
            .FirstAsync(Token);

        return (pair.ProductId, pair.Id);
    }
}
