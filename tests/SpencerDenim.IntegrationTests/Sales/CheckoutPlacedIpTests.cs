using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpencerDenim.Application.Sales.Checkout.Contracts;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Doc 11 P6 and doc 08 §8.4: "customer IP is stored with the order for fraud checks and deleted after
/// 90 days".
/// <para>
/// <b>It needs a host of its own, and that is the whole reason this file exists.</b>
/// <c>WebApplicationFactory</c>'s <c>TestServer</c> reports no <c>Connection.RemoteIpAddress</c> at all,
/// so an assertion in the ordinary suite could only ever confirm that the column was null — which says
/// nothing about whether the endpoint reads the connection or made the value up. Here the host is given
/// an address to report, through a middleware inserted ahead of the pipeline, and the question becomes a
/// real one: does the address the host reports reach <c>Orders.PlacedIpAddress</c>.
/// </para>
/// <para>
/// <b>What is still owed:</b> nothing purges the column. Doc 08 §8.4's 90-day deletion is a retention job
/// that does not exist, and it is in the task report rather than implied here.
/// </para>
/// </summary>
public sealed class CheckoutPlacedIpTests(SqlServerDatabaseFixture sqlServer) : CheckoutTestBase(sqlServer)
{
    /// <summary>A documentation-range address (RFC 5737), so it cannot be mistaken for a real one.</summary>
    private const string ReportedAddress = "203.0.113.7";

    [Fact]
    public async Task The_address_the_host_reports_is_stored_on_the_order()
    {
        SqlServer.SkipIfUnavailable();

        var variantId = await CheckoutVariantIdAsync();
        await SetStockAsync(variantId, 10);

        var colombo = await DistrictIdAsync("Colombo");
        var standard = await DeliveryMethodIdAsync(DeliveryMethod.StandardCode);

        await using var factory = new ApiFactory(
            SqlServer.ConnectionString,
            configureServices: services => services.AddSingleton<IStartupFilter>(
                new RemoteAddressStartupFilter(IPAddress.Parse(ReportedAddress))));

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            HandleCookies = true,
        });

        // The bag, then the checkout, then the order — all through the host that reports an address.
        var added = await client.PostAsJsonAsync(
            new Uri("/api/cart/items", UriKind.Relative),
            new { variantId, quantity = 1 },
            Token);

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var session = await client.GetFromJsonAsync<CheckoutSessionDto>(
            new Uri($"/api/checkout?districtId={colombo}&deliveryMethodId={standard}", UriKind.Relative),
            CheckoutApiClient.JsonOptions,
            Token);

        Assert.NotNull(session);

        var placed = await client.PostAsJsonAsync(
            new Uri("/api/checkout/orders", UriKind.Relative),
            ValidBody(session.CheckoutKey, standard, colombo, QuotedTotal(session)),
            Token);

        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);

        var order = await placed.Content.ReadFromJsonAsync<OrderConfirmationDto>(
            CheckoutApiClient.JsonOptions,
            Token);

        using var scope = CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
            .AsNoTracking()
            .Where(candidate => candidate.OrderNumber == order!.OrderNumber)
            .Select(candidate => candidate.PlacedIpAddress)
            .SingleAsync(Token);

        Assert.Equal(ReportedAddress, stored);

        // And it is not in the confirmation payload. The shopper has no use for it and a guest's order
        // link is shareable — doc 08 §3.5 keeps it for staff fraud checks, not for the success page.
        var body = await placed.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain(ReportedAddress, body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Inserts a middleware at the very front of the pipeline that sets
    /// <c>HttpContext.Connection.RemoteIpAddress</c>, which <c>TestServer</c> otherwise leaves null.
    /// </summary>
    private sealed class RemoteAddressStartupFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            builder =>
            {
                builder.Use(async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = address;
                    await continuation(context);
                });

                next(builder);
            };
    }
}
