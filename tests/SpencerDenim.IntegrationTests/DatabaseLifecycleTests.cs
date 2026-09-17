using Microsoft.Data.SqlClient;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests;

public sealed class DatabaseLifecycleTests(SqlServerDatabaseFixture sqlServer)
{
    [Fact]
    public async Task Test_database_is_unique_and_uses_the_platform_collation()
    {
        sqlServer.SkipIfUnavailable();

        Assert.StartsWith(SqlServerDatabaseFixture.DatabaseNamePrefix, sqlServer.DatabaseName, StringComparison.Ordinal);

        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DB_NAME(), CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(sqlServer.DatabaseName, reader.GetString(0));
        Assert.Equal(SqlServerDatabaseFixture.Collation, reader.GetString(1));
    }
}
