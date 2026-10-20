using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Application.Inventory.Abstractions;
using SpencerDenim.Infrastructure.Inventory;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Api.Admin;

/// <summary>
/// That the movement history (ADM-12) is served by <c>IX_InventoryMovements_Variant_Created</c> as a seek
/// with no sort, and that the two filtered ledger indexes accept exactly the predicate they were built for.
/// <para>
/// <b>A hint and the estimated plan, never an observation of what the optimiser chose</b> — the technique
/// <c>ReportingQueryPlanTests</c> records (#171): on a test-sized table a scan is often cheaper and the
/// optimiser's choice says nothing. Forcing the index on <b>the reader's own SQL</b> and asking for the plan
/// shows what the index <i>can</i> do: a forced non-filtered index always compiles, so the history test
/// asserts the shape (a seek, and no Sort operator, because the index order is the query's order); a forced
/// filtered index the predicate cannot match is refused with error 8622, whatever the table size.
/// </para>
/// </summary>
public sealed class InventoryQueryPlanTests(SqlServerDatabaseFixture sqlServer)
{
    private const string HistoryIndex = "IX_InventoryMovements_Variant_Created";
    private const string BatchIndex = "IX_InventoryMovements_Batch";

    private static readonly Regex MovementsReference = new(
        @"(\[inventory\]\.\[InventoryMovements\] AS \[[^\]]+\])",
        RegexOptions.None,
        TimeSpan.FromSeconds(5));

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_history_page_is_a_seek_in_index_order_with_no_sort()
    {
        using var scope = await ScopeAsync();
        var reader = (InventoryReader)scope.ServiceProvider.GetRequiredService<IInventoryReader>();

        var sql = reader.HistoryQuery(42).Skip(25).Take(25).ToQueryString();
        Assert.Contains("[VariantId]", sql, StringComparison.Ordinal);

        var hinted = MovementsReference.Replace(sql, "$1 WITH (INDEX([" + HistoryIndex + "]))");
        Assert.NotEqual(sql, hinted);

        var plan = await PlanForAsync(hinted);

        Assert.Contains(HistoryIndex, plan, StringComparison.Ordinal);
        Assert.Contains("PhysicalOp=\"Index Seek\"", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("PhysicalOp=\"Sort\"", plan, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control for the test above: the same history ordered the <i>other</i> way round does need a sort,
    /// so "no Sort operator" is something the plan can fail rather than something it never shows.
    /// </summary>
    [Fact]
    public async Task The_same_history_in_the_opposite_order_needs_a_sort()
    {
        using var scope = await ScopeAsync();
        var db = scope.ServiceProvider.GetRequiredService<SpencerDenim.Infrastructure.Persistence.AppDbContext>();

        var sql = db.InventoryMovements
            .Where(movement => movement.VariantId == 42)
            .OrderBy(movement => movement.CreatedAtUtc)
            .ThenByDescending(movement => movement.Id)
            .Take(25)
            .ToQueryString();

        var plan = await PlanForAsync(MovementsReference.Replace(sql, "$1 WITH (INDEX([" + HistoryIndex + "]))"));

        Assert.Contains("PhysicalOp=\"Sort\"", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_batch_index_serves_a_batch_lookup_and_refuses_anything_else_with_8622()
    {
        using var scope = await ScopeAsync();
        var db = scope.ServiceProvider.GetRequiredService<SpencerDenim.Infrastructure.Persistence.AppDbContext>();
        var batch = Guid.NewGuid();

        var byBatch = db.InventoryMovements.Where(movement => movement.BatchId == batch).ToQueryString();
        var plan = await PlanForAsync(MovementsReference.Replace(byBatch, "$1 WITH (INDEX([" + BatchIndex + "]))"));
        Assert.Contains(BatchIndex, plan, StringComparison.Ordinal);

        var byVariant = db.InventoryMovements.Where(movement => movement.VariantId == 42).ToQueryString();
        var error = await Assert.ThrowsAsync<SqlException>(
            () => PlanForAsync(MovementsReference.Replace(byVariant, "$1 WITH (INDEX([" + BatchIndex + "]))")));
        Assert.Equal(8622, error.Number);
    }

    private async Task<IServiceScope> ScopeAsync()
    {
        sqlServer.SkipIfUnavailable();
        await TestDatabaseMigrator.MigrateAsync(sqlServer.ConnectionString, Token);

        return sqlServer.CreateScope();
    }

    /// <summary>The estimated plan's XML for one batch of statements.</summary>
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
