using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Identity;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Identity;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// <b>The figures people decide on must agree with each other.</b> One quarter (January–March 2015) of mixed
/// orders, and the same revenue read from every screen that shows it:
/// <list type="number">
/// <item><description>the dashboard's Gross revenue KPI;</description></item>
/// <item><description>the monthly Revenue &amp; Orders trend, three points summed;</description></item>
/// <item><description>the sales report's total, grouped by day, by ISO week and by month;</description></item>
/// <item><description>the department breakdown <b>plus</b> the sales report's delivery column — departments
/// exclude delivery by definition (doc 11 P16), so they reconcile to items, and items + delivery to gross;</description></item>
/// <item><description>the customers report's total, the payment-method split and the district split;</description></item>
/// <item><description>and the W3-07 customer list's lifetime spend for one account, against the customers
/// report's figure for the same address — the two screens use the same gross rule.</description></item>
/// </list>
/// Each is also checked against the arithmetic of the orders as written, so a rule that is consistently
/// wrong everywhere still fails.
/// </summary>
public sealed class ReportingReconciliationTests(SqlServerDatabaseFixture sqlServer) : ReportingTestBase(sqlServer)
{
    /// <summary>Distinct from the order (24), bank-transfer (12) and customer (36) suites' variant bands.</summary>
    private const int VariantBandOffset = 48;

    [Fact]
    public async Task Revenue_for_one_period_is_the_same_on_every_screen()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var picks = catalogue.OnePerDepartment;
        var (first, second) = catalogue.TwoInOneDepartment();
        var accountEmail = Email("account");
        var accountId = await CreateCustomerAsync(host, accountEmail);

        var paid = new List<InsertedOrder>();

        // January: two departments, a promo, a delivery discount, PayPal, a registered customer.
        paid.Add(await InsertOrderAsync(catalogue, new OrderSpec(accountEmail, Colombo(2015, 1, 1, 0, 0), Colombo(2015, 1, 1, 0, 0), OrderStatus.Delivered, [new LineSpec(first, 2, 2500m, Promo: 500m), new LineSpec(picks[1], 1, 1200m)], DeliveryFee: 600m, DeliveryDiscount: 600m, CustomerUserId: accountId, District: "Kandy")));
        paid.Add(await InsertOrderAsync(catalogue, new OrderSpec(Email("b"), Colombo(2015, 1, 15), Colombo(2015, 1, 16), OrderStatus.Shipped, [new LineSpec(second, 3, 990m)], Provider: PaymentProviderCodes.PayPal, District: "Galle")));

        // February: a refunded order (still revenue), an unpaid one (never), a late approval placed in December.
        paid.Add(await InsertOrderAsync(catalogue, new OrderSpec(accountEmail, Colombo(2015, 2, 3), Colombo(2015, 2, 3), OrderStatus.Refunded, [new LineSpec(picks[2], 1, 4100m)], CustomerUserId: accountId, District: "Colombo")));
        await InsertOrderAsync(catalogue, new OrderSpec(accountEmail, Colombo(2015, 2, 4), null, OrderStatus.Expired, [new LineSpec(picks[2], 1, 9999m)], CustomerUserId: accountId));
        paid.Add(await InsertOrderAsync(catalogue, new OrderSpec(Email("late"), Colombo(2014, 12, 30), Colombo(2015, 2, 5), OrderStatus.Paid, [new LineSpec(picks[0], 1, 700m)], District: "Jaffna")));

        // March: the last minute of the quarter in Colombo, and one a minute later that is outside it.
        paid.Add(await InsertOrderAsync(catalogue, new OrderSpec(Email("b"), Colombo(2015, 3, 31, 23, 59), Colombo(2015, 3, 31, 23, 59), OrderStatus.Paid, [new LineSpec(picks[1], 4, 350m, Promo: 100m)], District: "Galle")));
        await InsertOrderAsync(catalogue, new OrderSpec(Email("b"), Colombo(2015, 4, 1, 0, 0), Colombo(2015, 4, 1, 0, 0), OrderStatus.Paid, [new LineSpec(picks[1], 1, 50000m)]));

        // A refund of the February order, completed in March.
        await InsertRefundAsync(paid[2].OrderId, paid[2].GrandTotal, Colombo(2015, 3, 10));

        // And two orders through the real checkout, the paid one approved through the real route, then moved
        // into the quarter: the reconciliation must hold for checkout's own arithmetic, not only for mine.
        var realPaid = await PlaceOrderAsync(host, Email("real"));
        await ApproveAsync(host, realPaid.PublicId);
        var realUnpaid = await PlaceOrderAsync(host, Email("real"));
        await MoveIntoQuarterAsync(realPaid.PublicId, Colombo(2015, 3, 20, 9, 0), paidUtc: Colombo(2015, 3, 20, 9, 30));
        await MoveIntoQuarterAsync(realUnpaid.PublicId, Colombo(2015, 3, 21, 9, 0), paidUtc: null);

