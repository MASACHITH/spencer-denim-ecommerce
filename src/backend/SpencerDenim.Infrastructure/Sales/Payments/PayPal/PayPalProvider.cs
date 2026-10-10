using System.Globalization;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayPal;

/// <summary>
/// PAY-02: PayPal, the second <see cref="IPaymentProvider"/> (doc 03 §3c, doc 11 P8).
/// <para>
/// <b>It answers the three questions checkout asks of every provider and nothing else, and the checkout code
/// does not know it exists.</b> What the payment row looks like and when it is due is
/// <see cref="InitiateAsync"/>; what the customer is told to do next is <see cref="DescribeAsync"/>; whether
/// PayPal can be offered at all right now is <see cref="IsAvailableAsync"/>. Creating the PayPal order, the
/// buyer's approval, the capture, the webhooks and the recovery are the other classes in this folder, reached
/// from the endpoints — not from here, because answering a question must not move money.
/// </para>
/// <para>
/// <b>The charge is in US dollars</b> (doc 08 D1; PayPal cannot settle in rupees): the order stays in LKR, and
/// the dollar amount, the rate and the buffer it was worked out with are frozen on the payment row at
/// initiation, so a refund can be reconciled later and a rate edit while the customer is at PayPal changes
/// nothing for them.
/// </para>
/// <para>
/// <b>Two-stage description</b>, as <see cref="IPaymentProvider"/> lays out for a provider the customer pays
/// on its own page. At placement the order does not exist yet, so the answer is null — which also means no
/// bank-transfer instructions email is queued. On the order page the order is found by its number and the
/// answer says exactly where the payment stands.
/// </para>
/// </summary>
internal sealed partial class PayPalProvider(
    IPayPalSettingsReader settings,
    IPayPalPaymentStore payments,
    PayPalLinks links,
    IClock clock,
    ILogger<PayPalProvider> logger) : IPaymentProvider
{
    public string Code => PaymentProviderCodes.PayPal;

    /// <summary>
    /// Doc 11 P8's preconditions: credentials, the webhook id and an exchange rate updated within
    /// <c>RateMaxAgeDays</c> — and a public origin to send the buyer back to. Without all of them PayPal is
    /// not offered, rather than offered and failing at the customer.
    /// </summary>
    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        var configured = await settings.ReadAsync(cancellationToken);
        var availability = configured.AvailabilityAt(clock.UtcNow.UtcDateTime);

        if (availability == PayPalAvailability.Available && links.IsAvailable)
        {
            return true;
        }

        LogNotOffered(logger, links.IsAvailable ? availability.ToString() : "NoPublicOrigin");
        return false;
    }

    /// <summary>
    /// Doc 08 §4.1's PayPal placement effects: an open <see cref="PaymentStatus.Initiated"/> row with the
    /// dollar amount at a frozen rate, due at placement + <c>Orders.PayPalExpiryMinutes</c> (60).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// PayPal is not available. Checkout asks <see cref="IsAvailableAsync"/> first, so this is the narrow
    /// window in which the rate went stale or a credential was removed between that question and this one.
    /// It is refused loudly rather than written with a made-up amount.
    /// </exception>
    public async ValueTask<PaymentInitiation> InitiateAsync(
        PaymentInitiationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var configured = await settings.ReadAsync(cancellationToken);
        var availability = configured.AvailabilityAt(clock.UtcNow.UtcDateTime);

        if (availability != PayPalAvailability.Available)
        {
            throw new InvalidOperationException(
                $"PayPal cannot take a payment right now ({availability}), so no PayPal order can be placed.");
        }

        return PayPalInitiationPolicy.Build(configured, request.AmountLkr, request.PlacedAtUtc);
    }

    public async Task<PaymentInstructions?> DescribeAsync(
        PaymentInstructionsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Stage one: at placement there is no order to find, and the answer is null (see the type remarks).
        var order = await payments.FindOrderByNumberAsync(request.OrderNumber, cancellationToken);

        if (order is null || order.PaymentProviderCode != Code)
        {
            return null;
        }

        var now = clock.UtcNow.UtcDateTime;
        var due = EmailBody.ColomboDateTime(order.PaymentDueAtUtc);

        switch (order.Open?.Status)
        {
            case PaymentStatus.Capturing:
                return Notice(
                    request,
                    "Your payment is being processed. Please wait a moment and reload this page. Do not pay "
                    + "again: if the payment went through, this page will show it.");

            case PaymentStatus.Pending:
                return Notice(
                    request,
                    "Your payment is pending with PayPal. Some payments, such as an eCheck, take a few days to "
                    + $"clear. We are holding your items until {due}, and this page will show your order as paid "
                    + "as soon as PayPal confirms it.");

            case PaymentStatus.Initiated:
                return PayNow(request, order, order.Open, due, retry: null);
        }

        // No open payment. Say what happened to the last one, and offer a fresh attempt while the order can
        // still be paid.
        //
        // The order page no longer reaches this for a payment in this status: GetOrderQueryHandler sees the
        // provider-neutral PaymentNotice and does not ask a provider at all. It stays so that the provider is
        // correct on its own - another caller of DescribeAsync must never be told to offer a second payment.
        if (order.Latest?.Status == PaymentStatus.CapturedRequiresRefund)
        {
            return Notice(
                request,
                "We received your payment, but could not apply it to this order automatically. Our team will "
                + "contact you. You do not need to pay again.");
        }

        var configured = await settings.ReadAsync(cancellationToken);

        if (order.PaymentDueAtUtc <= now)
        {
            return Notice(request, "The time to pay for this order has run out. Please place a new order.");
        }

        var canRetry = configured.AvailabilityAt(now) == PayPalAvailability.Available && links.IsAvailable;

        if (!canRetry)
        {
            return Notice(
                request,
                "Paying by PayPal is not available right now. Please try again later, or place a new order and "
                + "choose another way to pay.");
        }

        var previous = order.Latest?.Status switch
        {
            PaymentStatus.Failed => "Your payment was declined by PayPal. You can try again. ",
            PaymentStatus.Cancelled => "You left PayPal without paying. You can pay again. ",
            _ => string.Empty,
        };

        var fresh = PayPalInitiationPolicy.Build(configured, order.GrandTotal, order.PlacedAtUtc);

        return PayNow(
            request,
            order,
            new PayPalPaymentView(
                0,
                PaymentStatus.Initiated,
                null,
                fresh.ProviderAmount,
                fresh.ProviderCurrency,
                fresh.ExchangeRate,
                fresh.FxBufferPercent,
                null,
                null),
            due,
            retry: previous);
    }

    /// <summary>
    /// The "Pay now" answer: the dollar amount and how it was worked out, and a link to the shop's own start
    /// endpoint — which creates the PayPal order and sends the buyer there.
    /// </summary>
    private PaymentInstructions Notice(PaymentInstructionsRequest request, string notes) =>
        new(
            Code,
            Reference: null,
            request.AmountLkr,
            request.PaymentDueAtUtc,
            Fields: [],
            notes,
            RedirectUrl: null,
            AcceptsProof: false);

    private PaymentInstructions PayNow(
        PaymentInstructionsRequest request,
        PayPalOrderView order,
        PayPalPaymentView payment,
        string due,
        string? retry)
    {
        var start = links.StartUrl(order.PublicId);

        if (start is null || payment.ProviderAmount is not { } usd || payment.ExchangeRate is not { } rate)
        {
            return Notice(
                request,
                "Paying by PayPal is not available right now. Please try again later, or place a new order and "
                + "choose another way to pay.");
        }

        var fields = new List<PaymentInstructionField>
        {
            new("Amount charged by PayPal", $"USD {PayPalUsdAmount.Format(usd)}"),
            new("Exchange rate", $"Rs. {rate.ToString("0.00####", CultureInfo.InvariantCulture)} per US dollar"),
        };

        if (payment.FxBufferPercent is { } buffer and > 0m)
        {
            fields.Add(new PaymentInstructionField(
                "Currency buffer",
                $"{buffer.ToString("0.##", CultureInfo.InvariantCulture)}% added to cover exchange-rate movement"));
        }

        return new PaymentInstructions(
            Code,
            Reference: null,
            request.AmountLkr,
            request.PaymentDueAtUtc,
            fields,
            (retry ?? string.Empty)
            + "PayPal cannot charge in rupees, so your total is converted to US dollars. This is the amount "
            + $"you will be asked to pay at PayPal. Nothing is charged until you approve the payment there. "
            + $"We hold your items until {due}.",
            start,
            AcceptsProof: false);
    }

    [LoggerMessage(
        EventId = 3520,
        Level = LogLevel.Debug,
        Message = "PayPal is not being offered at checkout: {Reason}.")]
    private static partial void LogNotOffered(ILogger logger, string reason);
}
