using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayPal;

/// <summary>
/// <see cref="IPayPalCheckoutFlow"/>: the customer-facing half of doc 11 P8.
/// <para>
/// <b>Start</b> finds or makes the order's one open PayPal payment (the retry rule lives in
/// <see cref="IPayPalPaymentStore.OpenAttemptAsync"/>), creates the PayPal order for it if it has none —
/// idempotently, under a request id derived from the payment, so a crash between PayPal's answer and our write
/// costs nothing — and sends the buyer to approve it. <b>Return</b> runs claim, capture and apply
/// (<see cref="PayPalCaptureProcessor"/>) and picks the sentence the order page shows. <b>Cancel</b> closes
/// the abandoned attempt so the next one starts at the current rate.
/// </para>
/// <para>
/// <b>Nothing here trusts the browser.</b> The order captured is the one stored on the payment, the amount is
/// the one frozen on it, and the order page's status — not the hint in the redirect — is what tells the buyer
/// whether they are paid.
/// </para>
/// </summary>
internal sealed partial class PayPalCheckoutFlow(
    IPayPalPaymentStore payments,
    IPayPalSettingsReader settings,
    IPayPalGateway gateway,
    PayPalCaptureProcessor processor,
    PayPalLinks links,
    PayPalEventLog eventLog,
    IClock clock,
    ILogger<PayPalCheckoutFlow> logger) : IPayPalCheckoutFlow
{
    public async Task<PayPalStartResult> StartAsync(Guid orderPublicId, CancellationToken cancellationToken)
    {
        var order = await payments.FindOrderAsync(orderPublicId, cancellationToken);

        if (order is null)
        {
            return new PayPalStartResult.NotFound();
        }

        if (order.PaymentProviderCode != PaymentProviderCodes.PayPal || order.Status != OrderStatus.PendingPayment)
        {
            return new PayPalStartResult.Back(PayPalPageHints.Closed);
        }

        var configured = await settings.ReadAsync(cancellationToken);
        var now = clock.UtcNow.UtcDateTime;

        if (!configured.HasCredentials || !links.IsAvailable)
        {
            LogNotConfigured(logger, order.OrderNumber);
            return new PayPalStartResult.Back(PayPalPageHints.Unavailable);
        }

        // A new attempt needs a fresh rate. Reusing an existing one does not: it keeps the rate it was
        // opened with (doc 11 P8: "the stored USD amount on that payment is used; retries use the new rate").
        var initiation = configured.AvailabilityAt(now) == PayPalAvailability.Available
            ? PayPalInitiationPolicy.Build(configured, order.GrandTotal, order.PlacedAtUtc)
            : null;

        var opened = await payments.OpenAttemptAsync(order.OrderId, initiation, now, cancellationToken);

        PayPalPaymentView payment;

        switch (opened)
        {
            case OpenAttemptResult.Blocked blocked:
                return new PayPalStartResult.Back(blocked.Reason switch
                {
                    PayPalBlock.BeingProcessed => PayPalPageHints.Processing,
                    PayPalBlock.PendingWithPayPal => PayPalPageHints.Pending,
                    _ => PayPalPageHints.Closed,
                });

            case OpenAttemptResult.NeedsInitiation:
                return new PayPalStartResult.Back(PayPalPageHints.Unavailable);

            case OpenAttemptResult.Reused reused:
                payment = reused.Payment;
                break;

            case OpenAttemptResult.Created created:
                payment = created.Payment;
                break;

            default:
                return new PayPalStartResult.Back(PayPalPageHints.Unavailable);
        }

        // The PayPal order already exists: take the buyer back to it.
        if (payment.ProviderReference is { } existing)
        {
            return new PayPalStartResult.Redirect(PayPalHttpGateway.CheckoutUrl(configured, existing));
        }

        if (payment.ProviderAmount is not { } usd
            || links.ReturnUrl(order.PublicId) is not { } returnUrl
            || links.CancelUrl(order.PublicId) is not { } cancelUrl)
        {
            return new PayPalStartResult.Back(PayPalPageHints.Unavailable);
        }

        var created2 = await gateway.CreateOrderAsync(
            new PayPalCreateOrderRequest(
                PayPalRequestIds.Create(payment.Id),
                usd,
                payment.ProviderCurrency ?? PayPalUsdAmount.Currency,
                PayPalRequestIds.CustomId(payment.Id),
                $"Order {order.OrderNumber}",
                returnUrl,
                cancelUrl),
            cancellationToken);

        switch (created2)
        {
            case PayPalCreateOrderResult.Created ok:
            {
                var stored = await payments.SetProviderReferenceAsync(payment.Id, ok.OrderId, now, cancellationToken);

                if (!stored)
                {
                    // A concurrent start got there first. Both asked PayPal under the same request id, so
                    // both got the same order; whichever id is stored is the one.
                    var again = await payments.FindOrderAsync(orderPublicId, cancellationToken);

                    if (again?.Open?.ProviderReference is { } winner && winner != ok.OrderId)
                    {
                        LogReferenceDiffers(logger, payment.Id);
                        return new PayPalStartResult.Redirect(PayPalHttpGateway.CheckoutUrl(configured, winner));
                    }
                }

                await eventLog.OrderCreatedAsync(payment.Id, order.OrderId, ok.OrderId);

                return new PayPalStartResult.Redirect(ok.ApprovalUrl);
            }

            case PayPalCreateOrderResult.Rejected rejected:
                LogCreateFailed(logger, payment.Id, rejected.Reason);
                return new PayPalStartResult.Back(PayPalPageHints.Unavailable);

            case PayPalCreateOrderResult.Unavailable unavailable:
                LogCreateFailed(logger, payment.Id, unavailable.Reason);
                return new PayPalStartResult.Back(PayPalPageHints.Unavailable);

            default:
                return new PayPalStartResult.Back(PayPalPageHints.Unavailable);
        }
    }

    public async Task<PayPalReturnResult> ReturnAsync(
        Guid orderPublicId,
        string? paypalOrderToken,
        CancellationToken cancellationToken)
    {
        var order = await payments.FindOrderAsync(orderPublicId, cancellationToken);

        if (order is null)
        {
            return PayPalReturnResult.NotFound;
        }

        // For the log only: the browser controls this value. It is compared with what we stored and never
        // used to decide what is captured.
        var stored = order.Open?.ProviderReference ?? order.Latest?.ProviderReference;

        if (paypalOrderToken is not null && stored is not null && !string.Equals(paypalOrderToken, stored, StringComparison.Ordinal))
        {
            LogReturnTokenDiffers(logger, order.OrderNumber);
        }

        await eventLog.ReturnedAsync(order.Open?.Id ?? order.Latest?.Id, order.OrderId, stored);

        switch (order.Open)
        {
            case { Status: PaymentStatus.Initiated } open:
            {
                var outcome = await processor.CaptureAsync(order, open, cancellationToken);

                if (outcome == PayPalCaptureOutcome.NotPayable)
                {
                    // The claim was refused because the order changed between our read and the claim - most
                    // often another return got there first and paid it. Say what the order is *now*, not
                    // "can no longer be paid" to somebody whose order has just been paid.
                    return new PayPalReturnResult(await HintNowAsync(orderPublicId, cancellationToken));
                }

                return new PayPalReturnResult(Hint(outcome));
            }

            case { Status: PaymentStatus.Capturing }:
                return new PayPalReturnResult(PayPalPageHints.Processing);

            case { Status: PaymentStatus.Pending }:
                return new PayPalReturnResult(PayPalPageHints.Pending);
        }

        return new PayPalReturnResult(HintWithoutOpenPayment(order));
    }

    /// <summary>The sentence for the order as it stands now, read afresh.</summary>
    private async Task<string> HintNowAsync(Guid orderPublicId, CancellationToken cancellationToken)
    {
        var now = await payments.FindOrderAsync(orderPublicId, cancellationToken);

        return now?.Open?.Status switch
        {
            PaymentStatus.Capturing => PayPalPageHints.Processing,
            PaymentStatus.Pending => PayPalPageHints.Pending,
            _ => now is null ? PayPalPageHints.Closed : HintWithoutOpenPayment(now),
        };
    }

    public async Task<PayPalReturnResult> CancelAsync(Guid orderPublicId, CancellationToken cancellationToken)
    {
        var order = await payments.FindOrderAsync(orderPublicId, cancellationToken);

        if (order is null)
        {
            return PayPalReturnResult.NotFound;
        }

        switch (order.Open)
        {
            case { Status: PaymentStatus.Initiated } open:
                await payments.CancelInitiatedAsync(
                    order.OrderId,
                    open.Id,
                    clock.UtcNow.UtcDateTime,
                    cancellationToken);

                return new PayPalReturnResult(PayPalPageHints.Cancelled);

            case { Status: PaymentStatus.Capturing }:
                return new PayPalReturnResult(PayPalPageHints.Processing);

            case { Status: PaymentStatus.Pending }:
                return new PayPalReturnResult(PayPalPageHints.Pending);
        }

        return new PayPalReturnResult(HintWithoutOpenPayment(order));
    }

    /// <summary>The sentence for each way a capture can have gone (doc 11 P8 step 6).</summary>
    internal static string Hint(PayPalCaptureOutcome outcome) =>
        outcome switch
        {
            PayPalCaptureOutcome.Paid => PayPalPageHints.Paid,
            PayPalCaptureOutcome.PaymentException => PayPalPageHints.Received,
            PayPalCaptureOutcome.Pending => PayPalPageHints.Pending,
            PayPalCaptureOutcome.Declined => PayPalPageHints.Declined,
            PayPalCaptureOutcome.Processing => PayPalPageHints.Processing,
            PayPalCaptureOutcome.NotApproved => PayPalPageHints.NotApproved,
            _ => PayPalPageHints.Closed,
        };

    /// <summary>
    /// What to say when the order has no open PayPal payment: a paid order is paid, a declined attempt can be
    /// retried, money that could not be applied is with the team, and anything else is over.
    /// </summary>
    private static string HintWithoutOpenPayment(PayPalOrderView order)
    {
        if (order.Status is OrderStatus.Paid or OrderStatus.Processing or OrderStatus.Shipped
            or OrderStatus.Delivered or OrderStatus.Refunded)
        {
            return PayPalPageHints.Paid;
        }

        return order.Latest?.Status switch
        {
            // The order and its payments are two reads, so a payment can have settled between them: the order
            // still says PendingPayment and its newest payment says Succeeded. A settled payment is a paid order.
            PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded =>
                PayPalPageHints.Paid,
            PaymentStatus.CapturedRequiresRefund => PayPalPageHints.Received,
            PaymentStatus.Failed when order.Status == OrderStatus.PendingPayment => PayPalPageHints.Declined,
            PaymentStatus.Cancelled when order.Status == OrderStatus.PendingPayment => PayPalPageHints.Cancelled,
            _ => PayPalPageHints.Closed,
        };
    }

    [LoggerMessage(
        EventId = 3540,
        Level = LogLevel.Warning,
        Message = "A customer tried to pay order {OrderNumber} by PayPal but PayPal is not configured or the "
            + "shop has no public address set (Email:StorefrontBaseUrl). Fill in Settings.Payments: client ID, "
            + "secret and webhook ID.")]
    private static partial void LogNotConfigured(ILogger logger, string orderNumber);

    [LoggerMessage(
        EventId = 3541,
        Level = LogLevel.Warning,
        Message = "PayPal order for payment {PaymentId} could not be created ({Reason}); the customer was sent "
            + "back to their order page.")]
    private static partial void LogCreateFailed(ILogger logger, int paymentId, string reason);

    [LoggerMessage(
        EventId = 3542,
        Level = LogLevel.Warning,
        Message = "Payment {PaymentId} was given two different PayPal order ids by concurrent starts; the stored "
            + "one is used.")]
    private static partial void LogReferenceDiffers(ILogger logger, int paymentId);

    [LoggerMessage(
        EventId = 3543,
        Level = LogLevel.Warning,
        Message = "A buyer returned to order {OrderNumber} with a PayPal token that is not the order on the "
            + "payment. The stored PayPal order is the one used; the token was ignored.")]
    private static partial void LogReturnTokenDiffers(ILogger logger, string orderNumber);
}
