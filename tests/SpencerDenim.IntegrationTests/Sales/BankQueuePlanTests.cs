using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.Domain.Sales;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// That the bank-transfer queue's query actually <b>matches</b> <c>IX_Payments_BankQueue</c> (doc 08 §1.6
/// DB28).
/// <para>
/// <b>#103's review asked for this test by name</b>, and it asked for the right thing: the index is
/// filtered <c>WHERE ProviderCode = 'BankTransfer' AND Status = 1</c>, and SQL Server matches a filtered
/// index only when the query's predicate <i>provably implies</i> the filter. A literal implies it. A
/// parameter does not — <c>ProviderCode = @code</c> cannot be proved equal to <c>'BankTransfer'</c> at
/// compile time, so the plan falls back to scanning <c>sales.Payments</c>. The query still returns the
/// right rows, so nothing fails and nothing says so; the only way to know is to read the plan.
/// </para>
/// <para>
/// <b>Why the SQL is written out here rather than captured from the reader.</b> A plan test that captured
/// EF's generated SQL would prove whatever EF currently emits, which is the thing under test. These two
/// statements are the two <i>shapes</i> — literals and parameters — and the assertion is that the first is
/// matched and the second is not. If a future EF version started parameterising the constants in
/// <c>BankTransferReader</c>, the second case here is what documents the consequence, and
/// <see cref="The_reader_s_own_query_seeks_the_index"/> is what catches it.
/// </para>
/// </summary>
public sealed class BankQueuePlanTests(SqlServerDatabaseFixture sqlServer)
{
    private const string IndexName = "IX_Payments_BankQueue";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The queue's predicate, written with literals, seeks the filtered index.
    /// </summary>
    [Fact]
    public async Task A_literal_predicate_seeks_the_bank_queue_index()
    {
        sqlServer.SkipIfUnavailable();

        var plan = await PlanForAsync(
            $"""
            SELECT TOP (20) p.Id
              FROM sales.Payments AS p
             WHERE p.ProviderCode = '{PaymentProviderCodes.BankTransfer}'
               AND p.Status = {((byte)PaymentStatus.Pending).ToString(CultureInfo.InvariantCulture)}
             ORDER BY p.CreatedAtUtc;
            """);

        Assert.Contains(IndexName, plan.UsedIndexes);
        Assert.DoesNotContain(IndexName, plan.UnmatchedIndexes);
    }

    /// <summary>
    /// The same predicate with parameters does <b>not</b>, which is the fact the production code is
    /// written around.
    /// </summary>
    /// <remarks>
    /// <b>This asserts a limitation of SQL Server rather than of this codebase</b>, and it is worth
    /// pinning: it is the entire reason <c>PaymentProviderCodes.BankTransfer</c> is a <c>const string</c>
    /// and <c>BankTransferReader</c> compares against compile-time constants instead of locals. If a
    /// future SQL Server version learned to prove implication from a parameter, this test would fail —
    /// and the right response would be to delete it and relax the reader's comment, not to work around it.
    /// </remarks>
    [Fact]
    public async Task A_parameterised_predicate_does_not_seek_it()
    {
        sqlServer.SkipIfUnavailable();

        var plan = await PlanForAsync(
            """
            DECLARE @code varchar(30) = 'BankTransfer';
            DECLARE @status tinyint = 1;

            SELECT TOP (20) p.Id
              FROM sales.Payments AS p
             WHERE p.ProviderCode = @code
               AND p.Status = @status
             ORDER BY p.CreatedAtUtc;
            """);

        // **The plan says this in as many words**, which is the strongest evidence DB28 could have: SQL
        // Server lists a filtered index it could not match because of a parameter under
        // `<UnmatchedIndexes>`, by name.
        Assert.Contains(IndexName, plan.UnmatchedIndexes);
        Assert.DoesNotContain(IndexName, plan.UsedIndexes);

        // And what it reads instead is the whole table.
        Assert.Contains("PK_Payments", plan.UsedIndexes);
    }

    /// <summary>
    /// And the shape EF really emits for <c>BankTransferReader.ReadQueueAsync</c> seeks it too.
    /// </summary>
    /// <remarks>
    /// <b>This is the one that would catch a regression</b>, because the two above only establish that the
    /// index can be matched and when it cannot. A developer replacing
    /// <c>payment.ProviderCode == PaymentProviderCodes.BankTransfer</c> with a captured local would leave
    /// both of those green and make this one red.
    /// <para>
    /// The SQL is EF's own, taken from the generated command for that query rather than hand-written, so
    /// what is measured is the query the shop runs.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_reader_s_own_query_seeks_the_index()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();

