using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
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
            // The session the next borrower gets is back at READ COMMITTED (2), not SERIALIZABLE (4).
            var db = scope.ServiceProvider.GetRequiredService<SpencerDenim.Infrastructure.Persistence.AppDbContext>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID";
            Assert.Equal((short)2, (short)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            await connection.CloseAsync();
        }

        using (var scope = SqlServer.CreateScope())
        {
            var past = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            await scope.ServiceProvider.GetRequiredService<IEmailOutboxStore>()
                .ClaimAsync(1, past, past.AddMinutes(5), TestContext.Current.CancellationToken);
        }
    }
}
