using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>
/// Doc 08 §7 and §10.2 (DB28): the indexes the listing relies on exist and are actually chosen, and a
/// listing request over the seeded catalogue is comfortably fast.
/// <para>
/// The plan is checked with <c>SET SHOWPLAN_XML</c> on the shape of SQL the read store emits, because
/// an execution plan is the only proof that a filtered index was matched.
/// </para>
/// </summary>
public sealed class CatalogQueryPlanTests(SqlServerDatabaseFixture sqlServer) : CatalogTestBase(sqlServer)
{
    /// <summary>The listing shape of doc 08 §7.1, with literal published and deleted predicates.</summary>
    private const string ListingSql = """
        SELECT p.Id, p.Name, p.Slug, p.CardImageAssetId, p.HoverImageAssetId, p.NewUntilUtc, p.BadgeOverride,
               s.MinSalePrice, s.MaxOriginalPrice, s.MaxDiscountPercent, s.RatingAverage, s.RatingCount
        FROM catalog.Products p
        JOIN catalog.ProductStats s ON s.ProductId = p.Id
        WHERE p.IsPublished = 1 AND p.IsDeleted = 0
          AND p.DepartmentId = 1
          AND s.MinSalePrice BETWEEN 1000 AND 20000
          AND EXISTS (SELECT 1 FROM catalog.ProductVariants v
                      JOIN inventory.StockLevels sl ON sl.VariantId = v.Id
                      WHERE v.ProductId = p.Id AND v.IsActive = 1 AND sl.Available > 0)
          AND EXISTS (SELECT 1 FROM catalog.ProductColours c
                      WHERE c.ProductId = p.Id AND c.ColourFamilyId IN (7, 8))
        ORDER BY s.MinSalePrice, p.Id
        OFFSET 0 ROWS FETCH NEXT 24 ROWS ONLY;
        """;

    [Theory]
    [InlineData("catalog", "Products", "IX_Products_Listing")]
    [InlineData("catalog", "Products", "IX_Products_NewUntilUtc")]
    [InlineData("catalog", "Products", "UX_Products_Slug")]
    [InlineData("catalog", "Products", "UX_Products_ProductCode")]
    [InlineData("catalog", "ProductStats", "IX_ProductStats_MinSalePrice")]
    [InlineData("catalog", "ProductVariants", "IX_ProductVariants_Size")]
    [InlineData("catalog", "ProductVariants", "IX_ProductVariants_Product")]
    [InlineData("catalog", "ProductVariants", "UX_ProductVariants_Sku")]
    [InlineData("catalog", "ProductColours", "IX_ProductColours_ColourFamily")]
    [InlineData("catalog", "Categories", "IX_Categories_Path")]
    [InlineData("catalog", "Categories", "UX_Categories_Dept_Parent_Slug")]
    [InlineData("inventory", "StockLevels", "IX_StockLevels_Available")]
    [InlineData("media", "MediaAssets", "IX_MediaAssets_Library")]
    public async Task The_index_doc_08_section_7_specifies_exists_in_the_database(string schema, string table, string index)
    {
        SqlServer.SkipIfUnavailable();

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sys.indexes i
            JOIN sys.objects o ON o.object_id = i.object_id
            JOIN sys.schemas sc ON sc.schema_id = o.schema_id
            WHERE sc.name = @schema AND o.name = @table AND i.name = @index;
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@table", table);
        command.Parameters.AddWithValue("@index", index);

        var found = Convert.ToInt32(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);

        Assert.Equal(1, found);
    }

    private static readonly string[] ListingIndexes =
    [
        "IX_Products_Listing",
        "IX_ProductStats_MinSalePrice",
        "IX_ProductVariants_Product",
        "IX_ProductColours_ColourFamily",
    ];

