using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Ops.Email;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Ops;

/// <summary>
/// That the statements <c>SendEmails</c> runs every sweep actually <b>match</b> the filtered indexes on
/// <c>ops.EmailOutbox</c> (doc 08 §1.6 DB28).
/// <para>
/// <b>W3-06's tester added this suite because the branch shipped without it and the defect it catches was
/// live.</b> Both indexes are filtered on <c>Status</c>, and SQL Server matches a filtered index only when
/// the query's predicate <i>provably implies</i> the filter: a literal implies it, a parameter does not.
/// <c>ReleaseExpiredLeasesAsync</c> was written with <c>WHERE Status = {hole}</c> — and
/// <c>ExecuteSqlInterpolatedAsync</c> turns every interpolation hole into a parameter — so the one
/// statement <c>IX_EmailOutbox_Stuck</c> was added to serve could not use it: measured at <b>20,101
/// logical reads against 18</b> on a table of 20,003 rows, run once every thirty seconds for ever, with
/// the index paid for on every insert and read by nothing. Nothing failed and nothing said so.
/// </para>
/// <para>
/// This follows <c>BankQueuePlanTests</c>, which #103's review asked for by name and for the same reason.
/// A new filtered index belongs with a plan test, because its own correctness is invisible in the code
/// that depends on it.
/// </para>
/// </summary>
public sealed class EmailOutboxPlanTests(SqlServerDatabaseFixture sqlServer)
{
    private const string DueIndex = "IX_EmailOutbox_Due";
    private const string StuckIndex = "IX_EmailOutbox_Stuck";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Literal(EmailOutboxStatus status) =>
        ((byte)status).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The crashed-lease recovery statement's shape seeks <c>IX_EmailOutbox_Stuck</c> — the optimiser
    /// does not list it among the indexes it had to reject.
    /// </summary>
    /// <remarks>
    /// <b>W4-02a split this statement in two</b> (the kill reports which rows it killed, so doc 11 §4 can
    /// raise an alert for each; the requeue only has to report how many), and both halves keep the same
    /// predicate on <c>Status</c> and <c>LockedUntilUtc</c>. This probe is the requeue.
    /// </remarks>
    [Fact]
    public async Task The_recovery_statements_shape_seeks_the_stuck_index()
    {
        sqlServer.SkipIfUnavailable();

        var plan = await PlanForAsync(
            $"""
            DECLARE @now datetime2(3) = SYSUTCDATETIME();

            UPDATE ops.EmailOutbox
               SET Status = {Literal(EmailOutboxStatus.Failed)},
                   LockedUntilUtc = NULL,
                   NextAttemptAtUtc = @now,
                   LastError = N'probe'
             WHERE Status = {Literal(EmailOutboxStatus.Sending)}
               AND LockedUntilUtc IS NOT NULL
               AND LockedUntilUtc < @now
               AND AttemptCount < MaxAttempts;
            """);

        Assert.Contains(StuckIndex, plan.UsedIndexes);
        Assert.DoesNotContain(StuckIndex, plan.UnmatchedIndexes);
    }

    /// <summary>
    /// And so does the statement that kills a stranded final attempt (W4-02a) — the one whose
    /// <c>OUTPUT</c> an <c>EmailDeliveryFailed</c> alert is raised from.
    /// </summary>
    /// <remarks>
    /// It runs every sweep for ever, like the requeue beside it, so the same measurement applies: with a
    /// parameterised status this would scan a table full of <c>nvarchar(max)</c> bodies instead of seeking
    /// an index that is almost always empty.
    /// </remarks>
    [Fact]
    public async Task The_kill_statements_shape_seeks_the_stuck_index()
    {
        sqlServer.SkipIfUnavailable();

        // **The statement itself, not a copy of it.** The two parameters it takes are declared in front
        // of it so the batch can be given to SHOWPLAN_XML; everything the optimiser reads — the
        // predicate, the statuses, the OUTPUT clause — is the text `EmailOutboxStore` really sends.
        // A copy here would go green on its own while the real statement scanned the table.
        var plan = await PlanForAsync(
            $"""
            DECLARE @now datetime2(3) = SYSUTCDATETIME();
            DECLARE @failure nvarchar(2000) = N'probe';

            {EmailOutboxStore.KillStrandedExhaustedSql}
            """);

        Assert.Contains(StuckIndex, plan.UsedIndexes);
        Assert.DoesNotContain(StuckIndex, plan.UnmatchedIndexes);
    }

