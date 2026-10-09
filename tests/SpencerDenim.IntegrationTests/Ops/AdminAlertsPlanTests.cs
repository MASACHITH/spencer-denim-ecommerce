using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Ops.Alerts;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Ops;

/// <summary>
/// That the banner's two statements actually <b>match</b> <c>IX_AdminAlerts_Open</c> (doc 08 §1.6 DB28).
/// <para>
/// <b>A new filtered index belongs with a plan test, and this project has the measurement to prove
/// why.</b> W3-06 added <c>IX_EmailOutbox_Stuck</c> and the one statement it existed to serve could not
/// use it: <c>ExecuteSqlInterpolatedAsync</c> parameterises every interpolation hole, so
/// <c>WHERE Status = @p</c> could not be proved to imply the index's <c>WHERE Status = 1</c> filter.
/// Measured at <b>20,101 logical reads against 18</b> on 20,003 rows, every sweep, for ever — with
/// nothing failing and nothing saying so. <c>BankQueuePlanTests</c> and <c>EmailOutboxPlanTests</c> are
/// the precedent.
/// </para>
/// <para>
/// <b>This filter is <c>AcknowledgedAtUtc IS NULL</c>, which has nothing to parameterise</b>, so the
/// hazard here is a different one: the alert-type restriction R10 adds. EF renders a parameterised
/// collection through <c>OPENJSON</c>, which turns the seek into a join against a table-valued function
/// whose cardinality the optimiser has to guess — so <c>AdminAlertReader</c> wraps the types in
/// <c>EF.Constant</c>, and the first test below is what makes that visible.
/// </para>
/// </summary>
public sealed class AdminAlertsPlanTests(SqlServerDatabaseFixture sqlServer)
{
    private const string OpenIndex = "IX_AdminAlerts_Open";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The banner's read reaches SQL Server with its alert types as <b>values</b>, not as parameters.
    /// </summary>
    /// <remarks>
    /// <b>This pins a deliberate choice, not a correctness requirement, and the difference is measured.</b>
    /// R10 can produce exactly two lists, so as literals they give two stable cached plans; as parameters
    /// they give one plan compiled for whichever audience asked first. The filtered index is matched
    /// either way — its filter is on <c>AcknowledgedAtUtc</c>, and the alert type is a residual predicate
    /// — which a mutation showed by leaving every seek in this file unchanged.
    /// <para>
    /// <b>The guard was wrong first.</b> It looked for <c>@__</c>, EF's old parameter prefix; EF Core 10
    /// names these <c>@types1 … @types7</c>, so the mutant sent seven parameters and the assertion passed.
    /// It now looks for the literal list and for the absence of any parameter declaration — and both,
    /// because either one alone would go green on a shape nobody intended.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_banners_read_sends_its_alert_types_as_literals()
    {
        sqlServer.SkipIfUnavailable();

        var sql = OpenQueryString(AdminAlertTypes.All);

        Assert.Contains(
            $"IN ('{AdminAlertTypes.SensitiveSettingsChanged}'",
            sql,
            StringComparison.Ordinal);

        Assert.Contains($"'{AdminAlertTypes.EmailDeliveryFailed}'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DECLARE @", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENJSON", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A Super Admin's read — every alert type — seeks the filtered index.
    /// </summary>
    [Fact]
    public async Task A_super_admins_read_seeks_the_open_index()
    {
        sqlServer.SkipIfUnavailable();
        await SeedAsync();

        var plan = await PlanForAsync(OpenQueryString(AdminAlertTypes.All));

        Assert.Contains(OpenIndex, plan.UsedIndexes);
        Assert.DoesNotContain(OpenIndex, plan.UnmatchedIndexes);
    }

    /// <summary>
    /// And so does an order staff member's read, which is the narrower of the two lists R10 produces.
    /// </summary>
    /// <remarks>
    /// Both are pinned because they are different plans: with two literals in the <c>IN</c> list rather
    /// than seven, the optimiser's row estimate changes and it could in principle prefer a scan.
    /// </remarks>
    [Fact]
    public async Task An_order_staff_members_read_seeks_it_too()
    {
        sqlServer.SkipIfUnavailable();
        await SeedAsync();

        var plan = await PlanForAsync(OpenQueryString(AdminAlertVisibility.OrderStaffTypes));

        Assert.Contains(OpenIndex, plan.UsedIndexes);
        Assert.DoesNotContain(OpenIndex, plan.UnmatchedIndexes);
    }

    /// <summary>
    /// <b>And a <c>COUNT(*)</c> beside it does not, which is why there is no longer one.</b>
    /// </summary>
    /// <remarks>
    /// <b>This is the measurement that removed a field from the payload, so it is written down rather
    /// than remembered.</b> The banner used to carry an exact total, which needed a second statement over
    /// the same predicate. SQL Server costs that as a clustered scan however it is written — with the
    /// alert types in the predicate or without them, as <c>COUNT(*)</c> or <c>COUNT(Id)</c>, with or
    /// without a key predicate on <c>CreatedAtUtc</c> — because the filtered index gives it no key to
    /// seek on and the predicate is already implied by the filter. Measured on 20,000 alerts of this
    /// shape:
    /// <list type="bullet">
    /// <item><description>the list (<c>TOP (n) … ORDER BY CreatedAtUtc DESC, Id</c>): <b>38</b> logical
    /// reads;</description></item>
    /// <item><description>the same count with <c>WITH (INDEX(IX_AdminAlerts_Open))</c> forced:
    /// <b>38</b>;</description></item>
    /// <item><description>the count as the optimiser chooses it: <b>5,020</b>.</description></item>
    /// </list>
    /// An index hint would have bought the number back, and was not taken: it forbids the optimiser from
    /// ever choosing better and it is not a thing this codebase does anywhere else. So the reader asks
    /// for one row more than it needs and the payload carries "there are more" instead
    /// (<c>IAdminAlertReader</c>). What a staff member loses is a precise figure they do not act on; doc
    /// 11 §6.1 step 6 tells them to open every alert.
    /// <para>
    /// <b>The test asserts the scan rather than the seek</b>, which is the opposite of every other test
    /// in this file and is deliberate: adding the exact total back is the obvious readable change, and it
    /// has to fail loudly rather than quietly cost five thousand reads on every admin page load. If a
    /// future SQL Server learns to seek this, the right response is to delete this test and put the total
    /// back — not to work around it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_count_over_the_same_predicate_does_not_seek_the_open_index()
    {
        sqlServer.SkipIfUnavailable();
        await SeedAsync();

        var plan = await PlanForAsync(
            """
            SELECT COUNT(*)
              FROM ops.AdminAlerts AS a
             WHERE a.AcknowledgedAtUtc IS NULL;
            """);

        Assert.DoesNotContain(OpenIndex, plan.UsedIndexes);
    }

    /// <summary>
    /// The same read with the acknowledged column as a parameter does <b>not</b> match the index.
    /// </summary>
    /// <remarks>
    /// <b>This asserts a limitation of SQL Server rather than of this codebase</b>, and it is the whole
    /// reason the read spells <c>AcknowledgedAtUtc IS NULL</c> instead of comparing it to a nullable
    /// value a caller supplies — which is the readable way somebody would write "open alerts" if they
    /// were adding an "include acknowledged" switch. If a future SQL Server learned to prove implication
    /// from a parameter, the right response would be to delete this test, not to work around it.
    /// </remarks>
    [Fact]
    public async Task The_same_read_with_a_parameterised_acknowledgement_does_not()
    {
        sqlServer.SkipIfUnavailable();
        await SeedAsync();

        var plan = await PlanForAsync(
            """
            DECLARE @acknowledged datetime2(3) = NULL;

            SELECT TOP (10) a.Id
              FROM ops.AdminAlerts AS a
             WHERE (a.AcknowledgedAtUtc = @acknowledged OR (a.AcknowledgedAtUtc IS NULL AND @acknowledged IS NULL))
             ORDER BY a.CreatedAtUtc DESC;
            """);

        Assert.Contains(OpenIndex, plan.UnmatchedIndexes);
    }

    /// <summary>
    /// Fills <c>ops.AdminAlerts</c> with the shape a shop really reaches, so the plan means something.
    /// </summary>
    /// <remarks>
    /// <b>Without rows there is no plan test here at all, and the first version of this file was one.</b>
    /// On an empty table the optimiser scans the clustered index because a scan of nothing is cheapest, so
    /// every assertion about <c>IX_AdminAlerts_Open</c> read <c>PK_AdminAlerts</c> instead — a test that
    /// could not fail for the reason it was written, which is the trap this project has hit seven times in
    /// ten days.
    /// <para>
    /// <b>The shape is the one doc 08 §8.4 produces</b>: acknowledged alerts are kept a year and open ones
    /// are the handful nobody has dealt with yet, so 20,000 acknowledged rows against 12 open ones is what
    /// the filtered index exists for. Twenty thousand is also the order of magnitude W3-06's tester
    /// measured the outbox's filtered index at (20,003 rows), so the two measurements are comparable.
    /// </para>
    /// <para>
    /// Statistics are updated after the insert, because the optimiser chooses from what it has been told
    /// about the table rather than from what is in it — and a freshly bulk-loaded table it believes is
    /// empty gets the same clustered scan as an empty one.
    /// </para>
    /// <para>
    /// <b>The rows are as wide as the columns allow</b>, for the same reason the count matters: the
    /// choice between a seek with twelve key lookups and a scan of the whole clustered index turns on how
    /// many pages that scan is. W3-06's measurement was taken on a table whose rows carry
    /// <c>nvarchar(max)</c> bodies; alerts are smaller, so a probe table of short titles and null details
    /// is one the optimiser scans for free, and the first version of this file asserted a seek against
    /// exactly that and failed.
    /// </para>
    /// <para>
    /// Idempotent, and left behind on purpose. The suite shares one throwaway database the fixture
    /// created; <c>AdminAlertsBehaviourTests</c> empties this table before each of its own assertions, so
    /// the two classes can run in either order.
    /// </para>
    /// </remarks>
    private async Task SeedAsync()
    {
        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var open = await dbContext.AdminAlerts
            .AsNoTracking()
            .CountAsync(alert => alert.AcknowledgedAtUtc == null, Token);

        var total = await dbContext.AdminAlerts.AsNoTracking().CountAsync(Token);

        if (total >= Rows && open >= OpenRows)
        {
            return;
        }

        var userId = await AdminAlertsBehaviourTests.StaffUserIdAsync(dbContext, Token);

        // EF1002 is suppressed and the reason is that every hole is a compile-time constant or an int
        // this method just read back from the database: `Rows` and `OpenRows` are `const int` and
        // `userId` is an `int`. There is no caller-supplied value anywhere in this statement, and the
        // alternative — parameters — cannot carry `TOP (n)` or `UPDATE STATISTICS` anyway.
#pragma warning disable EF1002
        await dbContext.Database.ExecuteSqlRawAsync(
            $$"""
            DELETE FROM ops.AdminAlerts;

            WITH numbers AS (
                SELECT TOP ({{Rows}}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                  FROM sys.all_objects a CROSS JOIN sys.all_objects b
            )
            INSERT INTO ops.AdminAlerts
                (AlertType, Severity, Title, DetailsJson, RelatedEntityType, RelatedEntityId,
                 CreatedAtUtc, AcknowledgedByUserId, AcknowledgedAtUtc)
            SELECT
                CASE n % 3 WHEN 0 THEN 'EmailDeliveryFailed'
                           WHEN 1 THEN 'PaymentException'
                           ELSE 'CaptureRecoveryFailed' END,
                2,
                -- Padded to the column's width (nvarchar(200), doc 08 §3.8). A row's size is what
                -- decides whether a clustered scan is cheaper than a seek plus a key lookup, so a probe
                -- table of narrow rows would measure a table this one will never be.
                LEFT(CONCAT('A plan probe ', n, ' ', REPLICATE('x', 200)), 200),
                -- A JSON document of the size a ReconciliationDrift row will carry, built with
                -- CHAR(123) and CHAR(125) because ExecuteSqlRawAsync reads a brace in its SQL as a
                -- composite-format placeholder -- a literal JSON object in here is a FormatException
                -- rather than a row, and so is a comment that quotes one.
                CONCAT(CHAR(123), '"probe":"', REPLICATE('x', 600), '"', CHAR(125)),
                'EmailOutbox',
                CAST(n AS varchar(100)),
                DATEADD(minute, -n, SYSUTCDATETIME()),
                CASE WHEN n > {{OpenRows}} THEN {{userId}} END,
                CASE WHEN n > {{OpenRows}} THEN DATEADD(minute, -n, SYSUTCDATETIME()) END
              FROM numbers;

            UPDATE STATISTICS ops.AdminAlerts WITH FULLSCAN;
            """,
            Token);
#pragma warning restore EF1002
    }

    /// <summary>How many rows the probe table holds. A year of acknowledged alerts, and then some.</summary>
    private const int Rows = 20_000;

    /// <summary>How many of them are open — the handful a banner reads.</summary>
    private const int OpenRows = 12;

    /// <summary>
    /// The read the banner really runs, as a query string.
    /// </summary>
    /// <remarks>
    /// <b>Taken from <c>AdminAlertReader</c> itself, not rebuilt here.</b> The first version of this file
    /// wrote the query out again, and a mutation showed what that proved: swapping
    /// <see cref="EF.Constant{T}"/> for a plain collection in the reader — which turns the seek into an
    /// <c>OPENJSON</c> join — left every test in this file green, because they were all measuring a query
    /// this file had written. <c>AdminAlertReader.OpenPage</c> is <c>internal</c> for exactly this.
    /// </remarks>
    private string OpenQueryString(IReadOnlyList<string> alertTypes)
    {
        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        return new AdminAlertReader(dbContext).OpenPage(alertTypes, 10).ToQueryString();
    }

    /// <summary>
    /// The indexes one statement's estimated plan reads, and the filtered ones it says it could not use.
    /// </summary>
    /// <remarks>
    /// <c>SHOWPLAN_XML</c>, so nothing is executed. The <c>&lt;UnmatchedIndexes&gt;</c> block has to be
    /// separated out before looking for a seek, because the optimiser names a filtered index it
    /// <i>rejected</i> in there — <c>BankQueuePlanTests</c> learned that the hard way and
    /// <c>EmailOutboxPlanTests</c> reused the lesson rather than relearning it.
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

    private sealed record PlanIndexes(
        IReadOnlySet<string> UsedIndexes,
        IReadOnlySet<string> UnmatchedIndexes);
}
