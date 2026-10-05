using Microsoft.EntityFrameworkCore;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// W3-03's migration against a real SQL Server: the twelve tables of doc 08 §3.5 exist, every column has the
/// type and nullability doc 08 gives it, the foreign keys behave as doc 08 §1.6 says, and
/// <c>Carts.DeliveryMethodId</c> arrived with its constraint.
/// <para>
/// <b>The column assertions compare whole sets, not samples.</b> An assertion per interesting column proves
/// those columns are right and says nothing about a column that was forgotten or one that was invented. Each
/// case here lists the table's columns in full, so both failures show up.
/// </para>
/// </summary>
public sealed class SalesSchemaTests(SqlServerDatabaseFixture sqlServer) : SalesSchemaTestBase(sqlServer)
{
    [Theory]
    [InlineData("NumberSeries")]
    [InlineData("PaymentProviders")]
    [InlineData("DeliveryMethods")]
    [InlineData("DeliveryMethodZoneRates")]
    [InlineData("PickupLocations")]
    [InlineData("Couriers")]
    [InlineData("Orders")]
    [InlineData("OrderLines")]
    [InlineData("OrderStatusHistory")]
    [InlineData("Payments")]
    [InlineData("PaymentEvents")]
    [InlineData("Refunds")]
    public async Task The_migration_created_the_table(string table)
    {
        SqlServer.SkipIfUnavailable();

        var exists = await DbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'sales' AND t.name = {table}
                """)
            .SingleAsync(Token);

        Assert.Equal(1, exists);
    }

    /// <summary>
    /// W2-08's <c>WishlistItems</c> is the one doc 08 §3.5 table this migration must <b>not</b> have created.
    /// Creating it here would mean two branches racing to build the same table.
    /// </summary>
    [Fact]
    public async Task The_wishlist_table_is_still_not_here()
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

        Assert.Equal(0, exists);
    }

    [Theory]
    [MemberData(nameof(TableColumns))]
    public async Task The_columns_match_doc_08(string table, string[] expected)
    {
        SqlServer.SkipIfUnavailable();

        var actual = await ColumnsAsync(table);

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    /// <summary>
    /// W2-09 left <c>Carts.DeliveryMethodId</c> out because it could not have carried its foreign key; this
    /// task adds both in one piece. The other two columns W2-09 deferred must still be absent:
    /// <c>ref.Districts</c> does not exist (so <c>DistrictId</c> still could not be constrained) and the promo
    /// text belongs to W3-05.
    /// </summary>
    [Fact]
    public async Task The_cart_gained_the_delivery_method_column_and_nothing_else()
    {
        SqlServer.SkipIfUnavailable();

        var columns = await ColumnsAsync("Carts");

        Assert.Equal(ExpectedCartColumns, columns);
    }

    /// <summary>The column is only half the job: doc 08 §3.5 gives it an FK to <c>sales.DeliveryMethods</c>.</summary>
    [Fact]
    public async Task The_cart_delivery_method_is_a_real_foreign_key()
    {
        SqlServer.SkipIfUnavailable();

        var constraint = await DbContext.Database
            .SqlQuery<string>($"""
                -- COLLATE DATABASE_DEFAULT on each sysname: the catalogue views use the server collation and
                -- the test database uses doc 08 §1.1's, so concatenating the two without this is an error.
                SELECT f.name COLLATE DATABASE_DEFAULT + ' -> '
                    + OBJECT_SCHEMA_NAME(f.referenced_object_id) COLLATE DATABASE_DEFAULT + '.'
                    + OBJECT_NAME(f.referenced_object_id) COLLATE DATABASE_DEFAULT + ' '
                    + f.delete_referential_action_desc COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.foreign_keys f
                WHERE f.parent_object_id = OBJECT_ID('sales.Carts')
                  AND EXISTS (
                      SELECT 1
                      FROM sys.foreign_key_columns fc
                      JOIN sys.columns c
                        ON c.object_id = fc.parent_object_id AND c.column_id = fc.parent_column_id
                      WHERE fc.constraint_object_id = f.object_id AND c.name = 'DeliveryMethodId')
                """)
            .SingleAsync(Token);

        Assert.Equal(
            "FK_Carts_DeliveryMethods_DeliveryMethodId -> sales.DeliveryMethods NO_ACTION",
            constraint);
    }

    /// <summary>
    /// And the constraint has teeth. A bag pointing at a delivery method that does not exist is the state the
    /// FK exists to make impossible — the reason W2-09 refused to add the column without it.
    /// </summary>
    [Fact]
    public async Task A_cart_cannot_point_at_a_delivery_method_that_does_not_exist()
    {
        SqlServer.SkipIfUnavailable();

        // Coalesced, because the seeded database has no delivery methods of its own: doc 08 §9 leaves them to
        // the admin milestone, and this suite only creates them where a test needs one.
        var highest = await DbContext.DeliveryMethods
            .Select(method => (int?)method.Id)
            .MaxAsync(Token) ?? 0;

        await Writer.AssertRejectedAsync(
            "sales.Carts",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Token"] = Guid.NewGuid(),
                ["DeliveryMethodId"] = highest + 1000,
                ["CreatedAtUtc"] = DateTime.UtcNow,
                ["LastActivityAtUtc"] = DateTime.UtcNow,
            },
            "FK_Carts_DeliveryMethods_DeliveryMethodId",
            Token);
    }

    /// <summary>
    /// Doc 08 §1.6: the cascade allow-list. <c>DeliveryMethodZoneRates</c> is on it, and this task adds no
    /// other cascade anywhere in the database — asserted over <i>every</i> foreign key, not just the new ones,
    /// so a cascade introduced by accident in the sales module shows up here as well as in the model test.
    /// </summary>
    [Fact]
    public async Task Only_the_cascades_doc_08_allows_exist()
    {
        SqlServer.SkipIfUnavailable();

        var cascades = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT OBJECT_SCHEMA_NAME(f.parent_object_id) COLLATE DATABASE_DEFAULT + '.'
                    + OBJECT_NAME(f.parent_object_id) COLLATE DATABASE_DEFAULT + ' -> '
                    + OBJECT_NAME(f.referenced_object_id) COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.foreign_keys f
                WHERE f.delete_referential_action_desc = 'CASCADE'
                """)
            .ToListAsync(Token);

