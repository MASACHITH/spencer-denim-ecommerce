namespace SpencerDenim.Application.Ops.Alerts.Abstractions;

/// <summary>
/// One open alert as it comes off <c>ops.AdminAlerts</c>, before the order link is lifted out of its
/// details.
/// </summary>
/// <param name="DetailsJson">
/// Doc 08 §3.8's "Keys, entity IDs and amounts". Read by
/// <c>GetOpenAdminAlertsQueryHandler</c> and <b>never returned to a client as-is</b>: it is written by
/// whatever raised the alert, and a screen cannot render seven shapes it does not know.
/// </param>
public sealed record AdminAlertRow(
    int Id,
    string AlertType,
    byte Severity,
    string Title,
    DateTime CreatedAtUtc,
    string? RelatedEntityType,
    string? RelatedEntityId,
    string? DetailsJson);

/// <summary>
/// Reads <c>ops.AdminAlerts</c> for the banner (doc 08 §3.8 N2). Implemented in Infrastructure.
/// </summary>
/// <remarks>
/// <b>The alert types are the caller's, not the reader's.</b> Doc 08 §3.8 R10 is a rule about a user's
/// permissions, which the Domain owns (<c>AdminAlertVisibility</c>); this port takes the resulting list
/// so that the rule lives in one testable place and the SQL only ever filters by what it is given. An
/// empty list is a legitimate answer — most staff have no path to any alert — and the reader must then
/// return nothing rather than everything.
/// </remarks>
public interface IAdminAlertReader
{
    /// <summary>
    /// The open alerts of these types, newest first, and the total number of them.
    /// </summary>
    /// <param name="alertTypes">
    /// The types the caller may see. An empty list means "nothing", not "no filter".
    /// </param>
    /// <param name="take">How many rows to return; the count covers all of them.</param>
    Task<(int TotalOpen, IReadOnlyList<AdminAlertRow> Alerts)> GetOpenAsync(
        IReadOnlyList<string> alertTypes,
        int take,
        CancellationToken cancellationToken);
}
