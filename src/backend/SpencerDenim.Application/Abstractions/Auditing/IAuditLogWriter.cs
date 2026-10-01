using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.Application.Abstractions.Auditing;

/// <summary>Port for writing <c>ops.AuditLogs</c> rows (doc 08 §3.8). Implemented in Infrastructure.</summary>
public interface IAuditLogWriter
{
    /// <summary>
    /// Writes one row on its own. For a use case whose only outcome <i>is</i> the audit row — a refused
    /// sign-in, a spent recovery code — this is the whole write.
    /// </summary>
    Task WriteAsync(AuditLog entry, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a row to the caller's own unit of work <b>without committing it</b>, so the trail and the
    /// change it describes are one transaction.
    /// <para>
    /// <b>Why this exists (issue #59).</b> <see cref="WriteAsync"/> commits by itself, which is right for
    /// a use case that has nothing else to save and wrong for one that does: a catalogue edit that
    /// committed its audit row first and then failed its own save would leave a trail of a change that
    /// never happened, and committing the change first and then failing the audit row leaves a change
    /// with no trail. Neither is a state anybody can reason about afterwards. The caller enlists the row
    /// and its own save commits both, so the two are true together or not at all.
    /// </para>
    /// <para>
    /// The row is therefore written <i>only</i> when the change succeeds. A refused write leaves no audit
    /// row, which is the right answer for a validation failure and a deliberate gap for a security event
    /// — those are written with <see cref="WriteAsync"/>, as the sign-in handlers do.
    /// </para>
    /// </summary>
    void Enlist(AuditLog entry);
}
