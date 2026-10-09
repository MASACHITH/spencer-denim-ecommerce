using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Application.Ops.Email.Abstractions;

/// <summary>
/// Port for putting a row into <c>ops.EmailOutbox</c> (doc 08 §3.8). Implemented in Infrastructure.
/// </summary>
/// <remarks>
/// <b>There is one method and it does not save, which is the entire point of a transactional
/// outbox.</b> Doc 08 §3.8: "Rows are inserted in the business transaction". So this is
/// <c>IAuditLogWriter.Enlist</c>'s shape and for the same reason (issue #59): the caller's own
/// <c>SaveChangesAsync</c> commits the message together with the thing the message is about. An email
/// about an order that rolled back and an order that committed with no email queued are both defects, and
/// a port with a <c>SendAsync</c> on it would make the first one available to anybody who called it.
/// <para>
/// <b>There is deliberately no <c>WriteAsync</c></b>, unlike <c>IAuditLogWriter</c>, which has one for the
/// use cases whose only outcome is the audit row. No email in this build is the only outcome of anything:
/// every one of the six is about an order that is being written in the same breath.
/// </para>
/// </remarks>
public interface IEmailOutboxWriter
{
    /// <summary>
    /// Adds the message to the caller's unit of work <b>without committing it</b>.
    /// </summary>
    void Enlist(EmailOutboxMessage message);
}
