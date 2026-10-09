namespace SpencerDenim.Domain.Ops;

/// <summary>
/// <c>ops.EmailOutbox.Status</c> (doc 08 §3.8). A <c>tinyint</c> with a CK pinning the range, and the
/// numbers are the document's own — they are written into <c>IX_EmailOutbox_Due</c>'s filter
/// (<c>WHERE Status IN (0,3)</c>) and into the worker's claim, so renumbering them would silently change
/// which rows a sweep can see.
/// </summary>
public enum EmailOutboxStatus : byte
{
    /// <summary>Queued by a business transaction and never attempted. The only status an insert writes.</summary>
    Pending = 0,

    /// <summary>
    /// A sender holds the lease (<c>LockedUntilUtc</c>) and the message is in flight.
    /// <para>
    /// <b>Outside <c>IX_EmailOutbox_Due</c>'s filter on purpose</b>, which is what stops a second sender
    /// claiming a message that is already being handed to the mail server. A process that dies mid-send
    /// leaves the row here with an expired lease; <c>SendEmails</c> puts those back to
    /// <see cref="Failed"/> at the start of every sweep so the backoff ladder carries on rather than the
    /// message being stranded.
    /// </para>
    /// </summary>
    Sending = 1,

    /// <summary>Accepted by the mail server. Terminal.</summary>
    Sent = 2,

    /// <summary>
    /// An attempt failed and another is due at <c>NextAttemptAtUtc</c>. Inside the due index's filter, so
    /// the next sweep picks it up.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// Every attempt failed. Terminal: no sweep will try again, and v1 has no screen that can
    /// (doc 11 §7 G-42). Doc 11 §4 owes this status an <c>EmailDeliveryFailed</c> dashboard alert
    /// (G-37), which needs <c>ops.AdminAlerts</c> — a table that does not exist — so for now reaching it
    /// is logged at <c>Error</c> and nothing else.
    /// </summary>
    Dead = 4,

    /// <summary>
    /// Withdrawn before it was sent. Terminal. <b>Nothing in this build writes it:</b> doc 08 §3.8 R5
    /// makes cancelling a security-alert email impossible through any screen or API, and no other use
    /// case has a reason to withdraw a queued message. It exists because the status set is the
    /// document's and a CK that allowed 0–4 would make the schema disagree with it.
    /// </summary>
    Cancelled = 5,
}
