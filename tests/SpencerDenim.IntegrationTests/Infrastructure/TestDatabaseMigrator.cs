using Microsoft.EntityFrameworkCore;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Applies EF Core migrations directly against a connection string, without starting the full host. Admin
/// auth tests need real Identity/RBAC tables to exist before <see cref="ApiFactory"/> starts the host — if a
/// migration were applied only after <c>factory.Services</c> is first touched, the host's own startup (which
/// runs <c>AdminSeedHostedService</c>) would already be racing an unmigrated database.
/// <para>
/// It must configure the migrations-history table exactly as <c>AddInfrastructure</c> does (doc 08 §10.1:
/// <c>ops.__EFMigrationsHistory</c>). With EF Core's default <c>dbo.__EFMigrationsHistory</c> this reads an
/// empty history on a database <see cref="SqlServerDatabaseFixture"/> has already migrated and tries to
/// create every table a second time.
/// </para>
/// </summary>
internal static class TestDatabaseMigrator
{
    public static async Task MigrateAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(
                connectionString,
                sql => sql.MigrationsHistoryTable(
                    AppDbContext.MigrationsHistoryTable,
                    AppDbContext.MigrationsHistorySchema))
            .Options;

        await using var dbContext = new AppDbContext(options);
        await dbContext.Database.MigrateAsync(cancellationToken);
    }
}
