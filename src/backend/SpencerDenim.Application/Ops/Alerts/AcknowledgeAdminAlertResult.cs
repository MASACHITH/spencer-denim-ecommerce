namespace SpencerDenim.Application.Ops.Alerts;

/// <summary>The outcome of acknowledging an alert, as the endpoint maps it.</summary>
public abstract record AcknowledgeAdminAlertResult
{
    private AcknowledgeAdminAlertResult()
    {
    }

    /// <summary>
    /// Recorded, with the audit row beside it.
    /// </summary>
    /// <param name="AcknowledgedAtUtc">
    /// When. Returned so the screen can confirm it in a place that survives the banner disappearing —
    /// acknowledging an alert removes the node the button was in, and a message written into that node
    /// would never be read — a defect this project shipped twice last week.
    /// </param>
    public sealed record Acknowledged(int AlertId, string AlertType, DateTime AcknowledgedAtUtc)
        : AcknowledgeAdminAlertResult;

    /// <summary>
    /// Somebody else got there first. The caller's intention is satisfied; the screen says when it was
    /// cleared rather than showing a failure.
    /// </summary>
    public sealed record AlreadyAcknowledged(int AlertId, string AlertType, DateTime AcknowledgedAtUtc)
        : AcknowledgeAdminAlertResult;

    /// <summary>
    /// No such alert, or none of a type this user may see (doc 08 §3.8 R10). <b>One answer for both</b>,
    /// so the route cannot be used to discover that an alert exists.
    /// </summary>
    public sealed record NotFound : AcknowledgeAdminAlertResult;

    /// <summary>
    /// The request carried an acknowledgement note, and this build stores none.
    /// </summary>
    /// <remarks>
    /// <b>Refused rather than accepted and dropped.</b> A field a route takes and silently does not store
    /// is the shape of promise this project has already been burned by: the caller believes their sentence
    /// was kept. Doc 08 §3.8 defines the column and the migration creates it; the owner's decision
    /// (2026-10-09) is that nothing writes it until the PDPA routine can scrub it, so the honest answer to
    /// a note is to say so.
    /// </remarks>
    public sealed record NoteNotAccepted : AcknowledgeAdminAlertResult;

    /// <summary>
    /// The request carries no identified staff user, so there is nobody to record as having seen it.
    /// </summary>
    public sealed record NoActor : AcknowledgeAdminAlertResult;
}
