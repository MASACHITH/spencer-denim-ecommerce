using Testcontainers.MsSql;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Starts one real SQL Server container for the whole test assembly and removes it afterwards.
/// The image matches production (SQL Server 2025); override with the <c>TESTS_MSSQL_IMAGE</c> environment variable.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    public const string DefaultImage = "mcr.microsoft.com/mssql/server:2025-latest";

    private readonly MsSqlContainer _container = new MsSqlBuilder(
            Environment.GetEnvironmentVariable("TESTS_MSSQL_IMAGE") is { Length: > 0 } image ? image : DefaultImage)
        .WithEnvironment("MSSQL_PID", "Express")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