    /// <summary>
    /// The same statement with the status as a parameter does <b>not</b> — the optimiser names the index
    /// as one it considered and could not use. This is the shape the branch shipped with.
    /// </summary>
    /// <remarks>
    /// <b>This asserts a limitation of SQL Server rather than of this codebase</b>, and it is the entire
    /// reason the production statement writes its statuses into the command text. If a future SQL Server
    /// learned to prove implication from a parameter, this test would fail, and the right response would
    /// be to delete it and relax the store's remark — not to work around it.
    /// <para>
    /// <b>The assertion is on the unmatched set and not on the used set</b>, because an <c>UPDATE</c>'s
    /// plan names every index it has to <i>maintain</i> as well as every index it reads: this statement
    /// moves rows into and out of both filtered indexes, so both appear among the operators whether the
    /// seek was matched or not. The optimiser's own <c>&lt;UnmatchedIndexes&gt;</c> list is the signal,
    /// and it is the one the literal form above is free of.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_same_statement_with_a_parameterised_status_does_not()
    {
        sqlServer.SkipIfUnavailable();

        var plan = await PlanForAsync(
            """
            DECLARE @now datetime2(3) = SYSUTCDATETIME();
            DECLARE @sending tinyint = 1;
            DECLARE @dead tinyint = 4;
            DECLARE @failed tinyint = 3;

            UPDATE ops.EmailOutbox
               SET Status = CASE WHEN AttemptCount >= MaxAttempts THEN @dead ELSE @failed END,
                   LockedUntilUtc = NULL,
                   NextAttemptAtUtc = @now,
                   LastError = N'probe'
             WHERE Status = @sending
               AND LockedUntilUtc IS NOT NULL
               AND LockedUntilUtc < @now;
            """);

        Assert.Contains(StuckIndex, plan.UnmatchedIndexes);
    }

