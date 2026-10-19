using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Admin.Reporting;
using SpencerDenim.Infrastructure.Admin.Reporting;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// That the dashboard's paid-date queries <b>can</b> be answered from <c>IX_Orders_Paid</c> and the refund
/// queries from <c>IX_Refunds_Completed</c> — the two filtered indexes doc 08 §8.2 names for them. This task
/// adds no index; it relies on these two.
/// <para>
/// <b>Why a hint and not an observation.</b> Both indexes are filtered (<c>PaidAtUtc IS NOT NULL</c>,
/// <c>Status = 1</c>), and SQL Server uses a filtered index only when it can prove the query's predicate
/// implies the filter — for <i>any</i> parameter value. A query that cannot be matched falls back to a scan
/// of every order with nothing failing. Observing which plan the optimiser picks is a cost decision on
/// test-sized tables and has become a recurring flake (#171). So each test forces the index on every
/// reference to the table in <b>the reader's own SQL</b> (rendered by EF with <c>ToQueryString</c>) and asks
/// for the estimated plan: SQL Server refuses to compile a hint on a filtered index the predicate cannot
/// match (error 8622), whatever the table size. W3-07's tester established the technique.
/// </para>
/// </summary>
public sealed class ReportingQueryPlanTests(SqlServerDatabaseFixture sqlServer)
{
    private const string PaidIndex = "IX_Orders_Paid";
    private const string RefundIndex = "IX_Refunds_Completed";

    private static readonly UtcRange Range = new(
        new DateTime(2015, 1, 1, 18, 30, 0, DateTimeKind.Utc),
        new DateTime(2015, 4, 1, 18, 30, 0, DateTimeKind.Utc));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly ReportPeriod Period = new(new DateOnly(2015, 1, 2), new DateOnly(2015, 4, 1));

    public static TheoryData<string> PaidQueries =>
    [
        "totals", "daily", "districts", "departments",
        "buyers-day", "buyers-week", "buyers-month", "buyers-search",
        "first-time-day", "first-time-week", "first-time-month", "first-time-search",
    ];

    [Theory]
    [MemberData(nameof(PaidQueries))]
    public async Task Each_paid_date_query_can_be_answered_from_the_filtered_paid_index(string query)
    {
        using var scope = await ScopeAsync();
        var reader = Reader(scope);

        var sql = query switch
        {
            "totals" => reader.PaidTotalsQuery(Range).ToQueryString(),
            "daily" => reader.PaidDailyQuery(Range).ToQueryString(),
            "districts" => reader.BuyersByDistrictQuery(Range).ToQueryString(),
            "departments" => reader.DepartmentSalesQuery(Range).ToQueryString(),
            "buyers-day" => reader.BuyersPerBucketQuery(Period, ReportInterval.Day, null).ToQueryString(),
            "buyers-week" => reader.BuyersPerBucketQuery(Period, ReportInterval.Week, null).ToQueryString(),
            "buyers-month" => reader.BuyersPerBucketQuery(Period, ReportInterval.Month, null).ToQueryString(),
            "buyers-search" => reader.BuyersPerBucketQuery(Period, ReportInterval.Day, "NIMALI").ToQueryString(),
            "first-time-day" => reader.FirstTimeBuyersPerBucketQuery(Period, ReportInterval.Day, null).ToQueryString(),
            "first-time-week" => reader.FirstTimeBuyersPerBucketQuery(Period, ReportInterval.Week, null).ToQueryString(),
            "first-time-month" => reader.FirstTimeBuyersPerBucketQuery(Period, ReportInterval.Month, null).ToQueryString(),
            "first-time-search" => reader.FirstTimeBuyersPerBucketQuery(Period, ReportInterval.Day, "NIMALI").ToQueryString(),
            _ => throw new ArgumentOutOfRangeException(nameof(query)),
        };

        // The statement reads the orders table by its paid date, or this test measures nothing.
        Assert.Contains("[PaidAtUtc]", sql, StringComparison.Ordinal);

        var references = OrdersReference.Count(sql);
        Assert.True(references >= 1, sql);

        var hinted = OrdersReference.Replace(sql, "$1 WITH (INDEX([" + PaidIndex + "]))");

        // Throws SqlException 8622 if the predicate cannot be matched to the filtered index.
        var plan = await PlanForAsync(hinted);
        Assert.Contains(PaidIndex, plan);
    }

    [Fact]
    public async Task The_refund_query_can_be_answered_from_the_filtered_completed_index()
    {
        using var scope = await ScopeAsync();
        var reader = Reader(scope);

        var sql = reader.RefundsDailyQuery(Range).ToQueryString();
        Assert.Contains("[CompletedAtUtc]", sql, StringComparison.Ordinal);

        var hinted = RefundsReference.Replace(sql, "$1 WITH (INDEX([" + RefundIndex + "]))");
        Assert.NotEqual(sql, hinted);

        var plan = await PlanForAsync(hinted);
        Assert.Contains(RefundIndex, plan);
    }

    /// <summary>
    /// The control: the same technique <b>does</b> refuse a query that cannot use the index. Without it the
    /// tests above could pass because the hint was ignored, not because the predicate matched.
    /// </summary>
    [Fact]
    public async Task A_query_that_does_not_imply_the_filter_is_refused_with_8622()
    {
        using var scope = await ScopeAsync();

        const string sql = "SELECT SUM([o].[GrandTotal]) FROM [sales].[Orders] AS [o] WHERE [o].[PlacedAtUtc] >= '2015-01-01'";
        var hinted = OrdersReference.Replace(sql, "$1 WITH (INDEX([" + PaidIndex + "]))");

        var error = await Assert.ThrowsAsync<SqlException>(() => PlanForAsync(hinted));
        Assert.Equal(8622, error.Number);
    }

    private async Task<IServiceScope> ScopeAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return sqlServer.CreateScope();
    }

    private static ReportingReader Reader(IServiceScope scope) =>
        (ReportingReader)scope.ServiceProvider.GetRequiredService<IReportingReader>();

    private static readonly Regex OrdersReference = new(
        @"(\[sales\]\.\[Orders\] AS \[[^\]]+\])",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static readonly Regex RefundsReference = new(
        @"(\[sales\]\.\[Refunds\] AS \[[^\]]+\])",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>The estimated plan's XML for one statement.</summary>
    private async Task<string> PlanForAsync(string sql)
    {
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
            command.CommandText = sql;

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
