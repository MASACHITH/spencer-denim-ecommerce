using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// A throwaway database created exactly as doc 08 §10.1 says to create a real one, migrated and
/// nothing else, with a service provider over it and a drop at the end.
/// <para>
/// <b>It creates and drops only its own database</b>, named <c>SpencerDenim_Deploy_&lt;guid&gt;</c>: never
/// the fixture's <c>SpencerDenim_Test_*</c>, never <c>SpencerDenim_Dev</c>, never anything an operator or
/// another session owns.
/// </para>
/// <para>
/// Lifted out of <c>FreshDeploymentTests</c>, where it was a private nested class, so that the seeding
/// tests of issues #113 and #122 can use the same three-step deployment rather than a second copy of it.
/// Nothing about it changed in the move.
/// </para>
/// </summary>
internal sealed class FreshDeployment : IAsyncDisposable
{
    private readonly string _serverConnectionString;
    private readonly string _databaseName;
    private ServiceProvider? _services;

    private FreshDeployment(
        string serverConnectionString,
        string databaseName,
        string connectionString,
        ServiceProvider services)
    {
        _serverConnectionString = serverConnectionString;
        _databaseName = databaseName;
        ConnectionString = connectionString;
        _services = services;
    }

    public string ConnectionString { get; }

    public IServiceProvider Services =>
        _services ?? throw new ObjectDisposedException(nameof(FreshDeployment));

    /// <param name="migrate">
    /// False to leave the database empty of tables, which is the "connectable but no migrations
    /// applied" state that `CanConnectAsync()` cannot see.
    /// </param>
    public static async Task<FreshDeployment> CreateAsync(
        string fixtureConnectionString,
        CancellationToken cancellationToken,
        bool migrate = true)
    {
        var server = new SqlConnectionStringBuilder(fixtureConnectionString) { InitialCatalog = "master" }
            .ConnectionString;

        var databaseName = "SpencerDenim_Deploy_" + Guid.NewGuid().ToString("N");
        var connectionString = new SqlConnectionStringBuilder(fixtureConnectionString)
        {
            InitialCatalog = databaseName,
        }.ConnectionString;

        // Created by the live database's provisioning script (doc 08 §1.1: collation and
        // READ_COMMITTED_SNAPSHOT, neither of which a migration can set), not by a copy of it.
        await TestDatabaseProvisioner.ProvisionAsync(server, databaseName, cancellationToken);

        if (migrate)
        {
            await TestDatabaseMigrator.MigrateAsync(connectionString, cancellationToken);
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(
                connectionString,
                sql => sql.MigrationsHistoryTable(
                    AppDbContext.MigrationsHistoryTable,
                    AppDbContext.MigrationsHistorySchema))
            .Options;

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddScoped<AppDbContext>();

        return new FreshDeployment(server, databaseName, connectionString, services.BuildServiceProvider());
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
            _services = null;
        }

        // The API under test pooled connections to this database; they would keep it in use.
        SqlConnection.ClearAllPools();

        await ExecuteAsync(
            _serverConnectionString,
            $"""
            IF DB_ID(N'{_databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{_databaseName}];
            END
            """,
            CancellationToken.None);
    }

    private static async Task ExecuteAsync(
        string connectionString,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
