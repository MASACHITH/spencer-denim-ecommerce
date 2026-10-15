using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// W4-02 tester: <b>a sale-campaign line and a promo-code line, placed through the real checkout, in the
/// dashboard's department, category and product figures.</b> Doc 08 §6's worked example (a 20% campaign on
/// the shirt's category, a 10% promo capped at Rs. 2,000, Express delivery) is placed as a real order, its
/// dates moved into March 2006 (a year no other suite uses), and the dashboard for that day read through
/// the real reader. Each line must count for its own post-promo <c>LineTotal</c> — the campaign price minus
/// its share of the promo — and the lines plus delivery must be the grand total.
/// </summary>
public sealed class CampaignPromoReportingTests(SqlServerDatabaseFixture sqlServer)
    : CheckoutCampaignTestBase(sqlServer)
{
    private const string ShirtSlug = "relaxed-linen-blend-shirt";
    private const string JeansSlug = "high-rise-slim-fit-jeans";

    [Fact]
    public async Task A_campaign_line_and_a_promo_line_count_for_their_post_promo_line_totals()
    {
        var shirt = await BaseVariantIdAsync(ShirtSlug);
        var jeans = await BaseVariantIdAsync(JeansSlug);
        var colombo = await DistrictIdAsync("Colombo");
        var express = await DeliveryMethodIdAsync(DeliveryMethod.ExpressCode);

        await ArrangeStockAsync(shirt, 20);
        await ArrangeStockAsync(jeans, 20);

        var campaign = await NewCampaignAsync(SaleDiscountType.Percent, 20m, [await OnCategoryAsync("women", "shirts-blouses")]);
        var code = await NewPromoCodeAsync(
            discountType: PromoDiscountType.Percent,
            discountValue: 10m,
            minSpendAmount: 5_000m,
            maxDiscountAmount: 2_000m);

        var (_, api) = await NewMultiLineBagAsync((shirt, 2), (jeans, 1));
        var (_, session) = await api.GetAsync(colombo, express, Token, code.Code);
        var (response, confirmation) = await api.PlaceAsync(
            PromoBody(session, express, colombo, UniqueEmail("w402-report"), code.Code),
            Token);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(confirmation);

        var order = await OrderByNumberAsync(confirmation.OrderNumber);
        var lines = await LinesByVariantAsync(confirmation.OrderNumber);

        // The arrangement is the worked example: a campaign line and promo shares on both lines.
        Assert.Equal(campaign.Id, lines[shirt].SaleCampaignId);
        Assert.Null(lines[jeans].SaleCampaignId);
        Assert.True(lines[shirt].PromoDiscountAllocated > 0m);
        Assert.True(lines[jeans].PromoDiscountAllocated > 0m);
        Assert.Equal(1_627.40m, order.PromoDiscountAmount);

        var placedUtc = new DateTime(2006, 3, 15, 6, 0, 0, DateTimeKind.Utc);
        await MoveAsync(order.Id, placedUtc, placedUtc.AddMinutes(30));

        try
        {
            using var scope = SqlServer.CreateScope();
            var handler = new GetAdminDashboardQueryHandler(scope.ServiceProvider.GetRequiredService<IReportingReader>());
            var day = new DateOnly(2006, 3, 15);
            var dashboard = await handler.HandleAsync(new GetAdminDashboardQuery(new ReportPeriod(day, day)), Token);

            Assert.Equal(order.GrandTotal, dashboard.Kpis.GrossRevenue.Current);

            var products = dashboard.TopProducts.ToDictionary(row => row.ProductId);
            Assert.Equal(lines[shirt].LineTotal, products[lines[shirt].ProductId].Revenue.Current);
            Assert.Equal(lines[jeans].LineTotal, products[lines[jeans].ProductId].Revenue.Current);

            // Post-promo, and post-campaign: neither the list price nor the pre-promo subtotal.
            Assert.Equal(lines[shirt].LineSubtotal - lines[shirt].PromoDiscountAllocated, lines[shirt].LineTotal);
            Assert.NotEqual(lines[shirt].UnitListPrice * lines[shirt].Quantity, products[lines[shirt].ProductId].Revenue.Current);

            var items = dashboard.Departments.Sum(row => row.Revenue);
            Assert.Equal(order.ItemsSubtotal - order.PromoDiscountAmount, items);
            Assert.Equal(order.GrandTotal, items + order.DeliveryFeeAmount - order.DeliveryDiscountAmount);
            Assert.Equal(items, dashboard.TopCategories.Sum(row => row.Revenue));
            Assert.Equal(3, dashboard.TopProducts.Sum(row => row.Units));
        }
        finally
        {
            // Leave the order as checkout made it, unpaid, so the base's teardown can cancel it.
            await MoveAsync(order.Id, placedUtc, null);
        }
    }

    private async Task MoveAsync(int orderId, DateTime placedUtc, DateTime? paidUtc)
    {
        using var scope = SqlServer.CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .Where(row => row.Id == orderId)
            .ExecuteUpdateAsync(
                row => row
                    .SetProperty(o => o.PlacedAtUtc, placedUtc)
                    .SetProperty(o => o.PaidAtUtc, paidUtc),
                Token);

        Assert.Equal(1, moved);
    }
}
