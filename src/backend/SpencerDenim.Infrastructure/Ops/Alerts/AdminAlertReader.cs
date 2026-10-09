using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Ops.Alerts;

/// <summary>
/// The banner's read over <c>ops.AdminAlerts</c> (doc 08 §3.8 N2).
/// <para>
/// <b>One statement, shaped for <c>IX_AdminAlerts_Open</c></b>
/// (<c>(CreatedAtUtc DESC) WHERE AcknowledgedAtUtc IS NULL</c>, doc 08 §3.8). The filter is matched
/// because the predicate spells it: <c>AcknowledgedAtUtc IS NULL</c> is a literal with nothing to
/// parameterise, so it does not have the problem the outbox's <c>Status IN (0,3)</c> filter has (DB28,
/// where a parameterised status cost 20,101 logical reads against 18). The <c>ORDER BY CreatedAtUtc
/// DESC, Id</c> is the index's own physical order, so the top rows come off the front of it with no sort:
/// <b>38 logical reads on 20,000 alerts</b>.
/// </para>
/// <para>
/// <b>There is deliberately no second statement counting them.</b> A <c>COUNT(*)</c> over the same
/// predicate cost <b>5,020 logical reads</b> in the same measurement — SQL Server costs it as a clustered
/// scan however the predicate is written, because the filtered index has no key predicate to seek on, and
/// only an index hint changes its mind. One row more than the caller asked for answers "are there more"
/// instead, which is what the banner needs; see <see cref="IAdminAlertReader"/>.
/// </para>
/// <para>
/// <b><see cref="EF.Constant{T}"/> around the alert types, on purpose.</b> EF renders a parameterised
/// collection through <c>OPENJSON</c>, which turns this seek into a join against a table-valued function
/// and makes the plan depend on a cardinality estimate for a list of two or seven constants. There are
/// exactly two lists R10 can produce, so inlining them costs two cached plans and nothing else.
/// <c>AdminAlertsPlanTests</c> asserts both the absence of parameters and the index, because neither is
/// visible from here.
/// </para>
/// </summary>
internal sealed class AdminAlertReader(AppDbContext dbContext) : IAdminAlertReader
{
    public async Task<(bool MoreThanTake, IReadOnlyList<AdminAlertRow> Alerts)> GetOpenAsync(
        IReadOnlyList<string> alertTypes,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alertTypes);

        if (alertTypes.Count == 0 || take <= 0)
        {
            // An empty visible set is "nothing", never "no filter" (see IAdminAlertReader). Answered
            // without a statement, so there is no shape of this query that could return every alert.
            return (false, []);
        }

        // **One row more than the caller asked for, and the extra row is the whole answer to "are there
        // more".** It costs nothing: it comes off the same seek, from the same page.
        var fetched = await OpenQuery(alertTypes)
            .OrderByDescending(alert => alert.CreatedAtUtc)
            .ThenBy(alert => alert.Id)
            .Take(take + 1)
            .Select(alert => new AdminAlertRow(
                alert.Id,
                alert.AlertType,
                (byte)alert.Severity,
                alert.Title,
                alert.CreatedAtUtc,
                alert.RelatedEntityType,
                alert.RelatedEntityId,
                alert.DetailsJson))
            .ToListAsync(cancellationToken);

        return fetched.Count > take
            ? (true, fetched[..take])
            : (false, fetched);
    }

    /// <summary>
    /// The open alerts of these types.
    /// </summary>
    /// <remarks>
    /// <b>Tie-broken by id as well as by time.</b> <c>CreatedAtUtc</c> is <c>datetime2(3)</c> and one
    /// sweep can kill twenty messages inside one millisecond, so a sort on the timestamp alone returns
    /// them in whatever order the engine finds them — which makes "newest first" something no test can
    /// assert. The lesson is <c>OrderExpiryStore.FindDueOrdersAsync</c>'s and
    /// <c>EmailOutboxStore.ClaimAsync</c>'s before it.
    /// <para>
    /// <b>The tie-break ascends, which looks wrong beside a descending sort and is the point.</b>
    /// <c>IX_AdminAlerts_Open</c> is keyed <c>CreatedAtUtc DESC</c> and carries the clustered key as its
    /// row locator, so its physical order is <c>(CreatedAtUtc DESC, Id ASC)</c> — exactly this. Asking
    /// for <c>Id DESC</c> instead would be a different order from the index's and would make the
    /// optimiser sort, which on this shape of table it answers by scanning the clustered index instead
    /// (measured: <c>AdminAlertsPlanTests</c> went red that way before the tie-break was turned round).
    /// Among alerts raised in the same millisecond there is no "newer" to prefer; what the banner needs
    /// is an order two reads agree on.
    /// </para>
    /// </remarks>
    private IQueryable<Domain.Ops.AdminAlert> OpenQuery(IReadOnlyList<string> alertTypes) =>
        dbContext.AdminAlerts
            .AsNoTracking()
            .Where(alert =>
                alert.AcknowledgedAtUtc == null
                && EF.Constant(alertTypes).Contains(alert.AlertType));
}
