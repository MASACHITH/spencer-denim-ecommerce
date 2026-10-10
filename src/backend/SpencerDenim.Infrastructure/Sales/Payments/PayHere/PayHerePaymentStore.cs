using Microsoft.EntityFrameworkCore;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Sales;
using SpencerDenim.Infrastructure.Persistence;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayHere;

/// <summary>
/// <see cref="IPayHerePaymentStore"/> over <c>sales.Payments</c> and <c>sales.Orders</c> (doc 11 P8b).
/// <para>
/// <b>The mechanism is the one every payment decision here uses</b> (doc 08 §5.2): a conditional
/// <c>UPDATE</c> whose row count is the answer, in one <c>ReadCommitted</c> transaction, order row before
/// payment row. A duplicate notification, an out-of-order one, and a notification racing the expiry job each
/// find the row no longer in the state the statement requires, affect nothing, and are told so —
/// <see cref="PayHereTransition.NotApplicable"/> — instead of being asked to guess.
/// </para>
/// <para>
/// <b>Raw statements and not entities</b>, like <c>OrderSettlementStore</c>: the predicate is the control,
/// and an entity round trip would read a row, decide, and write it back with a window in between.
/// </para>
/// </summary>
internal sealed class PayHerePaymentStore(AppDbContext dbContext) : IPayHerePaymentStore
{
    public async Task<PayHerePayment?> FindAsync(
        string orderNumber,
        int paymentId,
        CancellationToken cancellationToken) =>
        await dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Id == paymentId
                && payment.ProviderCode == PaymentProviderCodes.PayHere
                && payment.Order!.OrderNumber == orderNumber)
            .Select(payment => new PayHerePayment(
                payment.Id,
                payment.OrderId,
                payment.Order!.PublicId,
                payment.Order.OrderNumber,
                payment.Status,
                payment.Order.Status,
                payment.Amount,
                payment.ProviderReference,
                payment.ProviderTransactionId))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<PayHereCheckoutContext?> FindCheckoutContextAsync(
        int paymentId,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.Payments
            .AsNoTracking()
            .Where(payment =>
                payment.Id == paymentId
                && payment.ProviderCode == PaymentProviderCodes.PayHere
                && payment.Order!.Status == OrderStatus.PendingPayment)
            .Select(payment => new
            {
                payment.Id,
                payment.Status,
                payment.Amount,
                payment.Order!.OrderNumber,
                payment.Order.PublicId,
                payment.Order.FirstName,
                payment.Order.LastName,
                payment.Order.Email,
                OrderPhone = payment.Order.Phone,
                ShipPhone = payment.Order.ShipAddress.Phone,
                payment.Order.ShipAddress.Line1,
                payment.Order.ShipAddress.Line2,
                ShipCity = payment.Order.ShipAddress.City,
                payment.Order.Delivery.PickupLocationName,
                payment.Order.DeliveryDistrictName,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        // A delivery order has an address; a pickup order has a store. Either way the form gets something
        // true, and PayHereCheckoutForm supplies a fixed placeholder for what is still missing.
        var addressLine = string.Join(
            ", ",
            new[] { row.Line1, row.Line2 }.Where(part => !string.IsNullOrWhiteSpace(part)));

        if (addressLine.Length == 0 && !string.IsNullOrWhiteSpace(row.PickupLocationName))
        {
            addressLine = "Store pickup: " + row.PickupLocationName;
        }

        return new PayHereCheckoutContext(
            row.Id,
            row.Status,
            row.OrderNumber,
            row.PublicId,
            row.Amount,
            row.FirstName,
            row.LastName,
            row.Email,
            string.IsNullOrWhiteSpace(row.OrderPhone) ? row.ShipPhone : row.OrderPhone,
            addressLine.Length == 0 ? null : addressLine,
            string.IsNullOrWhiteSpace(row.ShipCity) ? row.DeliveryDistrictName : row.ShipCity);
    }

    public async Task RecordReferenceAsync(
        int paymentId,
        string payHerePaymentId,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        // Only a payment that has not settled or been routed: money that has already been accounted for
        // keeps the id it was accounted under, and a second charge must not overwrite it. Idempotent.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET ProviderReference = {payHerePaymentId},
                   UpdatedAtUtc = {nowUtc}
             WHERE Id = {paymentId}
               AND ProviderCode = {PaymentProviderCodes.PayHere}
               AND Status IN (
                     {(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Pending},
                     {(byte)PaymentStatus.Failed}, {(byte)PaymentStatus.Cancelled},
                     {(byte)PaymentStatus.Expired})
               AND (ProviderReference IS NULL OR ProviderReference <> {payHerePaymentId});
            """,
            cancellationToken);

    public async Task<PayHereTransition> MarkPendingAsync(
        int paymentId,
        int orderId,
        string payHerePaymentId,
        int pendingMaxDays,
        DateTime nowUtc,
        CancellationToken cancellationToken) =>
        await SettlementTransaction.RunAsync(
            dbContext,
            async () =>
            {
                // The order first (doc 08 §5.3): extend the deadline to placed + N days if that is later than
                // it is now. Only an order that is still waiting for money, and is PayHere's.
                var order = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Orders
                       SET PaymentDueAtUtc = CASE
                                WHEN DATEADD(DAY, {pendingMaxDays}, PlacedAtUtc) > PaymentDueAtUtc
                                THEN DATEADD(DAY, {pendingMaxDays}, PlacedAtUtc)
                                ELSE PaymentDueAtUtc END,
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {orderId}
                       AND Status = {(byte)OrderStatus.PendingPayment}
                       AND PaymentProviderCode = {PaymentProviderCodes.PayHere};
                    """,
                    cancellationToken);

                if (order != 1)
                {
                    return PayHereTransition.NotApplicable;
                }

                var payment = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE sales.Payments
                       SET Status = {(byte)PaymentStatus.Pending},
                           ProviderReference = {payHerePaymentId},
                           UpdatedAtUtc = {nowUtc}
                     WHERE Id = {paymentId}
                       AND OrderId = {orderId}
                       AND ProviderCode = {PaymentProviderCodes.PayHere}
                       AND Status = {(byte)PaymentStatus.Initiated};
                    """,
                    cancellationToken);

                // 0 rows rolls the order's deadline back with it: the extension belongs to a payment that
                // really moved, and this one did not.
                return payment == 1 ? PayHereTransition.Applied : PayHereTransition.NotApplicable;
            },
            transition => transition == PayHereTransition.Applied,
            cancellationToken);

    public async Task<PayHereFailureResult> MarkFailedAsync(
        PayHereFailure failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return await SettlementTransaction.RunAsync(
            dbContext,
            () => MarkFailedInTransactionAsync(failure, cancellationToken),
            result => result.Transition == PayHereTransition.Applied,
            cancellationToken);
    }

    private async Task<PayHereFailureResult> MarkFailedInTransactionAsync(
        PayHereFailure failure,
        CancellationToken cancellationToken)
    {
        var now = failure.NowUtc;

        // 1. The order row first. A payment that was Pending ends the extended hold: the deadline comes back
        //    to now + the normal window, so stock and the promo use are released at expiry instead of days
        //    from now (doc 08 §4.2 R2's rule, PayHere's figure). min(), never a lengthening. Written as one
        //    statement keyed on the payment's status, so it takes the order's lock before the payment's and
        //    never cuts the deadline of an order whose payment was not Pending.
        var cut = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE o
               SET PaymentDueAtUtc = CASE
                        WHEN DATEADD(MINUTE, {failure.ExpiryMinutes}, {now}) < o.PaymentDueAtUtc
                        THEN DATEADD(MINUTE, {failure.ExpiryMinutes}, {now})
                        ELSE o.PaymentDueAtUtc END,
                   UpdatedAtUtc = {now}
              FROM sales.Orders AS o
             WHERE o.Id = {failure.OrderId}
               AND o.Status = {(byte)OrderStatus.PendingPayment}
               AND o.PaymentProviderCode = {PaymentProviderCodes.PayHere}
               AND EXISTS (
                     SELECT 1
                       FROM sales.Payments AS p
                      WHERE p.Id = {failure.PaymentId}
                        AND p.OrderId = o.Id
                        AND p.Status = {(byte)PaymentStatus.Pending});
            """,
            cancellationToken);

        // 2. The payment: only an open one. Initiated or Pending become Failed; anything else (already
        //    failed, settled, expired) is a duplicate or a late notice and changes nothing.
        var failed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE sales.Payments
               SET Status = {(byte)PaymentStatus.Failed},
                   ProviderReference = {failure.PayHerePaymentId},
                   FailureCode = {failure.FailureCode},
                   FailureMessage = {failure.FailureMessage},
                   CompletedAtUtc = {now},
                   UpdatedAtUtc = {now}
             WHERE Id = {failure.PaymentId}
               AND OrderId = {failure.OrderId}
               AND ProviderCode = {PaymentProviderCodes.PayHere}
               AND Status IN ({(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Pending});
            """,
            cancellationToken);

        if (failed != 1)
        {
            return new PayHereFailureResult(PayHereTransition.NotApplicable, WasPending: false, ReplacementOpened: false);
        }

        // 3. A fresh attempt, if the order can still be paid: a failed card is the ordinary case, and the
        //    customer's next move is to try another. One open payment per order is the database's rule
        //    (UX_Payments_OneOpen); the NOT EXISTS keeps this statement from tripping it, and it is
        //    skipped for an order past its deadline, which the expiry job is about to release.
        var opened = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO sales.Payments (OrderId, ProviderCode, Status, IsOrderSettlement, Amount, Currency, RefundedAmount, CreatedAtUtc)
            SELECT o.Id, {PaymentProviderCodes.PayHere}, {(byte)PaymentStatus.Initiated}, 0, o.GrandTotal, o.Currency, 0, {now}
              FROM sales.Orders AS o
             WHERE o.Id = {failure.OrderId}
               AND o.Status = {(byte)OrderStatus.PendingPayment}
               AND o.PaymentProviderCode = {PaymentProviderCodes.PayHere}
               AND o.PaymentDueAtUtc > {now}
               AND NOT EXISTS (
                     SELECT 1
                       FROM sales.Payments AS p
                      WHERE p.OrderId = o.Id
                        AND p.Status IN ({(byte)PaymentStatus.Initiated}, {(byte)PaymentStatus.Pending}));
            """,
            cancellationToken);

        return new PayHereFailureResult(PayHereTransition.Applied, WasPending: cut == 1, ReplacementOpened: opened == 1);
    }
}
