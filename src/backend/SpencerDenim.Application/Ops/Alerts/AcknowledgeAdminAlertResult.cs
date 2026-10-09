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

    /// <summary>The note is longer than the column (doc 08 §3.8: <c>nvarchar(500)</c>).</summary>
    public sealed record NoteTooLong(int MaxLength) : AcknowledgeAdminAlertResult;

    /// <summary>
    /// The request carries no identified staff user, so there is nobody to record as having seen it.
    /// </summary>
    public sealed record NoActor : AcknowledgeAdminAlertResult;
}
