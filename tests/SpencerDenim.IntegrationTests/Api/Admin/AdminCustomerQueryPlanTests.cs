using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Admin.Customers;
using SpencerDenim.Infrastructure.Admin.Customers;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// That the customer list's live statistics read <c>IX_Orders_Customer_Placed</c>, the index doc 08 §3.1
/// names for them.
/// <para>
/// <b>Why this needs a plan test although this task adds no index.</b> <c>IX_Orders_Customer_Placed</c> is
/// <i>filtered</i> (<c>WHERE CustomerUserId IS NOT NULL</c>), and SQL Server uses a filtered index only
/// when it can prove the query's predicate implies the filter. The statistics are correlated subqueries
/// (<c>WHERE CustomerUserId = u.Id</c>), which is exactly the shape where that proof might fail and the
/// plan fall back to scanning every order once per customer on the page — with nothing failing. This
/// project has shipped a filtered index whose only justifying query could not use it.
/// </para>
/// <para>
/// <b>The statement is the reader's own</b>: <see cref="AdminCustomerReader.PageQuery"/> is what
/// <c>ReadListAsync</c> runs, rendered by EF with <c>ToQueryString</c>. Nothing here rebuilds the query.
/// </para>
/// </summary>
public sealed class AdminCustomerQueryPlanTests(SqlServerDatabaseFixture sqlServer)
{
    private const string StatisticsIndex = "IX_Orders_Customer_Placed";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("nimali")]
    [InlineData("NIMALI@")]
    [InlineData("0771234567")]
    public async Task The_list_s_statistics_read_the_customer_orders_index(string? search)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        using var scope = sqlServer.CreateScope();
        var reader = (AdminCustomerReader)scope.ServiceProvider.GetRequiredService<IAdminCustomerReader>();

        var sql = reader.PageQuery(AdminCustomerSearch.Classify(search), null, null, 1, 25).ToQueryString();

        // The statement is the one with the three aggregates in it, or this test is measuring nothing.
        Assert.Contains("[sales].[Orders]", sql, StringComparison.Ordinal);
        Assert.Contains("[CustomerUserId]", sql, StringComparison.Ordinal);

        var plan = await PlanForAsync(sql);

        Assert.Contains(StatisticsIndex, plan.UsedIndexes);
        Assert.DoesNotContain(StatisticsIndex, plan.UnmatchedIndexes);

        // **Deliberately not asserted: that PK_Orders is never read.** The first version asserted it, and it
        // failed in one of three back-to-back runs of the same code — on the suite's tiny tables the optimiser sometimes
        // costs one of the three aggregates cheaper as a clustered read. That is a cost decision on test-sized
        // data, not the defect this test exists for (a filtered index the query *cannot* match).
    }

    /// <summary>
    /// <b>Every one of the three aggregates can use the filtered index</b> — not merely one of them. The test
    /// above asserts the index appears <i>somewhere</i> in the plan, so a regression in a single aggregate
    /// (its predicate no longer implying <c>CustomerUserId IS NOT NULL</c>) still passed (W3-07 tester,
    /// mutation m12c). This one forces the index on every <c>sales.Orders</c> reference in the reader's own
    /// SQL: SQL Server refuses to compile a hint on a filtered index the predicate cannot match (error 8622),
    /// so the answer depends on the query's shape and not on the optimiser's costing of test-sized tables —
    /// which is what made the removed <c>PK_Orders</c> assertion flaky.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("nimali")]
    [InlineData("NIMALI@")]
    [InlineData("0771234567")]
    public async Task Each_aggregate_can_be_answered_from_the_filtered_index(string? search)
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        using var scope = sqlServer.CreateScope();
        var reader = (AdminCustomerReader)scope.ServiceProvider.GetRequiredService<IAdminCustomerReader>();

        var sql = reader.PageQuery(AdminCustomerSearch.Classify(search), null, null, 1, 25).ToQueryString();

        var references = OrdersReference.Count(sql);
        Assert.Equal(3, references);

        var hinted = OrdersReference.Replace(sql, "$1 WITH (INDEX([" + StatisticsIndex + "]))");

        // Throws SqlException 8622 if any one aggregate cannot be matched to the filtered index.
        var plan = await PlanForAsync(hinted);
        Assert.Contains(StatisticsIndex, plan.UsedIndexes);
    }

    private static readonly Regex OrdersReference = new(
        @"(FROM \[sales\]\.\[Orders\] AS \[[^\]]+\])",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The indexes one statement's estimated plan <b>reads</b>, and the filtered indexes it names as
    /// considered and unusable — <c>BankQueuePlanTests</c>' helper, whose remarks explain why the
    /// <c>&lt;UnmatchedIndexes&gt;</c> block has to be separated out before reading the operators.
    /// </summary>
    private async Task<PlanIndexes> PlanForAsync(string sql)
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

        var xml = plan.ToString();

        var unmatched = UnmatchedBlock.Matches(xml)
            .SelectMany(block => IndexAttribute.Matches(block.Value))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var used = IndexAttribute.Matches(UnmatchedBlock.Replace(xml, string.Empty))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        TestContext.Current.TestOutputHelper!.WriteLine(
            $"used: {string.Join(", ", used.Order(StringComparer.Ordinal))}; "
            + $"unmatched: {string.Join(", ", unmatched.Order(StringComparer.Ordinal))}");

        return new PlanIndexes(used, unmatched);
    }

    private static readonly Regex UnmatchedBlock = new(
        "<UnmatchedIndexes>.*?</UnmatchedIndexes>",
        RegexOptions.Singleline,
        TimeSpan.FromSeconds(5));

    private static readonly Regex IndexAttribute = new(
        "Index=\"\\[([^\\]]+)\\]\"",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private sealed record PlanIndexes(IReadOnlySet<string> UsedIndexes, IReadOnlySet<string> UnmatchedIndexes);
}
