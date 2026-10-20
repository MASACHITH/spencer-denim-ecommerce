using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Application.Catalog;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Admin.Reporting;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// The customers chart reads <b>one consistent view</b> of the orders (#186b tester, Low): an order paid while the
/// chart is being read must not land in one count and miss another. The first version made three statements,
/// so an order committed between them could make a bar's first-time buyers exceed its buyers (returning −1).
/// <para>
/// The race is made deterministic: an interceptor commits a new first-time buyer's paid order, from another
/// connection, as soon as the reader's first command has started. With one statement, the snapshot was already
/// taken and every count agrees; with several, the later ones would see the new order and the earlier one would not.
/// </para>
/// </summary>
public sealed class AdminCustomerBuyersConsistencyTests(SqlServerDatabaseFixture sqlServer) : ReportingTestBase(sqlServer)
{
    private string Buyer(string name) => $"{Marker}.{name}@example.com";

    [Fact]
    public async Task An_order_paid_while_the_chart_is_read_never_makes_returning_negative()
    {
        await using var host = await StartAsync();
        var catalogue = await CatalogueAsync();
        var paid = Colombo(2013, 7, 10);

        await InsertOrderAsync(catalogue, new OrderSpec(Buyer("early"), paid.AddHours(-1), paid, OrderStatus.Paid, [new LineSpec(catalogue.Picks[0], 1, 1000m)]));

        var between = new AfterFirstCommand(() => InsertOrderAsync(
            catalogue,
            new OrderSpec(Buyer("late"), paid.AddHours(1), paid.AddHours(2), OrderStatus.Paid, [new LineSpec(catalogue.Picks[0], 1, 1000m)])));

        using var scope = SqlServer.CreateScope();
        var options = new DbContextOptionsBuilder<AppDbContext>(scope.ServiceProvider.GetRequiredService<DbContextOptions<AppDbContext>>())
            .AddInterceptors(between)
            .Options;
        await using var db = new AppDbContext(options);
        var reader = new ReportingReader(db, scope.ServiceProvider.GetRequiredService<IOptions<CatalogOptions>>());

        var chart = await new GetCustomerBuyersQueryHandler(reader).HandleAsync(
            new GetCustomerBuyersQuery(new ReportPeriod(new DateOnly(2013, 7, 1), new DateOnly(2013, 7, 31)), Marker),
            Token);

        Assert.True(between.Inserted, "the interceptor did not commit the order mid-read, so this test measured nothing");
        Assert.Equal(1, between.Commands);

        Assert.All(chart.Points, point =>
        {
            Assert.True(point.ReturningBuyers >= 0, $"{point.Start}: returning {point.ReturningBuyers}");
            Assert.Equal(point.Buyers, point.FirstTimeBuyers + point.ReturningBuyers);
        });
        Assert.True(chart.ReturningBuyers >= 0);
        Assert.Equal(chart.Points.Sum(point => point.FirstTimeBuyers), chart.FirstTimeBuyers);

        // The snapshot was taken before "late" was committed: one buyer everywhere.
        var day = chart.Points.Single(point => point.Start == new DateOnly(2013, 7, 10));
        Assert.Equal((1, 1, 0), (day.Buyers, day.FirstTimeBuyers, day.ReturningBuyers));
        Assert.Equal((1, 1, 0), (chart.Buyers, chart.FirstTimeBuyers, chart.ReturningBuyers));
    }

    /// <summary>Runs <c>action</c> once, right after the first command's reader opens, and counts the commands.</summary>
    private sealed class AfterFirstCommand(Func<Task> action) : DbCommandInterceptor
    {
        public int Commands { get; private set; }

        public bool Inserted { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Commands++;

            if (!Inserted)
            {
                await action();
                Inserted = true;
            }

            return result;
        }
    }
}
