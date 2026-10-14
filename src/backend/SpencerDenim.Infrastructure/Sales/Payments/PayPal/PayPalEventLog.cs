using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Sales.Payments;
using SpencerDenim.Application.Sales.Payments.Abstractions;
using SpencerDenim.Application.Sales.Payments.PayPal;
using SpencerDenim.Domain.Sales;

namespace SpencerDenim.Infrastructure.Sales.Payments.PayPal;

/// <summary>
/// The shop's own record of what PayPal said to it (doc 08 §3.5 <c>PaymentEvents</c>, EventSource 2 and 3):
/// the create-order and capture <b>responses</b> and the buyer's <b>return</b>, written for reconciliation
/// beside the verified webhooks the webhook endpoint stores.
/// <para>
/// <b>A log, never an instruction.</b> These rows are marked Processed the moment they are written, so the
/// retry job — which selects on <c>NextAttemptAtUtc</c> — never finds them, and it only ever returns
/// webhook-sourced events anyway. The return redirect is "informational, never acted on" (doc 08 §3.5): the
/// buyer's browser controls it.
/// </para>
/// <para>
/// <b>A summary we build, not PayPal's body.</b> A capture response carries the payer's name, email address
/// and shipping address. The summary here holds ids, statuses and the amount and nothing about the person,
/// so the PDPA anonymisation (doc 08 §3.1) has nothing in these rows to scrub.
/// </para>
/// <para>
/// <b>It must never break a payment.</b> An exception while writing a log row is logged and swallowed: the
/// customer's money is the thing that matters, and the capture has already happened by the time most of
/// these run.
/// </para>
/// </summary>
internal sealed partial class PayPalEventLog(IPaymentEventStore events, IClock clock, ILogger<PayPalEventLog> logger)
{
    private const string PayPal = PaymentProviderCodes.PayPal;

    /// <summary>PayPal created the order for a payment.</summary>
    public Task OrderCreatedAsync(int paymentId, int orderId, string paypalOrderId) =>
        WriteAsync(
            PaymentEventSource.ApiResponse,
            "api.create-order",
            PaymentEventKey.ForResource(PayPal, "create-order", Id(paymentId)),
            paymentId,
            orderId,
            writer =>
            {
                writer.WriteNumber("paymentId", paymentId);
                writer.WriteString("paypalOrderId", paypalOrderId);
            });

    /// <summary>The buyer came back to the return address (informational).</summary>
    public Task ReturnedAsync(int? paymentId, int orderId, string? paypalOrderId) =>
        WriteAsync(
            PaymentEventSource.ReturnRedirect,
            "return.redirect",
            PaymentEventKey.ForResource(PayPal, "return", paypalOrderId ?? $"order-{Id(orderId)}"),
            paymentId,
            orderId,
            writer =>
            {
                if (paymentId is { } id)
                {
                    writer.WriteNumber("paymentId", id);
                }

                if (paypalOrderId is not null)
                {
                    writer.WriteString("paypalOrderId", paypalOrderId);
                }
            });

    /// <summary>What PayPal answered to a capture request.</summary>
    public Task CaptureAnsweredAsync(int paymentId, int orderId, string paypalOrderId, PayPalCaptureCallResult result)
    {
        var (kind, snapshot, reason) = result switch
        {
            PayPalCaptureCallResult.Captured captured => ("captured", captured.Order, (string?)null),
            PayPalCaptureCallResult.AlreadyCaptured => ("already-captured", null, null),
            PayPalCaptureCallResult.Declined declined => ("declined", null, declined.Reason),
            PayPalCaptureCallResult.NotApproved => ("not-approved", null, null),
            PayPalCaptureCallResult.OrderNotFound => ("order-not-found", null, null),
            PayPalCaptureCallResult.Rejected rejected => ("rejected", null, rejected.Reason),
            PayPalCaptureCallResult.Unavailable => ("unavailable", null, null),
            _ => ("unknown", null, null),
        };

        // One row per distinct answer to a payment, so a repeated "unavailable" does not write a row each time
        // and a later "captured" still does.
        var discriminator = snapshot is { Captures.Count: > 0 }
            ? $"{kind}:{snapshot.Captures[^1].CaptureId}:{snapshot.Captures[^1].Status}"
            : kind;

        return WriteAsync(
            PaymentEventSource.ApiResponse,
            "api.capture",
            PaymentEventKey.ForResource(PayPal, "capture-response", $"{Id(paymentId)}:{discriminator}"),
            paymentId,
            orderId,
            writer =>
            {
                writer.WriteNumber("paymentId", paymentId);
                writer.WriteString("paypalOrderId", paypalOrderId);
                writer.WriteString("result", kind);

                if (reason is not null)
                {
                    writer.WriteString("reason", reason);
                }

                if (snapshot is not null)
                {
                    writer.WriteString("orderStatus", snapshot.Status.ToString());

                    writer.WriteStartArray("captures");

                    foreach (var capture in snapshot.Captures)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", capture.CaptureId);
                        writer.WriteString("status", capture.Status.ToString());

                        if (capture.Amount is { } amount)
                        {
                            writer.WriteString("amount", amount.ToString("0.00", CultureInfo.InvariantCulture));
                        }

                        if (capture.Currency is not null)
                        {
                            writer.WriteString("currency", capture.Currency);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }
            });
    }

    private async Task WriteAsync(
        PaymentEventSource source,
        string eventType,
        string key,
        int? paymentId,
        int? orderId,
        Action<Utf8JsonWriter> body)
    {
        try
        {
            var now = clock.UtcNow.UtcDateTime;

            using var buffer = new MemoryStream();

            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                body(writer);
                writer.WriteEndObject();
            }

            var receipt = await events.RecordAsync(
                new PaymentEventRecord(
                    PayPal,
                    source,
                    eventType,
                    ProviderEventId: null,
                    key,
                    Encoding.UTF8.GetString(buffer.ToArray()),
                    now,
                    paymentId,
                    orderId),
                CancellationToken.None);

            if (receipt.IsNew)
            {
                // A record of something that has already happened: nothing is waiting to be applied, so the
                // retry time the store stamps on a Received row is cleared at once.
                await events.MarkProcessedAsync(receipt.EventId, now, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogNotRecorded(logger, eventType, exception);
        }
    }

    private static string Id(int value) => value.ToString(CultureInfo.InvariantCulture);

    [LoggerMessage(
        EventId = 3510,
        Level = LogLevel.Warning,
        Message = "The PayPal event '{EventType}' could not be written to the payment event log. The payment "
            + "itself was not affected.")]
    private static partial void LogNotRecorded(ILogger logger, string eventType, Exception exception);
}
