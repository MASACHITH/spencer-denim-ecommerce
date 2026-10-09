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
    /// The open alerts of these types, newest first, at most <paramref name="take"/> of them, and whether
    /// there are more.
    /// </summary>
    /// <remarks>
    /// <b>"Are there more" rather than "how many", and the reason is measured.</b> A separate
    /// <c>COUNT(*)</c> over the same predicate cannot be made to use <c>IX_AdminAlerts_Open</c>: SQL
    /// Server costs it as a clustered scan even when the filter implies the whole predicate, and an index
    /// hint is the only way to change its mind. Measured on 20,000 alerts (the shape doc 08 §8.4
    /// produces — acknowledged kept a year, a handful open): the list <b>38 logical reads</b>, the count
    /// <b>5,020</b>, on every admin page load, for every staff member R10 gives an audience to. So the
    /// reader asks for one row more than it needs and the extra row is the answer. What a staff member
    /// loses is a precise number they were not going to act on; doc 11 §6.1 step 6 tells them to open
    /// every alert, and "there are more" is what says they have not finished.
    /// <para>
    /// <c>AdminAlertsPlanTests</c> holds both halves of that measurement, including the count that does
    /// not seek — because the obvious, readable change is to add one back.
    /// </para>
    /// </remarks>
    /// <param name="alertTypes">
    /// The types the caller may see. An empty list means "nothing", not "no filter" — and the reader
    /// answers it without running a statement at all, which is the property R10 needs rather than one
    /// EF's rendering of an empty collection happens to give.
    /// </param>
    /// <param name="take">How many rows to return at most.</param>
    Task<(bool MoreThanTake, IReadOnlyList<AdminAlertRow> Alerts)> GetOpenAsync(
        IReadOnlyList<string> alertTypes,
        int take,
        CancellationToken cancellationToken);
}
