using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Content.Abstractions;
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

    /// <summary>Counts the SQL commands the API runs, for the cache assertions.</summary>
    protected CommandCountingInterceptor CommandCounter { get; } = new();

    protected SqlServerDatabaseFixture SqlServer { get; } = sqlServer;

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Doc 08 §3.7's homepage cache, <b>as the API under test holds it</b>.
    ///
    /// <para>
    /// Resolved from the running host's own container rather than constructed here: it is a
    /// singleton, and a second instance would evict nothing. Every test that changes content
    /// behind the API's back has to drop it, which is what ADM-17's save will do through the front.
    /// </para>
    /// </summary>
    protected IHomePageCache Cache =>
        Host().Services.GetRequiredService<IHomePageCache>();

    /// <summary>Skips when no SQL Server is configured, then returns a client for the content API.</summary>
    protected ContentApiClient CreateApi() => new(SharedClient());

    /// <summary>The catalogue API on the same host, for comparing a homepage row with its listing.</summary>
    protected CatalogApiClient CreateCatalogApi() => new(SharedClient());

    private ApiFactory Host()
    {
        SqlServer.SkipIfUnavailable();

        return _factory ??= new ApiFactory(SqlServer.ConnectionString, commandInterceptor: CommandCounter);
    }

    private HttpClient SharedClient()
    {
        var factory = Host();

        return _client ??= factory.CreateClient();
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
