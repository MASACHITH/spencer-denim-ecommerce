using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Infrastructure.Sales.Payments;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// Issue #107's other half: the stock an unpaid order holds is given back by <c>ExpireUnpaidOrders</c>
/// (doc 08 §5.6), and that only happens if the API host actually runs the job.
/// <para>
/// <b>Why this test exists when <c>OrderExpiryTests</c> already proves the release.</b> Those tests call
/// the store directly, which proves what one expiry does and says nothing about whether anything ever
/// calls it. The issue's complaint was exactly that: a reservation "held for ever" because nothing ran. A
/// registration deleted from <c>AddInfrastructure</c>, or hidden behind a flag, would leave every one of
/// those tests green and bring the squatting back, so this asserts the host that production builds from
/// the same composition root has the job in it.
/// </para>
/// </summary>
public sealed class ExpireUnpaidOrdersHostingTests(SqlServerDatabaseFixture sqlServer)
{
    [Fact]
    public async Task The_api_host_runs_the_unpaid_order_expiry_job()
    {
        sqlServer.SkipIfUnavailable();

        await using var factory = new ApiFactory(sqlServer.ConnectionString);

        // Resolving the services builds and starts the host, hosted services included.
        var hosted = factory.Services.GetServices<IHostedService>().Select(service => service.GetType()).ToList();

        Assert.Contains(typeof(ExpireUnpaidOrdersService), hosted);
    }

    /// <summary>
    /// The hold window a bank-transfer order is promised, as the host resolves it: 48 hours (doc 08 §9
    /// <c>Orders.BankTransferExpiryHours</c>, doc 11 P7). Stated here because the success page repeats it
    /// to the shopper.
    /// </summary>
    [Fact]
    public async Task A_bank_transfer_order_is_held_for_forty_eight_hours_by_default()
    {
        sqlServer.SkipIfUnavailable();

        await using var factory = new ApiFactory(sqlServer.ConnectionString);

        Assert.Equal(48, factory.Services.GetRequiredService<BankTransferOptions>().BankTransferExpiryHours);
    }
}