        var expectedGross = paid.Sum(order => order.GrandTotal) + realPaid.GrandTotal;
        var expectedPaidOrders = paid.Count + 1;
        // Placed in the quarter, counted rather than worked out: 1 January, 15 January, 3 February, 4 February
        // (expired), 31 March, and the two real ones. Not the late approval (placed 30 December) and not the
        // order placed at 00:00 on 1 April.
        const int expectedOrders = 7;

        using var client = await host.SignInAsync(Permissions.Dashboard.View, Permissions.Reports.View, Permissions.Customers.View);
        var range = $"from={Day(2015, 1, 1)}&to={Day(2015, 3, 31)}";

        // 1. The KPI.
        var dashboard = await GetJsonAsync(client, $"/api/admin/dashboard?{range}");
        var kpis = dashboard.GetProperty("kpis");
        var kpiGross = kpis.GetProperty("grossRevenue").GetProperty("current").GetDecimal();
        Assert.Equal(expectedGross, kpiGross);
        Assert.Equal(expectedPaidOrders, (int)kpis.GetProperty("paidOrders").GetProperty("current").GetDecimal());
        Assert.Equal(expectedOrders, (int)kpis.GetProperty("orders").GetProperty("current").GetDecimal());

        // 2. The trend.
        var months = (await GetJsonAsync(client, $"/api/admin/dashboard/trend?interval=Month&to={Day(2015, 3, 31)}"))
            .GetProperty("points").EnumerateArray().TakeLast(3).ToList();
        Assert.Equal("2015-01-01", months[0].GetProperty("start").GetString());
        Assert.Equal(kpiGross, months.Sum(point => Money(point.GetProperty("grossRevenue"))));
        Assert.Equal(expectedPaidOrders, months.Sum(point => point.GetProperty("paidOrders").GetInt32()));
        Assert.Equal(expectedOrders, months.Sum(point => point.GetProperty("orders").GetInt32()));

        // 3. The sales report, three ways.
        JsonElement totals = default;
        foreach (var groupBy in new[] { "Day", "Week", "Month" })
        {
            var report = await GetJsonAsync(client, $"/api/admin/reports/sales?{range}&groupBy={groupBy}");
            totals = report.GetProperty("totals");

            Assert.Equal(kpiGross, Money(totals.GetProperty("grossRevenue")));
            Assert.Equal(kpiGross, report.GetProperty("rows").EnumerateArray().Sum(row => Money(row.GetProperty("grossRevenue"))));
            Assert.Equal(expectedPaidOrders, totals.GetProperty("paidOrders").GetInt32());
            Assert.Equal(expectedOrders, totals.GetProperty("orders").GetInt32());
            Assert.Equal(kpis.GetProperty("netRevenue").GetProperty("current").GetDecimal(), Money(totals.GetProperty("netRevenue")));
        }

        // 4. Departments are items; items + delivery is gross.
        var departmentRevenue = dashboard.GetProperty("departments").EnumerateArray().Sum(row => Money(row.GetProperty("revenue")));
        Assert.Equal(Money(totals.GetProperty("itemsRevenue")), departmentRevenue);
        Assert.Equal(paid.Sum(order => order.Items) + realPaid.Items, departmentRevenue);
        Assert.Equal(kpiGross, departmentRevenue + Money(totals.GetProperty("deliveryRevenue")));

        // 5. Customers, payment methods, districts.
        var customers = await GetJsonAsync(client, $"/api/admin/reports/customers?{range}");
        Assert.Equal(kpiGross, Money(customers.GetProperty("totalRevenue")));

        var methods = dashboard.GetProperty("paymentMethods").EnumerateArray().ToList();
        Assert.Equal(kpiGross, methods.Sum(row => Money(row.GetProperty("revenue"))));
        Assert.Equal(expectedPaidOrders, methods.Sum(row => row.GetProperty("paidOrders").GetInt32()));

        var districts = dashboard.GetProperty("buyersByDistrict").GetProperty("districts").EnumerateArray().ToList();
        Assert.Equal(kpiGross, districts.Sum(row => Money(row.GetProperty("revenue"))));
        Assert.Equal(expectedPaidOrders, districts.Sum(row => row.GetProperty("paidOrders").GetInt32()));

        var statuses = dashboard.GetProperty("orderStatuses").EnumerateArray().Sum(row => row.GetProperty("count").GetInt32());
        Assert.Equal(expectedOrders, statuses);

