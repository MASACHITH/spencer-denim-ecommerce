using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Doc 08 §7.4: the campaign pricing recompute reads its source tables <c>WITH (READCOMMITTEDLOCK)</c>, so
/// a product save that has not committed yet is waited for rather than priced from before it.
/// </summary>
/// <remarks>
/// <b>Why this test exists only now.</b> Until issue #163 every database this project ran on had
/// <c>READ_COMMITTED_SNAPSHOT OFF</c>, where a plain read already waits for an uncommitted writer and the
/// hint changes nothing — so no test could tell the hint from its absence, and <c>CampaignPricingRefresher</c>'s
/// claim about it was unverifiable (W3-05b's review said as much). With the option on, a read without the
/// hint takes the last committed version and does not wait.
/// <para>
/// <b>It means something only on a database with the option on</b>, which every test database now is
/// (<c>ReadCommittedSnapshotTests</c> fails otherwise). Measured: with the hint on
/// <c>catalog.ProductVariants</c> removed, this test fails in milliseconds.
/// </para>
/// <para>
/// The test changes one seeded product inside a transaction it rolls back.
/// </para>
/// </remarks>
public sealed class CampaignPricingRefreshLockTests(SqlServerDatabaseFixture sqlServer)
    : CheckoutCampaignTestBase(sqlServer)
{
    private const string ProductSlug = "relaxed-linen-blend-shirt";

    /// <summary>
    /// Long enough that an unblocked recompute of one product has certainly finished (it takes
    /// milliseconds), short enough not to slow the suite.
    /// </summary>
    private static readonly TimeSpan BlockedFor = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task The_recompute_waits_for_an_uncommitted_variant_price_change()
    {
        SqlServer.SkipIfUnavailable();
        var product = await ProductIdAsync(ProductSlug);

        await using var editor = new SqlConnection(SqlServer.ConnectionString);
        await editor.OpenAsync(Token);
        await using var edit = (SqlTransaction)await editor.BeginTransactionAsync(Token);
        await ExecuteAsync(editor, edit, "UPDATE catalog.ProductVariants SET PriceOverride = 100.00 WHERE ProductId = @product", product);

        using var scope = CreateScope();
        var refresh = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>()
            .RefreshProductsAsync([product], Token);

        var finishedWhileTheSaveWasOpen = await Task.WhenAny(refresh, Task.Delay(BlockedFor, Token)) == refresh;

        await edit.RollbackAsync(Token);
        await refresh;

        Assert.False(
            finishedWhileTheSaveWasOpen,
            "The recompute did not wait for an uncommitted change to the variants it prices: it read them from the "
            + "version store. WITH (READCOMMITTEDLOCK) on catalog.ProductVariants is what makes it wait (doc 08 §7.4).");
    }

    /// <summary>
    /// The product row's own hint. Added by the #163 tester: with <c>WITH (READCOMMITTEDLOCK)</c> removed from
    /// <c>catalog.Products</c> (the first branch), the test above still passed, because it only holds variants.
    /// </summary>
    [Fact]
    public async Task The_recompute_waits_for_an_uncommitted_base_price_change()
    {
        SqlServer.SkipIfUnavailable();
        var product = await ProductIdAsync(ProductSlug);

        await AssertRefreshWaitsForAsync(
            "UPDATE catalog.Products SET BasePrice = BasePrice + 1.00 WHERE Id = @id",
            product,
            product,
            "catalog.Products");
    }

    /// <summary>
    /// The campaign row's hint. Added by the #163 tester: with <c>WITH (READCOMMITTEDLOCK)</c> removed from
    /// <c>marketing.SaleCampaigns</c>, both tests above still passed. A staff member changing a live campaign's
    /// discount is exactly the save this recompute must not price from before.
    /// </summary>
    [Fact]
    public async Task The_recompute_waits_for_an_uncommitted_campaign_change()
    {
        SqlServer.SkipIfUnavailable();
        var product = await ProductIdAsync(ProductSlug);
        var campaign = await NewCampaignAsync(SaleDiscountType.Percent, 10m, [await OnProductAsync(ProductSlug)]);

        await AssertRefreshWaitsForAsync(
            "UPDATE marketing.SaleCampaigns SET DiscountValue = 20 WHERE Id = @id",
            campaign.Id,
            product,
            "marketing.SaleCampaigns");
    }

    private async Task AssertRefreshWaitsForAsync(string sql, int rowId, int product, string table)
    {
        await using var editor = new SqlConnection(SqlServer.ConnectionString);
        await editor.OpenAsync(Token);
        await using var edit = (SqlTransaction)await editor.BeginTransactionAsync(Token);
        await using (var command = new SqlCommand(sql, editor, edit))
        {
            command.Parameters.AddWithValue("@id", rowId);
            Assert.True(await command.ExecuteNonQueryAsync(Token) > 0, $"'{sql}' changed no rows.");
        }

        using var scope = CreateScope();
        var refresh = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>()
            .RefreshProductsAsync([product], Token);

        var finishedWhileTheSaveWasOpen = await Task.WhenAny(refresh, Task.Delay(BlockedFor, Token)) == refresh;

        await edit.RollbackAsync(Token);
        await refresh;

        Assert.False(
            finishedWhileTheSaveWasOpen,
            $"The recompute did not wait for an uncommitted change to {table}: it read it from the version store. "
            + $"WITH (READCOMMITTEDLOCK) on {table} is what makes it wait (doc 08 §7.4).");
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, int product)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@product", product);
        Assert.True(await command.ExecuteNonQueryAsync(Token) > 0, $"'{sql}' changed no rows.");
    }
}
