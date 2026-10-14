using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Application.Sales.Cart.Abstractions;
using SpencerDenim.IntegrationTests.Infrastructure;
using SpencerDenim.IntegrationTests.Sales;

namespace SpencerDenim.IntegrationTests.Ops;

[Collection(nameof(OutboxWipingTests))]
public sealed class PooledIsolationLeakTests(SqlServerDatabaseFixture sqlServer) : SalesSchemaTestBase(sqlServer)
{
    [Fact]
    public async Task A_sign_in_merge_does_not_break_the_next_email_claim_on_its_pooled_connection()
    {
        SqlConnection.ClearAllPools();

        using (var scope = SqlServer.CreateScope())
        {
            // No guest cart: the merge opens its Serializable transaction, finds nothing and rolls back.
            await scope.ServiceProvider.GetRequiredService<ICartStore>()
                .MergeGuestIntoCustomerAsync(Guid.NewGuid(), 1, TestContext.Current.CancellationToken);
        }

        using (var scope = SqlServer.CreateScope())
        {
            var past = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await scope.ServiceProvider.GetRequiredService<IEmailOutboxStore>()
                .ClaimAsync(1, past, past.AddMinutes(5), TestContext.Current.CancellationToken);
        }
    }
}
