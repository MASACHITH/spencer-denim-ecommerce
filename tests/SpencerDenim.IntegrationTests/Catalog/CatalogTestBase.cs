using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Catalog;

/// <summary>
/// Hosts the real API against the migrated and seeded test database. xUnit creates one instance per
/// test, so each test gets its own host and the factory is disposed with it.
/// </summary>
public abstract class CatalogTestBase(SqlServerDatabaseFixture sqlServer) : IAsyncDisposable
{
    private ApiFactory? _factory;
    private HttpClient? _client;

    /// <summary>The database created, migrated and seeded for this run.</summary>
    protected SqlServerDatabaseFixture SqlServer { get; } = sqlServer;

    /// <summary>The cancellation token xUnit associates with the running test.</summary>
    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Skips when no SQL Server is configured, then returns a client for the catalogue API.</summary>
    protected CatalogApiClient CreateApi()
    {
        SqlServer.SkipIfUnavailable();

        _factory ??= new ApiFactory(SqlServer.ConnectionString);
        _client ??= _factory.CreateClient();

        return new CatalogApiClient(_client);
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();

        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