    /// <summary>
    /// DB28: a filtered index is only usable when the query's predicate provably implies the filter at
    /// compile time. An <c>INDEX(...)</c> hint is the definitive check: if the literal
    /// <c>IsPublished = 1 AND IsDeleted = 0</c> predicate did not imply the index filter, SQL Server
    /// would refuse the plan with error 8622 instead of running the query.
    /// </summary>
    [Fact]
    public async Task The_literal_published_predicate_makes_the_filtered_listing_index_usable()
    {
        SqlServer.SkipIfUnavailable();

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM catalog.Products p WITH (INDEX(IX_Products_Listing))
            WHERE p.IsPublished = 1 AND p.IsDeleted = 0 AND p.DepartmentId = 1;
            """;

        var matched = Convert.ToInt32(await command.ExecuteScalarAsync(Token), CultureInfo.InvariantCulture);

        TestContext.Current.TestOutputHelper!.WriteLine(
            $"IX_Products_Listing (filtered on IsPublished = 1 AND IsDeleted = 0) served the query and matched {matched} rows.");

        Assert.True(matched > 0);
    }

    /// <summary>
    /// A parameterised predicate must NOT be able to use the filtered index; this pins DB28 from the
    /// other side, so a future refactor that turns the literals into parameters fails here.
    /// </summary>
    [Fact]
    public async Task A_parameterised_published_predicate_cannot_use_the_filtered_index()
    {
        SqlServer.SkipIfUnavailable();

        await using var connection = await OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @published bit = 1, @deleted bit = 0;
            SELECT COUNT(*)
            FROM catalog.Products p WITH (INDEX(IX_Products_Listing))
            WHERE p.IsPublished = @published AND p.IsDeleted = @deleted;
            """;

        var exception = await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(
            () => command.ExecuteScalarAsync(Token));

        TestContext.Current.TestOutputHelper!.WriteLine(
            "A parameterised predicate cannot match the filtered index (SQL Server error " +
            $"{exception.Number}), which is exactly why the read store writes the filter values as constants.");

        Assert.Equal(8622, exception.Number);
    }

    [Fact]
    public async Task The_listing_plan_uses_indexes_and_never_a_table_scan()
    {
        SqlServer.SkipIfUnavailable();

        var plan = await GetPlanAsync(ListingSql);
        var output = TestContext.Current.TestOutputHelper!;

        var used = ListingIndexes
            .Where(index => plan.Contains($"Index=\"[{index}]\"", StringComparison.Ordinal))
            .ToList();

        output.WriteLine("Indexes chosen by the listing plan: " + string.Join(", ", used));
        output.WriteLine(
            "Note: over the seeded catalogue (45 products, 480 variants) the optimiser reads Products " +
            "through its clustered index rather than IX_Products_Listing, because the whole table is a " +
            "handful of pages. The hint test above proves the filtered index is matchable, and it takes " +
            "over once the catalogue grows.");

        Assert.Contains("IX_ProductVariants_Product", used);
        Assert.Contains("IX_ProductColours_ColourFamily", used);
        Assert.DoesNotContain("<TableScan", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_listing_endpoint_answers_well_inside_the_budget()
    {
        var api = CreateApi();
        var output = TestContext.Current.TestOutputHelper!;

        // Warm up the host, the EF model and the SQL plan cache before timing.
        await api.ListAsync("?department=women&sort=price-asc&pageSize=24", Token);

        var timings = new List<double>(10);
        for (var run = 0; run < 10; run++)
        {
            var stopwatch = Stopwatch.StartNew();
            var page = await api.ListAsync("?department=women&colour=navy&inStock=true&sort=price-asc&pageSize=24", Token);
            stopwatch.Stop();

            Assert.NotEmpty(page.Items);
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var median = timings[timings.Count / 2];
        output.WriteLine(
            $"Listing request over {SqlServer.SeedCounts.Products} products: " +
            $"min {timings[0]:F1} ms, median {median:F1} ms, max {timings[^1]:F1} ms (10 runs, in-process host).");

        // Generous: the point is to catch an accidental N+1 or a missing index, not to benchmark.
        Assert.True(median < 500, $"The median listing request took {median:F1} ms.");
    }

    [Fact]
    public async Task The_listing_runs_a_bounded_number_of_statements_rather_than_one_per_row()
    {
        SqlServer.SkipIfUnavailable();

        await using var connection = await OpenAsync();

        var before = await ReadBatchCountAsync(connection);

        var api = CreateApi();
        var page = await api.ListAsync("?department=women&pageSize=24", Token);

        var after = await ReadBatchCountAsync(connection);
        var statements = after - before;

        TestContext.Current.TestOutputHelper!.WriteLine(
            $"{page.Items.Count} cards returned using {statements} SQL batches (filter lookups + count + page + best-seller thresholds).");

        // A per-row query would be 24 or more on top of the fixed ones.
        Assert.True(statements < 15, $"The listing issued {statements} SQL batches for {page.Items.Count} rows.");
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(SqlServer.ConnectionString);
        await connection.OpenAsync(Token);
        return connection;
    }

    private async Task<string> GetPlanAsync(string sql)
    {
        await using var connection = await OpenAsync();

        await using (var on = connection.CreateCommand())
        {
            on.CommandText = "SET SHOWPLAN_XML ON;";
            await on.ExecuteNonQueryAsync(Token);
        }

        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // A constant query defined in this test class.
        command.CommandText = sql;
#pragma warning restore CA2100
        await using var reader = await command.ExecuteReaderAsync(Token);

        Assert.True(await reader.ReadAsync(Token));
        return reader.GetString(0);
    }

    private static async Task<long> ReadBatchCountAsync(SqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cntr_value FROM sys.dm_os_performance_counters WHERE counter_name = 'Batch Requests/sec';";

        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
}
