using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Marketing.Promotions.Abstractions;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Doc 08 §7.4: the campaign pricing recompute reads its source tables <c>WITH (READCOMMITTEDLOCK)</c>, so
/// a product save that has not committed yet is waited for rather than priced from before it.
/// </summary>
/// <remarks>
/// <b>Why these tests exist only now.</b> Until issue #163 every database this project ran on had
/// <c>READ_COMMITTED_SNAPSHOT OFF</c>, where a plain read already waits for an uncommitted writer and the
/// hint changes nothing — so no test could tell the hint from its absence, and <c>CampaignPricingRefresher</c>'s
/// claim about it was unverifiable (W3-05b's review said as much). With the option on, a read without the
/// hint takes the last committed version and does not wait; these tests fail if the hint is dropped.
/// <para>
/// <b>The second test found a reference without the hint.</b> The "no active variant" fallback's
/// <c>NOT EXISTS</c> over <c>catalog.ProductVariants</c> was unhinted, so under the option it judged
/// "has an active variant" from a snapshot taken when the statement started, while the hinted branch
/// beside it waited and read the committed change. A save that deactivated a product's last active
/// variant mid-recompute then produced no row from either branch, and the product's summary was simply
/// not written.
/// </para>
/// <para>
/// Both tests change one seeded product inside a transaction they control and put it back. The suite runs
/// test classes one at a time (<c>AssemblyInfo.cs</c>), so nothing else reads the product meanwhile.
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

    [Fact]
    public async Task A_last_active_variant_deactivated_mid_recompute_still_leaves_the_product_priced()
    {
        SqlServer.SkipIfUnavailable();
        var product = await ProductIdAsync(ProductSlug);
        var active = await ActiveVariantIdsAsync(product);
        Assert.NotEmpty(active);

        var before = await PricingRefreshedAtAsync(product);

        try
        {
            await using var editor = new SqlConnection(SqlServer.ConnectionString);
            await editor.OpenAsync(Token);
            await using var edit = (SqlTransaction)await editor.BeginTransactionAsync(Token);
            await ExecuteAsync(editor, edit, "UPDATE catalog.ProductVariants SET IsActive = 0 WHERE ProductId = @product", product);

            using var scope = CreateScope();
            var refresher = scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>();
            var refresh = refresher.RefreshProductsAsync([product], Token);

            // The hinted branch is now waiting on the deactivated rows; commit, and let it finish.
            await Task.WhenAny(refresh, Task.Delay(BlockedFor, Token));
            await edit.CommitAsync(Token);
            await refresh;

            var during = await PricingRefreshedAtAsync(product);

            Assert.True(
                during > before,
                "The recompute that raced the deactivation wrote nothing for the product: one branch saw an active "
                + "variant from a snapshot and the other saw none after waiting. Every source table in the statement "
                + "needs WITH (READCOMMITTEDLOCK), including the fallback's NOT EXISTS (doc 08 §7.4).");
        }
        finally
        {
            await ReactivateAsync(active);

            using var scope = CreateScope();
            await scope.ServiceProvider.GetRequiredService<ICampaignPricingRefresher>()
                .RefreshProductsAsync([product], CancellationToken.None);
        }
    }

    private async Task<List<int>> ActiveVariantIdsAsync(int product)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.ProductId == product && variant.IsActive)
            .Select(variant => variant.Id)
            .ToListAsync(Token);
    }

    private async Task<DateTime> PricingRefreshedAtAsync(int product)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return await dbContext.ProductStats
            .AsNoTracking()
            .Where(stats => stats.ProductId == product)
            .Select(stats => stats.PricingRefreshedAtUtc)
            .SingleAsync(Token);
    }

    private async Task ReactivateAsync(List<int> variantIds)
    {
        using var scope = CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.ProductVariants
            .Where(variant => variantIds.Contains(variant.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(variant => variant.IsActive, true), CancellationToken.None);
    }

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql, int product)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@product", product);
        Assert.True(await command.ExecuteNonQueryAsync(Token) > 0, $"'{sql}' changed no rows.");
    }
}