        // 6. The customer list (W3-07) and the customers report agree about one account.
        var list = await GetJsonAsync(client, $"/api/admin/customers?search={Uri.EscapeDataString(accountEmail)}");
        var listRow = Assert.Single(list.GetProperty("rows").EnumerateArray());
        var reportRow = customers.GetProperty("rows").EnumerateArray()
            .Single(row => string.Equals(row.GetProperty("email").GetString(), accountEmail, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(paid[0].GrandTotal + paid[2].GrandTotal, Money(reportRow.GetProperty("grossRevenue")));
        Assert.Equal(Money(listRow.GetProperty("lifetimeSpend")), Money(reportRow.GetProperty("grossRevenue")));
    }

    // ---- the real checkout -----------------------------------------------------------------------------

    private async Task<PlacedOrder> PlaceOrderAsync(MediaTestHost host, string email)
    {
        using var shopper = host.CreateAnonymousClient();

        using (var scope = SqlServer.CreateScope())
        {
            var variantId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProductVariants
                .AsNoTracking()
                .Where(variant => variant.IsActive && variant.Product!.IsPublished)
                .Where(variant => variant.Product!.Department!.IsActive && variant.Product!.PrimaryCategory!.IsActive)
                .Where(variant => variant.StockLevel != null && variant.StockLevel.Available > 2)
                .OrderByDescending(variant => variant.Id)
                .Skip(VariantBandOffset)
                .Select(variant => variant.Id)
                .FirstAsync(Token);

            using var added = await shopper.PostAsJsonAsync(new Uri("/api/cart/items", UriKind.Relative), new { variantId, quantity = 2 }, JsonOptions, Token);
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        }

        var catalogue = await CatalogueAsync();
        var districtId = catalogue.Districts["Colombo"];

        using var quote = await shopper.GetAsync(
            new Uri($"/api/checkout?districtId={districtId}&deliveryMethodId={catalogue.StandardDeliveryId}", UriKind.Relative),
            Token);
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        var session = await quote.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);

        using var placed = await shopper.PostAsJsonAsync(
            new Uri("/api/checkout/orders", UriKind.Relative),
            new
            {
                checkoutKey = session.GetProperty("checkoutKey").GetGuid(),
                firstName = "Ruwan",
                lastName = "Fernando",
                email,
                phone = "0771234567",
                deliveryMethodId = catalogue.StandardDeliveryId,
                districtId,
                paymentMethodCode = PaymentProviderCodes.BankTransfer,
                expectedGrandTotal = session.GetProperty("totals").GetProperty("grandTotal").GetDecimal(),
                address = new { recipientName = "Ruwan Fernando", phone = "0771234567", line1 = "14 Galle Road", city = "Kollupitiya" },
            },
            JsonOptions,
            Token);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

        var body = await placed.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, Token);
        var publicId = body.GetProperty("publicId").GetGuid();

        using var read = SqlServer.CreateScope();
        var order = await read.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Where(row => row.PublicId == publicId)
            .Select(row => new
            {
                row.GrandTotal,
                Items = row.Lines.Sum(line => line.LineTotal),
                Delivery = row.DeliveryFeeAmount - row.DeliveryDiscountAmount,
            })
            .SingleAsync(Token);

        Assert.Equal(body.GetProperty("grandTotal").GetDecimal(), order.GrandTotal);

        return new PlacedOrder(publicId, order.GrandTotal, order.Items, order.Delivery);
    }

    private static async Task ApproveAsync(MediaTestHost host, Guid orderPublicId)
    {
        using var approver = await host.SignInAsync(Permissions.Orders.Approve);

        using var response = await approver.PostAsJsonAsync(
            new Uri($"/api/admin/orders/bank-transfers/{orderPublicId:D}/approve", UriKind.Relative),
            new { note = "Paid, for the reconciliation fixture." },
            JsonOptions,
            Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Moves a real order's dates into the quarter — the only thing about it that is not checkout's own.</summary>
    private async Task MoveIntoQuarterAsync(Guid publicId, DateTime placedUtc, DateTime? paidUtc)
    {
        using var scope = SqlServer.CreateScope();

        var moved = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .Where(order => order.PublicId == publicId)
            .ExecuteUpdateAsync(
                order => order
                    .SetProperty(row => row.PlacedAtUtc, placedUtc)
                    .SetProperty(row => row.PaidAtUtc, paidUtc),
                Token);

        Assert.Equal(1, moved);
    }

    private static async Task<int> CreateCustomerAsync(MediaTestHost host, string email)
    {
        await using var scope = host.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            UserType = UserType.Customer,
            FirstName = "Amaya",
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
        };

        var created = await users.CreateAsync(user);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));

        return user.Id;
    }

    private sealed record PlacedOrder(Guid PublicId, decimal GrandTotal, decimal Items, decimal Delivery);
}
