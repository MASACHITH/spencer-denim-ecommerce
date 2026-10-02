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

    /// <summary>Counts the SQL commands the API runs, for the "no N+1" assertion.</summary>
    protected CommandCountingInterceptor CommandCounter { get; } = new();

    /// <summary>The database created, migrated and seeded for this run.</summary>
    protected SqlServerDatabaseFixture SqlServer { get; } = sqlServer;

    /// <summary>The cancellation token xUnit associates with the running test.</summary>
    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Configuration this test class needs on top of the defaults, or <see langword="null"/> for none.
    /// <para>
    /// Added so a class can turn a cache off for itself: the sitemap endpoint carries an output-cache
    /// policy, and a test that changes the catalogue and reads it back needs the read to be a read. The
    /// tests that are *about* the cache build their own host with it on.
    /// </para>
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string?>? ExtraConfiguration => null;

    /// <summary>Skips when no SQL Server is configured, then returns a client for the catalogue API.</summary>
    protected CatalogApiClient CreateApi()
    {
        SqlServer.SkipIfUnavailable();

        _factory ??= new ApiFactory(
            SqlServer.ConnectionString,
            additionalConfiguration: ExtraConfiguration,
            commandInterceptor: CommandCounter);
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
