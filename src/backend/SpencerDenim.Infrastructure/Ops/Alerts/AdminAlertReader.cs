using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Ops.Alerts.Abstractions;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Ops.Alerts;

/// <summary>
/// The banner's read over <c>ops.AdminAlerts</c> (doc 08 §3.8 N2).
/// <para>
/// <b>Two statements and both are shaped for <c>IX_AdminAlerts_Open</c></b>
/// (<c>(CreatedAtUtc DESC) WHERE AcknowledgedAtUtc IS NULL</c>, doc 08 §3.8). The filter is matched
/// because the predicate spells it: <c>AcknowledgedAtUtc IS NULL</c> is a literal with nothing to
/// parameterise, so it does not have the problem the outbox's <c>Status IN (0,3)</c> filter has (DB28,
/// where a parameterised status cost 20,101 logical reads against 18). The <c>ORDER BY CreatedAtUtc
/// DESC</c> is the index's own key order, so the top ten come off the front of it with no sort.
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
    public async Task<(int TotalOpen, IReadOnlyList<AdminAlertRow> Alerts)> GetOpenAsync(
        IReadOnlyList<string> alertTypes,
        int take,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alertTypes);

        if (alertTypes.Count == 0 || take <= 0)
        {
            // An empty visible set is "nothing", never "no filter" (see IAdminAlertReader). Answered
            // without a statement, so there is no shape of this query that could return every alert.
            return (0, []);
        }

        var open = OpenQuery(alertTypes);

        var total = await open.CountAsync(cancellationToken);

        if (total == 0)
        {
            return (0, []);
        }

        var alerts = await open
            .OrderByDescending(alert => alert.CreatedAtUtc)
            .ThenByDescending(alert => alert.Id)
            .Take(take)
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

        return (total, alerts);
    }

    /// <summary>
    /// The open alerts of these types.
    /// </summary>
    /// <remarks>
    /// <b>Tie-broken by id as well as by time.</b> <c>CreatedAtUtc</c> is <c>datetime2(3)</c> and one
    /// sweep can kill several messages in the same millisecond, so a sort on the timestamp alone returns
    /// them in whatever order the engine finds them — which makes "newest first" something no test can
    /// assert. It is free: the index carries the clustered key as its locator. The lesson is
    /// <c>OrderExpiryStore.FindDueOrdersAsync</c>'s and <c>EmailOutboxStore.ClaimAsync</c>'s before it.
    /// </remarks>
    private IQueryable<Domain.Ops.AdminAlert> OpenQuery(IReadOnlyList<string> alertTypes) =>
        dbContext.AdminAlerts
            .AsNoTracking()
            .Where(alert =>
                alert.AcknowledgedAtUtc == null
                && EF.Constant(alertTypes).Contains(alert.AlertType));
}
