using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Application.Sales.Payments.PayPal;

/// <summary>What PayPal said about a capture, from whichever source said it.</summary>
/// <param name="PaymentId"><c>sales.Payments.Id</c> the news is about.</param>
/// <param name="CaptureId">PayPal's capture id, or null when PayPal gave none (a refusal at the capture call).</param>
/// <param name="Status">The capture's status.</param>
/// <param name="Amount">The amount, or null when it could not be read — which a COMPLETED capture treats as a mismatch.</param>
/// <param name="Currency">Its currency, or null when it could not be read.</param>
/// <param name="PayerId">PayPal's id for the payer, if known.</param>
/// <param name="Reason">PayPal's reason for a pending or declined capture, for <c>FailureMessage</c>.</param>
public sealed record PayPalCaptureFacts(
    int PaymentId,
    string? CaptureId,
    PayPalCaptureStatus Status,
    decimal? Amount,
    string? Currency,
    string? PayerId,
    string? Reason);

/// <summary>What applying a capture result did.</summary>
public abstract record PayPalApplyResult
{
    private PayPalApplyResult()
    {
    }

    /// <summary>The order is Paid.</summary>
    public sealed record Settled(string OrderNumber, string InvoiceNumber) : PayPalApplyResult;

    /// <summary>The money is on record as a payment exception and staff have been told; the order is untouched.</summary>
    public sealed record CapturedRequiresRefund(string Reason, string OrderNumber) : PayPalApplyResult;

    /// <summary>PayPal is holding the capture; the order's hold has been extended.</summary>
    public sealed record Pending : PayPalApplyResult;

    /// <summary>PayPal refused the capture; the payment has failed.</summary>
    public sealed record Declined : PayPalApplyResult;

    /// <summary>
    /// Nothing to do: the payment was already past this news (a duplicate, or an event out of order).
    /// <paramref name="Current"/> is where the payment stood.
    /// </summary>
    public sealed record Duplicate(PaymentStatus Current) : PayPalApplyResult;

    /// <summary>A refund or a reversal: recorded, and nothing changed (see <see cref="PayPalApplyAction.RecordOnly"/>).</summary>
    public sealed record Recorded : PayPalApplyResult;

    /// <summary>There is no such PayPal payment. Nothing was written.</summary>
    public sealed record NotFound : PayPalApplyResult;
}

/// <summary>
/// Doc 08 §4.2 step 3 and 4 — <b>the one place a PayPal result is applied</b>. The capture response, a
/// verified webhook and the recovery job all end here (doc 11 P8 step 6: "the same logic runs for the API
/// response, a verified webhook, or the recovery job"), so a payment cannot be settled one way by the
/// response and another by the webhook.
/// <para>
/// It reads where the payment stands, asks <see cref="PayPalApplyDecision"/> what to attempt, and does it
/// through the store that owns the statement: <see cref="IOrderSettlementStore"/> for money that is ours (the
/// same Paid effects a staff approval has — nothing is re-implemented here), <see cref="IPayPalPaymentStore"/>
/// for the PENDING hold and the decline. Every one of those is a conditional statement, so applying the same
/// news twice, or two different news out of order, is harmless by construction.
/// </para>
/// </summary>
public sealed class PayPalCaptureApplier(
    IOrderSettlementStore settlement,
    IPayPalPaymentStore payments,
    IPayPalSettingsReader settings,
    IClock clock)
{
    /// <summary>The failure code a refused capture leaves on the payment (doc 08 §4.2 step 4).</summary>
    public const string InstrumentDeclined = "INSTRUMENT_DECLINED";

    /// <summary>Applies <paramref name="facts"/>.</summary>
    public async Task<PayPalApplyResult> ApplyAsync(PayPalCaptureFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var payment = await payments.FindPaymentAsync(facts.PaymentId, cancellationToken);

        if (payment is null)
        {
            return new PayPalApplyResult.NotFound();
        }

        var now = clock.UtcNow.UtcDateTime;

        switch (PayPalApplyDecision.Decide(payment.Status, facts.Status))
        {
            case PayPalApplyAction.Settle:
                return await SettleAsync(payment, facts, now, cancellationToken);

            case PayPalApplyAction.MarkPending:
            {
                var configured = await settings.ReadAsync(cancellationToken);

                var moved = await payments.RecordPendingAsync(
                    payment.OrderId,
                    payment.PaymentId,
                    facts.CaptureId,
                    configured.PendingCaptureMaxDays,
                    now,
                    cancellationToken);

                return moved == PayPalMoveResult.Applied
                    ? new PayPalApplyResult.Pending()
                    : new PayPalApplyResult.Duplicate(payment.Status);
            }

            case PayPalApplyAction.MarkDeclined:
            {
                var configured = await settings.ReadAsync(cancellationToken);

                var moved = await payments.RecordDeclinedAsync(
                    payment.OrderId,
                    payment.PaymentId,
                    InstrumentDeclined,
                    Clip(facts.Reason),
                    configured.ExpiryMinutes,
                    now,
                    cancellationToken);

                return moved == PayPalMoveResult.Applied
                    ? new PayPalApplyResult.Declined()
                    : new PayPalApplyResult.Duplicate(payment.Status);
            }

            case PayPalApplyAction.RecordOnly:
                return new PayPalApplyResult.Recorded();

            default:
                return new PayPalApplyResult.Duplicate(payment.Status);
        }
    }

    /// <summary>
    /// A COMPLETED capture: <see cref="IOrderSettlementStore.SettleAsync"/> checks the amount and currency
    /// against what PayPal was asked for and pays the order — or puts the money on record as an exception.
    /// A capture whose amount could not be read at all cannot be compared, so it is routed to the exception
    /// path as a mismatch rather than guessed at.
    /// </summary>
    private async Task<PayPalApplyResult> SettleAsync(
        PayPalPaymentRef payment,
        PayPalCaptureFacts facts,
        DateTime now,
        CancellationToken cancellationToken)
    {
        PaymentSettlementResult result;

        if (facts.Amount is not { } amount || string.IsNullOrWhiteSpace(facts.Currency))
        {
            result = await settlement.RouteToExceptionAsync(
                new PaymentExceptionRouting(
                    payment.PaymentId,
                    PaymentProviderCodes.PayPal,
                    PaymentExceptionReasons.AmountMismatch,
                    now,
                    ProviderTransactionId: facts.CaptureId,
                    PayerReference: facts.PayerId),
                cancellationToken);
        }
        else
        {
            result = await settlement.SettleAsync(
                new PaymentSettlement(
                    payment.PaymentId,
                    PaymentProviderCodes.PayPal,
                    amount,
                    facts.Currency,
                    now,
                    facts.CaptureId,
                    facts.PayerId),
                cancellationToken);
        }

        return result switch
        {
            PaymentSettlementResult.Settled settled =>
                new PayPalApplyResult.Settled(settled.OrderNumber, settled.InvoiceNumber),
            PaymentSettlementResult.CapturedRequiresRefund refund =>
                new PayPalApplyResult.CapturedRequiresRefund(refund.Reason, refund.OrderNumber),
            PaymentSettlementResult.AlreadyHandled handled => new PayPalApplyResult.Duplicate(handled.PaymentStatus),
            _ => new PayPalApplyResult.NotFound(),
        };
    }

    /// <summary><c>FailureMessage</c> is <c>nvarchar(500)</c>.</summary>
    private static string? Clip(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? null : reason.Length <= 500 ? reason : reason[..500];
}
