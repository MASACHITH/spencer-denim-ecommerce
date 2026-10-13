using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments;

/// <summary>
/// <see cref="IPaymentAttemptReader"/> over <c>sales.Payments</c>.
/// <para>
/// One indexed read: <c>IX_Payments_OrderId</c> serves the join from the order's token, and the order has a
/// handful of payment rows at most. Read-only and untracked, like every reader behind the order page.
/// </para>
/// </summary>
internal sealed class PaymentAttemptReader(AppDbContext dbContext) : IPaymentAttemptReader
{
    public async Task<PaymentAttempt?> FindInFlightAsync(
        Guid orderPublicId,
        CancellationToken cancellationToken) =>
        await dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Order!.PublicId == orderPublicId
                && (payment.Status == PaymentStatus.Initiated
                    || payment.Status == PaymentStatus.Pending
                    || payment.Status == PaymentStatus.Capturing))
            .OrderByDescending(payment => payment.Id)

            // A class, so that "no such row" is representable (see OrderSnapshot for what a struct costs).
            .Select(payment => new PaymentAttempt(payment.Id, payment.Status, payment.ProviderReference))
            .FirstOrDefaultAsync(cancellationToken);
}
