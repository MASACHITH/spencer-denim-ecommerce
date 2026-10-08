using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Ops.Email;

/// <summary>
/// The statements <c>SendEmails</c> runs against <c>ops.EmailOutbox</c> (doc 08 §3.8).
/// </summary>
/// <remarks>
/// <b>The claim is the only thing standing between two senders, and it is enough</b> —
/// <c>ExpireUnpaidOrders</c>'s conclusion about <c>ops.JobLeases</c> applied to a queue. That table is in
/// doc 08 §3.8 and in no migration, and none is needed here: <c>UPDATE TOP (20) … OUTPUT</c> with
/// <c>Status IN (0, 3)</c> in its <c>WHERE</c> means SQL Server takes an update lock on each qualifying
/// row and re-evaluates the predicate against the row as it now is, so of any number of senders racing
/// for one message exactly one gets it and the others get a different batch or an empty one. A job lease
/// would make the <i>job</i> run once; the claim makes each <i>message</i> send once, which is the
/// invariant that matters when the thing being sent is somebody's bank details.
/// <para>
/// <b>Raw ADO rather than EF for the claim, for <c>NumberSeriesAllocator</c>'s reason.</b> The claimed
/// rows are in the statement's <c>OUTPUT</c> result set: <c>ExecuteSqlInterpolated</c> returns a row count
/// and discards them, and <c>SqlQuery</c> composes the SQL into a subquery, which an <c>UPDATE</c> cannot
/// be. A <c>DbCommand</c> on EF's own connection is the only shape that runs the statement verbatim and
/// reads what it emitted.
/// </para>
/// <para>
/// <b>No transaction anywhere in this class.</b> Each statement is a single atomic <c>UPDATE</c>, and
/// wrapping the claim and the send together would hold a row lock across a network conversation with a
/// mail server — which doc 08 §10.3 forbids outright ("Calls to PayPal and SMTP are never made inside a
/// retried transaction"). The lease is what covers the gap instead.
/// </para>
/// <para>
/// <b>Doc 08 §10.3's retry still applies, and the claim has to ask for it by name.</b> EF wraps its own
/// single operations in the execution strategy automatically, which covers every statement here except
/// one; it cannot wrap a raw <c>DbCommand</c>, so <see cref="ClaimAsync"/> does it explicitly. Without
/// that, a 1205 between contending senders reaches the sweep as an exception and costs a whole cycle.
/// </para>
/// </remarks>
internal sealed class EmailOutboxStore(AppDbContext dbContext) : IEmailOutboxStore
{
    /// <summary>
    /// What <see cref="ReleaseExpiredLeasesAsync"/> writes into <c>LastError</c>, so an operator reading
    /// the row can tell a crashed sender from a mail server that refused the message.
    /// </summary>
    internal const string LeaseExpiredError =
        "A sender claimed this message and did not finish — the process stopped, or it lost its database "
        + "connection. The attempt was counted and the message was put back in the queue.";

    public async Task<int> CountDueAsync(DateTime nowUtc, CancellationToken cancellationToken) =>
        await dbContext.EmailOutbox
            .AsNoTracking()

            // The two statuses are compile-time constants, not a parameter or a captured list, which is
            // what lets the optimiser prove this predicate implies IX_EmailOutbox_Due's own
            // `WHERE Status IN (0,3)` filter (DB28). A `Status = @p` here would scan a table full of
            // message bodies every sweep with nothing failing.
            .Where(message =>
                (message.Status == EmailOutboxStatus.Pending || message.Status == EmailOutboxStatus.Failed)
                && message.NextAttemptAtUtc <= nowUtc)
            .CountAsync(cancellationToken);

