using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Domain.Ops;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Ops.Email;

/// <summary>
/// Adds a message to <c>ops.EmailOutbox</c> through the scoped <see cref="AppDbContext"/> every use case
/// in a request writes through, so the caller's own <c>SaveChangesAsync</c> commits it in the same
/// transaction as the change it is about (doc 08 §3.8's transactional outbox).
/// </summary>
/// <remarks>
/// <b>Exactly <c>AuditLogWriter.Enlist</c>, and the symmetry is the point.</b> Both put a row into the
/// caller's unit of work and neither saves; both exist because committing the record of an event
/// separately from the event is a state nobody can reason about afterwards (issue #59).
/// </remarks>
internal sealed class EmailOutboxWriter(AppDbContext dbContext) : IEmailOutboxWriter
{
    public void Enlist(EmailOutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        dbContext.EmailOutbox.Add(message);
    }
}
