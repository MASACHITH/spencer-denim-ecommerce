using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayHere;

/// <summary>
/// <see cref="IPayHereEventRetryStore"/> over <c>sales.PaymentEvents</c>, using <c>IX_PaymentEvents_Retry</c>
/// (doc 08 §3.5).
/// <para>
/// <b>Find, then claim, and the claim is the lock.</b> The read is a plain query for what is due; taking an
/// event is a conditional <c>UPDATE</c> that moves its retry time into the future, and only the caller whose
/// statement affects a row owns it. Two sweeps — two instances of the API, or a slow pass overlapping the
/// next — therefore never apply one event at the same moment, and a caller that dies simply lets the lease
/// run out. (Applying is idempotent anyway; the claim is about not doing the same work twice at once.)
/// </para>
/// <para>
/// <b>Both statements name the two statuses as literals</b>, the rule DB28 sets for a filtered index: the
/// predicate has to be provably inside <c>IX_PaymentEvents_Retry</c>'s own filter for the optimiser to use it.
/// </para>
/// </summary>
internal sealed class PayHereEventRetryStore(AppDbContext dbContext) : IPayHereEventRetryStore
{
    public async Task<IReadOnlyList<PayHereDueEvent>> FindDueAsync(
        DateTime nowUtc,
        int take,
        CancellationToken cancellationToken) =>
        await dbContext.PaymentEvents
            .AsNoTracking()
            .Where(candidate =>
                candidate.ProviderCode == PaymentProviderCodes.PayHere
                && (candidate.ProcessingStatus == PaymentEventProcessingStatus.Received
                    || candidate.ProcessingStatus == PaymentEventProcessingStatus.Failed)
                && candidate.NextAttemptAtUtc <= nowUtc)
            .OrderBy(candidate => candidate.NextAttemptAtUtc)
            .ThenBy(candidate => candidate.Id)
            .Take(take)
            .Select(candidate => new PayHereDueEvent(candidate.Id, candidate.PayloadJson, candidate.AttemptCount))
            .ToListAsync(cancellationToken);

    public async Task<bool> ClaimAsync(
        long eventId,
        DateTime nowUtc,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        var until = nowUtc + lease;

        var claimed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.PaymentEvents
               SET NextAttemptAtUtc = {until}
             WHERE Id = {eventId}
               AND ProviderCode = {PaymentProviderCodes.PayHere}
               AND ProcessingStatus IN ({(byte)PaymentEventProcessingStatus.Received}, {(byte)PaymentEventProcessingStatus.Failed})
               AND NextAttemptAtUtc <= {nowUtc};
            """,
            cancellationToken);

        return claimed == 1;
    }
}