    /// <summary>
    /// Puts messages whose lease has expired back on the ladder, or kills the ones that have no attempt
    /// left.
    /// </summary>
    /// <remarks>
    /// <b>The <c>CASE</c> is not decoration.</b> Without it, a message on its final attempt whose sender
    /// died would go back to <c>Failed</c>, be reclaimed at once (its <c>NextAttemptAtUtc</c> is already
    /// in the past), and have its counter pushed past its own ceiling before the failure path noticed.
    /// The outcome would be the same — dead one attempt later — but the row would spend that time
    /// claiming a seventh attempt on a six-rung ladder, and the figure an operator reads would be wrong.
    /// <para>
    /// <b>A released message is due immediately rather than one rung further along</b>, because a sender
    /// that stopped is not evidence that the mail server is unwell: the backoff ladder exists for a
    /// refusing server, and making a shopper wait four hours for a container restart would be the wrong
    /// lesson drawn from the wrong signal. The attempt counter is what bounds a crash loop.
    /// </para>
    /// <para>
    /// <b>The statuses are written into the statement text and only the clock is a parameter, which is
    /// what lets this statement use <c>IX_EmailOutbox_Stuck</c> (DB28).</b> It is the same rule
    /// <see cref="CountDueAsync"/> and <see cref="ClaimAsync"/> already follow, and it was got wrong here:
    /// the first version used <c>ExecuteSqlInterpolatedAsync</c>, which turns <i>every</i> interpolation
    /// hole into a parameter — so <c>WHERE Status = @p</c> could not be proved to imply the index's
    /// <c>WHERE Status = 1</c> filter and the optimiser listed the index as unmatched and scanned the
    /// table instead. Measured on 20,003 rows: <b>20,101 logical reads against 18</b>, once every sweep
    /// for ever, on a table whose rows carry <c>nvarchar(max)</c> bodies. <c>EmailOutboxPlanTests</c>
    /// reads the plan and the command text, because neither is visible from here.
    /// </para>
    /// </remarks>
    public async Task<int> ReleaseExpiredLeasesAsync(DateTime nowUtc, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlRawAsync(
            $$"""
            UPDATE ops.EmailOutbox
               SET Status = CASE
                              WHEN AttemptCount >= MaxAttempts THEN {{(byte)EmailOutboxStatus.Dead}}
                              ELSE {{(byte)EmailOutboxStatus.Failed}}
                            END,
                   LockedUntilUtc = NULL,
                   NextAttemptAtUtc = {0},
                   LastError = {1},
                   TransportOverrideProtected = CASE
                              WHEN AttemptCount >= MaxAttempts THEN NULL
                              ELSE TransportOverrideProtected
                            END
             WHERE Status = {{(byte)EmailOutboxStatus.Sending}}
               AND LockedUntilUtc IS NOT NULL
               AND LockedUntilUtc < {0};
            """,
            [nowUtc, LeaseExpiredError],
            cancellationToken);

    /// <summary>
    /// Doc 08 §3.8: "the worker claims batches with <c>UPDATE TOP (20) … OUTPUT</c>".
    /// </summary>
    /// <remarks>
    /// <b>A CTE rather than doc 08's bare <c>UPDATE TOP (20)</c>, for two things that form cannot
    /// express.</b> It is the same statement — one atomic claim of up to <i>n</i> rows with an
    /// <c>OUTPUT</c> clause — wrapped so that it can carry an <c>ORDER BY</c> (see below) and the
    /// <c>READPAST</c> hint, which makes a second sender skip rows the first is already locking instead
    /// of queueing behind them. A bare <c>UPDATE TOP</c> has nowhere to put either.
    /// <para>
    /// <b>Two kinds of value are written into the statement text rather than parameterised, and both are
    /// deliberate.</b> The statuses are compile-time constants because a parameter would stop the
    /// optimiser proving the predicate implies <c>IX_EmailOutbox_Due</c>'s filter (DB28). The batch size
    /// is literal because it comes from configuration the host validates to a small positive integer
    /// before it starts (<c>Email:BatchSize</c>), so the plan is built for the batch it will use. The two
    /// values that vary per call — the clock and the lease — are parameters.
    /// </para>
    /// <para>
    /// <b><c>AttemptCount</c> is incremented by the claim, not by the failure path.</b> A message that is
    /// claimed and then lost — the process is killed, the connection drops — has been attempted as far as
    /// anybody can tell, and a counter that only moved on a <i>recorded</i> failure would let a crash loop
    /// retry one message for ever. The cost is that a message can be marked attempted without a mail
    /// server having seen it, which is the safe direction.
    /// </para>
    /// <para>
    /// <b>Ordered oldest-due first</b>, then by id, for <c>OrderExpiryStore.FindDueOrdersAsync</c>'s
    /// reason: two messages queued in the same millisecond share a <c>NextAttemptAtUtc</c> to the
    /// precision of <c>datetime2(3)</c>, and a sort on that column alone returns them in whatever order
    /// the engine finds them — which makes "oldest first" something nothing can rely on or assert. It is
    /// free: <c>IX_EmailOutbox_Due</c> is keyed on <c>NextAttemptAtUtc</c> and every row in it carries the
    /// clustered key as its locator.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<EmailOutboxClaim>> ClaimAsync(
        int batchSize,
        DateTime nowUtc,
        DateTime leaseUntilUtc,
        CancellationToken cancellationToken)
    {
        if (batchSize <= 0)
        {
            return [];
        }

        // **The one statement in this class that has to name the execution strategy.** Doc 08 §10.3 puts
        // `EnableRetryOnFailure(3)` on the context; EF applies it by itself to a single operation with no
        // user transaction open, which every other statement in this class is, but it cannot apply it to
        // a raw `DbCommand` — and this one has to be raw in order to read its own `OUTPUT` rows. Without
        // this, senders contending for one small table can hand a 1205 (deadlock victim) or a lock
        // timeout straight out to the sweep, which catches it, logs "the email sweep could not run" and
        // waits a whole cycle over a fault EF already classifies as transient.
        //
        // **A retry is safe because the claim is atomic.** If the first attempt committed and only its
        // answer was lost, the retry sees those rows at `Sending` and claims a different batch; the first
        // batch comes back through `ReleaseExpiredLeasesAsync` when its lease runs out. What a retry
        // cannot do is send anything twice, which is the only outcome that would not be acceptable.
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(() =>
            ClaimOnceAsync(batchSize, nowUtc, leaseUntilUtc, cancellationToken));
    }

