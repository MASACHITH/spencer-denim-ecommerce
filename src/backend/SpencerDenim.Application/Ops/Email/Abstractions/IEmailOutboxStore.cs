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
/// The four statements <c>SendEmails</c> runs against <c>ops.EmailOutbox</c> (doc 08 §3.8).
/// </summary>
/// <remarks>
/// <b>Every one of them is set-based and conditional, which is doc 08 §5.2's argument applied to a
/// queue.</b> Two senders racing for one message do not both read "Pending": the second waits on the row's
/// update lock, re-evaluates the claim's own <c>WHERE</c> against the row as the first left it, and
/// matches nothing. So a message is sent once however many hosts are running, and no lease table and no
/// leader election is needed — which is the same conclusion <c>ExpireUnpaidOrders</c> reached about
/// <c>ops.JobLeases</c>, a table doc 08 §3.8 specifies and no migration creates.
/// </remarks>
public interface IEmailOutboxStore
{
    /// <summary>
    /// How many messages are due now. Read only when there is no transport to send them with, so an
    /// operator can be told "<i>n</i> are waiting" rather than "something is wrong".
    /// </summary>
    Task<int> CountDueAsync(DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Puts messages whose sender's lease has expired back on the ladder, and answers how many.
    /// <para>
    /// A row in <c>Sending</c> is outside <c>IX_EmailOutbox_Due</c>'s filter, so without this a process
    /// killed between the claim and the send would strand its batch for ever — twenty bank-transfer
    /// instructions nobody is waiting to retry.
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
    Task MarkSentAsync(long id, string templateCode, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// The attempt failed: either back to <c>Failed</c> with <c>NextAttemptAtUtc</c> on the next rung of
    /// <c>EmailBackoff</c>, or to <c>Dead</c> when the ladder is exhausted.
    /// </summary>
    /// <returns><see langword="true"/> when the message is now <c>Dead</c>.</returns>
    Task<bool> MarkFailedAsync(
        EmailOutboxClaim claim,
        string failure,
        DateTime nowUtc,
        CancellationToken cancellationToken);
}
