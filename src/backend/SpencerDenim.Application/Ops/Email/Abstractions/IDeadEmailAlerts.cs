namespace SpencerDenim.Application.Ops.Email.Abstractions;

/// <summary>
/// Raises the <c>EmailDeliveryFailed</c> dashboard alert for an outbox row that has reached
/// <c>Dead</c> (doc 11 §4, §7 G-37). Implemented in Infrastructure.
/// <para>
/// <b>The id is the whole argument, deliberately.</b> Both ways a row reaches <c>Dead</c> — the last
/// failed attempt, and a sender that died on its final attempt and had its lease recovered — can name the
/// row and nothing else, so the port takes what both have. The implementation reads the row's template
/// code and order back from the database and <b>refuses to raise an alert for a row that is not actually
/// Dead</b>, which makes the port impossible to misuse into a banner about a message that is still being
/// retried.
/// </para>
/// <para>
/// <b>It is not in the same transaction as the statement that killed the row</b>, and that is a trade
/// rather than an oversight. The two statements run on one connection microseconds apart; the only
/// failure that can separate them is the database becoming unreachable between them, and in that case
/// <c>SendEmails</c> still writes the <c>Error</c> line it wrote before this alert existed — so the signal
/// degrades to the behaviour of the previous build instead of disappearing. The alternative is an explicit
/// transaction wrapped around the one set-based statement W3-06's tester proved over 900 contention runs,
/// which is a larger risk than the window it closes.
/// </para>
/// </summary>
public interface IDeadEmailAlerts
{
    /// <summary>
    /// Raises the alert, and answers whether a row was written.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when the outbox row is not <c>Dead</c> (so there is nothing to alert
    /// about), when it no longer exists, or when this message already has an open or acknowledged alert —
    /// which makes the call idempotent when it is repeated, so a retried sweep cannot raise the same
    /// banner twice.
    /// </returns>
    Task<bool> RaiseAsync(long outboxMessageId, CancellationToken cancellationToken);
}
