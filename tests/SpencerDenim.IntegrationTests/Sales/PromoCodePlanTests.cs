using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// That W3-05's two hot reads actually <b>match</b> their filtered indexes (doc 08 §1.6 DB28; ADM-13).
/// <para>
/// <b>Both indexes this exercises are filtered, and SQL Server matches a filtered index only when the
/// query's predicate provably implies the filter.</b> A literal implies it; a parameter does not. The
/// query still returns the right rows either way, so nothing fails and nothing says so — the only way to
/// know is to read the plan. This project has already shipped a filtered index whose one justifying query
/// could not use it, measured at 20,101 logical reads against 18, and
/// <c>BankQueuePlanTests</c> and <c>EmailOutboxPlanTests</c> are the precedent for refusing to repeat it.
/// </para>
/// <list type="number">
/// <item><description>
/// <b><c>UX_PromoCodes_Code</c></b>, filtered <c>WHERE IsDeleted = 0</c>, is what the shopper's lookup
/// seeks on every checkout read. It is matchable only because the global soft-delete filter's predicate
/// is a <i>constant</i> — <c>ApplySoftDelete</c> says so in as many words and this is the test behind
/// that claim.
/// </description></item>
/// <item><description>
/// <b><c>IX_Orders_Promo_Email</c></b>, filtered <c>WHERE PromoCodeId IS NOT NULL</c>, is what the
/// per-customer limit counts over — inside the placement transaction, holding an exclusive application
/// lock. A scan of <c>sales.Orders</c> there would serialise every checkout using that code behind a
/// table scan, which is the one place in this feature where a bad plan is a shop-wide stall rather than a
/// slow page.
/// </description></item>
/// </list>
/// </summary>
public sealed class PromoCodePlanTests(SqlServerDatabaseFixture sqlServer)
{
    private const string CodeIndex = "UX_PromoCodes_Code";
    private const string OrdersPromoIndex = "IX_Orders_Promo_Email";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The shopper's code lookup, as EF really emits it, seeks <c>UX_PromoCodes_Code</c>.
    /// </summary>
    /// <remarks>
    /// <b>The SQL is EF's own and not hand-written</b>, which is the point: it is what EF makes of the
    /// predicate <c>CheckoutReader.FindPromoCodeAsync</c> writes, with the global soft-delete filter on
    /// top.
    /// <para>
    /// <b>What it does not do is observe that reader.</b> The query is rebuilt here rather than taken from
    /// it, because <c>FindPromoCodeAsync</c> is an <c>async</c> method and its <c>IQueryable</c> is not
    /// reachable from outside — the same limitation <c>BankQueuePlanTests</c> works around the same way. So
    /// this proves the <i>shape</i> is matched; a developer who added an <c>IgnoreQueryFilters()</c> to the
    /// reader would turn a unique-index seek into a scan and leave this green. It is in the task report.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_shopper_s_code_lookup_seeks_the_unique_index()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sql = dbContext.PromoCodes
            .AsNoTracking()
            .Where(code => !code.IsDeleted && code.Code == "SOMECODE")
            .Select(code => code.Id)
            .Take(1)
            .ToQueryString();

        // **The filter has to reach the SQL as a literal, and this asserts it before the plan** — so a
        // failure says *why* the plan changed rather than only that it did. `CAST(0 AS bit)` is how EF
        // renders the global soft-delete filter's constant, which is exactly what makes the filtered
        // index matchable.
        Assert.Contains("CAST(0 AS bit)", sql, StringComparison.Ordinal);

        var plan = await PlanForAsync(sql);

