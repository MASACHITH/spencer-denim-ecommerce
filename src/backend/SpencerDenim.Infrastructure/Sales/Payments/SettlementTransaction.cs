using System.Data;
using Microsoft.EntityFrameworkCore;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// One <c>ReadCommitted</c> transaction inside EF's execution strategy, rolled back unless the body's
/// result says it should be kept — the shape every payment decision shares (a staff Approve, Reject or
/// Accept-late, and a provider's confirmed payment).
/// </summary>
/// <remarks>
/// <b>Rolling back on every answer that is not a success is the point.</b> A refusal discovered half way
/// through — the payment changed under us, a line is short — has usually already issued statements, and
/// the only safe thing is for none of them to have happened. Writing that once here rather than at
/// every <c>return</c> is what stops the next person adding a branch that forgets.
/// <para>
/// <see cref="DbContext.ChangeTracker"/> is cleared on every exit, successful or not, for the two
/// reasons <c>OrderPlacementStore</c> records: a retry by the execution strategy must not re-send the
/// rolled-back inserts, and the request's later reads must come from the database rather than from a
/// tracker holding what we meant to write.
/// </para>
/// <para>
/// (Moved out of <c>BankTransferReviewStore.InTransactionAsync</c> with its body unchanged, and generalised
/// over the result type: the bank flow commits on <c>Applied</c>, the provider-facing settlement on
/// <c>Settled</c> or <c>CapturedRequiresRefund</c>.)
/// </para>
/// </remarks>
internal static class SettlementTransaction
{
    public static async Task<TResult> RunAsync<TResult>(
        AppDbContext dbContext,
        Func<Task<TResult>> body,
        Func<TResult, bool> commitWhen,
        CancellationToken cancellationToken)
    {
        var strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

            try
            {
                var result = await body();

                if (commitWhen(result))
                {
                    await transaction.CommitAsync(cancellationToken);
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                }

                return result;
            }
            finally
            {
                dbContext.ChangeTracker.Clear();
            }
        });
    }
}
