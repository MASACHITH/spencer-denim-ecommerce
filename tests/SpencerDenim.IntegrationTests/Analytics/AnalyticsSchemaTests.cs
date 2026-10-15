using Microsoft.Data.SqlClient;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Analytics;

/// <summary>The <c>analytics</c> schema as doc 08 §3.8 specifies it, read back from the catalogue views.</summary>
public sealed class AnalyticsSchemaTests(SqlServerDatabaseFixture sqlServer)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PageViews_and_its_index_are_page_compressed()
    {
        var rows = await QueryAsync("""
            SELECT ISNULL(i.name, 'HEAP'), p.data_compression_desc
            FROM sys.partitions p
            JOIN sys.indexes i ON i.object_id = p.object_id AND i.index_id = p.index_id
            WHERE p.object_id = OBJECT_ID('analytics.PageViews')
            ORDER BY i.index_id
            """);

        Assert.Equal([("PK_PageViews", "PAGE"), ("IX_PageViews_Occurred", "PAGE")], rows);
    }

    [Fact]
    public async Task The_occurred_index_includes_exactly_the_four_columns_doc_08_names()
    {
        var rows = await QueryAsync("""
            SELECT c.name, CASE WHEN ic.is_included_column = 1 THEN 'include' ELSE 'key' END
            FROM sys.index_columns ic
            JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = 'IX_PageViews_Occurred' AND i.object_id = OBJECT_ID('analytics.PageViews')
            ORDER BY ic.is_included_column, c.name
            """);

        Assert.Equal(
            [("OccurredAtUtc", "key"), ("Channel", "include"), ("DeviceType", "include"), ("IsBot", "include"), ("SessionId", "include")],
            rows);
    }

    [Fact]
    public async Task No_analytics_column_can_hold_an_ip_address_and_no_table_has_a_foreign_key()
    {
        var suspicious = await QueryAsync("""
            SELECT t.name, c.name
            FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
            WHERE SCHEMA_NAME(t.schema_id) = 'analytics'
              AND (c.name LIKE '%Ip%' COLLATE Latin1_General_CS_AS OR c.name LIKE '%IP%' COLLATE Latin1_General_CS_AS
                   OR c.name LIKE '%Address%' OR c.name LIKE '%Visitor%' OR c.name LIKE '%User%')
            """);

        Assert.Empty(suspicious);

        var foreignKeys = await QueryAsync("""
            SELECT OBJECT_NAME(fk.parent_object_id), fk.name
            FROM sys.foreign_keys fk
            WHERE OBJECT_SCHEMA_NAME(fk.parent_object_id) = 'analytics' OR OBJECT_SCHEMA_NAME(fk.referenced_object_id) = 'analytics'
            """);

        Assert.Empty(foreignKeys);
    }

    [Fact]
    public async Task The_aggregate_keys_lead_with_the_colombo_date()
    {
        var rows = await QueryAsync("""
            SELECT t.name + '.' + c.name, CAST(ic.key_ordinal AS varchar(3))
            FROM sys.indexes i
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 1 AND SCHEMA_NAME(t.schema_id) = 'analytics' AND t.name <> 'PageViews'
            ORDER BY t.name, ic.key_ordinal
            """);

        Assert.Contains(("TrafficDaily.Date", "1"), rows);
        Assert.Contains(("TrafficDaily.Channel", "2"), rows);
        Assert.Contains(("TrafficDaily.DeviceType", "3"), rows);
        Assert.Contains(("TrafficDaily.SourceKey", "4"), rows);
        Assert.Contains(("PageDaily.Date", "1"), rows);
        Assert.Contains(("PageDaily.PathHash", "2"), rows);
    }

    private async Task<List<(string, string)>> QueryAsync(string sql)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var rows = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }
}
