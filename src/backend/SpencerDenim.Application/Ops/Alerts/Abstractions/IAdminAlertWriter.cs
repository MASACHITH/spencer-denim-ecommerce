using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Application.Ops.Alerts.Abstractions;

/// <summary>
/// Port for writing <c>ops.AdminAlerts</c> rows (doc 08 §3.8 N2). Implemented in Infrastructure.
/// <para>
/// <b>Two methods, and the shape is <c>IAuditLogWriter</c>'s for issue #59's reason.</b> An alert belongs
/// to the change that caused it: doc 08 §3.8 DB2 says a sensitive settings change "inserts an
/// <c>ops.AdminAlerts</c> row" in the same breath as the audit row and the email, and a settings write
/// that committed but whose alert did not would leave a Super Admin uninformed about a change that really
/// happened. So a use case with its own unit of work <see cref="Enlist"/>s and its own
/// <c>SaveChangesAsync</c> commits both together.
/// </para>
/// <para>
/// <see cref="RaiseAsync"/> is for the callers that have no transaction to join — a background job, the
/// break-glass CLI. The one caller in this build is the <c>SendEmails</c> sweep, whose alert follows a
/// set-based <c>UPDATE</c> that is already committed; see <c>IDeadEmailAlerts</c> for what that costs and
/// why it is the right trade there.
/// </para>
/// </summary>
public interface IAdminAlertWriter
{
    /// <summary>
    /// Writes one row on its own. For a caller whose only outcome <i>is</i> the alert.
    /// </summary>
    Task RaiseAsync(AdminAlert alert, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a row to the caller's own unit of work <b>without committing it</b>, so the alert and the
    /// change it is about are one transaction.
    /// </summary>
    /// <remarks>
    /// <b>No producer in this build uses it</b>, and it is here rather than left to the settings task
    /// because the rule it carries is doc 08 §3.8 DB2's and not that task's invention — the same argument
    /// W3-06 made for honouring R5's <c>TransportOverrideProtected</c> rule ahead of its first writer.
    /// It is exercised by its own test, not left as an untested promise.
    /// </remarks>
    void Enlist(AdminAlert alert);
}