        Assert.Equal(AllowedCascades, cascades.Order(StringComparer.Ordinal));
    }

    /// <summary>The cascade is real: deleting a method takes its rates, and nothing else cares.</summary>
    [Fact]
    public async Task Deleting_a_delivery_method_deletes_its_zone_rates()
    {
        SqlServer.SkipIfUnavailable();

        var methodId = await Writer.InsertAsync(
            "sales.DeliveryMethods",
            SalesRowWriter.DeliveryMethodRow(),
            Token);

        await Writer.InsertWithoutIdentityAsync(
            "sales.DeliveryMethodZoneRates",
            SalesRowWriter.ZoneRateRow(methodId, DeliveryZone.Colombo),
            Token);

        await DbContext.Database.ExecuteSqlAsync(
            $"DELETE FROM sales.DeliveryMethods WHERE Id = {methodId}",
            Token);

        var remaining = await DbContext.DeliveryMethodZoneRates
            .CountAsync(rate => rate.DeliveryMethodId == methodId, Token);

        Assert.Equal(0, remaining);
    }

    /// <summary>
    /// The converse, and the more important half: doc 08 §1.6 lists Orders and OrderLines among the rows that
    /// are <b>never deleted</b>, so the order → line relationship must <i>not</i> cascade. EF makes a required
    /// relationship cascade by default, so this is the assertion that catches the default coming back.
    /// </summary>
    [Fact]
    public async Task An_order_with_lines_cannot_be_deleted()
    {
        SqlServer.SkipIfUnavailable();

        var reference = await ReferenceAsync();
        var orderId = await InsertValidOrderAsync(reference);

        await Writer.InsertAsync(
            "sales.OrderLines",
            SalesRowWriter.OrderLineRow(
                orderId,
                reference.ProductId,
                reference.VariantId,
                reference.DepartmentId,
                reference.CategoryId),
            Token);

        var error = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(async () =>
            await DbContext.Database.ExecuteSqlAsync(
                $"DELETE FROM sales.Orders WHERE Id = {orderId}",
                Token));

        Assert.Contains("FK_OrderLines_Orders_OrderId", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Doc 08 §1.5: <c>[RV]</c> is <c>Orders</c>, <c>Payments</c>, <c>PaymentProviders</c>,
    /// <c>DeliveryMethods</c> and <c>DeliveryMethodZoneRates</c> — and nothing else in <c>sales</c>. Getting
    /// this wrong in either direction is a defect: a missing token loses the admin ETag, and an extra one on
    /// an append-only table invites a read-then-write where doc 08 wants a conditional UPDATE.
    /// </summary>
    [Fact]
    public async Task Only_the_rowversion_tables_of_doc_08_have_a_rowversion()
    {
        SqlServer.SkipIfUnavailable();

        var tables = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT t.name AS [Value]
                FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                WHERE s.name = 'sales' AND ty.name = 'timestamp'
                """)
            .ToListAsync(Token);

        Assert.Equal(RowVersionTables, tables.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Doc 08 §1.5's <c>[A]</c> marker, read strictly. <c>Payments</c>, <c>OrderLines</c>,
    /// <c>OrderStatusHistory</c>, <c>PaymentEvents</c>, <c>Refunds</c>, <c>NumberSeries</c>, <c>Carts</c> and
    /// <c>CartItems</c> carry <b>no</b> <c>…ByUserId</c> audit pair; the six administrative tables do.
    /// <c>Payments.ApprovedByUserId</c> and <c>Refunds.CreatedByUserId</c> are business facts, not the audit
    /// group, which is why this asserts on the pair and not on "any user column".
    /// </summary>
    [Fact]
    public async Task Only_the_audited_tables_of_doc_08_carry_the_audit_columns()
    {
        SqlServer.SkipIfUnavailable();

        var tables = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT t.name AS [Value]
                FROM sys.tables t
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'sales'
                  AND EXISTS (SELECT 1 FROM sys.columns c
                              WHERE c.object_id = t.object_id AND c.name = 'CreatedByUserId')
                  AND EXISTS (SELECT 1 FROM sys.columns c
                              WHERE c.object_id = t.object_id AND c.name = 'UpdatedByUserId')
                """)
            .ToListAsync(Token);

        Assert.Equal(AuditedTables, tables.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The foreign key columns whose <b>only</b> leading index is filtered, and for which that is correct
    /// rather than a gap — because the lookup predicate <i>implies</i> the filter, so the optimiser matches
    /// it. Key is <c>Table.Column</c>, value is the reason.
    /// <para>
    /// This list, and not a looser rule, is the whole content of the finding below. A filtered index covers
    /// an FK lookup only when <c>&lt;column&gt; = @x</c> provably implies the index's filter at compile time
    /// (doc 08 §1.6 and §10.2, DB28). <c>CustomerUserId = @x</c> implies <c>CustomerUserId IS NOT NULL</c>,
    /// so <c>UX_Carts_Customer</c> and <c>IX_Orders_Customer_Placed</c> really are enough and a plain
    /// duplicate of either would be dead weight. <c>OrderId = @o</c> implies neither <c>Status IN (0,1)</c>
    /// nor <c>IsOrderSettlement = 1</c>, so <c>UX_Payments_OneOpen</c> and <c>UX_Payments_OneSettlement</c>
    /// were <b>not</b> enough — and EF's FK-index convention, which suppresses its own index whenever some
    /// index already leads on the column and never looks at that index's filter, wrote no plain index for
    /// <c>Payments.OrderId</c> or <c>Payments.ProviderCode</c> at all. Both are declared explicitly now.
    /// </para>
    /// <para>
    /// Both entries here are doc-specified and both are <c>IS NOT NULL</c> on the FK column itself, which is
    /// the only shape of filter an equality lookup can imply.
    /// <see cref="Every_exempted_foreign_key_is_covered_by_an_implied_filter"/> checks that shape in the
    /// database rather than taking these sentences for it, so an entry cannot quietly start excusing a
    /// filter that implies nothing.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> FilteredLeadingIndexIsEnough =
        new(StringComparer.Ordinal)
        {
            ["Orders.CustomerUserId"] =
                "IX_Orders_Customer_Placed (CustomerUserId, PlacedAtUtc DESC) WHERE CustomerUserId IS NOT "
                + "NULL, doc 08 §3.5. Guest orders keep a NULL and are read by email, never by user id, so "
                + "the filter costs the index nothing it is used for — and 'my orders' passes a real user "
                + "id, which implies IS NOT NULL.",
            ["Carts.CustomerUserId"] =
                "UX_Carts_Customer (CustomerUserId) WHERE CustomerUserId IS NOT NULL, doc 08 §3.5 and "
                + "W2-09's. The filter is what makes 'one cart per customer' expressible at all, since a "
                + "plain unique index would allow exactly one guest cart in the whole table; and a cart is "
                + "looked up either by token or by a known user id, which implies the filter.",
        };

    /// <summary>
    /// Doc 08 §1.6 (DB7): every foreign key column leads an index. Asserted against the database rather than
    /// the model, because the model test cannot see an index a migration failed to write.
    /// <para>
    /// <b>Filtered indexes do not count</b> (<c>i.has_filter = 0</c>), except for
    /// <see cref="FilteredLeadingIndexIsEnough"/>. Without that clause this test passed while
    /// <c>Payments.OrderId</c> and <c>Payments.ProviderCode</c> had no index a lookup by order or by
    /// provider could use, because four filtered indexes lead on those two columns and the rule only asked
    /// whether <i>some</i> index led on them. That is how the gap got past a test written to catch it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_sales_foreign_key_column_leads_an_index()
    {
        SqlServer.SkipIfUnavailable();

        var offenders = await ForeignKeyColumnsWithNoUnfilteredLeadingIndexAsync();
        var gaps = offenders.Where(offender => !FilteredLeadingIndexIsEnough.ContainsKey(offender)).ToList();

        Assert.True(
            gaps.Count == 0,
            "Doc 08 §1.6 (DB7): every FK column leads an index, and the convention indexes exist on every "
            + "FK even where §3 does not list them. Each column below leads only filtered indexes, and a "
            + "filtered index is matched only when the lookup predicate implies the filter (DB28).\n\n"
            + "Declare the plain index with the named HasIndex overload — EF's convention will not, because "
            + "it sees an index already leading on the column and does not read its filter — or, if "
            + "'<column> = @x' really does imply every one of that column's filters, add it to "
            + "FilteredLeadingIndexIsEnough with the reason.\n\n"
            + string.Join('\n', gaps));
    }

    /// <summary>
    /// The exemptions are real and still shaped the way they claim: each is still a column whose only
    /// leading indexes are filtered, and every one of those filters is <c>IS NOT NULL</c> on the column
    /// itself — the only filter an equality lookup can imply.
    /// <para>
    /// This is the half that keeps the list from rotting in either direction. An entry that is no longer an
    /// offender is an exemption excusing nothing, which is how an allow-list turns into a no-op; and an
    /// entry whose filter has changed to something an equality predicate does <b>not</b> imply would
    /// otherwise go on excusing a real scan under a reason that had stopped being true.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_exempted_foreign_key_is_covered_by_an_implied_filter()
    {
        SqlServer.SkipIfUnavailable();

        var offenders = await ForeignKeyColumnsWithNoUnfilteredLeadingIndexAsync();

        foreach (var (qualified, reason) in FilteredLeadingIndexIsEnough)
        {
            Assert.True(
                offenders.Contains(qualified),
                $"sales.{qualified} is exempted from DB7 because \"{reason}\" — and it now has an unfiltered "
                + "leading index anyway, so the exemption excuses nothing and should go.");

            var parts = qualified.Split('.');
            var table = parts[0];
            var column = parts[1];

            var filters = await DbContext.Database
                .SqlQuery<string>($"""
                    SELECT ISNULL(i.filter_definition, '') COLLATE DATABASE_DEFAULT AS [Value]
                    FROM sys.indexes i
                    JOIN sys.index_columns ic
                      ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
                    JOIN sys.columns c
                      ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE i.object_id = OBJECT_ID('sales.' + {table}) AND c.name = {column}
                    """)
                .ToListAsync(Token);

            Assert.NotEmpty(filters);

            // The one filter shape that '<column> = @x' provably implies. Anything else — a status set, a
            // flag, another column's predicate — does not, and the exemption would be excusing a scan.
            var implied = $"([{column}] IS NOT NULL)";

            Assert.All(
                filters,
                filter => Assert.True(
                    filter == implied,
                    $"sales.{qualified} is exempted because every filter on it is implied by "
                    + $"'{column} = @x'. One of its leading indexes is now filtered \"{filter}\", which that "
                    + "predicate does not imply, so the column needs a plain index after all."));
        }
    }

    /// <summary>
    /// Every <c>sales</c> foreign key column that leads no <b>unfiltered</b> index, as <c>Table.Column</c>.
    /// Read from the database, not the model: the model cannot see an index a migration failed to write.
    /// </summary>
    private async Task<List<string>> ForeignKeyColumnsWithNoUnfilteredLeadingIndexAsync() =>
        await DbContext.Database
            .SqlQuery<string>($"""
                SELECT t.name COLLATE DATABASE_DEFAULT + '.' + c.name COLLATE DATABASE_DEFAULT AS [Value]
                FROM sys.foreign_keys f
                JOIN sys.tables t ON t.object_id = f.parent_object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.foreign_key_columns fc
                  ON fc.constraint_object_id = f.object_id AND fc.constraint_column_id = 1
                JOIN sys.columns c
                  ON c.object_id = fc.parent_object_id AND c.column_id = fc.parent_column_id
                WHERE s.name = 'sales'
                  AND NOT EXISTS (
                      SELECT 1
                      FROM sys.index_columns ic
                      JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                      WHERE ic.object_id = t.object_id
                        AND ic.key_ordinal = 1
                        AND ic.column_id = c.column_id
                        AND i.has_filter = 0)
                """)
            .ToListAsync(Token);

    /// <summary>
    /// The three doc 08 foreign keys that are columns without constraints, because the table they would point
    /// at does not exist yet. Named in a test so that creating <c>ref.Districts</c> or
    /// <c>marketing.PromoCodes</c> and forgetting the constraint is a failure rather than a silence.
    /// </summary>
    [Theory]
    [InlineData("Orders", "DeliveryDistrictId")]
    [InlineData("Orders", "PromoCodeId")]
    [InlineData("OrderLines", "SaleCampaignId")]
    [InlineData("PickupLocations", "DistrictId")]
    public async Task The_deferred_foreign_keys_are_indexed_columns_with_no_constraint(string table, string column)
    {
        SqlServer.SkipIfUnavailable();

        var constraints = await DbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.foreign_key_columns fc
                JOIN sys.columns c
                  ON c.object_id = fc.parent_object_id AND c.column_id = fc.parent_column_id
                WHERE fc.parent_object_id = OBJECT_ID('sales.' + {table}) AND c.name = {column}
                """)
            .SingleAsync(Token);

        Assert.Equal(0, constraints);

        var leadsAnIndex = await DbContext.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value]
                FROM sys.index_columns ic
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE ic.object_id = OBJECT_ID('sales.' + {table})
                  AND ic.key_ordinal = 1
                  AND c.name = {column}
                """)
            .SingleAsync(Token);

        Assert.True(leadsAnIndex >= 1, $"sales.{table}.{column} leads no index.");
    }

    /// <summary>Every index doc 08 §3.5 names by name, with the filter it names.</summary>
    /// <summary>
    /// Every index doc 08 section 3.5 names, pinned whole: <c>is_unique|key columns in order with direction|
    /// included columns|filter</c>. Each expectation was read from doc 08 and then confirmed against
    /// <c>sys.indexes</c>, so a mismatch means the schema drifted from the document, not that the test guessed.
    /// </summary>
    [Theory]
    [InlineData("Orders", "UX_Orders_OrderNumber", "1|OrderNumber||")]
    [InlineData("Orders", "UX_Orders_PublicId", "1|PublicId||")]
    [InlineData("Orders", "UX_Orders_CheckoutIdempotencyKey", "1|CheckoutIdempotencyKey||")]
    [InlineData("Orders", "UX_Orders_InvoiceNumber", "1|InvoiceNumber||([InvoiceNumber] IS NOT NULL)")]
    [InlineData("Orders", "IX_Orders_Customer_Placed", "0|CustomerUserId,PlacedAtUtc DESC|GrandTotal,PaidAtUtc,Status|([CustomerUserId] IS NOT NULL)")]
    [InlineData("Orders", "IX_Orders_Status_Placed", "0|Status,PlacedAtUtc DESC|GrandTotal,PaymentProviderCode|")]
    [InlineData("Orders", "IX_Orders_Placed", "0|PlacedAtUtc DESC|FirstName,GrandTotal,LastName,OrderNumber,Status|")]
    [InlineData("Orders", "IX_Orders_Pending_Due", "0|PaymentDueAtUtc|CaptureLockedUntilUtc|([Status]=(0))")]
    [InlineData("Orders", "IX_Orders_StockReserved", "0|Id||([StockState]=(0))")]
    [InlineData("Orders", "IX_Orders_Paid", "0|PaidAtUtc|CustomerUserId,DeliveryDistrictId,EmailNormalized,GrandTotal,PaymentProviderCode,Status|([PaidAtUtc] IS NOT NULL)")]
    [InlineData("Orders", "IX_Orders_Promo_Email", "0|PromoCodeId,EmailNormalized|PaidAtUtc,Status|([PromoCodeId] IS NOT NULL)")]
    [InlineData("Orders", "IX_Orders_Shipped", "0|ShippedAtUtc||([Status]=(3))")]
    [InlineData("Orders", "IX_Orders_EmailNormalized", "0|EmailNormalized||")]
    [InlineData("Orders", "IX_Orders_Phone", "0|Phone||")]
    [InlineData("OrderLines", "UX_OrderLines_Order_Line", "1|OrderId,LineNumber|CategoryId,DepartmentId,LineTotal,ProductId,Quantity,VariantId|")]
    [InlineData("OrderLines", "IX_OrderLines_Variant", "0|VariantId|OrderId,Quantity|")]
    [InlineData("OrderLines", "IX_OrderLines_Product", "0|ProductId|LineTotal,OrderId,Quantity|")]
    [InlineData("OrderStatusHistory", "IX_OrderStatusHistory_Order_Occurred", "0|OrderId,OccurredAtUtc||")]
    [InlineData("Payments", "UX_Payments_OneSettlement", "1|OrderId||([IsOrderSettlement]=(1))")]
    [InlineData("Payments", "UX_Payments_OneOpen", "1|OrderId||([Status] IN ((0), (1)))")]
    [InlineData("Payments", "IX_Payments_Capturing", "0|CaptureStartedAtUtc||([Status]=(2))")]
    [InlineData("Payments", "UX_Payments_Provider_Reference", "1|ProviderCode,ProviderReference||([ProviderReference] IS NOT NULL)")]
    [InlineData("Payments", "UX_Payments_Provider_Txn", "1|ProviderCode,ProviderTransactionId||([ProviderTransactionId] IS NOT NULL)")]
    [InlineData("Payments", "IX_Payments_Exceptions", "0|CreatedAtUtc||([Status]=(7))")]
    [InlineData("Payments", "IX_Payments_BankQueue", "0|CreatedAtUtc||([ProviderCode]='BankTransfer' AND [Status]=(1))")]
    [InlineData("PaymentEvents", "UX_PaymentEvents_IdempotencyKey", "1|IdempotencyKey||")]
    [InlineData("PaymentEvents", "IX_PaymentEvents_Retry", "0|NextAttemptAtUtc||([ProcessingStatus] IN ((0), (3)))")]
    [InlineData("Refunds", "IX_Refunds_Completed", "0|CompletedAtUtc||([Status]=(1))")]
    [InlineData("Refunds", "UX_Refunds_ProviderRefundId", "1|ProviderRefundId||([ProviderRefundId] IS NOT NULL)")]
    [InlineData("DeliveryMethods", "UX_DeliveryMethods_Code", "1|Code||")]
    [InlineData("Couriers", "UX_Couriers_Name", "1|Name||")]
    public async Task The_index_doc_08_names_exists_with_its_shape(string table, string index, string expected)
    {
        SqlServer.SkipIfUnavailable();

        // Tester round 1, Minor 1: this test used to compare the filter alone, so an index that existed but had
        // lost its uniqueness, or was keyed on the wrong column, or had dropped its INCLUDE list, still passed.
        // That is the exact failure mode this task already hit once — two HasIndex calls over the same property
        // collapsed into one and silently deleted UX_Payments_OneSettlement, while building clean and reporting
        // no pending model changes. So the shape is compared whole: uniqueness, key columns in order with their
        // direction, included columns, and the filter.
        var actual = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT CONVERT(varchar(11), i.is_unique)
                     + '|' + STUFF((
                         SELECT ',' + c.name + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                         FROM sys.index_columns ic
                         JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                         WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                         ORDER BY ic.key_ordinal
                         FOR XML PATH('')), 1, 1, '')
                     + '|' + ISNULL(STUFF((
                         SELECT ',' + c.name
                         FROM sys.index_columns ic
                         JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                         WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 1
                         ORDER BY c.name
                         FOR XML PATH('')), 1, 1, ''), '')
                     + '|' + ISNULL(i.filter_definition, '') AS [Value]
                FROM sys.indexes i
                WHERE i.object_id = OBJECT_ID('sales.' + {table}) AND i.name = {index}
                """)
            .ToListAsync(Token);

        Assert.Equal(expected, Assert.Single(actual));
    }

    public static TheoryData<string, string[]> TableColumns()
    {
        var data = new TheoryData<string, string[]>();

        data.Add(
            "NumberSeries",
            [
                "SeriesKey|varchar(30)|NOT NULL",
                "Prefix|varchar(10)|NOT NULL",
                "NextValue|int|NOT NULL",
                "UpdatedAtUtc|datetime2(3)|NOT NULL",
            ]);

        data.Add(
            "PaymentProviders",
            [
                "Code|varchar(30)|NOT NULL",
                "DisplayName|nvarchar(60)|NOT NULL",
                "IsEnabled|bit|NOT NULL",
                "DisplayOrder|smallint|NOT NULL",
                "CheckoutDescription|nvarchar(300)|NULL",
                .. AuditColumns,
                .. RowVersionColumn,
            ]);

        data.Add(
            "DeliveryMethods",
            [
                "Id|int|NOT NULL",
                "Code|varchar(20)|NOT NULL",
                "Name|nvarchar(60)|NOT NULL",
                "Description|nvarchar(300)|NULL",
                "IsEnabled|bit|NOT NULL",
                "RequiresAddress|bit|NOT NULL",
                "SortOrder|smallint|NOT NULL",
                .. AuditColumns,
                .. RowVersionColumn,
            ]);

        data.Add(
            "DeliveryMethodZoneRates",
            [
                "DeliveryMethodId|int|NOT NULL",
                "DeliveryZone|tinyint|NOT NULL",
                "IsAvailable|bit|NOT NULL",
                "Fee|decimal(18,2)|NOT NULL",
                "FreeAboveAmount|decimal(18,2)|NULL",
                "EstimatedDaysMin|tinyint|NOT NULL",
                "EstimatedDaysMax|tinyint|NOT NULL",
                .. AuditColumns,
                .. RowVersionColumn,
            ]);

        data.Add(
            "PickupLocations",
            [
                "Id|int|NOT NULL",
                "Name|nvarchar(100)|NOT NULL",
                "AddressLine|nvarchar(250)|NOT NULL",
                "City|nvarchar(80)|NOT NULL",
                "DistrictId|int|NOT NULL",
                "Phone|varchar(20)|NULL",
                "OpeningHours|nvarchar(200)|NULL",
                "IsActive|bit|NOT NULL",
                "SortOrder|smallint|NOT NULL",
                .. AuditColumns,
            ]);

        data.Add(
            "Couriers",
            [
                "Id|int|NOT NULL",
                "Name|nvarchar(80)|NOT NULL",
                "TrackingUrlTemplate|nvarchar(500)|NULL",
                "Phone|varchar(20)|NULL",
                "IsActive|bit|NOT NULL",
                "SortOrder|smallint|NOT NULL",
                .. AuditColumns,
            ]);

        data.Add(
            "Orders",
            [
                "Id|int|NOT NULL",
                "OrderNumber|varchar(20)|NOT NULL",
                "PublicId|uniqueidentifier|NOT NULL",
                "CheckoutIdempotencyKey|uniqueidentifier|NOT NULL",
                "CustomerUserId|int|NULL",
                "Status|tinyint|NOT NULL",
                "StockState|tinyint|NOT NULL",
                "CaptureLockedUntilUtc|datetime2(3)|NULL",
                "PaymentProviderCode|varchar(30)|NOT NULL",
                "Email|nvarchar(256)|NOT NULL",
                "EmailNormalized|nvarchar(256)|NOT NULL",
                "Phone|varchar(20)|NULL",
                "FirstName|nvarchar(80)|NOT NULL",
                "LastName|nvarchar(80)|NULL",
                "DeliveryDistrictId|int|NOT NULL",
                "DeliveryDistrictName|nvarchar(60)|NOT NULL",
                "DeliveryZone|tinyint|NOT NULL",
                "Ship_RecipientName|nvarchar(120)|NULL",
                "Ship_Phone|varchar(20)|NULL",
                "Ship_Line1|nvarchar(150)|NULL",
                "Ship_Line2|nvarchar(150)|NULL",
                "Ship_City|nvarchar(80)|NULL",
                "Ship_PostalCode|varchar(10)|NULL",
                "Ship_Landmark|nvarchar(150)|NULL",
                "DeliveryMethodId|int|NOT NULL",
                "PickupLocationId|int|NULL",
                "DeliveryMethodCode|varchar(20)|NOT NULL",
                "DeliveryMethodName|nvarchar(60)|NOT NULL",
                "PickupLocationName|nvarchar(100)|NULL",
                "EstimatedDaysMin|tinyint|NOT NULL",
                "EstimatedDaysMax|tinyint|NOT NULL",
                "Currency|char(3)|NOT NULL",
                "ItemsOriginalTotal|decimal(18,2)|NOT NULL",
                "ItemsListTotal|decimal(18,2)|NOT NULL",
                "ItemsSubtotal|decimal(18,2)|NOT NULL",
                "MarkdownDiscountTotal|decimal(18,2)|NOT NULL",
                "CampaignDiscountTotal|decimal(18,2)|NOT NULL",
                "PromoCodeId|int|NULL",
                "PromoCodeText|varchar(40)|NULL",
                "PromoDiscountAmount|decimal(18,2)|NOT NULL",
                "DeliveryFeeAmount|decimal(18,2)|NOT NULL",
                "DeliveryDiscountAmount|decimal(18,2)|NOT NULL",
                "TaxAmount|decimal(18,2)|NOT NULL",
                "GrandTotal|decimal(18,2)|NOT NULL",
                "RefundedAmount|decimal(18,2)|NOT NULL",
                "PlacedAtUtc|datetime2(3)|NOT NULL",
                "PaymentDueAtUtc|datetime2(3)|NOT NULL",
                "PaidAtUtc|datetime2(3)|NULL",
                "ShippedAtUtc|datetime2(3)|NULL",
                "DeliveredAtUtc|datetime2(3)|NULL",
                "ClosedAtUtc|datetime2(3)|NULL",
                "CourierId|int|NULL",
                "CourierName|nvarchar(80)|NULL",
                "TrackingNumber|varchar(60)|NULL",
                "InvoiceNumber|varchar(20)|NULL",
                "InvoiceIssuedAtUtc|datetime2(3)|NULL",
                "InvoiceSellerSnapshotJson|nvarchar(max)|NULL",
                "CustomerNote|nvarchar(500)|NULL",
                "InternalNote|nvarchar(2000)|NULL",
                "CancelReason|nvarchar(500)|NULL",
                "PlacedIpAddress|varchar(45)|NULL",
                "AnonymisedAtUtc|datetime2(3)|NULL",
                .. AuditColumns,
                .. RowVersionColumn,
            ]);

        data.Add(
            "OrderLines",
            [
                "Id|int|NOT NULL",
                "OrderId|int|NOT NULL",
                "LineNumber|smallint|NOT NULL",
                "ProductId|int|NOT NULL",
                "VariantId|int|NOT NULL",
                "DepartmentId|int|NOT NULL",
                "CategoryId|int|NOT NULL",
                "ProductName|nvarchar(200)|NOT NULL",
                "ProductCode|varchar(40)|NOT NULL",
                "Sku|varchar(64)|NOT NULL",
                "ColourName|nvarchar(60)|NOT NULL",
                "SizeLabel|nvarchar(20)|NOT NULL",
                "ThumbnailStorageKey|varchar(400)|NULL",
                "Quantity|smallint|NOT NULL",
                "UnitOriginalPrice|decimal(18,2)|NOT NULL",
                "UnitListPrice|decimal(18,2)|NOT NULL",
                "UnitPrice|decimal(18,2)|NOT NULL",
                "SaleCampaignId|int|NULL",
                "LineSubtotal|decimal(18,2)|NOT NULL",
                "PromoDiscountAllocated|decimal(18,2)|NOT NULL",
                "LineTotal|decimal(18,2)|NOT NULL",
                "RefundedQuantity|smallint|NOT NULL",
            ]);

        data.Add(
            "OrderStatusHistory",
            [
                "Id|bigint|NOT NULL",
                "OrderId|int|NOT NULL",
                "FromStatus|tinyint|NULL",
                "ToStatus|tinyint|NOT NULL",
                "Source|tinyint|NOT NULL",
                "ChangedByUserId|int|NULL",
                "Note|nvarchar(1000)|NULL",
                "OccurredAtUtc|datetime2(3)|NOT NULL",
            ]);

        data.Add(
            "Payments",
            [
                "Id|int|NOT NULL",
                "OrderId|int|NOT NULL",
                "ProviderCode|varchar(30)|NOT NULL",
                "Status|tinyint|NOT NULL",
                "IsOrderSettlement|bit|NOT NULL",
                "Amount|decimal(18,2)|NOT NULL",
                "Currency|char(3)|NOT NULL",
                "ProviderCurrency|char(3)|NULL",
                "ProviderAmount|decimal(18,2)|NULL",
                "ExchangeRate|decimal(18,6)|NULL",
                "FxBufferPercent|decimal(5,2)|NULL",
                "ProviderReference|varchar(100)|NULL",
                "ProviderTransactionId|varchar(100)|NULL",
                "PayerReference|nvarchar(100)|NULL",
                "ProofAssetId|int|NULL",
                "CaptureStartedAtUtc|datetime2(3)|NULL",
                "FailureCode|varchar(50)|NULL",
                "FailureMessage|nvarchar(500)|NULL",
                "ApprovedByUserId|int|NULL",
                "ApprovedAtUtc|datetime2(3)|NULL",
                "ApprovalNote|nvarchar(500)|NULL",
                "RefundedAmount|decimal(18,2)|NOT NULL",
                "CreatedAtUtc|datetime2(3)|NOT NULL",
                "UpdatedAtUtc|datetime2(3)|NULL",
                "CompletedAtUtc|datetime2(3)|NULL",
                .. RowVersionColumn,
            ]);

        data.Add(
            "PaymentEvents",
            [
                "Id|bigint|NOT NULL",
                "ProviderCode|varchar(30)|NOT NULL",
                "EventSource|tinyint|NOT NULL",
                "ProviderEventId|varchar(100)|NULL",
                "EventType|varchar(100)|NOT NULL",
                "IdempotencyKey|varchar(160)|NOT NULL",
                "PaymentId|int|NULL",
                "OrderId|int|NULL",
                "PayloadJson|nvarchar(max)|NOT NULL",
                "ProcessingStatus|tinyint|NOT NULL",
                "AttemptCount|smallint|NOT NULL",
                "NextAttemptAtUtc|datetime2(3)|NULL",
                "LastError|nvarchar(2000)|NULL",
                "ReceivedAtUtc|datetime2(3)|NOT NULL",
                "ProcessedAtUtc|datetime2(3)|NULL",
            ]);

        data.Add(
            "Refunds",
            [
                "Id|int|NOT NULL",
                "OrderId|int|NOT NULL",
                "PaymentId|int|NOT NULL",
                "Amount|decimal(18,2)|NOT NULL",
                "Currency|char(3)|NOT NULL",
                "ProviderAmount|decimal(18,2)|NULL",
                "Method|tinyint|NOT NULL",
                "ProviderRefundId|varchar(100)|NULL",
                "Status|tinyint|NOT NULL",
                "Reason|nvarchar(500)|NOT NULL",
                "RestockItems|bit|NOT NULL",
                "CreatedByUserId|int|NULL",
                "CreatedAtUtc|datetime2(3)|NOT NULL",
                "CompletedAtUtc|datetime2(3)|NULL",
            ]);

        return data;
    }

    /// <summary>Ordinal order, to match <c>ColumnsAsync</c>.</summary>
    private static readonly string[] ExpectedCartColumns =
    [
        "CreatedAtUtc|datetime2(3)|NOT NULL",
        "CustomerUserId|int|NULL",
        "DeliveryMethodId|int|NULL",
        "Id|int|NOT NULL",
        "LastActivityAtUtc|datetime2(3)|NOT NULL",
        "Token|uniqueidentifier|NOT NULL",
    ];

    /// <summary>Doc 08 §1.6's cascade allow-list, as it stands after W3-03, across the whole database.</summary>
    private static readonly string[] AllowedCascades =
    [
        "catalog.ProductCategories -> Products",
        "catalog.ProductColours -> Products",
        "catalog.ProductMedia -> Products",
        "catalog.ProductSearchIndex -> Products",
        "catalog.ProductStats -> Products",
        "catalog.RelatedProducts -> Products",
        "content.HomeSectionItems -> HomeSections",
        "identity.RoleClaims -> Roles",
        "identity.UserClaims -> Users",
        "identity.UserLogins -> Users",
        "identity.UserRoles -> Roles",
        "identity.UserRoles -> Users",
        "identity.UserTokens -> Users",
        "sales.CartItems -> Carts",
        "sales.DeliveryMethodZoneRates -> DeliveryMethods",
    ];

    /// <summary>Doc 08 §1.5's <c>[RV]</c> tables in the <c>sales</c> schema.</summary>
    private static readonly string[] RowVersionTables =
        ["DeliveryMethodZoneRates", "DeliveryMethods", "Orders", "PaymentProviders", "Payments"];

    /// <summary>Doc 08 §1.5's <c>[A]</c> tables in the <c>sales</c> schema.</summary>
    private static readonly string[] AuditedTables =
    [
        "Couriers",
        "DeliveryMethodZoneRates",
        "DeliveryMethods",
        "Orders",
        "PaymentProviders",
        "PickupLocations",
    ];

    /// <summary>Doc 08 §1.5's audit group, identical on every <c>[A]</c> table.</summary>
    private static string[] AuditColumns =>
    [
        "CreatedAtUtc|datetime2(3)|NOT NULL",
        "CreatedByUserId|int|NULL",
        "UpdatedAtUtc|datetime2(3)|NULL",
        "UpdatedByUserId|int|NULL",
    ];

    private static string[] RowVersionColumn => ["RowVersion|timestamp|NULL"];

    /// <summary>
    /// The table's columns as <c>name|type|nullability</c>, sorted <b>in C#</b> and not by SQL Server. The
    /// database collation does not sort an underscore where an ordinal comparison does, so ordering in SQL
    /// would make <c>Ship_City</c> and <c>ShippedAtUtc</c> compare differently on the two sides.
    /// </summary>
    private async Task<IEnumerable<string>> ColumnsAsync(string table)
    {
        var columns = await DbContext.Database
            .SqlQuery<string>($"""
                SELECT c.name COLLATE DATABASE_DEFAULT + '|' + ty.name COLLATE DATABASE_DEFAULT
                    + CASE
                        WHEN ty.name IN ('varchar', 'char')
                            THEN '(' + CASE WHEN c.max_length = -1 THEN 'max'
                                            ELSE CAST(c.max_length AS varchar(10)) END + ')'
                        WHEN ty.name IN ('nvarchar', 'nchar')
                            THEN '(' + CASE WHEN c.max_length = -1 THEN 'max'
                                            ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
                        WHEN ty.name IN ('decimal', 'numeric')
                            THEN '(' + CAST(c.precision AS varchar(10)) + ','
                                 + CAST(c.scale AS varchar(10)) + ')'
                        WHEN ty.name = 'datetime2' THEN '(' + CAST(c.scale AS varchar(10)) + ')'
                        ELSE ''
                      END
                    + '|' + CASE WHEN c.is_nullable = 1 THEN 'NULL' ELSE 'NOT NULL' END AS [Value]
                FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                WHERE s.name = 'sales' AND t.name = {table}
                """)
            .ToListAsync(Token);

        return columns.Order(StringComparer.Ordinal);
    }
}