    private async Task<IReadOnlyList<EmailOutboxClaim>> ClaimOnceAsync(
        int batchSize,
        DateTime nowUtc,
        DateTime leaseUntilUtc,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var openedHere = false;

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
            openedHere = true;
        }

        try
        {
            await using var command = connection.CreateCommand();

            command.CommandText =
                $"""
                WITH due AS (
                    SELECT TOP ({batchSize}) *
                      FROM ops.EmailOutbox WITH (ROWLOCK, READPAST, UPDLOCK)
                     WHERE Status IN ({(byte)EmailOutboxStatus.Pending}, {(byte)EmailOutboxStatus.Failed})
                       AND NextAttemptAtUtc <= @now
                     ORDER BY NextAttemptAtUtc, Id
                )
                UPDATE due
                   SET Status = {(byte)EmailOutboxStatus.Sending},
                       LockedUntilUtc = @leaseUntil,
                       AttemptCount = AttemptCount + 1
                OUTPUT inserted.Id,
                       inserted.TemplateCode,
                       inserted.ToEmail,
                       inserted.ToName,
                       inserted.Subject,
                       inserted.BodyHtml,
                       inserted.BodyText,
                       inserted.AttemptCount,
                       inserted.MaxAttempts,
                       inserted.RelatedEntityId;
                """;

            AddParameter(command, "@now", nowUtc);
            AddParameter(command, "@leaseUntil", leaseUntilUtc);

            var claims = new List<EmailOutboxClaim>(batchSize);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                claims.Add(new EmailOutboxClaim(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetInt16(7),
                    reader.GetInt16(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }

            return claims;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// Doc 08 §3.8's terminal success: <c>Sent</c>, stamped, lease cleared, and the two columns R5 and
    /// G-03 require clearing.
    /// </summary>
    /// <remarks>
    /// <b>The guard is this claim and no other.</b> <c>Status = 1 AND AttemptCount = @attempt</c> means
    /// "only if the row is still the one I claimed". A lease that expired and was reclaimed by another
    /// sender has a higher counter, so this statement affects nothing rather than overwriting that
    /// sender's work — and the 0 rows are returned to the caller, which logs them, because a message sent
    /// twice is the one failure this whole class is arranged to make visible. The status alone would not
    /// be enough: the row would be back at <c>Sending</c> under the second sender's lease, and this
    /// statement would happily stamp it Sent while that send was still in the air.
    /// <para>
    /// <b><c>TransportOverrideProtected</c> is NULLed</b> — doc 08 §3.8 R5: it is cleared "on any terminal
    /// status (Sent, Dead, Cancelled)". Nothing writes it in this build (the dual-send is the settings
    /// task's), so this is the rule being honoured ahead of its first writer rather than a no-op that
    /// happens to be true.
    /// </para>
    /// <para>
    /// <b>The body is destroyed for the two token-bearing templates</b>
    /// (<c>EmailOutboxMessage.BodyIsClearedOnSend</c>, doc 08 §3.8 G-03): "the body must not sit in the
    /// outbox table or in a backup". Neither template exists yet — both are W3-01's — and the rule is
    /// built now because the alternative is a privacy rule written in a document that the task adding
    /// those templates has to remember to come and find.
    /// </para>
    /// </remarks>
    public async Task<bool> MarkSentAsync(
        EmailOutboxClaim claim,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        if (EmailOutboxMessage.BodyIsClearedOnSend(claim.TemplateCode))
        {
            return await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE ops.EmailOutbox
                   SET Status = {(byte)EmailOutboxStatus.Sent},
                       SentAtUtc = {nowUtc},
                       LockedUntilUtc = NULL,
                       LastError = NULL,
                       TransportOverrideProtected = NULL,
                       BodyHtml = NULL,
                       BodyText = NULL
                 WHERE Id = {claim.Id}
                   AND Status = {(byte)EmailOutboxStatus.Sending}
                   AND AttemptCount = {claim.AttemptCount};
                """,
                cancellationToken) == 1;
        }

        return await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE ops.EmailOutbox
               SET Status = {(byte)EmailOutboxStatus.Sent},
                   SentAtUtc = {nowUtc},
                   LockedUntilUtc = NULL,
                   LastError = NULL,
                   TransportOverrideProtected = NULL
             WHERE Id = {claim.Id}
               AND Status = {(byte)EmailOutboxStatus.Sending}
               AND AttemptCount = {claim.AttemptCount};
            """,
            cancellationToken) == 1;
    }

    /// <summary>
    /// Records a failed attempt, under the same "still the row I claimed" guard as
    /// <see cref="MarkSentAsync"/>.
    /// </summary>
    /// <remarks>
    /// <b>Which branch is taken is decided by <see cref="EmailBackoff"/> and not by this class</b>, from
    /// the counter the claim's own <c>OUTPUT</c> returned — so the ladder is in one place and the two
    /// statements here are only ever "write the next attempt time" or "write the end".
    /// </remarks>
    public async Task<EmailFailureOutcome> MarkFailedAsync(
        EmailOutboxClaim claim,
        string failure,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);

        var clipped = Clip(failure);
        var delay = EmailBackoff.DelayAfter(claim.AttemptCount, claim.MaxAttempts);

        if (delay is not { } wait)
        {
            // The ladder is exhausted. Terminal: no sweep claims a Dead row, because 4 is outside
            // IX_EmailOutbox_Due's filter, and v1 has no screen that could retry it (doc 11 §7 G-42).
            var killed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE ops.EmailOutbox
                   SET Status = {(byte)EmailOutboxStatus.Dead},
                       LockedUntilUtc = NULL,
                       LastError = {clipped},
                       TransportOverrideProtected = NULL
                 WHERE Id = {claim.Id}
                   AND Status = {(byte)EmailOutboxStatus.Sending}
                   AND AttemptCount = {claim.AttemptCount};
                """,
                cancellationToken);

            return killed == 1 ? EmailFailureOutcome.Dead : EmailFailureOutcome.NotOurs;
        }

        var deferred = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE ops.EmailOutbox
               SET Status = {(byte)EmailOutboxStatus.Failed},
                   LockedUntilUtc = NULL,
                   NextAttemptAtUtc = {nowUtc.Add(wait)},
                   LastError = {clipped}
             WHERE Id = {claim.Id}
               AND Status = {(byte)EmailOutboxStatus.Sending}
               AND AttemptCount = {claim.AttemptCount};
            """,
            cancellationToken);

        return deferred == 1 ? EmailFailureOutcome.Retrying : EmailFailureOutcome.NotOurs;
    }

    /// <summary>
    /// <c>LastError</c> is <c>nvarchar(2000)</c> (doc 08 §3.8). Clipped rather than refused: an exception
    /// message two characters too long must not be the reason a message's state is never recorded.
    /// </summary>
    private static string Clip(string failure) =>
        failure.Length <= EmailOutboxMessage.LastErrorMaxLength
            ? failure
            : failure[..EmailOutboxMessage.LastErrorMaxLength];

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