        var sql = dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.ProviderCode == PaymentProviderCodes.BankTransfer
                && payment.Status == PaymentStatus.Pending)
            .OrderBy(payment => payment.CreatedAtUtc)
            .ThenBy(payment => payment.Id)
            .Take(20)
            .Select(payment => payment.Id)
            .ToQueryString();

        // EF emits the two filter values as literals because both are compile-time constants, which is
        // what the reader's remarks claim. Asserted before the plan, so a failure here says *why* the plan
        // changed rather than only that it did.
        Assert.Contains($"'{PaymentProviderCodes.BankTransfer}'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@__", sql, StringComparison.Ordinal);

        var plan = await PlanForAsync(sql);

        Assert.Contains(IndexName, plan.UsedIndexes);
    }

    /// <summary>
    /// The expiry job's batch read seeks <c>IX_Orders_Pending_Due</c>, for the same reason.
    /// </summary>
    /// <remarks>
    /// That index is filtered <c>WHERE Status = 0</c>, so <c>order.Status == OrderStatus.PendingPayment</c>
    /// has to compile to a literal. It does, because an enum member is a constant — but a developer
    /// hoisting it into a variable would turn a seek over the due orders into a scan of
    /// <c>sales.Orders</c> every five minutes for ever, with nothing failing.
    /// <para>
    /// The <c>now</c> comparison stays a parameter and that is fine: it is on the index <i>key</i>, not in
    /// its filter, and DB28 only bites on the filter's own columns.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_expiry_job_s_batch_read_seeks_the_due_orders_index()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider
            .GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;

        var sql = dbContext.Orders
            .AsNoTracking()
            .Where(order =>
                order.Status == OrderStatus.PendingPayment
                && order.PaymentDueAtUtc < now
                && (order.CaptureLockedUntilUtc == null || order.CaptureLockedUntilUtc < now))
            .OrderBy(order => order.PaymentDueAtUtc)
            .Take(50)
            .Select(order => order.Id)
            .ToQueryString();

        // `ToQueryString` renders a parameter's value as a declaration, so the statement is runnable.
        var plan = await PlanForAsync(sql);

        Assert.Contains("IX_Orders_Pending_Due", plan.UsedIndexes);
    }

    /// <summary>
    /// The indexes one statement's estimated plan <b>reads</b>, and the ones it says it could not use.
    /// </summary>
    /// <remarks>
    /// <b>Telling "used" from "mentioned" is the whole substance of this helper, and the first version of
    /// these tests got it wrong.</b> It asserted that a parameterised plan did not contain the string
    /// <c>IX_Payments_BankQueue</c> anywhere, and the assertion failed — because <c>SHOWPLAN_XML</c> lists
    /// the filtered indexes the optimiser considered and <i>rejected</i> in an
    /// <c>&lt;UnmatchedIndexes&gt;</c> element, naming each one. The name's presence there is the opposite
    /// of what the test was reading it as. So the block is separated out, and what remains is the
    /// operators.
    /// <para>
    /// That accident turned into the better test: the optimiser itself declaring
    /// <c>IX_Payments_BankQueue</c> unmatched is as direct a statement of DB28 as can be had, far better
    /// than inferring it from the absence of a seek.
    /// </para>
    /// <para>
    /// <c>SHOWPLAN_XML</c> rather than <c>STATISTICS XML</c>, so nothing is executed — an estimated plan is
    /// what answers "which index would this use".
    /// </para>
    /// <para>
    /// <b>The plan is for the data that is actually there</b>, which is the seeded demo catalogue plus
    /// whatever orders the suite has placed: tens of rows, not the hundred thousand #103's review measured
    /// on. That is sound for this question, because matching a filtered index is about <i>implication</i>
    /// and not about cardinality — the measured plan above is a clustered scan of a table with
    /// <c>TableCardinality="0"</c>, where a seek would be cheaper if it were legal. What a small table
    /// cannot tell us is whether the seek is faster, and that is not what is being asserted.
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

        var plan = new System.Text.StringBuilder();

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

        var unmatchedBlocks = UnmatchedBlock.Matches(xml);
        var unmatched = unmatchedBlocks
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

    /// <param name="UsedIndexes">Indexes an operator in the plan actually reads.</param>
    /// <param name="UnmatchedIndexes">
    /// Filtered indexes the optimiser named as considered and not usable — which for a parameterised
    /// predicate is doc 08 §1.6 DB28 in the optimiser's own words.
    /// </param>
    private sealed record PlanIndexes(
        IReadOnlySet<string> UsedIndexes,
        IReadOnlySet<string> UnmatchedIndexes);
}
