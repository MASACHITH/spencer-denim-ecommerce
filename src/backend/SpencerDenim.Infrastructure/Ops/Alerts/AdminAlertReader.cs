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
/// <b><see cref="EF.Constant{T}"/> around the alert types, and <i>not</i> because the index needs it.</b>
/// Measured, because the first version of this remark guessed and guessed wrong: EF Core 10 renders
/// <c>types.Contains(…)</c> as one parameter per element — <c>@types1 … @types7</c>, not the
/// <c>OPENJSON</c> join this comment used to claim — and <c>IX_AdminAlerts_Open</c> is matched either way,
/// because the predicate that has to imply its filter is <c>AcknowledgedAtUtc IS NULL</c> and the alert
/// type is only a residual. A mutation proved it: swapping <c>EF.Constant</c> out left every plan in this
/// file identical.
/// </para>
/// <para>
/// It stays for a smaller and honest reason. R10 can produce exactly two lists, so as literals they give
/// two stable cached plans; as seven sniffed parameters they give one plan compiled for whichever
/// audience asked first. <c>AdminAlertsPlanTests</c> pins the literals as the deliberate choice they are,
/// and pins the seek separately — the two are not the same claim.
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
        var fetched = await OpenPage(alertTypes, take).ToListAsync(cancellationToken);

        return fetched.Count > take
            ? (true, fetched[..take])
            : (false, fetched);
    }

    /// <summary>
    /// The banner's one query: the open alerts of these types, newest first, with one row more than
    /// <paramref name="take"/> so the caller can tell whether there are more.
    /// </summary>
    /// <remarks>
    /// <b><c>internal</c> rather than <c>private</c> so <c>AdminAlertsPlanTests</c> can read the SQL this
    /// reader really sends</b> — the shape <c>SendEmailsService.SweepAsync</c> and
    /// <c>EmailOutboxStore.KillStrandedExhaustedSql</c> already use. It is not a test-only member: it is
    /// the whole read, and <see cref="GetOpenAsync"/> is the bounds-checking and the paging around it.
    /// <para>
    /// <b>The first version of those plan tests rebuilt this query instead of calling it, and a mutation
    /// proved what that was worth:</b> replacing <see cref="EF.Constant{T}"/> with a plain collection —
    /// which turns the seven literals into seven parameters, not the <c>OPENJSON</c> join this remark
    /// claimed until it was measured (see the class summary above) — left every plan test green, because
    /// they were measuring a query the test had written.
    /// </para>
    /// </remarks>
    internal IQueryable<AdminAlertRow> OpenPage(IReadOnlyList<string> alertTypes, int take) =>
        OpenQuery(alertTypes)
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
                alert.DetailsJson));

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
    /// <b>The tie-break ascends, which looks wrong beside a descending sort and is deliberate.</b>
    /// <c>IX_AdminAlerts_Open</c> is keyed <c>CreatedAtUtc DESC</c> and carries the clustered key as its
    /// row locator, so its physical order is <c>(CreatedAtUtc DESC, Id ASC)</c> — exactly this ordering,
    /// which therefore needs no sort. Among alerts raised in the same millisecond there is no "newer" to
    /// prefer anyway; what the banner needs is an order two reads agree on.
    /// <para>
    /// <b>It is not a measured saving, and saying so is the point.</b> At the size
    /// <c>AdminAlertsPlanTests</c> probes — twenty thousand rows, twelve of them open — the optimiser
    /// seeks this index either way: a mutation that reversed the tie-break left every plan test in that
    /// file green and was caught only by the ordering assertion. A sort of ten rows is free. This is the
    /// shape that cannot become expensive as the table grows, not a cost that has been measured away.
    /// </para>
    /// </para>
    /// </remarks>
    private IQueryable<Domain.Ops.AdminAlert> OpenQuery(IReadOnlyList<string> alertTypes) =>
        dbContext.AdminAlerts
            .AsNoTracking()
            .Where(alert =>
                alert.AcknowledgedAtUtc == null
                && EF.Constant(alertTypes).Contains(alert.AlertType));
}
