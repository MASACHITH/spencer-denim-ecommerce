using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SpencerDenim.Infrastructure.Analytics;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Analytics;

/// <summary>
/// <c>IX_PageViews_Occurred INCLUDE (SessionId, IsBot, Channel, DeviceType)</c> (doc 08 §3.8) is a covering
/// index, and the live traffic figures are the query it covers. Observing which index the optimiser picks is a
/// recurring flake (#171), so this does what the reporting plan tests do for filtered indexes: it <b>forces</b> the
/// index on the reader's own statement and reads the estimated plan. A forced index that does not cover the
/// statement can still be used — with a Key Lookup per row — so the assertion is that there is no lookup at all.
/// A control proves the check sees a lookup when there is one.
/// </summary>
public sealed class TrafficQueryPlanTests(SqlServerDatabaseFixture sqlServer)
{
    private const string Index = "IX_PageViews_Occurred";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Regex PageViewsReference = new(@"(\[analytics\]\.\[PageViews\] AS pv)", RegexOptions.None, TimeSpan.FromSeconds(5));

    private const string Declarations = """
        DECLARE @fromUtc datetime2(0) = '2026-10-11T18:30:00', @toUtc datetime2(0) = '2026-10-15T18:30:00';
        DECLARE @fromDay date = '2026-09-16', @toDay date = '2026-10-15', @liveFrom date = '2026-10-12', @take int = 10;
        """;

    [Fact]
    public async Task The_live_traffic_figures_are_answered_from_the_covering_index_alone()
    {
        var plan = await ForcedPlanAsync(TrafficSql.TrafficByDay());

        Assert.Contains(Index, plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Lookup", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Control_a_statement_that_needs_an_uncovered_column_shows_a_lookup()
    {
        // The source needs UtmSource and ReferrerHost, which doc 08 §3.8 left out of the index.
        var plan = await ForcedPlanAsync(TrafficSql.TopSources());

        Assert.Contains(Index, plan, StringComparison.Ordinal);
        Assert.Contains("Lookup", plan, StringComparison.Ordinal);
    }

    private async Task<string> ForcedPlanAsync(string sql)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        Assert.Equal(1, PageViewsReference.Count(sql));
        var hinted = PageViewsReference.Replace(sql, "$1 WITH (INDEX([" + Index + "]))");

        await using var connection = new SqlConnection(sqlServer.ConnectionString);
        await connection.OpenAsync(Token);

        await using (var on = connection.CreateCommand())
        {
            on.CommandText = "SET SHOWPLAN_XML ON;";
            await on.ExecuteNonQueryAsync(Token);
        }

        var plan = new StringBuilder();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = Declarations + hinted;

            await using var reader = await command.ExecuteReaderAsync(Token);
            do
            {
                while (await reader.ReadAsync(Token))
                {
                    plan.Append(reader.GetString(0));
                }
            }
            while (await reader.NextResultAsync(Token));
        }

        Assert.NotEqual(0, plan.Length);
        return plan.ToString();
    }
}
