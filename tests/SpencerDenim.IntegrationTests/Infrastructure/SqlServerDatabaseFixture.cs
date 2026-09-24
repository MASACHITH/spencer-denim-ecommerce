using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SpencerDenim.Infrastructure;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Infrastructure.Persistence.Seeding;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Creates one uniquely named database (<c>SpencerDenim_Test_&lt;guid&gt;</c>) on the SQL Server given
/// by the <c>TEST_SQL_CONNECTION</c> environment variable, applies every EF migration to it, seeds the
/// catalogue, and drops the database when the test run ends.
/// When the variable is not set, <see cref="IsAvailable"/> is false and SQL-backed tests skip themselves.
/// <para>
/// The fixture applies the migrations itself, so CI needs no migration step before <c>dotnet test</c>.
/// </para>
/// </summary>
public sealed class SqlServerDatabaseFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "TEST_SQL_CONNECTION";
    public const string DatabaseNamePrefix = "SpencerDenim_Test_";

    /// <summary>Doc 08 §1.1: the collation is fixed at database creation.</summary>
    public const string Collation = "Latin1_General_100_CI_AS_SC";

    /// <summary>Attempts for a statement on the server, for a SQL Server that is still starting up (CI).</summary>
    private const int ConnectionAttempts = 5;

    /// <summary>Base delay between attempts; it grows with the attempt number (2 s, 4 s, 6 s, 8 s).</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public static readonly string SkipReason =
        $"SQL Server integration test skipped: set the {ConnectionVariable} environment variable to a SQL Server connection " +
        @"string, e.g. Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string? _serverConnectionString;

    private ServiceProvider? _services;

    public SqlServerDatabaseFixture()
    {
        var configured = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        // Always work from master; the test database name is generated here, never taken from the variable.
        _serverConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" }.ConnectionString;
        DatabaseName = DatabaseNamePrefix + Guid.NewGuid().ToString("N");
        ConnectionString = new SqlConnectionStringBuilder(configured) { InitialCatalog = DatabaseName }.ConnectionString;
    }

    /// <summary>True when <c>TEST_SQL_CONNECTION</c> is set.</summary>
    public bool IsAvailable => _serverConnectionString is not null;

    /// <summary>Name of the database created for this run (empty when not available).</summary>
    public string DatabaseName { get; } = string.Empty;

    /// <summary>Connection string to the test database (empty when not available).</summary>
    public string ConnectionString { get; } = string.Empty;

    /// <summary>What the seeders inserted, so tests can assert against the real catalogue size.</summary>
    public SeedCounts SeedCounts { get; private set; } = new();

    /// <summary>Skips the calling test when no SQL Server is configured.</summary>
    public void SkipIfUnavailable() => Assert.SkipUnless(IsAvailable, SkipReason);

    /// <summary>
    /// A DI scope on the seeded database, for tests that check mappings, run raw SQL or call a seeder
    /// again. The caller disposes it.
    /// </summary>
    public IServiceScope CreateScope() =>
        (_services ?? throw new InvalidOperationException("The fixture is not available; call SkipIfUnavailable() first."))
            .CreateScope();

    public async ValueTask InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;

        await ExecuteOnServerAsync(
            $"CREATE DATABASE {QuotedDatabaseName} COLLATE {Collation};",
            cancellationToken);

        _services = BuildServices(ConnectionString);

        using var scope = _services.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await dbContext.Database.MigrateAsync(cancellationToken);

        var runner = scope.ServiceProvider.GetRequiredService<CatalogSeedRunner>();
        SeedCounts = await runner.RunAsync(includeDemoCatalogue: true, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        if (_services is not null)
        {
            await _services.DisposeAsync();
            _services = null;
        }

        // Pooled connections from the API under test would keep the database in use.
        SqlConnection.ClearAllPools();

        await ExecuteOnServerAsync(
            $"""
            IF DB_ID(N'{DatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE {QuotedDatabaseName} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE {QuotedDatabaseName};
            END
            """,
            CancellationToken.None);
    }

    private static ServiceProvider BuildServices(string connectionString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = connectionString,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddInfrastructure();

        return services.BuildServiceProvider();
    }

    private string QuotedDatabaseName => string.Create(CultureInfo.InvariantCulture, $"[{DatabaseName}]");

    /// <summary>
    /// Runs a statement on the server, retrying transient failures. A SQL Server container in CI can
    /// accept a connection while it is still warming up, so the first attempts may fail with a
    /// connection or login error.
    /// </summary>
    private async Task ExecuteOnServerAsync(string sql, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteOnceAsync(sql, cancellationToken);
                return;
            }
            catch (SqlException exception) when (attempt < ConnectionAttempts && !cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"SQL attempt {attempt} of {ConnectionAttempts} failed ({exception.Number}): {exception.Message}");
                await Task.Delay(RetryDelay * attempt, cancellationToken);
            }
        }
    }

    private async Task ExecuteOnceAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_serverConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The only dynamic part is a database name generated from a GUID in this class.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