        Assert.Contains(CodeIndex, plan.UsedIndexes);
        Assert.DoesNotContain(CodeIndex, plan.UnmatchedIndexes);
    }

    /// <summary>
    /// The same lookup with the <c>IsDeleted</c> predicate parameterised does <b>not</b> seek it, which is
    /// the limitation the production code is written around.
    /// </summary>
    /// <remarks>
    /// <b>This asserts a limitation of SQL Server rather than of this codebase</b>, and it is worth
    /// pinning: it is the whole reason <c>ApplySoftDelete</c> uses a constant predicate instead of a
    /// captured flag. If a future SQL Server version learned to prove implication from a parameter, this
    /// test would fail — and the right response would be to delete it, not to work around it.
    /// </remarks>
    [Fact]
    public async Task A_parameterised_soft_delete_predicate_does_not_seek_it()
    {
        sqlServer.SkipIfUnavailable();

        var plan = await PlanForAsync(
            """
            DECLARE @deleted bit = 0;
            DECLARE @code varchar(40) = 'SOMECODE';

            SELECT TOP (1) p.Id
              FROM marketing.PromoCodes AS p
             WHERE p.IsDeleted = @deleted
               AND p.Code = @code;
            """);

        // **The plan says this in as many words**, which is the strongest evidence DB28 could have: SQL
        // Server lists a filtered index it could not match because of a parameter under
        // `<UnmatchedIndexes>`, by name.
        Assert.Contains(CodeIndex, plan.UnmatchedIndexes);
        Assert.DoesNotContain(CodeIndex, plan.UsedIndexes);
    }

    /// <summary>
    /// The per-customer limit's count seeks <c>IX_Orders_Promo_Email</c>, <b>statement for statement as
    /// <c>OrderPromoUse.TakeAsync</c> runs it</b>.
    /// </summary>
    /// <remarks>
    /// <b>Why this one can match a filtered index although its values are parameters.</b> The filter is
    /// <c>PromoCodeId IS NOT NULL</c> and the predicate is <c>o.PromoCodeId = p.Id</c> — a comparison
    /// with a <i>column</i>, which implies <c>IS NOT NULL</c> whatever the value turns out to be. That is
    /// the implication SQL Server can prove without a literal, and it is why the production statement
    /// joins <c>marketing.PromoCodes</c> rather than passing the id in twice.
    /// <para>
    /// <b>The email and the statuses are parameters and that is fine:</b> the email is on the index
    /// <i>key</i> and the statuses are <c>INCLUDE</c>d, and DB28 only bites on the filter's own columns.
    /// </para>
    /// <para>
    /// <b>The SQL is a copy of the production statement</b>, and that is this file's one honest weakness:
    /// a plan test over a copy proves the shape can be matched, not that the shipped statement is still
    /// that shape. <c>OrderPromoUse</c> builds its SQL with <c>SqlQuery</c>, whose text is not reachable
    /// without executing it inside a transaction, so there is no <c>ToQueryString</c> to take. What closes
    /// the gap is <c>CheckoutPromoRaceTests</c>, which counts executions of this statement by a fragment
    /// of its own text — so a rewrite that changed the shape would make that count zero and the race
    /// tests fail loudly. It is in the task report.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_per_customer_count_seeks_the_orders_promo_index()
    {
        sqlServer.SkipIfUnavailable();

        var held = string.Join(
            ", ",
            new[]
            {
                OrderStatus.PendingPayment,
                OrderStatus.Paid,
                OrderStatus.Processing,
                OrderStatus.Shipped,
                OrderStatus.Delivered,
            }.Select(status => ((byte)status).ToString(CultureInfo.InvariantCulture)));

        var plan = await PlanForAsync(
            $"""
            DECLARE @email nvarchar(256) = N'SOMEBODY@EXAMPLE.COM';
            DECLARE @promoCodeId int = 1;

            SELECT p.PerCustomerLimit AS PerCustomerLimit,
                   (SELECT COUNT_BIG(*)
                      FROM sales.Orders o
                     WHERE o.PromoCodeId = p.Id
                       AND o.EmailNormalized = @email
                       AND (o.Status IN ({held})
                            OR (o.Status = {(byte)OrderStatus.Refunded}
                                AND o.PaidAtUtc IS NOT NULL))) AS HeldUses
              FROM marketing.PromoCodes p
             WHERE p.Id = @promoCodeId AND p.IsDeleted = 0
            """);

        Assert.Contains(OrdersPromoIndex, plan.UsedIndexes);
        Assert.DoesNotContain(OrdersPromoIndex, plan.UnmatchedIndexes);

        // And it never touches the order table itself: `Status` and `PaidAtUtc` are INCLUDEd on the
        // index, which is what makes the whole count one seek rather than a seek plus a lookup per row.
        Assert.DoesNotContain("PK_Orders", plan.UsedIndexes);
    }

    /// <summary>
    /// The same count with the promo id passed as a <b>parameter</b> instead of read from the joined row:
    /// still matched, because the filter is <c>IS NOT NULL</c> and <c>= @p</c> implies it.
    /// </summary>
    /// <remarks>
    /// <b>This is the test that stops the one above being vacuous.</b> Its sibling asserts that the
    /// production shape matches; without a case that could plausibly <i>not</i> match, "matched" might
    /// just be what every shape does here. Measured: both match, and the reason is that
    /// <c>PromoCodeId IS NOT NULL</c> is implied by <i>any</i> equality on that column — unlike
    /// <c>IX_Payments_BankQueue</c>'s <c>Status = 1</c>, which a parameter cannot imply and which
    /// <c>BankQueuePlanTests</c> measures as unmatched.
    /// <para>
    /// So it also records a real finding: <c>OrderPromoUse</c>'s join is <b>not</b> load-bearing for the
    /// plan. It is there because the per-customer limit has to be read fresh inside the transaction (doc
    /// 08 §3.6 DB19 allows an admin to lower it a second earlier), and the count comes along for free.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_parameterised_promo_id_still_seeks_the_orders_promo_index()
    {
        sqlServer.SkipIfUnavailable();

        var plan = await PlanForAsync(
            $"""
            DECLARE @email nvarchar(256) = N'SOMEBODY@EXAMPLE.COM';
            DECLARE @promoCodeId int = 1;

            SELECT COUNT_BIG(*)
              FROM sales.Orders o
             WHERE o.PromoCodeId = @promoCodeId
               AND o.EmailNormalized = @email
               AND o.Status = {(byte)OrderStatus.PendingPayment};
            """);

        Assert.Contains(OrdersPromoIndex, plan.UsedIndexes);
        Assert.DoesNotContain(OrdersPromoIndex, plan.UnmatchedIndexes);
    }

    /// <summary>
    /// The return of a use seeks the order's primary key and the promo code's, which is the whole of it:
    /// one row by id in each table.
    /// </summary>
    /// <remarks>
    /// Asserted because the statement is written as an <c>UPDATE … WHERE Id = (SELECT PromoCodeId FROM
    /// sales.Orders WHERE Id = @o)</c>, and a reader could reasonably wonder whether the subquery costs a
    /// scan. It does not, and this is the measurement rather than the assurance — it runs inside the
    /// expiry job's transaction, which holds the order row and the stock rows.
    /// </remarks>
    [Fact]
    public async Task The_return_of_a_use_seeks_both_primary_keys()
    {
        sqlServer.SkipIfUnavailable();

        var plan = await PlanForAsync(
            """
            DECLARE @orderId int = 1;

            UPDATE marketing.PromoCodes
               SET UsageCount = UsageCount - 1
             WHERE Id = (SELECT PromoCodeId FROM sales.Orders WHERE Id = @orderId)
               AND UsageCount > 0;
            """);

        Assert.Contains("PK_PromoCodes", plan.UsedIndexes);
        Assert.Contains("PK_Orders", plan.UsedIndexes);
    }

    /// <summary>
    /// The indexes one statement's estimated plan <b>reads</b>, and the ones it says it could not use.
    /// </summary>
    /// <remarks>
    /// The same helper <c>BankQueuePlanTests</c> uses, and for the same reason its own remarks give:
    /// <c>SHOWPLAN_XML</c> lists the filtered indexes the optimiser considered and <i>rejected</i> in an
    /// <c>&lt;UnmatchedIndexes&gt;</c> element, naming each one — so a test that merely searched the plan
    /// for an index name would read a rejection as a use. The block is separated out and what remains is
    /// the operators.
    /// <para>
    /// <c>SHOWPLAN_XML</c> rather than <c>STATISTICS XML</c>, so <b>nothing is executed</b> — which also
    /// means the <c>UPDATE</c> above moves no row and this suite cannot disturb another test's promo code.
    /// </para>
    /// <para>
    /// <b>The plan is for the data that is actually there</b> — tens of rows. That is sound for this
    /// question, because matching a filtered index is about implication rather than cardinality. What a
    /// small table cannot tell us is whether the seek is faster, and that is not what is asserted.
    /// </para>
    /// </remarks>
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

            while (await reader.ReadAsync(Token))
            {
                plan.Append(reader.GetString(0));
            }
        }

        Assert.NotEqual(0, plan.Length);

        var xml = plan.ToString();

        var unmatched = UnmatchedBlock.Matches(xml)
            .SelectMany(block => IndexAttribute.Matches(block.Value))
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var operatorsOnly = UnmatchedBlock.Replace(xml, string.Empty);
        var used = IndexAttribute.Matches(operatorsOnly)
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

    private sealed record PlanIndexes(
        IReadOnlySet<string> UsedIndexes,
        IReadOnlySet<string> UnmatchedIndexes);
}
