using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SpencerDenim.IntegrationTests.Infrastructure;

/// <summary>
/// Creates one empty, uniquely named database (<c>SpencerDenim_Test_&lt;guid&gt;</c>) on the SQL Server given by the
/// <c>TEST_SQL_CONNECTION</c> environment variable, and drops it when the test run ends.
/// When the variable is not set, <see cref="IsAvailable"/> is false and SQL-backed tests skip themselves.
/// </summary>
public sealed class SqlServerDatabaseFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "TEST_SQL_CONNECTION";
    public const string DatabaseNamePrefix = "SpencerDenim_Test_";

    /// <summary>Doc 08 §1.1: the collation is fixed at database creation.</summary>
    public const string Collation = "Latin1_General_100_CI_AS_SC";

    public static readonly string SkipReason =
        $"SQL Server integration test skipped: set the {ConnectionVariable} environment variable to a SQL Server connection " +
        @"string, e.g. Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";

    private readonly string? _serverConnectionString;

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

    /// <summary>Skips the calling test when no SQL Server is configured.</summary>
    public void SkipIfUnavailable() => Assert.SkipUnless(IsAvailable, SkipReason);

    public async ValueTask InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        await ExecuteOnServerAsync(
            $"CREATE DATABASE {QuotedDatabaseName} COLLATE {Collation};",
            TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsAvailable)
        {
            return;
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

    private string QuotedDatabaseName => string.Create(CultureInfo.InvariantCulture, $"[{DatabaseName}]");

    private async Task ExecuteOnServerAsync(string sql, CancellationToken cancellationToken)
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
