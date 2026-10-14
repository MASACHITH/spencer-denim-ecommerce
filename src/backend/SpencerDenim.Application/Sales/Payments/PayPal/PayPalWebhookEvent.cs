using System.Globalization;
using System.Text.Json;

namespace SpencerDenim.Application.Sales.Payments.PayPal;

/// <summary>
/// A PayPal webhook notification, reduced to the facts the shop acts on. Built by
/// <see cref="PayPalWebhookEventParser"/> from the raw body — the same parse for a fresh delivery and for a
/// stored event the retry job picks up.
/// </summary>
/// <param name="EventId">PayPal's event id (<c>WH-…</c>), or null when the body has none.</param>
/// <param name="EventType">E.g. <c>PAYMENT.CAPTURE.COMPLETED</c>.</param>
/// <param name="ResourceId">The resource's id — for a capture event, the capture id.</param>
/// <param name="RelatedOrderId">PayPal's order id the capture belongs to (<c>supplementary_data.related_ids.order_id</c>).</param>
/// <param name="CustomId">The purchase unit's <c>custom_id</c> — ours, <see cref="PayPalRequestIds.CustomId"/>.</param>
/// <param name="CaptureStatus">
/// What the event says happened to the capture, or null for an event type this build does not act on
/// (an order event, an unknown type).
/// </param>
/// <param name="Amount">The resource's amount, or null.</param>
/// <param name="Currency">The resource's currency, or null.</param>
/// <param name="Reason">PayPal's <c>status_details.reason</c>, if any.</param>
public sealed record PayPalWebhookEvent(
    string? EventId,
    string EventType,
    string? ResourceId,
    string? RelatedOrderId,
    string? CustomId,
    PayPalCaptureStatus? CaptureStatus,
    decimal? Amount,
    string? Currency,
    string? Reason)
{
    /// <summary>Whether this build acts on the event (as opposed to recording and ignoring it).</summary>
    public bool IsCaptureEvent => CaptureStatus is not null;
}

/// <summary>
/// Reads a PayPal webhook body. <b>Defensive about every field</b> — the body is verified as PayPal's before
/// it gets here, but a verified body can still be an event type or a shape this build has not seen, and the
/// right answer to that is "record it and do nothing", never an exception that fails the event for ever.
/// </summary>
public static class PayPalWebhookEventParser
{
    /// <summary>The event types the shop acts on, mapped to what they say happened to a capture.</summary>
    public static PayPalCaptureStatus? CaptureStatusFor(string eventType) =>
        eventType switch
        {
            "PAYMENT.CAPTURE.COMPLETED" => PayPalCaptureStatus.Completed,
            "PAYMENT.CAPTURE.PENDING" => PayPalCaptureStatus.Pending,
            "PAYMENT.CAPTURE.DENIED" or "PAYMENT.CAPTURE.DECLINED" => PayPalCaptureStatus.Declined,
            "PAYMENT.CAPTURE.REFUNDED" => PayPalCaptureStatus.Refunded,
            "PAYMENT.CAPTURE.REVERSED" => PayPalCaptureStatus.Reversed,
            _ => null,
        };

    /// <summary>
    /// Parses <paramref name="rawBody"/>. False when it is not a JSON object with an <c>event_type</c> — the
    /// caller answers 400 for a body that cannot be an event at all.
    /// </summary>
    public static bool TryParse(string rawBody, out PayPalWebhookEvent? webhookEvent)
    {
        webhookEvent = null;

        if (string.IsNullOrWhiteSpace(rawBody))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(rawBody);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || Text(root, "event_type") is not { Length: > 0 } eventType)
            {
                return false;
            }

            var resource = root.TryGetProperty("resource", out var found) && found.ValueKind == JsonValueKind.Object
                ? found
                : default;

            var hasResource = resource.ValueKind == JsonValueKind.Object;

            string? orderId = null;

            if (hasResource
                && resource.TryGetProperty("supplementary_data", out var supplementary)
                && supplementary.ValueKind == JsonValueKind.Object
                && supplementary.TryGetProperty("related_ids", out var related)
                && related.ValueKind == JsonValueKind.Object)
            {
                orderId = Text(related, "order_id");
            }

            decimal? amount = null;
            string? currency = null;

            if (hasResource
                && resource.TryGetProperty("amount", out var money)
                && money.ValueKind == JsonValueKind.Object)
            {
                currency = Text(money, "currency_code");

                if (Text(money, "value") is { } value
                    && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                {
                    amount = parsed;
                }
            }

            string? reason = null;

            if (hasResource
                && resource.TryGetProperty("status_details", out var details)
                && details.ValueKind == JsonValueKind.Object)
            {
                reason = Text(details, "reason");
            }

            webhookEvent = new PayPalWebhookEvent(
                Text(root, "id"),
                eventType,
                hasResource ? Text(resource, "id") : null,
                orderId,
                hasResource ? Text(resource, "custom_id") : null,
                CaptureStatusFor(eventType),
                amount,
                currency,
                reason);

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
