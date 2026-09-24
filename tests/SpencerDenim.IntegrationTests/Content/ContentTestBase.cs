using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// Hosts the real API against the migrated and seeded test database, with clients for both the
/// content and the catalogue endpoints — the homepage's product rows are assertions about the
/// catalogue as much as about the content.
/// </summary>
public abstract class ContentTestBase(SqlServerDatabaseFixture sqlServer) : IAsyncDisposable
{
    private ApiFactory? _factory;
    private HttpClient? _client;

    protected SqlServerDatabaseFixture SqlServer { get; } = sqlServer;

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Skips when no SQL Server is configured, then returns a client for the content API.</summary>
    protected ContentApiClient CreateApi() => new(SharedClient());

    /// <summary>The catalogue API on the same host, for comparing a homepage row with its listing.</summary>
    protected CatalogApiClient CreateCatalogApi() => new(SharedClient());

    private HttpClient SharedClient()
    {
        SqlServer.SkipIfUnavailable();

        _factory ??= new ApiFactory(SqlServer.ConnectionString);
        _client ??= _factory.CreateClient();

        return _client;
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
