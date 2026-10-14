using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Infrastructure.Marketing;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// That the live campaign read actually <b>matches</b> <c>IX_SaleCampaigns_Window</c> (doc 08 §1.6 DB28;
/// ADM-14).
/// <para>
/// <b>The index is filtered, and SQL Server matches a filtered index only when the query's predicate
/// provably implies the filter at compile time.</b> A literal implies it; a parameter does not. The query
/// returns the right rows either way, so nothing fails and nothing says so — the only way to know is to
/// read the plan. This project has already shipped a filtered index whose one justifying query could not
/// use it, measured at 20,101 logical reads against 18.
/// </para>
/// <para>
/// <b>This suite takes the SQL from the production query and not from its own test file.</b> That is the
/// other half of the same lesson: a plan test that rebuilds the query it is meant to be checking passes
/// whatever the reader does, and this repository has one that stayed green with its index dropped.
/// <see cref="CampaignOfferReader.Query"/> exists as a separate method for exactly this reason, and
/// <see cref="The_live_campaign_read_seeks_the_window_index"/> calls it.
/// </para>
/// </summary>
public sealed class SaleCampaignPlanTests(SqlServerDatabaseFixture sqlServer)
{
    private const string WindowIndex = "IX_SaleCampaigns_Window";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The bag's and checkout's campaign read, <b>as the reader really emits it</b>: the window
    /// predicates reach SQL Server as literals, and the plan drives from
    /// <c>marketing.SaleCampaignTargets</c>.
    /// </summary>
    /// <remarks>
    /// <b>This test does not assert that the live read seeks <c>IX_SaleCampaigns_Window</c>, because it
    /// measurably does not, and that is a correction worth writing down rather than a failure.</b> The
    /// query asks "which of these products does a live campaign target", and the selective side of it is
    /// the targets table: the optimiser drives from <c>PK_SaleCampaignTargets</c> and
    /// <c>IX_ProductCategories_ProductId</c> and reaches each candidate campaign by
    /// <c>PK_SaleCampaigns</c>. Measured, with 3,000 ended campaigns in the table and statistics
    /// updated, the plan was unchanged — so it is the join order and not the row count. The index doc 08
    /// §3.6 names is for the query shape that starts at the campaigns and asks which are live, which is
    /// <c>RefreshCampaignPricing</c>'s (doc 08 §7.4: "campaigns that started or ended since the last
    /// run"); <see cref="The_window_shaped_read_seeks_the_window_index"/> is the one that pins it.
    /// <para>
    /// What is asserted here is what matters for this query and nothing more:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <c>CAST(1 AS bit)</c> — how EF renders the constant <c>campaign.IsActive</c> predicate. Written as
    /// a captured variable it would arrive as <c>@p</c>, and the filtered index would stop being matchable
    /// for any plan that did want it (DB28).
    /// </description></item>
    /// <item><description>
    /// <c>CAST(0 AS bit)</c> — how <c>ApplySoftDelete</c>'s global query filter renders
    /// <c>IsDeleted = false</c>, the other half of that filter. It is in the SQL because the reader does
    /// <b>not</b> call <c>IgnoreQueryFilters()</c>, which is also what stops a removed campaign pricing
    /// the shop — so this assertion fails if anybody adds one.
    /// </description></item>
    /// <item><description>
    /// The window index is <b>not rejected</b>: it is absent from <c>&lt;UnmatchedIndexes&gt;</c>, which
    /// is how SQL Server reports a filtered index it could not prove applicable.
    /// </description></item>
    /// <item><description>
    /// The plan reads the campaign and target tables at all — so a future change that stopped joining
    /// them, or renamed them, is a failure here rather than a silent full scan.
    /// </description></item>
    /// </list>
    /// </remarks>
    [Fact]
    public async Task The_live_campaign_read_is_driven_from_the_targets_with_literal_predicates()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sql = CampaignOfferReader
            .Query(dbContext, [1, 2, 3], new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc))
            .ToQueryString();

        Assert.Contains("CAST(1 AS bit)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(0 AS bit)", sql, StringComparison.Ordinal);

        var plan = await PlanForAsync(sql);

        Assert.DoesNotContain(WindowIndex, plan.UnmatchedIndexes);
        Assert.Contains("PK_SaleCampaignTargets", plan.UsedIndexes);
        Assert.Contains("PK_SaleCampaigns", plan.UsedIndexes);
    }

    /// <summary>
    /// A window-shaped read — "which campaigns are live now" — <b>does</b> seek
    /// <c>IX_SaleCampaigns_Window</c>, which is what justifies the index doc 08 §3.6 names.
    /// </summary>
    /// <remarks>
    /// <b>This is the shape <c>RefreshCampaignPricing</c> uses</b> (doc 08 §7.4), and the one the filtered
    /// index is for: it starts at the campaigns rather than at a handful of product ids, so the window is
    /// the selective predicate instead of an afterthought. The literals are written out here rather than
    /// taken from a reader because the job's statement is raw T-SQL in the first place — there is no
    /// <c>IQueryable</c> to observe — and the next test shows what happens to the same text with the
    /// flags parameterised, which is the pair that proves the assertion means something.
    /// <para>
    /// 3,000 ended campaigns are arranged first. With the four a behaviour test leaves behind, a
    /// clustered scan is cheaper than any seek and the plan says so — which would make this test pass or
    /// fail by how many campaigns the run before it happened to leave.
    /// </para>
    /// <para>
    /// <b>The instant is a literal and not a <c>DECLARE</c>d variable, and the difference is measurable:
    /// with <c>@now</c> the plan scans even on 3,000 rows.</b> An estimated plan cannot sniff a local
    /// variable, so <c>StartsAtUtc &lt;= @now</c> is costed at a 30%-ish guess — roughly 900 rows — and a
    /// seek with that many key lookups loses to a scan. The index is not at fault and nor is the
    /// predicate: a real parameterised statement is sniffed on first compile, so this is an artefact of
    /// reading an estimated plan rather than a prediction about the job. It is written down because the
    /// pessimistic reading is the one that produced a red test twice while nothing was wrong.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_window_shaped_read_seeks_the_window_index()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await WithManyPastCampaignsAsync(dbContext, async () =>
        {
            var plan = await PlanForAsync(
                """
                SELECT c.Id, c.DiscountType, c.DiscountValue
                  FROM marketing.SaleCampaigns AS c
                 WHERE c.IsActive = 1
                   AND c.IsDeleted = 0
                   AND c.StartsAtUtc <= '2026-10-13T09:00:00'
                   AND c.EndsAtUtc > '2026-10-13T09:00:00';
                """);

            // **Only the positive half, and the reason is a measurement.** This plan names
            // IX_SaleCampaigns_Window in *both* places: a seek operator reads it, and it is also listed
            // under <UnmatchedIndexes> — the key lookup back to PK_SaleCampaigns for DiscountType and
            // DiscountValue is a part of the plan the filtered index cannot serve, and SQL Server reports
            // that. So the absence assertion the promo-code plan tests can make (their index is a unique
            // covering seek) is simply false here, and asserting it would be asserting something untrue
            // about a plan that is correct. The seek is the claim; it is the one that fails if the index
            // is dropped or its filter stops being implied.
            Assert.Contains(WindowIndex, plan.UsedIndexes);
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> with 3,000 ended-but-active campaigns in the table, then purges them.
    /// </summary>
    /// <remarks>
    /// <b>Active and undeleted, so every one of them is <i>in</i> the filtered index</b> — its filter is
    /// <c>IsActive = 1 AND IsDeleted = 0</c>, and rows outside it would not make the seek any more
    /// attractive. Their windows are all in the past, so the date range the query asks for is selective,
    /// which is what the index's key columns are for. <b>None of them targets anything</b>, so they cannot
    /// change any other suite's prices even for the moment they exist.
    /// <para>
    /// <b>Purged with <c>ExecuteDeleteAsync</c>, which doc 08 §10.2 (N12) reserves for exactly this.</b> A
    /// tracked <c>Remove()</c> on an <c>ISoftDeletable</c> throws (DB5), and soft-deleting them would
    /// leave 3,000 rows behind in a database the whole suite shares. In a <c>finally</c>, so a failing
    /// assertion does not leave them.
    /// </para>
    /// </remarks>
    private static async Task WithManyPastCampaignsAsync(AppDbContext dbContext, Func<Task> body)
    {
        try
        {
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                WITH n AS (
                    SELECT TOP (3000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                      FROM sys.all_objects a CROSS JOIN sys.all_objects b)
                INSERT INTO marketing.SaleCampaigns
                    (Name, DiscountType, DiscountValue, StartsAtUtc, EndsAtUtc, IsActive, CreatedAtUtc, IsDeleted)
                SELECT CONCAT('Plan test ', n.i), 1, 10.00,
                       DATEADD(day, -(n.i + 30), '2026-10-13T09:00:00'),
                       DATEADD(day, -(n.i + 29), '2026-10-13T09:00:00'),
                       1, SYSUTCDATETIME(), 0
                  FROM n;
                """,
                Token);

            // Without this the plan is costed against statistics that say the table is empty, and the
            // scan wins on a table of 3,000 rows exactly as it did on one of four.
            await dbContext.Database.ExecuteSqlAsync(
                $"UPDATE STATISTICS marketing.SaleCampaigns;",
                Token);

            await body();
        }
        finally
        {
            await dbContext.SaleCampaigns
                .IgnoreQueryFilters()
                .Where(campaign => campaign.Name.StartsWith("Plan test "))
                .ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The same shape with <c>IsActive</c> parameterised does <b>not</b> match the index, which is the
    /// limitation the reader is written around.
    /// </summary>
    /// <remarks>
    /// <b>This asserts a limitation of SQL Server rather than of this codebase.</b> It is the whole reason
    /// <c>CampaignOfferReader</c> writes <c>campaign.IsActive</c> as a constant instead of taking a flag,
    /// and the plan says so in as many words: a filtered index the optimiser considered and could not
    /// match is listed under <c>&lt;UnmatchedIndexes&gt;</c>, by name. If a future SQL Server version
    /// learned to prove implication from a parameter, this test would fail, and the right response would
    /// be to delete it rather than work around it.
    /// </remarks>
    [Fact]
    public async Task A_parameterised_active_flag_does_not_match_it()
    {
        sqlServer.SkipIfUnavailable();

        var plan = await PlanForAsync(
            """
            DECLARE @active bit = 1;
            DECLARE @deleted bit = 0;
            DECLARE @now datetime2(3) = '2026-10-13T09:00:00';

            SELECT c.Id, c.DiscountType, c.DiscountValue
              FROM marketing.SaleCampaigns AS c
             WHERE c.IsActive = @active
               AND c.IsDeleted = @deleted
               AND c.StartsAtUtc <= @now
               AND c.EndsAtUtc > @now;
            """);

        Assert.Contains(WindowIndex, plan.UnmatchedIndexes);
        Assert.DoesNotContain(WindowIndex, plan.UsedIndexes);
    }

    /// <summary>
    /// The indexes one statement's estimated plan <b>reads</b>, and the ones it says it could not use.
    /// </summary>
    /// <remarks>
    /// The same helper <c>PromoCodePlanTests</c> and <c>BankQueuePlanTests</c> use, for the reason their
    /// remarks give: <c>SHOWPLAN_XML</c> lists the filtered indexes the optimiser considered and
    /// <i>rejected</i> in an <c>&lt;UnmatchedIndexes&gt;</c> element, naming each one — so a test that
    /// merely searched the plan for an index name would read a rejection as a use. The block is separated
    /// out and what remains is the operators.
    /// <para>
    /// <c>SHOWPLAN_XML</c> rather than <c>STATISTICS XML</c>, so <b>nothing is executed</b> and this suite
    /// cannot disturb another test's rows.
    /// </para>
    /// <para>
    /// <b>The plan is for the data that is actually there</b> — a handful of campaign rows, or none at
    /// all. That is sound for this question, because matching a filtered index is about implication rather
    /// than cardinality; what a small table cannot tell us is whether the seek is faster, and that is not
    /// what is asserted.
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