    /// <summary>
    /// And the statement <c>EmailOutboxStore.ReleaseExpiredLeasesAsync</c> really sends writes its
    /// statuses as literals.
    /// </summary>
    /// <remarks>
    /// <b>The guard against the defect coming back.</b> The two tests above establish that the index can
    /// be matched and when it cannot; this one reads the SQL the store actually sends, captured through
    /// an EF command interceptor rather than copied into this file. A developer moving a status back into
    /// an interpolation hole — the ordinary, readable way to write this, and exactly how it shipped —
    /// leaves both of those green and makes this one red.
    /// </remarks>
    [Fact]
    public async Task The_recovery_statement_writes_its_statuses_as_literals()
    {
        sqlServer.SkipIfUnavailable();

        var sql = await CapturedRecoverySqlAsync();

        // The predicate's status is what decides whether the filtered index can be matched.
        Assert.Contains($"Status = {Literal(EmailOutboxStatus.Sending)}", sql, StringComparison.Ordinal);
        Assert.Contains($"SET Status = {Literal(EmailOutboxStatus.Failed)}", sql, StringComparison.Ordinal);

        // W4-02a: the requeue no longer kills anything, so it carries no Dead branch at all — and it has
        // to say so, or an exhausted row would be put back on a ladder it has run out of.
        Assert.Contains("AttemptCount < MaxAttempts", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CASE", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the kill statement <see cref="EmailOutboxStore.KillStrandedExhaustedAsync"/> really sends
    /// writes its statuses as literals too, and reads back the ids an alert is raised from.
    /// </summary>
    /// <remarks>
    /// <b>The <c>OUTPUT</c> clause is asserted as well as the literals.</b> It is the whole reason this
    /// statement exists separately (doc 11 §4, G-37): a kill that reported only a count would leave a
    /// dead message with no alert and nobody told, which is the defect the statement was split to fix.
    /// Removing it is the ordinary, readable simplification, and it would leave every other test here
    /// green.
    /// </remarks>
    [Fact]
    public void The_kill_statement_writes_its_statuses_as_literals_and_outputs_the_ids()
    {
        var sql = EmailOutboxStore.KillStrandedExhaustedSql;

        Assert.Contains($"Status = {Literal(EmailOutboxStatus.Sending)}", sql, StringComparison.Ordinal);
        Assert.Contains($"SET Status = {Literal(EmailOutboxStatus.Dead)}", sql, StringComparison.Ordinal);
        Assert.Contains("AttemptCount >= MaxAttempts", sql, StringComparison.Ordinal);
        Assert.Contains("OUTPUT inserted.Id", sql, StringComparison.Ordinal);
        Assert.Contains("TransportOverrideProtected = NULL", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The due count — the one statement a sweep runs on a host with no SMTP configured, which is the
    /// host this shop is — seeks <c>IX_EmailOutbox_Due</c>, because EF renders the two enum constants as
    /// literals.
    /// </summary>
    /// <remarks>
    /// This one was already correct, and <c>EmailOutboxStore.CountDueAsync</c>'s own remark says why. It
    /// is pinned because the reason is invisible in the code: hoisting either status into a local would
    /// turn a seek into a scan of every body in the table, every thirty seconds, with nothing failing.
    /// </remarks>
    [Fact]
    public async Task The_due_count_seeks_the_due_index()
    {
        sqlServer.SkipIfUnavailable();

        using var scope = sqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = DateTime.UtcNow;

        var sql = dbContext.EmailOutbox
            .AsNoTracking()
            .Where(message =>
                (message.Status == EmailOutboxStatus.Pending || message.Status == EmailOutboxStatus.Failed)
                && message.NextAttemptAtUtc <= now)
            .Select(message => message.Id)
            .ToQueryString();

        // Asserted before the plan, so a failure here says *why* the plan changed rather than only that
        // it did: the two statuses must reach SQL Server as values and not as parameter names.
        Assert.DoesNotContain("@__", sql, StringComparison.Ordinal);

        var plan = await PlanForAsync(sql);

        Assert.Contains(DueIndex, plan.UsedIndexes);
        Assert.DoesNotContain(DueIndex, plan.UnmatchedIndexes);
    }

    /// <summary>
    /// The claim seeks the due index too, which is what makes an empty sweep one seek rather than a scan
    /// of the whole queue.
    /// </summary>
    [Fact]
    public async Task The_claim_seeks_the_due_index()
    {
        sqlServer.SkipIfUnavailable();

        var plan = await PlanForAsync(
            $"""
            DECLARE @now datetime2(3) = SYSUTCDATETIME();
            DECLARE @leaseUntil datetime2(3) = DATEADD(minute, 5, @now);

            WITH due AS (
                SELECT TOP (20) *
                  FROM ops.EmailOutbox WITH (ROWLOCK, READPAST, UPDLOCK)
                 WHERE Status IN ({Literal(EmailOutboxStatus.Pending)}, {Literal(EmailOutboxStatus.Failed)})
                   AND NextAttemptAtUtc <= @now
                 ORDER BY NextAttemptAtUtc, Id
            )
            UPDATE due
               SET Status = {Literal(EmailOutboxStatus.Sending)},
                   LockedUntilUtc = @leaseUntil,
                   AttemptCount = AttemptCount + 1
            OUTPUT inserted.Id;
            """);

        Assert.Contains(DueIndex, plan.UsedIndexes);
        Assert.DoesNotContain(DueIndex, plan.UnmatchedIndexes);
    }

    /// <summary>
    /// The SQL <see cref="EmailOutboxStore.ReleaseExpiredLeasesAsync"/> sends, captured from the command
    /// EF builds for it.
    /// </summary>
    /// <remarks>
    /// <b>Run inside a transaction that is rolled back.</b> The statement is set-based over the whole
    /// queue and this assembly shares one database, so executing it for real would be a write on rows
    /// another test owns. The command text is what is wanted, and it is built before the statement runs.
    /// </remarks>
    private async Task<string> CapturedRecoverySqlAsync()
    {
        var interceptor = new SqlCapture();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(builder => builder
            .UseSqlServer(sqlServer.ConnectionString)
            .AddInterceptors(interceptor));

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(Token);

        await new EmailOutboxStore(dbContext).ReleaseExpiredLeasesAsync(DateTime.UtcNow, Token);

        await transaction.RollbackAsync(Token);

        return Assert.Single(interceptor.Commands);
    }

    private sealed class SqlCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            Commands.Add(command.CommandText);

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// The indexes one statement's estimated plan reads, and the filtered ones it says it could not use.
    /// </summary>
    /// <remarks>
    /// <c>SHOWPLAN_XML</c>, so nothing is executed. The <c>&lt;UnmatchedIndexes&gt;</c> block has to be
    /// separated out before looking for a seek, because the optimiser names a filtered index it
    /// <i>rejected</i> in there — <c>BankQueuePlanTests</c> learned that the hard way, and the lesson is
    /// reused here rather than relearned.
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

    /// <param name="UsedIndexes">Indexes an operator in the plan actually reads.</param>
    /// <param name="UnmatchedIndexes">
    /// Filtered indexes the optimiser named as considered and not usable — which for a parameterised
    /// predicate is doc 08 §1.6 DB28 in the optimiser's own words.
    /// </param>
    private sealed record PlanIndexes(
        IReadOnlySet<string> UsedIndexes,
        IReadOnlySet<string> UnmatchedIndexes);
}
