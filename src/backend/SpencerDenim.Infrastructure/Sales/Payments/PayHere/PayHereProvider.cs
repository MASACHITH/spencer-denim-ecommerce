using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.Contracts;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayHere;

/// <summary>
/// PayHere (doc 11 P8b, issue #141): local cards, mobile wallets and internet banking, charged <b>in LKR</b>,
/// confirmed only by PayHere's own server-to-server notification. The third <see cref="IPaymentProvider"/>
/// and the first redirect gateway — a class, a registration line and settings rows, with no change to
/// checkout (PAY-03).
/// <para>
/// <b>What it does and does not do.</b> It answers the two PAY-03 questions and calls nothing: no request
/// leaves the server. The customer's browser carries a signed form to PayHere, and PayHere's notification
/// comes back to <c>POST /api/payments/payhere/notify</c>, which verifies it and pays the order through
/// <see cref="IOrderSettlementStore"/> (see <see cref="PayHereNotifyCommandHandler"/>). The browser's return
/// is display only and proves nothing.
/// </para>
/// <para>
/// <b>It is offered only when it can work.</b> <see cref="IsAvailableAsync"/> is false until the mode, the
/// merchant ID and the secret are set and the server knows the shop's public address — PayHere refuses a
/// form it cannot return the customer from, and cannot call an address it was never given. Checkout asks, so
/// a gateway that is switched on in the database but not ready is hidden rather than taking orders nobody
/// can pay for.
/// </para>
/// <para>
/// <b>Two-stage description, the convention for a gateway whose form needs the payment id.</b> At placement
/// there is no payment row yet, so <see cref="DescribeAsync"/> answers <see langword="null"/> (and no
/// bank-transfer instructions email is queued). On the order page the attempt exists and is named; the answer
/// is then the signed form to post, or — if PayHere has said the payment is pending — a notice and no button,
/// so a customer is not invited to pay twice.
/// </para>
/// </summary>
internal sealed partial class PayHereProvider(
    IPayHereSettingsReader settings,
    IPayHerePaymentStore payments,
    EmailOptions emailOptions,
    PayHereWarningThrottle throttle,
    IClock clock,
    ILogger<PayHereProvider> logger) : IPaymentProvider
{
    public string Code => PaymentProviderCodes.PayHere;

    /// <summary>
    /// Whether checkout may offer PayHere: the mode, the merchant ID and the secret are set and the shop's
    /// public address is usable. <b>Fails closed and says why.</b> This is only ever asked for a provider row
    /// the shop has switched <i>on</i>, so a "no" here means an administrator expects PayHere to work and it
    /// does not - which is exactly when a log line is owed. It carries the problems and never a value (the
    /// sentences name what is missing, not what is configured), and is throttled because the question is
    /// asked on every view of the checkout page.
    /// </summary>
    public async ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        var current = await settings.ReadAsync(cancellationToken);
        var problems = PayHereReadiness.Problems(current, emailOptions.StorefrontBaseUrl);

        if (problems.Count == 0)
        {
            return true;
        }

        if (throttle.ShouldWarn(clock.UtcNow))
        {
            LogUnavailable(logger, string.Join(' ', problems));
        }

        return false;
    }

    /// <summary>
    /// An <see cref="PaymentStatus.Initiated"/> payment in LKR, due at placement plus
    /// <c>Payments.PayHere.ExpiryMinutes</c> (60).
    /// </summary>
    /// <remarks>
    /// <b>No provider currency, amount, rate or buffer</b>: PayHere charges in the order's own currency, so
    /// the columns that mean "this payment was converted" stay null — and the settlement store therefore
    /// holds the notified amount to <c>Payments.Amount</c> itself.
    /// <para>
    /// <b>It refuses when PayHere is not configured</b> rather than writing a payment row nobody can pay.
    /// Checkout has just asked <see cref="IsAvailableAsync"/> through the list of methods, so reaching this
    /// with a gap in the settings means an administrator cleared them between two requests of one shopper's.
    /// </para>
    /// </remarks>
    public async ValueTask<PaymentInitiation> InitiateAsync(
        PaymentInitiationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var current = await settings.ReadAsync(cancellationToken);
        var problems = PayHereReadiness.Problems(current, emailOptions.StorefrontBaseUrl);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "PayHere cannot take an order right now: " + string.Join(' ', problems));
        }

        return new PaymentInitiation(
            PaymentStatus.Initiated,
            request.PlacedAtUtc.AddMinutes(current.ExpiryMinutes));
    }

    public async Task<PaymentInstructions?> DescribeAsync(
        PaymentInstructionsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // No attempt yet: the placement call. Nothing to describe until the payment row exists.
        if (request.PaymentId is not { } paymentId)
        {
            return null;
        }

        var current = await settings.ReadAsync(cancellationToken);

        if (PayHereReadiness.Problems(current, emailOptions.StorefrontBaseUrl).Count > 0)
        {
            return null;
        }

        var context = await payments.FindCheckoutContextAsync(paymentId, cancellationToken);

        if (context is null)
        {
            return null;
        }

        return context.Status switch
        {
            PaymentStatus.Initiated => new PaymentInstructions(
                Code,
                Reference: null,
                request.AmountLkr,
                request.PaymentDueAtUtc,
                Fields: [],
                Notes: null,
                RedirectUrl: null,

                // PayHere confirms itself; there is no slip to send. The order page also shows no upload for
                // a redirect, and the contract test holds every redirecting provider to this.
                AcceptsProof: false,
                RedirectForm: PayHereCheckoutForm.Build(current, context, emailOptions.StorefrontBaseUrl)),

            // PayHere has the payment and has not finished with it. No button: a second payment is the one
            // thing this page must not invite (doc 11 P8b step 6).
            PaymentStatus.Pending => new PaymentInstructions(
                Code,
                Reference: null,
                request.AmountLkr,
                request.PaymentDueAtUtc,
                Fields: [],
                Notes: PendingNotice,
                RedirectUrl: null,
                AcceptsProof: false),

            _ => null,
        };
    }

    [LoggerMessage(
        EventId = 3520,
        Level = LogLevel.Warning,
        Message = "PayHere is switched on but customers cannot choose it, so it is hidden at checkout: "
            + "{Problems} Fix Settings.Payments.PayHere in the admin panel or the server's "
            + "Email__StorefrontBaseUrl. This is logged at most every 30 minutes.")]
    private static partial void LogUnavailable(ILogger logger, string problems);

    /// <summary>
    /// What the page says while PayHere reports the payment as pending. Plain text, no markup. It names the
    /// gateway because it is server copy about this one payment, as PayPal's equivalent does.
    /// </summary>
    public const string PendingNotice =
        "Your payment is pending with PayHere. We will update this order as soon as PayHere confirms it, "
        + "so please do not pay again.";
}
