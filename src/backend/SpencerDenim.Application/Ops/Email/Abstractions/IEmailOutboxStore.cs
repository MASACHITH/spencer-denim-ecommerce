namespace SpencerDenim.Application.Ops.Email.Abstractions;

/// <summary>
/// One claimed message, as the claim's <c>OUTPUT</c> clause answers it.
/// </summary>
/// <param name="AttemptCount">
/// The value <i>after</i> the claim incremented it, so 1 is the first attempt. The claim increments
/// because a message that is claimed and then lost — the process dies, the lease expires — has been
/// attempted as far as anybody can tell, and a counter that only moved on a recorded failure would let a
/// crash loop retry one message for ever.
/// </param>
/// <param name="RelatedEntityId">
/// The order's public token, or null for a message about nothing in particular. It is here for one
/// reason: doc 11 §4 requires the message that reaches <c>Dead</c> to be reported with "the template
/// code, the order number and the outbox row ID", and a log line that named only the row id would leave
/// an operator to go and look the order up.
/// </param>
public sealed record EmailOutboxClaim(
    long Id,
    string TemplateCode,
    string ToEmail,
    string? ToName,
    string Subject,
    string? BodyHtml,
    string? BodyText,
    short AttemptCount,
    short MaxAttempts,
    string? RelatedEntityId);

/// <summary>
/// The five statements <c>SendEmails</c> runs against <c>ops.EmailOutbox</c> (doc 08 §3.8).
/// </summary>
/// <remarks>
/// <b>Every one of them is set-based and conditional, which is doc 08 §5.2's argument applied to a
/// queue.</b> Two senders racing for one message do not both read "Pending": the first holds an update
/// lock on it for the length of its own statement, and the second — which asks for <c>READPAST</c> —
/// <i>skips</i> that row rather than queueing behind it, takes the next due one instead, and comes back
/// for the skipped one on a later sweep by which time it is <c>Sending</c> and outside the claim's
/// <c>Status IN (0, 3)</c>. So a message is sent once however many hosts are running, and no lease table
/// and no leader election is needed — which is the same conclusion <c>ExpireUnpaidOrders</c> reached
/// about <c>ops.JobLeases</c>, a table doc 08 §3.8 specifies and no migration creates.
/// <para>
/// <b>This paragraph said the opposite until W3-06's tester measured it</b> ("the second waits on the
/// row's update lock and re-evaluates the predicate"), which is what a claim <i>without</i>
/// <c>READPAST</c> would do and is the mechanism this one is written to avoid — see
/// <c>EmailOutboxStore.ClaimAsync</c>. Both arrangements are exactly-once; only one of them is the one
/// in the code, and a reader who believed the lock alone carried the guarantee would be free to drop the
/// hint. Measured over 900 contention runs (3, 6 and 8 concurrent senders, batches of 2 to 20): no row
/// claimed twice, no row lost, no deadlock, and never more than three rounds.
/// </para>
/// </remarks>
public interface IEmailOutboxStore
{
    /// <summary>
    /// How many messages are due now. Read only when there is no transport to send them with, so an
    /// operator can be told "<i>n</i> are waiting" rather than "something is wrong".
    /// </summary>
    Task<int> CountDueAsync(DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Kills the stranded messages that have no attempt left — a sender died holding its lease on their
    /// <i>final</i> attempt — and answers their ids.
    /// <para>
    /// <b>This is the second of the two ways a row reaches <c>Dead</c></b> (the first is
    /// <see cref="MarkFailedAsync"/> running out of ladder), and the ids are returned because doc 11 §4
    /// (G-37) requires <i>every</i> row that reaches <c>Dead</c> to raise an
    /// <c>EmailDeliveryFailed</c> alert. Until W4-02a it was a branch inside
    /// <see cref="ReleaseExpiredLeasesAsync"/> that returned nothing but a count, so a customer whose
    /// final attempt was lost to a restart was never contacted, and no Super Admin was told either.
    /// </para>
    /// <para>
    /// It runs <b>before</b> <see cref="ReleaseExpiredLeasesAsync"/> in a sweep, which is what stops an
    /// exhausted row being requeued, reclaimed at once (its next attempt time is already past) and
    /// spending a seventh attempt on a six-rung ladder before the failure path notices.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<long>> KillStrandedExhaustedAsync(DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Puts messages whose sender's lease has expired back on the ladder, and answers how many.
    /// <para>
    /// A row in <c>Sending</c> is outside <c>IX_EmailOutbox_Due</c>'s filter, so without this a process
    /// killed between the claim and the send would strand its batch for ever — twenty bank-transfer
    /// instructions nobody is waiting to retry.
    /// </para>
    /// <para>
    /// <b>It requeues and never kills.</b> The exhausted rows are
    /// <see cref="KillStrandedExhaustedAsync"/>'s, because killing a row has to report which rows it
    /// killed and a requeue only has to report how many.
    /// </para>
    /// </summary>
    Task<int> ReleaseExpiredLeasesAsync(DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Doc 08 §3.8's "the worker claims batches with <c>UPDATE TOP (20) … OUTPUT</c>": takes up to
    /// <paramref name="batchSize"/> due messages, marks them <c>Sending</c> under a lease and returns
    /// them.
    /// </summary>
    Task<IReadOnlyList<EmailOutboxClaim>> ClaimAsync(
        int batchSize,
        DateTime nowUtc,
        DateTime leaseUntilUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// The mail server accepted it: <c>Sent</c>, stamped, lease cleared, transport override cleared
    /// (doc 08 §3.8 R5) — and the body destroyed if the template is one of the two whose link carries a
    /// single-use token (G-03).
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the row was no longer the one that was claimed — a lease that expired
    /// and was reclaimed while this send was still in flight. The send happened; the row belongs to
    /// somebody else, and the caller logs it, because it is the one shape in which a customer can receive
    /// the same message twice.
    /// </returns>
    Task<bool> MarkSentAsync(EmailOutboxClaim claim, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// The attempt failed: either back to <c>Failed</c> with <c>NextAttemptAtUtc</c> on the next rung of
    /// <c>EmailBackoff</c>, or to <c>Dead</c> when the ladder is exhausted.
    /// </summary>
    Task<EmailFailureOutcome> MarkFailedAsync(
        EmailOutboxClaim claim,
        string failure,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}

/// <summary>What recording a failed attempt did to the row.</summary>
public enum EmailFailureOutcome
{
    /// <summary>Back on the ladder: <c>Failed</c>, with the next attempt due after the next rung's wait.</summary>
    Retrying = 1,

    /// <summary>
    /// Out of attempts: <c>Dead</c>, and nothing will claim it again. Doc 11 §4 (G-37) owes this an
    /// <c>EmailDeliveryFailed</c> dashboard alert, which needs <c>ops.AdminAlerts</c>.
    /// </summary>
    Dead = 2,

    /// <summary>
    /// The row was no longer the one that was claimed, so nothing was written. Same cause as a
    /// <see langword="false"/> from <see cref="IEmailOutboxStore.MarkSentAsync"/>: an expired lease that
    /// another sender picked up.
    /// </summary>
    NotOurs = 3,
}
